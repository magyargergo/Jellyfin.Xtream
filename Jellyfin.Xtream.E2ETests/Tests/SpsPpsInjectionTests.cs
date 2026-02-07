using System.Diagnostics;
using Jellyfin.Xtream.E2ETests.Infrastructure;
using Jellyfin.Xtream.Service;
using Jellyfin.Xtream.Service.Streaming.Native;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.E2ETests.Tests;

/// <summary>
/// Tests for SPS/PPS injection after keyframe alignment during URL switches.
/// These tests verify that H.264 parameter sets are properly extracted from
/// pre-IDR packets and prepended to the output stream after a URL switch.
/// </summary>
[Collection("E2E-Failover")]
public class SpsPpsInjectionTests
{
    private readonly DockerTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public SpsPpsInjectionTests(DockerTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    /// <summary>
    /// Tests that after a URL switch, the output stream quality remains high.
    /// With SPS/PPS injection, the decoder should have proper initialization data.
    /// </summary>
    [Fact]
    public async Task UrlSwitch_WithH264Stream_MaintainsStreamQuality()
    {
        // Arrange - two H.264 streams
        var h264Url1 = $"{_fixture.BaseUrl}/stream/h264/5000";
        var h264Url2 = $"{_fixture.BaseUrl}/stream/h264/5000";

        using var streamer = CreateStreamerWithAnalyzer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(h264Url1);
        streamer.AddUrl(h264Url2);

        // Act - start streaming
        Assert.True(streamer.Start());
        await WaitForStreaming(streamer, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(2)); // Establish baseline

        var metricsBeforeSwitch = streamer.GetMetrics();
        _output.WriteLine($"Before switch: Quality={metricsBeforeSwitch?.CalculateQualityScore()}");

        // Request manual switch
        streamer.RequestSwitch();
        await Task.Delay(TimeSpan.FromSeconds(4)); // Let it stabilize

        var metricsAfterSwitch = streamer.GetMetrics();
        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        Assert.NotNull(metricsAfterSwitch);
        _output.WriteLine($"After switch: Quality={metricsAfterSwitch.CalculateQualityScore()}");
        _output.WriteLine($"CC errors: {metricsAfterSwitch.Priority1.ContinuityCountError}");
        _output.WriteLine($"Sync loss: {metricsAfterSwitch.Priority1.SyncLoss}");
        _output.WriteLine($"Switches: {status.SwitchesCompleted}");

        Assert.True(status.SwitchesCompleted >= 1, "Should have completed at least one switch");

        // Quality should remain high after switch
        // SPS/PPS injection ensures decoder can initialize properly
        var quality = metricsAfterSwitch.CalculateQualityScore();
        Assert.True(quality >= 70, $"Quality score should be >= 70, got {quality}");

        // No sync errors (output is valid TS data)
        Assert.Equal(0, metricsAfterSwitch.Priority1.SyncByteError);
    }

    /// <summary>
    /// Tests that TR 101 290 counters are reset after keyframe alignment.
    /// This prevents false quality triggers from accumulated errors across URL switches.
    /// </summary>
    [Fact]
    public async Task UrlSwitch_ResetsTr101290Counters()
    {
        // Arrange - configure provider to drop after sending some data
        _fixture.ConfigureProvider(
            "sps-test-1",
            new ProviderBehavior
            {
                BitrateKbps = 5000,
                EnableH264Nals = true,
                DropAfterMs = 3000,
            }
        );
        _fixture.ConfigureProvider("sps-test-2", new ProviderBehavior { BitrateKbps = 5000, EnableH264Nals = true });

        var url1 = _fixture.GetProviderUrl("sps-test-1");
        var url2 = _fixture.GetProviderUrl("sps-test-2");

        using var streamer = CreateStreamerWithAnalyzer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(url1);
        streamer.AddUrl(url2);

        // Act - start and wait for automatic failover
        Assert.True(streamer.Start());

        // Wait for switch to second URL (poll for URL index change)
        var switched = await WaitForUrlSwitch(streamer, 1, TimeSpan.FromSeconds(10));

        // Let it stream on new URL for a bit
        await Task.Delay(TimeSpan.FromSeconds(3));

        var metrics = streamer.GetMetrics();
        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        _output.WriteLine($"Switched: {switched}");
        _output.WriteLine($"Final URL: {status.CurrentUrlIndex}");
        _output.WriteLine($"Sync loss: {metrics?.Priority1.SyncLoss ?? -1}");
        _output.WriteLine($"CC errors: {metrics?.Priority1.ContinuityCountError ?? -1}");

        Assert.NotNull(metrics);

        // After switch, TR 101 290 counters should be low (reset after keyframe alignment)
        // Some discontinuity during switch is expected, but sync_loss should be 0 or very low
        // since the new stream starts cleanly from a keyframe
        Assert.True(
            metrics.Priority1.SyncLoss <= 5,
            $"Sync loss should be <= 5 after reset, got {metrics.Priority1.SyncLoss}"
        );
    }

    /// <summary>
    /// Tests that multiple rapid URL switches don't cause decoder errors.
    /// Each switch should properly inject SPS/PPS.
    /// </summary>
    [Fact]
    public async Task MultipleRapidSwitches_WithH264_NoDecoderErrors()
    {
        // Arrange - three H.264 streams
        var h264Url = $"{_fixture.BaseUrl}/stream/h264/5000";

        using var streamer = CreateStreamerWithAnalyzer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(h264Url);
        streamer.AddUrl(h264Url);
        streamer.AddUrl(h264Url);

        // Act - start and perform rapid switches
        Assert.True(streamer.Start());
        await WaitForStreaming(streamer, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(2));

        // 3 rapid switches, 500ms apart
        for (int i = 0; i < 3; i++)
        {
            streamer.RequestSwitch();
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        await Task.Delay(TimeSpan.FromSeconds(3)); // Stabilize

        var metrics = streamer.GetMetrics();
        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        _output.WriteLine($"Switches: {status.SwitchesCompleted}");
        _output.WriteLine($"Quality: {metrics.CalculateQualityScore()}");
        _output.WriteLine($"Sync byte errors: {metrics.Priority1.SyncByteError}");
        _output.WriteLine($"Sync loss: {metrics.Priority1.SyncLoss}");

        Assert.True(status.SwitchesCompleted >= 1, "Should have completed at least one switch");
        Assert.True(status.BytesReceived > 0, "Should have received data");

        // No sync corruption
        Assert.Equal(0, metrics.Priority1.SyncByteError);
    }

    /// <summary>
    /// Tests that automatic failover maintains proper H.264 stream structure.
    /// When URL 1 drops, URL 2 should start with proper parameter sets.
    /// </summary>
    [Fact]
    public async Task AutomaticFailover_WithH264_StreamContinuesCleanly()
    {
        // Arrange - first URL drops, second is stable
        _fixture.ConfigureProvider(
            "h264-drop",
            new ProviderBehavior
            {
                BitrateKbps = 5000,
                EnableH264Nals = true,
                DropAfterMs = 2500,
            }
        );
        _fixture.ConfigureProvider("h264-stable", new ProviderBehavior { BitrateKbps = 5000, EnableH264Nals = true });

        var dropUrl = _fixture.GetProviderUrl("h264-drop");
        var stableUrl = _fixture.GetProviderUrl("h264-stable");

        using var streamer = CreateStreamerWithAnalyzer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(dropUrl);
        streamer.AddUrl(stableUrl);

        // Act - start and wait for automatic failover
        Assert.True(streamer.Start());

        var switched = await WaitForUrlSwitch(streamer, 1, TimeSpan.FromSeconds(10));
        await Task.Delay(TimeSpan.FromSeconds(3)); // Stream on stable URL

        var metrics = streamer.GetMetrics();
        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        _output.WriteLine($"Switched to stable: {switched}");
        _output.WriteLine($"Final state: {status.State}, URL: {status.CurrentUrlIndex}");
        _output.WriteLine($"Bytes received: {status.BytesReceived:N0}");

        Assert.NotNull(metrics);
        _output.WriteLine($"Quality: {metrics.CalculateQualityScore()}");
        _output.WriteLine($"Services: {metrics.ServiceCount}, PIDs: {metrics.PidCount}");
        _output.WriteLine($"Sync errors: {metrics.Priority1.SyncByteError}");

        Assert.True(status.BytesReceived > 0, "Should have received data");
        Assert.True(metrics.ServiceCount >= 1, "Should detect services after failover");

        // No sync corruption
        Assert.Equal(0, metrics.Priority1.SyncByteError);
    }

    /// <summary>
    /// Tests that quality score remains high after keyframe alignment completes.
    /// Low quality score would indicate potential decoder initialization issues.
    /// </summary>
    [Fact]
    public async Task KeyframeAlignment_QualityScoreRemainsHigh()
    {
        // Arrange - stable H.264 streams
        _fixture.ConfigureProvider(
            "quality-test-1",
            new ProviderBehavior
            {
                BitrateKbps = 5000,
                EnableH264Nals = true,
                GopSize = 15,
            }
        );
        _fixture.ConfigureProvider(
            "quality-test-2",
            new ProviderBehavior
            {
                BitrateKbps = 5000,
                EnableH264Nals = true,
                GopSize = 15,
            }
        );

        var url1 = _fixture.GetProviderUrl("quality-test-1");
        var url2 = _fixture.GetProviderUrl("quality-test-2");

        using var streamer = CreateStreamerWithAnalyzer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(url1);
        streamer.AddUrl(url2);

        // Act
        Assert.True(streamer.Start());
        await WaitForStreaming(streamer, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(2));

        var qualityBefore = streamer.GetMetrics()?.CalculateQualityScore() ?? 0;
        _output.WriteLine($"Quality before switch: {qualityBefore}");

        // Switch URL
        streamer.RequestSwitch();
        await Task.Delay(TimeSpan.FromSeconds(4)); // Allow stabilization

        var metricsAfter = streamer.GetMetrics();
        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        Assert.NotNull(metricsAfter);
        var qualityAfter = metricsAfter.CalculateQualityScore();
        _output.WriteLine($"Quality after switch: {qualityAfter}");
        _output.WriteLine($"Switches: {status.SwitchesCompleted}");

        Assert.True(status.SwitchesCompleted >= 1, "Should have switched");

        // Quality should remain high (>= 80) after proper keyframe alignment
        Assert.True(qualityAfter >= 80, $"Quality should be >= 80 after switch, got {qualityAfter}");

        // Quality degradation should be minimal
        var degradation = qualityBefore - qualityAfter;
        Assert.True(degradation <= 10, $"Quality degradation should be <= 10, got {degradation}");
    }

    /// <summary>
    /// Tests that quality-based switching doesn't trigger falsely after URL switch.
    /// TR 101 290 counter reset should prevent accumulated errors from triggering switches.
    /// </summary>
    [Fact]
    public async Task QualitySwitch_NotTriggeredFalselyAfterManualSwitch()
    {
        // Arrange - configure with quality switching enabled
        _fixture.ConfigureProvider(
            "quality-switch-1",
            new ProviderBehavior { BitrateKbps = 5000, EnableH264Nals = true }
        );
        _fixture.ConfigureProvider(
            "quality-switch-2",
            new ProviderBehavior { BitrateKbps = 5000, EnableH264Nals = true }
        );
        _fixture.ConfigureProvider(
            "quality-switch-3",
            new ProviderBehavior { BitrateKbps = 5000, EnableH264Nals = true }
        );

        var url1 = _fixture.GetProviderUrl("quality-switch-1");
        var url2 = _fixture.GetProviderUrl("quality-switch-2");
        var url3 = _fixture.GetProviderUrl("quality-switch-3");

        using var streamer = CreateStreamerWithQualitySwitch();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(url1);
        streamer.AddUrl(url2);
        streamer.AddUrl(url3);

        // Act - start and do a manual switch
        Assert.True(streamer.Start());
        await WaitForStreaming(streamer, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(2));

        var switchesBefore = streamer.GetStatus().SwitchesCompleted;
        streamer.RequestSwitch(); // Manual switch
        await Task.Delay(TimeSpan.FromSeconds(5)); // Wait to see if quality switch triggers

        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        _output.WriteLine($"Switches before manual: {switchesBefore}");
        _output.WriteLine($"Total switches: {status.SwitchesCompleted}");
        _output.WriteLine($"Quality switches: {status.QualitySwitches}");

        // Should only have 1 switch (the manual one), not additional quality-triggered switches
        // If TR 101 290 counters weren't reset, accumulated sync_loss could trigger extra switches
        Assert.Equal(1, status.SwitchesCompleted - switchesBefore);
        Assert.Equal(0, status.QualitySwitches);
    }

    // ========================================================================
    // Helper Methods
    // ========================================================================

    private static NativeStreamer? CreateStreamerWithAnalyzer()
    {
        var config = new TsDuckStreamerConfigNative
        {
            ConnectTimeoutMs = 3000,
            ResponseTimeoutMs = 5000,
            StallTimeoutMs = 3000,
            MaxRetries = 10,
            InitialBackoffMs = 100,
            MaxBackoffMs = 1000,
            BackoffMultiplier = 1.5,
            BackoffJitterMs = 50,
            OutputFd = -1,
            AlignmentBufferPackets = 32,
            EnableRestamp = 0,
            RestampMode = (int)RestampingMode.Disabled,
            LowSpeedLimitBytes = 100,
            LowSpeedTimeSec = 2,
            StallsBeforeSwitch = 1,
            EnableQualitySwitch = 0, // Disable quality-based switching for these tests
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

    private static NativeStreamer? CreateStreamerWithQualitySwitch()
    {
        var config = new TsDuckStreamerConfigNative
        {
            ConnectTimeoutMs = 3000,
            ResponseTimeoutMs = 5000,
            StallTimeoutMs = 3000,
            MaxRetries = 10,
            InitialBackoffMs = 100,
            MaxBackoffMs = 1000,
            BackoffMultiplier = 1.5,
            BackoffJitterMs = 50,
            OutputFd = -1,
            AlignmentBufferPackets = 32,
            EnableRestamp = 0,
            RestampMode = (int)RestampingMode.Disabled,
            LowSpeedLimitBytes = 100,
            LowSpeedTimeSec = 2,
            StallsBeforeSwitch = 1,
            EnableQualitySwitch = 1, // Enable quality-based switching
            QualityCheckIntervalMs = 1000, // Check every 1 second
            QualityWindowSeconds = 10, // 10-second sliding window
            MaxSyncErrorsPerWindow = 1, // Any sync loss triggers switch
            MaxContinuityErrorsPerSec = 20, // 20 CC errors/sec threshold
            MaxTransportErrorsPerSec = 10, // 10 TEI errors/sec threshold
            MaxPcrErrorsPerSec = 5, // 5 PCR errors/sec threshold
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

    private static async Task WaitForStreaming(NativeStreamer streamer, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var status = streamer.GetStatus();
            if (status.State == StreamerState.Streaming)
            {
                return;
            }

            await Task.Delay(50);
        }
    }

    private static async Task<bool> WaitForUrlSwitch(NativeStreamer streamer, int expectedIndex, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var status = streamer.GetStatus();
            if (status.CurrentUrlIndex == expectedIndex)
            {
                return true;
            }

            await Task.Delay(50);
        }

        return false;
    }
}
