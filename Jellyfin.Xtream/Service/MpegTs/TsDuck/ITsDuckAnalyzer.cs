// Copyright (C) 2025  Gergo Magyar
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using Jellyfin.Xtream.Service.MpegTs.UseCases;

namespace Jellyfin.Xtream.Service.MpegTs.TsDuck;

/// <summary>
/// Interface for TSDuck-based stream analysis.
/// Provides broadcast-grade TR 101 290 monitoring via external tsp process.
/// </summary>
public interface ITsDuckAnalyzer : IQualityEventSource, IDisposable
{
    /// <summary>
    /// Gets a value indicating whether TSDuck is available and can be started.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Gets a value indicating whether the analyzer has been initialized and is processing.
    /// </summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Gets the current process status.
    /// </summary>
    TsDuckProcessStatus ProcessStatus { get; }

    /// <summary>
    /// Gets the latest aggregated metrics from TSDuck analysis.
    /// Returns null if no metrics have been received yet.
    /// </summary>
    /// <returns>The latest metrics, or null.</returns>
    TsDuckMetrics? GetMetrics();

    /// <summary>
    /// Gets the age of the most recent metrics, or null if no metrics available.
    /// </summary>
    /// <remarks>
    /// Use this to determine if metrics are fresh before making decisions.
    /// </remarks>
    TimeSpan? MetricsAge { get; }

    /// <summary>
    /// Gets a value indicating whether fresh metrics are available (not stale).
    /// </summary>
    /// <remarks>
    /// Returns true if metrics exist and are less than 5 seconds old.
    /// </remarks>
    bool HasFreshMetrics { get; }

    /// <summary>
    /// Feeds MPEG-TS data to TSDuck for analysis.
    /// This method is non-blocking: data is queued and written to stdin asynchronously.
    /// </summary>
    /// <param name="data">MPEG-TS data chunk.</param>
    void FeedData(ReadOnlySpan<byte> data);

    /// <summary>
    /// Resets the analyzer for a new stream (e.g., after provider switch).
    /// This restarts the tsp process to clear accumulated state.
    /// </summary>
    void Reset();

    /// <summary>
    /// Event raised when metrics are updated (typically every MetricsIntervalSeconds).
    /// </summary>
    event EventHandler<TsDuckMetricsEventArgs>? MetricsUpdated;
}
