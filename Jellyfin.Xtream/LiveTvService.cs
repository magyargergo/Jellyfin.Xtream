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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service;
using Jellyfin.Xtream.Service.Epg;
using Jellyfin.Xtream.Utility;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream;

/// <summary>
/// Class LiveTvService.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="LiveTvService"/> class.
/// </remarks>
/// <param name="appHost">Instance of the <see cref="IServerApplicationHost"/> interface.</param>
/// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
/// <param name="logger">Instance of the <see cref="ILogger{TCategoryName}"/> interface.</param>
/// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
/// <param name="memoryCache">Instance of the <see cref="IMemoryCache"/> interface.</param>
/// <param name="discordService">Instance of the <see cref="IDiscordNotificationService"/> interface.</param>
/// <param name="epgProvider">Instance of the <see cref="IEpgProvider"/> interface.</param>
/// <param name="externalEpgProvider">Instance of the <see cref="ExternalXmltvEpgProvider"/> for logo fallback.</param>
/// <param name="epgRefreshTracker">Instance of the <see cref="EpgRefreshTracker"/> for batch tracking.</param>
public class LiveTvService(
    IServerApplicationHost appHost,
    IHttpClientFactory httpClientFactory,
    ILogger<LiveTvService> logger,
    ILoggerFactory loggerFactory,
    IMemoryCache memoryCache,
    IDiscordNotificationService discordService,
    IEpgProvider epgProvider,
    ExternalXmltvEpgProvider externalEpgProvider,
    EpgRefreshTracker epgRefreshTracker
) : ILiveTvService, ISupportsDirectStreamProvider
{
    private const int MaxParallelEpgRequests = 10;

    private readonly IServerApplicationHost _appHost = appHost;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly ILogger<LiveTvService> _logger = logger;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly IMemoryCache _memoryCache = memoryCache;
    private readonly IDiscordNotificationService _discordService = discordService;
    private readonly IEpgProvider _epgProvider = epgProvider;
    private readonly ExternalXmltvEpgProvider _externalEpgProvider = externalEpgProvider;
    private readonly EpgRefreshTracker _epgRefreshTracker = epgRefreshTracker;

    private volatile ChannelProviderMap? _channelProviderMap;

    /// <inheritdoc />
    public string Name => "Xtream Live";

    /// <inheritdoc />
    public string HomePageUrl => string.Empty;

    /// <inheritdoc />
    public async Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        PluginConfiguration config = Plugin.Instance.Configuration;
        if (config.MergeDuplicateChannels)
        {
            return await GetDeduplicatedChannelsAsync(cancellationToken).ConfigureAwait(false);
        }

        return await GetAllChannelsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IEnumerable<ChannelInfo>> GetDeduplicatedChannelsAsync(CancellationToken cancellationToken)
    {
        ChannelProviderMap channelMap = _channelProviderMap = await StreamService
            .GetDeduplicatedChannelMap(cancellationToken)
            .ConfigureAwait(false);

        if (Plugin.Instance.Configuration.UseExternalLogoFallback)
        {
            await _externalEpgProvider.PrewarmAsync(cancellationToken).ConfigureAwait(false);
        }

        List<ChannelInfo> items = new List<ChannelInfo>();
        foreach (ChannelWithProviders channelWithProviders in channelMap.Channels)
        {
            ProviderStreamInfo? best = channelWithProviders.Best;
            if (best == null)
            {
                continue;
            }

            StreamInfo channel = best.Stream;
            XtreamProvider provider = best.Provider;
            string? imageUrl = channelWithProviders.BestImageUrl;

            if (string.IsNullOrEmpty(imageUrl) && Plugin.Instance.Configuration.UseExternalLogoFallback)
            {
                imageUrl = _externalEpgProvider.GetLogoUrl(channelWithProviders.DisplayName);
            }

            ChannelInfo channelInfo = new ChannelInfo
            {
                Id = StreamService.ToProviderGuid(StreamService.LiveTvPrefix, provider, channel.StreamId).ToString(),
                Number = channel.Num.ToString(CultureInfo.InvariantCulture),
                HasImage = !string.IsNullOrEmpty(imageUrl),
                ImageUrl = imageUrl,
                Name = channelWithProviders.DisplayName,
                Tags = Array.Empty<string>(),
            };

            if (channelWithProviders.ProviderCount > 1)
            {
                _logger.LogDebug(
                    "Channel '{ChannelName}' has {ProviderCount} providers available (best: {BestProvider})",
                    channelWithProviders.DisplayName,
                    channelWithProviders.ProviderCount,
                    provider.Name
                );
            }

            items.Add(channelInfo);
        }

        _logger.LogInformation(
            "Loaded {ChannelCount} deduplicated channels from {ProviderCount} providers",
            items.Count,
            Plugin.Instance.Configuration.GetEnabledProviders().Count()
        );

        if (channelMap.SkippedCount > 0)
        {
            _logger.LogWarning(
                "Skipped {SkippedCount} streams with empty names after normalization (e.g., channels named only 'HD' or '4K')",
                channelMap.SkippedCount
            );
        }

        return items;
    }

    private async Task<IEnumerable<ChannelInfo>> GetAllChannelsAsync(CancellationToken cancellationToken)
    {
        List<ChannelInfo> items = new List<ChannelInfo>();
        foreach (
            ProviderStreamInfo ps in await StreamService.GetAllLiveStreams(cancellationToken).ConfigureAwait(false)
        )
        {
            StreamInfo channel = ps.Stream;
            XtreamProvider provider = ps.Provider;
            ParsedName parsed = StreamService.ParseName(channel.Name);
            bool hasImage = !string.IsNullOrEmpty(channel.StreamIcon);

            items.Add(
                new ChannelInfo
                {
                    Id = StreamService
                        .ToProviderGuid(StreamService.LiveTvPrefix, provider, channel.StreamId)
                        .ToString(),
                    Number = channel.Num.ToString(CultureInfo.InvariantCulture),
                    HasImage = hasImage,
                    ImageUrl = hasImage ? channel.StreamIcon : null,
                    Name = parsed.Title,
                    Tags = parsed.Tags.ToArray(),
                }
            );
        }

        return items;
    }

    /// <inheritdoc />
    public Task CancelTimerAsync(string timerId, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<IEnumerable<TimerInfo>>(new List<TimerInfo>());
    }

    /// <inheritdoc />
    public Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<IEnumerable<SeriesTimerInfo>>(new List<SeriesTimerInfo>());
    }

    /// <inheritdoc />
    public Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task UpdateTimerAsync(TimerInfo updatedTimer, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public async Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(
        string channelId,
        CancellationToken cancellationToken
    )
    {
        MediaSourceInfo source = await GetChannelStream(channelId, string.Empty, cancellationToken)
            .ConfigureAwait(false);
        return new List<MediaSourceInfo>(1) { source };
    }

    /// <inheritdoc />
    public Task<MediaSourceInfo> GetChannelStream(
        string channelId,
        string streamId,
        CancellationToken cancellationToken
    )
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public async Task CloseLiveStream(string id, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Closing livestream {ChannelId}", id);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(
        CancellationToken cancellationToken,
        ProgramInfo? program = null
    )
    {
        return Task.FromResult(
            new SeriesTimerInfo
            {
                PostPaddingSeconds = 120,
                PrePaddingSeconds = 120,
                RecordAnyChannel = false,
                RecordAnyTime = true,
                RecordNewOnly = false,
            }
        );
    }

    /// <inheritdoc />
    public async Task<IEnumerable<ProgramInfo>> GetProgramsAsync(
        string channelId,
        DateTime startDateUtc,
        DateTime endDateUtc,
        CancellationToken cancellationToken
    )
    {
        _logger.LogInformation(
            "GetProgramsAsync called for channel {ChannelId}, date range: {StartDate} to {EndDate}",
            channelId,
            startDateUtc,
            endDateUtc
        );

        _epgRefreshTracker.RecordRequest();

        Guid guid = Guid.Parse(channelId);
        StreamService.FromGuid(guid, out var prefix, out var _, out var _, out var _);
        if (prefix != StreamService.LiveTvPrefix)
        {
            throw new ArgumentException("Unsupported channel");
        }

        string key = "xtream-epg-" + channelId;
        if (
            _memoryCache.TryGetValue<ICollection<ProgramInfo>>(key, out ICollection<ProgramInfo>? cachedItems)
            && cachedItems != null
        )
        {
            List<ProgramInfo> cachedFiltered = cachedItems
                .Where(epg => epg.EndDate >= startDateUtc && epg.StartDate < endDateUtc)
                .ToList();
            _logger.LogInformation(
                "Returning {Count} cached programs for channel {ChannelId}",
                cachedFiltered.Count,
                channelId
            );
            _epgRefreshTracker.RecordSuccess();
            return cachedFiltered;
        }

        PluginConfiguration config = Plugin.Instance.Configuration;
        List<ProgramInfo> items = new List<ProgramInfo>();
        IEnumerable<ProviderStreamInfo> providersToTry = GetEpgProvidersForChannel(guid, config);

        foreach (ProviderStreamInfo providerInfo in providersToTry)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                int providerStreamId = providerInfo.Stream.StreamId;
                _logger.LogDebugIfEnabled(
                    "Trying EPG from provider '{ProviderName}' stream {StreamId} for channel {ChannelId}",
                    providerInfo.Provider.Name,
                    providerStreamId,
                    channelId
                );

                IReadOnlyList<EpgProgram> programs = await _epgProvider
                    .GetProgramsAsync(providerStreamId, cancellationToken)
                    .ConfigureAwait(false);
                if (programs.Count == 0)
                {
                    _logger.LogDebugIfEnabled(
                        "No EPG data from provider '{ProviderName}' for channel {ChannelId}, trying next provider",
                        providerInfo.Provider.Name,
                        channelId
                    );
                    continue;
                }

                _logger.LogInformation(
                    "EPG provider '{ProviderName}' returned {Count} programs for channel {ChannelId}",
                    providerInfo.Provider.Name,
                    programs.Count,
                    channelId
                );

                int programCounter = 0;
                int invalidTimeCount = 0;

                foreach (EpgProgram prog in programs)
                {
                    if (!prog.HasValidTimes)
                    {
                        invalidTimeCount++;
                        continue;
                    }

                    items.Add(
                        new ProgramInfo
                        {
                            Id = StreamService
                                .ToGuid(StreamService.EpgPrefix, providerStreamId, prog.Id, programCounter++)
                                .ToString(),
                            ChannelId = channelId,
                            StartDate = prog.StartUtc,
                            EndDate = prog.EndUtc,
                            Name = string.IsNullOrWhiteSpace(prog.Title) ? "No Title" : prog.Title,
                            Overview = prog.Description,
                            HasImage = !string.IsNullOrEmpty(prog.ImageUrl),
                            ImageUrl = prog.ImageUrl,
                        }
                    );
                }

                if (invalidTimeCount > 0)
                {
                    _logger.LogWarning(
                        "Skipped {InvalidCount} EPG entries with invalid times for channel {ChannelId}",
                        invalidTimeCount,
                        channelId
                    );
                }

                if (items.Count > 0)
                {
                    DateTime minDate = items.Min(x => x.StartDate);
                    DateTime maxDate = items.Max(x => x.EndDate);
                    _logger.LogInformation(
                        "EPG data range for channel {ChannelId}: {MinDate} to {MaxDate} (from provider '{ProviderName}')",
                        channelId,
                        minDate,
                        maxDate,
                        providerInfo.Provider.Name
                    );
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebugIfEnabled("EPG request cancelled for channel {ChannelId}", channelId);
                break;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogDebugIfEnabled(
                    "HTTP error fetching EPG from provider '{ProviderName}' for channel {ChannelId}: {Error}",
                    providerInfo.Provider.Name,
                    channelId,
                    ex.Message
                );
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Error loading EPG from provider '{ProviderName}' for channel {ChannelId}",
                    providerInfo.Provider.Name,
                    channelId
                );
            }
        }

        if (items.Count == 0 && config.EnableExternalEpg)
        {
            items = await TryGetExternalEpgByNameAsync(guid, channelId, cancellationToken).ConfigureAwait(false);
        }

        _memoryCache.Set(key, items, DateTimeOffset.Now.AddMinutes(30));

        List<ProgramInfo> filtered = items
            .Where(epg => epg.EndDate >= startDateUtc && epg.StartDate < endDateUtc)
            .ToList();

        _logger.LogInformation(
            "Returning {FilteredCount} programs (of {TotalCount}) for channel {ChannelId} within date range {StartDate} to {EndDate}",
            filtered.Count,
            items.Count,
            channelId,
            startDateUtc,
            endDateUtc
        );

        if (items.Count > 0)
        {
            _epgRefreshTracker.RecordSuccess();
        }
        else
        {
            _epgRefreshTracker.RecordFailure();
        }

        return filtered;
    }

    /// <summary>
    /// Tries to get EPG from external XMLTV provider using the channel's display name.
    /// External sources like epg.ovh use channel names instead of stream IDs.
    /// </summary>
    private async Task<List<ProgramInfo>> TryGetExternalEpgByNameAsync(
        Guid channelGuid,
        string channelId,
        CancellationToken cancellationToken
    )
    {
        List<ProgramInfo> items = new List<ProgramInfo>();

        ChannelWithProviders? channelWithProviders = _channelProviderMap?.GetByGuid(channelGuid);
        if (channelWithProviders == null)
        {
            return items;
        }

        string channelName = channelWithProviders.DisplayName;
        if (string.IsNullOrWhiteSpace(channelName))
        {
            return items;
        }

        _logger.LogDebugIfEnabled(
            "Trying external EPG for channel '{ChannelName}' (ID: {ChannelId})",
            channelName,
            channelId
        );

        try
        {
            IReadOnlyList<EpgProgram> programs = await _externalEpgProvider
                .GetProgramsByNameAsync(channelName, cancellationToken)
                .ConfigureAwait(false);
            if (programs.Count == 0)
            {
                _logger.LogDebugIfEnabled("No external EPG data for channel '{ChannelName}'", channelName);
                return items;
            }

            _logger.LogInformation(
                "External EPG returned {Count} programs for channel '{ChannelName}'",
                programs.Count,
                channelName
            );

            int programCounter = 0;
            int invalidTimeCount = 0;

            foreach (EpgProgram prog in programs)
            {
                if (!prog.HasValidTimes)
                {
                    invalidTimeCount++;
                    continue;
                }

                items.Add(
                    new ProgramInfo
                    {
                        Id = StreamService.ToGuid(StreamService.EpgPrefix, 0, prog.Id, programCounter++).ToString(),
                        ChannelId = channelId,
                        StartDate = prog.StartUtc,
                        EndDate = prog.EndUtc,
                        Name = string.IsNullOrWhiteSpace(prog.Title) ? "No Title" : prog.Title,
                        Overview = prog.Description,
                        HasImage = !string.IsNullOrEmpty(prog.ImageUrl),
                        ImageUrl = prog.ImageUrl,
                    }
                );
            }

            if (invalidTimeCount > 0)
            {
                _logger.LogWarning(
                    "Skipped {InvalidCount} external EPG entries with invalid times for channel '{ChannelName}'",
                    invalidTimeCount,
                    channelName
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Error loading external EPG for channel '{ChannelName}': {Error}",
                channelName,
                ex.Message
            );
        }

        return items;
    }

    private IEnumerable<ProviderStreamInfo> GetEpgProvidersForChannel(Guid channelGuid, PluginConfiguration config)
    {
        if (config.MergeDuplicateChannels && _channelProviderMap != null)
        {
            ChannelWithProviders? channelWithProviders = _channelProviderMap.GetByGuid(channelGuid);
            if (channelWithProviders != null)
            {
                return channelWithProviders.Providers;
            }
        }

        XtreamProvider? provider = StreamService.FindProviderForGuid(channelGuid);
        if (provider == null)
        {
            return Array.Empty<ProviderStreamInfo>();
        }

        StreamService.FromGuid(channelGuid, out var _, out var streamId, out var _, out var _);
        StreamInfo streamInfo = new StreamInfo { StreamId = streamId, Name = string.Empty };

        return new[] { new ProviderStreamInfo(provider, streamInfo) };
    }

    /// <inheritdoc />
    public Task ResetTuner(string id, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Pre-warms EPG cache for all channels in parallel with retry logic.
    /// Call this to speed up guide refresh by loading all EPG data at once.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing success count and total channels.</returns>
    public async Task<(int SuccessCount, int TotalCount)> RefreshAllEpgParallelAsync(
        CancellationToken cancellationToken
    )
    {
        DateTime startTime = DateTime.UtcNow;
        string? errorMessage = null;

        if (_epgProvider is IEpgProviderWithPrewarm prewarmProvider)
        {
            _logger.LogInformation("Pre-warming EPG providers before refresh...");
            try
            {
                await prewarmProvider.PrewarmAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "EPG provider pre-warming failed, continuing with refresh");
            }
        }

        List<ProviderStreamInfo> channelList = (
            await StreamService.GetAllLiveStreams(cancellationToken).ConfigureAwait(false)
        ).ToList();
        _logger.LogInformation(
            "Starting parallel EPG refresh for {Count} channels across all providers",
            channelList.Count
        );

        await _discordService.NotifyEpgRefreshStartedAsync(channelList.Count, cancellationToken).ConfigureAwait(false);

        using SemaphoreSlim semaphore = new SemaphoreSlim(MaxParallelEpgRequests);

        int successCount = 0;
        int retriedSuccessCount = 0;
        int httpErrorCount = 0;
        int noDataCount = 0;
        ConcurrentBag<string> failedChannels = new ConcurrentBag<string>();
        ConcurrentQueue<ProviderStreamInfo> retryQueue = new ConcurrentQueue<ProviderStreamInfo>();

        try
        {
            var tasks = channelList.Select(async providerStreamInfo =>
            {
                StreamInfo channel = providerStreamInfo.Stream;
                XtreamProvider provider = providerStreamInfo.Provider;

                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    switch (
                        await TryFetchEpgForChannelAsync(channel, provider, cancellationToken).ConfigureAwait(false)
                    )
                    {
                        case EpgFetchStatus.Success:
                            Interlocked.Increment(ref successCount);
                            break;
                        case EpgFetchStatus.NoData:
                            Interlocked.Increment(ref noDataCount);
                            failedChannels.Add(StreamService.ParseName(channel.Name).Title);
                            break;
                        case EpgFetchStatus.HttpError:
                            Interlocked.Increment(ref httpErrorCount);
                            retryQueue.Enqueue(providerStreamInfo);
                            break;
                        case EpgFetchStatus.Error:
                            failedChannels.Add(StreamService.ParseName(channel.Name).Title);
                            break;
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);

            // Retry failed HTTP requests after a short delay
            if (!retryQueue.IsEmpty)
            {
                _logger.LogInformation("Retrying {Count} failed EPG requests after delay...", retryQueue.Count);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);

                List<ProviderStreamInfo> retryItems = new List<ProviderStreamInfo>();
                while (retryQueue.TryDequeue(out var ps))
                {
                    retryItems.Add(ps);
                }

                var retryTasks = retryItems.Select(async providerStreamInfo =>
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (
                            await TryFetchEpgForChannelAsync(
                                    providerStreamInfo.Stream,
                                    providerStreamInfo.Provider,
                                    cancellationToken
                                )
                                .ConfigureAwait(false) == EpgFetchStatus.Success
                        )
                        {
                            Interlocked.Increment(ref successCount);
                            Interlocked.Increment(ref retriedSuccessCount);
                        }
                        else
                        {
                            failedChannels.Add(StreamService.ParseName(providerStreamInfo.Stream.Name).Title);
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(retryTasks).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            _logger.LogError(ex, "EPG refresh failed with unexpected error");
        }

        TimeSpan elapsed = DateTime.UtcNow - startTime;
        _logger.LogInformation(
            "Parallel EPG refresh complete: {SuccessCount}/{TotalCount} channels with EPG data in {Elapsed:F1}s (retried: {RetriedCount}, HTTP errors: {HttpErrors}, no data: {NoData})",
            successCount,
            channelList.Count,
            elapsed.TotalSeconds,
            retriedSuccessCount,
            httpErrorCount,
            noDataCount
        );

        EpgRefreshResult result = new EpgRefreshResult
        {
            SuccessCount = successCount,
            TotalCount = channelList.Count,
            RetriedSuccessCount = retriedSuccessCount,
            Duration = elapsed,
            FailedChannels = failedChannels.Take(50).ToList(),
            ErrorMessage = errorMessage,
            HttpErrorCount = httpErrorCount,
            NoDataCount = noDataCount,
        };

        await _discordService.NotifyEpgRefreshAsync(result, cancellationToken).ConfigureAwait(false);

        return (successCount, channelList.Count);
    }

    private async Task<EpgFetchStatus> TryFetchEpgForChannelAsync(
        StreamInfo channel,
        XtreamProvider provider,
        CancellationToken cancellationToken
    )
    {
        string channelId = StreamService
            .ToProviderGuid(StreamService.LiveTvPrefix, provider, channel.StreamId)
            .ToString();
        try
        {
            IReadOnlyList<EpgProgram> programs = await _epgProvider
                .GetProgramsAsync(channel.StreamId, cancellationToken)
                .ConfigureAwait(false);
            if (programs.Count > 0)
            {
                var programInfoList = programs
                    .Where(p => p.HasValidTimes)
                    .Select(
                        (prog, index) =>
                            new ProgramInfo
                            {
                                Id = StreamService
                                    .ToGuid(StreamService.EpgPrefix, channel.StreamId, prog.Id, index)
                                    .ToString(),
                                ChannelId = channelId,
                                StartDate = prog.StartUtc,
                                EndDate = prog.EndUtc,
                                Name = string.IsNullOrWhiteSpace(prog.Title) ? "No Title" : prog.Title,
                                Overview = prog.Description,
                                HasImage = !string.IsNullOrEmpty(prog.ImageUrl),
                                ImageUrl = prog.ImageUrl,
                            }
                    )
                    .ToList();

                _memoryCache.Set(
                    "xtream-epg-" + channelId,
                    (ICollection<ProgramInfo>)programInfoList,
                    DateTimeOffset.Now.AddMinutes(30)
                );
                return EpgFetchStatus.Success;
            }

            return EpgFetchStatus.NoData;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                "HTTP error fetching EPG for channel {ChannelName} (stream {StreamId}): {Error}",
                channel.Name,
                channel.StreamId,
                ex.Message
            );
            return EpgFetchStatus.HttpError;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Error fetching EPG for channel {ChannelName} (stream {StreamId}): {Error}",
                channel.Name,
                channel.StreamId,
                ex.Message
            );
            return EpgFetchStatus.Error;
        }
    }

    /// <inheritdoc />
    public async Task<ILiveStream> GetChannelStreamWithDirectStreamProvider(
        string channelId,
        string streamId,
        List<ILiveStream> currentLiveStreams,
        CancellationToken cancellationToken
    )
    {
        Guid guid = Guid.Parse(channelId);
        StreamService.FromGuid(guid, out var prefix, out var _, out var _, out var _);
        if (prefix != StreamService.LiveTvPrefix)
        {
            throw new ArgumentException("Unsupported channel");
        }

        PluginConfiguration config = Plugin.Instance.Configuration;
        HashSet<string> failedProviders = new HashSet<string>(StringComparer.Ordinal);
        Exception? lastException = null;

        IEnumerable<ProviderStreamInfo> providersToTry = GetProvidersForChannel(guid, config);
        foreach (ProviderStreamInfo providerInfo in providersToTry)
        {
            XtreamProvider provider = providerInfo.Provider;
            int providerStreamId = providerInfo.Stream.StreamId;

            try
            {
                ILiveStream stream = await TryGetStreamFromProvider(
                        provider,
                        providerStreamId,
                        providerInfo.Stream.Name,
                        currentLiveStreams,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (failedProviders.Count > 0)
                {
                    _logger.LogInformation(
                        "Failover successful: Connected to '{ProviderName}' after {FailCount} failed attempts",
                        provider.Name,
                        failedProviders.Count
                    );
                }

                return stream;
            }
            catch (Exception ex)
                when (config.EnableProviderFailover && failedProviders.Count < config.MaxFailoverAttempts)
            {
                failedProviders.Add(provider.Id);
                lastException = ex;
                _logger.LogWarning(
                    ex,
                    "Failed to connect to provider '{ProviderName}' for channel, attempting failover ({AttemptCount}/{MaxAttempts})",
                    provider.Name,
                    failedProviders.Count,
                    config.MaxFailoverAttempts
                );
            }
        }

        throw new InvalidOperationException(
            $"Failed to connect to any provider for channel {channelId} after {failedProviders.Count} attempts",
            lastException
        );
    }

    private IEnumerable<ProviderStreamInfo> GetProvidersForChannel(Guid channelGuid, PluginConfiguration config)
    {
        if (config.MergeDuplicateChannels && _channelProviderMap != null)
        {
            ChannelWithProviders? channelWithProviders = _channelProviderMap.GetByGuid(channelGuid);
            if (channelWithProviders != null)
            {
                return channelWithProviders.Providers;
            }

            _logger.LogDebugIfEnabled(
                "Channel GUID {ChannelGuid} not found in channel map (map has {Count} entries). Falling back to direct provider lookup.",
                channelGuid,
                _channelProviderMap.ChannelCount
            );
        }

        XtreamProvider? provider = StreamService.FindProviderForGuid(channelGuid);
        if (provider == null)
        {
            StreamService.FromGuid(channelGuid, out var prefix, out var streamId, out var providerHash, out var _);
            _logger.LogWarning(
                "No provider found for channel GUID {ChannelGuid} (prefix={Prefix}, streamId={StreamId}, providerHash={ProviderHash}). This can happen after upgrading the plugin or if the provider was removed. Try refreshing Live TV channels in Jellyfin settings.",
                channelGuid,
                prefix,
                streamId,
                providerHash
            );
            return Array.Empty<ProviderStreamInfo>();
        }

        StreamService.FromGuid(channelGuid, out var _, out var sid, out var _, out var _);
        StreamInfo streamInfo = new StreamInfo { StreamId = sid, Name = string.Empty };

        return new[] { new ProviderStreamInfo(provider, streamInfo) };
    }

    private async Task<ILiveStream> TryGetStreamFromProvider(
        XtreamProvider provider,
        int streamId,
        string streamName,
        List<ILiveStream> currentLiveStreams,
        CancellationToken cancellationToken
    )
    {
        Plugin plugin = Plugin.Instance;
        string? channelName = null;

        if (!string.IsNullOrEmpty(streamName))
        {
            channelName = StreamService.ParseName(streamName).Title;
        }
        else
        {
            try
            {
                StreamInfo? info = (
                    await StreamService
                        .GetLiveStreamsWithOverridesForProvider(provider, cancellationToken)
                        .ConfigureAwait(false)
                ).FirstOrDefault(s => s.StreamId == streamId);
                if (info != null)
                {
                    channelName = StreamService.ParseName(info.Name).Title;
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Failed to look up channel name for stream {StreamId}", streamId);
            }
        }

        MediaSourceInfo mediaSourceInfo = plugin.StreamService.GetMediaSourceInfo(
            provider,
            StreamType.Live,
            streamId,
            channelName,
            "ts",
            restream: true
        );
        ILiveStream? stream = currentLiveStreams.Find(s =>
            s.TunerHostId == "Xtream-Restream" && s.MediaSource.Id == mediaSourceInfo.Id
        );

        if (stream != null)
        {
            if (stream is not Restream restream || !restream.IsDisposed)
            {
                _logger.LogDebugIfEnabled(
                    "Reusing existing Restream instance for stream {StreamId} from provider '{ProviderName}', current consumers: {ConsumerCount}",
                    streamId,
                    provider.Name,
                    stream.ConsumerCount
                );
                return stream;
            }

            _logger.LogWarning("Found disposed Restream instance for stream {StreamId}, creating new one", streamId);
        }

        _logger.LogInformation(
            "Creating new Restream instance for stream {StreamId} from provider '{ProviderName}'",
            streamId,
            provider.Name
        );

        Restream newStream = new Restream(
            appHost: _appHost,
            httpClientFactory: _httpClientFactory,
            logger: _loggerFactory.CreateLogger<Restream>(),
            loggerFactory: _loggerFactory,
            mediaSource: mediaSourceInfo,
            discordService: _discordService
        );

        try
        {
            await newStream.Open(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            newStream.Dispose();
            throw;
        }

        await EnforceConnectionLimitAsync(cancellationToken).ConfigureAwait(false);
        return newStream;
    }

    /// <summary>
    /// Enforces connection limits by checking active streams and optionally killing old ones.
    /// Currently uses a global limit across all providers.
    /// </summary>
    private async Task EnforceConnectionLimitAsync(CancellationToken cancellationToken)
    {
        PluginConfiguration config = Plugin.Instance.Configuration;
        if (!config.EnforceConnectionLimit)
        {
            return;
        }

        int maxConnections = config.MaxConcurrentStreams;
        if (maxConnections <= 0)
        {
            XtreamProvider? firstProvider = config.GetEnabledProviders().FirstOrDefault();
            if (firstProvider != null)
            {
                try
                {
                    using XtreamClient client = new XtreamClient(
                        _httpClientFactory,
                        _loggerFactory.CreateLogger<XtreamClient>()
                    );
                    maxConnections =
                        (
                            await client
                                .GetUserAndServerInfoAsync(firstProvider.ToConnectionInfo(), cancellationToken)
                                .ConfigureAwait(false)
                        )
                            ?.UserInfo
                            ?.MaxConnections
                        ?? 1;
                    _logger.LogDebugIfEnabled("Provider max connections: {MaxConnections}", maxConnections);
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(ex, "Failed to get provider connection limits, defaulting to 1");
                    maxConnections = 1;
                }
            }
            else
            {
                maxConnections = 1;
            }
        }

        int currentStreams = Restream.GetActiveStreamCount();
        _logger.LogDebugIfEnabled(
            "Connection limit check: {CurrentStreams}/{MaxConnections} streams active",
            currentStreams,
            maxConnections
        );

        if (currentStreams > maxConnections)
        {
            int streamsToKill = currentStreams - maxConnections;
            if (!config.AutoKillOldestStream)
            {
                throw new InvalidOperationException(
                    $"Connection limit reached ({currentStreams}/{maxConnections}). "
                        + "Cannot open new stream. Close an existing stream first or enable auto-kill."
                );
            }

            List<StreamInfoSnapshot> snapshots = Restream.GetActiveStreamSnapshots().OrderBy(s => s.StartTime).ToList();

            for (int i = 0; i < streamsToKill && i < snapshots.Count; i++)
            {
                StreamInfoSnapshot oldest = snapshots[i];
                TimeSpan streamAge = DateTime.UtcNow - oldest.StartTime;
                _logger.LogInformation(
                    "Connection limit exceeded ({CurrentStreams}/{MaxConnections}). Killing oldest stream: {StreamName} ({StreamId}), age: {Age}",
                    currentStreams,
                    maxConnections,
                    oldest.ChannelName,
                    oldest.StreamId,
                    streamAge
                );

                Restream.KillStream(
                    oldest.StreamId,
                    $"Connection limit exceeded ({currentStreams}/{maxConnections}) - auto-killed oldest stream ({oldest.ChannelName})"
                );
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }
}
