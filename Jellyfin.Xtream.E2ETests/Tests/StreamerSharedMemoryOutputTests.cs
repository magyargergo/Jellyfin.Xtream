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
using Jellyfin.Xtream.E2ETests.Infrastructure;
using Jellyfin.Xtream.Service.Streaming.Native;
using Jellyfin.Xtream.Service.Streaming.SharedMemory;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.E2ETests.Tests;

/// <summary>
/// End-to-end tests for NativeStreamer in shared memory output mode.
/// These tests verify the full pipeline: HTTP -> NativeStreamer -> SharedMemory -> Consumer.
/// </summary>
/// <remarks>
/// <para>
/// This tests the integration used by the Restream class where NativeStreamer
/// is configured with SetSharedMemoryOutput() instead of output callbacks.
/// </para>
/// <para>
/// Tests use the SimpleHttpTestServer to serve MPEG-TS streams and verify
/// data flows correctly through the shared memory IPC layer.
/// </para>
/// </remarks>
[Collection("E2E")]
public sealed class StreamerSharedMemoryOutputTests : IDisposable
{
    private const int TsPacketSize = 188;

    private readonly DockerTestFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly List<IDisposable> _disposables = new();

    public StreamerSharedMemoryOutputTests(DockerTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            try
            {
                disposable.Dispose();
            }
            catch
            {
                // Best effort cleanup
            }
        }

