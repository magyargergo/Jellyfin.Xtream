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

namespace Jellyfin.Xtream.Api.Models;

/// <summary>
/// Request model for discovering providers.
/// </summary>
public sealed class DiscoveryRequest
{
    /// <summary>
    /// Gets or sets the maximum number of pages to process.
    /// </summary>
    public int MaxPages { get; set; } = 5;

    /// <summary>
    /// Gets or sets the maximum number of parallel workers for discovery.
    /// </summary>
    public int MaxDiscoveryWorkers { get; set; } = 5;

    /// <summary>
    /// Gets or sets the maximum number of parallel workers for testing.
    /// </summary>
    public int MaxTestWorkers { get; set; } = 10;

    /// <summary>
    /// Gets or sets a value indicating whether to test stream playback.
    /// </summary>
    public bool TestStream { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to test EPG availability.
    /// </summary>
    public bool TestEpg { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to filter for Polish channels only.
    /// </summary>
    public bool PolishOnly { get; set; } = true;
}

/// <summary>
/// Backward compatibility alias for DiscoveryRequest.
/// </summary>
public sealed class ScrapeRequest
{
    /// <summary>
    /// Gets or sets the maximum number of pages to process.
    /// </summary>
    public int MaxPages { get; set; } = 5;

    /// <summary>
    /// Gets or sets the maximum number of parallel workers for discovery.
    /// </summary>
    public int MaxScrapeWorkers { get; set; } = 5;

    /// <summary>
    /// Gets or sets the maximum number of parallel workers for testing.
    /// </summary>
    public int MaxTestWorkers { get; set; } = 10;

    /// <summary>
    /// Gets or sets a value indicating whether to test stream playback.
    /// </summary>
    public bool TestStream { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to test EPG availability.
    /// </summary>
    public bool TestEpg { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to filter for Polish channels only.
    /// </summary>
    public bool PolishOnly { get; set; } = true;
}
