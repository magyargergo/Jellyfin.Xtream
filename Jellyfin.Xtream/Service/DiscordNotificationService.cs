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
using System.Globalization;
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
public sealed class DiscordNotificationService : IDiscordNotificationService, IDisposable
{
    private const int MinNotificationIntervalSeconds = 10; // Prevent spam
    private const int AVDriftCooldownMinutes = 5; // Per-channel cooldown for A/V drift notifications
    private const string JellyfinIconUrl =
        "https://raw.githubusercontent.com/jellyfin/jellyfin-ux/master/branding/SVG/icon-transparent.svg";

    private readonly ILogger<DiscordNotificationService> _logger;
    private readonly SemaphoreSlim _rateLimiter = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTime> _lastAVDriftNotificationPerChannel = new(
        StringComparer.Ordinal
    );
    private DateTime _lastNotification = DateTime.MinValue;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscordNotificationService"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public DiscordNotificationService(ILogger<DiscordNotificationService> logger)
    {
        _logger = logger;
    }

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
        if (config == null || !config.EnableDiscordNotifications || !config.NotifyOnBufferOverflow)
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
            .AddField("🕐 Time", $"<t:{new DateTimeOffset(now).ToUnixTimeSeconds()}:R>", true)
            .AddField("# Channel", channelName, true)
            .AddField("⚠️ Event", $"Overflow #{overflowCount}", true)
            .AddField("💾 Data Lost", $"{lostMB:F2} MB", true)
            .AddField("📉 Total Lost", $"{totalLostMB:F2} MB", true)
            .WithTimestamp(now)
            .Build();

        await SendDiscordMessageAsync(embed, cancellationToken).ConfigureAwait(false);
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
        if (config == null || !config.EnableDiscordNotifications || !config.NotifyOnStreamStart)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var embed = new EmbedBuilder()
            .WithAuthor("Jellyfin.Xtream", JellyfinIconUrl)
            .WithTitle("Stream Started")
            .WithDescription($"**Channel:** `{channelName}` ({streamId})")
            .WithColor(Color.Blue)
            .AddField("🕐 Time", $"<t:{new DateTimeOffset(now).ToUnixTimeSeconds()}:R>", true)
            .AddField("🖥️ Server", Environment.MachineName, true)
            .AddField("📡 Status", "Broadcasting", true)
            .WithTimestamp(now)
            .Build();

        await SendDiscordMessageAsync(embed, cancellationToken).ConfigureAwait(false);
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
        if (config == null || !config.EnableDiscordNotifications || !config.NotifyOnStreamError)
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
            .AddField("🕐 Time", $"<t:{new DateTimeOffset(now).ToUnixTimeSeconds()}:R>", true)
            .AddField("# Channel", channelName, true)
            .AddField("❌ Status", "Failed", true)
            .AddField("⚠️ Error Details", truncatedError, false)
            .WithTimestamp(now)
            .Build();

        await SendDiscordMessageAsync(embed, cancellationToken).ConfigureAwait(false);
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
        if (config == null || !config.EnableDiscordNotifications || !config.NotifyOnStreamKilled)
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
            .AddField("🕐 Time", $"<t:{new DateTimeOffset(now).ToUnixTimeSeconds()}:R>", true)
            .AddField("⏱️ Duration", FormatDuration(duration), true)
            .AddField("📊 Data Transferred", FormatBytes(bytesTransferred), true)
            .AddField("📝 Reason", reason, false)
            .WithTimestamp(now)
            .WithFooter("Stream terminated")
            .Build();

        await SendDiscordMessageAsync(embed, cancellationToken).ConfigureAwait(false);
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
        if (config == null || !config.EnableDiscordNotifications)
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
            .AddField("🕐 Time", $"<t:{new DateTimeOffset(now).ToUnixTimeSeconds()}:R>", true)
            .AddField("⚠️ Underrun Count", $"#{underrunCount}", true)
            .AddField("📊 Buffer Fill", $"{fillPercentage:F1}%", true)
            .AddField("📡 Bitrate", $"{currentBitrate:F2} Mbps", true)
            .AddField("💡 Recommendation", recommendation, false)
            .WithTimestamp(now)
            .WithFooter("Buffer health monitoring")
            .Build();

