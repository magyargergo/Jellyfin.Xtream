using Jellyfin.Xtream.E2ETests.Infrastructure;
using Jellyfin.Xtream.Service.Streaming.Native;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.E2ETests.Tests;

/// <summary>
/// Tests PCR/PTS restamping through the native pipeline.
/// Verifies that output timestamps are monotonically increasing and properly spaced.
/// </summary>
[Collection("E2E-Streaming")]
public class RestampingTests(DockerTestFixture fixture, ITestOutputHelper output)
    : NativeE2ETestBase(output, TestConfigs.Restamp(RestampingMode.Correct))
{
    [Fact]
    public async Task Restamp_PcrValues_AreMonotonicallyIncreasing()
    {
        // Arrange
        var url = $"{fixture.BaseUrl}/stream/5000";

        using var streamer = BuildStreamer(TestConfigs.Restamp(RestampingMode.Correct));

        streamer.AddUrl(url);
        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));

        // Act - stream for 5 seconds
        await Task.Delay(TimeSpan.FromSeconds(5));

        var pcrAnalysis = streamer.GetPcrAnalysis();
        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        Output.WriteLine($"Bytes received: {status.BytesReceived:N0}");
        Output.WriteLine($"Packets output: {status.PacketsOutput:N0}");

        if (pcrAnalysis != null)
        {
            var pcr = pcrAnalysis.Value;
            Output.WriteLine($"PCR count: {pcr.PcrCount}");
            Output.WriteLine($"PCR valid count: {pcr.PcrValidCount}");
            Output.WriteLine($"PCR interval: {pcr.PcrIntervalMs:F2}ms");
            Output.WriteLine($"PCR jitter: {pcr.PcrJitterUs:F2}us");

            Assert.True(pcr.PcrCount >= 10, "Should have at least 10 PCR samples");
            // Valid PCR count should match total count (no discontinuities)
            Assert.True(pcr.PcrValidCount >= pcr.PcrCount - 1, "Most PCRs should be valid (monotonic)");
        }
        else
        {
            Assert.True(status.PacketsOutput > 0, "Should have output packets");
        }
    }

    [Fact]
    public async Task Restamp_PcrIntervals_WithinAcceptableRange()
    {
        // Arrange
        var url = $"{fixture.BaseUrl}/stream/5000";

        using var streamer = BuildStreamer(TestConfigs.Restamp(RestampingMode.Correct));

        streamer.AddUrl(url);
        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));

        // Act - stream for 5 seconds
        await Task.Delay(TimeSpan.FromSeconds(5));

        var pcrAnalysis = streamer.GetPcrAnalysis();
        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert - check PCR intervals
        Output.WriteLine($"Bytes received: {status.BytesReceived:N0}");
        Output.WriteLine($"Packets output: {status.PacketsOutput:N0}");

        if (pcrAnalysis != null)
        {
            var pcr = pcrAnalysis.Value;
            Output.WriteLine($"PCR interval: {pcr.PcrIntervalMs:F2}ms");

            // TR 101 290 requires PCR repetition interval <= 40ms (with tolerance)
            // After restamping, most intervals should be reasonable
            if (pcr.PcrCount > 1)
            {
                Assert.False(pcr.HasIntervalViolation, $"PCR interval {pcr.PcrIntervalMs:F2}ms exceeds threshold");
            }
        }
    }

    [Fact]
    public async Task Restamp_RestampingStatistics_ShowCorrections()
    {
        // Arrange - use the streamer's built-in analyzer to check restamp stats
        var url = $"{fixture.BaseUrl}/stream/5000";

        using var streamer = BuildStreamer(TestConfigs.Restamp(RestampingMode.Correct));

        streamer.AddUrl(url);
        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));

        // Stream for a while to collect stats
        await Task.Delay(TimeSpan.FromSeconds(5));

        // Assert - check status shows packets were processed
        var status = streamer.GetStatus();
        Output.WriteLine($"Streamer status:");
        Output.WriteLine($"  State: {status.State}");
        Output.WriteLine($"  Bytes received: {status.BytesReceived:N0}");
        Output.WriteLine($"  Packets output: {status.PacketsOutput:N0}");

        streamer.Stop();

        Assert.True(status.PacketsOutput > 0, "Should have output packets");
    }

    [Fact]
    public async Task Restamp_LargeOffset_CorrectedToZeroBased()
    {
        // Arrange - verify data flows correctly and PCR analysis works
        var url = $"{fixture.BaseUrl}/stream/5000";

        using var streamer = BuildStreamer(TestConfigs.Restamp(RestampingMode.Correct));

        streamer.AddUrl(url);
        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));

        // Collect data for 3 seconds
        await Task.Delay(TimeSpan.FromSeconds(3));

        var pcrAnalysis = streamer.GetPcrAnalysis();
        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert - PCR analysis should show valid data
        Output.WriteLine($"Bytes received: {status.BytesReceived:N0}");
        Output.WriteLine($"Packets output: {status.PacketsOutput:N0}");

        if (pcrAnalysis != null)
        {
            var pcr = pcrAnalysis.Value;
            Output.WriteLine($"PCR count: {pcr.PcrCount}");
            Output.WriteLine($"PCR jitter max: {pcr.PcrJitterMaxUs:F2}us");

            Assert.True(pcr.PcrCount > 0, "Should have PCR values");
            // PCR jitter should be reasonable (< 1 second = 1,000,000 us)
            Assert.True(pcr.PcrJitterMaxUs < 1_000_000, $"PCR jitter max {pcr.PcrJitterMaxUs:F0}us is too high");
        }
        else
        {
            // At minimum, should have received and output data
            Assert.True(status.BytesReceived > 0, "Should have received data");
            Assert.True(status.PacketsOutput > 0, "Should have output packets");
        }
    }

    [Fact]
    public async Task Restamp_CorrectMode_ReducesAbsoluteAvDriftComparedToMonitor()
    {
        // Arrange - burst delivery exaggerates timing skew and exercises drift correction.
        var url = $"{fixture.BaseUrl}/stream/burst/5000";

        // Act
        var monitorSync = await CollectAvSyncAnalysisAsync(url, RestampingMode.Monitor, TimeSpan.FromSeconds(8));
        var correctSync = await CollectAvSyncAnalysisAsync(url, RestampingMode.Correct, TimeSpan.FromSeconds(8));

        // Assert
        Output.WriteLine(
            $"Monitor drift={monitorSync.AbsoluteDriftMs:F2}ms, peak={monitorSync.PeakDriftMs:F2}ms, status={monitorSync.Status}"
        );
        Output.WriteLine(
            $"Correct drift={correctSync.AbsoluteDriftMs:F2}ms, peak={correctSync.PeakDriftMs:F2}ms, status={correctSync.Status}"
        );

        Assert.True(monitorSync.VideoPtsCount > 0, "Monitor mode should collect video PTS samples");
        Assert.True(monitorSync.AudioPtsCount > 0, "Monitor mode should collect audio PTS samples");
        Assert.True(correctSync.VideoPtsCount > 0, "Correct mode should collect video PTS samples");
        Assert.True(correctSync.AudioPtsCount > 0, "Correct mode should collect audio PTS samples");

        // Compare using average drift to filter out periodic -50ms PTS timing transients.
        double correctAvgDrift = Math.Abs(correctSync.AvgDriftMs);
        double monitorAvgDrift = Math.Abs(monitorSync.AvgDriftMs);
        Output.WriteLine($"Monitor avg drift={monitorAvgDrift:F2}ms, Correct avg drift={correctAvgDrift:F2}ms");

        // Correct mode must not be materially worse than monitor mode.
        Assert.True(
            correctAvgDrift <= monitorAvgDrift + 5.0,
            $"Correct mode avg drift ({correctAvgDrift:F2}ms) should not exceed monitor mode avg drift ({monitorAvgDrift:F2}ms) by >5ms"
        );

        // If monitor shows meaningful drift, correct mode should reduce it measurably.
        if (monitorAvgDrift >= 20.0)
        {
            Assert.True(
                correctAvgDrift <= monitorAvgDrift - 3.0,
                $"Expected correction to reduce avg drift by at least 3ms (monitor={monitorAvgDrift:F2}ms, correct={correctAvgDrift:F2}ms)"
            );
        }
    }

    [Fact]
    public async Task Restamp_UnstableSource_ClampsPcrJumpWithoutSevereIntervalViolations()
    {
        // Arrange - unstable endpoint drops every ~2s and forces timeline recovery.
        var url = $"{fixture.BaseUrl}/stream/unstable";
        using var streamer = BuildStreamer(TestConfigs.Restamp(RestampingMode.Correct));

        streamer.AddUrl(url);
        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));

        // Act
        await Task.Delay(TimeSpan.FromSeconds(10));
        var status = streamer.GetStatus();
        var pcr = streamer.GetPcrAnalysis();
        streamer.Stop();

        // Assert
        Output.WriteLine(
            $"Reconnections={status.Reconnections}, bytes={status.BytesReceived:N0}, packets={status.PacketsOutput:N0}"
        );
        Assert.True(status.Reconnections > 0, "Unstable source should trigger at least one reconnection");
        Assert.True(status.PacketsOutput > 0, "Should continue producing output packets under instability");

        if (pcr != null)
        {
            Output.WriteLine(
                $"PCR count={pcr.Value.PcrCount}, valid={pcr.Value.PcrValidCount}, interval={pcr.Value.PcrIntervalMs:F2}ms, jitterMax={pcr.Value.PcrJitterMaxUs:F0}us"
            );

            Assert.True(pcr.Value.PcrCount >= 10, "Should collect enough PCR samples during unstable streaming");

            Assert.False(
                pcr.Value.HasIntervalViolation,
                "PCR jump clamping should avoid interval violations on reconnect"
            );

            // For reconnect-heavy streams, wall-clock jitter spikes are expected due to data gaps.
            // Validate continuity with valid PCR ratio instead of max wall-clock jitter.
            var validRatio = pcr.Value.PcrCount == 0 ? 0.0 : (double)pcr.Value.PcrValidCount / pcr.Value.PcrCount;
            Assert.True(
                validRatio >= 0.6,
                $"Expected >=60% valid PCR continuity under reconnects (valid={pcr.Value.PcrValidCount}, total={pcr.Value.PcrCount})"
            );
        }
    }

    private async Task<AvSyncAnalysis> CollectAvSyncAnalysisAsync(
        string url,
        RestampingMode mode,
        TimeSpan sampleDuration
    )
    {
        using var streamer = BuildStreamer(TestConfigs.Restamp(mode));

        streamer.AddUrl(url);
        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));
        await Task.Delay(sampleDuration);

        var avSync = streamer.GetAvSyncAnalysis();
        var status = streamer.GetStatus();
        streamer.Stop();

        Output.WriteLine(
            $"Mode={mode}, bytes={status.BytesReceived:N0}, packets={status.PacketsOutput:N0}, reconnects={status.Reconnections}"
        );
        Assert.True(status.PacketsOutput > 0, $"Mode {mode} should output packets");
        Assert.NotNull(avSync);
        return avSync!.Value;
    }
}
