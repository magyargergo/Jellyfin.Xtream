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
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace Jellyfin.Xtream.Benchmarks;

/// <summary>
/// Benchmarks for FFmpeg demuxer/remuxer pipeline hot paths.
/// Measures managed-side overhead without requiring actual FFmpeg libraries.
/// </summary>
/// <remarks>
/// These benchmarks focus on the C# code paths that are called frequently:
/// <list type="bullet">
///   <item>Output callback buffer allocation (new byte[] vs ArrayPool)</item>
///   <item>Output queue operations (enqueue/dequeue)</item>
///   <item>Output collection (concatenation vs segments)</item>
///   <item>Timestamp conversion arithmetic</item>
/// </list>
/// </remarks>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
public class FFmpegPipelineBenchmarks
{
    // Typical output chunk sizes from FFmpeg's AVIO callbacks
    private const int SmallChunkSize = 4 * 1024; // 4KB - small output
    private const int MediumChunkSize = 32 * 1024; // 32KB - typical IO buffer
    private const int LargeChunkSize = 64 * 1024; // 64KB - AVIO buffer size

    // Number of chunks to simulate in queue operations
    private const int ChunkCount = 50;

    private byte[] _sourceData = null!;
    private IntPtr _nativeBuffer;
    private ConcurrentQueue<byte[]> _outputQueue = null!;
    private ConcurrentQueue<PooledChunk> _pooledOutputQueue = null!;

    /// <summary>
    /// Represents a chunk that tracks its pooled origin for proper return.
    /// </summary>
    private readonly struct PooledChunk
    {
        public readonly byte[] Data;
        public readonly int Length;

        public PooledChunk(byte[] data, int length)
        {
            Data = data;
            Length = length;
        }
    }

    /// <summary>
    /// Setup test data for benchmarks.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _sourceData = new byte[LargeChunkSize];
        Random.Shared.NextBytes(_sourceData);

        _nativeBuffer = Marshal.AllocHGlobal(LargeChunkSize);
        Marshal.Copy(_sourceData, 0, _nativeBuffer, LargeChunkSize);