        await SendDiscordMessageAsync(embed, cancellationToken).ConfigureAwait(false);
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
        if (config == null || !config.EnableDiscordNotifications)
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
        string bufferSize = "Unknown";
        string totalWritten = "Unknown";
        string totalRead = "Unknown";
        string currentGap = "Unknown";
        string overflows = "Unknown";
        string dataLost = "Unknown";
        string aligned = "Unknown";
        string status = "Unknown";

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
            .AddField("🕐 Time", $"<t:{new DateTimeOffset(now).ToUnixTimeSeconds()}:R>", true)
            .AddField("📊 Status", $"{statusEmoji} {status}", true)
            .AddField("💾 Buffer Size", bufferSize, true)
            .AddField("📤 Written", totalWritten, true)
            .AddField("📥 Read", totalRead, true)
            .AddField("⏱️ Gap", currentGap, true)
            .AddField("⚠️ Overflows", overflows, true)
            .AddField("📉 Data Lost", dataLost, true)
            .AddField("🎯 Aligned", aligned, true)
            .WithTimestamp(now)
            .WithFooter("Buffer diagnostics snapshot")
            .Build();

        await SendDiscordMessageAsync(embed, cancellationToken).ConfigureAwait(false);
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
            .AddField("🕐 Time", $"<t:{new DateTimeOffset(now).ToUnixTimeSeconds()}:R>", true)
            .AddField("# Channel", "Configuration", true)
            .AddField("▶ Event", "Webhook Test", true)
            .AddField("📊 Status", "Ready", true)
            .WithTimestamp(now)
            .Build();

