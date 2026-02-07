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

    /// <summary>
    /// Gets or sets the DNS query timeout in seconds (1-30).
    /// </summary>
    public int DnsTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Gets or sets a value indicating whether TCP keepalive is enabled.
    /// </summary>
    public bool TcpKeepaliveEnabled { get; set; } = true;
}

/// <summary>
/// Request model for User-Agent configuration.
/// </summary>
public sealed class UserAgentConfigRequest
{
    /// <summary>
    /// Gets or sets the custom User-Agent header (null to use default).
    /// </summary>
    public string? CustomUserAgent { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to rotate User-Agent headers.
    /// </summary>
    public bool EnableRotation { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to use random selection (vs sequential).
    /// </summary>
    public bool UseRandom { get; set; } = true;
}

/// <summary>
/// Request model for rate limiting configuration.
/// </summary>
public sealed class RateLimitConfigRequest
{
    /// <summary>
    /// Gets or sets a value indicating whether rate limiting is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the maximum requests per second (1-50).
    /// </summary>
    public int RequestsPerSecond { get; set; } = 5;

    /// <summary>
    /// Gets or sets the burst size (1-100).
    /// </summary>
    public int BurstSize { get; set; } = 20;
}

/// <summary>
/// Request model for content visibility configuration.
/// </summary>
public sealed class VisibilityConfigRequest
{
    /// <summary>
    /// Gets or sets a value indicating whether Catch-up is visible.
    /// </summary>
    public bool IsCatchupVisible { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Series is visible.
    /// </summary>
    public bool IsSeriesVisible { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether VOD is visible.
    /// </summary>
    public bool IsVodVisible { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether TMDB VOD override is enabled.
    /// </summary>
    public bool IsTmdbVodOverride { get; set; } = true;
}

/// <summary>
/// Request model for failover configuration.
/// </summary>
public sealed class FailoverConfigRequest
{
    /// <summary>
    /// Gets or sets a value indicating whether duplicate channel merging is enabled.
    /// </summary>
    public bool MergeDuplicateChannels { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether provider failover is enabled.
    /// </summary>
    public bool EnableProviderFailover { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to skip unavailable providers.
    /// </summary>
    public bool SkipUnavailableProviders { get; set; } = true;

    /// <summary>
    /// Gets or sets the provider check interval in seconds (15-300).
    /// </summary>
    public int ProviderCheckIntervalSeconds { get; set; } = 60;
}

/// <summary>
/// Request model for connection limit configuration.
/// </summary>
public sealed class ConnectionLimitConfigRequest
{
    /// <summary>
    /// Gets or sets a value indicating whether connection limits are enforced.
    /// </summary>
    public bool EnforceConnectionLimit { get; set; }

    /// <summary>
    /// Gets or sets the max concurrent streams (0 = use provider limit).
    /// </summary>
    public int MaxConcurrentStreams { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to auto-kill oldest stream at limit.
    /// </summary>
    public bool AutoKillOldestStream { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to hide channels from providers at capacity.
    /// </summary>
    public bool FilterChannelsByCapacity { get; set; }
}

/// <summary>
/// Request model for hedging configuration.
/// </summary>
public sealed class HedgingConfigRequest
{
    /// <summary>
    /// Gets or sets a value indicating whether hedging is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the hedging delay in milliseconds (50-2000).
    /// </summary>
    public int DelayMs { get; set; } = 200;

    /// <summary>
    /// Gets or sets the max hedged attempts (1-5).
    /// </summary>
    public int MaxAttempts { get; set; } = 2;
}

/// <summary>
/// Request model for buffer health configuration.
/// </summary>
public sealed class BufferConfigRequest
{
    /// <summary>
    /// Gets or sets the underrun threshold percentage (1-50).
    /// </summary>
    public double UnderrunThresholdPercent { get; set; } = 10.0;

    /// <summary>
    /// Gets or sets the near-full threshold percentage (50-99).
    /// </summary>
    public double NearFullThresholdPercent { get; set; } = 90.0;

    /// <summary>
    /// Gets or sets the underrun notification threshold (1-50).
    /// </summary>
    public int UnderrunNotificationThreshold { get; set; } = 5;

    /// <summary>
    /// Gets or sets the consumer disconnect grace period in seconds (1-60).
    /// </summary>
    public int ConsumerDisconnectGraceSeconds { get; set; } = 5;
}

/// <summary>
/// Request model for logging configuration.
/// </summary>
public sealed class LoggingConfigRequest
{
    /// <summary>
    /// Gets or sets a value indicating whether debug logging is enabled.
    /// </summary>
    public bool EnableDebugLogging { get; set; }

    /// <summary>
    /// Gets or sets the log viewer max entries (100-50000).
    /// </summary>
    public int LogViewerMaxEntries { get; set; } = 5000;

    /// <summary>
    /// Gets or sets a value indicating whether periodic health reports are enabled.
    /// </summary>
    public bool EnablePeriodicHealthReports { get; set; }

    /// <summary>
    /// Gets or sets the health report interval in minutes (5-1440).
    /// </summary>
    public int HealthReportIntervalMinutes { get; set; } = 60;
}

/// <summary>
/// Request model for stream processing configuration.
/// </summary>
public sealed class StreamProcessingConfigRequest
{
    /// <summary>
    /// Gets or sets a value indicating whether to force FFmpeg remuxing.
    /// </summary>
    public bool ForceRemux { get; set; } = true;
}

/// <summary>
/// Request model for batch channel override operations.
/// </summary>
public sealed class BatchChannelOverrideRequest
{
    /// <summary>
    /// Gets or sets the list of overrides to apply.
    /// </summary>
    public System.Collections.ObjectModel.Collection<BatchOverrideEntry> Overrides { get; } = [];
}

/// <summary>
/// A single entry in a batch channel override request.
/// </summary>
public sealed class BatchOverrideEntry
{
    /// <summary>
    /// Gets or sets the stream ID.
    /// </summary>
    public int StreamId { get; set; }

    /// <summary>
    /// Gets or sets the TV channel number override.
    /// </summary>
    public int? Number { get; set; }

    /// <summary>
    /// Gets or sets the TV channel name override.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the channel logo URL override.
    /// </summary>
    public string? LogoUrl { get; set; }
}

/// <summary>
/// Request model for health and load balancer configuration.
/// </summary>
public sealed class HealthConfigRequest
{
    /// <summary>
    /// Gets or sets a value indicating whether P2C load balancing is enabled.
    /// </summary>
    public bool EnableP2C { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether outlier detection is enabled.
    /// </summary>
    public bool EnableOutlierDetection { get; set; } = true;

    /// <summary>
    /// Gets or sets the outlier detection standard deviation factor (0.5-5.0).
    /// </summary>
    public double OutlierStddevFactor { get; set; } = 1.9;

    /// <summary>
    /// Gets or sets the probation success threshold (1-10).
    /// </summary>
    public int ProbationSuccessThreshold { get; set; } = 3;
}
