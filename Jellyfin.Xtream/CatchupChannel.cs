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
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service;
using Jellyfin.Xtream.Utility;
using MediaBrowser.Controller.Channels;
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
public class CatchupChannel(ILogger<CatchupChannel> logger) : IChannel, IDisableMediaSourceDisplay
{
    private readonly ILogger<CatchupChannel> _logger = logger;

    /// <inheritdoc />
    public string? Name => "Xtream Catch-up";

    /// <inheritdoc />
    public string? Description => "Rewatch IPTV streamed from the Xtream-compatible server.";

    /// <inheritdoc />
    public string DataVersion => Plugin.Instance.DataVersion + DateTime.Today.ToShortDateString();

    /// <inheritdoc />
    public string HomePageUrl => string.Empty;

    /// <inheritdoc />
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    /// <inheritdoc />
    public InternalChannelFeatures GetChannelFeatures()
    {
        return new InternalChannelFeatures
        {
            ContentTypes = [ChannelMediaContentType.TvExtra],
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
                return await GetChannels(cancellationToken).ConfigureAwait(false);
            }

            var guid = Guid.Parse(query.FolderId);
            StreamService.FromGuid(guid, out var _, out var channelId, out var _, out var date);
            var provider =
                StreamService.FindProviderForGuid(guid)
                ?? throw new ArgumentException("Provider not found for channel");

            return date == 0
                ? await GetDays(provider, channelId, cancellationToken).ConfigureAwait(false)
                : await GetStreams(provider, channelId, date, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.PluginLogError(ex, "Failed to get channel items");
            throw;
        }
    }

    private static async Task<ChannelItemResult> GetChannels(CancellationToken cancellationToken)
    {
        List<ChannelItemInfo> items = [];

        foreach (var ps in await StreamService.GetAllLiveStreams(cancellationToken).ConfigureAwait(false))
        {
            var channel = ps.Stream;
            var provider = ps.Provider;

            if (!channel.TvArchive)
            {
                continue;
            }

            var parsedName = StreamService.ParseName(channel.Name);
            items.Add(
                new ChannelItemInfo
                {
                    Id = StreamService
                        .ToProviderGuid(StreamService.CatchupPrefix, provider, channel.StreamId)
                        .ToString(),
                    ImageUrl = channel.StreamIcon,
                    Name = parsedName.Title,
                    Tags = [.. parsedName.Tags],
                    Type = ChannelItemType.Folder,
                }
            );
        }

        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }

    private static async Task<ChannelItemResult> GetDays(
        XtreamProvider provider,
        int channelId,
        CancellationToken cancellationToken
    )
    {
        var plugin = Plugin.Instance;
        using var client = plugin.CreateXtreamClient();

        var channel =
            (
                await client.GetLiveStreamsAsync(provider.ToConnectionInfo(), cancellationToken).ConfigureAwait(false)
            ).FirstOrDefault(s => s.StreamId == channelId)
            ?? throw new ArgumentException($"Channel with id {channelId} not found for provider {provider.Name}");

        var parsedName = StreamService.ParseName(channel.Name);
        List<ChannelItemInfo> items = [];

        for (var i = 0; i <= channel.TvArchiveDuration; i++)
        {
            var channelDay = DateTime.Today.AddDays(-i);
            var day = (int)(channelDay - DateTime.UnixEpoch).TotalDays;

            items.Add(
                new ChannelItemInfo
                {
                    Id = StreamService
                        .ToGuid(StreamService.CatchupPrefix, channel.StreamId, provider.GetIdHash(), day)
                        .ToString(),
                    ImageUrl = channel.StreamIcon,
                    Name = channelDay.ToLocalTime().ToString("ddd dd'-'MM'-'yyyy", CultureInfo.InvariantCulture),
                    Tags = [.. parsedName.Tags],
                    Type = ChannelItemType.Folder,
                }
            );
        }

        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }

    private static async Task<ChannelItemResult> GetStreams(
        XtreamProvider provider,
        int channelId,
        int day,
        CancellationToken cancellationToken
    )
    {
        var start = DateTime.UnixEpoch.AddDays(day);
        var end = start.AddDays(1);

        var plugin = Plugin.Instance;
        using var client = plugin.CreateXtreamClient();

        var channel =
            (
                await client.GetLiveStreamsAsync(provider.ToConnectionInfo(), cancellationToken).ConfigureAwait(false)
            ).FirstOrDefault(s => s.StreamId == channelId)
            ?? throw new ArgumentException($"Channel with id {channelId} not found for provider {provider.Name}");

        var epgs = await client
            .GetEpgInfoAsync(provider.ToConnectionInfo(), channelId, cancellationToken)
            .ConfigureAwait(false);
        List<ChannelItemInfo> items = [];

        if (epgs.Listings.Count == 0)
        {
            const int durationMinutes = 1440;
            return new ChannelItemResult
            {
                Items =
                [
                    new()
                    {
                        ContentType = ChannelMediaContentType.TvExtra,
                        Id = StreamService
                            .ToGuid(StreamService.CatchupStreamPrefix, channelId, provider.GetIdHash(), day)
                            .ToString(),
                        IsLiveStream = false,
                        MediaSources =
                        [
                            StreamService.GetMediaSourceInfo(
                                provider,
                                StreamType.CatchUp,
                                channelId,
                                name: null,
                                extension: null,
                                restream: false,
                                start,
                                durationMinutes
                            ),
                        ],
                        MediaType = ChannelMediaType.Video,
                        Name = "No EPG available",
                        RunTimeTicks = (long)durationMinutes * TimeSpan.TicksPerMinute,
                        Type = ChannelItemType.Media,
                    },
                ],
                TotalRecordCount = 1,
            };
        }

        foreach (var epg in epgs.Listings.Where(epgInfo => epgInfo.Start <= end && epgInfo.End >= start))
        {
            var parsedName = StreamService.ParseName(epg.Title);
            var durationMinutes = (int)Math.Ceiling((epg.End - epg.Start).TotalMinutes);
            var dateTitle = epg.Start.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

            List<MediaSourceInfo> sources =
            [
                StreamService.GetMediaSourceInfo(
                    provider,
                    StreamType.CatchUp,
                    channelId,
name: null,
extension: null,
                    restream: false,
                    epg.StartLocalTime,
                    durationMinutes
                ),
            ];

            items.Add(
                new ChannelItemInfo
                {
                    ContentType = ChannelMediaContentType.TvExtra,
                    DateCreated = epg.Start,
                    Id = StreamService
                        .ToGuid(StreamService.CatchupStreamPrefix, channel.StreamId, epg.Id, day)
                        .ToString(),
                    IsLiveStream = false,
                    MediaSources = sources,
                    MediaType = ChannelMediaType.Video,
                    Name = dateTitle + " - " + parsedName.Title,
                    Overview = epg.Description,
                    PremiereDate = epg.Start,
                    RunTimeTicks = (long)durationMinutes * TimeSpan.TicksPerMinute,
                    Tags = [.. parsedName.Tags],
                    Type = ChannelItemType.Media,
                }
            );
        }

        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }

    /// <inheritdoc />
    public bool IsEnabledFor(string userId) => Plugin.Instance.Configuration.IsCatchupVisible;
}
