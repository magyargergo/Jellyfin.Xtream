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
using Jellyfin.Xtream.Configuration;

namespace Jellyfin.Xtream.Service.ProviderManagement;

/// <summary>
/// Centralized timeout policy for IPTV streaming operations.
/// Optimized for MPEG-TS persistent TCP streams with reference to CDN failover standards.
/// </summary>
/// <remarks>
/// <para>
/// MPEG-TS streams use persistent TCP connections with continuous 188-byte packet delivery,
/// unlike HLS which uses HTTP segment requests. This affects timeout considerations:
/// </para>
/// <list type="bullet">
/// <item><description>Connection timeout: 1-5s (standard TCP establishment)</description></item>
/// <item><description>First byte timeout: 2-5s (initial data after connection)</description></item>
/// <item><description>Data stall timeout: 5-10s (MPEG-TS expects continuous flow; shorter than HLS segment timeout)</description></item>
/// <item><description>Failover budget: 5-10s (total time before error to user)</description></item>
/// <item><description>Blacklist duration: 30-60s (quick provider recovery)</description></item>
/// </list>
/// <para>
/// References: DVB-IPTV standards, ETSI TS 102 034, CDN failover best practices.
/// </para>
/// </remarks>
public static class StreamingTimeoutPolicy
{
    // ========== Connection Phase ==========
    // Fast-fail to enable quick failover to backup providers

    /// <summary>
    /// Default TCP connection timeout in milliseconds.
    /// Industry standard: 1-5 seconds for CDN failover scenarios.
    /// </summary>
    public const int DefaultConnectTimeoutMs = 5000;

    /// <summary>
    /// Default timeout for receiving HTTP response headers after TCP connection.
    /// Detects "zombie backend" scenarios where TCP connects but HTTP never responds.
    /// This is distinct from ConnectTimeout (TCP) and FirstByteTimeout (content).
    /// </summary>
    /// <remarks>
    /// <para>
    /// IPTV providers often use load balancers that route to backend servers.
    /// When a backend is dead but TCP still accepts connections (common with nginx/haproxy),
    /// the HTTP request is sent but no response headers are ever received.
    /// </para>
    /// <para>
    /// Network analysis of provider 161.123.116.21 showed:
    /// - TCP connects in &lt;100ms ✓
    /// - HTTP request sent successfully ✓
    /// - No HTTP response headers received (hangs indefinitely) ✗
    /// </para>
    /// <para>
    /// 10 seconds allows for slow but responsive backends while catching dead ones.
    /// </para>
    /// </remarks>
    public const int DefaultResponseHeadersTimeoutMs = 10000;

    /// <summary>
    /// Default timeout for receiving first byte of data after connection.
    /// Detects "connected but no data" scenarios common with overloaded providers.
    /// </summary>
    public const int DefaultFirstByteTimeoutMs = 5000;

    // ========== Streaming Phase ==========
    // Detect stalls without false positives during normal playback

    /// <summary>
    /// Default timeout for detecting data stalls during MPEG-TS streaming.
    /// For persistent TCP streams, 20s accommodates bursty IPTV providers that deliver data
    /// in irregular intervals. Many providers pause for 5-15s between data bursts during
    /// lower-bitrate segments or congestion. Using 10s caused excessive false-positive
    /// reconnections and hot-swaps. 20s balances responsiveness with burst tolerance.
    /// </summary>
    public const int DefaultDataStallTimeoutMs = 20000;

    /// <summary>
    /// Minimum delay between reconnection attempts within same provider.
    /// Allows provider-side cleanup before retry.
    /// </summary>
    public const int ReconnectDelayMs = 500;

    // ========== Failover Phase ==========
    // Responsive provider switching for seamless playback

    /// <summary>
    /// Base delay between failover attempts to different providers.
    /// Fast exponential: 200ms, 400ms, 800ms, 1000ms (capped).
    /// </summary>
    public const int FailoverDelayBaseMs = 200;

    /// <summary>
    /// Maximum delay between failover attempts.
    /// </summary>
    public const int FailoverDelayMaxMs = 1000;

    /// <summary>
    /// Default maximum number of providers to try during failover.
    /// </summary>
    public const int DefaultMaxFailoverAttempts = 4;

