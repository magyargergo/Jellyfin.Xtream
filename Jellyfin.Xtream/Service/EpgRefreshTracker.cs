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
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Utility;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Tracks EPG refresh operations to detect when Jellyfin's guide refresh starts and ends.
/// Uses a sliding window approach - if no EPG requests come in for a threshold period,
/// the batch is considered complete.
/// </summary>
public sealed class EpgRefreshTracker : IDisposable
{
    private readonly IDiscordNotificationService _discordService;
    private readonly ILogger<EpgRefreshTracker> _logger;
    private readonly object _lock = new();
    private readonly TimeSpan _completionDelay = TimeSpan.FromSeconds(30);

    private DateTime _batchStartTime;
    private DateTime _lastRequestTime;
    private int _totalRequests;
    private int _successfulRequests;
    private int _failedRequests;
    private bool _batchInProgress;
    private CancellationTokenSource? _completionCts;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="EpgRefreshTracker"/> class.
    /// </summary>
    /// <param name="discordService">The Discord notification service.</param>
    /// <param name="logger">The logger.</param>
    public EpgRefreshTracker(IDiscordNotificationService discordService, ILogger<EpgRefreshTracker> logger)
    {
        _discordService = discordService;
        _logger = logger;
    }

    /// <summary>
    /// Records an EPG request, starting a new batch if needed.
    /// </summary>
    /// <param name="channelCount">Expected total channel count (0 if unknown).</param>
    public void RecordRequest(int channelCount = 0)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;

            if (!_batchInProgress)
            {
                StartNewBatch(now, channelCount);
            }

            _totalRequests++;
            _lastRequestTime = now;

            ResetCompletionTimer();
        }
    }

    /// <summary>
    /// Records a successful EPG fetch.
    /// </summary>
    public void RecordSuccess()
    {
        lock (_lock)
        {
            _successfulRequests++;
        }
    }

    /// <summary>
    /// Records a failed EPG fetch.
    /// </summary>
    public void RecordFailure()
    {
        lock (_lock)
        {
            _failedRequests++;
        }
    }

    private void StartNewBatch(DateTime startTime, int expectedChannels)
    {
        _batchInProgress = true;
        _batchStartTime = startTime;
        _lastRequestTime = startTime;
        _totalRequests = 0;
        _successfulRequests = 0;
        _failedRequests = 0;

        _logger.PluginLogInformation("EPG refresh batch started at {Time}", startTime);

        // Fire and forget - send start notification
        _ = Task.Run(async () =>
        {
            try
            {
                await _discordService
                    .NotifyEpgRefreshStartedAsync(expectedChannels, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.PluginLogWarning(ex, "Failed to send EPG refresh start notification");
            }
        });
    }

    private void ResetCompletionTimer()
    {
        _completionCts?.Cancel();
        _completionCts?.Dispose();
        _completionCts = new CancellationTokenSource();

        var token = _completionCts.Token;

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(_completionDelay, token).ConfigureAwait(false);

                    if (!token.IsCancellationRequested)
                    {
                        await CompleteBatchAsync().ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when timer is reset
                }
            },
            token
        );
    }

    private async Task CompleteBatchAsync()
    {
        EpgRefreshResult result;

        lock (_lock)
        {
            if (!_batchInProgress)
            {
                return;
            }

            var duration = _lastRequestTime - _batchStartTime;

            result = new EpgRefreshResult
            {
                SuccessCount = _successfulRequests,
                TotalCount = _totalRequests,
                Duration = duration,
                FailedChannels = [],
                HttpErrorCount = 0,
                NoDataCount = _failedRequests,
            };

            _logger.PluginLogInformation(
                "EPG refresh batch completed: {Success}/{Total} in {Duration:F1}s",
                _successfulRequests,
                _totalRequests,
                duration.TotalSeconds
            );

            _batchInProgress = false;
        }

        try
        {
            await _discordService.NotifyEpgRefreshAsync(result, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.PluginLogWarning(ex, "Failed to send EPG refresh completion notification");
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _completionCts?.Cancel();
        _completionCts?.Dispose();
        _disposed = true;
    }
}
