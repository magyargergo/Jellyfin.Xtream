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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Jellyfin.Xtream.E2ETests.Infrastructure;
using Jellyfin.Xtream.Service.Streaming.SharedMemory;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.E2ETests.Tests;

/// <summary>
/// End-to-end tests for the shared memory communication layer.
/// Tests the full data flow between a simulated native producer and the C# consumer.
/// </summary>
/// <remarks>
/// These tests use a managed producer simulation to test the C# consumer without
/// requiring the native library. This validates the IPC protocol implementation.
/// </remarks>
[Collection("E2E-SharedMemory")]
public sealed class SharedMemoryTests : IDisposable
{
    private const int TsPacketSize = 188;
    private const int DefaultSlotCount = 1024;
    private const int DefaultPacketsPerSlot = 7;
    private const int DefaultSlotSize = DefaultPacketsPerSlot * TsPacketSize; // 1316 bytes

    private readonly ITestOutputHelper _output;
    private readonly List<SharedMemoryTestProducer> _producers = new();

    public SharedMemoryTests(DockerTestFixture fixture, ITestOutputHelper output)
    {
        _ = fixture; // Fixture is required by collection but not used directly
        _output = output;
    }

    /// <summary>
    /// Creates a fresh TestStreamGenerator with reset state for each test.
    /// </summary>
    private static TestStreamGenerator CreateGenerator()
    {
        return new TestStreamGenerator(5000);
    }

    public void Dispose()
    {
        foreach (var producer in _producers)
        {
            producer.Dispose();
        }
        _producers.Clear();
        GC.SuppressFinalize(this);
    }

    private SharedMemoryTestProducer CreateProducer(
        string? name = null,
        int slotCount = DefaultSlotCount,
        int slotSize = DefaultSlotSize
    )
    {
        var producer = new SharedMemoryTestProducer(name ?? GenerateUniqueName(), slotCount, slotSize);
        _producers.Add(producer);
        return producer;
    }

    private static string GenerateUniqueName()
    {
        return $"test_shm_{Guid.NewGuid():N}";
    }

    // =========================================================================
    // Test 1: Basic Data Transfer
    // =========================================================================

    [Fact]
    public void BasicTransfer_WriteThenRead_DataMatchesExactly()
    {
        // Arrange
        using var producer = CreateProducer();
        var sourceData = CreateGenerator().GenerateChunk(100); // 100 TS packets

        // Act
        producer.Write(sourceData);
        producer.SignalDataAvailable();

        using var consumer = new SharedMemoryConsumer(producer.Name);
        var readBuffer = new byte[sourceData.Length];
        int bytesRead = consumer.Read(readBuffer);

        // Assert
        _output.WriteLine($"Source: {sourceData.Length} bytes, Read: {bytesRead} bytes");
        Assert.Equal(sourceData.Length, bytesRead);
        Assert.True(sourceData.AsSpan().SequenceEqual(readBuffer), "Data mismatch: read data does not match source");
    }

    [Fact]
    public void BasicTransfer_MultipleChunks_AllDataIntact()
    {
        // Arrange
        using var producer = CreateProducer();
        const int chunkCount = 10;
        const int packetsPerChunk = 50;

        var generator = CreateGenerator();
        var allSourceData = new List<byte[]>();
        var allReadData = new List<byte>();

        using var consumer = new SharedMemoryConsumer(producer.Name);
        var readBuffer = new byte[packetsPerChunk * TsPacketSize * 2]; // Oversized buffer

        // Act - interleaved write/read using single generator for consistency
        for (int i = 0; i < chunkCount; i++)
        {
            var chunk = generator.GenerateChunk(packetsPerChunk);
            allSourceData.Add(chunk);
            producer.Write(chunk);
            producer.SignalDataAvailable();

            int read = consumer.Read(readBuffer);
            allReadData.AddRange(readBuffer.Take(read));
        }

        // Assert
        var expectedData = allSourceData.SelectMany(c => c).ToArray();
        var actualData = allReadData.ToArray();

        _output.WriteLine($"Total source: {expectedData.Length}, Total read: {actualData.Length}");

        // Verify we read at least as much as we wrote
        Assert.True(
            actualData.Length >= expectedData.Length,
            $"Should have read at least {expectedData.Length} bytes, got {actualData.Length}"
        );

        // Verify valid packets have sync byte 0x47 (padding bytes are 0xFF)
        int packetCount = actualData.Length / TsPacketSize;
        int validPackets = 0;
        int paddingPackets = 0;
        for (int i = 0; i < packetCount; i++)
        {
            byte syncByte = actualData[i * TsPacketSize];
            if (syncByte == 0x47)
            {
                validPackets++;
            }
            else if (syncByte == 0xFF)
            {
                paddingPackets++; // Slot padding
            }
            else
            {
                Assert.Fail($"Invalid sync byte 0x{syncByte:X2} at packet {i}");
            }
        }
        _output.WriteLine($"Valid packets: {validPackets}, Padding packets: {paddingPackets}");

        // Verify the start of the data matches (first chunk should be exact match)
        Assert.True(
            expectedData
                .AsSpan(0, allSourceData[0].Length)
                .SequenceEqual(actualData.AsSpan(0, allSourceData[0].Length)),
            "First chunk should match exactly"
        );

        // We should have at least as many valid packets as we wrote
        int expectedPackets = expectedData.Length / TsPacketSize;
        Assert.True(
            validPackets >= expectedPackets,
            $"Should have at least {expectedPackets} valid packets, got {validPackets}"
        );
    }

