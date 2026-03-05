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
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.Logging;

/// <summary>
/// Service for centralized plugin logging with in-memory buffer and filtering.
/// </summary>
public interface IPluginLogService
{
    /// <summary>
    /// Gets the total number of log entries in the buffer.
    /// </summary>
    int EntryCount { get; }

    /// <summary>
    /// Gets the maximum number of entries the buffer can hold.
    /// </summary>
    int MaxEntries { get; }

    /// <summary>
    /// Logs a message to the centralized log buffer.
    /// </summary>
    /// <param name="level">The log level.</param>
    /// <param name="category">The logger category name.</param>
    /// <param name="message">The formatted message.</param>
    /// <param name="exception">Optional exception.</param>
    /// <param name="isDebug">Whether this is a debug log entry.</param>
    /// <param name="streamId">Optional stream ID for stream-related logs.</param>
    /// <param name="channelName">Optional channel name for channel-related logs.</param>
    void Log(
        LogLevel level,
        string category,
        string message,
        Exception? exception = null,
        bool isDebug = false,
        string? streamId = null,
        string? channelName = null
    );

    /// <summary>
    /// Gets log entries with optional filtering.
    /// </summary>
    /// <param name="minLevel">Minimum log level to include.</param>
    /// <param name="includeDebug">Whether to include debug entries.</param>
    /// <param name="category">Optional category filter (partial match).</param>
    /// <param name="streamId">Optional stream ID filter.</param>
    /// <param name="searchText">Optional text search in message.</param>
    /// <param name="skip">Number of entries to skip (for pagination).</param>
    /// <param name="take">Maximum number of entries to return.</param>
    /// <returns>Filtered log entries ordered by timestamp descending (newest first).</returns>
    IReadOnlyList<PluginLogEntry> GetEntries(
        LogLevel minLevel = LogLevel.Trace,
        bool includeDebug = true,
        string? category = null,
        string? streamId = null,
        string? searchText = null,
        int skip = 0,
        int take = 100
    );

    /// <summary>
    /// Gets log entries added after the specified ID.
    /// </summary>
    /// <param name="afterId">Return entries with ID greater than this value.</param>
    /// <param name="minLevel">Minimum log level to include.</param>
    /// <param name="includeDebug">Whether to include debug entries.</param>
    /// <returns>New log entries ordered by timestamp ascending (oldest first for appending).</returns>
    IReadOnlyList<PluginLogEntry> GetEntriesAfter(
        long afterId,
        LogLevel minLevel = LogLevel.Trace,
        bool includeDebug = true
    );

    /// <summary>
    /// Gets statistics about the log buffer.
    /// </summary>
    /// <returns>Log statistics.</returns>
    PluginLogStats GetStats();

    /// <summary>
    /// Clears all log entries from the buffer.
    /// </summary>
    void Clear();
}

/// <summary>
/// Statistics about the log buffer.
/// </summary>
public sealed class PluginLogStats
{
    /// <summary>
    /// Gets the total number of entries in the buffer.
    /// </summary>
    public int TotalEntries { get; init; }

    /// <summary>
    /// Gets the maximum buffer size.
    /// </summary>
    public int MaxEntries { get; init; }

    /// <summary>
    /// Gets the number of debug entries.
    /// </summary>
    public int DebugCount { get; init; }

    /// <summary>
    /// Gets the number of information entries.
    /// </summary>
    public int InfoCount { get; init; }

    /// <summary>
    /// Gets the number of warning entries.
    /// </summary>
    public int WarningCount { get; init; }

    /// <summary>
    /// Gets the number of error entries.
    /// </summary>
    public int ErrorCount { get; init; }

    /// <summary>
    /// Gets the timestamp of the oldest entry.
    /// </summary>
    public DateTime? OldestEntry { get; init; }

    /// <summary>
    /// Gets the timestamp of the newest entry.
    /// </summary>
    public DateTime? NewestEntry { get; init; }

    /// <summary>
    /// Gets the ID of the newest entry (for polling).
    /// </summary>
    public long LatestId { get; init; }
}
