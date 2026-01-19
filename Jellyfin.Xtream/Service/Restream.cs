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
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Service.MpegTs.Core;
using Jellyfin.Xtream.Service.MpegTs.Infrastructure;
using Jellyfin.Xtream.Service.MpegTs.TsDuck;
using Jellyfin.Xtream.Service.MpegTs.UseCases;
using Jellyfin.Xtream.Service.ProviderManagement;
using Jellyfin.Xtream.Utility;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// A live stream implementation that broadcasts a single IPTV source to multiple consumers.
/// Uses a circular buffer for multi-reader support with a single HTTP connection.
/// Solves HTTP 406 errors by maintaining only one connection to the provider.
/// </summary>
public class Restream : ILiveStream, IDisposable, IDirectStreamProvider
{
    /// <summary>
    /// The global constant for the restream tuner host.
    /// </summary>
    public const string TunerHost = "Xtream-Restream";

    // Buffer sizes based on stream quality
    private const int SdBufferSize = 33554432;
    private const int HdBufferSize = 67108864;
    private const int UhdBufferSize = 134217728;
    private const int CopyBufferSizeSd = 32768;
    private const int CopyBufferSizeHd = 65536;
    private const int CopyBufferSizeUhd = 262144;

    // Connection and retry limits
    private const int MaxRedirects = 10;
    private const int MaxConnectionAttempts = 10;
    private const int MaxConsecutiveTransientFailures = 5;
    private const int Max406Retries = 3;

    // Cleanup and timing constants
    private const int ConsumerDisconnectGraceSeconds = 5;
    private const int FirstBytePollIntervalMs = 50;
    private const int ProgressLogIntervalSeconds = 60;
    private const int HealthCheckIntervalSeconds = 30;
    private const double BufferUnderrunThresholdPercent = 10.0;
    private const double BufferNearFullThresholdPercent = 90.0;
    private const int BufferUnderrunNotificationThreshold = 5;
    private const int MinimumBytesForHealthyStream = 1048576;

    /// <summary>
    /// Global registry of all active Restream instances for monitoring and management.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Restream> _activeStreams = new(StringComparer.Ordinal);

    private static readonly HttpStatusCode[] _redirects =
    [
        HttpStatusCode.MovedPermanently,
        HttpStatusCode.MovedPermanently,
        HttpStatusCode.PermanentRedirect,
        HttpStatusCode.Found,
    ];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<Restream> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IDiscordNotificationService? _discordService;
    private readonly string _sourceUrl;
    private readonly SemaphoreSlim _openLock = new(1, 1);
    private readonly CircularBufferWriteStream _buffer;
    private readonly RefCountedResourcePool<CircularBufferReadStream> _readerPool;
    private readonly string _streamQuality;
    private readonly DateTime _startTime = DateTime.UtcNow;

    private CancellationTokenSource _tokenSource;
    private Uri? _resolvedUrl;
    private Task? _broadcastTask;
    private int _consumerCount;
    private bool _isDisposed;
    private int _bufferUnderrunCount;
    private int _bufferHealthWarnings;
    private DateTime _lastHealthCheck = DateTime.MinValue;
    private byte[]? _networkBuffer;
    private string? _killReason;
    private CancellationTokenSource? _cleanupCts;

    // Provider switching: single unified service for all switching needs (DIP)
    private readonly IProviderSwitchService? _providerSwitchService;

    // TR 101 290 violation-triggered switching (DIP: injected abstraction)
    private readonly IViolationSwitchTrigger? _violationSwitchTrigger;
    private volatile string _currentSourceUrl;

    // Tier 2: Automatic failover service - single source of truth for provider management
    // Includes metrics tracking, trend analysis, and failover decisions
    private readonly IAutomaticFailoverService? _failoverService;

    // FFmpeg context for native demuxing support in TsIndexer
    private readonly IFFmpegContext? _ffmpegContext;

    // TsDuck analyzer for TR 101 290 monitoring
    private readonly ITsDuckAnalyzer? _tsDuckAnalyzer;

    // Reconnection timestamp continuity tracking
    // These track the timing state at disconnection so we can remap timestamps on reconnection
    private DateTime _lastDisconnectTime;
    private long _lastDisconnectVideoPts;
    private long _lastDisconnectAudioPts;
    private long _lastDisconnectPcr;
    private bool _needsReconnectionRemapping;
    private bool _pendingReconnectionActivation; // True when we need to activate remapping on first data chunk

    // Expected PTS at reconnection - this is what the output timeline should be after remapping
    // Stored when we calculate the expected PTS, used when we activate remapping
    private long _expectedPtsAtReconnection;
    private DateTime _reconnectionTime;

    // Output timeline tracking - independent of provider timestamps
    // Once we start outputting, we track what PTS values we're sending to FFmpeg
    // This is our "output timeline" that must remain continuous across reconnections
    private DateTime _outputTimelineStartTime;
    private long _outputTimelineBasePts; // The first PTS we established as our output baseline
    private bool _outputTimelineInitialized;

    // PTS discontinuity handling
    // Some IPTV streams have systematic PTS discontinuities (e.g., ~9 second jumps between two sources).
    // Use a longer cooldown (15 seconds) to prevent continuous re-remapping that causes A/V desync.
    private DateTime _lastDiscontinuityRemappingTime = DateTime.MinValue;
    private const int DiscontinuityRemappingCooldownMs = 15000; // 15 second cooldown between activations

    // Throttled priority updates - only update every 60 seconds across all streams
    private static DateTime _lastPriorityUpdateTime = DateTime.MinValue;
    private static readonly object _priorityUpdateLock = new();
    private const int PriorityUpdateIntervalSeconds = 60;

    /// <inheritdoc />
    public int ConsumerCount
    {
        get => Interlocked.CompareExchange(ref _consumerCount, 0, 0);
        set => Interlocked.Exchange(ref _consumerCount, value);
    }

    /// <inheritdoc />
    public string OriginalStreamId { get; set; }

    /// <inheritdoc />
    public string TunerHostId => TunerHost;

    /// <inheritdoc />
    public bool EnableStreamSharing => true;

    /// <inheritdoc />
    public MediaSourceInfo MediaSource { get; set; }

    /// <inheritdoc />
    public string UniqueId { get; init; }

    /// <summary>
    /// Gets a value indicating whether this stream has been disposed.
    /// Used to detect stale stream references held by Jellyfin.
    /// </summary>
    public bool IsDisposed => Volatile.Read(in _isDisposed);

    /// <summary>
    /// Initializes a new instance of the <see cref="Restream"/> class.
    /// </summary>
    /// <param name="appHost">Instance of the <see cref="IServerApplicationHost"/> interface.</param>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{Restream}"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="mediaSource">The media which must be restreamed.</param>
    /// <param name="discordService">Optional Discord notification service.</param>
    /// <param name="ffmpegContext">Optional FFmpeg context for native demuxing.</param>
    public Restream(
        IServerApplicationHost appHost,
        IHttpClientFactory httpClientFactory,
        ILogger<Restream> logger,
        ILoggerFactory loggerFactory,
        MediaSourceInfo mediaSource,
        IDiscordNotificationService? discordService = null,
        IFFmpegContext? ffmpegContext = null
    )
        : this(
            appHost,
            httpClientFactory,
            logger,
            loggerFactory,
            mediaSource,
            discordService,
            providerSwitchService: null,
            failoverService: null,
            violationSwitchTrigger: null,
            ffmpegContext
        ) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="Restream"/> class with full provider resilience support.
    /// Follows DIP: depends on abstractions for provider switching and failover services.
    /// </summary>
    /// <param name="appHost">Instance of the <see cref="IServerApplicationHost"/> interface.</param>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{Restream}"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="mediaSource">The media which must be restreamed.</param>
    /// <param name="discordService">Optional Discord notification service.</param>
    /// <param name="providerSwitchService">Optional unified provider switch service (DIP).</param>
    /// <param name="failoverService">Optional failover service - single source of truth for provider management.</param>
    /// <param name="violationSwitchTrigger">Optional TR 101 290 violation switch trigger (DIP).</param>
    /// <param name="ffmpegContext">Optional FFmpeg context for native demuxing.</param>
    public Restream(
        IServerApplicationHost appHost,
        IHttpClientFactory httpClientFactory,
        ILogger<Restream> logger,
        ILoggerFactory loggerFactory,
        MediaSourceInfo mediaSource,
        IDiscordNotificationService? discordService,
        IProviderSwitchService? providerSwitchService,
        IAutomaticFailoverService? failoverService,
        IViolationSwitchTrigger? violationSwitchTrigger,
        IFFmpegContext? ffmpegContext = null
    )
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _discordService = discordService;
        _providerSwitchService = providerSwitchService;
        _failoverService = failoverService;
        _violationSwitchTrigger = violationSwitchTrigger;
        _ffmpegContext = ffmpegContext;
        MediaSource = mediaSource;
        _tokenSource = new CancellationTokenSource();
        _streamQuality = DetectStreamQuality(mediaSource);
        var bufferSize = GetBufferSize(_streamQuality);

        // Create TsDuck analyzer for TR 101 290 monitoring
        _tsDuckAnalyzer = TsDuckAnalyzerFactory.Create(logger: _logger);

        _buffer = new CircularBufferWriteStream(bufferSize, _loggerFactory, _ffmpegContext, _tsDuckAnalyzer);
        _buffer.TsIndexer.StreamQualityViolation += OnStreamQualityViolation;
        _buffer.TsIndexer.SyncDriftDetected += OnSyncDriftDetected;
        _buffer.TsIndexer.PtsDiscontinuityDetected += OnPtsDiscontinuityDetected;

        // Wire TsDuck metrics to ProviderSwitchService for combined quality monitoring
        if (_tsDuckAnalyzer != null && _providerSwitchService != null)
        {
            _tsDuckAnalyzer.MetricsUpdated += OnTsDuckMetricsUpdated;
        }

