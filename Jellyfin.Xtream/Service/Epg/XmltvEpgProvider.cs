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
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Utility;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.Epg;

/// <summary>
/// EPG provider using XMLTV format.
/// Used as fallback when standard Xtream API endpoints are unavailable.
/// Supports pre-warming to load data in background on startup.
/// </summary>
public sealed class XmltvEpgProvider : IEpgProviderWithPrewarm, IDisposable
{
    private const string XmltvCacheKey = "xtream-xmltv-epg";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(4);
    private static readonly TimeSpan _unavailableCooldown = TimeSpan.FromMinutes(10);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<XmltvEpgProvider> _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    private bool _isAvailable = true;
    private DateTime _lastFailureTime = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="XmltvEpgProvider"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="memoryCache">Memory cache.</param>
    /// <param name="logger">Logger.</param>
    public XmltvEpgProvider(
        IHttpClientFactory httpClientFactory,
        IMemoryCache memoryCache,
        ILogger<XmltvEpgProvider> logger
    )
    {
        _httpClientFactory = httpClientFactory;
        _memoryCache = memoryCache;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "XMLTV";

    /// <inheritdoc />
    public int Priority => 20; // Fallback provider

    /// <inheritdoc />
    public bool IsAvailable
    {
        get
        {
            // Reset availability after cooldown
            if (!_isAvailable && DateTime.UtcNow - _lastFailureTime > _unavailableCooldown)
            {
                _isAvailable = true;
                _logger.LogInformation("{Provider} cooldown expired - provider available again", Name);
            }

            return _isAvailable;
        }
    }

    /// <inheritdoc />
    public async Task PrewarmAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("{Provider} pre-warming cache...", Name);
        try
        {
            await GetOrLoadXmltvDataAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("{Provider} cache pre-warmed successfully", Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Provider} pre-warm failed: {Message}", Name, ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EpgProgram>> GetProgramsAsync(int streamId, CancellationToken cancellationToken)
    {
        var xmltvData = await GetOrLoadXmltvDataAsync(cancellationToken).ConfigureAwait(false);

        if (xmltvData == null)
        {
            return Array.Empty<EpgProgram>();
        }

        // Try to find programs by stream ID (channel ID in XMLTV)
        string streamIdStr = streamId.ToString(CultureInfo.InvariantCulture);

        if (xmltvData.TryGetValue(streamIdStr, out var programs))
        {
            _logger.LogDebugIfEnabled(
                "{ProviderName} returned {Count} programs for stream {StreamId}",
                Name,
                programs.Count,
                streamId
            );
            return programs;
        }

        // Some providers prefix with channel type
        if (xmltvData.TryGetValue($"live_{streamIdStr}", out programs))
        {
            _logger.LogDebugIfEnabled(
                "{ProviderName} returned {Count} programs for stream {StreamId} (live_ prefix)",
                Name,
                programs.Count,
                streamId
            );
            return programs;
        }

        return Array.Empty<EpgProgram>();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _loadLock.Dispose();
    }

    /// <summary>
    /// Gets or loads the XMLTV data from cache or remote.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<EpgProgram>>?> GetOrLoadXmltvDataAsync(
        CancellationToken cancellationToken
    )
    {
        if (
            _memoryCache.TryGetValue(
                XmltvCacheKey,
                out IReadOnlyDictionary<string, IReadOnlyList<EpgProgram>>? cachedData
            )
            && cachedData != null
        )
        {
            return cachedData;
        }

        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring lock
            if (_memoryCache.TryGetValue(XmltvCacheKey, out cachedData) && cachedData != null)
            {
                return cachedData;
            }

            var provider = Plugin.Instance.Configuration.GetEnabledProviders().FirstOrDefault();
            if (provider == null)
            {
                _logger.LogWarning("No enabled provider found for XMLTV data");
                return null;
            }

            using var client = new XtreamClient(_httpClientFactory, _logger as ILogger<XtreamClient>);
            string xmltvUrl = client.GetXmltvUrl(provider.ToConnectionInfo());

            _logger.LogInformation("Loading XMLTV EPG data from provider...");

            var httpClient = _httpClientFactory.CreateClient(HttpClientConfiguration.XtreamClientName);

            using var response = await httpClient
                .GetAsync(xmltvUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var data = await ParseXmltvAsync(stream, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Loaded XMLTV EPG data: {ChannelCount} channels, {ProgramCount} total programs",
                    data.Count,
                    data.Values.Sum(p => p.Count)
                );

                _memoryCache.Set(XmltvCacheKey, data, CacheDuration);
                return data;
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to load XMLTV data: {Message}", ex.Message);
            MarkUnavailable();
            return null;
        }
        catch (Exception ex)
            when (ex is System.Xml.XmlException || ex.Message.Contains("XML", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(ex, "Failed to parse XMLTV data: {Message}", ex.Message);
            MarkUnavailable();
            return null;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private void MarkUnavailable()
    {
        _isAvailable = false;
        _lastFailureTime = DateTime.UtcNow;
        _logger.LogWarning(
            "{Provider} marked unavailable for {Minutes} minutes",
            Name,
            _unavailableCooldown.TotalMinutes
        );
    }

    /// <summary>
    /// Parses XMLTV data from a stream using high-performance TurboXml parser.
    /// </summary>
    /// <remarks>
    /// This method uses synchronous TurboXml parsing which is faster than async XmlReader.
    /// The async version with XmlReader allocated ~64KB buffers on LOH and had
    /// ~10-15% overhead compared to synchronous parsing.
    /// Returns Task.FromResult to avoid CS1998 while maintaining the async signature for interface compatibility.
    /// </remarks>
    private static Task<IReadOnlyDictionary<string, IReadOnlyList<EpgProgram>>> ParseXmltvAsync(
        Stream stream,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Use TurboXml for high-performance, zero-allocation parsing
        return Task.FromResult(TurboXmltvParser.Parse(stream));
    }
}
