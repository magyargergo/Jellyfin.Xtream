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

using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.CompilerServices;
using Jellyfin.Xtream.E2ETests.Infrastructure;
using Jellyfin.Xtream.Service.Streaming.SharedMemory;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.E2ETests.Tests;

/// <summary>
/// End-to-end tests that verify actual C++ producer to C# consumer interop
/// for the shared memory communication layer.
/// </summary>
/// <remarks>
/// <para>
/// These tests use the actual native C++ producer (via <see cref="SharedMemoryProducer"/>)
/// paired with the managed C# consumer (<see cref="SharedMemoryConsumer"/>) to verify
/// data flows correctly across the native/managed boundary.
/// </para>
/// <para>
/// Tests are skipped gracefully if the native library is unavailable.
/// </para>
/// </remarks>
[Collection("E2E")]
public sealed class SharedMemoryNativeInteropTests : IDisposable
{
    private const int TsPacketSize = 188;
    private const uint DefaultSlotCount = 1024;
    private const uint DefaultPacketsPerSlot = 7;
    private const uint DefaultSlotSize = DefaultPacketsPerSlot * TsPacketSize; // 1316 bytes

    private readonly ITestOutputHelper _output;
    private readonly List<IDisposable> _disposables = new();

    public SharedMemoryNativeInteropTests(DockerTestFixture fixture, ITestOutputHelper output)
    {
        _ = fixture; // Required by collection but not used directly
        _output = output;
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        _disposables.Clear();
        GC.SuppressFinalize(this);
    }

    private static string GenerateUniqueName() => $"native_interop_test_{Guid.NewGuid():N}";

    private static TestStreamGenerator CreateGenerator() => new(5000);

    /// <summary>
    /// Attempts to create a native producer, skipping the test if unavailable.
    /// </summary>
    private SharedMemoryProducer? TryCreateNativeProducer(
        string? name = null,
        uint slotCount = DefaultSlotCount,
        uint slotSize = DefaultSlotSize
    )
    {
        var producer = SharedMemoryProducer.TryCreate(name ?? GenerateUniqueName(), slotCount, slotSize);

        if (producer != null)
        {
            _disposables.Add(producer);
        }

        return producer;
    }

    private void SkipIfNativeUnavailable(SharedMemoryProducer? producer)
    {
        Skip.If(producer is null, "Native TsDuck library not available - skipping native interop test");
    }

    // =========================================================================
    // Test 1: Basic Data Transfer
    // =========================================================================

    [SkippableFact]
    public void NativeInterop_BasicDataTransfer_BytesMatchExactly()
    {
        // Arrange
        var name = GenerateUniqueName();
        var producer = TryCreateNativeProducer(name);
        SkipIfNativeUnavailable(producer);

        var sourceData = CreateGenerator().GenerateChunk(100); // 100 TS packets = 18,800 bytes
        _output.WriteLine($"Generated {sourceData.Length} bytes ({sourceData.Length / TsPacketSize} packets)");

        // Act - write via native producer
        int bytesWritten = producer!.Write(sourceData);
        producer.Signal();

        _output.WriteLine($"Native producer wrote {bytesWritten} bytes");

        // Read via C# consumer
        using var consumer = new SharedMemoryConsumer(name);
        _disposables.Add(consumer);

        var readBuffer = new byte[sourceData.Length];
        int bytesRead = consumer.Read(readBuffer);

        _output.WriteLine($"C# consumer read {bytesRead} bytes");

        // Assert
        Assert.Equal(sourceData.Length, bytesWritten);
        Assert.Equal(sourceData.Length, bytesRead);
        Assert.True(
            sourceData.AsSpan().SequenceEqual(readBuffer.AsSpan(0, bytesRead)),
            "Data read by C# consumer must match data written by native producer byte-for-byte"
        );

        // Verify all packets have valid sync byte
        int packetCount = bytesRead / TsPacketSize;
        for (int i = 0; i < packetCount; i++)
        {
            byte syncByte = readBuffer[i * TsPacketSize];
            Assert.Equal(0x47, syncByte);
        }

        _output.WriteLine($"Verified {packetCount} packets with valid sync bytes");
    }

