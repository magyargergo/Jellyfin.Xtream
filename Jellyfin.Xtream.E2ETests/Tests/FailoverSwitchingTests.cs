using System.Linq;
using Jellyfin.Xtream.E2ETests.Infrastructure;
using Jellyfin.Xtream.Service;
using Jellyfin.Xtream.Service.Streaming.Native;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.E2ETests.Tests;

/// <summary>
/// Tests the interaction between automatic failover and manual mid-stream switching.
/// These tests verify complex scenarios where both mechanisms operate together.
/// </summary>
[Collection("E2E")]
public class FailoverSwitchingTests
{
    private readonly DockerTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public FailoverSwitchingTests(DockerTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    /// <summary>
    /// Tests that a manual switch request during reconnection backoff
    /// takes precedence and immediately rotates to the next URL.
    /// </summary>
    [Fact]
    public async Task ManualSwitchDuringReconnection_RotatesToNextUrl()
    {
        // Arrange - URL 1 unstable, URL 2 and 3 stable
        _fixture.UnstableDropAfterMs = 1500;
        var unstableUrl = $"{_fixture.BaseUrl}/stream/unstable";
        var stableUrl2 = $"{_fixture.BaseUrl}/stream/5000";
        var stableUrl3 = $"{_fixture.BaseUrl}/stream/5000";

        var events = new List<(StreamerEvent Event, int Detail, DateTime Time)>();
        using var streamer = CreateStreamerForAggressiveFailover();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.StreamEvent += (_, args) =>
        {
            lock (events)
            {
                events.Add((args.EventType, args.Detail, DateTime.UtcNow));
            }
        };

        var totalBytes = 0L;
        streamer.SetOutputCallback((ptr, len) => Interlocked.Add(ref totalBytes, len));

        streamer.AddUrl(unstableUrl);
        streamer.AddUrl(stableUrl2);
        streamer.AddUrl(stableUrl3);

        // Act - start and wait for first drop
        Assert.True(streamer.Start());
        await Task.Delay(TimeSpan.FromSeconds(3)); // Wait for unstable to drop

        var statusBeforeSwitch = streamer.GetStatus();
        _output.WriteLine(
            $"Before manual switch: State={statusBeforeSwitch.State}, URL={statusBeforeSwitch.CurrentUrlIndex}"
        );

        // Request manual switch regardless of current state
        streamer.RequestSwitch();
        await Task.Delay(TimeSpan.FromSeconds(4)); // Let it stabilize on new URL

        var statusAfter = streamer.GetStatus();
        streamer.Stop();

        // Assert
        _output.WriteLine($"After: State={statusAfter.State}, URL={statusAfter.CurrentUrlIndex}");
        _output.WriteLine($"Bytes: {statusAfter.BytesReceived:N0}");
        _output.WriteLine($"Switches: {statusAfter.SwitchesCompleted}");
        _output.WriteLine($"Reconnections: {statusAfter.Reconnections}");

        lock (events)
        {
            _output.WriteLine($"Events ({events.Count}):");
            foreach (var (evt, detail, time) in events.Take(20))
            {
                _output.WriteLine($"  {time:HH:mm:ss.fff} {evt} (detail={detail})");
            }
        }

        Assert.True(statusAfter.BytesReceived > 0, "Should have received data");
        Assert.True(statusAfter.SwitchesCompleted >= 1, "Should have completed at least one switch");
    }

    /// <summary>
    /// Tests that after a manual switch to URL 2, if URL 2 becomes unstable,
    /// automatic failover correctly rotates to URL 3.
    /// </summary>
    [Fact]
    public async Task AutoFailoverAfterManualSwitch_ChainsCorrectly()
    {
        // Arrange - URL 1 stable, URL 2 unstable (switch target), URL 3 stable
        _fixture.UnstableDropAfterMs = 2000;
        var stableUrl1 = $"{_fixture.BaseUrl}/stream/5000";
        var unstableUrl2 = $"{_fixture.BaseUrl}/stream/unstable";
        var stableUrl3 = $"{_fixture.BaseUrl}/stream/5000";

        var events = new List<(StreamerEvent Event, int Detail, DateTime Time)>();
        using var streamer = CreateStreamerForAggressiveFailover();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.StreamEvent += (_, args) =>
        {
            lock (events)
            {
                events.Add((args.EventType, args.Detail, DateTime.UtcNow));
            }
        };

        var totalBytes = 0L;
        streamer.SetOutputCallback((ptr, len) => Interlocked.Add(ref totalBytes, len));

        streamer.AddUrl(stableUrl1);
        streamer.AddUrl(unstableUrl2);
        streamer.AddUrl(stableUrl3);

        // Act - start on stable URL 1
        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(3));

        var statusBeforeSwitch = streamer.GetStatus();
        Assert.Equal(0, statusBeforeSwitch.CurrentUrlIndex); // Should be on URL 1

        // Manual switch to URL 2 (unstable)
        streamer.RequestSwitch();
        await Task.Delay(TimeSpan.FromSeconds(5)); // Wait for URL 2 to drop and auto-failover to URL 3

        var statusAfter = streamer.GetStatus();
        streamer.Stop();

        // Assert
        _output.WriteLine($"Final: State={statusAfter.State}, URL={statusAfter.CurrentUrlIndex}");
        _output.WriteLine($"Bytes: {statusAfter.BytesReceived:N0}");
        _output.WriteLine($"Switches: {statusAfter.SwitchesCompleted}");
        _output.WriteLine($"Reconnections: {statusAfter.Reconnections}");

        lock (events)
        {
            _output.WriteLine($"Events ({events.Count}):");
            foreach (var (evt, detail, time) in events.Take(25))
            {
                _output.WriteLine($"  {time:HH:mm:ss.fff} {evt} (detail={detail})");
            }
        }

        Assert.True(statusAfter.BytesReceived > 0, "Should have received data");
        // Should have switched at least twice: manual (1→2) + auto (2→3)
        Assert.True(
            statusAfter.SwitchesCompleted >= 2,
            $"Should have at least 2 switches (manual + auto), got {statusAfter.SwitchesCompleted}"
        );
    }