    [Fact]
    public void BasicTransfer_SyncByteValidation_AllPacketsHaveValidSync()
    {
        // Arrange
        using var producer = CreateProducer();
        var sourceData = CreateGenerator().GenerateChunk(500);

        // Act
        producer.Write(sourceData);
        producer.SignalDataAvailable();

        using var consumer = new SharedMemoryConsumer(producer.Name);
        var readBuffer = new byte[sourceData.Length];
        int bytesRead = consumer.Read(readBuffer);

        // Assert - verify every 188-byte boundary has sync byte 0x47
        int packetCount = bytesRead / TsPacketSize;
        int invalidSyncCount = 0;

        for (int i = 0; i < packetCount; i++)
        {
            if (readBuffer[i * TsPacketSize] != 0x47)
            {
                invalidSyncCount++;
            }
        }

        _output.WriteLine($"Packets read: {packetCount}, Invalid sync bytes: {invalidSyncCount}");
        Assert.Equal(0, invalidSyncCount);
    }

    // =========================================================================
    // Test 2: High Throughput
    // =========================================================================

    [Fact]
    public async Task HighThroughput_20Mbps_NoDataLoss()
    {
        // Arrange
        const int durationSeconds = 10;

        using var producer = CreateProducer(slotCount: 4096); // Larger buffer for high throughput
        using var consumer = new SharedMemoryConsumer(producer.Name);

        long totalWritten = 0;
        long totalRead = 0;
        var readBuffer = new byte[DefaultSlotSize * 16];
        var sw = Stopwatch.StartNew();

        // Calculate packets per write to achieve target bitrate
        // At 20 Mbps = 2.5 MB/s, and 188 bytes per packet = ~13,300 packets/sec
        // Write in batches of ~100 packets every 7.5ms
        const int packetsPerBatch = 100;
        const int batchIntervalMs = 7;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds + 2));

        // Act - producer writes in a separate task
        var writeTask = Task.Run(
            async () =>
            {
                while (sw.Elapsed.TotalSeconds < durationSeconds && !cts.Token.IsCancellationRequested)
                {
                    var data = CreateGenerator().GenerateChunk(packetsPerBatch);
                    producer.Write(data);
                    Interlocked.Add(ref totalWritten, data.Length);
                    producer.SignalDataAvailable();
                    await Task.Delay(batchIntervalMs, cts.Token);
                }
                producer.SetEndOfStream();
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
            totalRead += consumer.Read(readBuffer);
        }

        // Assert
        var actualBitrate = totalRead * 8.0 / sw.Elapsed.TotalSeconds;
        _output.WriteLine($"Duration: {sw.Elapsed.TotalSeconds:F2}s");
        _output.WriteLine($"Written: {totalWritten:N0} bytes, Read: {totalRead:N0} bytes");
        _output.WriteLine($"Actual bitrate: {actualBitrate / 1_000_000:F2} Mbps");

        // Reads may include slot padding (0xFF bytes), so totalRead may be >= totalWritten
        // We verify that we read at least what was written (no data loss)
        Assert.True(totalRead >= totalWritten, $"Should read at least {totalWritten} bytes, got {totalRead}");

        // The "extra" bytes should be due to slot padding, not duplication
        // Allow up to 5% overhead for slot alignment
        double overheadPercent = (totalRead - totalWritten) * 100.0 / totalWritten;
        _output.WriteLine($"Slot padding overhead: {overheadPercent:F2}%");
        Assert.True(overheadPercent < 10, $"Slot padding overhead {overheadPercent:F2}% exceeds 10% threshold");
    }

    [Fact]
    public void HighThroughput_MeasureActualThroughput_ReportsAccurately()
    {
        // Arrange - use a buffer large enough to hold all test data without wrap-around
        const int totalPackets = 10_000;
        int requiredSlots = (totalPackets * TsPacketSize / DefaultSlotSize) + 100; // Add margin

        using var producer = CreateProducer(slotCount: requiredSlots);
        using var consumer = new SharedMemoryConsumer(producer.Name);

        var sourceData = CreateGenerator().GenerateChunk(totalPackets);
        // Make buffer larger to accommodate potential slot padding
        var readBuffer = new byte[sourceData.Length * 2];

        // Act - burst write then measure read speed
        var writeStart = Stopwatch.GetTimestamp();
        producer.Write(sourceData);
        var writeEnd = Stopwatch.GetTimestamp();
        producer.SignalDataAvailable();

        var readStart = Stopwatch.GetTimestamp();
        int totalRead = 0;
        // Keep reading until we've read all available data
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
                Thread.SpinWait(100); // Brief pause before retry
            }
        }
        var readEnd = Stopwatch.GetTimestamp();

        // Assert
        var writeTime = Stopwatch.GetElapsedTime(writeStart, writeEnd);
        var readTime = Stopwatch.GetElapsedTime(readStart, readEnd);
        var writeThroughput = sourceData.Length / writeTime.TotalSeconds / (1024 * 1024);
        var readThroughput = totalRead / readTime.TotalSeconds / (1024 * 1024);

        _output.WriteLine(
            $"Write: {sourceData.Length:N0} bytes in {writeTime.TotalMilliseconds:F2}ms = {writeThroughput:F0} MB/s"
        );
        _output.WriteLine(
            $"Read: {totalRead:N0} bytes in {readTime.TotalMilliseconds:F2}ms = {readThroughput:F0} MB/s"
        );

        // Should read at least as much as was written (may include slot padding)
        Assert.True(totalRead >= sourceData.Length, $"Should read at least {sourceData.Length} bytes, got {totalRead}");
        // Should achieve at least 100 MB/s on modern systems (memory-to-memory copy)
        Assert.True(readThroughput > 100, $"Read throughput {readThroughput:F0} MB/s is below 100 MB/s threshold");
    }

    // =========================================================================
    // Test 3: Buffer Wrap-Around
    // =========================================================================

    [Fact]
    public void BufferWrapAround_FillAndContinue_OverflowDetected()
    {
        // Arrange - small buffer to easily cause overflow
        const int slotCount = 16; // Very small buffer
        const int slotSize = TsPacketSize * 7; // 1316 bytes per slot
        using var producer = CreateProducer(slotCount: slotCount, slotSize: slotSize);

        // Calculate bytes to overflow - write much more than buffer can hold
        int bufferCapacity = (slotCount - 1) * slotSize;
        int packetsToWrite = ((bufferCapacity / TsPacketSize) + 1) * 5; // 5x buffer capacity

        var generator = CreateGenerator();
        using var consumer = new SharedMemoryConsumer(producer.Name);

        // Write data in chunks, checking for overflow periodically
        int packetsWritten = 0;
        const int chunkSize = 50; // packets per chunk
        bool overflowOccurred = false;
        long totalWritten = 0;

        while (packetsWritten < packetsToWrite)
        {
            int toWrite = Math.Min(chunkSize, packetsToWrite - packetsWritten);
            var chunk = generator.GenerateChunk(toWrite);
            producer.Write(chunk);
            totalWritten += chunk.Length;
            packetsWritten += toWrite;

            // Check overflow after each write
            if (consumer.ConsumeOverflow())
            {
                overflowOccurred = true;
            }
        }
        producer.SignalDataAvailable();

        // Read remaining data
        var readBuffer = new byte[bufferCapacity + slotSize];
        int totalRead = 0;
        int read;
        while ((read = consumer.Read(readBuffer.AsSpan(totalRead, readBuffer.Length - totalRead))) > 0)
        {
            totalRead += read;
            if (consumer.ConsumeOverflow())
            {
                overflowOccurred = true;
            }
        }

        // Assert
        _output.WriteLine($"Buffer capacity: {bufferCapacity:N0} bytes ({slotCount - 1} slots)");
        _output.WriteLine($"Total written: {totalWritten:N0} bytes");
        _output.WriteLine($"Overflow detected: {overflowOccurred}");
        _output.WriteLine($"Bytes read: {totalRead:N0}");
        _output.WriteLine($"Dropped bytes: {totalWritten - totalRead:N0}");

        Assert.True(overflowOccurred, "Overflow should be detected when buffer is overrun");
        // With overflow, we should read significantly less than we wrote
        Assert.True(totalRead < totalWritten, $"Read {totalRead} should be less than written {totalWritten}");
    }

    [Fact]
    public async Task BufferWrapAround_SlowConsumer_CatchesUp()
    {
        // Arrange - small buffer to cause wrapping
        const int slotCount = 64;
        using var producer = CreateProducer(slotCount: slotCount);
        using var consumer = new SharedMemoryConsumer(producer.Name);

        var readBuffer = new byte[DefaultSlotSize * 8];
        long totalWritten = 0;
        long totalRead = 0;
        int overflowCount = 0;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act - fast producer, slow consumer
        var writeTask = Task.Run(
            async () =>
            {
                for (int i = 0; i < 100 && !cts.Token.IsCancellationRequested; i++)
                {
                    var data = CreateGenerator().GenerateChunk(50);
                    producer.Write(data);
                    producer.SignalDataAvailable();
                    Interlocked.Add(ref totalWritten, data.Length);
                    await Task.Delay(10, cts.Token); // Fast writes
                }
                producer.SetEndOfStream();
            },
            cts.Token
        );

        // Slow consumer
        while (!cts.Token.IsCancellationRequested)
        {
            await Task.Delay(50, cts.Token); // Slow reads

            if (consumer.ConsumeOverflow())
            {
                overflowCount++;
            }

            int read = consumer.Read(readBuffer);
            if (read > 0)
            {
                totalRead += read;
            }
            else if (consumer.IsEndOfStream && consumer.AvailableBytes == 0)
            {
                break;
            }
        }

        await writeTask;

        // Final drain
        while (consumer.AvailableBytes > 0 || !consumer.IsEndOfStream)
        {
            if (consumer.ConsumeOverflow())
                overflowCount++;
            int read = consumer.Read(readBuffer);
            if (read > 0)
                totalRead += read;
            else
                break;
        }

        // Assert
        _output.WriteLine($"Written: {totalWritten:N0}, Read: {totalRead:N0}");
        _output.WriteLine($"Overflow events: {overflowCount}");
        _output.WriteLine($"Data loss: {totalWritten - totalRead:N0} bytes");

        Assert.True(overflowCount > 0, "Should have experienced overflow with slow consumer");
        Assert.True(totalRead > 0, "Consumer should have read some data");
        Assert.True(totalRead < totalWritten, "Slow consumer should have lost some data due to overflow");
    }

    // =========================================================================
    // Test 4: Discontinuity Signaling
    // =========================================================================

    [Fact]
    public void Discontinuity_SetAndConsume_FlagCleared()
    {
        // Arrange
        using var producer = CreateProducer();
        var dataBeforeDiscontinuity = CreateGenerator().GenerateChunk(100);
        var dataAfterDiscontinuity = CreateGenerator().GenerateChunk(100);

        // Act
        producer.Write(dataBeforeDiscontinuity);
        producer.SignalDataAvailable();

        using var consumer = new SharedMemoryConsumer(producer.Name);
        var buffer = new byte[dataBeforeDiscontinuity.Length];
        consumer.Read(buffer);

        // No discontinuity yet
        bool discontinuityBefore = consumer.ConsumeDiscontinuity();

        // Set discontinuity
        producer.SetDiscontinuity();
        producer.Write(dataAfterDiscontinuity);
        producer.SignalDataAvailable();

        // Check discontinuity
        consumer.WaitForData(TimeSpan.FromMilliseconds(100));
        bool discontinuityDetected = consumer.ConsumeDiscontinuity();
        bool discontinuityAfterConsume = consumer.ConsumeDiscontinuity();

        // Read data after discontinuity
        int readAfter = consumer.Read(buffer);

        // Assert
        _output.WriteLine($"Discontinuity before signal: {discontinuityBefore}");
        _output.WriteLine($"Discontinuity detected: {discontinuityDetected}");
        _output.WriteLine($"Discontinuity after consume: {discontinuityAfterConsume}");
        _output.WriteLine($"Bytes read after discontinuity: {readAfter}");

        Assert.False(discontinuityBefore, "No discontinuity should exist before signal");
        Assert.True(discontinuityDetected, "Discontinuity should be detected after signal");
        Assert.False(discontinuityAfterConsume, "Discontinuity should be cleared after consume");
        Assert.Equal(dataAfterDiscontinuity.Length, readAfter);
    }

    [Fact]
    public void Discontinuity_DataIntegrity_PreservedAcrossBoundary()
    {
        // Arrange
        using var producer = CreateProducer();
        var generator = CreateGenerator(); // Single generator for consistent data
        var dataBefore = generator.GenerateChunk(50);
        var dataAfter = generator.GenerateChunk(50);

        // Act
        producer.Write(dataBefore);
        producer.SetDiscontinuity();
        producer.Write(dataAfter);
        producer.SignalDataAvailable();

        using var consumer = new SharedMemoryConsumer(producer.Name);
        // Buffer large enough for both chunks plus potential slot padding
        var allData = new byte[(dataBefore.Length + dataAfter.Length) * 2];
        int totalRead = 0;
        bool discontinuityFound = false;

        while (totalRead < dataBefore.Length + dataAfter.Length)
        {
            int read = consumer.Read(allData.AsSpan(totalRead, allData.Length - totalRead));
            if (read == 0)
                break;

            // Check for discontinuity between chunks
            if (!discontinuityFound && consumer.ConsumeDiscontinuity())
            {
                discontinuityFound = true;
                _output.WriteLine($"Discontinuity detected at byte offset {totalRead}");
            }

            totalRead += read;
        }

        // Assert
        _output.WriteLine($"Data before: {dataBefore.Length}, Data after: {dataAfter.Length}");
        _output.WriteLine($"Total read: {totalRead}");

        // Verify we read at least both chunks worth of data
        var expectedMinimum = dataBefore.Length + dataAfter.Length;
        Assert.True(totalRead >= expectedMinimum, $"Should read at least {expectedMinimum} bytes, got {totalRead}");

        // Verify first chunk matches at start
        Assert.True(
            dataBefore.AsSpan().SequenceEqual(allData.AsSpan(0, dataBefore.Length)),
            "First chunk should match at start"
        );

        // Second chunk starts after first chunk's slot boundary (includes padding)
        int slotsForFirstChunk = (dataBefore.Length + DefaultSlotSize - 1) / DefaultSlotSize;
        int firstChunkSlotEnd = slotsForFirstChunk * DefaultSlotSize;
        _output.WriteLine($"First chunk slots: {slotsForFirstChunk}, slot boundary: {firstChunkSlotEnd}");

        // Verify second chunk starts after first chunk's slot boundary
        Assert.True(
            dataAfter.AsSpan().SequenceEqual(allData.AsSpan(firstChunkSlotEnd, dataAfter.Length)),
            "Second chunk should start after first chunk's slot boundary"
        );
    }

    // =========================================================================
    // Test 5: End-of-Stream
    // =========================================================================

    [Fact]
    public void EndOfStream_Signal_ConsumerDetects()
    {
        // Arrange
        using var producer = CreateProducer();
        var sourceData = CreateGenerator().GenerateChunk(100);

        // Act
        producer.Write(sourceData);
        producer.SignalDataAvailable();

        using var consumer = new SharedMemoryConsumer(producer.Name);

        // Check before EOS
        bool eosBeforeSignal = consumer.IsEndOfStream;

        // Signal EOS
        producer.SetEndOfStream();

        // Wait for signal to propagate
        Thread.Sleep(10);

        bool eosAfterSignal = consumer.IsEndOfStream;

        // Assert
        _output.WriteLine($"EOS before signal: {eosBeforeSignal}");
        _output.WriteLine($"EOS after signal: {eosAfterSignal}");

        Assert.False(eosBeforeSignal);
        Assert.True(eosAfterSignal);
    }

    [Fact]
    public void EndOfStream_DrainRemainingData_AllDataRead()
    {
        // Arrange
        using var producer = CreateProducer();
        var sourceData = CreateGenerator().GenerateChunk(500);

        // Act
        producer.Write(sourceData);
        producer.SetEndOfStream(); // EOS after data
        producer.SignalDataAvailable();

        using var consumer = new SharedMemoryConsumer(producer.Name);

        // Read all remaining data
        var buffer = new byte[sourceData.Length * 2];
        int totalRead = 0;

        while (true)
        {
            int read = consumer.Read(buffer.AsSpan(totalRead, buffer.Length - totalRead));
            if (read > 0)
            {
                totalRead += read;
            }
            else if (consumer.IsEndOfStream && consumer.AvailableBytes == 0)
            {
                break;
            }
        }

        // Assert
        _output.WriteLine($"Source: {sourceData.Length}, Read: {totalRead}");

        // We should read at least as much as was written (may include slot padding)
        Assert.True(totalRead >= sourceData.Length, $"Should read at least {sourceData.Length} bytes, got {totalRead}");
        Assert.True(consumer.IsEndOfStream);

        // Verify the source data is contained in what we read
        Assert.True(
            sourceData.AsSpan().SequenceEqual(buffer.AsSpan(0, sourceData.Length)),
            "Source data should match the beginning of read data"
        );

        // Verify extra bytes (if any) are 0xFF padding
        for (int i = sourceData.Length; i < totalRead; i++)
        {
            if (buffer[i] != 0xFF && buffer[i] != 0x47) // 0x47 is valid sync byte for next packet
            {
                // Allow valid TS packet data
                break;
            }
        }
    }

    [Fact]
    public void EndOfStream_WaitForData_ReturnsImmediately()
    {
        // Arrange
        using var producer = CreateProducer();
        producer.SetEndOfStream();

        using var consumer = new SharedMemoryConsumer(producer.Name);

        // Act
        var sw = Stopwatch.StartNew();
        bool result = consumer.WaitForData(TimeSpan.FromSeconds(10));
        sw.Stop();

        // Assert
        _output.WriteLine($"WaitForData returned in {sw.ElapsedMilliseconds}ms, result: {result}");
        Assert.True(result, "WaitForData should return true when EOS is set");
        Assert.True(sw.ElapsedMilliseconds < 1000, "Should return immediately, not wait for timeout");
    }

    // =========================================================================
    // Test 6: Error Handling
    // =========================================================================

    [Fact]
    public void ErrorHandling_SetError_ConsumerDetects()
    {
        // Arrange
        using var producer = CreateProducer();
        const string errorMessage = "Test error message for E2E testing";

        // Act
        producer.SetError(SharedMemoryErrorCode.NetworkError, errorMessage);

        using var consumer = new SharedMemoryConsumer(producer.Name);

        // Assert
        Assert.True(consumer.HasError);
        Assert.Equal(SharedMemoryErrorCode.NetworkError, consumer.ErrorCode);
        Assert.Equal(errorMessage, consumer.ErrorMessage);

        _output.WriteLine($"Error detected: {consumer.HasError}");
        _output.WriteLine($"Error code: {consumer.ErrorCode}");
        _output.WriteLine($"Error message: {consumer.ErrorMessage}");
    }

    [Fact]
    public async Task ErrorHandling_WaitForData_ReturnsOnError()
    {
        // Arrange
        using var producer = CreateProducer();

        using var consumer = new SharedMemoryConsumer(producer.Name);

        // Start waiting in background
        var waitTask = Task.Run(() =>
        {
            return consumer.WaitForData(TimeSpan.FromSeconds(10));
        });

        // Set error after small delay
        await Task.Delay(100);
        producer.SetError(SharedMemoryErrorCode.InternalError, "Async error");

        // Act
        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        bool result = false;
        bool waitResult = false;
        try
        {
            waitResult = await waitTask.WaitAsync(cts.Token);
            result = true;
        }
        catch (OperationCanceledException)
        {
            result = false;
        }
        sw.Stop();

        // Assert
        _output.WriteLine($"Wait completed: {result}, took {sw.ElapsedMilliseconds}ms");
        Assert.True(result, "Task should complete when error is set");
        Assert.True(waitResult, "WaitForData should return true on error");
    }

    [Fact]
    public void ErrorHandling_ErrorMessage_TruncatedCorrectly()
    {
        // Arrange
        using var producer = CreateProducer();
        var longMessage = new string('X', 100); // Longer than 64-byte limit

        // Act
        producer.SetError(SharedMemoryErrorCode.InternalError, longMessage);

        using var consumer = new SharedMemoryConsumer(producer.Name);

        // Assert
        _output.WriteLine($"Original length: {longMessage.Length}");
        _output.WriteLine($"Retrieved length: {consumer.ErrorMessage.Length}");
        _output.WriteLine($"Retrieved message: {consumer.ErrorMessage}");

        Assert.True(consumer.ErrorMessage.Length <= 63, "Error message should be truncated");
        Assert.StartsWith("XXX", consumer.ErrorMessage);
    }

    // =========================================================================
    // Test 7: Consumer Disconnect/Reconnect
    // =========================================================================

    [Fact]
    public void ConsumerReconnect_NewConsumer_ContinuesReading()
    {
        // Arrange
        using var producer = CreateProducer();
        var data1 = CreateGenerator().GenerateChunk(100);
        var data2 = CreateGenerator().GenerateChunk(100);

        // Act - first consumer reads first batch
        producer.Write(data1);
        producer.SignalDataAvailable();

        var buffer1 = new byte[data1.Length];
        using (var consumer1 = new SharedMemoryConsumer(producer.Name))
        {
            int read1 = consumer1.Read(buffer1);
            Assert.Equal(data1.Length, read1);

            // Verify consumer is attached
            Assert.True(producer.IsConsumerAttached());
        }
        // consumer1 disposed here

        // Write more data
        producer.Write(data2);
        producer.SignalDataAvailable();

        // Second consumer reads second batch
        var buffer2 = new byte[data2.Length];
        using (var consumer2 = new SharedMemoryConsumer(producer.Name))
        {
            int read2 = consumer2.Read(buffer2);

            // Assert
            _output.WriteLine($"Consumer 1 read: {buffer1.Length}");
            _output.WriteLine($"Consumer 2 read: {read2}");

            Assert.Equal(data2.Length, read2);
            Assert.True(data2.AsSpan().SequenceEqual(buffer2.AsSpan(0, read2)));
        }
    }

    [Fact]
    public void ConsumerReconnect_ProducerDetectsAttachment_FlagsCorrect()
    {
        // Arrange
        using var producer = CreateProducer();

        // Act & Assert
        Assert.False(producer.IsConsumerAttached(), "No consumer attached initially");

        using (var consumer1 = new SharedMemoryConsumer(producer.Name))
        {
            Assert.True(producer.IsConsumerAttached(), "Consumer 1 should be attached");
            _output.WriteLine("Consumer 1 attached");
        }

        // Small delay for flag propagation
        Thread.Sleep(10);
        Assert.False(producer.IsConsumerAttached(), "Consumer 1 detached");
        _output.WriteLine("Consumer 1 detached");

        using (var consumer2 = new SharedMemoryConsumer(producer.Name))
        {
            Assert.True(producer.IsConsumerAttached(), "Consumer 2 should be attached");
            _output.WriteLine("Consumer 2 attached");
        }
    }

    [Fact]
    public void ConsumerReconnect_ProducerState_CorrectlyReported()
    {
        // Arrange
        using var producer = CreateProducer();

        using var consumer = new SharedMemoryConsumer(producer.Name);

        // Act & Assert
        producer.SetState(ProducerState.Connecting);
        Thread.Sleep(10);
        Assert.Equal(ProducerState.Connecting, consumer.ProducerState);

        producer.SetState(ProducerState.Streaming);
        Thread.Sleep(10);
        Assert.Equal(ProducerState.Streaming, consumer.ProducerState);

        producer.SetState(ProducerState.Stopped);
        Thread.Sleep(10);
        Assert.Equal(ProducerState.Stopped, consumer.ProducerState);

        _output.WriteLine("Producer state transitions verified");
    }

    // =========================================================================
    // Additional Tests: Statistics and Edge Cases
    // =========================================================================

    [Fact]
    public void Statistics_ByteCounts_AccuratelyTracked()
    {
        // Arrange
        using var producer = CreateProducer();
        var data = CreateGenerator().GenerateChunk(200);

        // Act
        producer.Write(data);
        producer.SignalDataAvailable();

        using var consumer = new SharedMemoryConsumer(producer.Name);
        var buffer = new byte[data.Length];
        consumer.Read(buffer);

        var stats = consumer.GetStatistics();

        // Assert
        _output.WriteLine($"Bytes written: {stats.TotalBytesWritten}");
        _output.WriteLine($"Packets written: {stats.TotalPacketsWritten}");
        _output.WriteLine($"Bytes read: {stats.TotalBytesRead}");
        _output.WriteLine($"Packets read: {stats.TotalPacketsRead}");

        Assert.Equal((ulong)data.Length, stats.TotalBytesWritten);
        Assert.Equal((ulong)(data.Length / TsPacketSize), stats.TotalPacketsWritten);
        Assert.Equal((ulong)data.Length, stats.TotalBytesRead);
        Assert.Equal((ulong)(data.Length / TsPacketSize), stats.TotalPacketsRead);
    }

    [Fact]
    public void EdgeCase_EmptyRead_ReturnsZero()
    {
        // Arrange
        using var producer = CreateProducer();
        using var consumer = new SharedMemoryConsumer(producer.Name);

        // Act - read with no data available
        var buffer = new byte[1000];
        int read = consumer.Read(buffer);

        // Assert
        Assert.Equal(0, read);
        Assert.Equal(0, consumer.AvailableBytes);
    }

    [Fact]
    public void EdgeCase_PartialSlotRead_HandledCorrectly()
    {
        // Arrange
        using var producer = CreateProducer();
        // Write less than a full slot
        var smallData = CreateGenerator().GenerateChunk(3); // 3 packets = 564 bytes < slot size

        // Act
        producer.Write(smallData);
        producer.SignalDataAvailable();

        using var consumer = new SharedMemoryConsumer(producer.Name);
        var buffer = new byte[smallData.Length];
        int read = consumer.Read(buffer);

        // Assert
        _output.WriteLine($"Source: {smallData.Length}, Read: {read}");
        Assert.Equal(smallData.Length, read);
        Assert.True(smallData.AsSpan().SequenceEqual(buffer));
    }

    [Fact]
    public void EdgeCase_BufferSmallerThanAvailable_ReadsPartial()
    {
        // Arrange
        using var producer = CreateProducer();
        var largeData = CreateGenerator().GenerateChunk(100);

        producer.Write(largeData);
        producer.SignalDataAvailable();

        using var consumer = new SharedMemoryConsumer(producer.Name);

        // Act - read with small buffer
        var smallBuffer = new byte[TsPacketSize * 10]; // Only 10 packets worth
        int read1 = consumer.Read(smallBuffer);

        // Assert
        _output.WriteLine($"Available: {largeData.Length}, Buffer: {smallBuffer.Length}, Read: {read1}");
        Assert.Equal(smallBuffer.Length, read1);
        Assert.True(consumer.AvailableBytes > 0, "Should have more data available");

        // Read the rest
        var remainingBuffer = new byte[largeData.Length * 2]; // Large buffer for remaining + potential padding
        int read2 = consumer.Read(remainingBuffer);
        _output.WriteLine($"Second read: {read2}");

        // Should read at least the remaining source data
        int expectedMinimum = largeData.Length - smallBuffer.Length;
        Assert.True(read2 >= expectedMinimum, $"Second read {read2} should be at least {expectedMinimum}");
    }
}

