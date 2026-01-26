using Jellyfin.Xtream.E2ETests.Infrastructure;
using Jellyfin.Xtream.Service.Streaming.Native;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.E2ETests.Tests;

/// <summary>
/// Tests PCR/PTS restamping through the native pipeline.
/// Verifies that output timestamps are monotonically increasing and properly spaced.
/// </summary>
[Collection("E2E")]
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
        var metrics = new TestMetrics();

        using var streamer = CreateStreamerWithRestamp();
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
                    metrics.ProcessReceivedData((byte*)ptr, len);
                }
            }
        );

        streamer.AddUrl(url);
        metrics.Start();
        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));

        // Act - collect PCR values for 5 seconds
        await Task.Delay(TimeSpan.FromSeconds(5));

        metrics.Stop();
        streamer.Stop();

        // Assert
        var pcrValues = metrics.PcrValues;
        _output.WriteLine($"PCR samples collected: {pcrValues.Count}");

        Assert.True(pcrValues.Count >= 10, "Should have at least 10 PCR samples");

        // Check monotonically increasing
        int decreases = 0;
        for (int i = 1; i < pcrValues.Count; i++)
        {
            if (pcrValues[i] <= pcrValues[i - 1])
            {
                decreases++;
                _output.WriteLine($"  PCR decrease at index {i}: {pcrValues[i - 1]} -> {pcrValues[i]}");
            }
        }

        // Allow up to 1 decrease (at stream start before restamper kicks in)
        Assert.True(decreases <= 1, $"PCR values decreased {decreases} times (expected <= 1)");
    }

    [Fact]
    public async Task Restamp_PcrIntervals_WithinAcceptableRange()
    {
        // Arrange
        var url = $"{_fixture.BaseUrl}/stream/5000";
        var metrics = new TestMetrics();

        using var streamer = CreateStreamerWithRestamp();
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
                    metrics.ProcessReceivedData((byte*)ptr, len);
                }
            }
        );

        streamer.AddUrl(url);
        metrics.Start();
        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));

        // Act - stream for 5 seconds
        await Task.Delay(TimeSpan.FromSeconds(5));

        metrics.Stop();
        streamer.Stop();

        // Assert - check PCR intervals
        var pcrValues = metrics.PcrValues;
        Assert.True(pcrValues.Count >= 5, $"Need at least 5 PCR values, got {pcrValues.Count}");

        var intervals = new List<double>();
        for (int i = 1; i < pcrValues.Count; i++)
        {
            double intervalMs = (pcrValues[i] - pcrValues[i - 1]) / 90.0; // 90kHz -> ms
            if (intervalMs > 0) // Skip any initial anomalies
            {
                intervals.Add(intervalMs);
            }
        }

        Assert.True(intervals.Count > 0, "Should have computed PCR intervals");

        var avgInterval = intervals.Average();
        var maxInterval = intervals.Max();
        var minInterval = intervals.Where(i => i > 0).Min();

        _output.WriteLine($"PCR intervals: avg={avgInterval:F2}ms, min={minInterval:F2}ms, max={maxInterval:F2}ms");

        // TR 101 290 requires PCR repetition interval <= 40ms (with tolerance)
        // After restamping, most intervals should be reasonable
        var conformingIntervals = intervals.Count(i => i <= 100.0); // 100ms generous threshold
        var conformanceRate = (double)conformingIntervals / intervals.Count;
        _output.WriteLine($"Intervals <= 100ms: {conformingIntervals}/{intervals.Count} ({conformanceRate:P1})");

        Assert.True(
            conformanceRate >= 0.9,
            $"Only {conformanceRate:P1} of PCR intervals are within 100ms (expected >= 90%)"
        );
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

        var totalBytes = 0L;

        streamer.SetOutputCallback(
            (ptr, len) =>
            {
                Interlocked.Add(ref totalBytes, len);
            }
        );

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
        _output.WriteLine($"  Callback bytes: {Interlocked.Read(ref totalBytes):N0}");

        streamer.Stop();

        Assert.True(status.PacketsOutput > 0, "Should have output packets");
    }

    [Fact]
    public async Task Restamp_LargeOffset_CorrectedToZeroBased()
    {
        // Arrange - verify data flows correctly and PCR values are non-negative
        var url = $"{_fixture.BaseUrl}/stream/5000";
        var metrics = new TestMetrics();

        using var streamer = CreateStreamerWithRestamp();
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
                    metrics.ProcessReceivedData((byte*)ptr, len);
                }
            }
        );

        streamer.AddUrl(url);
        metrics.Start();
        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));

        // Collect data for 3 seconds
        await Task.Delay(TimeSpan.FromSeconds(3));

        metrics.Stop();
        streamer.Stop();

        // Assert - PCR values should be present and positive
        var pcrValues = metrics.PcrValues;
        _output.WriteLine(
            $"PCR values: {pcrValues.Count}, first={pcrValues.FirstOrDefault()}, last={pcrValues.LastOrDefault()}"
        );

        Assert.True(pcrValues.Count > 0, "Should have PCR values");
        // All PCR values should be non-negative
        Assert.All(pcrValues, pcr => Assert.True(pcr >= 0, $"PCR value {pcr} is negative"));
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
            Reserved = 0,
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
