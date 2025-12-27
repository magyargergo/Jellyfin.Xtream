// Copyright (C) 2025  Gergo Magyar

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Xtream.Utility;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.Discovery.Pipeline;

/// <summary>
/// Base class for pipeline stages with lock-free channel-based communication.
/// </summary>
public abstract class PipelineStageBase : IPipelineStage
{
    private readonly Channel<PipelineItem> _inputChannel;
    private readonly Channel<PipelineItem> _outputChannel;
    private readonly Channel<PipelineItem> _failedChannel;
    private readonly Channel<StageEvent> _eventChannel;
    private readonly ILogger _logger;
    private readonly StageConfiguration _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="PipelineStageBase"/> class.
    /// </summary>
    /// <param name="stageId">The stage identifier.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="config">Stage configuration.</param>
    protected PipelineStageBase(PipelineStage stageId, ILogger logger, StageConfiguration? config = null)
    {
        StageId = stageId;
        _logger = logger;
        _config = config ?? new StageConfiguration();
        Stats = new StageStats();

        // Create channels with appropriate settings for lock-free operation
        _inputChannel = Channel.CreateUnbounded<PipelineItem>(
            new UnboundedChannelOptions
            {
                SingleWriter = false,
                SingleReader = false,
                AllowSynchronousContinuations = false,
            }
        );

        _outputChannel =
            _config.OutputCapacity > 0
                ? Channel.CreateBounded<PipelineItem>(
                    new BoundedChannelOptions(_config.OutputCapacity)
                    {
                        FullMode = BoundedChannelFullMode.Wait,
                        SingleWriter = false,
                        SingleReader = false,
                    }
                )
                : Channel.CreateUnbounded<PipelineItem>(
                    new UnboundedChannelOptions
                    {
                        SingleWriter = false,
                        SingleReader = false,
                        AllowSynchronousContinuations = false,
                    }
                );

        _failedChannel = Channel.CreateUnbounded<PipelineItem>(
            new UnboundedChannelOptions
            {
                SingleWriter = false,
                SingleReader = false,
                AllowSynchronousContinuations = false,
            }
        );

        _eventChannel = Channel.CreateUnbounded<StageEvent>(
            new UnboundedChannelOptions
            {
                SingleWriter = false,
                SingleReader = false,
                AllowSynchronousContinuations = false,
            }
        );
    }

    /// <inheritdoc />
    public PipelineStage StageId { get; }

    /// <inheritdoc />
    public StageStats Stats { get; }

    /// <inheritdoc />
    public ChannelWriter<PipelineItem> Input => _inputChannel.Writer;

    /// <inheritdoc />
    public ChannelReader<PipelineItem> Output => _outputChannel.Reader;

    /// <inheritdoc />
    public ChannelReader<PipelineItem> Failed => _failedChannel.Reader;

    /// <inheritdoc />
    public ChannelReader<StageEvent> Events => _eventChannel.Reader;

    /// <summary>
    /// Processes a single item through this stage.
    /// </summary>
    /// <param name="item">The item to process.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The processing result.</returns>
    protected abstract ValueTask<StageResult<PipelineItem>> ProcessAsync(
        PipelineItem item,
        CancellationToken cancellationToken
    );

    /// <inheritdoc />
    public async Task RunAsync(int concurrency, CancellationToken cancellationToken)
    {
        var effectiveConcurrency = concurrency > 0 ? concurrency : _config.Concurrency;

        _logger.LogDebug("Stage {Stage} starting with concurrency {Concurrency}", StageId, effectiveConcurrency);

        try
        {
            await Parallel
                .ForEachAsync(
                    _inputChannel.Reader.ReadAllAsync(cancellationToken),
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = effectiveConcurrency,
                        CancellationToken = cancellationToken,
                    },
                    async (item, ct) => await ProcessItemAsync(item, ct).ConfigureAwait(false)
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Stage {Stage} cancelled", StageId);
        }
        catch (Exception ex)
        {
            _logger.PluginLogError(ex, "Stage {Stage} failed with unexpected error", StageId);
            throw;
        }
        finally
        {
            // Signal completion to downstream
            _outputChannel.Writer.TryComplete();
            _failedChannel.Writer.TryComplete();

            // Emit stage completed event
            var stats = Stats;
            await _eventChannel
                .Writer.WriteAsync(
                    StageEvent.Completed(StageId, (int)stats.Passed, (int)stats.Failed),
                    CancellationToken.None
                )
                .ConfigureAwait(false);

            _eventChannel.Writer.TryComplete();

            _logger.LogDebug(
                "Stage {Stage} completed: {Passed} passed, {Failed} failed, avg {AvgMs:F1}ms",
                StageId,
                stats.Passed,
                stats.Failed,
                stats.AverageDurationMs
            );
        }
    }