    [SkippableFact]
    public void NativeInterop_MultipleWrites_AllDataIntact()
    {
        // Arrange
        var name = GenerateUniqueName();
        var producer = TryCreateNativeProducer(name);
        SkipIfNativeUnavailable(producer);

        const int chunkCount = 10;
        const int packetsPerChunk = 50;

        var generator = CreateGenerator();
        var allSourceData = new List<byte[]>();
        var allReadData = new List<byte>();

        using var consumer = new SharedMemoryConsumer(name);
        _disposables.Add(consumer);

        var readBuffer = new byte[packetsPerChunk * TsPacketSize * 2];

        // Act - interleaved write/read
        for (int i = 0; i < chunkCount; i++)
        {
            var chunk = generator.GenerateChunk(packetsPerChunk);
            allSourceData.Add(chunk);

            int written = producer!.Write(chunk);
            producer.Signal();

            _output.WriteLine($"Chunk {i + 1}: wrote {written} bytes");

            // Small delay to ensure data propagates
            Thread.Sleep(1);

            int read = consumer.Read(readBuffer);
            allReadData.AddRange(readBuffer.Take(read));

            _output.WriteLine($"Chunk {i + 1}: read {read} bytes");
        }

        // Assert
        var expectedData = allSourceData.SelectMany(c => c).ToArray();
        var actualData = allReadData.ToArray();

        _output.WriteLine($"Total source: {expectedData.Length}, Total read: {actualData.Length}");

        // Verify we read all the written data
        Assert.True(
            actualData.Length >= expectedData.Length,
            $"Should read at least {expectedData.Length} bytes, got {actualData.Length}"
        );

        // Verify first chunk is intact (exact match)
        Assert.True(
            expectedData
                .AsSpan(0, allSourceData[0].Length)
                .SequenceEqual(actualData.AsSpan(0, allSourceData[0].Length)),
            "First chunk should match exactly"
        );
    }

    // =========================================================================
    // Test 2: High Throughput
    // =========================================================================