    /// <summary>
    /// Default total time budget for all failover attempts.
    /// After this, return error to user rather than waiting indefinitely.
    /// UX research suggests 5-10s acceptable for live TV scenarios.
    /// </summary>
    public const int DefaultFailoverBudgetMs = 15000;

    /// <summary>
    /// Minimum per-attempt timeout to ensure meaningful connection attempts.
    /// IPTV providers often have slow initial response times (3-5s is common).
    /// Below this threshold, TCP establishment may not complete reliably.
    /// </summary>
    public const int MinPerAttemptTimeoutMs = 5000;

    // ========== Health Recovery Phase ==========
    // Quick recovery for user-initiated actions

    /// <summary>
    /// Default blacklist duration for providers after consecutive failures.
    /// Reduced from 2 minutes to 30 seconds for faster recovery.
    /// </summary>
    public const int DefaultBlacklistDurationMs = 30000;

    /// <summary>
    /// Extended blacklist duration for severe failures (connection limit, auth errors).
    /// </summary>
    public const int ExtendedBlacklistDurationMs = 60000;

    /// <summary>
    /// Quick blacklist duration for transient errors (timeout, rate limit).
    /// </summary>
    public const int QuickBlacklistDurationMs = 10000;

    /// <summary>
    /// Number of consecutive failures before blacklisting a provider.
    /// Reduced from 3 to 2 for faster failover per Polly best practices.
    /// At 20s stall detection, 2 failures = 40s before switch vs 60s with 3.
    /// </summary>
    public const int BlacklistThreshold = 2;

    // ========== Configuration Accessors ==========
    // Get timeout values from plugin configuration with fallback to defaults

    /// <summary>
    /// Gets the connection timeout from configuration or default.
    /// </summary>
    /// <param name="config">Plugin configuration (optional).</param>
    /// <returns>Connection timeout in milliseconds.</returns>
    public static int GetConnectTimeoutMs(PluginConfiguration? config = null)
    {
        config ??= GetConfig();
        var seconds = config?.StreamConnectTimeoutSeconds ?? 0;
        return seconds > 0 ? seconds * 1000 : DefaultConnectTimeoutMs;
    }

    /// <summary>
    /// Gets the first byte timeout from configuration or default.
    /// </summary>
    /// <param name="config">Plugin configuration (optional).</param>
    /// <returns>First byte timeout in milliseconds.</returns>
    public static int GetFirstByteTimeoutMs(PluginConfiguration? config = null)
    {
        config ??= GetConfig();
        var seconds = config?.StreamFirstByteTimeoutSeconds ?? 0;
        return seconds > 0 ? seconds * 1000 : DefaultFirstByteTimeoutMs;
    }

    /// <summary>
    /// Gets the response headers timeout from configuration or default.
    /// This is the time to wait for HTTP response headers after TCP connection.
    /// </summary>
    /// <param name="config">Plugin configuration (optional).</param>
    /// <returns>Response headers timeout in milliseconds.</returns>
    public static int GetResponseHeadersTimeoutMs(PluginConfiguration? config = null)
    {
        config ??= GetConfig();
        var seconds = config?.StreamResponseHeadersTimeoutSeconds ?? 0;
        return seconds > 0 ? seconds * 1000 : DefaultResponseHeadersTimeoutMs;
    }

    /// <summary>
    /// Gets the data stall timeout from configuration or default.
    /// </summary>
    /// <param name="config">Plugin configuration (optional).</param>
    /// <returns>Data stall timeout in milliseconds.</returns>
    public static int GetDataStallTimeoutMs(PluginConfiguration? config = null)
    {
        config ??= GetConfig();
        var seconds = config?.StreamDataStallTimeoutSeconds ?? 0;
        return seconds > 0 ? seconds * 1000 : DefaultDataStallTimeoutMs;
    }

    /// <summary>
    /// Gets the failover budget from configuration or default.
    /// </summary>
    /// <param name="config">Plugin configuration (optional).</param>
    /// <returns>Failover budget in milliseconds.</returns>
    public static int GetFailoverBudgetMs(PluginConfiguration? config = null)
    {
        config ??= GetConfig();
        var seconds = config?.FailoverBudgetSeconds ?? 0;
        return seconds > 0 ? seconds * 1000 : DefaultFailoverBudgetMs;
    }