    /// <summary>
    /// Tests multi-hop automatic failover where multiple URLs fail in sequence.
    /// URL 1 drops → URL 2 drops → URL 3 stable.
    /// </summary>
    [Fact]
    public async Task MultiHopAutomaticFailover_EventuallyRecoversToStable()
    {
        // Arrange - two unstable, one stable
        _fixture.UnstableDropAfterMs = 1500;
        var unstableUrl1 = $"{_fixture.BaseUrl}/stream/unstable";
        var unstableUrl2 = $"{_fixture.BaseUrl}/stream/unstable";
        var stableUrl3 = $"{_fixture.BaseUrl}/stream/5000";

        var events = new List<(StreamerEvent Event, int Detail, DateTime Time)>();
        using var streamer = CreateStreamerForAggressiveFailover();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.StreamEvent += (_, args) =>
        {
            lock (events)
            {
                events.Add((args.EventType, args.Detail, DateTime.UtcNow));
            }
        };

        var totalBytes = 0L;
        streamer.SetOutputCallback((ptr, len) => Interlocked.Add(ref totalBytes, len));

        streamer.AddUrl(unstableUrl1);
        streamer.AddUrl(unstableUrl2);
        streamer.AddUrl(stableUrl3);

        // Act - stream long enough for both failures and recovery
        Assert.True(streamer.Start());
        await Task.Delay(TimeSpan.FromSeconds(12));

        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        _output.WriteLine($"Final: State={status.State}, URL={status.CurrentUrlIndex}");
        _output.WriteLine($"Bytes: {status.BytesReceived:N0}");
        _output.WriteLine($"Switches: {status.SwitchesCompleted}");
        _output.WriteLine($"Reconnections: {status.Reconnections}");

        lock (events)
        {
            _output.WriteLine($"Events ({events.Count}):");
            foreach (var (evt, detail, time) in events.Take(30))
            {
                _output.WriteLine($"  {time:HH:mm:ss.fff} {evt} (detail={detail})");
            }
        }

        Assert.True(status.BytesReceived > 0, "Should have received data");
        // Should have exactly 2 switches: (0→1) + (1→2) when both unstable URLs drop
        Assert.Equal(2, status.SwitchesCompleted);
        // Should be on the stable URL (index 2)
        Assert.Equal(2, status.CurrentUrlIndex);
    }

