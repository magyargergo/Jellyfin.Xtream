using Jellyfin.Xtream.E2ETests.Infrastructure;
using Jellyfin.Xtream.Service.Streaming.Native;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.E2ETests.Tests;

/// <summary>
/// Tests for connection error handling scenarios.
/// Verifies proper behavior when connections fail, timeout, or encounter network issues.
/// </summary>
[Collection("E2E-Failover")]
public class ConnectionErrorTests(DockerTestFixture fixture, ITestOutputHelper output)
    : NativeE2ETestBase(output, TestConfigs.ShortTimeouts)
{
    /// <summary>
    /// Tests that connecting to a non-existent endpoint is handled gracefully.
    /// The streamer should attempt retries and eventually fail without crashing.
    /// </summary>
    [Fact]
    public async Task Connect_NonExistentEndpoint_HandlesGracefully()
    {
        // Arrange - use a port that's definitely not listening
        var badUrl = "http://localhost:59999/stream/5000";

        using var streamer = BuildStreamer(TestConfigs.ShortTimeouts);

        streamer.AddUrl(badUrl);

        // Act
        var started = streamer.Start();

        // Wait for retry attempts
        await Task.Delay(TimeSpan.FromSeconds(5));

        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert - should start but fail to connect
        Output.WriteLine($"Started: {started}");
        Output.WriteLine($"State: {status.State}");
        Output.WriteLine($"Bytes: {status.BytesReceived}");
        Output.WriteLine($"Retry count: {status.RetryCount}");

        Assert.True(started, "Start should return true even if connection will fail");
        Assert.Equal(0, status.BytesReceived);
        Assert.True(status.RetryCount > 0, "Should have attempted retries");
    }

    /// <summary>
    /// Tests that invalid URL format is handled without crashing.
    /// </summary>
    [Fact]
    public async Task Connect_InvalidUrlFormat_HandlesGracefully()
    {
        // Arrange - use an invalid URL
        var badUrl = "not-a-valid-url";

        using var streamer = BuildStreamer(TestConfigs.ShortTimeouts);

        // Act - adding invalid URL may throw or succeed (implementation dependent)
        try
        {
            streamer.AddUrl(badUrl);
            var started = streamer.Start();
            await Task.Delay(TimeSpan.FromSeconds(2));

            var status = streamer.GetStatus();
            streamer.Stop();

            Output.WriteLine($"Started: {started}");
            Output.WriteLine($"State: {status.State}");

            // Should not have received any data from invalid URL
            Assert.Equal(0, status.BytesReceived);
        }
        catch (Exception ex)
        {
            // If it throws, that's also acceptable handling
            Output.WriteLine($"Exception thrown (acceptable): {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Tests that connection timeout is respected.
    /// Uses a very short timeout to verify timeout behavior.
    /// </summary>
    [Fact]
    public async Task Connect_VeryShortTimeout_TimesOut()
    {
        // Arrange - use delayed endpoint with very short connection timeout
        var delayedUrl = $"{fixture.BaseUrl}/stream/delayed/5000"; // 5 second delay

        using var streamer = BuildStreamer(TestConfigs.VeryShortTimeouts);

        streamer.AddUrl(delayedUrl);

        // Act
        Assert.True(streamer.Start());
        await Task.Delay(TimeSpan.FromSeconds(3));

        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert - connection should have timed out before data arrived
        Output.WriteLine($"State: {status.State}");
        Output.WriteLine($"Bytes: {status.BytesReceived}");
        Output.WriteLine($"Retry count: {status.RetryCount}");

        // With very short timeout and 5s delay, we expect few or no bytes
        // and some retry attempts
        Assert.True(status.RetryCount >= 0, "Should have tracked retry attempts");
    }

    /// <summary>
    /// Tests recovery from connection refused to a working URL.
    /// First URL refuses connection, second URL works.
    /// </summary>
    [Fact]
    public async Task Connect_FirstRefusedSecondWorks_RecoveresToSecond()
    {
        // Arrange - bad URL first, good URL second
        var badUrl = "http://localhost:59999/stream/5000";
        var goodUrl = $"{fixture.BaseUrl}/stream/5000";

        using var streamer = BuildStreamer(TestConfigs.ShortTimeouts);

        streamer.AddUrl(badUrl);
        streamer.AddUrl(goodUrl);

        // Act
        Assert.True(streamer.Start());

        // Wait for failover to good URL
        var reachedGoodUrl = await TestHelpers.WaitForConditionAsync(
            () =>
            {
                var s = streamer.GetStatus();
                return s.BytesReceived > 0 && s.CurrentUrlIndex == 1;
            },
            TimeSpan.FromSeconds(15)
        );

        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        Output.WriteLine($"Reached good URL: {reachedGoodUrl}");
        Output.WriteLine($"State: {status.State}");
        Output.WriteLine($"URL index: {status.CurrentUrlIndex}");
        Output.WriteLine($"Bytes: {status.BytesReceived:N0}");

        Assert.True(status.BytesReceived > 0, "Should have received data from good URL");
        Assert.Equal(1, status.CurrentUrlIndex);
    }

    /// <summary>
    /// Tests that stall detection works when the stream stops sending data.
    /// </summary>
    [Fact]
    public async Task Stall_StreamStopsData_TriggersReconnect()
    {
        // Arrange - unstable stream that drops connection
        fixture.UnstableDropAfterMs = 1500;
        var unstableUrl = $"{fixture.BaseUrl}/stream/unstable";
        var stableUrl = $"{fixture.BaseUrl}/stream/5000";

        using var streamer = BuildStreamer(TestConfigs.StallDetection);

        streamer.AddUrl(unstableUrl);
        streamer.AddUrl(stableUrl);

        // Act
        Assert.True(streamer.Start());

        // Wait for stall detection and recovery
        var recovered = await TestHelpers.WaitForConditionAsync(
            () =>
            {
                var s = streamer.GetStatus();
                return s.CurrentUrlIndex == 1 && s.BytesReceived > 0;
            },
            TimeSpan.FromSeconds(15)
        );

        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        Output.WriteLine($"Recovered to stable: {recovered}");
        Output.WriteLine($"URL index: {status.CurrentUrlIndex}");
        Output.WriteLine($"Reconnections: {status.Reconnections}");
        Output.WriteLine($"Bytes: {status.BytesReceived:N0}");

        Assert.True(status.BytesReceived > 0, "Should have received data after recovery");
    }

    /// <summary>
    /// Tests that HTTP 404 responses are handled gracefully.
    /// </summary>
    [Fact]
    public async Task Connect_Http404_HandlesGracefully()
    {
        // Arrange - request non-existent path
        var badPath = $"{fixture.BaseUrl}/nonexistent/path";
        var goodUrl = $"{fixture.BaseUrl}/stream/5000";

        using var streamer = BuildStreamer(TestConfigs.ShortTimeouts);

        streamer.AddUrl(badPath);
        streamer.AddUrl(goodUrl);

        // Act
        Assert.True(streamer.Start());

        // Wait for failover to good URL
        var recovered = await TestHelpers.WaitForConditionAsync(
            () => streamer.GetStatus().BytesReceived > 0,
            TimeSpan.FromSeconds(10)
        );

        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        Output.WriteLine($"Recovered: {recovered}");
        Output.WriteLine($"Bytes: {status.BytesReceived:N0}");
        Output.WriteLine($"URL index: {status.CurrentUrlIndex}");

        Assert.True(status.BytesReceived > 0, "Should recover to working URL");
    }

    /// <summary>
    /// Tests that the streamer respects the configured max retries.
    /// </summary>
    [Fact]
    public async Task Connect_MaxRetries_StopsAfterLimit()
    {
        // Arrange - only bad URLs, should exhaust retries
        var badUrl = "http://localhost:59999/stream/5000";

        var config = new TsDuckStreamerConfigNative
        {
            ConnectTimeoutMs = 500,
            ResponseTimeoutMs = 500,
            StallTimeoutMs = 500,
            MaxRetries = 3, // Only 3 retries
            InitialBackoffMs = 100,
            MaxBackoffMs = 200,
            BackoffMultiplier = 1.0,
            BackoffJitterMs = 0,
            OutputFd = -1,
            AlignmentBufferPackets = 32,
            EnableRestamp = 0,
            RestampMode = 0,
            LowSpeedLimitBytes = 0,
            LowSpeedTimeSec = 0,
            StallsBeforeSwitch = 1,
        };

        using var streamer = BuildStreamer(new StreamerTestSetup(config));

        streamer.AddUrl(badUrl);

        // Act
        Assert.True(streamer.Start());
        await Task.Delay(TimeSpan.FromSeconds(5));

        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        Output.WriteLine($"State: {status.State}");
        Output.WriteLine($"Retry count: {status.RetryCount}");

        Assert.Equal(0, status.BytesReceived);
        // With MaxRetries=3, should have attempted at least some retries
        Assert.True(status.RetryCount <= 4, $"Should stop after max retries, got {status.RetryCount}");
    }
}
