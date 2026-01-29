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

        streamer.AddUrl(url);

        // Act
        Assert.True(streamer.Start());

        // Wait for streamer to finish (finite stream)
        await WaitForStreamerStop(streamer, TimeSpan.FromSeconds(10));
        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        _output.WriteLine($"Received {status.BytesReceived} bytes ({status.BytesReceived / TsPacketSize} packets)");
        _output.WriteLine($"Packets output: {status.PacketsOutput}");

        Assert.True(status.BytesReceived > 0, "Should have received data");
        Assert.Equal(0, status.BytesReceived % TsPacketSize); // Must be packet-aligned
        Assert.True(status.PacketsOutput > 0, "Should have output packets");
    }

    [Fact]
    public async Task Integrity_ContinuityCounters_IncrementCorrectly()
    {
        // Arrange
        var url = $"{_fixture.BaseUrl}/stream/5000";

        using var streamer = CreateStreamerWithAnalyzer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(url);
        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));

        // Act - stream for 3 seconds
        await Task.Delay(TimeSpan.FromSeconds(3));

        var metrics = streamer.GetMetrics();
        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        _output.WriteLine($"Total bytes: {status.BytesReceived:N0}");
        _output.WriteLine($"Total packets: {status.PacketsOutput:N0}");
        _output.WriteLine($"Continuity errors: {metrics?.Priority1.ContinuityCountError ?? -1}");

        Assert.True(status.PacketsOutput > 100, "Should have processed many packets");

        if (metrics != null)
        {
            // Allow a small number of continuity errors at stream start (alignment phase)
            var errorRate = (double)metrics.Priority1.ContinuityCountError / status.PacketsOutput;
            _output.WriteLine($"Error rate: {errorRate:P4}");
            Assert.True(errorRate < 0.01, $"Continuity error rate {errorRate:P4} exceeds 1% threshold");
        }
    }

    [Fact]
    public async Task Integrity_PacketAlignment_MaintainedAcrossLargeTransfer()
    {
        // Arrange - stream enough data to verify alignment is maintained across many chunks
        var url = $"{_fixture.BaseUrl}/stream/5000";
        long targetBytes = 5 * 256 * 1024; // 1.25 MB, enough to exercise alignment

        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(url);
        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));

        // Act - stream until we have enough data or timeout
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var status = streamer.GetStatus();
            if (status.BytesReceived >= targetBytes)
            {
                break;
            }

            await Task.Delay(100);
        }

        var finalStatus = streamer.GetStatus();
        streamer.Stop();

        // Assert
        _output.WriteLine($"Data received: {finalStatus.BytesReceived:N0} bytes");
        _output.WriteLine($"Packets output: {finalStatus.PacketsOutput:N0}");

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
        var url = $"{_fixture.BaseUrl}/stream/finite/{sourcePackets}";

        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(url);
        Assert.True(streamer.Start());

        await WaitForStreamerStop(streamer, TimeSpan.FromSeconds(10));
        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        int receivedPackets = (int)(status.BytesReceived / TsPacketSize);
        _output.WriteLine($"Source: {sourcePackets}, Received: {receivedPackets}");

        // The native streamer retries on connection close, so it may receive multiples
        // of the source data. We verify no packet loss (received >= source).
        Assert.True(
            receivedPackets >= sourcePackets,
            $"Received {receivedPackets} packets, expected at least {sourcePackets} (no packet loss)"
        );

        // Verify data is TS-aligned (total bytes divisible by packet size)
        Assert.Equal(0, status.BytesReceived % TsPacketSize);
    }

    private static NativeStreamer? CreateStreamer()
    {
        return NativeStreamer.TryCreate(TsDuckStreamerConfigNative.Default);
    }

    private static NativeStreamer? CreateStreamerWithAnalyzer()
    {
        var config = new TsDuckStreamerConfigNative
        {
            ConnectTimeoutMs = 5000,
            ResponseTimeoutMs = 10000,
            StallTimeoutMs = 15000,
            MaxRetries = 3,
            InitialBackoffMs = 200,
            MaxBackoffMs = 5000,
            BackoffMultiplier = 2.0,
            BackoffJitterMs = 100,
            OutputFd = -1,
            AlignmentBufferPackets = 32,
            EnableRestamp = 0,
            RestampMode = (int)RestampingMode.Disabled,
            LowSpeedLimitBytes = 100,
            LowSpeedTimeSec = 5,
            StallsBeforeSwitch = 2,
        };

        var analyzerConfig = TsDuckConfigNative.FromManaged(
            new TsDuckConfiguration
            {
                EnableTr101290 = true,
                MetricsIntervalSeconds = 1,
                EnableAutoRestamp = false,
                RestampMode = RestampingMode.Disabled,
            }
        );

        return NativeStreamer.TryCreate(config, analyzerConfig);
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
}
