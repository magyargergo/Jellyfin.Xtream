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
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Service.Streaming.Native;
using Jellyfin.Xtream.Utility;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// A live stream implementation that broadcasts a single IPTV source to multiple consumers.
/// Uses a circular buffer for multi-reader support with a single HTTP connection.
/// </summary>
/// <remarks>
/// <para>
/// This is a thin orchestrator that:
/// 1. Sets up providers with URLs and initial health scores
/// 2. Delegates shared memory reading to <see cref="RestreamSharedMemoryCoordinator"/>
/// 3. Delegates health monitoring to <see cref="RestreamHealthMonitor"/>
/// 4. Delegates stream registry operations to <see cref="RestreamActiveStreamRegistry"/>
/// 5. Serves bytes to Jellyfin consumers
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

    // Timeout defaults (can be overridden via constructor)
    private const int DefaultStreamOpenTimeoutMs = 15000;
    private const int DefaultFirstByteTimeoutMs = 15000;

    // Cleanup and timing constants
    private const int FirstBytePollIntervalMs = 50;

    // Data flow synchronization
    private volatile bool _receivingData;

    private ILogger<Restream> _logger = null!;
    private ILoggerFactory _loggerFactory = null!;
    private IDiscordNotificationService? _discordService;
    private readonly IReadOnlyList<string> _urls;
    private readonly string _sourceUrl;
    private readonly SemaphoreSlim _openLock = new(1, 1);
    private CircularBufferWriteStream _buffer = null!;
    private RefCountedResourcePool<CircularBufferReadStream> _readerPool = null!;
    private string _streamQuality = null!;
    private readonly DateTime _startTime = DateTime.UtcNow;
    private int _streamOpenTimeoutMs;
    private int _firstByteTimeoutMs;
    private int _consumerDisconnectGraceSeconds;
    private readonly IReadOnlyList<double>? _initialScores;

    // Extracted collaborators
    private RestreamHealthMonitor _healthMonitor = null!;
    private string _sharedMemoryName = null!;
    private RestreamSharedMemoryCoordinator? _shmCoordinator;

    private CancellationTokenSource _tokenSource = null!;
    private Task? _broadcastTask;
    private int _consumerCount;
    private volatile bool _isDisposed;
    private string? _killReason;
    private CancellationTokenSource? _cleanupCts;

    // Native HTTP streamer with failover and mid-stream switching
    private NativeStreamer? _nativeStreamer;

    // Registry-based streaming (Phase 4: single source of truth)
    private readonly NativeChannelRegistry? _registry;
    private readonly Guid _channelGuid;

    /// <inheritdoc />
    public int ConsumerCount
    {
        get => Interlocked.CompareExchange(ref _consumerCount, 0, 0);
        set => Interlocked.Exchange(ref _consumerCount, value);
    }

    /// <inheritdoc />
    public string OriginalStreamId { get; set; } = null!;

    /// <inheritdoc />
    public string TunerHostId => TunerHost;

    /// <inheritdoc />
    public bool EnableStreamSharing => true;

    /// <inheritdoc />
    public MediaSourceInfo MediaSource { get; set; } = null!;

    /// <inheritdoc />
    public string UniqueId { get; private set; } = null!;

    /// <summary>
    /// Gets a value indicating whether this stream has been disposed.
    /// Used to detect stale stream references held by Jellyfin.
    /// </summary>
    public bool IsDisposed => _isDisposed;

    /// <summary>
    /// Gets the native streamer instance for use by <see cref="RestreamActiveStreamRegistry"/>.
    /// </summary>
    internal NativeStreamer? NativeStreamer => _nativeStreamer;

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
        _urls = urls;
        _initialScores = initialScores;
        _sourceUrl = _urls.Count > 0 ? _urls[0] : "unknown";
        InitializeCommon(
            appHost,
            logger,
            loggerFactory,
            mediaSource,
            discordService,
            streamOpenTimeoutMs,
            firstByteTimeoutMs
        );

        _logger.PluginLogInformation(
            "Initialized stream {StreamId} ({Quality}) with automatic quality-based buffering ({BufferSizeMB}MB buffer)",
            mediaSource.Id,
            _streamQuality,
            (double)_buffer.BufferSize / 1048576.0
        );
        _logger.LogDebugIfEnabled(
            "Registered Restream {StreamId} (total active: {Count})",
            MediaSource.Id,
            RestreamActiveStreamRegistry.GetActiveStreamCount()
        );
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Restream"/> class using a native channel registry.
    /// The registry provides URL lookup, health-based provider selection, and failover.
    /// </summary>
    /// <param name="appHost">Instance of the <see cref="IServerApplicationHost"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{Restream}"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="mediaSource">The media which must be restreamed.</param>
    /// <param name="registry">The native channel registry (must be built).</param>
    /// <param name="channelGuid">The channel GUID to stream.</param>
    /// <param name="discordService">Optional Discord notification service.</param>
    /// <param name="streamOpenTimeoutMs">Maximum time for stream connection (default 15000ms).</param>
    /// <param name="firstByteTimeoutMs">Maximum time to wait for first data (default 15000ms).</param>
    public Restream(
        IServerApplicationHost appHost,
        ILogger<Restream> logger,
        ILoggerFactory loggerFactory,
        MediaSourceInfo mediaSource,
        NativeChannelRegistry registry,
        Guid channelGuid,
        IDiscordNotificationService? discordService = null,
        int streamOpenTimeoutMs = DefaultStreamOpenTimeoutMs,
        int firstByteTimeoutMs = DefaultFirstByteTimeoutMs
    )
    {
        _registry = registry;
        _channelGuid = channelGuid;
        _urls = [];
        _sourceUrl = $"registry:{channelGuid}";
        InitializeCommon(
            appHost,
            logger,
            loggerFactory,
            mediaSource,
            discordService,
            streamOpenTimeoutMs,
            firstByteTimeoutMs
        );

        _logger.PluginLogInformation(
            "Initialized registry-based stream for {StreamId} ({Quality}, GUID: {Guid}) with {BufferSizeMB}MB buffer",
            mediaSource.Id,
            _streamQuality,
            channelGuid,
            (double)_buffer.BufferSize / 1048576.0
        );
    }