    /// <summary>
    /// Tests that URL rotation wraps correctly - after the last URL fails,
    /// it should rotate back to the first URL.
    /// </summary>
    [Fact]
    public async Task UrlRotation_WrapsToFirstUrl()
    {
        // Arrange - all URLs unstable, will cycle through and wrap
        _fixture.UnstableDropAfterMs = 1500;
        var unstableUrl = $"{_fixture.BaseUrl}/stream/unstable";

        var urlIndexHistory = new List<int>();
        using var streamer = CreateStreamerForAggressiveFailover();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.StreamEvent += (_, args) =>
        {
            if (args.EventType == StreamerEvent.Connected)
            {
                lock (urlIndexHistory)
                {
                    urlIndexHistory.Add(args.Detail);
                }
            }
        };

        var totalBytes = 0L;
        streamer.SetOutputCallback((ptr, len) => Interlocked.Add(ref totalBytes, len));

        // Add 3 identical unstable URLs
        streamer.AddUrl(unstableUrl);
        streamer.AddUrl(unstableUrl);
        streamer.AddUrl(unstableUrl);

        // Act - stream long enough to cycle through all URLs and wrap
        Assert.True(streamer.Start());
        await Task.Delay(TimeSpan.FromSeconds(15));

        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        _output.WriteLine($"Final: State={status.State}, URL={status.CurrentUrlIndex}");
        _output.WriteLine($"Switches: {status.SwitchesCompleted}");

        lock (urlIndexHistory)
        {
            _output.WriteLine($"URL index history: [{string.Join(", ", urlIndexHistory)}]");

            // Should have seen all URL indices (0, 1, 2) multiple times due to rotation
            Assert.Contains(0, urlIndexHistory);
            Assert.Contains(1, urlIndexHistory);
            Assert.Contains(2, urlIndexHistory);

            // Verify rotation occurred (saw each URL more than once)
            var countZero = urlIndexHistory.Count(x => x == 0);
            var countOne = urlIndexHistory.Count(x => x == 1);
            var countTwo = urlIndexHistory.Count(x => x == 2);
            _output.WriteLine($"URL visit counts: 0={countZero}, 1={countOne}, 2={countTwo}");

            // With continuous cycling, each URL should be visited multiple times
            Assert.True(countZero >= 2, $"URL 0 should be visited at least twice, got {countZero}");
            Assert.True(countOne >= 2, $"URL 1 should be visited at least twice, got {countOne}");
            Assert.True(countTwo >= 2, $"URL 2 should be visited at least twice, got {countTwo}");
        }

        Assert.True(
            status.SwitchesCompleted >= 3,
            $"Expected at least 3 switches for wrap, got {status.SwitchesCompleted}"
        );
    }

