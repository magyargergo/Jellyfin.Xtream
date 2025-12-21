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
/// Options for the discovery and test operation.
/// </summary>
public sealed class DiscoveryOptions
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
}

/// <summary>
/// Service for discovering and testing provider credentials.
/// </summary>
public interface IProviderDiscoveryService
{
    /// <summary>
    /// Discovers credentials from configured sources and tests them.
    /// </summary>
    /// <param name="options">The discovery options.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The complete discovery and test result.</returns>
    Task<DiscoveryTestResult> DiscoverAndTestAsync(
        DiscoveryOptions options,
        IProgress<DiscoveryProgress>? progress,
        CancellationToken cancellationToken
    );

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
