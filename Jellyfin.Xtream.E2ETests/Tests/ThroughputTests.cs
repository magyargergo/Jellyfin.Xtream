using Jellyfin.Xtream.E2ETests.Infrastructure;
using Jellyfin.Xtream.Service;
using Jellyfin.Xtream.Service.Streaming.Native;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.E2ETests.Tests;

/// <summary>
/// Tests sustained streaming throughput at various bitrates through the native pipeline:
/// NativeStreamer (HTTP fetch → restamp → output callback).
/// </summary>
[Collection("E2E")]
public class ThroughputTests
{
    private readonly DockerTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public ThroughputTests(DockerTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Theory]
    [InlineData(2_000)] // 2 Mbps (SD)
    [InlineData(5_000)] // 5 Mbps (HD)
    [InlineData(15_000)] // 15 Mbps (4K)
    public async Task Throughput_SustainedStreaming_MeetsTargetBitrate(int bitrateKbps)
    {
        // Arrange
        var streamDurationSec = GetStreamDuration();
        var url = $"{_fixture.BaseUrl}/stream/{bitrateKbps}";
        var metrics = new TestMetrics();

        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        // Set up output callback to feed metrics directly (bypasses CircularBuffer warmup)
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

        // Act
        metrics.Start();
        Assert.True(streamer.Start(), "Streamer should start successfully");

        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(streamDurationSec), CancellationToken.None);
        metrics.Stop();
        streamer.Stop();

        // Assert
        var status = streamer.GetStatus();
        _output.WriteLine(metrics.ToString());
        _output.WriteLine($"Streamer bytes received: {status.BytesReceived:N0}");
        _output.WriteLine($"Streamer packets output: {status.PacketsOutput:N0}");

        var targetBytesPerSecond = bitrateKbps * 1000.0 / 8.0;
        var actualBytesPerSecond = metrics.ThroughputBytesPerSecond;
        var efficiency = actualBytesPerSecond / targetBytesPerSecond * 100.0;

        _output.WriteLine(
            $"Target: {targetBytesPerSecond / 1000:F1} KB/s, Actual: {actualBytesPerSecond / 1000:F1} KB/s ({efficiency:F1}%)"
        );

        // Should achieve at least 90% of target bitrate (allowing for protocol overhead)
        Assert.True(efficiency >= 90.0, $"Throughput {efficiency:F1}% is below 90% of target {bitrateKbps} Kbps");

        // Valid TS data
        Assert.True(metrics.ValidSyncPackets > 0, "Should have received valid TS packets");
    }

    [Fact]
    public async Task Throughput_BufferUtilization_RemainsHealthy()
    {
        // Arrange - stream at moderate bitrate and verify data flows through output callback
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

        // Stream for 5 seconds
        await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None);
        metrics.Stop();
        streamer.Stop();

        // Assert
        _output.WriteLine($"Total bytes: {metrics.TotalBytesReceived:N0}");
        _output.WriteLine($"Total packets: {metrics.TotalPackets:N0}");
        _output.WriteLine($"Valid sync: {metrics.ValidSyncPackets:N0}");
        _output.WriteLine($"Throughput: {metrics.ThroughputKbps:F1} Kbps");

        Assert.True(metrics.TotalBytesReceived > 0, "Should have received data");
        Assert.True(metrics.ValidSyncPackets > 0, "Should have valid TS packets");
        // At 5Mbps for 5s, expect at least 2MB (allowing for startup latency)
        Assert.True(
            metrics.TotalBytesReceived > 2_000_000,
            $"Expected at least 2MB, got {metrics.TotalBytesReceived:N0} bytes"
        );
    }

    private static NativeStreamer? CreateStreamer()
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
            EnableRestamp = 1,
            RestampMode = (int)RestampingMode.Correct,
            LowSpeedLimitBytes = 100,
            LowSpeedTimeSec = 5,
            StallsBeforeSwitch = 2,
            Reserved = 0,
        };

        return NativeStreamer.TryCreate(config);
    }

    private static async Task WaitForConnection(NativeStreamer streamer, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var status = streamer.GetStatus();
            if (status.State == StreamerState.Streaming)
            {
                return;
            }

            await Task.Delay(50);
        }
    }

    private static int GetStreamDuration()
    {
        var envValue = Environment.GetEnvironmentVariable("E2E_STREAM_DURATION_SEC");
        return int.TryParse(envValue, out var duration) ? duration : 10;
    }
}
