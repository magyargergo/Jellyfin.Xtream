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
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Xtream.Api;

/// <summary>
/// Configuration dry-run preview endpoint with validation and impact assessment.
/// </summary>
[ApiController]
[Route("Xtream")]
[Produces("application/json")]
public class XtreamConfigPreviewController : ControllerBase
{
    /// <summary>
    /// Preview configuration changes without applying them.
    /// Returns a diff of what would change, validation errors, warnings, and impact assessment.
    /// </summary>
    /// <param name="section">Configuration section name (Proxy, Epg, Discord, Timeouts, Health, UserAgent, RateLimiting, Visibility, Failover, ConnectionLimits, Hedging, Buffer, Logging, StreamProcessing).</param>
    /// <param name="body">The configuration payload (same format as the corresponding PUT endpoint).</param>
    /// <returns>Preview of changes, validation results, and impact assessment.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("Configuration/{section}/Preview")]
    public ActionResult<ConfigPreviewResponse> PreviewConfigChange(
        string section,
        [FromBody] System.Text.Json.JsonElement body
    )
    {
        var config = Plugin.Instance.Configuration;
        var response = new ConfigPreviewResponse { Section = section };
        var activeStreams = Restream.GetActiveStreamSnapshots().Count;

        switch (section.ToLowerInvariant())
        {
            case "timeouts":
                PreviewTimeouts(body, config, response, activeStreams);
                break;
            case "health":
                PreviewHealth(body, config, response, activeStreams);
                break;
            case "buffer":
                PreviewBuffer(body, config, response, activeStreams);
                break;
            case "ratelimiting":
                PreviewRateLimiting(body, config, response);
                break;
            case "connectionlimits":
                PreviewConnectionLimits(body, config, response, activeStreams);
                break;
            case "hedging":
                PreviewHedging(body, config, response, activeStreams);
                break;
            case "proxy":
                PreviewProxy(body, config, response, activeStreams);
                break;
            case "discord":
                PreviewDiscord(body, config, response);
                break;
            case "epg":
                PreviewEpg(body, config, response);
                break;
            case "useragent":
                PreviewUserAgent(body, config, response);
                break;
            case "visibility":
                PreviewVisibility(body, config, response);
                break;
            case "failover":
                PreviewFailover(body, config, response, activeStreams);
                break;
            case "logging":
                PreviewLogging(body, config, response);
                break;
            case "streamprocessing":
                PreviewStreamProcessing(body, config, response, activeStreams);
                break;
            default:
                response.Errors.Add("Unknown configuration section: " + section);
                break;
        }

        response.IsValid = response.Errors.Count == 0;
        return Ok(response);
    }

    // =========================================================================
    // JSON Extraction Helpers
    // =========================================================================

    private static void AddChange(ConfigPreviewResponse r, string field, object? oldVal, object? newVal)
    {
        var oldStr = oldVal?.ToString() ?? string.Empty;
        var newStr = newVal?.ToString() ?? string.Empty;
        r.Changes.Add(
            new ConfigFieldChange
            {
                Field = field,
                OldValue = oldStr,
                NewValue = newStr,
                Changed = !string.Equals(oldStr, newStr, StringComparison.Ordinal),
            }
        );
    }

    private static int JsonInt(System.Text.Json.JsonElement body, string prop, int fallback)
    {
        return body.TryGetProperty(prop, out var v) && v.TryGetInt32(out var i) ? i : fallback;
    }

    private static double JsonDouble(System.Text.Json.JsonElement body, string prop, double fallback)
    {
        return body.TryGetProperty(prop, out var v) && v.TryGetDouble(out var d) ? d : fallback;
    }

    private static bool JsonBool(System.Text.Json.JsonElement body, string prop, bool fallback)
    {
        return
            body.TryGetProperty(prop, out var v)
            && v.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
            ? v.GetBoolean()
            : fallback;
    }

