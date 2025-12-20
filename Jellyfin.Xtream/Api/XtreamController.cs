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
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Api.Models;
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service;
using Jellyfin.Xtream.Service.Epg;
using Jellyfin.Xtream.Utility;
using Microsoft.AspNetCore.Authorization;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="XtreamController"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="loggerFactory">The logger factory instance.</param>
    /// <param name="cache">The memory cache instance.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    public XtreamController(
        ILogger<XtreamController> logger,
        ILoggerFactory loggerFactory,
        IMemoryCache cache,
        IHttpClientFactory httpClientFactory
    )
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _cache = cache;
        _httpClientFactory = httpClientFactory;
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
}
