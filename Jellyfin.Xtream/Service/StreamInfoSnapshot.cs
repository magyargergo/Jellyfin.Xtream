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

namespace Jellyfin.Xtream.Service;

/// <summary>
/// A point-in-time snapshot of stream information for API/UI purposes.
/// Record struct to avoid heap allocations when returning stream info.
/// </summary>
public readonly record struct StreamInfoSnapshot
{
    /// <summary>
    /// Gets the stream identifier.
    /// </summary>
    public string StreamId { get; init; }

    /// <summary>
    /// Gets the channel name.
    /// </summary>
    public string ChannelName { get; init; }

    /// <summary>
    /// Gets the time when the stream was started (UTC).
    /// </summary>
    public DateTime StartTime { get; init; }

    /// <summary>
    /// Gets the buffer size in bytes.
    /// </summary>
    public long BufferSizeBytes { get; init; }

    /// <summary>
    /// Gets the total bytes written to the buffer.
    /// </summary>
    public long TotalBytesWritten { get; init; }

    /// <summary>
    /// Gets the total bytes read from the buffer.
    /// </summary>
    public long TotalBytesRead { get; init; }

    /// <summary>
    /// Gets the current gap between read and write heads in bytes.
    /// </summary>
    public long CurrentGapBytes { get; init; }

    /// <summary>
    /// Gets the current gap as a percentage of buffer size.
    /// </summary>
    public double GapPercentage { get; init; }

    /// <summary>
    /// Gets the number of buffer overflow events.
    /// </summary>
    public int OverflowCount { get; init; }

    /// <summary>
    /// Gets the total bytes lost due to overflows.
    /// </summary>
    public long OverflowBytes { get; init; }

    /// <summary>
    /// Gets the health status (Healthy, OK, Lagging).
    /// </summary>
    public string Status { get; init; }

    /// <summary>
    /// Gets a value indicating whether the stream is aligned to a keyframe.
    /// </summary>
    public bool IsAligned { get; init; }

    // Quality Metrics

    /// <summary>
    /// Gets the total number of TS packets with Transport Error Indicator set.
    /// </summary>
    public long PacketErrors { get; init; }

    /// <summary>
    /// Gets the total number of continuity counter discontinuities detected.
    /// </summary>
    public long ContinuityErrors { get; init; }

    /// <summary>
    /// Gets the number of sync byte errors detected.
    /// </summary>
    public long SyncErrors { get; init; }

    /// <summary>
    /// Gets the number of PAT interval violations.
    /// </summary>
    public long PatViolations { get; init; }

    /// <summary>
    /// Gets the number of CRC-32 validation failures.
    /// </summary>
    public long CrcErrors { get; init; }

    /// <summary>
    /// Gets the current A/V drift in milliseconds.
    /// </summary>
    public double AvDriftMs { get; init; }

    /// <summary>
    /// Gets the sync status (None, Good, Warning, Severe).
    /// </summary>
    public string SyncStatus { get; init; }

    /// <summary>
    /// Gets a value indicating whether there are quality issues.
    /// </summary>
    public bool HasQualityIssues { get; init; }

    /// <summary>
    /// Gets the quality warning level (None, Warning, Critical).
    /// </summary>
    public string QualityLevel { get; init; }

    /// <summary>
    /// Gets a summary of quality issues.
    /// </summary>
    public string? QualityIssues { get; init; }

    // Native Streamer Status

    /// <summary>
    /// Gets the current native streamer state (Idle, Connecting, Streaming, etc.).
    /// </summary>
    public string StreamerState { get; init; }

    /// <summary>
    /// Gets the index of the currently active URL (0-based).
    /// </summary>
    public int CurrentUrlIndex { get; init; }

    /// <summary>
    /// Gets the total number of URLs configured for failover.
    /// </summary>
    public int UrlCount { get; init; }

    /// <summary>
    /// Gets the total bytes received from the network by the native streamer.
    /// </summary>
    public long BytesReceived { get; init; }

    /// <summary>
    /// Gets the total TS packets output by the native streamer.
    /// </summary>
    public long PacketsOutput { get; init; }

    /// <summary>
    /// Gets the number of completed URL switches.
    /// </summary>
    public long SwitchesCompleted { get; init; }

    /// <summary>
    /// Gets the number of switches triggered by quality degradation (TR 101 290).
    /// </summary>
    public long QualitySwitches { get; init; }

    /// <summary>
    /// Gets the current transport stream bitrate in bits per second.
    /// </summary>
    public long TsBitrate { get; init; }

    /// <summary>
    /// Gets the last HTTP response status code.
    /// </summary>
    public int LastHttpStatus { get; init; }

    /// <summary>
    /// Gets the last libcurl error code (0 = no error).
    /// </summary>
    public int LastCurlError { get; init; }

    /// <summary>
    /// Gets the number of configured providers in the health system.
    /// </summary>
    public int ProviderCount { get; init; }

    // Reconnection/Discontinuity Metrics

    /// <summary>
    /// Gets the number of stream reconnections (discontinuities) that have occurred.
    /// </summary>
    public int ReconnectionCount { get; init; }

    /// <summary>
    /// Gets the offset where the last discontinuity occurred (0 if none).
    /// </summary>
    public long LastDiscontinuityOffset { get; init; }

    /// <summary>
    /// Gets the time since the last reconnection in seconds (null if no reconnections).
    /// </summary>
    public double? SecondsSinceLastReconnection { get; init; }
}
