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
using Jellyfin.Xtream.Api.Models;
using Jellyfin.Xtream.Service;
using Jellyfin.Xtream.Service.Logging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Api;

/// <summary>
/// Configuration section CRUD endpoints for all plugin settings.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="XtreamConfigurationController"/> class.
/// </remarks>
/// <param name="logger">The logger instance.</param>
[ApiController]
[Route("Xtream")]
[Produces("application/json")]
public class XtreamConfigurationController(ILogger<XtreamConfigurationController> logger) : ControllerBase
{
    private readonly ILogger<XtreamConfigurationController> _logger = logger;

    // =========================================================================
    // Proxy
    // =========================================================================

    /// <summary>
    /// Get proxy configuration.
    /// </summary>
    /// <returns>Current proxy settings.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Configuration/Proxy")]
    public ActionResult<object> GetProxyConfig()
    {
        var config = Plugin.Instance.Configuration;
        return Ok(
            new
            {
                enabled = config.EnableProxy,
                type = config.ProxyType.ToString(),
                address = config.ProxyAddress,
                port = config.ProxyPort,
                username = config.ProxyUsername,
                bypassLocal = config.ProxyBypassLocal,
            }
        );
    }

    /// <summary>
    /// Update proxy configuration.
    /// </summary>
    /// <param name="request">Proxy settings to apply.</param>
    /// <returns>Result indicating success.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPut("Configuration/Proxy")]
    public ActionResult<object> UpdateProxyConfig([FromBody] ProxyConfigRequest request)
    {
        var config = Plugin.Instance.Configuration;
        config.EnableProxy = request.Enabled;
        config.ProxyAddress = request.Address;
        config.ProxyPort = request.Port;
        config.ProxyUsername = request.Username;
        config.ProxyPassword = request.Password;
        config.ProxyBypassLocal = request.BypassLocal;

        if (Enum.TryParse<Configuration.ProxyType>(request.Type, ignoreCase: true, out var proxyType))
        {
            config.ProxyType = proxyType;
        }

        Plugin.Instance.SaveConfiguration();
        _logger.PluginLogInformation("Proxy configuration updated via API");
        XtreamControllerHelpers.PublishConfigChanged("Proxy");
        return Ok(new { success = true, message = "Proxy configuration updated" });
    }

    // =========================================================================
    // EPG
    // =========================================================================

    /// <summary>
    /// Get EPG configuration.
    /// </summary>
    /// <returns>Current EPG settings.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Configuration/Epg")]
    public ActionResult<object> GetEpgConfig()
    {
        var config = Plugin.Instance.Configuration;
        return Ok(
            new
            {
                enableExternalEpg = config.EnableExternalEpg,
                externalEpgUrl = config.ExternalEpgUrl,
                externalEpgLogoBaseUrl = config.ExternalEpgLogoBaseUrl,
                useExternalLogoFallback = config.UseExternalLogoFallback,
            }
        );
    }

    /// <summary>
    /// Update EPG configuration.
    /// </summary>
    /// <param name="request">EPG settings to apply.</param>
    /// <returns>Result indicating success.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPut("Configuration/Epg")]
    public ActionResult<object> UpdateEpgConfig([FromBody] EpgConfigRequest request)
    {
        var config = Plugin.Instance.Configuration;
        config.EnableExternalEpg = request.EnableExternalEpg;
        config.ExternalEpgUrl = request.ExternalEpgUrl;
        config.ExternalEpgLogoBaseUrl = request.ExternalEpgLogoBaseUrl;
        config.UseExternalLogoFallback = request.UseExternalLogoFallback;

        Plugin.Instance.SaveConfiguration();
        _logger.PluginLogInformation("EPG configuration updated via API");
        XtreamControllerHelpers.PublishConfigChanged("Epg");
        return Ok(new { success = true, message = "EPG configuration updated" });
    }

    // =========================================================================
    // Discord
    // =========================================================================

