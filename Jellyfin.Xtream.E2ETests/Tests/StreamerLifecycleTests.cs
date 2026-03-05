using Jellyfin.Xtream.E2ETests.Infrastructure;
using Jellyfin.Xtream.Service.Streaming.Native;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.E2ETests.Tests;

/// <summary>
/// Tests for NativeStreamer lifecycle management, resource cleanup, and error handling.
/// Verifies proper behavior during start/stop cycles, disposal, and edge cases.
/// </summary>
[Collection("E2E-Streaming")]
public class StreamerLifecycleTests(DockerTestFixture fixture, ITestOutputHelper output) : NativeE2ETestBase(output)
{
    // ========================================================================
    // Start/Stop Cycle Tests
    // ========================================================================

    /// <summary>
    /// Verifies that calling Start() without any URLs returns false.
    /// </summary>
    [Fact]
    public void Start_WithNoUrls_ReturnsFalse()
    {
        // Act - start without adding URLs
        var started = Streamer.Start();

        // Assert
        Assert.False(started, "Start() should return false when no URLs are configured");

        var status = Streamer.GetStatus();
        Output.WriteLine($"State after failed start: {status.State}");
        Assert.Equal(StreamerState.Idle, status.State);
    }

    /// <summary>
    /// Verifies that calling Start() twice returns false on the second call.
    /// </summary>
    [Fact]
    public async Task Start_CalledTwice_SecondCallReturnsFalse()
    {
        // Arrange
        var url = $"{fixture.BaseUrl}/stream/5000";

        Streamer.AddUrl(url);

        // Act
        var firstStart = Streamer.Start();
        await TestHelpers.WaitForStreamingAsync(Streamer, TimeSpan.FromSeconds(3));

        var secondStart = Streamer.Start();
        Streamer.Stop();

        // Assert
        Output.WriteLine($"First Start: {firstStart}, Second Start: {secondStart}");
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
        var url = $"{fixture.BaseUrl}/stream/5000";

        Streamer.AddUrl(url);
        Assert.True(Streamer.Start());
        await TestHelpers.WaitForStreamingAsync(Streamer, TimeSpan.FromSeconds(3));

        // Act - stop multiple times
        Streamer.Stop();
        Streamer.Stop();
        Streamer.Stop();

        // Assert - should not throw, status should be valid
        var status = Streamer.GetStatus();
        Output.WriteLine($"State after multiple stops: {status.State}");
        Assert.True(status.BytesReceived > 0, "Should have received some data before stopping");
    }

    /// <summary>
    /// Verifies that Stop() can be called on a streamer that was never started.
    /// </summary>
    [Fact]
    public void Stop_NeverStarted_NoErrors()
    {
        // Act - stop without starting
        Streamer.Stop();

        // Assert - should not throw
        var status = Streamer.GetStatus();
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
        var url = $"{fixture.BaseUrl}/stream/5000";

        Streamer.AddUrl(url);

        // Act - first cycle
        Assert.True(Streamer.Start(), "First start should succeed");
        await TestHelpers.WaitForStreamingAsync(Streamer, TimeSpan.FromSeconds(3));
        var bytesFirstRun = Streamer.GetStatus().BytesReceived;
        Streamer.Stop();

        Output.WriteLine($"First run bytes: {bytesFirstRun:N0}");

        // Second cycle
        Assert.True(Streamer.Start(), "Second start should succeed after stop");
        await TestHelpers.WaitForStreamingAsync(Streamer, TimeSpan.FromSeconds(3));
        await Task.Delay(TimeSpan.FromSeconds(2));

        var statusSecondRun = Streamer.GetStatus();
        Streamer.Stop();

        // Assert
        Output.WriteLine($"Second run state: {statusSecondRun.State}, bytes: {statusSecondRun.BytesReceived:N0}");
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
        var url = $"{fixture.BaseUrl}/stream/5000";
        StreamerStatus? statusBeforeDispose = null;

        // Act - use a block scope to force disposal
        {
            var streamer = BuildStreamer(TestConfigs.Default);
            streamer.AddUrl(url);
            Assert.True(streamer.Start());
            await TestHelpers.WaitForStreamingAsync(streamer, TimeSpan.FromSeconds(3));
            await Task.Delay(TimeSpan.FromSeconds(1));

            statusBeforeDispose = streamer.GetStatus();
            Output.WriteLine(
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
        var streamer = BuildStreamer(TestConfigs.Default);

        // Act - intentionally calling Dispose multiple times to verify it's safe
        streamer.Dispose();
        streamer.Dispose();
        streamer.Dispose();

        // Assert - no exception thrown
        Output.WriteLine("Multiple Dispose calls succeeded without errors");
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
        var url = $"{fixture.BaseUrl}/stream/5000";
        var streamer = BuildStreamer(TestConfigs.Default);
        streamer.AddUrl(url);
        Assert.True(streamer.Start());
        await TestHelpers.WaitForStreamingAsync(streamer, TimeSpan.FromSeconds(3));

        // Act - intentionally testing behavior after Dispose
        streamer.Dispose();
        var statusAfterDispose = streamer.GetStatus();

        // Assert - should return default status without throwing
        Output.WriteLine($"Status after dispose: State={statusAfterDispose.State}");
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
        var url = $"{fixture.BaseUrl}/stream/5000";
        var streamer = BuildStreamer(TestConfigs.Analyzer);
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
        Output.WriteLine("GetMetrics correctly returns null after disposal");
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
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => Streamer.AddUrl(null!));
    }

    /// <summary>
    /// Verifies that AddUrlWithScore throws on null input.
    /// </summary>
    [Fact]
    public void AddUrlWithScore_NullUrl_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => Streamer.AddUrlWithScore(null!, 50.0));
    }

    /// <summary>
    /// Verifies that URL operations throw after disposal.
    /// </summary>
    [Fact]
