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
using Jellyfin.Xtream.Service.MpegTs.Core;
using Jellyfin.Xtream.Service.MpegTs.Infrastructure;
using Jellyfin.Xtream.Service.MpegTs.Models;
using Jellyfin.Xtream.Service.MpegTs.UseCases;
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
public sealed class CircularBufferWriteStream : Stream
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

    private static readonly int _simdThreshold = DetermineSimdThreshold();
    private static readonly bool _avx512Supported = Avx512F.IsSupported;
    private static readonly bool _avx2Supported = Avx2.IsSupported;
    private static readonly bool _sse2Supported = Sse2.IsSupported;
    private static readonly int _prefetchDistance = DeterminePrefetchDistance();

    private readonly bool _isPowerOfTwo;
    private readonly long _bufferMask;

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

    // FFmpeg demuxer for program detection - owned by this stream, passed to TsIndexer
    private readonly FFmpegStreamDemuxer? _demuxer;

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
    /// Gets a value indicating whether the FFmpeg demuxer has completed initialization.
    /// The demuxer needs to probe enough data to detect programs and streams before
    /// it can provide video PID and keyframe information for stream alignment.
    /// </summary>
    /// <remarks>
    /// Returns true if:
    /// - FFmpeg demuxer is available and has completed <c>avformat_find_stream_info</c>
    /// - At least one program has been detected (ProgramCount > 0)
    /// Returns false if:
    /// - FFmpeg demuxer is not available (IsAvailable was false)
    /// - Demuxer is still initializing (probing stream data)
    /// </remarks>
    public bool IsDemuxerInitialized => _demuxer?.IsInitialized == true && _demuxer.ProgramCount > 0;

    /// <summary>
    /// Records a reader's final position when it disconnects.
    /// The next reader can use this to continue from the same position.
    /// </summary>
    /// <param name="position">The reader's final read head position.</param>
    public void RecordReaderDisconnect(long position)
    {
        _ = Interlocked.Exchange(ref _lastReaderPosition.Value, position);
        LastReaderDisconnectTime = DateTime.UtcNow;
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
            return -1;
        }

        // Check if position is still within valid buffer range
        var totalWritten = TotalBytesWritten;
        var minValidOffset = totalWritten - BufferSize + 524288; // 512KB safety margin

        if (position < minValidOffset || position > totalWritten)
        {
            return -1;
        }

        // Clear the position so it's only used once
        _ = Interlocked.Exchange(ref _lastReaderPosition.Value, 0);
        return position;
    }

    /// <summary>
    /// Gets the cached parameter sets (SPS/PPS) for the specified program, or null if not available.
    /// </summary>
    /// <param name="programNumber">The program number, or -1 for the first detected program.</param>
    /// <returns>A CachedParameterSets instance if extradata is available; null otherwise.</returns>
    public CachedParameterSets? GetCachedParameterSets(int programNumber = -1)
    {
        if (_demuxer == null)
        {
            return null;
        }

        var programs = _demuxer.Programs;
        if (programs.Count == 0)
        {
            return null;
        }

        // Find the requested program or use the first one
        FFmpegProgramInfo? programInfo = null;
        if (programNumber >= 0 && programs.TryGetValue(programNumber, out var found))
        {
            programInfo = found;
        }
        else
        {
            // Use the first available program
            foreach (var kvp in programs)
            {
                programInfo = kvp.Value;
                break;
            }
        }

        if (programInfo?.VideoExtradata == null || programInfo.VideoExtradata.Length == 0)
        {
            return null;
        }

        // Parse the AVCC/HVCC/Annex B extradata into NAL units using FFmpeg bitstream filter
        // Falls back to manual parsing if FFmpeg is not available
        var cache = new CachedParameterSets();
        if (FFmpegParameterSetExtractor.Extract(programInfo.VideoExtradata, programInfo.VideoCodecId, cache))
        {
            return cache;
        }

        return null;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="CircularBufferWriteStream"/> class.
    /// </summary>
    /// <param name="bufferSize">Size in bytes of the internal buffer.</param>
    /// <param name="loggerFactory">Optional logger factory for creating loggers.</param>
    /// <param name="ffmpegContext">Optional FFmpeg context for program detection. If null, uses the default adapter.</param>
    public CircularBufferWriteStream(
        int bufferSize,
        ILoggerFactory? loggerFactory = null,
        IFFmpegContext? ffmpegContext = null
    )
    {
        BufferSize = bufferSize;
        _isPowerOfTwo = (bufferSize & (bufferSize - 1)) == 0;
        _bufferMask = bufferSize - 1;

        // Use provided context or default to production adapter
        ffmpegContext ??= FFmpegContextAdapter.Instance;

        // Create FFmpeg demuxer for program detection if available
        var logger = loggerFactory?.CreateLogger<CircularBufferWriteStream>();
        var ffmpegAvailable = ffmpegContext.IsAvailable;
        logger?.PluginLogInformation(
            "CircularBufferWriteStream: FFmpegContext.IsAvailable={IsAvailable}, FFmpegPath={Path}",
            ffmpegAvailable,
            ffmpegContext.FFmpegPath ?? "(null)"
        );

        if (ffmpegAvailable)
        {
            try
            {
                // Use 8MB buffer to handle bursts and prevent overflow during high bitrate streams
                // Live TV streams can burst up to 20 Mbps which is ~2.5MB/s
                // 8MB provides ~3.2 seconds of buffering at max bitrate, allowing FFmpeg to catch up
                _demuxer = new FFmpegStreamDemuxer(
                    inputBufferSize: 8 * 1024 * 1024,
                    loggerFactory?.CreateLogger<FFmpegStreamDemuxer>(),
                    ffmpegContext
                );
                logger?.PluginLogInformation("FFmpegStreamDemuxer created successfully for program detection");
            }
            catch (Exception ex)
            {
                logger?.PluginLogWarning(ex, "Failed to create FFmpeg demuxer, program detection will be limited");
            }
        }
        else
        {
            logger?.PluginLogWarning(
                "FFmpeg not available - program detection will be limited. "
                    + "Ensure FFmpegInitializationService runs before streams are opened."
            );
        }

        TsIndexer = new TsIndexer(bufferSize, loggerFactory?.CreateLogger<TsIndexer>(), _demuxer);
        Buffer = new byte[bufferSize];
    }

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
        var startOffsetForIndexer = localWriteHead;

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

        TsIndexer.ProcessChunk(source, startOffsetForIndexer);
        _ = Interlocked.Exchange(ref _totalBytesWritten.Value, localWriteHead);
        LastWriteTime = DateTime.UtcNow;
    }

    /// <summary>
    /// Determines optimal SIMD threshold based on CPU capabilities.
    /// Lower-end CPUs get higher threshold to avoid SIMD overhead.
    /// </summary>
    private static int DetermineSimdThreshold()
    {
        return Avx2.IsSupported ? 512
            : Sse2.IsSupported ? 1024
            : 4096;
    }

    /// <summary>
    /// Determines optimal prefetch distance based on CPU capabilities.
    /// Smaller caches on low-end CPUs need shorter prefetch distance to avoid cache pollution.
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
            for (var avx512Length = length & -64; offset < avx512Length; offset += 64)
            {
                if (offset + _prefetchDistance < length)
                {
                    Sse.Prefetch0(src + offset + _prefetchDistance);
                }

                var vec = Avx512F.LoadVector512(src + offset);
                Avx512F.Store(dst + offset, vec);
            }
        }
        else if (_avx2Supported && length >= 32)
        {
            var avx2Length = length & -32;
            if (useNonTemporal && Sse2.IsSupported)
            {
                // Check if destination is 16-byte aligned for non-temporal stores
                // StoreAlignedNonTemporal requires 16-byte alignment; use regular Store if unaligned
                var isAligned = ((nuint)(dst + offset) & 15) == 0;

                if (isAligned)
                {
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
                    // Fallback to regular stores for unaligned destinations
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
            else
            {
                for (; offset < avx2Length; offset += 32)
                {
                    if (Sse.IsSupported && offset + _prefetchDistance < length)
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
            var sse2Length = length & -16;
            if (useNonTemporal)
            {
                // Check if destination is 16-byte aligned for non-temporal stores
                var isAligned = ((nuint)(dst + offset) & 15) == 0;

                if (isAligned)
                {
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
                    // Fallback to regular stores for unaligned destinations
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
            else
            {
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
        TsIndexer.Reset();
        _ = Interlocked.Exchange(ref _totalBytesWritten.Value, 0L);
        _ = Interlocked.Exchange(ref _lastDiscontinuityOffset.Value, 0L);
        _ = Interlocked.Exchange(ref _discontinuityCount.Value, 0);
        _ = Interlocked.Exchange(ref _reconnectionAttempts.Value, 0);
        _lastDiscontinuityTime = default;
        LastWriteTime = default;
        _isSourceConnected = false;
        _isReconnecting = false;
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
        _ = Interlocked.Increment(ref _discontinuityCount.Value);
        _lastDiscontinuityTime = DateTime.UtcNow;
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
        var remainder = (int)(currentOffset % TsConstants.PacketSize);

        if (remainder == 0)
        {
            return 0; // Already aligned
        }

        var paddingNeeded = TsConstants.PacketSize - remainder;

        // Create a null packet for padding
        // Null packet: sync byte (0x47), PID 0x1FFF, no adaptation field, payload all 0xFF
        Span<byte> nullPacket = stackalloc byte[TsConstants.PacketSize];
        nullPacket[0] = TsConstants.SyncByte; // Sync byte
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
    }

    /// <summary>
    /// Signals that a reconnection attempt is starting.
    /// Readers will wait longer when reconnection is in progress.
    /// </summary>
    public void SignalReconnecting()
    {
        _isReconnecting = true;
        _isSourceConnected = false;
        _ = Interlocked.Increment(ref _reconnectionAttempts.Value);
    }

    /// <summary>
    /// Signals that the source has disconnected (EOF, error, or intentional close).
    /// </summary>
    public void SignalSourceDisconnected() => _isSourceConnected = false;

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _demuxer?.Dispose();
        }

        base.Dispose(disposing);
    }
}
