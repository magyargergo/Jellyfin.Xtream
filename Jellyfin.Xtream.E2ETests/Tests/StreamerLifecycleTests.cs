using Jellyfin.Xtream.E2ETests.Infrastructure;
using Jellyfin.Xtream.Service.Streaming.Native;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.E2ETests.Tests;

/// <summary>
/// Tests for NativeStreamer lifecycle management, resource cleanup, and error handling.
/// Verifies proper behavior during start/stop cycles, disposal, and edge cases.
/// </summary>
[Collection("E2E-Streaming")]
public class StreamerLifecycleTests
{
    private readonly DockerTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public StreamerLifecycleTests(DockerTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    // ========================================================================
    // Start/Stop Cycle Tests
    // ========================================================================

    /// <summary>
    /// Verifies that calling Start() without any URLs returns false.
    /// </summary>
    [Fact]
    public void Start_WithNoUrls_ReturnsFalse()
    {
        // Arrange
        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        // Act - start without adding URLs
        var started = streamer.Start();

        // Assert
        Assert.False(started, "Start() should return false when no URLs are configured");

        var status = streamer.GetStatus();
        _output.WriteLine($"State after failed start: {status.State}");
        Assert.Equal(StreamerState.Idle, status.State);
    }

    /// <summary>
    /// Verifies that calling Start() twice returns false on the second call.
    /// </summary>
    [Fact]
    public async Task Start_CalledTwice_SecondCallReturnsFalse()
    {
        // Arrange
        var url = $"{_fixture.BaseUrl}/stream/5000";

        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(url);

        // Act
        var firstStart = streamer.Start();
        await TestHelpers.WaitForStreamingAsync(streamer, TimeSpan.FromSeconds(3));

        var secondStart = streamer.Start();
        streamer.Stop();

        // Assert
        _output.WriteLine($"First Start: {firstStart}, Second Start: {secondStart}");
        Assert.True(firstStart, "First Start() should succeed");
        Assert.False(secondStart, "Second Start() should return false when already running");
    }

    /// <summary>
    /// Verifies that Stop() can be called multiple times without issues.
    /// </summary>
    [Fact]
    public async Task Stop_CalledMultipleTimes_NoErrors()
    {
        // Arrange
        var url = $"{_fixture.BaseUrl}/stream/5000";

        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(url);
        Assert.True(streamer.Start());
        await TestHelpers.WaitForStreamingAsync(streamer, TimeSpan.FromSeconds(3));

        // Act - stop multiple times
        streamer.Stop();
        streamer.Stop();
        streamer.Stop();

        // Assert - should not throw, status should be valid
        var status = streamer.GetStatus();
        _output.WriteLine($"State after multiple stops: {status.State}");
        Assert.True(status.BytesReceived > 0, "Should have received some data before stopping");
    }

    /// <summary>
    /// Verifies that Stop() can be called on a streamer that was never started.
    /// </summary>
    [Fact]
    public void Stop_NeverStarted_NoErrors()
    {
        // Arrange
        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        // Act - stop without starting
        streamer.Stop();

        // Assert - should not throw
        var status = streamer.GetStatus();
        Assert.Equal(StreamerState.Idle, status.State);
        Assert.Equal(0, status.BytesReceived);
    }

    /// <summary>
    /// Verifies that the streamer can be restarted after being stopped.
    /// </summary>
    [Fact]
    public async Task StartStopRestart_CycleWorks()
    {
        // Arrange
        var url = $"{_fixture.BaseUrl}/stream/5000";

        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(url);

        // Act - first cycle
        Assert.True(streamer.Start(), "First start should succeed");
        await TestHelpers.WaitForStreamingAsync(streamer, TimeSpan.FromSeconds(3));
        var bytesFirstRun = streamer.GetStatus().BytesReceived;
        streamer.Stop();

        _output.WriteLine($"First run bytes: {bytesFirstRun:N0}");

        // Second cycle
        Assert.True(streamer.Start(), "Second start should succeed after stop");
        await TestHelpers.WaitForStreamingAsync(streamer, TimeSpan.FromSeconds(3));
        await Task.Delay(TimeSpan.FromSeconds(2));

        var statusSecondRun = streamer.GetStatus();
        streamer.Stop();

        // Assert
        _output.WriteLine($"Second run state: {statusSecondRun.State}, bytes: {statusSecondRun.BytesReceived:N0}");
        Assert.True(bytesFirstRun > 0, "First run should have received data");
        Assert.True(statusSecondRun.BytesReceived > 0, "Second run should have received data");
    }

    // ========================================================================
    // Disposal Tests
    // ========================================================================

    /// <summary>
    /// Verifies that disposing while streaming stops the stream gracefully.
    /// </summary>
    [Fact]
    public async Task Dispose_WhileStreaming_StopsGracefully()
    {
        // Arrange
        var url = $"{_fixture.BaseUrl}/stream/5000";
        StreamerStatus? statusBeforeDispose = null;

        // Act - use a block scope to force disposal
        {
            var streamer = CreateStreamer();
            if (streamer == null)
            {
                _output.WriteLine("SKIP: Native library not available");
                return;
            }

            streamer.AddUrl(url);
            Assert.True(streamer.Start());
            await TestHelpers.WaitForStreamingAsync(streamer, TimeSpan.FromSeconds(3));
            await Task.Delay(TimeSpan.FromSeconds(1));

            statusBeforeDispose = streamer.GetStatus();
            _output.WriteLine(
                $"Before dispose: State={statusBeforeDispose.Value.State}, Bytes={statusBeforeDispose.Value.BytesReceived:N0}"
            );

            // Dispose while streaming
            streamer.Dispose();
        }

        // Assert - should not hang or throw
        Assert.NotNull(statusBeforeDispose);
        Assert.True(statusBeforeDispose.Value.BytesReceived > 0, "Should have received data before disposal");
    }

    /// <summary>
    /// Verifies that Dispose can be called multiple times without issues.
    /// </summary>
    [Fact]
#pragma warning disable IDISP016 // Don't use disposed instance - intentional test for disposal behavior
    public void Dispose_CalledMultipleTimes_NoErrors()
    {
        // Arrange
        var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        // Act - intentionally calling Dispose multiple times to verify it's safe
        streamer.Dispose();
        streamer.Dispose();
        streamer.Dispose();

        // Assert - no exception thrown
        _output.WriteLine("Multiple Dispose calls succeeded without errors");
    }
#pragma warning restore IDISP016

    /// <summary>
    /// Verifies that GetStatus returns safe defaults after disposal.
    /// </summary>
    [Fact]
#pragma warning disable IDISP016 // Don't use disposed instance - intentional test for post-disposal behavior
    public async Task GetStatus_AfterDisposal_ReturnsSafeDefaults()
    {
        // Arrange
        var url = $"{_fixture.BaseUrl}/stream/5000";
        var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(url);
        Assert.True(streamer.Start());
        await TestHelpers.WaitForStreamingAsync(streamer, TimeSpan.FromSeconds(3));

        // Act - intentionally testing behavior after Dispose
        streamer.Dispose();
        var statusAfterDispose = streamer.GetStatus();

        // Assert - should return default status without throwing
        _output.WriteLine($"Status after dispose: State={statusAfterDispose.State}");
        // Default StreamerStatus has State = Idle (0)
        Assert.Equal(StreamerState.Idle, statusAfterDispose.State);
    }
#pragma warning restore IDISP016

    /// <summary>
    /// Verifies that GetMetrics returns null after disposal.
    /// </summary>
    [Fact]
#pragma warning disable IDISP016 // Don't use disposed instance - intentional test for post-disposal behavior
    public async Task GetMetrics_AfterDisposal_ReturnsNull()
    {
        // Arrange
        var url = $"{_fixture.BaseUrl}/stream/5000";
        var streamer = CreateStreamerWithAnalyzer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(url);
        Assert.True(streamer.Start());
        await TestHelpers.WaitForStreamingAsync(streamer, TimeSpan.FromSeconds(3));
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Verify metrics are available before disposal
        var metricsBeforeDispose = streamer.GetMetrics();
        Assert.NotNull(metricsBeforeDispose);

        // Act - intentionally testing behavior after Dispose
        streamer.Dispose();
        var metricsAfterDispose = streamer.GetMetrics();

        // Assert
        Assert.Null(metricsAfterDispose);
        _output.WriteLine("GetMetrics correctly returns null after disposal");
    }
#pragma warning restore IDISP016

    // ========================================================================
    // URL Management Tests
    // ========================================================================

    /// <summary>
    /// Verifies that AddUrl throws on null input.
    /// </summary>
    [Fact]
    public void AddUrl_NullUrl_ThrowsArgumentNullException()
    {
        // Arrange
        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => streamer.AddUrl(null!));
    }

