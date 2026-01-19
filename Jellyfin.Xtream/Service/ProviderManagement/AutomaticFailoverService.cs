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
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Utility;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.ProviderManagement;

/// <summary>
/// Automatic failover service that selects optimal providers based on
/// combined health metrics, circuit breaker state, performance data,
/// and predictive health trend analysis. This is the single entry point
/// for all provider-related operations outside the ProviderManagement namespace.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="AutomaticFailoverService"/> class.
/// </remarks>
/// <param name="availabilityService">The provider availability service (combines health scoring, circuit breaker, and capacity).</param>
/// <param name="metrics">The provider metrics tracker.</param>
/// <param name="trends">The health trend tracker.</param>
/// <param name="logger">The logger instance.</param>
/// <param name="configProvider">Configuration provider.</param>
public sealed class AutomaticFailoverService(
    IProviderAvailabilityService availabilityService,
    IProviderMetricsTracker metrics,
    IHealthTrendTracker trends,
    ILogger logger,
    IPluginConfigurationProvider? configProvider = null
) : IAutomaticFailoverService
{
    private readonly IProviderAvailabilityService _availabilityService = availabilityService;
    private readonly IProviderMetricsTracker _metrics = metrics;
    private readonly ILogger _logger = logger;
    private readonly IPluginConfigurationProvider _configProvider = configProvider ?? new PluginConfigurationProvider();

    /// <summary>
    /// Gets the health trend tracker for external access.
    /// </summary>
    public IHealthTrendTracker TrendTracker { get; } = trends;

    /// <summary>
    /// Gets providers ordered by combined health score for optimal failover.
    /// Combines circuit breaker state, capacity, latency, throughput, error rates,
    /// predictive trend analysis, user-defined priority, and least-connections load balancing.
    /// </summary>
    /// <param name="providers">Available providers for the channel.</param>
    /// <returns>Providers ordered by combined health score (best first).</returns>
    public IReadOnlyList<ProviderStreamInfo> GetOrderedProviders(IEnumerable<ProviderStreamInfo> providers)
    {
        var config = _configProvider.GetConfiguration();
        var skipUnavailable = config?.SkipUnavailableProviders ?? true;

        // Delegate to the optimized resilience service for base sorting
        var baseSorted = _availabilityService.GetSortedProviders(providers, forceIncludeAll: !skipUnavailable);

        if (baseSorted.Count == 0)
        {
            return baseSorted;
        }

        // Re-score with combined metrics for final ordering
        var count = baseSorted.Count;
        var scoredArray = ArrayPool<ScoredProvider>.Shared.Rent(count);
        try
        {
            for (var i = 0; i < count; i++)
            {
                var p = baseSorted[i];
                var providerId = p.Provider.Id;
                var resilienceScore = _availabilityService.GetSelectionScore(providerId);
                var metricsScore = _metrics.CalculateHealthScore(providerId);
                var isAvailable = _availabilityService.IsAvailable(providerId);
                var priority = p.Provider.Priority;
                var activeConnections = _availabilityService.GetAvailableSlots(providerId);
                var combined = CalculateCombinedScore(resilienceScore, metricsScore, isAvailable, providerId, priority);
                var trendSnapshot = TrendTracker.GetSnapshot(providerId);
                scoredArray[i] = new ScoredProvider(
                    p,
                    combined,
                    resilienceScore,
                    metricsScore,
                    priority,
                    activeConnections,
                    trendSnapshot.Trend
                );
            }

            // Sort by combined score descending with least-connections tie-breaking
            Array.Sort(scoredArray, 0, count, new ScoredProviderComparer(ScoreTolerance));

            if (_logger.IsDebugEnabled())
            {
                var topCount = Math.Min(3, count);
                var rankings = new string[topCount];
                for (var i = 0; i < topCount; i++)
                {
                    var s = scoredArray[i];
                    rankings[i] =
                        $"{s.Provider.Provider.Name}={s.Combined.ToString(System.Globalization.CultureInfo.InvariantCulture)} (R:{s.Resilience.ToString(System.Globalization.CultureInfo.InvariantCulture)} M:{s.Metrics.ToString(System.Globalization.CultureInfo.InvariantCulture)} P:{s.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture)} T:{s.Trend})";
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
            ArrayPool<ScoredProvider>.Shared.Return(scoredArray);
        }
    }

    /// <summary>
    /// Scored provider for sorting with all relevant metrics.
    /// </summary>
    private readonly record struct ScoredProvider(
        ProviderStreamInfo Provider,
        int Combined,
        int Resilience,
        int Metrics,
        int Priority,
        int AvailableSlots,
        HealthTrend Trend
    );

    /// <summary>
    /// Comparer for scored providers with least-connections tie-breaking.
    /// When scores are within tolerance, prefer providers with more available slots (least loaded).
    /// </summary>
    private sealed class ScoredProviderComparer(int scoreTolerance) : IComparer<ScoredProvider>
    {
        private readonly int _scoreTolerance = scoreTolerance;

        public int Compare(ScoredProvider x, ScoredProvider y)
        {
            // Primary: combined score descending
            var scoreDiff = y.Combined - x.Combined;
            if (Math.Abs(scoreDiff) > _scoreTolerance)
            {
                return scoreDiff;
            }

            // Tie-breaker 1: least-connections (more available slots = better)
            // Use available slots since active connections may not be tracked
            if (x.AvailableSlots >= 0 && y.AvailableSlots >= 0)
            {
                var slotsDiff = y.AvailableSlots.CompareTo(x.AvailableSlots);
                if (slotsDiff != 0)
                {
                    return slotsDiff;
                }
            }

            // Tie-breaker 2: user priority (lower priority number = higher preference)
            var priorityDiff = x.Priority.CompareTo(y.Priority);
            if (priorityDiff != 0)
            {
                return priorityDiff;
            }

            // Tie-breaker 3: prefer stable or improving trends
            var trendOrder = GetTrendOrder(x.Trend).CompareTo(GetTrendOrder(y.Trend));
            if (trendOrder != 0)
            {
                return trendOrder;
            }

            // Tie-breaker 4: resilience score (higher = better)
            var resilienceCompare = y.Resilience.CompareTo(x.Resilience);
            if (resilienceCompare != 0)
            {
                return resilienceCompare;
            }

            // Final: alphabetical by name for deterministic ordering
            return StringComparer.Ordinal.Compare(x.Provider.Provider.Name, y.Provider.Provider.Name);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetTrendOrder(HealthTrend trend) =>
            trend switch
            {
                HealthTrend.Improving => 0,
                HealthTrend.Stable => 1,
                HealthTrend.Degrading => 2,
                HealthTrend.RapidlyDegrading => 3,
                _ => 1,
            };
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
            var resilienceScore = _availabilityService.GetSelectionScore(p.Id);
            var metricsScore = _metrics.CalculateHealthScore(p.Id);
            var isAvailable = _availabilityService.IsAvailable(p.Id);

            summaries[i] = new ProviderHealthSummary
            {
                ProviderId = p.Id,
                ProviderName = p.Name,
                ResilienceScore = resilienceScore,
                MetricsScore = metricsScore,
                CombinedScore = CalculateCombinedScore(resilienceScore, metricsScore, isAvailable, p.Id, p.Priority),
                IsAvailable = isAvailable,
                CircuitState = _availabilityService.GetCircuitState(p.Id),
                Metrics = _metrics.GetSnapshot(p.Id),
                TrendSnapshot = TrendTracker.GetSnapshot(p.Id),
                Priority = p.Priority,
                AvailableSlots = _availabilityService.GetAvailableSlots(p.Id),
            };
        }

        // Sort by combined score descending
        Array.Sort(summaries, (a, b) => b.CombinedScore.CompareTo(a.CombinedScore));

        return summaries;
    }

    // Weights for combined score (total = 1.0)
    // Resilience: circuit breaker state, success rate, capacity - core reliability
    // Metrics: latency, throughput, error rates - performance quality
    // Trend: predictive health score - forward-looking stability
    // Priority: user-defined preference - explicit ordering
    private const double ResilienceWeight = 0.45;
    private const double MetricsWeight = 0.25;
    private const double TrendWeight = 0.15;
    private const double PriorityWeight = 0.15;

    // Score tolerance for least-connections tie-breaking
    private const int ScoreTolerance = 5;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CalculateCombinedScore(
        int resilienceScore,
        int metricsScore,
        bool isAvailable,
        string providerId,
        int priority
    )
    {
        if (!isAvailable)
        {
            return 0;
        }

        // Get predicted score from trend tracker (forward-looking health)
        var trendSnapshot = TrendTracker.GetSnapshot(providerId);
        var predictedScore = trendSnapshot.SampleCount >= 3 ? trendSnapshot.PredictedScore60s : resilienceScore;

        // Apply degrading trend penalty - proactive switching
        if (trendSnapshot.Trend == HealthTrend.RapidlyDegrading)
        {
            predictedScore = Math.Max(0, predictedScore - 20);
        }
        else if (trendSnapshot.Trend == HealthTrend.Degrading)
        {
            predictedScore = Math.Max(0, predictedScore - 10);
        }

        // Convert priority to score (0 priority = 100 score, 100 priority = 0 score)
        var priorityScore = 100 - Math.Clamp(priority, 0, 100);

        // Weighted combination
        var combined =
            (resilienceScore * ResilienceWeight)
            + (metricsScore * MetricsWeight)
            + (predictedScore * TrendWeight)
            + (priorityScore * PriorityWeight);

        return Math.Clamp((int)combined, 0, 100);
    }

    #region Provider State Management (delegated to underlying services)

    /// <inheritdoc />
    public int GetSelectionScore(string providerId) => _availabilityService.GetSelectionScore(providerId);

    /// <inheritdoc />
    public bool IsAvailable(string providerId) => _availabilityService.IsAvailable(providerId);

    /// <inheritdoc />
    public bool HasCapacity(string providerId) => _availabilityService.HasCapacity(providerId);

    /// <inheritdoc />
    public ProviderCircuitState GetCircuitState(string providerId) => _availabilityService.GetCircuitState(providerId);

    /// <inheritdoc />
    public bool NeedsRefresh() => _availabilityService.NeedsRefresh();

    /// <inheritdoc />
    public Task RefreshAsync(
        IEnumerable<XtreamProvider> providers,
        System.Threading.CancellationToken cancellationToken = default
    ) => _availabilityService.RefreshAsync(providers, cancellationToken);

    /// <inheritdoc />
    public Task ResetCircuitAsync(string providerId) => _availabilityService.ResetCircuitAsync(providerId);

    /// <inheritdoc />
    public IReadOnlyDictionary<string, ProviderResilienceState> GetProviderStates() =>
        _availabilityService.GetSnapshot();

    /// <inheritdoc />
    public ProviderMetricsSnapshot GetMetricsSnapshot(string providerId) => _metrics.GetSnapshot(providerId);

    /// <inheritdoc />
    public void RecordSuccess(string providerId) => _availabilityService.RecordSuccess(providerId);

    /// <inheritdoc />
    public bool RecordFailure(string providerId, ProviderFailureReason reason, string? providerName = null) =>
        _availabilityService.RecordFailure(providerId, reason, providerName);

    /// <inheritdoc />
    public IReadOnlyList<ProviderStreamInfo> GetSortedProviders(
        IEnumerable<ProviderStreamInfo> providers,
        bool forceIncludeAll = false
    ) => _availabilityService.GetSortedProviders(providers, forceIncludeAll);

    /// <inheritdoc />
    public void RecordThroughput(string providerId, long bytes, long elapsedMs) =>
        _metrics.RecordThroughput(providerId, bytes, elapsedMs);

    /// <inheritdoc />
    public void RecordError(string providerId, StreamErrorType errorType) =>
        _metrics.RecordError(providerId, errorType);

    /// <inheritdoc />
    public int CalculateHealthScore(string providerId) => _metrics.CalculateHealthScore(providerId);

    /// <inheritdoc />
    public int CalculatePriority(string providerId) => _metrics.CalculatePriority(providerId);

    // EMA smoothing constants - precomputed to avoid runtime division
    private const double SmoothingFactor = 0.3;
    private const double InverseSmoothingFactor = 1.0 - SmoothingFactor; // 0.7
    private const int MinSamplesForPriorityUpdate = 5;

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool UpdateProviderPriorities()
    {
        var config = _configProvider.GetConfiguration();
        var providers = config?.Providers;
        if (providers == null || providers.Count == 0)
        {
            return false;
        }

        var anyUpdated = false;
        var providerCount = providers.Count;

        // Process all providers in a single pass for cache locality
        for (var i = 0; i < providerCount; i++)
        {
            var provider = providers[i];

            // Single O(1) dictionary lookup
            var snapshot = _metrics.GetSnapshot(provider.Id);

            // Skip if insufficient data - avoids unnecessary calculations
            if (snapshot.TotalSamples < MinSamplesForPriorityUpdate)
            {
                continue;
            }

            // Calculate priority directly from snapshot - O(1) with lookup tables
            var calculatedPriority = ProviderMetricsTracker.CalculatePriority(snapshot);
            var currentPriority = provider.Priority;

            // EMA smoothing: new = α * calculated + (1-α) * old
            // Using precomputed constants to avoid runtime division
            var smoothedPriority = (int)(
                (calculatedPriority * SmoothingFactor) + (currentPriority * InverseSmoothingFactor)
            );

            // Branchless clamp using Math.Min/Max which JIT optimizes to conditional moves
            smoothedPriority = Math.Min(100, Math.Max(0, smoothedPriority));

            // Only update if changed - avoids unnecessary writes
            if (currentPriority != smoothedPriority)
            {
                _logger.LogDebugIfEnabled(
                    "Provider {Name} priority: {OldPriority} -> {NewPriority} (calc: {Calculated}, n={Samples})",
                    provider.Name,
                    currentPriority,
                    smoothedPriority,
                    calculatedPriority,
                    snapshot.TotalSamples
                );
                provider.Priority = smoothedPriority;
                anyUpdated = true;
            }
        }

        // Persist only if changes occurred - avoids unnecessary I/O
        if (anyUpdated)
        {
            try
            {
                Plugin.Instance.SaveConfiguration();
                _logger.LogDebugIfEnabled("Provider priorities persisted ({Count} providers)", providerCount);
            }
            catch (Exception ex)
            {
                _logger.PluginLogWarning(ex, "Failed to persist provider priorities");
                return false;
            }
        }

        return anyUpdated;
    }

    #endregion
}

/// <summary>
/// Summary of a provider's health status.
/// </summary>
public readonly record struct ProviderHealthSummary
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
    public required ProviderCircuitState CircuitState { get; init; }

    /// <summary>Gets the detailed metrics snapshot.</summary>
    public ProviderMetricsSnapshot Metrics { get; init; }

    /// <summary>Gets the health trend snapshot for predictive analysis.</summary>
    public HealthTrendSnapshot TrendSnapshot { get; init; }

    /// <summary>Gets the user-defined priority (0-100, lower = higher priority).</summary>
    public int Priority { get; init; }

    /// <summary>Gets the number of available connection slots (-1 if unknown).</summary>
    public int AvailableSlots { get; init; }

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
