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
using System.Collections.Generic;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Tests for FFmpeg pipeline optimizations to ensure correctness.
/// These tests validate the optimization patterns without requiring actual FFmpeg libraries.
/// </summary>
public class FFmpegPipelineOptimizationTests
{
    /// <summary>
    /// Test struct that mirrors PooledOutputChunk for validation.
    /// </summary>
    private readonly struct TestPooledChunk
    {
        public readonly byte[] Buffer;
        public readonly int Length;

        public TestPooledChunk(byte[] buffer, int length)
        {
            Buffer = buffer;
            Length = length;
        }
    }

    /// <summary>
    /// Verify ArrayPool buffers are properly tracked and returned.
    /// </summary>
    [Fact]
    public void ArrayPool_Buffers_Are_Properly_Returned()
    {
        // Arrange
        const int chunkSize = 4 * 1024;
        const int chunkCount = 100;
        var queue = new ConcurrentQueue<TestPooledChunk>();
        var rentedBuffers = new List<byte[]>();

        // Act - simulate OutputWritePacket pattern
        for (var i = 0; i < chunkCount; i++)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(chunkSize);
            rentedBuffers.Add(buffer);

            // Fill with test data
            Array.Fill(buffer, (byte)(i % 256), 0, chunkSize);

            queue.Enqueue(new TestPooledChunk(buffer, chunkSize));
        }

        // Act - simulate CollectOutput pattern
        var totalCollected = 0;
        var returnedCount = 0;

        while (queue.TryDequeue(out var chunk))
        {
            // Validate chunk data is intact
            var expectedByte = (byte)(returnedCount % 256);
            Assert.Equal(expectedByte, chunk.Buffer[0]);

            totalCollected += chunk.Length;
            ArrayPool<byte>.Shared.Return(chunk.Buffer);
            returnedCount++;
        }

