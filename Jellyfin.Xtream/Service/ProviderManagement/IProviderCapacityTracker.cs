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
/// Tracks provider connection capacity.
/// </summary>
public interface IProviderCapacityTracker
{
    /// <summary>
    /// Checks if a provider has available connection capacity.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>True if the provider has capacity for new connections.</returns>
    bool HasCapacity(string providerId);

    /// <summary>
    /// Updates capacity information for a provider.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="availableSlots">Available connection slots.</param>
    /// <param name="maxConnections">Maximum connections allowed.</param>
    void UpdateCapacity(string providerId, int availableSlots, int maxConnections);

    /// <summary>
    /// Gets the available connection slots for a provider.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>The number of available slots, or -1 if unknown.</returns>
    int GetAvailableSlots(string providerId);

    /// <summary>
    /// Gets the maximum connections for a provider.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>The maximum connections, or 0 if unlimited/unknown.</returns>
    int GetMaxConnections(string providerId);
}
