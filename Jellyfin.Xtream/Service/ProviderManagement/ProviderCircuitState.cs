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

using Polly.CircuitBreaker;

namespace Jellyfin.Xtream.Service.ProviderManagement;

/// <summary>
/// Circuit breaker state for a provider.
/// This abstracts away the Polly implementation detail.
/// </summary>
public enum ProviderCircuitState
{
    /// <summary>
    /// Circuit is closed - provider is healthy and accepting requests.
    /// </summary>
    Closed = 0,

    /// <summary>
    /// Circuit is open - provider has failed and requests are being blocked.
    /// </summary>
    Open = 1,

    /// <summary>
    /// Circuit is half-open - testing if provider has recovered.
    /// </summary>
    HalfOpen = 2,

    /// <summary>
    /// Circuit is isolated - provider has been manually isolated (e.g., connection limit reached).
    /// </summary>
    Isolated = 3,
}

/// <summary>
/// Extension methods for circuit state conversion.
/// </summary>
internal static class CircuitStateExtensions
{
    /// <summary>
    /// Converts a Polly CircuitState to ProviderCircuitState.
    /// </summary>
    /// <param name="state">The Polly circuit state.</param>
    /// <returns>The provider circuit state.</returns>
    public static ProviderCircuitState ToProviderCircuitState(this CircuitState state) =>
        state switch
        {
            CircuitState.Closed => ProviderCircuitState.Closed,
            CircuitState.Open => ProviderCircuitState.Open,
            CircuitState.HalfOpen => ProviderCircuitState.HalfOpen,
            CircuitState.Isolated => ProviderCircuitState.Isolated,
            _ => ProviderCircuitState.Closed,
        };

    /// <summary>
    /// Converts a ProviderCircuitState to Polly CircuitState.
    /// </summary>
    /// <param name="state">The provider circuit state.</param>
    /// <returns>The Polly circuit state.</returns>
    public static CircuitState ToPollyCircuitState(this ProviderCircuitState state) =>
        state switch
        {
            ProviderCircuitState.Closed => CircuitState.Closed,
            ProviderCircuitState.Open => CircuitState.Open,
            ProviderCircuitState.HalfOpen => CircuitState.HalfOpen,
            ProviderCircuitState.Isolated => CircuitState.Isolated,
            _ => CircuitState.Closed,
        };
}
