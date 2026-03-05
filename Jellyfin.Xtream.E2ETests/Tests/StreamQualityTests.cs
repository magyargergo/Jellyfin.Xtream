using Jellyfin.Xtream.E2ETests.Infrastructure;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.E2ETests.Tests;

/// <summary>
/// TDD-style E2E tests for stream quality metrics accuracy.
/// Tests bitrate estimation, continuity counter tracking, PCR accuracy,
/// delayed startup, and rapid failover scenarios.
/// </summary>
[Collection("E2E-Analysis")]
public class StreamQualityTests(DockerTestFixture fixture, ITestOutputHelper output)
    : NativeE2ETestBase(output, TestConfigs.Analyzer)
{
    // ========================================================================
    // Bitrate Estimation Accuracy
    // ========================================================================

    [Theory]
    [InlineData(2000)]
    [InlineData(5000)]
    [InlineData(10000)]
    public async Task BitrateEstimation_CleanStream_WithinTolerance(int bitrateKbps)
    {
        // Arrange - stream at known bitrate and verify the estimated bitrate
        // is within +/-25% of the target. This validates the core assumption
        // that packet-distance timing uses for PAT/PMT/PCR checks.
        var url = $"{fixture.BaseUrl}/stream/{bitrateKbps}";

        Streamer.AddUrl(url);

        Assert.True(Streamer.Start());
        await WaitForConnection(Streamer, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(5));

        var metrics = Streamer.GetMetrics();
        Streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        var targetBps = bitrateKbps * 1000L;
        var estimatedBps = metrics.TsBitrate;
        var ratio = (double)estimatedBps / targetBps;

        Output.WriteLine($"Target: {targetBps} bps, Estimated: {estimatedBps} bps, Ratio: {ratio:F3}");

        Assert.True(
            ratio >= 0.75 && ratio <= 1.25,
            $"Bitrate estimate {estimatedBps} should be within +/-25% of target {targetBps} (ratio: {ratio:F3})"
        );
    }

    // ========================================================================
    // Continuity Counter Error Detection
    // ========================================================================

    [Fact]
    public async Task ContinuityCounter_CorruptedStream_DetectsErrors()
    {
        // Arrange - the corrupted endpoint drops packets (corrupts sync bytes),
        // which causes gaps in continuity counters. The alignment buffer strips
        // invalid packets, so downstream sees CC discontinuities.
        var url = $"{fixture.BaseUrl}/stream/corrupted/5000";

        Streamer.AddUrl(url);

        Assert.True(Streamer.Start());
        await WaitForConnection(Streamer, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(5));

        var metrics = Streamer.GetMetrics();
        Streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        Output.WriteLine($"CC errors: {metrics.Priority1.ContinuityCountError}");
        Output.WriteLine($"Transport errors: {metrics.Priority2.TransportError}");
        Output.WriteLine($"Total P1: {metrics.Priority1.TotalErrors}, P2: {metrics.Priority2.TotalErrors}");

        // Corrupted stream should produce CC errors (from dropped packets)
        // and/or transport errors (from TEI flag)
        Assert.True(
            metrics.Priority1.ContinuityCountError > 0 || metrics.Priority2.TransportError > 0,
            "Corrupted stream should produce CC errors or transport errors"
        );
    }

    [Fact]
    public async Task ContinuityCounter_CleanStream_ZeroErrors()
    {
        // Arrange - a clean stream should have zero CC errors
        var url = $"{fixture.BaseUrl}/stream/5000";

        Streamer.AddUrl(url);

        Assert.True(Streamer.Start());
        await WaitForConnection(Streamer, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(5));

        var metrics = Streamer.GetMetrics();
        Streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        Output.WriteLine($"CC errors: {metrics.Priority1.ContinuityCountError}");

        Assert.Equal(0, metrics.Priority1.ContinuityCountError);
    }

    // ========================================================================
    // PCR Accuracy on Clean Streams
    // ========================================================================

    [Fact]
    public async Task PcrAccuracy_CleanStream_NoAccuracyErrors()
    {
        // Arrange - PCR accuracy check compares actual PCR deltas against
        // expected deltas derived from bitrate. On a clean stream with stable
        // bitrate, this should produce zero accuracy errors.
        var url = $"{fixture.BaseUrl}/stream/5000";

        Streamer.AddUrl(url);

        Assert.True(Streamer.Start());
        await WaitForConnection(Streamer, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(5));

        var metrics = Streamer.GetMetrics();
        Streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        Output.WriteLine($"PCR accuracy errors: {metrics.Priority2.PcrAccuracyError}");
        Output.WriteLine($"PCR discontinuity errors: {metrics.Priority2.PcrDiscontinuityError}");

        // Clean stream should have zero PCR accuracy errors
        Assert.Equal(0, metrics.Priority2.PcrAccuracyError);
        // Clean stream should have zero PCR discontinuity errors
        Assert.Equal(0, metrics.Priority2.PcrDiscontinuityError);
    }

    [Fact]
    public async Task PcrAccuracy_HighBitrate_NoAccuracyErrors()
    {
        // Arrange - same check at 10 Mbps to verify accuracy scales with bitrate
        var url = $"{fixture.BaseUrl}/stream/10000";

        Streamer.AddUrl(url);

        Assert.True(Streamer.Start());
        await WaitForConnection(Streamer, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(5));

        var metrics = Streamer.GetMetrics();
        Streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        Output.WriteLine($"PCR accuracy errors: {metrics.Priority2.PcrAccuracyError}");
        Output.WriteLine($"Bitrate: {metrics.TsBitrate / 1_000_000.0:F2} Mbps");

        Assert.Equal(0, metrics.Priority2.PcrAccuracyError);
    }

    // ========================================================================
    // Delayed Stream Startup
    // ========================================================================

    [Fact]
    public async Task DelayedStartup_MetricsStillValid()
    {
        // Arrange - delayed endpoint waits before sending data.
        // The analyzer should handle the initial empty period gracefully
        // (startup grace period prevents false PAT/PMT timeouts).
        var url = $"{fixture.BaseUrl}/stream/delayed/2000";

        Streamer.AddUrl(url);

        Assert.True(Streamer.Start());
        // Wait longer than the delay + connection time
        await Task.Delay(TimeSpan.FromSeconds(8));

        var metrics = Streamer.GetMetrics();
        var status = Streamer.GetStatus();
        Streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        Output.WriteLine($"Total bytes: {status.BytesReceived:N0}");
        Output.WriteLine($"Services: {metrics.ServiceCount}, PIDs: {metrics.PidCount}");
        Output.WriteLine($"PAT errors: {metrics.Priority1.PatError}");

        // Should have received data after the delay
        Assert.True(status.BytesReceived > 0, "Should receive data after delay period");
        // Service detection should work after delayed start
        Assert.True(metrics.ServiceCount >= 1, "Should detect services after delayed start");
        // No PAT errors (startup grace period should cover the delay)
        Assert.Equal(0, metrics.Priority1.PatError);
    }

    // ========================================================================
    // Rapid URL Switching
    // ========================================================================

    [Fact]
    public async Task RapidSwitch_MultipleConsecutive_NoCorruption()
    {
        // Arrange - rapidly switch URLs multiple times to stress-test
        // state management. Metrics should remain valid afterward.
        var url1 = $"{fixture.BaseUrl}/stream/5000";
        var url2 = $"{fixture.BaseUrl}/stream/5000";

        Streamer.AddUrl(url1);
        Streamer.AddUrl(url2);

        Assert.True(Streamer.Start());
        await WaitForConnection(Streamer, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Rapid switches: 3 switches with 500ms between each
        for (int i = 0; i < 3; i++)
        {
            Streamer.RequestSwitch();
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        // Let it stabilize
        await Task.Delay(TimeSpan.FromSeconds(3));

        var metrics = Streamer.GetMetrics();
        var status = Streamer.GetStatus();
        Streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        Output.WriteLine($"Final state: {status.State}");
        Output.WriteLine($"Packets output: {status.PacketsOutput}");
        Output.WriteLine($"Services: {metrics.ServiceCount}, PIDs: {metrics.PidCount}");
        Output.WriteLine($"Bitrate: {metrics.TsBitrate / 1000.0:F1} Kbps");
        Output.WriteLine($"P1 errors: {metrics.Priority1.TotalErrors}");

        // After stabilization, should still detect stream structure
        Assert.True(metrics.TsBitrate > 0, "Bitrate should be detected after rapid switches");
        Assert.True(metrics.ServiceCount >= 1, "Services should be detected after rapid switches");
        Assert.True(metrics.PidCount >= 4, "PIDs should be tracked after rapid switches");
        // No sync errors (stream data is valid, just switched URLs)
        Assert.Equal(0, metrics.Priority1.SyncByteError);
        Assert.Equal(0, metrics.Priority1.SyncLoss);
    }

    // ========================================================================
    // Quality Score Consistency Across Bitrates
    // ========================================================================

    [Theory]
    [InlineData(2000)]
    [InlineData(5000)]
    [InlineData(10000)]
    public async Task QualityScore_CleanStream_HighAtAllBitrates(int bitrateKbps)
    {
        // Arrange - quality score should be high on clean streams regardless
        // of bitrate. This validates that packet-distance timing scales correctly.
        var url = $"{fixture.BaseUrl}/stream/{bitrateKbps}";

        Streamer.AddUrl(url);

        Assert.True(Streamer.Start());
        await WaitForConnection(Streamer, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(5));

        var metrics = Streamer.GetMetrics();
        Streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        var qualityScore = metrics.CalculateQualityScore();
        Output.WriteLine($"Bitrate: {bitrateKbps} Kbps -> Quality: {qualityScore}/100");
        Output.WriteLine($"P1: {metrics.Priority1.TotalErrors}, P2: {metrics.Priority2.TotalErrors}");
        Output.WriteLine($"PAT: {metrics.Priority1.PatError}, PMT: {metrics.Priority1.PmtError}");
        Output.WriteLine($"PCR rep: {metrics.Priority2.PcrRepetitionError}, acc: {metrics.Priority2.PcrAccuracyError}");

        // Quality should be high on all clean streams
        Assert.True(qualityScore >= 80, $"Quality score at {bitrateKbps} Kbps should be >= 80, got {qualityScore}");
        Assert.Equal(0, metrics.Priority1.PatError);
        Assert.Equal(0, metrics.Priority1.PmtError);
    }

    // ========================================================================
    // PID Tracking Completeness
    // ========================================================================

    [Fact]
    public async Task PidTracking_CleanStream_DetectsAllExpectedPids()
    {
        // Arrange - a clean stream should detect at minimum:
        // PAT (0x0000), PMT (0x0100), Video (0x0101), Audio (0x0102)
        // Plus possibly null PID (0x1FFF) if present
        var url = $"{fixture.BaseUrl}/stream/5000";

        Streamer.AddUrl(url);

        Assert.True(Streamer.Start());
        await WaitForConnection(Streamer, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(5));

        var metrics = Streamer.GetMetrics();
        Streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        Output.WriteLine($"PIDs detected: {metrics.PidCount}");
        Output.WriteLine($"Services: {metrics.ServiceCount}");

        // Minimum expected PIDs: PAT, PMT, Video, Audio
        Assert.True(metrics.PidCount >= 4, $"Should detect at least 4 PIDs, got {metrics.PidCount}");
        // At least 1 service (program)
        Assert.True(metrics.ServiceCount >= 1, $"Should detect at least 1 service, got {metrics.ServiceCount}");
    }

    // ========================================================================
    // Stall Recovery
    // ========================================================================

    [Fact]
    public async Task StallRecovery_UnstableToStable_MetricsRecover()
    {
        // Arrange - start on an unstable URL (drops after 2s), fail over to stable.
        // After recovery, metrics should be valid with no sync/PAT errors.
        fixture.UnstableDropAfterMs = 2000;
        var unstableUrl = $"{fixture.BaseUrl}/stream/unstable";
        var stableUrl = $"{fixture.BaseUrl}/stream/5000";

        Streamer.AddUrl(unstableUrl);
        Streamer.AddUrl(stableUrl);

        Assert.True(Streamer.Start());
        // Wait long enough for: connect + drop + backoff + reconnect to stable + data
        await Task.Delay(TimeSpan.FromSeconds(10));

        var metrics = Streamer.GetMetrics();
        var status = Streamer.GetStatus();
        Streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        Output.WriteLine($"State: {status.State}, URL index: {status.CurrentUrlIndex}");
        Output.WriteLine($"Total bytes: {status.BytesReceived:N0}");
        Output.WriteLine($"Bitrate: {metrics.TsBitrate / 1000.0:F1} Kbps");
        Output.WriteLine($"Services: {metrics.ServiceCount}");
        Output.WriteLine($"PAT errors: {metrics.Priority1.PatError}");
        Output.WriteLine($"Sync errors: {metrics.Priority1.SyncByteError}");

        // Should have recovered and be producing valid metrics
        Assert.True(status.BytesReceived > 0, "Should have received data after recovery");
        Assert.True(metrics.TsBitrate > 0, "Bitrate should be detected after recovery");
        Assert.True(metrics.ServiceCount >= 1, "Services should be detected after recovery");
        // No sync errors (all stream data from either URL should have valid sync)
        Assert.Equal(0, metrics.Priority1.SyncByteError);
    }

    // ========================================================================
    // Helpers
    // ========================================================================
}
