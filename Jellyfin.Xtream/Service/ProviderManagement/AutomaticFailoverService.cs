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
    /// Combines circuit breaker state, capacity, latency, throughput, and error rates.
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
                var resilienceScore = _availabilityService.GetSelectionScore(p.Provider.Id);
                var metricsScore = _metrics.CalculateHealthScore(p.Provider.Id);
                var isAvailable = _availabilityService.IsAvailable(p.Provider.Id);
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
                    var (provider, Combined, resilience, metrics) = scoredArray[i];
                    rankings[i] = $"{provider.Provider.Name}={Combined} (R:{resilience} M:{metrics})";
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
            return resilienceCompare != 0
                ? resilienceCompare
                : StringComparer.Ordinal.Compare(x.Provider.Provider.Name, y.Provider.Provider.Name);
        }
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
                CombinedScore = CalculateCombinedScore(resilienceScore, metricsScore, isAvailable),
                IsAvailable = isAvailable,
                CircuitState = _availabilityService.GetCircuitState(p.Id),
                Metrics = _metrics.GetSnapshot(p.Id),
                TrendSnapshot = TrendTracker.GetSnapshot(p.Id),
            };
        }

        // Sort by combined score descending
        Array.Sort(summaries, (a, b) => b.CombinedScore.CompareTo(a.CombinedScore));

        return summaries;
    }

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

        var combined = (resilienceScore * ResilienceWeight) + (metricsScore * MetricsWeight);
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