    /// <summary>
    /// Verifies that AddUrlWithScore throws on null input.
    /// </summary>
    [Fact]
    public void AddUrlWithScore_NullUrl_ThrowsArgumentNullException()
    {
        // Arrange
        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => streamer.AddUrlWithScore(null!, 50.0));
    }

    /// <summary>
    /// Verifies that URL operations throw after disposal.
    /// </summary>
    [Fact]
#pragma warning disable IDISP016 // Don't use disposed instance - intentional test for post-disposal behavior
    public void AddUrl_AfterDisposal_ThrowsObjectDisposedException()
    {
        // Arrange
        var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.Dispose();

        // Act & Assert - intentionally testing that disposed streamer throws
        Assert.Throws<ObjectDisposedException>(() => streamer.AddUrl("http://example.com/stream"));
    }
#pragma warning restore IDISP016

    /// <summary>
    /// Verifies that multiple URLs can be added and the count is tracked.
    /// </summary>
    [Fact]
    public async Task AddUrl_MultipleUrls_TrackedInStatus()
    {
        // Arrange
        var url1 = $"{_fixture.BaseUrl}/stream/2000";
        var url2 = $"{_fixture.BaseUrl}/stream/5000";
        var url3 = $"{_fixture.BaseUrl}/stream/10000";

        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        // Act
        streamer.AddUrl(url1);
        streamer.AddUrl(url2);
        streamer.AddUrl(url3);

        Assert.True(streamer.Start());
        await TestHelpers.WaitForStreamingAsync(streamer, TimeSpan.FromSeconds(3));

        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        _output.WriteLine($"URL count: {status.UrlCount}");
        Assert.Equal(3, status.UrlCount);
    }

    // ========================================================================
    // Health Score Tests
    // ========================================================================

    /// <summary>
    /// Verifies that health scores can be set and retrieved.
    /// </summary>
    [Fact]
    public async Task HealthScore_SetAndGet_WorksCorrectly()
    {
        // Arrange
        var url = $"{_fixture.BaseUrl}/stream/5000";

        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        // Act - add URL with specific score
        streamer.AddUrlWithScore(url, 85.0);

        var initialScore = streamer.GetUrlScore(0);
        _output.WriteLine($"Initial score: {initialScore}");

        Assert.True(streamer.Start());
        await TestHelpers.WaitForStreamingAsync(streamer, TimeSpan.FromSeconds(3));

        // Update score
        streamer.UpdateUrlScore(0, 95.0);
        var updatedScore = streamer.GetUrlScore(0);

        streamer.Stop();

        // Assert
        _output.WriteLine($"Updated score: {updatedScore}");
        Assert.True(initialScore >= 80.0 && initialScore <= 90.0, $"Initial score should be ~85, got {initialScore}");
        Assert.True(updatedScore >= 90.0 && updatedScore <= 100.0, $"Updated score should be ~95, got {updatedScore}");
    }

    /// <summary>
    /// Verifies that GetUrlScore returns -1 for invalid index.
    /// </summary>
    [Fact]
    public void GetUrlScore_InvalidIndex_ReturnsNegativeOne()
    {
        // Arrange
        var url = $"{_fixture.BaseUrl}/stream/5000";

        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(url);

        // Act
        var invalidScore = streamer.GetUrlScore(999);

        // Assert
        Assert.Equal(-1.0, invalidScore);
    }

    // ========================================================================
    // RequestSwitch Tests
    // ========================================================================

    /// <summary>
    /// Verifies that RequestSwitch on a single-URL streamer does not crash.
    /// </summary>
    [Fact]
    public async Task RequestSwitch_SingleUrl_HandledGracefully()
    {
        // Arrange
        var url = $"{_fixture.BaseUrl}/stream/5000";

        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(url);
        Assert.True(streamer.Start());
        await TestHelpers.WaitForStreamingAsync(streamer, TimeSpan.FromSeconds(3));

        // Act - request switch with only one URL
        streamer.RequestSwitch();
        await Task.Delay(TimeSpan.FromSeconds(2));

        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert - should not crash, may or may not count as a switch
        _output.WriteLine($"State: {status.State}, Switches: {status.SwitchesCompleted}");
        Assert.True(status.BytesReceived > 0, "Should continue streaming after switch request");
    }

    /// <summary>
    /// Verifies that RequestSwitch before Start does not cause issues.
    /// </summary>
    [Fact]
    public void RequestSwitch_BeforeStart_NoErrors()
    {
        // Arrange
        var url = $"{_fixture.BaseUrl}/stream/5000";

        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(url);

        // Act - request switch before starting
        streamer.RequestSwitch();

        // Assert - should not throw
        var status = streamer.GetStatus();
        Assert.Equal(StreamerState.Idle, status.State);
    }

    /// <summary>
    /// Verifies that RequestSwitch after Stop does not cause issues.
    /// </summary>
    [Fact]
    public async Task RequestSwitch_AfterStop_NoErrors()
    {
        // Arrange
        var url = $"{_fixture.BaseUrl}/stream/5000";

        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(url);
        Assert.True(streamer.Start());
        await TestHelpers.WaitForStreamingAsync(streamer, TimeSpan.FromSeconds(2));
        streamer.Stop();

        // Act - request switch after stopping
        streamer.RequestSwitch();

        // Assert - should not throw
        _output.WriteLine("RequestSwitch after Stop completed without errors");
    }

    // ========================================================================
    // Concurrent Access Tests
    // ========================================================================

    /// <summary>
    /// Verifies that GetStatus can be called concurrently from multiple threads.
    /// </summary>
    [Fact]
    public async Task GetStatus_ConcurrentCalls_ThreadSafe()
    {
        // Arrange
        var url = $"{_fixture.BaseUrl}/stream/5000";

        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(url);
        Assert.True(streamer.Start());
        await TestHelpers.WaitForStreamingAsync(streamer, TimeSpan.FromSeconds(3));

        // Act - concurrent status calls
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var tasks = Enumerable
            .Range(0, 10)
            .Select(_ =>
                Task.Run(
                    async () =>
                    {
                        int callCount = 0;
                        while (!cts.IsCancellationRequested)
                        {
                            var status = streamer.GetStatus();
                            callCount++;
                            await Task.Delay(1);
                        }
                        return callCount;
                    },
                    cts.Token
                )
            )
            .ToList();

        var results = await Task.WhenAll(tasks);
        streamer.Stop();

        // Assert
        var totalCalls = results.Sum();
        _output.WriteLine($"Total concurrent GetStatus calls: {totalCalls}");
        Assert.True(totalCalls > 1000, $"Should have made many concurrent calls, got {totalCalls}");
    }

    // ========================================================================
    // Helper Methods
    // ========================================================================

    private static NativeStreamer? CreateStreamer()
    {
        return NativeStreamer.TryCreate(TsDuckStreamerConfigNative.Default);
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
}
