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
using System.Runtime.Intrinsics.X86;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Service.MpegTs;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Circular buffer write stream with self-overwriting behavior.
/// Ultra-optimized using unsafe code, direct memory operations, SIMD vectorization, and aggressive inlining.
/// Optimized for multi-core systems with cache-line awareness and hardware acceleration.
///
/// Thread Safety: This class is designed for SINGLE WRITER, MULTIPLE READERS pattern.
/// - Only ONE thread should write at a time.
/// - Multiple threads can read concurrently via CircularBufferReadStream.
/// - Uses memory barriers and atomic operations to ensure visibility across cores.
/// </summary>
public sealed class CircularBufferWriteStream : Stream
{
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct CacheLinePadded
    {
        [FieldOffset(0)]
        public long Value;
    }

    private const int NonTemporalThreshold = 262144;

    private static readonly int _simdThreshold = DetermineSimdThreshold();
    private static readonly bool _avx512Supported = Avx512F.IsSupported;
    private static readonly bool _avx2Supported = Avx2.IsSupported;
    private static readonly bool _sse2Supported = Sse2.IsSupported;
    private static readonly int _prefetchDistance = DeterminePrefetchDistance();

    private readonly bool _isPowerOfTwo;
    private readonly long _bufferMask;

    private CacheLinePadded _totalBytesWritten;

    /// <summary>
    /// Gets the maximal size in bytes of read/write chunks.
    /// </summary>
    public int BufferSize { get; }

    /// <summary>
    /// Gets the MPEG-TS indexer for keyframe detection.
    /// </summary>
    public TsIndexer TsIndexer { get; }

    /// <summary>
    /// Gets the internal buffer.
    /// </summary>
    /// <remarks>
    /// Intentionally exposed as byte[] for zero-copy reads. Readers access this buffer
    /// directly for high-performance streaming without memory copies.
    /// </remarks>
    public byte[] Buffer { get; }

    /// <summary>
    /// Gets the number of bytes that have been written to this stream.
    /// </summary>
    public long TotalBytesWritten => Volatile.Read(ref _totalBytesWritten.Value);

    /// <inheritdoc />
    public override long Position
    {
        get
        {
            long written = TotalBytesWritten;
            return _isPowerOfTwo ? (written & _bufferMask) : (written % BufferSize);
        }
        set { throw new NotSupportedException(); }
    }

    /// <inheritdoc />
    public override bool CanRead => false;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override long Length
    {
        get { throw new NotSupportedException(); }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CircularBufferWriteStream"/> class.
    /// </summary>
    /// <param name="bufferSize">Size in bytes of the internal buffer.</param>
    /// <param name="loggerFactory">Optional logger factory for creating loggers.</param>
    public CircularBufferWriteStream(int bufferSize, ILoggerFactory? loggerFactory = null)
    {
        BufferSize = bufferSize;
        _isPowerOfTwo = (bufferSize & (bufferSize - 1)) == 0;
        _bufferMask = bufferSize - 1;
        TsIndexer = new TsIndexer(bufferSize, loggerFactory?.CreateLogger<TsIndexer>());
        Buffer = new byte[bufferSize];
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        WriteSpan(new ReadOnlySpan<byte>(buffer, offset, count));
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        WriteSpan(buffer);
    }

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
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(cancellationToken);
        }

