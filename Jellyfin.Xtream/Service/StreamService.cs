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
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service.ProviderManagement;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// A service for dealing with stream information.
/// </summary>
public static partial class StreamService
{
    /// <summary>
    /// The id prefix for VOD category channel items.
    /// </summary>
    public const int VodCategoryPrefix = 0x5d774c35;

    /// <summary>
    /// The id prefix for stream channel items.
    /// </summary>
    public const int StreamPrefix = 0x5d774c36;

    /// <summary>
    /// The id prefix for series category channel items.
    /// </summary>
    public const int SeriesCategoryPrefix = 0x5d774c37;

    /// <summary>
    /// The id prefix for series category channel items.
    /// </summary>
    public const int SeriesPrefix = 0x5d774c38;

    /// <summary>
    /// The id prefix for season channel items.
    /// </summary>
    public const int SeasonPrefix = 0x5d774c39;

    /// <summary>
    /// The id prefix for season channel items.
    /// </summary>
    public const int EpisodePrefix = 0x5d774c3a;

    /// <summary>
    /// The id prefix for catchup channel items.
    /// </summary>
    public const int CatchupPrefix = 0x5d774c3b;

    /// <summary>
    /// The id prefix for catchup stream items.
    /// </summary>
    public const int CatchupStreamPrefix = 0x5d774c3c;

    /// <summary>
    /// The id prefix for media source items.
    /// </summary>
    public const int MediaSourcePrefix = 0x5d774c3d;

    /// <summary>
    /// The id prefix for Live TV items.
    /// </summary>
    public const int LiveTvPrefix = 0x5d774c3e;

    /// <summary>
    /// The id prefix for TV EPG items.
    /// </summary>
    public const int EpgPrefix = 0x5d774c3f;

    private static readonly Regex _tagRegex = TagRegex();

    /// <summary>
    /// Parses tags in the name of a stream entry.
    /// The name commonly contains tags of the forms:
    /// <list>
    /// <item>[TAG]</item>
    /// <item>|TAG|</item>
    /// </list>
    /// These tags are parsed and returned as separate strings.
    /// The returned title is cleaned from tags and trimmed.
    /// </summary>
    /// <param name="name">The name which should be parsed.</param>
    /// <returns>A <see cref="ParsedName"/> struct containing the cleaned title and parsed tags.</returns>
    public static ParsedName ParseName(string name)
    {
        List<string> tags = [];
        var title = _tagRegex.Replace(
            name,
            (match) =>
            {
                for (var i = 1; i < match.Groups.Count; ++i)
                {
                    var g = match.Groups[i];
                    if (g.Success)
                    {
                        tags.Add(g.Value);
                    }
                }

                return string.Empty;
            }
        );

        // Tag prefixes separated by the a character in the unicode Block Elements range
        var stripLength = 0;
        for (var i = 0; i < title.Length; i++)
        {
            var c = title[i];
            if (c is >= '\u2580' and <= '\u259F')
            {
                tags.Add(title[stripLength..i].Trim());
                stripLength = i + 1;
            }
        }

        return new ParsedName { Title = title[stripLength..].Trim(), Tags = [.. tags] };
    }

    private static bool IsConfigured(SerializableDictionary<int, HashSet<int>> config, int category, int id) =>
        config.TryGetValue(category, out var values) && (values.Count == 0 || values.Contains(id));

    /// <summary>
    /// Gets live streams for a specific provider.
    /// </summary>
    /// <param name="provider">The provider to fetch streams from.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Filtered streams based on provider configuration.</returns>
    public static async Task<IEnumerable<StreamInfo>> GetLiveStreamsForProvider(
        XtreamProvider provider,
        CancellationToken cancellationToken
    )
    {
        using var client = Plugin.Instance.CreateXtreamClient();
        return (
            await client.GetLiveStreamsAsync(provider.ToConnectionInfo(), cancellationToken).ConfigureAwait(false)
        ).Where(channel =>
            channel.CategoryId.HasValue && IsConfigured(provider.LiveTv, channel.CategoryId.Value, channel.StreamId)
        );
    }