        // Assert
        Assert.Equal(chunkCount, returnedCount);
        Assert.Equal(chunkCount * chunkSize, totalCollected);
    }

    /// <summary>
    /// Verify ArrayPool handles variable-sized chunks correctly.
    /// </summary>
    [Fact]
    public void ArrayPool_Handles_Variable_Chunk_Sizes()
    {
        // Arrange
        var sizes = new[] { 188, 512, 1024, 4096, 32768, 65536 };
        var queue = new ConcurrentQueue<TestPooledChunk>();

        // Act - rent various sizes
        foreach (var size in sizes)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(size);

            // ArrayPool may return larger buffer
            Assert.True(buffer.Length >= size);

            // Fill actual data portion
            Array.Fill(buffer, (byte)0xAB, 0, size);

            queue.Enqueue(new TestPooledChunk(buffer, size));
        }

        // Act - collect and verify
        var index = 0;
        while (queue.TryDequeue(out var chunk))
        {
            Assert.Equal(sizes[index], chunk.Length);
            Assert.Equal(0xAB, chunk.Buffer[0]);
            Assert.Equal(0xAB, chunk.Buffer[chunk.Length - 1]);

            ArrayPool<byte>.Shared.Return(chunk.Buffer);
            index++;
        }

        Assert.Equal(sizes.Length, index);
    }

    /// <summary>
    /// Verify overflow handling returns discarded buffers to pool.
    /// </summary>
    [Fact]
    public void Overflow_Handling_Returns_Discarded_Buffers()
    {
        // Arrange
        const int maxQueueBytes = 1024 * 1024; // 1MB limit
        const int chunkSize = 256 * 1024; // 256KB chunks
        const int totalChunks = 10; // Will exceed limit

        var queue = new ConcurrentQueue<TestPooledChunk>();
        long queueBytes = 0;
        var discardedCount = 0;

        // Act - fill queue, discarding when over limit
        for (var i = 0; i < totalChunks; i++)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(chunkSize);
            buffer[0] = (byte)i;
            queue.Enqueue(new TestPooledChunk(buffer, chunkSize));
            queueBytes += chunkSize;

            // Overflow handling - discard oldest when over limit
            while (queueBytes > maxQueueBytes && queue.TryDequeue(out var old))
            {
                queueBytes -= old.Length;
                ArrayPool<byte>.Shared.Return(old.Buffer);
                discardedCount++;
            }
        }

        // Assert - some chunks were discarded
        Assert.True(discardedCount > 0, "Expected overflow discards");

        // Cleanup remaining
        var remainingCount = 0;
        while (queue.TryDequeue(out var chunk))
        {
            ArrayPool<byte>.Shared.Return(chunk.Buffer);
            remainingCount++;
        }

        Assert.Equal(totalChunks, discardedCount + remainingCount);
    }

    /// <summary>
    /// Verify timestamp conversion is reversible and consistent.
    /// </summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(90000L)] // 1 second at 90kHz
    [InlineData(8100000L)] // 90 seconds
    [InlineData(810000000L)] // 2.5 hours
    public void Timestamp_Conversion_Is_Reversible(long pts90Khz)
    {
        // Standard video time_base: 1/90000 (already 90kHz)
        const int timeBaseNum = 1;
        const int timeBaseDen = 90000;
        const int clockRate = 90000;

        // Convert from 90kHz to stream timebase
        var streamPts = pts90Khz * timeBaseDen / (timeBaseNum * clockRate);

        // Convert back to 90kHz
        var roundtrip = streamPts * timeBaseNum * clockRate / timeBaseDen;

        Assert.Equal(pts90Khz, roundtrip);
    }

    /// <summary>
    /// Verify timestamp conversion handles common time bases correctly.
    /// </summary>
    [Theory]
    [InlineData(1, 90000, 90000, 90000)] // 1/90000 -> 1s = 90000 ticks in 90kHz
    [InlineData(1, 1000, 1000, 90000)] // 1/1000 (ms) -> 1s = 1000ms = 90000 ticks in 90kHz
    [InlineData(1001, 30000, 1, 3003)] // 1001/30000 timebase: 1 tick = 3003 ticks in 90kHz (NTSC)
    public void Timestamp_Conversion_Common_Timebases(int num, int den, long input, long expected90Khz)
    {
        const int clockRate = 90000;

        // Convert to 90kHz
        var result = input * num * clockRate / den;

        Assert.Equal(expected90Khz, result);
    }

    /// <summary>
    /// Verify timestamp offset application maintains monotonicity.
    /// </summary>
    [Fact]
    public void Timestamp_Offset_Maintains_Monotonicity()
    {
        // Simulate provider switch with timestamp correction
        var lastOutput = 900000L; // 10 seconds in 90kHz
        var firstNewInput = 0L; // New provider starts at 0
        var offset = lastOutput - firstNewInput + 90000; // 1 second gap

        // Process several packets
        var outputPts = new List<long>();
        for (var i = 0; i < 100; i++)
        {
            var inputPts = i * 3003L; // ~30fps
            var correctedPts = inputPts + offset;
            outputPts.Add(correctedPts);
        }

        // Verify monotonicity
        for (var i = 1; i < outputPts.Count; i++)
        {
            Assert.True(
                outputPts[i] > outputPts[i - 1],
                $"PTS at {i} ({outputPts[i]}) should be > PTS at {i - 1} ({outputPts[i - 1]})"
            );
        }

        // Verify first output PTS is after last output from previous provider
        Assert.True(outputPts[0] > lastOutput);
    }

    /// <summary>
    /// Verify concurrent queue operations are thread-safe.
    /// </summary>
    [Fact]
    public void Concurrent_Queue_Operations_Are_Thread_Safe()
    {
        // Arrange
        const int producerCount = 4;
        const int chunksPerProducer = 100;
        const int chunkSize = 1024;

        var queue = new ConcurrentQueue<TestPooledChunk>();
        var producedCount = 0;
        var consumedCount = 0;
        var totalBytes = 0L;

        // Act - produce chunks in parallel
        System.Threading.Tasks.Parallel.For(
            0,
            producerCount,
            _ =>
            {
                for (var i = 0; i < chunksPerProducer; i++)
                {
                    var buffer = ArrayPool<byte>.Shared.Rent(chunkSize);
                    queue.Enqueue(new TestPooledChunk(buffer, chunkSize));
                    System.Threading.Interlocked.Increment(ref producedCount);
                }
            }
        );

        // Act - consume all chunks
        while (queue.TryDequeue(out var chunk))
        {
            System.Threading.Interlocked.Add(ref totalBytes, chunk.Length);
            ArrayPool<byte>.Shared.Return(chunk.Buffer);
            consumedCount++;
        }

        // Assert
        Assert.Equal(producerCount * chunksPerProducer, producedCount);
        Assert.Equal(producedCount, consumedCount);
        Assert.Equal((long)producerCount * chunksPerProducer * chunkSize, totalBytes);
    }

    /// <summary>
    /// Verify data integrity through the chunking pipeline.
    /// </summary>
    [Fact]
    public void Data_Integrity_Through_Chunking_Pipeline()
    {
        // Arrange - create test pattern
        const int totalSize = 256 * 1024;
        var originalData = new byte[totalSize];
        Random.Shared.NextBytes(originalData);

        var queue = new ConcurrentQueue<TestPooledChunk>();

        // Act - chunk the data (simulating FFmpeg output)
        const int chunkSize = 4096;
        for (var offset = 0; offset < totalSize; offset += chunkSize)
        {
            var size = Math.Min(chunkSize, totalSize - offset);
            var buffer = ArrayPool<byte>.Shared.Rent(size);
            Buffer.BlockCopy(originalData, offset, buffer, 0, size);
            queue.Enqueue(new TestPooledChunk(buffer, size));
        }

        // Act - collect and reassemble
        var reassembled = new byte[totalSize];
        var reassembleOffset = 0;

        while (queue.TryDequeue(out var chunk))
        {
            Buffer.BlockCopy(chunk.Buffer, 0, reassembled, reassembleOffset, chunk.Length);
            reassembleOffset += chunk.Length;
            ArrayPool<byte>.Shared.Return(chunk.Buffer);
        }

        // Assert - data is identical
        Assert.Equal(totalSize, reassembleOffset);
        Assert.Equal(originalData, reassembled);
    }

    /// <summary>
    /// Verify the cleanup path properly handles edge cases.
    /// </summary>
    [Fact]
    public void Cleanup_Handles_Empty_Queue()
    {
        // Arrange
        var queue = new ConcurrentQueue<TestPooledChunk>();

        // Act - cleanup empty queue (should not throw)
        var exception = Record.Exception(() =>
        {
            while (queue.TryDequeue(out var chunk))
            {
                ArrayPool<byte>.Shared.Return(chunk.Buffer);
            }
        });

        // Assert
        Assert.Null(exception);
    }

    /// <summary>
    /// Verify cleanup handles partial consumption correctly.
    /// </summary>
    [Fact]
    public void Cleanup_Handles_Partial_Consumption()
    {
        // Arrange
        const int chunkCount = 10;
        const int chunkSize = 1024;
        var queue = new ConcurrentQueue<TestPooledChunk>();

        for (var i = 0; i < chunkCount; i++)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(chunkSize);
            queue.Enqueue(new TestPooledChunk(buffer, chunkSize));
        }

        // Act - consume only half
        for (var i = 0; i < chunkCount / 2; i++)
        {
            if (queue.TryDequeue(out var chunk))
            {
                ArrayPool<byte>.Shared.Return(chunk.Buffer);
            }
        }

        // Act - cleanup remaining (simulating dispose)
        var cleanedUp = 0;
        while (queue.TryDequeue(out var chunk))
        {
            ArrayPool<byte>.Shared.Return(chunk.Buffer);
            cleanedUp++;
        }

        // Assert
        Assert.Equal(chunkCount / 2, cleanedUp);
    }
}
