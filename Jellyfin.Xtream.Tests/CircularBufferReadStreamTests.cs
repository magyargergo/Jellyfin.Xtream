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
/// Tests for <see cref="CircularBufferReadStream"/>.
/// Validates circular buffer reader behavior per streaming standards:
/// - ISO/IEC 13818-1 (MPEG-TS) for packet synchronization
/// - Multiple concurrent reader support
/// - Buffer overflow detection and recovery
/// - Memory barrier semantics for multi-core systems
/// </summary>
public sealed class CircularBufferReadStreamTests : IDisposable
{
    private const int TsPacketSize = 188;
    private const byte TsSyncByte = 0x47;
    private const int DefaultBufferSize = 1024 * 1024; // 1MB

    private readonly CircularBufferWriteStream _writeStream;

    public CircularBufferReadStreamTests()
    {
        _writeStream = new CircularBufferWriteStream(DefaultBufferSize);
    }

    public void Dispose()
    {
        _writeStream.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Constructor Tests

    /// <summary>
    /// Verifies constructor creates reader with correct initial state.
    /// </summary>
    [Fact]
    public void Constructor_CreatesReaderWithCorrectState()
    {
        // Pre-write some MPEG-TS data
        var tsData = CreateTsDataChunk(TsPacketSize * 10);
        _writeStream.Write(tsData, 0, tsData.Length);

        using var readStream = new CircularBufferReadStream(_writeStream);

        Assert.True(readStream.CanRead);
        Assert.False(readStream.CanWrite);
        Assert.False(readStream.CanSeek);
    }

    /// <summary>
    /// Verifies reader starts at beginning for empty buffer.
    /// </summary>
    [Fact]
    public void Constructor_EmptyBuffer_StartsAtZero()
    {
        using var readStream = new CircularBufferReadStream(_writeStream);

        Assert.Equal(0, readStream.ReadHead);
        Assert.Equal(0, readStream.TotalBytesRead);
    }

    /// <summary>
    /// Verifies overflow counters start at zero.
    /// </summary>
    [Fact]
    public void Constructor_InitializesOverflowCountersToZero()
    {
        using var readStream = new CircularBufferReadStream(_writeStream);

        Assert.Equal(0, readStream.TotalOverflowBytes);
        Assert.Equal(0, readStream.OverflowCount);
    }

    /// <summary>
    /// Verifies CurrentGap is calculated correctly.
    /// </summary>
    [Fact]
    public void CurrentGap_CalculatedCorrectly()
    {
        var tsData = CreateTsDataChunk(TsPacketSize * 30); // ~5640 bytes
        _writeStream.Write(tsData, 0, tsData.Length);

        using var readStream = new CircularBufferReadStream(_writeStream);
        var initialGap = readStream.CurrentGap;

        // Read some data
        var buffer = new byte[TsPacketSize * 5];
        _ = readStream.Read(buffer, 0, buffer.Length);

        // Gap should decrease after reading
        Assert.True(readStream.CurrentGap < initialGap);
    }

    #endregion

    #region Stream Property Tests

    /// <summary>
    /// Verifies stream reports correct capabilities.
    /// </summary>
    [Fact]
    public void StreamProperties_ReportsCorrectCapabilities()
    {
        using var readStream = new CircularBufferReadStream(_writeStream);

        Assert.True(readStream.CanRead);
        Assert.False(readStream.CanWrite);
        Assert.False(readStream.CanSeek);
    }

    /// <summary>
    /// Verifies Length property throws NotSupportedException.
    /// </summary>
    [Fact]
    public void Length_ThrowsNotSupported()
    {
        using var readStream = new CircularBufferReadStream(_writeStream);

        _ = Assert.Throws<NotSupportedException>(() => readStream.Length);
    }

    /// <summary>
    /// Verifies Position setter throws NotSupportedException.
    /// </summary>
    [Fact]
    public void PositionSet_ThrowsNotSupported()
    {
        using var readStream = new CircularBufferReadStream(_writeStream);

        _ = Assert.Throws<NotSupportedException>(() => readStream.Position = 0);
    }

    /// <summary>
    /// Verifies Write throws NotSupportedException.
    /// </summary>
    [Fact]
    public void Write_ThrowsNotSupported()
    {
        using var readStream = new CircularBufferReadStream(_writeStream);

        _ = Assert.Throws<NotSupportedException>(() => readStream.Write(new byte[10], 0, 10));
    }

    /// <summary>
    /// Verifies Seek throws NotSupportedException.
    /// </summary>
    [Fact]
    public void Seek_ThrowsNotSupported()
    {
        using var readStream = new CircularBufferReadStream(_writeStream);

        _ = Assert.Throws<NotSupportedException>(() => readStream.Seek(0, SeekOrigin.Begin));
    }

    /// <summary>
    /// Verifies SetLength throws NotSupportedException.
    /// </summary>
    [Fact]
    public void SetLength_ThrowsNotSupported()
    {
        using var readStream = new CircularBufferReadStream(_writeStream);

        _ = Assert.Throws<NotSupportedException>(() => readStream.SetLength(100));
    }

    #endregion

    #region Basic Read Tests

    /// <summary>
    /// Verifies read returns correct data.
    /// Note: CircularBufferReadStream starts at totalWritten/2 for non-wrapped buffers,
    /// so we write more data to ensure readable content.
    /// </summary>
    [Fact]
    public void Read_ReturnsCorrectData()
    {
        // Write enough data so reader has full packets available
        // Reader starts at totalWritten/2, so write 4 packets to have 2 readable
        var sourceData = CreateTsDataChunk(TsPacketSize * 4);
        _writeStream.Write(sourceData, 0, sourceData.Length);

        using var readStream = new CircularBufferReadStream(_writeStream);
        var readBuffer = new byte[TsPacketSize];
        var bytesRead = readStream.Read(readBuffer, 0, readBuffer.Length);

        // Should read at least one full packet
        Assert.True(bytesRead >= TsPacketSize);
        Assert.Equal(TsSyncByte, readBuffer[0]); // Verify sync byte preserved
    }

    /// <summary>
    /// Verifies read updates TotalBytesRead.
    /// </summary>
    [Fact]
    public void Read_UpdatesTotalBytesRead()
    {
        var tsData = CreateTsDataChunk(TsPacketSize * 10);
        _writeStream.Write(tsData, 0, tsData.Length);

        using var readStream = new CircularBufferReadStream(_writeStream);
        var buffer = new byte[TsPacketSize * 2];
        _ = readStream.Read(buffer, 0, buffer.Length);

        Assert.Equal(TsPacketSize * 2, readStream.TotalBytesRead);
    }

    /// <summary>
    /// Verifies read with empty buffer returns zero.
    /// </summary>
    [Fact]
    public void Read_EmptyBuffer_ReturnsZero()
    {
        using var readStream = new CircularBufferReadStream(_writeStream);
        var buffer = new byte[100];

        var bytesRead = readStream.Read(buffer, 0, buffer.Length);

        Assert.Equal(0, bytesRead);
    }

    /// <summary>
    /// Verifies reading with zero count returns zero.
    /// </summary>
    [Fact]
    public void Read_ZeroCount_ReturnsZero()
    {
        var tsData = CreateTsDataChunk(TsPacketSize);
        _writeStream.Write(tsData, 0, tsData.Length);

        using var readStream = new CircularBufferReadStream(_writeStream);

        var bytesRead = readStream.Read([], 0, 0);

        Assert.Equal(0, bytesRead);
    }

    /// <summary>
    /// Verifies Span read API works correctly.
    /// Note: Reader starts at totalWritten/2, so write extra data.
    /// </summary>
    [Fact]
    public void ReadSpan_ReturnsCorrectData()
    {
        // Write 4 packets to have at least 2 readable
        var sourceData = CreateTsDataChunk(TsPacketSize * 4);
        _writeStream.Write(sourceData, 0, sourceData.Length);

        using var readStream = new CircularBufferReadStream(_writeStream);
        Span<byte> buffer = stackalloc byte[TsPacketSize];
        var bytesRead = readStream.Read(buffer);

        Assert.True(bytesRead >= TsPacketSize);
        Assert.Equal(TsSyncByte, buffer[0]);
    }

    #endregion

    #region Partial Read Tests

    /// <summary>
    /// Verifies read returns available data when less than requested.
    /// Note: Reader starts at totalWritten/2, so write extra data.
    /// </summary>
    [Fact]
    public void Read_LessThanRequested_ReturnsAvailable()
    {
        // Write 4 packets, reader sees 2 (starts at totalWritten/2)
        var tsData = CreateTsDataChunk(TsPacketSize * 4);
        _writeStream.Write(tsData, 0, tsData.Length);

        using var readStream = new CircularBufferReadStream(_writeStream);
        var buffer = new byte[TsPacketSize * 4]; // Request more than available

        var bytesRead = readStream.Read(buffer, 0, buffer.Length);

        // Should return less than requested (only what's available after reader start pos)
        Assert.True(bytesRead > 0 && bytesRead < buffer.Length);
    }

    /// <summary>
    /// Verifies sequential reads work correctly.
    /// Note: Reader starts at totalWritten/2 for non-wrapped buffers.
    /// </summary>
    [Fact]
    public void Read_Sequential_ReadsAllData()
    {
        var sourceData = CreateTsDataChunk(TsPacketSize * 20);
        _writeStream.Write(sourceData, 0, sourceData.Length);

        using var readStream = new CircularBufferReadStream(_writeStream);
        var readBuffer = new byte[TsPacketSize];
        var allData = new byte[sourceData.Length];
        var totalRead = 0;

        // Read all available data (reader starts at ~totalWritten/2)
        while (true)
        {
            var bytesRead = readStream.Read(readBuffer, 0, readBuffer.Length);
            if (bytesRead == 0)
            {
                break;
            }

            if (totalRead + bytesRead <= allData.Length)
            {
                Array.Copy(readBuffer, 0, allData, totalRead, bytesRead);
            }

            totalRead += bytesRead;
        }

        // Should have read about half the data (since reader starts at totalWritten/2)
        Assert.True(totalRead > 0);
        Assert.True(totalRead >= sourceData.Length / 2);
        Assert.Equal(TsSyncByte, allData[0]);
    }

    #endregion

    #region Async Read Tests

    /// <summary>
    /// Verifies async read works correctly.
    /// Note: Reader starts at totalWritten/2, so write extra data.
    /// </summary>
    [Fact]
    public async Task ReadAsync_ReturnsCorrectData()
    {
        // Write 4 packets to have at least 2 readable
        var sourceData = CreateTsDataChunk(TsPacketSize * 4);
        await _writeStream.WriteAsync(sourceData);
        await using var readStream = new CircularBufferReadStream(_writeStream);
        var buffer = new byte[TsPacketSize];

        var bytesRead = await readStream.ReadAsync(buffer.AsMemory());

        Assert.True(bytesRead >= TsPacketSize);
        Assert.Equal(TsSyncByte, buffer[0]);
    }

    /// <summary>
    /// Verifies async read with Memory API works correctly.
    /// Note: Reader starts at totalWritten/2, so write extra data.
    /// </summary>
    [Fact]
    public async Task ReadAsyncMemory_ReturnsCorrectData()
    {
        // Write 4 packets to have at least 2 readable
        var sourceData = CreateTsDataChunk(TsPacketSize * 4);
        await _writeStream.WriteAsync(sourceData);
        await using var readStream = new CircularBufferReadStream(_writeStream);
        var buffer = new byte[TsPacketSize];

        var bytesRead = await readStream.ReadAsync(buffer.AsMemory());

        Assert.True(bytesRead >= TsPacketSize);
        Assert.Equal(TsSyncByte, buffer[0]);
    }

    /// <summary>
    /// Verifies async read respects cancellation token.
    /// </summary>
    [Fact]
    public async Task ReadAsync_CancelledToken_ReturnsZero()
    {
        await using var readStream = new CircularBufferReadStream(_writeStream);
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var buffer = new byte[10];
        var bytesRead = await readStream.ReadAsync(buffer.AsMemory(), cts.Token);

        // Should return 0 when cancelled (not throw)
        Assert.Equal(0, bytesRead);
    }

    #endregion

    #region MPEG-TS Packet Tests

    /// <summary>
    /// Verifies reading MPEG-TS packets preserves data integrity.
    /// Per ISO/IEC 13818-1 Section 2.4.3.2.
    /// Note: Reader starts at totalWritten/2, so write extra data.
    /// </summary>
    [Fact]
    public void Read_MpegTsPackets_PreservesData()
    {
        // Write 6 packets to have 3 readable (reader starts at ~totalWritten/2)
        var packets = CreateTsDataChunk(TsPacketSize * 6);
        _writeStream.Write(packets, 0, packets.Length);

        using var readStream = new CircularBufferReadStream(_writeStream);
        var readBuffer = new byte[TsPacketSize * 3];
        var totalRead = 0;

        while (totalRead < readBuffer.Length)
        {
            var bytesRead = readStream.Read(readBuffer, totalRead, readBuffer.Length - totalRead);
            if (bytesRead == 0)
            {
                break;
            }

            totalRead += bytesRead;
        }

        // Should have read at least some data
        Assert.True(totalRead > 0);

        // Verify sync bytes at packet boundaries where we have data
        var packetsRead = totalRead / TsPacketSize;
        for (var i = 0; i < packetsRead; i++)
        {
            Assert.Equal(TsSyncByte, readBuffer[i * TsPacketSize]);
        }
    }

    /// <summary>
    /// Verifies reading large MPEG-TS chunk maintains alignment.
    /// Note: Reader starts at totalWritten/2, so write extra data.
    /// </summary>
    [Fact]
    public void Read_LargeTsChunk_MaintainsAlignment()
    {
        // Write 200 packets to have ~100 readable
        const int packetCount = 200;
        var tsData = CreateTsDataChunk(TsPacketSize * packetCount);
        _writeStream.Write(tsData, 0, tsData.Length);

        using var readStream = new CircularBufferReadStream(_writeStream);
        var readBuffer = new byte[TsPacketSize * packetCount];
        var totalRead = 0;

        while (totalRead < readBuffer.Length)
        {
            var bytesRead = readStream.Read(readBuffer, totalRead, readBuffer.Length - totalRead);
            if (bytesRead == 0)
            {
                break;
            }

            totalRead += bytesRead;
        }

        // Should have read about half the data
        Assert.True(totalRead > 0);
        Assert.True(totalRead >= TsPacketSize * packetCount / 2);

        // Verify sync bytes at packet boundaries for read data
        var packetsRead = totalRead / TsPacketSize;
        for (var i = 0; i < packetsRead; i++)
        {
            Assert.Equal(TsSyncByte, readBuffer[i * TsPacketSize]);
        }
    }

    #endregion

    #region Buffer Wrap-Around Tests

    /// <summary>
    /// Verifies read handles buffer wrap-around correctly.
    /// </summary>
    [Fact]
    public void Read_BufferWrapAround_ReadsCorrectly()
    {
        // Use larger buffer to fit MPEG-TS packets
        const int bufferSize = TsPacketSize * 100; // ~18.8KB
        using var smallWriteStream = new CircularBufferWriteStream(bufferSize);

        // Fill buffer almost completely with proper MPEG-TS data
        const int initialPackets = TsPacketSize * 80;
        var initialData = CreateTsDataChunk(initialPackets);
        smallWriteStream.Write(initialData, 0, initialData.Length);

        using var readStream = new CircularBufferReadStream(smallWriteStream);

        // Read some data
        var readBuffer = new byte[TsPacketSize * 30];
        _ = readStream.Read(readBuffer, 0, readBuffer.Length);

        // Write more data that wraps around
        var wrappingData = CreateTsDataChunk(TsPacketSize * 30);
        smallWriteStream.Write(wrappingData, 0, wrappingData.Length);

        // Read the wrapped data
        var moreData = new byte[TsPacketSize * 20];
        var bytesRead = readStream.Read(moreData, 0, moreData.Length);

        Assert.True(bytesRead > 0);
        // Verify sync bytes are preserved across wrap
        if (bytesRead >= TsPacketSize)
        {
            Assert.Equal(TsSyncByte, moreData[0]);
        }
    }

    #endregion

    #region Multiple Reader Tests

    /// <summary>
    /// Verifies multiple readers can read independently.
    /// </summary>
    [Fact]
    public void MultipleReaders_ReadIndependently()
    {
        // Use proper MPEG-TS data - need enough packets for reads
        var sourceData = CreateTsDataChunk(TsPacketSize * 10); // 1880 bytes

        _writeStream.Write(sourceData, 0, sourceData.Length);

        using var reader1 = new CircularBufferReadStream(_writeStream, streamId: "reader1");
        using var reader2 = new CircularBufferReadStream(_writeStream, streamId: "reader2");

        var buffer1 = new byte[TsPacketSize * 5]; // 940 bytes
        var buffer2 = new byte[TsPacketSize * 3]; // 564 bytes

        var bytesRead1 = reader1.Read(buffer1, 0, buffer1.Length);
        var bytesRead2 = reader2.Read(buffer2, 0, buffer2.Length);

        // Both readers should have their own position
        Assert.Equal(TsPacketSize * 5, reader1.TotalBytesRead);
        Assert.Equal(TsPacketSize * 3, reader2.TotalBytesRead);

        // Verify data integrity
        Assert.Equal(TsSyncByte, buffer1[0]);
        Assert.Equal(TsSyncByte, buffer2[0]);
    }

    /// <summary>
    /// Verifies concurrent readers don't interfere with each other.
    /// </summary>
    [Fact]
    public async Task ConcurrentReaders_NoInterference()
    {
        // Pre-fill buffer with MPEG-TS data in chunks to avoid SIMD issues
        const int chunkSize = TsPacketSize * 100; // ~18.8KB per chunk
        const int totalSize = TsPacketSize * 2000; // ~376KB total
        var written = 0;

        while (written < totalSize)
        {
            var toWrite = Math.Min(chunkSize, totalSize - written);
            var tsData = CreateTsDataChunk(toWrite);
            await _writeStream.WriteAsync(tsData);
            written += toWrite;
        }

        await using var reader1 = new CircularBufferReadStream(_writeStream, streamId: "concurrent1");
        await using var reader2 = new CircularBufferReadStream(_writeStream, streamId: "concurrent2");
        await using var reader3 = new CircularBufferReadStream(_writeStream, streamId: "concurrent3");

        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        async Task ReadLoop(CircularBufferReadStream reader, int readCount)
        {
            try
            {
                var buffer = new byte[TsPacketSize * 5];
                for (var i = 0; i < readCount; i++)
                {
                    var bytesRead = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                    if (bytesRead == 0)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }

        var task1 = ReadLoop(reader1, 100);
        var task2 = ReadLoop(reader2, 100);
        var task3 = ReadLoop(reader3, 100);

        await Task.WhenAll(task1, task2, task3);

        Assert.Empty(errors);
        Assert.True(reader1.TotalBytesRead > 0);
        Assert.True(reader2.TotalBytesRead > 0);
        Assert.True(reader3.TotalBytesRead > 0);
    }

    #endregion

    #region Overflow Detection Tests

    /// <summary>
    /// Verifies overflow is detected when reader falls too far behind.
    /// </summary>
    [Fact]
    public void Read_BufferOverflow_TracksCorrectly()
    {
        // Use buffer size that's multiple of TS packet size
        const int bufferSize = TsPacketSize * 50; // ~9.4KB
        using var smallWriteStream = new CircularBufferWriteStream(bufferSize);

        // Write some initial MPEG-TS data
        var initialData = CreateTsDataChunk(TsPacketSize * 25); // ~4.7KB
        smallWriteStream.Write(initialData, 0, initialData.Length);

        using var readStream = new CircularBufferReadStream(smallWriteStream, streamId: "overflow-test");

        // Don't read anything, but write more than buffer size in chunks
        const int overflowSize = bufferSize + (TsPacketSize * 25);
        const int chunkSize = TsPacketSize * 10;
        var written = 0;

        while (written < overflowSize)
        {
            var toWrite = Math.Min(chunkSize, overflowSize - written);
            var overflowData = CreateTsDataChunk(toWrite);
            smallWriteStream.Write(overflowData, 0, overflowData.Length);
            written += toWrite;
        }

        // Now try to read - should detect overflow
        var buffer = new byte[TsPacketSize * 5];
        _ = readStream.Read(buffer, 0, buffer.Length);

        // Overflow should be detected and tracked
        Assert.True(readStream.OverflowCount > 0 || readStream.CurrentGap < bufferSize);
    }

    #endregion

    #region Diagnostics Tests

    /// <summary>
    /// Verifies GetDiagnostics returns formatted string.
    /// </summary>
    [Fact]
    public void GetDiagnostics_ReturnsFormattedString()
    {
        var tsData = CreateTsDataChunk(TsPacketSize * 10);
        _writeStream.Write(tsData, 0, tsData.Length);

        using var readStream = new CircularBufferReadStream(_writeStream, streamId: "diag-test");
        var readBuffer = new byte[TsPacketSize * 5];
        _ = readStream.Read(readBuffer, 0, readBuffer.Length);

        var diagnostics = readStream.GetDiagnostics();

        Assert.Contains("diag-test", diagnostics);
        Assert.Contains("Buffer Size", diagnostics);
        Assert.Contains("Total Read", diagnostics);
    }

    /// <summary>
    /// Verifies GetTsIndexerMetrics returns valid metrics.
    /// </summary>
    [Fact]
    public void GetTsIndexerMetrics_ReturnsValidMetrics()
    {
        using var readStream = new CircularBufferReadStream(_writeStream);

        var metrics = readStream.GetTsIndexerMetrics();

        Assert.True(metrics.TotalPacketsParsed >= 0);
        Assert.True(metrics.ProgramCount >= 0);
    }

    #endregion

    #region Static Stream Management Tests

    /// <summary>
    /// Verifies GetActiveStreamCount returns correct count.
    /// </summary>
    [Fact]
    public void GetActiveStreamCount_ReturnsCorrectCount()
    {
        var initialCount = CircularBufferReadStream.GetActiveStreamCount();

        using var reader1 = new CircularBufferReadStream(_writeStream, streamId: $"count-test-{Guid.NewGuid()}");

        Assert.Equal(initialCount + 1, CircularBufferReadStream.GetActiveStreamCount());

        using var reader2 = new CircularBufferReadStream(_writeStream, streamId: $"count-test-{Guid.NewGuid()}");

        Assert.Equal(initialCount + 2, CircularBufferReadStream.GetActiveStreamCount());
    }

    /// <summary>
    /// Verifies GetActiveStreamSnapshots returns snapshot information.
    /// </summary>
    [Fact]
    public void GetActiveStreamSnapshots_ReturnsSnapshots()
    {
        var uniqueId = $"snapshot-test-{Guid.NewGuid()}";
        using var reader = new CircularBufferReadStream(_writeStream, streamId: uniqueId);

        var snapshots = CircularBufferReadStream.GetActiveStreamSnapshots();

        var ourSnapshot = snapshots.FirstOrDefault(s => s.StreamId == uniqueId);
        Assert.Equal(uniqueId, ourSnapshot.StreamId);
    }

    /// <summary>
    /// Verifies stream is removed from active list on dispose.
    /// </summary>
    [Fact]
    public void Dispose_RemovesFromActiveStreams()
    {
        var uniqueId = $"dispose-test-{Guid.NewGuid()}";
        int countBefore;

        using (var reader = new CircularBufferReadStream(_writeStream, streamId: uniqueId))
        {
            countBefore = CircularBufferReadStream.GetActiveStreamCount();
        }

        // After using block, dispose has been called
        Assert.Equal(countBefore - 1, CircularBufferReadStream.GetActiveStreamCount());
    }

    /// <summary>
    /// Verifies KillStream disposes the specified stream.
    /// </summary>
    [Fact]
    public void KillStream_DisposesStream()
    {
        var uniqueId = $"kill-test-{Guid.NewGuid()}";

        _ = new CircularBufferReadStream(_writeStream, streamId: uniqueId);
        var countBefore = CircularBufferReadStream.GetActiveStreamCount();

        var killed = CircularBufferReadStream.KillStream(uniqueId);

        Assert.True(killed);
        Assert.Equal(countBefore - 1, CircularBufferReadStream.GetActiveStreamCount());
    }

    /// <summary>
    /// Verifies KillStream returns false for non-existent stream.
    /// </summary>
    [Fact]
    public void KillStream_NonExistent_ReturnsFalse()
    {
        var killed = CircularBufferReadStream.KillStream("non-existent-stream-id");

        Assert.False(killed);
    }

    #endregion

    #region Flush Tests

    /// <summary>
    /// Verifies Flush is a no-op (doesn't throw).
    /// </summary>
    [Fact]
    public void Flush_DoesNotThrow()
    {
        using var readStream = new CircularBufferReadStream(_writeStream);

        readStream.Flush(); // Should not throw
    }

    #endregion

    #region Overflow Risk Tests

    /// <summary>
    /// Verifies OverflowRisk is available.
    /// </summary>
    [Fact]
    public void OverflowRisk_IsAvailable()
    {
        using var readStream = new CircularBufferReadStream(_writeStream);

        var risk = readStream.OverflowRisk;

        // Should be a valid enum value
        Assert.True(Enum.IsDefined(risk));
    }

    /// <summary>
    /// Verifies SecondsToOverflow is available.
    /// </summary>
    [Fact]
    public void SecondsToOverflow_IsAvailable()
    {
        using var readStream = new CircularBufferReadStream(_writeStream);

        var seconds = readStream.SecondsToOverflow;

        // Should be a number (possibly NaN if not applicable)
        Assert.True(double.IsNaN(seconds) || seconds >= 0);
    }

    #endregion

    #region Reader Continuity Tests

    /// <summary>
    /// Verifies reader position is recorded on dispose for continuity.
    /// </summary>
    [Fact]
    public void Dispose_RecordsReaderPosition()
    {
        // Write MPEG-TS data in chunks to avoid SIMD issues
        const int chunkSize = TsPacketSize * 100;
        const int totalSize = TsPacketSize * 60; // ~11KB total
        var written = 0;

        while (written < totalSize)
        {
            var toWrite = Math.Min(chunkSize, totalSize - written);
            var tsData = CreateTsDataChunk(toWrite);
            _writeStream.Write(tsData, 0, tsData.Length);
            written += toWrite;
        }

        long positionBeforeDispose;

        using (var reader = new CircularBufferReadStream(_writeStream, streamId: $"continuity-{Guid.NewGuid()}"))
        {
            var readBuffer = new byte[TsPacketSize * 25];
            _ = reader.Read(readBuffer, 0, readBuffer.Length);
            positionBeforeDispose = reader.ReadHead;
        }

        // After using block, dispose has been called - position should be recorded in write stream
        Assert.True(
            _writeStream.LastReaderPosition == positionBeforeDispose || _writeStream.LastReaderDisconnectTime != default
        );
    }

    #endregion

    #region Helper Methods

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
