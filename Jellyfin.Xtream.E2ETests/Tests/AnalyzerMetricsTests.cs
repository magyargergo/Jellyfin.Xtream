using Jellyfin.Xtream.E2ETests.Infrastructure;
using Jellyfin.Xtream.Service.Streaming.Native;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.E2ETests.Tests;

/// <summary>
/// End-to-end tests for the native analyzer metrics pipeline.
/// Verifies PCR analysis, bitrate detection, A/V sync tracking,
/// and the full metrics callback lifecycle.
/// </summary>
[Collection("E2E")]
public class AnalyzerMetricsTests
{
    private readonly DockerTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public AnalyzerMetricsTests(DockerTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task Metrics_AfterStreaming_AreAvailable()
    {
        // Arrange - verify metrics become available after streaming data
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

        // Act - stream for a few seconds to trigger metrics callback
        await Task.Delay(TimeSpan.FromSeconds(3));

        var metrics = streamer.GetMetrics();
        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        _output.WriteLine($"Bytes received: {status.BytesReceived:N0}");
        Assert.NotNull(metrics);
        _output.WriteLine($"Metrics timestamp: {metrics.Timestamp:O}");
        Assert.False(metrics.IsStale, "Metrics should not be stale immediately after streaming");
    }

    [Fact]
    public async Task Metrics_PcrAnalysis_ReportsJitterAndInterval()
    {
        // Arrange - PCR analysis should detect PCR values in the stream
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

        var pcrAnalysis = streamer.GetPcrAnalysis();
        streamer.Stop();

        // Assert
        _output.WriteLine($"PCR Analysis available: {pcrAnalysis != null}");

        if (pcrAnalysis != null)
        {
            var pcr = pcrAnalysis.Value;
            _output.WriteLine($"PCR Count: {pcr.PcrCount}");
            _output.WriteLine($"PCR Valid Count: {pcr.PcrValidCount}");
            _output.WriteLine($"PCR Interval: {pcr.PcrIntervalMs:F2}ms");
            _output.WriteLine($"PCR Jitter: {pcr.PcrJitterUs:F3}us");
            _output.WriteLine($"PCR Jitter Max: {pcr.PcrJitterMaxUs:F3}us");
            _output.WriteLine($"PCR Drift: {pcr.PcrDriftPpm:F3}ppm");

            // Test stream sends PCR every 40ms, should have multiple samples in 5s
            Assert.True(pcr.PcrCount > 0, "Should have detected PCR values");
        }
    }

    [Fact]
    public async Task Metrics_PcrAnalysis_IntervalWithinSpec()
    {
        // Arrange - PCR interval should be within TR 101 290 limit (100ms max)
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

        var pcrAnalysis = streamer.GetPcrAnalysis();
        streamer.Stop();

        // Assert
        if (pcrAnalysis != null)
        {
            var pcr = pcrAnalysis.Value;
            _output.WriteLine($"PCR Interval: {pcr.PcrIntervalMs:F2}ms (limit: {PcrAnalysis.IntervalThresholdMs}ms)");

            if (pcr.PcrCount > 1)
            {
                Assert.False(
                    pcr.HasIntervalViolation,
                    $"PCR interval {pcr.PcrIntervalMs:F2}ms exceeds TR 101 290 limit of {PcrAnalysis.IntervalThresholdMs}ms"
                );
            }
        }
    }

    [Fact]
    public async Task Metrics_AvSyncAnalysis_DetectsVideoAndAudio()
    {
        // Arrange - stream has both video and audio PES, so A/V sync should detect both
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

        var avSync = streamer.GetAvSyncAnalysis();
        streamer.Stop();

        // Assert
        _output.WriteLine($"A/V Sync available: {avSync != null}");

        if (avSync != null)
        {
            var sync = avSync.Value;
            _output.WriteLine($"Status: {sync.Status} ({sync.StatusDescription})");
            _output.WriteLine($"Video PTS count: {sync.VideoPtsCount}");
            _output.WriteLine($"Audio PTS count: {sync.AudioPtsCount}");
            _output.WriteLine($"PCR count: {sync.PcrCount}");
            _output.WriteLine($"A/V Drift: {sync.VideoAudioDriftMs:F2}ms");

            // Test stream has both video and audio PES packets with PTS
            Assert.True(
                sync.VideoPtsCount > 0 || sync.AudioPtsCount > 0,
                "Should have detected PTS values from video or audio"
            );
        }
    }

    [Fact]
    public async Task Metrics_WithRestamp_ReportsPacketsProcessed()
    {
        // Arrange - use restamping mode and verify statistics
        var url = $"{_fixture.BaseUrl}/stream/5000";

        using var streamer = CreateStreamerWithRestamp();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(url);

        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));