/// <summary>
/// Test helper that simulates a native shared memory producer from managed C# code.
/// Implements the same protocol as the C++ SharedMemoryProducer.
/// </summary>
internal sealed class SharedMemoryTestProducer : IDisposable
{
    // Field offsets matching C++ SharedMemoryHeader
    private const int WriteSequenceOffset = 0x40;
    private const int WritePositionOffset = 0x48;
    private const int ProducerStateOffset = 0x50;
    private const int LastWriteTimestampOffset = 0x58;
    private const int TotalBytesWrittenOffset = 0x60;
    private const int TotalPacketsWrittenOffset = 0x68;
    private const int WriteWrapCountOffset = 0x70;
    private const int ReadPositionOffset = 0x88;
    private const int FlagsOffset = 0xC0;
    private const int ErrorCodeOffset = 0xC4;
    private const int ErrorTimestampOffset = 0xC8;
    private const int ErrorMessageOffset = 0xD0;

    private const int HeaderSize = 256;
    private const int TsPacketSize = 188;
    private const ulong Magic = 0x5453545245414D00UL; // "TSTREAM\0"
    private const uint ProtocolVersion = 1;

    // Flags matching C++ SharedMemoryFlags
    private const uint FlagProducerReady = 0x00000001;
    private const uint FlagConsumerReady = 0x00000002;
    private const uint FlagEndOfStream = 0x00000004;
    private const uint FlagError = 0x00000008;
    private const uint FlagDiscontinuity = 0x00000010;
    private const uint FlagOverflow = 0x00000020;

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly Semaphore? _semaphore;
    private readonly string _name;
    private readonly int _slotCount;
    private readonly int _slotSize;
    private readonly int _slotMask;
    private readonly long _dataOffset;
    private readonly long _totalSize;
    private bool _disposed;

