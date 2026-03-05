using System;
using System.Threading.Tasks;
using Jellyfin.Xtream.Service.Streaming.Native;
using Xunit;

namespace Jellyfin.Xtream.E2ETests.Infrastructure;

/// <summary>
/// Assertion helpers for provider health E2E tests.
/// </summary>
internal static class ProviderHealthAssertions
{
    /// <summary>
    /// Waits for a provider to be ejected and asserts success.
    /// </summary>
    /// <param name="streamer">The streamer to check.</param>
    /// <param name="providerIndex">Index of the provider.</param>
    /// <param name="timeout">Maximum time to wait.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    public static async Task AssertProviderEjectedAsync(
        NativeStreamer streamer,
        int providerIndex,
        TimeSpan timeout,
        string because = ""
    )
    {
        var ejected = await TestHelpers.WaitForConditionAsync(
            () => streamer.GetProviderState(providerIndex) == ProviderState.Ejected,
            timeout
        );

        Assert.True(ejected, $"Provider {providerIndex} should be ejected within {timeout.TotalSeconds}s. {because}");
    }

    /// <summary>
    /// Waits for a provider to enter probation and asserts success.
    /// </summary>
    /// <param name="streamer">The streamer to check.</param>
    /// <param name="providerIndex">Index of the provider.</param>
    /// <param name="timeout">Maximum time to wait.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    public static async Task AssertProviderInProbationAsync(
        NativeStreamer streamer,
        int providerIndex,
        TimeSpan timeout,
        string because = ""
    )
    {
        var inProbation = await TestHelpers.WaitForConditionAsync(
            () =>
            {
                // The streaming loop doesn't re-check ejection expiry once connected
                // to a healthy provider. Trigger the check explicitly.
                streamer.CheckRecovery();
                return streamer.GetProviderState(providerIndex) == ProviderState.Probation;
            },
            timeout
        );

        Assert.True(
            inProbation,
            $"Provider {providerIndex} should be in probation within {timeout.TotalSeconds}s. {because}"
        );
    }

    /// <summary>
    /// Waits for a provider to recover to Active state and asserts success.
    /// </summary>
    /// <param name="streamer">The streamer to check.</param>
    /// <param name="providerIndex">Index of the provider.</param>
    /// <param name="timeout">Maximum time to wait.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    public static async Task AssertProviderRecoveredAsync(
        NativeStreamer streamer,
        int providerIndex,
        TimeSpan timeout,
        string because = ""
    )
    {
        var recovered = await TestHelpers.WaitForConditionAsync(
            () =>
            {
                // The streaming loop doesn't re-check ejection expiry once connected
                // to a healthy provider. Trigger the check explicitly.
                streamer.CheckRecovery();
                return streamer.GetProviderState(providerIndex) == ProviderState.Active;
            },
            timeout
        );

        Assert.True(
            recovered,
            $"Provider {providerIndex} should recover to Active within {timeout.TotalSeconds}s. {because}"
        );
    }

    /// <summary>
    /// Asserts that a provider has been isolated at least the specified number of times.
    /// </summary>
    /// <param name="streamer">The streamer to check.</param>
    /// <param name="providerIndex">Index of the provider.</param>
    /// <param name="minIsolations">Minimum expected isolation count.</param>
    public static void AssertMinIsolations(NativeStreamer streamer, int providerIndex, int minIsolations)
    {
        var isolatedTimes = streamer.GetIsolatedTimes(providerIndex);
        Assert.True(
            isolatedTimes >= minIsolations,
            $"Provider {providerIndex} should have been isolated at least {minIsolations} times, but was {isolatedTimes}"
        );
    }

    /// <summary>
    /// Asserts that a provider has a success rate within the expected range.
    /// </summary>
    /// <param name="streamer">The streamer to check.</param>
    /// <param name="providerIndex">Index of the provider.</param>
    /// <param name="minRate">Minimum expected success rate (0.0-1.0).</param>
    /// <param name="maxRate">Maximum expected success rate (0.0-1.0).</param>
    public static void AssertSuccessRateInRange(
        NativeStreamer streamer,
        int providerIndex,
        double minRate,
        double maxRate
    )
    {
        var health = streamer.GetProviderHealth(providerIndex);
        Assert.NotNull(health);

        var rate = health.Value.SuccessRate;
        Assert.True(
            rate >= minRate && rate <= maxRate,
            $"Provider {providerIndex} success rate {rate:P1} should be between {minRate:P1} and {maxRate:P1}"
        );
    }

    /// <summary>
    /// Asserts that a provider's latency EWMA is within the expected range.
    /// </summary>
    /// <param name="streamer">The streamer to check.</param>
    /// <param name="providerIndex">Index of the provider.</param>
    /// <param name="minLatencyMs">Minimum expected latency in milliseconds.</param>
    /// <param name="maxLatencyMs">Maximum expected latency in milliseconds.</param>
    public static void AssertLatencyInRange(
        NativeStreamer streamer,
        int providerIndex,
        double minLatencyMs,
        double maxLatencyMs
    )
    {
        var health = streamer.GetProviderHealth(providerIndex);
        Assert.NotNull(health);

        var latency = health.Value.LatencyEwmaMs;
        Assert.True(
            latency >= minLatencyMs && latency <= maxLatencyMs,
            $"Provider {providerIndex} latency {latency:F1}ms should be between {minLatencyMs:F1}ms and {maxLatencyMs:F1}ms"
        );
    }
}

