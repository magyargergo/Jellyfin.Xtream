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
using Jellyfin.Xtream.Service.Logging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Api;

/// <summary>
/// The Jellyfin Xtream configuration API.
/// Handles category and stream browsing, provider testing, legacy migration, and capabilities.
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

    private static readonly string[] ConfigurationSections =
    [
        "Proxy",
        "Epg",
        "Discord",
        "Timeouts",
        "Health",
        "UserAgent",
        "RateLimiting",
        "Visibility",
        "Failover",
        "ConnectionLimits",
        "Hedging",
        "Buffer",
        "Logging",
        "StreamProcessing",
    ];

    private static readonly string[] StreamOperations =
    [
        "GET ActiveStreams",
        "DELETE ActiveStreams/{id}",
        "GET ActiveStreams/{id}/Providers",
        "GET ActiveStreams/{id}/Providers/{idx}/Health",
        "POST ActiveStreams/{id}/ForceReconnect",
        "POST ActiveStreams/{id}/Providers/{idx}/Eject",
        "POST ActiveStreams/{id}/Providers/Reset",
        "GET ActiveStreams/{id}/Metrics",
        "GET Diagnostics/Bundle",
        "GET Metrics/Aggregate",
    ];

    private static readonly string[] RegistryOperations =
    [
        "GET Providers/Status",
        "GET Channels/Count",
        "GET Channels/List",
        "GET ConnectionStatus",
        "GET ConnectionInfo",
        "GET Events/Stream",
        "GET Events/Recent",
    ];

    private readonly ILogger<XtreamController> _logger = logger;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly IMemoryCache _cache = cache;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;

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
        var provider = XtreamControllerHelpers.GetProvider(providerId);
        if (provider == null)
        {
            return BadRequest(
                XtreamControllerHelpers.CreateError(
                    ErrorCodes.ProviderNotConfigured,
                    "No provider configured",
                    "Configure at least one provider via PUT /Xtream/Providers"
                )
            );
        }

        var cacheKey = "xtream-api-live-categories-" + provider.Id;
        if (_cache.TryGetValue<List<CategoryResponse>>(cacheKey, out var cached) && cached != null)
        {
            return Ok(cached);
        }

        try
        {
            using var client = new XtreamClient(_httpClientFactory, _loggerFactory.CreateLogger<XtreamClient>());
            List<CategoryResponse> result =
            [
                .. (
                    await client
                        .GetLiveCategoryAsync(provider.ToConnectionInfo(), cancellationToken)
                        .ConfigureAwait(false)
                ).Select(XtreamControllerHelpers.CreateCategoryResponse),
            ];

            _ = _cache.Set(cacheKey, result, TimeSpan.FromMinutes(CacheMinutes));
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.PluginLogError(ex, "Failed to get live categories from provider {ProviderId}", provider.Id);
            return StatusCode(
                StatusCodes.Status502BadGateway,
                XtreamControllerHelpers.CreateError(
                    ErrorCodes.ConnectionFailed,
                    "Failed to load categories from provider",
                    XtreamControllerHelpers.RedactCredentials(ex.Message)
                )
            );
        }
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
        var provider = XtreamControllerHelpers.GetProvider(providerId);
        if (provider == null)
        {
            return BadRequest(
                XtreamControllerHelpers.CreateError(
                    ErrorCodes.ProviderNotConfigured,
                    "No provider configured",
                    "Configure at least one provider via PUT /Xtream/Providers"
                )
            );
        }

        var cacheKey = $"xtream-api-live-streams-{provider.Id}-{categoryId}";
        if (_cache.TryGetValue<List<ItemResponse>>(cacheKey, out var cached) && cached != null)
        {
            return Ok(cached);
        }

        try
        {
            using var client = new XtreamClient(_httpClientFactory, _loggerFactory.CreateLogger<XtreamClient>());
            List<ItemResponse> result =
            [
                .. (
                    await client
                        .GetLiveStreamsByCategoryAsync(provider.ToConnectionInfo(), categoryId, cancellationToken)
                        .ConfigureAwait(false)
                ).Select(XtreamControllerHelpers.CreateItemResponse),
            ];

            _ = _cache.Set(cacheKey, result, TimeSpan.FromMinutes(CacheMinutes));
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.PluginLogError(
                ex,
                "Failed to get live streams for category {CategoryId} from provider {ProviderId}",
                categoryId,
                provider.Id
            );
            return StatusCode(
                StatusCodes.Status502BadGateway,
                XtreamControllerHelpers.CreateError(
                    ErrorCodes.ConnectionFailed,
                    "Failed to load streams from provider",
                    XtreamControllerHelpers.RedactCredentials(ex.Message)
                )
            );
        }
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
        var provider = XtreamControllerHelpers.GetProvider(providerId);
        if (provider == null)
        {
            return BadRequest(
                XtreamControllerHelpers.CreateError(
                    ErrorCodes.ProviderNotConfigured,
                    "No provider configured",
                    "Configure at least one provider via PUT /Xtream/Providers"
                )
            );
        }

        var cacheKey = "xtream-api-vod-categories-" + provider.Id;
        if (_cache.TryGetValue<List<CategoryResponse>>(cacheKey, out var cached) && cached != null)
        {
            return Ok(cached);
        }

        try
        {
            using var client = new XtreamClient(_httpClientFactory, _loggerFactory.CreateLogger<XtreamClient>());
            List<CategoryResponse> result =
            [
                .. (
                    await client
                        .GetVodCategoryAsync(provider.ToConnectionInfo(), cancellationToken)
                        .ConfigureAwait(false)
                ).Select(XtreamControllerHelpers.CreateCategoryResponse),
            ];

            _ = _cache.Set(cacheKey, result, TimeSpan.FromMinutes(CacheMinutes));
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.PluginLogError(ex, "Failed to get VOD categories from provider {ProviderId}", provider.Id);
            return StatusCode(
                StatusCodes.Status502BadGateway,
                XtreamControllerHelpers.CreateError(
                    ErrorCodes.ConnectionFailed,
                    "Failed to load VOD categories from provider",
                    XtreamControllerHelpers.RedactCredentials(ex.Message)
                )
            );
        }
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
        var provider = XtreamControllerHelpers.GetProvider(providerId);
        if (provider == null)
        {
            return BadRequest(
                XtreamControllerHelpers.CreateError(
                    ErrorCodes.ProviderNotConfigured,
                    "No provider configured",
                    "Configure at least one provider via PUT /Xtream/Providers"
                )
            );
        }

        var cacheKey = $"xtream-api-vod-streams-{provider.Id}-{categoryId}";
        if (_cache.TryGetValue<List<ItemResponse>>(cacheKey, out var cached) && cached != null)
        {
            return Ok(cached);
        }

        try
        {
            using var client = new XtreamClient(_httpClientFactory, _loggerFactory.CreateLogger<XtreamClient>());
            List<ItemResponse> result =
            [
                .. (
                    await client
                        .GetVodStreamsByCategoryAsync(provider.ToConnectionInfo(), categoryId, cancellationToken)
                        .ConfigureAwait(false)
                ).Select(XtreamControllerHelpers.CreateItemResponse),
            ];

            _ = _cache.Set(cacheKey, result, TimeSpan.FromMinutes(CacheMinutes));
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.PluginLogError(
                ex,
                "Failed to get VOD streams for category {CategoryId} from provider {ProviderId}",
                categoryId,
                provider.Id
            );
            return StatusCode(
                StatusCodes.Status502BadGateway,
                XtreamControllerHelpers.CreateError(
                    ErrorCodes.ConnectionFailed,
                    "Failed to load VOD streams from provider",
                    XtreamControllerHelpers.RedactCredentials(ex.Message)
                )
            );
        }
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
        var provider = XtreamControllerHelpers.GetProvider(providerId);
        if (provider == null)
        {
            return BadRequest(
                XtreamControllerHelpers.CreateError(
                    ErrorCodes.ProviderNotConfigured,
                    "No provider configured",
                    "Configure at least one provider via PUT /Xtream/Providers"
                )
            );
        }

        var cacheKey = "xtream-api-series-categories-" + provider.Id;
        if (_cache.TryGetValue<List<CategoryResponse>>(cacheKey, out var cached) && cached != null)
        {
            return Ok(cached);
        }

        try
        {
            using var client = new XtreamClient(_httpClientFactory, _loggerFactory.CreateLogger<XtreamClient>());
            List<CategoryResponse> result =
            [
                .. (
                    await client
                        .GetSeriesCategoryAsync(provider.ToConnectionInfo(), cancellationToken)
                        .ConfigureAwait(false)
                ).Select(XtreamControllerHelpers.CreateCategoryResponse),
            ];

            _ = _cache.Set(cacheKey, result, TimeSpan.FromMinutes(CacheMinutes));
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.PluginLogError(ex, "Failed to get series categories from provider {ProviderId}", provider.Id);
            return StatusCode(
                StatusCodes.Status502BadGateway,
                XtreamControllerHelpers.CreateError(
                    ErrorCodes.ConnectionFailed,
                    "Failed to load series categories from provider",
                    XtreamControllerHelpers.RedactCredentials(ex.Message)
                )
            );
        }
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
        var provider = XtreamControllerHelpers.GetProvider(providerId);
        if (provider == null)
        {
            return BadRequest(
                XtreamControllerHelpers.CreateError(
                    ErrorCodes.ProviderNotConfigured,
                    "No provider configured",
                    "Configure at least one provider via PUT /Xtream/Providers"
                )
            );
        }

        var cacheKey = $"xtream-api-series-streams-{provider.Id}-{categoryId}";
        if (_cache.TryGetValue<List<ItemResponse>>(cacheKey, out var cached) && cached != null)
        {
            return Ok(cached);
        }

        try
        {
            using var client = new XtreamClient(_httpClientFactory, _loggerFactory.CreateLogger<XtreamClient>());
            List<ItemResponse> result =
            [
                .. (
                    await client
                        .GetSeriesByCategoryAsync(provider.ToConnectionInfo(), categoryId, cancellationToken)
                        .ConfigureAwait(false)
                ).Select(XtreamControllerHelpers.CreateItemResponse),
            ];

            _ = _cache.Set(cacheKey, result, TimeSpan.FromMinutes(CacheMinutes));
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.PluginLogError(
                ex,
                "Failed to get series for category {CategoryId} from provider {ProviderId}",
                categoryId,
                provider.Id
            );
            return StatusCode(
                StatusCodes.Status502BadGateway,
                XtreamControllerHelpers.CreateError(
                    ErrorCodes.ConnectionFailed,
                    "Failed to load series from provider",
                    XtreamControllerHelpers.RedactCredentials(ex.Message)
                )
            );
        }
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
        var provider = XtreamControllerHelpers.GetProvider(providerId);
        if (provider == null)
        {
            return BadRequest(
                XtreamControllerHelpers.CreateError(
                    ErrorCodes.ProviderNotConfigured,
                    "No provider configured",
                    "Configure at least one provider via PUT /Xtream/Providers"
                )
            );
        }

        try
        {
            List<ChannelResponse> channels =
            [
                .. (
                    await StreamService
                        .GetLiveStreamsWithOverridesForProvider(provider, cancellationToken)
                        .ConfigureAwait(false)
                ).Select(XtreamControllerHelpers.CreateChannelResponse),
            ];

            return Ok(channels);
        }
        catch (Exception ex)
        {
            _logger.PluginLogError(ex, "Failed to get live TV channels from provider {ProviderId}", provider.Id);
            return StatusCode(
                StatusCodes.Status502BadGateway,
                XtreamControllerHelpers.CreateError(
                    ErrorCodes.ConnectionFailed,
                    "Failed to load live TV channels from provider",
                    XtreamControllerHelpers.RedactCredentials(ex.Message)
                )
            );
        }
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
            return Ok(new { success = false, message = "Connection failed. Check server logs for details." });
        }
        catch (Exception ex)
        {
            _logger.PluginLogError(ex, "Unexpected error testing provider {ProviderId}", providerId);
            return Ok(
                new { success = false, message = "An unexpected error occurred. Check server logs for details." }
            );
        }
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
            response.Message = "Failed to copy channels. Check server logs for details.";
            return StatusCode(500, response);
        }

        return Ok(response);
    }

    /// <summary>
    /// Get machine-readable capability manifest for agent and automation discovery.
    /// </summary>
    /// <returns>Plugin capabilities, features, and endpoint catalog.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Capabilities")]
    public ActionResult<object> GetCapabilities()
    {
        return Ok(
            new
            {
                pluginVersion = Plugin.Instance.Version.ToString(),
                apiVersion = "1.0",
                features = new
                {
                    multiProvider = new
                    {
                        enabled = true,
                        supportsFailover = true,
                        supportsLoadBalancing = true,
                    },
                    streamQuality = new
                    {
                        supportsTR101290 = true,
                        supportsPCRAnalysis = true,
                        supportsAVSync = true,
                    },
                    nativeStreamer = new
                    {
                        enabled = true,
                        supportsSharedMemory = true,
                        supportsHealthTracking = true,
                    },
                    notifications = new { discord = true, sse = true },
                },
                configurationSections = ConfigurationSections,
                streamOperations = StreamOperations,
                registryOperations = RegistryOperations,
                limits = new
                {
                    maxProviders = 10,
                    maxConcurrentStreams = Plugin.Instance.Configuration.MaxConcurrentStreams,
                },
            }
        );
    }
}
