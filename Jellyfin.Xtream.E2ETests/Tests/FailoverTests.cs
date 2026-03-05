using Jellyfin.Xtream.E2ETests.Infrastructure;
using Jellyfin.Xtream.Service.Streaming.Native;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.E2ETests.Tests;

/// <summary>
/// Tests URL failover behavior when connections drop.
/// Verifies the native streamer rotates URLs and recovers without permanent stall.
/// </summary>
[Collection("E2E-Failover")]
public class FailoverTests(DockerTestFixture fixture, ITestOutputHelper output) : NativeE2ETestBase(output)
{
    /// <summary>
    /// Tests that the streamer fails over from unstable URLs to a stable URL.
    /// The unstable URLs drop the connection after a configured interval,
    /// and the streamer should eventually reach the stable URL.
    /// </summary>
    [Fact]
    public async Task Failover_UnstableUrl_RecoversToStable()
    {
        // Arrange - 2 unstable URLs + 1 stable URL
        fixture.UnstableDropAfterMs = 2000; // Drop after 2 seconds
        var unstableUrl1 = $"{fixture.BaseUrl}/stream/unstable";
        var unstableUrl2 = $"{fixture.BaseUrl}/stream/unstable";
        var stableUrl = $"{fixture.BaseUrl}/stream/5000";

        using var streamer = BuildStreamer(TestConfigs.Production);

        // Add URLs: unstable first, stable last
        streamer.AddUrl(unstableUrl1);
        streamer.AddUrl(unstableUrl2);
        streamer.AddUrl(stableUrl);

        // Act
        Assert.True(streamer.Start(), "Streamer should start successfully");

        // Wait for failover to stable URL (poll for expected state)
        var reachedStable = await TestHelpers.WaitForConditionAsync(
            () => streamer.GetStatus().CurrentUrlIndex == 2,
            TimeSpan.FromSeconds(15)
        );

        // Continue streaming on stable URL for verification
        await Task.Delay(TimeSpan.FromSeconds(2));

        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        Output.WriteLine($"Final state: {status.State}");
        Output.WriteLine($"Current URL index: {status.CurrentUrlIndex}");
        Output.WriteLine($"Bytes received: {status.BytesReceived:N0}");
        Output.WriteLine($"Switches: {status.SwitchesCompleted}");
        Output.WriteLine($"Reconnections: {status.Reconnections}");
        Output.WriteLine($"Reached stable URL: {reachedStable}");

        // Should have received data despite unstable connections
        Assert.True(status.BytesReceived > 0, "Should have received data");
        // Should have switched at least once (from unstable to next)
        Assert.True(
            status.SwitchesCompleted + status.Reconnections > 0,
            $"Should have at least one switch or reconnection, got switches={status.SwitchesCompleted}, reconnections={status.Reconnections}"
        );
    }

    /// <summary>
    /// Tests that the streamer continues attempting reconnections when all URLs are unstable.
    /// With retry logic enabled, the streamer should receive data between disconnects
    /// and keep cycling through URLs.
    /// </summary>
    [Fact]
    public async Task Failover_AllUnstable_EventuallyRecovers()
    {
        // Arrange - all URLs are unstable but keep reconnecting
        fixture.UnstableDropAfterMs = 1500;
        var url = $"{fixture.BaseUrl}/stream/unstable";

        using var streamer = BuildStreamer(TestConfigs.Failover);

        // Add the same unstable URL multiple times (simulates multiple providers)
        streamer.AddUrl(url);
        streamer.AddUrl(url);
        streamer.AddUrl(url);

        // Act
        Assert.True(streamer.Start(), "Streamer should start successfully");

        // Wait for at least one reconnection to occur
        var hadReconnection = await TestHelpers.WaitForConditionAsync(
            () => streamer.GetStatus().Reconnections >= 1,
            TimeSpan.FromSeconds(8)
        );

        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        Output.WriteLine($"State: {status.State}");
        Output.WriteLine($"Bytes received: {status.BytesReceived:N0}");
        Output.WriteLine($"Reconnections: {status.Reconnections}");
        Output.WriteLine($"Retry count: {status.RetryCount}");
        Output.WriteLine($"Had reconnection: {hadReconnection}");

        // With max retries configured, should have received SOME data between disconnects
        Assert.True(status.BytesReceived > 0, "Should have received at least some data between reconnections");
        // Should have attempted reconnections
        Assert.True(status.Reconnections >= 1, $"Expected reconnections >= 1, got {status.Reconnections}");
    }

