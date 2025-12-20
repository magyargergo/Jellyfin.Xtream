// Copyright (C) 2025  Gergo Magyar
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Client;

/// <summary>
/// Manages streaming connection state transitions and metrics.
/// Implements a state machine with proper lifecycle management.
/// </summary>
/// <remarks>
/// State machine transitions:
/// <code>
/// Disconnected -> Connecting -> Negotiating -> Streaming
///                    |              |            |
///                    v              v            v
///                 Failed         Failed    Reconnecting -> Connecting
///                                              |
///                                              v
///                                           Failed (max retries)
///
/// Any State -> Closing -> Disconnected (graceful shutdown)
/// Any State -> ServerClosed -> Reconnecting (EOF)
/// </code>
/// </remarks>
public sealed class StreamingConnectionStateManager : IDisposable
{
    private const int BitrateWindowSeconds = 5;

    private static readonly Dictionary<ConnectionState, HashSet<ConnectionState>> _validTransitions = new()
    {
        [ConnectionState.Disconnected] = [ConnectionState.Connecting],
        [ConnectionState.Connecting] = [ConnectionState.Negotiating, ConnectionState.Failed, ConnectionState.Closing],
        [ConnectionState.Negotiating] = [ConnectionState.Streaming, ConnectionState.Failed, ConnectionState.Closing],
        [ConnectionState.Streaming] =
        [
            ConnectionState.Reconnecting,
            ConnectionState.ServerClosed,
            ConnectionState.Closing,
        ],
        [ConnectionState.Reconnecting] = [ConnectionState.Connecting, ConnectionState.Failed, ConnectionState.Closing],
        [ConnectionState.ServerClosed] = [ConnectionState.Reconnecting, ConnectionState.Closing],
        [ConnectionState.Failed] = [ConnectionState.Disconnected], // Can reset after failure
        [ConnectionState.Closing] = [ConnectionState.Disconnected],
    };

    private readonly StreamingConnectionMetrics _metrics;
    private readonly ILogger? _logger;
    private readonly object _stateLock = new();
    private readonly Stopwatch _connectionStopwatch = new();

    // Bitrate calculation using sliding window
    private readonly Queue<(DateTime Time, long Bytes)> _bitrateWindow = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamingConnectionStateManager"/> class.
    /// </summary>
    /// <param name="streamId">Unique identifier for the stream.</param>
    /// <param name="channelName">Human-readable channel name.</param>
    /// <param name="sourceUrl">The source URL for the stream.</param>
    /// <param name="logger">Optional logger for state transitions.</param>
    public StreamingConnectionStateManager(
        string streamId,
        string channelName,
        string sourceUrl,
        ILogger? logger = null
    )
    {
        _logger = logger;
        _metrics = new StreamingConnectionMetrics
        {
            StreamId = streamId,
            ChannelName = channelName,
            SourceUrl = sourceUrl,
        };
    }

    /// <summary>
    /// Event raised when state changes.
    /// </summary>
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Gets the current connection state.
    /// </summary>
    public ConnectionState State => _metrics.State;

    /// <summary>
    /// Gets the connection metrics.
    /// </summary>
    public StreamingConnectionMetrics Metrics => _metrics;