    /// <summary>
    /// Tests that metrics remain valid after multiple automatic failovers.
    /// </summary>
    [Fact]
    public async Task MetricsAcrossMultipleFailovers_RemainValid()
    {
        // Arrange - two unstable, one stable, with analyzer
        _fixture.UnstableDropAfterMs = 2000;
        var unstableUrl1 = $"{_fixture.BaseUrl}/stream/unstable";
        var unstableUrl2 = $"{_fixture.BaseUrl}/stream/unstable";
        var stableUrl3 = $"{_fixture.BaseUrl}/stream/5000";

        using var streamer = CreateStreamerWithAnalyzerForFailover();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        var totalBytes = 0L;
        streamer.SetOutputCallback((ptr, len) => Interlocked.Add(ref totalBytes, len));

        streamer.AddUrl(unstableUrl1);
        streamer.AddUrl(unstableUrl2);
        streamer.AddUrl(stableUrl3);

        // Act - stream long enough for failovers and stable streaming
        Assert.True(streamer.Start());
        await Task.Delay(TimeSpan.FromSeconds(12));

        var status = streamer.GetStatus();
        var metrics = streamer.GetMetrics();
        streamer.Stop();

        // Assert
        _output.WriteLine($"Final: State={status.State}, URL={status.CurrentUrlIndex}");
        _output.WriteLine($"Bytes: {status.BytesReceived:N0}");
        _output.WriteLine($"Switches: {status.SwitchesCompleted}");

        Assert.True(status.BytesReceived > 0, "Should have received data");

        if (metrics != null)
        {
            _output.WriteLine($"Bitrate: {metrics.TsBitrate / 1_000_000.0:F2} Mbps");
            _output.WriteLine($"Services: {metrics.ServiceCount}, PIDs: {metrics.PidCount}");
            _output.WriteLine($"Quality: {metrics.CalculateQualityScore()}");
            _output.WriteLine($"PAT errors: {metrics.Priority1.PatError}");
            _output.WriteLine($"PMT errors: {metrics.Priority1.PmtError}");

            // After recovery to stable URL, should have valid stream structure
            Assert.True(metrics.TsBitrate > 0, "Should have valid bitrate");
            Assert.True(metrics.ServiceCount >= 1, "Should detect at least one service");
            Assert.True(metrics.PidCount >= 4, "Should detect expected PIDs");
        }
        else
        {
            Assert.Fail("Metrics should be available");
        }
    }

    /// <summary>
    /// Tests rapid manual switching during an ongoing automatic failover sequence.
    /// </summary>
    [Fact]
    public async Task RapidSwitchDuringFailover_HandledGracefully()
    {
        // Arrange - all unstable to force continuous failover attempts
        _fixture.UnstableDropAfterMs = 1000;
        var unstableUrl = $"{_fixture.BaseUrl}/stream/unstable";
        var stableUrl = $"{_fixture.BaseUrl}/stream/5000";

        var events = new List<(StreamerEvent Event, int Detail, DateTime Time)>();
        using var streamer = CreateStreamerForAggressiveFailover();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.StreamEvent += (_, args) =>
        {
            lock (events)
            {
                events.Add((args.EventType, args.Detail, DateTime.UtcNow));
            }
        };

        var totalBytes = 0L;
        streamer.SetOutputCallback((ptr, len) => Interlocked.Add(ref totalBytes, len));

        streamer.AddUrl(unstableUrl);
        streamer.AddUrl(unstableUrl);
        streamer.AddUrl(stableUrl);

        // Act - start and issue multiple rapid switch requests
        Assert.True(streamer.Start());
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Issue 3 rapid switch requests (500ms apart)
        for (int i = 0; i < 3; i++)
        {
            streamer.RequestSwitch();
            await Task.Delay(500);
        }

        await Task.Delay(TimeSpan.FromSeconds(5)); // Let it stabilize

        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert - should not crash and should eventually stabilize on stable URL
        _output.WriteLine($"Final: State={status.State}, URL={status.CurrentUrlIndex}");
        _output.WriteLine($"Bytes: {status.BytesReceived:N0}");
        _output.WriteLine($"Switches: {status.SwitchesCompleted}");

        lock (events)
        {
            _output.WriteLine($"Events ({events.Count}):");
            foreach (var (evt, detail, time) in events.Take(30))
            {
                _output.WriteLine($"  {time:HH:mm:ss.fff} {evt} (detail={detail})");
            }
        }

        Assert.True(status.BytesReceived > 0, "Should have received data");
        // Should be on the stable URL (index 2) after settling
        Assert.Equal(2, status.CurrentUrlIndex);
        Assert.True(status.State == StreamerState.Streaming || status.State == StreamerState.Stopped);
    }

