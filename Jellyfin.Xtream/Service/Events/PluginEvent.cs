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
using System.Collections.Generic;

namespace Jellyfin.Xtream.Service.Events;

/// <summary>
/// A typed event for SSE broadcasting and internal consumption.
/// </summary>
public sealed class PluginEvent
{
    /// <summary>
    /// Gets the monotonically increasing event ID.
    /// </summary>
    public long Id { get; init; }

    /// <summary>
    /// Gets the event type (e.g. stream.started, buffer.overflow, provider.switched).
    /// </summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>
    /// Gets the UTC timestamp of the event.
    /// </summary>
    public DateTime Timestamp { get; init; }

    /// <summary>
    /// Gets the stream ID associated with this event (null for system events).
    /// </summary>
    public string? StreamId { get; init; }

    /// <summary>
    /// Gets the event payload as key-value pairs.
    /// </summary>
    public Dictionary<string, object>? Data { get; init; }
}
