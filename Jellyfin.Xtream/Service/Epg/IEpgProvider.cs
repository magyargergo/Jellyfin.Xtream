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

namespace Jellyfin.Xtream.Service.Epg;

/// <summary>
/// Interface for EPG (Electronic Program Guide) providers.
/// Follows Interface Segregation Principle - single responsibility for fetching EPG data.
/// </summary>
public interface IEpgProvider
{
    /// <summary>
    /// Gets the name of this EPG provider for logging/diagnostics.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the priority of this provider. Lower values = higher priority.
    /// Used by composite providers to determine fallback order.
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Gets a value indicating whether this provider is currently available.
    /// Used to skip providers that are known to be unavailable (e.g., 404 errors).
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Gets EPG programs for a specific stream.
    /// </summary>
    /// <param name="streamId">The stream ID to get EPG for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Collection of EPG programs, empty if none available.</returns>
    Task<IReadOnlyList<EpgProgram>> GetProgramsAsync(int streamId, CancellationToken cancellationToken);
}

/// <summary>
/// Interface for EPG providers that support pre-warming their cache.
/// </summary>
public interface IEpgProviderWithPrewarm : IEpgProvider
{
    /// <summary>
    /// Pre-warms the provider's cache in the background.
    /// Called during plugin initialization to improve first-request latency.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the pre-warm operation.</returns>
    Task PrewarmAsync(CancellationToken cancellationToken);
}
