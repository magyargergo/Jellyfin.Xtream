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
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.Logging;

/// <summary>
/// Represents a single log entry captured by the plugin logging system.
/// </summary>
public sealed class PluginLogEntry
{
    /// <summary>
    /// Gets the unique identifier for this log entry.
    /// </summary>
    public long Id { get; init; }

    /// <summary>
    /// Gets the timestamp when this log entry was created.
    /// </summary>
    public DateTime Timestamp { get; init; }

    /// <summary>
    /// Gets the log level of this entry as an integer for JSON serialization.
    /// 0=Trace, 1=Debug, 2=Information, 3=Warning, 4=Error, 5=Critical.
    /// </summary>
    [JsonPropertyName("level")]
    public int LevelValue => (int)Level;

    /// <summary>
    /// Gets or sets the log level of this entry.
    /// </summary>
    [JsonIgnore]
    public LogLevel Level { get; init; }

    /// <summary>
    /// Gets the source category name (logger name).
    /// </summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>
    /// Gets the formatted log message.
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Gets the exception message if an exception was logged.
    /// </summary>
    public string? ExceptionMessage { get; init; }

    /// <summary>
    /// Gets the exception stack trace if an exception was logged.
    /// </summary>
    public string? ExceptionStackTrace { get; init; }

    /// <summary>
    /// Gets the stream ID if this log is associated with a specific stream.
    /// </summary>
    public string? StreamId { get; init; }

    /// <summary>
    /// Gets the channel name if this log is associated with a specific channel.
    /// </summary>
    public string? ChannelName { get; init; }

    /// <summary>
    /// Gets whether this is a debug log entry.
    /// </summary>
    public bool IsDebug { get; init; }
}
