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
using System.Linq;
using Jellyfin.Xtream.Service.Streaming.Native;
using Jellyfin.Xtream.Utility;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Monitors stream health by checking buffer fill levels and native streamer status.
/// Reports underrun and near-full conditions, and triggers disposal when the native
/// streamer enters a terminal state.
/// </summary>
internal sealed class RestreamHealthMonitor
{
    private readonly ILogger _logger;
    private readonly IDiscordNotificationService? _discordService;
    private readonly double _bufferUnderrunThresholdPercent;
    private readonly double _bufferNearFullThresholdPercent;
    private readonly int _bufferUnderrunNotificationThreshold;

    private int _bufferUnderrunCount;
    private int _bufferHealthWarnings;

    /// <summary>
    /// Initializes a new instance of the <see cref="RestreamHealthMonitor"/> class.
    /// </summary>
    /// <param name="logger">Logger instance for health diagnostics.</param>
    /// <param name="discordService">Optional Discord notification service for alerts.</param>
    /// <param name="bufferUnderrunThresholdPercent">Buffer fill percentage below which an underrun is flagged.</param>
    /// <param name="bufferNearFullThresholdPercent">Buffer fill percentage above which a near-full warning is logged.</param>
    /// <param name="bufferUnderrunNotificationThreshold">Number of underruns before a Discord notification is sent.</param>
    public RestreamHealthMonitor(
        ILogger logger,
        IDiscordNotificationService? discordService,
        double bufferUnderrunThresholdPercent,
        double bufferNearFullThresholdPercent,
        int bufferUnderrunNotificationThreshold
    )
    {
        _logger = logger;
        _discordService = discordService;
        _bufferUnderrunThresholdPercent = bufferUnderrunThresholdPercent;
        _bufferNearFullThresholdPercent = bufferNearFullThresholdPercent;
        _bufferUnderrunNotificationThreshold = bufferUnderrunNotificationThreshold;
    }

    /// <summary>
    /// Performs a periodic health check on the stream, evaluating both the native
    /// streamer status and the buffer fill level across all active readers.
    /// </summary>
    /// <param name="streamId">The media source stream identifier.</param>
    /// <param name="streamName">Human-readable stream/channel name.</param>
    /// <param name="nativeStreamer">The native streamer instance (may be null if not started).</param>
    /// <param name="buffer">The circular buffer write stream.</param>
    /// <param name="consumerCount">Current number of active consumers.</param>
    /// <param name="onTerminal">Callback invoked with a kill reason when the streamer reaches a terminal state.</param>
    public void PerformHealthCheck(
        string streamId,
        string streamName,
        NativeStreamer? nativeStreamer,
        CircularBufferWriteStream buffer,
        int consumerCount,
        Action<string> onTerminal
    )
    {
        if (!TryGetStreamerStatus(nativeStreamer, out var status))
        {
            return;
        }

        if (status.IsTerminal)
        {
            _logger.PluginLogWarning(
                "Native streamer reached terminal state {State} for channel {ChannelId}",
                status.State,
                streamId
            );
            onTerminal($"Streamer: {status.State}");
            return;
        }

        CheckBufferHealth(streamId, streamName, buffer, consumerCount);
        LogProgress(streamId, buffer, consumerCount);
    }

    /// <summary>
    /// Safely retrieves the current streamer status in a thread-safe manner.
    /// </summary>
    /// <param name="nativeStreamer">The native streamer instance.</param>
    /// <param name="status">The streamer status if available.</param>
    /// <returns>True if status was retrieved, false if streamer is null or disposed.</returns>
    private static bool TryGetStreamerStatus(NativeStreamer? nativeStreamer, out StreamerStatus status)
    {
        if (nativeStreamer == null)
        {
            status = default;
            return false;
        }

        status = nativeStreamer.GetStatus();
        return true;
    }

    /// <summary>
    /// Evaluates buffer health by computing the minimum reader-to-writer gap and
    /// flagging underruns or near-full conditions.
    /// </summary>
    private void CheckBufferHealth(
        string streamId,
        string streamName,
        CircularBufferWriteStream buffer,
        int consumerCount
    )
    {
        // Buffer health checks based on actual reader-writer gap (not write position).
        var readerSnapshots = CircularBufferReadStream
            .GetActiveStreamSnapshots()
            .Where(s => string.Equals(s.StreamId, streamId, StringComparison.Ordinal))
            .ToList();

        // Use the minimum gap across all readers as the effective buffer fill
        var minReaderGapBytes =
            readerSnapshots.Count > 0 ? readerSnapshots.Min(s => s.CurrentGapBytes) : buffer.TotalBytesWritten; // No readers = full buffer available

        var bufferFillPct =
            buffer.BufferSize > 0
                ? Math.Min(100.0, (double)minReaderGapBytes * 100.0 / (double)buffer.BufferSize)
                : 0.0;

        if (bufferFillPct < _bufferUnderrunThresholdPercent && readerSnapshots.Count > 0)
        {
            _bufferUnderrunCount++;

            if (_bufferUnderrunCount % 3 == 1)
            {
                _logger.PluginLogWarning(
                    "Buffer underrun #{Count} for channel {ChannelId}. Slowest reader only {GapKB}KB ({FillPct:F1}%) behind live.",
                    _bufferUnderrunCount,
                    streamId,
                    minReaderGapBytes / 1024,
                    bufferFillPct
                );

                if (_bufferUnderrunCount >= _bufferUnderrunNotificationThreshold)
                {
                    var fillPct = bufferFillPct;
                    var count = _bufferUnderrunCount;
                    _discordService.SendFireAndForget(svc =>
                        svc.NotifyBufferHealthIssueAsync(streamId, streamName, count, fillPct, 0.0)
                    );
                }
            }
        }
        else if (bufferFillPct > _bufferNearFullThresholdPercent && readerSnapshots.Count > 0)
        {
            _bufferHealthWarnings++;
            _logger.LogDebugIfEnabled(
                "Buffer for channel {ChannelId} is {FillPct:F1}% full. Consumers: {Consumers}",
                streamId,
                bufferFillPct,
                consumerCount
            );
        }
    }

    /// <summary>
    /// Logs broadcast progress metrics at each health check interval.
    /// </summary>
    private void LogProgress(string streamId, CircularBufferWriteStream buffer, int consumerCount)
    {
        var readerSnapshots = CircularBufferReadStream
            .GetActiveStreamSnapshots()
            .Where(s => string.Equals(s.StreamId, streamId, StringComparison.Ordinal))
            .ToList();

        var minReaderGapBytes =
            readerSnapshots.Count > 0 ? readerSnapshots.Min(s => s.CurrentGapBytes) : buffer.TotalBytesWritten;

        var bufferFillPct =
            buffer.BufferSize > 0
                ? Math.Min(100.0, (double)minReaderGapBytes * 100.0 / (double)buffer.BufferSize)
                : 0.0;

        _logger.PluginLogInformation(
            "Broadcast progress for channel {ChannelId}: {TotalMB} MB written, {Consumers} consumers, reader gap {GapKB}KB ({FillPct:F1}%)",
            streamId,
            buffer.TotalBytesWritten / 1048576,
            consumerCount,
            minReaderGapBytes / 1024,
            bufferFillPct
        );
    }
}
