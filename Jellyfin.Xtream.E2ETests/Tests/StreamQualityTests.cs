using Jellyfin.Xtream.E2ETests.Infrastructure;
using Jellyfin.Xtream.Service.Streaming.Native;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.E2ETests.Tests;

/// <summary>
/// TDD-style E2E tests for stream quality metrics accuracy.
/// Tests bitrate estimation, continuity counter tracking, PCR accuracy,
/// delayed startup, and rapid failover scenarios.
/// </summary>
[Collection("E2E")]
public class StreamQualityTests
{
    private readonly DockerTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public StreamQualityTests(DockerTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

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
        var url = $"{_fixture.BaseUrl}/stream/{bitrateKbps}";

        using var streamer = CreateStreamerWithAnalyzer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(url);

        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(5));

        var metrics = streamer.GetMetrics();
        streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        var targetBps = bitrateKbps * 1000L;
        var estimatedBps = metrics.TsBitrate;
        var ratio = (double)estimatedBps / targetBps;

        _output.WriteLine($"Target: {targetBps} bps, Estimated: {estimatedBps} bps, Ratio: {ratio:F3}");

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
        var url = $"{_fixture.BaseUrl}/stream/corrupted/5000";

        using var streamer = CreateStreamerWithAnalyzer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(url);

        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(5));

        var metrics = streamer.GetMetrics();
        streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        _output.WriteLine($"CC errors: {metrics.Priority1.ContinuityCountError}");
        _output.WriteLine($"Transport errors: {metrics.Priority2.TransportError}");
        _output.WriteLine($"Total P1: {metrics.Priority1.TotalErrors}, P2: {metrics.Priority2.TotalErrors}");

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
        await Task.Delay(TimeSpan.FromSeconds(5));

        var metrics = streamer.GetMetrics();
        streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        _output.WriteLine($"CC errors: {metrics.Priority1.ContinuityCountError}");

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
        await Task.Delay(TimeSpan.FromSeconds(5));

        var metrics = streamer.GetMetrics();
        streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        _output.WriteLine($"PCR accuracy errors: {metrics.Priority2.PcrAccuracyError}");
        _output.WriteLine($"PCR discontinuity errors: {metrics.Priority2.PcrDiscontinuityError}");

        // Clean stream should have zero PCR accuracy errors
        Assert.Equal(0, metrics.Priority2.PcrAccuracyError);
        // Clean stream should have zero PCR discontinuity errors
        Assert.Equal(0, metrics.Priority2.PcrDiscontinuityError);
    }

    [Fact]
    public async Task PcrAccuracy_HighBitrate_NoAccuracyErrors()
    {
        // Arrange - same check at 10 Mbps to verify accuracy scales with bitrate
        var url = $"{_fixture.BaseUrl}/stream/10000";

        using var streamer = CreateStreamerWithAnalyzer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(url);

        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(5));

        var metrics = streamer.GetMetrics();
        streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        _output.WriteLine($"PCR accuracy errors: {metrics.Priority2.PcrAccuracyError}");
        _output.WriteLine($"Bitrate: {metrics.TsBitrate / 1_000_000.0:F2} Mbps");

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
        var url = $"{_fixture.BaseUrl}/stream/delayed/2000";

        using var streamer = CreateStreamerWithAnalyzer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(url);

        Assert.True(streamer.Start());
        // Wait longer than the delay + connection time
        await Task.Delay(TimeSpan.FromSeconds(8));

        var metrics = streamer.GetMetrics();
        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        _output.WriteLine($"Total bytes: {status.BytesReceived:N0}");
        _output.WriteLine($"Services: {metrics.ServiceCount}, PIDs: {metrics.PidCount}");
        _output.WriteLine($"PAT errors: {metrics.Priority1.PatError}");

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
        var url1 = $"{_fixture.BaseUrl}/stream/5000";
        var url2 = $"{_fixture.BaseUrl}/stream/5000";

        using var streamer = CreateStreamerWithAnalyzer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(url1);
        streamer.AddUrl(url2);

        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Rapid switches: 3 switches with 500ms between each
        for (int i = 0; i < 3; i++)
        {
            streamer.RequestSwitch();
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        // Let it stabilize
        await Task.Delay(TimeSpan.FromSeconds(3));

        var metrics = streamer.GetMetrics();
        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        _output.WriteLine($"Final state: {status.State}");
        _output.WriteLine($"Packets output: {status.PacketsOutput}");
        _output.WriteLine($"Services: {metrics.ServiceCount}, PIDs: {metrics.PidCount}");
        _output.WriteLine($"Bitrate: {metrics.TsBitrate / 1000.0:F1} Kbps");
        _output.WriteLine($"P1 errors: {metrics.Priority1.TotalErrors}");

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
        var url = $"{_fixture.BaseUrl}/stream/{bitrateKbps}";

        using var streamer = CreateStreamerWithAnalyzer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(url);

        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(5));

        var metrics = streamer.GetMetrics();
        streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        var qualityScore = metrics.CalculateQualityScore();
        _output.WriteLine($"Bitrate: {bitrateKbps} Kbps -> Quality: {qualityScore}/100");
        _output.WriteLine($"P1: {metrics.Priority1.TotalErrors}, P2: {metrics.Priority2.TotalErrors}");
        _output.WriteLine($"PAT: {metrics.Priority1.PatError}, PMT: {metrics.Priority1.PmtError}");
        _output.WriteLine(
            $"PCR rep: {metrics.Priority2.PcrRepetitionError}, acc: {metrics.Priority2.PcrAccuracyError}"
        );

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
        await Task.Delay(TimeSpan.FromSeconds(5));

        var metrics = streamer.GetMetrics();
        streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        _output.WriteLine($"PIDs detected: {metrics.PidCount}");
        _output.WriteLine($"Services: {metrics.ServiceCount}");

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
        _fixture.UnstableDropAfterMs = 2000;
        var unstableUrl = $"{_fixture.BaseUrl}/stream/unstable";
        var stableUrl = $"{_fixture.BaseUrl}/stream/5000";

        using var streamer = CreateStreamerWithAnalyzer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(unstableUrl);
        streamer.AddUrl(stableUrl);

        Assert.True(streamer.Start());
        // Wait long enough for: connect + drop + backoff + reconnect to stable + data
        await Task.Delay(TimeSpan.FromSeconds(10));

        var metrics = streamer.GetMetrics();
        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        _output.WriteLine($"State: {status.State}, URL index: {status.CurrentUrlIndex}");
        _output.WriteLine($"Total bytes: {status.BytesReceived:N0}");
        _output.WriteLine($"Bitrate: {metrics.TsBitrate / 1000.0:F1} Kbps");
        _output.WriteLine($"Services: {metrics.ServiceCount}");
        _output.WriteLine($"PAT errors: {metrics.Priority1.PatError}");
        _output.WriteLine($"Sync errors: {metrics.Priority1.SyncByteError}");

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
}
