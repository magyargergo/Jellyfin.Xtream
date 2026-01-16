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

namespace Jellyfin.Xtream.Service.ProviderManagement;

/// <summary>
/// Unified interface for provider resilience management.
/// Combines circuit breaker, health scoring, and capacity tracking.
/// </summary>
public interface IProviderAvailabilityService
{
    #region Circuit Breaker Operations

    /// <summary>
    /// Checks if a provider's circuit is available (not open or isolated).
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>True if the circuit allows connections.</returns>
    bool IsCircuitAvailable(string providerId);

    /// <summary>
    /// Gets the current circuit state for a provider.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>The circuit breaker state.</returns>
    ProviderCircuitState GetCircuitState(string providerId);

    /// <summary>
    /// Records a successful connection, potentially closing a half-open circuit.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    void RecordSuccess(string providerId);

    /// <summary>
    /// Records a failed connection, potentially opening the circuit.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="reason">The failure reason.</param>
    /// <param name="providerName">Optional provider name for logging.</param>
    /// <returns>True if the circuit was opened as a result.</returns>
    bool RecordFailure(string providerId, ProviderFailureReason reason, string? providerName = null);

    /// <summary>
    /// Immediately isolates a provider's circuit (connection limit reached).
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>A task representing the async operation.</returns>
    Task IsolateCircuitAsync(string providerId);

    /// <summary>
    /// Manually resets a provider's circuit to closed state.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>A task representing the async operation.</returns>
    Task ResetCircuitAsync(string providerId);

    #endregion

    #region Health Scoring

    /// <summary>
    /// Checks if a provider is available for new connections.
    /// Combines circuit breaker state with capacity checking.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>True if the provider should be tried.</returns>
    bool IsAvailable(string providerId);

    /// <summary>
    /// Gets a weighted score for provider selection (0-100, higher = better).
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>The weighted selection score.</returns>
    int GetSelectionScore(string providerId);

    /// <summary>
    /// Gets providers sorted by selection score for failover.
    /// </summary>
    /// <param name="providers">The providers to sort.</param>
    /// <param name="forceIncludeAll">Include unavailable providers.</param>
    /// <returns>Providers sorted by selection score descending.</returns>
    IReadOnlyList<ProviderStreamInfo> GetSortedProviders(
        IEnumerable<ProviderStreamInfo> providers,
        bool forceIncludeAll = false
    );

    #endregion

    #region Capacity Tracking

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

    #endregion

    #region State Management

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

    #endregion
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

    /// <summary>Provider's origin server returned 407 Proxy Authentication Required.</summary>
    ProxyAuthenticationError,

    /// <summary>
    /// Zombie backend: TCP connected but HTTP response headers never received.
    /// Indicates load balancer routing to a dead or overloaded backend server.
    /// </summary>
    ZombieBackend,
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
    public required ProviderCircuitState CircuitState { get; init; }

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
