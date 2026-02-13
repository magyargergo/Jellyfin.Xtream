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
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Utility;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// <para>
/// Circular buffer write stream with self-overwriting behavior.
/// Ultra-optimized using unsafe code, direct memory operations, SIMD vectorization, and aggressive inlining.
/// Optimized for multi-core systems with cache-line awareness and hardware acceleration.
/// </para>
/// <para>
/// Thread Safety: This class is designed for SINGLE WRITER, MULTIPLE READERS pattern.
/// - Only ONE thread should write at a time.
/// - Multiple threads can read concurrently via CircularBufferReadStream.
/// - Uses memory barriers and atomic operations to ensure visibility across cores.
/// </para>
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="CircularBufferWriteStream"/> class.
/// </remarks>
/// <param name="bufferSize">Size in bytes of the internal buffer.</param>
/// <param name="loggerFactory">Optional logger factory for creating loggers.</param>
public sealed class CircularBufferWriteStream(int bufferSize, ILoggerFactory? loggerFactory = null) : Stream
{
    private volatile byte[]? _initData;

    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct CacheLinePadded
    {
        [FieldOffset(0)]
        public long Value;
    }

    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct CacheLinePaddedInt
    {
        [FieldOffset(0)]
        public int Value;
    }

    private const int NonTemporalThreshold = 262144;
    private const long ProgressLogIntervalBytes = 10 * 1024 * 1024; // Log every 10MB

    private readonly ILogger<CircularBufferWriteStream>? _logger =
        loggerFactory?.CreateLogger<CircularBufferWriteStream>();
    private readonly bool _isPowerOfTwo = (bufferSize & (bufferSize - 1)) == 0;
    private readonly long _bufferMask = bufferSize - 1;
    private long _lastProgressLogBytes;

    private CacheLinePadded _totalBytesWritten;
    private CacheLinePadded _lastDiscontinuityOffset;
    private CacheLinePaddedInt _discontinuityCount;
    private DateTime _lastDiscontinuityTime;

    // Connection state signaling for reader synchronization
    private volatile bool _isSourceConnected;
    private volatile bool _isReconnecting;
    private CacheLinePaddedInt _reconnectionAttempts;

    // Track last reader position for continuity between FFprobe and FFmpeg
    // When a reader disconnects, it records its position here so the next reader can continue
    private CacheLinePadded _lastReaderPosition;

    // Disposed state for signaling to readers that the stream is terminated
    private volatile bool _isDisposed;

    private const int TsPacketSize = 188;
    private const byte TsSyncByte = 0x47;

    /// <summary>
    /// Gets the maximal size in bytes of read/write chunks.
    /// </summary>
    public int BufferSize { get; } = bufferSize;

    /// <summary>
    /// Gets the internal buffer.
    /// </summary>
    /// <remarks>
    /// Intentionally exposed as byte[] for zero-copy reads. Readers access this buffer
    /// directly for high-performance streaming without memory copies.
    /// </remarks>
    public byte[] Buffer { get; } = new byte[bufferSize];

    /// <summary>
    /// Gets the number of bytes that have been written to this stream.
    /// </summary>
    public long TotalBytesWritten => Volatile.Read(ref _totalBytesWritten.Value);

    /// <summary>
    /// Gets the offset where the last stream discontinuity occurred.
    /// Readers should skip past this point to avoid reading stale data from before a reconnection.
    /// </summary>
    public long LastDiscontinuityOffset => Volatile.Read(ref _lastDiscontinuityOffset.Value);

    /// <summary>
    /// Gets the number of discontinuities (reconnections) that have occurred.
    /// </summary>
    public int DiscontinuityCount => Volatile.Read(ref _discontinuityCount.Value);

    /// <summary>
    /// Gets the time of the last discontinuity (UTC), or null if no discontinuities have occurred.
    /// </summary>
    public DateTime? LastDiscontinuityTime => _lastDiscontinuityTime == default ? null : _lastDiscontinuityTime;

    /// <summary>
    /// Gets a value indicating whether the source is currently connected and writing data.
    /// </summary>
    public bool IsSourceConnected => _isSourceConnected;

    /// <summary>
    /// Gets a value indicating whether a reconnection attempt is in progress.
    /// Readers should wait longer when this is true.
    /// </summary>
    public bool IsReconnecting => _isReconnecting;

    /// <summary>
    /// Gets the number of reconnection attempts since the stream started.
    /// </summary>
    public int ReconnectionAttempts => Volatile.Read(ref _reconnectionAttempts.Value);

    /// <summary>
    /// Gets the time of the last successful write (UTC).
    /// Used by readers to detect stale connections.
    /// </summary>
    public DateTime LastWriteTime { get; private set; }