        return await SendDiscordMessageAsync(embed, cancellationToken, webhookUrl).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends MPEG-TS indexer diagnostics to Discord.
    /// Reports stream health metrics including packet loss, PCR jitter, and transport errors.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="channelName">The channel name.</param>
    /// <param name="diagnostics">The TsIndexer diagnostics string from GetDiagnostics().</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task SendTsIndexerDiagnosticsAsync(
        string streamId,
        string channelName,
        string diagnostics,
        CancellationToken cancellationToken = default
    )
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.EnableDiscordNotifications)
        {
            return;
        }

        var now = DateTime.UtcNow;

        // Parse TsIndexer diagnostics to extract key metrics
        var lines = diagnostics.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string programsDetected = "0";
        string programsWithVideo = "0";
        string packetsParsed = "0";
        string bytesProcessed = "0 MB";
        string resyncEvents = "0";
        string transportErrors = "0 (0%)";
        string continuityErrors = "0 (0%)";
        string partialPacket = "0 bytes";

        // Program-specific metrics
        string videoPid = "N/A";
        string pcrPid = "N/A";
        string keyframes = "0";
        string avgGop = "N/A";
        string packetLoss = "0";
        string pcrStatus = "N/A";

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Programs Detected:", StringComparison.Ordinal))
            {
                programsDetected = trimmed[18..].Trim();
            }
            else if (trimmed.StartsWith("Programs with Video:", StringComparison.Ordinal))
            {
                programsWithVideo = trimmed[20..].Trim();
            }
            else if (trimmed.StartsWith("Packets Parsed:", StringComparison.Ordinal))
            {
                packetsParsed = trimmed[15..].Trim();
            }
            else if (trimmed.StartsWith("Bytes Processed:", StringComparison.Ordinal))
            {
                bytesProcessed = trimmed[16..].Trim();
            }
            else if (trimmed.StartsWith("Resync Events:", StringComparison.Ordinal))
            {
                resyncEvents = trimmed[14..].Trim();
            }
            else if (trimmed.StartsWith("Transport Errors (TEI):", StringComparison.Ordinal))
            {
                transportErrors = trimmed[23..].Trim();
            }
            else if (trimmed.StartsWith("Continuity Errors (CC):", StringComparison.Ordinal))
            {
                continuityErrors = trimmed[23..].Trim();
            }
            else if (trimmed.StartsWith("Partial Packet Buffered:", StringComparison.Ordinal))
            {
                partialPacket = trimmed[24..].Trim();
            }
            else if (trimmed.StartsWith("Video PID:", StringComparison.Ordinal))
            {
                videoPid = trimmed[10..].Trim();
            }
            else if (trimmed.StartsWith("PCR PID:", StringComparison.Ordinal))
            {
                pcrPid = trimmed[8..].Trim();
            }
            else if (trimmed.StartsWith("Keyframes:", StringComparison.Ordinal))
            {
                keyframes = trimmed[10..].Trim();
            }
            else if (trimmed.StartsWith("Avg GOP:", StringComparison.Ordinal))
            {
                avgGop = trimmed[8..].Trim();
            }
            else if (trimmed.StartsWith("Packet Loss:", StringComparison.Ordinal))
            {
                packetLoss = trimmed[12..].Trim();
            }
            else if (trimmed.StartsWith("PCR Status:", StringComparison.Ordinal))
            {
                pcrStatus = trimmed[11..].Trim();
            }
        }

        // Determine health status and color based on error rates
        var hasErrors =
            continuityErrors.Contains('(', StringComparison.Ordinal)
            && !continuityErrors.Contains("(0.0000%)", StringComparison.Ordinal);
        var hasTransportErrors =
            transportErrors.Contains('(', StringComparison.Ordinal)
            && !transportErrors.Contains("(0.0000%)", StringComparison.Ordinal);
        var hasResync = resyncEvents != "0";

        Color color;
        string statusEmoji;
        string healthStatus;

        if (hasTransportErrors || (hasErrors && !continuityErrors.Contains("(0.", StringComparison.Ordinal)))
        {
            color = Color.Red;
            statusEmoji = "🔴";
            healthStatus = "DEGRADED";
        }
        else if (hasErrors || hasResync)
        {
            color = Color.Orange;
            statusEmoji = "⚠️";
            healthStatus = "WARNING";
        }
        else
        {
            color = Color.Green;
            statusEmoji = "✅";
            healthStatus = "HEALTHY";
        }

        var embed = new EmbedBuilder()
            .WithAuthor("Jellyfin.Xtream", JellyfinIconUrl)
            .WithTitle("MPEG-TS Stream Diagnostics")
            .WithDescription($"📊 **Stream Health Report**\n**Channel:** `{channelName}` ({streamId})")
            .WithColor(color)
            .AddField("🕐 Time", $"<t:{new DateTimeOffset(now).ToUnixTimeSeconds()}:R>", true)
            .AddField("📊 Status", $"{statusEmoji} {healthStatus}", true)
            .AddField("📺 Programs", $"{programsWithVideo}/{programsDetected} with video", true)
            .AddField("📦 Packets Parsed", packetsParsed, true)
            .AddField("💾 Data Processed", bytesProcessed, true)
            .AddField("🔄 Resync Events", resyncEvents, true)
            .AddField("❌ Transport Errors", transportErrors, true)
            .AddField("⚠️ Continuity Errors", continuityErrors, true)
            .AddField("📼 Partial Buffer", partialPacket, true);

        // Add program-specific fields if available
        if (videoPid != "N/A")
        {
            embed
                .AddField("🎬 Video PID", videoPid, true)
                .AddField("⏱️ PCR PID", pcrPid, true)
                .AddField("🔑 Keyframes", keyframes, true)
                .AddField("📐 Avg GOP", avgGop, true)
                .AddField("📉 Packet Loss", packetLoss, true)
                .AddField(
                    "🕰️ PCR Status",
                    pcrStatus.Length > 100 ? string.Concat(pcrStatus.AsSpan(0, 97), "...") : pcrStatus,
                    false
                );
        }

        embed.WithTimestamp(now).WithFooter("MPEG-TS Indexer Diagnostics (ISO/IEC 13818-1, TR 101 290)");

        await SendDiscordMessageAsync(embed.Build(), cancellationToken).ConfigureAwait(false);
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
        if (config == null || !config.EnableDiscordNotifications || !config.NotifyOnStreamQualityViolation)
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
            .AddField("🕐 Time", $"<t:{new DateTimeOffset(now).ToUnixTimeSeconds()}:R>", true)
            .AddField("📋 Violation Type", violationType, true)
            .AddField(
                "📝 Details",
                details.Length > 1000 ? string.Concat(details.AsSpan(0, 997), "...") : details,
                false
            )
            .WithTimestamp(now)
            .WithFooter("TR 101 290 Stream Quality Monitoring")
            .Build();

        await SendDiscordMessageAsync(embed, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task NotifyEpgRefreshStartedAsync(int channelCount, CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.EnableDiscordNotifications || !config.NotifyOnEpgRefresh)
        {
            return;
        }

        var now = DateTime.UtcNow;

        var embedBuilder = new EmbedBuilder()
            .WithAuthor("Jellyfin.Xtream", JellyfinIconUrl)
            .WithTitle("EPG Refresh Started")
            .WithDescription("🔄 **STARTED** - EPG data refresh in progress")
            .WithColor(Color.Blue)
            .AddField("🕐 Time", $"<t:{new DateTimeOffset(now).ToUnixTimeSeconds()}:R>", true);

        // Only show channel count if known (> 0)
        if (channelCount > 0)
        {
            embedBuilder.AddField("📺 Channels", $"{channelCount}", true);
        }

        embedBuilder
            .AddField("⚡ Status", "Processing...", true)
            .WithTimestamp(now)
            .WithFooter("EPG Refresh Monitoring");

        await SendDiscordMessageAsync(embedBuilder.Build(), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task NotifyEpgRefreshAsync(EpgRefreshResult result, CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.EnableDiscordNotifications || !config.NotifyOnEpgRefresh)
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
            .AddField("🕐 Time", $"<t:{new DateTimeOffset(now).ToUnixTimeSeconds()}:R>", true)
            .AddField("⏱️ Duration", FormatDuration(result.Duration), true)
            .AddField("📊 Success Rate", $"{successRate:F1}%", true)
            .AddField("✅ Channels with EPG", $"{result.SuccessCount}", true)
            .AddField("📺 Total Channels", $"{result.TotalCount}", true)
            .AddField("🔄 Retried Success", $"{result.RetriedSuccessCount}", true);

        if (result.HttpErrorCount > 0 || result.NoDataCount > 0)
        {
            embedBuilder
                .AddField("🌐 HTTP Errors", $"{result.HttpErrorCount}", true)
                .AddField("📭 No Data", $"{result.NoDataCount}", true);
        }

        if (!string.IsNullOrEmpty(result.ErrorMessage))
        {
            var errorMsg =
                result.ErrorMessage.Length > 500
                    ? string.Concat(result.ErrorMessage.AsSpan(0, 497), "...")
                    : result.ErrorMessage;
            embedBuilder.AddField("⚠️ Error", errorMsg, false);
        }

        if (result.FailedChannels.Count > 0 && result.FailedChannels.Count <= 10)
        {
            var failedList = string.Join(", ", result.FailedChannels);
            if (failedList.Length > 500)
            {
                failedList = string.Concat(failedList.AsSpan(0, 497), "...");
            }

            embedBuilder.AddField("📋 Failed Channels", failedList, false);
        }
        else if (result.FailedChannels.Count > 10)
        {
            embedBuilder.AddField(
                "📋 Failed Channels",
                $"{result.FailedChannels.Count} channels (too many to list)",
                false
            );
        }

        embedBuilder.WithTimestamp(now).WithFooter("EPG Refresh Monitoring");

        await SendDiscordMessageAsync(embedBuilder.Build(), cancellationToken).ConfigureAwait(false);
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
        if (config == null || !config.EnableDiscordNotifications || !config.NotifyOnAVDrift)
        {
            return;
        }

        var now = DateTime.UtcNow;

        // Per-channel rate limiting to prevent spam from problematic streams
        // Only send one notification per channel every AVDriftCooldownMinutes minutes
        if (_lastAVDriftNotificationPerChannel.TryGetValue(streamId, out var lastNotificationTime))
        {
            var timeSinceLastNotification = now - lastNotificationTime;
            if (timeSinceLastNotification.TotalMinutes < AVDriftCooldownMinutes)
            {
                _logger.LogDebugIfEnabled(
                    "Suppressing A/V drift notification for channel {ChannelId} - last notification was {Minutes:F1} minutes ago (cooldown: {Cooldown} minutes)",
                    streamId,
                    timeSinceLastNotification.TotalMinutes,
                    AVDriftCooldownMinutes
                );
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
        double absDrift = Math.Abs(driftMs);
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
        string direction = driftMs > 0 ? "Audio ahead of video" : "Audio behind video";
        string directionEmoji = driftMs > 0 ? "🔊⏩🎬" : "🎬⏩🔊";

        var embed = new EmbedBuilder()
            .WithAuthor("Jellyfin.Xtream", JellyfinIconUrl)
            .WithTitle("A/V Sync Drift Detected")
            .WithDescription($"{emoji} **{severity}** - {direction}\n**Channel:** `{channelName}` ({streamId})")
            .WithColor(color)
            .AddField("🕐 Time", $"<t:{new DateTimeOffset(now).ToUnixTimeSeconds()}:R>", true)
            .AddField("📊 Status", status, true)
            .AddField(directionEmoji, $"{Math.Abs(driftMs):F1}ms", true)
            .AddField("📈 Peak Drift", $"{peakDriftMs:F1}ms", true)
            .AddField("⚠️ Violations", $"#{violationCount}", true)
            .AddField(
                "💡 Info",
                absDrift >= 100
                    ? "Severe desync may cause noticeable audio/video mismatch. Check source stream quality."
                    : "Minor drift detected. Usually self-corrects. Monitor if persistent.",
                false
            )
            .WithTimestamp(now)
            .WithFooter("A/V Synchronization Monitoring")
            .Build();

        await SendDiscordMessageAsync(embed, cancellationToken).ConfigureAwait(false);
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
        if (config == null || !config.EnableDiscordNotifications || !config.NotifyOnConnectionLimitChange)
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
            .AddField("🕐 Time", $"<t:{new DateTimeOffset(now).ToUnixTimeSeconds()}:R>", true)
            .AddField("📊 Connections", $"{activeConnections}/{maxConnections}", true)
            .AddField("📈 Utilization", $"{utilizationPercent}%", true);

        if (externalConnections > 0)
        {
            embedBuilder.AddField("🌐 External", $"{externalConnections} connections", true);
        }

        embedBuilder
            .AddField("🎰 Available", $"{availableSlots} slot{(availableSlots != 1 ? "s" : "")}", true)
            .WithTimestamp(now)
            .WithFooter("Connection Limit Monitoring");

        await SendDiscordMessageAsync(embedBuilder.Build(), cancellationToken).ConfigureAwait(false);
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

        if (bytes >= mb)
        {
            return $"{bytes / (double)mb:F2} MB";
        }

        if (bytes >= kb)
        {
            return $"{bytes / (double)kb:F2} KB";
        }

        return $"{bytes} B";
    }

    /// <summary>
    /// Formats a TimeSpan duration into human-readable format.
    /// </summary>
    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
        {
            return $"{duration.Days}d {duration.Hours}h";
        }

        if (duration.TotalHours >= 1)
        {
            return $"{duration.Hours}h {duration.Minutes}m";
        }

        return $"{duration.Minutes}m {duration.Seconds}s";
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
            _rateLimiter.Release();
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
                _logger.LogWarning("Discord webhook URL not configured");
                return false;
            }

            var urlPreview = webhookUrl.Length > 50 ? string.Concat(webhookUrl.AsSpan(0, 50), "...") : webhookUrl;
            _logger.LogDebugIfEnabled("Sending Discord notification to: {Url}", urlPreview);

            // Use Discord.Net's DiscordWebhookClient for proper webhook handling
            using var webhookClient = new DiscordWebhookClient(webhookUrl);

            await webhookClient
                .SendMessageAsync(embeds: new[] { embed }, username: "Jellyfin.Xtream")
                .ConfigureAwait(false);

            _logger.LogDebugIfEnabled("Discord notification sent successfully");
            return true;
        }
        catch (global::Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogError("Discord webhook URL is invalid or has been deleted");
            return false;
        }
        catch (global::Discord.Net.HttpException ex)
        {
            _logger.LogError(ex, "Discord API error: {Message}", ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending Discord notification");
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
