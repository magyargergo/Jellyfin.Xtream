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

    private const long MinimumStartupFillBytes = 4194304L; // 4MB minimum before allowing reads
    private const int StartupWarmupTimeoutMs = 15000; // 15 seconds
    private const long ProgressLogIntervalBytes = 10 * 1024 * 1024; // Log every 10MB read

    // Stall detection constants
    // MaxStallWaitMs: 30 seconds - allows time for reconnection
    // ReconnectionWaitMs: extra grace period during active reconnection (10 seconds)
    private const int MaxStallWaitMs = 30000;
    private const int ReconnectionWaitMs = 10000;

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
    private readonly DateTime _startTime = DateTime.UtcNow;

    private CacheLinePadded _readHead;
    private CacheLinePadded _totalOverflowBytes;
    private CacheLinePaddedInt _overflowCount;
    private long _lastSeenWriteHead;
    private int _stallDetectionCount;
    private int _lastSeenDiscontinuityCount;
    private bool _isDisposed;
    private DateTime _lastOverflowLog = DateTime.MinValue;
    private DateTime _lastStallLog = DateTime.MinValue;
    private DateTime _lastStarvedLog = DateTime.MinValue;
    private DateTime _lastPredictorUpdate = DateTime.MinValue;
    private DateTime _lastDiscontinuityWaitLog = DateTime.MinValue;
    private DateTime _lastDiscontinuityHandledTime = DateTime.MinValue;
    private long _lastProgressLogBytes;

    /// <summary>
    /// Minimum time in seconds between processing consecutive discontinuities.
    /// This prevents rapid jumping when source streams have multiple inherent discontinuities.
    /// </summary>
    private const double MinDiscontinuityIntervalSeconds = 3.0;

    // Overflow prediction for proactive warning
    private readonly OverflowPredictor _overflowPredictor;

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
    public CircularBufferReadStream(
        CircularBufferWriteStream sourceBuffer,
        ILogger? logger = null,
        string streamId = "unknown",
        string? channelName = null,
        IDiscordNotificationService? discordService = null
    )
    {
        _sourceBuffer = sourceBuffer;
        _logger = logger;
        _streamId = streamId;
        _channelName = channelName ?? streamId;
        _discordService = discordService;
        var bufSize = sourceBuffer.BufferSize;
        _isPowerOfTwo = (bufSize & (bufSize - 1)) == 0;
        _bufferMask = bufSize - 1;
        var totalWritten = sourceBuffer.TotalBytesWritten;
        long bufferSize = sourceBuffer.BufferSize;

        // Check if there's a recent reader position we can continue from
        // This enables seamless handoff from FFprobe to FFmpeg
        var continuationPosition = sourceBuffer.ConsumeLastReaderPosition(10000);

        if (continuationPosition > 0)
        {
            _initialReadHead = continuationPosition;
            _logger?.PluginLogInformation(
                "Reader for stream {StreamId} continuing from previous reader position {Offset} ({GapMB:F1}MB behind live)",
                _streamId,
                continuationPosition,
                (double)(totalWritten - continuationPosition) / 1048576.0
            );
        }
        else
        {
            // Start at 1/4 buffer behind live to give a comfortable read margin
            _initialReadHead = Math.Max(0, totalWritten - bufferSize / 4);
            _logger?.LogDebugIfEnabled(
                "Reader for stream {StreamId} initialized at {Offset} ({GapMB:F1}MB behind live)",
                _streamId,
                _initialReadHead,
                (double)(totalWritten - _initialReadHead) / 1048576.0
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
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadFromCircularBuffer(new Span<byte>(buffer, offset, count));

    /// <inheritdoc />
    public override int Read(Span<byte> buffer) => ReadFromCircularBuffer(buffer);

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var currentReadHead = ReadHead;
        var gap = _sourceBuffer.TotalBytesWritten - currentReadHead;

        // Simple warmup: wait until we have enough data available after our read position
        var needsWarmup = TotalBytesRead == 0L && gap < MinimumStartupFillBytes;

        if (needsWarmup)
        {
            _logger?.LogDebugIfEnabled(
                "Stream {StreamId}: Warming up buffer - {CurrentKB}KB filled. Waiting for {RequiredMB}MB...",
                _streamId,
                gap / 1024,
                MinimumStartupFillBytes / 1048576
            );
            var warmupStart = DateTime.UtcNow;
            var pollCount = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                var elapsedMs = (DateTime.UtcNow - warmupStart).TotalMilliseconds;

                if (gap >= MinimumStartupFillBytes)
                {
                    break;
                }

                // Timeout: proceed anyway to avoid infinite wait
                if (elapsedMs > StartupWarmupTimeoutMs)
                {
                    _logger?.PluginLogWarning(
                        "Stream {StreamId}: Warmup timeout after {TimeMs}ms. Buffer={FilledKB}KB. Starting playback anyway.",
                        _streamId,
                        elapsedMs,
                        gap / 1024
                    );
                    break;
                }

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                pollCount++;
                currentReadHead = ReadHead;
                gap = _sourceBuffer.TotalBytesWritten - currentReadHead;

                if (pollCount % 20 == 0)
                {
                    var fillPct = (double)gap * 100.0 / MinimumStartupFillBytes;
                    _logger?.LogDebugIfEnabled(
                        "Stream {StreamId}: Buffering... {FillPct:F1}% ({CurrentKB}KB)",
                        _streamId,
                        fillPct,
                        gap / 1024
                    );
                }
            }

            if (gap >= MinimumStartupFillBytes)
            {
                var warmupDuration = (DateTime.UtcNow - warmupStart).TotalMilliseconds;
                _logger?.PluginLogInformation(
                    "Stream {StreamId}: Buffer warmup complete in {DurationMs}ms. {FilledMB:F1}MB buffered. Starting playback.",
                    _streamId,
                    warmupDuration,
                    (double)gap / 1048576.0
                );
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

        return ReadFromCircularBuffer(buffer.Span);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private unsafe int ReadFromCircularBuffer(Span<byte> destination)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

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

        // Check for stream discontinuity (reconnection after EOF/error)
        var currentDiscontinuityCount = _sourceBuffer.DiscontinuityCount;
        var discontinuityOffset = _sourceBuffer.LastDiscontinuityOffset;

        if (currentDiscontinuityCount > _lastSeenDiscontinuityCount)
        {
            // Rate-limit discontinuity processing to prevent rapid jumping
            var now = DateTime.UtcNow;
            var timeSinceLastHandled = (now - _lastDiscontinuityHandledTime).TotalSeconds;
            if (
                timeSinceLastHandled < MinDiscontinuityIntervalSeconds
                && _lastDiscontinuityHandledTime != DateTime.MinValue
            )
            {
                _lastSeenDiscontinuityCount = currentDiscontinuityCount;
                _logger?.LogDebugIfEnabled(
                    "Stream {StreamId}: Suppressing rapid discontinuity #{Count} ({TimeSince:F1}s since last, minimum {MinInterval:F0}s)",
                    _streamId,
                    currentDiscontinuityCount,
                    timeSinceLastHandled,
                    MinDiscontinuityIntervalSeconds
                );
            }
            else
            {
                _lastSeenDiscontinuityCount = currentDiscontinuityCount;
                _lastDiscontinuityHandledTime = now;
                var oldReadHead = currentReadHead;

                // Wait for fresh data to arrive after the discontinuity point
                const long MinFreshDataBytes = 1048576; // 1MB minimum fresh data
                var freshDataAvailable = totalWritten - discontinuityOffset;

                if (freshDataAvailable < MinFreshDataBytes)
                {
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

                    _lastSeenDiscontinuityCount = currentDiscontinuityCount - 1;
                    return 0;
                }

                // Jump directly to the discontinuity offset
                currentReadHead = discontinuityOffset;

                // Ensure we don't go past write head
                if (currentReadHead > totalWritten)
                {
                    currentReadHead = totalWritten;
                }

                _ = Interlocked.Exchange(ref _readHead.Value, currentReadHead);
                gap = totalWritten - currentReadHead;

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
            }
        }
        else if (discontinuityOffset > 0 && currentReadHead < discontinuityOffset)
        {
            // Reader is behind discontinuity point - skip forward to avoid reading stale data
            currentReadHead = discontinuityOffset;

            if (currentReadHead > totalWritten)
            {
                currentReadHead = totalWritten;
            }

            _ = Interlocked.Exchange(ref _readHead.Value, currentReadHead);
            gap = totalWritten - currentReadHead;

            _logger?.PluginLogInformation(
                "Stream {StreamId}: Skipped past discontinuity to {NewOffset} to avoid stale data",
                _streamId,
                currentReadHead
            );
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

        return $"Stream {_streamId} Diagnostics:\n  Buffer Size: {_sourceBuffer.BufferSize / 1048576}MB\n  Total Written: {totalWritten / 1048576}MB\n  Total Read: {totalRead / 1048576}MB\n  Current Gap: {gap / 1024}KB ({gapPct:F1}% of buffer)\n  Buffer Overflows: {Volatile.Read(ref _overflowCount.Value)} events\n  Data Lost: {overflowBytes / 1048576}MB ({overflowRate:F2}% of total)\n  Hardware: AVX2={_avx2Supported}, SSE2={_sse2Supported}, SIMD={Vector.IsHardwareAccelerated}\n  Status: {((double)gap < (double)_sourceBuffer.BufferSize * 0.1 ? "HEALTHY" : ((double)gap > (double)_sourceBuffer.BufferSize * 0.8 ? "LAGGING" : "OK"))}";
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
