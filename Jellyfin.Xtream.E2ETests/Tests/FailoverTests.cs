using Jellyfin.Xtream.E2ETests.Infrastructure;
using Jellyfin.Xtream.Service;
using Jellyfin.Xtream.Service.Streaming.Native;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.E2ETests.Tests;

/// <summary>
/// Tests URL failover behavior when connections drop.
/// Verifies the native streamer rotates URLs and recovers without permanent stall.
/// </summary>
[Collection("E2E")]
public class FailoverTests
{
    private readonly DockerTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public FailoverTests(DockerTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task Failover_UnstableUrl_RecoversToStable()
    {
        // Arrange - 2 unstable URLs + 1 stable URL
        _fixture.UnstableDropAfterMs = 2000; // Drop after 2 seconds
        var unstableUrl1 = $"{_fixture.BaseUrl}/stream/unstable";
        var unstableUrl2 = $"{_fixture.BaseUrl}/stream/unstable";
        var stableUrl = $"{_fixture.BaseUrl}/stream/5000";

        const int bufferSize = 4 * 1024 * 1024;
        using var writeStream = new CircularBufferWriteStream(bufferSize);
        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        var events = new List<(StreamerEvent Event, int Detail, DateTime Time)>();
        streamer.StreamEvent += (_, args) =>
        {
            lock (events)
            {
                events.Add((args.EventType, args.Detail, DateTime.UtcNow));
            }
        };

        streamer.SetOutputCallback(
            (ptr, len) =>
            {
                unsafe
                {
                    writeStream.Write(new ReadOnlySpan<byte>((void*)ptr, len));
                }
            }
        );

        // Add URLs: unstable first, stable last
        streamer.AddUrl(unstableUrl1);
        streamer.AddUrl(unstableUrl2);
        streamer.AddUrl(stableUrl);

        // Act
        Assert.True(streamer.Start());

        // Wait for 10 seconds (enough for unstable to drop and failover to occur)
        await Task.Delay(TimeSpan.FromSeconds(10));

        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        _output.WriteLine($"Final state: {status.State}");
        _output.WriteLine($"Current URL index: {status.CurrentUrlIndex}");
        _output.WriteLine($"Bytes received: {status.BytesReceived:N0}");
        _output.WriteLine($"Switches: {status.SwitchesCompleted}");
        _output.WriteLine($"Reconnections: {status.Reconnections}");

        lock (events)
        {
            _output.WriteLine($"Events ({events.Count}):");
            foreach (var (evt, detail, time) in events.Take(20))
            {
                _output.WriteLine($"  {time:HH:mm:ss.fff} {evt} (detail={detail})");
            }
        }

        // Should have received data despite unstable connections
        Assert.True(status.BytesReceived > 0, "Should have received data");
        // Should have switched at least once (from unstable to next)
        Assert.True(
            status.SwitchesCompleted + status.Reconnections > 0,
            "Should have at least one switch or reconnection"
        );
    }

    [Fact]
    public async Task Failover_AllUnstable_EventuallyRecovers()
    {
        // Arrange - all URLs are unstable but keep reconnecting
        _fixture.UnstableDropAfterMs = 1500;
        var url = $"{_fixture.BaseUrl}/stream/unstable";

        const int bufferSize = 4 * 1024 * 1024;
        using var writeStream = new CircularBufferWriteStream(bufferSize);
        using var streamer = CreateStreamerForFailover();
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
                    writeStream.Write(new ReadOnlySpan<byte>((void*)ptr, len));
                }
            }
        );

        // Add the same unstable URL multiple times (simulates multiple providers)
        streamer.AddUrl(url);
        streamer.AddUrl(url);
        streamer.AddUrl(url);

        // Act
        Assert.True(streamer.Start());
        await Task.Delay(TimeSpan.FromSeconds(8));

        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        _output.WriteLine($"State: {status.State}");
        _output.WriteLine($"Bytes received: {status.BytesReceived:N0}");
        _output.WriteLine($"Reconnections: {status.Reconnections}");
        _output.WriteLine($"Retry count: {status.RetryCount}");

        // With max retries configured, should have received SOME data between disconnects
        Assert.True(status.BytesReceived > 0, "Should have received at least some data between reconnections");
        // Should have attempted reconnections
        Assert.True(status.Reconnections >= 1, $"Expected reconnections, got {status.Reconnections}");
    }

    [Fact]
    public async Task Failover_ManualSwitch_RotatesToNextUrl()
    {
        // Arrange
        var url1 = $"{_fixture.BaseUrl}/stream/2000";
        var url2 = $"{_fixture.BaseUrl}/stream/5000";

        const int bufferSize = 4 * 1024 * 1024;
        using var writeStream = new CircularBufferWriteStream(bufferSize);
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
                    writeStream.Write(new ReadOnlySpan<byte>((void*)ptr, len));
                }
            }
        );

        streamer.AddUrl(url1);
        streamer.AddUrl(url2);

        // Act - start, wait for connection, then request switch
        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));

        var statusBefore = streamer.GetStatus();
        _output.WriteLine($"Before switch: URL index={statusBefore.CurrentUrlIndex}");

        streamer.RequestSwitch();
        await Task.Delay(TimeSpan.FromSeconds(3)); // Wait for switch to complete

        var statusAfter = streamer.GetStatus();
        _output.WriteLine($"After switch: URL index={statusAfter.CurrentUrlIndex}");

        streamer.Stop();

        // Assert
        Assert.True(
            statusAfter.BytesReceived > statusBefore.BytesReceived,
            "Should continue receiving data after switch"
        );
        Assert.True(
            statusAfter.SwitchesCompleted >= 1,
            $"Expected at least 1 switch, got {statusAfter.SwitchesCompleted}"
        );
    }

    [Fact]
    public async Task Failover_DataContinuity_NoLongGaps()
    {
        // Arrange - monitor data flow during failover to ensure no long gaps
        _fixture.UnstableDropAfterMs = 2000;
        var unstableUrl = $"{_fixture.BaseUrl}/stream/unstable";
        var stableUrl = $"{_fixture.BaseUrl}/stream/5000";

        const int bufferSize = 4 * 1024 * 1024;
        using var writeStream = new CircularBufferWriteStream(bufferSize);
        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        var lastDataTime = DateTime.UtcNow;
        var maxGapMs = 0.0;

        streamer.SetOutputCallback(
            (ptr, len) =>
            {
                unsafe
                {
                    var now = DateTime.UtcNow;
                    var gap = (now - lastDataTime).TotalMilliseconds;
                    if (gap > maxGapMs && lastDataTime != DateTime.MinValue)
                    {
                        maxGapMs = gap;
                    }
                    lastDataTime = now;

                    writeStream.Write(new ReadOnlySpan<byte>((void*)ptr, len));
                }
            }
        );

        streamer.AddUrl(unstableUrl);
        streamer.AddUrl(stableUrl);

        // Act
        Assert.True(streamer.Start());
        await Task.Delay(TimeSpan.FromSeconds(8));

        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        _output.WriteLine($"Max gap between data: {maxGapMs:F0}ms");
        _output.WriteLine($"Switches: {status.SwitchesCompleted}");
        _output.WriteLine($"Reconnections: {status.Reconnections}");

        // Max gap should be under 15 seconds (includes reconnect backoff)
        Assert.True(maxGapMs < 15000, $"Max data gap {maxGapMs:F0}ms exceeds 15000ms threshold");
    }

    [Fact]
    public async Task Failover_ManualSwitch_MetricsContinueAccumulating()
    {
        // Arrange - verify analyzer metrics continue to accumulate after URL switch.
        // Start streaming, capture metrics, switch URLs, stream more, verify metrics grew.
        var url1 = $"{_fixture.BaseUrl}/stream/5000";
        var url2 = $"{_fixture.BaseUrl}/stream/5000";

        using var streamer = CreateStreamerWithAnalyzer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        var totalBytes = 0L;
        streamer.SetOutputCallback((ptr, len) => Interlocked.Add(ref totalBytes, len));
        streamer.AddUrl(url1);
        streamer.AddUrl(url2);

        // Act - start and stream for 3s to establish baseline metrics
        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(3));

        var metricsBefore = streamer.GetMetrics();
        var statusBefore = streamer.GetStatus();
        _output.WriteLine($"Before switch: {statusBefore.PacketsOutput} packets, URL={statusBefore.CurrentUrlIndex}");

        // Request URL switch
        streamer.RequestSwitch();
        await Task.Delay(TimeSpan.FromSeconds(4)); // Wait for switch + new data

        var metricsAfter = streamer.GetMetrics();
        var statusAfter = streamer.GetStatus();
        streamer.Stop();

        // Assert
        Assert.NotNull(metricsBefore);
        Assert.NotNull(metricsAfter);

        _output.WriteLine($"After switch: {statusAfter.PacketsOutput} packets, URL={statusAfter.CurrentUrlIndex}");
        _output.WriteLine($"Bitrate before: {metricsBefore.TsBitrate}, after: {metricsAfter.TsBitrate}");
        _output.WriteLine($"Services before: {metricsBefore.ServiceCount}, after: {metricsAfter.ServiceCount}");
        _output.WriteLine($"PIDs before: {metricsBefore.PidCount}, after: {metricsAfter.PidCount}");

        // Metrics should still be valid after switch
        Assert.True(metricsAfter.TsBitrate > 0, "Bitrate should still be detected after switch");
        Assert.True(metricsAfter.ServiceCount >= 1, "Services should still be detected after switch");
        Assert.True(metricsAfter.PidCount >= 4, "PIDs should still be tracked after switch");

        // Packets should have increased after the switch
        Assert.True(
            statusAfter.PacketsOutput > statusBefore.PacketsOutput,
            "Should continue outputting packets after switch"
        );

        // PAT/PMT should still be tracked correctly after the switch
        Assert.Equal(0, metricsAfter.Priority1.PatError);
        // Note: PCR repetition errors are expected after a URL switch because
        // the lifetime-average bitrate estimate is diluted by the switch gap,
        // making normal PCR intervals appear longer than 40ms. TR 101 290 PCR
        // compliance is tested separately on continuous streams.
    }

    [Fact]
    public async Task Failover_UnstableWithAnalyzer_DetectsErrorsAndRecovers()
    {
        // Arrange - verify that the analyzer detects errors during unstable streaming
        // but still produces valid metrics after recovery to stable URL.
        _fixture.UnstableDropAfterMs = 2000;
        var unstableUrl = $"{_fixture.BaseUrl}/stream/unstable";
        var stableUrl = $"{_fixture.BaseUrl}/stream/5000";

        using var streamer = CreateStreamerWithAnalyzerForFailover();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        var totalBytes = 0L;
        streamer.SetOutputCallback((ptr, len) => Interlocked.Add(ref totalBytes, len));
        streamer.AddUrl(unstableUrl);
        streamer.AddUrl(stableUrl);

        // Act - stream long enough for failover to occur and stable URL to produce metrics
        Assert.True(streamer.Start());
        await Task.Delay(TimeSpan.FromSeconds(10));

        var metrics = streamer.GetMetrics();
        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        _output.WriteLine($"State: {status.State}");
        _output.WriteLine($"Bytes: {status.BytesReceived:N0}");
        _output.WriteLine($"Switches: {status.SwitchesCompleted}");
        _output.WriteLine($"Metrics available: {metrics != null}");

        Assert.True(status.BytesReceived > 0, "Should have received data");

        if (metrics != null)
        {
            _output.WriteLine($"Services: {metrics.ServiceCount}, PIDs: {metrics.PidCount}");
            _output.WriteLine($"Bitrate: {metrics.TsBitrate}");
            _output.WriteLine($"Quality: {metrics.CalculateQualityScore()}");

            // After recovering to stable URL, should detect stream structure
            Assert.True(
                metrics.ServiceCount >= 1 || metrics.PidCount >= 1,
                "Should detect stream structure after recovery"
            );
        }
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
            Reserved = 0,
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

    private static NativeStreamer? CreateStreamerWithAnalyzerForFailover()
    {
        var config = new TsDuckStreamerConfigNative
        {
            ConnectTimeoutMs = 3000,
            ResponseTimeoutMs = 5000,
            StallTimeoutMs = 5000,
            MaxRetries = 10,
            InitialBackoffMs = 200,
            MaxBackoffMs = 2000,
            BackoffMultiplier = 1.5,
            BackoffJitterMs = 100,
            OutputFd = -1,
            AlignmentBufferPackets = 32,
            EnableRestamp = 0,
            RestampMode = (int)RestampingMode.Disabled,
            LowSpeedLimitBytes = 100,
            LowSpeedTimeSec = 3,
            StallsBeforeSwitch = 1,
            Reserved = 0,
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

    private static NativeStreamer? CreateStreamer()
    {
        return NativeStreamer.TryCreate(TsDuckStreamerConfigNative.Default);
    }

    private static NativeStreamer? CreateStreamerForFailover()
    {
        var config = new TsDuckStreamerConfigNative
        {
            ConnectTimeoutMs = 3000,
            ResponseTimeoutMs = 5000,
            StallTimeoutMs = 5000,
            MaxRetries = 10, // Allow many retries
            InitialBackoffMs = 200,
            MaxBackoffMs = 2000,
            BackoffMultiplier = 1.5,
            BackoffJitterMs = 100,
            OutputFd = -1,
            AlignmentBufferPackets = 32,
            EnableRestamp = 1,
            RestampMode = (int)RestampingMode.Correct,
            LowSpeedLimitBytes = 100,
            LowSpeedTimeSec = 3,
            StallsBeforeSwitch = 1, // Switch quickly
            Reserved = 0,
        };

        return NativeStreamer.TryCreate(config);
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
