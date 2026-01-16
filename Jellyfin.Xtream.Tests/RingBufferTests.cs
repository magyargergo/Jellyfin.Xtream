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
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Service.MpegTs;
using Jellyfin.Xtream.Service.MpegTs.Core;
using Jellyfin.Xtream.Service.MpegTs.Models;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Unit tests for RingBuffer validating lock-free circular buffer behavior.
/// </summary>
/// <remarks>
/// <para>
/// The RingBuffer is designed for high-performance timestamp tracking in MPEG-TS
/// processing with the following characteristics:
/// </para>
/// <para>
/// - Fixed-size with automatic overwrite of oldest entries when full
/// - Power-of-2 capacity for bitwise AND masking (5x faster than modulo)
/// - Lock-free single-writer/multiple-reader thread safety
/// - Zero-allocation enumeration via ref struct enumerator
/// </para>
/// </remarks>
public sealed class RingBufferTests
{
    #region Constructor Tests

    /// <summary>
    /// Tests that constructor throws for zero capacity.
    /// </summary>
    [Fact]
    public void ConstructorThrowsForZeroCapacity() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RingBuffer<int>(0));

    /// <summary>
    /// Tests that constructor throws for negative capacity.
    /// </summary>
    [Fact]
    public void ConstructorThrowsForNegativeCapacity() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RingBuffer<int>(-1));

    /// <summary>
    /// Tests that capacity is rounded up to power of 2.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(5, 8)]
    [InlineData(7, 8)]
    [InlineData(9, 16)]
    [InlineData(50, 64)]
    [InlineData(100, 128)]
    public void ConstructorRoundsCapacityToPowerOf2(int requested, int expected)
    {
        var buffer = new RingBuffer<int>(requested);

        Assert.Equal(expected, buffer.Capacity);
    }

    /// <summary>
    /// Tests that new buffer is empty.
    /// </summary>
    [Fact]
    public void ConstructorCreatesEmptyBuffer()
    {
        var buffer = new RingBuffer<int>(10);

        Assert.Equal(0, buffer.Count);
        Assert.True(buffer.IsEmpty);
    }

    #endregion

    #region Add and Count Tests

    /// <summary>
    /// Tests that Add increases count.
    /// </summary>
    [Fact]
    public void AddIncreasesCount()
    {
        var buffer = new RingBuffer<int>(10);

        buffer.Add(1);

        Assert.Equal(1, buffer.Count);
        Assert.False(buffer.IsEmpty);
    }

    /// <summary>
    /// Tests that Add works up to capacity.
    /// </summary>
    [Fact]
    public void AddWorksUpToCapacity()
    {
        var buffer = new RingBuffer<int>(8);

        for (var i = 0; i < 8; i++)
        {
            buffer.Add(i);
        }

        Assert.Equal(8, buffer.Count);
    }

    /// <summary>
    /// Tests that count does not exceed capacity after overflow.
    /// </summary>
    [Fact]
    public void CountDoesNotExceedCapacityAfterOverflow()
    {
        var buffer = new RingBuffer<int>(4);

        // Add more items than capacity
        for (var i = 0; i < 10; i++)
        {
            buffer.Add(i);
        }

        Assert.Equal(4, buffer.Count);
    }

    #endregion

    #region Indexer Tests

    /// <summary>
    /// Tests that indexer returns items in order (oldest to newest).
    /// </summary>
    [Fact]
    public void IndexerReturnsItemsInOrder()
    {
        var buffer = new RingBuffer<int>(8);

        buffer.Add(10);
        buffer.Add(20);
        buffer.Add(30);

        Assert.Equal(10, buffer[0]); // Oldest
        Assert.Equal(20, buffer[1]);
        Assert.Equal(30, buffer[2]); // Newest
    }

    /// <summary>
    /// Tests that indexer throws for negative index.
    /// </summary>
    [Fact]
    public void IndexerThrowsForNegativeIndex()
    {
        var buffer = new RingBuffer<int>(8);
        buffer.Add(1);

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => buffer[-1]);
    }

    /// <summary>
    /// Tests that indexer throws for index equal to count.
    /// </summary>
    [Fact]
    public void IndexerThrowsForIndexEqualToCount()
    {
        var buffer = new RingBuffer<int>(8);
        buffer.Add(1);
        buffer.Add(2);

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => buffer[2]);
    }

    /// <summary>
    /// Tests that indexer throws for empty buffer.
    /// </summary>
    [Fact]
    public void IndexerThrowsForEmptyBuffer()
    {
        var buffer = new RingBuffer<int>(8);

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => buffer[0]);
    }

    /// <summary>
    /// Tests that indexer returns correct items after wrap-around.
    /// </summary>
    [Fact]
    public void IndexerReturnsCorrectItemsAfterWrapAround()
    {
        var buffer = new RingBuffer<int>(4); // Capacity = 4

        // Add 6 items (wraps around, overwrites first 2)
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);
        buffer.Add(4);
        buffer.Add(5); // Overwrites 1
        buffer.Add(6); // Overwrites 2

        // Should contain [3, 4, 5, 6] in order
        Assert.Equal(3, buffer[0]); // Oldest
        Assert.Equal(4, buffer[1]);
        Assert.Equal(5, buffer[2]);
        Assert.Equal(6, buffer[3]); // Newest
    }

    #endregion

    #region TryGetLatest Tests

    /// <summary>
    /// Tests that TryGetLatest returns false for empty buffer.
    /// </summary>
    [Fact]
    public void TryGetLatestReturnsFalseForEmptyBuffer()
    {
        var buffer = new RingBuffer<int>(8);

        var result = buffer.TryGetLatest(out var item);

        Assert.False(result);
        Assert.Equal(default, item);
    }

    /// <summary>
    /// Tests that TryGetLatest returns most recent item.
    /// </summary>
    [Fact]
    public void TryGetLatestReturnsMostRecentItem()
    {
        var buffer = new RingBuffer<int>(8);

        buffer.Add(10);
        buffer.Add(20);
        buffer.Add(30);

        var result = buffer.TryGetLatest(out var item);

        Assert.True(result);
        Assert.Equal(30, item);
    }

    /// <summary>
    /// Tests that TryGetLatest returns correct item after wrap-around.
    /// </summary>
    [Fact]
    public void TryGetLatestReturnsCorrectItemAfterWrapAround()
    {
        var buffer = new RingBuffer<int>(4);

        // Fill and overflow
        for (var i = 1; i <= 10; i++)
        {
            buffer.Add(i);
        }

        var result = buffer.TryGetLatest(out var item);

        Assert.True(result);
        Assert.Equal(10, item);
    }

    #endregion

    #region Clear Tests

    /// <summary>
    /// Tests that Clear resets buffer to empty state.
    /// </summary>
    [Fact]
    public void ClearResetsBufferToEmpty()
    {
        var buffer = new RingBuffer<int>(8);

        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);

        buffer.Clear();

        Assert.Equal(0, buffer.Count);
        Assert.True(buffer.IsEmpty);
    }

    /// <summary>
    /// Tests that Clear allows reuse of buffer.
    /// </summary>
    [Fact]
    public void ClearAllowsReuseOfBuffer()
    {
        var buffer = new RingBuffer<int>(4);

        buffer.Add(1);
        buffer.Add(2);
        buffer.Clear();

        buffer.Add(100);
        buffer.Add(200);

        Assert.Equal(2, buffer.Count);
        Assert.Equal(100, buffer[0]);
        Assert.Equal(200, buffer[1]);
    }

    /// <summary>
    /// Tests that TryGetLatest returns false after Clear.
    /// </summary>
    [Fact]
    public void TryGetLatestReturnsFalseAfterClear()
    {
        var buffer = new RingBuffer<int>(8);

        buffer.Add(1);
        buffer.Clear();

        var result = buffer.TryGetLatest(out _);

        Assert.False(result);
    }

    #endregion

    #region Enumerator Tests

    /// <summary>
    /// Tests that enumerator iterates in order from oldest to newest.
    /// </summary>
    [Fact]
    public void EnumeratorIteratesInOrder()
    {
        var buffer = new RingBuffer<int>(8);

        buffer.Add(10);
        buffer.Add(20);
        buffer.Add(30);

        var items = new List<int>();
        foreach (var item in buffer)
        {
            items.Add(item);
        }

        Assert.Equal(3, items.Count);
        Assert.Equal(10, items[0]);
        Assert.Equal(20, items[1]);
        Assert.Equal(30, items[2]);
    }

    /// <summary>
    /// Tests that enumerator works correctly after wrap-around.
    /// </summary>
    [Fact]
    public void EnumeratorWorksAfterWrapAround()
    {
        var buffer = new RingBuffer<int>(4);

        // Add 6 items (capacity is 4, so wraps)
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);
        buffer.Add(4);
        buffer.Add(5);
        buffer.Add(6);

        var items = new List<int>();
        foreach (var item in buffer)
        {
            items.Add(item);
        }

        // Should be [3, 4, 5, 6]
        Assert.Equal(4, items.Count);
        Assert.Equal(3, items[0]);
        Assert.Equal(4, items[1]);
        Assert.Equal(5, items[2]);
        Assert.Equal(6, items[3]);
    }

    /// <summary>
    /// Tests that enumerator handles empty buffer.
    /// </summary>
    [Fact]
    public void EnumeratorHandlesEmptyBuffer()
    {
        var buffer = new RingBuffer<int>(8);

        var items = new List<int>();
        foreach (var item in buffer)
        {
            items.Add(item);
        }

        Assert.Empty(items);
    }

    /// <summary>
    /// Tests that enumerator handles single item.
    /// </summary>
    [Fact]
    public void EnumeratorHandlesSingleItem()
    {
        var buffer = new RingBuffer<int>(8);
        buffer.Add(42);

        var items = new List<int>();
        foreach (var item in buffer)
        {
            items.Add(item);
        }

        _ = Assert.Single(items);
        Assert.Equal(42, items[0]);
    }

    /// <summary>
    /// Tests that enumerator handles full buffer (no overflow).
    /// </summary>
    [Fact]
    public void EnumeratorHandlesFullBufferNoOverflow()
    {
        var buffer = new RingBuffer<int>(4);

        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);
        buffer.Add(4);

        var items = new List<int>();
        foreach (var item in buffer)
        {
            items.Add(item);
        }

        Assert.Equal(4, items.Count);
        Assert.Equal(1, items[0]);
        Assert.Equal(2, items[1]);
        Assert.Equal(3, items[2]);
        Assert.Equal(4, items[3]);
    }

    #endregion

    #region Wrap-Around Behavior Tests

    /// <summary>
    /// Tests that oldest items are overwritten when buffer overflows.
    /// </summary>
    [Fact]
    public void OldestItemsOverwrittenOnOverflow()
    {
        var buffer = new RingBuffer<int>(4);

        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);
        buffer.Add(4);

        // This should overwrite 1
        buffer.Add(5);

        // Buffer should now contain [2, 3, 4, 5]
        Assert.Equal(4, buffer.Count);
        Assert.Equal(2, buffer[0]);
        Assert.Equal(5, buffer[3]);
    }

    /// <summary>
    /// Tests wrap-around with multiple complete cycles.
    /// </summary>
    [Fact]
    public void WrapAroundWithMultipleCycles()
    {
        var buffer = new RingBuffer<int>(4);

        // Add 12 items (3 complete cycles)
        for (var i = 1; i <= 12; i++)
        {
            buffer.Add(i);
        }

        // Should contain [9, 10, 11, 12]
        Assert.Equal(4, buffer.Count);
        Assert.Equal(9, buffer[0]);
        Assert.Equal(10, buffer[1]);
        Assert.Equal(11, buffer[2]);
        Assert.Equal(12, buffer[3]);
    }

    #endregion

    #region Struct Value Type Tests

    /// <summary>
    /// Tests that buffer works with custom struct types.
    /// </summary>
    [Fact]
    public void BufferWorksWithCustomStruct()
    {
        var buffer = new RingBuffer<TestTimestamp>(4);

        buffer.Add(new TestTimestamp(100, 1000));
        buffer.Add(new TestTimestamp(200, 2000));

        Assert.Equal(2, buffer.Count);

        var result = buffer.TryGetLatest(out var latest);
        Assert.True(result);
        Assert.Equal(200, latest.Pts);
        Assert.Equal(2000, latest.Offset);
    }

    /// <summary>
    /// Tests that struct values are copied correctly (value semantics).
    /// </summary>
    [Fact]
    public void StructValuesAreCopiedCorrectly()
    {
        var buffer = new RingBuffer<TestTimestamp>(4);

        var original = new TestTimestamp(100, 1000);
        buffer.Add(original);

        // Modify doesn't affect buffer (value semantics)
        _ = new TestTimestamp(999, 9999);

        _ = buffer.TryGetLatest(out var retrieved);
        Assert.Equal(100, retrieved.Pts);
        Assert.Equal(1000, retrieved.Offset);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
    private readonly struct TestTimestamp(long pts, long offset)
    {
        public long Pts { get; } = pts;

        public long Offset { get; } = offset;
    }

    #endregion

    #region Thread Safety Tests

    /// <summary>
    /// Tests concurrent writes from single writer (stress test).
    /// </summary>
    [Fact]
    public void ConcurrentWritesFromSingleWriter()
    {
        var buffer = new RingBuffer<int>(64);
        const int itemCount = 10000;

        // Single writer adding many items
        for (var i = 0; i < itemCount; i++)
        {
            buffer.Add(i);
        }

        // Count should be capped at capacity
        Assert.Equal(64, buffer.Count);

        // Latest should be the last item added
        _ = buffer.TryGetLatest(out var latest);
        Assert.Equal(itemCount - 1, latest);
    }

    /// <summary>
    /// Tests single writer with multiple concurrent readers.
    /// </summary>
    [Fact]
    public async Task SingleWriterMultipleReadersIsSafe()
    {
        var buffer = new RingBuffer<int>(64);
        var cts = new CancellationTokenSource();
        var readerExceptions = new List<Exception>();
        var readCounts = new int[3];
        var readersStarted = new ManualResetEventSlim(initialState: false);
        var readersReady = 0;

        // Start readers
        var readers = new Task[3];
        for (var r = 0; r < 3; r++)
        {
            var readerIndex = r;
            readers[r] = Task.Run(() =>
            {
                try
                {
                    // Signal this reader is ready
                    if (Interlocked.Increment(ref readersReady) == 3)
                    {
                        readersStarted.Set();
                    }

                    while (!cts.Token.IsCancellationRequested)
                    {
                        // Read operations
                        _ = buffer.Count;
                        _ = buffer.IsEmpty;
                        _ = buffer.TryGetLatest(out _);

                        if (buffer.Count > 0)
                        {
                            try
                            {
                                _ = buffer[0];
                            }
                            catch (ArgumentOutOfRangeException)
                            {
                                // Expected during concurrent modifications
                            }
                        }

                        _ = Interlocked.Increment(ref readCounts[readerIndex]);
                    }
                }
                catch (Exception ex)
                {
                    lock (readerExceptions)
                    {
                        readerExceptions.Add(ex);
                    }
                }
            });
        }

        // Wait for all readers to start
        _ = readersStarted.Wait(TimeSpan.FromSeconds(5));

        // Writer - add items with small delays to allow readers to interleave
        for (var i = 0; i < 1000; i++)
        {
            buffer.Add(i);
            if (i % 100 == 0)
            {
                await Task.Yield(); // Give readers a chance to run
            }
        }

        // Let readers run a bit more
        await Task.Delay(10);
        await
        // Stop readers
        cts.CancelAsync();
        await Task.WhenAll(readers);

        // No exceptions should have been thrown
        Assert.Empty(readerExceptions);

        // All readers should have done some work
        foreach (var count in readCounts)
        {
            Assert.True(count > 0, "Reader should have performed reads");
        }
    }

    #endregion

    #region Edge Cases

    /// <summary>
    /// Tests buffer with capacity of 1.
    /// </summary>
    [Fact]
    public void BufferWithCapacityOne()
    {
        var buffer = new RingBuffer<int>(1);

        buffer.Add(10);
        Assert.Equal(1, buffer.Count);
        Assert.Equal(10, buffer[0]);

        buffer.Add(20);
        Assert.Equal(1, buffer.Count);
        Assert.Equal(20, buffer[0]); // Overwrote 10
    }

    /// <summary>
    /// Tests buffer at maximum reasonable capacity.
    /// </summary>
    [Fact]
    public void BufferWithLargeCapacity()
    {
        var buffer = new RingBuffer<int>(1024);

        for (var i = 0; i < 1024; i++)
        {
            buffer.Add(i);
        }

        Assert.Equal(1024, buffer.Count);
        Assert.Equal(0, buffer[0]);
        Assert.Equal(1023, buffer[1023]);
    }

    /// <summary>
    /// Tests that mask is calculated correctly for power-of-2.
    /// </summary>
    [Theory]
    [InlineData(4, 3)] // 4 - 1 = 3 = 0b011
    [InlineData(8, 7)] // 8 - 1 = 7 = 0b0111
    [InlineData(16, 15)] // 16 - 1 = 15 = 0b01111
    public void MaskIsCapacityMinusOne(int capacity, int expectedMask)
    {
        var buffer = new RingBuffer<int>(capacity);

        Assert.Equal(expectedMask, buffer.Mask);
    }

    #endregion

    #region Indexer and Enumerator Consistency Tests

    /// <summary>
    /// Tests that indexer and enumerator return same values.
    /// </summary>
    [Fact]
    public void IndexerAndEnumeratorReturnSameValues()
    {
        var buffer = new RingBuffer<int>(8);

        for (var i = 0; i < 12; i++)
        {
            buffer.Add(i * 10);
        }

        // Collect via enumerator
        var enumeratedItems = new List<int>();
        foreach (var item in buffer)
        {
            enumeratedItems.Add(item);
        }

        // Verify indexer returns same values
        for (var i = 0; i < buffer.Count; i++)
        {
            Assert.Equal(enumeratedItems[i], buffer[i]);
        }
    }

    #endregion
}