/// <summary>
/// Helper for verifying selection distribution across providers.
/// </summary>
internal sealed class SelectionDistributionVerifier
{
    private readonly int[] _counts;
    private int _total;

    /// <summary>
    /// Initializes a new instance of the <see cref="SelectionDistributionVerifier"/> class.
    /// </summary>
    /// <param name="providerCount">Number of providers to track.</param>
    public SelectionDistributionVerifier(int providerCount)
    {
        _counts = new int[providerCount];
    }

    /// <summary>
    /// Records a provider selection.
    /// </summary>
    /// <param name="providerIndex">Index of the selected provider.</param>
    public void RecordSelection(int providerIndex)
    {
        if (providerIndex >= 0 && providerIndex < _counts.Length)
        {
            _counts[providerIndex]++;
            _total++;
        }
    }

    /// <summary>
    /// Gets the selection ratio for a provider.
    /// </summary>
    /// <param name="providerIndex">Index of the provider.</param>
    /// <returns>Selection ratio (0.0 to 1.0).</returns>
    public double GetRatio(int providerIndex)
    {
        return _total > 0 ? _counts[providerIndex] / (double)_total : 0;
    }

    /// <summary>
    /// Gets the total selection count.
    /// </summary>
    public int Total => _total;

    /// <summary>
    /// Asserts that a provider is selected at least the specified ratio of the time.
    /// </summary>
    /// <param name="providerIndex">Index of the preferred provider.</param>
    /// <param name="minRatio">Minimum expected selection ratio.</param>
    public void AssertPreference(int providerIndex, double minRatio)
    {
        var actual = GetRatio(providerIndex);
        Assert.True(
            actual >= minRatio,
            $"Provider {providerIndex} should be selected >= {minRatio:P0}, actual: {actual:P0}"
        );
    }

    /// <summary>
    /// Asserts that all providers get some traffic (no provider is completely starved).
    /// </summary>
    /// <param name="minSelectionsPerProvider">Minimum selections expected per provider.</param>
    public void AssertAllProvidersReceiveTraffic(int minSelectionsPerProvider = 1)
    {
        for (int i = 0; i < _counts.Length; i++)
        {
            Assert.True(
                _counts[i] >= minSelectionsPerProvider,
                $"Provider {i} should have at least {minSelectionsPerProvider} selections, but had {_counts[i]}"
            );
        }
    }

    /// <summary>
    /// Asserts that the distribution matches expected ratios within tolerance.
    /// </summary>
    /// <param name="expectedRatios">Expected ratio for each provider.</param>
    /// <param name="tolerance">Allowed deviation from expected ratio.</param>
    public void AssertDistribution(double[] expectedRatios, double tolerance = 0.1)
    {
        for (int i = 0; i < expectedRatios.Length && i < _counts.Length; i++)
        {
            var actual = GetRatio(i);
            var expected = expectedRatios[i];
            Assert.True(
                Math.Abs(actual - expected) <= tolerance,
                $"Provider {i} ratio {actual:P1} should be within {tolerance:P0} of {expected:P1}"
            );
        }
    }
}
