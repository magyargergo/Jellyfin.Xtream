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

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Configuration;

namespace Jellyfin.Xtream.Service.ProviderManagement;

/// <summary>
/// Selects optimal providers based on combined health metrics,
/// circuit breaker state, performance data, and predictive trend analysis.
/// This is the single entry point for all provider-related operations outside
/// the ProviderManagement namespace.
/// </summary>
public interface IAutomaticFailoverService
{
    /// <summary>
    /// Gets the health trend tracker for external access.
    /// </summary>
    IHealthTrendTracker TrendTracker { get; }

    /// <summary>
    /// Gets providers ordered by combined health score for optimal failover.
    /// Combines circuit breaker state, capacity, latency, throughput, and error rates.
    /// </summary>
    /// <param name="providers">Available providers for the channel.</param>
    /// <returns>Providers ordered by combined health score (best first).</returns>
    IReadOnlyList<ProviderStreamInfo> GetOrderedProviders(IEnumerable<ProviderStreamInfo> providers);

    /// <summary>
    /// Gets a health summary for all known providers.
    /// </summary>
    /// <param name="providers">The providers to get summaries for.</param>
    /// <returns>List of provider health summaries.</returns>
    IReadOnlyList<ProviderHealthSummary> GetHealthSummaries(IEnumerable<XtreamProvider> providers);

    #region Provider State Management

    /// <summary>
    /// Gets the selection score for a provider (0-100).
    /// Higher scores indicate healthier providers.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>The selection score.</returns>
    int GetSelectionScore(string providerId);

    /// <summary>
    /// Gets whether a provider is available (circuit not open).
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>True if the provider is available.</returns>
    bool IsAvailable(string providerId);

    /// <summary>
    /// Gets whether a provider has capacity for new connections.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>True if the provider has available slots.</returns>
    bool HasCapacity(string providerId);

    /// <summary>
    /// Gets the circuit breaker state for a provider.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>The circuit state.</returns>
    ProviderCircuitState GetCircuitState(string providerId);

    /// <summary>
    /// Gets whether the provider status data needs refreshing.
    /// </summary>
    /// <returns>True if refresh is needed.</returns>
    bool NeedsRefresh();

    /// <summary>
    /// Refreshes provider connection status asynchronously.
    /// </summary>
    /// <param name="providers">The providers to refresh.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    Task RefreshAsync(IEnumerable<XtreamProvider> providers, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resets the circuit breaker for a provider.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>A task representing the async operation.</returns>
    Task ResetCircuitAsync(string providerId);

    /// <summary>
    /// Gets a snapshot of all provider states.
    /// </summary>
    /// <returns>Dictionary of provider states.</returns>
    IReadOnlyDictionary<string, ProviderResilienceState> GetProviderStates();

    /// <summary>
    /// Gets the metrics snapshot for a provider.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>The metrics snapshot.</returns>
    ProviderMetricsSnapshot GetMetricsSnapshot(string providerId);

    /// <summary>
    /// Records a successful operation for a provider.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    void RecordSuccess(string providerId);

    /// <summary>
    /// Records a failed operation for a provider.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="reason">The failure reason.</param>
    /// <param name="providerName">Optional provider name for logging.</param>
    /// <returns>True if the circuit breaker tripped.</returns>
    bool RecordFailure(string providerId, ProviderFailureReason reason, string? providerName = null);

    /// <summary>
    /// Gets providers sorted by selection score for failover.
    /// </summary>
    /// <param name="providers">The providers to sort.</param>
    /// <param name="forceIncludeAll">Include unavailable providers.</param>
    /// <returns>Providers sorted by selection score descending.</returns>
    IReadOnlyList<ProviderStreamInfo> GetSortedProviders(
        IEnumerable<ProviderStreamInfo> providers,
        bool forceIncludeAll = false
    );

    /// <summary>
    /// Records throughput metrics for a provider.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="bytes">The number of bytes transferred.</param>
    /// <param name="elapsedMs">The elapsed time in milliseconds.</param>
    void RecordThroughput(string providerId, long bytes, long elapsedMs);

    /// <summary>
    /// Records a stream error for a provider.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="errorType">The type of error.</param>
    void RecordError(string providerId, StreamErrorType errorType);

    /// <summary>
    /// Calculates the health score for a provider based on metrics.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>The health score (0-100).</returns>
    int CalculateHealthScore(string providerId);

    /// <summary>
    /// Calculates the priority for a provider based on performance history.
    /// Priority is the inverse of health score (0 = best, 100 = worst).
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>The calculated priority (0-100, lower is better).</returns>
    int CalculatePriority(string providerId);

    /// <summary>
    /// Updates provider priorities based on their current performance metrics
    /// and persists the changes to the plugin configuration.
    /// Call this periodically or after significant streaming events.
    /// </summary>
    /// <returns>True if priorities were updated and saved, false otherwise.</returns>
    bool UpdateProviderPriorities();

    #endregion
}
