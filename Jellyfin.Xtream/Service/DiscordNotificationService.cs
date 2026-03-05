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
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.Webhook;
using Jellyfin.Xtream.Service.Discord;
using Jellyfin.Xtream.Utility;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Service for sending Discord webhook notifications about stream health using Discord.Net.
/// Implements IDiscordNotificationService for dependency injection and testability.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="DiscordNotificationService"/> class.
/// </remarks>
/// <param name="logger">The logger.</param>
public sealed class DiscordNotificationService(ILogger<DiscordNotificationService> logger)
    : IDiscordNotificationService,
        IDisposable
{
    private const int MinNotificationIntervalSeconds = 10; // Prevent spam
    private const int AVDriftCooldownMinutes = 5; // Per-channel cooldown for A/V drift notifications
    private const string JellyfinIconUrl =
        "https://raw.githubusercontent.com/jellyfin/jellyfin-ux/master/branding/SVG/icon-transparent.svg";

    private readonly ILogger<DiscordNotificationService> _logger = logger;
    private readonly SemaphoreSlim _rateLimiter = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTime> _lastAVDriftNotificationPerChannel = new(
        StringComparer.Ordinal
    );
    private DateTime _lastNotification = DateTime.MinValue;
    private bool _disposed;

    /// <summary>
    /// Sends a buffer overflow notification to Discord.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="channelName">The channel name.</param>
    /// <param name="overflowCount">Number of overflow events.</param>
    /// <param name="lostMB">Megabytes of data lost.</param>
    /// <param name="totalLostMB">Total megabytes lost.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task NotifyBufferOverflowAsync(
        string streamId,
        string channelName,
        int overflowCount,
        double lostMB,
        double totalLostMB,
        CancellationToken cancellationToken = default
    )
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.EnableDiscordNotifications != true || !config.NotifyOnBufferOverflow)
        {
            return;
        }

        if (!await ShouldSendNotificationAsync().ConfigureAwait(false))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var embed = new EmbedBuilder()
            .WithAuthor("Jellyfin.Xtream", JellyfinIconUrl)
            .WithTitle("Buffer Overflow")
            .WithDescription($"**⚠️ WARNING** - Stream buffer overflow detected\n**Channel:** `{streamId}`")
            .WithColor(Color.Orange)
            .AddField("🕐 Time", $"<t:{new DateTimeOffset(now).ToUnixTimeSeconds()}:R>", inline: true)
            .AddField("# Channel", channelName, inline: true)
            .AddField("⚠️ Event", $"Overflow #{overflowCount}", inline: true)
            .AddField("💾 Data Lost", $"{lostMB:F2} MB", inline: true)
            .AddField("📉 Total Lost", $"{totalLostMB:F2} MB", inline: true)
            .WithTimestamp(now)
            .Build();

        _ = await SendDiscordMessageAsync(embed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a stream start notification to Discord.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="channelName">The channel name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task NotifyStreamStartAsync(
        string streamId,
        string channelName,
        CancellationToken cancellationToken = default
    )
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.EnableDiscordNotifications != true || !config.NotifyOnStreamStart)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var embed = new EmbedBuilder()
            .WithAuthor("Jellyfin.Xtream", JellyfinIconUrl)
            .WithTitle("Stream Started")
            .WithDescription($"**Channel:** `{channelName}` ({streamId})")
            .WithColor(Color.Blue)
            .AddField("🕐 Time", $"<t:{new DateTimeOffset(now).ToUnixTimeSeconds()}:R>", inline: true)
            .AddField("🖥️ Server", "Jellyfin", inline: true)
            .AddField("📡 Status", "Broadcasting", inline: true)
            .WithTimestamp(now)
            .Build();

        _ = await SendDiscordMessageAsync(embed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a stream error notification to Discord.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="channelName">The channel name.</param>
    /// <param name="errorMessage">The error message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task NotifyStreamErrorAsync(
        string streamId,
        string channelName,
        string errorMessage,
        CancellationToken cancellationToken = default
    )
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.EnableDiscordNotifications != true || !config.NotifyOnStreamError)
        {
            return;
        }

        var now = DateTime.UtcNow;

        // Truncate error message if too long (Discord field value limit is 1024)
        var truncatedError = errorMessage.Length > 1024 ? errorMessage[..1021] + "..." : errorMessage;

        var embed = new EmbedBuilder()
            .WithAuthor("Jellyfin.Xtream", JellyfinIconUrl)
            .WithTitle("Stream Error")
            .WithDescription($"**🔴 ERROR** - Stream encountered an error\n**Channel:** `{streamId}`")
            .WithColor(Color.Red)
            .AddField("🕐 Time", $"<t:{new DateTimeOffset(now).ToUnixTimeSeconds()}:R>", inline: true)
            .AddField("# Channel", channelName, inline: true)
            .AddField("❌ Status", "Failed", inline: true)
            .AddField("⚠️ Error Details", truncatedError, inline: false)
            .WithTimestamp(now)
            .Build();

        _ = await SendDiscordMessageAsync(embed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a stream killed notification to Discord.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="channelName">The channel name.</param>
    /// <param name="reason">The reason the stream was killed.</param>
    /// <param name="duration">How long the stream was running.</param>
    /// <param name="bytesTransferred">Total bytes transferred during the stream.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task NotifyStreamKilledAsync(
        string streamId,
        string channelName,
        string reason,
        TimeSpan duration,
        long bytesTransferred,
        CancellationToken cancellationToken = default
    )
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.EnableDiscordNotifications != true || !config.NotifyOnStreamKilled)
        {
            return;
        }

        var now = DateTime.UtcNow;

        // Determine color based on reason
        var color =
            reason.Contains("connection limit", StringComparison.OrdinalIgnoreCase) ? Color.Orange
            : reason.Contains("error", StringComparison.OrdinalIgnoreCase) ? Color.Red
            : Color.DarkGrey;

        var embed = new EmbedBuilder()
            .WithAuthor("Jellyfin.Xtream", JellyfinIconUrl)
            .WithTitle("Stream Killed")
            .WithDescription($"**Channel:** `{channelName}` ({streamId})")
            .WithColor(color)
            .AddField("🕐 Time", $"<t:{new DateTimeOffset(now).ToUnixTimeSeconds()}:R>", inline: true)
            .AddField("⏱️ Duration", FormatDuration(duration), inline: true)
            .AddField("📊 Data Transferred", FormatBytes(bytesTransferred), inline: true)
            .AddField("📝 Reason", reason, inline: false)
            .WithTimestamp(now)
            .WithFooter("Stream terminated")
            .Build();

        _ = await SendDiscordMessageAsync(embed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a buffer health issue notification to Discord.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="channelName">The channel name.</param>
    /// <param name="underrunCount">Number of buffer underrun events.</param>
    /// <param name="fillPercentage">Current buffer fill percentage.</param>
    /// <param name="currentBitrate">Current stream bitrate in Mbps.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task NotifyBufferHealthIssueAsync(
        string streamId,
        string channelName,
        int underrunCount,
        double fillPercentage,
        double currentBitrate,
        CancellationToken cancellationToken = default
    )
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.EnableDiscordNotifications != true)
        {
            return;
        }

        if (!await ShouldSendNotificationAsync().ConfigureAwait(false))
        {
            return;
        }

        var now = DateTime.UtcNow;

        // Determine severity based on fill percentage and underrun count
        var severity =
            underrunCount >= 10 ? "🔴 CRITICAL"
            : underrunCount >= 5 ? "⚠️ WARNING"
            : "ℹ️ NOTICE";

        var color =
            underrunCount >= 10 ? Color.Red
            : underrunCount >= 5 ? Color.Orange
            : Color.LightOrange;

        // Build recommendation based on metrics
        var recommendation =
            fillPercentage < 5 ? "⚡ **URGENT**: Buffer critically low. Increase buffer size to 48-64MB immediately."
            : fillPercentage < 10 ? "📈 **Recommended**: Increase buffer size to 48MB for better stability."
            : "🔍 Check network stability and stream bitrate consistency.";

        var embed = new EmbedBuilder()
            .WithAuthor("Jellyfin.Xtream", JellyfinIconUrl)
            .WithTitle("Buffer Health Alert")
            .WithDescription($"{severity} · Buffer underrun detected\n**Channel:** `{channelName}` ({streamId})")
            .WithColor(color)
            .AddField("🕐 Time", $"<t:{new DateTimeOffset(now).ToUnixTimeSeconds()}:R>", inline: true)
            .AddField("⚠️ Underrun Count", $"#{underrunCount}", inline: true)
            .AddField("📊 Buffer Fill", $"{fillPercentage:F1}%", inline: true)
            .AddField("📡 Bitrate", $"{currentBitrate:F2} Mbps", inline: true)
            .AddField("💡 Recommendation", recommendation, inline: false)
            .WithTimestamp(now)
            .WithFooter("Buffer health monitoring")
            .Build();

        _ = await SendDiscordMessageAsync(embed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends buffer diagnostics for a specific stream to Discord.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="channelName">The channel name.</param>
    /// <param name="diagnostics">Diagnostic information string.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task SendBufferDiagnosticsAsync(
        string streamId,
        string channelName,
        string diagnostics,
        CancellationToken cancellationToken = default
    )
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.EnableDiscordNotifications != true)
        {
            return;
        }

        var now = DateTime.UtcNow;

        // Parse diagnostics string to extract key metrics
        // Format expected from GetDiagnostics():
        // Stream {streamId} Diagnostics:
        //   Buffer Size: {X}MB
        //   Total Written: {X}MB
        //   Total Read: {X}MB
        //   Current Gap: {X}KB ({X}% of buffer)
        //   Buffer Overflows: {X} events
        //   Data Lost: {X}MB ({X}% of total)
        //   Aligned: {bool}
        //   Status: {status}

        var lines = diagnostics.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var bufferSize = "Unknown";
        var totalWritten = "Unknown";
        var totalRead = "Unknown";
        var currentGap = "Unknown";
        var overflows = "Unknown";
        var dataLost = "Unknown";
        var aligned = "Unknown";
        var status = "Unknown";

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Buffer Size:", StringComparison.Ordinal))
            {
                bufferSize = trimmed[12..].Trim();
            }
            else if (trimmed.StartsWith("Total Written:", StringComparison.Ordinal))
            {
                totalWritten = trimmed[14..].Trim();
            }
            else if (trimmed.StartsWith("Total Read:", StringComparison.Ordinal))
            {
                totalRead = trimmed[11..].Trim();
            }
            else if (trimmed.StartsWith("Current Gap:", StringComparison.Ordinal))
            {
                currentGap = trimmed[12..].Trim();
            }
            else if (trimmed.StartsWith("Buffer Overflows:", StringComparison.Ordinal))
            {
                overflows = trimmed[17..].Trim();
            }
            else if (trimmed.StartsWith("Data Lost:", StringComparison.Ordinal))
            {
                dataLost = trimmed[10..].Trim();
            }
            else if (trimmed.StartsWith("Aligned:", StringComparison.Ordinal))
            {
                aligned = trimmed[8..].Trim();
            }
            else if (trimmed.StartsWith("Status:", StringComparison.Ordinal))
            {
                status = trimmed[7..].Trim();
            }
        }

        // Determine color based on status
        var color =
            status.Contains("CRITICAL", StringComparison.OrdinalIgnoreCase)
            || status.Contains("LAGGING", StringComparison.OrdinalIgnoreCase)
                ? Color.Red
            : status.Contains("WARNING", StringComparison.OrdinalIgnoreCase) ? Color.Orange
            : Color.Green;

        var statusEmoji =
            status.Contains("CRITICAL", StringComparison.OrdinalIgnoreCase)
            || status.Contains("LAGGING", StringComparison.OrdinalIgnoreCase)
                ? "🔴"
            : status.Contains("WARNING", StringComparison.OrdinalIgnoreCase) ? "⚠️"
            : "✅";

        var embed = new EmbedBuilder()
            .WithAuthor("Jellyfin.Xtream", JellyfinIconUrl)
            .WithTitle("Buffer Diagnostics")
            .WithDescription($"📊 **Detailed Buffer Status**\n**Channel:** `{channelName}` ({streamId})")
            .WithColor(color)
            .AddField("🕐 Time", $"<t:{new DateTimeOffset(now).ToUnixTimeSeconds()}:R>", inline: true)
            .AddField("📊 Status", $"{statusEmoji} {status}", inline: true)
            .AddField("💾 Buffer Size", bufferSize, inline: true)
            .AddField("📤 Written", totalWritten, inline: true)
            .AddField("📥 Read", totalRead, inline: true)
            .AddField("⏱️ Gap", currentGap, inline: true)
            .AddField("⚠️ Overflows", overflows, inline: true)
            .AddField("📉 Data Lost", dataLost, inline: true)
            .AddField("🎯 Aligned", aligned, inline: true)
            .WithTimestamp(now)
            .WithFooter("Buffer diagnostics snapshot")
            .Build();

        _ = await SendDiscordMessageAsync(embed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Tests the Discord webhook configuration.
    /// </summary>
    /// <param name="webhookUrl">The webhook URL to test.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the test succeeded, false otherwise.</returns>
    public async Task<bool> TestWebhookAsync(string webhookUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            return false;
        }

        var now = DateTime.UtcNow;

        var embed = new EmbedBuilder()
            .WithAuthor("Jellyfin.Xtream", JellyfinIconUrl)
            .WithTitle("Test Notification")
            .WithDescription("**SUCCESS** · Discord integration is working!")
            .WithColor(Color.Green)
            .AddField("🕐 Time", $"<t:{new DateTimeOffset(now).ToUnixTimeSeconds()}:R>", inline: true)
            .AddField("# Channel", "Configuration", inline: true)
            .AddField("▶ Event", "Webhook Test", inline: true)
            .AddField("📊 Status", "Ready", inline: true)
            .WithTimestamp(now)
            .Build();

        return await SendDiscordMessageAsync(embed, cancellationToken, webhookUrl).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task NotifyStreamQualityViolationAsync(
        string streamId,
        string channelName,
        string violationType,
        string details,
        CancellationToken cancellationToken = default
    )
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.EnableDiscordNotifications != true || !config.NotifyOnStreamQualityViolation)
        {
            return;
        }

        if (!await ShouldSendNotificationAsync().ConfigureAwait(false))
        {
            return;
        }

        var now = DateTime.UtcNow;

        // Determine severity based on violation type
        var (color, emoji) = GetViolationSeverity(violationType);

        var embed = new EmbedBuilder()
            .WithAuthor("Jellyfin.Xtream", JellyfinIconUrl)
            .WithTitle("TR 101 290 Stream Quality Violation")
            .WithDescription($"{emoji} **{violationType}**\n**Channel:** `{channelName}` ({streamId})")
            .WithColor(color)
            .AddField("🕐 Time", $"<t:{new DateTimeOffset(now).ToUnixTimeSeconds()}:R>", inline: true)
            .AddField("📋 Violation Type", violationType, inline: true)
            .AddField("📝 Details", details.Length > 1000 ? $"{details.AsSpan(0, 997)}..." : details, inline: false)
            .WithTimestamp(now)
            .WithFooter("TR 101 290 Stream Quality Monitoring")
            .Build();

        _ = await SendDiscordMessageAsync(embed, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task NotifyEpgRefreshStartedAsync(int channelCount, CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.EnableDiscordNotifications != true || !config.NotifyOnEpgRefresh)
        {
            return;
        }

        var now = DateTime.UtcNow;

        var embedBuilder = new EmbedBuilder()
            .WithAuthor("Jellyfin.Xtream", JellyfinIconUrl)
            .WithTitle("EPG Refresh Started")
            .WithDescription("🔄 **STARTED** - EPG data refresh in progress")
            .WithColor(Color.Blue)
            .AddField("🕐 Time", $"<t:{new DateTimeOffset(now).ToUnixTimeSeconds()}:R>", inline: true);

        // Only show channel count if known (> 0)
        if (channelCount > 0)
        {
            _ = embedBuilder.AddField("📺 Channels", $"{channelCount}", inline: true);
        }

        _ = embedBuilder
            .AddField("⚡ Status", "Processing...", inline: true)
            .WithTimestamp(now)
            .WithFooter("EPG Refresh Monitoring");

        _ = await SendDiscordMessageAsync(embedBuilder.Build(), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task NotifyEpgRefreshAsync(EpgRefreshResult result, CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.EnableDiscordNotifications != true || !config.NotifyOnEpgRefresh)
        {
            return;
        }

        var now = DateTime.UtcNow;

        Color color;
        string statusEmoji;
        string statusText;

        if (result.IsSuccess)
        {
            color = Color.Green;
            statusEmoji = "✅";
            statusText = "SUCCESS";
        }
        else if (result.IsPartialSuccess)
        {
            color = Color.Orange;
            statusEmoji = "⚠️";
            statusText = "PARTIAL";
        }
        else
        {
            color = Color.Red;
            statusEmoji = "🔴";
            statusText = "FAILED";
        }

        var successRate = result.TotalCount > 0 ? (result.SuccessCount * 100.0 / result.TotalCount) : 0;

        var embedBuilder = new EmbedBuilder()
            .WithAuthor("Jellyfin.Xtream", JellyfinIconUrl)
            .WithTitle("EPG Refresh Complete")
            .WithDescription($"{statusEmoji} **{statusText}** - EPG data refresh completed")
            .WithColor(color)
            .AddField("🕐 Time", $"<t:{new DateTimeOffset(now).ToUnixTimeSeconds()}:R>", inline: true)
            .AddField("⏱️ Duration", FormatDuration(result.Duration), inline: true)
            .AddField("📊 Success Rate", $"{successRate:F1}%", inline: true)
            .AddField("✅ Channels with EPG", $"{result.SuccessCount}", inline: true)
            .AddField("📺 Total Channels", $"{result.TotalCount}", inline: true)
            .AddField("🔄 Retried Success", $"{result.RetriedSuccessCount}", inline: true);

        if (result.HttpErrorCount > 0 || result.NoDataCount > 0)
        {
            _ = embedBuilder
                .AddField("🌐 HTTP Errors", $"{result.HttpErrorCount}", inline: true)
                .AddField("📭 No Data", $"{result.NoDataCount}", inline: true);
        }

        if (!string.IsNullOrEmpty(result.ErrorMessage))
        {
            var errorMsg =
                result.ErrorMessage.Length > 500 ? $"{result.ErrorMessage.AsSpan(0, 497)}..." : result.ErrorMessage;
            _ = embedBuilder.AddField("⚠️ Error", errorMsg, inline: false);
        }

        if (result.FailedChannels.Count is > 0 and <= 10)
        {
            var failedList = string.Join(", ", result.FailedChannels);
            if (failedList.Length > 500)
            {
                failedList = $"{failedList.AsSpan(0, 497)}...";
            }

            _ = embedBuilder.AddField("📋 Failed Channels", failedList, inline: false);
        }
        else if (result.FailedChannels.Count > 10)
        {
            _ = embedBuilder.AddField(
                "📋 Failed Channels",
                $"{result.FailedChannels.Count} channels (too many to list)",
                inline: false
            );
        }

        _ = embedBuilder.WithTimestamp(now).WithFooter("EPG Refresh Monitoring");

        _ = await SendDiscordMessageAsync(embedBuilder.Build(), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task NotifyAVDriftAsync(
        string streamId,
        string channelName,
        double driftMs,
        string status,
        double peakDriftMs,
        long violationCount,
        CancellationToken cancellationToken = default
    )
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.EnableDiscordNotifications != true || !config.NotifyOnAVDrift)
        {
            return;
        }

        var now = DateTime.UtcNow;

        // Per-channel rate limiting to prevent spam from problematic streams
        // Only send one notification per channel every AVDriftCooldownMinutes minutes
        // Note: We don't log suppressions here as this method is called very frequently
        // and logging every suppression would flood the logs
        if (_lastAVDriftNotificationPerChannel.TryGetValue(streamId, out var lastNotificationTime))
        {
            var timeSinceLastNotification = now - lastNotificationTime;
            if (timeSinceLastNotification.TotalMinutes < AVDriftCooldownMinutes)
            {
                return;
            }
        }

        if (!await ShouldSendNotificationAsync().ConfigureAwait(false))
        {
            return;
        }

        // Update last notification time for this channel
        _lastAVDriftNotificationPerChannel[streamId] = now;

        // Determine severity based on drift magnitude
        var absDrift = Math.Abs(driftMs);
        Color color;
        string emoji;
        string severity;

        if (absDrift >= 100)
        {
            // Severe drift - 100ms or more
            color = Color.Red;
            emoji = "🔴";
            severity = "SEVERE";
        }
        else if (absDrift >= 40)
        {
            // Warning drift - 40-100ms
            color = Color.Orange;
            emoji = "⚠️";
            severity = "WARNING";
        }
        else
        {
            // This shouldn't happen as we only notify on threshold violations
            color = Color.Gold;
            emoji = "ℹ️";
            severity = "NOTICE";
        }

        // Determine drift direction
        var direction = driftMs > 0 ? "Audio ahead of video" : "Audio behind video";
        var directionEmoji = driftMs > 0 ? "🔊⏩🎬" : "🎬⏩🔊";

        var embed = new EmbedBuilder()
            .WithAuthor("Jellyfin.Xtream", JellyfinIconUrl)
            .WithTitle("A/V Sync Drift Detected")
            .WithDescription($"{emoji} **{severity}** - {direction}\n**Channel:** `{channelName}` ({streamId})")
            .WithColor(color)
            .AddField("🕐 Time", $"<t:{new DateTimeOffset(now).ToUnixTimeSeconds()}:R>", inline: true)
            .AddField("📊 Status", status, inline: true)
            .AddField(directionEmoji, $"{Math.Abs(driftMs):F1}ms", inline: true)
            .AddField("📈 Peak Drift", $"{peakDriftMs:F1}ms", inline: true)
            .AddField("⚠️ Violations", $"#{violationCount}", inline: true)
            .AddField(
                "💡 Info",
                absDrift >= 100
                    ? "Severe desync may cause noticeable audio/video mismatch. Check source stream quality."
                    : "Minor drift detected. Usually self-corrects. Monitor if persistent.",
                inline: false
            )
            .WithTimestamp(now)
            .WithFooter("A/V Synchronization Monitoring")
            .Build();

        _ = await SendDiscordMessageAsync(embed, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task NotifyConnectionLimitChangeAsync(
        string providerName,
        int activeConnections,
        int maxConnections,
        bool isAtLimit,
        int externalConnections = 0,
        CancellationToken cancellationToken = default
    )
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.EnableDiscordNotifications != true || !config.NotifyOnConnectionLimitChange)
        {
            return;
        }

        var now = DateTime.UtcNow;

        Color color;
        string emoji;
        string title;
        string description;

        if (isAtLimit)
        {
            color = Color.Red;
            emoji = "🔴";
            title = "Provider At Connection Limit";
            description = $"{emoji} **AT LIMIT** - No available connections\n**Provider:** `{providerName}`";
        }
        else
        {
            color = Color.Green;
            emoji = "✅";
            title = "Provider Connections Available";
            description = $"{emoji} **AVAILABLE** - Connections freed up\n**Provider:** `{providerName}`";
        }

        var availableSlots = maxConnections - activeConnections;
        var utilizationPercent = maxConnections > 0 ? (int)Math.Round(100.0 * activeConnections / maxConnections) : 100;

        var embedBuilder = new EmbedBuilder()
            .WithAuthor("Jellyfin.Xtream", JellyfinIconUrl)
            .WithTitle(title)
            .WithDescription(description)
            .WithColor(color)
            .AddField("🕐 Time", $"<t:{new DateTimeOffset(now).ToUnixTimeSeconds()}:R>", inline: true)
            .AddField("📊 Connections", $"{activeConnections}/{maxConnections}", inline: true)
            .AddField("📈 Utilization", $"{utilizationPercent}%", inline: true);

        if (externalConnections > 0)
        {
            _ = embedBuilder.AddField("🌐 External", $"{externalConnections} connections", inline: true);
        }

        _ = embedBuilder
            .AddField("🎰 Available", $"{availableSlots} slot{(availableSlots != 1 ? "s" : "")}", inline: true)
            .WithTimestamp(now)
            .WithFooter("Connection Limit Monitoring");

        _ = await SendDiscordMessageAsync(embedBuilder.Build(), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task NotifyAudioSyncCorrectionAsync(
        string streamId,
        string channelName,
        double originalDriftMs,
        double correctionMs,
        string correctionType,
        long streamOffset,
        CancellationToken cancellationToken = default
    )
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.EnableDiscordNotifications != true || !config.NotifyOnAudioSyncCorrection)
        {
            return;
        }

        if (!await ShouldSendNotificationAsync().ConfigureAwait(false))
        {
            return;
        }

        var now = DateTime.UtcNow;

        // Determine severity based on correction type and magnitude
        var absDrift = Math.Abs(originalDriftMs);
        Color color;
        string emoji;
        string severity;

        if (correctionType == "Reset" || absDrift >= 100)
        {
            color = Color.Red;
            emoji = "🔴";
            severity = "RESET";
        }
        else if (correctionType == "Immediate" || absDrift >= 40)
        {
            color = Color.Orange;
            emoji = "⚠️";
            severity = "IMMEDIATE";
        }
        else if (correctionType == "Predictive")
        {
            color = Color.Blue;
            emoji = "🔮";
            severity = "PREDICTIVE";
        }
        else
        {
            color = Color.Green;
            emoji = "🔧";
            severity = "GRADUAL";
        }

        // Determine drift direction
        var direction = originalDriftMs > 0 ? "Audio was ahead" : "Audio was behind";
        var correctionDirection = correctionMs > 0 ? "delayed" : "advanced";
        var residualDrift = originalDriftMs + correctionMs;

        var embed = new EmbedBuilder()
            .WithAuthor("Jellyfin.Xtream", JellyfinIconUrl)
            .WithTitle("Audio Sync Correction Applied")
            .WithDescription(
                $"{emoji} **{severity}** - PTS correction applied\n**Channel:** `{channelName}` ({streamId})"
            )
            .WithColor(color)
            .AddField("🕐 Time", $"<t:{new DateTimeOffset(now).ToUnixTimeSeconds()}:R>", inline: true)
            .AddField("📊 Type", correctionType, inline: true)
            .AddField("🎯 Original Drift", $"{originalDriftMs:F1}ms", inline: true)
            .AddField("🔧 Correction", $"{Math.Abs(correctionMs):F1}ms {correctionDirection}", inline: true)
            .AddField("📍 Residual", $"{residualDrift:F1}ms", inline: true)
            .AddField("📦 Stream Offset", FormatBytes(streamOffset), inline: true)
            .AddField(
                "💡 Info",
                correctionType == "Reset"
                    ? "Drift exceeded correctable range. Audio PTS was reset to match video."
                    : $"{direction} by {Math.Abs(originalDriftMs):F1}ms. Audio PTS was {correctionDirection} to compensate.",
                inline: false
            )
            .WithTimestamp(now)
            .WithFooter("Audio Synchronization Correction")
            .Build();

        _ = await SendDiscordMessageAsync(embed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Formats bytes into human-readable format (GB, MB, KB).
    /// </summary>
    private static string FormatBytes(long bytes)
    {
        const long gb = 1024L * 1024L * 1024L;
        const long mb = 1024L * 1024L;
        const long kb = 1024L;

        if (bytes >= gb)
        {
            return $"{bytes / (double)gb:F2} GB";
        }

        return bytes >= mb ? $"{bytes / (double)mb:F2} MB"
            : bytes >= kb ? $"{bytes / (double)kb:F2} KB"
            : $"{bytes} B";
    }

    /// <summary>
    /// Formats a TimeSpan duration into human-readable format.
    /// </summary>
    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalDays >= 1 ? $"{duration.Days}d {duration.Hours}h"
            : duration.TotalHours >= 1 ? $"{duration.Hours}h {duration.Minutes}m"
            : $"{duration.Minutes}m {duration.Seconds}s";
    }

    private async Task<bool> ShouldSendNotificationAsync()
    {
        await _rateLimiter.WaitAsync().ConfigureAwait(false);
        try
        {
            var now = DateTime.UtcNow;
            if ((now - _lastNotification).TotalSeconds < MinNotificationIntervalSeconds)
            {
                return false;
            }

            _lastNotification = now;
            return true;
        }
        finally
        {
            _ = _rateLimiter.Release();
        }
    }

    /// <summary>
    /// Sends a Discord message using Discord.Net's webhook client.
    /// </summary>
    private async Task<bool> SendDiscordMessageAsync(
        Embed embed,
        CancellationToken cancellationToken,
        string? customWebhookUrl = null
    )
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            var webhookUrl = customWebhookUrl ?? config?.DiscordWebhookUrl;

            if (string.IsNullOrWhiteSpace(webhookUrl))
            {
                _logger.PluginLogWarning("Discord webhook URL not configured");
                return false;
            }

            var urlPreview = webhookUrl.Length > 50 ? $"{webhookUrl.AsSpan(0, 50)}..." : webhookUrl;
            _logger.LogDebugIfEnabled("Sending Discord notification to: {Url}", urlPreview);

            // Use Discord.Net's DiscordWebhookClient for proper webhook handling
            using var webhookClient = new DiscordWebhookClient(webhookUrl);

            _ = await webhookClient
                .SendMessageAsync(embeds: [embed], username: "Jellyfin.Xtream")
                .ConfigureAwait(false);

            _logger.LogDebugIfEnabled("Discord notification sent successfully");
            return true;
        }
        catch (global::Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.PluginLogError("Discord webhook URL is invalid or has been deleted");
            return false;
        }
        catch (global::Discord.Net.HttpException ex)
        {
            _logger.PluginLogError(ex, "Discord API error: {Message}", ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            _logger.PluginLogError(ex, "Error sending Discord notification");
            return false;
        }
    }

    /// <summary>
    /// Determines the severity color and emoji for a violation type.
    /// Maps TR 101 290 priority levels to visual indicators.
    /// </summary>
    private static (Color Color, string Emoji) GetViolationSeverity(string violationType)
    {
        // TR 101 290 Priority 1 violations - critical
        if (
            violationType.Contains("PCR", StringComparison.OrdinalIgnoreCase)
            && violationType.Contains("Invalid", StringComparison.OrdinalIgnoreCase)
        )
        {
            return (Color.Red, "🔴");
        }

        if (
            violationType.Contains("Sync", StringComparison.OrdinalIgnoreCase)
            && violationType.Contains("Error", StringComparison.OrdinalIgnoreCase)
        )
        {
            return (Color.Red, "🔴");
        }

        // Encryption detection - important informational
        if (violationType.Contains("Encrypted", StringComparison.OrdinalIgnoreCase))
        {
            return (Color.Purple, "🔐");
        }

        // CRC errors - data corruption
        if (violationType.Contains("CRC", StringComparison.OrdinalIgnoreCase))
        {
            return (Color.Red, "❌");
        }

        // TR 101 290 Priority 2 violations - warning
        if (violationType.Contains("Version", StringComparison.OrdinalIgnoreCase))
        {
            return (Color.Orange, "⚠️");
        }

        if (violationType.Contains("Interval", StringComparison.OrdinalIgnoreCase))
        {
            return (Color.Orange, "⏱️");
        }

        // PCR jitter violations
        if (violationType.Contains("Jitter", StringComparison.OrdinalIgnoreCase))
        {
            return (Color.Gold, "⏱️");
        }

        // Default for unknown types
        return (Color.Orange, "📊");
    }

    /// <summary>
    /// Dispose resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _rateLimiter?.Dispose();
        _disposed = true;
    }
}
