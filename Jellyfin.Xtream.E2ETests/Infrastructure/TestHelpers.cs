using System.Diagnostics;
using Jellyfin.Xtream.Service.Streaming.Native;

namespace Jellyfin.Xtream.E2ETests.Infrastructure;

/// <summary>
/// Shared test helper methods for E2E tests.
/// Provides consistent polling patterns with proper timeout handling and descriptive error messages.
/// </summary>
public static class TestHelpers
{
    /// <summary>
    /// Default poll interval for status checks.
    /// </summary>
    public const int DefaultPollIntervalMs = 50;

    /// <summary>
    /// Default timeout for connection establishment.
    /// </summary>
    public static readonly TimeSpan DefaultConnectionTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Default timeout for streaming operations.
    /// </summary>
    public static readonly TimeSpan DefaultStreamingTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Waits for the streamer to reach the Streaming state.
    /// </summary>
    /// <param name="streamer">The streamer instance to monitor.</param>
    /// <param name="timeout">Maximum time to wait.</param>
    /// <param name="pollIntervalMs">Interval between status checks in milliseconds.</param>
    /// <returns>True if streaming state was reached, false if timed out.</returns>
    public static async Task<bool> WaitForStreamingAsync(
        NativeStreamer streamer,
        TimeSpan timeout,
        int pollIntervalMs = DefaultPollIntervalMs
    )
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var status = streamer.GetStatus();
            if (status.State == StreamerState.Streaming)
            {
                return true;
            }

            await Task.Delay(pollIntervalMs).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Waits for the streamer to reach the Streaming state and throws if timed out.
    /// </summary>
    /// <param name="streamer">The streamer instance to monitor.</param>
    /// <param name="timeout">Maximum time to wait.</param>
    /// <param name="context">Optional context string for the error message.</param>
    /// <exception cref="TimeoutException">Thrown if the timeout is exceeded.</exception>
    public static async Task WaitForStreamingOrThrowAsync(
        NativeStreamer streamer,
        TimeSpan timeout,
        string? context = null
    )
    {
        if (!await WaitForStreamingAsync(streamer, timeout).ConfigureAwait(false))
        {
            var status = streamer.GetStatus();
            var contextMsg = string.IsNullOrEmpty(context) ? "" : $" ({context})";
            throw new TimeoutException(
                $"Timed out waiting for streaming state after {timeout.TotalSeconds:F1}s{contextMsg}. "
                    + $"Current state: {status.State}, Bytes: {status.BytesReceived}, Errors: {status.RetryCount}"
            );
        }
    }

    /// <summary>
    /// Waits for the streamer to receive at least the specified number of bytes.
    /// </summary>
    /// <param name="streamer">The streamer instance to monitor.</param>
    /// <param name="minBytes">Minimum bytes to wait for.</param>
    /// <param name="timeout">Maximum time to wait.</param>
    /// <param name="pollIntervalMs">Interval between status checks in milliseconds.</param>
    /// <returns>True if the byte count was reached, false if timed out.</returns>
    public static async Task<bool> WaitForBytesReceivedAsync(
        NativeStreamer streamer,
        long minBytes,
        TimeSpan timeout,
        int pollIntervalMs = DefaultPollIntervalMs
    )
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var status = streamer.GetStatus();
            if (status.BytesReceived >= minBytes)
            {
                return true;
            }

            await Task.Delay(pollIntervalMs).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Waits for the streamer to switch to a specific URL index.
    /// </summary>
    /// <param name="streamer">The streamer instance to monitor.</param>
    /// <param name="expectedUrlIndex">The expected URL index after switching.</param>
    /// <param name="timeout">Maximum time to wait.</param>
    /// <param name="pollIntervalMs">Interval between status checks in milliseconds.</param>
    /// <returns>True if the switch occurred, false if timed out.</returns>
    public static async Task<bool> WaitForUrlSwitchAsync(
        NativeStreamer streamer,
        int expectedUrlIndex,
        TimeSpan timeout,
        int pollIntervalMs = DefaultPollIntervalMs
    )
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var status = streamer.GetStatus();
            if (status.CurrentUrlIndex == expectedUrlIndex)
            {
                return true;
            }

            await Task.Delay(pollIntervalMs).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Waits for the streamer to complete at least the specified number of switches.
    /// </summary>
    /// <param name="streamer">The streamer instance to monitor.</param>
    /// <param name="minSwitches">Minimum number of switches to wait for.</param>
    /// <param name="timeout">Maximum time to wait.</param>
    /// <param name="pollIntervalMs">Interval between status checks in milliseconds.</param>
    /// <returns>True if the switch count was reached, false if timed out.</returns>
    public static async Task<bool> WaitForSwitchCountAsync(
        NativeStreamer streamer,
        int minSwitches,
        TimeSpan timeout,
        int pollIntervalMs = DefaultPollIntervalMs
    )
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var status = streamer.GetStatus();
            if (status.SwitchesCompleted >= minSwitches)
            {
                return true;
            }

            await Task.Delay(pollIntervalMs).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Waits for the streamer to finish (terminal state or stopped after receiving data).
    /// Useful for finite streams.
    /// </summary>
    /// <param name="streamer">The streamer instance to monitor.</param>
    /// <param name="timeout">Maximum time to wait.</param>
    /// <param name="pollIntervalMs">Interval between status checks in milliseconds.</param>
    /// <returns>True if the streamer finished, false if timed out.</returns>
    public static async Task<bool> WaitForStreamerFinishAsync(
        NativeStreamer streamer,
        TimeSpan timeout,
        int pollIntervalMs = 100
    )
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var status = streamer.GetStatus();
            if (status.IsTerminal || (status.BytesReceived > 0 && status.State == StreamerState.Idle))
            {
                return true;
            }