    /// <summary>
    /// Get Discord notification configuration.
    /// </summary>
    /// <returns>Current Discord settings.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Configuration/Discord")]
    public ActionResult<object> GetDiscordConfig()
    {
        var config = Plugin.Instance.Configuration;
        return Ok(
            new
            {
                enabled = config.EnableDiscordNotifications,
                webhookUrl = config.DiscordWebhookUrl,
                notifyOnBufferOverflow = config.NotifyOnBufferOverflow,
                notifyOnStreamStart = config.NotifyOnStreamStart,
                notifyOnStreamError = config.NotifyOnStreamError,
                notifyOnStreamKilled = config.NotifyOnStreamKilled,
                notifyOnStreamQualityViolation = config.NotifyOnStreamQualityViolation,
                notifyOnAVDrift = config.NotifyOnAVDrift,
                notifyOnAudioSyncCorrection = config.NotifyOnAudioSyncCorrection,
                notifyOnEpgRefresh = config.NotifyOnEpgRefresh,
                notifyOnConnectionLimitChange = config.NotifyOnConnectionLimitChange,
                notifyOnProviderBlacklist = config.NotifyOnProviderBlacklist,
            }
        );
    }

    /// <summary>
    /// Update Discord notification configuration.
    /// </summary>
    /// <param name="request">Discord settings to apply.</param>
    /// <returns>Result indicating success.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPut("Configuration/Discord")]
    public ActionResult<object> UpdateDiscordConfig([FromBody] DiscordConfigRequest request)
    {
        if (request.Enabled && !UrlValidator.IsValidDiscordWebhookUrl(request.WebhookUrl, out var urlError))
        {
            return BadRequest(XtreamControllerHelpers.CreateError(ErrorCodes.ValidationFailed, urlError));
        }

        var config = Plugin.Instance.Configuration;
        config.EnableDiscordNotifications = request.Enabled;
        config.DiscordWebhookUrl = request.WebhookUrl;
        config.NotifyOnBufferOverflow = request.NotifyOnBufferOverflow;
        config.NotifyOnStreamStart = request.NotifyOnStreamStart;
        config.NotifyOnStreamError = request.NotifyOnStreamError;
        config.NotifyOnStreamKilled = request.NotifyOnStreamKilled;
        config.NotifyOnStreamQualityViolation = request.NotifyOnStreamQualityViolation;
        config.NotifyOnAVDrift = request.NotifyOnAVDrift;
        config.NotifyOnAudioSyncCorrection = request.NotifyOnAudioSyncCorrection;
        config.NotifyOnEpgRefresh = request.NotifyOnEpgRefresh;
        config.NotifyOnConnectionLimitChange = request.NotifyOnConnectionLimitChange;
        config.NotifyOnProviderBlacklist = request.NotifyOnProviderBlacklist;

        Plugin.Instance.SaveConfiguration();
        _logger.PluginLogInformation("Discord configuration updated via API");
        XtreamControllerHelpers.PublishConfigChanged("Discord");
        return Ok(new { success = true, message = "Discord configuration updated" });
    }

    // =========================================================================
    // Timeouts
    // =========================================================================

    /// <summary>
    /// Get streaming timeout configuration.
    /// </summary>
    /// <returns>Current timeout settings.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Configuration/Timeouts")]
    public ActionResult<object> GetTimeoutConfig()
    {
        var config = Plugin.Instance.Configuration;
        return Ok(
            new
            {
                connectTimeoutSeconds = config.StreamConnectTimeoutSeconds,
                firstByteTimeoutSeconds = config.StreamFirstByteTimeoutSeconds,
                responseHeadersTimeoutSeconds = config.StreamResponseHeadersTimeoutSeconds,
                dataStallTimeoutSeconds = config.StreamDataStallTimeoutSeconds,
                failoverBudgetSeconds = config.FailoverBudgetSeconds,
                providerBlacklistSeconds = config.ProviderBlacklistSeconds,
                maxFailoverAttempts = config.MaxFailoverAttempts,
            }
        );
    }

