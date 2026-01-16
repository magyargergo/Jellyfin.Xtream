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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Manages reader sessions across all streams to coordinate position handoffs.
/// Thread-safe singleton that tracks FFprobe→FFmpeg transitions.
/// </summary>
public sealed class ReaderSessionManager : IDisposable
{
    /// <summary>
    /// Maximum age of a session before it's cleaned up.
    /// </summary>
    private const int SessionMaxAgeMinutes = 30;

    /// <summary>
    /// Interval between cleanup runs.
    /// </summary>
    private const int CleanupIntervalMs = 60000;

    private static readonly Lazy<ReaderSessionManager> _instance = new(() => new ReaderSessionManager());

    private readonly ConcurrentDictionary<string, ReaderSession> _sessions = new(StringComparer.Ordinal);
    private readonly Timer _cleanupTimer;
    private readonly ILogger<ReaderSessionManager>? _logger;
    private bool _disposed;

    /// <summary>
    /// Gets the singleton instance of the session manager.
    /// </summary>
    public static ReaderSessionManager Instance => _instance.Value;

    /// <summary>
    /// Gets the number of active sessions.
    /// </summary>
    public int SessionCount => _sessions.Count;

    /// <summary>
    /// Gets aggregate statistics across all sessions.
    /// </summary>
    public SessionManagerStatistics Statistics
    {
        get
        {
            var totalSessions = 0;
            var activeSessions = 0;
            var totalReaders = 0;
            var totalHandoffs = 0;
            long totalBytesRead = 0;

            foreach (var session in _sessions.Values)
            {
                totalSessions++;
                totalReaders += session.ReaderCount;
                totalHandoffs += session.HandoffCount;
                totalBytesRead += session.TotalBytesRead;

                if (session.PositionAgeMs < 10000)
                {
                    activeSessions++;
                }
            }

            return new SessionManagerStatistics
            {
                TotalSessions = totalSessions,
                ActiveSessions = activeSessions,
                TotalReaders = totalReaders,
                TotalHandoffs = totalHandoffs,
                TotalBytesRead = totalBytesRead,
            };
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ReaderSessionManager"/> class.
    /// </summary>
    /// <param name="logger">Optional logger.</param>
    private ReaderSessionManager(ILogger<ReaderSessionManager>? logger = null)
    {
        _logger = logger;
        _cleanupTimer = new Timer(CleanupOldSessions, state: null, CleanupIntervalMs, CleanupIntervalMs);
    }

    /// <summary>
    /// Gets or creates a session for the specified stream.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <returns>The reader session.</returns>
    public ReaderSession GetOrCreateSession(string streamId) =>
        _sessions.GetOrAdd(streamId, id => new ReaderSession(id));

    /// <summary>
    /// Gets an existing session if available.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <returns>The reader session, or null if not found.</returns>
    public ReaderSession? GetSession(string streamId) =>
        _sessions.TryGetValue(streamId, out var session) ? session : null;

    /// <summary>
    /// Removes a session for a stream.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <returns>True if the session was removed.</returns>
    public bool RemoveSession(string streamId) => _sessions.TryRemove(streamId, out _);

    /// <summary>
    /// Resets a session for a new stream.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    public void ResetSession(string streamId)
    {
        if (_sessions.TryGetValue(streamId, out var session))
        {
            session.Reset();
        }
    }

    /// <summary>
    /// Gets information about all active sessions.
    /// </summary>
    /// <returns>A list of session information.</returns>
    public IReadOnlyList<SessionInfo> GetAllSessions()
    {
        return
        [
            .. _sessions.Values.Select(s => new SessionInfo
            {
                SessionId = s.SessionId,
                CreatedAt = s.CreatedAt,
                ReaderCount = s.ReaderCount,
                HandoffCount = s.HandoffCount,
                TotalBytesRead = s.TotalBytesRead,
                LastPosition = s.LastReaderPosition,
                PositionAgeMs = s.PositionAgeMs,
            }),
        ];
    }

    /// <summary>
    /// Gets diagnostic information for all sessions.
    /// </summary>
    /// <returns>Formatted diagnostic string.</returns>
    public string GetDiagnostics()
    {
        var sessions = _sessions.Values.ToList();
        if (sessions.Count == 0)
        {
            return "ReaderSessionManager: No active sessions";
        }

        var lines = new List<string> { $"ReaderSessionManager: {sessions.Count} session(s)", string.Empty };

        foreach (var session in sessions.OrderByDescending(s => s.CreatedAt))
        {
            lines.Add(session.GetDiagnostics());
            lines.Add(string.Empty);
        }

        return string.Join('\n', lines);
    }

    private void CleanupOldSessions(object? state)
    {
        if (_disposed)
        {
            return;
        }

        var cutoff = DateTime.UtcNow.AddMinutes(-SessionMaxAgeMinutes);
        var toRemove = new List<string>();

        foreach (var kvp in _sessions)
        {
            var session = kvp.Value;

            // Remove if session is old AND has no recent activity
            if (session.CreatedAt < cutoff && session.PositionAgeMs > SessionMaxAgeMinutes * 60000)
            {
                toRemove.Add(kvp.Key);
            }
        }

        foreach (var key in toRemove)
        {
            if (_sessions.TryRemove(key, out _))
            {
                _logger?.LogDebugIfEnabled("Cleaned up stale reader session: {SessionId}", key);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cleanupTimer.Dispose();
        _sessions.Clear();
    }
}

/// <summary>
/// Aggregate statistics for the session manager.
/// </summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
public readonly struct SessionManagerStatistics : IEquatable<SessionManagerStatistics>
{
    /// <summary>
    /// Gets the total number of sessions ever created.
    /// </summary>
    public int TotalSessions { get; init; }

    /// <summary>
    /// Gets the number of currently active sessions.
    /// </summary>
    public int ActiveSessions { get; init; }

    /// <summary>
    /// Gets the total number of readers connected.
    /// </summary>
    public int TotalReaders { get; init; }

    /// <summary>
    /// Gets the total number of successful handoffs.
    /// </summary>
    public int TotalHandoffs { get; init; }

    /// <summary>
    /// Gets the total bytes read across all sessions.
    /// </summary>
    public long TotalBytesRead { get; init; }

    /// <inheritdoc />
    public bool Equals(SessionManagerStatistics other) =>
        TotalSessions == other.TotalSessions
        && ActiveSessions == other.ActiveSessions
        && TotalReaders == other.TotalReaders
        && TotalHandoffs == other.TotalHandoffs
        && TotalBytesRead == other.TotalBytesRead;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SessionManagerStatistics other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(TotalSessions, ActiveSessions, TotalReaders, TotalHandoffs, TotalBytesRead);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(SessionManagerStatistics left, SessionManagerStatistics right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(SessionManagerStatistics left, SessionManagerStatistics right) =>
        !left.Equals(right);
}

/// <summary>
/// Information about a single reader session.
/// </summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
public readonly struct SessionInfo : IEquatable<SessionInfo>
{
    /// <summary>
    /// Gets the session identifier.
    /// </summary>
    public string SessionId { get; init; }

    /// <summary>
    /// Gets when the session was created.
    /// </summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// Gets the number of readers that connected.
    /// </summary>
    public int ReaderCount { get; init; }

    /// <summary>
    /// Gets the number of successful handoffs.
    /// </summary>
    public int HandoffCount { get; init; }

    /// <summary>
    /// Gets the total bytes read.
    /// </summary>
    public long TotalBytesRead { get; init; }

    /// <summary>
    /// Gets the last known reader position.
    /// </summary>
    public long LastPosition { get; init; }

    /// <summary>
    /// Gets the age of the last position in milliseconds.
    /// </summary>
    public double PositionAgeMs { get; init; }

    /// <inheritdoc />
    public bool Equals(SessionInfo other) => SessionId == other.SessionId && CreatedAt == other.CreatedAt;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SessionInfo other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(SessionId, CreatedAt);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(SessionInfo left, SessionInfo right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(SessionInfo left, SessionInfo right) => !left.Equals(right);
}
