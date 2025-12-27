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
using Jellyfin.Xtream.Service.MpegTs;
using Jellyfin.Xtream.Service.ProviderManagement;
using Jellyfin.Xtream.Service.Switching;
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
    private static readonly ConcurrentDictionary<string, Restream> _activeStreams = new ConcurrentDictionary<
        string,
        Restream
    >(StringComparer.Ordinal);

    private static readonly HttpStatusCode[] _redirects = new HttpStatusCode[]
    {
        HttpStatusCode.MovedPermanently,
        HttpStatusCode.MovedPermanently,
        HttpStatusCode.PermanentRedirect,
        HttpStatusCode.Found,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<Restream> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IDiscordNotificationService? _discordService;
    private readonly string _sourceUrl;
    private readonly SemaphoreSlim _openLock = new SemaphoreSlim(1, 1);
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

    // Hot-swap: depends on abstraction (DIP), not concrete implementation
    private readonly IStreamHotSwapService? _hotSwapService;
    private volatile string _currentSourceUrl;

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
    public Restream(
        IServerApplicationHost appHost,
        IHttpClientFactory httpClientFactory,
        ILogger<Restream> logger,
        ILoggerFactory loggerFactory,
        MediaSourceInfo mediaSource,
        IDiscordNotificationService? discordService = null
    )
        : this(appHost, httpClientFactory, logger, loggerFactory, mediaSource, discordService, null) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="Restream"/> class with hot-swap support.
    /// Follows DIP: depends on IStreamHotSwapService abstraction, not concrete implementation.
    /// </summary>
    /// <param name="appHost">Instance of the <see cref="IServerApplicationHost"/> interface.</param>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{Restream}"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="mediaSource">The media which must be restreamed.</param>
    /// <param name="discordService">Optional Discord notification service.</param>
    /// <param name="hotSwapService">Optional hot-swap service for mid-stream provider switching (DIP).</param>
    public Restream(
        IServerApplicationHost appHost,
        IHttpClientFactory httpClientFactory,
        ILogger<Restream> logger,
        ILoggerFactory loggerFactory,
        MediaSourceInfo mediaSource,
        IDiscordNotificationService? discordService,
        IStreamHotSwapService? hotSwapService
    )
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _discordService = discordService;
        _hotSwapService = hotSwapService;
        MediaSource = mediaSource;
        _tokenSource = new CancellationTokenSource();
        _streamQuality = DetectStreamQuality(mediaSource);
        int bufferSize = GetBufferSize(_streamQuality);
        _buffer = new CircularBufferWriteStream(bufferSize, _loggerFactory);
        _buffer.TsIndexer.StreamQualityViolation += OnStreamQualityViolation;
        _buffer.TsIndexer.SyncDriftDetected += OnSyncDriftDetected;
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
        string path = "/LiveTv/LiveStreamFiles/" + UniqueId + "/stream.ts";
        MediaSource.Path = appHost.GetSmartApiUrl(IPAddress.Any) + path;
        MediaSource.EncoderPath = appHost.GetApiUrlForLocalAccess() + path;
        MediaSource.Protocol = MediaProtocol.Http;
        _readerPool = new RefCountedResourcePool<CircularBufferReadStream>(CreateReaderStream, OnConsumerCountChanged);
        _activeStreams.TryAdd(MediaSource.Id, this);
        _logger.LogDebugIfEnabled(
            "Registered Restream {StreamId} (total active: {Count}){HotSwap}",
            MediaSource.Id,
            _activeStreams.Count,
            hotSwapService != null ? " [hot-swap enabled]" : string.Empty
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
    private CircularBufferReadStream CreateReaderStream()
    {
        return new CircularBufferReadStream(_buffer, _logger, MediaSource.Id, MediaSource.Name, _discordService, -1);
    }

    /// <summary>
    /// Callback when consumer count changes.
    /// Automatically cleans up streams when all consumers disconnect.
    /// </summary>
    private void OnConsumerCountChanged(int newCount)
    {
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
            CancellationToken cancellationToken = _cleanupCts.Token;

            Task.Run(
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
        int streamOpenTimeoutMs = StreamingTimeoutPolicy.GetStreamOpenTimeoutMs();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(openCancellationToken);
        timeoutCts.CancelAfter(streamOpenTimeoutMs);

        await _openLock.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        try
        {
            if (_broadcastTask != null)
            {
                _logger.LogDebugIfEnabled("Broadcast for channel {ChannelId} is already running.", MediaSource.Id);
                return;
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
            else if (_tokenSource == null)
            {
                _tokenSource = new CancellationTokenSource();
            }

            _resolvedUrl = await ResolveStreamUrlAsync(_sourceUrl, timeoutCts.Token).ConfigureAwait(false);
            _broadcastTask = BroadcastFromSourceAsync(_tokenSource.Token);

            // Wait for first data with timeout (fast-fail if no data)
            // This detects "connected but no data" scenarios common with overloaded providers
            int firstByteTimeoutMs = StreamingTimeoutPolicy.GetFirstByteTimeoutMs();
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
            _openLock.Release();
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

        int pollCount = 0;
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
    private async Task<Uri> ResolveStreamUrlAsync(string initialUrl, CancellationToken cancellationToken)
    {
        Uri currentUrl = new Uri(initialUrl);
        HttpClient client = _httpClientFactory.CreateClient("XtreamClient");

        for (int redirectCount = 0; redirectCount < MaxRedirects; redirectCount++)
        {
            using HttpResponseMessage response = await client
                .GetAsync(currentUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            _logger.LogDebugIfEnabled(
                "URL resolution for channel {ChannelId} - Attempt {Attempt}, URL: {Url}, Status: {StatusCode}",
                MediaSource.Id,
                redirectCount + 1,
                currentUrl,
                response.StatusCode
            );

            if (_redirects.Contains(response.StatusCode))
            {
                Uri? redirectLocation = response.Headers.Location;
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

                _logger.LogDebugIfEnabled(
                    "Stream for channel {ChannelId} redirected: {OldUrl} → {NewUrl} (HTTPS→HTTP: {IsDowngrade})",
                    MediaSource.Id,
                    currentUrl,
                    redirectLocation,
                    currentUrl.Scheme == "https" && redirectLocation.Scheme == "http"
                );
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

        int networkBufferSize = _streamQuality switch
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

        Memory<byte> bufferMemory = _networkBuffer.AsMemory();
        long totalBytesAllConnections = 0L;
        DateTime sessionStartTime = DateTime.UtcNow;
        int connectionAttempt = 0;
        int consecutiveFailures = 0;

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
                using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, _resolvedUrl);
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
                        string responseHeaders = string.Join(
                            ", ",
                            response.Headers.Select(h => h.Key + "=" + string.Join(";", h.Value))
                        );
                        string contentType = response.Content.Headers.ContentType?.ToString() ?? "none";
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
                            int conflictBackoff = Math.Max(3000, CalculateBackoffDelay(consecutiveFailures, 2000));
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

                        bool isPermanentError = response.StatusCode switch
                        {
                            HttpStatusCode.Unauthorized => true,
                            HttpStatusCode.Forbidden => true,
                            HttpStatusCode.NotFound => true,
                            HttpStatusCode.NotAcceptable => true,
                            HttpStatusCode.ProxyAuthenticationRequired => true, // 407 - provider auth issue
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
                            int acceptBackoff = Math.Max(4000, CalculateBackoffDelay(consecutiveFailures, 2500));
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
                                    await TryHotSwapAsync(SwitchReason.HealthDegraded, cancellationToken)
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

                            int backoffDelay = CalculateBackoffDelay(consecutiveFailures);
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
                        // Mark discontinuity point so readers skip past stale pre-disconnect data
                        // This prevents video loops caused by timestamp discontinuities after reconnection
                        _buffer.MarkDiscontinuity();
                        _buffer.TsIndexer.ResetTimingState();
                        _logger.LogDebugIfEnabled(
                            "Marked discontinuity at offset {Offset} for channel {ChannelId} after reconnection",
                            _buffer.TotalBytesWritten,
                            MediaSource.Id
                        );
                    }

                    long connectionBytes = 0L;
                    DateTime connectionStartTime = DateTime.UtcNow;
                    DateTime lastLogTime = connectionStartTime;
                    DateTime lastHealthCheckTime = connectionStartTime;

                    // Get configurable data stall timeout (industry standard: 10-20s)
                    int dataStallTimeoutMs = StreamingTimeoutPolicy.GetDataStallTimeoutMs();

                    while (!cancellationToken.IsCancellationRequested)
                    {
                        int bytesRead;
                        try
                        {
                            // Apply data stall timeout to detect hung connections
                            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            readCts.CancelAfter(dataStallTimeoutMs);

                            bytesRead = await sourceStream.ReadAsync(bufferMemory, readCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            // Data stall detected - connection is alive but not sending data
                            // Signal disconnection so readers know source is stalled
                            _buffer.SignalSourceDisconnected();

                            _logger.PluginLogWarning(
                                "Data stall detected for channel {ChannelId} after {TimeoutMs}ms without data. Attempting hot-swap...",
                                MediaSource.Id,
                                dataStallTimeoutMs
                            );

                            // Try hot-swap to a different provider before reconnecting
                            if (
                                await TryHotSwapAsync(SwitchReason.ConnectionFailed, cancellationToken)
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

                            if (connectionBytes == 0)
                            {
                                _logger.PluginLogError(
                                    "Connection #{Attempt} for channel {ChannelId} closed immediately without sending data",
                                    connectionAttempt,
                                    MediaSource.Id
                                );
                                break;
                            }

                            double connectionDuration = (DateTime.UtcNow - connectionStartTime).TotalSeconds;
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
                                    && await TryHotSwapAsync(SwitchReason.CapacityReached, cancellationToken)
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

                        await _buffer
                            .WriteAsync(bufferMemory.Slice(0, bytesRead), cancellationToken)
                            .ConfigureAwait(false);
                        connectionBytes += bytesRead;
                        totalBytesAllConnections += bytesRead;

                        DateTime now = DateTime.UtcNow;
                        if ((now - lastLogTime).TotalSeconds >= ProgressLogIntervalSeconds)
                        {
                            double sessionElapsed = (now - sessionStartTime).TotalSeconds;
                            double mbps = (double)totalBytesAllConnections * 8.0 / 1000000.0 / sessionElapsed;
                            double bufferFillPct =
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
                            double bufferFillPct2 =
                                (double)(_buffer.TotalBytesWritten % _buffer.BufferSize)
                                * 100.0
                                / (double)_buffer.BufferSize;
                            double currentBitrate =
                                totalBytesAllConnections > 0
                                    ? (double)totalBytesAllConnections
                                        * 8.0
                                        / 1000000.0
                                        / (now - sessionStartTime).TotalSeconds
                                    : 0.0;
                            bool isUnderrun = bufferFillPct2 < BufferUnderrunThresholdPercent;

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
                                        int underrunCount = _bufferUnderrunCount;
                                        double fillPct = bufferFillPct2;
                                        double bitrate = currentBitrate;
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

                            bool isNearFull = bufferFillPct2 > BufferNearFullThresholdPercent;
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
                    int cleanupDelayMs = consecutiveFailures == 0 && totalBytesAllConnections > 1048576 ? 250 : 500;
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

                    bool isHealthyStream =
                        consecutiveFailures == 0 && totalBytesAllConnections > MinimumBytesForHealthyStream;
                    int minimumReconnectDelay = isHealthyStream
                        ? 500
                        : (totalBytesAllConnections > MinimumBytesForHealthyStream ? 1500 : 0);
                    int backoffDelay2 = Math.Max(minimumReconnectDelay, CalculateBackoffDelay(consecutiveFailures));
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
                    int backoffDelay3 = CalculateBackoffDelay(consecutiveFailures);
                    _logger.LogDebugIfEnabled(
                        "Waiting {DelayMs}ms before retry #{NextAttempt} (exponential backoff)",
                        backoffDelay3,
                        connectionAttempt + 1
                    );
                    await Task.Delay(backoffDelay3, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        double totalSessionDuration = (DateTime.UtcNow - sessionStartTime).TotalSeconds;
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

        if (_tokenSource != null && !_tokenSource.IsCancellationRequested)
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

        if (_broadcastTask == null)
        {
            _logger.PluginLogWarning(
                "Broadcast not initialized for channel {ChannelId}, initializing now...",
                MediaSource.Id
            );
            Open(CancellationToken.None).GetAwaiter().GetResult();
        }

        Stream? stream = _readerPool.Acquire();
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

        long bytesTransferred = _buffer.TotalBytesWritten;
        bool streamActuallyStarted = bytesTransferred > 0 || _broadcastTask != null;

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

        if (_tokenSource != null)
        {
            if (!_tokenSource.IsCancellationRequested)
            {
                _tokenSource.Cancel();
            }

            _tokenSource.Dispose();
        }

        _buffer.Dispose();
        _openLock.Dispose();
        _readerPool.Dispose();
        _networkBuffer = null;
        _logger.PluginLogInformation("Restream for channel {ChannelId} disposed", MediaSource.Id);
    }

    /// <summary>
    /// Handles TR 101 290 stream quality violation events from the TsIndexer.
    /// Sends Discord notifications for stream quality issues.
    /// </summary>
    private void OnStreamQualityViolation(object? sender, StreamQualityViolationEventArgs e)
    {
        _discordService.SendFireAndForget(svc =>
            svc.NotifyStreamQualityViolationAsync(
                MediaSource.Id,
                MediaSource.Name ?? "Unknown Channel",
                e.ViolationType,
                e.Details
            )
        );
    }

    /// <summary>
    /// Handles A/V synchronization drift events from the TsIndexer.
    /// Sends Discord notifications when audio and video drift out of sync.
    /// </summary>
    private void OnSyncDriftDetected(object? sender, SyncDriftEventArgs e)
    {
        var indexer = _buffer.TsIndexer;
        var peakDrift = Math.Abs(e.DriftMs);
        var violationCount = 1L;
        var program = indexer.GetFirstProgramWithVideo();

        if (program != null)
        {
            var tracker = program.GetOrCreateTimestampTracker();
            peakDrift = tracker.PeakDriftMs;
            violationCount = tracker.DriftViolationCount;
        }

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
    }

    /// <summary>
    /// Attempts a hot-swap to a new provider when the current one is failing.
    /// Delegates to IStreamHotSwapService following DIP (Dependency Inversion Principle).
    /// </summary>
    /// <param name="reason">The reason for the hot-swap attempt.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the hot-swap succeeded and _currentSourceUrl was updated.</returns>
    private async Task<bool> TryHotSwapAsync(SwitchReason reason, CancellationToken cancellationToken)
    {
        // Fast-path: check if hot-swap service is available (DIP - depend on abstraction)
        if (_hotSwapService == null)
        {
            return false;
        }

        // Create context for the hot-swap service
        var context = new HotSwapContext
        {
            StreamId = MediaSource.Id,
            CurrentUrl = _currentSourceUrl,
            BytesTransferred = _buffer.TotalBytesWritten,
        };

        // Delegate to the hot-swap service (SRP - Restream doesn't manage hot-swap logic)
        var result = await _hotSwapService.TrySwitchAsync(context, reason, cancellationToken).ConfigureAwait(false);

        if (result.Success && result.NewUrl is { Length: > 0 })
        {
            // Update URL atomically
            _currentSourceUrl = result.NewUrl;

            // Mark discontinuity for timestamp handling (critical for seamless playback)
            _buffer.MarkDiscontinuity();
            _buffer.TsIndexer.ResetTimingState();

            _logger.PluginLogInformation(
                "Hot-swap successful for {ChannelId} in {ElapsedMs}ms",
                MediaSource.Id,
                result.ElapsedMs
            );

            // Fire-and-forget notification (non-blocking)
            _discordService.SendFireAndForget(svc =>
                svc.NotifyStreamQualityViolationAsync(
                    MediaSource.Id,
                    MediaSource.Name ?? "Unknown",
                    "Provider Switch",
                    $"Hot-swap: {reason}"
                )
            );

            return true;
        }

        return false;
    }

    /// <summary>
    /// Calculates exponential backoff delay with jitter to prevent thundering herd effect.
    /// </summary>
    private static int CalculateBackoffDelay(int failureCount, int baseDelayMs = 1000)
    {
        int exponentialDelay = baseDelayMs * (int)Math.Pow(2.0, Math.Min(failureCount - 1, 5));
        int cappedDelay = Math.Min(exponentialDelay, 30000);
        Random random = Random.Shared;
        double jitterFactor = 0.8 + random.NextDouble() * 0.4;
        return (int)(cappedDelay * jitterFactor);
    }

    /// <summary>
    /// Detects stream quality (SD/HD/FHD/UHD) based on name and metadata.
    /// </summary>
    private static string DetectStreamQuality(MediaSourceInfo mediaSource)
    {
        string name = mediaSource.Name?.ToLowerInvariant() ?? string.Empty;

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
            foreach (MediaStream stream in mediaSource.MediaStreams)
            {
                if (stream.Type == MediaStreamType.Video)
                {
                    if (stream.Width >= 3840 || stream.Height >= 2160)
                    {
                        return "UHD/4K";
                    }

                    if (stream.Width >= 1920 || stream.Height >= 1080)
                    {
                        return "Full HD";
                    }

                    if (stream.Width >= 1280 || stream.Height >= 720)
                    {
                        return "HD";
                    }

                    return "SD";
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
    public static int GetActiveStreamCount()
    {
        return _activeStreams.Count;
    }

    /// <summary>
    /// Gets information about all active streams for API/UI purposes.
    /// </summary>
    /// <returns>A list of stream information objects.</returns>
    public static IReadOnlyList<StreamInfoSnapshot> GetActiveStreamSnapshots()
    {
        List<StreamInfoSnapshot> result = new List<StreamInfoSnapshot>();

        foreach (KeyValuePair<string, Restream> activeStream in _activeStreams)
        {
            Restream stream = activeStream.Value;
            try
            {
                long bufferSize = stream._buffer.BufferSize;
                long totalWritten = stream._buffer.TotalBytesWritten;
                long currentPosition = totalWritten % bufferSize;
                double fillPct = bufferSize > 0 ? (double)currentPosition * 100.0 / (double)bufferSize : 0.0;
                bool hasWrapped = totalWritten >= bufferSize;
                string status;

                if (stream._broadcastTask == null)
                {
                    status = "Stopped";
                }
                else if (!hasWrapped)
                {
                    double initialFillPct = (double)totalWritten * 100.0 / (double)bufferSize;
                    status = initialFillPct > 75.0 ? "Filling" : "Buffering";
                    fillPct = initialFillPct;
                }
                else
                {
                    status = "Streaming";
                }

                // Get quality metrics from TsIndexer
                MpegTs.TsIndexer monitor = stream._buffer.TsIndexer;
                long packetErrors = monitor.TotalPacketErrors;
                long continuityErrors = monitor.TotalContinuityErrors;
                long syncErrors = monitor.SyncByteErrors;
                long patViolations = monitor.PatIntervalViolations;
                long crcErrors = monitor.PatCrcErrors + monitor.PmtCrcErrors + monitor.CatCrcErrors;
                double avDriftMs = monitor.GetCurrentDriftMs();
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

                string qualityLevel =
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
        if (_activeStreams.TryGetValue(streamId, out Restream? stream))
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
        int count = 0;

        foreach (Restream stream in _activeStreams.Values.ToList())
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