#pragma warning disable IDISP003 // Fields are only assigned once from constructors via this method
    private void InitializeCommon(
        IServerApplicationHost appHost,
        ILogger<Restream> logger,
        ILoggerFactory loggerFactory,
        MediaSourceInfo mediaSource,
        IDiscordNotificationService? discordService,
        int streamOpenTimeoutMs,
        int firstByteTimeoutMs
    )
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _discordService = discordService;
        MediaSource = mediaSource;
        _streamOpenTimeoutMs = streamOpenTimeoutMs;
        _firstByteTimeoutMs = firstByteTimeoutMs;
        _tokenSource = new CancellationTokenSource();
        _streamQuality = RestreamConfiguration.DetectStreamQuality(mediaSource);
        var bufferSize = RestreamConfiguration.GetBufferSize(_streamQuality);

        _buffer = new CircularBufferWriteStream(bufferSize, _loggerFactory);

        var pluginConfig = RestreamConfiguration.GetPluginConfiguration();
        _consumerDisconnectGraceSeconds = pluginConfig?.ConsumerDisconnectGraceSeconds ?? 5;

        _healthMonitor = new RestreamHealthMonitor(
            _logger,
            _discordService,
            pluginConfig?.BufferUnderrunThresholdPercent ?? 10.0,
            pluginConfig?.BufferNearFullThresholdPercent ?? 90.0,
            pluginConfig?.BufferUnderrunNotificationThreshold ?? 5
        );

        OriginalStreamId = MediaSource.Id;
        UniqueId = Guid.NewGuid().ToString();
        _sharedMemoryName = $"jellyfin_stream_{UniqueId.Replace("-", string.Empty, StringComparison.Ordinal)}";
        var path = "/LiveTv/LiveStreamFiles/" + UniqueId + "/stream.ts";
        MediaSource.Path = appHost.GetSmartApiUrl(IPAddress.Any) + path;
        MediaSource.EncoderPath = appHost.GetApiUrlForLocalAccess() + path;
        MediaSource.Protocol = MediaProtocol.Http;
        _readerPool = new RefCountedResourcePool<CircularBufferReadStream>(CreateReaderStream, OnConsumerCountChanged);
        _ = RestreamActiveStreamRegistry.Register(MediaSource.Id, this);
    }
