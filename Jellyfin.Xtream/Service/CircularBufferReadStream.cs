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
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Service.MpegTs;
using Jellyfin.Xtream.Service.MpegTs.Core;
using Jellyfin.Xtream.Service.MpegTs.Models;
using Jellyfin.Xtream.Service.ProviderManagement;
using Jellyfin.Xtream.Utility;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// <para>
/// Circular buffer read stream with optimized multi-reader support.
/// Ultra-optimized using unsafe code, direct memory operations, SIMD vectorization, and aggressive inlining.
/// Optimized for multi-core systems with cache-line awareness and hardware acceleration.
/// </para>
/// <para>
/// Thread Safety: This class supports MULTIPLE CONCURRENT READERS.
/// - Each instance maintains its own read head position.
/// - Uses atomic operations (Interlocked) for read head updates.
/// - Memory barriers ensure buffer data visibility across cores.
/// - Graceful cancellation handling for async operations.
/// </para>
/// </summary>
public sealed class CircularBufferReadStream : Stream
{
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct CacheLinePadded
    {
        [FieldOffset(0)]
        public long Value;
    }

    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct CacheLinePaddedInt
    {
        [FieldOffset(0)]
        public int Value;
    }

    private const int TsPacketSize = 188;
    private const byte TsSyncByte = 71;
    private const long MinimumStartupFillBytes = 4194304L; // 4MB minimum before checking for keyframe
    private const long MaximumWarmupFillBytes = 16777216L; // 16MB max - handles very long GOPs (15-20 sec at 4-8 Mbps)
    private const int StartupWarmupTimeoutMs = 15000; // 15 seconds - increased to allow keyframe detection
    private const long ProgressLogIntervalBytes = 10 * 1024 * 1024; // Log every 10MB read

    // Stall detection uses StreamingTimeoutPolicy for configurable timeouts
    // MaxStallWaitMs: 3x data stall timeout (default 30s) - allows time for reconnection
    // ReconnectionWaitMs: extra grace period during active reconnection (10s)
    private static int MaxStallWaitMs => StreamingTimeoutPolicy.GetDataStallTimeoutMs() * 3;
    private static int ReconnectionWaitMs => StreamingTimeoutPolicy.DefaultBlacklistDurationMs / 3;

    private static readonly int _simdThreshold = DetermineSimdThreshold();
    private static readonly bool _avx512Supported = Avx512F.IsSupported;
    private static readonly bool _avx2Supported = Avx2.IsSupported;
    private static readonly bool _sse2Supported = Sse2.IsSupported;
    private static readonly int _prefetchDistance = DeterminePrefetchDistance();
    private static readonly int _maxReadSpins = DetermineMaxSpins();
    private static readonly int _maxAsyncPhase1Spins = Math.Max(10, _maxReadSpins / 5);

    /// <summary>
    /// Global registry of all active buffer read streams for diagnostics.
    /// </summary>
    private static readonly ConcurrentDictionary<string, CircularBufferReadStream> _activeStreams = new(
        StringComparer.Ordinal
    );

    private readonly CircularBufferWriteStream _sourceBuffer;
    private readonly ILogger? _logger;
    private readonly IDiscordNotificationService? _discordService;
    private readonly string _streamId;
    private readonly string _channelName;
    private readonly long _initialReadHead;
    private readonly bool _isPowerOfTwo;
    private readonly long _bufferMask;
    private readonly int _programNumber;
    private readonly DateTime _startTime = DateTime.UtcNow;

    private CacheLinePadded _readHead;
    private CacheLinePadded _totalOverflowBytes;
    private CacheLinePaddedInt _overflowCount;
    private long _lastSeenWriteHead;
    private int _stallDetectionCount;
    private int _lastSeenDiscontinuityCount;
    private bool _isAligned;
    private bool _isDisposed;
    private DateTime _lastOverflowLog = DateTime.MinValue;
    private DateTime _lastStallLog = DateTime.MinValue;
    private DateTime _lastStarvedLog = DateTime.MinValue;
    private DateTime _lastPredictorUpdate = DateTime.MinValue;
    private DateTime _lastDiscontinuityWaitLog = DateTime.MinValue;
    private DateTime _lastDiscontinuityHandledTime = DateTime.MinValue;
    private DateTime _lastSpsUnavailableLog = DateTime.MinValue;
    private long _lastProgressLogBytes;

    /// <summary>
    /// Minimum time in seconds between processing consecutive discontinuities.
    /// This prevents rapid jumping when source streams have multiple inherent discontinuities.
    /// </summary>
    private const double MinDiscontinuityIntervalSeconds = 3.0;

    // Overflow prediction for proactive warning
    private readonly OverflowPredictor _overflowPredictor;

    // Prefix buffer for injecting SPS/PPS parameter sets before first read
    // This ensures decoders receive required NAL units even if they're not in the circular buffer
    private byte[]? _prefixBuffer;
    private int _prefixBufferOffset;

    // Deferred parameter set injection - set when reader aligns to keyframe but SPS/PPS not yet cached
    // Will attempt injection on first read when parameter sets become available
    private bool _needsParameterSetInjection;
    private bool _parameterSetsInjected;

    /// <summary>
    /// Gets the virtual position in the source buffer.
    /// </summary>
    public long ReadHead => Volatile.Read(ref _readHead.Value);

    /// <summary>
    /// Gets the number of bytes that have been read from this stream.
    /// </summary>
    public long TotalBytesRead => ReadHead - _initialReadHead;

    /// <summary>
    /// Gets the total number of bytes lost due to buffer overflows.
    /// </summary>
    public long TotalOverflowBytes => Volatile.Read(ref _totalOverflowBytes.Value);

    /// <summary>
    /// Gets the number of buffer overflow events that have occurred.
    /// </summary>
    public int OverflowCount => Volatile.Read(ref _overflowCount.Value);

    /// <summary>
    /// Gets the current gap between reader and writer (in bytes).
    /// A large gap means reader is lagging; zero means reader is caught up.
    /// </summary>
    public long CurrentGap => _sourceBuffer.TotalBytesWritten - ReadHead;

    /// <summary>
    /// Gets the current overflow risk level based on gap trend analysis.
    /// </summary>
    public OverflowRisk OverflowRisk => _overflowPredictor.CurrentRisk;

    /// <summary>
    /// Gets the estimated seconds until buffer overflow (NaN if not applicable).
    /// </summary>
    public double SecondsToOverflow => _overflowPredictor.SecondsToOverflow;