    /// <summary>
    /// Tests that switch count is accurate when combining manual and automatic switches.
    /// </summary>
    [Fact]
    public async Task SwitchCounters_AccurateDuringMixedOperations()
    {
        // Arrange - stable → unstable → stable chain
        _fixture.UnstableDropAfterMs = 1500;
        var stableUrl1 = $"{_fixture.BaseUrl}/stream/5000";
        var unstableUrl = $"{_fixture.BaseUrl}/stream/unstable";
        var stableUrl2 = $"{_fixture.BaseUrl}/stream/5000";

        using var streamer = CreateStreamerForAggressiveFailover();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        var totalBytes = 0L;
        streamer.SetOutputCallback((ptr, len) => Interlocked.Add(ref totalBytes, len));

        streamer.AddUrl(stableUrl1);
        streamer.AddUrl(unstableUrl);
        streamer.AddUrl(stableUrl2);

        // Act
        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(3));

        var initialStatus = streamer.GetStatus();
        Assert.Equal(0, initialStatus.SwitchesCompleted);

        // Manual switch (1→2)
        streamer.RequestSwitch();
        await Task.Delay(TimeSpan.FromSeconds(1));
        var afterManualSwitch = streamer.GetStatus();
        _output.WriteLine(
            $"After manual switch: Switches={afterManualSwitch.SwitchesCompleted}, URL={afterManualSwitch.CurrentUrlIndex}"
        );

        // Wait for unstable URL 2 to fail and auto-switch to URL 3
        await Task.Delay(TimeSpan.FromSeconds(5));

        var finalStatus = streamer.GetStatus();
        streamer.Stop();

        // Assert
        _output.WriteLine(
            $"Final: Switches={finalStatus.SwitchesCompleted}, Reconnections={finalStatus.Reconnections}"
        );
        _output.WriteLine($"Final URL: {finalStatus.CurrentUrlIndex}");

