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
using System.Xml;
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Service.ChannelMatching;
using Jellyfin.Xtream.Utility;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.Epg;

/// <summary>
/// EPG provider using external XMLTV sources like epg.ovh.
/// Supports configurable URL, channel ID mapping, and logo fallback.
/// Maps channel names (not stream IDs) to EPG data using display-name variants.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ExternalXmltvEpgProvider"/> class.
/// </remarks>
/// <param name="httpClientFactory">HTTP client factory.</param>
/// <param name="memoryCache">Memory cache.</param>
/// <param name="logger">Logger.</param>
public sealed class ExternalXmltvEpgProvider(
    IHttpClientFactory httpClientFactory,
    IMemoryCache memoryCache,
    ILogger<ExternalXmltvEpgProvider> logger
) : IEpgProviderWithPrewarm, IDisposable
{
    private const string CacheKeyPrefix = "external-xmltv-epg-";
    private const string ChannelMapCacheKeyPrefix = "external-xmltv-channels-";
    private const string LogoMapCacheKeyPrefix = "external-xmltv-logos-";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(6);
    private static readonly TimeSpan UnavailableCooldown = TimeSpan.FromMinutes(15);
    private static readonly string[] DateTimeFormats =
    [
        "yyyyMMddHHmmss zzz",
        "yyyyMMddHHmmss +HHmm",
        "yyyyMMddHHmmss -HHmm",
    ];

    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly IMemoryCache _memoryCache = memoryCache;
    private readonly ILogger<ExternalXmltvEpgProvider> _logger = logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    private bool _isAvailable = true;
    private DateTime _lastFailureTime = DateTime.MinValue;

    /// <inheritdoc />
    public string Name => "External XMLTV";

    /// <inheritdoc />
    public int Priority => 15; // Between Xtream (10) and built-in XMLTV (20)

    /// <inheritdoc />
    public bool IsAvailable
    {
        get
        {
            if (!Plugin.Instance.Configuration.EnableExternalEpg)
            {
                return false;
            }

            if (!_isAvailable && DateTime.UtcNow - _lastFailureTime > UnavailableCooldown)
            {
                _isAvailable = true;
                _logger.PluginLogInformation("{Provider} cooldown expired - provider available again", Name);
            }

            return _isAvailable;
        }
    }

    /// <inheritdoc />
    public async Task PrewarmAsync(CancellationToken cancellationToken)
    {
        if (!Plugin.Instance.Configuration.EnableExternalEpg)
        {
            _logger.LogDebugIfEnabled("{Provider} is disabled, skipping pre-warm", Name);
            return;
        }

        var urls = GetConfiguredUrls();
        if (urls.Count == 0)
        {
            _logger.LogDebugIfEnabled("{Provider} has no configured URLs, skipping pre-warm", Name);
            return;
        }

        _logger.PluginLogInformation("{Provider} pre-warming cache for {Count} sources...", Name, urls.Count);

        var tasks = urls.Select(url => LoadXmltvDataAsync(url, cancellationToken));
        _ = await Task.WhenAll(tasks).ConfigureAwait(false);

        _logger.PluginLogInformation("{Provider} cache pre-warmed", Name);
    }

    /// <inheritdoc />
    /// <remarks>
    /// This method is part of the IEpgProvider interface but external XMLTV sources
    /// like epg.ovh use channel names, not stream IDs. Use <see cref="GetProgramsByNameAsync"/>
    /// for better matching. This implementation returns empty results.
    /// </remarks>
    public Task<IReadOnlyList<EpgProgram>> GetProgramsAsync(int streamId, CancellationToken cancellationToken) =>
        // External XMLTV sources use channel names, not stream IDs
        // This method can't match without knowing the channel name
        // The composite provider should call GetProgramsByNameAsync instead
        Task.FromResult<IReadOnlyList<EpgProgram>>([]);

    /// <summary>
    /// Gets EPG programs by channel name (the primary lookup method for external XMLTV).
    /// </summary>
    /// <param name="channelName">The channel name to look up (e.g., "TVP 1", "Polsat HD").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Collection of EPG programs, empty if none available.</returns>
    public async Task<IReadOnlyList<EpgProgram>> GetProgramsByNameAsync(
        string channelName,
        CancellationToken cancellationToken
    )
    {
        if (!Plugin.Instance.Configuration.EnableExternalEpg || string.IsNullOrWhiteSpace(channelName))
        {
            return [];
        }

        var urls = GetConfiguredUrls();
        if (urls.Count == 0)
        {
            return [];
        }

        // Normalize the channel name for matching
        var normalizedName = NormalizeChannelName(channelName);

        foreach (var url in urls)
        {
            var (programs, channelMap) = await GetOrLoadXmltvDataAsync(url, cancellationToken).ConfigureAwait(false);
            if (programs == null || channelMap == null)
            {
                continue;
            }

            // Try to find the channel ID using the display-name mapping
            if (channelMap.TryGetValue(normalizedName, out var channelId))
            {
                if (programs.TryGetValue(channelId, out var programList) && programList.Count > 0)
                {
                    _logger.LogDebugIfEnabled(
                        "{Provider} returned {Count} programs for channel '{ChannelName}' (matched to '{ChannelId}')",
                        Name,
                        programList.Count,
                        channelName,
                        channelId
                    );
                    return programList;
                }
            }

            // Also try direct channel ID lookup (in case the XMLTV uses names as IDs)
            if (programs.TryGetValue(normalizedName, out var directPrograms) && directPrograms.Count > 0)
            {
                _logger.LogDebugIfEnabled(
                    "{Provider} returned {Count} programs for channel '{ChannelName}' (direct match)",
                    Name,
                    directPrograms.Count,
                    channelName
                );
                return directPrograms;
            }
        }

        return [];
    }

    /// <inheritdoc />
    public void Dispose() => _loadLock.Dispose();

    /// <summary>
    /// Gets the logo URL from external EPG source for a channel.
    /// Returns the actual icon URL from the XMLTV source, or falls back to constructed URL.
    /// </summary>
    /// <param name="channelName">The channel name to look up.</param>
    /// <returns>The logo URL or null.</returns>
    public string? GetLogoUrl(string channelName)
    {
        var config = Plugin.Instance.Configuration;
        if (!config.EnableExternalEpg || string.IsNullOrWhiteSpace(channelName))
        {
            return null;
        }

        var urls = GetConfiguredUrls();
        if (urls.Count == 0)
        {
            return null;
        }

        var normalizedName = NormalizeChannelName(channelName);

        // Check cached logo mappings from parsed XMLTV
        foreach (var url in urls)
        {
            var logoMapCacheKey = LogoMapCacheKeyPrefix + url.GetHashCode(StringComparison.Ordinal);
            if (
                _memoryCache.TryGetValue(logoMapCacheKey, out Dictionary<string, string>? logoMap)
                && logoMap != null
                && logoMap.TryGetValue(normalizedName, out var logoUrl)
            )
            {
                _logger.LogDebugIfEnabled(
                    "{Provider} returning cached logo URL for '{ChannelName}': {LogoUrl}",
                    Name,
                    channelName,
                    logoUrl
                );
                return logoUrl;
            }
        }

        // Fall back to constructed URL if base URL is configured
        if (!string.IsNullOrEmpty(config.ExternalEpgLogoBaseUrl))
        {
            // Use URL encoding with + for spaces (epg.ovh format)
            var encodedName = Uri.EscapeDataString(channelName.Trim()).Replace("%20", "+", StringComparison.Ordinal);

            var baseUrl = config.ExternalEpgLogoBaseUrl.TrimEnd('/');
            var fallbackUrl = $"{baseUrl}/{encodedName}.png";

            _logger.LogDebugIfEnabled(
                "{Provider} using fallback logo URL for '{ChannelName}': {LogoUrl}",
                Name,
                channelName,
                fallbackUrl
            );

            return fallbackUrl;
        }

        return null;
    }

    private static List<string> GetConfiguredUrls()
    {
        var config = Plugin.Instance.Configuration;
        var urls = new List<string>();

        if (!string.IsNullOrWhiteSpace(config.ExternalEpgUrl))
        {
            urls.Add(config.ExternalEpgUrl.Trim());
        }

        return urls;
    }

    /// <summary>
    /// Normalizes a channel name for matching against XMLTV display-names.
    /// Uses the shared ChannelNameNormalizer for consistent matching across the plugin.
    /// </summary>
    private static string NormalizeChannelName(string name) => ChannelNameNormalizer.Default.Normalize(name);

    private async Task<(
        Dictionary<string, List<EpgProgram>>? Programs,
        Dictionary<string, string>? ChannelMap
    )> GetOrLoadXmltvDataAsync(string url, CancellationToken cancellationToken)
    {
        var programsCacheKey = CacheKeyPrefix + url.GetHashCode(StringComparison.Ordinal);
        var channelMapCacheKey = ChannelMapCacheKeyPrefix + url.GetHashCode(StringComparison.Ordinal);
        var logoMapCacheKey = LogoMapCacheKeyPrefix + url.GetHashCode(StringComparison.Ordinal);

        return
            _memoryCache.TryGetValue(programsCacheKey, out Dictionary<string, List<EpgProgram>>? cachedPrograms)
            && _memoryCache.TryGetValue(channelMapCacheKey, out Dictionary<string, string>? cachedChannelMap)
            && _memoryCache.TryGetValue(logoMapCacheKey, out Dictionary<string, string>? _)
            && cachedPrograms != null
            && cachedChannelMap != null
            ? (cachedPrograms, cachedChannelMap)
            : await LoadXmltvDataAsync(url, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(
        Dictionary<string, List<EpgProgram>>? Programs,
        Dictionary<string, string>? ChannelMap
    )> LoadXmltvDataAsync(string url, CancellationToken cancellationToken)
    {
        var programsCacheKey = CacheKeyPrefix + url.GetHashCode(StringComparison.Ordinal);
        var channelMapCacheKey = ChannelMapCacheKeyPrefix + url.GetHashCode(StringComparison.Ordinal);
        var logoMapCacheKey = LogoMapCacheKeyPrefix + url.GetHashCode(StringComparison.Ordinal);

        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring lock
            if (
                _memoryCache.TryGetValue(programsCacheKey, out Dictionary<string, List<EpgProgram>>? cachedPrograms)
                && _memoryCache.TryGetValue(channelMapCacheKey, out Dictionary<string, string>? cachedChannelMap)
                && _memoryCache.TryGetValue(logoMapCacheKey, out Dictionary<string, string>? _)
                && cachedPrograms != null
                && cachedChannelMap != null
            )
            {
                return (cachedPrograms, cachedChannelMap);
            }

            _logger.PluginLogInformation("Loading external XMLTV EPG data from {Url}...", url);

            var httpClient = _httpClientFactory.CreateClient(HttpClientConfiguration.XtreamClientName);

            using var response = await httpClient
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            _ = response.EnsureSuccessStatusCode();

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var (programs, channelMap, logoMap) = await ParseXmltvAsync(stream, cancellationToken)
                    .ConfigureAwait(false);

                _logger.PluginLogInformation(
                    "Loaded external XMLTV EPG data from {Url}: {ChannelCount} channels, {DisplayNameCount} display-names, {LogoCount} logos, {ProgramCount} total programs",
                    url,
                    programs.Count,
                    channelMap.Count,
                    logoMap.Count,
                    programs.Values.Sum(p => p.Count)
                );

                _ = _memoryCache.Set(programsCacheKey, programs, CacheDuration);
                _ = _memoryCache.Set(channelMapCacheKey, channelMap, CacheDuration);
                _ = _memoryCache.Set(logoMapCacheKey, logoMap, CacheDuration);
                return (programs, channelMap);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.PluginLogWarning(ex, "Failed to load external XMLTV data from {Url}: {Message}", url, ex.Message);
            MarkUnavailable();
            return (null, null);
        }
        catch (XmlException ex)
        {
            _logger.PluginLogWarning(ex, "Failed to parse external XMLTV data from {Url}: {Message}", url, ex.Message);
            MarkUnavailable();
            return (null, null);
        }
        finally
        {
            _ = _loadLock.Release();
        }
    }

    private void MarkUnavailable()
    {
        _isAvailable = false;
        _lastFailureTime = DateTime.UtcNow;
        _logger.PluginLogWarning(
            "{Provider} marked unavailable for {Minutes} minutes",
            Name,
            UnavailableCooldown.TotalMinutes
        );
    }

    private static async Task<(
        Dictionary<string, List<EpgProgram>> Programs,
        Dictionary<string, string> ChannelMap,
        Dictionary<string, string> LogoMap
    )> ParseXmltvAsync(Stream stream, CancellationToken cancellationToken)
    {
        var programsByChannel = new Dictionary<string, List<EpgProgram>>(StringComparer.OrdinalIgnoreCase);

        // Map from normalized display-name to channel id
        // This allows matching "TVP 1 HD" -> "TVP 1" channel id
        var displayNameToChannelId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Map from normalized display-name to logo URL (from <icon> elements)
        var displayNameToLogoUrl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var settings = new XmlReaderSettings
        {
            Async = true,
            IgnoreWhitespace = true,
            IgnoreComments = true,
            DtdProcessing = DtdProcessing.Ignore,
        };

        using var reader = XmlReader.Create(stream, settings);

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.Name)
                {
                    case "channel":
                        ParseChannelElement(reader, displayNameToChannelId, displayNameToLogoUrl);
                        break;
                    case "programme":
                        var channelId = reader.GetAttribute("channel") ?? string.Empty;
                        if (!string.IsNullOrEmpty(channelId))
                        {
                            var program = ParseProgramElement(reader);
                            if (program != null)
                            {
                                if (!programsByChannel.TryGetValue(channelId, out var list))
                                {
                                    list = [];
                                    programsByChannel[channelId] = list;
                                }

                                list.Add(program);
                            }
                        }

                        break;
                }
            }
        }

        return (programsByChannel, displayNameToChannelId, displayNameToLogoUrl);
    }

    /// <summary>
    /// Parses a channel element and extracts all display-name variants and icon URL.
    /// Maps each normalized display-name to the channel's id attribute and logo URL.
    /// </summary>
    private static void ParseChannelElement(
        XmlReader reader,
        Dictionary<string, string> displayNameToChannelId,
        Dictionary<string, string> displayNameToLogoUrl
    )
    {
        var channelId = reader.GetAttribute("id");
        if (string.IsNullOrEmpty(channelId))
        {
            return;
        }

        // Also map the channel ID itself (normalized)
        var normalizedId = NormalizeChannelName(channelId);
        if (!string.IsNullOrEmpty(normalizedId))
        {
            _ = displayNameToChannelId.TryAdd(normalizedId, channelId);
        }

        if (reader.IsEmptyElement)
        {
            return;
        }

        // Collect display names and icon URL from this channel
        var displayNames = new List<string>();
        string? iconUrl = null;

        // Read all child elements within this channel
        var subtree = reader.ReadSubtree();
        while (subtree.Read())
        {
            if (subtree.NodeType == XmlNodeType.Element)
            {
                switch (subtree.Name)
                {
                    case "display-name":
                        var displayName = subtree.ReadElementContentAsString();
                        if (!string.IsNullOrWhiteSpace(displayName))
                        {
                            displayNames.Add(displayName);
                            var normalized = NormalizeChannelName(displayName);
                            if (!string.IsNullOrEmpty(normalized))
                            {
                                // TryAdd - first mapping wins (typically the canonical name)
                                _ = displayNameToChannelId.TryAdd(normalized, channelId);
                            }
                        }

                        break;
                    case "icon":
                        // Get the icon URL from src attribute
                        iconUrl ??= subtree.GetAttribute("src");
                        break;
                }
            }
        }

        // Map all display name variants to the icon URL
        if (!string.IsNullOrEmpty(iconUrl))
        {
            foreach (var displayName in displayNames)
            {
                var normalized = NormalizeChannelName(displayName);
                if (!string.IsNullOrEmpty(normalized))
                {
                    _ = displayNameToLogoUrl.TryAdd(normalized, iconUrl);
                }
            }

            // Also map the normalized channel ID to the icon
            if (!string.IsNullOrEmpty(normalizedId))
            {
                _ = displayNameToLogoUrl.TryAdd(normalizedId, iconUrl);
            }
        }
    }

    private static EpgProgram? ParseProgramElement(XmlReader reader)
    {
        var startStr = reader.GetAttribute("start");
        var stopStr = reader.GetAttribute("stop");

        if (string.IsNullOrEmpty(startStr))
        {
            return null;
        }

        var startUtc = ParseXmltvDateTime(startStr);
        var endUtc = ParseXmltvDateTime(stopStr);

        // Collect mutable data during parsing
        var title = string.Empty;
        string? description = null;
        string? imageUrl = null;
        List<string>? categories = null;

        if (!reader.IsEmptyElement)
        {
            var subtree = reader.ReadSubtree();
            while (subtree.Read())
            {
                if (subtree.NodeType == XmlNodeType.Element)
                {
                    switch (subtree.Name)
                    {
                        case "title":
                            title = subtree.ReadElementContentAsString();
                            break;
                        case "desc":
                            description = subtree.ReadElementContentAsString();
                            break;
                        case "icon":
                            imageUrl = subtree.GetAttribute("src");
                            break;
                        case "category":
                            categories ??= new List<string>(4);
                            categories.Add(subtree.ReadElementContentAsString());
                            break;
                    }
                }
            }
        }

        // Create immutable EpgProgram with all collected data
        return new EpgProgram
        {
            Id = HashCode.Combine(startStr, stopStr),
            Title = title,
            Description = description,
            StartUtc = startUtc,
            EndUtc = endUtc,
            ImageUrl = imageUrl,
            Categories = categories ?? [],
        };
    }

    private static DateTime ParseXmltvDateTime(string? dateStr)
    {
        if (string.IsNullOrEmpty(dateStr))
        {
            return DateTime.MinValue;
        }

        try
        {
            if (
                dateStr.Length >= 20
                && (dateStr.Contains('+', StringComparison.Ordinal) || dateStr.Contains('-', StringComparison.Ordinal))
            )
            {
                if (
                    DateTimeOffset.TryParseExact(
                        dateStr,
                        DateTimeFormats,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal,
                        out var dto
                    )
                )
                {
                    return dto.UtcDateTime;
                }

                var datePart = dateStr[..14];
                var tzPart = dateStr[15..].Trim();

                if (
                    DateTime.TryParseExact(
                        datePart,
                        "yyyyMMddHHmmss",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var dt
                    )
                )
                {
                    if (tzPart.Length >= 4)
                    {
                        var sign = tzPart[0] == '-' ? -1 : 1;
                        var offsetStr = tzPart.AsSpan().TrimStart(['+', '-']);
                        if (
                            offsetStr.Length >= 4
                            && int.TryParse(offsetStr[..2], out var hours)
                            && int.TryParse(offsetStr.Slice(2, 2), out var minutes)
                        )
                        {
                            var offset = new TimeSpan(sign * hours, sign * minutes, 0);
                            return dt.Add(-offset).ToUniversalTime();
                        }
                    }

                    return dt.ToUniversalTime();
                }
            }

            if (
                DateTime.TryParseExact(
                    dateStr.Trim(),
                    "yyyyMMddHHmmss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var result
                )
            )
            {
                return result.ToUniversalTime();
            }
        }
        catch
        {
            // Fall through to return MinValue
        }

        return DateTime.MinValue;
    }
}