    private static string JsonString(System.Text.Json.JsonElement body, string prop, string fallback)
    {
        return body.TryGetProperty(prop, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
            ? v.GetString() ?? fallback
            : fallback;
    }

    // =========================================================================
    // Per-Section Preview Methods
    // =========================================================================

    private static void PreviewTimeouts(
        System.Text.Json.JsonElement body,
        Configuration.PluginConfiguration config,
        ConfigPreviewResponse r,
        int activeStreams
    )
    {
        var connect = Math.Clamp(JsonInt(body, "connectTimeoutSeconds", config.StreamConnectTimeoutSeconds), 1, 30);
        var firstByte = Math.Clamp(
            JsonInt(body, "firstByteTimeoutSeconds", config.StreamFirstByteTimeoutSeconds),
            1,
            30
        );
        var headers = Math.Clamp(
            JsonInt(body, "responseHeadersTimeoutSeconds", config.StreamResponseHeadersTimeoutSeconds),
            5,
            30
        );
        var stall = Math.Clamp(JsonInt(body, "dataStallTimeoutSeconds", config.StreamDataStallTimeoutSeconds), 5, 60);
        var budget = Math.Clamp(JsonInt(body, "failoverBudgetSeconds", config.FailoverBudgetSeconds), 5, 30);
        var blacklist = Math.Clamp(JsonInt(body, "providerBlacklistSeconds", config.ProviderBlacklistSeconds), 10, 300);
        var maxAttempts = Math.Clamp(JsonInt(body, "maxFailoverAttempts", config.MaxFailoverAttempts), 1, 10);
        var dns = Math.Clamp(JsonInt(body, "dnsTimeoutSeconds", config.DnsTimeoutSeconds), 1, 30);
        var keepalive = JsonBool(body, "tcpKeepaliveEnabled", config.TcpKeepaliveEnabled);

        AddChange(r, "ConnectTimeoutSeconds", config.StreamConnectTimeoutSeconds, connect);
        AddChange(r, "FirstByteTimeoutSeconds", config.StreamFirstByteTimeoutSeconds, firstByte);
        AddChange(r, "ResponseHeadersTimeoutSeconds", config.StreamResponseHeadersTimeoutSeconds, headers);
        AddChange(r, "DataStallTimeoutSeconds", config.StreamDataStallTimeoutSeconds, stall);
        AddChange(r, "FailoverBudgetSeconds", config.FailoverBudgetSeconds, budget);
        AddChange(r, "ProviderBlacklistSeconds", config.ProviderBlacklistSeconds, blacklist);
        AddChange(r, "MaxFailoverAttempts", config.MaxFailoverAttempts, maxAttempts);
        AddChange(r, "DnsTimeoutSeconds", config.DnsTimeoutSeconds, dns);
        AddChange(r, "TcpKeepaliveEnabled", config.TcpKeepaliveEnabled, keepalive);

        if (connect > firstByte)
        {
            r.Warnings.Add(
                "ConnectTimeout exceeds FirstByteTimeout -- connections may time out before data is expected"
            );
        }

        r.ImpactedStreams = activeStreams;
        r.RequiresRestart = true;
    }

    private static void PreviewHealth(
        System.Text.Json.JsonElement body,
        Configuration.PluginConfiguration config,
        ConfigPreviewResponse r,
        int activeStreams
    )
    {
        var p2c = JsonBool(body, "enableP2C", config.EnableP2CLoadBalancing);
        var outlier = JsonBool(body, "enableOutlierDetection", config.EnableOutlierDetection);
        var stddev = Math.Clamp(JsonDouble(body, "outlierStddevFactor", config.OutlierStddevFactor), 0.5, 5.0);
        var probation = Math.Clamp(JsonInt(body, "probationSuccessThreshold", config.ProbationSuccessThreshold), 1, 10);

        AddChange(r, "EnableP2C", config.EnableP2CLoadBalancing, p2c);
        AddChange(r, "EnableOutlierDetection", config.EnableOutlierDetection, outlier);
        AddChange(r, "OutlierStddevFactor", config.OutlierStddevFactor, stddev);
        AddChange(r, "ProbationSuccessThreshold", config.ProbationSuccessThreshold, probation);

        if (!p2c && outlier)
        {
            r.Warnings.Add("Outlier detection has limited effect without P2C load balancing");
        }

        r.ImpactedStreams = activeStreams;
        r.RequiresRestart = false;
    }

    private static void PreviewBuffer(
        System.Text.Json.JsonElement body,
        Configuration.PluginConfiguration config,
        ConfigPreviewResponse r,
        int activeStreams
    )
    {
        var underrun = Math.Clamp(
            JsonDouble(body, "underrunThresholdPercent", config.BufferUnderrunThresholdPercent),
            1,
            50
        );
        var nearFull = Math.Clamp(
            JsonDouble(body, "nearFullThresholdPercent", config.BufferNearFullThresholdPercent),
            50,
            99
        );
        var notifThreshold = Math.Clamp(
            JsonInt(body, "underrunNotificationThreshold", config.BufferUnderrunNotificationThreshold),
            1,
            50
        );
        var grace = Math.Clamp(
            JsonInt(body, "consumerDisconnectGraceSeconds", config.ConsumerDisconnectGraceSeconds),
            1,
            60
        );

        AddChange(r, "UnderrunThresholdPercent", config.BufferUnderrunThresholdPercent, underrun);
        AddChange(r, "NearFullThresholdPercent", config.BufferNearFullThresholdPercent, nearFull);
        AddChange(r, "UnderrunNotificationThreshold", config.BufferUnderrunNotificationThreshold, notifThreshold);
        AddChange(r, "ConsumerDisconnectGraceSeconds", config.ConsumerDisconnectGraceSeconds, grace);

        if (underrun >= nearFull)
        {
            r.Errors.Add("UnderrunThreshold must be less than NearFullThreshold");
        }

        r.ImpactedStreams = activeStreams;
        r.RequiresRestart = false;
    }

    private static void PreviewRateLimiting(
        System.Text.Json.JsonElement body,
        Configuration.PluginConfiguration config,
        ConfigPreviewResponse r
    )
    {
        var enabled = JsonBool(body, "enabled", config.EnableRateLimiting);
        var rps = Math.Clamp(JsonInt(body, "requestsPerSecond", config.RequestsPerSecond), 1, 50);
        var burst = Math.Clamp(JsonInt(body, "burstSize", config.BurstSize), 1, 100);

        AddChange(r, "Enabled", config.EnableRateLimiting, enabled);
        AddChange(r, "RequestsPerSecond", config.RequestsPerSecond, rps);
        AddChange(r, "BurstSize", config.BurstSize, burst);

        if (burst < rps)
        {
            r.Warnings.Add("BurstSize is less than RequestsPerSecond -- burst may be ineffective");
        }

        r.RequiresRestart = false;
    }

    private static void PreviewConnectionLimits(
        System.Text.Json.JsonElement body,
        Configuration.PluginConfiguration config,
        ConfigPreviewResponse r,
        int activeStreams
    )
    {
        var enforce = JsonBool(body, "enforceConnectionLimit", config.EnforceConnectionLimit);
        var maxStreams = Math.Max(0, JsonInt(body, "maxConcurrentStreams", config.MaxConcurrentStreams));
        var autoKill = JsonBool(body, "autoKillOldestStream", config.AutoKillOldestStream);
        var filter = JsonBool(body, "filterChannelsByCapacity", config.FilterChannelsByCapacity);

        AddChange(r, "EnforceConnectionLimit", config.EnforceConnectionLimit, enforce);
        AddChange(r, "MaxConcurrentStreams", config.MaxConcurrentStreams, maxStreams);
        AddChange(r, "AutoKillOldestStream", config.AutoKillOldestStream, autoKill);
        AddChange(r, "FilterChannelsByCapacity", config.FilterChannelsByCapacity, filter);

        if (enforce && maxStreams > 0 && activeStreams > maxStreams)
        {
            r.Warnings.Add($"Currently {activeStreams} active stream(s) exceed the proposed limit of {maxStreams}");
        }

        r.ImpactedStreams = enforce && maxStreams > 0 ? Math.Max(0, activeStreams - maxStreams) : 0;
        r.RequiresRestart = false;
    }

    private static void PreviewHedging(
        System.Text.Json.JsonElement body,
        Configuration.PluginConfiguration config,
        ConfigPreviewResponse r,
        int activeStreams
    )
    {
        var enabled = JsonBool(body, "enabled", config.EnableHedging);
        var delay = Math.Clamp(JsonInt(body, "delayMs", config.HedgingDelayMs), 50, 2000);
        var max = Math.Clamp(JsonInt(body, "maxAttempts", config.MaxHedgedAttempts), 1, 5);

        AddChange(r, "Enabled", config.EnableHedging, enabled);
        AddChange(r, "DelayMs", config.HedgingDelayMs, delay);
        AddChange(r, "MaxAttempts", config.MaxHedgedAttempts, max);

        if (enabled && delay < 100)
        {
            r.Warnings.Add("Very low hedging delay may cause excess bandwidth usage");
        }

        r.ImpactedStreams = activeStreams;
        r.RequiresRestart = true;
    }

    private static void PreviewProxy(
        System.Text.Json.JsonElement body,
        Configuration.PluginConfiguration config,
        ConfigPreviewResponse r,
        int activeStreams
    )
    {
        var enabled = JsonBool(body, "enabled", config.EnableProxy);
        var address = JsonString(body, "address", config.ProxyAddress);
        var port = Math.Clamp(JsonInt(body, "port", config.ProxyPort), 1, 65535);

        AddChange(r, "Enabled", config.EnableProxy, enabled);
        AddChange(r, "Address", config.ProxyAddress, address);
        AddChange(r, "Port", config.ProxyPort, port);

        if (enabled && string.IsNullOrWhiteSpace(address))
        {
            r.Errors.Add("Proxy address is required when proxy is enabled");
        }

        r.ImpactedStreams = activeStreams;
        r.RequiresRestart = true;
    }

    private static void PreviewDiscord(
        System.Text.Json.JsonElement body,
        Configuration.PluginConfiguration config,
        ConfigPreviewResponse r
    )
    {
        var enabled = JsonBool(body, "enabled", config.EnableDiscordNotifications);
        var url = JsonString(body, "webhookUrl", config.DiscordWebhookUrl);

        AddChange(r, "Enabled", config.EnableDiscordNotifications, enabled);
        AddChange(r, "WebhookUrl", config.DiscordWebhookUrl, url);

        if (enabled && string.IsNullOrWhiteSpace(url))
        {
            r.Errors.Add("Webhook URL is required when Discord notifications are enabled");
        }

        r.RequiresRestart = false;
    }

    private static void PreviewEpg(
        System.Text.Json.JsonElement body,
        Configuration.PluginConfiguration config,
        ConfigPreviewResponse r
    )
    {
        var enabled = JsonBool(body, "enableExternalEpg", config.EnableExternalEpg);
        var url = JsonString(body, "externalEpgUrl", config.ExternalEpgUrl);

        AddChange(r, "EnableExternalEpg", config.EnableExternalEpg, enabled);
        AddChange(r, "ExternalEpgUrl", config.ExternalEpgUrl, url);

        if (enabled && string.IsNullOrWhiteSpace(url))
        {
            r.Errors.Add("External EPG URL is required when external EPG is enabled");
        }

        r.RequiresRestart = false;
    }

    private static void PreviewUserAgent(
        System.Text.Json.JsonElement body,
        Configuration.PluginConfiguration config,
        ConfigPreviewResponse r
    )
    {
        var rotation = JsonBool(body, "enableRotation", config.EnableUserAgentRotation);

        AddChange(r, "EnableRotation", config.EnableUserAgentRotation, rotation);

        r.RequiresRestart = false;
    }

    private static void PreviewVisibility(
        System.Text.Json.JsonElement body,
        Configuration.PluginConfiguration config,
        ConfigPreviewResponse r
    )
    {
        var catchup = JsonBool(body, "isCatchupVisible", config.IsCatchupVisible);
        var series = JsonBool(body, "isSeriesVisible", config.IsSeriesVisible);
        var vod = JsonBool(body, "isVodVisible", config.IsVodVisible);

        AddChange(r, "IsCatchupVisible", config.IsCatchupVisible, catchup);
        AddChange(r, "IsSeriesVisible", config.IsSeriesVisible, series);
        AddChange(r, "IsVodVisible", config.IsVodVisible, vod);

        r.RequiresRestart = false;
    }

    private static void PreviewFailover(
        System.Text.Json.JsonElement body,
        Configuration.PluginConfiguration config,
        ConfigPreviewResponse r,
        int activeStreams
    )
    {
        var merge = JsonBool(body, "mergeDuplicateChannels", config.MergeDuplicateChannels);
        var failover = JsonBool(body, "enableProviderFailover", config.EnableProviderFailover);
        var skip = JsonBool(body, "skipUnavailableProviders", config.SkipUnavailableProviders);
        var interval = Math.Clamp(
            JsonInt(body, "providerCheckIntervalSeconds", config.ProviderCheckIntervalSeconds),
            15,
            300
        );

        AddChange(r, "MergeDuplicateChannels", config.MergeDuplicateChannels, merge);
        AddChange(r, "EnableProviderFailover", config.EnableProviderFailover, failover);
        AddChange(r, "SkipUnavailableProviders", config.SkipUnavailableProviders, skip);
        AddChange(r, "ProviderCheckIntervalSeconds", config.ProviderCheckIntervalSeconds, interval);

        if (!failover && skip)
        {
            r.Warnings.Add("SkipUnavailableProviders has no effect when failover is disabled");
        }

        r.ImpactedStreams = activeStreams;
        r.RequiresRestart = false;
    }

    private static void PreviewLogging(
        System.Text.Json.JsonElement body,
        Configuration.PluginConfiguration config,
        ConfigPreviewResponse r
    )
    {
        var debug = JsonBool(body, "enableDebugLogging", config.EnableDebugLogging);
        var maxEntries = Math.Clamp(JsonInt(body, "logViewerMaxEntries", config.LogViewerMaxEntries), 100, 50000);

        AddChange(r, "EnableDebugLogging", config.EnableDebugLogging, debug);
        AddChange(r, "LogViewerMaxEntries", config.LogViewerMaxEntries, maxEntries);

        if (debug)
        {
            r.Warnings.Add("Debug logging increases disk I/O and may impact performance");
        }

        r.RequiresRestart = false;
    }

    private static void PreviewStreamProcessing(
        System.Text.Json.JsonElement body,
        Configuration.PluginConfiguration config,
        ConfigPreviewResponse r,
        int activeStreams
    )
    {
        var remux = JsonBool(body, "forceRemux", config.ForceRemux);

        AddChange(r, "ForceRemux", config.ForceRemux, remux);

        r.ImpactedStreams = activeStreams;
        r.RequiresRestart = true;
    }
}
