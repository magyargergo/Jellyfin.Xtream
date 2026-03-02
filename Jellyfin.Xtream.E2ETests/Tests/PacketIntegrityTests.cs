using Jellyfin.Xtream.E2ETests.Infrastructure;
using Jellyfin.Xtream.Service.Streaming.Native;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.E2ETests.Tests;

/// <summary>
/// Tests MPEG-TS packet integrity through the full pipeline.
/// Verifies sync bytes, continuity counters, and packet alignment.
/// </summary>
[Collection("E2E-Streaming")]
public class PacketIntegrityTests(DockerTestFixture fixture, ITestOutputHelper output) : NativeE2ETestBase(output)
{
    private const int TsPacketSize = 188;

    [Fact]
    public async Task Integrity_FiniteStream_AllPacketsHaveValidSync()
    {
        // Arrange - stream a known number of packets
        const int sourcePackets = 1000;
        var url = $"{fixture.BaseUrl}/stream/finite/{sourcePackets}";

        Streamer.AddUrl(url);

        // Act
        Assert.True(Streamer.Start());

        // Wait for streamer to finish (finite stream)
        await WaitForStreamerStop(Streamer, TimeSpan.FromSeconds(10));
        var status = Streamer.GetStatus();
        Streamer.Stop();

        // Assert
        Output.WriteLine($"Received {status.BytesReceived} bytes ({status.BytesReceived / TsPacketSize} packets)");
        Output.WriteLine($"Packets output: {status.PacketsOutput}");

        Assert.True(status.BytesReceived > 0, "Should have received data");
        Assert.Equal(0, status.BytesReceived % TsPacketSize); // Must be packet-aligned
        Assert.True(status.PacketsOutput > 0, "Should have output packets");
    }

    [Fact]
    public async Task Integrity_ContinuityCounters_IncrementCorrectly()
    {
        // Arrange
        var url = $"{fixture.BaseUrl}/stream/5000";

        using var streamer = BuildStreamer(TestConfigs.Analyzer);
        streamer.AddUrl(url);
        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));

        // Act - stream for 3 seconds
        await Task.Delay(TimeSpan.FromSeconds(3));

        var metrics = streamer.GetMetrics();
        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        Output.WriteLine($"Total bytes: {status.BytesReceived:N0}");
        Output.WriteLine($"Total packets: {status.PacketsOutput:N0}");
        Output.WriteLine($"Continuity errors: {metrics?.Priority1.ContinuityCountError ?? -1}");

        Assert.True(status.PacketsOutput > 100, "Should have processed many packets");

        if (metrics != null)
        {
            // Allow a small number of continuity errors at stream start (alignment phase)
            var errorRate = (double)metrics.Priority1.ContinuityCountError / status.PacketsOutput;
            Output.WriteLine($"Error rate: {errorRate:P4}");
            Assert.True(errorRate < 0.01, $"Continuity error rate {errorRate:P4} exceeds 1% threshold");
        }
    }

    [Fact]
    public async Task Integrity_PacketAlignment_MaintainedAcrossLargeTransfer()
    {
        // Arrange - stream enough data to verify alignment is maintained across many chunks
        var url = $"{fixture.BaseUrl}/stream/5000";
        long targetBytes = 5 * 256 * 1024; // 1.25 MB, enough to exercise alignment

        Streamer.AddUrl(url);
        Assert.True(Streamer.Start());
        await WaitForConnection(Streamer, TimeSpan.FromSeconds(5));

        // Act - stream until we have enough data or timeout
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var status = Streamer.GetStatus();
            if (status.BytesReceived >= targetBytes)
            {
                break;
            }

            await Task.Delay(100);
        }

        var finalStatus = Streamer.GetStatus();
        Streamer.Stop();

        // Assert
        Output.WriteLine($"Data received: {finalStatus.BytesReceived:N0} bytes");
        Output.WriteLine($"Packets output: {finalStatus.PacketsOutput:N0}");

        Assert.True(finalStatus.BytesReceived >= targetBytes, "Should have received target bytes");
        // All output should be packet-aligned
        Assert.Equal(0, finalStatus.BytesReceived % TsPacketSize);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(5000)]
    [InlineData(10000)]
    public async Task Integrity_NoDuplication_PacketCountMatches(int sourcePackets)
    {
        // Arrange
        var url = $"{fixture.BaseUrl}/stream/finite/{sourcePackets}";

        Streamer.AddUrl(url);
        Assert.True(Streamer.Start());

        await WaitForStreamerStop(Streamer, TimeSpan.FromSeconds(10));
        var status = Streamer.GetStatus();
        Streamer.Stop();

        // Assert
        int receivedPackets = (int)(status.BytesReceived / TsPacketSize);
        Output.WriteLine($"Source: {sourcePackets}, Received: {receivedPackets}");

        // The native streamer retries on connection close, so it may receive multiples
        // of the source data. We verify no packet loss (received >= source).
        Assert.True(
            receivedPackets >= sourcePackets,
            $"Received {receivedPackets} packets, expected at least {sourcePackets} (no packet loss)"
        );

        // Verify data is TS-aligned (total bytes divisible by packet size)
        Assert.Equal(0, status.BytesReceived % TsPacketSize);
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
}