    /// <summary>
    /// Attempts to transition to a new state.
    /// </summary>
    /// <param name="newState">The target state.</param>
    /// <returns>True if transition was successful, false if invalid.</returns>
    public bool TryTransition(ConnectionState newState)
    {
        lock (_stateLock)
        {
            var currentState = _metrics.State;

            // Check if transition is valid
            if (
                !_validTransitions.TryGetValue(currentState, out var allowedStates) || !allowedStates.Contains(newState)
            )
            {
                _logger?.LogWarning(
                    "Invalid state transition for stream {StreamId}: {Current} -> {Target}",
                    _metrics.StreamId,
                    currentState,
                    newState
                );
                return false;
            }

            // Apply transition
            var previousState = currentState;
            _metrics.State = newState;

            // Handle state-specific logic
            OnStateEnter(newState);

            _logger?.LogDebug(
                "Stream {StreamId} state: {Previous} -> {Current}",
                _metrics.StreamId,
                previousState,
                newState
            );

            // Raise event
            StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(previousState, newState));

            return true;
        }
    }

    /// <summary>
    /// Forces a state reset (for recovery scenarios).
    /// </summary>
    public void ForceReset()
    {
        lock (_stateLock)
        {
            var previousState = _metrics.State;
            _metrics.Reset();
            _bitrateWindow.Clear();
            _connectionStopwatch.Reset();

            _logger?.LogInformation(
                "Stream {StreamId} state forcibly reset from {Previous} to Disconnected",
                _metrics.StreamId,
                previousState
            );

            StateChanged?.Invoke(
                this,
                new ConnectionStateChangedEventArgs(previousState, ConnectionState.Disconnected)
            );
        }
    }

    /// <summary>
    /// Records bytes received and updates bitrate calculation.
    /// </summary>
    /// <param name="bytes">Number of bytes received.</param>
    public void RecordBytesReceived(long bytes)
    {
        _metrics.AddBytesReceived(bytes);
        UpdateBitrate(bytes);
    }

    /// <summary>
    /// Records packets received.
    /// </summary>
    /// <param name="packets">Number of packets received.</param>
    public void RecordPacketsReceived(long packets)
    {
        _metrics.AddPacketsReceived(packets);
    }

    /// <summary>
    /// Records a reconnection attempt.
    /// </summary>
    public void RecordReconnect()
    {
        _metrics.IncrementReconnects();
    }

    /// <summary>
    /// Records an error.
    /// </summary>
    public void RecordError()
    {
        _metrics.IncrementErrors();
    }

    /// <summary>
    /// Sets the resolved URL after redirect handling.
    /// </summary>
    /// <param name="resolvedUrl">The final resolved URL.</param>
    public void SetResolvedUrl(string resolvedUrl)
    {
        _metrics.ResolvedUrl = resolvedUrl;
    }

    /// <summary>
    /// Gets a formatted diagnostics string.
    /// </summary>
    /// <returns>Formatted connection diagnostics.</returns>
    public string GetDiagnostics()
    {
        var metrics = _metrics;
        return $"Connection Diagnostics for {metrics.StreamId}:\n"
            + $"  Channel: {metrics.ChannelName}\n"
            + $"  State: {metrics.State}\n"
            + $"  Source URL: {metrics.SourceUrl}\n"
            + $"  Resolved URL: {metrics.ResolvedUrl ?? "N/A"}\n"
            + $"  Connected At: {metrics.ConnectedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "N/A"}\n"
            + $"  Uptime: {metrics.Uptime?.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) ?? "N/A"}\n"
            + $"  Last Data: {metrics.TimeSinceLastData?.TotalSeconds:F1}s ago\n"
            + $"  Total Bytes: {metrics.TotalBytesReceived / (1024.0 * 1024.0):F2} MB\n"
            + $"  Current Bitrate: {metrics.CurrentBitrateMbps:F2} Mbps\n"
            + $"  Average Bitrate: {metrics.AverageBitrateMbps:F2} Mbps\n"
            + $"  Reconnects: {metrics.ReconnectCount}\n"
            + $"  Errors: {metrics.ErrorCount}";
    }

    /// <summary>
    /// Checks if the connection is considered healthy.
    /// </summary>
    /// <param name="maxDataAgeSeconds">Maximum age of last received data in seconds.</param>
    /// <returns>True if connection is healthy.</returns>
    public bool IsHealthy(int maxDataAgeSeconds = 30)
    {
        if (_metrics.State != ConnectionState.Streaming)
        {
            return false;
        }

        if (!_metrics.LastDataReceivedAt.HasValue)
        {
            return false;
        }

        var dataAge = DateTime.UtcNow - _metrics.LastDataReceivedAt.Value;
        return dataAge.TotalSeconds <= maxDataAgeSeconds;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        StateChanged = null;
    }

    private void OnStateEnter(ConnectionState state)
    {
        switch (state)
        {
            case ConnectionState.Connecting:
                _connectionStopwatch.Restart();
                break;

            case ConnectionState.Streaming:
                _metrics.ConnectedAt = DateTime.UtcNow;
                _connectionStopwatch.Stop();
                _logger?.LogDebug(
                    "Stream {StreamId} connected in {ElapsedMs}ms",
                    _metrics.StreamId,
                    _connectionStopwatch.ElapsedMilliseconds
                );
                break;

            case ConnectionState.Reconnecting:
                _metrics.IncrementReconnects();
                break;

            case ConnectionState.Failed:
                _metrics.IncrementErrors();
                break;

            case ConnectionState.Disconnected:
                _bitrateWindow.Clear();
                break;
        }
    }

    private void UpdateBitrate(long bytesReceived)
    {
        var now = DateTime.UtcNow;

        lock (_bitrateWindow)
        {
            // Add new sample
            _bitrateWindow.Enqueue((now, bytesReceived));

            // Remove samples older than window
            var cutoff = now.AddSeconds(-BitrateWindowSeconds);
            while (_bitrateWindow.Count > 0 && _bitrateWindow.Peek().Time < cutoff)
            {
                _bitrateWindow.Dequeue();
            }

            // Calculate bitrate from window
            if (_bitrateWindow.Count > 1)
            {
                long totalBytes = 0;
                foreach (var sample in _bitrateWindow)
                {
                    totalBytes += sample.Bytes;
                }

                var windowDuration = (now - _bitrateWindow.Peek().Time).TotalSeconds;
                if (windowDuration > 0)
                {
                    _metrics.CurrentBitrateMbps = (totalBytes * 8.0 / 1_000_000) / windowDuration;
                }
            }
        }
    }
}
