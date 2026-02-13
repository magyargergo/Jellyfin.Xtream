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
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Utility;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Jellyfin.Xtream.Client;

/// <summary>
/// The Xtream API client implementation with token validation and health monitoring.
/// </summary>
/// <remarks>
/// This class implements IDisposable to properly clean up the authentication lock semaphore.
/// HttpClient instances from IHttpClientFactory are managed by the factory and should not be disposed manually.
/// </remarks>
/// <remarks>
/// Initializes a new instance of the <see cref="XtreamClient"/> class.
/// </remarks>
/// <param name="httpClientFactory">The HTTP client factory.</param>
/// <param name="logger">Optional logger for diagnostics.</param>
public sealed class XtreamClient(IHttpClientFactory httpClientFactory, ILogger<XtreamClient>? logger = null)
    : IDisposable
{
    private const int AuthValidityMinutes = 30;

    private readonly HttpClient _client = httpClientFactory.CreateClient("XtreamClient");

    private readonly ILogger<XtreamClient>? _logger = logger;

    private readonly SemaphoreSlim _authLock = new(1, 1);

    private DateTime _lastAuthCheck = DateTime.MinValue;

    private bool _lastAuthSuccess;

    /// <summary>
    /// Validates credentials are still working by checking authentication status.
    /// Prevents 406 errors from expired/invalid tokens.
    /// </summary>
    private async Task<bool> ValidateAuthenticationAsync(
        ConnectionInfo connectionInfo,
        CancellationToken cancellationToken
    )
    {
        var now = DateTime.UtcNow;

        if (_lastAuthSuccess && (now - _lastAuthCheck).TotalMinutes < AuthValidityMinutes)
        {
            return true;
        }

        await _authLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_lastAuthSuccess && (now - _lastAuthCheck).TotalMinutes < AuthValidityMinutes)
            {
                return true;
            }

            _logger?.LogDebugIfEnabled("Validating Xtream credentials (last check: {LastCheck})", _lastAuthCheck);

            try
            {
                var result = await GetUserAndServerInfoAsync(connectionInfo, cancellationToken).ConfigureAwait(false);
                _lastAuthSuccess = result?.UserInfo?.Status == "Active";
                _lastAuthCheck = now;

                if (!_lastAuthSuccess)
                {
                    _logger?.PluginLogWarning(
                        "Xtream authentication validation failed. User status: {Status}. This may cause 406 errors.",
                        result?.UserInfo?.Status ?? "null"
                    );
                }
                else
                {
                    _logger?.LogDebugIfEnabled(
                        "Xtream credentials validated successfully. Expires: {Expiry}",
                        result!.UserInfo!.ExpDate?.ToString("yyyy-MM-dd HH:mm:ss")
                    );
                }

                return _lastAuthSuccess;
            }
            catch (HttpRequestException exception)
            {
                // Log without stack trace for expected connection failures
                if (IsExpectedConnectionFailure(exception))
                {
                    _logger?.LogDebugIfEnabled("Failed to validate Xtream credentials: {Message}", exception.Message);
                }
                else
                {
                    _logger?.PluginLogWarning(
                        exception,
                        "Failed to validate Xtream credentials. This will likely cause 406 errors."
                    );
                }

                _lastAuthSuccess = false;
                _lastAuthCheck = now;
                return false;
            }
        }
        finally
        {
            _ = _authLock.Release();
        }
    }

    private async Task<T> QueryApi<T>(
        ConnectionInfo connectionInfo,
        string urlPath,
        CancellationToken cancellationToken,
        bool skipValidation = false
    )
    {
        if (
            !skipValidation
            && !await ValidateAuthenticationAsync(connectionInfo, cancellationToken).ConfigureAwait(false)
        )
        {
            _logger?.PluginLogWarning(
                "Proceeding with API call despite failed authentication. Expect possible 406 errors. Check credentials and provider status."
            );
        }

        var uri = new Uri(connectionInfo.BaseUrl + urlPath);
        return JsonConvert.DeserializeObject<T>(
            await _client.GetStringAsync(uri, cancellationToken).ConfigureAwait(false)
        )!;
    }

    /// <summary>
    /// Gets user and server information from the Xtream API.
    /// </summary>
    /// <param name="connectionInfo">Connection credentials.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Player API response with user and server info.</returns>
    public Task<PlayerApi> GetUserAndServerInfoAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken)
    {
        return QueryApi<PlayerApi>(
            connectionInfo,
            $"/player_api.php?username={connectionInfo.UserName}&password={connectionInfo.Password}",
            cancellationToken,
            skipValidation: true
        );
    }

    /// <summary>
    /// Gets series by category ID.
    /// </summary>
    /// <param name="connectionInfo">Connection credentials.</param>
    /// <param name="categoryId">The category ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of series in the category.</returns>
    public async Task<IReadOnlyList<Series>> GetSeriesByCategoryAsync(
        ConnectionInfo connectionInfo,
        int categoryId,
        CancellationToken cancellationToken
    )
    {
        return await QueryApi<List<Series>>(
                connectionInfo,
                $"/player_api.php?username={connectionInfo.UserName}&password={connectionInfo.Password}&action=get_series&category_id={categoryId}",
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Gets series stream information by series ID.
    /// </summary>
    /// <param name="connectionInfo">Connection credentials.</param>
    /// <param name="seriesId">The series ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Series stream information including seasons and episodes.</returns>
    public Task<SeriesStreamInfo> GetSeriesStreamsBySeriesAsync(
        ConnectionInfo connectionInfo,
        int seriesId,
        CancellationToken cancellationToken
    )
    {
        return QueryApi<SeriesStreamInfo>(
            connectionInfo,
            $"/player_api.php?username={connectionInfo.UserName}&password={connectionInfo.Password}&action=get_series_info&series_id={seriesId}",
            cancellationToken
        );
    }

    /// <summary>
    /// Gets VOD streams by category ID.
    /// </summary>
    /// <param name="connectionInfo">Connection credentials.</param>
    /// <param name="categoryId">The category ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of VOD streams in the category.</returns>
    public async Task<IReadOnlyList<StreamInfo>> GetVodStreamsByCategoryAsync(
        ConnectionInfo connectionInfo,
        int categoryId,
        CancellationToken cancellationToken
    )
    {
        return await QueryApi<List<StreamInfo>>(
                connectionInfo,
                $"/player_api.php?username={connectionInfo.UserName}&password={connectionInfo.Password}&action=get_vod_streams&category_id={categoryId}",
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Gets VOD information by stream ID.
    /// </summary>
    /// <param name="connectionInfo">Connection credentials.</param>
    /// <param name="streamId">The stream ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>VOD stream information.</returns>
    public Task<VodStreamInfo> GetVodInfoAsync(
        ConnectionInfo connectionInfo,
        int streamId,
        CancellationToken cancellationToken
    )
    {
        return QueryApi<VodStreamInfo>(
            connectionInfo,
            $"/player_api.php?username={connectionInfo.UserName}&password={connectionInfo.Password}&action=get_vod_info&vod_id={streamId}",
            cancellationToken
        );
    }

    /// <summary>
    /// Gets all live streams.
    /// </summary>
    /// <param name="connectionInfo">Connection credentials.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of all live streams.</returns>
    public async Task<IReadOnlyList<StreamInfo>> GetLiveStreamsAsync(
        ConnectionInfo connectionInfo,
        CancellationToken cancellationToken
    )
    {
        return await QueryApi<List<StreamInfo>>(
                connectionInfo,
                $"/player_api.php?username={connectionInfo.UserName}&password={connectionInfo.Password}&action=get_live_streams",
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Gets live streams by category ID.
    /// </summary>
    /// <param name="connectionInfo">Connection credentials.</param>
    /// <param name="categoryId">The category ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of live streams in the category.</returns>
    public async Task<IReadOnlyList<StreamInfo>> GetLiveStreamsByCategoryAsync(
        ConnectionInfo connectionInfo,
        int categoryId,
        CancellationToken cancellationToken
    )
    {
        return await QueryApi<List<StreamInfo>>(
                connectionInfo,
                $"/player_api.php?username={connectionInfo.UserName}&password={connectionInfo.Password}&action=get_live_streams&category_id={categoryId}",
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Gets all series categories.
    /// </summary>
    /// <param name="connectionInfo">Connection credentials.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of series categories.</returns>
    public async Task<IReadOnlyList<Category>> GetSeriesCategoryAsync(
        ConnectionInfo connectionInfo,
        CancellationToken cancellationToken
    )
    {
        return await QueryApi<List<Category>>(
                connectionInfo,
                $"/player_api.php?username={connectionInfo.UserName}&password={connectionInfo.Password}&action=get_series_categories",
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Gets all VOD categories.
    /// </summary>
    /// <param name="connectionInfo">Connection credentials.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of VOD categories.</returns>
    public async Task<IReadOnlyList<Category>> GetVodCategoryAsync(
        ConnectionInfo connectionInfo,
        CancellationToken cancellationToken
    )
    {
        return await QueryApi<List<Category>>(
                connectionInfo,
                $"/player_api.php?username={connectionInfo.UserName}&password={connectionInfo.Password}&action=get_vod_categories",
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Gets all live TV categories.
    /// </summary>
    /// <param name="connectionInfo">Connection credentials.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of live TV categories.</returns>
    public async Task<IReadOnlyList<Category>> GetLiveCategoryAsync(
        ConnectionInfo connectionInfo,
        CancellationToken cancellationToken
    )
    {
        return await QueryApi<List<Category>>(
                connectionInfo,
                $"/player_api.php?username={connectionInfo.UserName}&password={connectionInfo.Password}&action=get_live_categories",
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the full EPG for a stream (all listings regardless of day).
    /// Xtream API: action=get_simple_data_table.
    /// </summary>
    /// <param name="connectionInfo">Connection credentials.</param>
    /// <param name="streamId">The stream ID to get EPG for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>EPG listings for the stream.</returns>
    public Task<EpgListings> GetEpgInfoAsync(
        ConnectionInfo connectionInfo,
        int streamId,
        CancellationToken cancellationToken
    )
    {
        return QueryApi<EpgListings>(
            connectionInfo,
            $"/player_api.php?username={connectionInfo.UserName}&password={connectionInfo.Password}&action=get_simple_data_table&stream_id={streamId}",
            cancellationToken
        );
    }

    /// <summary>
    /// Gets the short EPG for a stream (next few upcoming programs).
    /// Xtream API: action=get_short_epg.
    /// </summary>
    /// <param name="connectionInfo">Connection credentials.</param>
    /// <param name="streamId">The stream ID to get EPG for.</param>
    /// <param name="limit">Maximum number of EPG entries to return (default 4).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>EPG listings for the stream.</returns>
    public Task<EpgListings> GetShortEpgAsync(
        ConnectionInfo connectionInfo,
        int streamId,
        int limit,
        CancellationToken cancellationToken
    )
    {
        return QueryApi<EpgListings>(
            connectionInfo,
            $"/player_api.php?username={connectionInfo.UserName}&password={connectionInfo.Password}&action=get_short_epg&stream_id={streamId}&limit={limit}",
            cancellationToken
        );
    }

    /// <summary>
    /// Gets the XMLTV EPG data URL for bulk EPG loading.
    /// Note: Returns the URL, not the content - XMLTV files can be very large.
    /// </summary>
    /// <param name="connectionInfo">Connection credentials.</param>
    /// <returns>The XMLTV endpoint URL.</returns>
    public static string GetXmltvUrl(ConnectionInfo connectionInfo) =>
        $"{connectionInfo.BaseUrl}/xmltv.php?username={connectionInfo.UserName}&password={connectionInfo.Password}";

    /// <summary>
    /// Disposes the authentication lock semaphore.
    /// Note: HttpClient from IHttpClientFactory should NOT be disposed manually - it's managed by the factory.
    /// </summary>
    public void Dispose()
    {
        _authLock.Dispose();
        GC.SuppressFinalize(this);
    }

    private static bool IsExpectedConnectionFailure(HttpRequestException ex)
    {
        // Check for common expected failures that don't need stack traces
        var message = ex.Message.ToUpperInvariant();

        // Connection refused, host not found, network unreachable
        if (
            message.Contains("CONNECTION REFUSED", StringComparison.Ordinal)
            || message.Contains("NO SUCH HOST", StringComparison.Ordinal)
            || message.Contains("HOST NOT FOUND", StringComparison.Ordinal)
            || message.Contains("NAME OR SERVICE NOT KNOWN", StringComparison.Ordinal)
            || message.Contains("NETWORK IS UNREACHABLE", StringComparison.Ordinal)
            || message.Contains("NODENAME NOR SERVNAME", StringComparison.Ordinal)
            || message.Contains("ACTIVELY REFUSED", StringComparison.Ordinal)
        )
        {
            return true;
        }

        // Check HTTP status codes that are expected failures
        if (ex.StatusCode.HasValue)
        {
            var code = (int)ex.StatusCode.Value;
            // 404 Not Found, 401 Unauthorized, 403 Forbidden are expected for invalid providers
            return code is 404 or 401 or 403 or 406;
        }

        return false;
    }
}