    /// <inheritdoc />
    public override long Position
    {
        get
        {
            var head = ReadHead;
            return _isPowerOfTwo ? (head & _bufferMask) : (head % _sourceBuffer.BufferSize);
        }
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <summary>
    /// Initializes a new instance of the <see cref="CircularBufferReadStream"/> class.
    /// </summary>
    /// <param name="sourceBuffer">The source buffer to read from.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <param name="streamId">Optional stream identifier for logging.</param>
    /// <param name="channelName">Optional channel name for notifications.</param>
    /// <param name="discordService">Optional Discord notification service.</param>
    /// <param name="programNumber">The program number to read (for MPTS support). Use 0 for first/default program.</param>
    public CircularBufferReadStream(
        CircularBufferWriteStream sourceBuffer,
        ILogger? logger = null,
        string streamId = "unknown",
        string? channelName = null,
        IDiscordNotificationService? discordService = null,
        int programNumber = 0
    )
    {
        _sourceBuffer = sourceBuffer;
        _logger = logger;
        _streamId = streamId;
        _channelName = channelName ?? streamId;
        _discordService = discordService;
        _programNumber = programNumber;
        var bufSize = sourceBuffer.BufferSize;
        _isPowerOfTwo = (bufSize & (bufSize - 1)) == 0;
        _bufferMask = bufSize - 1;
        var totalWritten = sourceBuffer.TotalBytesWritten;
        long bufferSize = sourceBuffer.BufferSize;

        // First, check if there's a recent reader position we can continue from
        // This enables seamless handoff from FFprobe to FFmpeg
        var continuationPosition = sourceBuffer.ConsumeLastReaderPosition(10000);

        if (continuationPosition > 0)
        {
            // Continue from where the previous reader left off
            // Find next keyframe from that position for clean video start
            var keyframeOffset = sourceBuffer.TsIndexer.GetBestStartOffset(continuationPosition, _programNumber);

            if (keyframeOffset != -1 && keyframeOffset >= continuationPosition && keyframeOffset <= totalWritten)
            {
                _initialReadHead = keyframeOffset;
                _logger?.PluginLogInformation(
                    "Reader for stream {StreamId} continuing from previous reader at keyframe {Offset} ({GapMB:F1}MB behind live)",
                    _streamId,
                    keyframeOffset,
                    (double)(totalWritten - keyframeOffset) / 1048576.0
                );
                _isAligned = true;
                InjectCachedParameterSets();
            }
            else
            {
                _initialReadHead = continuationPosition;
                _logger?.PluginLogInformation(
                    "Reader for stream {StreamId} continuing from previous reader position {Offset} ({GapMB:F1}MB behind live)",
                    _streamId,
                    continuationPosition,
                    (double)(totalWritten - continuationPosition) / 1048576.0
                );
            }

            // Reset PCR timing state to avoid false jitter from position continuity
            sourceBuffer.TsIndexer.ResetTimingState();
        }
        else if (totalWritten > bufferSize)
        {
            var targetLagBytes = Math.Min(bufferSize / 4, 4194304L);
            var targetOffset = totalWritten - targetLagBytes;
            var minValidOffset = totalWritten - bufferSize + 524288;

            if (targetOffset < minValidOffset)
            {
                targetOffset = minValidOffset;
            }

            var syncPoint = sourceBuffer.TsIndexer.GetBestSyncPoint(targetOffset, _programNumber);

            if (syncPoint.HasValue && syncPoint.Value.Offset >= minValidOffset)
            {
                _initialReadHead = syncPoint.Value.Offset;
                _logger?.LogDebugIfEnabled(
                    "Reader for stream {StreamId} (program {ProgramNumber}) aligned to SYNC POINT at {Offset} ({GapMB:F1}MB behind live, drift: {DriftMs:F1}ms)",
                    _streamId,
                    _programNumber,
                    syncPoint.Value.Offset,
                    (double)(totalWritten - syncPoint.Value.Offset) / 1048576.0,
                    syncPoint.Value.DriftMs
                );
                _isAligned = true;
                InjectCachedParameterSets();
            }
            else
            {
                var keyframeOffset = sourceBuffer.TsIndexer.GetBestStartOffset(targetOffset, _programNumber);

                if (keyframeOffset != -1 && keyframeOffset >= minValidOffset)
                {
                    _initialReadHead = keyframeOffset;
                    _logger?.LogDebugIfEnabled(
                        "Reader for stream {StreamId} (program {ProgramNumber}) aligned to KEYFRAME at {Offset} ({GapMB:F1}MB behind live)",
                        _streamId,
                        _programNumber,
                        keyframeOffset,
                        (double)(totalWritten - keyframeOffset) / 1048576.0
                    );
                    _isAligned = true;
                    InjectCachedParameterSets();
                }
                else
                {
                    var safetyMargin = Math.Max(524288L, bufferSize / 32);
                    var startGap = Math.Min(targetLagBytes, bufferSize - safetyMargin);
                    _initialReadHead = totalWritten - startGap;
                    _logger?.PluginLogWarning(
                        "Reader for stream {StreamId} (program {ProgramNumber}) could not find keyframe (video PID: {VideoPid}). Falling back to byte alignment at {GapMB:F1}MB behind.",
                        _streamId,
                        _programNumber,
                        sourceBuffer.TsIndexer.GetVideoPid(_programNumber),
                        (double)startGap / 1048576.0
                    );
                }
            }
        }
        else if (totalWritten > 0)
        {
            // Buffer has data but hasn't wrapped yet - start near current position
            // to avoid re-reading old data that previous consumers already read
            var targetLagBytes = Math.Min(totalWritten / 2, 2097152L); // Up to 2MB behind
            var targetOffset = Math.Max(0L, totalWritten - targetLagBytes);

            // Try to align to a keyframe if possible
            var keyframeOffset = sourceBuffer.TsIndexer.GetBestStartOffset(targetOffset, _programNumber);

            if (keyframeOffset != -1 && keyframeOffset <= totalWritten)
            {
                _initialReadHead = keyframeOffset;
                _logger?.LogDebugIfEnabled(
                    "Reader for stream {StreamId} aligned to keyframe at {Offset} ({GapMB:F1}MB behind, buffer {Pct:F0}% full)",
                    _streamId,
                    keyframeOffset,
                    (double)(totalWritten - keyframeOffset) / 1048576.0,
                    (double)totalWritten * 100.0 / bufferSize
                );
                _isAligned = true;
                InjectCachedParameterSets();
            }
            else
            {
                _initialReadHead = targetOffset;
                _logger?.LogDebugIfEnabled(
                    "Reader for stream {StreamId} initialized at {Offset} ({GapMB:F1}MB behind, buffer {Pct:F0}% full)",
                    _streamId,
                    targetOffset,
                    (double)(totalWritten - targetOffset) / 1048576.0,
                    (double)totalWritten * 100.0 / bufferSize
                );
            }
        }
        else
        {
            _initialReadHead = 0L;
            _logger?.LogDebugIfEnabled(
                "Reader for stream {StreamId} initialized at buffer start (empty buffer)",
                _streamId
            );
        }

        _readHead.Value = _initialReadHead;
        _ = _activeStreams.TryAdd(_streamId, this);

        // Initialize overflow predictor for proactive overflow detection
        _overflowPredictor = new OverflowPredictor(sourceBuffer.BufferSize);

        _logger?.LogDebugIfEnabled(
            "Registered buffer reader for stream {StreamId} (total active: {Count})",
            _streamId,
            _activeStreams.Count
        );
    }

    /// <summary>
    /// Finalizes an instance of the <see cref="CircularBufferReadStream"/> class.
    /// Ensures cleanup of static dictionary entry if Dispose() is not called.
    /// </summary>
    ~CircularBufferReadStream()
    {
        _ = _activeStreams.TryRemove(_streamId, out _);
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => ReadSpan(new Span<byte>(buffer, offset, count));

    /// <inheritdoc />
    public override int Read(Span<byte> buffer) => ReadSpan(buffer);

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var currentReadHead = ReadHead;
        var gap = _sourceBuffer.TotalBytesWritten - currentReadHead;

        // For new readers (TotalBytesRead == 0), ensure we have at least one keyframe detected
        // before allowing playback. This applies even if buffer already has >4MB of data,
        // which can happen during channel switches when the broadcast starts before the reader connects.
        var hasKeyframe = _sourceBuffer.TsIndexer.GetKeyframeCount(_programNumber) > 0;
        var demuxerInitialized = _sourceBuffer.IsDemuxerInitialized;
        var needsWarmup =
            TotalBytesRead == 0L && (!hasKeyframe || gap < MinimumStartupFillBytes || !demuxerInitialized);

        if (needsWarmup)
        {
            _logger?.LogDebugIfEnabled(
                "Stream {StreamId}: Warming up buffer - {CurrentKB}KB filled, Keyframe={HasKeyframe}, DemuxerReady={DemuxerReady}. Waiting for playback conditions...",
                _streamId,
                gap / 1024,
                hasKeyframe,
                demuxerInitialized
            );
            var warmupStart = DateTime.UtcNow;
            var pollCount = 0;

            // Wait for buffer fill, demuxer initialization, AND keyframe detection
            // This ensures video can start immediately without black frames
            // - MinimumStartupFillBytes (4MB): Ensures enough data for demuxer/decoder buffers
            // - Keyframe detection: Ensures we can align to IDR frame for instant video display
            // - MaximumWarmupFillBytes (16MB): Cap for very long GOP streams (15-20 sec GOPs at 4-8 Mbps)
            while (!cancellationToken.IsCancellationRequested)
            {
                var elapsedMs = (DateTime.UtcNow - warmupStart).TotalMilliseconds;

                // Check completion conditions:
                // 1. Have enough data AND demuxer ready AND keyframe detected = ideal
                // 2. Exceeded maximum warmup size = proceed anyway (very long GOP)
                // 3. Timeout reached = proceed anyway (fallback)
                var hasMinimumData = gap >= MinimumStartupFillBytes && demuxerInitialized;
                var warmupComplete = hasMinimumData && hasKeyframe;

                if (warmupComplete)
                {
                    break;
                }

                // Maximum data limit: proceed even without keyframe if we've buffered enough
                // This handles streams with extremely long GOPs (>16MB between keyframes)
                if (gap >= MaximumWarmupFillBytes)
                {
                    _logger?.PluginLogWarning(
                        "Stream {StreamId}: Warmup reached max buffer ({MaxMB}MB) without keyframe. GOP may be very long. Keyframes={KeyframeCount}, Programs={ProgramCount}. Starting playback anyway.",
                        _streamId,
                        MaximumWarmupFillBytes / 1048576,
                        _sourceBuffer.TsIndexer.GetKeyframeCount(_programNumber),
                        _sourceBuffer.TsIndexer.ProgramCount
                    );
                    break;
                }

                // Timeout: proceed anyway to avoid infinite wait
                if (elapsedMs > StartupWarmupTimeoutMs)
                {
                    _logger?.PluginLogWarning(
                        "Stream {StreamId}: Warmup timeout after {TimeMs}ms. Buffer={FilledKB}KB, DemuxerReady={DemuxerReady}, Keyframe={HasKeyframe}. Starting playback anyway.",
                        _streamId,
                        elapsedMs,
                        gap / 1024,
                        demuxerInitialized,
                        hasKeyframe
                    );
                    break;
                }

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                pollCount++;
                currentReadHead = ReadHead;
                gap = _sourceBuffer.TotalBytesWritten - currentReadHead;
                demuxerInitialized = _sourceBuffer.IsDemuxerInitialized;
                hasKeyframe = _sourceBuffer.TsIndexer.GetKeyframeCount(_programNumber) > 0;

                if (pollCount % 20 == 0)
                {
                    var fillPct = (double)gap * 100.0 / MinimumStartupFillBytes;
                    _logger?.LogDebugIfEnabled(
                        "Stream {StreamId}: Buffering... {FillPct:F1}% ({CurrentKB}KB), DemuxerReady={DemuxerReady}, Keyframe={HasKeyframe}",
                        _streamId,
                        fillPct,
                        gap / 1024,
                        demuxerInitialized,
                        hasKeyframe
                    );
                }
            }

            if (gap >= MinimumStartupFillBytes)
            {
                var warmupDuration = (DateTime.UtcNow - warmupStart).TotalMilliseconds;
                var detectedPrograms = _sourceBuffer.TsIndexer.ProgramCount;
                var detectedKeyframes = _sourceBuffer.TsIndexer.GetKeyframeCount(_programNumber);
                _logger?.PluginLogInformation(
                    "Stream {StreamId}: Buffer warmup complete in {DurationMs}ms. {FilledMB:F1}MB buffered, {ProgramCount} programs, {KeyframeCount} keyframes detected, DemuxerReady={DemuxerReady}. Starting playback.",
                    _streamId,
                    warmupDuration,
                    (double)gap / 1048576.0,
                    detectedPrograms,
                    detectedKeyframes,
                    demuxerInitialized
                );

                if (!_isAligned)
                {
                    var totalWritten = _sourceBuffer.TotalBytesWritten;
                    long targetLagBytes = Math.Min(_sourceBuffer.BufferSize / 4, 4194304);
                    var targetOffset = totalWritten - targetLagBytes;
                    var minValidOffset = totalWritten - _sourceBuffer.BufferSize + 524288;

                    if (targetOffset < minValidOffset)
                    {
                        targetOffset = minValidOffset;
                    }

                    var keyframeOffset = _sourceBuffer.TsIndexer.GetBestStartOffset(targetOffset, _programNumber);

                    if (keyframeOffset != -1 && keyframeOffset >= minValidOffset)
                    {
                        _ = Interlocked.Exchange(ref _readHead.Value, keyframeOffset);
                        currentReadHead = keyframeOffset;
                        gap = totalWritten - currentReadHead;
                        _isAligned = true;
                        InjectCachedParameterSets();
                        _logger?.PluginLogInformation(
                            "Stream {StreamId}: Post-warmup KEYFRAME alignment successful at offset {Offset} ({GapMB:F1}MB behind live). Video should now be visible.",
                            _streamId,
                            keyframeOffset,
                            (double)gap / 1048576.0
                        );
                    }
                    else
                    {
                        var videoPid = _sourceBuffer.TsIndexer.GetVideoPid(_programNumber);
                        var keyframeCount = _sourceBuffer.TsIndexer.GetKeyframeCount(_programNumber);
                        var programCount = _sourceBuffer.TsIndexer.ProgramCount;
                        var packetsParsed = _sourceBuffer.TsIndexer.TotalPacketsParsed;
                        var patCrcErrors = _sourceBuffer.TsIndexer.PatCrcErrors;
                        var pmtCrcErrors = _sourceBuffer.TsIndexer.PmtCrcErrors;

                        _logger?.PluginLogWarning(
                            "Stream {StreamId}: Post-warmup keyframe alignment failed. VideoPID={VideoPid}, KeyframeCount={KeyframeCount}, Programs={ProgramCount}, PacketsParsed={PacketsParsed}, PAT_CRC_Errors={PatCrcErrors}, PMT_CRC_Errors={PmtCrcErrors}. Video may not display correctly.",
                            _streamId,
                            videoPid,
                            keyframeCount,
                            programCount,
                            packetsParsed,
                            patCrcErrors,
                            pmtCrcErrors
                        );

                        // Log program details if any were detected
                        if (programCount > 0)
                        {
                            var programs = _sourceBuffer.TsIndexer.GetProgramNumbers();
                            foreach (var pn in programs)
                            {
                                var pInfo = _sourceBuffer.TsIndexer.GetProgramInfo(pn);
                                if (pInfo != null)
                                {
                                    _logger?.PluginLogWarning(
                                        "Stream {StreamId}: Program {ProgramNumber}: VideoPid={VideoPid}, PcrPid={PcrPid}, AudioPid={AudioPid}, PMT_Version={PmtVersion}",
                                        _streamId,
                                        pn,
                                        pInfo.VideoPid,
                                        pInfo.PcrPid,
                                        pInfo.Audio.Pid,
                                        pInfo.PmtVersion
                                    );
                                }
                            }
                        }
                    }
                }
            }
        }

        SpinWait spinWait = default;
        var startWaitTime = gap == 0L ? DateTime.UtcNow : DateTime.MinValue;
        var yieldCount = 0;
        var lastSeenWrite = _sourceBuffer.TotalBytesWritten;
        var asyncStallCount = 0;

        while (gap == 0)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return 0;
            }

            if (spinWait.Count < _maxAsyncPhase1Spins)
            {
                spinWait.SpinOnce();
            }
            else if (yieldCount < 50)
            {
                try
                {
                    await Task.Delay(0, cancellationToken).ConfigureAwait(false);
                    yieldCount++;
                }
                catch (TaskCanceledException)
                {
                    return 0;
                }
            }
            else
            {
                try
                {
                    await Task.Yield();

                    for (var i = 0; i < 5; i++)
                    {
                        var latestWrite = _sourceBuffer.TotalBytesWritten;
                        if (latestWrite > currentReadHead)
                        {
                            break;
                        }

                        Thread.SpinWait(100);
                    }

                    var currentWrite = _sourceBuffer.TotalBytesWritten;

                    if (currentWrite == lastSeenWrite)
                    {
                        asyncStallCount++;

                        // Check connection state to determine how long to wait
                        var isReconnecting = _sourceBuffer.IsReconnecting;
                        var lastWriteTime = _sourceBuffer.LastWriteTime;
                        var msSinceLastWrite =
                            lastWriteTime != default
                                ? (DateTime.UtcNow - lastWriteTime).TotalMilliseconds
                                : asyncStallCount * 10.0; // Fallback estimate

                        // Determine max wait time based on connection state
                        var maxWaitMs = isReconnecting ? MaxStallWaitMs + ReconnectionWaitMs : MaxStallWaitMs;

                        if (msSinceLastWrite > maxWaitMs)
                        {
                            // Exceeded maximum wait time - source is likely permanently dead
                            if (!_isDisposed)
                            {
                                _logger?.PluginLogError(
                                    "Stream {StreamId}: Source disconnected permanently. No data for {Seconds:F1}s (max wait: {MaxWait}s). Reconnecting={Reconnecting}",
                                    _streamId,
                                    msSinceLastWrite / 1000.0,
                                    maxWaitMs / 1000.0,
                                    isReconnecting
                                );
                            }

                            // Return 0 to signal EOF - the stream is dead
                            return 0;
                        }

                        if (asyncStallCount >= 500)
                        {
                            // Only log if stream is still active and stall is significant (>15s)
                            // Shorter stalls (5-15s) are normal for bursty IPTV streams
                            // This prevents noise during normal stream bursts
                            if (!_isDisposed && msSinceLastWrite > 15000)
                            {
                                var now = DateTime.UtcNow;
                                if ((now - _lastStallLog).TotalSeconds >= 60)
                                {
                                    _lastStallLog = now;
                                    _logger?.PluginLogWarning(
                                        "Stream {StreamId} data stall: No new data for {Seconds:F1}s (write head: {WriteHead}). Reconnecting={Reconnecting}, SourceConnected={Connected}",
                                        _streamId,
                                        msSinceLastWrite / 1000.0,
                                        currentWrite,
                                        isReconnecting,
                                        _sourceBuffer.IsSourceConnected
                                    );
                                }
                            }

                            // Reset stall count but DON'T return 0 immediately - wait for reconnection
                            // Returning 0 causes ffmpeg to loop on stale data. Instead, wait longer.
                            asyncStallCount = 0;

                            // Wait longer if reconnection is in progress
                            var waitMs = isReconnecting ? 1000 : 500;
                            try
                            {
                                await Task.Delay(waitMs, cancellationToken).ConfigureAwait(false);
                            }
                            catch (TaskCanceledException)
                            {
                                return 0;
                            }

                            // Check if new data arrived during the wait
                            var newWrite = _sourceBuffer.TotalBytesWritten;
                            if (newWrite > currentWrite)
                            {
                                // Data arrived! Update tracking and continue reading
                                lastSeenWrite = newWrite;
                                continue;
                            }

                            // Still no data - check if stream is being disposed
                            if (_isDisposed || cancellationToken.IsCancellationRequested)
                            {
                                return 0;
                            }

                            // Continue waiting for data (broadcast may be reconnecting)
                            continue;
                        }
                    }
                    else
                    {
                        lastSeenWrite = currentWrite;
                        asyncStallCount = 0;
                    }
                }
                catch (Exception)
                {
                    return 0;
                }
            }

            currentReadHead = ReadHead;
            gap = _sourceBuffer.TotalBytesWritten - currentReadHead;

            if (startWaitTime != DateTime.MinValue && spinWait.Count > 100)
            {
                var now = DateTime.UtcNow;
                var waitTime = (now - startWaitTime).TotalMilliseconds;
                if (waitTime > 50.0 && (now - _lastStarvedLog).TotalSeconds >= 30)
                {
                    _lastStarvedLog = now;
                    _logger?.PluginLogWarning(
                        "Stream {StreamId} reader starved: waited {WaitMs:F1}ms for data. Reader caught up to writer (source may be slow or network congested). This causes playback stuttering on Apple TV and other clients!",
                        _streamId,
                        waitTime
                    );
                }

                startWaitTime = now;
            }
        }

