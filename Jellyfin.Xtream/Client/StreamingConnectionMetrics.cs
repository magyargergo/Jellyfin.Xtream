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
using System.Threading;

namespace Jellyfin.Xtream.Client;

/// <summary>
/// Metrics and diagnostics for a streaming connection.
/// Provides industry-standard observability for streaming health.
/// </summary>
public sealed class StreamingConnectionMetrics
{
    private long _totalBytesReceived;
    private long _totalPacketsReceived;
    private long _reconnectCount;
    private long _errorCount;

    /// <summary>
    /// Gets or sets the stream identifier.
    /// </summary>
    public string StreamId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the channel name.
    /// </summary>
    public string ChannelName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source URL.
    /// </summary>
    public string SourceUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the resolved URL after redirects.
    /// </summary>
    public string? ResolvedUrl { get; set; }

    /// <summary>
    /// Gets or sets the current connection state.
    /// </summary>
    public ConnectionState State { get; set; } = ConnectionState.Disconnected;

    /// <summary>
    /// Gets or sets the timestamp when the connection was established.
    /// </summary>
    public DateTime? ConnectedAt { get; set; }

    /// <summary>
    /// Gets or sets the timestamp of the last successful data receive.
    /// </summary>
    public DateTime? LastDataReceivedAt { get; set; }

    /// <summary>
    /// Gets the total bytes received across all connections.
    /// </summary>
    public long TotalBytesReceived => Interlocked.Read(ref _totalBytesReceived);

    /// <summary>
    /// Gets the total MPEG-TS packets received.
    /// </summary>
    public long TotalPacketsReceived => Interlocked.Read(ref _totalPacketsReceived);

    /// <summary>
    /// Gets the number of reconnection attempts.
    /// </summary>
    public long ReconnectCount => Interlocked.Read(ref _reconnectCount);

    /// <summary>
    /// Gets the number of errors encountered.
    /// </summary>
    public long ErrorCount => Interlocked.Read(ref _errorCount);

    /// <summary>
    /// Gets or sets the current bitrate in Mbps (calculated from recent samples).
    /// </summary>
    public double CurrentBitrateMbps { get; set; }

    /// <summary>
    /// Gets the average bitrate in Mbps since connection started.
    /// </summary>
    public double AverageBitrateMbps
    {
        get
        {
            if (!ConnectedAt.HasValue)
            {
                return 0;
            }

            var duration = (DateTime.UtcNow - ConnectedAt.Value).TotalSeconds;
            return duration <= 0 ? 0 : TotalBytesReceived * 8.0 / 1_000_000 / duration;
        }
    }

    /// <summary>
    /// Gets the time since last data was received.
    /// </summary>
    public TimeSpan? TimeSinceLastData =>
        LastDataReceivedAt.HasValue ? DateTime.UtcNow - LastDataReceivedAt.Value : null;

    /// <summary>
    /// Gets the connection uptime.
    /// </summary>
    public TimeSpan? Uptime => ConnectedAt.HasValue ? DateTime.UtcNow - ConnectedAt.Value : null;

    /// <summary>
    /// Atomically adds bytes to the total received count.
    /// </summary>
    /// <param name="bytes">Number of bytes received.</param>
    public void AddBytesReceived(long bytes)
    {
        _ = Interlocked.Add(ref _totalBytesReceived, bytes);
        LastDataReceivedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Atomically adds packets to the total received count.
    /// </summary>
    /// <param name="packets">Number of packets received.</param>
    public void AddPacketsReceived(long packets) => Interlocked.Add(ref _totalPacketsReceived, packets);

    /// <summary>
    /// Increments the reconnect counter.
    /// </summary>
    public void IncrementReconnects() => Interlocked.Increment(ref _reconnectCount);

    /// <summary>
    /// Increments the error counter.
    /// </summary>
    public void IncrementErrors() => Interlocked.Increment(ref _errorCount);

    /// <summary>
    /// Resets all metrics for a new session.
    /// </summary>
    public void Reset()
    {
        _ = Interlocked.Exchange(ref _totalBytesReceived, 0);
        _ = Interlocked.Exchange(ref _totalPacketsReceived, 0);
        _ = Interlocked.Exchange(ref _reconnectCount, 0);
        _ = Interlocked.Exchange(ref _errorCount, 0);
        State = ConnectionState.Disconnected;
        ConnectedAt = null;
        LastDataReceivedAt = null;
        CurrentBitrateMbps = 0;
    }
}