    /// <summary>
    /// Gets the last known reader position for continuity between consecutive readers.
    /// Used when FFmpeg connects after FFprobe to continue from where FFprobe left off.
    /// </summary>
    public long LastReaderPosition => Volatile.Read(ref _lastReaderPosition.Value);

    /// <summary>
    /// Gets the time when the last reader disconnected.
    /// </summary>
    public DateTime LastReaderDisconnectTime { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the stream has been disposed.
    /// Readers should check this to detect when the source stream is terminated.
    /// </summary>
    public bool IsDisposed => _isDisposed;

    /// <summary>
    /// Gets the cached MPEG-TS initialization data (PAT + PMT + SPS/PPS packets).
    /// New readers should prepend this data so FFprobe/FFmpeg can initialize the H.264 decoder
    /// without waiting for the next IDR frame in the circular buffer.
    /// </summary>
    /// <returns>TS-aligned initialization packets, or empty if not yet available.</returns>
    public ReadOnlyMemory<byte> GetInitializationData()
    {
        var data = _initData;
        return data != null ? new ReadOnlyMemory<byte>(data) : ReadOnlyMemory<byte>.Empty;
    }

    /// <summary>
    /// Sets the initialization data (PAT + PMT + SPS/PPS) from the native streamer.
    /// Called once after the native streamer has cached PAT/PMT and SPS/PPS data.
    /// </summary>
    /// <param name="data">The initialization data, or null to clear.</param>
    public void SetInitializationData(byte[]? data)
    {
        _initData = data;
    }

    /// <summary>
    /// Records a reader's final position when it disconnects.
    /// The next reader can use this to continue from the same position.
    /// </summary>
    /// <param name="position">The reader's final read head position.</param>
    public void RecordReaderDisconnect(long position)
    {
        _ = Interlocked.Exchange(ref _lastReaderPosition.Value, position);
        LastReaderDisconnectTime = DateTime.UtcNow;

        _logger?.LogDebugIfEnabled(
            "Reader disconnected: position={PositionMB:F2}MB, writeHead={WriteHeadMB:F2}MB, lag={LagKB:F0}KB",
            position / (1024.0 * 1024.0),
            TotalBytesWritten / (1024.0 * 1024.0),
            (TotalBytesWritten - position) / 1024.0
        );
    }

    /// <summary>
    /// Consumes the last reader position if it's recent (within threshold).
    /// Returns -1 if no recent reader position is available.
    /// </summary>
    /// <param name="maxAgeMs">Maximum age in milliseconds for the position to be considered valid.</param>
    /// <returns>The last reader position, or -1 if not available or too old.</returns>
    public long ConsumeLastReaderPosition(int maxAgeMs = 10000)
    {
        var position = Volatile.Read(ref _lastReaderPosition.Value);
        if (position <= 0)
        {
            return -1;
        }

        // Check if the position is recent enough
        var age = DateTime.UtcNow - LastReaderDisconnectTime;
        if (age.TotalMilliseconds > maxAgeMs)
        {
            _logger?.LogDebugIfEnabled(
                "Reader position too old: age={AgeMs:F0}ms > maxAge={MaxAgeMs}ms",
                age.TotalMilliseconds,
                maxAgeMs
            );
            return -1;
        }

        // Check if position is still within valid buffer range
        var totalWritten = TotalBytesWritten;
        var minValidOffset = totalWritten - BufferSize + 524288; // 512KB safety margin

        if (position < minValidOffset || position > totalWritten)
        {
            _logger?.LogDebugIfEnabled(
                "Reader position out of range: position={PositionMB:F2}MB, validRange=[{MinMB:F2}MB, {MaxMB:F2}MB]",
                position / (1024.0 * 1024.0),
                minValidOffset / (1024.0 * 1024.0),
                totalWritten / (1024.0 * 1024.0)
            );
            return -1;
        }

        // Clear the position so it's only used once
        _ = Interlocked.Exchange(ref _lastReaderPosition.Value, 0);

        _logger?.LogDebugIfEnabled(
            "Reader position consumed: position={PositionMB:F2}MB, age={AgeMs:F0}ms",
            position / (1024.0 * 1024.0),
            age.TotalMilliseconds
        );
        return position;
    }

    /// <inheritdoc />
    public override long Position
    {
        get
        {
            var written = TotalBytesWritten;
            return _isPowerOfTwo ? (written & _bufferMask) : (written % BufferSize);
        }
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override bool CanRead => false;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) =>
        WriteSpan(new ReadOnlySpan<byte>(buffer, offset, count));

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer) => WriteSpan(buffer);

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        try
        {
            WriteSpan(new ReadOnlySpan<byte>(buffer, offset, count));
            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
    }

