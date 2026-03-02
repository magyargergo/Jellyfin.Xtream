using Jellyfin.Xtream.E2ETests.Infrastructure;
using Jellyfin.Xtream.Service;
using Jellyfin.Xtream.Service.Streaming.Native;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.E2ETests.Tests;

/// <summary>
/// Tests sustained streaming throughput at various bitrates through the native pipeline:
/// NativeStreamer (HTTP fetch -> restamp -> output).
/// </summary>
[Collection("E2E-Streaming")]
public class ThroughputTests(DockerTestFixture fixture, ITestOutputHelper output) : NativeE2ETestBase(output)
{
    [Theory]
    [InlineData(2_000)] // 2 Mbps (SD)
    [InlineData(5_000)] // 5 Mbps (HD)
    [InlineData(15_000)] // 15 Mbps (4K)
    public async Task Throughput_SustainedStreaming_MeetsTargetBitrate(int bitrateKbps)
    {
        // Arrange
        var streamDurationSec = GetStreamDuration();
        var url = $"{fixture.BaseUrl}/stream/{bitrateKbps}";

        Streamer.AddUrl(url);

        // Act
        Assert.True(Streamer.Start(), "Streamer should start successfully");

        await WaitForConnection(Streamer, TimeSpan.FromSeconds(5));

        // Record start time and bytes
        var startStatus = Streamer.GetStatus();
        var startTime = DateTime.UtcNow;

        await Task.Delay(TimeSpan.FromSeconds(streamDurationSec), CancellationToken.None);

        var endStatus = Streamer.GetStatus();
        var endTime = DateTime.UtcNow;
        Streamer.Stop();

        // Assert
        var elapsedSeconds = (endTime - startTime).TotalSeconds;
        var bytesTransferred = endStatus.BytesReceived - startStatus.BytesReceived;
        var actualBytesPerSecond = bytesTransferred / elapsedSeconds;

        Output.WriteLine($"Streamer bytes received: {endStatus.BytesReceived:N0}");
        Output.WriteLine($"Streamer packets output: {endStatus.PacketsOutput:N0}");
        Output.WriteLine($"Duration: {elapsedSeconds:F2}s");

        var targetBytesPerSecond = bitrateKbps * 1000.0 / 8.0;
        var efficiency = actualBytesPerSecond / targetBytesPerSecond * 100.0;

        Output.WriteLine(
            $"Target: {targetBytesPerSecond / 1000:F1} KB/s, Actual: {actualBytesPerSecond / 1000:F1} KB/s ({efficiency:F1}%)"
        );

        // Should achieve at least 90% of target bitrate (allowing for protocol overhead)
        Assert.True(efficiency >= 90.0, $"Throughput {efficiency:F1}% is below 90% of target {bitrateKbps} Kbps");

        // Should have output packets
        Assert.True(endStatus.PacketsOutput > 0, "Should have output TS packets");
    }

    [Fact]
    public async Task Throughput_BufferUtilization_RemainsHealthy()
    {
        // Arrange - stream at moderate bitrate and verify data flows through
        var url = $"{fixture.BaseUrl}/stream/5000";

        Streamer.AddUrl(url);
        Assert.True(Streamer.Start());

        await WaitForConnection(Streamer, TimeSpan.FromSeconds(5));

        // Stream for 5 seconds
        await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None);

        var status = Streamer.GetStatus();
        Streamer.Stop();

        // Assert
        Output.WriteLine($"Total bytes: {status.BytesReceived:N0}");
        Output.WriteLine($"Total packets: {status.PacketsOutput:N0}");
        Output.WriteLine($"Throughput: {status.BytesReceived / 5.0 * 8 / 1000:F1} Kbps");

        Assert.True(status.BytesReceived > 0, "Should have received data");
        Assert.True(status.PacketsOutput > 0, "Should have output TS packets");
        // At 5Mbps for 5s, expect at least 2MB (allowing for startup latency)
        Assert.True(status.BytesReceived > 2_000_000, $"Expected at least 2MB, got {status.BytesReceived:N0} bytes");
    }

    private static int GetStreamDuration()
    {
        var envValue = Environment.GetEnvironmentVariable("E2E_STREAM_DURATION_SEC");
        return int.TryParse(envValue, out var duration) ? duration : 10;
    }
}
