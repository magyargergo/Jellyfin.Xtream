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
using System.Runtime.CompilerServices;
using System.Threading;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Represents a reader session that tracks position continuity across multiple readers.
/// This enables seamless handoff from FFprobe to FFmpeg by preserving read state.
/// </summary>
/// <remarks>
/// <para>
/// The session maintains a timeline of reader positions that allows subsequent readers
/// to continue from where the previous reader left off, ensuring:
/// </para>
/// <list type="bullet">
/// <item><description>No repeated content (video doesn't jump back)</description></item>
/// <item><description>No skipped content (video doesn't jump forward)</description></item>
/// <item><description>Proper keyframe alignment for clean video start</description></item>
/// <item><description>PCR timing reset to avoid false jitter readings</description></item>
/// </list>
/// </remarks>
/// <remarks>
/// Initializes a new instance of the <see cref="ReaderSession"/> class.
/// </remarks>
/// <param name="sessionId">The session identifier.</param>
public sealed class ReaderSession(string sessionId)
{
    /// <summary>
    /// Maximum age in milliseconds for a reader position to be considered valid.
    /// FFprobe typically disconnects 1-3 seconds before FFmpeg connects.
    /// </summary>
    public const int DefaultMaxPositionAgeMs = 10000;

    /// <summary>
    /// Minimum buffer margin to ensure position is still readable.
    /// </summary>
    public const int DefaultSafetyMarginBytes = 524288; // 512KB

    private long _lastReaderPosition;
    private long _lastReaderTimestampTicks;
    private int _readerCount;
    private long _totalBytesRead;
    private int _handoffCount;

    /// <summary>
    /// Gets the session identifier.
    /// </summary>
    public string SessionId { get; } = sessionId;

    /// <summary>
    /// Gets the creation time of this session.
    /// </summary>
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    /// <summary>
    /// Gets the last known reader position.
    /// </summary>
    public long LastReaderPosition => Volatile.Read(ref _lastReaderPosition);

    /// <summary>
    /// Gets the timestamp when the last reader position was recorded.
    /// </summary>
    public DateTime LastReaderTimestamp => new(Volatile.Read(ref _lastReaderTimestampTicks), DateTimeKind.Utc);

    /// <summary>
    /// Gets the number of readers that have connected to this session.
    /// </summary>
    public int ReaderCount => Volatile.Read(ref _readerCount);

    /// <summary>
    /// Gets the total bytes read across all readers in this session.
    /// </summary>
    public long TotalBytesRead => Volatile.Read(ref _totalBytesRead);

    /// <summary>
    /// Gets the number of successful handoffs between readers.
    /// </summary>
    public int HandoffCount => Volatile.Read(ref _handoffCount);

    /// <summary>
    /// Gets the age of the last reader position in milliseconds.
    /// </summary>
    public double PositionAgeMs
    {
        get
        {
            var ticks = Volatile.Read(ref _lastReaderTimestampTicks);
            return ticks == 0
                ? double.MaxValue
                : (DateTime.UtcNow.Ticks - ticks) / (double)TimeSpan.TicksPerMillisecond;
        }
    }

    /// <summary>
    /// Records a reader connecting to this session.
    /// </summary>
    /// <returns>The reader sequence number (1-based).</returns>
    public int RecordReaderConnect() => Interlocked.Increment(ref _readerCount);

    /// <summary>
    /// Records a reader disconnecting and saves its final position.
    /// </summary>
    /// <param name="position">The reader's final read position.</param>
    /// <param name="bytesRead">The total bytes read by this reader.</param>
    public void RecordReaderDisconnect(long position, long bytesRead)
    {
        if (position > 0)
        {
            _ = Interlocked.Exchange(ref _lastReaderPosition, position);
            _ = Interlocked.Exchange(ref _lastReaderTimestampTicks, DateTime.UtcNow.Ticks);
        }

        if (bytesRead > 0)
        {
            _ = Interlocked.Add(ref _totalBytesRead, bytesRead);
        }
    }

    /// <summary>
    /// Attempts to get a continuation position for a new reader.
    /// Returns the last reader's position if it's recent enough and still valid.
    /// </summary>
    /// <param name="currentWriteHead">Current write head position for validation.</param>
    /// <param name="bufferSize">Buffer size for validation.</param>
    /// <param name="maxAgeMs">Maximum age for position to be considered valid.</param>
    /// <param name="safetyMarginBytes">Minimum distance from buffer start.</param>
    /// <returns>The continuation position, or -1 if not available.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long TryGetContinuationPosition(
        long currentWriteHead,
        int bufferSize,
        int maxAgeMs = DefaultMaxPositionAgeMs,
        int safetyMarginBytes = DefaultSafetyMarginBytes
    )
    {
        var position = Volatile.Read(ref _lastReaderPosition);
        if (position <= 0)
        {
            return -1;
        }

        // Check if position is recent enough
        var ageMs = PositionAgeMs;
        if (ageMs > maxAgeMs)
        {
            return -1;
        }

        // Check if position is still within valid buffer range
        var minValidOffset = currentWriteHead - bufferSize + safetyMarginBytes;
        if (position < minValidOffset || position > currentWriteHead)
        {
            return -1;
        }

        // Valid position - increment handoff counter
        _ = Interlocked.Increment(ref _handoffCount);
        return position;
    }

    /// <summary>
    /// Consumes the continuation position, clearing it so it's only used once.
    /// This should be called after successfully starting from the continuation position.
    /// </summary>
    /// <param name="currentWriteHead">Current write head position for validation.</param>
    /// <param name="bufferSize">Buffer size for validation.</param>
    /// <param name="maxAgeMs">Maximum age for position to be considered valid.</param>
    /// <param name="safetyMarginBytes">Minimum distance from buffer start.</param>
    /// <returns>The continuation position, or -1 if not available.</returns>
    public long ConsumeContinuationPosition(
        long currentWriteHead,
        int bufferSize,
        int maxAgeMs = DefaultMaxPositionAgeMs,
        int safetyMarginBytes = DefaultSafetyMarginBytes
    )
    {
        var position = TryGetContinuationPosition(currentWriteHead, bufferSize, maxAgeMs, safetyMarginBytes);

        if (position > 0)
        {
            // Clear the position so it's only used once
            _ = Interlocked.Exchange(ref _lastReaderPosition, 0);
        }

        return position;
    }

    /// <summary>
    /// Resets the session state for a new stream.
    /// </summary>
    public void Reset()
    {
        _ = Interlocked.Exchange(ref _lastReaderPosition, 0);
        _ = Interlocked.Exchange(ref _lastReaderTimestampTicks, 0);
        // Don't reset reader count or total bytes - useful for diagnostics
    }

    /// <summary>
    /// Gets diagnostic information about the session.
    /// </summary>
    /// <returns>Formatted diagnostic string.</returns>
    public string GetDiagnostics()
    {
        var ageMs = PositionAgeMs;
        var ageStr = ageMs == double.MaxValue ? "N/A" : $"{ageMs:F1}ms";

        return $"ReaderSession[{SessionId}]:\n"
            + $"  Created: {CreatedAt:HH:mm:ss.fff}\n"
            + $"  Readers: {ReaderCount}\n"
            + $"  Handoffs: {HandoffCount}\n"
            + $"  Total Read: {TotalBytesRead / 1048576.0:F1}MB\n"
            + $"  Last Position: {LastReaderPosition}\n"
            + $"  Position Age: {ageStr}";
    }
}
