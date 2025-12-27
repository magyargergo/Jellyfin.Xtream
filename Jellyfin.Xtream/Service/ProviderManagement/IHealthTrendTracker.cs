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

namespace Jellyfin.Xtream.Service.ProviderManagement;

/// <summary>
/// Tracks health score trends over time for predictive provider switching.
/// </summary>
public interface IHealthTrendTracker
{
    /// <summary>
    /// Records a health score sample for a provider.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="healthScore">The current health score (0-100).</param>
    void RecordSample(string providerId, int healthScore);

    /// <summary>
    /// Gets the predicted health score for a provider based on trend analysis.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="secondsAhead">How far ahead to predict (default 60 seconds).</param>
    /// <returns>Predicted health score, or current score if insufficient data.</returns>
    int GetPredictedScore(string providerId, int secondsAhead = 60);

    /// <summary>
    /// Gets the health trend direction for a provider.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>The trend direction.</returns>
    HealthTrend GetTrend(string providerId);

    /// <summary>
    /// Gets the trend snapshot for a provider.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>The trend snapshot, or default if no data.</returns>
    HealthTrendSnapshot GetSnapshot(string providerId);

    /// <summary>
    /// Gets all provider trends.
    /// </summary>
    /// <returns>Dictionary of provider IDs to trend snapshots.</returns>
    IReadOnlyDictionary<string, HealthTrendSnapshot> GetAllSnapshots();

    /// <summary>
    /// Clears trend data for a specific provider.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    void Clear(string providerId);

    /// <summary>
    /// Clears all trend data.
    /// </summary>
    void ClearAll();
}