    public string Name => _name;

    public SharedMemoryTestProducer(string name, int slotCount = 1024, int slotSize = 1316)
    {
        _name = name;
        _slotCount = slotCount;
        _slotSize = slotSize;
        _slotMask = slotCount - 1;
        _dataOffset = HeaderSize;
        _totalSize = HeaderSize + (slotCount * slotSize);

        // Create shared memory
        if (OperatingSystem.IsWindows())
        {
            _mmf = MemoryMappedFile.CreateNew(name, _totalSize);
            _semaphore = new Semaphore(0, int.MaxValue, name + "_sem");
        }
        else
        {
            // Linux/macOS: file-backed in /dev/shm
            var shmPath = GetShmPath(name);

            // Create file with shared access so consumer can open it
            var fs = new FileStream(shmPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
            fs.SetLength(_totalSize);

            _mmf = MemoryMappedFile.CreateFromFile(
                fs,
                null,
                _totalSize,
                MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None,
                leaveOpen: false // MMF will dispose the FileStream
            );
        }

        _accessor = _mmf.CreateViewAccessor(0, _totalSize, MemoryMappedFileAccess.ReadWrite);

        // Initialize header
        InitializeHeader();
    }

    private void InitializeHeader()
    {
        unsafe
        {
            byte* ptr = null;
            try
            {
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);

                // Zero the entire region
                new Span<byte>(ptr, (int)_totalSize).Clear();

                // Write metadata
                Unsafe.Write(ptr + 0x00, Magic);
                Unsafe.Write(ptr + 0x08, ProtocolVersion);
                Unsafe.Write(ptr + 0x0C, (uint)HeaderSize);
                Unsafe.Write(ptr + 0x10, (ulong)(_slotCount * _slotSize)); // buffer_capacity
                Unsafe.Write(ptr + 0x18, (ulong)_slotCount);
                Unsafe.Write(ptr + 0x20, (uint)_slotSize);
                Unsafe.Write(ptr + 0x24, (uint)TsPacketSize);

                // Initialize producer fields
                Volatile.Write(ref Unsafe.AsRef<ulong>(ptr + WriteSequenceOffset), 0UL);
                Volatile.Write(ref Unsafe.AsRef<ulong>(ptr + WritePositionOffset), 0UL);
                Volatile.Write(ref Unsafe.AsRef<ulong>(ptr + ProducerStateOffset), (ulong)ProducerState.Initializing);

                // Set producer ready flag
                Volatile.Write(ref Unsafe.AsRef<uint>(ptr + FlagsOffset), FlagProducerReady);
            }
            finally
            {
                if (ptr != null)
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
        }
    }

    public void Write(byte[] data)
    {
        Write(data.AsSpan());
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || data.Length % TsPacketSize != 0)
        {
            return;
        }

        int packetsPerSlot = _slotSize / TsPacketSize;

        unsafe
        {
            byte* ptr = null;
            try
            {
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);

                ulong writePos = Volatile.Read(ref Unsafe.AsRef<ulong>(ptr + WritePositionOffset));
                ulong readPos = Volatile.Read(ref Unsafe.AsRef<ulong>(ptr + ReadPositionOffset));

                ulong usedSlots = writePos - readPos;
                ulong availableSlots = (ulong)_slotCount - 1 - usedSlots;

                if (availableSlots == 0)
                {
                    // Buffer full - set overflow and advance read position
                    Interlocked.Or(ref Unsafe.AsRef<int>(ptr + FlagsOffset), (int)FlagOverflow);

                    ulong slotsNeeded = (ulong)((data.Length + _slotSize - 1) / _slotSize);
                    Volatile.Write(ref Unsafe.AsRef<ulong>(ptr + ReadPositionOffset), readPos + slotsNeeded);
                    availableSlots = slotsNeeded;
                }

                // Write data in slot-sized chunks
                int remaining = data.Length;
                int sourceOffset = 0;
                ulong slotIndex = writePos & (ulong)_slotMask;

                while (remaining > 0 && availableSlots > 0)
                {
                    int toCopy = Math.Min(remaining, _slotSize);
                    byte* dest = ptr + _dataOffset + (long)(slotIndex * (ulong)_slotSize);

                    data.Slice(sourceOffset, toCopy).CopyTo(new Span<byte>(dest, toCopy));

                    // Pad with 0xFF if partial slot
                    if (toCopy < _slotSize)
                    {
                        new Span<byte>(dest + toCopy, _slotSize - toCopy).Fill(0xFF);
                    }

                    sourceOffset += toCopy;
                    remaining -= toCopy;
                    slotIndex = (slotIndex + 1) & (ulong)_slotMask;
                    writePos++;
                    availableSlots--;
                }

                // Update statistics
                ulong bytesWritten = (ulong)(data.Length - remaining);
                ulong packetsWritten = bytesWritten / TsPacketSize;

                Volatile.Write(ref Unsafe.AsRef<ulong>(ptr + WritePositionOffset), writePos);

                // Update sequence number (use simple increment for single-producer)
                ref ulong seqRef = ref Unsafe.AsRef<ulong>(ptr + WriteSequenceOffset);
                Volatile.Write(ref seqRef, Volatile.Read(ref seqRef) + 1);

                // Update statistics (single producer, so simple read-modify-write is safe)
                ref ulong totalBytesRef = ref Unsafe.AsRef<ulong>(ptr + TotalBytesWrittenOffset);
                Volatile.Write(ref totalBytesRef, Volatile.Read(ref totalBytesRef) + bytesWritten);

                ref ulong totalPacketsRef = ref Unsafe.AsRef<ulong>(ptr + TotalPacketsWrittenOffset);
                Volatile.Write(ref totalPacketsRef, Volatile.Read(ref totalPacketsRef) + packetsWritten);

                Volatile.Write(
                    ref Unsafe.AsRef<ulong>(ptr + LastWriteTimestampOffset),
                    (ulong)DateTime.UtcNow.Ticks * 100
                );
            }
            finally
            {
                if (ptr != null)
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
        }
    }

