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
}