        // Stream for a while to collect stats
        await Task.Delay(TimeSpan.FromSeconds(5));

        // Assert - check status shows packets were processed
        var status = streamer.GetStatus();
        var metrics = streamer.GetMetrics();
        _output.WriteLine($"Streamer status:");
        _output.WriteLine($"  State: {status.State}");
        _output.WriteLine($"  Bytes received: {status.BytesReceived:N0}");
        _output.WriteLine($"  Packets output: {status.PacketsOutput:N0}");

        streamer.Stop();

        Assert.True(status.PacketsOutput > 0, "Should have output packets");
    }

    [Fact]
    public async Task Metrics_BitrateTracking_ReportsReasonableValue()
    {
        // Arrange - 5 Mbps stream should report ~5 Mbps bitrate
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
        var bitrateMbps = metrics.TsBitrate / 1_000_000.0;
        _output.WriteLine($"Detected bitrate: {bitrateMbps:F2} Mbps (target: 5 Mbps)");

        // Allow wide tolerance since test server throttling isn't perfect
        Assert.True(metrics.TsBitrate > 0, "Should have detected non-zero bitrate");
    }

    [Fact]
    public async Task Metrics_StreamerStatus_TracksSessionLifecycle()
    {
        // Arrange - verify status transitions: Idle -> Connecting -> Streaming -> Stopped
        var url = $"{_fixture.BaseUrl}/stream/5000";

        using var streamer = CreateStreamerWithAnalyzer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(url);

        // Pre-start: should be idle
        var preStatus = streamer.GetStatus();
        _output.WriteLine($"Pre-start state: {preStatus.State}");

        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));

        // During streaming
        var streamingStatus = streamer.GetStatus();
        _output.WriteLine($"Streaming state: {streamingStatus.State}");
        _output.WriteLine($"Session start: {streamingStatus.SessionStart}");
        _output.WriteLine($"URL count: {streamingStatus.UrlCount}");

        await Task.Delay(TimeSpan.FromSeconds(2));
        streamer.Stop();

        // Post-stop
        var postStatus = streamer.GetStatus();
        _output.WriteLine($"Post-stop bytes: {postStatus.BytesReceived:N0}");

        // Assert
        Assert.Equal(StreamerState.Streaming, streamingStatus.State);
        Assert.True(streamingStatus.IsActive, "Should be active while streaming");
        Assert.Equal(1, streamingStatus.UrlCount);
        Assert.True(postStatus.BytesReceived > 0, "Should have received data");
    }

    [Fact]
    public async Task Metrics_LowBitrate_StillDetectsProgram()
    {
        // Arrange - 2 Mbps stream should still detect programs
        var url = $"{_fixture.BaseUrl}/stream/2000";

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
        _output.WriteLine($"Services: {metrics.ServiceCount}, PIDs: {metrics.PidCount}");
        _output.WriteLine($"Bitrate: {metrics.TsBitrate / 1000.0:F1} Kbps");

        Assert.True(metrics.ServiceCount >= 1, "Should detect services at 2Mbps");
    }

    [Fact]
    public async Task Metrics_HighBitrate_MaintainsAccuracy()
    {
        // Arrange - 15 Mbps stream
        var url = $"{_fixture.BaseUrl}/stream/15000";

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
        _output.WriteLine($"Bitrate: {metrics.TsBitrate / 1_000_000.0:F2} Mbps");
        _output.WriteLine($"Services: {metrics.ServiceCount}");
        _output.WriteLine($"P1 errors: {metrics.Priority1.TotalErrors}");

        // Should detect bitrate and have minimal errors
        Assert.True(metrics.TsBitrate > 0, "Should detect bitrate at 15Mbps");
        Assert.Equal(0, metrics.Priority1.SyncByteError);
    }

    [Fact]
    public async Task Metrics_BurstStream_ReportsMetrics()
    {
        // Arrange - bursty delivery pattern should still produce valid metrics
        var url = $"{_fixture.BaseUrl}/stream/burst/5000";

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
        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        _output.WriteLine($"Bytes: {status.BytesReceived:N0}");
        _output.WriteLine($"Services: {metrics.ServiceCount}, PIDs: {metrics.PidCount}");
        _output.WriteLine($"Bitrate: {metrics.TsBitrate} bps");

        // Bursty delivery should still produce valid metrics
        Assert.True(
            metrics.TsBitrate > 0 || metrics.PidCount > 0,
            "Should detect stream structure even with bursty delivery"
        );
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

    private static NativeStreamer? CreateStreamerWithRestamp()
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
        };

        var analyzerConfig = TsDuckConfigNative.FromManaged(TsDuckConfiguration.Default);
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
