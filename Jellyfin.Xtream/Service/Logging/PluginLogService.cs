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
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.Logging;

/// <summary>
/// Thread-safe in-memory log buffer with circular eviction.
/// </summary>
public sealed class PluginLogService : IPluginLogService
{
    private const int DefaultMaxEntries = 5000;

    private readonly object _lock = new();
    private readonly LinkedList<PluginLogEntry> _entries = new();
    private long _nextId = 1;

    /// <inheritdoc />
    public int EntryCount
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    /// <inheritdoc />
    public int MaxEntries => GetMaxEntries();

    /// <inheritdoc />
    public void Log(
        LogLevel level,
        string category,
        string message,
        Exception? exception = null,
        bool isDebug = false,
        string? streamId = null,
        string? channelName = null
    )
    {
        var entry = new PluginLogEntry
        {
            Id = Interlocked.Increment(ref _nextId),
            Timestamp = DateTime.UtcNow,
            Level = level,
            Category = SimplifyCategory(category),
            Message = message,
            ExceptionMessage = exception?.Message,
            ExceptionStackTrace = exception?.StackTrace,
            IsDebug = isDebug,
            StreamId = streamId,
            ChannelName = channelName,
        };

        lock (_lock)
        {
            _entries.AddLast(entry);

            int maxEntries = GetMaxEntries();
            while (_entries.Count > maxEntries)
            {
                _entries.RemoveFirst();
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginLogEntry> GetEntries(
        LogLevel minLevel = LogLevel.Trace,
        bool includeDebug = true,
        string? category = null,
        string? streamId = null,
        string? searchText = null,
        int skip = 0,
        int take = 100
    )
    {
        lock (_lock)
        {
            var query = _entries.AsEnumerable().Reverse();

            query = ApplyFilters(query, minLevel, includeDebug, category, streamId, searchText);

            return query.Skip(skip).Take(take).ToList();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginLogEntry> GetEntriesAfter(
        long afterId,
        LogLevel minLevel = LogLevel.Trace,
        bool includeDebug = true
    )
    {
        lock (_lock)
        {
            var query = _entries.Where(e => e.Id > afterId);

            if (minLevel > LogLevel.Trace)
            {
                query = query.Where(e => e.Level >= minLevel);
            }

            if (!includeDebug)
            {
                query = query.Where(e => !e.IsDebug);
            }

            return query.ToList();
        }
    }

    /// <inheritdoc />
    public PluginLogStats GetStats()
    {
        lock (_lock)
        {
            var entries = _entries.ToList();

            return new PluginLogStats
            {
                TotalEntries = entries.Count,
                MaxEntries = GetMaxEntries(),
                DebugCount = entries.Count(e => e.IsDebug),
                InfoCount = entries.Count(e => e.Level == LogLevel.Information && !e.IsDebug),
                WarningCount = entries.Count(e => e.Level == LogLevel.Warning),
                ErrorCount = entries.Count(e => e.Level >= LogLevel.Error),
                OldestEntry = entries.FirstOrDefault()?.Timestamp,
                NewestEntry = entries.LastOrDefault()?.Timestamp,
                LatestId = entries.LastOrDefault()?.Id ?? 0,
            };
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }

    private static int GetMaxEntries()
    {
        try
        {
            return Plugin.Instance?.Configuration?.LogViewerMaxEntries ?? DefaultMaxEntries;
        }
        catch
        {
            return DefaultMaxEntries;
        }
    }

    private static string SimplifyCategory(string category)
    {
        if (string.IsNullOrEmpty(category))
        {
            return "Unknown";
        }

        int lastDot = category.LastIndexOf('.');
        return lastDot >= 0 ? category[(lastDot + 1)..] : category;
    }

    private static IEnumerable<PluginLogEntry> ApplyFilters(
        IEnumerable<PluginLogEntry> query,
        LogLevel minLevel,
        bool includeDebug,
        string? category,
        string? streamId,
        string? searchText
    )
    {
        if (minLevel > LogLevel.Trace)
        {
            query = query.Where(e => e.Level >= minLevel);
        }

        if (!includeDebug)
        {
            query = query.Where(e => !e.IsDebug);
        }

        if (!string.IsNullOrEmpty(category))
        {
            query = query.Where(e => e.Category.Contains(category, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(streamId))
        {
            query = query.Where(e =>
                e.StreamId != null && e.StreamId.Contains(streamId, StringComparison.OrdinalIgnoreCase)
            );
        }

        if (!string.IsNullOrEmpty(searchText))
        {
            query = query.Where(e =>
                e.Message.Contains(searchText, StringComparison.OrdinalIgnoreCase)
                || (e.ChannelName?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false)
            );
        }

        return query;
    }
}
