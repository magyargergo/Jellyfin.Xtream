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
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service.Streaming.Native;
using Jellyfin.Xtream.Service.Streaming.SharedMemory;
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
/// </summary>
/// <remarks>
/// <para>
/// This is a thin layer that:
/// 1. Sets up providers with URLs and initial health scores
/// 2. Reads buffer data from shared memory (C++ writes to it)
/// 3. Serves bytes to Jellyfin consumers
/// </para>
/// <para>
/// C++ handles all decision making: URL selection, failover, health tracking, outcome recording.
/// </para>
/// </remarks>
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

    // Timeout defaults (can be overridden via constructor)
    private const int DefaultStreamOpenTimeoutMs = 15000;
    private const int DefaultFirstByteTimeoutMs = 15000;

    // Cleanup and timing constants
    private const int FirstBytePollIntervalMs = 50;
    private const int HealthCheckIntervalSeconds = 30;

    // Data flow synchronization
    private volatile bool _receivingData;

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
    private readonly int _streamOpenTimeoutMs;
    private readonly int _firstByteTimeoutMs;

    // Cached buffer threshold values from plugin configuration (used in hot paths)
    private readonly double _bufferUnderrunThresholdPercent;
    private readonly double _bufferNearFullThresholdPercent;
    private readonly int _bufferUnderrunNotificationThreshold;
    private readonly int _consumerDisconnectGraceSeconds;

    private CancellationTokenSource _tokenSource;
    private Task? _broadcastTask;
    private int _consumerCount;
    private volatile bool _isDisposed;
    private int _bufferUnderrunCount;
    private int _bufferHealthWarnings;
    private string? _killReason;
    private CancellationTokenSource? _cleanupCts;

    // Native HTTP streamer with failover and mid-stream switching
    private NativeStreamer? _nativeStreamer;

    // Shared memory for data transfer from native streamer
    private readonly string _sharedMemoryName;
    private SharedMemoryConsumer? _shmConsumer;

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
    public bool IsDisposed => _isDisposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="Restream"/> class.
    /// </summary>
    /// <param name="appHost">Instance of the <see cref="IServerApplicationHost"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{Restream}"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="mediaSource">The media which must be restreamed.</param>
    /// <param name="urls">The list of URLs to use for streaming (C++ handles failover/selection).</param>
    /// <param name="initialScores">Initial health scores for each URL (same order as urls). C++ manages scores after setup.</param>
    /// <param name="discordService">Optional Discord notification service.</param>
    /// <param name="streamOpenTimeoutMs">Maximum time for stream connection (default 15000ms).</param>
    /// <param name="firstByteTimeoutMs">Maximum time to wait for first data (default 15000ms).</param>
    public Restream(
        IServerApplicationHost appHost,
        ILogger<Restream> logger,
        ILoggerFactory loggerFactory,
        MediaSourceInfo mediaSource,
        IReadOnlyList<string> urls,
        IReadOnlyList<double>? initialScores = null,
        IDiscordNotificationService? discordService = null,
        int streamOpenTimeoutMs = DefaultStreamOpenTimeoutMs,
        int firstByteTimeoutMs = DefaultFirstByteTimeoutMs
    )
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _discordService = discordService;
        _urls = urls;
        MediaSource = mediaSource;
        _streamOpenTimeoutMs = streamOpenTimeoutMs;
        _firstByteTimeoutMs = firstByteTimeoutMs;
        _tokenSource = new CancellationTokenSource();
        _streamQuality = DetectStreamQuality(mediaSource);
        var bufferSize = GetBufferSize(_streamQuality);

        _buffer = new CircularBufferWriteStream(bufferSize, _loggerFactory);

        // Cache buffer threshold settings from plugin configuration for use in hot paths
        var pluginConfig = GetPluginConfiguration();
        _bufferUnderrunThresholdPercent = pluginConfig?.BufferUnderrunThresholdPercent ?? 10.0;
        _bufferNearFullThresholdPercent = pluginConfig?.BufferNearFullThresholdPercent ?? 90.0;
        _bufferUnderrunNotificationThreshold = pluginConfig?.BufferUnderrunNotificationThreshold ?? 5;
        _consumerDisconnectGraceSeconds = pluginConfig?.ConsumerDisconnectGraceSeconds ?? 5;

        _logger.PluginLogInformation(
            "Initialized stream {StreamId} ({Quality}) with automatic quality-based buffering ({BufferSizeMB}MB buffer)",
            mediaSource.Id,
            _streamQuality,
            (double)bufferSize / 1048576.0
        );
        OriginalStreamId = MediaSource.Id;
        UniqueId = Guid.NewGuid().ToString();
        _sharedMemoryName = $"jellyfin_stream_{UniqueId.Replace("-", string.Empty, StringComparison.Ordinal)}";
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

        // Store initial scores for use when creating native streamer
        _initialScores = initialScores;
    }

    private readonly IReadOnlyList<double>? _initialScores;

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
                "All consumers disconnected from channel {ChannelId}. Scheduling cleanup in {GraceSeconds} seconds.",
                MediaSource.Id,
                _consumerDisconnectGraceSeconds
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
                        await Task.Delay(TimeSpan.FromSeconds(_consumerDisconnectGraceSeconds), cancellationToken)
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
        timeoutCts.CancelAfter(_streamOpenTimeoutMs);

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
                _streamOpenTimeoutMs
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
            if (!await WaitForFirstDataAsync(_firstByteTimeoutMs, timeoutCts.Token).ConfigureAwait(false))
            {
                _logger.PluginLogWarning(
                    "Stream {ChannelId} did not produce data within {TimeoutMs}ms first-byte timeout",
                    MediaSource.Id,
                    _firstByteTimeoutMs
                );
                throw new TimeoutException($"Stream did not produce data within {_firstByteTimeoutMs}ms");
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
                _streamOpenTimeoutMs
            );
            throw new TimeoutException($"Stream connection timed out after {_streamOpenTimeoutMs}ms");
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
    /// The native C++ code handles HTTP connections, redirects, retry/backoff, stall detection,
    /// packet alignment, restamping, failover/rotation, and outcome recording.
    /// Data is transferred via shared memory for high-performance zero-copy IPC.
    /// </summary>
    private async Task BroadcastFromSourceAsync(CancellationToken cancellationToken)
    {
        var streamerConfig = BuildStreamerConfig();
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
            // Add all URLs with initial health scores
            // C++ handles ongoing health tracking and URL selection
            for (int i = 0; i < _urls.Count; i++)
            {
                var url = _urls[i];
                var healthScore = _initialScores != null && i < _initialScores.Count ? _initialScores[i] : 50.0; // Neutral score for unknown providers

                _nativeStreamer.AddUrlWithScore(url, healthScore);
            }

            // Apply network configuration (DNS, TCP keepalive, timeouts) from plugin settings
            var networkConfig = BuildNetworkConfig();
            if (networkConfig != null)
            {
                _nativeStreamer.SetNetworkConfig(networkConfig);
            }

            // Configure shared memory output (replaces callback-based data transfer)
            // Slot count 2048 for ~2.5MB buffer, slot size 1316 (7 TS packets)
            _nativeStreamer.SetSharedMemoryOutput(_sharedMemoryName, slotCount: 2048, slotSize: 1316);

            // Set up event callback for stream events (connected, disconnected, switched, etc.)
            _nativeStreamer.SetEventCallback(OnNativeEvent);

            if (!_nativeStreamer.Start())
            {
                _killReason = "Native streamer start failed";
                return;
            }

            _receivingData = true;
            _buffer.SignalSourceConnected();

            // Connect to shared memory and read data in a loop
            await ConnectAndReadSharedMemoryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _receivingData = false;

            // Capture local reference: Dispose() may set _nativeStreamer to null concurrently
            var streamer = _nativeStreamer;
            if (streamer != null)
            {
                streamer.SetEventCallback(callback: null);
                streamer.Stop();
            }

            _shmConsumer?.Dispose();
            _shmConsumer = null;
            _buffer.SignalSourceDisconnected();
        }
    }

    /// <summary>
    /// Builds a <see cref="TsDuckStreamerConfigNative"/> from the plugin configuration,
    /// mapping user-configured timeouts and resilience settings to native struct fields.
    /// Falls back to <see cref="TsDuckStreamerConfigNative.Default"/> values when the
    /// plugin instance is not available.
    /// </summary>
    /// <returns>A configured native streamer configuration struct.</returns>
    private static TsDuckStreamerConfigNative BuildStreamerConfig()
    {
        var streamerConfig = TsDuckStreamerConfigNative.Default;
        var pluginConfig = GetPluginConfiguration();

        if (pluginConfig == null)
        {
            return streamerConfig;
        }

        // Map user-configured timeouts (seconds to milliseconds)
        streamerConfig.ConnectTimeoutMs = pluginConfig.StreamConnectTimeoutSeconds * 1000;
        streamerConfig.ResponseTimeoutMs = pluginConfig.StreamResponseHeadersTimeoutSeconds * 1000;
        streamerConfig.StallTimeoutMs = pluginConfig.StreamDataStallTimeoutSeconds * 1000;

        // Map resilience settings
        streamerConfig.MaxRetries = pluginConfig.MaxFailoverAttempts;
        streamerConfig.QuarantineDurationMs = pluginConfig.ProviderBlacklistSeconds * 1000;

        // Map load balancer settings
        streamerConfig.EnableP2C = pluginConfig.EnableP2CLoadBalancing ? 1 : 0;
        streamerConfig.EnableOutlierDetection = pluginConfig.EnableOutlierDetection ? 1 : 0;
        streamerConfig.OutlierStddevFactor = pluginConfig.OutlierStddevFactor;
        streamerConfig.ProbationSuccessThreshold = pluginConfig.ProbationSuccessThreshold;

        return streamerConfig;
    }

    /// <summary>
    /// Builds a <see cref="NetworkConfig"/> from the plugin configuration,
    /// mapping user-configured network and timeout settings to the native network layer.
    /// </summary>
    /// <returns>A configured network configuration, or null if plugin is unavailable.</returns>
    private static NetworkConfig? BuildNetworkConfig()
    {
        var pluginConfig = GetPluginConfiguration();
        if (pluginConfig == null)
        {
            return null;
        }

        var networkConfig = new NetworkConfig
        {
            TcpConnectTimeoutMs = pluginConfig.StreamConnectTimeoutSeconds * 1000,
            FirstByteTimeoutMs = pluginConfig.StreamFirstByteTimeoutSeconds * 1000,
            DnsTimeoutMs = pluginConfig.DnsTimeoutSeconds * 1000,
            TcpKeepaliveEnabled = pluginConfig.TcpKeepaliveEnabled,
        };

        return networkConfig;
    }

    /// <summary>
    /// Safely retrieves the plugin configuration, returning null if the plugin is not yet initialized.
    /// This allows Restream to be used in test environments where the plugin may not be registered.
    /// </summary>
    /// <returns>The current plugin configuration, or null if unavailable.</returns>
    private static PluginConfiguration? GetPluginConfiguration()
    {
        try
        {
            return Plugin.Instance.Configuration;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Connects to shared memory and reads data in a loop, writing to the circular buffer.
    /// Handles discontinuity, overflow, end-of-stream, and error flags from the producer.
    /// Uses exponential backoff for initial shared memory connection.
    /// </summary>
    private async Task ConnectAndReadSharedMemoryAsync(CancellationToken cancellationToken)
    {
        // Wait for producer to initialize shared memory with exponential backoff.
        // The C++ worker creates the /dev/shm file on startup, but thread scheduling
        // can delay this. Retry avoids the race condition where C# opens before C++ creates.
        const int maxRetries = 6;
        var retryDelayMs = 100;
        var connected = false;

        for (int attempt = 0; attempt < maxRetries && !cancellationToken.IsCancellationRequested; attempt++)
        {
            await Task.Delay(retryDelayMs, cancellationToken).ConfigureAwait(false);

            try
            {
                _shmConsumer?.Dispose();
                _shmConsumer = new SharedMemoryConsumer(_sharedMemoryName);
                connected = true;
                break;
            }
            catch (FileNotFoundException) when (attempt < maxRetries - 1)
            {
                _logger.LogDebugIfEnabled(
                    "Shared memory not ready (attempt {Attempt}/{Max}), retrying in {Delay}ms",
                    attempt + 1,
                    maxRetries,
                    retryDelayMs
                );
                retryDelayMs = Math.Min(retryDelayMs * 2, 1600);
            }
            catch (Exception ex)
            {
                _logger.PluginLogError(ex, "Failed to connect to shared memory: {Name}", _sharedMemoryName);
                _killReason = "Shared memory connection failed";
                return;
            }
        }

        if (!connected)
        {
            _logger.PluginLogWarning(
                "Shared memory not available after {Max} retries for channel {ChannelId}: {Name}",
                maxRetries,
                MediaSource.Id,
                _sharedMemoryName
            );
            _killReason = "Shared memory connection failed after retries";
            return;
        }

        _logger.LogDebugIfEnabled("Connected to shared memory: {Name}", _sharedMemoryName);

        var consumer = _shmConsumer;
        if (consumer == null)
        {
            _killReason = "Shared memory consumer unexpectedly null";
            return;
        }

        var lastHealthCheckTime = DateTime.UtcNow;
        var lastDataTime = DateTime.UtcNow;
        const int noDataTimeoutSeconds = 30;

        while (!cancellationToken.IsCancellationRequested && _receivingData)
        {
            // Wait for data with timeout
            if (!consumer.WaitForData(TimeSpan.FromMilliseconds(100), cancellationToken))
            {
                // Check for terminal conditions
                if (consumer.IsEndOfStream && consumer.AvailableBytes == 0)
                {
                    _logger.PluginLogInformation("Shared memory end of stream for {ChannelId}", MediaSource.Id);
                    _killReason = "Stream ended";
                    break;
                }

                if (consumer.HasError)
                {
                    _logger.PluginLogWarning(
                        "Shared memory error {Code}: {Message}",
                        consumer.ErrorCode,
                        consumer.ErrorMessage
                    );
                    _killReason = $"Shared memory error: {consumer.ErrorCode}";
                    break;
                }

                // No-data timeout: if no data arrives for 30s, the stream is dead
                if ((DateTime.UtcNow - lastDataTime).TotalSeconds >= noDataTimeoutSeconds)
                {
                    _logger.PluginLogWarning(
                        "No data from shared memory for {Timeout}s for channel {ChannelId}",
                        noDataTimeoutSeconds,
                        MediaSource.Id
                    );
                    _killReason = "No data timeout";
                    break;
                }

                continue;
            }

            // Data received - reset no-data timer
            lastDataTime = DateTime.UtcNow;

            // Handle discontinuity (URL switch in native streamer)
            if (consumer.ConsumeDiscontinuity())
            {
                _logger.LogDebugIfEnabled("Discontinuity detected for {ChannelId}", MediaSource.Id);
                _buffer.MarkDiscontinuityAligned();
            }

            // Handle overflow (slow consumer - data was dropped)
            if (consumer.ConsumeOverflow())
            {
                _logger.PluginLogWarning("Shared memory overflow for {ChannelId}", MediaSource.Id);
            }

            // Read directly from shared memory to circular buffer (zero-copy)
            consumer.ReadTo(_buffer);

            // Periodic health check
            var now = DateTime.UtcNow;
            if ((now - lastHealthCheckTime).TotalSeconds >= HealthCheckIntervalSeconds)
            {
                lastHealthCheckTime = now;
                PerformHealthCheck();
            }
        }
    }

    /// <summary>
    /// Callback invoked by the native streamer when an event occurs.
    /// This is called from the native worker thread.
    /// </summary>
    /// <param name="eventType">The type of event.</param>
    /// <param name="detail">Event-specific detail.</param>
    private void OnNativeEvent(StreamerEvent eventType, int detail)
    {
        if (_isDisposed)
        {
            return;
        }

        switch (eventType)
        {
            case StreamerEvent.Connected:
                _logger.LogDebugIfEnabled("Native streamer connected for channel {ChannelId}", MediaSource.Id);
                break;

            case StreamerEvent.Disconnected:
                _logger.LogDebugIfEnabled("Native streamer disconnected for channel {ChannelId}", MediaSource.Id);
                break;

            case StreamerEvent.Switched:
                _logger.PluginLogInformation(
                    "Native streamer switched to URL index {UrlIndex} for channel {ChannelId}",
                    detail,
                    MediaSource.Id
                );
                // Note: Discontinuity is handled via shared memory flag in ConnectAndReadSharedMemoryAsync
                break;

            case StreamerEvent.Stalled:
                _logger.PluginLogWarning("Native streamer stalled for channel {ChannelId}", MediaSource.Id);
                break;

            case StreamerEvent.Error:
                _logger.PluginLogWarning(
                    "Native streamer error (code={ErrorCode}) for channel {ChannelId}",
                    detail,
                    MediaSource.Id
                );
                break;

            case StreamerEvent.Stopped:
                _logger.LogDebugIfEnabled("Native streamer stopped for channel {ChannelId}", MediaSource.Id);
                _killReason = "Stream stopped";
                break;

            case StreamerEvent.QualityDegraded:
                _logger.PluginLogWarning("Quality degraded for channel {ChannelId}, switching URL", MediaSource.Id);
                break;
        }
    }

    /// <summary>
    /// Performs periodic health checks on the stream.
    /// </summary>
    private void PerformHealthCheck()
    {
        if (!TryGetStreamerStatus(out var status))
        {
            return;
        }

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

        // Buffer health checks
        var bufferFillPct =
            (double)(_buffer.TotalBytesWritten % _buffer.BufferSize) * 100.0 / (double)_buffer.BufferSize;

        if (bufferFillPct < _bufferUnderrunThresholdPercent)
        {
            _bufferUnderrunCount++;

            if (_bufferUnderrunCount % 3 == 1)
            {
                _logger.PluginLogWarning(
                    "Buffer underrun #{Count} for channel {ChannelId}. Only {FillPct:F1}% filled.",
                    _bufferUnderrunCount,
                    MediaSource.Id,
                    bufferFillPct
                );

                if (_bufferUnderrunCount >= _bufferUnderrunNotificationThreshold)
                {
                    var fillPct = bufferFillPct;
                    var count = _bufferUnderrunCount;
                    _discordService.SendFireAndForget(svc =>
                        svc.NotifyBufferHealthIssueAsync(
                            MediaSource.Id,
                            MediaSource.Name ?? "Unknown",
                            count,
                            fillPct,
                            0.0
                        )
                    );
                }
            }
        }
        else if (bufferFillPct > _bufferNearFullThresholdPercent)
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
        _logger.LogDebugIfEnabled(
            "Broadcast progress for channel {ChannelId}: {TotalMB} MB written, {Consumers} consumers, buffer {FillPct:F1}%",
            MediaSource.Id,
            _buffer.TotalBytesWritten / 1048576,
            ConsumerCount,
            bufferFillPct
        );
    }

    /// <summary>
    /// Safely retrieves the current streamer status in a thread-safe manner.
    /// </summary>
    /// <param name="status">The streamer status if available.</param>
    /// <returns>True if status was retrieved, false if streamer is null or disposed.</returns>
    private bool TryGetStreamerStatus(out StreamerStatus status)
    {
        var streamer = _nativeStreamer;
        if (streamer == null)
        {
            status = default;
            return false;
        }

        status = streamer.GetStatus();
        return true;
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

            // Wait for broadcast task to complete so its finally block runs
            // before we dispose the resources it uses. Use a timeout to avoid
            // hanging if the C++ worker is stuck in a long curl operation.
            if (_broadcastTask != null)
            {
                try
                {
                    if (!_broadcastTask.Wait(TimeSpan.FromSeconds(5)))
                    {
                        _logger.PluginLogWarning(
                            "Broadcast task did not complete within 5s for channel {ChannelId}",
                            MediaSource.Id
                        );
                    }
                }
                catch (AggregateException)
                {
                    // Expected: broadcast was cancelled or errored
                }

                _broadcastTask = null;
            }

            _tokenSource.Dispose();
        }

        _buffer.Dispose();
        _shmConsumer?.Dispose();
        _shmConsumer = null;
        Interlocked.Exchange(ref _nativeStreamer, value: null)?.Dispose();
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

                // Query native streamer for real-time status and quality metrics
                var nativeStreamer = stream._nativeStreamer;
                var streamerStatus = nativeStreamer?.GetStatus();
                var metrics = nativeStreamer?.GetMetrics();
                var avSync = nativeStreamer?.GetAvSyncAnalysis();
                var providerCount = nativeStreamer?.GetProviderCount() ?? 0;

                // Populate quality metrics from native analyzer
                long packetErrors = 0L;
                long continuityErrors = 0L;
                long syncErrors = 0L;
                long patViolations = 0L;
                long crcErrors = 0L;
                long tsBitrate = 0L;

                if (metrics != null)
                {
                    packetErrors = metrics.Priority2.TransportError;
                    continuityErrors = metrics.Priority1.ContinuityCountError;
                    syncErrors = metrics.Priority1.SyncByteError + metrics.Priority1.SyncLoss;
                    patViolations = metrics.Priority1.PatError + metrics.Priority1.PatError2;
                    crcErrors = metrics.Priority2.CrcError;
                    tsBitrate = metrics.TsBitrate;
                }

                double avDriftMs = avSync?.VideoAudioDriftMs ?? 0.0;
                string syncStatus = avSync?.Status.ToString() ?? "Unknown";
                bool hasQualityIssues = packetErrors > 0 || continuityErrors > 10 || syncErrors > 0;
                string qualityLevel = hasQualityIssues
                    ? (syncErrors > 0 || packetErrors > 100 ? "Critical" : "Warning")
                    : "None";

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
                        // Quality metrics from native analyzer
                        PacketErrors = packetErrors,
                        ContinuityErrors = continuityErrors,
                        SyncErrors = syncErrors,
                        PatViolations = patViolations,
                        CrcErrors = crcErrors,
                        AvDriftMs = avDriftMs,
                        SyncStatus = syncStatus,
                        HasQualityIssues = hasQualityIssues,
                        QualityLevel = qualityLevel,
                        QualityIssues = hasQualityIssues
                            ? $"TEI:{packetErrors} CC:{continuityErrors} Sync:{syncErrors}"
                            : null,
                        // Native streamer status
                        StreamerState = streamerStatus?.State.ToString() ?? "Unknown",
                        CurrentUrlIndex = streamerStatus?.CurrentUrlIndex ?? 0,
                        UrlCount = streamerStatus?.UrlCount ?? 0,
                        BytesReceived = streamerStatus?.BytesReceived ?? 0L,
                        PacketsOutput = streamerStatus?.PacketsOutput ?? 0L,
                        SwitchesCompleted = streamerStatus?.SwitchesCompleted ?? 0L,
                        QualitySwitches = streamerStatus?.QualitySwitches ?? 0L,
                        TsBitrate = tsBitrate,
                        LastHttpStatus = streamerStatus?.LastHttpStatus ?? 0,
                        LastCurlError = streamerStatus?.LastCurlError ?? 0,
                        ProviderCount = providerCount,
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
    /// Gets the provider health snapshot for a specific provider in a stream.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <param name="providerIndex">The provider index (0-based).</param>
    /// <returns>The health snapshot, or null if not found.</returns>
    public static Streaming.Native.ProviderHealthSnapshot? GetProviderHealth(string streamId, int providerIndex)
    {
        if (!_activeStreams.TryGetValue(streamId, out var stream))
        {
            return null;
        }

        return stream._nativeStreamer?.GetProviderHealth(providerIndex);
    }

    /// <summary>
    /// Gets all provider health snapshots for a stream.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <returns>List of provider health snapshots, or null if stream not found.</returns>
    public static IReadOnlyList<Streaming.Native.ProviderHealthSnapshot>? GetAllProviderHealth(string streamId)
    {
        if (!_activeStreams.TryGetValue(streamId, out var stream))
        {
            return null;
        }

        var nativeStreamer = stream._nativeStreamer;
        if (nativeStreamer == null)
        {
            return null;
        }

        var count = nativeStreamer.GetProviderCount();
        var results = new List<Streaming.Native.ProviderHealthSnapshot>(count);
        for (int i = 0; i < count; i++)
        {
            var health = nativeStreamer.GetProviderHealth(i);
            if (health != null)
            {
                results.Add(health.Value);
            }
        }

        return results;
    }

    /// <summary>
    /// Requests a URL switch (force reconnect) for a stream.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <returns>True if the switch was requested, false if stream not found.</returns>
    public static bool RequestSwitch(string streamId)
    {
        if (!_activeStreams.TryGetValue(streamId, out var stream))
        {
            return false;
        }

        stream._nativeStreamer?.RequestSwitch();
        return true;
    }

    /// <summary>
    /// Gets detailed TR 101 290 metrics for a stream.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <returns>The metrics, or null if not available.</returns>
    public static Streaming.Native.TsDuckMetrics? GetStreamMetrics(string streamId)
    {
        if (!_activeStreams.TryGetValue(streamId, out var stream))
        {
            return null;
        }

        return stream._nativeStreamer?.GetMetrics();
    }

    /// <summary>
    /// Gets A/V sync analysis for a stream.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <returns>The A/V sync analysis, or null if not available.</returns>
    public static Streaming.Native.AvSyncAnalysis? GetStreamAvSync(string streamId)
    {
        if (!_activeStreams.TryGetValue(streamId, out var stream))
        {
            return null;
        }

        return stream._nativeStreamer?.GetAvSyncAnalysis();
    }

    /// <summary>
    /// Gets PCR analysis for a stream.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <returns>The PCR analysis, or null if not available.</returns>
    public static Streaming.Native.PcrAnalysis? GetStreamPcrAnalysis(string streamId)
    {
        if (!_activeStreams.TryGetValue(streamId, out var stream))
        {
            return null;
        }

        return stream._nativeStreamer?.GetPcrAnalysis();
    }

    /// <summary>
    /// Force ejects a provider from a stream's health system.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <param name="providerIndex">The provider index (0-based).</param>
    /// <param name="durationMs">Ejection duration in milliseconds.</param>
    /// <returns>True if the provider was ejected, false if stream not found.</returns>
    public static bool ForceEjectProvider(string streamId, int providerIndex, int durationMs)
    {
        if (!_activeStreams.TryGetValue(streamId, out var stream))
        {
            return false;
        }

        stream._nativeStreamer?.ForceEjectProvider(providerIndex, durationMs);
        return true;
    }

    /// <summary>
    /// Resets all providers in a stream to Active state.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <returns>True if reset, false if stream not found.</returns>
    public static bool ResetAllProviders(string streamId)
    {
        if (!_activeStreams.TryGetValue(streamId, out var stream))
        {
            return false;
        }

        stream._nativeStreamer?.ResetAllProviders();
        return true;
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
