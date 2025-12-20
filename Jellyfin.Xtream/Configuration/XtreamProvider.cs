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
using System.Collections.Generic;

namespace Jellyfin.Xtream.Configuration;

/// <summary>
/// Represents an Xtream provider configuration with credentials and channel selections.
/// </summary>
public class XtreamProvider
{
    /// <summary>
    /// Gets or sets the unique identifier for this provider.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Gets or sets the display name for this provider.
    /// </summary>
    public string Name { get; set; } = "Provider";

    /// <summary>
    /// Gets or sets the base URL of the Xtream provider.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the username for authentication.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the password for authentication.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this provider is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets the selected Live TV categories and streams.
    /// Key: Category ID, Value: Set of Stream IDs.
    /// </summary>
    public SerializableDictionary<int, HashSet<int>> LiveTv { get; init; } = [];

    /// <summary>
    /// Gets the selected VOD categories and streams.
    /// Key: Category ID, Value: Set of Stream IDs.
    /// </summary>
    public SerializableDictionary<int, HashSet<int>> Vod { get; init; } = [];

    /// <summary>
    /// Gets the selected Series categories.
    /// Key: Category ID, Value: Set of Series IDs.
    /// </summary>
    public SerializableDictionary<int, HashSet<int>> Series { get; init; } = [];

    /// <summary>
    /// Gets channel-specific overrides for Live TV.
    /// Key: Stream ID, Value: Channel overrides.
    /// </summary>
    public SerializableDictionary<int, ChannelOverrides> LiveTvOverrides { get; init; } = [];

    /// <summary>
    /// Gets a deterministic hash of the provider ID for use in stream GUIDs.
    /// CRITICAL: Uses FNV-1a instead of string.GetHashCode() because .NET's hash
    /// is randomized per-process for security. This ensures channel IDs remain
    /// valid across Jellyfin restarts.
    /// </summary>
    /// <returns>A deterministic 32-bit hash value derived from the provider ID.</returns>
    public int GetIdHash()
    {
        // FNV-1a hash: deterministic, fast, good distribution
        // CRITICAL: Do NOT use string.GetHashCode() - it's randomized per-process in .NET Core!
        unchecked
        {
            const uint FnvPrime = 16777619;
            const uint FnvOffsetBasis = 2166136261;

            uint hash = FnvOffsetBasis;
            foreach (char c in Id)
            {
                hash ^= c;
                hash *= FnvPrime;
            }

            return (int)hash;
        }
    }

    /// <summary>
    /// Creates a ConnectionInfo for this provider.
    /// </summary>
    /// <returns>A ConnectionInfo instance with this provider's credentials.</returns>
    public Client.ConnectionInfo ToConnectionInfo()
    {
        return new Client.ConnectionInfo(BaseUrl, Username, Password);
    }
}