#pragma warning restore IDISP003

    /// <summary>
    /// Finalizes an instance of the <see cref="Restream"/> class.
    /// Ensures cleanup of static dictionary entry if Dispose() is not called.
    /// </summary>
    ~Restream()
    {
        Dispose(disposing: false);
    }

    /// <summary>
    /// Sets the kill reason for this stream. Used by <see cref="RestreamActiveStreamRegistry"/>
    /// when a stream is killed via the management API.
    /// </summary>
    /// <param name="reason">The reason the stream is being killed.</param>
    internal void SetKillReason(string reason)
    {
        _killReason = reason;
    }

    /// <summary>
    /// Creates a <see cref="StreamInfoSnapshot"/> for the current stream state.
    /// Used by <see cref="RestreamActiveStreamRegistry"/> for API/UI reporting.
    /// </summary>
    /// <returns>A snapshot of the current stream state.</returns>
    internal StreamInfoSnapshot CreateSnapshot()
    {
        long bufferSize = _buffer.BufferSize;
        var totalWritten = _buffer.TotalBytesWritten;
        var currentPosition = totalWritten % bufferSize;
        var fillPct = bufferSize > 0 ? (double)currentPosition * 100.0 / (double)bufferSize : 0.0;
        var hasWrapped = totalWritten >= bufferSize;
        string status;

        if (_broadcastTask == null)
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
        var reconnectionCount = _buffer.DiscontinuityCount;
        var lastDiscontinuityOffset = _buffer.LastDiscontinuityOffset;
        var lastDiscontinuityTime = _buffer.LastDiscontinuityTime;
        double? secondsSinceLastReconnection = lastDiscontinuityTime.HasValue
            ? (DateTime.UtcNow - lastDiscontinuityTime.Value).TotalSeconds
            : null;

        // Query native streamer for real-time status and quality metrics
        var nativeStreamer = _nativeStreamer;
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

        return new StreamInfoSnapshot
        {
            StreamId = MediaSource.Id,
            ChannelName = MediaSource.Name ?? "Unknown",
            StartTime = _startTime,
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
            QualityIssues = hasQualityIssues ? $"TEI:{packetErrors} CC:{continuityErrors} Sync:{syncErrors}" : null,
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
        };
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
                _logger.PluginLogInformation(
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

            _logger.PluginLogInformation(
                "Starting broadcast for channel {ChannelId} from URL: {Url} (timeout: {TimeoutMs}ms)",
                MediaSource.Id,
                _sourceUrl,
                _streamOpenTimeoutMs
            );
            _buffer.Reset();

            if (_tokenSource.IsCancellationRequested)
            {
                _tokenSource.Dispose();
                _tokenSource = new CancellationTokenSource();
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

            _logger.PluginLogInformation(
                "Broadcast started for channel {ChannelId} (first data received)",
                MediaSource.Id
            );

            // Query native streamer for cached PAT/PMT/SPS/PPS init packets (no PTS, avoids A/V desync)
            var initData = _nativeStreamer?.GetInitPackets();
            if (initData != null)
            {
                _buffer.SetInitializationData(initData);
                _logger.PluginLogInformation(
                    "Init packets cached for channel {ChannelId} ({ByteCount} bytes)",
                    MediaSource.Id,
                    initData.Length
                );
            }

            _discordService.SendFireAndForget(svc =>
                svc.NotifyStreamStartAsync(MediaSource.Id, MediaSource.Name ?? "Unknown Channel")
            );

            Events.PluginEventBus.Instance.Publish(
                "stream.started",
                MediaSource.Id,
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["channelName"] = MediaSource.Name ?? "Unknown Channel",
                }
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
                _logger.PluginLogInformation(
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
        _logger.PluginLogWarning(
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
        var streamerConfig = RestreamConfiguration.BuildStreamerConfig();
        var analyzerConfig = TsDuckConfigNative.FromManaged(TsDuckConfiguration.Default);

        _nativeStreamer?.Dispose();
        _nativeStreamer = NativeStreamer.TryCreate(streamerConfig, analyzerConfig, _logger);

        if (_nativeStreamer == null)
        {
            _logger.PluginLogWarning(
                "Native streamer unavailable for channel {ChannelId} - native library not loaded",
                MediaSource.Id
            );
            _killReason = "Native streamer unavailable";
            return;
        }

        _shmCoordinator?.Dispose();
        _shmCoordinator = new RestreamSharedMemoryCoordinator(
            _logger,
            _sharedMemoryName,
            MediaSource.Id,
            MediaSource.Name ?? "Unknown",
            _buffer,
            _healthMonitor,
            () => ConsumerCount
        );

        try
        {
            ConfigureNativeStreamer();

            if (!_nativeStreamer.Start())
            {
                _logger.PluginLogWarning(
                    "Native streamer Start() returned false for channel {ChannelId}",
                    MediaSource.Id
                );
                _killReason = "Native streamer start failed";
                return;
            }

            _receivingData = true;
            _buffer.SignalSourceConnected();

            // Connect to shared memory and read data in a loop
            await _shmCoordinator
                .ConnectAndReadAsync(
                    _nativeStreamer,
                    () => _receivingData,
                    reason => _killReason = reason,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        finally
        {
            _receivingData = false;
            _logger.PluginLogInformation(
                "Broadcast task ending for channel {ChannelId}, reason: {KillReason}",
                MediaSource.Id,
                _killReason ?? "normal exit"
            );

            // Capture local reference: Dispose() may set _nativeStreamer to null concurrently
            var streamer = _nativeStreamer;
            if (streamer != null)
            {
                streamer.SetEventCallback(callback: null);
                streamer.Stop();
            }

            _shmCoordinator?.Dispose();
            _shmCoordinator = null;
            _buffer.SignalSourceDisconnected();
        }
    }

    /// <summary>
    /// Configures the native streamer with URLs, registry, network settings, shared memory,
    /// and the event callback.
    /// </summary>
    private void ConfigureNativeStreamer()
    {
        var streamer = _nativeStreamer!;

        // Registry-based path: C++ uses registry for URL lookup + health-based provider selection
        if (_registry != null)
        {
            streamer.SetRegistry(_registry);
            streamer.SetChannelGuid(_channelGuid);
        }
        else
        {
            // Legacy path: Add all URLs with initial health scores
            // C++ handles ongoing health tracking and URL selection
            for (int i = 0; i < _urls.Count; i++)
            {
                var url = _urls[i];
                var healthScore = _initialScores != null && i < _initialScores.Count ? _initialScores[i] : 50.0; // Neutral score for unknown providers

                streamer.AddUrlWithScore(url, healthScore);
            }
        }

        // Apply network configuration (DNS, TCP keepalive, timeouts) from plugin settings
        var networkConfig = RestreamConfiguration.BuildNetworkConfig();
        if (networkConfig != null)
        {
            streamer.SetNetworkConfig(networkConfig);
        }

        // Configure shared memory output (replaces callback-based data transfer)
        // Slot count 2048 for ~2.5MB buffer, slot size 1316 (7 TS packets)
        streamer.SetSharedMemoryOutput(_sharedMemoryName, slotCount: 2048, slotSize: 1316);

        // Set up event callback for stream events (connected, disconnected, switched, etc.)
        streamer.SetEventCallback(OnNativeEvent);
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
                _logger.PluginLogInformation("Native streamer connected for channel {ChannelId}", MediaSource.Id);
                break;

            case StreamerEvent.Disconnected:
                _logger.PluginLogWarning("Native streamer disconnected for channel {ChannelId}", MediaSource.Id);
                break;

            case StreamerEvent.Switched:
                _logger.PluginLogInformation(
                    "Native streamer switched to URL index {UrlIndex} for channel {ChannelId}",
                    detail,
                    MediaSource.Id
                );
                // Note: Discontinuity is handled via shared memory flag in RestreamSharedMemoryCoordinator
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
                _logger.PluginLogWarning("Native streamer stopped for channel {ChannelId}", MediaSource.Id);
                _killReason = "Stream stopped";
                break;

            case StreamerEvent.QualityDegraded:
                _logger.PluginLogWarning("Quality degraded for channel {ChannelId}, switching URL", MediaSource.Id);
                break;
        }
    }

    /// <inheritdoc />
    public async Task Close()
    {
        _logger.PluginLogInformation("Closing broadcast for channel {ChannelId}", MediaSource.Id);

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
            catch (OperationCanceledException)
            {
                // Expected: broadcast task was cancelled during Close()
            }
            catch (ObjectDisposedException)
            {
                // Expected: resources were disposed before broadcast task completed
            }

            _broadcastTask = null;
        }

        _logger.PluginLogInformation(
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
        if (disposing && RestreamActiveStreamRegistry.Unregister(MediaSource.Id))
        {
            _logger.PluginLogInformation(
                "Unregistered Restream {StreamId} (remaining active: {Count})",
                MediaSource.Id,
                RestreamActiveStreamRegistry.GetActiveStreamCount()
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

            Events.PluginEventBus.Instance.Publish(
                "stream.stopped",
                MediaSource.Id,
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["channelName"] = MediaSource.Name ?? "Unknown",
                    ["reason"] = reason,
                    ["durationSeconds"] = duration.TotalSeconds,
                    ["bytesTransferred"] = bytesTransferred,
                }
            );
        }
        else if (_discordService != null)
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
        _shmCoordinator?.Dispose();
        _shmCoordinator = null;
        Interlocked.Exchange(ref _nativeStreamer, value: null)?.Dispose();
        _openLock.Dispose();
        _readerPool.Dispose();

        _logger.PluginLogInformation("Restream for channel {ChannelId} disposed", MediaSource.Id);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    // ========================================================================
    // Static API delegates — preserve public surface, forward to registry
    // ========================================================================

    /// <summary>
    /// Gets the count of active Restream instances.
    /// </summary>
    /// <returns>The number of active streams.</returns>
    public static int GetActiveStreamCount() => RestreamActiveStreamRegistry.GetActiveStreamCount();

    /// <summary>
    /// Gets information about all active streams for API/UI purposes.
    /// </summary>
    /// <returns>A list of stream information objects.</returns>
    public static IReadOnlyList<StreamInfoSnapshot> GetActiveStreamSnapshots() =>
        RestreamActiveStreamRegistry.GetActiveStreamSnapshots();

    /// <summary>
    /// Kills (disposes) a stream by its ID.
    /// </summary>
    /// <param name="streamId">The stream ID to kill.</param>
    /// <param name="reason">Optional reason for killing the stream (for notifications).</param>
    /// <returns>True if the stream was found and killed, false otherwise.</returns>
    public static bool KillStream(string streamId, string? reason = null) =>
        RestreamActiveStreamRegistry.KillStream(streamId, reason);

    /// <summary>
    /// Gets the provider health snapshot for a specific provider in a stream.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <param name="providerIndex">The provider index (0-based).</param>
    /// <returns>The health snapshot, or null if not found.</returns>
    public static Streaming.Native.ProviderHealthSnapshot? GetProviderHealth(string streamId, int providerIndex) =>
        RestreamActiveStreamRegistry.GetProviderHealth(streamId, providerIndex);

    /// <summary>
    /// Gets all provider health snapshots for a stream.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <returns>List of provider health snapshots, or null if stream not found.</returns>
    public static IReadOnlyList<Streaming.Native.ProviderHealthSnapshot>? GetAllProviderHealth(string streamId) =>
        RestreamActiveStreamRegistry.GetAllProviderHealth(streamId);

    /// <summary>
    /// Requests a URL switch (force reconnect) for a stream.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <returns>True if the switch was requested, false if stream not found.</returns>
    public static bool RequestSwitch(string streamId) => RestreamActiveStreamRegistry.RequestSwitch(streamId);

    /// <summary>
    /// Gets detailed TR 101 290 metrics for a stream.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <returns>The metrics, or null if not available.</returns>
    public static Streaming.Native.TsDuckMetrics? GetStreamMetrics(string streamId) =>
        RestreamActiveStreamRegistry.GetStreamMetrics(streamId);

    /// <summary>
    /// Gets A/V sync analysis for a stream.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <returns>The A/V sync analysis, or null if not available.</returns>
    public static Streaming.Native.AvSyncAnalysis? GetStreamAvSync(string streamId) =>
        RestreamActiveStreamRegistry.GetStreamAvSync(streamId);

    /// <summary>
    /// Gets PCR analysis for a stream.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <returns>The PCR analysis, or null if not available.</returns>
    public static Streaming.Native.PcrAnalysis? GetStreamPcrAnalysis(string streamId) =>
        RestreamActiveStreamRegistry.GetStreamPcrAnalysis(streamId);

    /// <summary>
    /// Force ejects a provider from a stream's health system.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <param name="providerIndex">The provider index (0-based).</param>
    /// <param name="durationMs">Ejection duration in milliseconds.</param>
    /// <returns>True if the provider was ejected, false if stream not found.</returns>
    public static bool ForceEjectProvider(string streamId, int providerIndex, int durationMs) =>
        RestreamActiveStreamRegistry.ForceEjectProvider(streamId, providerIndex, durationMs);

    /// <summary>
    /// Resets all providers in a stream to Active state.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <returns>True if reset, false if stream not found.</returns>
    public static bool ResetAllProviders(string streamId) => RestreamActiveStreamRegistry.ResetAllProviders(streamId);

    /// <summary>
    /// Kills all active streams.
    /// </summary>
    /// <param name="reason">Optional reason for killing the streams (for notifications).</param>
    /// <returns>The number of streams killed.</returns>
    public static int KillAllStreams(string? reason = null) => RestreamActiveStreamRegistry.KillAllStreams(reason);
}