        try
        {
            WriteSpan(buffer.Span);
            return ValueTask.CompletedTask;
        }
        catch (Exception exception)
        {
            return ValueTask.FromException(exception);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private unsafe void WriteSpan(ReadOnlySpan<byte> source)
    {
        if (source.IsEmpty)
        {
            return;
        }

        int remaining = source.Length;
        int sourceOffset = 0;
        long localWriteHead = Volatile.Read(ref _totalBytesWritten.Value);
        int localBufferSize = BufferSize;
        long startOffsetForIndexer = localWriteHead;

        fixed (byte* srcPtr = source)
        {
            fixed (byte* dstPtr = Buffer)
            {
                while (remaining > 0)
                {
                    long currentPosition = _isPowerOfTwo
                        ? (localWriteHead & _bufferMask)
                        : (localWriteHead % localBufferSize);
                    int writable = (int)Math.Min(remaining, localBufferSize - currentPosition);

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

        TsIndexer.ProcessChunk(source, startOffsetForIndexer);
        Interlocked.Exchange(ref _totalBytesWritten.Value, localWriteHead);
    }

    /// <summary>
    /// Determines optimal SIMD threshold based on CPU capabilities.
    /// Lower-end CPUs get higher threshold to avoid SIMD overhead.
    /// </summary>
    private static int DetermineSimdThreshold()
    {
        if (Avx2.IsSupported)
        {
            return 512;
        }

        if (Sse2.IsSupported)
        {
            return 1024;
        }

        return 4096;
    }

    /// <summary>
    /// Determines optimal prefetch distance based on CPU capabilities.
    /// Smaller caches on low-end CPUs need shorter prefetch distance to avoid cache pollution.
    /// </summary>
    private static int DeterminePrefetchDistance()
    {
        return Avx2.IsSupported ? 256 : 128;
    }

    /// <summary>
    /// Hardware-accelerated memory copy using SIMD instructions.
    /// Optimized for multi-core systems with AVX-512/AVX2/SSE support.
    /// Uses non-temporal stores for large copies to bypass cache pollution.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static unsafe void CopyMemorySimd(byte* src, byte* dst, int length)
    {
        int offset = 0;
        bool useNonTemporal = length >= NonTemporalThreshold;

        if (_avx512Supported && length >= 64)
        {
            for (int avx512Length = length & -64; offset < avx512Length; offset += 64)
            {
                if (offset + _prefetchDistance < length)
                {
                    Sse.Prefetch0(src + offset + _prefetchDistance);
                }

                Vector512<byte> vec = Avx512F.LoadVector512(src + offset);
                Avx512F.Store(dst + offset, vec);
            }
        }
        else if (_avx2Supported && length >= 32)
        {
            int avx2Length = length & -32;
            if (useNonTemporal && Sse2.IsSupported)
            {
                for (; offset < avx2Length; offset += 32)
                {
                    if (offset + _prefetchDistance < length)
                    {
                        Sse.Prefetch0(src + offset + _prefetchDistance);
                    }

                    Vector256<byte> vec = Avx.LoadVector256(src + offset);
                    Vector128<byte> lo = vec.GetLower();
                    Vector128<byte> hi = vec.GetUpper();
                    Sse2.StoreAlignedNonTemporal(dst + offset, lo);
                    Sse2.StoreAlignedNonTemporal(dst + offset + 16, hi);
                }
            }
            else
            {
                for (; offset < avx2Length; offset += 32)
                {
                    if (Sse.IsSupported && offset + _prefetchDistance < length)
                    {
                        Sse.Prefetch0(src + offset + _prefetchDistance);
                    }

                    Vector256<byte> vec = Avx.LoadVector256(src + offset);
                    Avx.Store(dst + offset, vec);
                }
            }
        }
        else if (_sse2Supported && length >= 16)
        {
            int sse2Length = length & -16;
            if (useNonTemporal)
            {
                for (; offset < sse2Length; offset += 16)
                {
                    if (offset + _prefetchDistance < length)
                    {
                        Sse.Prefetch0(src + offset + _prefetchDistance);
                    }

                    Vector128<byte> vec = Sse2.LoadVector128(src + offset);
                    Sse2.StoreAlignedNonTemporal(dst + offset, vec);
                }
            }
            else
            {
                for (; offset < sse2Length; offset += 16)
                {
                    if (offset + _prefetchDistance < length)
                    {
                        Sse.Prefetch0(src + offset + _prefetchDistance);
                    }

                    Vector128<byte> vec = Sse2.LoadVector128(src + offset);
                    Sse2.Store(dst + offset, vec);
                }
            }
        }
        else if (Vector.IsHardwareAccelerated && length >= Vector<byte>.Count)
        {
            for (
                int vectorLength = length & ~(Vector<byte>.Count - 1);
                offset < vectorLength;
                offset += Vector<byte>.Count
            )
            {
                Vector<byte> vec = Unsafe.ReadUnaligned<Vector<byte>>(src + offset);
                Unsafe.WriteUnaligned(dst + offset, vec);
            }
        }

        int remaining = length - offset;
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

            while (remaining > 0)
            {
                dst[offset] = src[offset];
                offset++;
                remaining--;
            }
        }

        if (useNonTemporal && Sse2.IsSupported)
        {
            Sse2.MemoryFence();
        }
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Flush() { }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Resets the buffer for a new stream session, resetting counters.
    /// Note: The buffer is NOT cleared for performance reasons. Readers track position
    /// via _totalBytesWritten, so they will never read stale data as long as they
    /// respect the write head position.
    ///
    /// Thread Safety: Safe to call while readers are active. Readers will see the reset
    /// atomically and will wait for new data to be written.
    /// </summary>
    public void Reset()
    {
        TsIndexer.Reset();
        Interlocked.Exchange(ref _totalBytesWritten.Value, 0L);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing) => base.Dispose(disposing);
}
