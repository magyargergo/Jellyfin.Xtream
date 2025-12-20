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

namespace Jellyfin.Xtream.Service.Discord;

/// <summary>
/// Stream event data for notifications.
/// </summary>
public sealed class StreamEvent
{
    /// <summary>
    /// Gets or initializes the channel identifier.
    /// </summary>
    public required string ChannelId { get; init; }

    /// <summary>
    /// Gets or initializes the channel name.
    /// </summary>
    public required string ChannelName { get; init; }

    /// <summary>
    /// Gets or initializes the event timestamp.
    /// </summary>
    public required DateTime Timestamp { get; init; }

    /// <summary>
    /// Gets or initializes the bitrate in Mbps.
    /// </summary>
    public double? BitrateMbps { get; init; }

    /// <summary>
    /// Gets or initializes the duration in milliseconds.
    /// </summary>
    public long? DurationMs { get; init; }

    /// <summary>
    /// Gets or initializes the bytes streamed.
    /// </summary>
    public long? BytesStreamed { get; init; }

    /// <summary>
    /// Gets or initializes the server name.
    /// </summary>
    public string? ServerName { get; init; }

    /// <summary>
    /// Gets or initializes the latency in milliseconds.
    /// </summary>
    public int? LatencyMs { get; init; }
}
