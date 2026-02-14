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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Api.Models;
using Jellyfin.Xtream.Service;
using Jellyfin.Xtream.Service.Logging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Api;

/// <summary>
/// Event streaming (SSE), recent events, log viewer, and Discord notification endpoints.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="XtreamEventsController"/> class.
/// </remarks>
/// <param name="logger">The logger instance.</param>
[ApiController]
[Route("Xtream")]
[Produces("application/json")]
public class XtreamEventsController(ILogger<XtreamEventsController> logger) : ControllerBase
{
    private readonly ILogger<XtreamEventsController> _logger = logger;

    // =========================================================================
    // Server-Sent Events
    // =========================================================================

    /// <summary>
    /// Subscribe to real-time plugin events via Server-Sent Events (SSE).
    /// Supports optional filtering by event type prefix and stream ID.
    /// Supports Last-Event-ID header for automatic reconnection and replay.
    /// </summary>
    /// <param name="typeFilter">Optional event type prefix filter (e.g. "stream", "buffer", "provider").</param>
    /// <param name="streamId">Optional stream ID to filter events for a specific stream.</param>
    /// <returns>SSE event stream.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Events/Stream")]
    [Produces("text/event-stream")]
    public async Task GetEventStream([FromQuery] string? typeFilter = null, [FromQuery] string? streamId = null)
    {
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");
        Response.ContentType = "text/event-stream";

        long lastEventId = 0;
        if (
            Request.Headers.TryGetValue("Last-Event-ID", out var lastIdHeader)
            && long.TryParse(lastIdHeader.ToString(), out var parsedId)
        )
        {
            lastEventId = parsedId;
        }

        var ct = HttpContext.RequestAborted;
        await foreach (
            var evt in Service.Events.PluginEventBus.Instance.SubscribeAsync(lastEventId, ct).ConfigureAwait(false)
        )
        {
            if (
                !string.IsNullOrEmpty(typeFilter)
                && !evt.Type.StartsWith(typeFilter, StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            if (!string.IsNullOrEmpty(streamId) && evt.StreamId != streamId)
            {
                continue;
            }

            var json = System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    evt.Id,
                    evt.Type,
                    evt.Timestamp,
                    evt.StreamId,
                    evt.Data,
                }
            );

            await Response.WriteAsync($"id: {evt.Id}\nevent: {evt.Type}\ndata: {json}\n\n", ct).ConfigureAwait(false);
            await Response.Body.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Get recent events from the replay buffer (for agents that don't support SSE).
    /// </summary>
    /// <param name="count">Maximum number of events to return (default 50, max 200).</param>
    /// <param name="typeFilter">Optional event type prefix filter.</param>
    /// <returns>Recent events, newest last.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Events/Recent")]
    public ActionResult<object> GetRecentEvents([FromQuery] int count = 50, [FromQuery] string? typeFilter = null)
    {
        count = Math.Clamp(count, 1, 200);
        var events = Service.Events.PluginEventBus.Instance.GetRecentEvents(count);

        IEnumerable<Service.Events.PluginEvent> filtered = events;
        if (!string.IsNullOrEmpty(typeFilter))
        {
            filtered = filtered.Where(e => e.Type.StartsWith(typeFilter, StringComparison.OrdinalIgnoreCase));
        }

        var items = filtered.ToList();
        return Ok(
            new
            {
                count = items.Count,
                subscribers = Service.Events.PluginEventBus.Instance.GetSubscriberCount(),
                events = items,
            }
        );
    }

    // =========================================================================
    // Log Viewer
    // =========================================================================

    /// <summary>
    /// Get plugin log entries with optional filtering.
    /// </summary>
    /// <param name="logService">The log service.</param>
    /// <param name="minLevel">Minimum log level (0=Trace, 1=Debug, 2=Info, 3=Warning, 4=Error, 5=Critical).</param>
    /// <param name="includeDebug">Whether to include debug entries.</param>
    /// <param name="category">Optional category filter.</param>
    /// <param name="streamId">Optional stream ID filter.</param>
    /// <param name="searchText">Optional text search.</param>
    /// <param name="skip">Number of entries to skip for pagination.</param>
    /// <param name="take">Number of entries to return (max 500).</param>
    /// <returns>List of log entries.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Logs")]
    public ActionResult<IReadOnlyList<PluginLogEntry>> GetLogs(
        [FromServices] IPluginLogService logService,
        [FromQuery] int minLevel = 0,
        [FromQuery] bool includeDebug = true,
        [FromQuery] string? category = null,
        [FromQuery] string? streamId = null,
        [FromQuery] string? searchText = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100
    )
    {
        var level = (LogLevel)Math.Clamp(minLevel, 0, 5);
        var entries = logService.GetEntries(
            level,
            includeDebug,
            category,
            streamId,
            searchText,
            skip,
            Math.Min(take, 500)
        );
        return Ok(entries);
    }

    /// <summary>
    /// Get new log entries since a specific ID (for polling).
    /// </summary>
    /// <param name="logService">The log service.</param>
    /// <param name="afterId">Return entries with ID greater than this value.</param>
    /// <param name="minLevel">Minimum log level.</param>
    /// <param name="includeDebug">Whether to include debug entries.</param>
    /// <returns>New log entries.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Logs/After/{afterId}")]
    public ActionResult<IReadOnlyList<PluginLogEntry>> GetLogsAfter(
        [FromServices] IPluginLogService logService,
        long afterId,
        [FromQuery] int minLevel = 0,
        [FromQuery] bool includeDebug = true
    )
    {
        var level = (LogLevel)Math.Clamp(minLevel, 0, 5);
        var entries = logService.GetEntriesAfter(afterId, level, includeDebug);
        return Ok(entries);
    }

    /// <summary>
    /// Get log buffer statistics.
    /// </summary>
    /// <param name="logService">The log service.</param>
    /// <returns>Log statistics.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Logs/Stats")]
    public ActionResult<PluginLogStats> GetLogStats([FromServices] IPluginLogService logService) =>
        Ok(logService.GetStats());

    /// <summary>
    /// Clear all log entries.
    /// </summary>
    /// <param name="logService">The log service.</param>
    /// <returns>Success result.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpDelete("Logs")]
    public ActionResult<object> ClearLogs([FromServices] IPluginLogService logService)
    {
        logService.Clear();
        _logger.PluginLogInformation("Log buffer cleared by user");
        return Ok(new { success = true, message = "Log buffer cleared" });
    }

    // =========================================================================
    // Discord Notifications
    // =========================================================================

    /// <summary>
    /// Test Discord webhook configuration.
    /// </summary>
    /// <param name="webhookUrl">The webhook URL to test.</param>
    /// <param name="discordService">The Discord notification service.</param>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>A result indicating whether the test succeeded.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("TestDiscordWebhook")]
    public async Task<ActionResult> TestDiscordWebhook(
        [FromQuery] string webhookUrl,
        [FromServices] IDiscordNotificationService discordService,
        CancellationToken cancellationToken
    )
    {
        if (!UrlValidator.IsValidDiscordWebhookUrl(webhookUrl, out var urlError))
        {
            return BadRequest(XtreamControllerHelpers.CreateError(ErrorCodes.ValidationFailed, urlError));
        }

        var success = await discordService.TestWebhookAsync(webhookUrl, cancellationToken).ConfigureAwait(false);
        return Ok(
            new
            {
                success,
                message = success ? "Test notification sent successfully" : "Failed to send test notification",
            }
        );
    }

    /// <summary>
    /// Send buffer diagnostics for all active streams to Discord.
    /// </summary>
    /// <param name="discordService">The Discord notification service.</param>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>A result with the count of streams processed.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("SendBufferDiagnostics")]
    public async Task<ActionResult> SendBufferDiagnostics(
        [FromServices] IDiscordNotificationService discordService,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var count = await CircularBufferReadStream
                .SendAllDiagnosticsToDiscordAsync(discordService, _logger)
                .ConfigureAwait(false);
            return Ok(
                new
                {
                    success = true,
                    count,
                    message = count > 0 ? $"Buffer diagnostics sent for {count} stream(s)" : "No active streams found",
                }
            );
        }
        catch (Exception ex)
        {
            _logger.PluginLogError(ex, "Failed to send buffer diagnostics");
            return Ok(
                new
                {
                    success = false,
                    count = 0,
                    message = "Failed to send diagnostics. Check server logs for details.",
                }
            );
        }
    }

    /// <summary>
    /// Refresh Discord notification service configuration.
    /// Called after saving settings to update the service with new webhook URL and notification preferences.
    /// </summary>
    /// <param name="discordService">The Discord notification service.</param>
    /// <returns>Result indicating success.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("RefreshDiscordConfiguration")]
    public ActionResult<object> RefreshDiscordConfiguration([FromServices] IDiscordNotificationService discordService)
    {
        _logger.LogDebugIfEnabled("Refreshing Discord notification service configuration");
        return Ok(new { success = true, message = "Discord configuration refreshed" });
    }
}
