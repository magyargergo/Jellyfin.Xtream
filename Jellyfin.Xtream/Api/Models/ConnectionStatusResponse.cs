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

namespace Jellyfin.Xtream.Api.Models;

/// <summary>
/// Connection status for a single provider.
/// </summary>
public sealed class ProviderConnectionStatus
{
    /// <summary>
    /// Gets or sets the provider ID.
    /// </summary>
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the provider name.
    /// </summary>
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the maximum connections allowed by the provider.
    /// </summary>
    public int MaxConnections { get; set; }

    /// <summary>
    /// Gets or sets the active connections reported by the provider.
    /// </summary>
    public int ProviderActiveConnections { get; set; }

    /// <summary>
    /// Gets or sets the account status (Active, Expired, etc.).
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the account expiration date.
    /// </summary>
    public DateTime? ExpirationDate { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the account is a trial.
    /// </summary>
    public bool IsTrial { get; set; }

    /// <summary>
    /// Gets or sets any error message if connection info couldn't be retrieved.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether connection info was successfully retrieved.
    /// </summary>
    public bool IsOnline { get; set; }

    // ========== Resilience Service Data ==========

    /// <summary>
    /// Gets or sets the circuit breaker state (Closed, Open, HalfOpen, Isolated).
    /// </summary>
    public string CircuitState { get; set; } = "Closed";

    /// <summary>
    /// Gets or sets the health selection score (0-100, higher = better).
    /// Combines success rate, capacity, and recent activity.
    /// </summary>
    public int SelectionScore { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the provider is available for new connections.
    /// False when circuit is open or provider is blacklisted.
    /// </summary>
    public bool IsAvailable { get; set; } = true;

    /// <summary>
    /// Gets or sets the number of consecutive failures.
    /// </summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>
    /// Gets or sets the number of available connection slots for this provider.
    /// Calculated as MaxConnections - ProviderActiveConnections when online, 0 when offline.
    /// </summary>
    public int AvailableSlots { get; set; }
}

/// <summary>
/// Overall connection status response.
/// </summary>
[SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "DTO requires setter")]
[SuppressMessage("Design", "CA1002:Do not expose generic lists", Justification = "DTO requires setter")]
public sealed class ConnectionStatusResponse
{
    /// <summary>
    /// Gets or sets the total active streams managed by the plugin.
    /// </summary>
    public int PluginActiveStreams { get; set; }

    /// <summary>
    /// Gets or sets the configured maximum concurrent streams.
    /// </summary>
    public int ConfiguredMaxStreams { get; set; }

    /// <summary>
    /// Gets or sets the effective maximum streams (configured or provider's min).
    /// </summary>
    public int EffectiveMaxStreams { get; set; }

    /// <summary>
    /// Gets or sets the connection utilization percentage (0-100).
    /// </summary>
    public int UtilizationPercent { get; set; }

    /// <summary>
    /// Gets or sets the warning level (None, Warning, Critical).
    /// </summary>
    public string WarningLevel { get; set; } = "None";

    /// <summary>
    /// Gets or sets a warning message if utilization is high.
    /// </summary>
    public string? WarningMessage { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether connection limit enforcement is enabled.
    /// </summary>
    public bool EnforcementEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether auto-kill oldest stream is enabled.
    /// </summary>
    public bool AutoKillEnabled { get; set; }

    /// <summary>
    /// Gets or sets the number of available connection slots.
    /// </summary>
    public int AvailableSlots { get; set; }

    /// <summary>
    /// Gets or sets the total active connections across all providers.
    /// This includes connections from ALL clients using these credentials, not just this plugin.
    /// </summary>
    public int TotalProviderActiveConnections { get; set; }

    /// <summary>
    /// Gets or sets the total provider capacity (sum of MaxConnections across all online providers).
    /// </summary>
    public int TotalProviderCapacity { get; set; }

    /// <summary>
    /// Gets or sets per-provider connection status.
    /// </summary>
    public List<ProviderConnectionStatus> Providers { get; set; } = [];
}