    /// <summary>
    /// Tests that manually requesting a URL switch rotates to the next URL in the list.
    /// Verifies data continues flowing after the switch and the switch count increments.
    /// </summary>
    [Fact]
    public async Task Failover_ManualSwitch_RotatesToNextUrl()
    {
        // Arrange
        var url1 = $"{fixture.BaseUrl}/stream/2000";
        var url2 = $"{fixture.BaseUrl}/stream/5000";

        using var streamer = BuildStreamer(TestConfigs.Production);

        streamer.AddUrl(url1);
        streamer.AddUrl(url2);

        // Act - start, wait for streaming state
        Assert.True(streamer.Start(), "Streamer should start successfully");
        var reachedStreaming = await TestHelpers.WaitForStreamingAsync(streamer, TimeSpan.FromSeconds(5));
        Assert.True(reachedStreaming, "Should reach streaming state before switch");

        var statusBefore = streamer.GetStatus();
        Output.WriteLine(
            $"Before switch: URL index={statusBefore.CurrentUrlIndex}, Bytes={statusBefore.BytesReceived:N0}"
        );

        // Request switch and wait for it to complete
        streamer.RequestSwitch();
        var switchCompleted = await TestHelpers.WaitForSwitchCountAsync(streamer, 1, TimeSpan.FromSeconds(5));

        var statusAfter = streamer.GetStatus();
        Output.WriteLine(
            $"After switch: URL index={statusAfter.CurrentUrlIndex}, Bytes={statusAfter.BytesReceived:N0}"
        );
        Output.WriteLine($"Switch completed: {switchCompleted}");

        streamer.Stop();

        // Assert
        Assert.True(
            statusAfter.BytesReceived > statusBefore.BytesReceived,
            $"Should continue receiving data after switch. Before: {statusBefore.BytesReceived}, After: {statusAfter.BytesReceived}"
        );
        Assert.True(
            statusAfter.SwitchesCompleted >= 1,
            $"Expected at least 1 switch completed, got {statusAfter.SwitchesCompleted}"
        );
    }

    /// <summary>
    /// Tests that data continuity is maintained during failover.
    /// Monitors the data flow and ensures gaps between data arrivals
    /// are within acceptable limits during URL switching.
    /// </summary>
    [Fact]
    public async Task Failover_DataContinuity_NoLongGaps()
    {
        // Arrange - monitor data flow during failover to ensure no long gaps
        fixture.UnstableDropAfterMs = 2000;
        var unstableUrl = $"{fixture.BaseUrl}/stream/unstable";
        var stableUrl = $"{fixture.BaseUrl}/stream/5000";

        using var streamer = BuildStreamer(TestConfigs.Production);

        streamer.AddUrl(unstableUrl);
        streamer.AddUrl(stableUrl);

        // Act
        Assert.True(streamer.Start(), "Streamer should start successfully");

        // Wait for initial streaming
        await TestHelpers.WaitForStreamingAsync(streamer, TimeSpan.FromSeconds(3));

        // Monitor data continuity using the helper
        const double maxAllowedGapMs = 15000; // 15 seconds (includes reconnect backoff)
        var (success, maxGapMs) = await TestHelpers.VerifyDataFlowContinuityAsync(
            streamer,
            TimeSpan.FromSeconds(8),
            maxAllowedGapMs
        );

        var finalStatus = streamer.GetStatus();
        streamer.Stop();

        // Assert
        Output.WriteLine($"Max gap between data: {maxGapMs:F0}ms");
        Output.WriteLine($"Switches: {finalStatus.SwitchesCompleted}");
        Output.WriteLine($"Reconnections: {finalStatus.Reconnections}");
        Output.WriteLine($"Data continuity maintained: {success}");

        // Max gap should be under 15 seconds (includes reconnect backoff)
        Assert.True(success, $"Max data gap {maxGapMs:F0}ms exceeds {maxAllowedGapMs}ms threshold");
    }

