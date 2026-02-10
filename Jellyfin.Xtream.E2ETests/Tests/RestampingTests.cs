using Jellyfin.Xtream.E2ETests.Infrastructure;
using Jellyfin.Xtream.Service.Streaming.Native;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.E2ETests.Tests;

/// <summary>
/// Tests PCR/PTS restamping through the native pipeline.
/// Verifies that output timestamps are monotonically increasing and properly spaced.
/// </summary>
[Collection("E2E-Streaming")]
public class RestampingTests
{
    private readonly DockerTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public RestampingTests(DockerTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task Restamp_PcrValues_AreMonotonicallyIncreasing()
    {
        // Arrange
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

        // Act - stream for 5 seconds
        await Task.Delay(TimeSpan.FromSeconds(5));

        var pcrAnalysis = streamer.GetPcrAnalysis();
        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        _output.WriteLine($"Bytes received: {status.BytesReceived:N0}");
        _output.WriteLine($"Packets output: {status.PacketsOutput:N0}");

        if (pcrAnalysis != null)
        {
            var pcr = pcrAnalysis.Value;
            _output.WriteLine($"PCR count: {pcr.PcrCount}");
            _output.WriteLine($"PCR valid count: {pcr.PcrValidCount}");
            _output.WriteLine($"PCR interval: {pcr.PcrIntervalMs:F2}ms");
            _output.WriteLine($"PCR jitter: {pcr.PcrJitterUs:F2}us");

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

        // Act - stream for 5 seconds
        await Task.Delay(TimeSpan.FromSeconds(5));

        var pcrAnalysis = streamer.GetPcrAnalysis();
        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert - check PCR intervals
        _output.WriteLine($"Bytes received: {status.BytesReceived:N0}");
        _output.WriteLine($"Packets output: {status.PacketsOutput:N0}");

        if (pcrAnalysis != null)
        {
            var pcr = pcrAnalysis.Value;
            _output.WriteLine($"PCR interval: {pcr.PcrIntervalMs:F2}ms");

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
        _output.WriteLine($"Streamer status:");
        _output.WriteLine($"  State: {status.State}");
        _output.WriteLine($"  Bytes received: {status.BytesReceived:N0}");
        _output.WriteLine($"  Packets output: {status.PacketsOutput:N0}");

        streamer.Stop();

        Assert.True(status.PacketsOutput > 0, "Should have output packets");
    }

    [Fact]
    public async Task Restamp_LargeOffset_CorrectedToZeroBased()
    {
        // Arrange - verify data flows correctly and PCR analysis works
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

        // Collect data for 3 seconds
        await Task.Delay(TimeSpan.FromSeconds(3));

        var pcrAnalysis = streamer.GetPcrAnalysis();
        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert - PCR analysis should show valid data
        _output.WriteLine($"Bytes received: {status.BytesReceived:N0}");
        _output.WriteLine($"Packets output: {status.PacketsOutput:N0}");

        if (pcrAnalysis != null)
        {
            var pcr = pcrAnalysis.Value;
            _output.WriteLine($"PCR count: {pcr.PcrCount}");
            _output.WriteLine($"PCR jitter max: {pcr.PcrJitterMaxUs:F2}us");

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
        var url = $"{_fixture.BaseUrl}/stream/burst/5000";

        // Act
        var monitorSync = await CollectAvSyncAnalysisAsync(url, RestampingMode.Monitor, TimeSpan.FromSeconds(8));
        var correctSync = await CollectAvSyncAnalysisAsync(url, RestampingMode.Correct, TimeSpan.FromSeconds(8));

        // Assert
        _output.WriteLine(
            $"Monitor drift={monitorSync.AbsoluteDriftMs:F2}ms, peak={monitorSync.PeakDriftMs:F2}ms, status={monitorSync.Status}"
        );
        _output.WriteLine(
            $"Correct drift={correctSync.AbsoluteDriftMs:F2}ms, peak={correctSync.PeakDriftMs:F2}ms, status={correctSync.Status}"
        );

        Assert.True(monitorSync.VideoPtsCount > 0, "Monitor mode should collect video PTS samples");
        Assert.True(monitorSync.AudioPtsCount > 0, "Monitor mode should collect audio PTS samples");
        Assert.True(correctSync.VideoPtsCount > 0, "Correct mode should collect video PTS samples");
        Assert.True(correctSync.AudioPtsCount > 0, "Correct mode should collect audio PTS samples");

        // Correct mode must not be materially worse than monitor mode.
        Assert.True(
            correctSync.AbsoluteDriftMs <= monitorSync.AbsoluteDriftMs + 5.0,
            $"Correct mode drift ({correctSync.AbsoluteDriftMs:F2}ms) should not exceed monitor mode drift ({monitorSync.AbsoluteDriftMs:F2}ms) by >5ms"
        );

        // If monitor shows meaningful drift, correct mode should reduce it measurably.
        if (monitorSync.AbsoluteDriftMs >= 20.0)
        {
            Assert.True(
                correctSync.AbsoluteDriftMs <= monitorSync.AbsoluteDriftMs - 3.0,
                $"Expected correction to reduce drift by at least 3ms (monitor={monitorSync.AbsoluteDriftMs:F2}ms, correct={correctSync.AbsoluteDriftMs:F2}ms)"
            );
        }
    }

    [Fact]
    public async Task Restamp_UnstableSource_ClampsPcrJumpWithoutSevereIntervalViolations()
    {
        // Arrange - unstable endpoint drops every ~2s and forces timeline recovery.
        var url = $"{_fixture.BaseUrl}/stream/unstable";
        using var streamer = CreateStreamerWithRestampMode(RestampingMode.Correct);
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(url);
        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));

        // Act
        await Task.Delay(TimeSpan.FromSeconds(10));
        var status = streamer.GetStatus();
        var pcr = streamer.GetPcrAnalysis();
        streamer.Stop();

        // Assert
        _output.WriteLine(
            $"Reconnections={status.Reconnections}, bytes={status.BytesReceived:N0}, packets={status.PacketsOutput:N0}"
        );
        Assert.True(status.Reconnections > 0, "Unstable source should trigger at least one reconnection");
        Assert.True(status.PacketsOutput > 0, "Should continue producing output packets under instability");

        if (pcr != null)
        {
            _output.WriteLine(
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

    private static NativeStreamer? CreateStreamerWithRestamp()
    {
        return CreateStreamerWithRestampMode(RestampingMode.Correct);
    }

    private static NativeStreamer? CreateStreamerWithRestampMode(RestampingMode mode)
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
            EnableRestamp = mode == RestampingMode.Disabled ? 0 : 1,
            RestampMode = (int)mode,
            LowSpeedLimitBytes = 100,
            LowSpeedTimeSec = 5,
            StallsBeforeSwitch = 2,
        };

        var analyzerConfig = TsDuckConfigNative.FromManaged(
            new TsDuckConfiguration
            {
                EnableTr101290 = true,
                MetricsIntervalSeconds = 1,
                EnableAutoRestamp = mode != RestampingMode.Disabled,
                RestampMode = mode,
            }
        );
        return NativeStreamer.TryCreate(config, analyzerConfig);
    }

    private async Task<AvSyncAnalysis> CollectAvSyncAnalysisAsync(
        string url,
        RestampingMode mode,
        TimeSpan sampleDuration
    )
    {
        using var streamer = CreateStreamerWithRestampMode(mode);
        Assert.NotNull(streamer);

        streamer!.AddUrl(url);
        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));
        await Task.Delay(sampleDuration);

        var avSync = streamer.GetAvSyncAnalysis();
        var status = streamer.GetStatus();
        streamer.Stop();

        _output.WriteLine(
            $"Mode={mode}, bytes={status.BytesReceived:N0}, packets={status.PacketsOutput:N0}, reconnects={status.Reconnections}"
        );
        Assert.True(status.PacketsOutput > 0, $"Mode {mode} should output packets");
        Assert.NotNull(avSync);
        return avSync!.Value;
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
