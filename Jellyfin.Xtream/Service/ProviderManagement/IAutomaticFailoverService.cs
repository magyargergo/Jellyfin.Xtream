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
using Jellyfin.Xtream.Configuration;

namespace Jellyfin.Xtream.Service.ProviderManagement;

/// <summary>
/// Selects optimal providers based on combined health metrics,
/// circuit breaker state, performance data, and predictive trend analysis.
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
    /// Determines if a provider switch is recommended based on health trends.
    /// Uses predictive analysis to switch before failures occur.
    /// </summary>
    /// <param name="currentProviderId">The currently active provider.</param>
    /// <param name="availableProviders">All available providers.</param>
    /// <returns>Recommended provider if switch is advised, null otherwise.</returns>
    ProviderStreamInfo? ShouldSwitchProvider(
        string currentProviderId,
        IEnumerable<ProviderStreamInfo> availableProviders
    );

    /// <summary>
    /// Gets a health summary for all known providers.
    /// </summary>
    /// <param name="providers">The providers to get summaries for.</param>
    /// <returns>List of provider health summaries.</returns>
    IReadOnlyList<ProviderHealthSummary> GetHealthSummaries(IEnumerable<XtreamProvider> providers);

    /// <summary>
    /// Records a health sample for trend tracking.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="healthScore">The current health score (0-100).</param>
    void RecordHealthSample(string providerId, int healthScore);

    /// <summary>
    /// Updates all provider trends based on current metrics.
    /// Should be called periodically (e.g., every 5-10 seconds).
    /// </summary>
    /// <param name="providers">The providers to update.</param>
    void UpdateTrends(IEnumerable<XtreamProvider> providers);
}