        _logger.PluginLogInformation(
            "Initialized stream {StreamId} ({Quality}) with automatic quality-based buffering ({BufferSizeMB}MB buffer)",
            mediaSource.Id,
            _streamQuality,
            (double)bufferSize / 1048576.0
        );
        OriginalStreamId = MediaSource.Id;
        UniqueId = Guid.NewGuid().ToString();
        _sourceUrl = MediaSource.Path;
        _currentSourceUrl = _sourceUrl;
        var path = "/LiveTv/LiveStreamFiles/" + UniqueId + "/stream.ts";
        MediaSource.Path = appHost.GetSmartApiUrl(IPAddress.Any) + path;
        MediaSource.EncoderPath = appHost.GetApiUrlForLocalAccess() + path;
        MediaSource.Protocol = MediaProtocol.Http;
        _readerPool = new RefCountedResourcePool<CircularBufferReadStream>(CreateReaderStream, OnConsumerCountChanged);
        _ = _activeStreams.TryAdd(MediaSource.Id, this);
        _logger.LogDebugIfEnabled(
            "Registered Restream {StreamId} (total active: {Count}){ProviderSwitch}{Metrics}",
            MediaSource.Id,
            _activeStreams.Count,
            providerSwitchService != null ? " [provider-switch enabled]" : string.Empty,
            failoverService != null ? " [metrics enabled]" : string.Empty
        );
    }

    /// <summary>
    /// Finalizes an instance of the <see cref="Restream"/> class.
    /// Ensures cleanup of static dictionary entry if Dispose() is not called.
    /// </summary>
    ~Restream()
    {
        Dispose(disposing: false);
    }

    /// <summary>
    /// Factory method to create a new reader stream instance.
    /// </summary>
    private CircularBufferReadStream CreateReaderStream() =>
        new(_buffer, _logger, MediaSource.Id, MediaSource.Name, _discordService, -1);

    /// <summary>
    /// Callback when consumer count changes.
    /// Automatically cleans up streams when all consumers disconnect.
    /// </summary>
    private void OnConsumerCountChanged(int newCount)
    {
        // Update the tracked consumer count
        ConsumerCount = newCount;

        _logger.PluginLogInformation(
            "Consumer count changed for channel {ChannelId}: {Count} active",
            MediaSource.Id,
            newCount
        );

        if (newCount > 0)
        {
            if (_cleanupCts != null)
            {
                _logger.LogDebugIfEnabled(
                    "Consumer reconnected to channel {ChannelId}, cancelling scheduled cleanup",
                    MediaSource.Id
                );
                _cleanupCts.Cancel();
                _cleanupCts.Dispose();
                _cleanupCts = null;
            }
        }
        else
        {
            if (newCount != 0 || _isDisposed)
            {
                return;
            }

            _logger.PluginLogInformation(
                "All consumers disconnected from channel {ChannelId}. Scheduling cleanup in 5 seconds.",
                MediaSource.Id
            );
            _cleanupCts?.Cancel();
            _cleanupCts?.Dispose();
            _cleanupCts = new CancellationTokenSource();
            var cancellationToken = _cleanupCts.Token;

            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(ConsumerDisconnectGraceSeconds), cancellationToken)
                            .ConfigureAwait(false);
                        if (ConsumerCount == 0 && !_isDisposed)
                        {
                            _logger.PluginLogInformation(
                                "No consumers reconnected to channel {ChannelId} after grace period. Cleaning up.",
                                MediaSource.Id
                            );
                            _killReason = "All consumers disconnected";
                            Dispose();
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogDebugIfEnabled(
                            "Cleanup cancelled for channel {ChannelId} - consumer reconnected",
                            MediaSource.Id
                        );
                    }
                },
                CancellationToken.None
            );
        }
    }

    /// <inheritdoc />
    public async Task Open(CancellationToken openCancellationToken)
    {
        // Create timeout-aware cancellation for fast-fail on connection + first byte
        // Industry standard: 5-10s total for initial connection phase
        var streamOpenTimeoutMs = StreamingTimeoutPolicy.GetStreamOpenTimeoutMs();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(openCancellationToken);
        timeoutCts.CancelAfter(streamOpenTimeoutMs);

        await _openLock.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        try
        {
            if (_broadcastTask != null && !_broadcastTask.IsCompleted)
            {
                _logger.LogDebugIfEnabled("Broadcast for channel {ChannelId} is already running.", MediaSource.Id);
                return;
            }

            // If broadcast task completed (died), reset it so we can start fresh
            if (_broadcastTask?.IsCompleted == true)
            {
                _logger.PluginLogWarning(
                    "Broadcast task for channel {ChannelId} had previously completed/failed. Restarting...",
                    MediaSource.Id
                );
                _broadcastTask = null;
            }

            _logger.LogDebugIfEnabled(
                "Starting broadcast for channel {ChannelId} from URL: {Url} (timeout: {TimeoutMs}ms)",
                MediaSource.Id,
                _sourceUrl,
                streamOpenTimeoutMs
            );
            _buffer.Reset();
            _logger.LogDebugIfEnabled(
                "Buffer reset for channel {ChannelId} - starting fresh stream session",
                MediaSource.Id
            );

            if (_tokenSource?.IsCancellationRequested ?? false)
            {
                _tokenSource?.Dispose();
                _tokenSource = new CancellationTokenSource();
                _logger.LogDebugIfEnabled("CancellationTokenSource recreated for channel {ChannelId}", MediaSource.Id);
            }
            else
            {
                _tokenSource ??= new CancellationTokenSource();
            }

            _resolvedUrl = await ResolveStreamUrlAsync(_sourceUrl, timeoutCts.Token).ConfigureAwait(false);
            _broadcastTask = BroadcastFromSourceAsync(_tokenSource.Token);

            // Wait for first data with timeout (fast-fail if no data)
            // This detects "connected but no data" scenarios common with overloaded providers
            var firstByteTimeoutMs = StreamingTimeoutPolicy.GetFirstByteTimeoutMs();
            if (!await WaitForFirstDataAsync(firstByteTimeoutMs, timeoutCts.Token).ConfigureAwait(false))
            {
                _logger.PluginLogWarning(
                    "Stream {ChannelId} did not produce data within {TimeoutMs}ms first-byte timeout",
                    MediaSource.Id,
                    firstByteTimeoutMs
                );
                throw new TimeoutException($"Stream did not produce data within {firstByteTimeoutMs}ms");
            }

            _logger.LogDebugIfEnabled(
                "Broadcast started for channel {ChannelId} from {ResolvedUrl} (first data received)",
                MediaSource.Id,
                _resolvedUrl
            );

            // Register stream with provider switch service for lifecycle management
            _providerSwitchService?.RegisterStream(MediaSource.Id, _currentSourceUrl);

            _discordService.SendFireAndForget(svc =>
                svc.NotifyStreamStartAsync(MediaSource.Id, MediaSource.Name ?? "Unknown Channel")
            );
        }
        catch (OperationCanceledException) when (!openCancellationToken.IsCancellationRequested)
        {
            // Timeout occurred (not user cancellation)
            _logger.PluginLogWarning(
                "Stream {ChannelId} connection timed out after {TimeoutMs}ms",
                MediaSource.Id,
                streamOpenTimeoutMs
            );
            throw new TimeoutException($"Stream connection timed out after {streamOpenTimeoutMs}ms");
        }
        finally
        {
            _ = _openLock.Release();
        }
    }

    /// <summary>
    /// Waits for first data to appear in the buffer with timeout.
    /// Enables fast-fail detection for "connected but no data" scenarios.
    /// </summary>
    /// <param name="timeoutMs">Maximum time to wait for first byte.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if data was received, false if timeout expired.</returns>
    private async Task<bool> WaitForFirstDataAsync(int timeoutMs, CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        var deadline = startTime.AddMilliseconds(timeoutMs);

        _logger.LogDebugIfEnabled(
            "WaitForFirstDataAsync: channel {ChannelId}, timeout={TimeoutMs}ms, starting wait...",
            MediaSource.Id,
            timeoutMs
        );

        var pollCount = 0;
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (_buffer.TotalBytesWritten > 0)
            {
                var elapsedMs = (DateTime.UtcNow - startTime).TotalMilliseconds;
                _logger.LogDebugIfEnabled(
                    "WaitForFirstDataAsync: channel {ChannelId} received first data after {ElapsedMs}ms ({BytesWritten} bytes)",
                    MediaSource.Id,
                    elapsedMs,
                    _buffer.TotalBytesWritten
                );
                return true;
            }

            pollCount++;
            await Task.Delay(FirstBytePollIntervalMs, cancellationToken).ConfigureAwait(false);
        }

        var totalElapsedMs = (DateTime.UtcNow - startTime).TotalMilliseconds;
        _logger.LogDebugIfEnabled(
            "WaitForFirstDataAsync: channel {ChannelId} TIMEOUT after {ElapsedMs}ms, {PollCount} polls, {BytesWritten} bytes written",
            MediaSource.Id,
            totalElapsedMs,
            pollCount,
            _buffer.TotalBytesWritten
        );

        return _buffer.TotalBytesWritten > 0;
    }

    /// <summary>
    /// Resolves the final stream URL by following all redirects including HTTPS→HTTP downgrades.
    /// .NET blocks HTTPS→HTTP redirects by default, so we handle them manually.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method implements "zombie backend" detection - a common IPTV provider failure mode where:
    /// 1. Load balancer accepts TCP connection ✓
    /// 2. Load balancer sends 302 redirect to backend ✓
    /// 3. Backend accepts TCP connection ✓
    /// 4. Backend NEVER sends HTTP response headers ✗ (hangs indefinitely)
    /// </para>
    /// <para>
    /// To detect this, we apply a response headers timeout to each redirect hop.
    /// If a backend is dead but accepting TCP, we fail fast and try the next provider.
    /// </para>
    /// </remarks>
    private async Task<Uri> ResolveStreamUrlAsync(string initialUrl, CancellationToken cancellationToken)
    {
        var currentUrl = new Uri(initialUrl);
        var client = _httpClientFactory.CreateClient("XtreamClient");
        var healthValidator = new BackendHealthValidator(client, _logger);

        // Response headers timeout: detects zombie backends that accept TCP but never respond
        var responseHeadersTimeoutMs = StreamingTimeoutPolicy.GetResponseHeadersTimeoutMs();

        for (var redirectCount = 0; redirectCount < MaxRedirects; redirectCount++)
        {
            var requestStartTime = DateTime.UtcNow;
            using var response = await healthValidator
                .SendWithZombieDetectionAsync(currentUrl, responseHeadersTimeoutMs, cancellationToken)
                .ConfigureAwait(false);

            var responseTimeMs = (DateTime.UtcNow - requestStartTime).TotalMilliseconds;
            _logger.LogDebugIfEnabled(
                "URL resolution for channel {ChannelId} - Attempt {Attempt}, URL: {Url}, Status: {StatusCode}, ResponseTime: {ResponseTimeMs}ms",
                MediaSource.Id,
                redirectCount + 1,
                currentUrl,
                response.StatusCode,
                responseTimeMs
            );

            if (_redirects.Contains(response.StatusCode))
            {
                var redirectLocation = response.Headers.Location;
                if (redirectLocation == null)
                {
                    _logger.PluginLogWarning(
                        "Redirect response for channel {ChannelId} missing Location header",
                        MediaSource.Id
                    );
                    break;
                }

                if (!redirectLocation.IsAbsoluteUri)
                {
                    redirectLocation = new Uri(currentUrl, redirectLocation);
                }

                // Log redirect with host change detection (critical for backend validation)
                var isHostChange = !string.Equals(
                    currentUrl.Host,
                    redirectLocation.Host,
                    StringComparison.OrdinalIgnoreCase
                );
                _logger.LogDebugIfEnabled(
                    "Stream for channel {ChannelId} redirected: {OldUrl} → {NewUrl} (HTTPS→HTTP: {IsDowngrade}, HostChange: {IsHostChange})",
                    MediaSource.Id,
                    currentUrl,
                    redirectLocation,
                    currentUrl.Scheme == "https" && redirectLocation.Scheme == "http",
                    isHostChange
                );

                // If redirecting to a different host, validate it can respond before following
                // This catches zombie backend scenarios BEFORE we commit to the redirect
                if (isHostChange && redirectCount < MaxRedirects - 1)
                {
                    var isHealthy = await healthValidator
                        .ValidateRedirectTargetAsync(currentUrl, redirectLocation, cancellationToken)
                        .ConfigureAwait(false);
                    if (!isHealthy)
                    {
                        _logger.PluginLogWarning(
                            "Redirect target {Host} failed health check for channel {ChannelId}. "
                                + "Backend may be at capacity or offline. Failing fast to try next provider.",
                            redirectLocation.Host,
                            MediaSource.Id
                        );
                        throw new HttpRequestException(
                            $"Redirect target {redirectLocation.Host} failed pre-flight health check"
                        );
                    }
                }

                currentUrl = redirectLocation;
                continue;
            }

            if (response.IsSuccessStatusCode)
            {
                if (redirectCount > 0)
                {
                    _logger.PluginLogInformation(
                        "Resolved stream URL for channel {ChannelId} after {Redirects} redirect(s): {FinalUrl}",
                        MediaSource.Id,
                        redirectCount,
                        currentUrl
                    );
                }

                return currentUrl;
            }

            _logger.PluginLogError(
                "Failed to resolve stream URL for channel {ChannelId}. URL: {Url}, Status: {StatusCode}, Reason: {Reason}",
                MediaSource.Id,
                currentUrl,
                response.StatusCode,
                response.ReasonPhrase
            );
            throw new HttpRequestException(
                $"Failed to resolve stream: {response.StatusCode} - {response.ReasonPhrase}"
            );
        }

        _logger.PluginLogError(
            "Too many redirects ({Max}) for channel {ChannelId}. Final URL: {Url}",
            MaxRedirects,
            MediaSource.Id,
            currentUrl
        );
        throw new HttpRequestException($"Too many redirects ({MaxRedirects})");
    }

    /// <summary>
    /// Broadcasts data from the single HTTP source to the circular buffer, which feeds all consumers.
    /// Implements automatic reconnection if the provider closes the connection.
    /// </summary>
    private async Task BroadcastFromSourceAsync(CancellationToken cancellationToken)
    {
        if (_resolvedUrl == null)
        {
            throw new InvalidOperationException("Broadcast URL not resolved");
        }

        var networkBufferSize = _streamQuality switch
        {
            "UHD/4K" => CopyBufferSizeUhd,
            "Full HD" => CopyBufferSizeHd,
            "HD" => CopyBufferSizeHd,
            _ => CopyBufferSizeSd,
        };

        if (_networkBuffer == null || _networkBuffer.Length != networkBufferSize)
        {
            _networkBuffer = new byte[networkBufferSize];
            _logger.LogDebugIfEnabled(
                "Allocated {Size}KB network buffer for {Quality} stream {ChannelId}",
                networkBufferSize / 1024,
                _streamQuality,
                MediaSource.Id
            );
        }

        var bufferMemory = _networkBuffer.AsMemory();
        var totalBytesAllConnections = 0L;
        var sessionStartTime = DateTime.UtcNow;
        var connectionAttempt = 0;
        var consecutiveFailures = 0;

        _logger.LogDebugIfEnabled(
            "BroadcastFromSourceAsync: starting for channel {ChannelId}, max attempts={MaxAttempts}, URL={Url}",
            MediaSource.Id,
            MaxConnectionAttempts,
            _resolvedUrl
        );

        while (!cancellationToken.IsCancellationRequested && connectionAttempt < MaxConnectionAttempts)
        {
            connectionAttempt++;
            HttpResponseMessage? response = null;
            Stream? sourceStream = null;

            try
            {
                _logger.PluginLogInformation(
                    "Opening HTTP connection #{Attempt} for channel {ChannelId} to {Url}",
                    connectionAttempt,
                    MediaSource.Id,
                    _resolvedUrl
                );

                _logger.LogDebugIfEnabled(
                    "HTTP request starting: channel {ChannelId}, attempt #{Attempt}, consecutiveFailures={Failures}",
                    MediaSource.Id,
                    connectionAttempt,
                    consecutiveFailures
                );

                var httpStartTime = DateTime.UtcNow;
                using var request = new HttpRequestMessage(HttpMethod.Get, _resolvedUrl);
                response = await _httpClientFactory
                    .CreateClient("XtreamClient")
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                var httpDuration = (DateTime.UtcNow - httpStartTime).TotalMilliseconds;
                _logger.LogDebugIfEnabled(
                    "HTTP response received: channel {ChannelId}, status={StatusCode}, duration={DurationMs}ms",
                    MediaSource.Id,
                    (int)response.StatusCode,
                    httpDuration
                );

                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        var responseHeaders = string.Join(
                            ", ",
                            response.Headers.Select(h => h.Key + "=" + string.Join(';', h.Value))
                        );
                        var contentType = response.Content.Headers.ContentType?.ToString() ?? "none";
                        _logger.PluginLogError(
                            "Failed to open broadcast source for channel {ChannelId}. Status: {StatusCode} ({StatusCodeInt}), ContentType: {ContentType}, Response Headers: [{Headers}]",
                            MediaSource.Id,
                            response.StatusCode,
                            (int)response.StatusCode,
                            contentType,
                            responseHeaders
                        );

                        if (response.StatusCode == HttpStatusCode.Conflict)
                        {
                            consecutiveFailures++;
                            var conflictBackoff = Math.Max(3000, CalculateBackoffDelay(consecutiveFailures, 2000));
                            _logger.PluginLogWarning(
                                "Provider conflict ({StatusCode}) for channel {ChannelId}. Previous connection may still be active on provider side. Failure #{FailureCount}. Retrying in {DelayMs}ms (extended backoff)...",
                                response.StatusCode,
                                MediaSource.Id,
                                consecutiveFailures,
                                conflictBackoff
                            );
                            await Task.Delay(conflictBackoff, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        // Handle 407 Proxy Authentication Required specially
                        // This error often comes from Cloudflare-protected providers when their origin
                        // server has an upstream proxy that requires auth. It's not a client config issue -
                        // it's a provider infrastructure problem that may be transient or provider-specific.
                        // Solution: Retry with backoff, then attempt provider switch if it persists.
                        if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                        {
                            consecutiveFailures++;
                            const int Max407Retries = 2;

                            if (consecutiveFailures <= Max407Retries)
                            {
                                var proxyBackoff = Math.Max(1000, CalculateBackoffDelay(consecutiveFailures, 500));
                                _logger.PluginLogWarning(
                                    "Proxy Authentication Required (407) for channel {ChannelId}. This is a provider infrastructure issue (origin proxy requires auth). Attempt {Attempt}/{MaxRetries}. Retrying in {DelayMs}ms...",
                                    MediaSource.Id,
                                    consecutiveFailures,
                                    Max407Retries,
                                    proxyBackoff
                                );
                                await Task.Delay(proxyBackoff, cancellationToken).ConfigureAwait(false);
                                continue;
                            }

                            // After retries exhausted, try provider switch
                            _logger.PluginLogWarning(
                                "Persistent 407 error for channel {ChannelId}. Provider's origin proxy may be misconfigured. Attempting provider switch...",
                                MediaSource.Id
                            );

                            if (
                                await TryProviderSwitchAsync(SwitchReason.HealthDegraded, cancellationToken)
                                    .ConfigureAwait(false)
                            )
                            {
                                _resolvedUrl = await ResolveStreamUrlAsync(_currentSourceUrl, cancellationToken)
                                    .ConfigureAwait(false);
                                consecutiveFailures = 0;
                                _logger.PluginLogInformation(
                                    "Provider switch resolved 407 error for channel {ChannelId}, continuing with new provider",
                                    MediaSource.Id
                                );
                                continue;
                            }

                            // No alternative provider available - fail
                            _logger.PluginLogError(
                                "Unable to resolve 407 error for channel {ChannelId}. No alternative providers available. This is a provider infrastructure issue.",
                                MediaSource.Id
                            );
                            throw new HttpRequestException(
                                $"Provider proxy authentication failed: {response.StatusCode}"
                            );
                        }

                        var isPermanentError = response.StatusCode switch
                        {
                            HttpStatusCode.Unauthorized => true,
                            HttpStatusCode.Forbidden => true,
                            HttpStatusCode.NotFound => true,
                            HttpStatusCode.MethodNotAllowed => true, // 405 - provider doesn't support HTTP method
                            HttpStatusCode.NotAcceptable => true,
                            HttpStatusCode.Gone => true,
                            (HttpStatusCode)509 => true, // Bandwidth Limit Exceeded - provider connection limit
                            _ => false,
                        };

                        if (isPermanentError)
                        {
                            if (
                                response.StatusCode != HttpStatusCode.NotAcceptable
                                || consecutiveFailures >= Max406Retries
                            )
                            {
                                _logger.PluginLogError(
                                    "Permanent client error ({StatusCode}) for channel {ChannelId}. This indicates a configuration issue or invalid stream. Common causes: incorrect credentials, invalid stream ID, unsupported stream format, missing Accept headers, or connection limit exceeded. Hint: Check that the stream URL is valid and the provider supports MPEG-TS streaming.",
                                    response.StatusCode,
                                    MediaSource.Id
                                );
                                throw new HttpRequestException($"Client error (not retryable): {response.StatusCode}");
                            }

                            consecutiveFailures++;
                            var acceptBackoff = Math.Max(4000, CalculateBackoffDelay(consecutiveFailures, 2500));
                            _logger.PluginLogWarning(
                                "Not Acceptable ({StatusCode}) for channel {ChannelId}. May be connection limit or provider rejecting reconnect. Failure #{FailureCount}/{MaxRetries}. Retrying in {DelayMs}ms...",
                                response.StatusCode,
                                MediaSource.Id,
                                consecutiveFailures,
                                Max406Retries,
                                acceptBackoff
                            );
                            await Task.Delay(acceptBackoff, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            consecutiveFailures++;

                            // Give up after too many consecutive transient failures to allow failover
                            if (consecutiveFailures >= MaxConsecutiveTransientFailures)
                            {
                                _logger.PluginLogWarning(
                                    "Too many consecutive transient errors ({FailureCount}) for channel {ChannelId}. Last error: {StatusCode}. Attempting hot-swap...",
                                    consecutiveFailures,
                                    MediaSource.Id,
                                    response.StatusCode
                                );

                                // Try hot-swap before giving up completely
                                if (
                                    await TryProviderSwitchAsync(SwitchReason.HealthDegraded, cancellationToken)
                                        .ConfigureAwait(false)
                                )
                                {
                                    _resolvedUrl = await ResolveStreamUrlAsync(_currentSourceUrl, cancellationToken)
                                        .ConfigureAwait(false);
                                    consecutiveFailures = 0;
                                    _logger.PluginLogInformation(
                                        "Hot-swap resolved transient failures for channel {ChannelId}, continuing with new provider",
                                        MediaSource.Id
                                    );
                                    continue;
                                }

                                _logger.PluginLogError(
                                    "Hot-swap failed for channel {ChannelId}. Giving up after {FailureCount} transient errors.",
                                    MediaSource.Id,
                                    consecutiveFailures
                                );
                                throw new HttpRequestException(
                                    string.Create(
                                        CultureInfo.InvariantCulture,
                                        $"Too many transient errors ({consecutiveFailures}): {response.StatusCode}"
                                    )
                                );
                            }

                            var backoffDelay = CalculateBackoffDelay(consecutiveFailures);
                            _logger.PluginLogWarning(
                                "Transient error ({StatusCode}) for channel {ChannelId}. Failure #{FailureCount}/5. Retrying in {DelayMs}ms (exponential backoff)...",
                                response.StatusCode,
                                MediaSource.Id,
                                consecutiveFailures,
                                backoffDelay
                            );
                            await Task.Delay(backoffDelay, cancellationToken).ConfigureAwait(false);
                        }

                        continue;
                    }

                    sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    _buffer.SignalSourceConnected();
                    _logger.PluginLogInformation(
                        "HTTP connection #{Attempt} established for channel {ChannelId} - ContentType: {ContentType}",
                        connectionAttempt,
                        MediaSource.Id,
                        response.Content.Headers.ContentType
                    );

                    if (totalBytesAllConnections > 0)
                    {
                        // Activate timestamp remapping for same-provider reconnection
                        // This calculates the expected PTS based on wall-clock time elapsed during disconnection
                        // and remaps the new stream's timestamps to maintain continuity
                        if (
                            _needsReconnectionRemapping
                            && _providerSwitchService != null
                            && _lastDisconnectVideoPts > 0
                        )
                        {
                            var elapsedSinceDisconnect = DateTime.UtcNow - _lastDisconnectTime;
                            var elapsedPts90Khz = (long)(elapsedSinceDisconnect.TotalSeconds * 90000);

                            // Calculate expected PTS: last known PTS + wall-clock elapsed time
                            // This is where the stream SHOULD be if it had continued playing
                            var expectedVideoPts = _lastDisconnectVideoPts + elapsedPts90Khz;

                            // Store for output timeline reset after remapping activation
                            // This is the PTS value that the remapped output will start from
                            _expectedPtsAtReconnection = expectedVideoPts;
                            _reconnectionTime = DateTime.UtcNow;

                            // Update the timing state in the provider switch service with the expected values
                            // The remapping service will use this to calculate the offset when we receive
                            // the first PTS from the new connection
                            _providerSwitchService.UpdateTimingState(
                                MediaSource.Id,
                                expectedVideoPts,
                                _lastDisconnectAudioPts > 0 ? _lastDisconnectAudioPts + elapsedPts90Khz : 0,
                                _lastDisconnectPcr > 0
                                    ? _lastDisconnectPcr + (long)(elapsedSinceDisconnect.TotalSeconds * 27000000)
                                    : 0
                            );

                            _logger.PluginLogInformation(
                                "Reconnection timestamp continuity for channel {ChannelId}: elapsed={ElapsedMs}ms, expectedPTS={ExpectedPts} (last={LastPts} + elapsed={ElapsedPts})",
                                MediaSource.Id,
                                elapsedSinceDisconnect.TotalMilliseconds,
                                expectedVideoPts,
                                _lastDisconnectVideoPts,
                                elapsedPts90Khz
                            );

                            _needsReconnectionRemapping = false;
                            _pendingReconnectionActivation = true; // Activate remapping on first data chunk
                            // CRITICAL: Do NOT mark discontinuity here! The discontinuity offset must point
                            // to data that has ALREADY been remapped. If we mark it here, data written before
                            // remapping is activated will have un-remapped timestamps, causing DTS out-of-order
                            // errors in FFmpeg ("DTS X < Y out of order"). The discontinuity will be marked
                            // when remapping is successfully activated in the data processing loop below.
                        }
                        else
                        {
                            // Without remapping, mark discontinuity immediately at the new connection start
                            // The data from the new connection is valid as-is (no timestamp adjustment needed)
                            var paddingBytes = _buffer.MarkDiscontinuityAligned();
                            if (paddingBytes > 0)
                            {
                                _logger.LogDebugIfEnabled(
                                    "Added {PaddingBytes} bytes of null packet padding at reconnection for channel {ChannelId}",
                                    paddingBytes,
                                    MediaSource.Id
                                );
                            }
                            _logger.LogDebugIfEnabled(
                                "Marked discontinuity at offset {Offset} for channel {ChannelId} after reconnection (no remapping)",
                                _buffer.TotalBytesWritten,
                                MediaSource.Id
                            );

                            _logger.PluginLogWarning(
                                "Reconnection for channel {ChannelId} without timestamp continuity: needsRemapping={NeedsRemapping}, hasSwitchService={HasSwitchService}, lastVideoPts={LastVideoPts}",
                                MediaSource.Id,
                                _needsReconnectionRemapping,
                                _providerSwitchService != null,
                                _lastDisconnectVideoPts
                            );
                        }

                        _buffer.TsIndexer.ResetTimingState();

                        // CRITICAL: Reset the in-process remuxer on reconnection.
                        // The FFmpeg demuxer holds stale PAT/PMT and stream mapping from before
                        // the disconnection. Without resetting, it cannot process the new stream
                        // (which may have different PIDs), causing ProcessDataForRemapping to
                        // return empty output and all data to be silently dropped.
                        _providerSwitchService?.ResetRemuxer(MediaSource.Id);

                        // Reset violation counters to prevent false positives after reconnection
                        // Stale PAT/PCR timing from before disconnect shouldn't trigger provider switches
                        _violationSwitchTrigger?.ResetCounters(MediaSource.Id);
                    }

                    var connectionBytes = 0L;
                    var lastHealthCheckBytes = 0L; // Track bytes at last health check for bitrate calculation
                    var connectionStartTime = DateTime.UtcNow;
                    var lastLogTime = connectionStartTime;
                    var lastHealthCheckTime = connectionStartTime;

                    // Stall detection: Use cumulative tracking for bursty IPTV streams
                    // - perReadTimeoutMs: Short timeout per read to stay responsive (5s)
                    // - dataStallTimeoutMs: Total no-data time before triggering reconnect (20s)
                    // This allows for normal bursty behavior (5-15s gaps) while detecting true stalls
                    var dataStallTimeoutMs = StreamingTimeoutPolicy.GetDataStallTimeoutMs();
                    const int PerReadTimeoutMs = 5000; // 5s per-read timeout for responsiveness
                    var lastDataReceivedTime = DateTime.UtcNow;
                    var consecutiveReadTimeouts = 0;

                    while (!cancellationToken.IsCancellationRequested)
                    {
                        int bytesRead;
                        try
                        {
                            // Guard against disposed stream (can happen during reconnection race)
                            if (sourceStream == null)
                            {
                                _logger.LogDebugIfEnabled(
                                    "Source stream is null for channel {ChannelId}, breaking read loop",
                                    MediaSource.Id
                                );
                                break;
                            }

                            // Apply short per-read timeout to detect hung connections quickly
                            // but only trigger reconnect after cumulative stall time exceeds threshold
                            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            readCts.CancelAfter(PerReadTimeoutMs);

                            bytesRead = await sourceStream.ReadAsync(bufferMemory, readCts.Token).ConfigureAwait(false);

                            // Data received - reset stall tracking
                            if (bytesRead > 0)
                            {
                                lastDataReceivedTime = DateTime.UtcNow;
                                consecutiveReadTimeouts = 0;
                            }
                        }
                        catch (ObjectDisposedException)
                        {
                            // Stream was disposed (connection closed by server or timeout)
                            // This is expected during reconnection - break out and reconnect
                            _logger.LogDebugIfEnabled(
                                "Source stream disposed for channel {ChannelId} during read - connection closed",
                                MediaSource.Id
                            );
                            _buffer.SignalSourceDisconnected();
                            CaptureTimingStateForReconnection();
                            break;
                        }
                        catch (IOException ioEx) when (ioEx.InnerException is ObjectDisposedException)
                        {
                            // Wrapped ObjectDisposedException in IOException
                            _logger.LogDebugIfEnabled(
                                "Source stream disposed (wrapped in IOException) for channel {ChannelId}",
                                MediaSource.Id
                            );
                            _buffer.SignalSourceDisconnected();
                            CaptureTimingStateForReconnection();
                            break;
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            // Per-read timeout - check if we've exceeded cumulative stall threshold
                            consecutiveReadTimeouts++;
                            var totalStallMs = (DateTime.UtcNow - lastDataReceivedTime).TotalMilliseconds;

                            if (totalStallMs < dataStallTimeoutMs)
                            {
                                // Not yet at threshold - this is likely normal burst behavior
                                // Log at debug level only after multiple consecutive timeouts
                                if (consecutiveReadTimeouts >= 2)
                                {
                                    _logger.LogDebugIfEnabled(
                                        "Read timeout #{Count} for channel {ChannelId} ({StallMs:F0}ms / {ThresholdMs}ms). Waiting for data burst...",
                                        consecutiveReadTimeouts,
                                        MediaSource.Id,
                                        totalStallMs,
                                        dataStallTimeoutMs
                                    );
                                }

                                continue; // Keep waiting for data - don't break out of loop
                            }

                            // Data stall threshold exceeded - connection is alive but truly stalled
                            // Signal disconnection so readers know source is stalled
                            _buffer.SignalSourceDisconnected();

                            _logger.PluginLogWarning(
                                "Data stall detected for channel {ChannelId} after {TotalStallMs:F0}ms without data (threshold: {ThresholdMs}ms, {TimeoutCount} read timeouts). Attempting hot-swap...",
                                MediaSource.Id,
                                totalStallMs,
                                dataStallTimeoutMs,
                                consecutiveReadTimeouts
                            );

                            // Try hot-swap to a different provider before reconnecting
                            if (
                                await TryProviderSwitchAsync(SwitchReason.ConnectionFailed, cancellationToken)
                                    .ConfigureAwait(false)
                            )
                            {
                                // Hot-swap succeeded - resolve the new URL and continue
                                _resolvedUrl = await ResolveStreamUrlAsync(_currentSourceUrl, cancellationToken)
                                    .ConfigureAwait(false);
                                consecutiveFailures = 0;
                                _logger.PluginLogInformation(
                                    "Hot-swap resolved new URL for channel {ChannelId}, continuing with new provider",
                                    MediaSource.Id
                                );
                            }
                            else
                            {
                                consecutiveFailures++;
                            }

                            break; // Exit read loop, trigger reconnect
                        }

                        if (bytesRead == 0)
                        {
                            // Signal disconnection so readers know to wait for reconnection
                            _buffer.SignalSourceDisconnected();

                            // Capture timing state for reconnection timestamp continuity
                            CaptureTimingStateForReconnection();

                            if (connectionBytes == 0)
                            {
                                _logger.PluginLogError(
                                    "Connection #{Attempt} for channel {ChannelId} closed immediately without sending data",
                                    connectionAttempt,
                                    MediaSource.Id
                                );
                                break;
                            }

                            var connectionDuration = (DateTime.UtcNow - connectionStartTime).TotalSeconds;
                            if (connectionBytes < MinimumBytesForHealthyStream || connectionDuration < 10.0)
                            {
                                consecutiveFailures++;
                                _logger.PluginLogWarning(
                                    "Connection #{Attempt} for channel {ChannelId} reached PREMATURE EOF after only {MB} MB in {Duration:F1}s. Failure #{FailureCount}. Attempting hot-swap...",
                                    connectionAttempt,
                                    MediaSource.Id,
                                    connectionBytes / 1048576,
                                    connectionDuration,
                                    consecutiveFailures
                                );

                                // Try hot-swap on premature EOF (likely capacity/rate limit issue)
                                if (
                                    consecutiveFailures >= 2
                                    && await TryProviderSwitchAsync(SwitchReason.CapacityReached, cancellationToken)
                                        .ConfigureAwait(false)
                                )
                                {
                                    _resolvedUrl = await ResolveStreamUrlAsync(_currentSourceUrl, cancellationToken)
                                        .ConfigureAwait(false);
                                    consecutiveFailures = 0;
                                    _logger.PluginLogInformation(
                                        "Hot-swap resolved premature EOF for channel {ChannelId}, continuing with new provider",
                                        MediaSource.Id
                                    );
                                }
                            }
                            else
                            {
                                consecutiveFailures = 0;
                                _logger.PluginLogInformation(
                                    "Connection #{Attempt} for channel {ChannelId} reached EOF after {MB} MB in {Duration:F1}s. Reconnecting...",
                                    connectionAttempt,
                                    MediaSource.Id,
                                    connectionBytes / 1048576,
                                    connectionDuration
                                );
                            }

                            break;
                        }

                        // Activate reconnection timestamp remapping on first data chunk after reconnection
                        // This extracts the first video PTS and activates remapping based on expected vs actual PTS
                        // CRITICAL: Must be done BEFORE writing to buffer so the remapped data goes into the buffer
                        var remappingJustActivated = false;
                        if (_pendingReconnectionActivation && _providerSwitchService != null)
                        {
                            // FIX: Only clear the flag if activation succeeds.
                            // The first chunk might not contain a video PES with PTS, so we need to
                            // keep trying on subsequent chunks until we find one.
                            if (
                                _providerSwitchService.ActivateReconnectionRemapping(
                                    MediaSource.Id,
                                    bufferMemory[..bytesRead].Span
                                )
                            )
                            {
                                // Activation succeeded - clear the flag
                                _pendingReconnectionActivation = false;
                                remappingJustActivated = true;

                                // Reset output timeline to match the remapped output.
                                // The remapper calculates: offset = expectedPTS - actualFirstPTS
                                // Then all incoming PTS values are remapped: remapped = actual + offset
                                // So the first remapped PTS will be: actualFirstPTS + offset = expectedPTS
                                _outputTimelineBasePts = _expectedPtsAtReconnection;
                                _outputTimelineStartTime = _reconnectionTime;

                                // CRITICAL: Set the discontinuity remapping cooldown timer to prevent
                                // source stream discontinuities from triggering additional remapping.
                                // Some IPTV streams have inherent PTS discontinuities (e.g., ~9 second jumps)
                                // that would cause double-remapping if not suppressed.
                                _lastDiscontinuityRemappingTime = DateTime.UtcNow;

                                _logger.PluginLogInformation(
                                    "Reconnection timestamp remapping activated for channel {ChannelId}. "
                                        + "Reset output timeline: BasePTS={BasePts} (expected at reconnection) at {BaseTime}",
                                    MediaSource.Id,
                                    _outputTimelineBasePts,
                                    _reconnectionTime
                                );
                            }
                            else
                            {
                                // CRITICAL FIX: Do NOT write data to buffer until remapping is activated!
                                // Data chunks that arrive before we find a video PTS have un-remapped timestamps.
                                // Writing them to the buffer would cause DTS out-of-order errors in FFmpeg
                                // because readers could jump to these un-remapped packets after discontinuity.
                                // Instead, we discard these early chunks and wait for a chunk with video PTS.
                                _logger.LogDebugIfEnabled(
                                    "Discarding {BytesRead} bytes of pre-remapping data for channel {ChannelId} (waiting for video PTS)",
                                    bytesRead,
                                    MediaSource.Id
                                );
                                continue; // Skip to next iteration without writing to buffer
                            }
                        }

                        // Process data through in-process FFmpeg remuxer for proper A/V synchronization.
                        // Handles: A/V sync via genpts/igndts, PCR regeneration, timestamp continuity.
                        // The output bytes may differ from input due to FFmpeg's internal buffering.
                        ReadOnlyMemory<byte> dataToWrite = bufferMemory[..bytesRead];

                        if (_providerSwitchService != null)
                        {
                            var pipelineActive = _providerSwitchService.ProcessDataForRemapping(
                                MediaSource.Id,
                                bufferMemory[..bytesRead].Span,
                                out var remuxedOutput
                            );

                            if (pipelineActive)
                            {
                                if (remuxedOutput is { Length: > 0 })
                                {
                                    // Pipeline is running and producing output - use remuxed data
                                    // This ensures proper A/V sync and timestamp continuity
                                    dataToWrite = remuxedOutput;
                                }
                                else if (_providerSwitchService.IsRemappingActive(MediaSource.Id))
                                {
                                    // Remuxer IS running but output queue is empty (internal buffering)
                                    // Check if remuxer is healthy (producing output at reasonable ratio)
                                    if (_providerSwitchService.IsRemuxerHealthy(MediaSource.Id))
                                    {
                                        // Healthy remuxer - just wait for next chunk
                                        // CRITICAL: Don't write raw data here - it would cause DTS out-of-order
                                        // errors because raw timestamps would mix with remapped timestamps
                                        continue;
                                    }

                                    // Remuxer is STUCK (consumed lots of data but not producing output)
                                    // Disable it permanently for this session to prevent repeated init/stuck cycles
                                    _logger.PluginLogWarning(
                                        "Remuxer stuck for channel {ChannelId} - disabling and falling back to raw data",
                                        MediaSource.Id
                                    );
                                    _providerSwitchService.DisableRemuxer(MediaSource.Id);
                                    // Fall through to write raw data
                                }
                                else
                                {
                                    // Remuxer is INITIALIZING (needs 512KB before producing output)
                                    // Fall through to write raw data to keep buffer filled
                                    // This prevents reader timeouts while remuxer warms up
                                    // Raw data is safe here because no timestamp remapping has started yet
                                    _logger.LogDebugIfEnabled(
                                        "Remuxer initializing for channel {ChannelId}, writing raw data ({Bytes} bytes)",
                                        MediaSource.Id,
                                        bytesRead
                                    );
                                }
                            }
                            // If pipeline is not active (stream not registered with remuxer),
                            // fall through to write raw data. This only happens when remuxing
                            // is truly not configured for this stream.
                        }

                        // Mark discontinuity AFTER remapping is activated and this chunk is remapped,
                        // but BEFORE writing to buffer. This ensures the discontinuity offset points to the first
                        // REMAPPED data, not to un-remapped data that would cause DTS out-of-order errors.
                        if (remappingJustActivated)
                        {
                            var paddingBytes = _buffer.MarkDiscontinuityAligned();
                            if (paddingBytes > 0)
                            {
                                _logger.LogDebugIfEnabled(
                                    "Added {PaddingBytes} bytes of null packet padding before remapped data for channel {ChannelId}",
                                    paddingBytes,
                                    MediaSource.Id
                                );
                            }
                            _logger.PluginLogInformation(
                                "Marked discontinuity at offset {Offset} for channel {ChannelId} after remapping activated (first remapped chunk)",
                                _buffer.TotalBytesWritten,
                                MediaSource.Id
                            );
                        }

                        await _buffer.WriteAsync(dataToWrite, cancellationToken).ConfigureAwait(false);
                        connectionBytes += dataToWrite.Length;
                        totalBytesAllConnections += dataToWrite.Length;

                        // Cache parameter sets (SPS/PPS/VPS) for injection during provider switches
                        _providerSwitchService?.UpdateParameterSetCache(MediaSource.Id, bufferMemory[..bytesRead].Span);

                        // Initialize output timeline on first valid PTS - must happen early for reconnection remapping
                        // The health check runs every 30s which is too late for fast-disconnecting providers
                        if (!_outputTimelineInitialized)
                        {
                            var (videoPts, _, _) = _buffer.TsIndexer.GetCurrentTimingState();
                            if (videoPts > 0)
                            {
                                _outputTimelineBasePts = videoPts;
                                _outputTimelineStartTime = DateTime.UtcNow;
                                _outputTimelineInitialized = true;
                                _logger.LogDebugIfEnabled(
                                    "Output timeline initialized for channel {ChannelId}: BasePTS={BasePts}",
                                    MediaSource.Id,
                                    videoPts
                                );
                            }
                        }

                        var now = DateTime.UtcNow;
                        if ((now - lastLogTime).TotalSeconds >= ProgressLogIntervalSeconds)
                        {
                            var sessionElapsed = (now - sessionStartTime).TotalSeconds;
                            var mbps = (double)totalBytesAllConnections * 8.0 / 1000000.0 / sessionElapsed;
                            var bufferFillPct =
                                (double)(_buffer.TotalBytesWritten % _buffer.BufferSize)
                                * 100.0
                                / (double)_buffer.BufferSize;
                            _logger.PluginLogInformation(
                                "Broadcast progress for channel {ChannelId}: Connection #{Attempt}, {TotalMB} MB total, {Mbps:F2} Mbps avg, {Consumers} consumers, buffer {FillPct:F1}% filled",
                                MediaSource.Id,
                                connectionAttempt,
                                totalBytesAllConnections / 1048576,
                                mbps,
                                ConsumerCount,
                                bufferFillPct
                            );
                            lastLogTime = now;
                        }

                        if ((now - lastHealthCheckTime).TotalSeconds >= HealthCheckIntervalSeconds)
                        {
                            var bufferFillPct2 =
                                (double)(_buffer.TotalBytesWritten % _buffer.BufferSize)
                                * 100.0
                                / (double)_buffer.BufferSize;
                            var currentBitrate =
                                totalBytesAllConnections > 0
                                    ? (double)totalBytesAllConnections
                                        * 8.0
                                        / 1000000.0
                                        / (now - sessionStartTime).TotalSeconds
                                    : 0.0;
                            var isUnderrun = bufferFillPct2 < BufferUnderrunThresholdPercent;

                            if (isUnderrun)
                            {
                                _bufferUnderrunCount++;
                                if (_bufferUnderrunCount % 3 == 1)
                                {
                                    _logger.PluginLogWarning(
                                        "Buffer underrun #{Count} for channel {ChannelId}. Only {FillPct:F1}% filled ({FilledMB:F1}MB / {TotalMB}MB). Current bitrate: {Mbps:F2} Mbps. Consider increasing buffer size or checking network stability.",
                                        _bufferUnderrunCount,
                                        MediaSource.Id,
                                        bufferFillPct2,
                                        (double)(_buffer.TotalBytesWritten % _buffer.BufferSize) / 1048576.0,
                                        _buffer.BufferSize / 1048576,
                                        currentBitrate
                                    );

                                    if (_bufferUnderrunCount >= BufferUnderrunNotificationThreshold)
                                    {
                                        var underrunCount = _bufferUnderrunCount;
                                        var fillPct = bufferFillPct2;
                                        var bitrate = currentBitrate;
                                        _discordService.SendFireAndForget(svc =>
                                            svc.NotifyBufferHealthIssueAsync(
                                                MediaSource.Id,
                                                MediaSource.Name ?? "Unknown",
                                                underrunCount,
                                                fillPct,
                                                bitrate
                                            )
                                        );
                                    }
                                }
                            }

                            var isNearFull = bufferFillPct2 > BufferNearFullThresholdPercent;
                            if (isNearFull)
                            {
                                _bufferHealthWarnings++;
                                _logger.LogDebugIfEnabled(
                                    "Buffer for channel {ChannelId} is {FillPct:F1}% full. Readers may be slow or disconnected. Active consumers: {Consumers}",
                                    MediaSource.Id,
                                    bufferFillPct2,
                                    ConsumerCount
                                );
                            }

                            if (!isUnderrun && !isNearFull)
                            {
                                _logger.LogDebugIfEnabled(
                                    "Buffer health for channel {ChannelId}: {FillPct:F1}% filled, {Mbps:F2} Mbps, {Consumers} consumer(s), {TotalMB}MB written",
                                    MediaSource.Id,
                                    bufferFillPct2,
                                    currentBitrate,
                                    ConsumerCount,
                                    _buffer.TotalBytesWritten / 1048576
                                );
                            }

                            // Tier 2: Bitrate monitoring for metrics and trend tracking
                            RecordBitrateMetrics(connectionBytes, lastHealthCheckBytes, lastHealthCheckTime, now);
                            lastHealthCheckBytes = connectionBytes;

                            // Sync timing state to provider switch service for seamless failover
                            if (_providerSwitchService != null)
                            {
                                var (videoPts, audioPts, pcr) = _buffer.TsIndexer.GetCurrentTimingState();
                                if (videoPts > 0 || audioPts > 0 || pcr > 0)
                                {
                                    _providerSwitchService.UpdateTimingState(MediaSource.Id, videoPts, audioPts, pcr);

                                    // Initialize output timeline on first valid PTS
                                    // This establishes our baseline for continuous output regardless of provider timestamps
                                    if (!_outputTimelineInitialized && videoPts > 0)
                                    {
                                        _outputTimelineBasePts = videoPts;
                                        _outputTimelineStartTime = DateTime.UtcNow;
                                        _outputTimelineInitialized = true;
                                        _logger.LogDebugIfEnabled(
                                            "Output timeline initialized for channel {ChannelId}: BasePTS={BasePts}",
                                            MediaSource.Id,
                                            videoPts
                                        );
                                    }
                                }
                            }

                            lastHealthCheckTime = now;
                            _lastHealthCheck = now;
                        }
                    }

                    if (sourceStream != null)
                    {
                        try
                        {
                            await sourceStream.DisposeAsync().ConfigureAwait(false);
                            _logger.LogDebugIfEnabled("Stream disposed for channel {ChannelId}", MediaSource.Id);
                        }
                        catch (ObjectDisposedException) { }
                        catch (IOException ioEx)
                        {
                            _logger.LogDebugIfEnabled(
                                ioEx,
                                "IO error disposing stream for channel {ChannelId}",
                                MediaSource.Id
                            );
                        }

                        sourceStream = null;
                    }
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    var cleanupDelayMs = consecutiveFailures == 0 && totalBytesAllConnections > 1048576 ? 250 : 500;
                    await Task.Delay(cleanupDelayMs, CancellationToken.None).ConfigureAwait(false);
                    _logger.LogDebugIfEnabled(
                        "Connection cleanup delay ({DelayMs}ms) complete for channel {ChannelId}",
                        cleanupDelayMs,
                        MediaSource.Id
                    );
                }

                if (totalBytesAllConnections > MinimumBytesForHealthyStream)
                {
                    connectionAttempt = 0;
                    consecutiveFailures = 0;
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    // Signal that we're attempting to reconnect - readers will wait longer
                    _buffer.SignalReconnecting();

                    // Tier 2: Check if current provider is predicted to fail and try hot-swap preemptively
                    if (await TryTrendBasedHotSwapAsync(cancellationToken).ConfigureAwait(false))
                    {
                        // Hot-swap succeeded based on trend prediction - reset failures and continue
                        consecutiveFailures = 0;
                    }

                    var isHealthyStream =
                        consecutiveFailures == 0 && totalBytesAllConnections > MinimumBytesForHealthyStream;
                    var minimumReconnectDelay = isHealthyStream
                        ? 500
                        : (totalBytesAllConnections > MinimumBytesForHealthyStream ? 1500 : 0);
                    var backoffDelay2 = Math.Max(minimumReconnectDelay, CalculateBackoffDelay(consecutiveFailures));
                    _logger.LogDebugIfEnabled(
                        "Waiting {DelayMs}ms before reconnecting channel {ChannelId} (healthy: {IsHealthy}, failures: {FailureCount}, bytes: {BytesMB}MB)...",
                        backoffDelay2,
                        MediaSource.Id,
                        isHealthyStream,
                        consecutiveFailures,
                        (double)totalBytesAllConnections / 1048576.0
                    );
                    await Task.Delay(backoffDelay2, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.PluginLogInformation("Broadcast for channel {ChannelId} was cancelled", MediaSource.Id);
                break;
            }
            catch (Exception exception)
            {
                consecutiveFailures++;
                _logger.PluginLogError(
                    exception,
                    "Connection #{Attempt} for channel {ChannelId} failed. Failure #{FailureCount}",
                    connectionAttempt,
                    MediaSource.Id,
                    consecutiveFailures
                );

                if (sourceStream != null)
                {
                    await sourceStream.DisposeAsync().ConfigureAwait(false);
                }

                response?.Dispose();

                if (!cancellationToken.IsCancellationRequested && connectionAttempt < MaxConnectionAttempts)
                {
                    var backoffDelay3 = CalculateBackoffDelay(consecutiveFailures);
                    _logger.LogDebugIfEnabled(
                        "Waiting {DelayMs}ms before retry #{NextAttempt} (exponential backoff)",
                        backoffDelay3,
                        connectionAttempt + 1
                    );
                    await Task.Delay(backoffDelay3, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        var totalSessionDuration = (DateTime.UtcNow - sessionStartTime).TotalSeconds;
        _logger.PluginLogInformation(
            "Broadcast session ended for channel {ChannelId}: {TotalMB} MB in {Duration:F1}s across {Attempts} connection(s)",
            MediaSource.Id,
            totalBytesAllConnections / 1048576,
            totalSessionDuration,
            connectionAttempt
        );

        if (connectionAttempt >= MaxConnectionAttempts)
        {
            _logger.PluginLogError(
                "Maximum reconnection attempts ({Max}) reached for channel {ChannelId}. Giving up.",
                MaxConnectionAttempts,
                MediaSource.Id
            );

            _discordService.SendFireAndForget(svc =>
                svc.NotifyStreamErrorAsync(
                    MediaSource.Id,
                    MediaSource.Name ?? "Unknown",
                    $"Connection failed after {MaxConnectionAttempts} attempts"
                )
            );

            _killReason = $"Max reconnection attempts ({MaxConnectionAttempts}) reached";
            Dispose();
        }
        else if (ConsumerCount == 0 && cancellationToken.IsCancellationRequested)
        {
            _logger.PluginLogInformation(
                "Auto-cleaning up stream {ChannelId} - broadcast cancelled with no active consumers",
                MediaSource.Id
            );
            _killReason = "Broadcast cancelled with no consumers";
            Dispose();
        }
    }

    /// <inheritdoc />
    public async Task Close()
    {
        _logger.LogDebugIfEnabled("Closing broadcast for channel {ChannelId}", MediaSource.Id);

        if (_isDisposed)
        {
            _logger.LogDebugIfEnabled("Close called on already disposed stream {ChannelId}, ignoring", MediaSource.Id);
            return;
        }

        if (_tokenSource?.IsCancellationRequested == false)
        {
            try
            {
                await _tokenSource.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                _logger.LogDebugIfEnabled("TokenSource already disposed for channel {ChannelId}", MediaSource.Id);
            }
        }

        if (_broadcastTask != null)
        {
            try
            {
                await _broadcastTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }

            _broadcastTask = null;
        }

        _logger.LogDebugIfEnabled(
            "Broadcast closed for channel {ChannelId}, {Count} consumers were active",
            MediaSource.Id,
            ConsumerCount
        );
    }

    /// <inheritdoc />
    public Stream GetStream()
    {
        if (_isDisposed)
        {
            _logger.PluginLogWarning(
                "GetStream called on disposed stream {ChannelId}. Stream may have failed or been terminated.",
                MediaSource.Id
            );
            throw new InvalidOperationException("Stream " + MediaSource.Id + " has been disposed");
        }

        if (_broadcastTask == null || _broadcastTask.IsCompleted)
        {
            _logger.PluginLogWarning(
                "Broadcast not running for channel {ChannelId} (task={TaskState}), initializing now...",
                MediaSource.Id,
                _broadcastTask?.Status.ToString() ?? "null"
            );
            Open(CancellationToken.None).GetAwaiter().GetResult();
        }

        var stream = _readerPool.Acquire();
        if (stream == null)
        {
            _logger.PluginLogWarning(
                "Failed to acquire stream for channel {ChannelId} - reader pool was disposed (stream failed or terminated)",
                MediaSource.Id
            );
            throw new InvalidOperationException("Stream " + MediaSource.Id + " is no longer available");
        }

        return stream;
    }

    /// <summary>
    /// Disposes the fields.
    /// </summary>
    /// <param name="disposing">Whether or not to dispose.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        // Only remove from registry during proper disposal (not finalizer)
        // This prevents zombie streams that are still broadcasting but not trackable
        if (disposing && _activeStreams.TryRemove(MediaSource.Id, out _))
        {
            _logger.PluginLogInformation(
                "Unregistered Restream {StreamId} (remaining active: {Count})",
                MediaSource.Id,
                _activeStreams.Count
            );
        }

        if (!disposing)
        {
            return;
        }

        // Unregister from provider switch service for proper lifecycle cleanup
        _providerSwitchService?.UnregisterStream(MediaSource.Id);

        var bytesTransferred = _buffer.TotalBytesWritten;
        var streamActuallyStarted = bytesTransferred > 0 || _broadcastTask != null;

        if (streamActuallyStarted)
        {
            var duration = DateTime.UtcNow - _startTime;
            var reason = _killReason ?? "Stream ended";
            _discordService.SendFireAndForget(svc =>
                svc.NotifyStreamKilledAsync(
                    MediaSource.Id,
                    MediaSource.Name ?? "Unknown",
                    reason,
                    duration,
                    bytesTransferred
                )
            );
        }
        else if (_discordService != null && !streamActuallyStarted)
        {
            _logger.LogDebugIfEnabled(
                "Skipping 'Stream Killed' notification for channel {ChannelId} - stream never started (0 bytes transferred)",
                MediaSource.Id
            );
        }

        if (_cleanupCts != null)
        {
            _cleanupCts.Cancel();
            _cleanupCts.Dispose();
            _cleanupCts = null;
        }

        _buffer.TsIndexer.StreamQualityViolation -= OnStreamQualityViolation;
        _buffer.TsIndexer.SyncDriftDetected -= OnSyncDriftDetected;
        _buffer.TsIndexer.PtsDiscontinuityDetected -= OnPtsDiscontinuityDetected;

        // Unsubscribe from TsDuck metrics events
        if (_tsDuckAnalyzer != null)
        {
            _tsDuckAnalyzer.MetricsUpdated -= OnTsDuckMetricsUpdated;
        }

        if (_tokenSource != null)
        {
            if (!_tokenSource.IsCancellationRequested)
            {
                _tokenSource.Cancel();
            }

            _tokenSource.Dispose();
        }

        _buffer.Dispose();
        _tsDuckAnalyzer?.Dispose();
        _openLock.Dispose();
        _readerPool.Dispose();
        _networkBuffer = null;

        // Throttled priority update - recalculate provider priorities based on streaming performance
        TryUpdateProviderPriorities();

        _logger.PluginLogInformation("Restream for channel {ChannelId} disposed", MediaSource.Id);
    }

    /// <summary>
    /// Attempts to update provider priorities with throttling.
    /// Only updates if enough time has passed since the last update.
    /// </summary>
    private void TryUpdateProviderPriorities()
    {
        if (_failoverService == null)
        {
            return;
        }

        lock (_priorityUpdateLock)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastPriorityUpdateTime).TotalSeconds < PriorityUpdateIntervalSeconds)
            {
                return;
            }

            _lastPriorityUpdateTime = now;
        }

        // Fire and forget - don't block disposal
        try
        {
            var updated = _failoverService.UpdateProviderPriorities();
            if (updated)
            {
                _logger.LogDebugIfEnabled("Provider priorities updated after stream {ChannelId} ended", MediaSource.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebugIfEnabled(
                ex,
                "Failed to update provider priorities after stream {ChannelId} ended",
                MediaSource.Id
            );
        }
    }

    /// <summary>
    /// Handles TsDuck TR 101 290 metrics updates.
    /// Forwards metrics to ProviderSwitchService for combined quality monitoring.
    /// </summary>
    private void OnTsDuckMetricsUpdated(object? sender, TsDuckMetricsEventArgs e)
    {
        _providerSwitchService?.RecordTsDuckMetrics(MediaSource.Id, e.Metrics);
    }

    /// <summary>
    /// Handles TR 101 290 stream quality violation events from the TsIndexer.
    /// Sends Discord notifications and delegates switch evaluation to IViolationSwitchTrigger (DIP).
    /// </summary>
    private void OnStreamQualityViolation(object? sender, StreamQualityViolationEventArgs e)
    {
        // Send Discord notification (SRP: notification is separate from switch logic)
        _discordService.SendFireAndForget(svc =>
            svc.NotifyStreamQualityViolationAsync(
                MediaSource.Id,
                MediaSource.Name ?? "Unknown Channel",
                e.ViolationType,
                e.Details
            )
        );

        // Delegate switch evaluation to injected service (DIP/SRP)
        if (_violationSwitchTrigger == null || _providerSwitchService == null)
        {
            return;
        }

        var result = _violationSwitchTrigger.RecordViolation(MediaSource.Id, e.ViolationType);
        if (result.ShouldSwitch)
        {
            _logger.PluginLogWarning(
                "TR 101 290 violation threshold exceeded for channel {ChannelId}: {Reason}. Attempting provider switch.",
                MediaSource.Id,
                result.Reason
            );

            // Capture the cancellation token before fire-and-forget to avoid race condition
            // where _tokenSource could be disposed while the async method is running
            var token = _tokenSource?.Token ?? CancellationToken.None;
            if (token.IsCancellationRequested)
            {
                return;
            }

            _ = TryViolationBasedSwitchAsync(result.Reason ?? "Unknown violation", token);
        }
    }

    /// <summary>
    /// Attempts a provider switch triggered by TR 101 290 violations.
    /// </summary>
    /// <param name="violationReason">The reason for the violation.</param>
    /// <param name="cancellationToken">Cancellation token captured before fire-and-forget call.</param>
    private async Task TryViolationBasedSwitchAsync(string violationReason, CancellationToken cancellationToken)
    {
        try
        {
            if (await TryProviderSwitchAsync(SwitchReason.HealthDegraded, cancellationToken).ConfigureAwait(false))
            {
                // Use the passed cancellationToken consistently to avoid race with _tokenSource disposal
                _resolvedUrl = await ResolveStreamUrlAsync(_currentSourceUrl, cancellationToken).ConfigureAwait(false);
                _logger.PluginLogInformation(
                    "TR 101 290 violation-triggered switch successful for channel {ChannelId}: {Reason}",
                    MediaSource.Id,
                    violationReason
                );
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown - don't log as error
        }
        catch (Exception ex)
        {
            _logger.LogDebugIfEnabled(
                ex,
                "TR 101 290 violation-triggered switch failed for channel {ChannelId}",
                MediaSource.Id
            );
        }
    }

    /// <summary>
    /// Handles A/V synchronization drift events from the TsIndexer.
    /// Sends Discord notifications and delegates switch evaluation to IViolationSwitchTrigger (DIP).
    /// </summary>
    private void OnSyncDriftDetected(object? sender, SyncDriftEventArgs e)
    {
        var indexer = _buffer.TsIndexer;
        var peakDrift = indexer.GetPeakDriftMs();
        var violationCount = indexer.GetDriftViolationCount();

        // Fallback if no program detected yet
        if (peakDrift == 0)
        {
            peakDrift = Math.Abs(e.DriftMs);
        }

        if (violationCount == 0)
        {
            violationCount = 1;
        }

        // Send Discord notification (SRP: notification is separate from switch logic)
        _discordService.SendFireAndForget(svc =>
            svc.NotifyAVDriftAsync(
                MediaSource.Id,
                MediaSource.Name ?? "Unknown Channel",
                e.DriftMs,
                e.Status.ToString(),
                peakDrift,
                violationCount
            )
        );

        // Delegate switch evaluation to injected service (DIP/SRP)
        if (_violationSwitchTrigger == null || _providerSwitchService == null)
        {
            return;
        }

        var result = _violationSwitchTrigger.RecordDrift(MediaSource.Id, e.DriftMs);
        if (result.ShouldSwitch)
        {
            _logger.PluginLogWarning(
                "Severe A/V drift detected for channel {ChannelId}: {Reason}. Attempting provider switch.",
                MediaSource.Id,
                result.Reason
            );

            // Capture the cancellation token before fire-and-forget to avoid race condition
            var token = _tokenSource?.Token ?? CancellationToken.None;
            if (!token.IsCancellationRequested)
            {
                _ = TryViolationBasedSwitchAsync(result.Reason ?? "A/V drift", token);
            }
        }
    }

    /// <summary>
    /// Captures the current timing state for reconnection timestamp continuity.
    /// Called when the HTTP connection is closed (either gracefully or via exception).
    /// </summary>
    /// <remarks>
    /// Uses our output timeline (wall-clock based) rather than provider timestamps.
    /// Provider PCR/PTS values are not continuous across reconnections because each
    /// HTTP connection gets a different point in the provider's broadcast timeline.
    /// Our output timeline is continuous and monotonically increasing.
    /// </remarks>
    private void CaptureTimingStateForReconnection()
    {
        // Primary approach: Use output timeline based on wall-clock time
        // This is independent of provider timestamps and remains continuous across reconnections
        if (_outputTimelineInitialized)
        {
            var elapsedSinceStart = DateTime.UtcNow - _outputTimelineStartTime;
            var elapsedPts90Khz = (long)(elapsedSinceStart.TotalSeconds * 90000);
            var outputPts = _outputTimelineBasePts + elapsedPts90Khz;

            _lastDisconnectTime = DateTime.UtcNow;
            _lastDisconnectVideoPts = outputPts;
            _lastDisconnectAudioPts = outputPts; // Use same for audio since they should be in sync
            _lastDisconnectPcr = outputPts * 300; // Convert 90kHz to 27MHz
            _needsReconnectionRemapping = true;

            _logger.PluginLogInformation(
                "Captured timing state at disconnect for channel {ChannelId} (output timeline): "
                    + "OutputPTS={OutputPts} (base={BasePts} + elapsed={ElapsedPts}), ElapsedMs={ElapsedMs:F1}",
                MediaSource.Id,
                outputPts,
                _outputTimelineBasePts,
                elapsedPts90Khz,
                elapsedSinceStart.TotalMilliseconds
            );
            return;
        }

        // Fallback: Try to get timing from TsIndexer (for initial connection before output timeline is established)
        var (videoPts, audioPts, pcr) = _buffer.TsIndexer.GetCurrentTimingState();

        if (videoPts > 0)
        {
            _lastDisconnectTime = DateTime.UtcNow;
            _lastDisconnectVideoPts = videoPts;
            _lastDisconnectAudioPts = audioPts;
            _lastDisconnectPcr = pcr;
            _needsReconnectionRemapping = true;

            _logger.PluginLogInformation(
                "Captured timing state at disconnect for channel {ChannelId}: VideoPTS={VideoPts}, AudioPTS={AudioPts}, PCR={Pcr}",
                MediaSource.Id,
                videoPts,
                audioPts,
                pcr
            );
            return;
        }

        // Fallback 2: Derive from PCR if no video PTS available
        if (pcr > 0)
        {
            var pcrDerivedPts = pcr / 300; // Convert 27MHz to 90kHz

            _lastDisconnectTime = DateTime.UtcNow;
            _lastDisconnectVideoPts = pcrDerivedPts;
            _lastDisconnectAudioPts = pcrDerivedPts;
            _lastDisconnectPcr = pcr;
            _needsReconnectionRemapping = true;

            _logger.PluginLogInformation(
                "Captured timing state at disconnect for channel {ChannelId} (PCR-derived): VideoPTS={VideoPts}, PCR={Pcr}",
                MediaSource.Id,
                pcrDerivedPts,
                pcr
            );
            return;
        }

        // No timing state available
        _logger.PluginLogWarning(
            "No timing state available at disconnect for channel {ChannelId} - reconnection remapping will not be available",
            MediaSource.Id
        );
    }

    /// <summary>
    /// Handles PTS discontinuity events from the TsIndexer.
    /// When a large PTS jump is detected in the source stream, this activates timestamp remapping
    /// to smooth out the discontinuity and prevent frame jumping/looping for viewers.
    /// </summary>
    private void OnPtsDiscontinuityDetected(object? sender, PtsDiscontinuityEventArgs e)
    {
        // Only handle if we have the remapping service available
        if (_providerSwitchService == null)
        {
            return;
        }

        // Apply cooldown to prevent rapid-fire activations from streams with systematic small jumps
        var now = DateTime.UtcNow;
        var timeSinceLastActivation = (now - _lastDiscontinuityRemappingTime).TotalMilliseconds;
        if (timeSinceLastActivation < DiscontinuityRemappingCooldownMs)
        {
            _logger.LogDebugIfEnabled(
                "PTS discontinuity for channel {ChannelId} ignored - cooldown active ({TimeSince:F0}ms < {Cooldown}ms)",
                MediaSource.Id,
                timeSinceLastActivation,
                DiscontinuityRemappingCooldownMs
            );
            return;
        }

        _lastDiscontinuityRemappingTime = now;

        _logger.PluginLogWarning(
            "Source stream PTS discontinuity for channel {ChannelId}: {Direction} jump of {DeltaMs:F1}ms. Activating timestamp smoothing.",
            MediaSource.Id,
            e.IsBackwardJump ? "BACKWARD" : "FORWARD",
            e.AbsoluteDeltaMs
        );

        // Activate remapping IMMEDIATELY with known PTS values.
        // This is critical - the discontinuity has already occurred in the data that was just parsed,
        // so we need to start remapping right away. The next packets will have their timestamps adjusted.
        // Note: The packet that triggered this event has already been written to the buffer unremapped,
        // but all subsequent packets will be remapped correctly.
        _providerSwitchService.ActivateDiscontinuityRemapping(MediaSource.Id, e.PreviousPts, e.NewPts);

        _logger.LogDebugIfEnabled(
            "Activated discontinuity remapping for channel {ChannelId}: baseline PTS={BaselinePts}, remapping from {NewPts}",
            MediaSource.Id,
            e.PreviousPts,
            e.NewPts
        );
    }

    /// <summary>
    /// Attempts to switch to a new provider when the current one is failing.
    /// Delegates to IProviderSwitchService following DIP (Dependency Inversion Principle).
    /// </summary>
    /// <param name="reason">The reason for the switch attempt.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the switch succeeded and _currentSourceUrl was updated.</returns>
    private async Task<bool> TryProviderSwitchAsync(SwitchReason reason, CancellationToken cancellationToken)
    {
        // Fast-path: check if provider switch service is available (DIP - depend on abstraction)
        if (_providerSwitchService == null)
        {
            return false;
        }

        // CRITICAL: Sync timing state BEFORE the switch begins
        // The switch service needs the current PTS/PCR values to calculate remapping offsets.
        // Without this, timestamp remapping cannot produce continuous playback.
        var (videoPts, audioPts, pcr) = _buffer.TsIndexer.GetCurrentTimingState();
        if (videoPts > 0 || audioPts > 0 || pcr > 0)
        {
            _providerSwitchService.UpdateTimingState(MediaSource.Id, videoPts, audioPts, pcr);
        }

        // Delegate to the provider switch service (SRP - Restream doesn't manage switch logic)
        var result = await _providerSwitchService
            .TrySwitchAsync(MediaSource.Id, _currentSourceUrl, reason, cancellationToken)
            .ConfigureAwait(false);

        if (result.Success && result.NewUrl is { Length: > 0 })
        {
            // Update URL atomically - but DON'T mark discontinuity yet!
            // We need to verify the URL works before marking discontinuity, otherwise
            // if the URL fails, we'll have a discontinuity pointing to nowhere useful.
            _currentSourceUrl = result.NewUrl;

            // CRITICAL FIX: Only mark discontinuity if we have aligned data (which means the
            // connection was successfully established and data was received). If AlignedData
            // is null/empty, it means the aligned switch failed and we're falling back to a
            // simple URL switch - in which case the main HTTP loop will handle discontinuity
            // when the new connection is actually established and remapping is activated.
            if (result.AlignedData is { Length: > 0 })
            {
                // P1 FIX: Mark discontinuity for timestamp handling (critical for seamless playback)
                // MarkDiscontinuityAligned pads to 188-byte boundary to prevent partial packets at switch
                // This MUST happen before writing aligned data to ensure consumers see the discontinuity first
                var paddingBytes = _buffer.MarkDiscontinuityAligned();
                _buffer.TsIndexer.ResetTimingState();

                if (paddingBytes > 0)
                {
                    _logger.LogDebugIfEnabled(
                        "Added {PaddingBytes} bytes of null packet padding at provider switch for {ChannelId}",
                        paddingBytes,
                        MediaSource.Id
                    );
                }

                // P0 FIX: Write aligned data from the switch to the buffer
                // This is CRITICAL - the aligned data contains:
                // 1. Data starting at a keyframe boundary (if found)
                // 2. Timestamps that have been remapped for continuity
                // 3. Discontinuity indicators injected in adaptation fields
                // Without writing this data, consumers receive unaligned/un-remapped data from the new provider
                _buffer.Write(result.AlignedData);
                _logger.LogDebugIfEnabled(
                    "Injected {AlignedDataBytes} bytes of aligned data into buffer for {ChannelId} (keyframe: {Keyframe})",
                    result.AlignedData.Length,
                    MediaSource.Id,
                    result.AlignedToKeyframe
                );
            }
            else
            {
                // No aligned data means the aligned switch failed. The main HTTP loop will handle
                // discontinuity marking when it establishes the new connection. We still reset
                // timing state to prepare for the new stream.
                _buffer.TsIndexer.ResetTimingState();

                _logger.LogDebugIfEnabled(
                    "Provider switch without aligned data for {ChannelId} - discontinuity will be marked when new connection is established",
                    MediaSource.Id
                );
            }

            _logger.PluginLogInformation(
                "Provider switch successful for {ChannelId} in {ElapsedMs}ms (remapping: {Remapping}, keyframe: {Keyframe}, alignedData: {AlignedBytes} bytes)",
                MediaSource.Id,
                result.DurationMs,
                result.TimestampRemappingActive,
                result.AlignedToKeyframe,
                result.AlignedData?.Length ?? 0
            );

            // Fire-and-forget notification (non-blocking)
            _discordService.SendFireAndForget(svc =>
                svc.NotifyStreamQualityViolationAsync(
                    MediaSource.Id,
                    MediaSource.Name ?? "Unknown",
                    "Provider Switch",
                    $"Switch reason: {reason}"
                )
            );

            // Post-switch PSI monitoring: verify PAT appears within 500ms (TR 101 290 requirement)
            // This runs in the background and only logs a warning if PSI is missing
            _ = MonitorPostSwitchPsiAsync(cancellationToken);

            return true;
        }

        return false;
    }

    /// <summary>
    /// Monitors PSI presence after a provider switch per TR 101 290 requirements.
    /// PAT must appear within 500ms of a switch for compliant streams.
    /// Runs as fire-and-forget; only logs warnings on failure.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the monitoring operation.</returns>
    private async Task MonitorPostSwitchPsiAsync(CancellationToken cancellationToken)
    {
        const int PsiMonitorDelayMs = 500; // TR 101 290: PAT must appear within 500ms

        try
        {
            // Capture bytes written before waiting to detect if new data arrived
            var bytesBeforeWait = _buffer.TotalBytesWritten;

            await Task.Delay(PsiMonitorDelayMs, cancellationToken).ConfigureAwait(false);

            // Check if data was received and programs are detected (PAT processed)
            var bytesAfterWait = _buffer.TotalBytesWritten;
            var programCount = _buffer.TsIndexer.ProgramCount;

            // If we received significant data but no programs, PSI is missing
            var bytesReceived = bytesAfterWait - bytesBeforeWait;
            if (bytesReceived > 10000 && programCount == 0)
            {
                _logger.PluginLogWarning(
                    "No PAT/PMT received from new provider within {TimeoutMs}ms for channel {ChannelId} "
                        + "({BytesReceived} bytes received, 0 programs). Stream may have PSI issues (TR 101 290 violation).",
                    PsiMonitorDelayMs,
                    MediaSource.Id,
                    bytesReceived
                );

                // Notify via Discord if available
                _discordService.SendFireAndForget(svc =>
                    svc.NotifyStreamQualityViolationAsync(
                        MediaSource.Id,
                        MediaSource.Name ?? "Unknown",
                        "PSI Warning",
                        $"No PAT received within {PsiMonitorDelayMs}ms after provider switch"
                    )
                );
            }
            else if (programCount > 0)
            {
                _logger.LogDebugIfEnabled(
                    "Post-switch PSI validation passed for channel {ChannelId}: {ProgramCount} program(s) detected",
                    MediaSource.Id,
                    programCount
                );
            }
        }
        catch (OperationCanceledException)
        {
            // Stream was stopped during monitoring - ignore
        }
        catch (Exception ex)
        {
            _logger.LogDebugIfEnabled(
                ex,
                "Error during post-switch PSI monitoring for channel {ChannelId}",
                MediaSource.Id
            );
        }
    }

    /// <summary>
    /// Attempts a preemptive hot-swap based on health trend predictions.
    /// Uses the failover service's trend tracker to predict imminent failures.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if hot-swap was triggered and succeeded.</returns>
    private async Task<bool> TryTrendBasedHotSwapAsync(CancellationToken cancellationToken)
    {
        // Skip if trend tracking is not available
        if (_failoverService?.TrendTracker is not { } trendTracker)
        {
            return false;
        }

        var providerId = GetCurrentProviderId();
        if (string.IsNullOrEmpty(providerId))
        {
            return false;
        }

        // Get trend snapshot for current provider
        var snapshot = trendTracker.GetSnapshot(providerId);

        // Only trigger preemptive switch if imminent failure is predicted
        if (!snapshot.SuggestsImminentFailure)
        {
            return false;
        }

        _logger.PluginLogInformation(
            "Provider {ProviderId} predicted to fail (trend={Trend}, predicted60s={Score}). Attempting preemptive hot-swap for channel {ChannelId}.",
            providerId,
            snapshot.Trend,
            snapshot.PredictedScore60s,
            MediaSource.Id
        );

        // Try hot-swap to a healthier provider
        if (await TryProviderSwitchAsync(SwitchReason.HealthDegraded, cancellationToken).ConfigureAwait(false))
        {
            // Update resolved URL to the new provider
            _resolvedUrl = await ResolveStreamUrlAsync(_currentSourceUrl, cancellationToken).ConfigureAwait(false);

            _logger.PluginLogInformation(
                "Preemptive hot-swap successful for channel {ChannelId} - switched away from degrading provider",
                MediaSource.Id
            );

            return true;
        }

        return false;
    }

    /// <summary>
    /// Extracts a provider ID from the current source URL for metrics tracking.
    /// Uses the URL host as the provider identifier for simplicity.
    /// </summary>
    /// <returns>The provider ID (host), or null if extraction fails.</returns>
    private string? GetCurrentProviderId()
    {
        try
        {
            var url = _currentSourceUrl;
            if (string.IsNullOrEmpty(url))
            {
                return null;
            }

            var uri = new Uri(url);
            return $"{uri.Host}:{uri.Port}";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Records bitrate metrics for the current provider and updates health trends.
    /// Implements Tier 2 bitrate monitoring per the provider resilience plan.
    /// </summary>
    /// <param name="currentBytes">Current connection bytes.</param>
    /// <param name="previousBytes">Bytes at previous health check.</param>
    /// <param name="previousTime">Time of previous health check.</param>
    /// <param name="currentTime">Current time.</param>
    private void RecordBitrateMetrics(
        long currentBytes,
        long previousBytes,
        DateTime previousTime,
        DateTime currentTime
    )
    {
        // Skip if metrics tracking is not configured
        if (_failoverService == null)
        {
            return;
        }

        var providerId = GetCurrentProviderId();
        if (string.IsNullOrEmpty(providerId))
        {
            return;
        }

        // Calculate interval-specific bitrate
        var intervalSeconds = (currentTime - previousTime).TotalSeconds;
        if (intervalSeconds <= 0 || previousBytes <= 0)
        {
            return;
        }

        var bytesDelta = currentBytes - previousBytes;
        var intervalMbps = bytesDelta * 8.0 / (intervalSeconds * 1_000_000);

        // Record throughput for metrics tracking
        _failoverService.RecordThroughput(providerId, bytesDelta, (long)(intervalSeconds * 1000));

        // Record throughput for ProviderSwitchService quality monitoring
        _providerSwitchService?.RecordThroughput(MediaSource.Id, currentBytes);

        // Low bitrate detection: <0.5 Mbps is below minimum viable streaming
        // This catches slow providers before complete stalls (per Netflix QoE research)
        const double MinViableMbps = 0.5;
        if (intervalMbps < MinViableMbps && currentBytes > MinimumBytesForHealthyStream)
        {
            _logger.PluginLogWarning(
                "Low bitrate detected for channel {ChannelId}: {Mbps:F2} Mbps (minimum: {Min} Mbps)",
                MediaSource.Id,
                intervalMbps,
                MinViableMbps
            );

            // Record as data stall for health scoring
            _failoverService.RecordError(providerId, StreamErrorType.DataStall);

            // Update health trend for predictive failover
            if (_failoverService?.TrendTracker is { } trendTracker)
            {
                var currentScore = _failoverService.CalculateHealthScore(providerId);
                // Penalize score for low bitrate (-20 points)
                trendTracker.RecordSample(providerId, Math.Max(0, currentScore - 20));
            }
        }
        else if (_failoverService?.TrendTracker is { } trendTracker)
        {
            // Record healthy sample for trend tracking
            var currentScore = _failoverService.CalculateHealthScore(providerId);
            trendTracker.RecordSample(providerId, currentScore);
        }
    }

    /// <summary>
    /// Calculates exponential backoff delay with jitter to prevent thundering herd effect.
    /// </summary>
    private static int CalculateBackoffDelay(int failureCount, int baseDelayMs = 1000)
    {
        var exponentialDelay = baseDelayMs * (int)Math.Pow(2.0, Math.Min(failureCount - 1, 5));
        var cappedDelay = Math.Min(exponentialDelay, 30000);
        var random = Random.Shared;
        var jitterFactor = 0.8 + (random.NextDouble() * 0.4);
        return (int)(cappedDelay * jitterFactor);
    }

    /// <summary>
    /// Detects stream quality (SD/HD/FHD/UHD) based on name and metadata.
    /// </summary>
    private static string DetectStreamQuality(MediaSourceInfo mediaSource)
    {
        var name = mediaSource.Name?.ToLowerInvariant() ?? string.Empty;

        if (
            name.Contains("4k", StringComparison.Ordinal)
            || name.Contains("uhd", StringComparison.Ordinal)
            || name.Contains("ultra hd", StringComparison.Ordinal)
            || name.Contains("2160", StringComparison.Ordinal)
        )
        {
            return "UHD/4K";
        }

        if (
            name.Contains("fhd", StringComparison.Ordinal)
            || name.Contains("full hd", StringComparison.Ordinal)
            || name.Contains("1080", StringComparison.Ordinal)
        )
        {
            return "Full HD";
        }

        if (name.Contains("hd", StringComparison.Ordinal) || name.Contains("720", StringComparison.Ordinal))
        {
            return "HD";
        }

        if (mediaSource.MediaStreams != null)
        {
            foreach (var stream in mediaSource.MediaStreams)
            {
                if (stream.Type == MediaStreamType.Video)
                {
                    if (stream.Width >= 3840 || stream.Height >= 2160)
                    {
                        return "UHD/4K";
                    }

                    return stream.Width >= 1920 || stream.Height >= 1080 ? "Full HD"
                        : stream.Width >= 1280 || stream.Height >= 720 ? "HD"
                        : "SD";
                }
            }
        }

        return "HD";
    }

    /// <summary>
    /// Gets the buffer size based on detected stream quality.
    /// </summary>
    private static int GetBufferSize(string quality)
    {
        return quality switch
        {
            "UHD/4K" => UhdBufferSize,
            "Full HD" => HdBufferSize,
            "HD" => HdBufferSize,
            "SD" => SdBufferSize,
            _ => HdBufferSize,
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Gets the count of active Restream instances.
    /// </summary>
    /// <returns>The number of active streams.</returns>
    public static int GetActiveStreamCount() => _activeStreams.Count;

    /// <summary>
    /// Gets information about all active streams for API/UI purposes.
    /// </summary>
    /// <returns>A list of stream information objects.</returns>
    public static IReadOnlyList<StreamInfoSnapshot> GetActiveStreamSnapshots()
    {
        List<StreamInfoSnapshot> result = [];

        foreach (var activeStream in _activeStreams)
        {
            var stream = activeStream.Value;
            try
            {
                long bufferSize = stream._buffer.BufferSize;
                var totalWritten = stream._buffer.TotalBytesWritten;
                var currentPosition = totalWritten % bufferSize;
                var fillPct = bufferSize > 0 ? (double)currentPosition * 100.0 / (double)bufferSize : 0.0;
                var hasWrapped = totalWritten >= bufferSize;
                string status;

                if (stream._broadcastTask == null)
                {
                    status = "Stopped";
                }
                else if (!hasWrapped)
                {
                    var initialFillPct = (double)totalWritten * 100.0 / (double)bufferSize;
                    status = initialFillPct > 75.0 ? "Filling" : "Buffering";
                    fillPct = initialFillPct;
                }
                else
                {
                    status = "Streaming";
                }

                // Get quality metrics from TsIndexer and TsDuck
                var monitor = stream._buffer.TsIndexer;
                var packetErrors = monitor.TotalPacketErrors;
                var continuityErrors = monitor.TotalContinuityErrors;
                var syncErrors = monitor.SyncByteErrors;
                var crcErrors = monitor.PatCrcErrors + monitor.PmtCrcErrors + monitor.CatCrcErrors;

                // Get TR 101 290 metrics from TsDuck (preferred) or fall back to basic CRC errors
                var tsDuckMetrics = stream._buffer.TsDuckAnalyzer?.GetMetrics();
                var patViolations = tsDuckMetrics?.Priority1.PatError ?? 0L;
                if (tsDuckMetrics != null)
                {
                    // Use TsDuck's comprehensive TR 101 290 metrics
                    syncErrors = tsDuckMetrics.Priority1.SyncByteError;
                    continuityErrors = tsDuckMetrics.Priority1.ContinuityCountError;
                    crcErrors = tsDuckMetrics.Priority2.CrcError;
                }
                var avDriftMs = monitor.GetCurrentDriftMs();
                var syncStatus = monitor.GetSyncStatus();

                // Calculate quality level and issues
                var issues = new List<string>();
                if (packetErrors > 0)
                {
                    issues.Add(string.Format(CultureInfo.InvariantCulture, "{0} packet errors", packetErrors));
                }

                if (continuityErrors > 0)
                {
                    issues.Add(string.Format(CultureInfo.InvariantCulture, "{0} continuity errors", continuityErrors));
                }

                if (syncErrors > 0)
                {
                    issues.Add(string.Format(CultureInfo.InvariantCulture, "{0} sync errors", syncErrors));
                }

                if (patViolations > 0)
                {
                    issues.Add(string.Format(CultureInfo.InvariantCulture, "{0} PAT violations", patViolations));
                }

                if (crcErrors > 0)
                {
                    issues.Add(string.Format(CultureInfo.InvariantCulture, "{0} CRC errors", crcErrors));
                }

                if (Math.Abs(avDriftMs) > 40)
                {
                    issues.Add(string.Format(CultureInfo.InvariantCulture, "A/V drift {0:F0}ms", avDriftMs));
                }

                var qualityLevel =
                    issues.Count == 0 ? "None"
                    : Math.Abs(avDriftMs) > 100 || crcErrors > 10 || packetErrors > 100 ? "Critical"
                    : "Warning";

                // Get discontinuity/reconnection info
                var reconnectionCount = stream._buffer.DiscontinuityCount;
                var lastDiscontinuityOffset = stream._buffer.LastDiscontinuityOffset;
                var lastDiscontinuityTime = stream._buffer.LastDiscontinuityTime;
                double? secondsSinceLastReconnection = lastDiscontinuityTime.HasValue
                    ? (DateTime.UtcNow - lastDiscontinuityTime.Value).TotalSeconds
                    : null;

                result.Add(
                    new StreamInfoSnapshot
                    {
                        StreamId = stream.MediaSource.Id,
                        ChannelName = stream.MediaSource.Name ?? "Unknown",
                        StartTime = stream._startTime,
                        BufferSizeBytes = bufferSize,
                        TotalBytesWritten = totalWritten,
                        TotalBytesRead = 0L,
                        CurrentGapBytes = 0L,
                        GapPercentage = 100.0 - fillPct,
                        OverflowCount = 0,
                        OverflowBytes = 0L,
                        Status = status,
                        IsAligned = true,
                        // Quality metrics
                        PacketErrors = packetErrors,
                        ContinuityErrors = continuityErrors,
                        SyncErrors = syncErrors,
                        PatViolations = patViolations,
                        CrcErrors = crcErrors,
                        AvDriftMs = avDriftMs,
                        SyncStatus = syncStatus.ToString(),
                        HasQualityIssues = issues.Count > 0,
                        QualityLevel = qualityLevel,
                        QualityIssues = issues.Count > 0 ? string.Join(", ", issues) : null,
                        // Reconnection metrics
                        ReconnectionCount = reconnectionCount,
                        LastDiscontinuityOffset = lastDiscontinuityOffset,
                        SecondsSinceLastReconnection = secondsSinceLastReconnection,
                    }
                );
            }
            catch
            {
                // Ignore errors for individual streams
            }
        }

        return result;
    }

    /// <summary>
    /// Kills (disposes) a stream by its ID.
    /// </summary>
    /// <param name="streamId">The stream ID to kill.</param>
    /// <param name="reason">Optional reason for killing the stream (for notifications).</param>
    /// <returns>True if the stream was found and killed, false otherwise.</returns>
    public static bool KillStream(string streamId, string? reason = null)
    {
        if (_activeStreams.TryGetValue(streamId, out var stream))
        {
            stream._killReason = reason ?? "Manual termination";
            stream.Dispose();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Kills all active streams.
    /// </summary>
    /// <param name="reason">Optional reason for killing the streams (for notifications).</param>
    /// <returns>The number of streams killed.</returns>
    public static int KillAllStreams(string? reason = null)
    {
        var count = 0;

        foreach (var stream in _activeStreams.Values.ToList())
        {
            try
            {
                stream._killReason = reason ?? "Bulk termination";
                stream.Dispose();
                count++;
            }
            catch
            {
                // Ignore disposal errors
            }
        }

        return count;
    }
}
