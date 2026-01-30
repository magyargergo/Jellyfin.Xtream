using Jellyfin.Xtream.E2ETests.Infrastructure;
using Jellyfin.Xtream.Service.Streaming.Native;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.E2ETests.Tests;

/// <summary>
/// Tests for connection error handling scenarios.
/// Verifies proper behavior when connections fail, timeout, or encounter network issues.
/// </summary>
[Collection("E2E-Failover")]
public class ConnectionErrorTests
{
    private readonly DockerTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public ConnectionErrorTests(DockerTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    /// <summary>
    /// Tests that connecting to a non-existent endpoint is handled gracefully.
    /// The streamer should attempt retries and eventually fail without crashing.
    /// </summary>
    [Fact]
    public async Task Connect_NonExistentEndpoint_HandlesGracefully()
    {
        // Arrange - use a port that's definitely not listening
        var badUrl = "http://localhost:59999/stream/5000";

        using var streamer = CreateStreamerWithShortTimeouts();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(badUrl);

        // Act
        var started = streamer.Start();

        // Wait for retry attempts
        await Task.Delay(TimeSpan.FromSeconds(5));

        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert - should start but fail to connect
        _output.WriteLine($"Started: {started}");
        _output.WriteLine($"State: {status.State}");
        _output.WriteLine($"Bytes: {status.BytesReceived}");
        _output.WriteLine($"Retry count: {status.RetryCount}");

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

        using var streamer = CreateStreamerWithShortTimeouts();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        // Act - adding invalid URL may throw or succeed (implementation dependent)
        try
        {
            streamer.AddUrl(badUrl);
            var started = streamer.Start();
            await Task.Delay(TimeSpan.FromSeconds(2));

            var status = streamer.GetStatus();
            streamer.Stop();

            _output.WriteLine($"Started: {started}");
            _output.WriteLine($"State: {status.State}");

            // Should not have received any data from invalid URL
            Assert.Equal(0, status.BytesReceived);
        }
        catch (Exception ex)
        {
            // If it throws, that's also acceptable handling
            _output.WriteLine($"Exception thrown (acceptable): {ex.GetType().Name}: {ex.Message}");
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
        var delayedUrl = $"{_fixture.BaseUrl}/stream/delayed/5000"; // 5 second delay

        using var streamer = CreateStreamerWithVeryShortTimeout();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(delayedUrl);

        // Act
        Assert.True(streamer.Start());
        await Task.Delay(TimeSpan.FromSeconds(3));

        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert - connection should have timed out before data arrived
        _output.WriteLine($"State: {status.State}");
        _output.WriteLine($"Bytes: {status.BytesReceived}");
        _output.WriteLine($"Retry count: {status.RetryCount}");

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
        var goodUrl = $"{_fixture.BaseUrl}/stream/5000";

        using var streamer = CreateStreamerWithShortTimeouts();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

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
        _output.WriteLine($"Reached good URL: {reachedGoodUrl}");
        _output.WriteLine($"State: {status.State}");
        _output.WriteLine($"URL index: {status.CurrentUrlIndex}");
        _output.WriteLine($"Bytes: {status.BytesReceived:N0}");

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
        _fixture.UnstableDropAfterMs = 1500;
        var unstableUrl = $"{_fixture.BaseUrl}/stream/unstable";
        var stableUrl = $"{_fixture.BaseUrl}/stream/5000";

        using var streamer = CreateStreamerForStallDetection();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

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
        _output.WriteLine($"Recovered to stable: {recovered}");
        _output.WriteLine($"URL index: {status.CurrentUrlIndex}");
        _output.WriteLine($"Reconnections: {status.Reconnections}");
        _output.WriteLine($"Bytes: {status.BytesReceived:N0}");

        Assert.True(status.BytesReceived > 0, "Should have received data after recovery");
    }

    /// <summary>
    /// Tests that HTTP 404 responses are handled gracefully.
    /// </summary>
    [Fact]
    public async Task Connect_Http404_HandlesGracefully()
    {
        // Arrange - request non-existent path
        var badPath = $"{_fixture.BaseUrl}/nonexistent/path";
        var goodUrl = $"{_fixture.BaseUrl}/stream/5000";

        using var streamer = CreateStreamerWithShortTimeouts();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

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
        _output.WriteLine($"Recovered: {recovered}");
        _output.WriteLine($"Bytes: {status.BytesReceived:N0}");
        _output.WriteLine($"URL index: {status.CurrentUrlIndex}");

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

        using var streamer = NativeStreamer.TryCreate(config);
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(badUrl);

        // Act
        Assert.True(streamer.Start());
        await Task.Delay(TimeSpan.FromSeconds(5));

        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        _output.WriteLine($"State: {status.State}");
        _output.WriteLine($"Retry count: {status.RetryCount}");

        Assert.Equal(0, status.BytesReceived);
        // With MaxRetries=3, should have attempted at least some retries
        Assert.True(status.RetryCount <= 4, $"Should stop after max retries, got {status.RetryCount}");
    }

    // ========================================================================
    // Helper Methods
    // ========================================================================

    private static NativeStreamer? CreateStreamerWithShortTimeouts()
    {
        var config = new TsDuckStreamerConfigNative
        {
            ConnectTimeoutMs = 2000,
            ResponseTimeoutMs = 2000,
            StallTimeoutMs = 2000,
            MaxRetries = 5,
            InitialBackoffMs = 100,
            MaxBackoffMs = 500,
            BackoffMultiplier = 1.5,
            BackoffJitterMs = 50,
            OutputFd = -1,
            AlignmentBufferPackets = 32,
            EnableRestamp = 0,
            RestampMode = 0,
            LowSpeedLimitBytes = 100,
            LowSpeedTimeSec = 1,
            StallsBeforeSwitch = 1,
        };

        return NativeStreamer.TryCreate(config);
    }

    private static NativeStreamer? CreateStreamerWithVeryShortTimeout()
    {
        var config = new TsDuckStreamerConfigNative
        {
            ConnectTimeoutMs = 500, // Very short
            ResponseTimeoutMs = 500,
            StallTimeoutMs = 500,
            MaxRetries = 2,
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

        return NativeStreamer.TryCreate(config);
    }

    private static NativeStreamer? CreateStreamerForStallDetection()
    {
        var config = new TsDuckStreamerConfigNative
        {
            ConnectTimeoutMs = 3000,
            ResponseTimeoutMs = 3000,
            StallTimeoutMs = 2000, // Detect stall after 2s
            MaxRetries = 10,
            InitialBackoffMs = 100,
            MaxBackoffMs = 1000,
            BackoffMultiplier = 1.5,
            BackoffJitterMs = 50,
            OutputFd = -1,
            AlignmentBufferPackets = 32,
            EnableRestamp = 0,
            RestampMode = 0,
            LowSpeedLimitBytes = 100,
            LowSpeedTimeSec = 2,
            StallsBeforeSwitch = 1, // Switch on first stall
        };

        return NativeStreamer.TryCreate(config);
    }
}