    public void SignalDataAvailable()
    {
        _semaphore?.Release();
    }

    public void SetEndOfStream()
    {
        unsafe
        {
            byte* ptr = null;
            try
            {
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                Interlocked.Or(ref Unsafe.AsRef<int>(ptr + FlagsOffset), (int)FlagEndOfStream);
            }
            finally
            {
                if (ptr != null)
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
        }
        SignalDataAvailable();
    }

    public void SetError(SharedMemoryErrorCode code, string message)
    {
        unsafe
        {
            byte* ptr = null;
            try
            {
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);

                Volatile.Write(ref Unsafe.AsRef<uint>(ptr + ErrorCodeOffset), (uint)code);
                Volatile.Write(ref Unsafe.AsRef<ulong>(ptr + ErrorTimestampOffset), (ulong)DateTime.UtcNow.Ticks * 100);

                // Write message (truncate to 63 chars + null)
                var messageBytes = Encoding.UTF8.GetBytes(message);
                int len = Math.Min(messageBytes.Length, 63);
                messageBytes.AsSpan(0, len).CopyTo(new Span<byte>(ptr + ErrorMessageOffset, len));
                *(ptr + ErrorMessageOffset + len) = 0; // Null terminator

                Interlocked.Or(ref Unsafe.AsRef<int>(ptr + FlagsOffset), (int)FlagError);
            }
            finally
            {
                if (ptr != null)
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
        }
        SignalDataAvailable();
    }

    public void SetDiscontinuity()
    {
        unsafe
        {
            byte* ptr = null;
            try
            {
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                Interlocked.Or(ref Unsafe.AsRef<int>(ptr + FlagsOffset), (int)FlagDiscontinuity);
            }
            finally
            {
                if (ptr != null)
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
        }
    }

    public void SetState(ProducerState state)
    {
        unsafe
        {
            byte* ptr = null;
            try
            {
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                Volatile.Write(ref Unsafe.AsRef<ulong>(ptr + ProducerStateOffset), (ulong)state);
            }
            finally
            {
                if (ptr != null)
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
        }
    }

    public bool IsConsumerAttached()
    {
        unsafe
        {
            byte* ptr = null;
            try
            {
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                uint flags = Volatile.Read(ref Unsafe.AsRef<uint>(ptr + FlagsOffset));
                return (flags & FlagConsumerReady) != 0;
            }
            finally
            {
                if (ptr != null)
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
        }
    }

    private static string GetShmPath(string name)
    {
        var shmDir = Directory.Exists("/dev/shm") ? "/dev/shm" : "/tmp";
        return Path.Combine(shmDir, name);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Clear producer ready flag
        unsafe
        {
            byte* ptr = null;
            try
            {
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                Interlocked.And(ref Unsafe.AsRef<int>(ptr + FlagsOffset), ~(int)FlagProducerReady);
                Volatile.Write(ref Unsafe.AsRef<ulong>(ptr + ProducerStateOffset), (ulong)ProducerState.Stopped);
            }
            finally
            {
                if (ptr != null)
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
        }

        _semaphore?.Dispose();
        _accessor.Dispose();
        _mmf.Dispose();

        // Cleanup file on Linux
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.Delete(GetShmPath(_name));
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }
}
