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
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Xtream.Service.Discovery;

/// <summary>
/// Interface for discovering credentials from web sources.
/// </summary>
public interface ICredentialSource
{
    /// <summary>
    /// Gets the name of this credential source.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the base URL this source targets.
    /// </summary>
    string BaseUrl { get; }

    /// <summary>
    /// Discovers credentials from the configured source.
    /// </summary>
    /// <param name="maxPages">Maximum number of pages to process.</param>
    /// <param name="maxWorkers">Maximum parallel workers for discovery.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The discovery result containing found credentials.</returns>
    Task<DiscoveryResult> DiscoverAsync(
        int maxPages,
        int maxWorkers,
        IProgress<DiscoveryProgress>? progress,
        CancellationToken cancellationToken
    );
}
