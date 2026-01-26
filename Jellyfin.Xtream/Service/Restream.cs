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
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Service.Streaming.Native;
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

    // Timeout constants
    private const int StreamOpenTimeoutMs = 10000;
    private const int FirstByteTimeoutMs = 5000;

    // Cleanup and timing constants
    private const int ConsumerDisconnectGraceSeconds = 5;
    private const int FirstBytePollIntervalMs = 50;
    private const int HealthCheckIntervalSeconds = 30;
    private const double BufferUnderrunThresholdPercent = 10.0;
    private const double BufferNearFullThresholdPercent = 90.0;
    private const int BufferUnderrunNotificationThreshold = 5;

    /// <summary>
    /// Global registry of all active Restream instances for monitoring and management.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Restream> _activeStreams = new(StringComparer.Ordinal);

    private readonly ILogger<Restream> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IDiscordNotificationService? _discordService;
    private readonly IReadOnlyList<string> _urls;
    private readonly string _sourceUrl;
    private readonly SemaphoreSlim _openLock = new(1, 1);
    private readonly CircularBufferWriteStream _buffer;
    private readonly RefCountedResourcePool<CircularBufferReadStream> _readerPool;
    private readonly string _streamQuality;
    private readonly DateTime _startTime = DateTime.UtcNow;

    private CancellationTokenSource _tokenSource;
    private Task? _broadcastTask;
    private int _consumerCount;
    private bool _isDisposed;
    private int _bufferUnderrunCount;
    private int _bufferHealthWarnings;
    private string? _killReason;
    private CancellationTokenSource? _cleanupCts;

    // Native HTTP streamer with failover and mid-stream switching
    private NativeStreamer? _nativeStreamer;

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
    /// <param name="logger">Instance of the <see cref="ILogger{Restream}"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="mediaSource">The media which must be restreamed.</param>
    /// <param name="urls">The list of URLs to use for streaming (native handles failover/rotation).</param>
    /// <param name="discordService">Optional Discord notification service.</param>
    public Restream(
        IServerApplicationHost appHost,
        ILogger<Restream> logger,
        ILoggerFactory loggerFactory,
        MediaSourceInfo mediaSource,
        IReadOnlyList<string> urls,
        IDiscordNotificationService? discordService = null
    )
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _discordService = discordService;
        _urls = urls;
        MediaSource = mediaSource;
        _tokenSource = new CancellationTokenSource();
        _streamQuality = DetectStreamQuality(mediaSource);
        var bufferSize = GetBufferSize(_streamQuality);

        _buffer = new CircularBufferWriteStream(bufferSize, _loggerFactory);

        _logger.PluginLogInformation(
            "Initialized stream {StreamId} ({Quality}) with automatic quality-based buffering ({BufferSizeMB}MB buffer)",
            mediaSource.Id,
            _streamQuality,
            (double)bufferSize / 1048576.0
        );
        OriginalStreamId = MediaSource.Id;
        UniqueId = Guid.NewGuid().ToString();
        _sourceUrl = _urls.Count > 0 ? _urls[0] : "unknown";
        var path = "/LiveTv/LiveStreamFiles/" + UniqueId + "/stream.ts";
        MediaSource.Path = appHost.GetSmartApiUrl(IPAddress.Any) + path;
        MediaSource.EncoderPath = appHost.GetApiUrlForLocalAccess() + path;
        MediaSource.Protocol = MediaProtocol.Http;
        _readerPool = new RefCountedResourcePool<CircularBufferReadStream>(CreateReaderStream, OnConsumerCountChanged);
        _ = _activeStreams.TryAdd(MediaSource.Id, this);
        _logger.LogDebugIfEnabled(
            "Registered Restream {StreamId} (total active: {Count})",
            MediaSource.Id,
            _activeStreams.Count
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
        new(_buffer, _logger, MediaSource.Id, MediaSource.Name, _discordService);

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
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(openCancellationToken);
        timeoutCts.CancelAfter(StreamOpenTimeoutMs);

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
                StreamOpenTimeoutMs
            );
            _buffer.Reset();

            if (_tokenSource?.IsCancellationRequested ?? false)
            {
                _tokenSource?.Dispose();
                _tokenSource = new CancellationTokenSource();
            }
            else
            {
                _tokenSource ??= new CancellationTokenSource();
            }

            _broadcastTask = BroadcastFromSourceAsync(_tokenSource.Token);

            // Wait for first data with timeout (fast-fail if no data)
            if (!await WaitForFirstDataAsync(FirstByteTimeoutMs, timeoutCts.Token).ConfigureAwait(false))
            {
                _logger.PluginLogWarning(
                    "Stream {ChannelId} did not produce data within {TimeoutMs}ms first-byte timeout",
                    MediaSource.Id,
                    FirstByteTimeoutMs
                );
                throw new TimeoutException($"Stream did not produce data within {FirstByteTimeoutMs}ms");
            }

            _logger.LogDebugIfEnabled(
                "Broadcast started for channel {ChannelId} (first data received)",
                MediaSource.Id
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
                StreamOpenTimeoutMs
            );
            throw new TimeoutException($"Stream connection timed out after {StreamOpenTimeoutMs}ms");
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
    /// Broadcasts data from the native HTTP streamer to the circular buffer.
    /// The native streamer handles HTTP connections, redirects, retry/backoff,
    /// stall detection, packet alignment, restamping, and failover/rotation.
    /// </summary>
    private async Task BroadcastFromSourceAsync(CancellationToken cancellationToken)
    {
        var streamerConfig = TsDuckStreamerConfigNative.Default;

        var analyzerConfig = TsDuckConfigNative.FromManaged(TsDuckConfiguration.Default);

        _nativeStreamer?.Dispose();
        _nativeStreamer = NativeStreamer.TryCreate(streamerConfig, analyzerConfig, _logger);

        if (_nativeStreamer == null)
        {
            _killReason = "Native streamer unavailable";

            return;
        }

        try
        {
            // Add all URLs upfront - native handles failover/rotation
            foreach (var url in _urls)
            {
                _nativeStreamer.AddUrl(url);
            }

            _nativeStreamer.SetOutputCallback(
                (dataPtr, length) =>
                {
                    unsafe
                    {
                        _buffer.Write(new ReadOnlySpan<byte>((void*)dataPtr, length));
                    }
                }
            );

            _nativeStreamer.StreamEvent += OnNativeStreamerEvent;

            if (!_nativeStreamer.Start())
            {
                _killReason = "Native streamer start failed";

                return;
            }

            _buffer.SignalSourceConnected();

            await RunHealthMonitoringLoopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _nativeStreamer.StreamEvent -= OnNativeStreamerEvent;

            _nativeStreamer.Stop();

            _buffer.SignalSourceDisconnected();
        }
    }

    /// <summary>
    /// Handles events from the native streamer (connected, disconnected, switched, etc.).
    /// </summary>
    private void OnNativeStreamerEvent(object? sender, StreamerEventArgs e)
    {
        switch (e.EventType)
        {
            case StreamerEvent.Connected:

                _buffer.SignalSourceConnected();

                break;

            case StreamerEvent.Disconnected:

                _buffer.SignalSourceDisconnected();

                _buffer.MarkDiscontinuityAligned();

                break;

            case StreamerEvent.Reconnecting:

                _buffer.SignalReconnecting();

                break;

            case StreamerEvent.Switched:

                _buffer.MarkDiscontinuityAligned();

                break;

            case StreamerEvent.Stalled:
            case StreamerEvent.Error:
            case StreamerEvent.DataReceived:
            case StreamerEvent.Stopped:

                break;
        }
    }

    /// <summary>
    /// Monitors streamer health periodically, checking for terminal states
    /// and buffer health.
    /// </summary>
    private async Task RunHealthMonitoringLoopAsync(CancellationToken cancellationToken)
    {
        var sessionStartTime = DateTime.UtcNow;

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(HealthCheckIntervalSeconds * 1000, cancellationToken).ConfigureAwait(false);

            var status = _nativeStreamer?.GetStatus() ?? default;

            if (status.IsTerminal)
            {
                _logger.PluginLogWarning(
                    "Native streamer reached terminal state {State} for channel {ChannelId}",
                    status.State,
                    MediaSource.Id
                );

                _killReason = $"Streamer: {status.State}";

                Dispose();

                return;
            }

            var now = DateTime.UtcNow;

            var currentBytes = status.BytesReceived;

            // Buffer health checks

            var bufferFillPct =
                (double)(_buffer.TotalBytesWritten % _buffer.BufferSize) * 100.0 / (double)_buffer.BufferSize;

            if (bufferFillPct < BufferUnderrunThresholdPercent)
            {
                _bufferUnderrunCount++;

                if (_bufferUnderrunCount % 3 == 1)
                {
                    var currentBitrate =
                        currentBytes > 0
                            ? (double)currentBytes * 8.0 / 1000000.0 / (now - sessionStartTime).TotalSeconds
                            : 0.0;

                    _logger.PluginLogWarning(
                        "Buffer underrun #{Count} for channel {ChannelId}. Only {FillPct:F1}% filled. Bitrate: {Mbps:F2} Mbps.",
                        _bufferUnderrunCount,
                        MediaSource.Id,
                        bufferFillPct,
                        currentBitrate
                    );

                    if (_bufferUnderrunCount >= BufferUnderrunNotificationThreshold)
                    {
                        var fillPct = bufferFillPct;

                        var bitrate = currentBitrate;

                        var count = _bufferUnderrunCount;

                        _discordService.SendFireAndForget(svc =>
                            svc.NotifyBufferHealthIssueAsync(
                                MediaSource.Id,
                                MediaSource.Name ?? "Unknown",
                                count,
                                fillPct,
                                bitrate
                            )
                        );
                    }
                }
            }
            else if (bufferFillPct > BufferNearFullThresholdPercent)
            {
                _bufferHealthWarnings++;

                _logger.LogDebugIfEnabled(
                    "Buffer for channel {ChannelId} is {FillPct:F1}% full. Consumers: {Consumers}",
                    MediaSource.Id,
                    bufferFillPct,
                    ConsumerCount
                );
            }

            // Progress logging

            var sessionElapsed = (now - sessionStartTime).TotalSeconds;

            if (sessionElapsed > 0 && currentBytes > 0)
            {
                var mbps = (double)currentBytes * 8.0 / 1000000.0 / sessionElapsed;

                _logger.LogDebugIfEnabled(
                    "Broadcast progress for channel {ChannelId}: {TotalMB} MB, {Mbps:F2} Mbps avg, {Consumers} consumers, buffer {FillPct:F1}%",
                    MediaSource.Id,
                    currentBytes / 1048576,
                    mbps,
                    ConsumerCount,
                    bufferFillPct
                );
            }
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

        if (_tokenSource != null)
        {
            if (!_tokenSource.IsCancellationRequested)
            {
                _tokenSource.Cancel();
            }

            _tokenSource.Dispose();
        }

        _buffer.Dispose();
        _nativeStreamer?.Dispose();
        _nativeStreamer = null;
        _openLock.Dispose();
        _readerPool.Dispose();

        _logger.PluginLogInformation("Restream for channel {ChannelId} disposed", MediaSource.Id);
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
                        // Quality metrics - defaults (native handles analysis internally)
                        PacketErrors = 0L,
                        ContinuityErrors = 0L,
                        SyncErrors = 0L,
                        PatViolations = 0L,
                        CrcErrors = 0L,
                        AvDriftMs = 0.0,
                        SyncStatus = "Unknown",
                        HasQualityIssues = false,
                        QualityLevel = "None",
                        QualityIssues = null,
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
