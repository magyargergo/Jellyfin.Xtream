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
/// Calculates and provides health scores for provider selection.
/// </summary>
public interface IProviderHealthScorer
{
    /// <summary>
    /// Checks if a provider is available for new connections.
    /// Combines circuit breaker state with capacity checking.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>True if the provider should be tried.</returns>
    bool IsAvailable(string providerId);

    /// <summary>
    /// Gets a weighted score for provider selection (0-100, higher = better).
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>The weighted selection score.</returns>
    int GetSelectionScore(string providerId);

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
}
