using System.Diagnostics;
using Jellyfin.Xtream.E2ETests.Infrastructure;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.E2ETests.Tests;

/// <summary>
/// Tests pipeline startup latency: time from NativeStreamer.Start() to first
/// data arriving (detected via GetStatus().BytesReceived).
/// </summary>
[Collection("E2E-Streaming")]
public class LatencyTests(DockerTestFixture fixture, ITestOutputHelper output) : NativeE2ETestBase(output)
{
    [Fact]
    public async Task Latency_FirstByte_WithinAcceptableRange()
    {
        // Arrange
        const int iterations = 20;
        var measurements = new List<double>();
        var url = $"{fixture.BaseUrl}/stream/5000";

        for (int i = 0; i < iterations; i++)
        {
            var latency = await MeasureFirstByteLatency(url);
            if (latency >= 0)
            {
                measurements.Add(latency);
            }
        }

        // Assert
        Assert.True(
            measurements.Count >= iterations / 2,
            $"Expected at least {iterations / 2} successful measurements, got {measurements.Count}"
        );

        measurements.Sort();
        var p50 = GetPercentile(measurements, 50);
        var p95 = GetPercentile(measurements, 95);
        var p99 = GetPercentile(measurements, 99);

        Output.WriteLine($"Latency measurements: {measurements.Count}");
        Output.WriteLine($"  p50: {p50:F2}ms");
        Output.WriteLine($"  p95: {p95:F2}ms");
        Output.WriteLine($"  p99: {p99:F2}ms");
        Output.WriteLine($"  min: {measurements.Min():F2}ms");
        Output.WriteLine($"  max: {measurements.Max():F2}ms");

        // p99 should be under 500ms for localhost
        Assert.True(p99 < 500.0, $"p99 latency {p99:F2}ms exceeds 500ms threshold");
    }

    [Fact]
    public async Task Latency_DelayedStream_AddsExpectedDelay()
    {
        // Arrange - server introduces 200ms delay before sending data
        const int serverDelayMs = 200;
        var url = $"{fixture.BaseUrl}/stream/delayed/{serverDelayMs}";

        var latency = await MeasureFirstByteLatency(url, timeoutMs: 5000);

        // Assert
        Output.WriteLine($"Latency with {serverDelayMs}ms server delay: {latency:F2}ms");

        Assert.True(latency >= 0, "Should have received data");
        // Latency should include the server delay (within tolerance)
        Assert.True(
            latency >= serverDelayMs * 0.8,
            $"Latency {latency:F2}ms should be at least {serverDelayMs * 0.8}ms (80% of server delay)"
        );
    }

    [Fact]
    public async Task Latency_CircularBufferWarmup_CompletesInTime()
    {
        // Arrange - test that CircularBufferReadStream's warmup phase completes quickly
        // Use high bitrate (50 Mbps) so the 4MB warmup fills in < 1 second
        var url = $"{fixture.BaseUrl}/stream/50000";

        Streamer.AddUrl(url);
        var sw = Stopwatch.StartNew();
        Assert.True(Streamer.Start());

        // Act - poll for data arriving
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var status = Streamer.GetStatus();
            if (status.BytesReceived > 4 * 1024 * 1024) // 4MB warmup equivalent
            {
                break;
            }

            await Task.Delay(50);
        }

        sw.Stop();
        var status2 = Streamer.GetStatus();
        Streamer.Stop();

        // Assert
        Output.WriteLine($"Time to 4MB: {sw.ElapsedMilliseconds}ms, bytes: {status2.BytesReceived:N0}");
        Assert.True(status2.BytesReceived > 0, "Should have received data");
        // Warmup should complete within 10 seconds (generous for CI)
        Assert.True(sw.ElapsedMilliseconds < 10000, $"Warmup took {sw.ElapsedMilliseconds}ms, expected < 10000ms");
    }

    private async Task<double> MeasureFirstByteLatency(string url, int timeoutMs = 3000)
    {
        using var streamer = BuildStreamer(TestConfigs.Default);
        streamer.AddUrl(url);

        // Start and measure
        var sw = Stopwatch.StartNew();
        streamer.Start();

        // Poll for first byte received
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var status = streamer.GetStatus();
            if (status.BytesReceived > 0)
            {
                sw.Stop();
                streamer.Stop();
                return sw.Elapsed.TotalMilliseconds;
            }

            await Task.Delay(5);
        }

        streamer.Stop();
        return -1;
    }

    private static double GetPercentile(List<double> sortedData, int percentile)
    {
        if (sortedData.Count == 0)
        {
            return 0;
        }

        var index = (int)Math.Ceiling(percentile / 100.0 * sortedData.Count) - 1;
        return sortedData[Math.Max(0, Math.Min(index, sortedData.Count - 1))];
    }
}
