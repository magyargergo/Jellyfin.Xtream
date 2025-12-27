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
using Jellyfin.Xtream.Service.ProviderManagement;
using Jellyfin.Xtream.Utility;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Circular buffer read stream with optimized multi-reader support.
/// Ultra-optimized using unsafe code, direct memory operations, SIMD vectorization, and aggressive inlining.
/// Optimized for multi-core systems with cache-line awareness and hardware acceleration.
///
/// Thread Safety: This class supports MULTIPLE CONCURRENT READERS.
/// - Each instance maintains its own read head position.
/// - Uses atomic operations (Interlocked) for read head updates.
/// - Memory barriers ensure buffer data visibility across cores.
/// - Graceful cancellation handling for async operations.
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
    private const long MinimumStartupFillBytes = 4194304L;
    private const int StartupWarmupTimeoutMs = 10000;

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
    private static readonly ConcurrentDictionary<string, CircularBufferReadStream> _activeStreams =
        new ConcurrentDictionary<string, CircularBufferReadStream>(StringComparer.Ordinal);

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

    /// <inheritdoc />
    public override long Position
    {
        get
        {
            long head = ReadHead;
            return _isPowerOfTwo ? (head & _bufferMask) : (head % _sourceBuffer.BufferSize);
        }
        set { throw new NotSupportedException(); }
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override long Length
    {
        get { throw new NotSupportedException(); }
    }

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
        int bufSize = sourceBuffer.BufferSize;
        _isPowerOfTwo = (bufSize & (bufSize - 1)) == 0;
        _bufferMask = bufSize - 1;
        long totalWritten = sourceBuffer.TotalBytesWritten;
        long bufferSize = sourceBuffer.BufferSize;

        if (totalWritten > bufferSize)
        {
            long targetLagBytes = Math.Min(bufferSize / 4, 4194304L);
            long targetOffset = totalWritten - targetLagBytes;
            long minValidOffset = totalWritten - bufferSize + 524288;

            if (targetOffset < minValidOffset)
            {
                targetOffset = minValidOffset;
            }

            SyncPoint? syncPoint = sourceBuffer.TsIndexer.GetBestSyncPoint(targetOffset, _programNumber);

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
            }
            else
            {
                long keyframeOffset = sourceBuffer.TsIndexer.GetBestStartOffset(targetOffset, _programNumber);

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
                }
                else
                {
                    long safetyMargin = Math.Max(524288L, bufferSize / 32);
                    long startGap = Math.Min(targetLagBytes, bufferSize - safetyMargin);
                    _initialReadHead = totalWritten - startGap;
                    _logger?.LogWarning(
                        "Reader for stream {StreamId} (program {ProgramNumber}) could not find keyframe (video PID: {VideoPid}). Falling back to byte alignment at {GapMB:F1}MB behind.",
                        _streamId,
                        _programNumber,
                        sourceBuffer.TsIndexer.GetVideoPid(_programNumber),
                        (double)startGap / 1048576.0
                    );
                }
            }
        }
        else
        {
            _initialReadHead = 0L;
            _logger?.LogDebugIfEnabled(
                "Reader for stream {StreamId} initialized at buffer start (fresh stream)",
                _streamId
            );
        }

        _readHead.Value = _initialReadHead;
        _activeStreams.TryAdd(_streamId, this);

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
        _activeStreams.TryRemove(_streamId, out _);
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadSpan(new Span<byte>(buffer, offset, count));
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        return ReadSpan(buffer);
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        long currentReadHead = ReadHead;
        long gap = _sourceBuffer.TotalBytesWritten - currentReadHead;

        if (TotalBytesRead == 0L && gap < MinimumStartupFillBytes)
        {
            _logger?.LogDebugIfEnabled(
                "Stream {StreamId}: Warming up buffer - {CurrentKB}KB / {TargetKB}KB filled. Waiting for minimum threshold...",
                _streamId,
                gap / 1024,
                4096L
            );
            DateTime warmupStart = DateTime.UtcNow;
            int pollCount = 0;

            while (gap < MinimumStartupFillBytes && !cancellationToken.IsCancellationRequested)
            {
                if ((DateTime.UtcNow - warmupStart).TotalMilliseconds > StartupWarmupTimeoutMs)
                {
                    _logger?.LogWarning(
                        "Stream {StreamId}: Warmup timeout after {TimeMs}ms with only {FilledKB}KB buffered. Starting playback anyway. This may cause initial stuttering.",
                        _streamId,
                        (DateTime.UtcNow - warmupStart).TotalMilliseconds,
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
                    double fillPct = (double)gap * 100.0 / MinimumStartupFillBytes;
                    _logger?.LogDebugIfEnabled(
                        "Stream {StreamId}: Buffering... {FillPct:F1}% ({CurrentKB}KB / {TargetKB}KB)",
                        _streamId,
                        fillPct,
                        gap / 1024,
                        4096L
                    );
                }
            }

            if (gap >= MinimumStartupFillBytes)
            {
                double warmupDuration = (DateTime.UtcNow - warmupStart).TotalMilliseconds;
                _logger?.LogInformation(
                    "Stream {StreamId}: Buffer warmup complete in {DurationMs}ms. {FilledMB:F1}MB buffered. Starting playback.",
                    _streamId,
                    warmupDuration,
                    (double)gap / 1048576.0
                );

                if (!_isAligned)
                {
                    long totalWritten = _sourceBuffer.TotalBytesWritten;
                    long targetLagBytes = Math.Min(_sourceBuffer.BufferSize / 4, 4194304);
                    long targetOffset = totalWritten - targetLagBytes;
                    long minValidOffset = totalWritten - _sourceBuffer.BufferSize + 524288;

                    if (targetOffset < minValidOffset)
                    {
                        targetOffset = minValidOffset;
                    }

                    long keyframeOffset = _sourceBuffer.TsIndexer.GetBestStartOffset(targetOffset, _programNumber);

                    if (keyframeOffset != -1 && keyframeOffset >= minValidOffset)
                    {
                        Interlocked.Exchange(ref _readHead.Value, keyframeOffset);
                        currentReadHead = keyframeOffset;
                        gap = totalWritten - currentReadHead;
                        _isAligned = true;
                        _logger?.LogInformation(
                            "Stream {StreamId}: Post-warmup KEYFRAME alignment successful at offset {Offset} ({GapMB:F1}MB behind live). Video should now be visible.",
                            _streamId,
                            keyframeOffset,
                            (double)gap / 1048576.0
                        );
                    }
                    else
                    {
                        int videoPid = _sourceBuffer.TsIndexer.GetVideoPid(_programNumber);
                        int keyframeCount = _sourceBuffer.TsIndexer.GetKeyframeCount(_programNumber);
                        _logger?.LogWarning(
                            "Stream {StreamId}: Post-warmup keyframe alignment failed. VideoPID={VideoPid}, KeyframeCount={KeyframeCount}. Video may not display correctly.",
                            _streamId,
                            videoPid,
                            keyframeCount
                        );
                    }
                }
            }
        }

        SpinWait spinWait = default;
        DateTime startWaitTime = gap == 0L ? DateTime.UtcNow : DateTime.MinValue;
        int yieldCount = 0;
        long lastSeenWrite = _sourceBuffer.TotalBytesWritten;
        int asyncStallCount = 0;

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

                    for (int i = 0; i < 5; i++)
                    {
                        long latestWrite = _sourceBuffer.TotalBytesWritten;
                        if (latestWrite > currentReadHead)
                        {
                            break;
                        }

                        Thread.SpinWait(100);
                    }

                    long currentWrite = _sourceBuffer.TotalBytesWritten;

                    if (currentWrite == lastSeenWrite)
                    {
                        asyncStallCount++;

                        // Check connection state to determine how long to wait
                        bool isReconnecting = _sourceBuffer.IsReconnecting;
                        DateTime lastWriteTime = _sourceBuffer.LastWriteTime;
                        double msSinceLastWrite =
                            lastWriteTime != default
                                ? (DateTime.UtcNow - lastWriteTime).TotalMilliseconds
                                : asyncStallCount * 10.0; // Fallback estimate

                        // Determine max wait time based on connection state
                        int maxWaitMs = isReconnecting ? MaxStallWaitMs + ReconnectionWaitMs : MaxStallWaitMs;

                        if (msSinceLastWrite > maxWaitMs)
                        {
                            // Exceeded maximum wait time - source is likely permanently dead
                            if (!_isDisposed)
                            {
                                _logger?.LogError(
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
                            // Only log warning if stream is still active (not during disposal/cleanup)
                            // This prevents noise during normal stream transitions (e.g., after ffprobe)
                            if (!_isDisposed)
                            {
                                var now = DateTime.UtcNow;
                                if ((now - _lastStallLog).TotalSeconds >= 30)
                                {
                                    _lastStallLog = now;
                                    _logger?.LogWarning(
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
                            int waitMs = isReconnecting ? 1000 : 500;
                            try
                            {
                                await Task.Delay(waitMs, cancellationToken).ConfigureAwait(false);
                            }
                            catch (TaskCanceledException)
                            {
                                return 0;
                            }

                            // Check if new data arrived during the wait
                            long newWrite = _sourceBuffer.TotalBytesWritten;
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
                double waitTime = (now - startWaitTime).TotalMilliseconds;
                if (waitTime > 50.0 && (now - _lastStarvedLog).TotalSeconds >= 30)
                {
                    _lastStarvedLog = now;
                    _logger?.LogWarning(
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

        long currentReadHead = Volatile.Read(ref _readHead.Value);
        long totalWritten = _sourceBuffer.TotalBytesWritten;
        Thread.MemoryBarrier();
        long gap = totalWritten - currentReadHead;
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
                    _logger?.LogWarning(
                        "Writer stall detected on stream {StreamId}: Write head stuck at {WriteHead} for {Count} reads. Reader at {ReadHead}, gap={GapKB}KB. Skipping forward to prevent replay loop.",
                        _streamId,
                        totalWritten,
                        _stallDetectionCount,
                        currentReadHead,
                        gap / 1024
                    );
                    Interlocked.Exchange(ref _readHead.Value, totalWritten);
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
            Interlocked.Exchange(ref _readHead.Value, currentReadHead);
            gap = totalWritten - currentReadHead;
            _isAligned = true;
        }

        // Check for stream discontinuity (reconnection after EOF/error)
        // We track by count, not position, because the reader might be AHEAD of the discontinuity
        // point if it was consuming buffered data when the source disconnected. In that case,
        // the buffered data after the discontinuity point contains OLD frames that will cause
        // video loops when ffmpeg decodes them (timestamps jump backward).
        int currentDiscontinuityCount = _sourceBuffer.DiscontinuityCount;
        long discontinuityOffset = _sourceBuffer.LastDiscontinuityOffset;

        if (currentDiscontinuityCount > _lastSeenDiscontinuityCount)
        {
            _lastSeenDiscontinuityCount = currentDiscontinuityCount;
            long oldReadHead = currentReadHead;

            // CRITICAL: We must wait for fresh data to arrive AFTER the discontinuity point.
            // The discontinuity offset marks where new data STARTS being written after reconnection.
            // We need at least 1MB of fresh data before we can safely resume reading.
            const long MinFreshDataBytes = 1048576; // 1MB minimum fresh data
            long freshDataAvailable = totalWritten - discontinuityOffset;

            if (freshDataAvailable < MinFreshDataBytes)
            {
                // Not enough fresh data yet - return 0 to make reader wait
                _logger?.LogInformation(
                    "Stream {StreamId}: Discontinuity #{Count} detected, waiting for fresh data ({FreshKB:F0}KB/{RequiredKB}KB available)",
                    _streamId,
                    currentDiscontinuityCount,
                    freshDataAvailable / 1024.0,
                    MinFreshDataBytes / 1024
                );
                // Reset the count so we check again next read
                _lastSeenDiscontinuityCount = currentDiscontinuityCount - 1;
                return 0;
            }

            // Now we have enough fresh data - skip to fresh data after discontinuity
            // Start from discontinuity offset + small margin to ensure we're in fresh data territory
            long targetOffset = discontinuityOffset + 188 * 10; // Skip a few TS packets past discontinuity
            long keyframeOffset = _sourceBuffer.TsIndexer.GetBestStartOffset(targetOffset, _programNumber);

            if (keyframeOffset != -1 && keyframeOffset >= discontinuityOffset)
            {
                currentReadHead = keyframeOffset;
            }
            else
            {
                // No keyframe found, start from just after discontinuity
                currentReadHead = targetOffset;
            }

            // Ensure we don't go past write head
            if (currentReadHead > totalWritten)
            {
                currentReadHead = totalWritten;
            }

            Interlocked.Exchange(ref _readHead.Value, currentReadHead);
            gap = totalWritten - currentReadHead;

            double skippedMB = (double)(currentReadHead - oldReadHead) / 1048576.0;
            _logger?.LogInformation(
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
        else if (discontinuityOffset > 0 && currentReadHead < discontinuityOffset)
        {
            // Reader is behind discontinuity point - skip forward to avoid reading stale data
            long targetOffset = discontinuityOffset + 188 * 10;
            long keyframeOffset = _sourceBuffer.TsIndexer.GetBestStartOffset(targetOffset, _programNumber);

            if (keyframeOffset != -1 && keyframeOffset >= discontinuityOffset)
            {
                currentReadHead = keyframeOffset;
            }
            else
            {
                currentReadHead = targetOffset;
            }

            if (currentReadHead > totalWritten)
            {
                currentReadHead = totalWritten;
            }

            Interlocked.Exchange(ref _readHead.Value, currentReadHead);
            gap = totalWritten - currentReadHead;

            _logger?.LogInformation(
                "Stream {StreamId}: Skipped past discontinuity from {OldOffset} to {NewOffset} to avoid stale data",
                _streamId,
                Volatile.Read(ref _readHead.Value),
                currentReadHead
            );
        }

        if (gap > _sourceBuffer.BufferSize)
        {
            long bytesLost = gap - _sourceBuffer.BufferSize;
            long safetyMargin = Math.Max(524288, _sourceBuffer.BufferSize >> 4);
            long safeGap = _sourceBuffer.BufferSize - safetyMargin;
            long newReadHead = totalWritten - safeGap;
            SyncPoint? syncPoint = _sourceBuffer.TsIndexer.GetBestSyncPoint(newReadHead, _programNumber);

            if (syncPoint.HasValue && syncPoint.Value.Offset > newReadHead - safetyMargin)
            {
                newReadHead = syncPoint.Value.Offset;
            }
            else
            {
                long keyframeOffset = _sourceBuffer.TsIndexer.GetBestStartOffset(newReadHead, _programNumber);
                if (keyframeOffset != -1 && keyframeOffset > newReadHead - safetyMargin)
                {
                    newReadHead = keyframeOffset;
                }
            }

            long originalHead = Interlocked.CompareExchange(ref _readHead.Value, newReadHead, currentReadHead);

            if (originalHead == currentReadHead)
            {
                currentReadHead = newReadHead;
                gap = safeGap;
                Interlocked.Add(ref _totalOverflowBytes.Value, bytesLost);
                int currentOverflowCount = Interlocked.Increment(ref _overflowCount.Value);
                DateTime now = DateTime.UtcNow;

                if ((now - _lastOverflowLog).TotalSeconds >= 5.0 || currentOverflowCount == 1)
                {
                    double lostMB = (double)bytesLost / 1048576.0;
                    double totalLostMB = (double)Volatile.Read(ref _totalOverflowBytes.Value) / 1048576.0;
                    _logger?.LogInformation(
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

        int totalRead = 0;
        int remaining = (int)Math.Min(destination.Length, gap);
        long startingReadHead = currentReadHead;

        fixed (byte* dstPtr = destination)
        {
            fixed (byte* srcBuffer = _sourceBuffer.Buffer)
            {
                while (remaining > 0)
                {
                    long currentPosition = _isPowerOfTwo
                        ? (currentReadHead & _bufferMask)
                        : (currentReadHead % _sourceBuffer.BufferSize);
                    long bytesToEndOfBuffer = _sourceBuffer.BufferSize - currentPosition;
                    int chunkSize = (int)Math.Min(remaining, bytesToEndOfBuffer);
                    totalWritten = _sourceBuffer.TotalBytesWritten;
                    long currentGap = totalWritten - currentReadHead;

                    if (currentGap > _sourceBuffer.BufferSize)
                    {
                        _logger?.LogWarning(
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

                            for (int i = 0; i < 10; i++)
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
            // The TimestampTracker still monitors drift for diagnostics/Discord notifications.

            long expectedHead = startingReadHead;
            long newHead = startingReadHead + totalRead;
            long actualHead = Interlocked.CompareExchange(ref _readHead.Value, newHead, expectedHead);

            if (actualHead != expectedHead)
            {
                _logger?.LogDebug(
                    "Read head was modified during read for stream {StreamId}. Expected {Expected}, found {Actual}. Read {Bytes} bytes. This is normal during overflow recovery.",
                    _streamId,
                    expectedHead,
                    actualHead,
                    totalRead
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
        int offset = 0;

        if (_avx512Supported && length >= 64)
        {
            for (int avx512Length = length & -64; offset < avx512Length; offset += 64)
            {
                if (offset + _prefetchDistance < length)
                {
                    Sse.Prefetch0(src + offset + _prefetchDistance);
                }

                Vector512<byte> vec = Avx512F.LoadVector512(src + offset);
                Avx512F.Store(dst + offset, vec);
            }
        }
        else if (_avx2Supported && length >= 32)
        {
            for (int avx2Length = length & -32; offset < avx2Length; offset += 32)
            {
                if (Sse.IsSupported && offset + _prefetchDistance < length)
                {
                    Sse.Prefetch0(src + offset + _prefetchDistance);
                }

                Vector256<byte> vec = Avx.LoadVector256(src + offset);
                Avx.Store(dst + offset, vec);
            }
        }
        else if (_sse2Supported && length >= 16)
        {
            for (int sse2Length = length & -16; offset < sse2Length; offset += 16)
            {
                if (Sse.IsSupported && offset + _prefetchDistance < length)
                {
                    Sse.Prefetch0(src + offset + _prefetchDistance);
                }

                Vector128<byte> vec = Sse2.LoadVector128(src + offset);
                Sse2.Store(dst + offset, vec);
            }
        }
        else if (Vector.IsHardwareAccelerated && length >= Vector<byte>.Count)
        {
            for (
                int vectorLength = length & ~(Vector<byte>.Count - 1);
                offset < vectorLength;
                offset += Vector<byte>.Count
            )
            {
                Vector<byte> vec = Unsafe.ReadUnaligned<Vector<byte>>(src + offset);
                Unsafe.WriteUnaligned(dst + offset, vec);
            }
        }

        int remaining = length - offset;

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
        if (Avx2.IsSupported)
        {
            return 512;
        }

        if (Sse2.IsSupported)
        {
            return 1024;
        }

        return 4096;
    }

    /// <summary>
    /// Determines optimal prefetch distance based on CPU capabilities.
    /// Smaller caches on low-end CPUs need shorter prefetch distance to avoid cache pollution.
    /// </summary>
    private static int DeterminePrefetchDistance()
    {
        return Avx2.IsSupported ? 256 : 128;
    }

    /// <summary>
    /// Determines maximum spinning iterations based on CPU capabilities.
    /// Lower-end CPUs get fewer spins to avoid wasting cycles.
    /// </summary>
    private static int DetermineMaxSpins()
    {
        if (Avx2.IsSupported)
        {
            return 100;
        }

        if (Sse2.IsSupported)
        {
            return 50;
        }

        return 25;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryAlignToMpegTsSync(ref long readHead, long totalWritten)
    {
        long available = totalWritten - readHead;

        if (available < 564)
        {
            return false;
        }

        int maxScan = (int)Math.Min(8192L, available - 376);

        if (maxScan <= 0)
        {
            return false;
        }

        byte[] buffer = _sourceBuffer.Buffer;
        int bufSize = _sourceBuffer.BufferSize;

        for (int offset = 0; offset < maxScan; offset++)
        {
            int p0 = (int)(_isPowerOfTwo ? ((readHead + offset) & _bufferMask) : ((readHead + offset) % bufSize));
            int p1 = (int)(
                _isPowerOfTwo
                    ? ((readHead + offset + TsPacketSize) & _bufferMask)
                    : ((readHead + offset + TsPacketSize) % bufSize)
            );
            int p2 = (int)(
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
    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

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

        Task.Run(async () =>
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
        long gap = CurrentGap;
        long totalWritten = _sourceBuffer.TotalBytesWritten;
        long totalRead = TotalBytesRead;
        double gapPct = (double)gap * 100.0 / (double)_sourceBuffer.BufferSize;
        long overflowBytes = Volatile.Read(ref _totalOverflowBytes.Value);
        double overflowRate = totalWritten > 0 ? (double)overflowBytes * 100.0 / (double)totalWritten : 0.0;

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
            _logger?.LogWarning(
                "Cannot send diagnostics to Discord: Discord service not configured for stream {StreamId}",
                _streamId
            );
            return;
        }

        try
        {
            string diagnostics = GetDiagnostics();
            await _discordService
                .SendBufferDiagnosticsAsync(_streamId, _channelName, diagnostics)
                .ConfigureAwait(false);
            _logger?.LogInformation("Buffer diagnostics sent to Discord for stream {StreamId}", _streamId);
        }
        catch (Exception exception)
        {
            _logger?.LogError(
                exception,
                "Failed to send buffer diagnostics to Discord for stream {StreamId}",
                _streamId
            );
        }
    }

    /// <summary>
    /// Gets comprehensive MPEG-TS indexer diagnostics for this stream.
    /// Includes packet loss, PCR jitter, transport errors, and program information.
    /// </summary>
    /// <returns>A formatted string with MPEG-TS diagnostics.</returns>
    public string GetTsIndexerDiagnostics() => _sourceBuffer.TsIndexer.GetDiagnostics();

    /// <summary>
    /// Sends MPEG-TS indexer diagnostics to Discord.
    /// Reports stream health metrics including packet loss, PCR jitter, and transport errors.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task SendTsIndexerDiagnosticsToDiscordAsync()
    {
        if (_discordService == null)
        {
            _logger?.LogWarning(
                "Cannot send TS indexer diagnostics to Discord: Discord service not configured for stream {StreamId}",
                _streamId
            );
            return;
        }

        try
        {
            string diagnostics = GetTsIndexerDiagnostics();
            await _discordService
                .SendTsIndexerDiagnosticsAsync(_streamId, _channelName, diagnostics)
                .ConfigureAwait(false);
            _logger?.LogInformation("MPEG-TS indexer diagnostics sent to Discord for stream {StreamId}", _streamId);
        }
        catch (Exception exception)
        {
            _logger?.LogError(
                exception,
                "Failed to send TS indexer diagnostics to Discord for stream {StreamId}",
                _streamId
            );
        }
    }

    /// <summary>
    /// Sends MPEG-TS indexer diagnostics for all active streams to Discord.
    /// Reports stream health including packet loss, PCR jitter, transport errors, and keyframe stats.
    /// </summary>
    /// <param name="discordService">The Discord notification service.</param>
    /// <param name="logger">Optional logger.</param>
    /// <returns>The number of streams for which diagnostics were sent.</returns>
    public static async Task<int> SendAllTsIndexerDiagnosticsToDiscordAsync(
        IDiscordNotificationService discordService,
        ILogger? logger = null
    )
    {
        List<CircularBufferReadStream> activeStreams = _activeStreams.Values.ToList();
        logger?.LogInformation("Sending MPEG-TS indexer diagnostics for {Count} active stream(s)", activeStreams.Count);
        int sentCount = 0;

        foreach (CircularBufferReadStream stream in activeStreams)
        {
            try
            {
                await discordService
                    .SendTsIndexerDiagnosticsAsync(
                        stream._streamId,
                        stream._channelName,
                        stream.GetTsIndexerDiagnostics()
                    )
                    .ConfigureAwait(false);
                sentCount++;
            }
            catch (Exception exception)
            {
                logger?.LogError(
                    exception,
                    "Failed to send TS indexer diagnostics for stream {StreamId}",
                    stream._streamId
                );
            }
        }

        logger?.LogInformation(
            "Successfully sent MPEG-TS diagnostics for {Sent}/{Total} stream(s)",
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
        List<CircularBufferReadStream> activeStreams = _activeStreams.Values.ToList();
        logger?.LogInformation("Sending buffer diagnostics for {Count} active stream(s)", activeStreams.Count);
        int sentCount = 0;

        foreach (CircularBufferReadStream stream in activeStreams)
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
                logger?.LogError(exception, "Failed to send diagnostics for stream {StreamId}", stream._streamId);
            }
        }

        logger?.LogInformation(
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

        foreach (KeyValuePair<string, CircularBufferReadStream> activeStream in _activeStreams)
        {
            CircularBufferReadStream stream = activeStream.Value;

            if (!stream._isDisposed)
            {
                try
                {
                    long gap = stream.CurrentGap;
                    int bufferSize = stream._sourceBuffer.BufferSize;
                    long totalWritten = stream._sourceBuffer.TotalBytesWritten;
                    long totalRead = stream.TotalBytesRead;
                    double gapPct = (double)gap * 100.0 / (double)bufferSize;
                    long overflowBytes = stream.TotalOverflowBytes;
                    int overflowCount = stream.OverflowCount;
                    string status = gapPct < 10.0 ? "Healthy" : (gapPct > 80.0 ? "Lagging" : "OK");

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
        if (_activeStreams.TryGetValue(streamId, out CircularBufferReadStream? stream))
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
        int count = 0;

        foreach (CircularBufferReadStream stream in _activeStreams.Values.ToList())
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

            if (disposing && _activeStreams.TryRemove(_streamId, out _))
            {
                _logger?.LogInformation(
                    "Unregistered buffer reader for stream {StreamId} (remaining active: {Count})",
                    _streamId,
                    _activeStreams.Count
                );
            }

            base.Dispose(disposing);
        }
    }
}
