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

namespace Jellyfin.Xtream.Service.Discovery;

/// <summary>
/// Represents the current phase of the discovery operation.
/// </summary>
public enum DiscoveryPhase
{
    /// <summary>
    /// Currently discovering credential sources.
    /// </summary>
    Discovering,

    /// <summary>
    /// Currently testing discovered credentials.
    /// </summary>
    Testing,

    /// <summary>
    /// Operation completed.
    /// </summary>
    Completed,

    /// <summary>
    /// Operation failed.
    /// </summary>
    Failed,
}

/// <summary>
/// Represents the progress of a discovery and testing operation.
/// </summary>
public sealed class DiscoveryProgress
{
    /// <summary>
    /// Gets or sets the current phase of the operation.
    /// </summary>
    public DiscoveryPhase Phase { get; set; }

    /// <summary>
    /// Gets or sets the current item being processed.
    /// </summary>
    public int CurrentItem { get; set; }

    /// <summary>
    /// Gets or sets the total items to process.
    /// </summary>
    public int TotalItems { get; set; }

    /// <summary>
    /// Gets or sets the count of credentials found so far.
    /// </summary>
    public int CredentialsFound { get; set; }

    /// <summary>
    /// Gets or sets the count of providers that passed connectivity check.
    /// </summary>
    public int ConnectivityPassed { get; set; }

    /// <summary>
    /// Gets or sets the count of providers that passed authentication.
    /// </summary>
    public int AuthenticationPassed { get; set; }

    /// <summary>
    /// Gets or sets the count of working providers (Active + Stream works).
    /// </summary>
    public int WorkingProviders { get; set; }

    /// <summary>
    /// Gets or sets the count of working providers with EPG (Active + Stream + EPG).
    /// </summary>
    public int WorkingWithEpg { get; set; }

    /// <summary>
    /// Gets or sets the count of fully working providers with Polish channels (Active + Stream + EPG + Polish).
    /// </summary>
    public int FullyWorking { get; set; }

    /// <summary>
    /// Gets or sets the count of excellent providers (fully working + high quality streams).
    /// </summary>
    public int ExcellentProviders { get; set; }

    /// <summary>
    /// Gets or sets the current status message.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the count of items currently being processed across all stages.
    /// </summary>
    public int InProgress { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the pipeline has finished processing all items.
    /// </summary>
    public bool IsComplete { get; set; }

    /// <summary>
    /// Gets the progress percentage (0-100).
    /// </summary>
    public int ProgressPercent => TotalItems > 0 ? CurrentItem * 100 / TotalItems : 0;
}