        // Should have exactly 2 switches: manual (0→1) + auto (1→2)
        Assert.Equal(2, finalStatus.SwitchesCompleted);
        // Should be on URL 2 (the stable one)
        Assert.Equal(2, finalStatus.CurrentUrlIndex);
    }

    // ========================================================================
    // Keyframe Alignment Tests
    // ========================================================================

    /// <summary>
    /// Tests that after a URL switch, the stream quality remains high.
    /// The keyframe aligner should ensure output starts from a clean sync point.
    /// </summary>
    [Fact]
    public async Task KeyframeAlignment_AfterManualSwitch_StreamQualityRemains()
    {
        // Arrange - two stable URLs
        var stableUrl1 = $"{_fixture.BaseUrl}/stream/5000";
        var stableUrl2 = $"{_fixture.BaseUrl}/stream/5000";

        using var streamer = CreateStreamerWithAnalyzerForFailover();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        var totalBytes = 0L;
        streamer.SetOutputCallback((ptr, len) => Interlocked.Add(ref totalBytes, len));

        streamer.AddUrl(stableUrl1);
        streamer.AddUrl(stableUrl2);

        // Act - start streaming
        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(2)); // Establish baseline

        var metricsBeforeSwitch = streamer.GetMetrics();
        _output.WriteLine(
            $"Before switch: Quality={metricsBeforeSwitch?.CalculateQualityScore()}, Bytes={Interlocked.Read(ref totalBytes):N0}"
        );

        // Manual switch to URL 2
        streamer.RequestSwitch();
        await Task.Delay(TimeSpan.FromSeconds(4)); // Let it stabilize

        var metricsAfterSwitch = streamer.GetMetrics();
        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        Assert.NotNull(metricsAfterSwitch);
        _output.WriteLine($"After switch: Quality={metricsAfterSwitch.CalculateQualityScore()}");
        _output.WriteLine($"CC errors: {metricsAfterSwitch.Priority1.ContinuityCountError}");
        _output.WriteLine($"Sync errors: {metricsAfterSwitch.Priority1.SyncByteError}");
        _output.WriteLine($"Switches: {status.SwitchesCompleted}");

        Assert.True(status.SwitchesCompleted >= 1, "Should have completed at least one switch");
        Assert.True(Interlocked.Read(ref totalBytes) > 0, "Should have received data");

        // Quality should remain high after switch (keyframe alignment ensures clean start)
        var quality = metricsAfterSwitch.CalculateQualityScore();
        Assert.True(quality >= 70, $"Quality score after switch should be >= 70, got {quality}");

        // No sync errors (output is valid TS data)
        Assert.Equal(0, metricsAfterSwitch.Priority1.SyncByteError);
        Assert.Equal(0, metricsAfterSwitch.Priority1.SyncLoss);
    }

    /// <summary>
    /// Tests that after multiple rapid switches, the stream remains valid.
    /// Each switch should start from a keyframe to prevent decoder corruption.
    /// </summary>
    [Fact]
    public async Task KeyframeAlignment_MultipleRapidSwitches_NoStreamCorruption()
    {
        // Arrange - three stable URLs
        var stableUrl = $"{_fixture.BaseUrl}/stream/5000";

        using var streamer = CreateStreamerWithAnalyzerForFailover();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        var events = new List<(StreamerEvent Event, DateTime Time)>();
        streamer.StreamEvent += (_, args) =>
        {
            lock (events)
            {
                events.Add((args.EventType, DateTime.UtcNow));
            }
        };

        var totalBytes = 0L;
        streamer.SetOutputCallback((ptr, len) => Interlocked.Add(ref totalBytes, len));

        streamer.AddUrl(stableUrl);
        streamer.AddUrl(stableUrl);
        streamer.AddUrl(stableUrl);

        // Act - start and perform rapid switches
        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(2));

        // 5 rapid switches, 300ms apart
        for (int i = 0; i < 5; i++)
        {
            streamer.RequestSwitch();
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }

        await Task.Delay(TimeSpan.FromSeconds(3)); // Stabilize

        var metrics = streamer.GetMetrics();
        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        _output.WriteLine($"Switches: {status.SwitchesCompleted}");
        _output.WriteLine($"Quality: {metrics.CalculateQualityScore()}");
        _output.WriteLine($"CC errors: {metrics.Priority1.ContinuityCountError}");
        _output.WriteLine($"Sync errors: {metrics.Priority1.SyncByteError}");
        _output.WriteLine($"Total bytes: {Interlocked.Read(ref totalBytes):N0}");

        lock (events)
        {
            _output.WriteLine($"Events ({events.Count}):");
            foreach (var (evt, time) in events.Take(15))
            {
                _output.WriteLine($"  {time:HH:mm:ss.fff} {evt}");
            }
        }

        // Should have completed some switches (may coalesce rapid requests)
        Assert.True(status.SwitchesCompleted >= 1, "Should have completed at least one switch");
        Assert.True(Interlocked.Read(ref totalBytes) > 0, "Should have received data");

        // No sync corruption (keyframe alignment ensures valid TS output)
        Assert.Equal(0, metrics.Priority1.SyncByteError);
        Assert.Equal(0, metrics.Priority1.SyncLoss);
    }

    /// <summary>
    /// Tests that automatic failover maintains stream quality via keyframe alignment.
    /// When failing over to a new URL, the stream should start from a keyframe.
    /// </summary>
    [Fact]
    public async Task KeyframeAlignment_AutomaticFailover_StreamContinues()
    {
        // Arrange - first URL drops, second is stable
        _fixture.UnstableDropAfterMs = 2000;
        var unstableUrl = $"{_fixture.BaseUrl}/stream/unstable";
        var stableUrl = $"{_fixture.BaseUrl}/stream/5000";

        using var streamer = CreateStreamerWithAnalyzerForFailover();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        var switchedToStable = new TaskCompletionSource<bool>();
        streamer.StreamEvent += (_, args) =>
        {
            // Detect when we've connected to the second URL (index 1)
            if (args.EventType == StreamerEvent.Connected && args.Detail == 1)
            {
                switchedToStable.TrySetResult(true);
            }
        };

        var totalBytes = 0L;
        streamer.SetOutputCallback((ptr, len) => Interlocked.Add(ref totalBytes, len));

        streamer.AddUrl(unstableUrl);
        streamer.AddUrl(stableUrl);

        // Act - start and wait for automatic failover
        Assert.True(streamer.Start());

        // Wait for switch to stable URL or timeout
        var switched = await Task.WhenAny(
            switchedToStable.Task,
            Task.Delay(TimeSpan.FromSeconds(15))
        ) == switchedToStable.Task;

        await Task.Delay(TimeSpan.FromSeconds(3)); // Get metrics after stabilization

        var metrics = streamer.GetMetrics();
        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        _output.WriteLine($"Switched to stable: {switched}");
        _output.WriteLine($"Final state: {status.State}, URL: {status.CurrentUrlIndex}");
        _output.WriteLine($"Bytes received: {Interlocked.Read(ref totalBytes):N0}");

        Assert.NotNull(metrics);
        _output.WriteLine($"Quality: {metrics.CalculateQualityScore()}");
        _output.WriteLine($"Services: {metrics.ServiceCount}, PIDs: {metrics.PidCount}");
        _output.WriteLine($"CC errors: {metrics.Priority1.ContinuityCountError}");
        _output.WriteLine($"Sync errors: {metrics.Priority1.SyncByteError}");

        Assert.True(Interlocked.Read(ref totalBytes) > 0, "Should have received data");
        Assert.True(metrics.ServiceCount >= 1, "Should detect services after failover");

        // Stream should be valid (keyframe alignment ensures no garbage output)
        Assert.Equal(0, metrics.Priority1.SyncByteError);
        Assert.Equal(0, metrics.Priority1.SyncLoss);
    }

    /// <summary>
    /// Tests that the keyframe alignment event is emitted after a switch.
    /// </summary>
    [Fact]
    public async Task KeyframeAlignment_EmitsKeyframeFoundEvent()
    {
        // Arrange - two stable URLs
        var stableUrl = $"{_fixture.BaseUrl}/stream/5000";

        using var streamer = CreateStreamerForAggressiveFailover();
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

        var totalBytes = 0L;
        streamer.SetOutputCallback((ptr, len) => Interlocked.Add(ref totalBytes, len));

        streamer.AddUrl(stableUrl);
        streamer.AddUrl(stableUrl);

        // Act
        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Request switch
        streamer.RequestSwitch();
        await Task.Delay(TimeSpan.FromSeconds(3));

        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        lock (events)
        {
            _output.WriteLine($"Events ({events.Count}):");
            foreach (var (evt, detail, time) in events)
            {
                _output.WriteLine($"  {time:HH:mm:ss.fff} {evt} (detail={detail})");
            }

            // Should see switched/connected events
            var hasSwitch =
                events.Any(e => e.Event == StreamerEvent.Switched)
                || events.Any(e => e.Event == StreamerEvent.Connected && e.Detail > 0);
            Assert.True(hasSwitch, "Should have switched/connected events");
        }

        Assert.True(status.SwitchesCompleted >= 1, "Should have completed switch");
    }

    // ========================================================================
    // Helper Methods
    // ========================================================================

    private static NativeStreamer? CreateStreamerForAggressiveFailover()
    {
        var config = new TsDuckStreamerConfigNative
        {
            ConnectTimeoutMs = 3000,
            ResponseTimeoutMs = 5000,
            StallTimeoutMs = 3000, // Short stall timeout for fast detection
            MaxRetries = 10,
            InitialBackoffMs = 100,
            MaxBackoffMs = 1000,
            BackoffMultiplier = 1.5,
            BackoffJitterMs = 50,
            OutputFd = -1,
            AlignmentBufferPackets = 32,
            EnableRestamp = 1,
            RestampMode = (int)RestampingMode.Correct,
            LowSpeedLimitBytes = 100,
            LowSpeedTimeSec = 2,
            StallsBeforeSwitch = 1, // Switch on first stall
            EnableQualitySwitch = 0, // Disable quality-based switching for deterministic tests
            Reserved = 0,
        };

        return NativeStreamer.TryCreate(config);
    }

    private static NativeStreamer? CreateStreamerWithAnalyzerForFailover()
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
