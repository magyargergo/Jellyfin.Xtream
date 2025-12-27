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
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Service.ProviderManagement;

namespace Jellyfin.Xtream.Service.Switching;

/// <summary>
/// Maintains warm connections to backup providers for ultra-fast switching.
/// </summary>
public interface IPreconnectPool : IDisposable
{
    /// <summary>
    /// Gets the current pool size.
    /// </summary>
    int PoolSize { get; }

    /// <summary>
    /// Gets the number of active connections.
    /// </summary>
    int ActiveConnections { get; }

    /// <summary>
    /// Gets the total number of connections made.
    /// </summary>
    long TotalConnections { get; }

    /// <summary>
    /// Gets the number of connection hits (found in pool).
    /// </summary>
    long ConnectionHits { get; }

    /// <summary>
    /// Gets the number of connection misses (not found in pool).
    /// </summary>
    long ConnectionMisses { get; }

    /// <summary>
    /// Gets the hit rate percentage (0-100).
    /// </summary>
    double HitRatePercent { get; }

    /// <summary>
    /// Tries to get a preconnected stream for a provider.
    /// </summary>
    /// <param name="providerId">The provider ID to get connection for.</param>
    /// <param name="connection">The connection if found.</param>
    /// <returns>True if a valid connection was found.</returns>
    bool TryGetConnection(string providerId, out PooledConnection? connection);

    /// <summary>
    /// Tries to get a preconnected stream for a provider and stream.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="streamId">The stream ID.</param>
    /// <param name="connection">The connection if found.</param>
    /// <returns>True if a valid connection was found.</returns>
    bool TryGetConnection(string providerId, int streamId, out PooledConnection? connection);

    /// <summary>
    /// Preconnects to backup providers for a specific stream.
    /// </summary>
    /// <param name="currentProviderId">The current provider ID to exclude.</param>
    /// <param name="streamId">The stream ID to preconnect for.</param>
    /// <param name="alternativeProviders">The alternative providers to potentially connect to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of successful preconnections.</returns>
    Task<int> PreconnectToBackupsAsync(
        string currentProviderId,
        int streamId,
        IEnumerable<ProviderStreamInfo> alternativeProviders,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Removes and disposes all stale connections.
    /// </summary>
    /// <returns>Number of connections removed.</returns>
    int PruneStaleConnections();

    /// <summary>
    /// Sets the pool size.
    /// </summary>
    /// <param name="size">The new pool size.</param>
    void SetPoolSize(int size);

    /// <summary>
    /// Clears all pooled connections.
    /// </summary>
    void Clear();

    /// <summary>
    /// Starts background refresh of connections before they expire.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to stop refreshing.</param>
    void StartBackgroundRefresh(CancellationToken cancellationToken);

    /// <summary>
    /// Stops background refresh of connections.
    /// </summary>
    void StopBackgroundRefresh();

    /// <summary>
    /// Gets a value indicating whether background refresh is running.
    /// </summary>
    bool IsRefreshRunning { get; }
}