    /// <summary>
    /// Update streaming timeout configuration.
    /// </summary>
    /// <param name="request">Timeout settings to apply.</param>
    /// <returns>Result indicating success.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPut("Configuration/Timeouts")]
    public ActionResult<object> UpdateTimeoutConfig([FromBody] TimeoutConfigRequest request)
    {
        var config = Plugin.Instance.Configuration;
        config.StreamConnectTimeoutSeconds = Math.Clamp(request.ConnectTimeoutSeconds, 1, 30);
        config.StreamFirstByteTimeoutSeconds = Math.Clamp(request.FirstByteTimeoutSeconds, 1, 30);
        config.StreamResponseHeadersTimeoutSeconds = Math.Clamp(request.ResponseHeadersTimeoutSeconds, 5, 30);
        config.StreamDataStallTimeoutSeconds = Math.Clamp(request.DataStallTimeoutSeconds, 5, 60);
        config.FailoverBudgetSeconds = Math.Clamp(request.FailoverBudgetSeconds, 5, 30);
        config.ProviderBlacklistSeconds = Math.Clamp(request.ProviderBlacklistSeconds, 10, 300);
        config.MaxFailoverAttempts = Math.Clamp(request.MaxFailoverAttempts, 1, 10);
        config.DnsTimeoutSeconds = Math.Clamp(request.DnsTimeoutSeconds, 1, 30);
        config.TcpKeepaliveEnabled = request.TcpKeepaliveEnabled;

        Plugin.Instance.SaveConfiguration();
        _logger.PluginLogInformation("Timeout configuration updated via API");
        XtreamControllerHelpers.PublishConfigChanged("Timeouts");
        return Ok(new { success = true, message = "Timeout configuration updated" });
    }

    // =========================================================================
    // Health
    // =========================================================================

    /// <summary>
    /// Get health and load balancer configuration.
    /// </summary>
    /// <returns>The current health configuration.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Configuration/Health")]
    public ActionResult<object> GetHealthConfig()
    {
        var config = Plugin.Instance.Configuration;
        return Ok(
            new
            {
                enableP2C = config.EnableP2CLoadBalancing,
                enableOutlierDetection = config.EnableOutlierDetection,
                outlierStddevFactor = config.OutlierStddevFactor,
                probationSuccessThreshold = config.ProbationSuccessThreshold,
            }
        );
    }

    /// <summary>
    /// Update health and load balancer configuration.
    /// </summary>
    /// <param name="request">Health settings to apply.</param>
    /// <returns>Result indicating success.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPut("Configuration/Health")]
    public ActionResult<object> UpdateHealthConfig([FromBody] HealthConfigRequest request)
    {
        var config = Plugin.Instance.Configuration;
        config.EnableP2CLoadBalancing = request.EnableP2C;
        config.EnableOutlierDetection = request.EnableOutlierDetection;
        config.OutlierStddevFactor = Math.Clamp(request.OutlierStddevFactor, 0.5, 5.0);
        config.ProbationSuccessThreshold = Math.Clamp(request.ProbationSuccessThreshold, 1, 10);

        Plugin.Instance.SaveConfiguration();
        _logger.PluginLogInformation("Health configuration updated via API");
        XtreamControllerHelpers.PublishConfigChanged("Health");
        return Ok(new { success = true, message = "Health configuration updated" });
    }

    // =========================================================================
    // User-Agent
    // =========================================================================

    /// <summary>
    /// Get User-Agent configuration.
    /// </summary>
    /// <returns>Current User-Agent settings.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Configuration/UserAgent")]
    public ActionResult<object> GetUserAgentConfig()
    {
        var config = Plugin.Instance.Configuration;
        return Ok(
            new
            {
                customUserAgent = config.CustomUserAgent,
                enableRotation = config.EnableUserAgentRotation,
                useRandom = config.UseRandomUserAgent,
            }
        );
    }

    /// <summary>
    /// Update User-Agent configuration.
    /// </summary>
    /// <param name="request">User-Agent settings to apply.</param>
    /// <returns>Result indicating success.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPut("Configuration/UserAgent")]
    public ActionResult<object> UpdateUserAgentConfig([FromBody] UserAgentConfigRequest request)
    {
        var config = Plugin.Instance.Configuration;
        config.CustomUserAgent = request.CustomUserAgent;
        config.EnableUserAgentRotation = request.EnableRotation;
        config.UseRandomUserAgent = request.UseRandom;

        Plugin.Instance.SaveConfiguration();
        _logger.PluginLogInformation("User-Agent configuration updated via API");
        XtreamControllerHelpers.PublishConfigChanged("UserAgent");
        return Ok(new { success = true, message = "User-Agent configuration updated" });
    }

