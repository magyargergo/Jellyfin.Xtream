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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Xtream.Service.Discovery.Pipeline.Stages;
using Jellyfin.Xtream.Utility;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.Discovery.Pipeline;

/// <summary>
/// Orchestrates the multi-stage provider discovery pipeline.
/// Provides lock-free, event-driven processing with real-time progress.
/// </summary>
public sealed class DiscoveryPipeline : IAsyncDisposable
{
    private readonly IPipelineStage[] _stages;
    private readonly Channel<StageEvent> _aggregatedEvents;
    private readonly ConcurrentDictionary<PipelineStage, StageStats> _stageStats;
    private readonly ConcurrentBag<ProviderTestResult> _completedResults;
    private readonly ConcurrentBag<PipelineItem> _failedItems;
    private readonly ILogger _logger;

    private int _totalItems;
    private int _itemIdCounter;
    private Task? _pipelineTask;
    private CancellationTokenSource? _cts;

    private DiscoveryPipeline(IPipelineStage[] stages, ILogger logger)
    {
        _stages = stages;
        _logger = logger;
        _stageStats = new ConcurrentDictionary<PipelineStage, StageStats>();
        _completedResults = [];
        _failedItems = [];

        // Create aggregated event channel for all stages
        _aggregatedEvents = Channel.CreateUnbounded<StageEvent>(
            new UnboundedChannelOptions
            {
                SingleWriter = false,
                SingleReader = false,
                AllowSynchronousContinuations = false,
            }
        );

        // Initialize stats for each stage
        foreach (var stage in _stages)
        {
            _stageStats[stage.StageId] = stage.Stats;
        }
    }

    /// <summary>
    /// Gets the count of items that completed all stages successfully.
    /// </summary>
    public int CompletedCount => _completedResults.Count;

    /// <summary>
    /// Gets the count of items that failed at any stage.
    /// </summary>
    public int FailedCount => _failedItems.Count;

    /// <summary>
    /// Gets the total items submitted to the pipeline.
    /// </summary>
    public int TotalItems => _totalItems;

    /// <summary>
    /// Gets statistics for a specific stage.
    /// </summary>
    /// <param name="stage">The stage identifier.</param>
    /// <returns>Stage statistics or null if not found.</returns>
    public StageStats? GetStageStats(PipelineStage stage) =>
        _stageStats.TryGetValue(stage, out var stats) ? stats : null;

    /// <summary>
    /// Gets all completed provider test results.
    /// </summary>
    /// <returns>Collection of completed results.</returns>
    public IReadOnlyCollection<ProviderTestResult> GetCompletedResults() => [.. _completedResults];

    /// <summary>
    /// Gets all failed pipeline items.
    /// </summary>
    /// <returns>Collection of failed items.</returns>
    public IReadOnlyCollection<PipelineItem> GetFailedItems() => [.. _failedItems];

    /// <summary>
    /// Starts the pipeline with the given credentials.
    /// </summary>
    /// <param name="credentials">Credentials to process.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task that completes when pipeline finishes.</returns>
    public async Task RunAsync(IEnumerable<DiscoveredCredential> credentials, CancellationToken cancellationToken)
    {
        _cts?.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _cts.Token;

        // Create pipeline items
        var items = credentials
            .Select(c => new PipelineItem
            {
                Id = Interlocked.Increment(ref _itemIdCounter),
                Credential = c,
                Stage = PipelineStage.Pending,
            })
            .ToList();

        _ = Interlocked.Exchange(ref _totalItems, items.Count);

        _logger.PluginLogInformation("Starting pipeline with {Count} credentials", items.Count);

        // Start all stages
        var stageTasks = new List<Task>();
        for (var i = 0; i < _stages.Length; i++)
        {
            var stage = _stages[i];
            var concurrency = GetStageConcurrency(stage.StageId);

            // Start event forwarding for this stage
            _ = ForwardEventsAsync(stage, ct);

            // Start the stage
            var stageTask = stage.RunAsync(concurrency, ct);
            stageTasks.Add(stageTask);
        }

        // Connect stages: output of stage N goes to input of stage N+1
        for (var i = 0; i < _stages.Length - 1; i++)
        {
            _ = ForwardItemsAsync(_stages[i].Output, _stages[i + 1].Input, ct);
        }

        // Collect completed items from last stage
        _ = CollectCompletedAsync(_stages[^1].Output, ct);

        // Collect failed items from all stages
        foreach (var stage in _stages)
        {
            _ = CollectFailedAsync(stage.Failed, ct);
        }

        // Feed items to first stage
        var firstStage = _stages[0];
        foreach (var item in items)
        {
            await firstStage.Input.WriteAsync(item, ct).ConfigureAwait(false);
        }

        firstStage.Complete();

        // Wait for all stages to complete
        _pipelineTask = Task.WhenAll(stageTasks);

        try
        {
            await _pipelineTask.ConfigureAwait(false);
        }
        finally
        {
            _ = _aggregatedEvents.Writer.TryComplete();
        }

        _logger.PluginLogInformation(
            "Pipeline completed: {Completed} succeeded, {Failed} failed",
            _completedResults.Count,
            _failedItems.Count
        );
    }