    private async ValueTask ProcessItemAsync(PipelineItem item, CancellationToken cancellationToken)
    {
        // Record entry
        Stats.RecordEntered();
        await _eventChannel
            .Writer.WriteAsync(StageEvent.Entered(StageId, item.Id), cancellationToken)
            .ConfigureAwait(false);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Apply timeout if configured
            using var timeoutCts =
                _config.TimeoutMs > 0 ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;

            if (timeoutCts != null)
            {
                timeoutCts.CancelAfter(_config.TimeoutMs);
            }

            var effectiveCt = timeoutCts?.Token ?? cancellationToken;

            // Process the item
            var result = await ProcessAsync(item, effectiveCt).ConfigureAwait(false);
            var durationMs = (int)stopwatch.ElapsedMilliseconds;

            if (result.Success)
            {
                // Item passed - advance to next stage
                var advancedItem = result.Value.AdvanceTo(GetNextStage());

                Stats.RecordPassed(durationMs);
                await _outputChannel.Writer.WriteAsync(advancedItem, cancellationToken).ConfigureAwait(false);
                await _eventChannel
                    .Writer.WriteAsync(StageEvent.Passed(StageId, item.Id, durationMs), cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                // Item failed - send to failed channel
                var failedItem = item.MarkFailed(result.FailureReason ?? "Unknown");

                Stats.RecordFailed(durationMs);
                await _failedChannel.Writer.WriteAsync(failedItem, cancellationToken).ConfigureAwait(false);
                await _eventChannel
                    .Writer.WriteAsync(
                        StageEvent.Failed(StageId, item.Id, durationMs, result.FailureReason),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout occurred
            var durationMs = (int)stopwatch.ElapsedMilliseconds;
            var failedItem = item.MarkFailed("Timeout");

            Stats.RecordFailed(durationMs);
            await _failedChannel.Writer.WriteAsync(failedItem, cancellationToken).ConfigureAwait(false);
            await _eventChannel
                .Writer.WriteAsync(StageEvent.Failed(StageId, item.Id, durationMs, "Timeout"), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (_config.ContinueOnError)
        {
            var durationMs = (int)stopwatch.ElapsedMilliseconds;
            var reason = $"Error: {ex.Message}";
            var failedItem = item.MarkFailed(reason);

            _logger.LogDebug(ex, "Stage {Stage} item {Id} failed with exception", StageId, item.Id);

            Stats.RecordFailed(durationMs);
            await _failedChannel.Writer.WriteAsync(failedItem, cancellationToken).ConfigureAwait(false);
            await _eventChannel
                .Writer.WriteAsync(StageEvent.Failed(StageId, item.Id, durationMs, reason), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private PipelineStage GetNextStage() =>
        StageId switch
        {
            PipelineStage.Connectivity => PipelineStage.Authentication,
            PipelineStage.Authentication => PipelineStage.CountryFilter,
            PipelineStage.CountryFilter => PipelineStage.StreamTest,
            PipelineStage.StreamTest => PipelineStage.QualityScoring,
            PipelineStage.QualityScoring => PipelineStage.Completed,
            _ => PipelineStage.Completed,
        };

    /// <inheritdoc />
    public void Complete()
    {
        _inputChannel.Writer.TryComplete();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Performs async cleanup. Override in derived classes for custom disposal.
    /// </summary>
    /// <returns>A task representing the async operation.</returns>
    protected virtual ValueTask DisposeAsyncCore()
    {
        Complete();
        return ValueTask.CompletedTask;
    }
}