    // =========================================================================
    // Rate Limiting
    // =========================================================================

    /// <summary>
    /// Get rate limiting configuration.
    /// </summary>
    /// <returns>Current rate limiting settings.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Configuration/RateLimiting")]
    public ActionResult<object> GetRateLimitConfig()
    {
        var config = Plugin.Instance.Configuration;
        return Ok(
            new
            {
                enabled = config.EnableRateLimiting,
                requestsPerSecond = config.RequestsPerSecond,
                burstSize = config.BurstSize,
            }
        );
    }

    /// <summary>
    /// Update rate limiting configuration.
    /// </summary>
    /// <param name="request">Rate limiting settings to apply.</param>
    /// <returns>Result indicating success.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPut("Configuration/RateLimiting")]
    public ActionResult<object> UpdateRateLimitConfig([FromBody] RateLimitConfigRequest request)
    {
        var config = Plugin.Instance.Configuration;
        config.EnableRateLimiting = request.Enabled;
        config.RequestsPerSecond = Math.Clamp(request.RequestsPerSecond, 1, 50);
        config.BurstSize = Math.Clamp(request.BurstSize, 1, 100);

        Plugin.Instance.SaveConfiguration();
        _logger.PluginLogInformation("Rate limiting configuration updated via API");
        XtreamControllerHelpers.PublishConfigChanged("RateLimiting");
        return Ok(new { success = true, message = "Rate limiting configuration updated" });
    }

    // =========================================================================
    // Visibility
    // =========================================================================

    /// <summary>
    /// Get content visibility configuration.
    /// </summary>
    /// <returns>Current visibility settings.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Configuration/Visibility")]
    public ActionResult<object> GetVisibilityConfig()
    {
        var config = Plugin.Instance.Configuration;
        return Ok(
            new
            {
                isCatchupVisible = config.IsCatchupVisible,
                isSeriesVisible = config.IsSeriesVisible,
                isVodVisible = config.IsVodVisible,
                isTmdbVodOverride = config.IsTmdbVodOverride,
            }
        );
    }

    /// <summary>
    /// Update content visibility configuration.
    /// </summary>
    /// <param name="request">Visibility settings to apply.</param>
    /// <returns>Result indicating success.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPut("Configuration/Visibility")]
    public ActionResult<object> UpdateVisibilityConfig([FromBody] VisibilityConfigRequest request)
    {
        var config = Plugin.Instance.Configuration;
        config.IsCatchupVisible = request.IsCatchupVisible;
        config.IsSeriesVisible = request.IsSeriesVisible;
        config.IsVodVisible = request.IsVodVisible;
        config.IsTmdbVodOverride = request.IsTmdbVodOverride;

        Plugin.Instance.SaveConfiguration();
        _logger.PluginLogInformation("Visibility configuration updated via API");
        XtreamControllerHelpers.PublishConfigChanged("Visibility");
        return Ok(new { success = true, message = "Visibility configuration updated" });
    }

    // =========================================================================
    // Failover
    // =========================================================================

    /// <summary>
    /// Get failover configuration.
    /// </summary>
    /// <returns>Current failover settings.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Configuration/Failover")]
    public ActionResult<object> GetFailoverConfig()
    {
        var config = Plugin.Instance.Configuration;
        return Ok(
            new
            {
                mergeDuplicateChannels = config.MergeDuplicateChannels,
                enableProviderFailover = config.EnableProviderFailover,
                skipUnavailableProviders = config.SkipUnavailableProviders,
                providerCheckIntervalSeconds = config.ProviderCheckIntervalSeconds,
            }
        );
    }

