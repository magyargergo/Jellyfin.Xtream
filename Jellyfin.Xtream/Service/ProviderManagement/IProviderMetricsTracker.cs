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

namespace Jellyfin.Xtream.Service.ProviderManagement;

/// <summary>
/// Tracks provider performance metrics for enhanced health scoring.
/// </summary>
public interface IProviderMetricsTracker
{
    /// <summary>
    /// Records the latency of a connection attempt.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="latencyMs">Connection latency in milliseconds.</param>
    void RecordLatency(string providerId, double latencyMs);

    /// <summary>
    /// Records bytes transferred for throughput calculation.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="bytes">Bytes transferred.</param>
    /// <param name="durationMs">Duration in milliseconds.</param>
    void RecordThroughput(string providerId, long bytes, double durationMs);

    /// <summary>
    /// Records a stream error.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="errorType">Type of error.</param>
    void RecordError(string providerId, StreamErrorType errorType);

    /// <summary>
    /// Records a stream disconnection.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="streamDurationMs">How long the stream was active before disconnection.</param>
    void RecordDisconnection(string providerId, double streamDurationMs);

    /// <summary>
    /// Gets the current metrics snapshot for a provider.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>The metrics snapshot, or default values if not tracked.</returns>
    ProviderMetricsSnapshot GetSnapshot(string providerId);

    /// <summary>
    /// Calculates a health score based on metrics (0-100, higher is better).
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>Health score from 0 to 100.</returns>
    int CalculateHealthScore(string providerId);

    /// <summary>
    /// Resets metrics for a provider.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    void Reset(string providerId);

    /// <summary>
    /// Clears all tracked metrics.
    /// </summary>
    void Clear();
}