    /// <summary>
    /// Gets the blacklist duration from configuration or default.
    /// </summary>
    /// <param name="config">Plugin configuration (optional).</param>
    /// <returns>Blacklist duration as TimeSpan.</returns>
    public static TimeSpan GetBlacklistDuration(PluginConfiguration? config = null)
    {
        config ??= GetConfig();
        var seconds = config?.ProviderBlacklistSeconds ?? 0;
        return seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromMilliseconds(DefaultBlacklistDurationMs);
    }

    /// <summary>
    /// Gets the extended blacklist duration (for severe errors).
    /// </summary>
    /// <returns>Extended blacklist duration as TimeSpan.</returns>
    public static TimeSpan GetExtendedBlacklistDuration() => TimeSpan.FromMilliseconds(ExtendedBlacklistDurationMs);

    /// <summary>
    /// Gets the quick blacklist duration (for transient errors).
    /// </summary>
    /// <returns>Quick blacklist duration as TimeSpan.</returns>
    public static TimeSpan GetQuickBlacklistDuration() => TimeSpan.FromMilliseconds(QuickBlacklistDurationMs);

    /// <summary>
    /// Gets the max failover attempts from configuration or default.
    /// </summary>
    /// <param name="config">Plugin configuration (optional).</param>
    /// <returns>Maximum failover attempts.</returns>
    public static int GetMaxFailoverAttempts(PluginConfiguration? config = null)
    {
        config ??= GetConfig();
        return config?.MaxFailoverAttempts > 0 ? config.MaxFailoverAttempts : DefaultMaxFailoverAttempts;
    }

    /// <summary>
    /// Calculates the failover backoff delay using fast exponential backoff.
    /// Formula: base * 2^(attempt-2), capped at max.
    /// Results: 200ms, 400ms, 800ms, 1000ms...
    /// </summary>
    /// <param name="attemptNumber">Current attempt number (1-based).</param>
    /// <returns>Delay in milliseconds.</returns>
    public static int CalculateFailoverBackoff(int attemptNumber)
    {
        if (attemptNumber <= 1)
        {
            return 0; // No delay for first attempt
        }

        var delay = FailoverDelayBaseMs * (1 << (attemptNumber - 2));
        return Math.Min(delay, FailoverDelayMaxMs);
    }

    /// <summary>
    /// Gets the combined timeout for opening a stream (connect + first byte).
    /// </summary>
    /// <param name="config">Plugin configuration (optional).</param>
    /// <returns>Combined timeout in milliseconds.</returns>
    public static int GetStreamOpenTimeoutMs(PluginConfiguration? config = null) =>
        GetConnectTimeoutMs(config) + GetFirstByteTimeoutMs(config);

    /// <summary>
    /// Calculates the per-attempt timeout to allow multiple providers within budget.
    /// Formula: min(remainingBudget, max(budget/maxAttempts, minTimeout)).
    /// </summary>
    /// <param name="remainingBudgetMs">Remaining failover budget in milliseconds.</param>
    /// <param name="attemptsRemaining">Number of attempts still possible.</param>
    /// <param name="config">Plugin configuration (optional).</param>
    /// <returns>Timeout for this attempt in milliseconds.</returns>
    public static int CalculatePerAttemptTimeout(
        int remainingBudgetMs,
        int attemptsRemaining,
        PluginConfiguration? config = null
    )
    {
        if (remainingBudgetMs <= 0)
        {
            return 0;
        }

        // Calculate fair share of remaining budget
        var fairShareMs = attemptsRemaining > 0 ? remainingBudgetMs / attemptsRemaining : remainingBudgetMs;

        // Ensure minimum viable timeout for TCP establishment
        var effectiveTimeout = Math.Max(fairShareMs, MinPerAttemptTimeoutMs);

        // Cap at remaining budget (can't exceed what's left)
        effectiveTimeout = Math.Min(effectiveTimeout, remainingBudgetMs);

        // Also cap at stream open timeout (no need to wait longer than connection time)
        var streamOpenTimeout = GetStreamOpenTimeoutMs(config);
        return Math.Min(effectiveTimeout, streamOpenTimeout);
    }

    private static PluginConfiguration? GetConfig()
    {
        try
        {
            return Plugin.Instance?.Configuration;
        }
        catch
        {
            return null;
        }
    }
}