    /// <summary>
    /// Update failover configuration.
    /// </summary>
    /// <param name="request">Failover settings to apply.</param>
    /// <returns>Result indicating success.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPut("Configuration/Failover")]
    public ActionResult<object> UpdateFailoverConfig([FromBody] FailoverConfigRequest request)
    {
        var config = Plugin.Instance.Configuration;
        config.MergeDuplicateChannels = request.MergeDuplicateChannels;
        config.EnableProviderFailover = request.EnableProviderFailover;
        config.SkipUnavailableProviders = request.SkipUnavailableProviders;
        config.ProviderCheckIntervalSeconds = Math.Clamp(request.ProviderCheckIntervalSeconds, 15, 300);

        Plugin.Instance.SaveConfiguration();
        _logger.PluginLogInformation("Failover configuration updated via API");
        XtreamControllerHelpers.PublishConfigChanged("Failover");
        return Ok(new { success = true, message = "Failover configuration updated" });
    }

    // =========================================================================
    // Connection Limits
    // =========================================================================

    /// <summary>
    /// Get connection limit configuration.
    /// </summary>
    /// <returns>Current connection limit settings.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Configuration/ConnectionLimits")]
    public ActionResult<object> GetConnectionLimitConfig()
    {
        var config = Plugin.Instance.Configuration;
        return Ok(
            new
            {
                enforceConnectionLimit = config.EnforceConnectionLimit,
                maxConcurrentStreams = config.MaxConcurrentStreams,
                autoKillOldestStream = config.AutoKillOldestStream,
                filterChannelsByCapacity = config.FilterChannelsByCapacity,
            }
        );
    }

    /// <summary>
    /// Update connection limit configuration.
    /// </summary>
    /// <param name="request">Connection limit settings to apply.</param>
    /// <returns>Result indicating success.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPut("Configuration/ConnectionLimits")]
    public ActionResult<object> UpdateConnectionLimitConfig([FromBody] ConnectionLimitConfigRequest request)
    {
        var config = Plugin.Instance.Configuration;
        config.EnforceConnectionLimit = request.EnforceConnectionLimit;
        config.MaxConcurrentStreams = Math.Clamp(request.MaxConcurrentStreams, 0, 100);
        config.AutoKillOldestStream = request.AutoKillOldestStream;
        config.FilterChannelsByCapacity = request.FilterChannelsByCapacity;

        Plugin.Instance.SaveConfiguration();
        _logger.PluginLogInformation("Connection limit configuration updated via API");
        XtreamControllerHelpers.PublishConfigChanged("ConnectionLimits");
        return Ok(new { success = true, message = "Connection limit configuration updated" });
    }

    // =========================================================================
    // Hedging
    // =========================================================================

    /// <summary>
    /// Get hedging configuration.
    /// </summary>
    /// <returns>Current hedging settings.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Configuration/Hedging")]
    public ActionResult<object> GetHedgingConfig()
    {
        var config = Plugin.Instance.Configuration;
        return Ok(
            new
            {
                enabled = config.EnableHedging,
                delayMs = config.HedgingDelayMs,
                maxAttempts = config.MaxHedgedAttempts,
            }
        );
    }

    /// <summary>
    /// Update hedging configuration.
    /// </summary>
    /// <param name="request">Hedging settings to apply.</param>
    /// <returns>Result indicating success.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPut("Configuration/Hedging")]
    public ActionResult<object> UpdateHedgingConfig([FromBody] HedgingConfigRequest request)
    {
        var config = Plugin.Instance.Configuration;
        config.EnableHedging = request.Enabled;
        config.HedgingDelayMs = Math.Clamp(request.DelayMs, 50, 2000);
        config.MaxHedgedAttempts = Math.Clamp(request.MaxAttempts, 1, 5);

        Plugin.Instance.SaveConfiguration();
        _logger.PluginLogInformation("Hedging configuration updated via API");
        XtreamControllerHelpers.PublishConfigChanged("Hedging");
        return Ok(new { success = true, message = "Hedging configuration updated" });
    }

    // =========================================================================
    // Buffer
    // =========================================================================

    /// <summary>
    /// Get buffer health configuration.
    /// </summary>
    /// <returns>Current buffer health settings.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Configuration/Buffer")]
    public ActionResult<object> GetBufferConfig()
    {
        var config = Plugin.Instance.Configuration;
        return Ok(
            new
            {
                underrunThresholdPercent = config.BufferUnderrunThresholdPercent,
                nearFullThresholdPercent = config.BufferNearFullThresholdPercent,
                underrunNotificationThreshold = config.BufferUnderrunNotificationThreshold,
                consumerDisconnectGraceSeconds = config.ConsumerDisconnectGraceSeconds,
            }
        );
    }

