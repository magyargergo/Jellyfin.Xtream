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
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream;

/// <summary>
/// The Xtream Codes API channel.
/// </summary>
/// <param name="logger">Instance of the <see cref="ILogger{TCategoryName}"/> interface.</param>
public class SeriesChannel(ILogger<SeriesChannel> logger) : IChannel, IDisableMediaSourceDisplay
{
    /// <inheritdoc />
    public string? Name => "Xtream Series";

    /// <inheritdoc />
    public string? Description => "Series streamed from the Xtream-compatible server.";

    /// <inheritdoc />
    public string DataVersion => Plugin.Instance.DataVersion;

    /// <inheritdoc />
    public string HomePageUrl => string.Empty;

    /// <inheritdoc />
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    /// <inheritdoc />
    public InternalChannelFeatures GetChannelFeatures()
    {
        return new InternalChannelFeatures
        {
            ContentTypes = [ChannelMediaContentType.Episode],
            MediaTypes = [ChannelMediaType.Video],
        };
    }

    /// <inheritdoc />
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken) =>
        throw new ArgumentException("Unsupported image type: " + type);

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedChannelImages() => [];

    /// <inheritdoc />
    public async Task<ChannelItemResult> GetChannelItems(
        InternalChannelItemQuery query,
        CancellationToken cancellationToken
    )
    {
        try
        {
            if (string.IsNullOrEmpty(query.FolderId))
            {
                return await GetCategories(cancellationToken).ConfigureAwait(false);
            }

            var guid = Guid.Parse(query.FolderId);
            StreamService.FromGuid(guid, out var prefix, out var categoryId, out var seriesId, out var seasonId);

            switch (prefix)
            {
                case StreamService.SeriesCategoryPrefix:
                    return await GetSeries(categoryId, cancellationToken).ConfigureAwait(false);
                case StreamService.SeriesPrefix:
                    return await GetSeasons(seriesId, cancellationToken).ConfigureAwait(false);
                case StreamService.SeasonPrefix:
                    return await GetEpisodes(seriesId, seasonId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.PluginLogError(ex, "Failed to get channel items");
            throw;
        }

        return new ChannelItemResult { TotalRecordCount = 0 };
    }

    private static ChannelItemInfo CreateChannelItemInfo(Series series)
    {
        var parsedName = StreamService.ParseName(series.Name);
        return new ChannelItemInfo
        {
            CommunityRating = (float)series.Rating5Based,
            DateModified = series.LastModified,
            Genres = GetGenres(series.Genre),
            Id = StreamService.ToGuid(StreamService.SeriesPrefix, series.CategoryId, series.SeriesId, 0).ToString(),
            ImageUrl = series.Cover,
            Name = parsedName.Title,
            People = GetPeople(series.Cast),
            Tags = [.. parsedName.Tags],
            Type = ChannelItemType.Folder,
        };
    }

    private static List<string> GetGenres(string genreString) =>
        [.. genreString.Split(',').Select(genre => genre.Trim())];

    private static List<PersonInfo> GetPeople(string cast) =>
        [.. cast.Split(',').Select(name => new PersonInfo { Name = name.Trim() })];

    private static ChannelItemInfo CreateChannelItemInfo(int seriesId, SeriesStreamInfo series, int seasonId)
    {
        var serie = series.Info;
        var name = $"Season {seasonId}";
        var cover = series.Info.Cover;
        string? overview = null;
        DateTime? created = null;
        List<string> tags = [];

        var season = series.Seasons.FirstOrDefault(s => s.SeasonId == seasonId);
        if (season != null)
        {
            var parsedName = StreamService.ParseName(season.Name);
            name = parsedName.Title;
            tags.AddRange(parsedName.Tags);
            created = season.AirDate;
            overview = season.Overview;
            if (!string.IsNullOrEmpty(season.Cover))
            {
                cover = season.Cover;
            }
        }

        return new ChannelItemInfo
        {
            DateCreated = created,
            Genres = GetGenres(serie.Genre),
            Id = StreamService.ToGuid(StreamService.SeasonPrefix, serie.CategoryId, seriesId, seasonId).ToString(),
            ImageUrl = cover,
            Name = name,
            Overview = overview,
            People = GetPeople(serie.Cast),
            Tags = tags,
            Type = ChannelItemType.Folder,
        };
    }

    private static ChannelItemInfo CreateChannelItemInfo(SeriesStreamInfo series, Season? season, Episode episode)
    {
        var serie = series.Info;
        var parsedName = StreamService.ParseName(episode.Title);

        var provider =
            Plugin.Instance.Configuration.GetEnabledProviders().FirstOrDefault()
            ?? throw new InvalidOperationException("No enabled provider found");

        List<MediaSourceInfo> sources =
        [
            StreamService.GetMediaSourceInfo(
                provider,
                StreamType.Series,
                episode.EpisodeId,
                episode.ContainerExtension,
                extension: null,
                restream: false,
                start: null,
                0,
                episode.Info?.Video,
                episode.Info?.Audio
            ),
        ];

        var cover = episode.Info?.MovieImage;
        cover ??= season?.Cover;
        cover ??= serie.Cover;

        return new ChannelItemInfo
        {
            ContentType = ChannelMediaContentType.Episode,
            DateCreated = DateTimeOffset.FromUnixTimeSeconds(episode.Added).DateTime,
            Genres = GetGenres(serie.Genre),
            Id = StreamService.ToGuid(StreamService.EpisodePrefix, 0, 0, episode.EpisodeId).ToString(),
            ImageUrl = cover,
            IsLiveStream = false,
            MediaSources = sources,
            MediaType = ChannelMediaType.Video,
            Name = parsedName.Title,
            Overview = episode.Info?.Plot,
            People = GetPeople(serie.Cast),
            Tags = [.. parsedName.Tags],
            Type = ChannelItemType.Media,
        };
    }

    private static async Task<ChannelItemResult> GetCategories(CancellationToken cancellationToken)
    {
        List<ChannelItemInfo> items =
        [
            .. (await StreamService.GetAllSeriesCategories(cancellationToken).ConfigureAwait(false)).Select(
                providerCategory =>
                    StreamService.CreateChannelItemInfo(StreamService.SeriesCategoryPrefix, providerCategory.Category)
            ),
        ];

        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }

    private async Task<ChannelItemResult> GetSeries(int categoryId, CancellationToken cancellationToken)
    {
        var categoryGuid = StreamService.ToGuid(StreamService.SeriesCategoryPrefix, categoryId, 0, 0);
        var provider = StreamService.FindProviderForGuid(categoryGuid);

        if (provider == null)
        {
            return new ChannelItemResult { Items = [], TotalRecordCount = 0 };
        }

        List<ChannelItemInfo> items =
        [
            .. (
                await StreamService.GetSeriesForProvider(provider, categoryId, cancellationToken).ConfigureAwait(false)
            ).Select(CreateChannelItemInfo),
        ];

        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }

    private async Task<ChannelItemResult> GetSeasons(int seriesId, CancellationToken cancellationToken)
    {
        var seriesGuid = StreamService.ToGuid(StreamService.SeriesPrefix, 0, seriesId, 0);
        var provider = StreamService.FindProviderForGuid(seriesGuid);

        if (provider == null)
        {
            return new ChannelItemResult { Items = [], TotalRecordCount = 0 };
        }

        List<ChannelItemInfo> items =
        [
            .. (
                await StreamService.GetSeasonsForProvider(provider, seriesId, cancellationToken).ConfigureAwait(false)
            ).Select(tuple => CreateChannelItemInfo(seriesId, tuple.Item1, tuple.Item2)),
        ];

        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }

    private async Task<ChannelItemResult> GetEpisodes(int seriesId, int seasonId, CancellationToken cancellationToken)
    {
        var seasonGuid = StreamService.ToGuid(StreamService.SeasonPrefix, 0, seriesId, seasonId);
        var provider = StreamService.FindProviderForGuid(seasonGuid);

        if (provider == null)
        {
            return new ChannelItemResult { Items = [], TotalRecordCount = 0 };
        }

        List<ChannelItemInfo> items =
        [
            .. (
                await StreamService
                    .GetEpisodesForProvider(provider, seriesId, seasonId, cancellationToken)
                    .ConfigureAwait(false)
            ).Select(tuple => CreateChannelItemInfo(tuple.Item1, tuple.Item2, tuple.Item3)),
        ];

        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }

    /// <inheritdoc />
    public bool IsEnabledFor(string userId) => Plugin.Instance.Configuration.IsSeriesVisible;
}
