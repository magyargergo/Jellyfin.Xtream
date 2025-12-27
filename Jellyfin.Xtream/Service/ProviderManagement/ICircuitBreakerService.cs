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

using System.Threading.Tasks;
using Polly.CircuitBreaker;

namespace Jellyfin.Xtream.Service.ProviderManagement;

/// <summary>
/// Manages circuit breaker state for providers.
/// </summary>
public interface ICircuitBreakerService
{
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
    CircuitState GetCircuitState(string providerId);

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
}