    /// <summary>
    /// Update buffer health configuration.
    /// </summary>
    /// <param name="request">Buffer settings to apply.</param>
    /// <returns>Result indicating success.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPut("Configuration/Buffer")]
    public ActionResult<object> UpdateBufferConfig([FromBody] BufferConfigRequest request)
    {
        var config = Plugin.Instance.Configuration;
        config.BufferUnderrunThresholdPercent = Math.Clamp(request.UnderrunThresholdPercent, 1.0, 50.0);
        config.BufferNearFullThresholdPercent = Math.Clamp(request.NearFullThresholdPercent, 50.0, 99.0);
        config.BufferUnderrunNotificationThreshold = Math.Clamp(request.UnderrunNotificationThreshold, 1, 50);
        config.ConsumerDisconnectGraceSeconds = Math.Clamp(request.ConsumerDisconnectGraceSeconds, 1, 60);

        Plugin.Instance.SaveConfiguration();
        _logger.PluginLogInformation("Buffer configuration updated via API");
        XtreamControllerHelpers.PublishConfigChanged("Buffer");
        return Ok(new { success = true, message = "Buffer configuration updated" });
    }

    // =========================================================================
    // Logging
    // =========================================================================

    /// <summary>
    /// Get logging configuration.
    /// </summary>
    /// <returns>Current logging settings.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Configuration/Logging")]
    public ActionResult<object> GetLoggingConfig()
    {
        var config = Plugin.Instance.Configuration;
        return Ok(
            new
            {
                enableDebugLogging = config.EnableDebugLogging,
                logViewerMaxEntries = config.LogViewerMaxEntries,
                enablePeriodicHealthReports = config.EnablePeriodicHealthReports,
                healthReportIntervalMinutes = config.HealthReportIntervalMinutes,
            }
        );
    }

    /// <summary>
    /// Update logging configuration.
    /// </summary>
    /// <param name="request">Logging settings to apply.</param>
    /// <returns>Result indicating success.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPut("Configuration/Logging")]
    public ActionResult<object> UpdateLoggingConfig([FromBody] LoggingConfigRequest request)
    {
        var config = Plugin.Instance.Configuration;
        config.EnableDebugLogging = request.EnableDebugLogging;
        config.LogViewerMaxEntries = Math.Clamp(request.LogViewerMaxEntries, 100, 50000);
        config.EnablePeriodicHealthReports = request.EnablePeriodicHealthReports;
        config.HealthReportIntervalMinutes = Math.Clamp(request.HealthReportIntervalMinutes, 5, 1440);

        Plugin.Instance.SaveConfiguration();
        _logger.PluginLogInformation("Logging configuration updated via API");
        XtreamControllerHelpers.PublishConfigChanged("Logging");
        return Ok(new { success = true, message = "Logging configuration updated" });
    }

    // =========================================================================
    // Stream Processing
    // =========================================================================

    /// <summary>
    /// Get stream processing configuration.
    /// </summary>
    /// <returns>Current stream processing settings.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Configuration/StreamProcessing")]
    public ActionResult<object> GetStreamProcessingConfig()
    {
        var config = Plugin.Instance.Configuration;
        return Ok(new { forceRemux = config.ForceRemux });
    }

    /// <summary>
    /// Update stream processing configuration.
    /// </summary>
    /// <param name="request">Stream processing settings to apply.</param>
    /// <returns>Result indicating success.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPut("Configuration/StreamProcessing")]
    public ActionResult<object> UpdateStreamProcessingConfig([FromBody] StreamProcessingConfigRequest request)
    {
        var config = Plugin.Instance.Configuration;
        config.ForceRemux = request.ForceRemux;

        Plugin.Instance.SaveConfiguration();
        _logger.PluginLogInformation("Stream processing configuration updated via API");
        XtreamControllerHelpers.PublishConfigChanged("StreamProcessing");
        return Ok(new { success = true, message = "Stream processing configuration updated" });
    }
}
