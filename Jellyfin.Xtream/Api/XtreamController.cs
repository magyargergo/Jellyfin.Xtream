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
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Api.Models;
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service;
using Jellyfin.Xtream.Service.Discovery;
using Jellyfin.Xtream.Service.Epg;
using Jellyfin.Xtream.Service.Logging;
using Jellyfin.Xtream.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Api;

/// <summary>
/// The Jellyfin Xtream configuration API.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="XtreamController"/> class.
/// </remarks>
/// <param name="logger">The logger instance.</param>
/// <param name="loggerFactory">The logger factory instance.</param>
/// <param name="cache">The memory cache instance.</param>
/// <param name="httpClientFactory">The HTTP client factory.</param>
[ApiController]
[Route("[controller]")]
[Produces("application/json")]
public class XtreamController(
    ILogger<XtreamController> logger,
    ILoggerFactory loggerFactory,
    IMemoryCache cache,
    IHttpClientFactory httpClientFactory
) : ControllerBase
{
    private const int CacheMinutes = 5;

    private readonly ILogger<XtreamController> _logger = logger;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly IMemoryCache _cache = cache;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;

    private static XtreamProvider? GetProvider(string? providerId)
    {
        var config = Plugin.Instance.Configuration;
        return string.IsNullOrEmpty(providerId)
            ? config.GetEnabledProviders().FirstOrDefault()
            : config.GetProvider(providerId);
    }

    private static CategoryResponse CreateCategoryResponse(Category category) =>
        new() { Id = category.CategoryId, Name = category.CategoryName };

    private static ItemResponse CreateItemResponse(StreamInfo stream)
    {
        return new ItemResponse
        {
            Id = stream.StreamId,
            Name = stream.Name,
            HasCatchup = stream.TvArchive,
            CatchupDuration = stream.TvArchiveDuration,
        };
    }

    private static ItemResponse CreateItemResponse(Series series)
    {
        return new ItemResponse
        {
            Id = series.SeriesId,
            Name = series.Name,
            HasCatchup = false,
            CatchupDuration = 0,
        };
    }

    private static ChannelResponse CreateChannelResponse(StreamInfo stream)
    {
        return new ChannelResponse
        {
            Id = stream.StreamId,
            LogoUrl = stream.StreamIcon,
            Name = stream.Name,
            Number = stream.Num,
        };
    }

    /// <summary>
    /// Get all Live TV categories for a provider.
    /// </summary>
    /// <param name="providerId">Optional provider ID. If not specified, uses first enabled provider.</param>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>An enumerable containing the categories.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("LiveCategories")]
    public async Task<ActionResult<IEnumerable<CategoryResponse>>> GetLiveCategories(
        [FromQuery] string? providerId,
        CancellationToken cancellationToken
    )
    {
        var provider = GetProvider(providerId);
        if (provider == null)
        {
            return BadRequest(new { message = "No provider configured" });
        }

        var cacheKey = "xtream-api-live-categories-" + provider.Id;
        if (_cache.TryGetValue<List<CategoryResponse>>(cacheKey, out var cached) && cached != null)
        {
            return Ok(cached);
        }

        using var client = new XtreamClient(_httpClientFactory, _loggerFactory.CreateLogger<XtreamClient>());
        List<CategoryResponse> result =
        [
            .. (
                await client.GetLiveCategoryAsync(provider.ToConnectionInfo(), cancellationToken).ConfigureAwait(false)
            ).Select(CreateCategoryResponse),
        ];

        _ = _cache.Set(cacheKey, result, TimeSpan.FromMinutes(CacheMinutes));
        return Ok(result);
    }

    /// <summary>
    /// Get all Live TV streams for the given category.
    /// </summary>
    /// <param name="categoryId">The category for which to fetch the streams.</param>
    /// <param name="providerId">Optional provider ID. If not specified, uses first enabled provider.</param>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>An enumerable containing the streams.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("LiveCategories/{categoryId}")]
    public async Task<ActionResult<IEnumerable<StreamInfo>>> GetLiveStreams(
        int categoryId,
        [FromQuery] string? providerId,
        CancellationToken cancellationToken
    )
    {
        var provider = GetProvider(providerId);
        if (provider == null)
        {
            return BadRequest(new { message = "No provider configured" });
        }

        var cacheKey = $"xtream-api-live-streams-{provider.Id}-{categoryId}";
        if (_cache.TryGetValue<List<ItemResponse>>(cacheKey, out var cached) && cached != null)
        {
            return Ok(cached);
        }

        using var client = new XtreamClient(_httpClientFactory, _loggerFactory.CreateLogger<XtreamClient>());
        List<ItemResponse> result =
        [
            .. (
                await client
                    .GetLiveStreamsByCategoryAsync(provider.ToConnectionInfo(), categoryId, cancellationToken)
                    .ConfigureAwait(false)
            ).Select(CreateItemResponse),
        ];

        _ = _cache.Set(cacheKey, result, TimeSpan.FromMinutes(CacheMinutes));
        return Ok(result);
    }

    /// <summary>
    /// Get all VOD categories for a provider.
    /// </summary>
    /// <param name="providerId">Optional provider ID. If not specified, uses first enabled provider.</param>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>An enumerable containing the categories.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("VodCategories")]
    public async Task<ActionResult<IEnumerable<CategoryResponse>>> GetVodCategories(
        [FromQuery] string? providerId,
        CancellationToken cancellationToken
    )
    {
        var provider = GetProvider(providerId);
        if (provider == null)
        {
            return BadRequest(new { message = "No provider configured" });
        }

        var cacheKey = "xtream-api-vod-categories-" + provider.Id;
        if (_cache.TryGetValue<List<CategoryResponse>>(cacheKey, out var cached) && cached != null)
        {
            return Ok(cached);
        }

        using var client = new XtreamClient(_httpClientFactory, _loggerFactory.CreateLogger<XtreamClient>());
        List<CategoryResponse> result =
        [
            .. (
                await client.GetVodCategoryAsync(provider.ToConnectionInfo(), cancellationToken).ConfigureAwait(false)
            ).Select(CreateCategoryResponse),
        ];

        _ = _cache.Set(cacheKey, result, TimeSpan.FromMinutes(CacheMinutes));
        return Ok(result);
    }

    /// <summary>
    /// Get all VOD streams for the given category.
    /// </summary>
    /// <param name="categoryId">The category for which to fetch the streams.</param>
    /// <param name="providerId">Optional provider ID. If not specified, uses first enabled provider.</param>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>An enumerable containing the streams.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("VodCategories/{categoryId}")]
    public async Task<ActionResult<IEnumerable<StreamInfo>>> GetVodStreams(
        int categoryId,
        [FromQuery] string? providerId,
        CancellationToken cancellationToken
    )
    {
        var provider = GetProvider(providerId);
        if (provider == null)
        {
            return BadRequest(new { message = "No provider configured" });
        }

        var cacheKey = $"xtream-api-vod-streams-{provider.Id}-{categoryId}";
        if (_cache.TryGetValue<List<ItemResponse>>(cacheKey, out var cached) && cached != null)
        {
            return Ok(cached);
        }

        using var client = new XtreamClient(_httpClientFactory, _loggerFactory.CreateLogger<XtreamClient>());
        List<ItemResponse> result =
        [
            .. (
                await client
                    .GetVodStreamsByCategoryAsync(provider.ToConnectionInfo(), categoryId, cancellationToken)
                    .ConfigureAwait(false)
            ).Select(CreateItemResponse),
        ];

        _ = _cache.Set(cacheKey, result, TimeSpan.FromMinutes(CacheMinutes));
        return Ok(result);
    }

    /// <summary>
    /// Get all Series categories for a provider.
    /// </summary>
    /// <param name="providerId">Optional provider ID. If not specified, uses first enabled provider.</param>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>An enumerable containing the categories.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("SeriesCategories")]
    public async Task<ActionResult<IEnumerable<CategoryResponse>>> GetSeriesCategories(
        [FromQuery] string? providerId,
        CancellationToken cancellationToken
    )
    {
        var provider = GetProvider(providerId);
        if (provider == null)
        {
            return BadRequest(new { message = "No provider configured" });
        }

        var cacheKey = "xtream-api-series-categories-" + provider.Id;
        if (_cache.TryGetValue<List<CategoryResponse>>(cacheKey, out var cached) && cached != null)
        {
            return Ok(cached);
        }

        using var client = new XtreamClient(_httpClientFactory, _loggerFactory.CreateLogger<XtreamClient>());
        List<CategoryResponse> result =
        [
            .. (
                await client
                    .GetSeriesCategoryAsync(provider.ToConnectionInfo(), cancellationToken)
                    .ConfigureAwait(false)
            ).Select(CreateCategoryResponse),
        ];

        _ = _cache.Set(cacheKey, result, TimeSpan.FromMinutes(CacheMinutes));
        return Ok(result);
    }

    /// <summary>
    /// Get all Series streams for the given category.
    /// </summary>
    /// <param name="categoryId">The category for which to fetch the streams.</param>
    /// <param name="providerId">Optional provider ID. If not specified, uses first enabled provider.</param>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>An enumerable containing the streams.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("SeriesCategories/{categoryId}")]
    public async Task<ActionResult<IEnumerable<StreamInfo>>> GetSeriesStreams(
        int categoryId,
        [FromQuery] string? providerId,
        CancellationToken cancellationToken
    )
    {
        var provider = GetProvider(providerId);
        if (provider == null)
        {
            return BadRequest(new { message = "No provider configured" });
        }

        var cacheKey = $"xtream-api-series-streams-{provider.Id}-{categoryId}";
        if (_cache.TryGetValue<List<ItemResponse>>(cacheKey, out var cached) && cached != null)
        {
            return Ok(cached);
        }

        using var client = new XtreamClient(_httpClientFactory, _loggerFactory.CreateLogger<XtreamClient>());
        List<ItemResponse> result =
        [
            .. (
                await client
                    .GetSeriesByCategoryAsync(provider.ToConnectionInfo(), categoryId, cancellationToken)
                    .ConfigureAwait(false)
            ).Select(CreateItemResponse),
        ];

        _ = _cache.Set(cacheKey, result, TimeSpan.FromMinutes(CacheMinutes));
        return Ok(result);
    }

    /// <summary>
    /// Get all configured TV channels for a provider.
    /// </summary>
    /// <param name="providerId">Optional provider ID. If not specified, uses first enabled provider.</param>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>An enumerable containing the streams.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("LiveTv")]
    public async Task<ActionResult<IEnumerable<StreamInfo>>> GetLiveTvChannels(
        [FromQuery] string? providerId,
        CancellationToken cancellationToken
    )
    {
        var provider = GetProvider(providerId);
        if (provider == null)
        {
            return BadRequest(new { message = "No provider configured" });
        }

        List<ChannelResponse> channels =
        [
            .. (
                await StreamService
                    .GetLiveStreamsWithOverridesForProvider(provider, cancellationToken)
                    .ConfigureAwait(false)
            ).Select(CreateChannelResponse),
        ];

        return Ok(channels);
    }

    /// <summary>
    /// Test a provider connection.
    /// </summary>
    /// <param name="providerId">The provider ID to test.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Connection info if successful.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("TestProvider/{providerId}")]
    public async Task<ActionResult<object>> TestProvider(string providerId, CancellationToken cancellationToken)
    {
        var provider = GetProvider(providerId);
        if (provider == null)
        {
            return NotFound(new { success = false, message = "Provider not found" });
        }

        try
        {
            using var client = new XtreamClient(_httpClientFactory, _loggerFactory.CreateLogger<XtreamClient>());
            var playerApi = await client
                .GetUserAndServerInfoAsync(provider.ToConnectionInfo(), cancellationToken)
                .ConfigureAwait(false);

            if (playerApi?.UserInfo == null)
            {
                return Ok(new { success = false, message = "Failed to get user info from provider" });
            }

            var userInfo = playerApi.UserInfo;
            return Ok(
                new
                {
                    success = true,
                    status = userInfo.Status,
                    maxConnections = userInfo.MaxConnections,
                    activeConnections = userInfo.ActiveCons,
                    expDate = userInfo.ExpDate,
                    isTrial = userInfo.IsTrial,
                    username = userInfo.Username,
                }
            );
        }
        catch (HttpRequestException ex)
        {
            _logger.PluginLogError(ex, "Failed to test provider {ProviderId}", providerId);
            return Ok(new { success = false, message = "Connection failed: " + ex.Message });
        }
        catch (Exception ex)
        {
            _logger.PluginLogError(ex, "Unexpected error testing provider {ProviderId}", providerId);
            return Ok(new { success = false, message = ex.Message });
        }
    }

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
            return NotFound(new { success = false, message = "Provider not found" });
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
            return BadRequest(new { success = false, message = "BaseUrl and Username are required" });
        }

        var config = Plugin.Instance.Configuration;

        var newProvider = new Configuration.XtreamProvider
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
            return NotFound(new { success = false, message = "Provider not found" });
        }

        provider.Name = request.Name;
        provider.Enabled = request.Enabled;
        provider.Priority = Math.Clamp(request.Priority, 0, 100);

        if (!string.IsNullOrWhiteSpace(request.BaseUrl))
        {
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
            return NotFound(new { success = false, message = "Provider not found" });
        }

        var enabledCount = config.Providers.Count(p => p.Enabled);
        if (provider.Enabled && enabledCount <= 1)
        {
            return BadRequest(new { success = false, message = "Cannot delete the last enabled provider" });
        }

        config.Providers.Remove(provider);
        Plugin.Instance.SaveConfiguration();

        _logger.PluginLogWarning("Deleted provider: {Name} ({Id})", provider.Name, providerId);

        return Ok(new { success = true, message = "Provider deleted" });
    }

    /// <summary>
    /// Check if legacy configuration exists that can be migrated.
    /// </summary>
    /// <returns>Status of legacy configuration.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("LegacyConfig")]
    public ActionResult<object> GetLegacyConfigStatus()
    {
        var config = Plugin.Instance.Configuration;
        var hasLegacyCredentials = !string.IsNullOrEmpty(config.BaseUrl) && config.BaseUrl != "https://example.com";
        var hasLegacyChannels = config.LiveTv.Count > 0 || config.Vod.Count > 0 || config.Series.Count > 0;
        var hasLegacyConfig = hasLegacyCredentials || hasLegacyChannels;

        _logger.PluginLogInformation(
            "Legacy config check: BaseUrl={BaseUrl}, Username={Username}, LiveTv={LiveTvCount}, Vod={VodCount}, Series={SeriesCount}",
            config.BaseUrl,
            config.Username,
            config.LiveTv.Count,
            config.Vod.Count,
            config.Series.Count
        );

        return Ok(
            new
            {
                hasLegacyConfig,
                hasLegacyCredentials,
                hasLegacyChannels,
                baseUrl = config.BaseUrl,
                username = config.Username,
                hasLiveTv = config.LiveTv.Count > 0,
                hasVod = config.Vod.Count > 0,
                hasSeries = config.Series.Count > 0,
                liveTvCount = config.LiveTv.Count,
                vodCount = config.Vod.Count,
                seriesCount = config.Series.Count,
            }
        );
    }

    /// <summary>
    /// Migrate legacy configuration to a new provider.
    /// </summary>
    /// <returns>Result of migration.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("MigrateLegacy")]
    public ActionResult<object> MigrateLegacyConfig()
    {
        var config = Plugin.Instance.Configuration;

        if (string.IsNullOrEmpty(config.BaseUrl) || config.BaseUrl == "https://example.com")
        {
            return Ok(new { success = false, message = "No legacy configuration found to migrate" });
        }

        var migratedProvider = new XtreamProvider
        {
            Id = "migrated-" + DateTime.UtcNow.Ticks.ToString("x", CultureInfo.InvariantCulture)[..8],
            Name = "Migrated Provider",
            BaseUrl = config.BaseUrl,
            Username = config.Username,
            Password = config.Password,
            Enabled = true,
            LiveTv = config.LiveTv,
            Vod = config.Vod,
            Series = config.Series,
            LiveTvOverrides = config.LiveTvOverrides,
        };

        config.Providers.Add(migratedProvider);

        config.BaseUrl = string.Empty;
        config.Username = string.Empty;
        config.Password = string.Empty;
        config.LiveTv.Clear();
        config.Vod.Clear();
        config.Series.Clear();
        config.LiveTvOverrides.Clear();

        Plugin.Instance.SaveConfiguration();

        _logger.PluginLogInformation("Legacy configuration migrated to provider {ProviderId}", migratedProvider.Id);

        return Ok(
            new
            {
                success = true,
                message = "Legacy configuration migrated successfully",
                providerId = migratedProvider.Id,
                providerName = migratedProvider.Name,
            }
        );
    }

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
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            return BadRequest(new { success = false, message = "Webhook URL is required" });
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
                    message = "Failed to send diagnostics: " + ex.Message,
                }
            );
        }
    }

    /// <summary>
    /// Test EPG data retrieval for a specific channel.
    /// </summary>
    /// <param name="streamId">The stream ID to get EPG for.</param>
    /// <param name="epgProvider">The EPG provider service.</param>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>EPG test response with programs.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("EpgTest/{streamId}")]
    public async Task<ActionResult<EpgTestResponse>> GetEpgTest(
        int streamId,
        [FromServices] IEpgProvider epgProvider,
        CancellationToken cancellationToken
    )
    {
        var response = new EpgTestResponse { StreamId = streamId, Provider = epgProvider.Name };

        try
        {
            response.ChannelName =
                (await StreamService.GetAllLiveStreams(cancellationToken).ConfigureAwait(false))
                    .FirstOrDefault(s => s.Stream.StreamId == streamId)
                    ?.Stream.Name
                ?? $"Stream {streamId}";

            response.Programs =
            [
                .. (await epgProvider.GetProgramsAsync(streamId, cancellationToken).ConfigureAwait(false)).Select(
                    p => new EpgProgramResponse
                    {
                        Id = p.Id,
                        Title = p.Title,
                        Description = p.Description,
                        StartUtc = p.StartUtc,
                        EndUtc = p.EndUtc,
                        ImageUrl = p.ImageUrl,
                    }
                ),
            ];

            response.Success = true;
            _logger.PluginLogInformation(
                "EPG test for stream {StreamId}: {Count} programs from {Provider}",
                streamId,
                response.Programs.Count,
                epgProvider.Name
            );
        }
        catch (Exception ex)
        {
            _logger.PluginLogError(ex, "EPG test failed for stream {StreamId}", streamId);
            response.Success = false;
            response.ErrorMessage = ex.Message;
        }

        return Ok(response);
    }

    /// <summary>
    /// Get EPG provider status information.
    /// </summary>
    /// <param name="epgProvider">The EPG provider service.</param>
    /// <returns>EPG provider status.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("EpgStatus")]
    public ActionResult<object> GetEpgStatus([FromServices] IEpgProvider epgProvider)
    {
        return Ok(
            new
            {
                providerName = epgProvider.Name,
                isAvailable = epgProvider.IsAvailable,
                priority = epgProvider.Priority,
            }
        );
    }

    /// <summary>
    /// Refresh EPG data for all channels in parallel.
    /// This pre-warms the EPG cache for faster guide loading.
    /// </summary>
    /// <param name="liveTvService">The Live TV service.</param>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>Result with success count and total channels.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("RefreshEpg")]
    public async Task<ActionResult<object>> RefreshEpgParallel(
        [FromServices] LiveTvService liveTvService,
        CancellationToken cancellationToken
    )
    {
        _logger.PluginLogInformation("Starting parallel EPG refresh via API");

        try
        {
            var (successCount, totalCount) = await liveTvService
                .RefreshAllEpgParallelAsync(cancellationToken)
                .ConfigureAwait(false);
            return Ok(
                new
                {
                    success = true,
                    successCount,
                    totalCount,
                    message = $"EPG refresh complete: {successCount}/{totalCount} channels with EPG data",
                }
            );
        }
        catch (OperationCanceledException)
        {
            return Ok(
                new
                {
                    success = false,
                    successCount = 0,
                    totalCount = 0,
                    message = "EPG refresh was cancelled",
                }
            );
        }
        catch (Exception ex)
        {
            _logger.PluginLogError(ex, "EPG refresh failed");
            return Ok(
                new
                {
                    success = false,
                    successCount = 0,
                    totalCount = 0,
                    message = "EPG refresh failed: " + ex.Message,
                }
            );
        }
    }

    /// <summary>
    /// Get all active streams with their health statistics.
    /// </summary>
    /// <returns>List of active streams with statistics.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("ActiveStreams")]
    public ActionResult<IReadOnlyList<StreamInfoSnapshot>> GetActiveStreams()
    {
        var streams = Restream.GetActiveStreamSnapshots();
        _logger.PluginLogInformation("Retrieved {Count} active stream(s)", streams.Count);
        return Ok(streams);
    }

    /// <summary>
    /// Kill (terminate) a specific stream by its ID.
    /// </summary>
    /// <param name="streamId">The stream ID to kill.</param>
    /// <returns>Result indicating success or failure.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpDelete("ActiveStreams/{streamId}")]
    public ActionResult<object> KillStream(string streamId)
    {
        _logger.PluginLogWarning("Killing stream {StreamId} via API", streamId);

        if (Restream.KillStream(streamId))
        {
            _logger.PluginLogInformation("Successfully killed stream {StreamId}", streamId);
            return Ok(new { success = true, message = "Stream " + streamId + " killed successfully" });
        }

        _logger.PluginLogWarning("Stream {StreamId} not found", streamId);
        return NotFound(new { success = false, message = "Stream " + streamId + " not found" });
    }

    /// <summary>
    /// Kill all active streams.
    /// </summary>
    /// <returns>Result with the number of streams killed.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpDelete("ActiveStreams")]
    public ActionResult<object> KillAllStreams()
    {
        _logger.PluginLogWarning("Killing all active streams via API");
        var count = Restream.KillAllStreams();
        _logger.PluginLogInformation("Killed {Count} active stream(s)", count);
        return Ok(
            new
            {
                success = true,
                count,
                message = $"Killed {count} stream(s)",
            }
        );
    }

    // =========================================================================
    // Per-Stream Provider Health & Control Endpoints
    // =========================================================================

    /// <summary>
    /// Get all provider health snapshots for a specific stream.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <returns>List of provider health snapshots.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("ActiveStreams/{streamId}/Providers")]
    public ActionResult<object> GetStreamProviders(string streamId)
    {
        var providers = Restream.GetAllProviderHealth(streamId);
        if (providers == null)
        {
            return NotFound(new { message = "Stream " + streamId + " not found" });
        }

        return Ok(
            providers.Select(p => new
            {
                providerIndex = p.ProviderIndex,
                state = p.State.ToString(),
                successRate = p.SuccessRate,
                latencyEwmaMs = p.LatencyEwmaMs,
                activeRequests = p.ActiveRequests,
                isolatedTimes = p.IsolatedTimes,
                isolationDurationMs = p.IsolationDurationMs,
                isHealthy = p.IsHealthy,
                isEjected = p.IsEjected,
            })
        );
    }

    /// <summary>
    /// Get health snapshot for a specific provider in a stream.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <param name="providerIndex">The provider index (0-based).</param>
    /// <returns>Provider health snapshot.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("ActiveStreams/{streamId}/Providers/{providerIndex:int}/Health")]
    public ActionResult<object> GetStreamProviderHealth(string streamId, int providerIndex)
    {
        var health = Restream.GetProviderHealth(streamId, providerIndex);
        if (health == null)
        {
            return NotFound(new { message = "Stream or provider not found" });
        }

        var h = health.Value;
        return Ok(
            new
            {
                providerIndex = h.ProviderIndex,
                state = h.State.ToString(),
                successRate = h.SuccessRate,
                latencyEwmaMs = h.LatencyEwmaMs,
                activeRequests = h.ActiveRequests,
                isolatedTimes = h.IsolatedTimes,
                isolationDurationMs = h.IsolationDurationMs,
                isHealthy = h.IsHealthy,
                isEjected = h.IsEjected,
                isInProbation = h.IsInProbation,
            }
        );
    }

    /// <summary>
    /// Force a URL switch (reconnect) for a specific stream.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <returns>Result indicating success.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("ActiveStreams/{streamId}/ForceReconnect")]
    public ActionResult<object> ForceStreamReconnect(string streamId)
    {
        _logger.PluginLogWarning("Force reconnect requested for stream {StreamId}", streamId);

        if (Restream.RequestSwitch(streamId))
        {
            return Ok(new { success = true, message = "URL switch requested for stream " + streamId });
        }

        return NotFound(new { message = "Stream " + streamId + " not found" });
    }

    /// <summary>
    /// Force eject a provider from a stream's health system.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <param name="providerIndex">The provider index (0-based).</param>
    /// <param name="durationMs">Ejection duration in milliseconds (default 30000).</param>
    /// <returns>Result indicating success.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("ActiveStreams/{streamId}/Providers/{providerIndex:int}/Eject")]
    public ActionResult<object> EjectProvider(string streamId, int providerIndex, [FromQuery] int durationMs = 30000)
    {
        _logger.PluginLogWarning(
            "Ejecting provider {ProviderIndex} from stream {StreamId} for {Duration}ms",
            providerIndex,
            streamId,
            durationMs
        );

        if (Restream.ForceEjectProvider(streamId, providerIndex, durationMs))
        {
            return Ok(new { success = true, message = $"Provider {providerIndex} ejected for {durationMs}ms" });
        }

        return NotFound(new { message = "Stream " + streamId + " not found" });
    }

    /// <summary>
    /// Reset all providers in a stream to Active state.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <returns>Result indicating success.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("ActiveStreams/{streamId}/Providers/Reset")]
    public ActionResult<object> ResetStreamProviders(string streamId)
    {
        if (Restream.ResetAllProviders(streamId))
        {
            return Ok(new { success = true, message = "All providers reset to Active" });
        }

        return NotFound(new { message = "Stream " + streamId + " not found" });
    }

    /// <summary>
    /// Get detailed TR 101 290 quality metrics for a specific stream.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <returns>Detailed quality metrics including Priority 1, Priority 2, A/V sync, and PCR analysis.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("ActiveStreams/{streamId}/Metrics")]
    public ActionResult<object> GetStreamMetrics(string streamId)
    {
        var metrics = Restream.GetStreamMetrics(streamId);
        if (metrics == null)
        {
            return NotFound(new { message = "Stream " + streamId + " not found or no metrics available" });
        }

        var m = metrics;
        var avSync = Restream.GetStreamAvSync(streamId);
        var pcr = Restream.GetStreamPcrAnalysis(streamId);

        return Ok(
            new
            {
                timestamp = m.Timestamp,
                tsBitrate = m.TsBitrate,
                serviceCount = m.ServiceCount,
                pidCount = m.PidCount,
                priority1 = new
                {
                    syncByteError = m.Priority1.SyncByteError,
                    syncLoss = m.Priority1.SyncLoss,
                    patError = m.Priority1.PatError,
                    patError2 = m.Priority1.PatError2,
                    continuityCountError = m.Priority1.ContinuityCountError,
                    pmtError = m.Priority1.PmtError,
                    pmtError2 = m.Priority1.PmtError2,
                    pidError = m.Priority1.PidError,
                },
                priority2 = new
                {
                    transportError = m.Priority2.TransportError,
                    crcError = m.Priority2.CrcError,
                    pcrRepetitionError = m.Priority2.PcrRepetitionError,
                    pcrDiscontinuityError = m.Priority2.PcrDiscontinuityError,
                    pcrAccuracyError = m.Priority2.PcrAccuracyError,
                    ptsError = m.Priority2.PtsError,
                    catError = m.Priority2.CatError,
                },
                avSync = avSync != null
                    ? new
                    {
                        videoAudioDriftMs = avSync.Value.VideoAudioDriftMs,
                        driftRateMsPerSec = avSync.Value.DriftRateMsPerSec,
                        peakDriftMs = avSync.Value.PeakDriftMs,
                        avgDriftMs = avSync.Value.AvgDriftMs,
                        status = avSync.Value.Status.ToString(),
                        statusDescription = avSync.Value.StatusDescription,
                        isSynchronized = avSync.Value.IsSynchronized,
                    }
                    : (object?)null,
                pcr = pcr != null
                    ? new
                    {
                        pcrJitterUs = pcr.Value.PcrJitterUs,
                        pcrJitterMaxUs = pcr.Value.PcrJitterMaxUs,
                        pcrJitterAvgUs = pcr.Value.PcrJitterAvgUs,
                        pcrIntervalMs = pcr.Value.PcrIntervalMs,
                        pcrDriftPpm = pcr.Value.PcrDriftPpm,
                        pcrCount = pcr.Value.PcrCount,
                        pcrFrequencyOffsetPpm = pcr.Value.PcrFrequencyOffsetPpm,
                        pcrAccuracyNs = pcr.Value.PcrAccuracyNs,
                    }
                    : (object?)null,
            }
        );
    }

    // =========================================================================
    // Diagnostics Bundle
    // =========================================================================

    /// <summary>
    /// Get a comprehensive diagnostics bundle with all stream, provider, and configuration data.
    /// </summary>
    /// <returns>Complete system diagnostics for troubleshooting.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Diagnostics/Bundle")]
    public ActionResult<object> GetDiagnosticsBundle()
    {
        var config = Plugin.Instance.Configuration;
        var streams = Restream.GetActiveStreamSnapshots();

        // Collect per-stream provider health
        var streamDetails = new List<object>();
        foreach (var s in streams)
        {
            var providers = Restream.GetAllProviderHealth(s.StreamId);
            var metrics = Restream.GetStreamMetrics(s.StreamId);
            var avSync = Restream.GetStreamAvSync(s.StreamId);

            streamDetails.Add(
                new
                {
                    stream = s,
                    providers = providers?.Select(p => new
                    {
                        providerIndex = p.ProviderIndex,
                        state = p.State.ToString(),
                        successRate = p.SuccessRate,
                        latencyEwmaMs = p.LatencyEwmaMs,
                        isolatedTimes = p.IsolatedTimes,
                    }),
                    tsBitrate = metrics?.TsBitrate,
                    avSyncStatus = avSync?.Status.ToString(),
                    avDriftMs = avSync?.VideoAudioDriftMs,
                }
            );
        }

        return Ok(
            new
            {
                generatedAt = DateTime.UtcNow,
                activeStreamCount = streams.Count,
                configuration = new
                {
                    timeouts = new
                    {
                        connectTimeoutSeconds = config.StreamConnectTimeoutSeconds,
                        firstByteTimeoutSeconds = config.StreamFirstByteTimeoutSeconds,
                        responseHeadersTimeoutSeconds = config.StreamResponseHeadersTimeoutSeconds,
                        dataStallTimeoutSeconds = config.StreamDataStallTimeoutSeconds,
                        failoverBudgetSeconds = config.FailoverBudgetSeconds,
                        providerBlacklistSeconds = config.ProviderBlacklistSeconds,
                        maxFailoverAttempts = config.MaxFailoverAttempts,
                        dnsTimeoutSeconds = config.DnsTimeoutSeconds,
                        tcpKeepaliveEnabled = config.TcpKeepaliveEnabled,
                    },
                    health = new
                    {
                        enableP2C = config.EnableP2CLoadBalancing,
                        enableOutlierDetection = config.EnableOutlierDetection,
                        outlierStddevFactor = config.OutlierStddevFactor,
                        probationSuccessThreshold = config.ProbationSuccessThreshold,
                    },
                    buffer = new
                    {
                        underrunThresholdPercent = config.BufferUnderrunThresholdPercent,
                        nearFullThresholdPercent = config.BufferNearFullThresholdPercent,
                        underrunNotificationThreshold = config.BufferUnderrunNotificationThreshold,
                        consumerDisconnectGraceSeconds = config.ConsumerDisconnectGraceSeconds,
                    },
                },
                streams = streamDetails,
            }
        );
    }

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
            var provider = GetProvider(providerId);
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
            return Ok(new { success = false, message = ex.Message });
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
            var receiveTask = ReceiveWebSocketMessagesAsync(webSocket, cts);

            // Send progress updates with heartbeat
            var lastProgressTime = DateTime.UtcNow;
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
            return BadRequest(new { success = false, message = "Server and username are required" });
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

    /// <summary>
    /// Copy channel selections from one provider to another by matching channel names.
    /// </summary>
    /// <param name="request">The copy request with source and target provider IDs.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of the copy operation with matched channels.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("CopyChannelSelections")]
    public async Task<ActionResult<CopyChannelsResponse>> CopyChannelSelections(
        [FromBody] CopyChannelsRequest request,
        CancellationToken cancellationToken
    )
    {
        var config = Plugin.Instance.Configuration;
        var response = new CopyChannelsResponse();

        // Validate providers
        var sourceProvider = config.Providers.Find(p => p.Id == request.SourceProviderId);
        var targetProvider = config.Providers.Find(p => p.Id == request.TargetProviderId);

        if (sourceProvider == null)
        {
            response.Message = "Source provider not found";
            return BadRequest(response);
        }

        if (targetProvider == null)
        {
            response.Message = "Target provider not found";
            return BadRequest(response);
        }

        if (sourceProvider.Id == targetProvider.Id)
        {
            response.Message = "Source and target providers must be different";
            return BadRequest(response);
        }

        // Get source provider's selected streams
        var sourceSelections = sourceProvider.LiveTv ?? [];
        if (sourceSelections.Count == 0)
        {
            response.Message = "Source provider has no channel selections";
            return Ok(response);
        }

        // Count total selected streams in source
        var sourceSelectedStreamIds = sourceSelections.Values.SelectMany(s => s).ToHashSet();
        response.SourceSelectedCount = sourceSelectedStreamIds.Count;

        try
        {
            // Fetch all streams from both providers
            using var client = new XtreamClient(_httpClientFactory, _loggerFactory.CreateLogger<XtreamClient>());

            var sourceStreams = await client
                .GetLiveStreamsAsync(sourceProvider.ToConnectionInfo(), cancellationToken)
                .ConfigureAwait(false);

            var targetStreams = await client
                .GetLiveStreamsAsync(targetProvider.ToConnectionInfo(), cancellationToken)
                .ConfigureAwait(false);

            // Build index of target streams for efficient matching
            var channelMatcher = Service.ChannelMatching.ChannelMatcher.Default;
            var targetIndex = channelMatcher.BuildIndex(targetStreams);

            // Match source selections to target streams
            var targetSelections = targetProvider.LiveTv;
            var matchedChannels = new List<MatchedChannelInfo>();
            var unmatchedChannels = new List<string>();

            foreach (var sourceStream in sourceStreams.Where(s => sourceSelectedStreamIds.Contains(s.StreamId)))
            {
                var matchResult = channelMatcher.FindBestMatch(sourceStream, targetIndex);

                if (matchResult.MatchedStream != null)
                {
                    var targetStream = matchResult.MatchedStream;
                    var categoryId = targetStream.CategoryId ?? 0;

                    // Add to target selections (modify existing dictionary)
                    if (!targetSelections.TryGetValue(categoryId, out var categoryStreams))
                    {
                        categoryStreams = [];
                        targetSelections[categoryId] = categoryStreams;
                    }

                    _ = categoryStreams.Add(targetStream.StreamId);

                    matchedChannels.Add(
                        new MatchedChannelInfo
                        {
                            SourceName = sourceStream.Name ?? string.Empty,
                            TargetName = targetStream.Name ?? string.Empty,
                            TargetStreamId = targetStream.StreamId,
                            TargetCategoryId = categoryId,
                            NormalizedName = $"{matchResult.NormalizedName} ({matchResult.SimilarityScore}%)",
                        }
                    );
                }
                else
                {
                    // Track unmatched channels
                    var sourceName = sourceStream.Name ?? string.Empty;
                    unmatchedChannels.Add($"{sourceName} -> {matchResult.NormalizedName}");
                    _logger.LogDebugIfEnabled(
                        "Channel copy: No match for '{SourceName}' (normalized: '{NormalizedName}')",
                        sourceName,
                        matchResult.NormalizedName
                    );
                }
            }

            // Save the updated configuration (LiveTv was modified in-place)
            Plugin.Instance.SaveConfiguration();

            response.Success = true;
            response.MatchedCount = matchedChannels.Count;
            response.UnmatchedCount = unmatchedChannels.Count;
            response.MatchedChannels = matchedChannels;
            response.UnmatchedChannels = unmatchedChannels;
            response.Message =
                $"Matched {matchedChannels.Count} of {response.SourceSelectedCount} channels ({unmatchedChannels.Count} unmatched)";

            _logger.PluginLogInformation(
                "Copied channel selections from {SourceProvider} to {TargetProvider}: {Matched}/{Total} matched, {Unmatched} unmatched",
                sourceProvider.Name,
                targetProvider.Name,
                matchedChannels.Count,
                response.SourceSelectedCount,
                unmatchedChannels.Count
            );
        }
        catch (Exception ex)
        {
            _logger.PluginLogError(
                ex,
                "Failed to copy channel selections from {Source} to {Target}",
                sourceProvider.Name,
                targetProvider.Name
            );
            response.Message = $"Failed to copy channels: {ex.Message}";
            return StatusCode(500, response);
        }

        return Ok(response);
    }

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
            return NotFound(new { success = false, message = "Provider not found" });
        }

        if (!provider.LiveTvOverrides.TryGetValue(streamId, out var channelOverride))
        {
            return NotFound(new { success = false, message = "No override for this channel" });
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
            return NotFound(new { success = false, message = "Provider not found" });
        }

        provider.LiveTvOverrides[streamId] = new Configuration.ChannelOverrides
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
            return NotFound(new { success = false, message = "Provider not found" });
        }

        if (!provider.LiveTvOverrides.Remove(streamId))
        {
            return NotFound(new { success = false, message = "No override for this channel" });
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
    // Provider Health
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
    // Configuration Section Endpoints
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
        return Ok(new { success = true, message = "Proxy configuration updated" });
    }

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
        return Ok(new { success = true, message = "EPG configuration updated" });
    }

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
        return Ok(new { success = true, message = "Discord configuration updated" });
    }

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
        return Ok(new { success = true, message = "Timeout configuration updated" });
    }

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
        return Ok(new { success = true, message = "Health configuration updated" });
    }
}
