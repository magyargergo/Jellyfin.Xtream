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
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Api.Models;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service.Discovery;
using Jellyfin.Xtream.Service.Logging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Api;

/// <summary>
/// Provider discovery pipeline endpoints: status, start, progress (SSE and WebSocket),
/// results, cancel, cache management, and provider import.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="XtreamDiscoveryController"/> class.
/// </remarks>
/// <param name="logger">The logger instance.</param>
[ApiController]
[Route("Xtream")]
[Produces("application/json")]
public class XtreamDiscoveryController(ILogger<XtreamDiscoveryController> logger) : ControllerBase
{
    private readonly ILogger<XtreamDiscoveryController> _logger = logger;

    /// <summary>
    /// Get the current discovery status and progress.
    /// </summary>
    /// <param name="discoveryService">The discovery service.</param>
    /// <returns>The current discovery status with progress.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("DiscoveryStatus")]
    public ActionResult<DiscoveryStatusResponse> GetDiscoveryStatus(
        [FromServices] IProviderDiscoveryService discoveryService
    )
    {
        var progress = discoveryService.GetCurrentProgress();
        return Ok(
            new DiscoveryStatusResponse
            {
                IsRunning = discoveryService.IsRunning,
                Progress =
                    progress != null
                        ? new DiscoveryProgressResponse
                        {
                            Phase = progress.Phase.ToString(),
                            CurrentItem = progress.CurrentItem,
                            TotalItems = progress.TotalItems,
                            ProgressPercent = progress.ProgressPercent,
                            CredentialsFound = progress.CredentialsFound,
                            ConnectivityPassed = progress.ConnectivityPassed,
                            AuthenticationPassed = progress.AuthenticationPassed,
                            WorkingProviders = progress.WorkingProviders,
                            WorkingWithEpg = progress.WorkingWithEpg,
                            FullyWorking = progress.FullyWorking,
                            Excellent = progress.ExcellentProviders,
                            Message = progress.Message,
                            InProgress = progress.InProgress,
                            IsComplete = progress.IsComplete,
                        }
                        : null,
            }
        );
    }

    /// <summary>
    /// Start a discovery operation in the background.
    /// Use DiscoveryProgress SSE endpoint to receive real-time updates.
    /// </summary>
    /// <param name="request">The discovery request options.</param>
    /// <param name="discoveryService">The discovery service.</param>
    /// <returns>Result indicating if the operation was started.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("StartDiscovery")]
    public ActionResult<object> StartDiscovery(
        [FromBody] DiscoveryRequest? request,
        [FromServices] IProviderDiscoveryService discoveryService
    )
    {
        var timeRange = request?.TimeRange ?? Models.DiscoveryTimeRangeOption.LastMonth;
        var options = new DiscoveryOptions
        {
            TimeRange = (DiscoveryTimeRange)timeRange,
            CustomStartDate = request?.CustomStartDate,
            CustomEndDate = request?.CustomEndDate,
            MaxDiscoveryWorkers = request?.MaxDiscoveryWorkers ?? 5,
            TestStream = request?.TestStream ?? true,
            TestEpg = request?.TestEpg ?? true,
            CountryCode = request?.CountryCode ?? "PL",
        };

        var startDate = options.GetStartDate();
        var endDate = options.GetEndDate();

        _logger.PluginLogInformation(
            "Starting background discovery: TimeRange={TimeRange} ({StartDate:yyyy-MM-dd} to {EndDate:yyyy-MM-dd}), DiscoveryWorkers={DiscoveryWorkers}",
            options.TimeRange,
            startDate,
            endDate,
            options.MaxDiscoveryWorkers
        );

        var started = discoveryService.StartDiscoveryAsync(options);

        if (!started)
        {
            return Ok(new { success = false, message = "A discovery operation is already in progress" });
        }

        return Ok(new { success = true, message = "Discovery started. Use /DiscoveryProgress for real-time updates." });
    }

    /// <summary>
    /// Get real-time discovery progress updates via Server-Sent Events (SSE).
    /// </summary>
    /// <param name="discoveryService">The discovery service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>SSE stream of progress updates.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("DiscoveryProgress")]
    public async Task DiscoveryProgress(
        [FromServices] IProviderDiscoveryService discoveryService,
        CancellationToken cancellationToken
    )
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["Connection"] = "keep-alive";

