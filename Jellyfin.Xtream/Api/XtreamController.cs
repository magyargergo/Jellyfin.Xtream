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
[ApiController]
[Route("[controller]")]
[Produces("application/json")]
public class XtreamController : ControllerBase
{
    private const int CacheMinutes = 5;

    private readonly ILogger<XtreamController> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IMemoryCache _cache;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ProviderConnectionCache _connectionCache;

    /// <summary>
    /// Initializes a new instance of the <see cref="XtreamController"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="loggerFactory">The logger factory instance.</param>
    /// <param name="cache">The memory cache instance.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="connectionCache">The provider connection cache.</param>
    public XtreamController(
        ILogger<XtreamController> logger,
        ILoggerFactory loggerFactory,
        IMemoryCache cache,
        IHttpClientFactory httpClientFactory,
        ProviderConnectionCache connectionCache
    )
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _cache = cache;
        _httpClientFactory = httpClientFactory;
        _connectionCache = connectionCache;
    }

    private XtreamProvider? GetProvider(string? providerId)
    {
        PluginConfiguration config = Plugin.Instance.Configuration;
        if (string.IsNullOrEmpty(providerId))
        {
            return config.GetEnabledProviders().FirstOrDefault();
        }

        return config.GetProvider(providerId);
    }

    private static CategoryResponse CreateCategoryResponse(Category category)
    {
        return new CategoryResponse { Id = category.CategoryId, Name = category.CategoryName };
    }

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
        XtreamProvider? provider = GetProvider(providerId);
        if (provider == null)
        {
            return BadRequest(new { message = "No provider configured" });
        }

        string cacheKey = "xtream-api-live-categories-" + provider.Id;
        if (_cache.TryGetValue<List<CategoryResponse>>(cacheKey, out List<CategoryResponse>? cached) && cached != null)
        {
            return Ok(cached);
        }

        using XtreamClient client = new XtreamClient(_httpClientFactory, _loggerFactory.CreateLogger<XtreamClient>());
        List<CategoryResponse> result = (
            await client.GetLiveCategoryAsync(provider.ToConnectionInfo(), cancellationToken).ConfigureAwait(false)
        )
            .Select(CreateCategoryResponse)
            .ToList();

        _cache.Set(cacheKey, result, TimeSpan.FromMinutes(CacheMinutes));
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
        XtreamProvider? provider = GetProvider(providerId);
        if (provider == null)
        {
            return BadRequest(new { message = "No provider configured" });
        }

        string cacheKey = $"xtream-api-live-streams-{provider.Id}-{categoryId}";
        if (_cache.TryGetValue<List<ItemResponse>>(cacheKey, out List<ItemResponse>? cached) && cached != null)
        {
            return Ok(cached);
        }

        using XtreamClient client = new XtreamClient(_httpClientFactory, _loggerFactory.CreateLogger<XtreamClient>());
        List<ItemResponse> result = (
            await client
                .GetLiveStreamsByCategoryAsync(provider.ToConnectionInfo(), categoryId, cancellationToken)
                .ConfigureAwait(false)
        )
            .Select(CreateItemResponse)
            .ToList();

        _cache.Set(cacheKey, result, TimeSpan.FromMinutes(CacheMinutes));
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
        XtreamProvider? provider = GetProvider(providerId);
        if (provider == null)
        {
            return BadRequest(new { message = "No provider configured" });
        }

        string cacheKey = "xtream-api-vod-categories-" + provider.Id;
        if (_cache.TryGetValue<List<CategoryResponse>>(cacheKey, out List<CategoryResponse>? cached) && cached != null)
        {
            return Ok(cached);
        }

        using XtreamClient client = new XtreamClient(_httpClientFactory, _loggerFactory.CreateLogger<XtreamClient>());
        List<CategoryResponse> result = (
            await client.GetVodCategoryAsync(provider.ToConnectionInfo(), cancellationToken).ConfigureAwait(false)
        )
            .Select(CreateCategoryResponse)
            .ToList();

        _cache.Set(cacheKey, result, TimeSpan.FromMinutes(CacheMinutes));
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
        XtreamProvider? provider = GetProvider(providerId);
        if (provider == null)
        {
            return BadRequest(new { message = "No provider configured" });
        }

        string cacheKey = $"xtream-api-vod-streams-{provider.Id}-{categoryId}";
        if (_cache.TryGetValue<List<ItemResponse>>(cacheKey, out List<ItemResponse>? cached) && cached != null)
        {
            return Ok(cached);
        }

        using XtreamClient client = new XtreamClient(_httpClientFactory, _loggerFactory.CreateLogger<XtreamClient>());
        List<ItemResponse> result = (
            await client
                .GetVodStreamsByCategoryAsync(provider.ToConnectionInfo(), categoryId, cancellationToken)
                .ConfigureAwait(false)
        )
            .Select(CreateItemResponse)
            .ToList();

        _cache.Set(cacheKey, result, TimeSpan.FromMinutes(CacheMinutes));
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
        XtreamProvider? provider = GetProvider(providerId);
        if (provider == null)
        {
            return BadRequest(new { message = "No provider configured" });
        }

        string cacheKey = "xtream-api-series-categories-" + provider.Id;
        if (_cache.TryGetValue<List<CategoryResponse>>(cacheKey, out List<CategoryResponse>? cached) && cached != null)
        {
            return Ok(cached);
        }

        using XtreamClient client = new XtreamClient(_httpClientFactory, _loggerFactory.CreateLogger<XtreamClient>());
        List<CategoryResponse> result = (
            await client.GetSeriesCategoryAsync(provider.ToConnectionInfo(), cancellationToken).ConfigureAwait(false)
        )
            .Select(CreateCategoryResponse)
            .ToList();

        _cache.Set(cacheKey, result, TimeSpan.FromMinutes(CacheMinutes));
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
        XtreamProvider? provider = GetProvider(providerId);
        if (provider == null)
        {
            return BadRequest(new { message = "No provider configured" });
        }

        string cacheKey = $"xtream-api-series-streams-{provider.Id}-{categoryId}";
        if (_cache.TryGetValue<List<ItemResponse>>(cacheKey, out List<ItemResponse>? cached) && cached != null)
        {
            return Ok(cached);
        }

        using XtreamClient client = new XtreamClient(_httpClientFactory, _loggerFactory.CreateLogger<XtreamClient>());
        List<ItemResponse> result = (
            await client
                .GetSeriesByCategoryAsync(provider.ToConnectionInfo(), categoryId, cancellationToken)
                .ConfigureAwait(false)
        )
            .Select(CreateItemResponse)
            .ToList();

        _cache.Set(cacheKey, result, TimeSpan.FromMinutes(CacheMinutes));
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
        XtreamProvider? provider = GetProvider(providerId);
        if (provider == null)
        {
            return BadRequest(new { message = "No provider configured" });
        }

        List<ChannelResponse> channels = (
            await StreamService
                .GetLiveStreamsWithOverridesForProvider(provider, cancellationToken)
                .ConfigureAwait(false)
        )
            .Select(CreateChannelResponse)
            .ToList();

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
        XtreamProvider? provider = GetProvider(providerId);
        if (provider == null)
        {
            return NotFound(new { success = false, message = "Provider not found" });
        }

        try
        {
            using XtreamClient client = new XtreamClient(
                _httpClientFactory,
                _loggerFactory.CreateLogger<XtreamClient>()
            );
            PlayerApi? playerApi = await client
                .GetUserAndServerInfoAsync(provider.ToConnectionInfo(), cancellationToken)
                .ConfigureAwait(false);

            if (playerApi?.UserInfo == null)
            {
                return Ok(new { success = false, message = "Failed to get user info from provider" });
            }

            UserInfo userInfo = playerApi.UserInfo;
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
            _logger.LogError(ex, "Failed to test provider {ProviderId}", providerId);
            return Ok(new { success = false, message = "Connection failed: " + ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error testing provider {ProviderId}", providerId);
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
                Id = p.Id,
                Name = p.Name,
                Enabled = p.Enabled,
                HasCredentials = !string.IsNullOrEmpty(p.BaseUrl) && !string.IsNullOrEmpty(p.Username),
            })
            .ToList();

        return Ok(providers);
    }

    /// <summary>
    /// Check if legacy configuration exists that can be migrated.
    /// </summary>
    /// <returns>Status of legacy configuration.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("LegacyConfig")]
    public ActionResult<object> GetLegacyConfigStatus()
    {
        PluginConfiguration config = Plugin.Instance.Configuration;
        bool hasLegacyCredentials = !string.IsNullOrEmpty(config.BaseUrl) && config.BaseUrl != "https://example.com";
        bool hasLegacyChannels = config.LiveTv.Count > 0 || config.Vod.Count > 0 || config.Series.Count > 0;
        bool hasLegacyConfig = hasLegacyCredentials || hasLegacyChannels;

        _logger.LogInformation(
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
        PluginConfiguration config = Plugin.Instance.Configuration;

        if (string.IsNullOrEmpty(config.BaseUrl) || config.BaseUrl == "https://example.com")
        {
            return Ok(new { success = false, message = "No legacy configuration found to migrate" });
        }

        XtreamProvider migratedProvider = new XtreamProvider
        {
            Id = "migrated-" + DateTime.UtcNow.Ticks.ToString("x", CultureInfo.InvariantCulture).Substring(0, 8),
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

        _logger.LogInformation("Legacy configuration migrated to provider {ProviderId}", migratedProvider.Id);

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

        bool success = await discordService.TestWebhookAsync(webhookUrl, cancellationToken).ConfigureAwait(false);
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
            int count = await CircularBufferReadStream
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
            _logger.LogError(ex, "Failed to send buffer diagnostics");
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
        EpgTestResponse response = new EpgTestResponse { StreamId = streamId, Provider = epgProvider.Name };

        try
        {
            response.ChannelName =
                (await StreamService.GetAllLiveStreams(cancellationToken).ConfigureAwait(false))
                    .FirstOrDefault(s => s.Stream.StreamId == streamId)
                    ?.Stream.Name
                ?? $"Stream {streamId}";

            response.Programs = (await epgProvider.GetProgramsAsync(streamId, cancellationToken).ConfigureAwait(false))
                .Select(p => new EpgProgramResponse
                {
                    Id = p.Id,
                    Title = p.Title,
                    Description = p.Description,
                    StartUtc = p.StartUtc,
                    EndUtc = p.EndUtc,
                    ImageUrl = p.ImageUrl,
                })
                .ToList();

            response.Success = true;
            _logger.LogInformation(
                "EPG test for stream {StreamId}: {Count} programs from {Provider}",
                streamId,
                response.Programs.Count,
                epgProvider.Name
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EPG test failed for stream {StreamId}", streamId);
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
        _logger.LogInformation("Starting parallel EPG refresh via API");

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
            _logger.LogError(ex, "EPG refresh failed");
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
        IReadOnlyList<StreamInfoSnapshot> streams = Restream.GetActiveStreamSnapshots();
        _logger.LogInformation("Retrieved {Count} active stream(s)", streams.Count);
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
        _logger.LogWarning("Killing stream {StreamId} via API", streamId);

        if (Restream.KillStream(streamId))
        {
            _logger.LogInformation("Successfully killed stream {StreamId}", streamId);
            return Ok(new { success = true, message = "Stream " + streamId + " killed successfully" });
        }

        _logger.LogWarning("Stream {StreamId} not found", streamId);
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
        _logger.LogWarning("Killing all active streams via API");
        int count = Restream.KillAllStreams();
        _logger.LogInformation("Killed {Count} active stream(s)", count);
        return Ok(
            new
            {
                success = true,
                count,
                message = $"Killed {count} stream(s)",
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
            XtreamProvider? provider = GetProvider(providerId);
            if (provider == null)
            {
                return Ok(new { success = false, message = "No provider configured" });
            }

            using XtreamClient client = new XtreamClient(
                _httpClientFactory,
                _loggerFactory.CreateLogger<XtreamClient>()
            );
            PlayerApi? playerApi = await client
                .GetUserAndServerInfoAsync(provider.ToConnectionInfo(), cancellationToken)
                .ConfigureAwait(false);

            if (playerApi?.UserInfo == null)
            {
                return Ok(new { success = false, message = "Failed to get user info from provider" });
            }

            UserInfo userInfo = playerApi.UserInfo;
            int pluginActiveStreams = Restream.GetActiveStreamCount();

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
            _logger.LogError(ex, "Failed to get connection info from provider");
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

        _logger.LogInformation(
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
        Response.Headers["Cache-Control"] = "no-cache";
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
        _logger.LogDebug("WebSocket connection established for discovery progress");

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
                    _logger.LogDebug("WebSocket send failed, connection may be closed");
                    break;
                }

                // Send heartbeat if no progress update for a while
                if ((DateTime.UtcNow - lastProgressTime).TotalSeconds > HeartbeatIntervalSeconds)
                {
                    var heartbeat = "{\"type\":\"heartbeat\"}";
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
                var completeJson = "{\"Phase\":\"Complete\",\"Message\":\"Operation finished\"}";
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
            _logger.LogDebug("WebSocket connection cancelled");
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "WebSocket error during discovery progress");
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

        if (result == null)
        {
            return Ok(
                new DiscoveryResponse
                {
                    Success = false,
                    ErrorMessage = "No cached results available. Run a discovery first.",
                }
            );
        }

        return Ok(
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
                WorkingProviders = result.WorkingProviders.Select(MapToDiscoveryResponse).ToList(),
                FullyWorkingProviders = result.FullyWorkingProviders.Select(MapToDiscoveryResponse).ToList(),
                ExcellentProviders = result.ExcellentProviders.Select(MapToDiscoveryResponse).ToList(),
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
        _logger.LogInformation("Discovery operation cancelled by user");
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
        _logger.LogInformation("Discovery cache cleared by user");
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
        var existingProvider = config.Providers.FirstOrDefault(p =>
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

        _logger.LogInformation(
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

    /// <summary>
    /// Get comprehensive connection status for all providers.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Connection status with per-provider details and utilization warnings.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("ConnectionStatus")]
    public async Task<ActionResult<ConnectionStatusResponse>> GetConnectionStatus(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;
        var enabledProviders = config.GetEnabledProviders().ToList();
        var response = new ConnectionStatusResponse
        {
            PluginActiveStreams = Restream.GetActiveStreamCount(),
            ConfiguredMaxStreams = config.MaxConcurrentStreams,
            EnforcementEnabled = config.EnforceConnectionLimit,
            AutoKillEnabled = config.AutoKillOldestStream,
        };

        // Always refresh the shared connection cache to get fresh data
        // This ensures both UI display and channel deduplication use the same data
        await _connectionCache.RefreshAsync(enabledProviders, cancellationToken).ConfigureAwait(false);

        // Build provider status from the shared cache (same data used for channel ordering)
        response.Providers = enabledProviders
            .Select(provider =>
            {
                var status = new ProviderConnectionStatus { ProviderId = provider.Id, ProviderName = provider.Name };

                // Get cached status (just refreshed above)
                var cachedStatus = _connectionCache.GetStatus(provider.Id);
                if (cachedStatus != null)
                {
                    status.MaxConnections = cachedStatus.MaxConnections;
                    status.ProviderActiveConnections = cachedStatus.ActiveConnections;
                    status.Status = cachedStatus.Status;
                    status.ExpirationDate = cachedStatus.ExpirationDate;
                    status.IsTrial = cachedStatus.IsTrial;
                    status.IsOnline = cachedStatus.IsOnline;
                    status.ErrorMessage = cachedStatus.ErrorMessage;
                }
                else
                {
                    status.ErrorMessage = "No cached status";
                    status.IsOnline = false;
                }

                return status;
            })
            .ToList();

        // Calculate effective max streams and available slots
        // Available slots must consider BOTH:
        // 1. The configured max streams limit (if set)
        // 2. The actual provider capacity (MaxConnections - ProviderActiveConnections)
        var onlineProviders = response.Providers.Where(p => p.IsOnline).ToList();

        // Calculate provider-side availability (accounts for external usage of the same credentials)
        var providerAvailableSlots = onlineProviders.Sum(p =>
            Math.Max(0, p.MaxConnections - p.ProviderActiveConnections)
        );
        var providerTotalCapacity = onlineProviders.Sum(p => p.MaxConnections);
        var providerTotalActiveConnections = onlineProviders.Sum(p => p.ProviderActiveConnections);

        // Set provider-side totals for UI transparency
        response.TotalProviderActiveConnections = providerTotalActiveConnections;
        response.TotalProviderCapacity = providerTotalCapacity;

        if (config.MaxConcurrentStreams > 0)
        {
            // Use configured limit as the effective max
            response.EffectiveMaxStreams = config.MaxConcurrentStreams;

            // Available slots is the minimum of:
            // - What config allows (config limit - plugin streams)
            // - What providers have available (considering all clients using the credentials)
            var configAvailableSlots = Math.Max(0, config.MaxConcurrentStreams - response.PluginActiveStreams);
            response.AvailableSlots = Math.Min(configAvailableSlots, providerAvailableSlots);
        }
        else if (onlineProviders.Count > 0)
        {
            // No configured limit - use total provider capacity
            response.EffectiveMaxStreams = providerTotalCapacity;
            response.AvailableSlots = providerAvailableSlots;
        }
        else
        {
            response.EffectiveMaxStreams = 1;
            response.AvailableSlots = 1;
        }

        // Utilization is based on plugin's own streams vs effective max
        response.UtilizationPercent =
            response.EffectiveMaxStreams > 0
                ? (int)Math.Round(100.0 * response.PluginActiveStreams / response.EffectiveMaxStreams)
                : 0;

        // Set warning level and message
        if (response.PluginActiveStreams >= response.EffectiveMaxStreams)
        {
            response.WarningLevel = "Critical";
            response.WarningMessage =
                $"Connection limit reached! {response.PluginActiveStreams}/{response.EffectiveMaxStreams} streams active.";
        }
        else if (response.UtilizationPercent >= 80)
        {
            response.WarningLevel = "Warning";
            response.WarningMessage =
                $"High utilization: {response.PluginActiveStreams}/{response.EffectiveMaxStreams} streams ({response.UtilizationPercent}%).";
        }
        else
        {
            response.WarningLevel = "None";
        }

        // Also check each provider for over-subscription
        foreach (var providerStatus in response.Providers.Where(p => p.IsOnline))
        {
            if (providerStatus.ProviderActiveConnections >= providerStatus.MaxConnections)
            {
                response.WarningLevel = "Critical";
                response.WarningMessage =
                    $"Provider {providerStatus.ProviderName} at limit: {providerStatus.ProviderActiveConnections}/{providerStatus.MaxConnections} connections.";
                break;
            }

            if (
                providerStatus.ProviderActiveConnections > 0
                && (double)providerStatus.ProviderActiveConnections / providerStatus.MaxConnections >= 0.8
            )
            {
                if (response.WarningLevel != "Critical")
                {
                    response.WarningLevel = "Warning";
                    response.WarningMessage =
                        $"Provider {providerStatus.ProviderName} high usage: {providerStatus.ProviderActiveConnections}/{providerStatus.MaxConnections} connections.";
                }
            }
        }

        return Ok(response);
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
        var sourceProvider = config.Providers.FirstOrDefault(p => p.Id == request.SourceProviderId);
        var targetProvider = config.Providers.FirstOrDefault(p => p.Id == request.TargetProviderId);

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
        var sourceSelections = sourceProvider.LiveTv ?? new SerializableDictionary<int, HashSet<int>>();
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
                        categoryStreams = new HashSet<int>();
                        targetSelections[categoryId] = categoryStreams;
                    }

                    categoryStreams.Add(targetStream.StreamId);

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
                    _logger.LogDebug(
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

            _logger.LogInformation(
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
            _logger.LogError(
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
}