        return ReadSpan(buffer.Span);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private unsafe int ReadSpan(Span<byte> destination)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        // Check for deferred parameter set injection
        // This handles the case where reader connected before SPS/PPS were cached
        if (_needsParameterSetInjection && !_parameterSetsInjected)
        {
            InjectCachedParameterSets();
        }

        // First, consume any prefix buffer data (injected SPS/PPS parameter sets)
        if (_prefixBuffer != null && _prefixBufferOffset < _prefixBuffer.Length)
        {
            var prefixRemaining = _prefixBuffer.Length - _prefixBufferOffset;
            var prefixToCopy = Math.Min(prefixRemaining, destination.Length);

            _logger?.PluginLogInformation(
                "Stream {StreamId}: Serving {Bytes} bytes from prefix buffer (SPS/PPS injection), {Remaining} bytes remaining",
                _streamId,
                prefixToCopy,
                prefixRemaining - prefixToCopy
            );

            _prefixBuffer.AsSpan(_prefixBufferOffset, prefixToCopy).CopyTo(destination);
            _prefixBufferOffset += prefixToCopy;

            // If we've consumed the entire prefix buffer, clear it
            if (_prefixBufferOffset >= _prefixBuffer.Length)
            {
                _logger?.PluginLogInformation("Stream {StreamId}: Prefix buffer (SPS/PPS) fully consumed", _streamId);
                _prefixBuffer = null;
                _prefixBufferOffset = 0;
            }

            // If we filled the destination from prefix alone, return
            if (prefixToCopy == destination.Length)
            {
                return prefixToCopy;
            }

            // Otherwise, continue reading from circular buffer into remaining space
            destination = destination[prefixToCopy..];
            var circularRead = ReadFromCircularBuffer(destination);
            return prefixToCopy + circularRead;
        }

