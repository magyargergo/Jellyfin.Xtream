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
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Api.Models;
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service;
using Jellyfin.Xtream.Service.Logging;
using Jellyfin.Xtream.Service.Streaming.Native;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Api;

/// <summary>
/// Provider management endpoints: CRUD, connection status, channel overrides, and registry health.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="XtreamProviderController"/> class.
/// </remarks>
/// <param name="logger">The logger instance.</param>
/// <param name="loggerFactory">The logger factory instance.</param>
/// <param name="httpClientFactory">The HTTP client factory.</param>
[ApiController]
[Route("Xtream")]
[Produces("application/json")]
public class XtreamProviderController(
    ILogger<XtreamProviderController> logger,
    ILoggerFactory loggerFactory,
    IHttpClientFactory httpClientFactory
) : ControllerBase
{
    private readonly ILogger<XtreamProviderController> _logger = logger;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;

    // =========================================================================
    // Provider CRUD
    // =========================================================================

    /// <summary>
    /// Get list of configured providers.
    /// </summary>
    /// <returns>List of providers with basic info.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Providers")]
    public ActionResult<IEnumerable<object>> GetProviders()
    {
        var providers = Plugin
            .Instance.Configuration.Providers.Select(p => new
            {
                p.Id,
                p.Name,
                p.Enabled,
                p.Priority,
                HasCredentials = !string.IsNullOrEmpty(p.BaseUrl) && !string.IsNullOrEmpty(p.Username),
            })
            .ToList();

        return Ok(providers);
    }

    /// <summary>
    /// Get a single provider by ID.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>Provider details (credentials redacted).</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Providers/{providerId}")]
    public ActionResult<object> GetProviderDetails(string providerId)
    {
        var provider = Plugin.Instance.Configuration.GetProvider(providerId);
        if (provider == null)
        {
            return NotFound(
                XtreamControllerHelpers.CreateError(
                    ErrorCodes.ProviderNotFound,
                    "Provider not found",
                    "List providers via GET /Xtream/Providers"
                )
            );
        }

        return Ok(
            new
            {
                provider.Id,
                provider.Name,
                provider.BaseUrl,
                provider.Username,
                provider.Enabled,
                provider.Priority,
                LiveTvCategories = provider.LiveTv.Count,
                VodCategories = provider.Vod.Count,
                SeriesCategories = provider.Series.Count,
                OverrideCount = provider.LiveTvOverrides.Count,
            }
        );
    }

    /// <summary>
    /// Create a new provider.
    /// </summary>
    /// <param name="request">Provider details.</param>
    /// <returns>The created provider ID.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("Providers")]
    public ActionResult<object> CreateProvider([FromBody] ProviderRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.BaseUrl) || string.IsNullOrWhiteSpace(request.Username))
        {
            return BadRequest(
                XtreamControllerHelpers.CreateError(ErrorCodes.ValidationFailed, "BaseUrl and Username are required")
            );
        }

        if (!UrlValidator.IsValidProviderBaseUrl(request.BaseUrl, out var urlError))
        {
            return BadRequest(XtreamControllerHelpers.CreateError(ErrorCodes.ValidationFailed, urlError));
        }

        var config = Plugin.Instance.Configuration;

        var newProvider = new XtreamProvider
        {
            Name = request.Name,
            BaseUrl = request.BaseUrl.TrimEnd('/'),
            Username = request.Username,
            Password = request.Password,
            Enabled = request.Enabled,
            Priority = Math.Clamp(request.Priority, 0, 100),
        };

        config.Providers.Add(newProvider);
        Plugin.Instance.SaveConfiguration();

        _logger.PluginLogInformation("Created provider: {Name} ({Id})", newProvider.Name, newProvider.Id);

        return Ok(
            new
            {
                success = true,
                message = "Provider created",
                providerId = newProvider.Id,
                providerName = newProvider.Name,
            }
        );
    }

    /// <summary>
    /// Update an existing provider.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="request">Updated provider details.</param>
    /// <returns>Result indicating success.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPut("Providers/{providerId}")]
    public ActionResult<object> UpdateProvider(string providerId, [FromBody] ProviderRequest request)
    {
        var config = Plugin.Instance.Configuration;
        var provider = config.GetProvider(providerId);

        if (provider == null)
        {
            return NotFound(
                XtreamControllerHelpers.CreateError(
                    ErrorCodes.ProviderNotFound,
                    "Provider not found",
                    "List providers via GET /Xtream/Providers"
                )
            );
        }

        provider.Name = request.Name;
        provider.Enabled = request.Enabled;
        provider.Priority = Math.Clamp(request.Priority, 0, 100);

        if (!string.IsNullOrWhiteSpace(request.BaseUrl))
        {
            if (!UrlValidator.IsValidProviderBaseUrl(request.BaseUrl, out var urlError))
            {
                return BadRequest(XtreamControllerHelpers.CreateError(ErrorCodes.ValidationFailed, urlError));
            }

            provider.BaseUrl = request.BaseUrl.TrimEnd('/');
        }

        if (!string.IsNullOrWhiteSpace(request.Username))
        {
            provider.Username = request.Username;
        }

        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            provider.Password = request.Password;
        }

        Plugin.Instance.SaveConfiguration();

        _logger.PluginLogInformation("Updated provider: {Name} ({Id})", provider.Name, provider.Id);

        return Ok(
            new
            {
                success = true,
                message = "Provider updated",
                providerId = provider.Id,
            }
        );
    }

    /// <summary>
    /// Delete a provider.
    /// </summary>
    /// <param name="providerId">The provider ID to delete.</param>
    /// <returns>Result indicating success.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpDelete("Providers/{providerId}")]
    public ActionResult<object> DeleteProvider(string providerId)
    {
        var config = Plugin.Instance.Configuration;
        var provider = config.GetProvider(providerId);

        if (provider == null)
        {
            return NotFound(
                XtreamControllerHelpers.CreateError(
                    ErrorCodes.ProviderNotFound,
                    "Provider not found",
                    "List providers via GET /Xtream/Providers"
                )
            );
        }

        var enabledCount = config.Providers.Count(p => p.Enabled);
        if (provider.Enabled && enabledCount <= 1)
        {
            return BadRequest(
                XtreamControllerHelpers.CreateError(
                    ErrorCodes.LastProviderDeletion,
                    "Cannot delete the last enabled provider",
                    "Disable the provider instead, or add another provider first"
                )
            );
        }

        config.Providers.Remove(provider);
        Plugin.Instance.SaveConfiguration();

        _logger.PluginLogWarning("Deleted provider: {Name} ({Id})", provider.Name, providerId);

        return Ok(new { success = true, message = "Provider deleted" });
    }

    // =========================================================================
    // Connection Info & Status
    // =========================================================================

    /// <summary>
    /// Get connection information from the Xtream provider.
    /// </summary>
    /// <param name="providerId">Optional provider ID. If not specified, uses first enabled provider.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Connection info including max connections, active connections, and account status.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("ConnectionInfo")]
    public async Task<ActionResult<object>> GetConnectionInfo(
        [FromQuery] string? providerId,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var provider = XtreamControllerHelpers.GetProvider(providerId);
            if (provider == null)
            {
                return Ok(new { success = false, message = "No provider configured" });
            }

            using var client = new XtreamClient(_httpClientFactory, _loggerFactory.CreateLogger<XtreamClient>());
            var playerApi = await client
                .GetUserAndServerInfoAsync(provider.ToConnectionInfo(), cancellationToken)
                .ConfigureAwait(false);

            if (playerApi?.UserInfo == null)
            {
                return Ok(new { success = false, message = "Failed to get user info from provider" });
            }

            var userInfo = playerApi.UserInfo;
            var pluginActiveStreams = Restream.GetActiveStreamCount();

            return Ok(
                new
                {
                    success = true,
                    providerId = provider.Id,
                    providerName = provider.Name,
                    status = userInfo.Status,
                    maxConnections = userInfo.MaxConnections,
                    activeConnections = userInfo.ActiveCons,
                    pluginActiveStreams,
                    expDate = userInfo.ExpDate,
                    isTrial = userInfo.IsTrial,
                    username = userInfo.Username,
                }
            );
        }
        catch (Exception ex)
        {
            _logger.PluginLogError(ex, "Failed to get connection info from provider");
            return Ok(
                new { success = false, message = "Failed to get connection info. Check server logs for details." }
            );
        }
    }

    /// <summary>
    /// Get aggregated connection status across all enabled providers.
    /// Returns capacity, utilization, warnings, and per-provider breakdown.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Aggregated connection status for all providers.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("ConnectionStatus")]
    public async Task<ActionResult<object>> GetConnectionStatus(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;
        var enabledProviders = config.GetEnabledProviders().ToList();
        var pluginActiveStreams = Restream.GetActiveStreamCount();
        var configuredMaxStreams = config.MaxConcurrentStreams;

        // Query all providers in parallel with a per-provider timeout.
        // Without this, a single timing-out provider (3 retries x 5s connect timeout + backoff)
        // blocks the entire endpoint for 30+ seconds when queried sequentially.
        const int PerProviderTimeoutSeconds = 8;
        var tasks = enabledProviders
            .Select(provider => QueryProviderStatusAsync(provider, PerProviderTimeoutSeconds, cancellationToken))
            .ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        int totalCapacity = 0;
        int totalActiveConnections = 0;
        var providerDetails = new List<object>(results.Length);

        foreach (var result in results)
        {
            totalCapacity += result.MaxConnections;
            totalActiveConnections += result.ProviderActiveConnections;
            providerDetails.Add(result);
        }

        // Compute effective max and available slots
        var effectiveMax = configuredMaxStreams > 0 ? Math.Min(configuredMaxStreams, totalCapacity) : totalCapacity;
        var availableSlots = Math.Max(0, effectiveMax - totalActiveConnections);

        // Compute warning level
        string? warningLevel = null;
        string? warningMessage = null;
        if (totalCapacity > 0)
        {
            var utilPct = (double)totalActiveConnections / totalCapacity * 100;
            if (totalActiveConnections >= totalCapacity)
            {
                warningLevel = "Critical";
                warningMessage = "All provider connections are in use. New streams will fail.";
            }
            else if (utilPct >= 80)
            {
                warningLevel = "Warning";
                warningMessage = $"Provider capacity is {utilPct:F0}% utilized. Consider reducing active streams.";
            }
        }

        return Ok(
            new
            {
                TotalProviderCapacity = totalCapacity,
                TotalProviderActiveConnections = totalActiveConnections,
                PluginActiveStreams = pluginActiveStreams,
                ConfiguredMaxStreams = configuredMaxStreams,
                AvailableSlots = availableSlots,
                WarningLevel = warningLevel,
                WarningMessage = warningMessage,
                Providers = providerDetails,
            }
        );
    }

    // =========================================================================
    // Provider Health (from active streams)
    // =========================================================================

    /// <summary>
    /// Get health state for all providers in active streams.
    /// </summary>
    /// <returns>Health snapshots from native streamers.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("ProviderHealth")]
    public ActionResult<object> GetProviderHealth()
    {
        var snapshots = Restream.GetActiveStreamSnapshots();
        var healthData = new List<object>();

        foreach (var snapshot in snapshots)
        {
            healthData.Add(
                new
                {
                    snapshot.StreamId,
                    snapshot.ChannelName,
                    snapshot.Status,
                    snapshot.ReconnectionCount,
                    snapshot.TotalBytesWritten,
                    snapshot.OverflowCount,
                    snapshot.GapPercentage,
                    snapshot.HasQualityIssues,
                    snapshot.QualityLevel,
                    snapshot.AvDriftMs,
                    snapshot.SyncStatus,
                }
            );
        }

        return Ok(
            new
            {
                success = true,
                activeStreams = snapshots.Count,
                providers = healthData,
            }
        );
    }

    // =========================================================================
    // Registry-based Provider Health & Channel Endpoints
    // =========================================================================

    /// <summary>
    /// Get health status for all providers from the native channel registry.
    /// Returns circuit breaker state, EWMA latency, success rate, and quarantine info.
    /// </summary>
    /// <param name="registryService">The channel registry service.</param>
    /// <returns>Provider health status from the native registry.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Providers/Status")]
    public ActionResult<object> GetProviderStatus([FromServices] ChannelRegistryService registryService)
    {
        var registry = registryService.Registry;
        if (registry == null || !registry.IsBuilt)
        {
            return Ok(
                new
                {
                    success = false,
                    message = "Channel registry not yet built",
                    providers = Array.Empty<object>(),
                }
            );
        }

        var statuses = registry.GetProviderStatus();
        return Ok(
            new
            {
                success = true,
                providerCount = statuses.Count,
                providers = statuses.Select(s => new
                {
                    id = s.Id,
                    name = s.Name,
                    healthScore = s.HealthScore,
                    state = s.State.ToString(),
                    circuitBreaker = s.CircuitBreaker.ToString(),
                    quarantineUntil = s.QuarantineUntil,
                    channelCount = s.ChannelCount,
                    consecutiveFailures = s.ConsecutiveFailures,
                    latencyEwmaMs = s.LatencyEwmaMs,
                    successRate = s.SuccessRate,
                    isEjected = s.IsEjected,
                    isInProbation = s.IsInProbation,
                    isHealthy = s.IsHealthy,
                }),
            }
        );
    }

    /// <summary>
    /// Get the number of deduplicated channels from the native registry.
    /// </summary>
    /// <param name="registryService">The channel registry service.</param>
    /// <returns>Channel count.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Channels/Count")]
    public ActionResult<object> GetChannelCount([FromServices] ChannelRegistryService registryService)
    {
        var registry = registryService.Registry;
        if (registry == null || !registry.IsBuilt)
        {
            return Ok(
                new
                {
                    success = false,
                    message = "Channel registry not yet built",
                    count = 0,
                }
            );
        }

        var stats = registry.GetStats();
        return Ok(
            new
            {
                success = true,
                count = stats.ChannelCount,
                streamCount = stats.StreamCount,
                providerCount = stats.ProviderCount,
                deduplicationRatio = stats.DeduplicationRatio,
                skippedCount = stats.SkippedCount,
            }
        );
    }

    /// <summary>
    /// Get a paginated list of channels from the native registry.
    /// </summary>
    /// <param name="registryService">The channel registry service.</param>
    /// <param name="offset">Number of channels to skip (default 0).</param>
    /// <param name="limit">Maximum channels to return (default 50, max 200).</param>
    /// <returns>Paginated channel list.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Channels/List")]
    public ActionResult<object> GetChannelList(
        [FromServices] ChannelRegistryService registryService,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50
    )
    {
        var registry = registryService.Registry;
        if (registry == null || !registry.IsBuilt)
        {
            return Ok(
                new
                {
                    success = false,
                    message = "Channel registry not yet built",
                    channels = Array.Empty<object>(),
                    total = 0,
                }
            );
        }

        limit = Math.Clamp(limit, 1, 200);
        offset = Math.Max(0, offset);

        var total = registry.GetChannelCount();
        var channels = registry.EnumerateChannels(offset, limit);

        return Ok(
            new
            {
                success = true,
                total,
                offset,
                limit,
                count = channels.Count,
                channels = channels.Select(c => new
                {
                    guid = c.Guid,
                    displayName = c.DisplayName,
                    iconUrl = c.IconUrl,
                    providerCount = c.ProviderCount,
                    bestQualityScore = c.BestQualityScore,
                }),
            }
        );
    }

    // =========================================================================
    // Channel Override CRUD
    // =========================================================================

    /// <summary>
    /// Get a channel override for a provider.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="streamId">The stream ID.</param>
    /// <returns>The channel override or 404.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Providers/{providerId}/Channels/{streamId}/Override")]
    public ActionResult<object> GetChannelOverride(string providerId, int streamId)
    {
        var provider = Plugin.Instance.Configuration.GetProvider(providerId);
        if (provider == null)
        {
            return NotFound(
                XtreamControllerHelpers.CreateError(
                    ErrorCodes.ProviderNotFound,
                    "Provider not found",
                    "List providers via GET /Xtream/Providers"
                )
            );
        }

        if (!provider.LiveTvOverrides.TryGetValue(streamId, out var channelOverride))
        {
            return NotFound(
                XtreamControllerHelpers.CreateError(ErrorCodes.OverrideNotFound, "No override for this channel")
            );
        }

        return Ok(
            new
            {
                streamId,
                channelOverride.Number,
                channelOverride.Name,
                channelOverride.LogoUrl,
            }
        );
    }

    /// <summary>
    /// Set or update a channel override.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="streamId">The stream ID.</param>
    /// <param name="request">The override values.</param>
    /// <returns>Result indicating success.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPut("Providers/{providerId}/Channels/{streamId}/Override")]
    public ActionResult<object> SetChannelOverride(
        string providerId,
        int streamId,
        [FromBody] ChannelOverrideRequest request
    )
    {
        var provider = Plugin.Instance.Configuration.GetProvider(providerId);
        if (provider == null)
        {
            return NotFound(
                XtreamControllerHelpers.CreateError(
                    ErrorCodes.ProviderNotFound,
                    "Provider not found",
                    "List providers via GET /Xtream/Providers"
                )
            );
        }

        provider.LiveTvOverrides[streamId] = new ChannelOverrides
        {
            Number = request.Number,
            Name = request.Name,
            LogoUrl = request.LogoUrl,
        };

        Plugin.Instance.SaveConfiguration();

        _logger.PluginLogInformation(
            "Set channel override for stream {StreamId} on provider {ProviderId}",
            streamId,
            providerId
        );

        return Ok(new { success = true, message = "Channel override set" });
    }

    /// <summary>
    /// Delete a channel override.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="streamId">The stream ID.</param>
    /// <returns>Result indicating success.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpDelete("Providers/{providerId}/Channels/{streamId}/Override")]
    public ActionResult<object> DeleteChannelOverride(string providerId, int streamId)
    {
        var provider = Plugin.Instance.Configuration.GetProvider(providerId);
        if (provider == null)
        {
            return NotFound(
                XtreamControllerHelpers.CreateError(
                    ErrorCodes.ProviderNotFound,
                    "Provider not found",
                    "List providers via GET /Xtream/Providers"
                )
            );
        }

        if (!provider.LiveTvOverrides.Remove(streamId))
        {
            return NotFound(
                XtreamControllerHelpers.CreateError(ErrorCodes.OverrideNotFound, "No override for this channel")
            );
        }

        Plugin.Instance.SaveConfiguration();

        _logger.PluginLogInformation(
            "Deleted channel override for stream {StreamId} on provider {ProviderId}",
            streamId,
            providerId
        );

        return Ok(new { success = true, message = "Channel override removed" });
    }

    // =========================================================================
    // Batch Channel Override Operations
    // =========================================================================

    /// <summary>
    /// List all channel overrides for a provider.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>All overrides for the provider.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Providers/{providerId}/Channels/Overrides")]
    public ActionResult<object> GetAllChannelOverrides(string providerId)
    {
        var provider = XtreamControllerHelpers.GetProvider(providerId);
        if (provider == null)
        {
            return NotFound(
                XtreamControllerHelpers.CreateError(
                    ErrorCodes.ProviderNotFound,
                    "Provider not found",
                    "List providers via GET /Xtream/Providers"
                )
            );
        }

        var overrides = provider.LiveTvOverrides.Select(kvp => new
        {
            streamId = kvp.Key,
            number = kvp.Value.Number,
            name = kvp.Value.Name,
            logoUrl = kvp.Value.LogoUrl,
        });

        return Ok(
            new
            {
                providerId,
                count = provider.LiveTvOverrides.Count,
                overrides,
            }
        );
    }

    /// <summary>
    /// Set multiple channel overrides in a single request.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="request">Batch override request.</param>
    /// <returns>Result with count of applied overrides.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("Providers/{providerId}/Channels/Overrides/Batch")]
    public ActionResult<object> BatchSetChannelOverrides(
        string providerId,
        [FromBody] BatchChannelOverrideRequest request
    )
    {
        var provider = XtreamControllerHelpers.GetProvider(providerId);
        if (provider == null)
        {
            return NotFound(
                XtreamControllerHelpers.CreateError(
                    ErrorCodes.ProviderNotFound,
                    "Provider not found",
                    "List providers via GET /Xtream/Providers"
                )
            );
        }

        var applied = 0;
        foreach (var entry in request.Overrides)
        {
            provider.LiveTvOverrides[entry.StreamId] = new ChannelOverrides
            {
                Number = entry.Number ?? 0,
                Name = entry.Name ?? string.Empty,
                LogoUrl = entry.LogoUrl ?? string.Empty,
            };
            applied++;
        }

        Plugin.Instance.SaveConfiguration();
        _logger.PluginLogInformation(
            "Batch set {Count} channel overrides for provider {ProviderId}",
            applied,
            providerId
        );
        return Ok(
            new
            {
                success = true,
                applied,
                message = $"Applied {applied} override(s)",
            }
        );
    }

    /// <summary>
    /// Clear all channel overrides for a provider.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>Result with count of cleared overrides.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpDelete("Providers/{providerId}/Channels/Overrides")]
    public ActionResult<object> ClearAllChannelOverrides(string providerId)
    {
        var provider = XtreamControllerHelpers.GetProvider(providerId);
        if (provider == null)
        {
            return NotFound(
                XtreamControllerHelpers.CreateError(
                    ErrorCodes.ProviderNotFound,
                    "Provider not found",
                    "List providers via GET /Xtream/Providers"
                )
            );
        }

        var count = provider.LiveTvOverrides.Count;
        provider.LiveTvOverrides.Clear();

        Plugin.Instance.SaveConfiguration();
        _logger.PluginLogInformation("Cleared {Count} channel overrides for provider {ProviderId}", count, providerId);
        return Ok(
            new
            {
                success = true,
                cleared = count,
                message = $"Cleared {count} override(s)",
            }
        );
    }

    // =========================================================================
    // Private Helpers
    // =========================================================================

    private record ProviderStatusResult(
        string ProviderId,
        string ProviderName,
        bool IsOnline,
        int MaxConnections,
        int ProviderActiveConnections,
        bool IsTrial,
        DateTime? ExpirationDate,
        string? ErrorMessage
    );

    private async Task<ProviderStatusResult> QueryProviderStatusAsync(
        XtreamProvider provider,
        int timeoutSeconds,
        CancellationToken cancellationToken
    )
    {
        try
        {
            // Short-circuit the retry handler with a per-provider timeout so a single
            // unreachable provider doesn't delay the entire dashboard response.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            using var client = new XtreamClient(_httpClientFactory, _loggerFactory.CreateLogger<XtreamClient>());
            var playerApi = await client
                .GetUserAndServerInfoAsync(provider.ToConnectionInfo(), cts.Token)
                .ConfigureAwait(false);

            if (playerApi?.UserInfo != null)
            {
                var userInfo = playerApi.UserInfo;
                return new ProviderStatusResult(
                    provider.Id,
                    provider.Name,
                    true,
                    userInfo.MaxConnections,
                    userInfo.ActiveCons,
                    userInfo.IsTrial,
                    userInfo.ExpDate,
                    null
                );
            }

            return new ProviderStatusResult(
                provider.Id,
                provider.Name,
                false,
                0,
                0,
                false,
                null,
                "Failed to get user info"
            );
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebugIfEnabled("Status check timed out for provider {ProviderId}", provider.Id);
            return new ProviderStatusResult(
                provider.Id,
                provider.Name,
                false,
                0,
                0,
                false,
                null,
                "Connection timed out"
            );
        }
        catch (Exception ex)
        {
            _logger.PluginLogError(ex, "Failed to get connection status for provider {ProviderId}", provider.Id);
            return new ProviderStatusResult(
                provider.Id,
                provider.Name,
                false,
                0,
                0,
                false,
                null,
                "Failed to connect to provider"
            );
        }
    }
}
