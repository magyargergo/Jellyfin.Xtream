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

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Xml.Serialization;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Xtream.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
/// <remarks>
/// <para>
/// IMPORTANT: Property order matters for XML deserialization compatibility.
/// Legacy fields must be in the same order as the original plugin to properly
/// deserialize existing configuration files.
/// </para>
/// <para>
/// SA1201 is suppressed because property order is dictated by XML serialization compatibility,
/// not code style guidelines.
/// </para>
/// </remarks>
[SuppressMessage(
    "StyleCop.CSharp.OrderingRules",
    "SA1201:Elements should appear in correct order",
    Justification = "Property order is required for XML serialization compatibility"
)]
public class PluginConfiguration : BasePluginConfiguration
{
    // ============================================================================
    // Legacy fields for migration - MUST be in original order for XML compatibility
    // These fields are deprecated but kept for backwards compatibility
    // ============================================================================

    /// <summary>
    /// Gets or sets the base URL of the Xtream-compatible server (legacy, use Providers instead).
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the username (legacy, use Providers instead).
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the password (legacy, use Providers instead).
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the Catch-up channel is visible.
    /// </summary>
    public bool IsCatchupVisible { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the Series channel is visible.
    /// </summary>
    public bool IsSeriesVisible { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the Video On-demand channel is visible.
    /// </summary>
    public bool IsVodVisible { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the Video On-demand channel is visible.
    /// </summary>
    public bool IsTmdbVodOverride { get; set; } = true;

    /// <summary>
    /// Gets the selected Live TV categories and streams (legacy, use Providers instead).
    /// </summary>
    public SerializableDictionary<int, HashSet<int>> LiveTv { get; init; } = [];

    /// <summary>
    /// Gets the selected VOD categories and streams (legacy, use Providers instead).
    /// </summary>
    public SerializableDictionary<int, HashSet<int>> Vod { get; init; } = [];

    /// <summary>
    /// Gets the selected Series categories (legacy, use Providers instead).
    /// </summary>
    public SerializableDictionary<int, HashSet<int>> Series { get; init; } = [];

    /// <summary>
    /// Gets channel-specific overrides for Live TV (legacy, use Providers instead).
    /// </summary>
    public SerializableDictionary<int, ChannelOverrides> LiveTvOverrides { get; init; } = [];

    // ============================================================================
    // New properties (order doesn't matter for these since they're not in old XML)
    // ============================================================================

    /// <summary>
    /// Gets or sets a value indicating whether proxy is enabled.
    /// </summary>
    public bool EnableProxy { get; set; }

    /// <summary>
    /// Gets or sets the proxy protocol type.
    /// Supports HTTP, SOCKS4, SOCKS4a, and SOCKS5 protocols.
    /// </summary>
    public ProxyType ProxyType { get; set; } = ProxyType.Http;

    /// <summary>
    /// Gets or sets the proxy server address.
    /// </summary>
    public string ProxyAddress { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the proxy server port.
    /// </summary>
    public int ProxyPort { get; set; } = 8080;

    /// <summary>
    /// Gets or sets the proxy username (optional).
    /// </summary>
    public string ProxyUsername { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the proxy password (optional).
    /// </summary>
    public string ProxyPassword { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether to bypass proxy for local addresses.
    /// </summary>
    public bool ProxyBypassLocal { get; set; } = true;

    /// <summary>
    /// Gets or sets a custom User-Agent header for HTTP requests.
    /// </summary>
    /// <remarks>
    /// When set, overrides the default User-Agent. Useful for bypassing WAF/DDoS protection
    /// or when IPTV providers block non-browser User-Agents.
    /// Leave empty when using User-Agent rotation.
    /// </remarks>
    public string? CustomUserAgent { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to rotate User-Agent headers.
    /// </summary>
    /// <remarks>
    /// When enabled, rotates through a pool of realistic browser User-Agents to avoid
    /// WAF/DDoS protection blocking. This is useful when making many requests.
    /// Custom User-Agent takes precedence if set.
    /// </remarks>
    public bool EnableUserAgentRotation { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to use random User-Agent selection.
    /// </summary>
    /// <remarks>
    /// If true, randomly selects from User-Agent pool. If false, uses sequential rotation.
    /// Only applies when EnableUserAgentRotation is true.
    /// </remarks>
    public bool UseRandomUserAgent { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to enable request rate limiting.
    /// </summary>
    /// <remarks>
    /// When enabled, limits the number of HTTP requests to the IPTV provider to avoid
    /// triggering WAF/DDoS protection or rate limit bans.
    /// </remarks>
    public bool EnableRateLimiting { get; set; }

    /// <summary>
    /// Gets or sets the maximum requests per second.
    /// </summary>
    /// <remarks>
    /// Controls how many requests can be made per second. Lower values are safer but slower.
    /// Typical values: 1-10 requests/second depending on provider tolerance.
    /// </remarks>
    public int RequestsPerSecond { get; set; } = 5;

    /// <summary>
    /// Gets or sets the burst size for rate limiting.
    /// </summary>
    /// <remarks>
    /// Maximum number of requests that can be made in a burst before rate limiting kicks in.
    /// Allows initial burst of requests while still maintaining average rate.
    /// </remarks>
    public int BurstSize { get; set; } = 20;

    /// <summary>
    /// Gets or sets a value indicating whether Discord notifications are enabled.
    /// </summary>
    public bool EnableDiscordNotifications { get; set; }

    /// <summary>
    /// Gets or sets the Discord webhook URL.
    /// </summary>
    public string DiscordWebhookUrl { get; set; } = string.Empty;

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
    /// Gets or sets a value indicating whether to notify when a stream is killed.
    /// </summary>
    /// <remarks>
    /// When enabled, sends a Discord notification whenever a stream is terminated,
    /// whether manually via API, automatically due to connection limits, or for other reasons.
    /// </remarks>
    public bool NotifyOnStreamKilled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to notify on TR 101 290 stream quality violations.
    /// </summary>
    /// <remarks>
    /// When enabled, sends a Discord notification when stream quality issues are detected,
    /// such as invalid PCR PIDs, PAT/PMT version changes, or excessive PCR jitter.
    /// These violations indicate potential playback issues or stream configuration problems.
    /// </remarks>
    public bool NotifyOnStreamQualityViolation { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to notify on A/V synchronization drift.
    /// </summary>
    /// <remarks>
    /// When enabled, sends a Discord notification when audio and video streams drift out of sync
    /// beyond acceptable thresholds (40ms warning, 100ms severe). This can indicate stream encoding
    /// issues, network problems, or source stream quality problems.
    /// </remarks>
    public bool NotifyOnAVDrift { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to notify on audio sync corrections.
    /// </summary>
    /// <remarks>
    /// When enabled, sends a Discord notification when audio PTS correction is applied to fix
    /// audio/video desynchronization. This includes gradual corrections, immediate corrections,
    /// predictive corrections, and PTS resets for severe drift.
    /// </remarks>
    public bool NotifyOnAudioSyncCorrection { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to notify on EPG refresh completion.
    /// </summary>
    /// <remarks>
    /// When enabled, sends a Discord notification after EPG data refresh completes,
    /// reporting success, partial completion, or failure with diagnostic details.
    /// </remarks>
    public bool NotifyOnEpgRefresh { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to notify on connection limit changes.
    /// </summary>
    /// <remarks>
    /// When enabled, sends a Discord notification when a provider reaches its connection limit
    /// or when connections become available again. This helps monitor provider capacity without
    /// actively using the service.
    /// </remarks>
    public bool NotifyOnConnectionLimitChange { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to send periodic health reports.
    /// </summary>
    public bool EnablePeriodicHealthReports { get; set; }

    /// <summary>
    /// Gets or sets the interval in minutes for periodic health reports.
    /// </summary>
    public int HealthReportIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Gets or sets a value indicating whether debug logging is enabled.
    /// </summary>
    public bool EnableDebugLogging { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of log entries to keep in the log viewer buffer.
    /// </summary>
    /// <remarks>
    /// Higher values allow viewing more historical logs but use more memory.
    /// Default: 5000 entries. Range: 100-50000.
    /// </remarks>
    public int LogViewerMaxEntries { get; set; } = 5000;

    /// <summary>
    /// Gets or sets a value indicating whether to enforce connection limits.
    /// </summary>
    /// <remarks>
    /// When enabled, the plugin will check active connections before opening new streams
    /// and optionally kill existing streams when the limit is reached.
    /// </remarks>
    public bool EnforceConnectionLimit { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of concurrent streams.
    /// </summary>
    /// <remarks>
    /// Set to 0 to use the provider's max_connections limit automatically.
    /// Set to a positive value to override with a custom limit.
    /// </remarks>
    public int MaxConcurrentStreams { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to auto-kill the oldest stream when limit is reached.
    /// </summary>
    /// <remarks>
    /// When enabled, if a new stream is requested and the connection limit is reached,
    /// the oldest active stream will be killed to make room for the new one.
    /// When disabled, the new stream request will fail with an error.
    /// </remarks>
    public bool AutoKillOldestStream { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to filter out channels from providers at capacity.
    /// </summary>
    /// <remarks>
    /// When enabled, channels are only shown if at least one provider has available connection capacity.
    /// Channels from providers that are at their connection limit will be hidden until capacity frees up.
    /// This provides a cleaner user experience by hiding channels that cannot currently be streamed.
    /// </remarks>
    public bool FilterChannelsByCapacity { get; set; }

    /// <summary>
    /// Gets or sets the list of configured Xtream providers.
    /// </summary>
    /// <remarks>
    /// This is the new multi-provider configuration. Legacy single-provider fields
    /// above are migrated to this list on first startup.
    /// </remarks>
    [SuppressMessage(
        "Usage",
        "CA2227:Collection properties should be read only",
        Justification = "Required for XML deserialization"
    )]
    [SuppressMessage(
        "Design",
        "CA1002:Do not expose generic lists",
        Justification = "List<T> is required for XML serialization (interfaces not supported)"
    )]
    public List<XtreamProvider> Providers { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether to merge duplicate channels from multiple providers.
    /// </summary>
    /// <remarks>
    /// When enabled, channels with the same name from different providers are merged into one.
    /// The highest quality stream is selected by default, with failover to lower quality if unavailable.
    /// </remarks>
    public bool MergeDuplicateChannels { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to enable provider failover.
    /// </summary>
    /// <remarks>
    /// When enabled and a stream fails to connect, the system will automatically
    /// try alternative providers offering the same channel.
    /// </remarks>
    public bool EnableProviderFailover { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of failover attempts before giving up.
    /// </summary>
    public int MaxFailoverAttempts { get; set; } = 3;

    // ============================================================================
    // Streaming Timeout Configuration (Industry Standard Defaults)
    // Based on RFC 8216 (HLS), Apple HLS Best Practices, Google Media CDN
    // ============================================================================

    /// <summary>
    /// Gets or sets the connection timeout in seconds (1-30).
    /// </summary>
    /// <remarks>
    /// Time to establish TCP connection before failing over to next provider.
    /// Industry standard: 1-5 seconds for CDN failover scenarios.
    /// Default: 5 seconds.
    /// </remarks>
    public int StreamConnectTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Gets or sets the first byte timeout in seconds (1-30).
    /// </summary>
    /// <remarks>
    /// Time to receive first byte of data after connection.
    /// Detects "connected but no data" scenarios common with overloaded providers.
    /// Should be long enough for C++ native failover to try alternate URLs on timeout.
    /// Default: 15 seconds.
    /// </remarks>
    public int StreamFirstByteTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Gets or sets the HTTP response headers timeout in seconds (5-30).
    /// </summary>
    /// <remarks>
    /// Time to wait for HTTP response headers after TCP connection is established.
    /// Detects "zombie backend" scenarios where the server accepts TCP connections
    /// but never sends HTTP responses (common with load balancer routing to dead backends).
    /// Default: 10 seconds.
    /// </remarks>
    public int StreamResponseHeadersTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Gets or sets the data stall timeout in seconds (5-60).
    /// </summary>
    /// <remarks>
    /// Time without receiving data before triggering reconnection.
    /// Industry standard: 10-20 seconds (buffering threshold before rebuffering UI).
    /// Default: 10 seconds.
    /// </remarks>
    public int StreamDataStallTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Gets or sets the total failover budget in seconds (5-30).
    /// </summary>
    /// <remarks>
    /// Maximum time to spend trying all providers before returning error to user.
    /// For live TV streaming, 10-15s is acceptable for initial channel load.
    /// Default: 15 seconds.
    /// </remarks>
    public int FailoverBudgetSeconds { get; set; } = 15;

    /// <summary>
    /// Gets or sets the provider blacklist duration in seconds (10-300).
    /// </summary>
    /// <remarks>
    /// Time a provider is temporarily disabled after consecutive failures.
    /// Reduced from traditional 2-5 minutes for faster recovery.
    /// Default: 30 seconds.
    /// </remarks>
    public int ProviderBlacklistSeconds { get; set; } = 30;

    /// <summary>
    /// Gets or sets the interval in seconds between provider availability checks.
    /// </summary>
    /// <remarks>
    /// The background checker queries each provider's connection status at this interval.
    /// Lower values provide fresher data but increase API load.
    /// Valid range: 15-300 seconds. Default: 60 seconds.
    /// </remarks>
    public int ProviderCheckIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets a value indicating whether to skip unavailable providers during failover.
    /// </summary>
    /// <remarks>
    /// When enabled, providers that are known to be at capacity or offline will be skipped
    /// during the failover process, reducing failed connection attempts.
    /// </remarks>
    public bool SkipUnavailableProviders { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to notify on provider blacklist events.
    /// </summary>
    /// <remarks>
    /// When enabled, sends a Discord notification when a provider is temporarily blacklisted
    /// due to consecutive failures. Useful for monitoring provider health.
    /// </remarks>
    public bool NotifyOnProviderBlacklist { get; set; } = true;

    // ============================================================================
    // Provider Hedging Configuration
    // ============================================================================

    /// <summary>
    /// Gets or sets a value indicating whether provider hedging is enabled.
    /// </summary>
    /// <remarks>
    /// When enabled, starts backup provider connections in parallel if the primary
    /// provider is slow to respond. This reduces initial channel load time but may
    /// slightly increase provider API load.
    /// </remarks>
    public bool EnableHedging { get; set; }

    /// <summary>
    /// Gets or sets the delay in milliseconds before starting hedged requests.
    /// </summary>
    /// <remarks>
    /// Time to wait for primary provider before starting backup providers.
    /// Lower values improve failover speed but increase parallel connections.
    /// Industry standard: 100-500ms. Default: 200ms.
    /// </remarks>
    public int HedgingDelayMs { get; set; } = 200;

    /// <summary>
    /// Gets or sets the maximum number of hedged attempts.
    /// </summary>
    /// <remarks>
    /// Maximum backup providers to try in parallel with the primary.
    /// Higher values improve success rate but increase resource usage.
    /// Default: 2 (primary + 2 backups maximum).
    /// </remarks>
    public int MaxHedgedAttempts { get; set; } = 2;

    // ============================================================================
    // Stream Processing Configuration
    // ============================================================================

    /// <summary>
    /// Gets or sets a value indicating whether to force FFmpeg remuxing for live streams.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When enabled, Jellyfin will always remux live TV streams through FFmpeg instead of
    /// allowing direct playback. This fixes issues with:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Audio/video desynchronization after stream reconnections</description></item>
    /// <item><description>"non-existing PPS 0 referenced" errors in H.264 streams</description></item>
    /// <item><description>Missing SPS/PPS parameter sets after provider switching</description></item>
    /// <item><description>Discontinuity handling in MPEG-TS streams</description></item>
    /// </list>
    /// <para>
    /// FFmpeg remuxing uses codec copy (no re-encoding), so CPU usage is minimal.
    /// This is the recommended setting for problematic IPTV providers.
    /// </para>
    /// <para>
    /// Default: true (enabled). Disable only if you experience issues with FFmpeg transcoding
    /// or prefer direct playback for compatible clients.
    /// </para>
    /// </remarks>
    public bool ForceRemux { get; set; } = true;

    // ============================================================================
    // External EPG Configuration
    // ============================================================================

    /// <summary>
    /// Gets or sets a value indicating whether external EPG sources are enabled.
    /// </summary>
    /// <remarks>
    /// When enabled, the plugin will fetch EPG data from external XMLTV sources
    /// like epg.ovh as a fallback when the provider's EPG is unavailable.
    /// </remarks>
    public bool EnableExternalEpg { get; set; }

    /// <summary>
    /// Gets or sets the external EPG XMLTV URL.
    /// </summary>
    /// <remarks>
    /// URL to an external XMLTV source for EPG data. Example: https://epg.ovh/pl.xml for Polish channels.
    /// </remarks>
    public string ExternalEpgUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the base URL for external EPG logos.
    /// </summary>
    /// <remarks>
    /// Base URL for channel logos. The channel ID is appended to form the full URL.
    /// Example: https://epg.ovh/logo/ - logos are fetched as {base_url}/{channel_id}.png.
    /// Leave empty to disable logo fallback.
    /// </remarks>
    public string ExternalEpgLogoBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether to use external logos as fallback.
    /// </summary>
    /// <remarks>
    /// When enabled, if a channel has no logo from the provider, the plugin will
    /// attempt to fetch a logo from the external EPG logo URL.
    /// </remarks>
    public bool UseExternalLogoFallback { get; set; } = true;

    // ============================================================================
    // Computed properties and methods
    // ============================================================================

    /// <summary>
    /// Gets a value indicating whether legacy credentials need to be migrated.
    /// </summary>
    [XmlIgnore]
    public bool NeedsMigration =>
        !string.IsNullOrEmpty(BaseUrl)
        && BaseUrl != "https://example.com"
        && !string.IsNullOrEmpty(Username)
        && Providers.Count == 0;

    /// <summary>
    /// Gets all enabled providers.
    /// </summary>
    /// <returns>An enumerable of enabled providers.</returns>
    public IEnumerable<XtreamProvider> GetEnabledProviders() => Providers.Where(p => p.Enabled);

    /// <summary>
    /// Gets a provider by its ID.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>The provider, or null if not found.</returns>
    public XtreamProvider? GetProvider(string providerId) => Providers.Find(p => p.Id == providerId);
}
