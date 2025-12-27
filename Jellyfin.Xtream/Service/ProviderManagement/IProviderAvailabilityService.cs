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
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Configuration;
using Polly.CircuitBreaker;

namespace Jellyfin.Xtream.Service.ProviderManagement;

/// <summary>
/// Unified interface for provider resilience management.
/// Combines circuit breaker, health scoring, and capacity tracking.
/// </summary>
public interface IProviderAvailabilityService : ICircuitBreakerService, IProviderHealthScorer, IProviderCapacityTracker
{
    /// <summary>
    /// Gets a snapshot of all tracked provider states.
    /// </summary>
    /// <returns>Dictionary of provider IDs to their current states.</returns>
    IReadOnlyDictionary<string, ProviderResilienceState> GetSnapshot();

    /// <summary>
    /// Gets the cached state for a specific provider.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>The cached state, or null if not available.</returns>
    ProviderResilienceState? GetStatus(string providerId);

    /// <summary>
    /// Refreshes the connection status for all enabled providers.
    /// Makes HTTP API calls to fetch current account status and connection info.
    /// </summary>
    /// <param name="providers">The providers to refresh.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    Task RefreshAsync(IEnumerable<XtreamProvider> providers, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a refresh is needed based on cache age.
    /// </summary>
    /// <returns>True if refresh is recommended.</returns>
    bool NeedsRefresh();
}

/// <summary>
/// Reasons why a provider connection might fail.
/// Used for failure categorization and blacklist duration decisions.
/// </summary>
public enum ProviderFailureReason
{
    /// <summary>Unknown or uncategorized failure.</summary>
    Unknown,

    /// <summary>Network connectivity issues.</summary>
    NetworkError,

    /// <summary>Server returned an error response (5xx).</summary>
    ServerError,

    /// <summary>Client error (4xx).</summary>
    ClientError,

    /// <summary>Connection limit reached on provider.</summary>
    ConnectionLimit,

    /// <summary>Request timed out.</summary>
    Timeout,

    /// <summary>Rate limited by provider.</summary>
    RateLimited,

    /// <summary>Stream ended prematurely (EOF).</summary>
    PrematureEof,

    /// <summary>Too many transient errors in a short period.</summary>
    TransientErrors,
}

/// <summary>
/// Snapshot of a provider's resilience state.
/// </summary>
public readonly record struct ProviderResilienceState
{
    /// <summary>
    /// Gets the provider ID.
    /// </summary>
    public required string ProviderId { get; init; }

    /// <summary>
    /// Gets the circuit breaker state.
    /// </summary>
    public required CircuitState CircuitState { get; init; }

    /// <summary>
    /// Gets the weighted selection score (0-100).
    /// </summary>
    public required int SelectionScore { get; init; }

    /// <summary>
    /// Gets a value indicating whether the provider is available.
    /// </summary>
    public required bool IsAvailable { get; init; }

    /// <summary>
    /// Gets the number of consecutive failures.
    /// </summary>
    public required int ConsecutiveFailures { get; init; }

    /// <summary>
    /// Gets the available connection slots.
    /// </summary>
    public required int AvailableSlots { get; init; }

    /// <summary>
    /// Gets the maximum connections.
    /// </summary>
    public required int MaxConnections { get; init; }

    /// <summary>
    /// Gets the timestamp of this snapshot.
    /// </summary>
    public required DateTime Timestamp { get; init; }

    /// <summary>
    /// Gets a value indicating whether the provider is online (reachable via API).
    /// </summary>
    public bool IsOnline { get; init; }

    /// <summary>
    /// Gets the current active connections reported by the provider.
    /// </summary>
    public int ActiveConnections { get; init; }

    /// <summary>
    /// Gets the account status (Active, Expired, etc.).
    /// </summary>
    public string? Status { get; init; }

    /// <summary>
    /// Gets the account expiration date.
    /// </summary>
    public DateTime? ExpirationDate { get; init; }

    /// <summary>
    /// Gets a value indicating whether the account is a trial.
    /// </summary>
    public bool IsTrial { get; init; }

    /// <summary>
    /// Gets any error message if connection info couldn't be retrieved.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Gets the utilization percentage (0-100).
    /// </summary>
    public int UtilizationPercent =>
        MaxConnections > 0 ? (int)Math.Round(100.0 * ActiveConnections / MaxConnections) : 100;

    /// <summary>
    /// Gets a value indicating whether the provider has available capacity.
    /// </summary>
    public bool HasCapacity => IsOnline && AvailableSlots > 0;
}