#pragma warning disable IDISP016 // Don't use disposed instance - intentional test for post-disposal behavior
    public void AddUrl_AfterDisposal_ThrowsObjectDisposedException()
    {
        // Arrange
        var streamer = BuildStreamer(TestConfigs.Default);
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
        var url1 = $"{fixture.BaseUrl}/stream/2000";
        var url2 = $"{fixture.BaseUrl}/stream/5000";
        var url3 = $"{fixture.BaseUrl}/stream/10000";

        // Act
        Streamer.AddUrl(url1);
        Streamer.AddUrl(url2);
        Streamer.AddUrl(url3);

        Assert.True(Streamer.Start());
        await TestHelpers.WaitForStreamingAsync(Streamer, TimeSpan.FromSeconds(3));

        var status = Streamer.GetStatus();
        Streamer.Stop();

        // Assert
        Output.WriteLine($"URL count: {status.UrlCount}");
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
        var url = $"{fixture.BaseUrl}/stream/5000";

        // Act - add URL with specific score
        Streamer.AddUrlWithScore(url, 85.0);

        var initialScore = Streamer.GetUrlScore(0);
        Output.WriteLine($"Initial score: {initialScore}");

        Assert.True(Streamer.Start());
        await TestHelpers.WaitForStreamingAsync(Streamer, TimeSpan.FromSeconds(3));

        // Update score
        Streamer.UpdateUrlScore(0, 95.0);
        var updatedScore = Streamer.GetUrlScore(0);

        Streamer.Stop();

        // Assert
        Output.WriteLine($"Updated score: {updatedScore}");
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
        var url = $"{fixture.BaseUrl}/stream/5000";

        Streamer.AddUrl(url);

        // Act
        var invalidScore = Streamer.GetUrlScore(999);

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
        var url = $"{fixture.BaseUrl}/stream/5000";

        Streamer.AddUrl(url);
        Assert.True(Streamer.Start());
        await TestHelpers.WaitForStreamingAsync(Streamer, TimeSpan.FromSeconds(3));

        // Act - request switch with only one URL
        Streamer.RequestSwitch();
        await Task.Delay(TimeSpan.FromSeconds(2));

        var status = Streamer.GetStatus();
        Streamer.Stop();

        // Assert - should not crash, may or may not count as a switch
        Output.WriteLine($"State: {status.State}, Switches: {status.SwitchesCompleted}");
        Assert.True(status.BytesReceived > 0, "Should continue streaming after switch request");
    }

    /// <summary>
    /// Verifies that RequestSwitch before Start does not cause issues.
    /// </summary>
    [Fact]
    public void RequestSwitch_BeforeStart_NoErrors()
    {
        // Arrange
        var url = $"{fixture.BaseUrl}/stream/5000";

        Streamer.AddUrl(url);

        // Act - request switch before starting
        Streamer.RequestSwitch();

        // Assert - should not throw
        var status = Streamer.GetStatus();
        Assert.Equal(StreamerState.Idle, status.State);
    }

    /// <summary>
    /// Verifies that RequestSwitch after Stop does not cause issues.
    /// </summary>
    [Fact]
    public async Task RequestSwitch_AfterStop_NoErrors()
    {
        // Arrange
        var url = $"{fixture.BaseUrl}/stream/5000";

        Streamer.AddUrl(url);
        Assert.True(Streamer.Start());
        await TestHelpers.WaitForStreamingAsync(Streamer, TimeSpan.FromSeconds(2));
        Streamer.Stop();

        // Act - request switch after stopping
        Streamer.RequestSwitch();

        // Assert - should not throw
        Output.WriteLine("RequestSwitch after Stop completed without errors");
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
        var url = $"{fixture.BaseUrl}/stream/5000";

        Streamer.AddUrl(url);
        Assert.True(Streamer.Start());
        await TestHelpers.WaitForStreamingAsync(Streamer, TimeSpan.FromSeconds(3));

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
                            var status = Streamer.GetStatus();
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
        Streamer.Stop();

        // Assert
        var totalCalls = results.Sum();
        Output.WriteLine($"Total concurrent GetStatus calls: {totalCalls}");
        Assert.True(totalCalls > 1000, $"Should have made many concurrent calls, got {totalCalls}");
    }
}