    /// <summary>
    /// Streams progress updates as an async enumerable.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Async enumerable of progress updates.</returns>
    public async IAsyncEnumerable<PipelineProgress> StreamProgressAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        await foreach (var evt in _aggregatedEvents.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return CreateProgress(evt);
        }
    }

    /// <summary>
    /// Gets the current progress snapshot.
    /// </summary>
    /// <returns>Current progress.</returns>
    public PipelineProgress GetCurrentProgress()
    {
        return new PipelineProgress
        {
            TotalItems = _totalItems,
            CompletedItems = _completedResults.Count + _failedItems.Count,
            PassedItems = _completedResults.Count,
            FailedItems = _failedItems.Count,
            StageStats = _stageStats.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToSnapshot()),
        };
    }

    /// <summary>
    /// Cancels the pipeline execution.
    /// </summary>
    public void Cancel() => _cts?.Cancel();

    private PipelineProgress CreateProgress(StageEvent evt)
    {
        return new PipelineProgress
        {
            CurrentStage = evt.Stage,
            CurrentEventType = evt.Type,
            TotalItems = _totalItems,
            CompletedItems = _completedResults.Count + _failedItems.Count,
            PassedItems = _completedResults.Count,
            FailedItems = _failedItems.Count,
            StageStats = _stageStats.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToSnapshot()),
        };
    }

    private async Task ForwardEventsAsync(IPipelineStage stage, CancellationToken ct)
    {
        try
        {
            await foreach (var evt in stage.Events.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await _aggregatedEvents.Writer.WriteAsync(evt, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected on cancellation
        }
    }

    private static async Task ForwardItemsAsync(
        ChannelReader<PipelineItem> source,
        ChannelWriter<PipelineItem> target,
        CancellationToken ct
    )
    {
        try
        {
            await foreach (var item in source.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await target.WriteAsync(item, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected on cancellation
        }
        finally
        {
            _ = target.TryComplete();
        }
    }

    private async Task CollectCompletedAsync(ChannelReader<PipelineItem> source, CancellationToken ct)
    {
        try
        {
            await foreach (var item in source.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var result = item.GetProperty<ProviderTestResult>("TestResult");
                if (result != null)
                {
                    _completedResults.Add(result);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected on cancellation
        }
    }

    private async Task CollectFailedAsync(ChannelReader<PipelineItem> source, CancellationToken ct)
    {
        try
        {
            await foreach (var item in source.ReadAllAsync(ct).ConfigureAwait(false))
            {
                _failedItems.Add(item);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected on cancellation
        }
    }

    private static int GetStageConcurrency(PipelineStage stage) =>
        stage switch
        {
            PipelineStage.Connectivity => 50, // Lots of parallel connections
            PipelineStage.Authentication => 20, // API rate limiting
            PipelineStage.StreamTest => 10, // Bandwidth intensive
            PipelineStage.CountryFilter => 4, // CPU intensive
            PipelineStage.QualityScoring => 4, // CPU bound
            _ => 10,
        };

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Cancel();

        if (_pipelineTask != null)
        {
            try
            {
#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks - acceptable in dispose pattern
                await _pipelineTask.ConfigureAwait(false);
#pragma warning restore VSTHRD003
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        foreach (var stage in _stages)
        {
            await stage.DisposeAsync().ConfigureAwait(false);
        }

        _cts?.Dispose();
    }

    /// <summary>
    /// Creates a new discovery pipeline with all stages using the default country (Poland).
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <returns>A configured discovery pipeline.</returns>
    public static DiscoveryPipeline Create(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory) =>
        new Builder(httpClientFactory, loggerFactory).Build();

    /// <summary>
    /// Creates a new discovery pipeline with all stages for a specific country.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="countryCode">The country code (e.g., "PL", "UK", "DE", "FR") or null for no filtering.</param>
    /// <returns>A configured discovery pipeline.</returns>
    public static DiscoveryPipeline Create(
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        string? countryCode
    ) => new Builder(httpClientFactory, loggerFactory).WithCountry(countryCode).Build();

    /// <summary>
    /// Builder for creating discovery pipelines.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="Builder"/> class.
    /// </remarks>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    public sealed class Builder(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    {
        private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
        private readonly ILoggerFactory _loggerFactory = loggerFactory;
        private CountryProfile? _countryProfile = CountryBroadcasters.Poland;

        /// <summary>
        /// Sets the country profile for channel filtering.
        /// </summary>
        /// <param name="countryCode">The country code (e.g., "PL", "UK", "DE", "FR") or null for no filtering.</param>
        /// <returns>The builder for chaining.</returns>
        public Builder WithCountry(string? countryCode)
        {
            _countryProfile = string.IsNullOrEmpty(countryCode) ? null : CountryBroadcasters.GetByCode(countryCode);
            return this;
        }

        /// <summary>
        /// Sets the country profile for channel filtering.
        /// </summary>
        /// <param name="profile">The country profile or null for no filtering.</param>
        /// <returns>The builder for chaining.</returns>
        public Builder WithCountry(CountryProfile? profile)
        {
            _countryProfile = profile;
            return this;
        }

        /// <summary>
        /// Builds the standard discovery pipeline with all stages.
        /// </summary>
        /// <returns>Configured discovery pipeline.</returns>
        public DiscoveryPipeline Build()
        {
            var stages = new List<IPipelineStage>
            {
                new ConnectivityStage(_loggerFactory.CreateLogger<ConnectivityStage>()),
                new AuthenticationStage(_httpClientFactory, _loggerFactory.CreateLogger<AuthenticationStage>()),
            };

            // Add country filter stage only if a country profile is specified
            if (_countryProfile != null)
            {
                stages.Add(
                    new CountryFilterStage(
                        _httpClientFactory,
                        _countryProfile,
                        PipelineStage.CountryFilter,
                        _loggerFactory.CreateLogger<CountryFilterStage>()
                    )
                );
            }

            stages.Add(new StreamTestStage(_httpClientFactory, _loggerFactory.CreateLogger<StreamTestStage>()));
            stages.Add(new QualityScoringStage(_loggerFactory.CreateLogger<QualityScoringStage>()));

            return new DiscoveryPipeline([.. stages], _loggerFactory.CreateLogger<DiscoveryPipeline>());
        }

        /// <summary>
        /// Builds a pipeline with only connectivity and authentication stages.
        /// Useful for quick validation of credentials.
        /// </summary>
        /// <returns>Quick validation pipeline.</returns>
        public DiscoveryPipeline BuildQuickValidation()
        {
            var stages = new IPipelineStage[]
            {
                new ConnectivityStage(_loggerFactory.CreateLogger<ConnectivityStage>()),
                new AuthenticationStage(_httpClientFactory, _loggerFactory.CreateLogger<AuthenticationStage>()),
            };

            return new DiscoveryPipeline(stages, _loggerFactory.CreateLogger<DiscoveryPipeline>());
        }
    }
}

/// <summary>
/// Progress information for the discovery pipeline.
/// </summary>
public sealed class PipelineProgress
{
    /// <summary>
    /// Gets or sets the current active stage.
    /// </summary>
    public PipelineStage CurrentStage { get; set; }

    /// <summary>
    /// Gets or sets the current event type.
    /// </summary>
    public StageEventType CurrentEventType { get; set; }

    /// <summary>
    /// Gets or sets the total items in the pipeline.
    /// </summary>
    public int TotalItems { get; set; }

    /// <summary>
    /// Gets or sets the count of completed items (passed + failed).
    /// </summary>
    public int CompletedItems { get; set; }

    /// <summary>
    /// Gets or sets the count of items that passed all stages.
    /// </summary>
    public int PassedItems { get; set; }

    /// <summary>
    /// Gets or sets the count of failed items.
    /// </summary>
    public int FailedItems { get; set; }

    /// <summary>
    /// Gets or sets the progress percentage.
    /// </summary>
    public int ProgressPercent => TotalItems > 0 ? (int)Math.Round(100.0 * CompletedItems / TotalItems) : 0;

    /// <summary>
    /// Gets or sets per-stage statistics.
    /// </summary>
    public IReadOnlyDictionary<PipelineStage, StageStatsSnapshot>? StageStats { get; init; }

    /// <summary>
    /// Gets the count of providers that passed connectivity.
    /// </summary>
    public long ConnectivityPassed =>
        StageStats?.TryGetValue(PipelineStage.Connectivity, out var s) == true ? s.Passed : 0;

    /// <summary>
    /// Gets the count of providers that passed authentication.
    /// </summary>
    public long AuthenticationPassed =>
        StageStats?.TryGetValue(PipelineStage.Authentication, out var s) == true ? s.Passed : 0;

    /// <summary>
    /// Gets the count of providers with working streams.
    /// </summary>
    public long StreamTestPassed => StageStats?.TryGetValue(PipelineStage.StreamTest, out var s) == true ? s.Passed : 0;

    /// <summary>
    /// Gets the count of providers with EPG.
    /// </summary>
    public long EpgCheckPassed => StageStats?.TryGetValue(PipelineStage.EpgCheck, out var s) == true ? s.Passed : 0;

    /// <summary>
    /// Gets the count of providers with Polish channels.
    /// </summary>
    public long CountryFilterPassed =>
        StageStats?.TryGetValue(PipelineStage.CountryFilter, out var s) == true ? s.Passed : 0;

    /// <summary>
    /// Gets the count of fully scored providers.
    /// </summary>
    public long QualityScoringPassed =>
        StageStats?.TryGetValue(PipelineStage.QualityScoring, out var s) == true ? s.Passed : 0;

    /// <summary>
    /// Gets the total count of items currently being processed across all stages.
    /// </summary>
    public long TotalInProgress => StageStats?.Values.Sum(s => s.InProgress) ?? 0;

    /// <summary>
    /// Gets a value indicating whether the pipeline has finished processing all items.
    /// </summary>
    public bool IsComplete => TotalItems > 0 && TotalInProgress == 0 && CompletedItems >= TotalItems;
}