            await Task.Delay(pollIntervalMs).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Waits for a condition to become true by polling.
    /// </summary>
    /// <param name="condition">The condition to check.</param>
    /// <param name="timeout">Maximum time to wait.</param>
    /// <param name="pollIntervalMs">Interval between checks in milliseconds.</param>
    /// <returns>True if the condition became true, false if timed out.</returns>
    public static async Task<bool> WaitForConditionAsync(
        Func<bool> condition,
        TimeSpan timeout,
        int pollIntervalMs = DefaultPollIntervalMs
    )
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(pollIntervalMs).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Waits for an async condition to become true by polling.
    /// </summary>
    /// <param name="condition">The async condition to check.</param>
    /// <param name="timeout">Maximum time to wait.</param>
    /// <param name="pollIntervalMs">Interval between checks in milliseconds.</param>
    /// <returns>True if the condition became true, false if timed out.</returns>
    public static async Task<bool> WaitForConditionAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout,
        int pollIntervalMs = DefaultPollIntervalMs
    )
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (await condition().ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(pollIntervalMs).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Polls the streamer status and collects samples over a duration.
    /// Useful for observing state changes and data flow.
    /// </summary>
    /// <param name="streamer">The streamer instance to monitor.</param>
    /// <param name="duration">Duration to collect samples.</param>
    /// <param name="pollIntervalMs">Interval between samples in milliseconds.</param>
    /// <returns>List of status snapshots collected during the period.</returns>
    public static async Task<List<StreamerStatus>> CollectStatusSamplesAsync(
        NativeStreamer streamer,
        TimeSpan duration,
        int pollIntervalMs = 100
    )
    {
        var samples = new List<StreamerStatus>();
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < duration)
        {
            samples.Add(streamer.GetStatus());
            await Task.Delay(pollIntervalMs).ConfigureAwait(false);
        }

        return samples;
    }

    /// <summary>
    /// Measures the time to first byte received from the streamer.
    /// </summary>
    /// <param name="streamer">The streamer instance (should not be started yet).</param>
    /// <param name="timeout">Maximum time to wait for first byte.</param>
    /// <param name="pollIntervalMs">Interval between status checks in milliseconds.</param>
    /// <returns>Time to first byte in milliseconds, or -1 if timed out.</returns>
    public static async Task<double> MeasureFirstByteLatencyAsync(
        NativeStreamer streamer,
        TimeSpan timeout,
        int pollIntervalMs = 5
    )
    {
        var sw = Stopwatch.StartNew();
        if (!streamer.Start())
        {
            return -1;
        }

        while (sw.Elapsed < timeout)
        {
            var status = streamer.GetStatus();
            if (status.BytesReceived > 0)
            {
                sw.Stop();
                return sw.Elapsed.TotalMilliseconds;
            }

            await Task.Delay(pollIntervalMs).ConfigureAwait(false);
        }

        return -1;
    }

    /// <summary>
    /// Verifies that data continues to flow by checking that bytes received increases.
    /// </summary>
    /// <param name="streamer">The streamer instance to monitor.</param>
    /// <param name="duration">Duration to monitor for data flow.</param>
    /// <param name="maxGapMs">Maximum allowed gap between data arrivals in milliseconds.</param>
    /// <param name="pollIntervalMs">Interval between checks in milliseconds.</param>
    /// <returns>True if data flowed continuously (no gaps exceeding maxGapMs), false otherwise.</returns>
    public static async Task<(bool Success, double MaxGapMs)> VerifyDataFlowContinuityAsync(
        NativeStreamer streamer,
        TimeSpan duration,
        double maxGapMs,
        int pollIntervalMs = 100
    )
    {
        var sw = Stopwatch.StartNew();
        var lastBytes = streamer.GetStatus().BytesReceived;
        var lastDataTime = DateTime.UtcNow;
        var observedMaxGapMs = 0.0;

        while (sw.Elapsed < duration)
        {
            await Task.Delay(pollIntervalMs).ConfigureAwait(false);

            var status = streamer.GetStatus();
            if (status.BytesReceived > lastBytes)
            {
                var now = DateTime.UtcNow;
                var gap = (now - lastDataTime).TotalMilliseconds;
                if (gap > observedMaxGapMs && lastBytes > 0)
                {
                    observedMaxGapMs = gap;
                }

                lastDataTime = now;
                lastBytes = status.BytesReceived;
            }
        }

        return (observedMaxGapMs <= maxGapMs, observedMaxGapMs);
    }

    /// <summary>
    /// Calculates percentile from a sorted list of values.
    /// </summary>
    /// <param name="sortedValues">Sorted list of values.</param>
    /// <param name="percentile">Percentile to calculate (0-100).</param>
    /// <returns>The value at the specified percentile.</returns>
    public static double GetPercentile(IReadOnlyList<double> sortedValues, int percentile)
    {
        if (sortedValues.Count == 0)
        {
            return 0;
        }

        var index = (int)Math.Ceiling(percentile / 100.0 * sortedValues.Count) - 1;
        return sortedValues[Math.Max(0, Math.Min(index, sortedValues.Count - 1))];
    }
}