    /// <inheritdoc />
    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        if (cancellationToken.IsCancellationRequested)
        {
            await ValueTask.FromCanceled(cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            WriteSpan(buffer.Span);
            await ValueTask.CompletedTask.ConfigureAwait(false);
            return;
        }
        catch (Exception exception)
        {
            await ValueTask.FromException(exception).ConfigureAwait(false);
            return;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private unsafe void WriteSpan(ReadOnlySpan<byte> source)
    {
        if (source.IsEmpty)
        {
            return;
        }

        var remaining = source.Length;
        var sourceOffset = 0;
        var localWriteHead = Volatile.Read(ref _totalBytesWritten.Value);
        var localBufferSize = BufferSize;

        fixed (byte* srcPtr = source)
        {
            fixed (byte* dstPtr = Buffer)
            {
                while (remaining > 0)
                {
                    var currentPosition = _isPowerOfTwo
                        ? (localWriteHead & _bufferMask)
                        : (localWriteHead % localBufferSize);
                    var writable = (int)Math.Min(remaining, localBufferSize - currentPosition);

                    if (writable >= SimdMemoryCopy.SimdThreshold)
                    {
                        SimdMemoryCopy.Copy(
                            srcPtr + sourceOffset,
                            dstPtr + currentPosition,
                            writable,
                            useNonTemporal: writable >= NonTemporalThreshold
                        );
                    }
                    else
                    {
                        System.Buffer.MemoryCopy(
                            srcPtr + sourceOffset,
                            dstPtr + currentPosition,
                            localBufferSize - currentPosition,
                            writable
                        );
                    }

                    sourceOffset += writable;
                    remaining -= writable;
                    localWriteHead += writable;
                }
            }
        }

        // Interlocked.Exchange provides a full memory barrier (lock xchg on x86),
        // which ensures that all preceding stores -- including any non-temporal (NT) SIMD
        // stores from SimdMemoryCopy.Copy -- are globally visible to reader threads before
        // the updated write position is published. On x86, the sfence emitted by
        // Sse2.MemoryFence() at the end of SimdMemoryCopy.Copy orders NT stores relative
        // to subsequent stores, and Interlocked.Exchange's implicit mfence provides the
        // acquire-release semantics needed for cross-thread visibility.
        _ = Interlocked.Exchange(ref _totalBytesWritten.Value, localWriteHead);
        LastWriteTime = DateTime.UtcNow;

        // Periodic progress logging (every 10MB) to track data flow without spam
        if (localWriteHead - _lastProgressLogBytes >= ProgressLogIntervalBytes)
        {
            _lastProgressLogBytes = localWriteHead;
            _logger?.LogDebugIfEnabled(
                "Buffer write progress: {TotalMB:F1}MB written, position={Position}, discontinuities={DiscontinuityCount}",
                localWriteHead / (1024.0 * 1024.0),
                Position,
                DiscontinuityCount
            );
        }

        // Init data (PAT/PMT/SPS/PPS) is now set by the native streamer via SetInitializationData()
        // instead of scanning every packet in C#. This avoids duplicated TS parsing logic.
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Flush() { }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// <para>
    /// Resets the buffer for a new stream session, resetting counters.
    /// Note: The buffer is NOT cleared for performance reasons. Readers track position
    /// via _totalBytesWritten, so they will never read stale data as long as they
    /// respect the write head position.
    /// </para>
    /// <para>
    /// Thread Safety: Safe to call while readers are active. Readers will see the reset
    /// atomically and will wait for new data to be written.
    /// </para>
    /// </summary>
    public void Reset()
    {
        var previousBytes = TotalBytesWritten;
        var previousDiscontinuities = DiscontinuityCount;

        _ = Interlocked.Exchange(ref _totalBytesWritten.Value, 0L);
        _ = Interlocked.Exchange(ref _lastDiscontinuityOffset.Value, 0L);
        _ = Interlocked.Exchange(ref _discontinuityCount.Value, 0);
        _ = Interlocked.Exchange(ref _reconnectionAttempts.Value, 0);
        _lastDiscontinuityTime = default;
        LastWriteTime = default;
        _isSourceConnected = false;
        _isReconnecting = false;
        _lastProgressLogBytes = 0;
        _initData = null;

        _logger?.LogDebugIfEnabled(
            "Buffer reset: cleared {PreviousMB:F1}MB, {PreviousDiscontinuities} discontinuities",
            previousBytes / (1024.0 * 1024.0),
            previousDiscontinuities
        );
    }

    /// <summary>
    /// Marks the current write position as a discontinuity point.
    /// Called when the source stream reconnects after an EOF or error.
    /// Readers will skip past this point to avoid reading stale pre-disconnect data
    /// that would cause video loops or timestamp discontinuities.
    /// </summary>
    public void MarkDiscontinuity()
    {
        var currentOffset = Volatile.Read(ref _totalBytesWritten.Value);
        _ = Interlocked.Exchange(ref _lastDiscontinuityOffset.Value, currentOffset);
        var newCount = Interlocked.Increment(ref _discontinuityCount.Value);
        _lastDiscontinuityTime = DateTime.UtcNow;

        _logger?.LogDebugIfEnabled(
            "Discontinuity #{Count} marked at offset {OffsetMB:F2}MB",
            newCount,
            currentOffset / (1024.0 * 1024.0)
        );
    }

    /// <summary>
    /// Aligns the current write position to the next MPEG-TS packet boundary (188 bytes).
    /// Call this before marking a discontinuity during provider switch to ensure no partial
    /// packets exist at the switch boundary. This prevents decoder errors from partial packets.
    /// </summary>
    /// <remarks>
    /// If the current position is not aligned, null packets (PID 0x1FFF) are written to
    /// pad to the next boundary. Per ISO/IEC 13818-1, null packets are used for CBR padding
    /// and should be silently discarded by decoders.
    /// </remarks>
    /// <returns>The number of padding bytes written (0 if already aligned).</returns>
    public int AlignToPacketBoundary()
    {
        var currentOffset = Volatile.Read(ref _totalBytesWritten.Value);
        var remainder = (int)(currentOffset % TsPacketSize);

        if (remainder == 0)
        {
            return 0; // Already aligned
        }

        var paddingNeeded = TsPacketSize - remainder;

        // Create a null packet for padding
        // Null packet: sync byte (0x47), PID 0x1FFF, no adaptation field, payload all 0xFF
        Span<byte> nullPacket = stackalloc byte[TsPacketSize];
        nullPacket[0] = TsSyncByte; // Sync byte
        nullPacket[1] = 0x1F; // PID high byte (0x1FFF >> 8) with TEI=0, PUSI=0, priority=0
        nullPacket[2] = 0xFF; // PID low byte
        nullPacket[3] = 0x10; // Adaptation field control = 01 (payload only), CC = 0
        nullPacket[4..].Fill(0xFF); // Payload filled with 0xFF

        // Write only the padding portion needed
        WriteSpan(nullPacket[..paddingNeeded]);

        return paddingNeeded;
    }

    /// <summary>
    /// Marks a discontinuity with automatic packet boundary alignment.
    /// This is the recommended method for provider switches to ensure clean boundaries.
    /// </summary>
    /// <returns>The number of padding bytes written for alignment.</returns>
    public int MarkDiscontinuityAligned()
    {
        var paddingBytes = AlignToPacketBoundary();
        MarkDiscontinuity();
        return paddingBytes;
    }

    /// <summary>
    /// Signals that the source is now connected and data is flowing.
    /// Called by the writer when HTTP connection is established successfully.
    /// </summary>
    public void SignalSourceConnected()
    {
        _isSourceConnected = true;
        _isReconnecting = false;
        LastWriteTime = DateTime.UtcNow;

        _logger?.LogDebugIfEnabled(
            "Source connected: totalWritten={TotalMB:F2}MB, reconnectionAttempts={Attempts}",
            TotalBytesWritten / (1024.0 * 1024.0),
            ReconnectionAttempts
        );
    }

    /// <summary>
    /// Signals that a reconnection attempt is starting.
    /// Readers will wait longer when reconnection is in progress.
    /// </summary>
    public void SignalReconnecting()
    {
        _isReconnecting = true;
        _isSourceConnected = false;
        var attempts = Interlocked.Increment(ref _reconnectionAttempts.Value);

        _logger?.LogDebugIfEnabled(
            "Source reconnecting: attempt #{Attempts}, totalWritten={TotalMB:F2}MB",
            attempts,
            TotalBytesWritten / (1024.0 * 1024.0)
        );
    }

    /// <summary>
    /// Signals that the source has disconnected (EOF, error, or intentional close).
    /// </summary>
    public void SignalSourceDisconnected()
    {
        _isSourceConnected = false;

        _logger?.LogDebugIfEnabled(
            "Source disconnected: totalWritten={TotalMB:F2}MB, discontinuities={Count}",
            TotalBytesWritten / (1024.0 * 1024.0),
            DiscontinuityCount
        );
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        _isDisposed = true;
        base.Dispose(disposing);
    }
}
