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
using Jellyfin.Xtream.Service.MpegTs.UseCases;
using Jellyfin.Xtream.Service.ProviderManagement;
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
/// <param name="failoverService">Instance of the <see cref="IAutomaticFailoverService"/> - the source of truth for provider management.</param>
/// <param name="providerSwitchService">Instance of the <see cref="IProviderSwitchService"/> for provider switching.</param>
/// <param name="violationSwitchTrigger">Instance of the <see cref="IViolationSwitchTrigger"/> for TR 101 290 violation-triggered switching.</param>
/// <param name="ffmpegContext">Instance of the <see cref="IFFmpegContext"/> for FFmpeg native demuxing.</param>
/// <param name="channelWarmupService">Instance of the <see cref="IChannelWarmupService"/> for Fast Channel Change.</param>
public class LiveTvService(
    IServerApplicationHost appHost,
    IHttpClientFactory httpClientFactory,
    ILogger<LiveTvService> logger,
    ILoggerFactory loggerFactory,
    IMemoryCache memoryCache,
    IDiscordNotificationService discordService,
    IEpgProvider epgProvider,
    ExternalXmltvEpgProvider externalEpgProvider,
    EpgRefreshTracker epgRefreshTracker,
    IAutomaticFailoverService failoverService,
    IProviderSwitchService providerSwitchService,
    IViolationSwitchTrigger violationSwitchTrigger,
    IFFmpegContext ffmpegContext,
    IChannelWarmupService channelWarmupService
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
    private readonly IAutomaticFailoverService _failoverService = failoverService;
    private readonly IProviderSwitchService _providerSwitchService = providerSwitchService;
    private readonly IViolationSwitchTrigger _violationSwitchTrigger = violationSwitchTrigger;
    private readonly IFFmpegContext _ffmpegContext = ffmpegContext;
    private readonly IChannelWarmupService _channelWarmupService = channelWarmupService;

    private volatile ChannelProviderMap? _channelProviderMap;

    /// <inheritdoc />
    public string Name => "Xtream Live";

    /// <inheritdoc />
    public string HomePageUrl => string.Empty;

    /// <inheritdoc />
    public async Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;
        return config.MergeDuplicateChannels
            ? await GetDeduplicatedChannelsAsync(cancellationToken).ConfigureAwait(false)
            : await GetAllChannelsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IEnumerable<ChannelInfo>> GetDeduplicatedChannelsAsync(CancellationToken cancellationToken)
    {
        // Refresh connection status for all enabled providers to get current availability
        var enabledProviders = Plugin.Instance.Configuration.GetEnabledProviders().ToList();
        if (_failoverService.NeedsRefresh() && enabledProviders.Count > 0)
        {
            _logger.LogDebugIfEnabled("Refreshing provider connection status for connection-aware channel ordering");
            await _failoverService.RefreshAsync(enabledProviders, cancellationToken).ConfigureAwait(false);
        }

        // Build channel map with health-aware provider ordering
        // Uses resilience service for unified health scoring (success rate + capacity + circuit state)
        // Optionally filter out channels from providers at capacity
        var config = Plugin.Instance.Configuration;

        var channelMap = _channelProviderMap = await StreamService
            .GetDeduplicatedChannelMap(_failoverService, config.FilterChannelsByCapacity, cancellationToken)
            .ConfigureAwait(false);

        // Log channel map statistics for debugging failover
        var multiProviderChannels = channelMap.Channels.Where(c => c.ProviderCount > 1).ToList();
        var singleProviderChannels = channelMap.Channels.Where(c => c.ProviderCount == 1).ToList();
        _logger.LogDebugIfEnabled(
            "Channel map built: {TotalChannels} unique channels, {GuidCount} total GUIDs, {MultiProvider} with failover ({MultiProviderPct}%), {SingleProvider} single-provider, {SkippedCount} skipped",
            channelMap.ChannelCount,
            channelMap.GuidCount,
            multiProviderChannels.Count,
            channelMap.ChannelCount > 0 ? (multiProviderChannels.Count * 100 / channelMap.ChannelCount) : 0,
            singleProviderChannels.Count,
            channelMap.SkippedCount
        );

        // Log top multi-provider channels for verification
        if (multiProviderChannels.Count > 0)
        {
            foreach (var ch in multiProviderChannels.OrderByDescending(c => c.ProviderCount).Take(5))
            {
                _logger.LogDebugIfEnabled(
                    "  Multi-provider channel: '{ChannelName}' ({ProviderCount} providers: {Providers})",
                    ch.DisplayName,
                    ch.ProviderCount,
                    string.Join(", ", ch.Providers.Select(p => p.Provider.Name))
                );
            }
        }

        // Log sample single-provider channels to check for potential matching issues
        if (singleProviderChannels.Count > 0)
        {
            foreach (var ch in singleProviderChannels.Take(3))
            {
                _logger.LogDebugIfEnabled(
                    "  Single-provider channel: '{ChannelName}' (normalized: '{NormalizedName}', provider: {Provider})",
                    ch.DisplayName,
                    ch.NormalizedName,
                    ch.Providers.Count > 0 ? ch.Providers[0].Provider.Name : "none"
                );
            }
        }

        if (Plugin.Instance.Configuration.UseExternalLogoFallback)
        {
            await _externalEpgProvider.PrewarmAsync(cancellationToken).ConfigureAwait(false);
        }

        List<ChannelInfo> items = [];
        foreach (var channelWithProviders in channelMap.Channels)
        {
            var best = channelWithProviders.Best;
            if (best == null)
            {
                continue;
            }

            var channel = best.Stream;
            var provider = best.Provider;
            var imageUrl = channelWithProviders.BestImageUrl;

            if (string.IsNullOrEmpty(imageUrl) && Plugin.Instance.Configuration.UseExternalLogoFallback)
            {
                imageUrl = _externalEpgProvider.GetLogoUrl(channelWithProviders.DisplayName);
            }

            var channelInfo = new ChannelInfo
            {
                Id = StreamService.ToProviderGuid(StreamService.LiveTvPrefix, provider, channel.StreamId).ToString(),
                Number = channel.Num.ToString(CultureInfo.InvariantCulture),
                HasImage = !string.IsNullOrEmpty(imageUrl),
                ImageUrl = imageUrl,
                Name = channelWithProviders.DisplayName,
                Tags = [],
            };

            if (channelWithProviders.ProviderCount > 1)
            {
                _logger.LogDebugIfEnabled(
                    "Channel '{ChannelName}' has {ProviderCount} providers available (best: {BestProvider})",
                    channelWithProviders.DisplayName,
                    channelWithProviders.ProviderCount,
                    provider.Name
                );
            }

            items.Add(channelInfo);
        }

        // Log connection-aware ordering status
        var statuses = _failoverService.GetProviderStates();
        if (statuses.Count > 0)
        {
            var onlineCount = statuses.Values.Count(s => s.IsOnline);
            var totalCapacity = statuses.Values.Where(s => s.IsOnline).Sum(s => s.MaxConnections);
            var totalActive = statuses.Values.Where(s => s.IsOnline).Sum(s => s.ActiveConnections);
            _logger.PluginLogInformation(
                "Loaded {ChannelCount} deduplicated channels from {ProviderCount} providers with connection-aware ordering ({OnlineProviders} online, {ActiveConnections}/{TotalCapacity} connections)",
                items.Count,
                enabledProviders.Count,
                onlineCount,
                totalActive,
                totalCapacity
            );
        }
        else
        {
            _logger.PluginLogInformation(
                "Loaded {ChannelCount} deduplicated channels from {ProviderCount} providers (quality-only ordering, no connection data)",
                items.Count,
                enabledProviders.Count
            );
        }

        if (channelMap.SkippedCount > 0)
        {
            _logger.PluginLogWarning(
                "Skipped {SkippedCount} streams with empty names after normalization (e.g., channels named only 'HD' or '4K')",
                channelMap.SkippedCount
            );
        }

        return items;
    }

    private static async Task<IEnumerable<ChannelInfo>> GetAllChannelsAsync(CancellationToken cancellationToken)
    {
        List<ChannelInfo> items = [];
        foreach (var ps in await StreamService.GetAllLiveStreams(cancellationToken).ConfigureAwait(false))
        {
            var channel = ps.Stream;
            var provider = ps.Provider;
            var parsed = StreamService.ParseName(channel.Name);
            var hasImage = !string.IsNullOrEmpty(channel.StreamIcon);

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
                    Tags = [.. parsed.Tags],
                }
            );
        }

        return items;
    }

    /// <inheritdoc />
    public Task CancelTimerAsync(string timerId, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    public Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    public Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IEnumerable<TimerInfo>>([]);

    /// <inheritdoc />
    public Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IEnumerable<SeriesTimerInfo>>([]);

    /// <inheritdoc />
    public Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    public Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    public Task UpdateTimerAsync(TimerInfo updatedTimer, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    public Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    public async Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(
        string channelId,
        CancellationToken cancellationToken
    )
    {
        var source = await GetChannelStream(channelId, string.Empty, cancellationToken).ConfigureAwait(false);
        return [source];
    }

    /// <inheritdoc />
    public Task<MediaSourceInfo> GetChannelStream(
        string channelId,
        string streamId,
        CancellationToken cancellationToken
    ) => throw new NotImplementedException();

    /// <inheritdoc />
    public async Task CloseLiveStream(string id, CancellationToken cancellationToken)
    {
        _logger.PluginLogInformation("Closing livestream {ChannelId}", id);
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
        _logger.PluginLogInformation(
            "GetProgramsAsync called for channel {ChannelId}, date range: {StartDate} to {EndDate}",
            channelId,
            startDateUtc,
            endDateUtc
        );

        _epgRefreshTracker.RecordRequest();

        var guid = Guid.Parse(channelId);
        StreamService.FromGuid(guid, out var prefix, out _, out _, out _);
        if (prefix != StreamService.LiveTvPrefix)
        {
            throw new ArgumentException("Unsupported channel");
        }

        var key = "xtream-epg-" + channelId;
        if (_memoryCache.TryGetValue<ICollection<ProgramInfo>>(key, out var cachedItems) && cachedItems != null)
        {
            List<ProgramInfo> cachedFiltered =
            [
                .. cachedItems.Where(epg => epg.EndDate >= startDateUtc && epg.StartDate < endDateUtc),
            ];
            _logger.PluginLogInformation(
                "Returning {Count} cached programs for channel {ChannelId}",
                cachedFiltered.Count,
                channelId
            );
            _epgRefreshTracker.RecordSuccess();
            return cachedFiltered;
        }

        var config = Plugin.Instance.Configuration;
        List<ProgramInfo> items = [];
        var providersToTry = GetEpgProvidersForChannel(guid, config);

        foreach (var providerInfo in providersToTry)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var providerStreamId = providerInfo.Stream.StreamId;
                _logger.LogDebugIfEnabled(
                    "Trying EPG from provider '{ProviderName}' stream {StreamId} for channel {ChannelId}",
                    providerInfo.Provider.Name,
                    providerStreamId,
                    channelId
                );

                var programs = await _epgProvider
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

                _logger.PluginLogInformation(
                    "EPG provider '{ProviderName}' returned {Count} programs for channel {ChannelId}",
                    providerInfo.Provider.Name,
                    programs.Count,
                    channelId
                );

                var programCounter = 0;
                var invalidTimeCount = 0;

                foreach (var prog in programs)
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
                    _logger.PluginLogWarning(
                        "Skipped {InvalidCount} EPG entries with invalid times for channel {ChannelId}",
                        invalidTimeCount,
                        channelId
                    );
                }

                if (items.Count > 0)
                {
                    var minDate = items.Min(x => x.StartDate);
                    var maxDate = items.Max(x => x.EndDate);
                    _logger.PluginLogInformation(
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
                _logger.PluginLogWarning(
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

        _ = _memoryCache.Set(key, items, DateTimeOffset.Now.AddMinutes(30));

        List<ProgramInfo> filtered = [.. items.Where(epg => epg.EndDate >= startDateUtc && epg.StartDate < endDateUtc)];

        _logger.PluginLogInformation(
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
        List<ProgramInfo> items = [];

        var channelWithProviders = _channelProviderMap?.GetByGuid(channelGuid);
        if (channelWithProviders == null)
        {
            return items;
        }

        var channelName = channelWithProviders.DisplayName;
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
            var programs = await _externalEpgProvider
                .GetProgramsByNameAsync(channelName, cancellationToken)
                .ConfigureAwait(false);
            if (programs.Count == 0)
            {
                _logger.LogDebugIfEnabled("No external EPG data for channel '{ChannelName}'", channelName);
                return items;
            }

            _logger.PluginLogInformation(
                "External EPG returned {Count} programs for channel '{ChannelName}'",
                programs.Count,
                channelName
            );

            var programCounter = 0;
            var invalidTimeCount = 0;

            foreach (var prog in programs)
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
                _logger.PluginLogWarning(
                    "Skipped {InvalidCount} external EPG entries with invalid times for channel '{ChannelName}'",
                    invalidTimeCount,
                    channelName
                );
            }
        }
        catch (Exception ex)
        {
            _logger.PluginLogWarning(
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
            var channelWithProviders = _channelProviderMap.GetByGuid(channelGuid);
            if (channelWithProviders != null)
            {
                return channelWithProviders.Providers;
            }
        }

        var provider = StreamService.FindProviderForGuid(channelGuid);
        if (provider == null)
        {
            return [];
        }

        StreamService.FromGuid(channelGuid, out var _, out var streamId, out var _, out var _);
        var streamInfo = new StreamInfo { StreamId = streamId, Name = string.Empty };

        return [new ProviderStreamInfo(provider, streamInfo)];
    }

    /// <inheritdoc />
    public Task ResetTuner(string id, CancellationToken cancellationToken) => throw new NotImplementedException();

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
        var startTime = DateTime.UtcNow;
        string? errorMessage = null;

        if (_epgProvider is IEpgProviderWithPrewarm prewarmProvider)
        {
            _logger.PluginLogInformation("Pre-warming EPG providers before refresh...");
            try
            {
                await prewarmProvider.PrewarmAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.PluginLogWarning(ex, "EPG provider pre-warming failed, continuing with refresh");
            }
        }

        List<ProviderStreamInfo> channelList =
        [
            .. await StreamService.GetAllLiveStreams(cancellationToken).ConfigureAwait(false),
        ];
        _logger.PluginLogInformation(
            "Starting parallel EPG refresh for {Count} channels across all providers",
            channelList.Count
        );

        await _discordService.NotifyEpgRefreshStartedAsync(channelList.Count, cancellationToken).ConfigureAwait(false);

        using var semaphore = new SemaphoreSlim(MaxParallelEpgRequests);

        var successCount = 0;
        var retriedSuccessCount = 0;
        var httpErrorCount = 0;
        var noDataCount = 0;
        ConcurrentBag<string> failedChannels = [];
        var retryQueue = new ConcurrentQueue<ProviderStreamInfo>();

        try
        {
            var tasks = channelList.Select(async providerStreamInfo =>
            {
                var channel = providerStreamInfo.Stream;
                var provider = providerStreamInfo.Provider;

                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    switch (
                        await TryFetchEpgForChannelAsync(channel, provider, cancellationToken).ConfigureAwait(false)
                    )
                    {
                        case EpgFetchStatus.Success:
                            _ = Interlocked.Increment(ref successCount);
                            break;
                        case EpgFetchStatus.NoData:
                            _ = Interlocked.Increment(ref noDataCount);
                            failedChannels.Add(StreamService.ParseName(channel.Name).Title);
                            break;
                        case EpgFetchStatus.HttpError:
                            _ = Interlocked.Increment(ref httpErrorCount);
                            retryQueue.Enqueue(providerStreamInfo);
                            break;
                        case EpgFetchStatus.Error:
                            failedChannels.Add(StreamService.ParseName(channel.Name).Title);
                            break;
                    }
                }
                finally
                {
                    _ = semaphore.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);

            // Retry failed HTTP requests after a short delay
            if (!retryQueue.IsEmpty)
            {
                _logger.PluginLogInformation("Retrying {Count} failed EPG requests after delay...", retryQueue.Count);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);

                List<ProviderStreamInfo> retryItems = [];
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
                            _ = Interlocked.Increment(ref successCount);
                            _ = Interlocked.Increment(ref retriedSuccessCount);
                        }
                        else
                        {
                            failedChannels.Add(StreamService.ParseName(providerStreamInfo.Stream.Name).Title);
                        }
                    }
                    finally
                    {
                        _ = semaphore.Release();
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
            _logger.PluginLogError(ex, "EPG refresh failed with unexpected error");
        }

        var elapsed = DateTime.UtcNow - startTime;
        _logger.PluginLogInformation(
            "Parallel EPG refresh complete: {SuccessCount}/{TotalCount} channels with EPG data in {Elapsed:F1}s (retried: {RetriedCount}, HTTP errors: {HttpErrors}, no data: {NoData})",
            successCount,
            channelList.Count,
            elapsed.TotalSeconds,
            retriedSuccessCount,
            httpErrorCount,
            noDataCount
        );

        var result = new EpgRefreshResult
        {
            SuccessCount = successCount,
            TotalCount = channelList.Count,
            RetriedSuccessCount = retriedSuccessCount,
            Duration = elapsed,
            FailedChannels = [.. failedChannels.Take(50)],
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
        var channelId = StreamService.ToProviderGuid(StreamService.LiveTvPrefix, provider, channel.StreamId).ToString();
        try
        {
            var programs = await _epgProvider
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

                _ = _memoryCache.Set(
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
            _logger.PluginLogWarning(
                "HTTP error fetching EPG for channel {ChannelName} (stream {StreamId}): {Error}",
                channel.Name,
                channel.StreamId,
                ex.Message
            );
            return EpgFetchStatus.HttpError;
        }
        catch (Exception ex)
        {
            _logger.PluginLogWarning(
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
        var guid = Guid.Parse(channelId);
        StreamService.FromGuid(guid, out var prefix, out _, out _, out _);
        if (prefix != StreamService.LiveTvPrefix)
        {
            throw new ArgumentException("Unsupported channel");
        }

        var config = Plugin.Instance.Configuration;
        var failedProviders = new HashSet<string>(StringComparer.Ordinal);
        Exception? lastException = null;
        var attemptNumber = 0;

        // Time-budgeted failover: industry standard 5-10s total before returning error
        var failoverBudgetMs = StreamingTimeoutPolicy.GetFailoverBudgetMs(config);
        var maxAttempts = StreamingTimeoutPolicy.GetMaxFailoverAttempts(config);
        var failoverDeadline = DateTime.UtcNow.AddMilliseconds(failoverBudgetMs);

        _logger.LogDebugIfEnabled(
            "Starting failover for channel {ChannelId} with {BudgetMs}ms budget, max {MaxAttempts} attempts",
            channelId,
            failoverBudgetMs,
            maxAttempts
        );

        // Get providers, filtering out blacklisted ones and sorting by health
        var providersToTry = GetProvidersForChannelWithHealth(guid);
        var isSingleProviderScenario = providersToTry.Count == 1;

        // For single provider, repeat it to allow multiple retry attempts
        if (isSingleProviderScenario && providersToTry.Count > 0 && maxAttempts > 1)
        {
            var singleProvider = providersToTry[0];
            for (var i = 1; i < maxAttempts; i++)
            {
                providersToTry.Add(singleProvider);
            }
        }

        _logger.LogDebugIfEnabled(
            "Found {ProviderCount} provider(s) for channel {ChannelId}: [{ProviderNames}]",
            providersToTry.Count,
            channelId,
            string.Join(
                ", ",
                providersToTry.Select(p =>
                    $"{p.Provider.Name} (score:{_failoverService.GetSelectionScore(p.Provider.Id).ToString(CultureInfo.InvariantCulture)})"
                )
            )
        );

        // Warn if budget is too small for effective failover
        const int MinPerAttemptMs = StreamingTimeoutPolicy.MinPerAttemptTimeoutMs;
        var effectiveMaxAttempts = failoverBudgetMs / MinPerAttemptMs;
        if (providersToTry.Count > 1 && effectiveMaxAttempts < 2)
        {
            _logger.PluginLogWarning(
                "Failover budget ({BudgetMs}ms) is too small for multiple providers. "
                    + "With {MinPerAttemptMs}ms minimum per attempt, only {EffectiveAttempts} attempt(s) possible. "
                    + "Consider increasing FailoverBudgetSeconds to {RecommendedSeconds}s in Advanced settings.",
                failoverBudgetMs,
                MinPerAttemptMs,
                effectiveMaxAttempts,
                MinPerAttemptMs * 3 / 1000
            );
        }

        if (isSingleProviderScenario)
        {
            _logger.LogDebugIfEnabled(
                "Single provider scenario for channel {ChannelId} - blacklist will be bypassed",
                channelId
            );
        }

        foreach (var providerInfo in providersToTry)
        {
            // Check total failover budget
            var remainingBudgetMs = (int)(failoverDeadline - DateTime.UtcNow).TotalMilliseconds;
            if (remainingBudgetMs <= 0)
            {
                _logger.PluginLogWarning(
                    "Failover budget exhausted ({BudgetMs}ms) after {Attempts} attempts for channel {ChannelId}",
                    failoverBudgetMs,
                    attemptNumber,
                    channelId
                );
                break;
            }

            // Check user cancellation (pressing stop)
            cancellationToken.ThrowIfCancellationRequested();

            var provider = providerInfo.Provider;
            var providerStreamId = providerInfo.Stream.StreamId;

            // Skip unavailable providers unless it's the only provider available
            if (!isSingleProviderScenario && !_failoverService.IsAvailable(provider.Id))
            {
                _logger.LogDebugIfEnabled(
                    "Skipping blacklisted provider '{ProviderName}' for channel {ChannelId}",
                    provider.Name,
                    channelId
                );
                continue;
            }

            attemptNumber++;

            // Check max attempts
            if (attemptNumber > maxAttempts)
            {
                _logger.LogDebugIfEnabled(
                    "Max failover attempts ({MaxAttempts}) reached for channel {ChannelId}",
                    maxAttempts,
                    channelId
                );
                break;
            }

            // Apply fast backoff delay between failover attempts (not on first attempt)
            if (failedProviders.Count > 0)
            {
                var backoffMs = StreamingTimeoutPolicy.CalculateFailoverBackoff(attemptNumber);
                _logger.PluginLogInformation(
                    "Failover attempt {Attempt}/{Max}: trying '{ProviderName}' in {BackoffMs}ms ({RemainingMs}ms budget remaining)",
                    attemptNumber,
                    maxAttempts,
                    provider.Name,
                    backoffMs,
                    remainingBudgetMs
                );
                await Task.Delay(Math.Min(backoffMs, remainingBudgetMs), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _logger.PluginLogInformation(
                    "Failover attempt {Attempt}/{Max}: trying '{ProviderName}' ({RemainingMs}ms budget remaining)",
                    attemptNumber,
                    maxAttempts,
                    provider.Name,
                    remainingBudgetMs
                );
            }

            try
            {
                // Calculate per-attempt timeout to allow multiple providers within budget
                // Uses fair-share allocation: budget / remaining attempts (with minimum threshold)
                var attemptsRemaining = maxAttempts - attemptNumber + 1;
                var perAttemptTimeoutMs = StreamingTimeoutPolicy.CalculatePerAttemptTimeout(
                    remainingBudgetMs,
                    attemptsRemaining,
                    config
                );

                _logger.LogDebugIfEnabled(
                    "Per-attempt timeout: {TimeoutMs}ms (budget: {BudgetMs}ms, attempts remaining: {Remaining})",
                    perAttemptTimeoutMs,
                    remainingBudgetMs,
                    attemptsRemaining
                );

                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCts.CancelAfter(perAttemptTimeoutMs);

                var stream = await TryGetStreamFromProvider(
                        provider,
                        providerStreamId,
                        providerInfo.Stream.Name,
                        currentLiveStreams,
                        attemptCts.Token
                    )
                    .ConfigureAwait(false);

                // Record success in resilience service
                _failoverService.RecordSuccess(provider.Id);

                if (failedProviders.Count > 0)
                {
                    var elapsedMs = failoverBudgetMs - (int)(failoverDeadline - DateTime.UtcNow).TotalMilliseconds;
                    _logger.PluginLogInformation(
                        "Failover successful: Connected to '{ProviderName}' after {FailCount} failed attempts in {ElapsedMs}ms",
                        provider.Name,
                        failedProviders.Count,
                        elapsedMs
                    );
                }

                // Notify warmup service for Fast Channel Change (FCC)
                // This triggers predictive warmup of adjacent channels
                // Extract base URLs from providers for connection pooling
                var providerUrls = providersToTry
                    .Select(p => p.Provider.BaseUrl)
                    .Where(url => !string.IsNullOrEmpty(url))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                _channelWarmupService.OnChannelViewing(channelId, providerUrls);

                return stream;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Per-attempt timeout (not user cancellation)
                _ = failedProviders.Add(provider.Id);
                lastException = new TimeoutException($"Provider '{provider.Name}' timed out");

                // Don't blacklist single providers - they're our only option
                const ProviderFailureReason failureReason = ProviderFailureReason.Timeout;
                var wasBlacklisted = false;
                if (!isSingleProviderScenario)
                {
                    wasBlacklisted = _failoverService.RecordFailure(provider.Id, failureReason, provider.Name);
                }

                _logger.PluginLogWarning(
                    "Provider '{ProviderName}' timed out for channel {ChannelId} ({Reason}), attempting failover ({AttemptCount}/{MaxAttempts}){Blacklisted}",
                    provider.Name,
                    channelId,
                    failureReason,
                    failedProviders.Count,
                    maxAttempts,
                    wasBlacklisted ? " [BLACKLISTED]" : (isSingleProviderScenario ? " [SINGLE PROVIDER]" : string.Empty)
                );
            }
            catch (Exception ex) when (config.EnableProviderFailover && failedProviders.Count < maxAttempts)
            {
                _ = failedProviders.Add(provider.Id);
                lastException = ex;

                // Record failure and potentially blacklist (but not for single providers)
                var failureReason = CategorizeException(ex);
                var wasBlacklisted = false;
                if (!isSingleProviderScenario)
                {
                    wasBlacklisted = _failoverService.RecordFailure(provider.Id, failureReason, provider.Name);
                }

                _logger.PluginLogWarning(
                    ex,
                    "Failed to connect to provider '{ProviderName}' for channel {ChannelId} ({Reason}), attempting failover ({AttemptCount}/{MaxAttempts}){Blacklisted}",
                    provider.Name,
                    channelId,
                    failureReason,
                    failedProviders.Count,
                    maxAttempts,
                    wasBlacklisted ? " [BLACKLISTED]" : (isSingleProviderScenario ? " [SINGLE PROVIDER]" : string.Empty)
                );
            }
        }

        var totalElapsedMs =
            failoverBudgetMs - Math.Max(0, (int)(failoverDeadline - DateTime.UtcNow).TotalMilliseconds);
        throw new InvalidOperationException(
            $"Failed to connect to any provider for channel {channelId} after {failedProviders.Count} attempts in {totalElapsedMs}ms",
            lastException
        );
    }

    private IEnumerable<ProviderStreamInfo> GetProvidersForChannel(Guid channelGuid)
    {
        // Primary: Look up in channel-provider map
        var channelWithProviders = _channelProviderMap?.GetByGuid(channelGuid);
        if (channelWithProviders != null)
        {
            return channelWithProviders.Providers;
        }

        // Fallback: GUID not found in map (stale GUID from before channel refresh)
        // Return only the primary provider - no cross-provider failover available
        StreamService.FromGuid(channelGuid, out _, out var streamId, out _, out _);
        var primaryProvider = StreamService.FindProviderForGuid(channelGuid);
        if (primaryProvider == null)
        {
            _logger.PluginLogWarning(
                "No provider found for channel GUID {ChannelGuid}. Try refreshing Live TV channels.",
                channelGuid
            );
            return [];
        }

        var primaryStreamInfo = new StreamInfo { StreamId = streamId, Name = string.Empty };
        var primaryProviderInfo = new ProviderStreamInfo(primaryProvider, primaryStreamInfo);

        _logger.PluginLogWarning(
            "Stream {StreamId} on provider '{ProviderName}' not in channel map - failover unavailable. "
                + "Refresh Live TV channels to enable cross-provider failover.",
            streamId,
            primaryProvider.Name
        );

        return [primaryProviderInfo];
    }

    /// <summary>
    /// Categorizes an exception to determine the appropriate failure reason for resilience tracking.
    /// </summary>
    private static ProviderFailureReason CategorizeException(Exception ex)
    {
        return ex switch
        {
            HttpRequestException httpEx when httpEx.StatusCode == System.Net.HttpStatusCode.TooManyRequests =>
                ProviderFailureReason.RateLimited,
            HttpRequestException httpEx when (int?)httpEx.StatusCode is >= 400 and < 500 =>
                ProviderFailureReason.ClientError,
            HttpRequestException httpEx when (int?)httpEx.StatusCode >= 500 => ProviderFailureReason.ServerError,
            HttpRequestException => ProviderFailureReason.NetworkError,
            TimeoutException => ProviderFailureReason.Timeout,
            OperationCanceledException => ProviderFailureReason.Timeout,
            _ when ex.Message.Contains("connection limit", StringComparison.OrdinalIgnoreCase) =>
                ProviderFailureReason.ConnectionLimit,
            _ => ProviderFailureReason.Unknown,
        };
    }

    /// <summary>
    /// Gets providers for a channel, sorted by selection score (best first).
    /// Unavailable providers are included but will be filtered in the failover loop.
    /// </summary>
    private List<ProviderStreamInfo> GetProvidersForChannelWithHealth(Guid channelGuid)
    {
        var baseProviders = GetProvidersForChannel(channelGuid).ToList();

        if (baseProviders.Count <= 1)
        {
            return baseProviders;
        }

        // Use resilience service to sort by selection score (combines health + availability)
        // This provides real-time ordering based on current provider state
        return [.. _failoverService.GetSortedProviders(baseProviders, forceIncludeAll: true)];
    }

    private async Task<ILiveStream> TryGetStreamFromProvider(
        XtreamProvider provider,
        int streamId,
        string streamName,
        List<ILiveStream> currentLiveStreams,
        CancellationToken cancellationToken
    )
    {
        _logger.LogDebugIfEnabled(
            "TryGetStreamFromProvider: provider='{ProviderName}' ({ProviderId}), streamId={StreamId}, streamName='{StreamName}'",
            provider.Name,
            provider.Id,
            streamId,
            streamName
        );

        var plugin = Plugin.Instance;
        string? channelName = null;

        if (!string.IsNullOrEmpty(streamName))
        {
            channelName = StreamService.ParseName(streamName).Title;
            _logger.LogDebugIfEnabled("Parsed channel name from streamName: '{ChannelName}'", channelName);
        }
        else
        {
            _logger.LogDebugIfEnabled("No streamName provided, looking up channel info from provider API...");
            try
            {
                var info = (
                    await StreamService
                        .GetLiveStreamsWithOverridesForProvider(provider, cancellationToken)
                        .ConfigureAwait(false)
                ).FirstOrDefault(s => s.StreamId == streamId);
                if (info != null)
                {
                    channelName = StreamService.ParseName(info.Name).Title;
                    _logger.LogDebugIfEnabled("Found channel name from API: '{ChannelName}'", channelName);
                }
                else
                {
                    _logger.LogDebugIfEnabled("Stream {StreamId} not found in provider's stream list", streamId);
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.PluginLogWarning(ex, "Failed to look up channel name for stream {StreamId}", streamId);
            }
        }

        var mediaSourceInfo = StreamService.GetMediaSourceInfo(
            provider,
            StreamType.Live,
            streamId,
            channelName,
            "ts",
            restream: true
        );
        var stream = currentLiveStreams.Find(s =>
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

            _logger.PluginLogWarning(
                "Found disposed Restream instance for stream {StreamId}, creating new one",
                streamId
            );
        }

        _logger.PluginLogInformation(
            "Creating new Restream instance for stream {StreamId} from provider '{ProviderName}'",
            streamId,
            provider.Name
        );

        _logger.LogDebugIfEnabled(
            "MediaSourceInfo: Id={MediaSourceId}, Path={Path}, Container={Container}",
            mediaSourceInfo.Id,
            mediaSourceInfo.Path,
            mediaSourceInfo.Container
        );

        var newStream = new Restream(
            appHost: _appHost,
            httpClientFactory: _httpClientFactory,
            logger: _loggerFactory.CreateLogger<Restream>(),
            loggerFactory: _loggerFactory,
            mediaSource: mediaSourceInfo,
            discordService: _discordService,
            providerSwitchService: _providerSwitchService,
            failoverService: _failoverService,
            violationSwitchTrigger: _violationSwitchTrigger,
            ffmpegContext: _ffmpegContext
        );

        _logger.LogDebugIfEnabled("Restream instance created, calling Open() for stream {StreamId}...", streamId);

        try
        {
            var openStartTime = DateTime.UtcNow;
            await newStream.Open(cancellationToken).ConfigureAwait(false);
            var openDuration = (DateTime.UtcNow - openStartTime).TotalMilliseconds;

            _logger.LogDebugIfEnabled(
                "Restream.Open() completed successfully for stream {StreamId} in {DurationMs}ms",
                streamId,
                openDuration
            );
        }
        catch (Exception ex)
        {
            _logger.LogDebugIfEnabled(
                "Restream.Open() FAILED for stream {StreamId}: {ExceptionType} - {Message}",
                streamId,
                ex.GetType().Name,
                ex.Message
            );
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
        var config = Plugin.Instance.Configuration;
        if (!config.EnforceConnectionLimit)
        {
            return;
        }

        var maxConnections = config.MaxConcurrentStreams;
        if (maxConnections <= 0)
        {
            var firstProvider = config.GetEnabledProviders().FirstOrDefault();
            if (firstProvider != null)
            {
                try
                {
                    using var client = new XtreamClient(
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
                    _logger.PluginLogWarning(ex, "Failed to get provider connection limits, defaulting to 1");
                    maxConnections = 1;
                }
            }
            else
            {
                maxConnections = 1;
            }
        }

        var currentStreams = Restream.GetActiveStreamCount();
        _logger.LogDebugIfEnabled(
            "Connection limit check: {CurrentStreams}/{MaxConnections} streams active",
            currentStreams,
            maxConnections
        );

        if (currentStreams > maxConnections)
        {
            var streamsToKill = currentStreams - maxConnections;
            if (!config.AutoKillOldestStream)
            {
                throw new InvalidOperationException(
                    $"Connection limit reached ({currentStreams}/{maxConnections}). "
                        + "Cannot open new stream. Close an existing stream first or enable auto-kill."
                );
            }

            List<StreamInfoSnapshot> snapshots = [.. Restream.GetActiveStreamSnapshots().OrderBy(s => s.StartTime)];

            for (var i = 0; i < streamsToKill && i < snapshots.Count; i++)
            {
                var oldest = snapshots[i];
                var streamAge = DateTime.UtcNow - oldest.StartTime;
                _logger.PluginLogInformation(
                    "Connection limit exceeded ({CurrentStreams}/{MaxConnections}). Killing oldest stream: {StreamName} ({StreamId}), age: {Age}",
                    currentStreams,
                    maxConnections,
                    oldest.ChannelName,
                    oldest.StreamId,
                    streamAge
                );

                _ = Restream.KillStream(
                    oldest.StreamId,
                    $"Connection limit exceeded ({currentStreams}/{maxConnections}) - auto-killed oldest stream ({oldest.ChannelName})"
                );
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }
}