        return ReadFromCircularBuffer(destination);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private unsafe int ReadFromCircularBuffer(Span<byte> destination)
    {
        var currentReadHead = Volatile.Read(ref _readHead.Value);
        var totalWritten = _sourceBuffer.TotalBytesWritten;
        Thread.MemoryBarrier();
        var gap = totalWritten - currentReadHead;
        SpinWait spinWait = default;

        while (gap == 0L && spinWait.Count < _maxReadSpins)
        {
            spinWait.SpinOnce();
            totalWritten = _sourceBuffer.TotalBytesWritten;
            gap = totalWritten - currentReadHead;
        }

        if (gap == 0)
        {
            return 0;
        }

        if (totalWritten == _lastSeenWriteHead && totalWritten > 0)
        {
            if (gap < 1048576)
            {
                _stallDetectionCount++;
                if (_stallDetectionCount > 1000)
                {
                    _logger?.PluginLogWarning(
                        "Writer stall detected on stream {StreamId}: Write head stuck at {WriteHead} for {Count} reads. Reader at {ReadHead}, gap={GapKB}KB. Skipping forward to prevent replay loop.",
                        _streamId,
                        totalWritten,
                        _stallDetectionCount,
                        currentReadHead,
                        gap / 1024
                    );
                    _ = Interlocked.Exchange(ref _readHead.Value, totalWritten);
                    _stallDetectionCount = 0;
                    return 0;
                }
            }
        }
        else
        {
            _lastSeenWriteHead = totalWritten;
            _stallDetectionCount = 0;
        }

        if (!_isAligned && TryAlignToMpegTsSync(ref currentReadHead, totalWritten))
        {
            _ = Interlocked.Exchange(ref _readHead.Value, currentReadHead);
            gap = totalWritten - currentReadHead;
            _isAligned = true;
        }

        // Check for stream discontinuity (reconnection after EOF/error)
        // We track by count, not position, because the reader might be AHEAD of the discontinuity
        // point if it was consuming buffered data when the source disconnected. In that case,
        // the buffered data after the discontinuity point contains OLD frames that will cause
        // video loops when ffmpeg decodes them (timestamps jump backward).
        var currentDiscontinuityCount = _sourceBuffer.DiscontinuityCount;
        var discontinuityOffset = _sourceBuffer.LastDiscontinuityOffset;

        if (currentDiscontinuityCount > _lastSeenDiscontinuityCount)
        {
            // Rate-limit discontinuity processing to prevent rapid jumping when source has
            // multiple inherent discontinuities (e.g., ad insertion, multi-source multiplexing)
            var now = DateTime.UtcNow;
            var timeSinceLastHandled = (now - _lastDiscontinuityHandledTime).TotalSeconds;
            if (
                timeSinceLastHandled < MinDiscontinuityIntervalSeconds
                && _lastDiscontinuityHandledTime != DateTime.MinValue
            )
            {
                // Too soon after last discontinuity - suppress this one but update the count
                // to prevent reprocessing on every read
                _lastSeenDiscontinuityCount = currentDiscontinuityCount;
                _logger?.LogDebugIfEnabled(
                    "Stream {StreamId}: Suppressing rapid discontinuity #{Count} ({TimeSince:F1}s since last, minimum {MinInterval:F0}s)",
                    _streamId,
                    currentDiscontinuityCount,
                    timeSinceLastHandled,
                    MinDiscontinuityIntervalSeconds
                );
                // Don't return - continue reading normally to avoid stalling
            }
            else
            {
                _lastSeenDiscontinuityCount = currentDiscontinuityCount;
                _lastDiscontinuityHandledTime = now;
                var oldReadHead = currentReadHead;

                // CRITICAL: We must wait for fresh data to arrive AFTER the discontinuity point.
                // The discontinuity offset marks where new data STARTS being written after reconnection.
                // We need at least 1MB of fresh data before we can safely resume reading.
                const long MinFreshDataBytes = 1048576; // 1MB minimum fresh data
                var freshDataAvailable = totalWritten - discontinuityOffset;

                if (freshDataAvailable < MinFreshDataBytes)
                {
                    // Not enough fresh data yet - return 0 to make reader wait
                    // Only log once per second to avoid log spam during the wait
                    if ((now - _lastDiscontinuityWaitLog).TotalSeconds >= 1.0)
                    {
                        _lastDiscontinuityWaitLog = now;
                        _logger?.PluginLogInformation(
                            "Stream {StreamId}: Discontinuity #{Count} detected, waiting for fresh data ({FreshKB:F0}KB/{RequiredKB}KB available)",
                            _streamId,
                            currentDiscontinuityCount,
                            freshDataAvailable / 1024.0,
                            MinFreshDataBytes / 1024
                        );
                    }

                    // Reset the count so we check again next read
                    _lastSeenDiscontinuityCount = currentDiscontinuityCount - 1;
                    return 0;
                }

                // CRITICAL: Only consider sync points from FRESH data (after discontinuityOffset).
                // Data indexed before the discontinuity point is from the OLD HTTP connection
                // and contains stale data that causes video/audio loops when read.
                // Using GetBestSyncPoint instead of GetBestStartOffset ensures both audio AND video
                // start at clean boundaries - this prevents audio from repeating after reconnections.
                var minValidOffset = discontinuityOffset;
                var syncPoint = _sourceBuffer.TsIndexer.GetBestSyncPoint(totalWritten, _programNumber, minValidOffset);

                if (syncPoint.HasValue && syncPoint.Value.Offset >= minValidOffset)
                {
                    currentReadHead = syncPoint.Value.Offset;
                }
                else
                {
                    // No sync point found, fall back to video keyframe only
                    var keyframeOffset = _sourceBuffer.TsIndexer.GetBestStartOffset(
                        totalWritten,
                        _programNumber,
                        minValidOffset
                    );

                    if (keyframeOffset != -1 && keyframeOffset >= minValidOffset)
                    {
                        currentReadHead = keyframeOffset;
                    }
                    else
                    {
                        // No keyframe found in fresh data, use the discontinuity offset as fallback
                        // This ensures we start reading from fresh data even without keyframe alignment
                        currentReadHead = discontinuityOffset;
                    }
                }

                // Ensure we don't go past write head
                if (currentReadHead > totalWritten)
                {
                    currentReadHead = totalWritten;
                }

                _ = Interlocked.Exchange(ref _readHead.Value, currentReadHead);
                gap = totalWritten - currentReadHead;

                // Reset injection state after discontinuity to reinject SPS/PPS for new provider
                _parameterSetsInjected = false;
                _needsParameterSetInjection = true;
                InjectCachedParameterSets();

                var skippedMB = (double)(currentReadHead - oldReadHead) / 1048576.0;
                _logger?.PluginLogInformation(
                    "Stream {StreamId}: Discontinuity #{Count} handled - jumped from {OldOffset} to {NewOffset} ({SkippedMB:F1}MB {Direction}, {FreshMB:F1}MB fresh data available)",
                    _streamId,
                    currentDiscontinuityCount,
                    oldReadHead,
                    currentReadHead,
                    Math.Abs(skippedMB),
                    skippedMB >= 0 ? "forward" : "backward",
                    freshDataAvailable / 1048576.0
                );

                // CRITICAL: Return 0 to force the next Read() call to serve the prefix buffer (SPS/PPS)
                // before reading from the circular buffer. Without this, FFmpeg continues reading
                // from the new position without receiving the parameter sets, causing decoder errors
                // like "non-existing PPS 0 referenced".
                if (_prefixBuffer is { Length: > 0 })
                {
                    return 0;
                }
            }
        }
        else if (discontinuityOffset > 0 && currentReadHead < discontinuityOffset)
        {
            // Reader is behind discontinuity point - skip forward to avoid reading stale data
            // CRITICAL: Only consider sync points from FRESH data (after discontinuityOffset)
            // Using GetBestSyncPoint ensures both audio and video start at clean boundaries
            var syncPoint = _sourceBuffer.TsIndexer.GetBestSyncPoint(totalWritten, _programNumber, discontinuityOffset);

            if (syncPoint.HasValue && syncPoint.Value.Offset >= discontinuityOffset)
            {
                currentReadHead = syncPoint.Value.Offset;
            }
            else
            {
                // Fall back to video keyframe only
                var keyframeOffset = _sourceBuffer.TsIndexer.GetBestStartOffset(
                    totalWritten,
                    _programNumber,
                    discontinuityOffset
                );

                currentReadHead =
                    keyframeOffset != -1 && keyframeOffset >= discontinuityOffset
                        ? keyframeOffset
                        : discontinuityOffset;
            }

            if (currentReadHead > totalWritten)
            {
                currentReadHead = totalWritten;
            }

            _ = Interlocked.Exchange(ref _readHead.Value, currentReadHead);
            gap = totalWritten - currentReadHead;

            // Reset injection state and reinject SPS/PPS after skipping past discontinuity
            _parameterSetsInjected = false;
            _needsParameterSetInjection = true;
            InjectCachedParameterSets();

            _logger?.PluginLogInformation(
                "Stream {StreamId}: Skipped past discontinuity from {OldOffset} to {NewOffset} to avoid stale data",
                _streamId,
                Volatile.Read(ref _readHead.Value),
                currentReadHead
            );

            // CRITICAL: Return 0 to force the next Read() call to serve the prefix buffer (SPS/PPS)
            if (_prefixBuffer is { Length: > 0 })
            {
                return 0;
            }
        }

        // Update overflow predictor periodically (every 500ms)
        var predictorNow = DateTime.UtcNow;
        if ((predictorNow - _lastPredictorUpdate).TotalMilliseconds >= 500)
        {
            _lastPredictorUpdate = predictorNow;
            var risk = _overflowPredictor.RecordSample(totalWritten, currentReadHead);

            // Log warning if risk is elevated
            if (risk is OverflowRisk.Critical or OverflowRisk.Imminent)
            {
                _logger?.PluginLogWarning(
                    "Stream {StreamId}: Overflow risk {Risk} - gap at {GapPct:F1}%, estimated {SecondsToOverflow:F1}s to overflow",
                    _streamId,
                    risk,
                    _overflowPredictor.CurrentGapPercentage * 100,
                    _overflowPredictor.SecondsToOverflow
                );
            }
        }

        if (gap > _sourceBuffer.BufferSize)
        {
            var bytesLost = gap - _sourceBuffer.BufferSize;
            long safetyMargin = Math.Max(524288, _sourceBuffer.BufferSize >> 4);
            var safeGap = _sourceBuffer.BufferSize - safetyMargin;
            var newReadHead = totalWritten - safeGap;
            var syncPoint = _sourceBuffer.TsIndexer.GetBestSyncPoint(newReadHead, _programNumber);

            if (syncPoint.HasValue && syncPoint.Value.Offset > newReadHead - safetyMargin)
            {
                newReadHead = syncPoint.Value.Offset;
            }
            else
            {
                var keyframeOffset = _sourceBuffer.TsIndexer.GetBestStartOffset(newReadHead, _programNumber);
                if (keyframeOffset != -1 && keyframeOffset > newReadHead - safetyMargin)
                {
                    newReadHead = keyframeOffset;
                }
            }

            var originalHead = Interlocked.CompareExchange(ref _readHead.Value, newReadHead, currentReadHead);

            if (originalHead == currentReadHead)
            {
                currentReadHead = newReadHead;
                gap = safeGap;
                _ = Interlocked.Add(ref _totalOverflowBytes.Value, bytesLost);
                var currentOverflowCount = Interlocked.Increment(ref _overflowCount.Value);
                var now = DateTime.UtcNow;

                if ((now - _lastOverflowLog).TotalSeconds >= 5.0 || currentOverflowCount == 1)
                {
                    var lostMB = (double)bytesLost / 1048576.0;
                    var totalLostMB = (double)Volatile.Read(ref _totalOverflowBytes.Value) / 1048576.0;
                    _logger?.PluginLogInformation(
                        "BUFFER OVERFLOW #{Count} on stream {StreamId}: Reader fell behind by {LostMB:F2}MB ({LostKB}KB). Skipping forward to {GapKB}KB behind live. Total lost: {TotalLostMB:F2}MB. This causes visible lag/stuttering!",
                        currentOverflowCount,
                        _streamId,
                        lostMB,
                        bytesLost / 1024,
                        safeGap / 1024,
                        totalLostMB
                    );
                    SendDiscordNotification(lostMB, totalLostMB);
                    _lastOverflowLog = now;
                }
            }
            else
            {
                currentReadHead = Volatile.Read(ref _readHead.Value);
                totalWritten = _sourceBuffer.TotalBytesWritten;
                gap = totalWritten - currentReadHead;

                if (gap > _sourceBuffer.BufferSize || gap <= 0)
                {
                    return 0;
                }
            }
        }

        var totalRead = 0;
        var remaining = (int)Math.Min(destination.Length, gap);
        var startingReadHead = currentReadHead;

        fixed (byte* dstPtr = destination)
        {
            fixed (byte* srcBuffer = _sourceBuffer.Buffer)
            {
                while (remaining > 0)
                {
                    var currentPosition = _isPowerOfTwo
                        ? (currentReadHead & _bufferMask)
                        : (currentReadHead % _sourceBuffer.BufferSize);
                    var bytesToEndOfBuffer = _sourceBuffer.BufferSize - currentPosition;
                    var chunkSize = (int)Math.Min(remaining, bytesToEndOfBuffer);
                    totalWritten = _sourceBuffer.TotalBytesWritten;
                    var currentGap = totalWritten - currentReadHead;

                    if (currentGap > _sourceBuffer.BufferSize)
                    {
                        _logger?.PluginLogWarning(
                            "Buffer wrap-around detected during read for stream {StreamId}. Read position {ReadHead} is {GapMB:F2}MB behind write head. Aborting read to prevent data corruption.",
                            _streamId,
                            currentReadHead,
                            (double)currentGap / 1048576.0
                        );
                        break;
                    }

                    if (currentGap < chunkSize)
                    {
                        if (currentGap == 0)
                        {
                            SpinWait localSpin = default;

                            for (var i = 0; i < 10; i++)
                            {
                                if (currentGap != 0)
                                {
                                    break;
                                }

                                localSpin.SpinOnce();
                                totalWritten = _sourceBuffer.TotalBytesWritten;
                                currentGap = totalWritten - currentReadHead;
                            }

                            if (currentGap == 0)
                            {
                                break;
                            }
                        }

                        chunkSize = (int)Math.Min(chunkSize, currentGap);
                    }

                    if (chunkSize <= 0)
                    {
                        break;
                    }

                    if (chunkSize >= _simdThreshold)
                    {
                        CopyMemorySimd(srcBuffer + currentPosition, dstPtr + totalRead, chunkSize);
                    }
                    else
                    {
                        Buffer.MemoryCopy(
                            srcBuffer + currentPosition,
                            dstPtr + totalRead,
                            destination.Length - totalRead,
                            chunkSize
                        );
                    }

                    totalRead += chunkSize;
                    currentReadHead += chunkSize;
                    remaining -= chunkSize;
                }
            }
        }

        if (totalRead > 0)
        {
            // NOTE: We intentionally do NOT modify PTS timestamps here.
            // IPTV streams should be passed through unchanged - the player (Jellyfin client)
            // has sophisticated A/V sync algorithms that work best with original timestamps.
            // Server-side PTS modification can cause sync issues because:
            // 1. We only see one direction of the stream (can't detect encoder clock drift)
            // 2. Modifying audio PTS while leaving video unchanged creates inconsistency
            // 3. Players expect PCR-based timing to be coherent across all elementary streams
            //
            // A/V sync monitoring is handled by the native TsDuck analyzer.

            var expectedHead = startingReadHead;
            var newHead = startingReadHead + totalRead;
            var actualHead = Interlocked.CompareExchange(ref _readHead.Value, newHead, expectedHead);

            if (actualHead != expectedHead)
            {
                _logger?.LogDebugIfEnabled(
                    "Read head was modified during read for stream {StreamId}. Expected {Expected}, found {Actual}. Read {Bytes} bytes. This is normal during overflow recovery.",
                    _streamId,
                    expectedHead,
                    actualHead,
                    totalRead
                );
            }

            // Periodic progress logging (every 10MB) to track read throughput without spam
            var bytesRead = TotalBytesRead;
            if (bytesRead - _lastProgressLogBytes >= ProgressLogIntervalBytes)
            {
                _lastProgressLogBytes = bytesRead;
                _logger?.LogDebugIfEnabled(
                    "Buffer read progress [{StreamId}]: {TotalMB:F1}MB read, gap={GapKB:F0}KB ({GapPct:F1}%), overflows={OverflowCount}",
                    _streamId,
                    bytesRead / (1024.0 * 1024.0),
                    gap / 1024.0,
                    (double)gap * 100.0 / _sourceBuffer.BufferSize,
                    OverflowCount
                );
            }
        }

        return totalRead;
    }