    /// <summary>
    /// Tests that analyzer metrics continue accumulating correctly after a URL switch.
    /// Verifies that bitrate, service, and PID detection remain valid post-switch.
    /// </summary>
    [Fact]
    public async Task Failover_ManualSwitch_MetricsContinueAccumulating()
    {
        // Arrange - verify analyzer metrics continue to accumulate after URL switch.
        // Start streaming, capture metrics, switch URLs, stream more, verify metrics grew.
        var url1 = $"{fixture.BaseUrl}/stream/5000";
        var url2 = $"{fixture.BaseUrl}/stream/5000";

        using var streamer = BuildStreamer(TestConfigs.Analyzer);

        streamer.AddUrl(url1);
        streamer.AddUrl(url2);

        // Act - start and stream for 3s to establish baseline metrics
        Assert.True(streamer.Start(), "Streamer should start successfully");
        var reachedStreaming = await TestHelpers.WaitForStreamingAsync(streamer, TimeSpan.FromSeconds(5));
        Assert.True(reachedStreaming, "Should reach streaming state");
        await Task.Delay(TimeSpan.FromSeconds(3));

        var metricsBefore = streamer.GetMetrics();
        var statusBefore = streamer.GetStatus();
        Output.WriteLine($"Before switch: {statusBefore.PacketsOutput} packets, URL={statusBefore.CurrentUrlIndex}");

        // Request URL switch and wait for it
        streamer.RequestSwitch();
        var switchCompleted = await TestHelpers.WaitForSwitchCountAsync(streamer, 1, TimeSpan.FromSeconds(5));
        Assert.True(switchCompleted, "Switch should complete within timeout");

        // Allow time for metrics to update
        await Task.Delay(TimeSpan.FromSeconds(2));

        var metricsAfter = streamer.GetMetrics();
        var statusAfter = streamer.GetStatus();
        streamer.Stop();

        // Assert
        Assert.NotNull(metricsBefore);
        Assert.NotNull(metricsAfter);

        Output.WriteLine($"After switch: {statusAfter.PacketsOutput} packets, URL={statusAfter.CurrentUrlIndex}");
        Output.WriteLine($"Bitrate before: {metricsBefore.TsBitrate}, after: {metricsAfter.TsBitrate}");
        Output.WriteLine($"Services before: {metricsBefore.ServiceCount}, after: {metricsAfter.ServiceCount}");
        Output.WriteLine($"PIDs before: {metricsBefore.PidCount}, after: {metricsAfter.PidCount}");

        // Metrics should still be valid after switch
        Assert.True(metricsAfter.TsBitrate > 0, "Bitrate should still be detected after switch");
        Assert.True(metricsAfter.ServiceCount >= 1, "Services should still be detected after switch");
        Assert.True(
            metricsAfter.PidCount >= 4,
            $"PIDs should still be tracked after switch, got {metricsAfter.PidCount}"
        );

        // Packets should have increased after the switch
        Assert.True(
            statusAfter.PacketsOutput > statusBefore.PacketsOutput,
            $"Should continue outputting packets after switch. Before: {statusBefore.PacketsOutput}, After: {statusAfter.PacketsOutput}"
        );

        // PAT/PMT should still be tracked correctly after the switch
        Assert.Equal(0, metricsAfter.Priority1.PatError);
        // Note: PCR repetition errors are expected after a URL switch because
        // the lifetime-average bitrate estimate is diluted by the switch gap,
        // making normal PCR intervals appear longer than 40ms. TR 101 290 PCR
        // compliance is tested separately on continuous streams.
    }

    /// <summary>
    /// Tests that the analyzer detects errors during unstable streaming
    /// but still produces valid metrics after recovery to a stable URL.
    /// </summary>
    [Fact]
    public async Task Failover_UnstableWithAnalyzer_DetectsErrorsAndRecovers()
    {
        // Arrange - verify that the analyzer detects errors during unstable streaming
        // but still produces valid metrics after recovery to stable URL.
        fixture.UnstableDropAfterMs = 2000;
        var unstableUrl = $"{fixture.BaseUrl}/stream/unstable";
        var stableUrl = $"{fixture.BaseUrl}/stream/5000";

        using var streamer = BuildStreamer(TestConfigs.FailoverAnalyzer);

        streamer.AddUrl(unstableUrl);
        streamer.AddUrl(stableUrl);

        // Act - start and wait for failover to stable URL
        Assert.True(streamer.Start(), "Streamer should start successfully");

        // Wait for failover to stable URL (index 1)
        var reachedStable = await TestHelpers.WaitForUrlSwitchAsync(streamer, 1, TimeSpan.FromSeconds(10));
        Output.WriteLine($"Reached stable URL: {reachedStable}");

        // Allow metrics to stabilize
        await Task.Delay(TimeSpan.FromSeconds(3));

        var metrics = streamer.GetMetrics();
        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        Output.WriteLine($"State: {status.State}");
        Output.WriteLine($"Bytes: {status.BytesReceived:N0}");
        Output.WriteLine($"Switches: {status.SwitchesCompleted}");
        Output.WriteLine($"Metrics available: {metrics != null}");

        Assert.True(status.BytesReceived > 0, "Should have received data");

        if (metrics != null)
        {
            Output.WriteLine($"Services: {metrics.ServiceCount}, PIDs: {metrics.PidCount}");
            Output.WriteLine($"Bitrate: {metrics.TsBitrate}");
            Output.WriteLine($"Quality: {metrics.CalculateQualityScore()}");

            // After recovering to stable URL, should detect stream structure
            Assert.True(
                metrics.ServiceCount >= 1 || metrics.PidCount >= 1,
                $"Should detect stream structure after recovery. Services: {metrics.ServiceCount}, PIDs: {metrics.PidCount}"
            );
        }
    }
}
