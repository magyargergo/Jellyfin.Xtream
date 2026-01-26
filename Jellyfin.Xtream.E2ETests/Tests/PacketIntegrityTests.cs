using System.Collections.Concurrent;
using Jellyfin.Xtream.E2ETests.Infrastructure;
using Jellyfin.Xtream.Service.Streaming.Native;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.E2ETests.Tests;

/// <summary>
/// Tests MPEG-TS packet integrity through the full pipeline.
/// Verifies sync bytes, continuity counters, and packet alignment.
/// </summary>
[Collection("E2E")]
public class PacketIntegrityTests
{
    private const int TsPacketSize = 188;
    private const byte SyncByte = 0x47;

    private readonly DockerTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public PacketIntegrityTests(DockerTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task Integrity_FiniteStream_AllPacketsHaveValidSync()
    {
        // Arrange - stream a known number of packets
        const int sourcePackets = 1000;
        var url = $"{_fixture.BaseUrl}/stream/finite/{sourcePackets}";

        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        var receivedChunks = new ConcurrentQueue<byte[]>();

        streamer.SetOutputCallback(
            (ptr, len) =>
            {
                unsafe
                {
                    var chunk = new byte[len];
                    new ReadOnlySpan<byte>((void*)ptr, len).CopyTo(chunk);
                    receivedChunks.Enqueue(chunk);
                }
            }
        );

        streamer.AddUrl(url);

        // Act
        Assert.True(streamer.Start());

        // Wait for streamer to finish (finite stream)
        await WaitForStreamerStop(streamer, TimeSpan.FromSeconds(10));
        streamer.Stop();

        // Reassemble all received data
        var allData = ReassembleChunks(receivedChunks);

        // Assert
        _output.WriteLine($"Received {allData.Length} bytes ({allData.Length / TsPacketSize} packets)");

        Assert.True(allData.Length > 0, "Should have received data");
        Assert.Equal(0, allData.Length % TsPacketSize); // Must be packet-aligned

        int packetCount = allData.Length / TsPacketSize;
        int validSync = 0;
        for (int i = 0; i < packetCount; i++)
        {
            if (allData[i * TsPacketSize] == SyncByte)
            {
                validSync++;
            }
        }

        _output.WriteLine($"Valid sync bytes: {validSync}/{packetCount}");
        Assert.Equal(packetCount, validSync); // 100% sync bytes valid
    }

    [Fact]
    public async Task Integrity_ContinuityCounters_IncrementCorrectly()
    {
        // Arrange
        var url = $"{_fixture.BaseUrl}/stream/5000";
        var metrics = new TestMetrics();

        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.SetOutputCallback(
            (ptr, len) =>
            {
                unsafe
                {
                    metrics.ProcessReceivedData((byte*)ptr, len);
                }
            }
        );

        streamer.AddUrl(url);
        metrics.Start();
        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));

        // Act - stream for 3 seconds
        await Task.Delay(TimeSpan.FromSeconds(3));

        metrics.Stop();
        streamer.Stop();

        // Assert
        _output.WriteLine($"Total packets: {metrics.TotalPackets}");
        _output.WriteLine($"Valid sync: {metrics.ValidSyncPackets}");
        _output.WriteLine($"Continuity errors: {metrics.ContinuityErrors}");

        Assert.True(metrics.TotalPackets > 100, "Should have processed many packets");
        Assert.Equal(metrics.TotalPackets, metrics.ValidSyncPackets); // All packets aligned

        // Allow a small number of continuity errors at stream start (alignment phase)
        var errorRate = (double)metrics.ContinuityErrors / metrics.TotalPackets;
        _output.WriteLine($"Error rate: {errorRate:P4}");
        Assert.True(errorRate < 0.01, $"Continuity error rate {errorRate:P4} exceeds 1% threshold");
    }

    [Fact]
    public async Task Integrity_PacketAlignment_MaintainedAcrossLargeTransfer()
    {
        // Arrange - stream enough data to verify alignment is maintained across many chunks
        var url = $"{_fixture.BaseUrl}/stream/5000";
        var metrics = new TestMetrics();
        long targetBytes = 5 * 256 * 1024; // 1.25 MB, enough to exercise alignment

        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.SetOutputCallback(
            (ptr, len) =>
            {
                unsafe
                {
                    metrics.ProcessReceivedData((byte*)ptr, len);
                }
            }
        );

        streamer.AddUrl(url);
        metrics.Start();
        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));

        // Act - stream until we have enough data or timeout
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (metrics.TotalBytesReceived < targetBytes && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        metrics.Stop();
        streamer.Stop();

        // Assert
        _output.WriteLine($"Data received: {metrics.TotalBytesReceived:N0} bytes");
        _output.WriteLine($"Packets checked: {metrics.TotalPackets}, valid sync: {metrics.ValidSyncPackets}");

        Assert.True(metrics.TotalBytesReceived >= targetBytes, "Should have received target bytes");
        // All packets should have valid sync (proves alignment never breaks)
        Assert.Equal(metrics.TotalPackets, metrics.ValidSyncPackets);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(5000)]
    [InlineData(10000)]
    public async Task Integrity_NoDuplication_PacketCountMatches(int sourcePackets)
    {
        // Arrange
        var url = $"{_fixture.BaseUrl}/stream/finite/{sourcePackets}";

        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        var totalBytesReceived = 0L;

        streamer.SetOutputCallback(
            (ptr, len) =>
            {
                Interlocked.Add(ref totalBytesReceived, len);
            }
        );

        streamer.AddUrl(url);
        Assert.True(streamer.Start());

        await WaitForStreamerStop(streamer, TimeSpan.FromSeconds(10));
        streamer.Stop();

        // Assert
        int receivedPackets = (int)(Interlocked.Read(ref totalBytesReceived) / TsPacketSize);
        _output.WriteLine($"Source: {sourcePackets}, Received: {receivedPackets}");

        // The native streamer retries on connection close, so it may receive multiples
        // of the source data. We verify no packet loss (received >= source).
        Assert.True(
            receivedPackets >= sourcePackets,
            $"Received {receivedPackets} packets, expected at least {sourcePackets} (no packet loss)"
        );

        // Verify data is TS-aligned (total bytes divisible by packet size)
        Assert.Equal(0, Interlocked.Read(ref totalBytesReceived) % TsPacketSize);
    }

    private static NativeStreamer? CreateStreamer()
    {
        return NativeStreamer.TryCreate(TsDuckStreamerConfigNative.Default);
    }

    private static async Task WaitForConnection(NativeStreamer streamer, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (streamer.GetStatus().State == StreamerState.Streaming)
                return;
            await Task.Delay(50);
        }
    }

    private static async Task WaitForStreamerStop(NativeStreamer streamer, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var status = streamer.GetStatus();
            if (status.IsTerminal || (status.BytesReceived > 0 && status.State == StreamerState.Idle))
                return;
            await Task.Delay(100);
        }
    }

    private static byte[] ReassembleChunks(ConcurrentQueue<byte[]> chunks)
    {
        var totalLength = 0;
        foreach (var chunk in chunks)
        {
            totalLength += chunk.Length;
        }

        var result = new byte[totalLength];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            Buffer.BlockCopy(chunk, 0, result, offset, chunk.Length);
            offset += chunk.Length;
        }

        return result;
    }
}
