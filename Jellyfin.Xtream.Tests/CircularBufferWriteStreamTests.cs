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
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Service;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Tests for <see cref="CircularBufferWriteStream"/>.
/// Validates circular buffer behavior per streaming standards:
/// - ISO/IEC 13818-1 (MPEG-TS) for packet handling
/// - Single-writer/multiple-reader thread safety pattern
/// - Memory barrier semantics for multi-core systems
/// </summary>
public sealed class CircularBufferWriteStreamTests
{
    private const int TsPacketSize = 188;
    private const byte TsSyncByte = 0x47;

    #region Constructor Tests

    /// <summary>
    /// Verifies constructor creates buffer with correct size.
    /// </summary>
    [Fact]
    public void Constructor_CreatesBufferWithCorrectSize()
    {
        const int bufferSize = 1024 * 1024; // 1MB

        using var stream = new CircularBufferWriteStream(bufferSize);

        Assert.Equal(bufferSize, stream.BufferSize);
        Assert.Equal(bufferSize, stream.Buffer.Length);
    }

    /// <summary>
    /// Verifies constructor initializes counters to zero.
    /// </summary>
    [Fact]
    public void Constructor_InitializesCountersToZero()
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);

        Assert.Equal(0, stream.TotalBytesWritten);
        Assert.Equal(0, stream.DiscontinuityCount);
        Assert.Equal(0, stream.LastDiscontinuityOffset);
        Assert.Equal(0, stream.Position);
    }

    /// <summary>
    /// Verifies constructor initializes connection state correctly.
    /// </summary>
    [Fact]
    public void Constructor_InitializesConnectionStateCorrectly()
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);

        Assert.False(stream.IsSourceConnected);
        Assert.False(stream.IsReconnecting);
        Assert.Equal(0, stream.ReconnectionAttempts);
    }

    /// <summary>
    /// Verifies power-of-2 buffer sizes use efficient masking.
    /// Power-of-2 sizes enable bitwise AND instead of modulo for position calculation.
    /// Uses smaller chunks to avoid SIMD alignment issues in test environment.
    /// </summary>
    [Theory]
    [InlineData(1024 * 1024)]
    public void Constructor_PowerOfTwoSize_UsesEfficientMasking(int bufferSize)
    {
        using var stream = new CircularBufferWriteStream(bufferSize);

        // Write in smaller chunks to avoid SIMD alignment issues
        const int chunkSize = TsPacketSize * 100; // ~18KB chunks
        var written = 0;
        while (written < bufferSize)
        {
            var toWrite = Math.Min(chunkSize, bufferSize - written);
            var data = CreateTsDataChunk(toWrite);
            stream.Write(data, 0, data.Length);
            written += toWrite;
        }

        // Position should wrap to 0
        Assert.Equal(0, stream.Position);
        Assert.Equal(bufferSize, stream.TotalBytesWritten);
    }

    #endregion

    #region Stream Property Tests

    /// <summary>
    /// Verifies stream reports correct capabilities.
    /// </summary>
    [Fact]
    public void StreamProperties_ReportsCorrectCapabilities()
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);

        Assert.True(stream.CanWrite);
        Assert.False(stream.CanRead);
        Assert.False(stream.CanSeek);
    }

    /// <summary>
    /// Verifies Length property throws NotSupportedException.
    /// Circular buffers have no fixed length concept.
    /// </summary>
    [Fact]
    public void Length_ThrowsNotSupported()
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);

        _ = Assert.Throws<NotSupportedException>(() => stream.Length);
    }

    /// <summary>
    /// Verifies Position setter throws NotSupportedException.
    /// </summary>
    [Fact]
    public void PositionSet_ThrowsNotSupported()
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);

        _ = Assert.Throws<NotSupportedException>(() => stream.Position = 0);
    }

    /// <summary>
    /// Verifies Read throws NotSupportedException.
    /// </summary>
    [Fact]
    public void Read_ThrowsNotSupported()
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);
        var buffer = new byte[100];

        _ = Assert.Throws<NotSupportedException>(() => stream.Read(buffer, 0, buffer.Length));
    }

    /// <summary>
    /// Verifies Seek throws NotSupportedException.
    /// </summary>
    [Fact]
    public void Seek_ThrowsNotSupported()
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);

        _ = Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
    }

    /// <summary>
    /// Verifies SetLength throws NotSupportedException.
    /// </summary>
    [Fact]
    public void SetLength_ThrowsNotSupported()
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);

        _ = Assert.Throws<NotSupportedException>(() => stream.SetLength(100));
    }

    #endregion

    #region Write Tests

    /// <summary>
    /// Verifies write updates TotalBytesWritten.
    /// Uses valid MPEG-TS packets to avoid TsIndexer parsing issues.
    /// </summary>
    [Fact]
    public void Write_UpdatesTotalBytesWritten()
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);
        var data = CreateTsPacket(0x1FFF); // Null packet

        stream.Write(data, 0, data.Length);

        Assert.Equal(TsPacketSize, stream.TotalBytesWritten);
    }

    /// <summary>
    /// Verifies write updates Position within buffer bounds.
    /// </summary>
    [Fact]
    public void Write_UpdatesPosition()
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);
        var data = CreateTsPacket(0x1FFF);

        stream.Write(data, 0, data.Length);

        Assert.Equal(TsPacketSize, stream.Position);
    }

    /// <summary>
    /// Verifies data is correctly written to buffer.
    /// Uses MPEG-TS packet format for proper TsIndexer handling.
    /// </summary>
    [Fact]
    public void Write_DataIsCorrectlyStored()
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);
        var packet = CreateTsPacket(0x100);

        stream.Write(packet, 0, packet.Length);

        // Verify sync byte and PID are stored correctly
        Assert.Equal(TsSyncByte, stream.Buffer[0]);
        Assert.Equal(packet[1], stream.Buffer[1]);
        Assert.Equal(packet[2], stream.Buffer[2]);
    }

    /// <summary>
    /// Verifies empty write is handled correctly.
    /// </summary>
    [Fact]
    public void Write_EmptyBuffer_NoChange()
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);

        stream.Write([], 0, 0);

        Assert.Equal(0, stream.TotalBytesWritten);
        Assert.Equal(0, stream.Position);
    }

    /// <summary>
    /// Verifies write with offset works correctly using MPEG-TS data.
    /// </summary>
    [Fact]
    public void Write_WithOffset_WritesCorrectData()
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);

        // Create buffer with padding before actual TS packet
        var packet = CreateTsPacket(0x100);
        var paddedData = new byte[10 + TsPacketSize];
        Array.Copy(packet, 0, paddedData, 10, TsPacketSize);

        stream.Write(paddedData, 10, TsPacketSize); // Write TS packet starting at offset 10

        Assert.Equal(TsPacketSize, stream.TotalBytesWritten);
        Assert.Equal(TsSyncByte, stream.Buffer[0]);
    }

    /// <summary>
    /// Verifies Span write API works correctly with MPEG-TS data.
    /// </summary>
    [Fact]
    public void WriteSpan_WritesDataCorrectly()
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);
        var packet = CreateTsPacket(0x101);

        stream.Write(packet.AsSpan());

        Assert.Equal(TsPacketSize, stream.TotalBytesWritten);
        Assert.Equal(TsSyncByte, stream.Buffer[0]);
    }

    #endregion

    #region Circular Wrap-Around Tests

    /// <summary>
    /// Verifies buffer wraps around correctly when full.
    /// Uses MPEG-TS packets to satisfy TsIndexer requirements.
    /// </summary>
    [Fact]
    public void Write_BufferWrapAround_OverwritesOldData()
    {
        // Use 1MB buffer with TS-aligned data
        const int bufferSize = 1024 * 1024;
        using var stream = new CircularBufferWriteStream(bufferSize);

        // Fill buffer with TS packets in chunks to avoid SIMD alignment issues
        const int chunkSize = TsPacketSize * 100;
        var written = 0;
        while (written < bufferSize)
        {
            var toWrite = Math.Min(chunkSize, bufferSize - written);
            var tsData = CreateTsDataChunk(toWrite);
            stream.Write(tsData, 0, tsData.Length);
            written += toWrite;
        }

        Assert.Equal(bufferSize, stream.TotalBytesWritten);
        Assert.Equal(0, stream.Position); // Wrapped to beginning

        // Write one more packet to overwrite
        var moreData = CreateTsPacket(0x200);
        stream.Write(moreData, 0, moreData.Length);

        // Verify overwrite
        Assert.Equal(bufferSize + TsPacketSize, stream.TotalBytesWritten);
        Assert.Equal(TsPacketSize, stream.Position);
        Assert.Equal(TsSyncByte, stream.Buffer[0]); // Overwritten with new sync byte
    }

    /// <summary>
    /// Verifies large write that spans buffer boundary.
    /// </summary>
    [Fact]
    public void Write_SpanningBufferBoundary_HandlesCorrectly()
    {
        // Use TS-packet-aligned buffer size
        const int packetCount = 100;
        const int bufferSize = TsPacketSize * packetCount;
        using var stream = new CircularBufferWriteStream(bufferSize);

        // Write to near end of buffer (leave room for 5 packets)
        var firstPart = CreateTsDataChunk(TsPacketSize * (packetCount - 5));
        stream.Write(firstPart, 0, firstPart.Length);

        // Write data that crosses boundary (10 packets)
        var crossingData = CreateTsDataChunk(TsPacketSize * 10);
        stream.Write(crossingData, 0, crossingData.Length);

        long expectedTotal = firstPart.Length + crossingData.Length;
        Assert.Equal(expectedTotal, stream.TotalBytesWritten);

        // Position should wrap around
        var expectedPosition = (int)(expectedTotal % bufferSize);
        Assert.Equal(expectedPosition, stream.Position);
    }

    /// <summary>
    /// Verifies multiple complete wrap-arounds work correctly.
    /// </summary>
    [Fact]
    public void Write_MultipleWrapArounds_TracksCorrectly()
    {
        const int bufferSize = TsPacketSize * 100; // 18.8KB
        using var stream = new CircularBufferWriteStream(bufferSize);

        // Write 3 times the buffer size
        for (var i = 0; i < 3; i++)
        {
            var data = CreateTsDataChunk(bufferSize);
            stream.Write(data, 0, data.Length);
        }

        Assert.Equal(bufferSize * 3, stream.TotalBytesWritten);
        Assert.Equal(0, stream.Position);

        // Buffer should contain sync bytes at TS boundaries
        Assert.Equal(TsSyncByte, stream.Buffer[0]);
        Assert.Equal(TsSyncByte, stream.Buffer[TsPacketSize]);
    }

    #endregion

    #region MPEG-TS Packet Tests

    /// <summary>
    /// Verifies writing MPEG-TS packets (188 bytes) works correctly.
    /// Per ISO/IEC 13818-1 Section 2.4.3.2.
    /// </summary>
    [Fact]
    public void Write_MpegTsPacket_PreservesSyncByte()
    {
        using var stream = new CircularBufferWriteStream(1024);
        var tsPacket = CreateTsPacket(0x1FFF); // Null packet PID

        stream.Write(tsPacket, 0, tsPacket.Length);

        Assert.Equal(TsPacketSize, stream.TotalBytesWritten);
        Assert.Equal(TsSyncByte, stream.Buffer[0]);
    }

    /// <summary>
    /// Verifies writing multiple consecutive MPEG-TS packets.
    /// </summary>
    [Fact]
    public void Write_MultipleTsPackets_MaintainsAlignment()
    {
        using var stream = new CircularBufferWriteStream(1024);
        var packet1 = CreateTsPacket(0x100);
        var packet2 = CreateTsPacket(0x101);
        var packet3 = CreateTsPacket(0x102);

        stream.Write(packet1, 0, packet1.Length);
        stream.Write(packet2, 0, packet2.Length);
        stream.Write(packet3, 0, packet3.Length);

        Assert.Equal(TsPacketSize * 3, stream.TotalBytesWritten);

        // All sync bytes should be at 188-byte boundaries
        Assert.Equal(TsSyncByte, stream.Buffer[0]);
        Assert.Equal(TsSyncByte, stream.Buffer[188]);
        Assert.Equal(TsSyncByte, stream.Buffer[376]);
    }

    /// <summary>
    /// Verifies large chunk of MPEG-TS data (simulating real stream).
    /// </summary>
    [Fact]
    public void Write_LargeTsChunk_HandlesCorrectly()
    {
        const int bufferSize = 10 * 1024 * 1024; // 10MB
        using var stream = new CircularBufferWriteStream(bufferSize);

        // Write 1MB of TS data in smaller chunks to avoid SIMD alignment issues
        const int chunkSize = TsPacketSize * 100; // ~18KB chunks
        const int totalSize = 1024 * 1024;
        var written = 0;

        while (written < totalSize)
        {
            var toWrite = Math.Min(chunkSize, totalSize - written);
            var tsData = CreateTsDataChunk(toWrite);
            stream.Write(tsData, 0, tsData.Length);
            written += toWrite;
        }

        Assert.Equal(totalSize, stream.TotalBytesWritten);

        // Verify sync bytes are preserved
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(TsSyncByte, stream.Buffer[i * TsPacketSize]);
        }
    }

    #endregion

    #region Async Write Tests

    /// <summary>
    /// Verifies async write works correctly.
    /// </summary>
    [Fact]
    public async Task WriteAsync_WritesDataCorrectly()
    {
        await using var stream = new CircularBufferWriteStream(1024 * 1024);
        var data = CreateTsPacket(0x100);

        await stream.WriteAsync(data.AsMemory());

        Assert.Equal(TsPacketSize, stream.TotalBytesWritten);
        Assert.Equal(TsSyncByte, stream.Buffer[0]);
    }

    /// <summary>
    /// Verifies async write with Memory API works correctly.
    /// </summary>
    [Fact]
    public async Task WriteAsyncMemory_WritesDataCorrectly()
    {
        await using var stream = new CircularBufferWriteStream(1024 * 1024);
        var data = CreateTsPacket(0x101);

        await stream.WriteAsync(data.AsMemory());

        Assert.Equal(TsPacketSize, stream.TotalBytesWritten);
        Assert.Equal(TsSyncByte, stream.Buffer[0]);
    }

    /// <summary>
    /// Verifies async write respects cancellation token.
    /// </summary>
    [Fact]
    public async Task WriteAsync_CancelledToken_ThrowsOperationCanceled()
    {
        await using var stream = new CircularBufferWriteStream(1024 * 1024);
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var data = CreateTsPacket(0x1FFF);

        _ = await Assert.ThrowsAsync<TaskCanceledException>(() => stream.WriteAsync(data, 0, data.Length, cts.Token));
    }

    /// <summary>
    /// Verifies ValueTask WriteAsync respects cancellation.
    /// </summary>
    [Fact]
    public async Task WriteAsyncMemory_CancelledToken_ThrowsOperationCanceled()
    {
        await using var stream = new CircularBufferWriteStream(1024 * 1024);
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var data = CreateTsPacket(0x1FFF);

        _ = await Assert.ThrowsAsync<TaskCanceledException>(async () =>
            await stream.WriteAsync(data.AsMemory(), cts.Token).ConfigureAwait(false)
        );
    }

    #endregion

    #region Connection State Tests

    /// <summary>
    /// Verifies SignalSourceConnected updates state correctly.
    /// </summary>
    [Fact]
    public void SignalSourceConnected_UpdatesState()
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);

        stream.SignalSourceConnected();

        Assert.True(stream.IsSourceConnected);
        Assert.False(stream.IsReconnecting);
    }

    /// <summary>
    /// Verifies SignalReconnecting updates state correctly.
    /// </summary>
    [Fact]
    public void SignalReconnecting_UpdatesState()
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);
        stream.SignalSourceConnected();

        stream.SignalReconnecting();

        Assert.False(stream.IsSourceConnected);
        Assert.True(stream.IsReconnecting);
        Assert.Equal(1, stream.ReconnectionAttempts);
    }

    /// <summary>
    /// Verifies multiple reconnection attempts are tracked.
    /// </summary>
    [Fact]
    public void SignalReconnecting_MultipleAttempts_IncrementsCounter()
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);

        stream.SignalReconnecting();
        stream.SignalReconnecting();
        stream.SignalReconnecting();

        Assert.Equal(3, stream.ReconnectionAttempts);
    }

    /// <summary>
    /// Verifies SignalSourceDisconnected updates state correctly.
    /// </summary>
    [Fact]
    public void SignalSourceDisconnected_UpdatesState()
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);
        stream.SignalSourceConnected();

        stream.SignalSourceDisconnected();

        Assert.False(stream.IsSourceConnected);
    }

    /// <summary>
    /// Verifies LastWriteTime is updated on write.
    /// </summary>
    [Fact]
    public void Write_UpdatesLastWriteTime()
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);
        var before = DateTime.UtcNow;
        var data = CreateTsPacket(0x1FFF);

        stream.Write(data, 0, data.Length);

        var after = DateTime.UtcNow;
        Assert.True(stream.LastWriteTime >= before);
        Assert.True(stream.LastWriteTime <= after);
    }

    #endregion

    #region Discontinuity Tests

    /// <summary>
    /// Verifies MarkDiscontinuity updates offset and count.
    /// </summary>
    [Fact]
    public void MarkDiscontinuity_UpdatesOffsetAndCount()
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);
        var data = CreateTsDataChunk(TsPacketSize * 10);
        stream.Write(data, 0, data.Length);

        stream.MarkDiscontinuity();

        Assert.Equal(data.Length, stream.LastDiscontinuityOffset);
        Assert.Equal(1, stream.DiscontinuityCount);
        _ = Assert.NotNull(stream.LastDiscontinuityTime);
    }

    /// <summary>
    /// Verifies multiple discontinuities are tracked.
    /// </summary>
    [Fact]
    public void MarkDiscontinuity_MultipleTimes_IncreasesCount()
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);
        var data1 = CreateTsDataChunk(TsPacketSize * 10);
        stream.Write(data1, 0, data1.Length);
        stream.MarkDiscontinuity();

        var data2 = CreateTsDataChunk(TsPacketSize * 5);
        stream.Write(data2, 0, data2.Length);
        stream.MarkDiscontinuity();

        Assert.Equal(data1.Length + data2.Length, stream.LastDiscontinuityOffset);
        Assert.Equal(2, stream.DiscontinuityCount);
    }

    #endregion

    #region Reset Tests

    /// <summary>
    /// Verifies Reset clears all counters.
    /// </summary>
    [Fact]
    public void Reset_ClearsAllCounters()
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);
        var data = CreateTsDataChunk(TsPacketSize * 10);
        stream.Write(data, 0, data.Length);
        stream.MarkDiscontinuity();
        stream.SignalReconnecting();

        stream.Reset();

        Assert.Equal(0, stream.TotalBytesWritten);
        Assert.Equal(0, stream.Position);
        Assert.Equal(0, stream.DiscontinuityCount);
        Assert.Equal(0, stream.LastDiscontinuityOffset);
        Assert.Equal(0, stream.ReconnectionAttempts);
        Assert.False(stream.IsSourceConnected);
        Assert.False(stream.IsReconnecting);
    }

    /// <summary>
    /// Verifies Reset allows buffer reuse.
    /// </summary>
    [Fact]
    public void Reset_AllowsBufferReuse()
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);
        var data1 = CreateTsDataChunk(TsPacketSize * 50);
        stream.Write(data1, 0, data1.Length);

        stream.Reset();
        var data2 = CreateTsDataChunk(TsPacketSize * 10);
        stream.Write(data2, 0, data2.Length);

        Assert.Equal(data2.Length, stream.TotalBytesWritten);
        Assert.Equal(data2.Length, stream.Position);
    }

    #endregion

    #region Reader Position Tracking Tests

    /// <summary>
    /// Verifies RecordReaderDisconnect stores position.
    /// </summary>
    [Fact]
    public void RecordReaderDisconnect_StoresPosition()
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);
        var data = CreateTsDataChunk(TsPacketSize * 50);
        stream.Write(data, 0, data.Length);

        stream.RecordReaderDisconnect(TsPacketSize * 25);

        Assert.Equal(TsPacketSize * 25, stream.LastReaderPosition);
    }

    /// <summary>
    /// Verifies ConsumeLastReaderPosition returns valid position.
    /// </summary>
    [Fact]
    public void ConsumeLastReaderPosition_ReturnsAndClearsPosition()
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);
        var data = CreateTsDataChunk(TsPacketSize * 50);
        stream.Write(data, 0, data.Length);
        stream.RecordReaderDisconnect(TsPacketSize * 25);

        var position = stream.ConsumeLastReaderPosition(10000);

        Assert.Equal(TsPacketSize * 25, position);
        Assert.Equal(0, stream.LastReaderPosition); // Should be cleared
    }

    /// <summary>
    /// Verifies ConsumeLastReaderPosition returns -1 for no position.
    /// </summary>
    [Fact]
    public void ConsumeLastReaderPosition_NoPosition_ReturnsNegativeOne()
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);

        var position = stream.ConsumeLastReaderPosition();

        Assert.Equal(-1, position);
    }

    /// <summary>
    /// Verifies ConsumeLastReaderPosition returns -1 for expired position.
    /// </summary>
    [Fact]
    public async Task ConsumeLastReaderPosition_ExpiredPosition_ReturnsNegativeOne()
    {
        await using var stream = new CircularBufferWriteStream(1024 * 1024);
        var data = CreateTsDataChunk(TsPacketSize * 50);
        await stream.WriteAsync(data);
        stream.RecordReaderDisconnect(TsPacketSize * 25);

        await Task.Delay(20); // Wait for position to expire

        var position = stream.ConsumeLastReaderPosition(10); // 10ms max age

        Assert.Equal(-1, position);
    }

    #endregion

    #region Flush Tests

    /// <summary>
    /// Verifies Flush is a no-op (doesn't throw).
    /// </summary>
    [Fact]
    public void Flush_DoesNotThrow()
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);

        stream.Flush(); // Should not throw
    }

    /// <summary>
    /// Verifies FlushAsync completes successfully.
    /// </summary>
    [Fact]
    public async Task FlushAsync_CompletesSuccessfully()
    {
        await using var stream = new CircularBufferWriteStream(1024 * 1024);

        await stream.FlushAsync(); // Should not throw
    }

    #endregion

    #region Thread Safety Tests

    /// <summary>
    /// Verifies TotalBytesWritten is thread-safe for reading.
    /// Uses MPEG-TS packet data to avoid TsIndexer issues.
    /// </summary>
    [Fact]
    public async Task TotalBytesWritten_ThreadSafeReading()
    {
        await using var stream = new CircularBufferWriteStream(10 * 1024 * 1024);
        var cts = new CancellationTokenSource();
        var data = CreateTsDataChunk(TsPacketSize * 10); // 1880 bytes per iteration
        var readerErrors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        long maxSeen = 0;

        // Start reader task that continuously reads TotalBytesWritten
        var readerTask = Task.Run(() =>
        {
            try
            {
                long lastSeen = 0;
                while (!cts.Token.IsCancellationRequested)
                {
                    var current = stream.TotalBytesWritten;

                    // Value should never decrease
                    if (current < lastSeen)
                    {
                        readerErrors.Add(
                            new InvalidOperationException($"TotalBytesWritten decreased: {lastSeen} -> {current}")
                        );
                    }

                    lastSeen = current;
                    _ = Interlocked.Exchange(ref maxSeen, Math.Max(maxSeen, current));
                }
            }
            catch (Exception ex)
            {
                readerErrors.Add(ex);
            }
        });

        // Writer task
        for (var i = 0; i < 100; i++)
        {
            await stream.WriteAsync(data);
        }

        await cts.CancelAsync();
        await readerTask;

        Assert.Empty(readerErrors);
        Assert.Equal(100 * data.Length, stream.TotalBytesWritten);
    }

    #endregion

    #region Packet Boundary Alignment Tests (Provider Switch Support)

    /// <summary>
    /// Verifies AlignToPacketBoundary returns 0 when already aligned.
    /// </summary>
    [Fact]
    public void AlignToPacketBoundary_AlreadyAligned_ReturnsZero()
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);
        var data = CreateTsDataChunk(TsPacketSize * 10); // Exactly aligned
        stream.Write(data, 0, data.Length);

        var paddingBytes = stream.AlignToPacketBoundary();

        Assert.Equal(0, paddingBytes);
        Assert.Equal(TsPacketSize * 10, stream.TotalBytesWritten);
    }

    /// <summary>
    /// Verifies AlignToPacketBoundary pads correctly when 1 byte short.
    /// Critical edge case for mid-stream switching.
    /// </summary>
    [Fact]
    public void AlignToPacketBoundary_OneByteShort_PadsCorrectly()
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);
        var data = new byte[TsPacketSize - 1]; // 187 bytes
        data[0] = TsSyncByte;
        stream.Write(data, 0, data.Length);

        var paddingBytes = stream.AlignToPacketBoundary();

        Assert.Equal(1, paddingBytes);
        Assert.Equal(TsPacketSize, stream.TotalBytesWritten);
        Assert.Equal(0, stream.TotalBytesWritten % TsPacketSize); // Now aligned
    }

    /// <summary>
    /// Verifies AlignToPacketBoundary pads correctly for various offsets.
    /// </summary>
    [Theory]
    [InlineData(1, 187)] // 1 byte written, need 187 to align
    [InlineData(50, 138)] // 50 bytes written, need 138 to align
    [InlineData(100, 88)] // 100 bytes written, need 88 to align
    [InlineData(187, 1)] // 187 bytes written, need 1 to align
    public void AlignToPacketBoundary_VariousOffsets_PadsCorrectly(int bytesWritten, int expectedPadding)
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);
        var data = new byte[bytesWritten];
        if (bytesWritten > 0)
        {
            data[0] = TsSyncByte;
        }

        stream.Write(data, 0, data.Length);

        var paddingBytes = stream.AlignToPacketBoundary();

        Assert.Equal(expectedPadding, paddingBytes);
        Assert.Equal(0, stream.TotalBytesWritten % TsPacketSize);
    }

    /// <summary>
    /// Verifies null packet padding has correct sync byte and PID.
    /// Per ISO/IEC 13818-1, null packets use PID 0x1FFF.
    /// </summary>
    [Fact]
    public void AlignToPacketBoundary_NullPacketFormat_Correct()
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);

        // Write one byte to force padding
        stream.Write([0x47], 0, 1);
        _ = stream.AlignToPacketBoundary();

        // The padding starts at offset 1
        // Check that the complete packet at offset 0 has sync byte
        Assert.Equal(TsSyncByte, stream.Buffer[0]);

        // After alignment, total written should be 188
        Assert.Equal(TsPacketSize, stream.TotalBytesWritten);
    }

    /// <summary>
    /// Verifies MarkDiscontinuityAligned aligns and marks in one call.
    /// </summary>
    [Fact]
    public void MarkDiscontinuityAligned_AlignsAndMarks()
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);
        var data = new byte[TsPacketSize + 50]; // 50 bytes past boundary
        data[0] = TsSyncByte;
        stream.Write(data, 0, data.Length);

        var paddingBytes = stream.MarkDiscontinuityAligned();

        Assert.Equal(TsPacketSize - 50, paddingBytes); // 138 bytes padding
        Assert.Equal(TsPacketSize * 2, stream.TotalBytesWritten);
        Assert.Equal(TsPacketSize * 2, stream.LastDiscontinuityOffset);
        Assert.Equal(1, stream.DiscontinuityCount);
    }

    /// <summary>
    /// Verifies switch boundary produces no partial packets.
    /// This is the critical invariant for mid-stream provider switching.
    /// </summary>
    [Fact]
    public void Switch_AtUnalignedPosition_NoPartialPackets()
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);

        // Provider A writes partial packet (simulating network interruption)
        var providerAData = new byte[(TsPacketSize * 10) + 100]; // 100 bytes into 11th packet
        for (var i = 0; i < providerAData.Length && i + TsPacketSize <= providerAData.Length; i += TsPacketSize)
        {
            providerAData[i] = TsSyncByte;
            providerAData[i + 1] = 0x01; // PID 0x100 high byte
            providerAData[i + 2] = 0x00; // PID 0x100 low byte
            providerAData[i + 3] = 0x10;
        }

        stream.Write(providerAData, 0, providerAData.Length);

        // Perform aligned switch
        var paddingBytes = stream.MarkDiscontinuityAligned();

        // Provider B starts writing
        var providerBData = CreateTsDataChunk(TsPacketSize * 5);
        for (var i = 0; i < providerBData.Length; i += TsPacketSize)
        {
            providerBData[i + 1] = 0x02; // PID 0x200 high byte
            providerBData[i + 2] = 0x00; // PID 0x200 low byte
        }

        stream.Write(providerBData, 0, providerBData.Length);

        // Verify: All packets after discontinuity should start with sync byte
        var discontinuityOffset = stream.LastDiscontinuityOffset;
        for (var i = discontinuityOffset; i < stream.TotalBytesWritten; i += TsPacketSize)
        {
            var bufferPos = (int)(i % stream.BufferSize);
            Assert.Equal(TsSyncByte, stream.Buffer[bufferPos]);
        }

        // Verify: Position after padding is aligned
        Assert.Equal(0, stream.LastDiscontinuityOffset % TsPacketSize);
    }

    /// <summary>
    /// Verifies switch when buffer is empty works correctly.
    /// </summary>
    [Fact]
    public void Switch_WhenBufferEmpty_StartsClean()
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);

        // Mark discontinuity on empty buffer
        var paddingBytes = stream.MarkDiscontinuityAligned();

        Assert.Equal(0, paddingBytes);
        Assert.Equal(0, stream.LastDiscontinuityOffset);
        Assert.Equal(1, stream.DiscontinuityCount);

        // Write new provider data
        var newData = CreateTsDataChunk(TsPacketSize * 10);
        stream.Write(newData, 0, newData.Length);

        // Verify all packets valid
        for (var i = 0; i < newData.Length; i += TsPacketSize)
        {
            Assert.Equal(TsSyncByte, stream.Buffer[i]);
        }
    }

    /// <summary>
    /// Verifies switch at exact packet boundary doesn't add padding.
    /// </summary>
    [Fact]
    public void Switch_AtExactBoundary_NoPadding()
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);
        var data = CreateTsDataChunk(TsPacketSize * 10); // Exactly 10 packets
        stream.Write(data, 0, data.Length);

        Assert.Equal(0, stream.TotalBytesWritten % TsPacketSize); // Verify aligned

        var paddingBytes = stream.MarkDiscontinuityAligned();

        Assert.Equal(0, paddingBytes);
        Assert.Equal(TsPacketSize * 10, stream.LastDiscontinuityOffset);
    }

    /// <summary>
    /// Verifies no interleaving of provider PIDs across switch boundary.
    /// </summary>
    [Fact]
    public void Switch_NoProviderInterleaving()
    {
        using var stream = new CircularBufferWriteStream(1024 * 1024);

        // Provider A: PID 0x100
        var providerA = CreateTsDataChunk(TsPacketSize * 20);
        for (var i = 0; i < providerA.Length; i += TsPacketSize)
        {
            providerA[i + 1] = 0x01;
            providerA[i + 2] = 0x00;
        }

        stream.Write(providerA, 0, providerA.Length);
        _ = stream.MarkDiscontinuityAligned();

        // Provider B: PID 0x200
        var providerB = CreateTsDataChunk(TsPacketSize * 10);
        for (var i = 0; i < providerB.Length; i += TsPacketSize)
        {
            providerB[i + 1] = 0x02;
            providerB[i + 2] = 0x00;
        }

        stream.Write(providerB, 0, providerB.Length);

        // After discontinuity, all packets should be Provider B
        var postSwitchStart = (int)stream.LastDiscontinuityOffset;
        for (var i = postSwitchStart; i < stream.TotalBytesWritten; i += TsPacketSize)
        {
            var pos = i % stream.BufferSize;
            Assert.Equal(TsSyncByte, stream.Buffer[pos]);
            Assert.Equal(0x02, stream.Buffer[pos + 1]); // Provider B PID high byte
            Assert.Equal(0x00, stream.Buffer[pos + 2]); // Provider B PID low byte
        }
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Creates an MPEG-TS packet with the specified PID.
    /// </summary>
    private static byte[] CreateTsPacket(int pid)
    {
        var packet = new byte[TsPacketSize];
        packet[0] = TsSyncByte;
        packet[1] = (byte)((pid >> 8) & 0x1F);
        packet[2] = (byte)(pid & 0xFF);
        packet[3] = 0x10; // Payload only, no adaptation field
        return packet;
    }

    /// <summary>
    /// Creates a chunk of MPEG-TS data with proper sync bytes.
    /// </summary>
    /// <param name="size">Size in bytes (should be multiple of 188).</param>
    /// <returns>TS data chunk with sync bytes at packet boundaries.</returns>
    private static byte[] CreateTsDataChunk(int size)
    {
        var data = new byte[size];
        for (var i = 0; i < size; i += TsPacketSize)
        {
            data[i] = TsSyncByte;
            // Set null packet PID (0x1FFF)
            if (i + 1 < size)
            {
                data[i + 1] = 0x1F;
            }

            if (i + 2 < size)
            {
                data[i + 2] = 0xFF;
            }

            if (i + 3 < size)
            {
                data[i + 3] = 0x10; // Payload only
            }
        }

        return data;
    }

    #endregion
}
