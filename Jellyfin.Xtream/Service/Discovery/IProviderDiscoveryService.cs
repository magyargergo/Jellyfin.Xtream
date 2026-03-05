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
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Xtream.Service.Discovery;

/// <summary>
/// Time range for credential discovery.
/// </summary>
public enum DiscoveryTimeRange
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
/// Options for the discovery and test operation.
/// </summary>
public sealed class DiscoveryOptions
{
    /// <summary>
    /// Gets or sets the time range for discovery.
    /// </summary>
    public DiscoveryTimeRange TimeRange { get; set; } = DiscoveryTimeRange.LastMonth;

    /// <summary>
    /// Gets or sets the custom start date (used when TimeRange is Custom).
    /// </summary>
    public DateTime? CustomStartDate { get; set; }

    /// <summary>
    /// Gets or sets the custom end date (used when TimeRange is Custom).
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

    /// <summary>
    /// Gets the start date for the discovery time range.
    /// </summary>
    /// <returns>The start date.</returns>
    public DateTime GetStartDate()
    {
        var today = DateTime.UtcNow.Date;
        return TimeRange switch
        {
            DiscoveryTimeRange.LastWeek => today.AddDays(-7),
            DiscoveryTimeRange.LastMonth => today.AddDays(-30),
            DiscoveryTimeRange.Last3Months => today.AddDays(-90),
            DiscoveryTimeRange.Last6Months => today.AddDays(-180),
            DiscoveryTimeRange.ThisYear => new DateTime(today.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            DiscoveryTimeRange.LastYear => today.AddYears(-1).Date is var lastYearDate
                ? new DateTime(lastYearDate.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                : today.AddDays(-365),
            DiscoveryTimeRange.Custom => CustomStartDate?.Date ?? today.AddDays(-30),
            _ => today.AddDays(-30),
        };
    }

    /// <summary>
    /// Gets the end date for the discovery time range.
    /// </summary>
    /// <returns>The end date.</returns>
    public DateTime GetEndDate()
    {
        var today = DateTime.UtcNow.Date;
        return TimeRange switch
        {
            DiscoveryTimeRange.LastYear => today.AddYears(-1).Date is var lastYearEnd
                ? new DateTime(lastYearEnd.Year, 12, 31, 23, 59, 59, DateTimeKind.Utc)
                : today.AddDays(-1),
            DiscoveryTimeRange.Custom => CustomEndDate?.Date ?? today,
            _ => today,
        };
    }
}

/// <summary>
/// The complete result of a discovery and test operation.
/// </summary>
[SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "DTO requires setter")]
[SuppressMessage("Design", "CA1002:Do not expose generic lists", Justification = "DTO requires setter")]
public sealed class DiscoveryTestResult
{
    /// <summary>
    /// Gets or sets a value indicating whether the operation was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets any error message.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Gets or sets the discovery result.
    /// </summary>
    public DiscoveryResult? DiscoveryResult { get; set; }

    /// <summary>
    /// Gets or sets all test results.
    /// </summary>
    public List<ProviderTestResult> TestResults { get; set; } = [];

    /// <summary>
    /// Gets or sets the working providers (active + stream works).
    /// </summary>
    public List<ProviderTestResult> WorkingProviders { get; set; } = [];

    /// <summary>
    /// Gets or sets the working providers with EPG (active + stream + EPG).
    /// </summary>
    public List<ProviderTestResult> WorkingWithEpgProviders { get; set; } = [];

    /// <summary>
    /// Gets or sets the fully working providers (active + stream + EPG + Polish).
    /// </summary>
    public List<ProviderTestResult> FullyWorkingProviders { get; set; } = [];

    /// <summary>
    /// Gets or sets the excellent providers (fully working + high quality streams).
    /// </summary>
    public List<ProviderTestResult> ExcellentProviders { get; set; } = [];
}

/// <summary>
/// Service for discovering and testing provider credentials.
/// </summary>
public interface IProviderDiscoveryService
{
    /// <summary>
    /// Starts a discovery operation in the background and returns immediately.
    /// Use <see cref="GetProgressUpdatesAsync"/> to receive progress updates.
    /// </summary>
    /// <param name="options">The discovery options.</param>
    /// <returns>True if the operation was started, false if one is already running.</returns>
    bool StartDiscoveryAsync(DiscoveryOptions options);

    /// <summary>
    /// Gets an async enumerable of progress updates for the current discovery operation.
    /// Completes when the operation finishes.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of progress updates.</returns>
    IAsyncEnumerable<DiscoveryProgress> GetProgressUpdatesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets the current progress of the discovery operation.
    /// </summary>
    /// <returns>The current progress, or null if no operation is running.</returns>
    DiscoveryProgress? GetCurrentProgress();

    /// <summary>
    /// Gets the result of the last completed discovery operation.
    /// </summary>
    /// <returns>The result, or null if no operation has completed.</returns>
    DiscoveryTestResult? GetLastResult();

    /// <summary>
    /// Gets whether a discovery operation is currently in progress.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Cancels the current discovery operation if one is running.
    /// </summary>
    void Cancel();

    /// <summary>
    /// Clears the cached discovery results.
    /// </summary>
    /// <returns>True if cache was cleared, false if no cache existed.</returns>
    bool ClearCache();
}