    /// <summary>
    /// Gets live streams for a specific provider with overrides applied.
    /// </summary>
    /// <param name="provider">The provider to fetch streams from.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Filtered streams with overrides applied.</returns>
    public static async Task<IEnumerable<StreamInfo>> GetLiveStreamsWithOverridesForProvider(
        XtreamProvider provider,
        CancellationToken cancellationToken
    )
    {
        return (await GetLiveStreamsForProvider(provider, cancellationToken).ConfigureAwait(false)).Select(stream =>
        {
            if (provider.LiveTvOverrides.TryGetValue(stream.StreamId, out var value))
            {
                stream.Num = value.Number ?? stream.Num;
                stream.Name = value.Name ?? stream.Name;
                stream.StreamIcon = value.LogoUrl ?? stream.StreamIcon;
            }

            return stream;
        });
    }

    /// <summary>
    /// Gets all live streams from all enabled providers.
    /// Fetches from multiple providers in parallel for improved performance.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>All streams from all enabled providers with provider info.</returns>
    public static async Task<IEnumerable<ProviderStreamInfo>> GetAllLiveStreams(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;
        List<XtreamProvider> providers = [.. config.GetEnabledProviders()];

        if (providers.Count == 0)
        {
            return [];
        }

        if (providers.Count == 1)
        {
            var provider = providers[0];
            var streams = await GetLiveStreamsWithOverridesForProvider(provider, cancellationToken)
                .ConfigureAwait(false);
            var streamList = streams as IList<StreamInfo> ?? [.. streams];
            var result = new List<ProviderStreamInfo>(streamList.Count);

            foreach (var s in streamList)
            {
                result.Add(new ProviderStreamInfo(provider, s));
            }

            return result;
        }

        var tasks = new Task<List<ProviderStreamInfo>>[providers.Count];

        for (var i = 0; i < providers.Count; i++)
        {
            var provider = providers[i];
            tasks[i] = FetchProviderStreamsAsync(provider, cancellationToken);
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var totalCount = 0;

        foreach (var result in results)
        {
            totalCount += result.Count;
        }

        var combined = new List<ProviderStreamInfo>(totalCount);

        foreach (var result in results)
        {
            combined.AddRange(result);
        }

        return combined;
    }

    private static async Task<List<ProviderStreamInfo>> FetchProviderStreamsAsync(
        XtreamProvider provider,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var streams = await GetLiveStreamsWithOverridesForProvider(provider, cancellationToken)
                .ConfigureAwait(false);
            var streamList = streams as IList<StreamInfo> ?? [.. streams];
            var result = new List<ProviderStreamInfo>(streamList.Count);

            foreach (var s in streamList)
            {
                result.Add(new ProviderStreamInfo(provider, s));
            }

            return result;
        }
        catch (HttpRequestException ex)
        {
            Utility.PluginLogger.DirectLog(
                Microsoft.Extensions.Logging.LogLevel.Warning,
                nameof(StreamService),
                $"Failed to fetch streams from provider '{provider.Name}': {ex.Message}. Provider will be excluded from channel map."
            );
            return [];
        }
        catch (OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>
    /// Gets an channel item info for the category.
    /// </summary>
    /// <param name="prefix">The channel category prefix.</param>
    /// <param name="category">The Xtream category.</param>
    /// <returns>A channel item representing the category.</returns>
    public static ChannelItemInfo CreateChannelItemInfo(int prefix, Category category)
    {
        var parsedName = ParseName(category.CategoryName);
        return new ChannelItemInfo()
        {
            Id = ToGuid(prefix, category.CategoryId, 0, 0).ToString(),
            Name = category.CategoryName,
            Tags = [.. parsedName.Tags],
            Type = ChannelItemType.Folder,
        };
    }

    /// <summary>
    /// Gets VOD categories for a specific provider.
    /// </summary>
    /// <param name="provider">The provider to fetch from.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Filtered categories based on provider configuration.</returns>
    public static async Task<IEnumerable<Category>> GetVodCategoriesForProvider(
        XtreamProvider provider,
        CancellationToken cancellationToken
    )
    {
        using var client = Plugin.Instance.CreateXtreamClient();
        return (
            await client.GetVodCategoryAsync(provider.ToConnectionInfo(), cancellationToken).ConfigureAwait(false)
        ).Where(category => provider.Vod.ContainsKey(category.CategoryId));
    }

    /// <summary>
    /// Gets all VOD categories from all enabled providers.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>All categories from all enabled providers.</returns>
    public static async Task<IEnumerable<ProviderCategory>> GetAllVodCategories(CancellationToken cancellationToken)
    {
        List<ProviderCategory> results = [];
        var config = Plugin.Instance.Configuration;

        foreach (var provider in config.GetEnabledProviders())
        {
            results.AddRange(
                (await GetVodCategoriesForProvider(provider, cancellationToken).ConfigureAwait(false)).Select(
                    c => new ProviderCategory(provider, c)
                )
            );
        }

        return results;
    }

    /// <summary>
    /// Gets VOD streams for a specific provider and category.
    /// </summary>
    /// <param name="provider">The provider to fetch from.</param>
    /// <param name="categoryId">The Xtream id of the category.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Filtered streams based on provider configuration.</returns>
    public static async Task<IEnumerable<StreamInfo>> GetVodStreamsForProvider(
        XtreamProvider provider,
        int categoryId,
        CancellationToken cancellationToken
    )
    {
        if (!provider.Vod.ContainsKey(categoryId))
        {
            return [];
        }

        using var client = Plugin.Instance.CreateXtreamClient();
        return (
            await client
                .GetVodStreamsByCategoryAsync(provider.ToConnectionInfo(), categoryId, cancellationToken)
                .ConfigureAwait(false)
        ).Where(stream => IsConfigured(provider.Vod, categoryId, stream.StreamId));
    }

    /// <summary>
    /// Gets Series categories for a specific provider.
    /// </summary>
    /// <param name="provider">The provider to fetch from.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Filtered categories based on provider configuration.</returns>
    public static async Task<IEnumerable<Category>> GetSeriesCategoriesForProvider(
        XtreamProvider provider,
        CancellationToken cancellationToken
    )
    {
        using var client = Plugin.Instance.CreateXtreamClient();
        return (
            await client.GetSeriesCategoryAsync(provider.ToConnectionInfo(), cancellationToken).ConfigureAwait(false)
        ).Where(category => provider.Series.ContainsKey(category.CategoryId));
    }

    /// <summary>
    /// Gets all Series categories from all enabled providers.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>All categories from all enabled providers.</returns>
    public static async Task<IEnumerable<ProviderCategory>> GetAllSeriesCategories(CancellationToken cancellationToken)
    {
        List<ProviderCategory> results = [];
        var config = Plugin.Instance.Configuration;

        foreach (var provider in config.GetEnabledProviders())
        {
            results.AddRange(
                (await GetSeriesCategoriesForProvider(provider, cancellationToken).ConfigureAwait(false)).Select(
                    c => new ProviderCategory(provider, c)
                )
            );
        }

        return results;
    }

    /// <summary>
    /// Gets Series for a specific provider and category.
    /// </summary>
    /// <param name="provider">The provider to fetch from.</param>
    /// <param name="categoryId">The Xtream id of the category.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Filtered series based on provider configuration.</returns>
    public static async Task<IEnumerable<Series>> GetSeriesForProvider(
        XtreamProvider provider,
        int categoryId,
        CancellationToken cancellationToken
    )
    {
        if (!provider.Series.ContainsKey(categoryId))
        {
            return [];
        }

        using var client = Plugin.Instance.CreateXtreamClient();
        return (
            await client
                .GetSeriesByCategoryAsync(provider.ToConnectionInfo(), categoryId, cancellationToken)
                .ConfigureAwait(false)
        ).Where(s => IsConfigured(provider.Series, s.CategoryId, s.SeriesId));
    }

    /// <summary>
    /// Gets seasons for a specific provider and series.
    /// </summary>
    /// <param name="provider">The provider to fetch from.</param>
    /// <param name="seriesId">The Xtream id of the Series.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Seasons with series info.</returns>
    public static async Task<IEnumerable<Tuple<SeriesStreamInfo, int>>> GetSeasonsForProvider(
        XtreamProvider provider,
        int seriesId,
        CancellationToken cancellationToken
    )
    {
        using var client = Plugin.Instance.CreateXtreamClient();
        var series = await client
            .GetSeriesStreamsBySeriesAsync(provider.ToConnectionInfo(), seriesId, cancellationToken)
            .ConfigureAwait(false);

        return !IsConfigured(provider.Series, series.Info.CategoryId, seriesId)
            ? []
            : series.Episodes.Keys.Select(seasonId => new Tuple<SeriesStreamInfo, int>(series, seasonId));
    }

    /// <summary>
    /// Gets episodes for a specific provider, series, and season.
    /// </summary>
    /// <param name="provider">The provider to fetch from.</param>
    /// <param name="seriesId">The Xtream id of the Series.</param>
    /// <param name="seasonId">The Xtream id of the Season.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Episodes with series and season info.</returns>
    public static async Task<IEnumerable<Tuple<SeriesStreamInfo, Season?, Episode>>> GetEpisodesForProvider(
        XtreamProvider provider,
        int seriesId,
        int seasonId,
        CancellationToken cancellationToken
    )
    {
        using var client = Plugin.Instance.CreateXtreamClient();
        var series = await client
            .GetSeriesStreamsBySeriesAsync(provider.ToConnectionInfo(), seriesId, cancellationToken)
            .ConfigureAwait(false);
        var season = series.Seasons.FirstOrDefault(s => s.SeasonId == seasonId);
        return series
            .Episodes[seasonId]
            .Select(episode => new Tuple<SeriesStreamInfo, Season?, Episode>(series, season, episode));
    }

    /// <summary>
    /// Gets a GUID representing the four 32-bit integers.
    /// Optimized using BinaryPrimitives and stackalloc for zero-allocation performance.
    /// </summary>
    /// <param name="i0">Bytes 0-3.</param>
    /// <param name="i1">Bytes 4-7.</param>
    /// <param name="i2">Bytes 8-11.</param>
    /// <param name="i3">Bytes 12-15.</param>
    /// <returns>Guid.</returns>
    public static Guid ToGuid(int i0, int i1, int i2, int i3)
    {
        Span<byte> guidBytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt32BigEndian(guidBytes, i0);
        BinaryPrimitives.WriteInt32BigEndian(guidBytes[4..], i1);
        BinaryPrimitives.WriteInt32BigEndian(guidBytes[8..], i2);
        BinaryPrimitives.WriteInt32BigEndian(guidBytes[12..], i3);
        return new Guid(guidBytes);
    }

    /// <summary>
    /// Gets a GUID representing a stream from a specific provider.
    /// The provider ID is stored in the third slot (i2) as a hash.
    /// </summary>
    /// <param name="prefix">The ID prefix (e.g., LiveTvPrefix).</param>
    /// <param name="provider">The provider this stream belongs to.</param>
    /// <param name="streamId">The stream ID from the provider.</param>
    /// <returns>A unique GUID combining provider and stream information.</returns>
    public static Guid ToProviderGuid(int prefix, XtreamProvider provider, int streamId) =>
        ToGuid(prefix, streamId, provider.GetIdHash(), 0);

    /// <summary>
    /// Gets the four 32-bit integers represented in the GUID.
    /// Optimized using BinaryPrimitives and Span for high-performance decoding.
    /// </summary>
    /// <param name="id">The input GUID.</param>
    /// <param name="i0">Bytes 0-3.</param>
    /// <param name="i1">Bytes 4-7.</param>
    /// <param name="i2">Bytes 8-11.</param>
    /// <param name="i3">Bytes 12-15.</param>
    public static void FromGuid(Guid id, out int i0, out int i1, out int i2, out int i3)
    {
        Span<byte> guidBytes = stackalloc byte[16];

        if (!id.TryWriteBytes(guidBytes))
        {
            throw new InvalidOperationException("Failed to write GUID bytes");
        }

        if (BitConverter.IsLittleEndian)
        {
            guidBytes.Reverse();
            i0 = BinaryPrimitives.ReadInt32LittleEndian(guidBytes[12..]);
            i1 = BinaryPrimitives.ReadInt32LittleEndian(guidBytes[8..]);
            i2 = BinaryPrimitives.ReadInt32LittleEndian(guidBytes[4..]);
            i3 = BinaryPrimitives.ReadInt32LittleEndian(guidBytes);
        }
        else
        {
            i0 = BinaryPrimitives.ReadInt32BigEndian(guidBytes);
            i1 = BinaryPrimitives.ReadInt32BigEndian(guidBytes[4..]);
            i2 = BinaryPrimitives.ReadInt32BigEndian(guidBytes[8..]);
            i3 = BinaryPrimitives.ReadInt32BigEndian(guidBytes[12..]);
        }
    }

    /// <summary>
    /// Parses a GUID to extract provider and stream information.
    /// </summary>
    /// <param name="id">The GUID to parse.</param>
    /// <returns>A ParsedStreamId containing prefix, provider ID, and stream ID.</returns>
    public static ParsedStreamId ParseProviderGuid(Guid id)
    {
        FromGuid(id, out var prefix, out var streamId, out var providerHash, out _);
        var config = Plugin.Instance?.Configuration;
        var providerId = string.Empty;

        if (config != null)
        {
            foreach (var provider in config.Providers)
            {
                if (provider.GetIdHash() == providerHash)
                {
                    providerId = provider.Id;
                    break;
                }
            }
        }

        return new ParsedStreamId(prefix, providerId, streamId);
    }

    /// <summary>
    /// Finds the provider for a given GUID by matching the provider hash.
    /// </summary>
    /// <param name="id">The GUID to parse.</param>
    /// <returns>The matching provider, or null if not found.</returns>
    public static XtreamProvider? FindProviderForGuid(Guid id)
    {
        FromGuid(id, out _, out _, out var providerHash, out _);
        var config = Plugin.Instance?.Configuration;

        if (config == null)
        {
            return null;
        }

        foreach (var provider in config.Providers)
        {
            if (provider.GetIdHash() == providerHash)
            {
                return provider;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the provider for a given GUID string by matching the provider hash.
    /// </summary>
    /// <param name="guidString">The GUID string to parse.</param>
    /// <returns>The matching provider, or null if not found or invalid GUID.</returns>
    public static XtreamProvider? FindProviderForGuid(string guidString) =>
        Guid.TryParse(guidString, out var guid) ? FindProviderForGuid(guid) : null;

    /// <summary>
    /// Gets the media source information for the given Xtream stream.
    /// </summary>
    /// <param name="provider">The provider this stream belongs to.</param>
    /// <param name="type">The stream media type.</param>
    /// <param name="id">The unique identifier of the stream.</param>
    /// <param name="name">The display name of the stream/channel.</param>
    /// <param name="extension">The container extension of the stream.</param>
    /// <param name="restream">Boolean indicating whether or not restreaming is used.</param>
    /// <param name="start">The datetime representing the start time of catchup TV.</param>
    /// <param name="durationMinutes">The duration in minutes of the catchup TV stream.</param>
    /// <param name="videoInfo">The Xtream video info if known.</param>
    /// <param name="audioInfo">The Xtream audio info if known.</param>
    /// <returns>The media source info as <see cref="MediaSourceInfo"/> class.</returns>
    public static MediaSourceInfo GetMediaSourceInfo(
        XtreamProvider provider,
        StreamType type,
        int id,
        string? name = null,
        string? extension = null,
        bool restream = false,
        DateTime? start = null,
        int durationMinutes = 0,
        VideoInfo? videoInfo = null,
        AudioInfo? audioInfo = null
    )
    {
        var pathPrefix = string.Empty;

        switch (type)
        {
            case StreamType.Live:
                pathPrefix = "/live";
                break;
            case StreamType.Series:
                pathPrefix = "/series";
                break;
            case StreamType.Vod:
                pathPrefix = "/movie";
                break;
        }

        var uri = $"{provider.BaseUrl}{pathPrefix}/{provider.Username}/{provider.Password}/{id}";

        if (!string.IsNullOrEmpty(extension))
        {
            uri = uri + "." + extension;
        }

        if (type == StreamType.CatchUp)
        {
            var startString = start?.ToString("yyyy'-'MM'-'dd':'HH'-'mm", CultureInfo.InvariantCulture);
            uri =
                $"{provider.BaseUrl}/streaming/timeshift.php?username={provider.Username}&password={provider.Password}&stream={id}&start={startString}&duration={durationMinutes}";
        }

        var isLive = type == StreamType.Live;

        // Check if ForceRemux is enabled for live streams
        // When enabled, disable direct play/stream to force Jellyfin to use FFmpeg remuxing
        // This fixes audio desync, SPS/PPS issues, and discontinuity handling
        var config = Plugin.Instance.Configuration;
        var forceRemux = isLive && config.ForceRemux;

        return new MediaSourceInfo()
        {
            Container = extension,
            EncoderProtocol = MediaProtocol.Http,
            Id = ToProviderGuid(MediaSourcePrefix, provider, id).ToString(),
            IsInfiniteStream = isLive,
            IsRemote = true,
            MediaStreams =
            [
                new()
                {
                    AspectRatio = videoInfo?.AspectRatio,
                    BitDepth = videoInfo?.BitsPerRawSample,
                    Codec = videoInfo?.CodecName,
                    ColorPrimaries = videoInfo?.ColorPrimaries,
                    ColorRange = videoInfo?.ColorRange,
                    ColorSpace = videoInfo?.ColorSpace,
                    ColorTransfer = videoInfo?.ColorTransfer,
                    Height = videoInfo?.Height,
                    Index = videoInfo?.Index ?? -1,
                    IsAVC = videoInfo?.IsAVC,
                    IsInterlaced = true,
                    Level = videoInfo?.Level,
                    PixelFormat = videoInfo?.PixelFormat,
                    Profile = videoInfo?.Profile,
                    Type = MediaStreamType.Video,
                    Width = videoInfo?.Width,
                },
                new()
                {
                    BitRate = audioInfo?.Bitrate,
                    ChannelLayout = audioInfo?.ChannelLayout,
                    Channels = audioInfo?.Channels,
                    Codec = audioInfo?.CodecName,
                    Index = audioInfo?.Index ?? -1,
                    Profile = audioInfo?.Profile,
                    SampleRate = audioInfo?.SampleRate,
                    Type = MediaStreamType.Audio,
                },
            ],
            Name = name ?? "default",
            Path = uri,
            Protocol = MediaProtocol.Http,
            RequiresClosing = restream,
            RequiresOpening = restream,

            // When ForceRemux is enabled, disable direct play/stream to force FFmpeg remuxing
            // FFmpeg will properly handle SPS/PPS injection and audio sync after discontinuities
            SupportsDirectPlay = !forceRemux,
            SupportsDirectStream = !forceRemux,
            SupportsProbing = true,
            SupportsTranscoding = true,
            TranscodingContainer = extension,
            TranscodingUrl = uri,
            ReadAtNativeFramerate = false,

            // === IPTV Stream Timestamp & Discontinuity Handling ===
            // These settings configure FFmpeg to properly handle the timestamp discontinuities
            // that occur frequently in IPTV streams due to:
            // - Provider connection drops/reconnections (every ~10-30 seconds for some providers)
            // - Provider switching during failover
            // - Source stream discontinuities
            //
            // Without these, symptoms include:
            // - Playback jumping/skipping after reconnections
            // - Audio desync accumulating over time
            // - "Non-monotonic DTS" warnings in FFmpeg logs
            // - HLS segment timing issues

            // GenPtsInput (-fflags +genpts): Generate missing PTS from DTS.
            // Essential for codec copy mode where FFmpeg decoder is bypassed.
            // Without this, streams lacking proper PTS data won't play.
            // See: https://github.com/jellyfin/jellyfin/pull/977
            GenPtsInput = isLive,

            // IgnoreDts (-fflags +igndts): Ignore DTS when PTS is available.
            // IPTV streams often have inconsistent DTS after reconnections that can
            // cause "Non-monotonic DTS" errors. When PTS is available (which genpts
            // ensures), we can safely ignore the potentially corrupted DTS.
            // See: https://github.com/jellyfin/jellyfin/issues/13301
            IgnoreDts = isLive,

            // AnalyzeDurationMs: Time FFmpeg spends analyzing the stream format.
            // For live IPTV, we want quick startup but enough analysis to detect
            // all streams. 3000ms matches Jellyfin's default for live TV probing.
            // Setting to 0 uses FFmpeg default which can cause slow startup.
            AnalyzeDurationMs = isLive ? 3000 : 0,
        };
    }

    /// <summary>
    /// Gets all live streams with channel deduplication and quality-based provider selection.
    /// Channels with the same name are merged, keeping only the highest quality variant.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A map of channels to their providers, supporting failover.</returns>
    public static async Task<ChannelProviderMap> GetDeduplicatedChannelMap(CancellationToken cancellationToken) =>
        ChannelProviderMap.Build(await GetAllLiveStreams(cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Gets all live streams with channel deduplication and health-aware provider ordering.
    /// Channels with the same name are merged. Providers are sorted by a combination of
    /// stream quality and provider health (success rate, capacity, circuit state).
    /// </summary>
    /// <param name="failoverService">Failover service for health-aware sorting and filtering.</param>
    /// <param name="filterByCapacity">When true, channels with no providers having capacity are filtered out.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A map of channels to their providers, supporting failover.</returns>
    public static async Task<ChannelProviderMap> GetDeduplicatedChannelMap(
        IAutomaticFailoverService? failoverService,
        bool filterByCapacity,
        CancellationToken cancellationToken
    )
    {
        return ChannelProviderMap.Build(
            await GetAllLiveStreams(cancellationToken).ConfigureAwait(false),
            failoverService,
            filterByCapacity
        );
    }

    [GeneratedRegex(@"\[([^\]]+)\]|\|([^\|]+)\|")]
    private static partial Regex TagRegex();
}
