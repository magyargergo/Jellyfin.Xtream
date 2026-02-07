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
/// Request model for proxy configuration.
/// </summary>
public sealed class ProxyConfigRequest
{
    /// <summary>
    /// Gets or sets a value indicating whether proxy is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the proxy protocol type (Http, Socks4, Socks4a, Socks5).
    /// </summary>
    public string Type { get; set; } = "Http";

    /// <summary>
    /// Gets or sets the proxy server address.
    /// </summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the proxy server port.
    /// </summary>
    public int Port { get; set; } = 8080;

    /// <summary>
    /// Gets or sets the proxy username.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the proxy password.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether to bypass proxy for local addresses.
    /// </summary>
    public bool BypassLocal { get; set; } = true;
}

/// <summary>
/// Request model for EPG configuration.
/// </summary>
public sealed class EpgConfigRequest
{
    /// <summary>
    /// Gets or sets a value indicating whether external EPG is enabled.
    /// </summary>
    public bool EnableExternalEpg { get; set; }

    /// <summary>
    /// Gets or sets the external EPG XMLTV URL.
    /// </summary>
    public string ExternalEpgUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the base URL for external EPG logos.
    /// </summary>
    public string ExternalEpgLogoBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether to use external logos as fallback.
    /// </summary>
    public bool UseExternalLogoFallback { get; set; } = true;
}

/// <summary>
/// Request model for Discord notification configuration.
/// </summary>
public sealed class DiscordConfigRequest
{
    /// <summary>
    /// Gets or sets a value indicating whether Discord notifications are enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the Discord webhook URL.
    /// </summary>
    public string WebhookUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether to notify on buffer overflows.
    /// </summary>
    public bool NotifyOnBufferOverflow { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to notify on stream start.
    /// </summary>
    public bool NotifyOnStreamStart { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to notify on stream errors.
    /// </summary>
    public bool NotifyOnStreamError { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to notify on stream killed.
    /// </summary>
    public bool NotifyOnStreamKilled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to notify on quality violations.
    /// </summary>
    public bool NotifyOnStreamQualityViolation { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to notify on A/V drift.
    /// </summary>
    public bool NotifyOnAVDrift { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to notify on audio sync corrections.
    /// </summary>
    public bool NotifyOnAudioSyncCorrection { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to notify on EPG refresh.
    /// </summary>
    public bool NotifyOnEpgRefresh { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to notify on connection limit changes.
    /// </summary>
    public bool NotifyOnConnectionLimitChange { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to notify on provider blacklist.
    /// </summary>
    public bool NotifyOnProviderBlacklist { get; set; } = true;
}

/// <summary>
/// Request model for streaming timeout configuration.
/// </summary>
public sealed class TimeoutConfigRequest
{
    /// <summary>
    /// Gets or sets the connection timeout in seconds (1-30).
    /// </summary>
    public int ConnectTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Gets or sets the first byte timeout in seconds (1-30).
    /// </summary>
    public int FirstByteTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Gets or sets the HTTP response headers timeout in seconds (5-30).
    /// </summary>
    public int ResponseHeadersTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Gets or sets the data stall timeout in seconds (5-60).
    /// </summary>
    public int DataStallTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Gets or sets the total failover budget in seconds (5-30).
    /// </summary>
    public int FailoverBudgetSeconds { get; set; } = 15;

    /// <summary>
    /// Gets or sets the provider blacklist duration in seconds (10-300).
    /// </summary>
    public int ProviderBlacklistSeconds { get; set; } = 30;

    /// <summary>
    /// Gets or sets the maximum number of failover attempts.
    /// </summary>
    public int MaxFailoverAttempts { get; set; } = 3;
}