    /// <summary>
    /// Hardware-accelerated memory copy using SIMD instructions.
    /// Optimized for multi-core systems with AVX-512/AVX2/SSE support.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static unsafe void CopyMemorySimd(byte* src, byte* dst, int length)
    {
        var offset = 0;

        if (_avx512Supported && length >= 64)
        {
            for (var avx512Length = length & -64; offset < avx512Length; offset += 64)
            {
                if (offset + _prefetchDistance < length)
                {
                    Sse.Prefetch0(src + offset + _prefetchDistance);
                }

                var vec = Avx512F.LoadVector512(src + offset);
                Avx512F.Store(dst + offset, vec);
            }
        }
        else if (_avx2Supported && length >= 32)
        {
            for (var avx2Length = length & -32; offset < avx2Length; offset += 32)
            {
                if (Sse.IsSupported && offset + _prefetchDistance < length)
                {
                    Sse.Prefetch0(src + offset + _prefetchDistance);
                }

                var vec = Avx.LoadVector256(src + offset);
                Avx.Store(dst + offset, vec);
            }
        }
        else if (_sse2Supported && length >= 16)
        {
            for (var sse2Length = length & -16; offset < sse2Length; offset += 16)
            {
                if (Sse.IsSupported && offset + _prefetchDistance < length)
                {
                    Sse.Prefetch0(src + offset + _prefetchDistance);
                }

                var vec = Sse2.LoadVector128(src + offset);
                Sse2.Store(dst + offset, vec);
            }
        }
        else if (Vector.IsHardwareAccelerated && length >= Vector<byte>.Count)
        {
            for (
                var vectorLength = length & ~(Vector<byte>.Count - 1);
                offset < vectorLength;
                offset += Vector<byte>.Count
            )
            {
                var vec = Unsafe.ReadUnaligned<Vector<byte>>(src + offset);
                Unsafe.WriteUnaligned(dst + offset, vec);
            }
        }

        var remaining = length - offset;

        if (remaining > 0)
        {
            if (remaining >= 8)
            {
                Unsafe.WriteUnaligned(dst + offset, Unsafe.ReadUnaligned<long>(src + offset));
                offset += 8;
                remaining -= 8;
            }

            if (remaining >= 4)
            {
                Unsafe.WriteUnaligned(dst + offset, Unsafe.ReadUnaligned<int>(src + offset));
                offset += 4;
                remaining -= 4;
            }

            while (remaining > 0)
            {
                dst[offset] = src[offset];
                offset++;
                remaining--;
            }
        }
    }