        await foreach (
            var progress in discoveryService.GetProgressUpdatesAsync(cancellationToken).ConfigureAwait(false)
        )
        {
            var data = System.Text.Json.JsonSerializer.Serialize(
                new DiscoveryProgressResponse
                {
                    Phase = progress.Phase.ToString(),
                    CurrentItem = progress.CurrentItem,
                    TotalItems = progress.TotalItems,
                    ProgressPercent = progress.ProgressPercent,
                    CredentialsFound = progress.CredentialsFound,
                    ConnectivityPassed = progress.ConnectivityPassed,
                    AuthenticationPassed = progress.AuthenticationPassed,
                    WorkingProviders = progress.WorkingProviders,
                    WorkingWithEpg = progress.WorkingWithEpg,
                    FullyWorking = progress.FullyWorking,
                    Excellent = progress.ExcellentProviders,
                    Message = progress.Message,
                    InProgress = progress.InProgress,
                    IsComplete = progress.IsComplete,
                }
            );

            var bytes = Encoding.UTF8.GetBytes($"data: {data}\n\n");
            await Response.Body.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        // Send completion event
        var completeBytes = Encoding.UTF8.GetBytes("event: complete\ndata: {}\n\n");
        await Response.Body.WriteAsync(completeBytes, cancellationToken).ConfigureAwait(false);
        await Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Get real-time discovery progress updates via WebSocket.
    /// This is the preferred method over SSE as it works better with Jellyfin's infrastructure.
    /// </summary>
    /// <param name="discoveryService">The discovery service.</param>
    /// <returns>WebSocket connection for progress updates.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("DiscoveryProgressWs")]
    public async Task DiscoveryProgressWebSocket([FromServices] IProviderDiscoveryService discoveryService)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Configure WebSocket with keep-alive
        var wsOptions = new WebSocketAcceptContext { KeepAliveInterval = TimeSpan.FromSeconds(30) };
        using var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync(wsOptions).ConfigureAwait(false);
        _logger.LogDebugIfEnabled("WebSocket connection established for discovery progress");

        try
        {
            using var cts = new CancellationTokenSource();

            // Start receiving messages (handles ping/pong and close frames)
            _ = ReceiveWebSocketMessagesAsync(webSocket, cts);

            // Send progress updates with heartbeat
            // Initialized by the first successful SendAsync before any read
            var lastProgressTime = default(DateTime);
            const int HeartbeatIntervalSeconds = 15;

            await foreach (var progress in discoveryService.GetProgressUpdatesAsync(cts.Token).ConfigureAwait(false))
            {
                if (webSocket.State != WebSocketState.Open || cts.Token.IsCancellationRequested)
                {
                    break;
                }

                var response = new DiscoveryProgressResponse
                {
                    Phase = progress.Phase.ToString(),
                    CurrentItem = progress.CurrentItem,
                    TotalItems = progress.TotalItems,
                    ProgressPercent = progress.ProgressPercent,
                    CredentialsFound = progress.CredentialsFound,
                    ConnectivityPassed = progress.ConnectivityPassed,
                    AuthenticationPassed = progress.AuthenticationPassed,
                    WorkingProviders = progress.WorkingProviders,
                    WorkingWithEpg = progress.WorkingWithEpg,
                    FullyWorking = progress.FullyWorking,
                    Excellent = progress.ExcellentProviders,
                    Message = progress.Message,
                    InProgress = progress.InProgress,
                    IsComplete = progress.IsComplete,
                };

                var json = System.Text.Json.JsonSerializer.Serialize(response);
                var bytes = Encoding.UTF8.GetBytes(json);

                try
                {
                    await webSocket
                        .SendAsync(
                            new ArraySegment<byte>(bytes),
                            WebSocketMessageType.Text,
                            endOfMessage: true,
                            cts.Token
                        )
                        .ConfigureAwait(false);
                    lastProgressTime = DateTime.UtcNow;
                }
                catch (WebSocketException)
                {
                    _logger.LogDebugIfEnabled("WebSocket send failed, connection may be closed");
                    break;
                }

                // Send heartbeat if no progress update for a while
                if ((DateTime.UtcNow - lastProgressTime).TotalSeconds > HeartbeatIntervalSeconds)
                {
                    const string heartbeat = "{\"type\":\"heartbeat\"}";
                    var heartbeatBytes = Encoding.UTF8.GetBytes(heartbeat);
                    try
                    {
                        await webSocket
                            .SendAsync(
                                new ArraySegment<byte>(heartbeatBytes),
                                WebSocketMessageType.Text,
                                endOfMessage: true,
                                cts.Token
                            )
                            .ConfigureAwait(false);
                    }
                    catch (WebSocketException)
                    {
                        break;
                    }
                }
            }

            // Send completion message
            if (webSocket.State == WebSocketState.Open && !cts.Token.IsCancellationRequested)
            {
                const string completeJson = "{\"Phase\":\"Complete\",\"Message\":\"Operation finished\"}";
                var completeBytes = Encoding.UTF8.GetBytes(completeJson);
                try
                {
                    await webSocket
                        .SendAsync(
                            new ArraySegment<byte>(completeBytes),
                            WebSocketMessageType.Text,
                            endOfMessage: true,
                            CancellationToken.None
                        )
                        .ConfigureAwait(false);

                    await webSocket
                        .CloseAsync(WebSocketCloseStatus.NormalClosure, "Complete", CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                    // Client already disconnected
                }
            }

            await cts.CancelAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebugIfEnabled("WebSocket connection cancelled");
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebugIfEnabled(ex, "WebSocket error during discovery progress");
        }
    }

    /// <summary>
    /// Get the result of the last completed discovery operation.
    /// </summary>
    /// <param name="discoveryService">The discovery service.</param>
    /// <returns>The discovery result or null if no operation has completed.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("DiscoveryResult")]
    public ActionResult<DiscoveryResponse> GetDiscoveryResult([FromServices] IProviderDiscoveryService discoveryService)
    {
        var result = discoveryService.GetLastResult();

        return result == null
            ? (ActionResult<DiscoveryResponse>)
                Ok(
                    new DiscoveryResponse
                    {
                        Success = false,
                        ErrorMessage = "No cached results available. Run a discovery first.",
                    }
                )
            : (ActionResult<DiscoveryResponse>)
                Ok(
                    new DiscoveryResponse
                    {
                        Success = result.Success,
                        ErrorMessage = result.ErrorMessage,
                        PagesProcessed = result.DiscoveryResult?.PagesProcessed ?? 0,
                        TotalCredentialsFound = result.DiscoveryResult?.Credentials.Count ?? 0,
                        TotalCredentialsTested = result.TestResults.Count,
                        WorkingProviderCount = result.WorkingProviders.Count,
                        WorkingWithEpgCount = result.WorkingWithEpgProviders.Count,
                        FullyWorkingCount = result.FullyWorkingProviders.Count,
                        ExcellentCount = result.ExcellentProviders.Count,
                        CountryCode = result.TestResults.FirstOrDefault()?.CountryCode,
                        WorkingProviders = [.. result.WorkingProviders.Select(MapToDiscoveryResponse)],
                        FullyWorkingProviders = [.. result.FullyWorkingProviders.Select(MapToDiscoveryResponse)],
                        ExcellentProviders = [.. result.ExcellentProviders.Select(MapToDiscoveryResponse)],
                    }
                );
    }

    /// <summary>
    /// Cancel the current discovery operation.
    /// </summary>
    /// <param name="discoveryService">The discovery service.</param>
    /// <returns>Result indicating success.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("CancelDiscovery")]
    public ActionResult<object> CancelDiscovery([FromServices] IProviderDiscoveryService discoveryService)
    {
        discoveryService.Cancel();
        _logger.PluginLogInformation("Discovery operation cancelled by user");
        return Ok(new { success = true, message = "Discovery cancelled" });
    }

    /// <summary>
    /// Clear the cached discovery results.
    /// </summary>
    /// <param name="discoveryService">The discovery service.</param>
    /// <returns>Result indicating success.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("ClearDiscoveryCache")]
    public ActionResult<object> ClearDiscoveryCache([FromServices] IProviderDiscoveryService discoveryService)
    {
        var cleared = discoveryService.ClearCache();
        _logger.PluginLogInformation("Discovery cache cleared by user");
        return Ok(
            new
            {
                success = true,
                cleared,
                message = cleared ? "Cache cleared" : "No cache to clear",
            }
        );
    }

    /// <summary>
    /// Import a discovered provider as a configured provider.
    /// </summary>
    /// <param name="provider">The provider to import.</param>
    /// <returns>Result with the new provider ID.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("ImportDiscoveredProvider")]
    public ActionResult<object> ImportDiscoveredProvider([FromBody] DiscoveredProviderResponse provider)
    {
        if (string.IsNullOrEmpty(provider.Server) || string.IsNullOrEmpty(provider.Username))
        {
            return BadRequest(
                XtreamControllerHelpers.CreateError(ErrorCodes.ValidationFailed, "Server and username are required")
            );
        }

        if (!UrlValidator.IsValidProviderHost(provider.Server, out var hostError))
        {
            return BadRequest(XtreamControllerHelpers.CreateError(ErrorCodes.ValidationFailed, hostError));
        }

        var config = Plugin.Instance.Configuration;

        // Check for duplicate
        var existingProvider = config.Providers.Find(p =>
            p.BaseUrl.Contains(provider.Server, StringComparison.OrdinalIgnoreCase)
            && p.Username.Equals(provider.Username, StringComparison.OrdinalIgnoreCase)
        );

        if (existingProvider != null)
        {
            return Ok(
                new
                {
                    success = false,
                    message = $"Provider already exists: {existingProvider.Name}",
                    providerId = existingProvider.Id,
                }
            );
        }

        var newProvider = new XtreamProvider
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = $"Discovered - {provider.Server}",
            BaseUrl = $"http://{provider.Server}:{provider.Port}",
            Username = provider.Username,
            Password = provider.Password,
            Enabled = true,
        };

        config.Providers.Add(newProvider);
        Plugin.Instance.SaveConfiguration();

        _logger.PluginLogInformation(
            "Imported discovered provider: {Name} ({Username}@{Server})",
            newProvider.Name,
            newProvider.Username,
            provider.Server
        );

        return Ok(
            new
            {
                success = true,
                message = "Provider imported successfully",
                providerId = newProvider.Id,
                providerName = newProvider.Name,
            }
        );
    }

