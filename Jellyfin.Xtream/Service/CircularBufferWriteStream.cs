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
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using System.Threading.Tasks;
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

    private static readonly int _simdThreshold = DetermineSimdThreshold();
    private static readonly bool _avx512Supported = Avx512F.IsSupported;
    private static readonly bool _avx2Supported = Avx2.IsSupported;
    private static readonly bool _sse2Supported = Sse2.IsSupported;
    private static readonly bool _advSimdSupported = AdvSimd.IsSupported;
    private static readonly bool _advSimdArm64Supported = AdvSimd.Arm64.IsSupported;
    private static readonly int _prefetchDistance = DeterminePrefetchDistance();

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

                    if (writable >= _simdThreshold)
                    {
                        CopyMemorySimd(srcPtr + sourceOffset, dstPtr + currentPosition, writable);
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
    }

    /// <summary>
    /// Determines optimal SIMD threshold based on CPU capabilities.
    /// Lower-end CPUs get higher threshold to avoid SIMD overhead.
    /// ARM NEON has similar characteristics to SSE2 for threshold selection.
    /// </summary>
    private static int DetermineSimdThreshold()
    {
        return Avx2.IsSupported ? 512
            : Sse2.IsSupported ? 1024
            : AdvSimd.IsSupported ? 1024
            : 4096;
    }

    /// <summary>
    /// Determines optimal prefetch distance based on CPU capabilities.
    /// Smaller caches on low-end CPUs need shorter prefetch distance to avoid cache pollution.
    /// AVX2 systems typically have larger caches supporting longer prefetch distances.
    /// </summary>
    private static int DeterminePrefetchDistance() => Avx2.IsSupported ? 256 : 128;

    /// <summary>
    /// Hardware-accelerated memory copy using SIMD instructions.
    /// Optimized for multi-core systems with AVX-512/AVX2/SSE support.
    /// Uses non-temporal stores for large copies to bypass cache pollution.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static unsafe void CopyMemorySimd(byte* src, byte* dst, int length)
    {
        var offset = 0;
        var useNonTemporal = length >= NonTemporalThreshold;

        if (_avx512Supported && length >= 64)
        {
            // AVX-512 path - 512-bit (64-byte) vectors
            // Process 128 bytes at a time using two vector registers for better pipelining.
            // Interleaving loads before stores hides memory latency and utilizes out-of-order execution.

            // Check alignment for non-temporal stores (64-byte alignment for AVX-512)
            var isAligned = ((nuint)(dst + offset) & 63) == 0;
            var useNonTemporalAvx512 = useNonTemporal && isAligned;

            if (useNonTemporalAvx512 && length >= 128)
            {
                // 128-byte non-temporal with 2x pipelining
                var avx512x2Length = length & -128;
                for (; offset < avx512x2Length; offset += 128)
                {
                    // Prefetch 2-3 iterations ahead for 128-byte stride
                    if (offset + 384 < length)
                    {
                        Sse.Prefetch0(src + offset + 256);
                        Sse.Prefetch0(src + offset + 320);
                    }

                    var vec0 = Avx512F.LoadVector512(src + offset);
                    var vec1 = Avx512F.LoadVector512(src + offset + 64);
                    Avx512F.StoreAlignedNonTemporal(dst + offset, vec0);
                    Avx512F.StoreAlignedNonTemporal(dst + offset + 64, vec1);
                }

                // Handle remaining 64-byte chunk with non-temporal
                var avx512Length = length & -64;
                for (; offset < avx512Length; offset += 64)
                {
                    var vec = Avx512F.LoadVector512(src + offset);
                    Avx512F.StoreAlignedNonTemporal(dst + offset, vec);
                }
            }
            else if (useNonTemporalAvx512)
            {
                // 64-byte non-temporal (smaller buffers)
                var avx512Length = length & -64;
                for (; offset < avx512Length; offset += 64)
                {
                    if (offset + _prefetchDistance < length)
                    {
                        Sse.Prefetch0(src + offset + _prefetchDistance);
                    }

                    var vec = Avx512F.LoadVector512(src + offset);
                    Avx512F.StoreAlignedNonTemporal(dst + offset, vec);
                }
            }
            else if (length >= 128)
            {
                // Regular stores with 128-byte pipelining
                var avx512x2Length = length & -128;
                for (; offset < avx512x2Length; offset += 128)
                {
                    // Prefetch 2-3 iterations ahead for 128-byte stride
                    if (offset + 384 < length)
                    {
                        Sse.Prefetch0(src + offset + 256);
                        Sse.Prefetch0(src + offset + 320);
                    }

                    var vec0 = Avx512F.LoadVector512(src + offset);
                    var vec1 = Avx512F.LoadVector512(src + offset + 64);
                    Avx512F.Store(dst + offset, vec0);
                    Avx512F.Store(dst + offset + 64, vec1);
                }

                // Handle remaining 64-byte chunk
                var avx512Length = length & -64;
                for (; offset < avx512Length; offset += 64)
                {
                    var vec = Avx512F.LoadVector512(src + offset);
                    Avx512F.Store(dst + offset, vec);
                }
            }
            else
            {
                // 64-byte only (smaller buffers)
                var avx512Length = length & -64;
                for (; offset < avx512Length; offset += 64)
                {
                    if (offset + _prefetchDistance < length)
                    {
                        Sse.Prefetch0(src + offset + _prefetchDistance);
                    }

                    var vec = Avx512F.LoadVector512(src + offset);
                    Avx512F.Store(dst + offset, vec);
                }
            }
        }
        else if (_avx2Supported && length >= 32)
        {
            // AVX2 path - 256-bit (32-byte) vectors
            // Process 64 bytes at a time using two vector registers for better pipelining.
            // This matches cache line size (64 bytes) for optimal memory throughput.
            if (useNonTemporal && Sse2.IsSupported)
            {
                // Non-temporal stores for large copies to avoid cache pollution
                var isAligned = ((nuint)(dst + offset) & 15) == 0;

                if (isAligned && length >= 64)
                {
                    // 64-byte non-temporal with pipelining
                    var avx2x2Length = length & -64;
                    for (; offset < avx2x2Length; offset += 64)
                    {
                        if (offset + _prefetchDistance < length)
                        {
                            Sse.Prefetch0(src + offset + _prefetchDistance);
                        }

                        var vec0 = Avx.LoadVector256(src + offset);
                        var vec1 = Avx.LoadVector256(src + offset + 32);
                        var lo0 = vec0.GetLower();
                        var hi0 = vec0.GetUpper();
                        var lo1 = vec1.GetLower();
                        var hi1 = vec1.GetUpper();
                        Sse2.StoreAlignedNonTemporal(dst + offset, lo0);
                        Sse2.StoreAlignedNonTemporal(dst + offset + 16, hi0);
                        Sse2.StoreAlignedNonTemporal(dst + offset + 32, lo1);
                        Sse2.StoreAlignedNonTemporal(dst + offset + 48, hi1);
                    }

                    // Handle remaining 32-byte chunk
                    var avx2Length = length & -32;
                    for (; offset < avx2Length; offset += 32)
                    {
                        var vec = Avx.LoadVector256(src + offset);
                        var lo = vec.GetLower();
                        var hi = vec.GetUpper();
                        Sse2.StoreAlignedNonTemporal(dst + offset, lo);
                        Sse2.StoreAlignedNonTemporal(dst + offset + 16, hi);
                    }
                }
                else if (isAligned)
                {
                    // 32-byte non-temporal (smaller buffers)
                    var avx2Length = length & -32;
                    for (; offset < avx2Length; offset += 32)
                    {
                        if (offset + _prefetchDistance < length)
                        {
                            Sse.Prefetch0(src + offset + _prefetchDistance);
                        }

                        var vec = Avx.LoadVector256(src + offset);
                        var lo = vec.GetLower();
                        var hi = vec.GetUpper();
                        Sse2.StoreAlignedNonTemporal(dst + offset, lo);
                        Sse2.StoreAlignedNonTemporal(dst + offset + 16, hi);
                    }
                }
                else
                {
                    // Unaligned destination - use regular stores with 64-byte pipelining
                    if (length >= 64)
                    {
                        var avx2x2Length = length & -64;
                        for (; offset < avx2x2Length; offset += 64)
                        {
                            if (offset + _prefetchDistance < length)
                            {
                                Sse.Prefetch0(src + offset + _prefetchDistance);
                            }

                            var vec0 = Avx.LoadVector256(src + offset);
                            var vec1 = Avx.LoadVector256(src + offset + 32);
                            Avx.Store(dst + offset, vec0);
                            Avx.Store(dst + offset + 32, vec1);
                        }
                    }

                    // Handle remaining 32-byte chunk
                    var avx2Length = length & -32;
                    for (; offset < avx2Length; offset += 32)
                    {
                        var vec = Avx.LoadVector256(src + offset);
                        Avx.Store(dst + offset, vec);
                    }
                }
            }
            else
            {
                // Regular stores with 64-byte pipelining
                // Note: AVX2 implies SSE support, so no need to check Sse.IsSupported
                if (length >= 64)
                {
                    var avx2x2Length = length & -64;
                    for (; offset < avx2x2Length; offset += 64)
                    {
                        if (offset + _prefetchDistance < length)
                        {
                            Sse.Prefetch0(src + offset + _prefetchDistance);
                        }

                        var vec0 = Avx.LoadVector256(src + offset);
                        var vec1 = Avx.LoadVector256(src + offset + 32);
                        Avx.Store(dst + offset, vec0);
                        Avx.Store(dst + offset + 32, vec1);
                    }
                }

                // Handle remaining 32-byte chunk
                var avx2Length = length & -32;
                for (; offset < avx2Length; offset += 32)
                {
                    if (offset + _prefetchDistance < length)
                    {
                        Sse.Prefetch0(src + offset + _prefetchDistance);
                    }

                    var vec = Avx.LoadVector256(src + offset);
                    Avx.Store(dst + offset, vec);
                }
            }
        }
        else if (_sse2Supported && length >= 16)
        {
            // SSE2 path - 128-bit (16-byte) vectors
            // Process 64 bytes at a time using four vector registers to match cache line size.
            // This improves memory throughput by better utilizing the memory subsystem.
            if (useNonTemporal)
            {
                var isAligned = ((nuint)(dst + offset) & 15) == 0;

                if (isAligned && length >= 64)
                {
                    // 64-byte non-temporal with 4x pipelining
                    var sse2x4Length = length & -64;
                    for (; offset < sse2x4Length; offset += 64)
                    {
                        if (offset + _prefetchDistance < length)
                        {
                            Sse.Prefetch0(src + offset + _prefetchDistance);
                        }

                        var vec0 = Sse2.LoadVector128(src + offset);
                        var vec1 = Sse2.LoadVector128(src + offset + 16);
                        var vec2 = Sse2.LoadVector128(src + offset + 32);
                        var vec3 = Sse2.LoadVector128(src + offset + 48);
                        Sse2.StoreAlignedNonTemporal(dst + offset, vec0);
                        Sse2.StoreAlignedNonTemporal(dst + offset + 16, vec1);
                        Sse2.StoreAlignedNonTemporal(dst + offset + 32, vec2);
                        Sse2.StoreAlignedNonTemporal(dst + offset + 48, vec3);
                    }

                    // Handle remaining 16-byte chunks
                    var sse2Length = length & -16;
                    for (; offset < sse2Length; offset += 16)
                    {
                        var vec = Sse2.LoadVector128(src + offset);
                        Sse2.StoreAlignedNonTemporal(dst + offset, vec);
                    }
                }
                else if (isAligned)
                {
                    // 16-byte non-temporal (smaller buffers)
                    var sse2Length = length & -16;
                    for (; offset < sse2Length; offset += 16)
                    {
                        if (offset + _prefetchDistance < length)
                        {
                            Sse.Prefetch0(src + offset + _prefetchDistance);
                        }

                        var vec = Sse2.LoadVector128(src + offset);
                        Sse2.StoreAlignedNonTemporal(dst + offset, vec);
                    }
                }
                else
                {
                    // Unaligned - use regular stores with 64-byte pipelining
                    if (length >= 64)
                    {
                        var sse2x4Length = length & -64;
                        for (; offset < sse2x4Length; offset += 64)
                        {
                            if (offset + _prefetchDistance < length)
                            {
                                Sse.Prefetch0(src + offset + _prefetchDistance);
                            }

                            var vec0 = Sse2.LoadVector128(src + offset);
                            var vec1 = Sse2.LoadVector128(src + offset + 16);
                            var vec2 = Sse2.LoadVector128(src + offset + 32);
                            var vec3 = Sse2.LoadVector128(src + offset + 48);
                            Sse2.Store(dst + offset, vec0);
                            Sse2.Store(dst + offset + 16, vec1);
                            Sse2.Store(dst + offset + 32, vec2);
                            Sse2.Store(dst + offset + 48, vec3);
                        }
                    }

                    // Handle remaining 16-byte chunks
                    var sse2Length = length & -16;
                    for (; offset < sse2Length; offset += 16)
                    {
                        var vec = Sse2.LoadVector128(src + offset);
                        Sse2.Store(dst + offset, vec);
                    }
                }
            }
            else
            {
                // Regular stores with 64-byte pipelining
                if (length >= 64)
                {
                    var sse2x4Length = length & -64;
                    for (; offset < sse2x4Length; offset += 64)
                    {
                        if (offset + _prefetchDistance < length)
                        {
                            Sse.Prefetch0(src + offset + _prefetchDistance);
                        }

                        var vec0 = Sse2.LoadVector128(src + offset);
                        var vec1 = Sse2.LoadVector128(src + offset + 16);
                        var vec2 = Sse2.LoadVector128(src + offset + 32);
                        var vec3 = Sse2.LoadVector128(src + offset + 48);
                        Sse2.Store(dst + offset, vec0);
                        Sse2.Store(dst + offset + 16, vec1);
                        Sse2.Store(dst + offset + 32, vec2);
                        Sse2.Store(dst + offset + 48, vec3);
                    }
                }

                // Handle remaining 16-byte chunks
                var sse2Length = length & -16;
                for (; offset < sse2Length; offset += 16)
                {
                    if (offset + _prefetchDistance < length)
                    {
                        Sse.Prefetch0(src + offset + _prefetchDistance);
                    }

                    var vec = Sse2.LoadVector128(src + offset);
                    Sse2.Store(dst + offset, vec);
                }
            }
        }
        else if (_advSimdSupported && length >= 16)
        {
            // ARM NEON path - 128-bit vectors
            // Note: ARM relies on hardware prefetching; PRFM is not exposed via AdvSimd intrinsics.
            // Modern ARM cores (Apple Silicon, Cortex-A78+) have aggressive hardware prefetchers.
            // No software prefetch needed unlike x86 SSE/AVX paths.
            //
            // Memory barrier note: ARM NEON uses regular stores (not non-temporal),
            // so no explicit memory barrier is required for cache coherency.
            if (_advSimdArm64Supported && length >= 64)
            {
                // Process 64 bytes at a time using four vector registers
                // This maximizes memory bandwidth utilization on ARM64 cores with wide
                // execution units (Apple M-series, Cortex-X series).
                // JIT should emit LDP/STP pairs for optimal throughput.
                var advSimd64Length = length & -64;
                for (; offset < advSimd64Length; offset += 64)
                {
                    var vec0 = AdvSimd.LoadVector128(src + offset);
                    var vec1 = AdvSimd.LoadVector128(src + offset + 16);
                    var vec2 = AdvSimd.LoadVector128(src + offset + 32);
                    var vec3 = AdvSimd.LoadVector128(src + offset + 48);
                    AdvSimd.Store(dst + offset, vec0);
                    AdvSimd.Store(dst + offset + 16, vec1);
                    AdvSimd.Store(dst + offset + 32, vec2);
                    AdvSimd.Store(dst + offset + 48, vec3);
                }
            }
            else if (_advSimdArm64Supported && length >= 32)
            {
                // Process 32 bytes at a time using two vector registers for pipelining
                var advSimd32Length = length & -32;
                for (; offset < advSimd32Length; offset += 32)
                {
                    var vec0 = AdvSimd.LoadVector128(src + offset);
                    var vec1 = AdvSimd.LoadVector128(src + offset + 16);
                    AdvSimd.Store(dst + offset, vec0);
                    AdvSimd.Store(dst + offset + 16, vec1);
                }
            }

            // Process remaining 16-byte chunks (handles both ARM32 NEON and ARM64 remainder)
            var advSimdLength = length & -16;
            for (; offset < advSimdLength; offset += 16)
            {
                var vec = AdvSimd.LoadVector128(src + offset);
                AdvSimd.Store(dst + offset, vec);
            }
        }
        else if (Vector.IsHardwareAccelerated && length >= Vector<byte>.Count)
        {
            for (
                var vectorLength = length & ~(Vector<byte>.Count - 1);
                offset < vectorLength;
                offset += Vector<byte>.Count
            )
            {
                var vec = Unsafe.ReadUnaligned<Vector<byte>>(src + offset);
                Unsafe.WriteUnaligned(dst + offset, vec);
            }
        }

        // Handle remaining bytes using progressively smaller copy sizes
        // This avoids the byte-by-byte loop overhead for small remainders
        var remaining = length - offset;
        if (remaining > 0)
        {
            if (remaining >= 8)
            {
                Unsafe.WriteUnaligned(dst + offset, Unsafe.ReadUnaligned<long>(src + offset));
                offset += 8;
                remaining -= 8;
            }

            if (remaining >= 4)
            {
                Unsafe.WriteUnaligned(dst + offset, Unsafe.ReadUnaligned<int>(src + offset));
                offset += 4;
                remaining -= 4;
            }

            if (remaining >= 2)
            {
                Unsafe.WriteUnaligned(dst + offset, Unsafe.ReadUnaligned<short>(src + offset));
                offset += 2;
                remaining -= 2;
            }

            if (remaining > 0)
            {
                dst[offset] = src[offset];
            }
        }

        if (useNonTemporal && Sse2.IsSupported)
        {
            Sse2.MemoryFence();
        }
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