    /// <summary>
    /// Determines optimal SIMD threshold based on CPU capabilities.
    /// Lower-end CPUs get higher threshold to avoid SIMD overhead.
    /// </summary>
    private static int DetermineSimdThreshold()
    {
        return Avx2.IsSupported ? 512
            : Sse2.IsSupported ? 1024
            : 4096;
    }

    /// <summary>
    /// Determines optimal prefetch distance based on CPU capabilities.
    /// Smaller caches on low-end CPUs need shorter prefetch distance to avoid cache pollution.
    /// </summary>
    private static int DeterminePrefetchDistance() => Avx2.IsSupported ? 256 : 128;

    /// <summary>
    /// Determines maximum spinning iterations based on CPU capabilities.
    /// Lower-end CPUs get fewer spins to avoid wasting cycles.
    /// </summary>
    private static int DetermineMaxSpins()
    {
        return Avx2.IsSupported ? 100
            : Sse2.IsSupported ? 50
            : 25;
    }

    /// <summary>
    /// Injects cached SPS/PPS parameter sets into the stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method retrieves cached parameter sets from the FFmpegStreamDemuxer (via CircularBufferWriteStream)
    /// and prepends them to the stream as MPEG-TS packets. This ensures decoders receive the necessary
    /// SPS/PPS NAL units even after mid-stream connections or provider switches.
    /// </para>
    /// <para>
    /// The parameter sets are stored in a prefix buffer that is consumed before reading from the
    /// circular buffer, ensuring they are the first data seen by the decoder.
    /// </para>
    /// </remarks>
    private void InjectCachedParameterSets()
    {
        // Try to get cached parameter sets from the source buffer
        var cache = _sourceBuffer.GetCachedParameterSets(_programNumber);
        if (cache is not { HasSps: true, HasPps: true })
        {
            // Don't mark as injected if we don't have SPS/PPS yet - keep trying
            // Rate-limit log to once per 5 seconds to avoid spam during initialization
            var now = DateTime.UtcNow;
            if ((now - _lastSpsUnavailableLog).TotalSeconds >= 5.0)
            {
                _lastSpsUnavailableLog = now;
                _logger?.LogDebugIfEnabled(
                    "Stream {StreamId}: SPS/PPS not yet available (cache={CacheNull}, HasSps={HasSps}, HasPps={HasPps}). Will retry.",
                    _streamId,
                    cache == null ? "null" : "exists",
                    cache?.HasSps ?? false,
                    cache?.HasPps ?? false
                );
            }

            return;
        }

        // Only mark as complete once we actually have and inject the parameter sets
        _parameterSetsInjected = true;
        _needsParameterSetInjection = false;

        // Get video PID for packet construction
        var videoPid = GetVideoPid();
        if (videoPid < 0)
        {
            _logger?.LogDebugIfEnabled(
                "Stream {StreamId}: Cannot inject parameter sets - video PID not detected yet.",
                _streamId
            );
            return;
        }

        // Build MPEG-TS packets containing the parameter sets
        var continuityCounter = 0;
        var videoPackets = cache.BuildInjectionPackets(videoPid, ref continuityCounter);

        // Also build an audio discontinuity packet to signal FFmpeg's audio decoder to reset
        // Without this, FFmpeg may replay buffered audio from before the discontinuity
        var audioPid = GetAudioPid();
        byte[]? audioDiscontinuityPacket = null;
        if (audioPid > 0)
        {
            audioDiscontinuityPacket = BuildDiscontinuityPacket(audioPid, continuityCounter: 0);
        }

        // Combine video packets with audio discontinuity packet
        var totalLength = videoPackets.Length + (audioDiscontinuityPacket?.Length ?? 0);
        if (totalLength > 0)
        {
            var combinedPackets = new byte[totalLength];
            var offset = 0;

            // Audio discontinuity first (so decoder resets audio before processing video)
            if (audioDiscontinuityPacket != null)
            {
                audioDiscontinuityPacket.CopyTo(combinedPackets, offset);
                offset += audioDiscontinuityPacket.Length;
            }

            // Then video SPS/PPS packets
            if (videoPackets.Length > 0)
            {
                videoPackets.CopyTo(combinedPackets, offset);
            }

            _prefixBuffer = combinedPackets;
            _prefixBufferOffset = 0;

            _logger?.PluginLogInformation(
                "Stream {StreamId}: Injecting {PacketCount} MPEG-TS packets ({Bytes} bytes) with SPS/PPS for video PID {VideoPid} and audio discontinuity for PID {AudioPid}. "
                    + "SPS={SpsLen}B, PPS={PpsLen}B, VPS={VpsLen}B, Total NAL={TotalNal}B",
                _streamId,
                totalLength / TsPacketSize,
                totalLength,
                videoPid,
                audioPid,
                cache.Sps?.Length ?? 0,
                cache.Pps?.Length ?? 0,
                cache.Vps?.Length ?? 0,
                cache.GetTotalSize()
            );
        }
    }

