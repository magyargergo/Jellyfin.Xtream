// Copyright (C) 2025  Gergo Magyar

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Utility;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.ProviderManagement;

/// <summary>
/// Automatic failover service that selects optimal providers based on
/// combined health metrics, circuit breaker state, performance data,
/// and predictive health trend analysis.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="AutomaticFailoverService"/> class.
/// </remarks>
/// <param name="healthScorer">The provider health scorer for availability and scoring.</param>
/// <param name="circuitBreaker">The circuit breaker service for state queries.</param>
/// <param name="metrics">The provider metrics tracker.</param>
/// <param name="trends">The health trend tracker.</param>
/// <param name="logger">The logger instance.</param>
/// <param name="configProvider">Configuration provider function.</param>
public sealed class AutomaticFailoverService(
    IProviderHealthScorer healthScorer,
    ICircuitBreakerService circuitBreaker,
    IProviderMetricsTracker metrics,
    IHealthTrendTracker trends,
    ILogger logger,
    Func<PluginConfiguration?>? configProvider = null
) : IAutomaticFailoverService
{
    private readonly IProviderHealthScorer _healthScorer = healthScorer;
    private readonly ICircuitBreakerService _circuitBreaker = circuitBreaker;
    private readonly IProviderMetricsTracker _metrics = metrics;
    private readonly IHealthTrendTracker _trends = trends;
    private readonly ILogger _logger = logger;
    private readonly Func<PluginConfiguration?> _configProvider = configProvider ?? GetDefaultConfiguration;

    /// <summary>
    /// Gets the health trend tracker for external access.
    /// </summary>
    public IHealthTrendTracker TrendTracker => _trends;

    /// <summary>
    /// Gets providers ordered by combined health score for optimal failover.
    /// Combines circuit breaker state, capacity, latency, throughput, and error rates.
    /// </summary>
    /// <param name="providers">Available providers for the channel.</param>
    /// <returns>Providers ordered by combined health score (best first).</returns>
    public IReadOnlyList<ProviderStreamInfo> GetOrderedProviders(IEnumerable<ProviderStreamInfo> providers)
    {
        var config = _configProvider();
        var skipUnavailable = config?.SkipUnavailableProviders ?? true;

        // Delegate to the optimized resilience service for base sorting
        var baseSorted = _healthScorer.GetSortedProviders(providers, forceIncludeAll: !skipUnavailable);

        if (baseSorted.Count == 0)
        {
            return baseSorted;
        }

        // Re-score with combined metrics for final ordering
        var count = baseSorted.Count;
        var scoredArray = ArrayPool<(
            ProviderStreamInfo Provider,
            int Combined,
            int Resilience,
            int Metrics
        )>.Shared.Rent(count);
        try
        {
            for (var i = 0; i < count; i++)
            {
                var p = baseSorted[i];
                var resilienceScore = _healthScorer.GetSelectionScore(p.Provider.Id);
                var metricsScore = _metrics.CalculateHealthScore(p.Provider.Id);
                var isAvailable = _healthScorer.IsAvailable(p.Provider.Id);
                var combined = CalculateCombinedScore(resilienceScore, metricsScore, isAvailable);
                scoredArray[i] = (p, combined, resilienceScore, metricsScore);
            }

            // Sort by combined score descending, then resilience, then name
            Array.Sort(scoredArray, 0, count, CombinedScoreComparer.Instance);

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                var topCount = Math.Min(3, count);
                var rankings = new string[topCount];
                for (var i = 0; i < topCount; i++)
                {
                    var s = scoredArray[i];
                    rankings[i] = $"{s.Provider.Provider.Name}={s.Combined} (R:{s.Resilience} M:{s.Metrics})";
                }

                _logger.LogDebugIfEnabled("Provider ranking: {Providers}", string.Join(", ", rankings));
            }

            var result = new ProviderStreamInfo[count];
            for (var i = 0; i < count; i++)
            {
                result[i] = scoredArray[i].Provider;
            }

            return result;
        }
        finally
        {
            ArrayPool<(ProviderStreamInfo Provider, int Combined, int Resilience, int Metrics)>.Shared.Return(
                scoredArray
            );
        }
    }

    /// <summary>
    /// Comparer for combined scores.
    /// </summary>
    private sealed class CombinedScoreComparer
        : IComparer<(ProviderStreamInfo Provider, int Combined, int Resilience, int Metrics)>
    {
        public static readonly CombinedScoreComparer Instance = new();

        public int Compare(
            (ProviderStreamInfo Provider, int Combined, int Resilience, int Metrics) x,
            (ProviderStreamInfo Provider, int Combined, int Resilience, int Metrics) y
        )
        {
            var combinedCompare = y.Combined.CompareTo(x.Combined);
            if (combinedCompare != 0)
            {
                return combinedCompare;
            }

            var resilienceCompare = y.Resilience.CompareTo(x.Resilience);
            if (resilienceCompare != 0)
            {
                return resilienceCompare;
            }

            return StringComparer.Ordinal.Compare(x.Provider.Provider.Name, y.Provider.Provider.Name);
        }
    }

    /// <summary>
    /// Determines if a provider switch is recommended based on health trends.
    /// Uses predictive analysis to switch before failures occur.
    /// </summary>
    /// <param name="currentProviderId">The currently active provider.</param>
    /// <param name="availableProviders">All available providers.</param>
    /// <returns>Recommended provider if switch is advised, null otherwise.</returns>
    public ProviderStreamInfo? ShouldSwitchProvider(
        string currentProviderId,
        IEnumerable<ProviderStreamInfo> availableProviders
    )
    {
        var providers = availableProviders as IList<ProviderStreamInfo> ?? [.. availableProviders];
        if (providers.Count <= 1)
        {
            return null;
        }

        // Find current provider
        ProviderStreamInfo? currentProvider = null;
        foreach (var p in providers)
        {
            if (p.Provider.Id == currentProviderId)
            {
                currentProvider = p;
                break;
            }
        }

        if (currentProvider is null)
        {
            return null;
        }

        // Get current provider scores and trend data
        var currentResilienceScore = _healthScorer.GetSelectionScore(currentProviderId);
        var currentMetricsScore = _metrics.CalculateHealthScore(currentProviderId);
        var currentCombined = CalculateCombinedScore(currentResilienceScore, currentMetricsScore, true);
        var currentTrend = _trends.GetSnapshot(currentProviderId);

        // Check if current provider is degraded or predicted to fail
        var isCurrentDegraded = currentCombined < DegradedThreshold;
        var isCurrentCritical = currentCombined < CriticalThreshold;
        var predictedFailure = currentTrend.SuggestsImminentFailure;

        // Find the best alternative - filter out current provider manually
        var alternativesCount = providers.Count - 1;
        if (alternativesCount == 0)
        {
            return null;
        }

        var alternatives = ArrayPool<ProviderStreamInfo>.Shared.Rent(alternativesCount);
        try
        {
            var altIndex = 0;
            foreach (var p in providers)
            {
                if (p.Provider.Id != currentProviderId)
                {
                    alternatives[altIndex++] = p;
                }
            }

            var ordered = GetOrderedProviders(alternatives.AsSpan(0, altIndex).ToArray());
            if (ordered.Count == 0)
            {
                return null;
            }

            var bestAlt = ordered[0];
            var altResilienceScore = _healthScorer.GetSelectionScore(bestAlt.Provider.Id);
            var altMetricsScore = _metrics.CalculateHealthScore(bestAlt.Provider.Id);
            var altCombined = CalculateCombinedScore(altResilienceScore, altMetricsScore, true);
            var altTrend = _trends.GetSnapshot(bestAlt.Provider.Id);

            return EvaluateSwitchDecision(
                currentProvider,
                currentCombined,
                currentTrend,
                isCurrentDegraded,
                isCurrentCritical,
                predictedFailure,
                bestAlt,
                altCombined,
                altTrend
            );
        }
        finally
        {
            ArrayPool<ProviderStreamInfo>.Shared.Return(alternatives);
        }
    }

    private ProviderStreamInfo? EvaluateSwitchDecision(
        ProviderStreamInfo currentProvider,
        int currentCombined,
        HealthTrendSnapshot currentTrend,
        bool isCurrentDegraded,
        bool isCurrentCritical,
        bool predictedFailure,
        ProviderStreamInfo bestAlt,
        int altCombined,
        HealthTrendSnapshot altTrend
    )
    {
        // Switch decision logic:
        // 0. Predictive: Switch if current provider is predicted to fail soon
        // 1. Critical current provider - switch if any alternative is better
        // 2. Degraded current provider - switch if alternative is significantly better
        // 3. Healthy current provider - switch only if alternative is much better (avoid flapping)

        // Predictive switching: switch before failure if alternative is stable/improving
        if (predictedFailure && !altTrend.SuggestsImminentFailure && altCombined >= DegradedThreshold)
        {
            _logger.PluginLogInformation(
                "Predictive provider switch: {Current} (score={CurrentScore}, trend={Trend}, predicted60s={Predicted}) → {Alt} (score={AltScore}) - imminent failure predicted",
                currentProvider.Provider.Name,
                currentCombined,
                currentTrend.Trend,
                currentTrend.PredictedScore60s,
                bestAlt.Provider.Name,
                altCombined
            );
            return bestAlt;
        }

        if (isCurrentCritical && altCombined > currentCombined)
        {
            _logger.PluginLogInformation(
                "Recommending provider switch: {Current} (score={CurrentScore}) → {Alt} (score={AltScore}) - current is critical",
                currentProvider.Provider.Name,
                currentCombined,
                bestAlt.Provider.Name,
                altCombined
            );
            return bestAlt;
        }

        if (isCurrentDegraded && altCombined > currentCombined + SignificantImprovement)
        {
            _logger.PluginLogInformation(
                "Recommending provider switch: {Current} (score={CurrentScore}) → {Alt} (score={AltScore}) - significant improvement available",
                currentProvider.Provider.Name,
                currentCombined,
                bestAlt.Provider.Name,
                altCombined
            );
            return bestAlt;
        }

        // Healthy provider - only switch if alternative is much better
        if (altCombined > currentCombined + MajorImprovement)
        {
            _logger.PluginLogInformation(
                "Recommending provider switch: {Current} (score={CurrentScore}) → {Alt} (score={AltScore}) - major improvement available",
                currentProvider.Provider.Name,
                currentCombined,
                bestAlt.Provider.Name,
                altCombined
            );
            return bestAlt;
        }

        // Also consider switching if current is degrading and alternative is improving
        if (
            currentTrend.Trend == HealthTrend.Degrading
            && altTrend.Trend == HealthTrend.Improving
            && altCombined >= currentCombined
        )
        {
            _logger.PluginLogInformation(
                "Trend-based provider switch: {Current} (score={CurrentScore}, trend={CurrentTrend}) → {Alt} (score={AltScore}, trend={AltTrend}) - better trajectory",
                currentProvider.Provider.Name,
                currentCombined,
                currentTrend.Trend,
                bestAlt.Provider.Name,
                altCombined,
                altTrend.Trend
            );
            return bestAlt;
        }

        return null;
    }

    /// <summary>
    /// Gets a health summary for all known providers.
    /// </summary>
    /// <returns>List of provider health summaries.</returns>
    public IReadOnlyList<ProviderHealthSummary> GetHealthSummaries(IEnumerable<XtreamProvider> providers)
    {
        var providerList = providers as IList<XtreamProvider> ?? [.. providers];
        var count = providerList.Count;

        if (count == 0)
        {
            return [];
        }

        var summaries = new ProviderHealthSummary[count];
        for (var i = 0; i < count; i++)
        {
            var p = providerList[i];
            var resilienceScore = _healthScorer.GetSelectionScore(p.Id);
            var metricsScore = _metrics.CalculateHealthScore(p.Id);
            var isAvailable = _healthScorer.IsAvailable(p.Id);

            summaries[i] = new ProviderHealthSummary
            {
                ProviderId = p.Id,
                ProviderName = p.Name,
                ResilienceScore = resilienceScore,
                MetricsScore = metricsScore,
                CombinedScore = CalculateCombinedScore(resilienceScore, metricsScore, isAvailable),
                IsAvailable = isAvailable,
                CircuitState = _circuitBreaker.GetCircuitState(p.Id),
                Metrics = _metrics.GetSnapshot(p.Id),
                TrendSnapshot = _trends.GetSnapshot(p.Id),
            };
        }

        // Sort by combined score descending
        Array.Sort(summaries, (a, b) => b.CombinedScore.CompareTo(a.CombinedScore));

        return summaries;
    }

    /// <summary>
    /// Records a health sample for trend tracking.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="healthScore">The current health score (0-100).</param>
    public void RecordHealthSample(string providerId, int healthScore)
    {
        _trends.RecordSample(providerId, healthScore);
    }

    /// <summary>
    /// Updates all provider trends based on current metrics.
    /// Should be called periodically (e.g., every 5-10 seconds).
    /// </summary>
    /// <param name="providers">The providers to update.</param>
    public void UpdateTrends(IEnumerable<XtreamProvider> providers)
    {
        foreach (var provider in providers)
        {
            var resilienceScore = _healthScorer.GetSelectionScore(provider.Id);
            var metricsScore = _metrics.CalculateHealthScore(provider.Id);
            var isAvailable = _healthScorer.IsAvailable(provider.Id);
            var combined = CalculateCombinedScore(resilienceScore, metricsScore, isAvailable);
            _trends.RecordSample(provider.Id, combined);
        }
    }

    // Thresholds for failover decisions
    private const int DegradedThreshold = 50;
    private const int CriticalThreshold = 25;
    private const int SignificantImprovement = 15;
    private const int MajorImprovement = 30;

    // Weights for combined score
    private const double ResilienceWeight = 0.6;
    private const double MetricsWeight = 0.4;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CalculateCombinedScore(int resilienceScore, int metricsScore, bool isAvailable)
    {
        if (!isAvailable)
        {
            return 0;
        }

        double combined = (resilienceScore * ResilienceWeight) + (metricsScore * MetricsWeight);
        return Math.Clamp((int)combined, 0, 100);
    }

    private static PluginConfiguration? GetDefaultConfiguration()
    {
        try
        {
            return Plugin.Instance?.Configuration;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Summary of a provider's health status.
/// </summary>
public record struct ProviderHealthSummary
{
    /// <summary>Gets the provider ID.</summary>
    public required string ProviderId { get; init; }

    /// <summary>Gets the provider name.</summary>
    public required string ProviderName { get; init; }

    /// <summary>Gets the resilience score (0-100).</summary>
    public required int ResilienceScore { get; init; }

    /// <summary>Gets the metrics-based score (0-100).</summary>
    public required int MetricsScore { get; init; }

    /// <summary>Gets the combined health score (0-100).</summary>
    public int CombinedScore { get; init; }

    /// <summary>Gets whether the provider is available.</summary>
    public required bool IsAvailable { get; init; }

    /// <summary>Gets the circuit breaker state.</summary>
    public required Polly.CircuitBreaker.CircuitState CircuitState { get; init; }

    /// <summary>Gets the detailed metrics snapshot.</summary>
    public ProviderMetricsSnapshot Metrics { get; init; }

    /// <summary>Gets the health trend snapshot for predictive analysis.</summary>
    public HealthTrendSnapshot TrendSnapshot { get; init; }

    /// <summary>Gets the health status description.</summary>
    public readonly string Status =>
        CombinedScore switch
        {
            > 75 => "Healthy",
            > 50 => "Degraded",
            > 25 => "Poor",
            _ => "Critical",
        };

    /// <summary>Gets whether imminent failure is predicted.</summary>
    public readonly bool PredictedFailure => TrendSnapshot.SuggestsImminentFailure;

    /// <summary>Gets the health trend direction.</summary>
    public readonly HealthTrend Trend => TrendSnapshot.Trend;
}