        _outputQueue = new ConcurrentQueue<byte[]>();
        _pooledOutputQueue = new ConcurrentQueue<PooledChunk>();
    }

    /// <summary>
    /// Cleanup native memory.
    /// </summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        Marshal.FreeHGlobal(_nativeBuffer);

        // Return any pooled buffers
        while (_pooledOutputQueue.TryDequeue(out var chunk))
        {
            ArrayPool<byte>.Shared.Return(chunk.Data);
        }
    }

    #region Output Callback Allocation Benchmarks

    /// <summary>
    /// Baseline: Current implementation using new byte[] allocation.
    /// This is what OutputWritePacket currently does.
    /// </summary>
    [Benchmark(Baseline = true, Description = "OutputWrite: new byte[] (current)")]
    public unsafe byte[] OutputWritePacket_NewAllocation()
    {
        var chunk = new byte[MediumChunkSize];
        fixed (byte* dstPtr = chunk)
        {
            Unsafe.CopyBlockUnaligned(dstPtr, (byte*)_nativeBuffer, (uint)MediumChunkSize);
        }

        return chunk;
    }

    /// <summary>
    /// Optimized: Using ArrayPool to rent buffer.
    /// </summary>
    [Benchmark(Description = "OutputWrite: ArrayPool.Rent")]
    public unsafe byte[] OutputWritePacket_ArrayPool()
    {
        var chunk = ArrayPool<byte>.Shared.Rent(MediumChunkSize);
        fixed (byte* dstPtr = chunk)
        {
            Unsafe.CopyBlockUnaligned(dstPtr, (byte*)_nativeBuffer, (uint)MediumChunkSize);
        }

        // In real code, we'd track this for later return
        // For benchmark, return immediately
        ArrayPool<byte>.Shared.Return(chunk);
        return chunk;
    }

    /// <summary>
    /// Small chunk (4KB) with new allocation.
    /// </summary>
    [Benchmark(Description = "OutputWrite 4KB: new byte[]")]
    public unsafe byte[] OutputWritePacket_Small_NewAllocation()
    {
        var chunk = new byte[SmallChunkSize];
        fixed (byte* dstPtr = chunk)
        {
            Unsafe.CopyBlockUnaligned(dstPtr, (byte*)_nativeBuffer, (uint)SmallChunkSize);
        }

        return chunk;
    }

    /// <summary>
    /// Small chunk (4KB) with ArrayPool.
    /// </summary>
    [Benchmark(Description = "OutputWrite 4KB: ArrayPool")]
    public unsafe byte[] OutputWritePacket_Small_ArrayPool()
    {
        var chunk = ArrayPool<byte>.Shared.Rent(SmallChunkSize);
        fixed (byte* dstPtr = chunk)
        {
            Unsafe.CopyBlockUnaligned(dstPtr, (byte*)_nativeBuffer, (uint)SmallChunkSize);
        }

        ArrayPool<byte>.Shared.Return(chunk);
        return chunk;
    }

    /// <summary>
    /// Large chunk (64KB) with new allocation.
    /// </summary>
    [Benchmark(Description = "OutputWrite 64KB: new byte[]")]
    public unsafe byte[] OutputWritePacket_Large_NewAllocation()
    {
        var chunk = new byte[LargeChunkSize];
        fixed (byte* dstPtr = chunk)
        {
            Unsafe.CopyBlockUnaligned(dstPtr, (byte*)_nativeBuffer, (uint)LargeChunkSize);
        }

        return chunk;
    }

    /// <summary>
    /// Large chunk (64KB) with ArrayPool.
    /// </summary>
    [Benchmark(Description = "OutputWrite 64KB: ArrayPool")]
    public unsafe byte[] OutputWritePacket_Large_ArrayPool()
    {
        var chunk = ArrayPool<byte>.Shared.Rent(LargeChunkSize);
        fixed (byte* dstPtr = chunk)
        {
            Unsafe.CopyBlockUnaligned(dstPtr, (byte*)_nativeBuffer, (uint)LargeChunkSize);
        }

        ArrayPool<byte>.Shared.Return(chunk);
        return chunk;
    }

    #endregion

    #region Output Collection Benchmarks

    /// <summary>
    /// Current implementation: Concatenate all queued chunks into single array.
    /// </summary>
    [Benchmark(Description = "CollectOutput: Concatenate (current)")]
    public byte[]? CollectOutput_Concatenate()
    {
        // Setup: Fill queue with chunks
        var queue = new ConcurrentQueue<byte[]>();
        for (var i = 0; i < ChunkCount; i++)
        {
            var chunk = new byte[SmallChunkSize];
            queue.Enqueue(chunk);
        }

        // Measure: Concatenation logic (from FFmpegStreamRemuxer.CollectOutput)
        var totalSize = ChunkCount * SmallChunkSize;
        var result = new byte[totalSize];
        var offset = 0;

        while (queue.TryDequeue(out var chunk))
        {
            Buffer.BlockCopy(chunk, 0, result, offset, chunk.Length);
            offset += chunk.Length;
        }

        return result;
    }

    /// <summary>
    /// Alternative: Return list of segments (no concatenation).
    /// Consumer processes segments individually.
    /// </summary>
    [Benchmark(Description = "CollectOutput: Segments (no concat)")]
    public byte[][]? CollectOutput_Segments()
    {
        // Setup: Fill queue with chunks
        var queue = new ConcurrentQueue<byte[]>();
        for (var i = 0; i < ChunkCount; i++)
        {
            var chunk = new byte[SmallChunkSize];
            queue.Enqueue(chunk);
        }

        // Measure: Just dequeue into array (no Buffer.BlockCopy)
        var result = new byte[ChunkCount][];
        var index = 0;

        while (queue.TryDequeue(out var chunk) && index < ChunkCount)
        {
            result[index++] = chunk;
        }

        return result;
    }

    /// <summary>
    /// Pooled chunks with concatenation.
    /// </summary>
    [Benchmark(Description = "CollectOutput: Pooled + Concat")]
    public byte[]? CollectOutput_Pooled_Concatenate()
    {
        // Setup: Fill queue with pooled chunks
        var queue = new ConcurrentQueue<PooledChunk>();
        for (var i = 0; i < ChunkCount; i++)
        {
            var chunk = ArrayPool<byte>.Shared.Rent(SmallChunkSize);
            queue.Enqueue(new PooledChunk(chunk, SmallChunkSize));
        }

        // Measure: Concatenation with pool return
        var totalSize = ChunkCount * SmallChunkSize;
        var result = new byte[totalSize];
        var offset = 0;

        while (queue.TryDequeue(out var pooledChunk))
        {
            Buffer.BlockCopy(pooledChunk.Data, 0, result, offset, pooledChunk.Length);
            offset += pooledChunk.Length;
            ArrayPool<byte>.Shared.Return(pooledChunk.Data);
        }

        return result;
    }

    #endregion

    #region Timestamp Conversion Benchmarks

    // MPEG-TS clock rate
    private const int MpegTsClockRate = 90_000;
    private const long NoTimestamp = long.MinValue;
    private const long AvNoPtsValue = unchecked((long)0x8000000000000000);

    /// <summary>
    /// Timestamp conversion: FFmpeg time_base to 90kHz.
    /// This is called for every packet.
    /// </summary>
    [Benchmark(Description = "ConvertToMpegTs: Per packet")]
    public long ConvertToMpegTsTimestamp_Single()
    {
        // Typical video stream time_base: 1/90000 (already 90kHz)
        return ConvertToMpegTsTimestamp(12345678L, 1, 90000);
    }

    /// <summary>
    /// Timestamp conversion for 1000 packets (simulates tight loop).
    /// </summary>
    [Benchmark(Description = "ConvertToMpegTs: 1000 packets")]
    public long ConvertToMpegTsTimestamp_Batch()
    {
        long result = 0;
        for (var i = 0; i < 1000; i++)
        {
            result = ConvertToMpegTsTimestamp(12345678L + i, 1, 90000);
        }

        return result;
    }

    /// <summary>
    /// Bidirectional conversion (to 90kHz then back).
    /// This is what remuxer does for timestamp correction.
    /// </summary>
    [Benchmark(Description = "ConvertTs: Bidirectional (remuxer)")]
    public long ConvertTimestamp_Bidirectional()
    {
        // Convert to 90kHz
        var ts90Khz = ConvertToMpegTsTimestamp(12345678L, 1, 90000);

        // Apply offset (provider switch)
        ts90Khz += 90000; // 1 second offset

        // Convert back to output time_base
        return ConvertFromMpegTsTimestamp(ts90Khz, 1, 90000);
    }

    /// <summary>
    /// Full timestamp processing for 500 packets (ProcessAvailablePackets limit).
    /// </summary>
    [Benchmark(Description = "ConvertTs: 500 packets full cycle")]
    public long ConvertTimestamp_FullCycle_500()
    {
        long result = 0;
        const long offset = 90000;

        for (var i = 0; i < 500; i++)
        {
            var inputTs = 12345678L + (i * 3003); // ~30fps video

            // To 90kHz (from input timebase)
            var ts90Khz = ConvertToMpegTsTimestamp(inputTs, 1, 90000);
            if (ts90Khz == NoTimestamp)
            {
                continue;
            }

            // Apply offset
            ts90Khz += offset;

            // Back to output timebase
            result = ConvertFromMpegTsTimestamp(ts90Khz, 1, 90000);
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ConvertToMpegTsTimestamp(long timestamp, int timeBaseNum, int timeBaseDen)
    {
        if (timestamp == AvNoPtsValue)
        {
            return NoTimestamp;
        }

        return timeBaseDen == 0 ? timestamp : timestamp * timeBaseNum * MpegTsClockRate / timeBaseDen;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ConvertFromMpegTsTimestamp(long timestamp90Khz, int timeBaseNum, int timeBaseDen)
    {
        if (timestamp90Khz == NoTimestamp)
        {
            return AvNoPtsValue;
        }

        return timeBaseNum == 0 ? timestamp90Khz : timestamp90Khz * timeBaseDen / (timeBaseNum * MpegTsClockRate);
    }

    #endregion

    #region Queue Operation Benchmarks

    /// <summary>
    /// Enqueue + Dequeue cycle with new byte[] (current pattern).
    /// </summary>
    [Benchmark(Description = "Queue: Enqueue+Dequeue new byte[]")]
    public int QueueCycle_NewAllocation()
    {
        var queue = new ConcurrentQueue<byte[]>();

        // Enqueue
        for (var i = 0; i < ChunkCount; i++)
        {
            queue.Enqueue(new byte[SmallChunkSize]);
        }

        // Dequeue
        var count = 0;
        while (queue.TryDequeue(out _))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Enqueue + Dequeue cycle with ArrayPool (proposed pattern).
    /// </summary>
    [Benchmark(Description = "Queue: Enqueue+Dequeue ArrayPool")]
    public int QueueCycle_ArrayPool()
    {
        var queue = new ConcurrentQueue<PooledChunk>();

        // Enqueue
        for (var i = 0; i < ChunkCount; i++)
        {
            var chunk = ArrayPool<byte>.Shared.Rent(SmallChunkSize);
            queue.Enqueue(new PooledChunk(chunk, SmallChunkSize));
        }

        // Dequeue and return
        var count = 0;
        while (queue.TryDequeue(out var pooledChunk))
        {
            ArrayPool<byte>.Shared.Return(pooledChunk.Data);
            count++;
        }

        return count;
    }

    #endregion

    #region Interlocked Batching Benchmarks

    private long _counterA;
    private long _counterB;

    /// <summary>
    /// Baseline: Per-iteration Interlocked operations (old pattern).
    /// </summary>
    [Benchmark(Description = "Interlocked: Per-iteration (500 ops)")]
    public long Interlocked_PerIteration_500()
    {
        _counterA = 0;
        _counterB = 0;

        for (var i = 0; i < 500; i++)
        {
            // Simulate per-packet Interlocked (old pattern)
            Interlocked.Increment(ref _counterA);
            if (i % 10 == 0) // Simulate occasional error
            {
                Interlocked.Increment(ref _counterB);
            }
        }

        return _counterA + _counterB;
    }

    /// <summary>
    /// Optimized: Batched Interlocked operations (new pattern).
    /// </summary>
    [Benchmark(Description = "Interlocked: Batched (500 ops)")]
    public long Interlocked_Batched_500()
    {
        _counterA = 0;
        _counterB = 0;

        var localA = 0;
        var localB = 0;

        for (var i = 0; i < 500; i++)
        {
            // Simulate per-packet local accumulation (new pattern)
            localA++;
            if (i % 10 == 0)
            {
                localB++;
            }
        }

        // Single batch update
        Interlocked.Add(ref _counterA, localA);
        Interlocked.Add(ref _counterB, localB);

        return _counterA + _counterB;
    }

    /// <summary>
    /// Per-iteration Volatile reads/writes (old pattern).
    /// </summary>
    [Benchmark(Description = "Volatile: Per-iteration R/W (500 ops)")]
    public long Volatile_PerIteration_500()
    {
        long lastPts = 0;
        long offset = 12345;

        for (var i = 0; i < 500; i++)
        {
            // Simulate per-packet Volatile operations (old pattern)
            var cachedOffset = Volatile.Read(ref offset);
            var newPts = (long)i * 3003 + cachedOffset;
            Volatile.Write(ref lastPts, newPts);
        }

        return lastPts;
    }

    /// <summary>
    /// Batched Volatile operations (new pattern).
    /// </summary>
    [Benchmark(Description = "Volatile: Batched R/W (500 ops)")]
    public long Volatile_Batched_500()
    {
        long lastPts = 0;
        long offset = 12345;

        // Read once at start (new pattern)
        var cachedOffset = Volatile.Read(ref offset);
        long localLastPts = 0;

        for (var i = 0; i < 500; i++)
        {
            // Local tracking only
            localLastPts = (long)i * 3003 + cachedOffset;
        }

        // Single batch write
        Volatile.Write(ref lastPts, localLastPts);

        return lastPts;
    }

    #endregion

    #region Simulated Full Pipeline Benchmarks

    /// <summary>
    /// Simulates ProcessAvailablePackets hot path (current implementation).
    /// Without actual FFmpeg, measures managed overhead only.
    /// </summary>
    [Benchmark(Description = "Pipeline: 100 packets (current)")]
    public unsafe int SimulatedPipeline_Current_100Packets()
    {
        var outputQueue = new ConcurrentQueue<byte[]>();
        var packetsProcessed = 0;

        for (var i = 0; i < 100; i++)
        {
            // Simulate read from native buffer
            var chunk = new byte[SmallChunkSize];
            fixed (byte* dstPtr = chunk)
            {
                Unsafe.CopyBlockUnaligned(dstPtr, (byte*)_nativeBuffer, (uint)SmallChunkSize);
            }

            // Timestamp conversion (simulated)
            var ts = ConvertToMpegTsTimestamp(90000L * i, 1, 90000);
            ts += 90000; // offset
            _ = ConvertFromMpegTsTimestamp(ts, 1, 90000);

            // Queue output
            outputQueue.Enqueue(chunk);
            packetsProcessed++;
        }

        // Collect output
        var totalSize = 0;
        while (outputQueue.TryDequeue(out var c))
        {
            totalSize += c.Length;
        }

        return packetsProcessed;
    }

    /// <summary>
    /// Simulates ProcessAvailablePackets with ArrayPool optimization.
    /// </summary>
    [Benchmark(Description = "Pipeline: 100 packets (pooled)")]
    public unsafe int SimulatedPipeline_Pooled_100Packets()
    {
        var outputQueue = new ConcurrentQueue<PooledChunk>();
        var packetsProcessed = 0;

        for (var i = 0; i < 100; i++)
        {
            // Simulate read from native buffer with pooled allocation
            var chunk = ArrayPool<byte>.Shared.Rent(SmallChunkSize);
            fixed (byte* dstPtr = chunk)
            {
                Unsafe.CopyBlockUnaligned(dstPtr, (byte*)_nativeBuffer, (uint)SmallChunkSize);
            }

            // Timestamp conversion (simulated)
            var ts = ConvertToMpegTsTimestamp(90000L * i, 1, 90000);
            ts += 90000; // offset
            _ = ConvertFromMpegTsTimestamp(ts, 1, 90000);

            // Queue output
            outputQueue.Enqueue(new PooledChunk(chunk, SmallChunkSize));
            packetsProcessed++;
        }

        // Collect output and return to pool
        while (outputQueue.TryDequeue(out var pooledChunk))
        {
            ArrayPool<byte>.Shared.Return(pooledChunk.Data);
        }

        return packetsProcessed;
    }

    /// <summary>
    /// Simulates 500 packets (ProcessAvailablePackets max) - current.
    /// </summary>
    [Benchmark(Description = "Pipeline: 500 packets (current)")]
    public unsafe int SimulatedPipeline_Current_500Packets()
    {
        var outputQueue = new ConcurrentQueue<byte[]>();
        var packetsProcessed = 0;

        for (var i = 0; i < 500; i++)
        {
            var chunk = new byte[SmallChunkSize];
            fixed (byte* dstPtr = chunk)
            {
                Unsafe.CopyBlockUnaligned(dstPtr, (byte*)_nativeBuffer, (uint)SmallChunkSize);
            }

            var ts = ConvertToMpegTsTimestamp(90000L * i, 1, 90000);
            ts += 90000;
            _ = ConvertFromMpegTsTimestamp(ts, 1, 90000);

            outputQueue.Enqueue(chunk);
            packetsProcessed++;
        }

        while (outputQueue.TryDequeue(out _)) { }

        return packetsProcessed;
    }

    /// <summary>
    /// Simulates 500 packets with ArrayPool optimization.
    /// </summary>
    [Benchmark(Description = "Pipeline: 500 packets (pooled)")]
    public unsafe int SimulatedPipeline_Pooled_500Packets()
    {
        var outputQueue = new ConcurrentQueue<PooledChunk>();
        var packetsProcessed = 0;

        for (var i = 0; i < 500; i++)
        {
            var chunk = ArrayPool<byte>.Shared.Rent(SmallChunkSize);
            fixed (byte* dstPtr = chunk)
            {
                Unsafe.CopyBlockUnaligned(dstPtr, (byte*)_nativeBuffer, (uint)SmallChunkSize);
            }

            var ts = ConvertToMpegTsTimestamp(90000L * i, 1, 90000);
            ts += 90000;
            _ = ConvertFromMpegTsTimestamp(ts, 1, 90000);

            outputQueue.Enqueue(new PooledChunk(chunk, SmallChunkSize));
            packetsProcessed++;
        }

        while (outputQueue.TryDequeue(out var pooledChunk))
        {
            ArrayPool<byte>.Shared.Return(pooledChunk.Data);
        }

        return packetsProcessed;
    }

    #endregion
}