    /// <summary>
    /// Gets the video PID from the TsIndexer or demuxer.
    /// </summary>
    /// <returns>The video PID, or -1 if not available.</returns>
    private int GetVideoPid()
    {
        // Try to get from TsIndexer first
        var indexer = _sourceBuffer.TsIndexer;
        var programNumbers = indexer.GetProgramNumbers();

        if (programNumbers.Length > 0)
        {
            // Use specified program number or first available
            var targetProgram =
                _programNumber >= 0 && programNumbers.Contains(_programNumber) ? _programNumber : programNumbers[0];

            var programInfo = indexer.GetProgramInfo(targetProgram);
            if (programInfo != null)
            {
                return programInfo.VideoPid;
            }
        }

        return -1;
    }

    /// <summary>
    /// Gets the audio PID from the TsIndexer.
    /// </summary>
    /// <returns>The audio PID, or -1 if not available.</returns>
    private int GetAudioPid()
    {
        var indexer = _sourceBuffer.TsIndexer;
        var programNumbers = indexer.GetProgramNumbers();

        if (programNumbers.Length > 0)
        {
            var targetProgram =
                _programNumber >= 0 && programNumbers.Contains(_programNumber) ? _programNumber : programNumbers[0];

            var programInfo = indexer.GetProgramInfo(targetProgram);
            if (programInfo?.Audio.HasAudio == true)
            {
                return programInfo.Audio.Pid;
            }
        }

        return -1;
    }

