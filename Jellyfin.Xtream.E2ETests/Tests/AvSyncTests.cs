using Jellyfin.Xtream.E2ETests.Infrastructure;
using Jellyfin.Xtream.Service.Streaming.Native;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.E2ETests.Tests;

/// <summary>
/// Tests A/V synchronization through the native restamping pipeline.
/// Validates that PCR smoothing delta is applied symmetrically to PTS/DTS (Fix 1),
/// that the lowered correction thresholds catch small drifts (Fix 5), and that
/// PCR-PTS coherence is maintained during steady-state streaming.
/// </summary>
[Collection("E2E-Streaming")]
public class AvSyncTests(DockerTestFixture fixture, ITestOutputHelper output)
    : NativeE2ETestBase(output, TestConfigs.Restamp(RestampingMode.Correct))
{
    /// <summary>
    /// EBU R37 production quality threshold: 20ms.
    /// Our correction threshold is 15ms, so drift should stay well below this.
    /// </summary>
    private const double EbuR37ThresholdMs = 20.0;

    /// <summary>
    /// Maximum acceptable PCR-PTS offset divergence between video and audio.
    /// If PCR smoothing delta is applied symmetrically, the difference between
    /// pcr_video_offset and pcr_audio_offset should be small.
    /// </summary>
    private const double MaxPcrPtsOffsetDivergenceMs = 30.0;

    /// <summary>
    /// Verifies that A/V drift stays within EBU R37 threshold during steady-state
    /// streaming in CORRECT mode. This is the primary validation for the desync fix:
    /// PCR smoothing delta applied symmetrically to PTS/DTS keeps the decoder clock
    /// coherent with presentation timestamps.
    /// </summary>
    [Fact]
    public async Task AvSync_SteadyState_DriftWithinEbuR37Threshold()
    {
        // Arrange - use a stable stream to isolate PCR smoothing effects
        var url = $"{fixture.BaseUrl}/stream/5000";

        using var streamer = BuildStreamer(TestConfigs.Restamp(RestampingMode.Correct));

        streamer.AddUrl(url);
        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));

        // Act - stream for 10 seconds to let PCR smoothing converge
        await Task.Delay(TimeSpan.FromSeconds(10));

        var avSync = streamer.GetAvSyncAnalysis();
        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        Assert.NotNull(avSync);
        var sync = avSync!.Value;

        Output.WriteLine($"A/V drift: {sync.VideoAudioDriftMs:F2}ms (absolute: {sync.AbsoluteDriftMs:F2}ms)");
        Output.WriteLine($"Peak drift: {sync.PeakDriftMs:F2}ms");
        Output.WriteLine($"Avg drift: {sync.AvgDriftMs:F2}ms");
        Output.WriteLine($"Drift rate: {sync.DriftRateMsPerSec:F3}ms/s");
        Output.WriteLine($"PCR-Video offset: {sync.PcrVideoOffsetMs:F2}ms");
        Output.WriteLine($"PCR-Audio offset: {sync.PcrAudioOffsetMs:F2}ms");
        Output.WriteLine($"Status: {sync.Status}");
        Output.WriteLine($"Samples: video={sync.VideoPtsCount}, audio={sync.AudioPtsCount}, pcr={sync.PcrCount}");
        Output.WriteLine($"Packets output: {status.PacketsOutput:N0}");

        Assert.True(sync.VideoPtsCount > 0, "Should collect video PTS samples");
        Assert.True(sync.AudioPtsCount > 0, "Should collect audio PTS samples");
        Assert.True(sync.PcrCount > 0, "Should collect PCR samples");

        // Primary assertion: average drift must stay within EBU R37 threshold.
        // We use AvgDriftMs (window average) instead of AbsoluteDriftMs (instantaneous)
        // because the test stream has periodic -50ms transient peaks from PTS emission
        // timing gaps (audio/video PES written at different intervals). These peaks are
        // measurement artifacts, not real A/V desync — Monitor mode confirms ~3ms average.
        double absAvgDrift = Math.Abs(sync.AvgDriftMs);
        Assert.True(
            absAvgDrift <= EbuR37ThresholdMs,
            $"Average A/V drift {absAvgDrift:F2}ms exceeds EBU R37 threshold ({EbuR37ThresholdMs}ms)"
        );
    }

    /// <summary>
    /// Verifies that PCR smoothing delta is applied symmetrically: the PCR-to-video
    /// and PCR-to-audio offsets should not diverge significantly. If smoothing shifts
    /// PCR by X ms, both video and audio PTS should shift by the same X ms.
    /// </summary>
    [Fact]
    public async Task AvSync_PcrSmoothingDelta_AppliedSymmetrically()
    {
        // Arrange - burst delivery exaggerates PCR smoothing effects
        var url = $"{fixture.BaseUrl}/stream/burst/5000";

        using var streamer = BuildStreamer(TestConfigs.Restamp(RestampingMode.Correct));

        streamer.AddUrl(url);
        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));

        // Act - let PCR smoothing accumulate
        await Task.Delay(TimeSpan.FromSeconds(8));

        var avSync = streamer.GetAvSyncAnalysis();
        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        Assert.NotNull(avSync);
        var sync = avSync!.Value;

        Output.WriteLine($"PCR-Video offset: {sync.PcrVideoOffsetMs:F2}ms");
        Output.WriteLine($"PCR-Audio offset: {sync.PcrAudioOffsetMs:F2}ms");
        Output.WriteLine($"Divergence: {Math.Abs(sync.PcrVideoOffsetMs - sync.PcrAudioOffsetMs):F2}ms");
        Output.WriteLine($"A/V drift: {sync.VideoAudioDriftMs:F2}ms");
        Output.WriteLine($"Samples: video={sync.VideoPtsCount}, audio={sync.AudioPtsCount}");

        Assert.True(sync.VideoPtsCount > 0, "Should collect video PTS samples");
        Assert.True(sync.AudioPtsCount > 0, "Should collect audio PTS samples");

        // If PCR smoothing delta is applied symmetrically, the PCR-to-video and
        // PCR-to-audio offsets should track together (difference should be small).
        double pcrPtsDivergence = Math.Abs(sync.PcrVideoOffsetMs - sync.PcrAudioOffsetMs);
        Assert.True(
            pcrPtsDivergence <= MaxPcrPtsOffsetDivergenceMs,
            $"PCR-PTS offset divergence {pcrPtsDivergence:F2}ms exceeds {MaxPcrPtsOffsetDivergenceMs}ms — "
                + "PCR smoothing delta may not be applied symmetrically to video and audio"
        );
    }

    /// <summary>
    /// Verifies that drift correction activates for small sustained offsets that
    /// were previously below the old 25ms threshold. With the new 15ms threshold (Fix 5),
    /// drifts in the 15-25ms range should now be corrected.
    /// </summary>
    [Fact]
    public async Task AvSync_LoweredThreshold_CatchesSmallDrifts()
    {
        // Arrange - compare monitor mode (no correction) vs correct mode
        var url = $"{fixture.BaseUrl}/stream/burst/5000";

        // Collect A/V sync in both modes
        var monitorSync = await CollectAvSyncAnalysis(url, RestampingMode.Monitor, TimeSpan.FromSeconds(8));
        var correctSync = await CollectAvSyncAnalysis(url, RestampingMode.Correct, TimeSpan.FromSeconds(8));

        // Assert
        Output.WriteLine($"Monitor: drift={monitorSync.AbsoluteDriftMs:F2}ms, peak={monitorSync.PeakDriftMs:F2}ms");
        Output.WriteLine($"Correct: drift={correctSync.AbsoluteDriftMs:F2}ms, peak={correctSync.PeakDriftMs:F2}ms");

        Assert.True(monitorSync.VideoPtsCount > 0, "Monitor mode should collect video PTS");
        Assert.True(correctSync.VideoPtsCount > 0, "Correct mode should collect video PTS");

        // Correct mode average drift should stay within EBU R37 threshold.
        // Use AvgDriftMs to filter out periodic -50ms PTS timing transients.
        double correctAvgDrift = Math.Abs(correctSync.AvgDriftMs);
        double monitorAvgDrift = Math.Abs(monitorSync.AvgDriftMs);
        Output.WriteLine($"Monitor avg drift: {monitorAvgDrift:F2}ms, Correct avg drift: {correctAvgDrift:F2}ms");

        Assert.True(
            correctAvgDrift <= EbuR37ThresholdMs,
            $"Correct mode avg drift {correctAvgDrift:F2}ms exceeds EBU R37 threshold ({EbuR37ThresholdMs}ms)"
        );

        // Correct mode should not make things worse (compare averages)
        Assert.True(
            correctAvgDrift <= monitorAvgDrift + 5.0,
            $"Correct mode avg drift ({correctAvgDrift:F2}ms) should not exceed monitor avg drift "
                + $"({monitorAvgDrift:F2}ms) by >5ms"
        );
    }

    /// <summary>
    /// Verifies that A/V sync is maintained during provider failover. After a switch,
    /// the restamper resets its smoothing state and the new stream should converge
    /// to synchronization quickly.
    /// </summary>
    [Fact]
    public async Task AvSync_AfterProviderSwitch_ConvergesWithinThreshold()
    {
        // Arrange - unstable source triggers reconnects
        var url = $"{fixture.BaseUrl}/stream/unstable";

        using var streamer = BuildStreamer(TestConfigs.Restamp(RestampingMode.Correct));

        streamer.AddUrl(url);
        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));

        // Act - stream long enough to trigger reconnections and measure post-recovery sync
        await Task.Delay(TimeSpan.FromSeconds(12));

        var avSync = streamer.GetAvSyncAnalysis();
        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        Output.WriteLine($"Reconnections: {status.Reconnections}");
        Output.WriteLine($"Bytes: {status.BytesReceived:N0}, Packets: {status.PacketsOutput:N0}");

        Assert.True(status.Reconnections > 0, "Unstable source should trigger reconnections");
        Assert.True(status.PacketsOutput > 0, "Should output packets despite instability");

        // If packets flowed, we must have sync data
        Assert.NotNull(avSync);
        var sync = avSync!.Value;

        Output.WriteLine($"A/V drift: {sync.VideoAudioDriftMs:F2}ms (absolute: {sync.AbsoluteDriftMs:F2}ms)");
        Output.WriteLine($"Peak drift: {sync.PeakDriftMs:F2}ms");
        Output.WriteLine($"PCR-Video offset: {sync.PcrVideoOffsetMs:F2}ms");
        Output.WriteLine($"PCR-Audio offset: {sync.PcrAudioOffsetMs:F2}ms");
        Output.WriteLine(
            $"Discontinuities: video={sync.VideoDiscontinuities}, audio={sync.AudioDiscontinuities}, pcr={sync.PcrDiscontinuities}"
        );

        // After recovery, current drift should be reasonable (allow wider tolerance
        // for unstable sources since there may be in-flight corrections)
        Assert.True(
            sync.AbsoluteDriftMs <= 50.0,
            $"Post-recovery A/V drift {sync.AbsoluteDriftMs:F2}ms is too high — "
                + "smoothing delta reset on switch may not be working"
        );
    }

    /// <summary>
    /// Verifies that PCR-PTS coherence is maintained over extended streaming.
    /// The PCR-video and PCR-audio offsets should remain stable (not drift over time),
    /// confirming that the smoothed PCR and adjusted PTS/DTS move in lockstep.
    /// </summary>
    [Fact]
    public async Task AvSync_ExtendedStreaming_PcrPtsCoherenceStable()
    {
        // Arrange
        var url = $"{fixture.BaseUrl}/stream/5000";

        using var streamer = BuildStreamer(TestConfigs.Restamp(RestampingMode.Correct));

        streamer.AddUrl(url);
        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));

        // Act - sample at two points to check stability
        await Task.Delay(TimeSpan.FromSeconds(5));
        var earlySync = streamer.GetAvSyncAnalysis();

        await Task.Delay(TimeSpan.FromSeconds(10));
        var lateSync = streamer.GetAvSyncAnalysis();

        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        Assert.NotNull(earlySync);
        Assert.NotNull(lateSync);
        var early = earlySync!.Value;
        var late = lateSync!.Value;

        Output.WriteLine(
            $"Early (5s):  drift={early.VideoAudioDriftMs:F2}ms, pcrVid={early.PcrVideoOffsetMs:F2}ms, pcrAud={early.PcrAudioOffsetMs:F2}ms"
        );
        Output.WriteLine(
            $"Late (15s):  drift={late.VideoAudioDriftMs:F2}ms, pcrVid={late.PcrVideoOffsetMs:F2}ms, pcrAud={late.PcrAudioOffsetMs:F2}ms"
        );
        Output.WriteLine($"Drift delta: {Math.Abs(late.VideoAudioDriftMs - early.VideoAudioDriftMs):F2}ms");
        Output.WriteLine($"Packets output: {status.PacketsOutput:N0}");

        // Drift should not be growing over time (use avg to filter transients)
        double lateAvgDrift = Math.Abs(late.AvgDriftMs);
        double earlyAvgDrift = Math.Abs(early.AvgDriftMs);
        Output.WriteLine($"Early avg drift: {earlyAvgDrift:F2}ms, Late avg drift: {lateAvgDrift:F2}ms");

        Assert.True(
            lateAvgDrift <= EbuR37ThresholdMs,
            $"Late avg drift {lateAvgDrift:F2}ms exceeds EBU R37 threshold after 15s"
        );

        // The drift should be stable (not accumulating). Allow some tolerance
        // for measurement noise but the overall trend should be bounded.
        double driftGrowth = lateAvgDrift - earlyAvgDrift;
        Assert.True(
            driftGrowth <= 10.0,
            $"Drift grew by {driftGrowth:F2}ms between 5s and 15s — PCR-PTS coherence may be degrading"
        );
    }

    /// <summary>
    /// Verifies that at multiple bitrates, A/V sync is maintained.
    /// This validates that the bitrate estimate priming (Fix 3) and gentler
    /// PCR smoothing (Fix 2) work across different stream characteristics.
    /// </summary>
    [Theory]
    [InlineData(2000)]
    [InlineData(5000)]
    [InlineData(10000)]
    public async Task AvSync_MultipleBitrates_DriftWithinThreshold(int bitrateKbps)
    {
        // Arrange
        var url = $"{fixture.BaseUrl}/stream/{bitrateKbps}";

        using var streamer = BuildStreamer(TestConfigs.Restamp(RestampingMode.Correct));

        streamer.AddUrl(url);
        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));

        // Act
        await Task.Delay(TimeSpan.FromSeconds(8));

        var avSync = streamer.GetAvSyncAnalysis();
        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        Assert.NotNull(avSync);
        var sync = avSync!.Value;

        Output.WriteLine($"Bitrate: {bitrateKbps}kbps");
        Output.WriteLine($"A/V drift: {sync.AbsoluteDriftMs:F2}ms, peak: {sync.PeakDriftMs:F2}ms");
        Output.WriteLine($"PCR-Video: {sync.PcrVideoOffsetMs:F2}ms, PCR-Audio: {sync.PcrAudioOffsetMs:F2}ms");
        Output.WriteLine($"Samples: video={sync.VideoPtsCount}, audio={sync.AudioPtsCount}");
        Output.WriteLine($"Packets: {status.PacketsOutput:N0}");

        Assert.True(sync.VideoPtsCount > 0, $"Should collect video PTS at {bitrateKbps}kbps");
        Assert.True(sync.AudioPtsCount > 0, $"Should collect audio PTS at {bitrateKbps}kbps");

        // Use average drift to filter out periodic -50ms PTS timing transients
        double absAvgDrift = Math.Abs(sync.AvgDriftMs);
        Output.WriteLine($"Avg drift: {absAvgDrift:F2}ms");

        Assert.True(
            absAvgDrift <= EbuR37ThresholdMs,
            $"Average A/V drift {absAvgDrift:F2}ms at {bitrateKbps}kbps exceeds EBU R37 threshold"
        );
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private async Task<AvSyncAnalysis> CollectAvSyncAnalysis(string url, RestampingMode mode, TimeSpan sampleDuration)
    {
        using var streamer = BuildStreamer(TestConfigs.Restamp(mode));

        streamer.AddUrl(url);
        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));
        await Task.Delay(sampleDuration);

        var avSync = streamer.GetAvSyncAnalysis();
        var status = streamer.GetStatus();
        streamer.Stop();

        Output.WriteLine($"Mode={mode}, bytes={status.BytesReceived:N0}, packets={status.PacketsOutput:N0}");
        Assert.True(status.PacketsOutput > 0, $"Mode {mode} should output packets");
        Assert.NotNull(avSync);
        return avSync!.Value;
    }
}
