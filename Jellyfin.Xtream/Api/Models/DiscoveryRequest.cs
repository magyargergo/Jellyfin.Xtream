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

namespace Jellyfin.Xtream.Api.Models;

/// <summary>
/// Time range options for discovery.
/// </summary>
public enum DiscoveryTimeRangeOption
{
    /// <summary>
    /// Last week (7 days).
    /// </summary>
    LastWeek,

    /// <summary>
    /// Last month (30 days).
    /// </summary>
    LastMonth,

    /// <summary>
    /// Last 3 months (90 days).
    /// </summary>
    Last3Months,

    /// <summary>
    /// Last 6 months (180 days).
    /// </summary>
    Last6Months,

    /// <summary>
    /// This year (from January 1st).
    /// </summary>
    ThisYear,

    /// <summary>
    /// Last year (previous calendar year).
    /// </summary>
    LastYear,

    /// <summary>
    /// Custom date range (uses CustomStartDate and CustomEndDate).
    /// </summary>
    Custom,
}

/// <summary>
/// Request model for discovering providers.
/// </summary>
public sealed class DiscoveryRequest
{
    /// <summary>
    /// Gets or sets the time range for discovery.
    /// </summary>
    public DiscoveryTimeRangeOption TimeRange { get; set; } = DiscoveryTimeRangeOption.LastMonth;

    /// <summary>
    /// Gets or sets the custom start date (used when TimeRange is Custom).
    /// Format: yyyy-MM-dd.
    /// </summary>
    public DateTime? CustomStartDate { get; set; }

    /// <summary>
    /// Gets or sets the custom end date (used when TimeRange is Custom).
    /// Format: yyyy-MM-dd.
    /// </summary>
    public DateTime? CustomEndDate { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of parallel workers for discovery.
    /// </summary>
    public int MaxDiscoveryWorkers { get; set; } = 5;

    /// <summary>
    /// Gets or sets a value indicating whether to test stream playback.
    /// </summary>
    public bool TestStream { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to test EPG availability.
    /// </summary>
    public bool TestEpg { get; set; } = true;

    /// <summary>
    /// Gets or sets the country code to filter channels by.
    /// </summary>
    /// <remarks>
    /// Supported values: "PL" (Poland), "UK" (United Kingdom), "DE" (Germany), "FR" (France), or null for no filtering.
    /// Defaults to "PL" for backward compatibility.
    /// </remarks>
    public string? CountryCode { get; set; } = "PL";
}