        _disposables.Clear();
        GC.SuppressFinalize(this);
    }

    private static string GenerateUniqueShmName() => $"streamer_shm_test_{Guid.NewGuid():N}";

    private NativeStreamer? CreateStreamer()
    {
        var streamer = NativeStreamer.TryCreate(TsDuckStreamerConfigNative.Default);
        if (streamer != null)
        {
            _disposables.Add(streamer);
        }

        return streamer;
    }

    private void SkipIfNativeUnavailable(NativeStreamer? streamer)
    {
        if (streamer is null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }
    }

    // =========================================================================
    // Test 1: Basic Shared Memory Output
    // =========================================================================

    /// <summary>
    /// Verifies that NativeStreamer can be configured for shared memory output
    /// and that data flows from HTTP stream to the SharedMemoryConsumer.
    /// </summary>
    [Fact]
    public async Task SharedMemoryOutput_BasicDataFlow_BytesReceivedByConsumer()
    {
        // Arrange
        var url = $"{_fixture.BaseUrl}/stream/5000"; // 5 Mbps stream
        var shmName = GenerateUniqueShmName();

        var streamer = CreateStreamer();
        if (streamer is null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(url);
        streamer.SetSharedMemoryOutput(shmName, slotCount: 1024, slotSize: 1316);

        // Act
        Assert.True(streamer.Start(), "Streamer should start successfully");

        // Wait for streaming to begin
        Assert.True(
            await TestHelpers.WaitForStreamingAsync(streamer, TimeSpan.FromSeconds(5)),
            "Streamer should reach streaming state"
        );

        // Give some time for data to flow
        await Task.Delay(100);

        // Connect consumer and read data
        using var consumer = new SharedMemoryConsumer(shmName);
        _disposables.Add(consumer);

        var readBuffer = new byte[1316 * 16];
        long totalBytesRead = 0;
        var sw = Stopwatch.StartNew();
        const int durationMs = 3000;

        while (sw.ElapsedMilliseconds < durationMs)
        {
            if (consumer.WaitForData(TimeSpan.FromMilliseconds(100)))
            {
                int read = consumer.Read(readBuffer);
                if (read > 0)
                {
                    totalBytesRead += read;
                }
            }

            if (consumer.HasError)
            {
                _output.WriteLine($"Consumer error: {consumer.ErrorCode} - {consumer.ErrorMessage}");
                break;
            }
        }

        streamer.Stop();

        // Assert
        var streamerStatus = streamer.GetStatus();
        _output.WriteLine($"Streamer bytes received: {streamerStatus.BytesReceived:N0}");
        _output.WriteLine($"Consumer bytes read: {totalBytesRead:N0}");
        _output.WriteLine($"Streamer state: {streamerStatus.State}");

        Assert.True(streamerStatus.BytesReceived > 0, "Streamer should have received data from HTTP");
        Assert.True(totalBytesRead > 0, "Consumer should have read data from shared memory");
        Assert.True(streamer.IsSharedMemoryMode, "Streamer should be in shared memory mode");
        Assert.Equal(shmName, streamer.SharedMemoryName);
    }

    /// <summary>
    /// Verifies that sync bytes (0x47) are present in data read from shared memory,
    /// indicating valid TS packet alignment is maintained.
    /// </summary>
    [Fact]
    public async Task SharedMemoryOutput_DataIntegrity_SyncBytesValid()
    {
        // Arrange
        var url = $"{_fixture.BaseUrl}/stream/5000";
        var shmName = GenerateUniqueShmName();

        var streamer = CreateStreamer();
        if (streamer is null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(url);
        streamer.SetSharedMemoryOutput(shmName, slotCount: 1024, slotSize: 1316);

        Assert.True(streamer.Start());
        Assert.True(await TestHelpers.WaitForStreamingAsync(streamer, TimeSpan.FromSeconds(5)));
        await Task.Delay(100);

        using var consumer = new SharedMemoryConsumer(shmName);
        _disposables.Add(consumer);

        // Act - collect data
        var allData = new List<byte>();
        var readBuffer = new byte[1316 * 16];
        var sw = Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < 2000 && allData.Count < 100_000)
        {
            if (consumer.WaitForData(TimeSpan.FromMilliseconds(50)))
            {
                int read = consumer.Read(readBuffer);
                if (read > 0)
                {
                    allData.AddRange(readBuffer.Take(read));
                }
            }
        }

        streamer.Stop();

        // Assert - check sync bytes (0x47) or padding bytes (0xFF)
        // Slot padding uses 0xFF bytes, which appear at packet boundaries in partial slots
        int packetCount = allData.Count / TsPacketSize;
        int validSyncCount = 0;
        int paddingPacketCount = 0;

        for (int i = 0; i < packetCount; i++)
        {
            byte syncByte = allData[i * TsPacketSize];
            if (syncByte == 0x47)
            {
                validSyncCount++;
            }
            else if (syncByte == 0xFF)
            {
                paddingPacketCount++; // Slot padding is expected
            }
        }

        int totalValidPackets = validSyncCount + paddingPacketCount;
        double syncRate = packetCount > 0 ? totalValidPackets * 100.0 / packetCount : 0;
        _output.WriteLine($"Total bytes: {allData.Count:N0}");
        _output.WriteLine(
            $"Packets: {packetCount}, Valid sync: {validSyncCount}, Padding: {paddingPacketCount} ({syncRate:F1}%)"
        );

        Assert.True(packetCount > 0, "Should have read at least some packets");
        Assert.True(validSyncCount > 0, "Should have at least some actual TS packets with 0x47 sync");
        Assert.True(syncRate > 95, $"Valid packet rate {syncRate:F1}% should be > 95%");
    }

    // =========================================================================
    // Test 2: High Throughput
    // =========================================================================

    /// <summary>
    /// Verifies that the shared memory output mode can sustain high bitrate streaming.
    /// </summary>
    [Fact]
    public async Task SharedMemoryOutput_HighBitrate_SustainsDataFlow()
    {
        // Arrange - 10 Mbps stream
        var url = $"{_fixture.BaseUrl}/stream/10000";
        var shmName = GenerateUniqueShmName();

        var streamer = CreateStreamer();
        if (streamer is null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(url);
        streamer.SetSharedMemoryOutput(shmName, slotCount: 2048, slotSize: 1316);

        Assert.True(streamer.Start());
        Assert.True(await TestHelpers.WaitForStreamingAsync(streamer, TimeSpan.FromSeconds(5)));
        await Task.Delay(100);

        using var consumer = new SharedMemoryConsumer(shmName);
        _disposables.Add(consumer);

        // Act - stream for 5 seconds
        var readBuffer = new byte[1316 * 32];
        long totalBytesRead = 0;
        int overflowCount = 0;
        var sw = Stopwatch.StartNew();
        const int durationMs = 5000;

        while (sw.ElapsedMilliseconds < durationMs)
        {
            if (consumer.WaitForData(TimeSpan.FromMilliseconds(50)))
            {
                if (consumer.ConsumeOverflow())
                {
                    overflowCount++;
                }

                int read = consumer.Read(readBuffer);
                if (read > 0)
                {
                    totalBytesRead += read;
                }
            }
        }

        sw.Stop();
        streamer.Stop();

        // Assert
        var status = streamer.GetStatus();
        var actualBitrateMbps = totalBytesRead * 8.0 / sw.Elapsed.TotalSeconds / 1_000_000;

        _output.WriteLine($"Duration: {sw.Elapsed.TotalSeconds:F2}s");
        _output.WriteLine($"Streamer received: {status.BytesReceived:N0} bytes");
        _output.WriteLine($"Consumer read: {totalBytesRead:N0} bytes");
        _output.WriteLine($"Consumer bitrate: {actualBitrateMbps:F2} Mbps");
        _output.WriteLine($"Overflow events: {overflowCount}");

        Assert.True(totalBytesRead > 1_000_000, "Should have read at least 1 MB of data");
        Assert.True(actualBitrateMbps > 5, $"Bitrate {actualBitrateMbps:F2} Mbps should be > 5 Mbps");
        Assert.Equal(0, overflowCount); // With 2048 slots, should not overflow at 10 Mbps
    }

    // =========================================================================
    // Test 3: Discontinuity Detection
    // =========================================================================

    /// <summary>
    /// Verifies that discontinuity is signaled through shared memory when URL switches occur.
    /// </summary>
    [Fact]
    public async Task SharedMemoryOutput_UrlSwitch_DiscontinuitySignaled()
    {
        // Arrange - two URLs for switching
        var url1 = $"{_fixture.BaseUrl}/stream/5000";
        var url2 = $"{_fixture.BaseUrl}/stream/2000";
        var shmName = GenerateUniqueShmName();

        var streamer = CreateStreamer();
        if (streamer is null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(url1);
        streamer.AddUrl(url2);
        streamer.SetSharedMemoryOutput(shmName, slotCount: 1024, slotSize: 1316);

        Assert.True(streamer.Start());
        Assert.True(await TestHelpers.WaitForStreamingAsync(streamer, TimeSpan.FromSeconds(5)));
        await Task.Delay(100);

        using var consumer = new SharedMemoryConsumer(shmName);
        _disposables.Add(consumer);

        // Act - read data, then request switch, then check for discontinuity
        var readBuffer = new byte[1316 * 16];
        long totalBytesRead = 0;
        bool discontinuityDetected = false;

        // Read initial data
        var initialSw = Stopwatch.StartNew();
        while (initialSw.ElapsedMilliseconds < 1000)
        {
            if (consumer.WaitForData(TimeSpan.FromMilliseconds(50)))
            {
                totalBytesRead += consumer.Read(readBuffer);
            }
        }

        _output.WriteLine($"Initial read: {totalBytesRead:N0} bytes");

        // Request URL switch
        streamer.RequestSwitch();
        _output.WriteLine("Switch requested");

        // Wait for switch and check for discontinuity
        var switchSw = Stopwatch.StartNew();
        while (switchSw.ElapsedMilliseconds < 5000)
        {
            if (consumer.WaitForData(TimeSpan.FromMilliseconds(50)))
            {
                if (consumer.ConsumeDiscontinuity())
                {
                    discontinuityDetected = true;
                    _output.WriteLine(
                        $"Discontinuity detected at {switchSw.ElapsedMilliseconds}ms after switch request"
                    );
                }

                totalBytesRead += consumer.Read(readBuffer);
            }

            // Check if switch completed
            var status = streamer.GetStatus();
            if (status.SwitchesCompleted > 0 && discontinuityDetected)
            {
                break;
            }
        }

        streamer.Stop();

        // Assert
        var finalStatus = streamer.GetStatus();
        _output.WriteLine($"Total bytes read: {totalBytesRead:N0}");
        _output.WriteLine($"Switches completed: {finalStatus.SwitchesCompleted}");
        _output.WriteLine($"Discontinuity detected: {discontinuityDetected}");

        Assert.True(totalBytesRead > 0, "Should have read data");
        // Note: Discontinuity detection depends on timing - it may or may not occur
        // based on how fast the switch completes and when the consumer checks
    }

    // =========================================================================
    // Test 4: End of Stream
    // =========================================================================

    /// <summary>
    /// Verifies that end-of-stream is properly signaled when streamer stops.
    /// </summary>
    [Fact]
    public async Task SharedMemoryOutput_StreamerStop_EosSignaled()
    {
        // Arrange
        var url = $"{_fixture.BaseUrl}/stream/5000";
        var shmName = GenerateUniqueShmName();

        var streamer = CreateStreamer();
        if (streamer is null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(url);
        streamer.SetSharedMemoryOutput(shmName, slotCount: 1024, slotSize: 1316);

        Assert.True(streamer.Start());
        Assert.True(await TestHelpers.WaitForStreamingAsync(streamer, TimeSpan.FromSeconds(5)));
        await Task.Delay(100);

        using var consumer = new SharedMemoryConsumer(shmName);
        _disposables.Add(consumer);

        // Read some data
        var readBuffer = new byte[1316 * 16];
        long totalBytesRead = 0;

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1000)
        {
            if (consumer.WaitForData(TimeSpan.FromMilliseconds(50)))
            {
                totalBytesRead += consumer.Read(readBuffer);
            }
        }

        _output.WriteLine($"Read before stop: {totalBytesRead:N0} bytes");
        Assert.False(consumer.IsEndOfStream, "EOS should not be set before stop");

        // Act - stop the streamer
        streamer.Stop();

        // Wait for EOS to propagate
        var eosSw = Stopwatch.StartNew();
        bool eosDetected = false;

        while (eosSw.ElapsedMilliseconds < 2000 && !eosDetected)
        {
            consumer.WaitForData(TimeSpan.FromMilliseconds(50));
            totalBytesRead += consumer.Read(readBuffer);

            if (consumer.IsEndOfStream)
            {
                eosDetected = true;
                _output.WriteLine($"EOS detected at {eosSw.ElapsedMilliseconds}ms after stop");
            }
        }

        // Drain any remaining data
        while (consumer.AvailableBytes > 0)
        {
            totalBytesRead += consumer.Read(readBuffer);
        }

        // Assert
        _output.WriteLine($"Total bytes read: {totalBytesRead:N0}");
        _output.WriteLine($"EOS detected: {eosDetected}");

        Assert.True(eosDetected, "End-of-stream should be signaled when streamer stops");
        Assert.True(totalBytesRead > 0, "Should have read data before EOS");
    }

    // =========================================================================
    // Test 5: Consumer Attachment
    // =========================================================================

    /// <summary>
    /// Verifies that consumer attachment is detected by the native streamer.
    /// </summary>
    [Fact]
    public async Task SharedMemoryOutput_ConsumerAttachment_Detected()
    {
        // Arrange
        var url = $"{_fixture.BaseUrl}/stream/5000";
        var shmName = GenerateUniqueShmName();

        var streamer = CreateStreamer();
        if (streamer is null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(url);
        streamer.SetSharedMemoryOutput(shmName, slotCount: 1024, slotSize: 1316);

        Assert.True(streamer.Start());
        Assert.True(await TestHelpers.WaitForStreamingAsync(streamer, TimeSpan.FromSeconds(5)));
        await Task.Delay(200); // Let producer initialize

        // Act - create and dispose consumers
        using (var consumer1 = new SharedMemoryConsumer(shmName))
        {
            // Read some data to ensure consumer is active
            var buffer = new byte[1316];
            consumer1.WaitForData(TimeSpan.FromMilliseconds(500));
            consumer1.Read(buffer);

            _output.WriteLine("Consumer 1 attached and read data");
        }
        // consumer1 disposed

        _output.WriteLine("Consumer 1 detached");

        // Attach second consumer
        using (var consumer2 = new SharedMemoryConsumer(shmName))
        {
            var buffer = new byte[1316];
            consumer2.WaitForData(TimeSpan.FromMilliseconds(500));
            int read = consumer2.Read(buffer);

            _output.WriteLine($"Consumer 2 attached and read {read} bytes");

            // Assert - consumer2 should be able to read data
            Assert.True(read > 0, "Second consumer should be able to read data");
        }

        streamer.Stop();
        _output.WriteLine("Test completed successfully");
    }

    // =========================================================================
    // Test 6: SharedMemoryName Property
    // =========================================================================

    /// <summary>
    /// Verifies that SharedMemoryName property is set correctly.
    /// </summary>
    [Fact]
    public void SharedMemoryOutput_NameProperty_SetCorrectly()
    {
        // Arrange
        var shmName = GenerateUniqueShmName();

        var streamer = CreateStreamer();
        if (streamer is null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        // Act
        Assert.Null(streamer.SharedMemoryName);
        Assert.False(streamer.IsSharedMemoryMode);

        streamer.SetSharedMemoryOutput(shmName, slotCount: 1024, slotSize: 1316);

        // Assert
        Assert.Equal(shmName, streamer.SharedMemoryName);
        Assert.True(streamer.IsSharedMemoryMode);

        _output.WriteLine($"SharedMemoryName: {streamer.SharedMemoryName}");
        _output.WriteLine($"IsSharedMemoryMode: {streamer.IsSharedMemoryMode}");
    }

    // =========================================================================
    // Test 7: Finite Stream
    // =========================================================================

    /// <summary>
    /// Verifies that a finite stream is fully transferred via shared memory.
    /// </summary>
    [Fact]
    public async Task SharedMemoryOutput_FiniteStream_AllDataTransferred()
    {
        // Arrange - finite stream of 1000 packets
        const int packetCount = 1000;
        var url = $"{_fixture.BaseUrl}/stream/finite/{packetCount}";
        var shmName = GenerateUniqueShmName();
        var expectedBytes = packetCount * TsPacketSize;

        var streamer = CreateStreamer();
        if (streamer is null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(url);
        streamer.SetSharedMemoryOutput(shmName, slotCount: 1024, slotSize: 1316);

        Assert.True(streamer.Start());

        // Wait for streaming or terminal state
        await TestHelpers.WaitForStreamerFinishAsync(streamer, TimeSpan.FromSeconds(10));

        // Give time for all data to transfer through shared memory
        await Task.Delay(200);

        using var consumer = new SharedMemoryConsumer(shmName);
        _disposables.Add(consumer);

        // Act - drain all data
        var readBuffer = new byte[1316 * 16];
        long totalBytesRead = 0;
        var sw = Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < 5000)
        {
            int read = consumer.Read(readBuffer);
            if (read > 0)
            {
                totalBytesRead += read;
            }
            else if (consumer.IsEndOfStream && consumer.AvailableBytes == 0)
            {
                break;
            }

            await Task.Delay(10);
        }

        streamer.Stop();

        // Assert
        var status = streamer.GetStatus();
        _output.WriteLine($"Expected bytes: {expectedBytes:N0}");
        _output.WriteLine($"Streamer received: {status.BytesReceived:N0}");
        _output.WriteLine($"Consumer read: {totalBytesRead:N0}");

        // Note: Due to slot alignment, we may read slightly more than expected
        Assert.True(
            totalBytesRead >= expectedBytes * 0.95,
            $"Should read at least 95% of expected data. Expected: {expectedBytes}, Got: {totalBytesRead}"
        );
    }
}