    /// <summary>
    /// Builds an MPEG-TS null packet with discontinuity indicator for a specific PID.
    /// This signals decoders to reset their buffers for that PID.
    /// </summary>
    /// <param name="pid">The PID to signal discontinuity for.</param>
    /// <param name="continuityCounter">The continuity counter for this PID.</param>
    /// <returns>A 188-byte MPEG-TS packet with discontinuity indicator set.</returns>
    private static byte[] BuildDiscontinuityPacket(int pid, int continuityCounter)
    {
        var packet = new byte[TsPacketSize];

        // Fill with stuffing bytes
        Array.Fill(packet, (byte)0xFF);

        // TS header (4 bytes)
        packet[0] = 0x47; // Sync byte
        packet[1] = (byte)((pid >> 8) & 0x1F); // PID high bits (no PUSI, no TEI, no priority)
        packet[2] = (byte)(pid & 0xFF); // PID low bits

        // Adaptation field control: 10 = adaptation field only, no payload
        // This is a "null" packet for this PID that just signals discontinuity
        packet[3] = (byte)(0x20 | (continuityCounter & 0x0F));

        // Adaptation field length (183 bytes = rest of packet after header + length byte)
        packet[4] = 183;

        // Adaptation field flags:
        // Bit 7: discontinuity_indicator = 1
        // Bit 6: random_access_indicator = 1 (for good measure)
        // All other flags = 0
        packet[5] = 0xC0;

        // Rest is stuffing (already filled with 0xFF)

        return packet;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryAlignToMpegTsSync(ref long readHead, long totalWritten)
    {
        var available = totalWritten - readHead;

        if (available < 564)
        {
            return false;
        }

        var maxScan = (int)Math.Min(8192L, available - 376);

        if (maxScan <= 0)
        {
            return false;
        }

        var buffer = _sourceBuffer.Buffer;
        var bufSize = _sourceBuffer.BufferSize;

        for (var offset = 0; offset < maxScan; offset++)
        {
            var p0 = (int)(_isPowerOfTwo ? ((readHead + offset) & _bufferMask) : ((readHead + offset) % bufSize));
            var p1 = (int)(
                _isPowerOfTwo
                    ? ((readHead + offset + TsPacketSize) & _bufferMask)
                    : ((readHead + offset + TsPacketSize) % bufSize)
            );
            var p2 = (int)(
                _isPowerOfTwo
                    ? ((readHead + offset + (TsPacketSize * 2)) & _bufferMask)
                    : ((readHead + offset + (TsPacketSize * 2)) % bufSize)
            );

            if (buffer[p0] == TsSyncByte && buffer[p1] == TsSyncByte && buffer[p2] == TsSyncByte)
            {
                readHead += offset;
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Flush() { }

    /// <summary>
    /// Sends Discord notification for buffer overflow (fire and forget).
    /// </summary>
    /// <param name="lostMB">Megabytes lost in this overflow.</param>
    /// <param name="totalLostMB">Total megabytes lost.</param>
    private void SendDiscordNotification(double lostMB, double totalLostMB)
    {
        if (_discordService == null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _discordService
                    .NotifyBufferOverflowAsync(
                        _streamId,
                        _channelName,
                        Volatile.Read(ref _overflowCount.Value),
                        lostMB,
                        totalLostMB
                    )
                    .ConfigureAwait(false);
            }
            catch
            {
                // Ignore notification failures
            }
        });
    }

    /// <summary>
    /// Gets comprehensive diagnostics about buffer health and reader status.
    /// </summary>
    /// <returns>A formatted string with diagnostic information.</returns>
    public string GetDiagnostics()
    {
        var gap = CurrentGap;
        var totalWritten = _sourceBuffer.TotalBytesWritten;
        var totalRead = TotalBytesRead;
        var gapPct = (double)gap * 100.0 / (double)_sourceBuffer.BufferSize;
        var overflowBytes = Volatile.Read(ref _totalOverflowBytes.Value);
        var overflowRate = totalWritten > 0 ? (double)overflowBytes * 100.0 / (double)totalWritten : 0.0;

        return $"Stream {_streamId} Diagnostics:\n  Buffer Size: {_sourceBuffer.BufferSize / 1048576}MB\n  Total Written: {totalWritten / 1048576}MB\n  Total Read: {totalRead / 1048576}MB\n  Current Gap: {gap / 1024}KB ({gapPct:F1}% of buffer)\n  Buffer Overflows: {Volatile.Read(ref _overflowCount.Value)} events\n  Data Lost: {overflowBytes / 1048576}MB ({overflowRate:F2}% of total)\n  Aligned: {_isAligned}\n  Hardware: AVX2={_avx2Supported}, SSE2={_sse2Supported}, SIMD={Vector.IsHardwareAccelerated}\n  Status: {((double)gap < (double)_sourceBuffer.BufferSize * 0.1 ? "HEALTHY" : ((double)gap > (double)_sourceBuffer.BufferSize * 0.8 ? "LAGGING" : "OK"))}";
    }

    /// <summary>
    /// Sends comprehensive buffer diagnostics to Discord.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task SendDiagnosticsToDiscordAsync()
    {
        if (_discordService == null)
        {
            _logger?.PluginLogWarning(
                "Cannot send diagnostics to Discord: Discord service not configured for stream {StreamId}",
                _streamId
            );
            return;
        }

        try
        {
            var diagnostics = GetDiagnostics();
            await _discordService
                .SendBufferDiagnosticsAsync(_streamId, _channelName, diagnostics)
                .ConfigureAwait(false);
            _logger?.PluginLogInformation("Buffer diagnostics sent to Discord for stream {StreamId}", _streamId);
        }
        catch (Exception exception)
        {
            _logger?.PluginLogError(
                exception,
                "Failed to send buffer diagnostics to Discord for stream {StreamId}",
                _streamId
            );
        }
    }

    /// <summary>
    /// Gets structured MPEG-TS indexer metrics for this stream.
    /// Includes packet loss, PCR jitter, transport errors, and program information.
    /// </summary>
    /// <returns>Structured metrics about the MPEG-TS stream.</returns>
    public TsIndexerMetrics GetTsIndexerMetrics() => _sourceBuffer.TsIndexer.GetMetrics();

    /// <summary>
    /// Sends MPEG-TS indexer diagnostics to Discord.
    /// Reports stream health metrics including packet loss, PCR jitter, and transport errors.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task SendTsIndexerDiagnosticsToDiscordAsync()
    {
        if (_discordService == null)
        {
            _logger?.PluginLogWarning(
                "Cannot send TS indexer diagnostics to Discord: Discord service not configured for stream {StreamId}",
                _streamId
            );
            return;
        }

        try
        {
            var metrics = GetTsIndexerMetrics();
            await _discordService.SendTsIndexerMetricsAsync(_streamId, _channelName, metrics).ConfigureAwait(false);
            _logger?.PluginLogInformation("MPEG-TS indexer metrics sent to Discord for stream {StreamId}", _streamId);
        }
        catch (Exception exception)
        {
            _logger?.PluginLogError(
                exception,
                "Failed to send TS indexer metrics to Discord for stream {StreamId}",
                _streamId
            );
        }
    }

    /// <summary>
    /// Sends MPEG-TS indexer metrics for all active streams to Discord.
    /// Reports stream health including packet loss, PCR jitter, transport errors, and keyframe stats.
    /// </summary>
    /// <param name="discordService">The Discord notification service.</param>
    /// <param name="logger">Optional logger.</param>
    /// <returns>The number of streams for which metrics were sent.</returns>
    public static async Task<int> SendAllTsIndexerMetricsToDiscordAsync(
        IDiscordNotificationService discordService,
        ILogger? logger = null
    )
    {
        List<CircularBufferReadStream> activeStreams = [.. _activeStreams.Values];
        logger?.PluginLogInformation(
            "Sending MPEG-TS indexer metrics for {Count} active stream(s)",
            activeStreams.Count
        );
        var sentCount = 0;

        foreach (var stream in activeStreams)
        {
            try
            {
                var metrics = stream.GetTsIndexerMetrics();
                await discordService
                    .SendTsIndexerMetricsAsync(stream._streamId, stream._channelName, metrics)
                    .ConfigureAwait(false);
                sentCount++;
            }
            catch (Exception exception)
            {
                logger?.PluginLogError(
                    exception,
                    "Failed to send TS indexer metrics for stream {StreamId}",
                    stream._streamId
                );
            }
        }

        logger?.PluginLogInformation(
            "Successfully sent MPEG-TS metrics for {Sent}/{Total} stream(s)",
            sentCount,
            activeStreams.Count
        );
        return sentCount;
    }

    /// <summary>
    /// Sends buffer diagnostics for all active streams to Discord.
    /// </summary>
    /// <param name="discordService">The Discord notification service.</param>
    /// <param name="logger">Optional logger.</param>
    /// <returns>The number of streams for which diagnostics were sent.</returns>
    public static async Task<int> SendAllDiagnosticsToDiscordAsync(
        IDiscordNotificationService discordService,
        ILogger? logger = null
    )
    {
        List<CircularBufferReadStream> activeStreams = [.. _activeStreams.Values];
        logger?.PluginLogInformation("Sending buffer diagnostics for {Count} active stream(s)", activeStreams.Count);
        var sentCount = 0;

        foreach (var stream in activeStreams)
        {
            try
            {
                await discordService
                    .SendBufferDiagnosticsAsync(stream._streamId, stream._channelName, stream.GetDiagnostics())
                    .ConfigureAwait(false);
                sentCount++;
            }
            catch (Exception exception)
            {
                logger?.PluginLogError(exception, "Failed to send diagnostics for stream {StreamId}", stream._streamId);
            }
        }

        logger?.PluginLogInformation(
            "Successfully sent diagnostics for {Sent}/{Total} stream(s)",
            sentCount,
            activeStreams.Count
        );
        return sentCount;
    }

    /// <summary>
    /// Gets the count of active buffer streams.
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

            if (!stream._isDisposed)
            {
                try
                {
                    var gap = stream.CurrentGap;
                    var bufferSize = stream._sourceBuffer.BufferSize;
                    var totalWritten = stream._sourceBuffer.TotalBytesWritten;
                    var totalRead = stream.TotalBytesRead;
                    var gapPct = (double)gap * 100.0 / (double)bufferSize;
                    var overflowBytes = stream.TotalOverflowBytes;
                    var overflowCount = stream.OverflowCount;
                    var status = gapPct < 10.0 ? "Healthy" : (gapPct > 80.0 ? "Lagging" : "OK");

                    result.Add(
                        new StreamInfoSnapshot
                        {
                            StreamId = stream._streamId,
                            ChannelName = stream._channelName,
                            StartTime = stream._startTime,
                            BufferSizeBytes = bufferSize,
                            TotalBytesWritten = totalWritten,
                            TotalBytesRead = totalRead,
                            CurrentGapBytes = gap,
                            GapPercentage = gapPct,
                            OverflowCount = overflowCount,
                            OverflowBytes = overflowBytes,
                            Status = status,
                            IsAligned = stream._isAligned,
                        }
                    );
                }
                catch
                {
                    // Ignore errors for individual streams
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Kills (disposes) a stream by its ID.
    /// </summary>
    /// <param name="streamId">The stream ID to kill.</param>
    /// <returns>True if the stream was found and killed, false otherwise.</returns>
    public static bool KillStream(string streamId)
    {
        if (_activeStreams.TryGetValue(streamId, out var stream))
        {
            stream.Dispose();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Kills all active streams.
    /// </summary>
    /// <returns>The number of streams killed.</returns>
    public static int KillAllStreams()
    {
        var count = 0;

        foreach (var stream in _activeStreams.Values.ToList())
        {
            try
            {
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

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            _isDisposed = true;

            if (disposing)
            {
                // Record our final position so the next reader can continue from here
                // This enables seamless handoff from FFprobe to FFmpeg
                var finalPosition = ReadHead;
                if (finalPosition > 0)
                {
                    _sourceBuffer.RecordReaderDisconnect(finalPosition);
                }

                if (_activeStreams.TryRemove(_streamId, out _))
                {
                    _logger?.PluginLogInformation(
                        "Unregistered buffer reader for stream {StreamId} at position {Position} (remaining active: {Count})",
                        _streamId,
                        finalPosition,
                        _activeStreams.Count
                    );
                }
            }

            base.Dispose(disposing);
        }
    }
}