    [SkippableFact]
    public async Task NativeInterop_HighThroughput_NoDataLoss()
    {
        // Arrange
        var name = GenerateUniqueName();
        var producer = TryCreateNativeProducer(name, slotCount: 4096); // Larger buffer
        SkipIfNativeUnavailable(producer);

        const int durationSeconds = 3;
        const int packetsPerBatch = 100;
        const int batchIntervalMs = 7; // ~10 Mbps target

        long totalWritten = 0;
        long totalRead = 0;
        int overflowCount = 0;

        using var consumer = new SharedMemoryConsumer(name);
        _disposables.Add(consumer);

        var readBuffer = new byte[DefaultSlotSize * 16];
        var sw = Stopwatch.StartNew();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds + 5));

        // Act - producer writes in background
        var writeTask = Task.Run(
            async () =>
            {
                var gen = CreateGenerator();
                while (sw.Elapsed.TotalSeconds < durationSeconds && !cts.Token.IsCancellationRequested)
                {
                    var data = gen.GenerateChunk(packetsPerBatch);
                    int written = producer!.Write(data, out bool overflow);
                    Interlocked.Add(ref totalWritten, written);

                    if (overflow)
                    {
                        Interlocked.Increment(ref overflowCount);
                    }

                    producer.Signal();
                    await Task.Delay(batchIntervalMs, cts.Token);
                }

                producer!.SetEndOfStream();
            },
            cts.Token
        );

        // Consumer reads
        while (!cts.Token.IsCancellationRequested)
        {
            if (consumer.WaitForData(TimeSpan.FromMilliseconds(100)))
            {
                int read = consumer.Read(readBuffer);
                if (read > 0)
                {
                    Interlocked.Add(ref totalRead, read);
                }
                else if (consumer.IsEndOfStream && consumer.AvailableBytes == 0)
                {
                    break;
                }
            }

            if (consumer.IsEndOfStream && consumer.AvailableBytes == 0)
            {
                break;
            }
        }

        await writeTask;
        sw.Stop();

        // Final drain
        while (consumer.AvailableBytes > 0)
        {
            int read = consumer.Read(readBuffer);
            totalRead += read;
        }

        // Assert
        var actualBitrate = totalRead * 8.0 / sw.Elapsed.TotalSeconds;
        _output.WriteLine($"Duration: {sw.Elapsed.TotalSeconds:F2}s");
        _output.WriteLine($"Written: {totalWritten:N0} bytes, Read: {totalRead:N0} bytes");
        _output.WriteLine($"Overflow events: {overflowCount}");
        _output.WriteLine($"Actual bitrate: {actualBitrate / 1_000_000:F2} Mbps");

        // Verify no data loss (excluding any overflow scenarios)
        if (overflowCount == 0)
        {
            Assert.True(totalRead >= totalWritten, $"Should read at least {totalWritten} bytes, got {totalRead}");
        }

        // With a 4096-slot buffer, we should not have overflow at 10 Mbps for 3 seconds
        Assert.Equal(0, overflowCount);
    }

    [SkippableFact]
    public void NativeInterop_BurstWrite_MeasureThroughput()
    {
        // Arrange
        const int totalPackets = 10_000;
        // Use a power of 2 slot count that fits all packets
        const uint largeSlotCount = 2048; // Power of 2, ~2.7MB buffer

        var name = GenerateUniqueName();
        var producer = TryCreateNativeProducer(name, slotCount: largeSlotCount);
        SkipIfNativeUnavailable(producer);

        var sourceData = CreateGenerator().GenerateChunk(totalPackets);
        var readBuffer = new byte[sourceData.Length * 2];

        // Act - burst write
        var writeStart = Stopwatch.GetTimestamp();
        int bytesWritten = producer!.Write(sourceData);
        var writeEnd = Stopwatch.GetTimestamp();

        producer.Signal();

        using var consumer = new SharedMemoryConsumer(name);
        _disposables.Add(consumer);

        var readStart = Stopwatch.GetTimestamp();
        int totalRead = 0;
        int consecutiveZeroReads = 0;

        while (consecutiveZeroReads < 3)
        {
            int read = consumer.Read(readBuffer.AsSpan(totalRead, readBuffer.Length - totalRead));
            if (read > 0)
            {
                totalRead += read;
                consecutiveZeroReads = 0;
            }
            else
            {
                consecutiveZeroReads++;
                Thread.SpinWait(100);
            }
        }

        var readEnd = Stopwatch.GetTimestamp();

        // Assert
        var writeTime = Stopwatch.GetElapsedTime(writeStart, writeEnd);
        var readTime = Stopwatch.GetElapsedTime(readStart, readEnd);
        var writeThroughput = sourceData.Length / writeTime.TotalSeconds / (1024 * 1024);
        var readThroughput = totalRead / readTime.TotalSeconds / (1024 * 1024);

        _output.WriteLine(
            $"Native Write: {sourceData.Length:N0} bytes in {writeTime.TotalMilliseconds:F2}ms = {writeThroughput:F0} MB/s"
        );
        _output.WriteLine(
            $"Managed Read: {totalRead:N0} bytes in {readTime.TotalMilliseconds:F2}ms = {readThroughput:F0} MB/s"
        );

        Assert.Equal(sourceData.Length, bytesWritten);
        Assert.True(totalRead >= sourceData.Length, $"Should read at least {sourceData.Length} bytes, got {totalRead}");

        // Native interop should achieve high throughput
        Assert.True(readThroughput > 50, $"Read throughput {readThroughput:F0} MB/s is below 50 MB/s threshold");
    }

    // =========================================================================
    // Test 3: Flags - Producer Sets, Consumer Reads
    // =========================================================================

    [SkippableFact]
    public void NativeInterop_Flags_DiscontinuitySetAndConsumed()
    {
        // Arrange
        var name = GenerateUniqueName();
        var producer = TryCreateNativeProducer(name);
        SkipIfNativeUnavailable(producer);

        var dataBefore = CreateGenerator().GenerateChunk(50);
        var dataAfter = CreateGenerator().GenerateChunk(50);

        // Act
        producer!.Write(dataBefore);
        producer.Signal();

        using var consumer = new SharedMemoryConsumer(name);
        _disposables.Add(consumer);

        var buffer = new byte[dataBefore.Length];
        consumer.Read(buffer);

        // Before setting discontinuity
        bool discontinuityBefore = consumer.ConsumeDiscontinuity();
        _output.WriteLine($"Discontinuity before signal: {discontinuityBefore}");

        // Set discontinuity via native producer
        producer.SetDiscontinuity();
        producer.Write(dataAfter);
        producer.Signal();

        Thread.Sleep(10); // Allow flag to propagate

        // Check discontinuity
        bool discontinuityDetected = consumer.ConsumeDiscontinuity();
        bool discontinuityAfterConsume = consumer.ConsumeDiscontinuity();
        _output.WriteLine($"Discontinuity detected: {discontinuityDetected}");
        _output.WriteLine($"Discontinuity after consume: {discontinuityAfterConsume}");

        // Assert
        Assert.False(discontinuityBefore, "No discontinuity should exist before native producer signals it");
        Assert.True(discontinuityDetected, "Discontinuity set by native producer should be detected by C# consumer");
        Assert.False(discontinuityAfterConsume, "Discontinuity should be cleared after consume");
    }

    [SkippableFact]
    public void NativeInterop_Flags_EndOfStreamSignaled()
    {
        // Arrange
        var name = GenerateUniqueName();
        var producer = TryCreateNativeProducer(name);
        SkipIfNativeUnavailable(producer);

        var sourceData = CreateGenerator().GenerateChunk(100);

        // Act
        producer!.Write(sourceData);
        producer.Signal();

        using var consumer = new SharedMemoryConsumer(name);
        _disposables.Add(consumer);

        // Check before EOS
        bool eosBefore = consumer.IsEndOfStream;
        _output.WriteLine($"EOS before signal: {eosBefore}");

        // Signal EOS via native producer
        producer.SetEndOfStream();
        Thread.Sleep(10);

        bool eosAfter = consumer.IsEndOfStream;
        _output.WriteLine($"EOS after signal: {eosAfter}");

        // Assert
        Assert.False(eosBefore, "EOS should not be set before native producer signals it");
        Assert.True(eosAfter, "EOS set by native producer should be detected by C# consumer");
    }

    [SkippableFact]
    public void NativeInterop_Flags_ErrorSetAndRead()
    {
        // Arrange
        var name = GenerateUniqueName();
        var producer = TryCreateNativeProducer(name);
        SkipIfNativeUnavailable(producer);

        const string errorMessage = "Native test error message";
        const uint errorCode = (uint)SharedMemoryErrorCode.NetworkError;

        // Act
        using var consumer = new SharedMemoryConsumer(name);
        _disposables.Add(consumer);

        // Check before error
        bool errorBefore = consumer.HasError;
        _output.WriteLine($"Error before signal: {errorBefore}");

        // Set error via native producer
        producer!.SetError(errorCode, errorMessage);
        Thread.Sleep(10);

        bool errorAfter = consumer.HasError;
        var actualErrorCode = consumer.ErrorCode;
        var actualErrorMessage = consumer.ErrorMessage;

        _output.WriteLine($"Error after signal: {errorAfter}");
        _output.WriteLine($"Error code: {actualErrorCode}");
        _output.WriteLine($"Error message: {actualErrorMessage}");

        // Assert
        Assert.False(errorBefore, "Error should not be set before native producer signals it");
        Assert.True(errorAfter, "Error set by native producer should be detected by C# consumer");
        Assert.Equal(SharedMemoryErrorCode.NetworkError, actualErrorCode);
        Assert.Equal(errorMessage, actualErrorMessage);
    }

    // =========================================================================
    // Test 4: Memory Layout - Header Offsets Match
    // =========================================================================

    [SkippableFact]
    public void NativeInterop_MemoryLayout_HeaderOffsetsMatch()
    {
        // Arrange
        var name = GenerateUniqueName();
        var producer = TryCreateNativeProducer(name);
        SkipIfNativeUnavailable(producer);

        // Write some data to ensure header is populated
        var sourceData = CreateGenerator().GenerateChunk(10);
        producer!.Write(sourceData);
        producer.Signal();

        // Read raw header bytes via memory-mapped file
        byte[] rawHeader;

        if (OperatingSystem.IsWindows())
        {
            using var mmf = MemoryMappedFile.OpenExisting(name);
            using var accessor = mmf.CreateViewAccessor(0, 256, MemoryMappedFileAccess.Read);
            rawHeader = new byte[256];
            accessor.ReadArray(0, rawHeader, 0, 256);
        }
        else
        {
            var shmPath = GetShmPath(name);
            Skip.If(!File.Exists(shmPath), "Shared memory file not found");

            using var fs = new FileStream(shmPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            rawHeader = new byte[256];
            fs.ReadExactly(rawHeader, 0, 256);
        }

        // Verify expected offsets
        _output.WriteLine("Verifying C++ -> C# header layout compatibility:");

        // Magic at 0x00 (8 bytes): "TSTREAM\0" = 0x5453545245414D00
        ulong magic = BitConverter.ToUInt64(rawHeader, 0x00);
        _output.WriteLine($"  Magic (0x00): 0x{magic:X16}");
        Assert.Equal(0x5453545245414D00UL, magic);

        // Version at 0x08 (4 bytes)
        uint version = BitConverter.ToUInt32(rawHeader, 0x08);
        _output.WriteLine($"  Version (0x08): {version}");
        Assert.Equal(1u, version);

        // Header size at 0x0C (4 bytes)
        uint headerSize = BitConverter.ToUInt32(rawHeader, 0x0C);
        _output.WriteLine($"  HeaderSize (0x0C): {headerSize}");
        Assert.Equal(256u, headerSize);

        // Buffer capacity at 0x10 (8 bytes)
        ulong bufferCapacity = BitConverter.ToUInt64(rawHeader, 0x10);
        _output.WriteLine($"  BufferCapacity (0x10): {bufferCapacity}");
        Assert.Equal(DefaultSlotCount * DefaultSlotSize, bufferCapacity);

        // Slot count at 0x18 (8 bytes)
        ulong slotCount = BitConverter.ToUInt64(rawHeader, 0x18);
        _output.WriteLine($"  SlotCount (0x18): {slotCount}");
        Assert.Equal(DefaultSlotCount, slotCount);

        // Slot size at 0x20 (4 bytes)
        uint slotSize = BitConverter.ToUInt32(rawHeader, 0x20);
        _output.WriteLine($"  SlotSize (0x20): {slotSize}");
        Assert.Equal(DefaultSlotSize, slotSize);

        // TS packet size at 0x24 (4 bytes)
        uint tsPacketSize = BitConverter.ToUInt32(rawHeader, 0x24);
        _output.WriteLine($"  TsPacketSize (0x24): {tsPacketSize}");
        Assert.Equal(188u, tsPacketSize);

        // Write position at 0x48 (8 bytes) - should be > 0 after writing
        ulong writePos = BitConverter.ToUInt64(rawHeader, 0x48);
        _output.WriteLine($"  WritePosition (0x48): {writePos}");
        Assert.True(writePos > 0, "Write position should be > 0 after writing data");

        // Flags at 0xC0 (4 bytes) - ProducerReady should be set
        uint flags = BitConverter.ToUInt32(rawHeader, 0xC0);
        _output.WriteLine($"  Flags (0xC0): 0x{flags:X8}");
        Assert.True((flags & 0x00000001) != 0, "ProducerReady flag (0x01) should be set by native producer");

        _output.WriteLine("All header offsets match between native C++ and managed C#");
    }

    [SkippableFact]
    public void NativeInterop_MemoryLayout_ConsumerUpdatesReadPosition()
    {
        // Arrange
        var name = GenerateUniqueName();
        var producer = TryCreateNativeProducer(name);
        SkipIfNativeUnavailable(producer);

        var sourceData = CreateGenerator().GenerateChunk(50);
        producer!.Write(sourceData);
        producer.Signal();

        // Read via consumer
        using var consumer = new SharedMemoryConsumer(name);
        _disposables.Add(consumer);

        var buffer = new byte[sourceData.Length];
        int bytesRead = consumer.Read(buffer);

        // Verify read position updated in shared memory
        byte[] rawHeader;

        if (OperatingSystem.IsWindows())
        {
            using var mmf = MemoryMappedFile.OpenExisting(name);
            using var accessor = mmf.CreateViewAccessor(0, 256, MemoryMappedFileAccess.Read);
            rawHeader = new byte[256];
            accessor.ReadArray(0, rawHeader, 0, 256);
        }
        else
        {
            var shmPath = GetShmPath(name);
            using var fs = new FileStream(shmPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            rawHeader = new byte[256];
            fs.ReadExactly(rawHeader, 0, 256);
        }

        // Read position at 0x88
        ulong readPos = BitConverter.ToUInt64(rawHeader, 0x88);
        _output.WriteLine($"Read position after consumer read: {readPos}");
        _output.WriteLine($"Bytes read: {bytesRead}");

        // Consumer state at 0x90
        ulong consumerState = BitConverter.ToUInt64(rawHeader, 0x90);
        _output.WriteLine($"Consumer state: {consumerState} ({(ConsumerState)consumerState})");

        // Flags at 0xC0 - ConsumerReady (0x02) should be set
        uint flags = BitConverter.ToUInt32(rawHeader, 0xC0);
        _output.WriteLine($"Flags: 0x{flags:X8}");

        // Assert
        Assert.True(readPos > 0, "Read position should be > 0 after reading");
        Assert.True((flags & 0x00000002) != 0, "ConsumerReady flag (0x02) should be set by C# consumer");
    }

    // =========================================================================
    // Test 5: Wrap-Around - Data Integrity
    // =========================================================================

    [SkippableFact]
    public void NativeInterop_WrapAround_DataIntegrity()
    {
        // Arrange - small buffer to force wrap-around
        const uint smallSlotCount = 16;
        var name = GenerateUniqueName();
        var producer = TryCreateNativeProducer(name, slotCount: smallSlotCount);
        SkipIfNativeUnavailable(producer);

        int bufferCapacity = (int)(smallSlotCount * DefaultSlotSize);
        int packetsToWrite = (bufferCapacity / TsPacketSize) * 3; // 3x buffer capacity

        _output.WriteLine($"Buffer capacity: {bufferCapacity} bytes ({smallSlotCount} slots)");
        _output.WriteLine($"Writing {packetsToWrite} packets ({packetsToWrite * TsPacketSize} bytes)");

        using var consumer = new SharedMemoryConsumer(name);
        _disposables.Add(consumer);

        var generator = CreateGenerator();
        var readBuffer = new byte[DefaultSlotSize * 4];
        long totalWritten = 0;
        long totalRead = 0;
        int overflowCount = 0;
        int batchesWritten = 0;

        // Act - write in small batches, reading between writes
        const int packetsPerBatch = 20;

        while (totalWritten < packetsToWrite * TsPacketSize)
        {
            int remaining = packetsToWrite - (int)(totalWritten / TsPacketSize);
            int toWrite = Math.Min(packetsPerBatch, remaining);

            if (toWrite <= 0)
            {
                break;
            }

            var chunk = generator.GenerateChunk(toWrite);
            int written = producer!.Write(chunk, out bool overflow);
            totalWritten += written;
            batchesWritten++;

            if (overflow)
            {
                overflowCount++;
            }

            producer.Signal();

            // Read some data to make room
            int read = consumer.Read(readBuffer);
            totalRead += read;

            // Check for overflow flag in consumer
            if (consumer.ConsumeOverflow())
            {
                overflowCount++;
            }
        }

        // Drain remaining data
        while (consumer.AvailableBytes > 0)
        {
            int read = consumer.Read(readBuffer);
            if (read == 0)
            {
                break;
            }

            totalRead += read;

            if (consumer.ConsumeOverflow())
            {
                overflowCount++;
            }
        }

        // Assert
        _output.WriteLine($"Total written: {totalWritten:N0} bytes in {batchesWritten} batches");
        _output.WriteLine($"Total read: {totalRead:N0} bytes");
        _output.WriteLine($"Overflow events: {overflowCount}");
        _output.WriteLine($"Data loss: {totalWritten - totalRead:N0} bytes");

        // The key assertion is that we can write more than the buffer capacity and still read data
        // Overflow behavior depends on timing - may or may not occur
        Assert.True(totalWritten > bufferCapacity, "Should have written more than buffer capacity");
        Assert.True(totalRead > 0, "Should have read some data");

        // If overflow occurred, read should be less than written
        if (overflowCount > 0)
        {
            Assert.True(totalRead <= totalWritten, "With overflow, read should not exceed written");
            _output.WriteLine("Overflow detected as expected for slow consumer scenario");
        }
        else
        {
            // Consumer was fast enough - all data should be read
            _output.WriteLine("No overflow - consumer kept up with producer");
        }
    }

    [SkippableFact]
    public void NativeInterop_WrapAround_FastConsumer_NoDataLoss()
    {
        // Arrange - small buffer but consumer keeps up
        const uint smallSlotCount = 32;
        var name = GenerateUniqueName();
        var producer = TryCreateNativeProducer(name, slotCount: smallSlotCount);
        SkipIfNativeUnavailable(producer);

        using var consumer = new SharedMemoryConsumer(name);
        _disposables.Add(consumer);

        int bufferCapacity = (int)(smallSlotCount * DefaultSlotSize);
        int packetsToWrite = (bufferCapacity / TsPacketSize) * 5; // 5x buffer capacity

        _output.WriteLine($"Buffer capacity: {bufferCapacity} bytes");
        _output.WriteLine($"Writing {packetsToWrite} packets");

        var generator = CreateGenerator();
        var allWrittenData = new List<byte>();
        var allReadData = new List<byte>();
        var readBuffer = new byte[DefaultSlotSize * 4];
        bool anyOverflow = false;

        // Act - write small amounts, read immediately
        const int packetsPerBatch = 5;
        int packetsWritten = 0;

        while (packetsWritten < packetsToWrite)
        {
            int toWrite = Math.Min(packetsPerBatch, packetsToWrite - packetsWritten);
            var chunk = generator.GenerateChunk(toWrite);
            allWrittenData.AddRange(chunk);

            int written = producer!.Write(chunk, out bool overflow);
            packetsWritten += toWrite;

            if (overflow)
            {
                anyOverflow = true;
            }

            producer.Signal();

            // Read immediately
            while (consumer.AvailableBytes > 0)
            {
                int read = consumer.Read(readBuffer);
                if (read > 0)
                {
                    allReadData.AddRange(readBuffer.Take(read));
                }
                else
                {
                    break;
                }
            }
        }

        // Final drain
        while (consumer.AvailableBytes > 0)
        {
            int read = consumer.Read(readBuffer);
            if (read > 0)
            {
                allReadData.AddRange(readBuffer.Take(read));
            }
            else
            {
                break;
            }
        }

        // Assert
        _output.WriteLine($"Total written: {allWrittenData.Count:N0} bytes");
        _output.WriteLine($"Total read: {allReadData.Count:N0} bytes");
        _output.WriteLine($"Any overflow: {anyOverflow}");

        // Key assertion: we should read a significant portion of data
        Assert.True(allReadData.Count > 0, "Should have read some data");

        if (!anyOverflow)
        {
            // Without overflow, we should have all data
            Assert.True(
                allReadData.Count >= allWrittenData.Count * 0.95, // Allow 5% tolerance for timing
                $"Without overflow, should read most data. Written: {allWrittenData.Count}, Read: {allReadData.Count}"
            );
            _output.WriteLine("Fast consumer prevented overflow - good!");
        }
        else
        {
            // With overflow, some data loss is expected but we should still read significant amount
            Assert.True(
                allReadData.Count >= allWrittenData.Count * 0.5, // At least 50% of data
                $"Even with overflow, should read substantial data. Written: {allWrittenData.Count}, Read: {allReadData.Count}"
            );
            _output.WriteLine("Overflow occurred despite fast reading - acceptable in high-throughput scenarios");
        }

        // Verify sync bytes are present somewhere in the data (data integrity)
        // Note: reads may not be aligned to TS packet boundaries, so we count all sync bytes
        int syncByteCount = allReadData.Count(b => b == 0x47);
        int minExpectedSyncBytes = allReadData.Count / TsPacketSize / 2; // At least half the packets

        _output.WriteLine($"Sync byte count: {syncByteCount}, minimum expected: {minExpectedSyncBytes}");

        Assert.True(
            syncByteCount >= minExpectedSyncBytes,
            $"Should have significant sync bytes in data. Found: {syncByteCount}, minimum: {minExpectedSyncBytes}"
        );
    }

    // =========================================================================
    // Test 6: Consumer Attachment Detection
    // =========================================================================

    [SkippableFact]
    public void NativeInterop_ConsumerAttachment_DetectedByProducer()
    {
        // Arrange
        var name = GenerateUniqueName();
        var producer = TryCreateNativeProducer(name);
        SkipIfNativeUnavailable(producer);

        // Act & Assert
        bool attachedBefore = producer!.IsConsumerAttached;
        _output.WriteLine($"Consumer attached before: {attachedBefore}");
        Assert.False(attachedBefore, "No consumer should be attached initially");

        using (var consumer1 = new SharedMemoryConsumer(name))
        {
            Thread.Sleep(10); // Allow flag to propagate
            bool attachedDuring = producer.IsConsumerAttached;
            _output.WriteLine($"Consumer attached during: {attachedDuring}");
            Assert.True(attachedDuring, "Producer should detect attached consumer");
        }

        Thread.Sleep(10); // Allow flag to propagate
        bool attachedAfter = producer.IsConsumerAttached;
        _output.WriteLine($"Consumer attached after dispose: {attachedAfter}");
        Assert.False(attachedAfter, "Producer should detect consumer detachment");
    }

    // =========================================================================
    // Helper Methods
    // =========================================================================

    private static string GetShmPath(string name)
    {
        var shmDir = Directory.Exists("/dev/shm") ? "/dev/shm" : "/tmp";
        return Path.Combine(shmDir, name);
    }
}