    // =========================================================================
    // Private Helpers
    // =========================================================================

    private static DiscoveredProviderResponse MapToDiscoveryResponse(ProviderTestResult result)
    {
        return new DiscoveredProviderResponse
        {
            Server = result.Credential.Server,
            Port = result.Credential.Port,
            Username = result.Credential.Username,
            Password = result.Credential.Password,
            Status = result.Status.ToString(),
            ExpirationDate = result.ExpirationDate,
            MaxConnections = result.MaxConnections,
            HasCountryChannels = result.HasCountryChannels,
            CountryChannelCount = result.CountryChannelCount,
            CountryCode = result.CountryCode,
            TotalChannelCount = result.TotalChannelCount,
            StreamWorks = result.StreamWorks,
            StreamStatus = result.StreamStatus,
            HasEpg = result.HasEpg,
            EpgProgramCount = result.EpgProgramCount,
            IsFullyWorking = result.IsFullyWorking,
            HasHighQualityStreams = result.HasHighQualityStreams,
            IsExcellent = result.IsExcellent,
            QualityScore = result.StreamQuality?.QualityScore,
            QualityLevel = result.StreamQuality?.QualityLevel.ToString(),
            QualityIssues = result.StreamQuality?.QualityIssues,
            TrustScore = result.TrustScore?.Score,
            TrustLevel = result.TrustScore?.Level.ToString(),
            TrustSummary = result.TrustScore?.Summary,
            ErrorMessage = result.ErrorMessage,
            CountryChannelNames = result.CountryChannelNames,
        };
    }

    private static async Task ReceiveWebSocketMessagesAsync(WebSocket webSocket, CancellationTokenSource cts)
    {
        var buffer = new byte[1024];
        try
        {
            while (webSocket.State == WebSocketState.Open && !cts.Token.IsCancellationRequested)
            {
                var result = await webSocket
                    .ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token)
                    .ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await cts.CancelAsync().ConfigureAwait(false);
                    break;
                }

                // Handle ping from client (respond with pong) - this is automatic in .NET WebSockets
                // Handle text messages (e.g., client heartbeat/ping)
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    if (message.Contains("ping", StringComparison.OrdinalIgnoreCase))
                    {
                        // Respond with pong
                        var pong = Encoding.UTF8.GetBytes("{\"type\":\"pong\"}");
                        if (webSocket.State == WebSocketState.Open)
                        {
                            await webSocket
                                .SendAsync(
                                    new ArraySegment<byte>(pong),
                                    WebSocketMessageType.Text,
                                    endOfMessage: true,
                                    cts.Token
                                )
                                .ConfigureAwait(false);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
        catch (WebSocketException)
        {
            // Connection closed
            await cts.CancelAsync().ConfigureAwait(false);
        }
    }
}
