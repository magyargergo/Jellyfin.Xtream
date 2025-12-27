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
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Utility;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Jellyfin.Xtream.Service.Epg;

/// <summary>
/// EPG provider using standard Xtream Codes API endpoints.
/// Tries get_short_epg first, falls back to get_simple_data_table.
/// Implements circuit breaker pattern to avoid repeated failures.
/// </summary>
public class XtreamEpgProvider : IEpgProvider
{
    private const int MaxConsecutiveFailures = 3;
    private static readonly TimeSpan CircuitBreakerResetTime = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<XtreamEpgProvider> _logger;

    private int _consecutiveFailures;
    private DateTime _lastFailureTime = DateTime.MinValue;
    private bool _isAvailable = true;

    /// <summary>
    /// Initializes a new instance of the <see cref="XtreamEpgProvider"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public XtreamEpgProvider(IHttpClientFactory httpClientFactory, ILogger<XtreamEpgProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Xtream API";

    /// <inheritdoc />
    public int Priority => 10; // Primary provider

    /// <inheritdoc />
    public bool IsAvailable
    {
        get
        {
            // Reset circuit breaker after timeout
            if (!_isAvailable && DateTime.UtcNow - _lastFailureTime > CircuitBreakerResetTime)
            {
                _isAvailable = true;
                _consecutiveFailures = 0;
                _logger.PluginLogInformation("{Provider} circuit breaker reset - provider available again", Name);
            }

            return _isAvailable;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EpgProgram>> GetProgramsAsync(int streamId, CancellationToken cancellationToken)
    {
        var provider = Plugin.Instance.Configuration.GetEnabledProviders().FirstOrDefault();
        if (provider == null)
        {
            _logger.PluginLogWarning("No enabled provider found for EPG data");
            return Array.Empty<EpgProgram>();
        }

        using var client = new XtreamClient(_httpClientFactory, _logger as ILogger<XtreamClient>);

        EpgListings? epgs = null;

        bool hadHttpError = false;

        // Try get_short_epg first (more reliable with many providers)
        try
        {
            _logger.PluginLogInformation("Trying get_short_epg for stream {StreamId}", streamId);
            epgs = await client
                .GetShortEpgAsync(provider.ToConnectionInfo(), streamId, 100, cancellationToken)
                .ConfigureAwait(false);
            _logger.PluginLogInformation(
                "get_short_epg returned {Count} listings for stream {StreamId}",
                epgs?.Listings?.Count ?? 0,
                streamId
            );
        }
        catch (HttpRequestException ex)
        {
            _logger.PluginLogWarning("get_short_epg failed for stream {StreamId}: {Error}", streamId, ex.Message);
            hadHttpError = true;
        }
        catch (JsonException ex)
        {
            _logger.PluginLogWarning(
                "get_short_epg JSON parse failed for stream {StreamId}: {Error}",
                streamId,
                ex.Message
            );
        }

        // Fall back to get_simple_data_table
        if (epgs == null || epgs.Listings.Count == 0)
        {
            try
            {
                _logger.PluginLogInformation("Trying get_simple_data_table for stream {StreamId}", streamId);
                epgs = await client
                    .GetEpgInfoAsync(provider.ToConnectionInfo(), streamId, cancellationToken)
                    .ConfigureAwait(false);
                _logger.PluginLogInformation(
                    "get_simple_data_table returned {Count} listings for stream {StreamId}",
                    epgs?.Listings?.Count ?? 0,
                    streamId
                );
                hadHttpError = false; // Fallback succeeded
            }
            catch (HttpRequestException ex)
            {
                _logger.PluginLogWarning(
                    "get_simple_data_table failed for stream {StreamId}: {Error}",
                    streamId,
                    ex.Message
                );
                hadHttpError = true;
            }
            catch (JsonException ex)
            {
                _logger.PluginLogWarning(
                    "get_simple_data_table JSON parse failed for stream {StreamId}: {Error}",
                    streamId,
                    ex.Message
                );
            }
        }

        // Only record failure for actual HTTP errors (server unreachable, etc.)
        if (hadHttpError)
        {
            RecordFailure();
        }

        if (epgs?.Listings == null || epgs.Listings.Count == 0)
        {
            // No EPG data for this channel is normal - don't count as failure
            // Only actual HTTP/JSON errors should trigger the circuit breaker
            _logger.PluginLogInformation("No EPG data available from Xtream API for stream {StreamId}", streamId);
            return Array.Empty<EpgProgram>();
        }

        // Success - reset failure counter (only on actual successful data retrieval)
        RecordSuccess();

        // Convert to unified model
        var programs = new List<EpgProgram>(epgs.Listings.Count);
        foreach (var epg in epgs.Listings)
        {
            programs.Add(
                new EpgProgram
                {
                    Id = epg.Id != 0 ? epg.Id : HashCode.Combine(epg.Title, epg.Start),
                    Title = epg.Title,
                    Description = epg.Description,
                    StartUtc = epg.Start,
                    EndUtc = epg.End,
                    ImageUrl = epg.ThumbImageUrl,
                }
            );
        }

        _logger.LogDebugIfEnabled(
            "{ProviderName} returned {Count} programs for stream {StreamId}",
            Name,
            programs.Count,
            streamId
        );

        return programs;
    }

    private void RecordSuccess()
    {
        _consecutiveFailures = 0;
        _isAvailable = true;
    }

    private void RecordFailure()
    {
        _consecutiveFailures++;
        _lastFailureTime = DateTime.UtcNow;

        if (_consecutiveFailures >= MaxConsecutiveFailures)
        {
            _isAvailable = false;
            _logger.PluginLogWarning(
                "{Provider} circuit breaker triggered after {Failures} consecutive failures. "
                    + "Provider disabled for {Minutes} minutes.",
                Name,
                _consecutiveFailures,
                CircuitBreakerResetTime.TotalMinutes
            );
        }
    }
}
