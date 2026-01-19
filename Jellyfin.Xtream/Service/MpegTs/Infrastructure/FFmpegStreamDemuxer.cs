// Copyright (C) 2025  Gergo Magyar
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen.Abstractions;
using Jellyfin.Xtream.Service.MpegTs.UseCases;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.MpegTs.Infrastructure;

/// <summary>
/// FFmpeg-based MPEG-TS demuxer using custom AVIOContext for memory-based input.
/// Uses a background task for demuxing to avoid blocking the data write path.
/// Provides program/stream detection and packet demultiplexing using FFmpeg's libavformat.
/// </summary>
/// <remarks>
/// <para>
/// This demuxer uses a lock-free producer-consumer pattern:
/// </para>
/// <list type="bullet">
///   <item><description>Producer: FeedData() writes to circular buffer using atomic operations</description></item>
///   <item><description>Consumer: Background task reads from buffer via FFmpeg</description></item>
///   <item><description>Program data uses copy-on-write immutable dictionaries</description></item>
/// </list>
/// <para>
/// This design ensures HTTP reads are never blocked by FFmpeg operations or locks.
/// </para>
/// </remarks>
public sealed unsafe class FFmpegStreamDemuxer : FFmpegProcessorBase, ITsDemuxer
{
    private const int IoBufferSize = 32 * 1024; // 32KB IO buffer for AVIO context
    private const int MinDataForInit = 32 * 1024; // Minimum data before starting demuxer
    private const int DefaultInputBufferSize = 1024 * 1024; // 1MB default

    // FFmpeg contexts - only accessed from background thread after initialization
    private AVFormatContext* _formatContext;
    private AVIOContext* _ioContext;
    private byte* _ioBuffer;
    private AVPacket* _packet; // Reusable packet to avoid per-call allocation

    // Background processing
    private readonly SemaphoreSlim _dataAvailable = new(0, int.MaxValue);
    private CancellationTokenSource _cts = new();
    private Task? _processingTask;
    private int _processingStartedFlag; // 0 = not started, 1 = started (for CAS)

    // Delegate must be kept alive to prevent GC
    private avio_alloc_context_read_packet? _readCallback;

    // Lock for synchronizing FFmpeg context access during cleanup
    private readonly object _ffmpegLock = new();

    // Copy-on-write program data - immutable snapshots replaced atomically
    private volatile ProgramSnapshot _programSnapshot = ProgramSnapshot.Empty;
    private long _bytesProcessed;
    private volatile bool _initialized;

    // Rate-limiting for overflow logs
    private DateTime _lastOverflowLog = DateTime.MinValue;
    private long _totalOverflowBytes;

    // Rate-limiting for FeedData logs during init
    private DateTime _lastFeedDataLog = DateTime.MinValue;
    private long _totalFedBytes;

    /// <summary>
    /// Initializes a new instance of the <see cref="FFmpegStreamDemuxer"/> class.
    /// </summary>
    /// <param name="inputBufferSize">Size of the circular input buffer. Must be a power of 2 for efficient modulo.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <param name="ffmpegContext">Optional FFmpeg context. If null, uses the default adapter.</param>
    public FFmpegStreamDemuxer(
        int inputBufferSize = DefaultInputBufferSize,
        ILogger? logger = null,
        IFFmpegContext? ffmpegContext = null
    )
        : base(inputBufferSize, logger, ffmpegContext)
    {
        Logger?.LogDebugIfEnabled(
            "FFmpegStreamDemuxer created with {BufferSize} byte lock-free circular buffer",
            InputBuffer.Length
        );
    }

    /// <summary>
    /// Gets a value indicating whether the demuxer has been initialized with stream data.
    /// </summary>
    public bool IsInitialized => _initialized;

    /// <summary>
    /// Gets the detected programs in the stream (immutable snapshot).
    /// </summary>
    public IReadOnlyDictionary<int, FFmpegProgramInfo> Programs => _programSnapshot.Programs;

    /// <summary>
    /// Gets the number of detected programs.
    /// </summary>
    public int ProgramCount => _programSnapshot.Programs.Count;

    /// <summary>
    /// Occurs when program information is updated (PAT/PMT parsed).
    /// </summary>
    public event EventHandler<DemuxerProgramEventArgs>? ProgramDetected;

    /// <summary>
    /// Occurs when a packet is demuxed from the stream.
    /// </summary>
    public event EventHandler<DemuxedPacketEventArgs>? PacketDemuxed;

    /// <inheritdoc/>
    public IEnumerable<int> GetProgramNumbers()
    {
        var snapshot = _programSnapshot;
        foreach (var program in snapshot.Programs.Values)
        {
            if (program.ProgramNumber != 0) // Skip NIT
            {
                yield return program.ProgramNumber;
            }
        }
    }

    /// <inheritdoc/>
    public int GetPmtPid(int programNumber) =>
        _programSnapshot.Programs.TryGetValue(programNumber, out var program) ? program.PmtPid : -1;

    /// <summary>
    /// Feeds data to the demuxer for processing.
    /// This method is lock-free and non-blocking - data is written to a circular buffer
    /// and processed by a background task.
    /// </summary>
    /// <param name="data">MPEG-TS data chunk.</param>
    public void FeedData(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || IsDisposed)
        {
            return;
        }

        var bufferSize = InputBuffer.Length;
        var bufferMask = bufferSize - 1; // For power-of-2 modulo

        // Read current positions atomically
        var head = ReadInputHead();
        var tail = ReadInputTail();

        // Calculate available space
        var used = head - tail;
        var freeSpace = bufferSize - used;

        if (data.Length > freeSpace)
        {
            // Buffer overflow - in SPSC, producer can advance tail to make room
            // This discards oldest data that hasn't been consumed yet
            // This is expected during high-bitrate streams and is handled gracefully
            var discardAmount = data.Length - freeSpace;
            _ = AddInputTail(discardAmount);
            _totalOverflowBytes += discardAmount;

            // Rate-limit overflow logging to once per 10 seconds to avoid spam
            var now = DateTime.UtcNow;
            if ((now - _lastOverflowLog).TotalSeconds >= 10.0)
            {
                _lastOverflowLog = now;
                Logger?.LogDebugIfEnabled(
                    "FFmpeg demuxer input buffer overflow: discarded {Bytes} bytes this event, {TotalKB:F0}KB total (expected during high-bitrate streams)",
                    discardAmount,
                    _totalOverflowBytes / 1024.0
                );
            }
        }

        // Copy data to circular buffer (may wrap around)
        var writePos = (int)(head & bufferMask);
        var firstCopy = Math.Min(data.Length, bufferSize - writePos);

        data[..firstCopy].CopyTo(InputBuffer.AsSpan(writePos));

        if (firstCopy < data.Length)
        {
            // Wrap around to beginning of buffer
            data[firstCopy..].CopyTo(InputBuffer.AsSpan(0));
        }

        // Publish new head position with release semantics
        _ = AddInputHead(data.Length);
        _totalFedBytes += data.Length;

        // Log data feed progress during init (once per second)
        if (!_initialized)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastFeedDataLog).TotalSeconds >= 1.0)
            {
                _lastFeedDataLog = now;
                Logger?.LogDebugIfEnabled(
                    "FFmpeg demuxer: FeedData progress - {TotalKB:F0}KB fed, available={AvailableKB:F0}KB, initialized={Init}",
                    _totalFedBytes / 1024.0,
                    AvailableBytes / 1024.0,
                    _initialized
                );
            }
        }

        // Signal that data is available for the background task
        try
        {
            _ = _dataAvailable.Release();
        }
        catch (SemaphoreFullException)
        {
            // Semaphore is full, background task will catch up
        }

        // Start background processing task if not already started and we have enough data
        if (!IsProcessingStarted && AvailableBytes >= MinDataForInit)
        {
            StartBackgroundProcessing();
        }
    }

    /// <summary>
    /// Processes available data and updates program information.
    /// With background processing enabled, this method is a no-op as processing
    /// happens automatically in a background task.
    /// </summary>
    /// <returns>True if the demuxer is initialized and processing.</returns>
    public bool Process() =>
        // With background processing, this is just a status check
        // The actual processing happens in ProcessingLoop
        _initialized && IsProcessingStarted;

    /// <summary>
    /// Starts the background processing task using lock-free compare-and-swap.
    /// </summary>
    private void StartBackgroundProcessing()
    {
        if (IsDisposed)
        {
            return;
        }

        // Use compare-and-swap to ensure only one thread starts the task
        // This replaces the lock with an atomic operation
        if (Interlocked.CompareExchange(ref _processingStartedFlag, 1, 0) == 0)
        {
            _processingTask = Task.Run(() => ProcessingLoop(_cts.Token));
            Logger?.LogDebugIfEnabled("Background demuxer processing task started");
        }
    }

    private bool IsProcessingStarted => Volatile.Read(ref _processingStartedFlag) != 0;

    /// <summary>
    /// Background processing loop that reads packets from FFmpeg.
    /// Uses a polling approach instead of async/await since the class is unsafe.
    /// </summary>
    private void ProcessingLoop(CancellationToken cancellationToken)
    {
        try
        {
            // Initialize FFmpeg context
            if (!TryInitialize())
            {
                Logger?.PluginLogWarning("Failed to initialize FFmpeg demuxer in background task");
                return;
            }

            Logger?.PluginLogInformation("FFmpeg background demuxer loop started");

            while (!cancellationToken.IsCancellationRequested && !IsDisposed)
            {
                // Wait for data to be available (with short timeout for responsive cancellation)
                var gotSignal = _dataAvailable.Wait(50, cancellationToken);

                // Check disposed again after wait
                if (IsDisposed)
                {
                    break;
                }

                if (!gotSignal)
                {
                    // Timeout - check if we have data anyway (in case signal was missed)
                    if (AvailableBytes == 0)
                    {
                        continue;
                    }
                }

                // Process all available packets (check disposed between packets)
                while (!cancellationToken.IsCancellationRequested && !IsDisposed && ProcessOnePacket())
                {
                    // Continue processing while packets are available
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            Logger?.PluginLogError(ex, "Error in FFmpeg background demuxer loop");
        }
        finally
        {
            Logger?.LogDebugIfEnabled("FFmpeg background demuxer loop ended");
        }
    }

    /// <summary>
    /// Processes a single packet from FFmpeg.
    /// Uses a lock to synchronize with CleanupFFmpeg during reset/dispose.
    /// </summary>
    /// <returns>True if a packet was processed, false if no data available.</returns>
    private bool ProcessOnePacket()
    {
        // Check disposed first without lock to avoid contention
        if (IsDisposed)
        {
            return false;
        }

        lock (_ffmpegLock)
        {
            // Double-check after acquiring lock - also check packet is allocated
            if (_formatContext == null || _packet == null || IsDisposed)
            {
                return false;
            }

            // Read one packet using the reusable packet field
            // This avoids av_packet_alloc/av_packet_free overhead per packet
            var result = ffmpeg.av_read_frame(_formatContext, _packet);

            if (result >= 0)
            {
                // Packet read successfully - program info should be available
                UpdateProgramInfo();

                // Extract packet data and fire event
                EmitPacketEvent(_packet);

                // Reset packet for reuse (releases any referenced data)
                ffmpeg.av_packet_unref(_packet);
                return true;
            }

            if (result == ffmpeg.AVERROR_EOF)
            {
                return false;
            }

            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                // Need more data - this is expected, not an error
                return false;
            }

            // Other error - log but continue
            Logger?.LogTrace("av_read_frame returned {Error}", GetErrorMessage(result));
            return false;
        }
    }

    private void EmitPacketEvent(AVPacket* packet)
    {
        if (_formatContext == null || PacketDemuxed == null)
        {
            return;
        }

        var streamIndex = packet->stream_index;
        if (streamIndex < 0 || streamIndex >= (int)_formatContext->nb_streams)
        {
            return;
        }

        var stream = _formatContext->streams[streamIndex];

        // Get PID from stream ID
        var pid = stream->id;

        // Use copy-on-write snapshot for thread-safe access
        var snapshot = _programSnapshot;

        // Get program number from mapping
        _ = snapshot.StreamIndexToProgram.TryGetValue(streamIndex, out var programNumber);
        if (programNumber == 0)
        {
            // Try to find program number from the first available program
            foreach (var prog in snapshot.Programs.Values)
            {
                if (prog.VideoPid == pid || Array.IndexOf(prog.AudioPids, pid) >= 0)
                {
                    programNumber = prog.ProgramNumber;
                    // Update snapshot with new mapping
                    UpdateStreamProgramMapping(streamIndex, programNumber);
                    break;
                }
            }
        }

        // Determine if video or audio
        var isVideo = snapshot.VideoStreamIndices.Contains(streamIndex);
        var isAudio = snapshot.AudioStreamIndices.Contains(streamIndex);

        // Convert timestamps - FFmpeg uses stream time_base, we need 90kHz
        long pts = 0;
        long dts = 0;

        if (packet->pts != ffmpeg.AV_NOPTS_VALUE)
        {
            pts = ConvertToMpegTsTimestamp(packet->pts, stream->time_base);
        }

        if (packet->dts != ffmpeg.AV_NOPTS_VALUE)
        {
            dts = ConvertToMpegTsTimestamp(packet->dts, stream->time_base);
        }

        // Check keyframe flag
        var isKeyframe = (packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;

        // Get byte position - may be -1 if not available
        var bytePosition = packet->pos;
        if (bytePosition < 0)
        {
            // Estimate from bytes processed
            bytePosition = Interlocked.Read(ref _bytesProcessed);
        }

        _ = Interlocked.Add(ref _bytesProcessed, packet->size);

        PacketDemuxed.Invoke(
            this,
            new DemuxedPacketEventArgs
            {
                StreamIndex = streamIndex,
                Pid = pid,
                Pts = pts,
                Dts = dts,
                IsKeyframe = isKeyframe,
                BytePosition = bytePosition,
                ProgramNumber = programNumber,
                IsVideo = isVideo,
                IsAudio = isAudio,
            }
        );
    }

    private void UpdateStreamProgramMapping(int streamIndex, int programNumber)
    {
        // Copy-on-write update
        ProgramSnapshot oldSnapshot,
            newSnapshot;
        do
        {
            oldSnapshot = _programSnapshot;
            newSnapshot = oldSnapshot.WithStreamProgramMapping(streamIndex, programNumber);
        } while (
            !ReferenceEquals(Interlocked.CompareExchange(ref _programSnapshot, newSnapshot, oldSnapshot), oldSnapshot)
        );
    }

    /// <summary>
    /// Gets the video PID for a program.
    /// </summary>
    /// <param name="programNumber">The program number.</param>
    /// <returns>Video PID or -1 if not found.</returns>
    public int GetVideoPid(int programNumber) =>
        _programSnapshot.Programs.TryGetValue(programNumber, out var program) ? program.VideoPid : -1;

    /// <summary>
    /// Gets the audio PIDs for a program.
    /// </summary>
    /// <param name="programNumber">The program number.</param>
    /// <returns>Audio PIDs or empty array.</returns>
    public int[] GetAudioPids(int programNumber) =>
        _programSnapshot.Programs.TryGetValue(programNumber, out var program) ? program.AudioPids : [];

    /// <summary>
    /// Gets the PCR PID for a program.
    /// </summary>
    /// <param name="programNumber">The program number.</param>
    /// <returns>PCR PID or -1 if not found.</returns>
    public int GetPcrPid(int programNumber) =>
        _programSnapshot.Programs.TryGetValue(programNumber, out var program) ? program.PcrPid : -1;

    /// <summary>
    /// Resets the demuxer state for a new stream (e.g., provider switch).
    /// This properly shuts down the background task and cleans up FFmpeg resources,
    /// then resets all state so the demuxer can be re-initialized with new data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method is safe to call during provider switches. It:
    /// </para>
    /// <list type="number">
    ///   <item><description>Signals the background task to stop processing</description></item>
    ///   <item><description>Waits for the task to complete (with timeout)</description></item>
    ///   <item><description>Cleans up FFmpeg resources</description></item>
    ///   <item><description>Resets all internal state</description></item>
    ///   <item><description>Creates new CTS for next processing cycle</description></item>
    /// </list>
    /// <para>
    /// After Reset(), the demuxer will re-initialize when new data arrives via FeedData().
    /// </para>
    /// </remarks>
    public void Reset()
    {
        if (IsDisposed)
        {
            return;
        }

        // If processing was never started, nothing to reset - demuxer is already in clean state
        // This avoids unnecessary work when Reset() is called on a freshly created demuxer
        // (e.g., during initial stream setup in Restream.StartBroadcastAsync)
        if (!IsProcessingStarted && !_initialized)
        {
            Logger?.LogDebugIfEnabled("FFmpegStreamDemuxer.Reset() - no-op, demuxer was never initialized");
            return;
        }

        Logger?.LogDebugIfEnabled("FFmpegStreamDemuxer.Reset() - stopping background task for provider switch");

        // Cancel the current processing task
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed, nothing to cancel
        }

        // Release any blocked ReadPacket calls to help task exit faster
        try
        {
            _ = _dataAvailable.Release();
        }
        catch
        {
            // Ignore semaphore errors
        }

        // Wait for background task to complete
        // FFmpeg's av_read_frame can block for up to the stream's I/O timeout,
        // so we need a generous timeout here to avoid resource leaks
        var taskCompleted = false;
        if (_processingTask != null && Volatile.Read(ref _processingStartedFlag) != 0)
        {
            try
            {
                // Use 5 seconds to give FFmpeg enough time to exit its blocking I/O
                taskCompleted = _processingTask.Wait(TimeSpan.FromSeconds(5));
                if (!taskCompleted)
                {
                    Logger?.PluginLogWarning(
                        "FFmpegStreamDemuxer.Reset() - background task did not complete within 5s, proceeding anyway"
                    );
                }
            }
            catch (AggregateException)
            {
                taskCompleted = true;
            }
        }
        else
        {
            taskCompleted = true;
        }

        // Cleanup FFmpeg resources if task completed
        if (taskCompleted)
        {
            CleanupFFmpeg();
        }
        else
        {
            // Task still running - null out pointers to prevent access
            lock (_ffmpegLock)
            {
                _formatContext = null;
                _ioContext = null;
                _ioBuffer = null;
            }

            Logger?.PluginLogWarning(
                "FFmpegStreamDemuxer.Reset() - resources leaked due to incomplete shutdown (non-fatal)"
            );
        }

        // Recreate the CTS for next processing cycle
        try
        {
            _cts.Dispose();
        }
        catch
        {
            // Ignore disposal errors
        }

        _cts = new CancellationTokenSource();

        // Clear the input buffer by resetting head/tail
        ClearInputBuffer();

        // Reset state flags - the processing flag allows restart
        _ = Interlocked.Exchange(ref _processingStartedFlag, 0);
        _initialized = false;
        _bytesProcessed = 0;
        _totalOverflowBytes = 0;
        _lastOverflowLog = DateTime.MinValue;
        _totalFedBytes = 0;
        _lastFeedDataLog = DateTime.MinValue;
        _readPacketCallCount = 0;
        _lastReadPacketLog = DateTime.MinValue;

        // Reset program snapshot
        _programSnapshot = ProgramSnapshot.Empty;

        // Clear the semaphore (drain any pending signals)
        while (_dataAvailable.CurrentCount > 0)
        {
            if (!_dataAvailable.Wait(0))
            {
                break;
            }
        }

        Logger?.PluginLogInformation("FFmpegStreamDemuxer.Reset() - demuxer reset complete, ready for new data");
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (IsDisposed)
        {
            return;
        }

        MarkDisposed();

        if (disposing)
        {
            // Cancel background processing
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed
            }

            // Release any blocked ReadPacket calls
            try
            {
                _ = _dataAvailable.Release();
            }
            catch
            {
                // Ignore
            }

            // Wait for background task to complete
            // FFmpeg may be blocked in avformat_open_input, avformat_find_stream_info, or av_read_frame
            // These calls will eventually return when ReadPacket returns AVERROR_EOF
            var taskCompleted = false;
            if (_processingTask != null)
            {
                try
                {
                    // Give FFmpeg time to process the EOF and return
                    taskCompleted = _processingTask.Wait(TimeSpan.FromSeconds(5));
                    if (!taskCompleted)
                    {
                        Logger?.PluginLogWarning(
                            "FFmpeg background task did not complete within timeout during dispose"
                        );
                    }
                }
                catch (AggregateException)
                {
                    // Task was cancelled or errored - this is expected
                    taskCompleted = true;
                }
            }
            else
            {
                taskCompleted = true;
            }

            // Only cleanup FFmpeg if the task completed
            // If the task is still running, we must leak memory to avoid SIGSEGV
            if (taskCompleted)
            {
                CleanupFFmpeg();
            }
            else
            {
                Logger?.PluginLogWarning(
                    "FFmpeg resources leaked due to incomplete task shutdown - this is intentional to prevent crash"
                );
                // Null out pointers so we don't try to access them
                _formatContext = null;
                _ioContext = null;
                _ioBuffer = null;
            }

            try
            {
                _dataAvailable.Dispose();
            }
            catch
            {
                // Ignore
            }

            try
            {
                _cts.Dispose();
            }
            catch
            {
                // Ignore
            }
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Initializes the FFmpeg demuxer context.
    /// Called only from the background processing task, so no locking needed.
    /// </summary>
    /// <returns>True if initialization succeeded.</returns>
    private bool TryInitialize()
    {
        if (_initialized)
        {
            return true;
        }

        try
        {
            // Allocate IO buffer
            _ioBuffer = (byte*)ffmpeg.av_malloc((ulong)IoBufferSize);
            if (_ioBuffer == null)
            {
                Logger?.PluginLogError("Failed to allocate IO buffer");
                return false;
            }

            // Create read callback - must keep reference to prevent GC
            _readCallback = ReadPacket;

            // Create custom IO context
            _ioContext = ffmpeg.avio_alloc_context(
                _ioBuffer,
                IoBufferSize,
                0, // read-only
                opaque: null, // opaque (we use instance fields instead)
                _readCallback,
                write_packet: null, // write callback
                seek: null // seek callback
            );

            if (_ioContext == null)
            {
                Logger?.PluginLogError("Failed to create AVIO context");
                ffmpeg.av_free(_ioBuffer);
                _ioBuffer = null;
                return false;
            }

            // Allocate format context
            _formatContext = ffmpeg.avformat_alloc_context();
            if (_formatContext == null)
            {
                Logger?.PluginLogError("Failed to allocate format context");
                CleanupFFmpeg();
                return false;
            }

            _formatContext->pb = _ioContext;
            _formatContext->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;

            // Configure for live streaming - balance between quick startup and reliable detection
            // MPEG-TS is self-describing - streams are discovered incrementally via av_read_frame()
            // Larger probesize helps avoid "not enough frames to estimate rate" warnings
            _formatContext->probesize = 256 * 1024; // 256KB probe size for reliable stream detection
            _formatContext->max_analyze_duration = 1_000_000; // 1 second max for audio codec detection

            // Critical flags for robust MPEG-TS demuxing:
            // GENPTS: Generate PTS from DTS when PTS is missing. Essential for streams where
            //         encoders omit PTS on non-keyframes (common in IPTV). Without this,
            //         av_read_frame() returns packets with AV_NOPTS_VALUE, breaking A/V sync.
            // IGNDTS: Ignore DTS when PTS is available. Prevents "Non-monotonic DTS" errors
            //         during provider switches where DTS may jump backwards. When GENPTS
            //         generates PTS, DTS becomes redundant and can cause false errors.
            // DISCARD_CORRUPT: Drop packets that fail CRC or have invalid headers rather than
            //                  passing corrupted data downstream. Prevents decoder artifacts
            //                  and cascading errors during network glitches.
            // NOBUFFER: Reduce internal buffering for lower latency on live streams.
            _formatContext->flags |= ffmpeg.AVFMT_FLAG_GENPTS;
            _formatContext->flags |= ffmpeg.AVFMT_FLAG_IGNDTS;
            _formatContext->flags |= ffmpeg.AVFMT_FLAG_DISCARD_CORRUPT;
            _formatContext->flags |= ffmpeg.AVFMT_FLAG_NOBUFFER;

            // Find MPEG-TS input format
            var inputFormat = ffmpeg.av_find_input_format("mpegts");

            // Open input - this blocks until FFmpeg reads enough data via ReadPacket
            Logger?.LogDebugIfEnabled("FFmpeg demuxer: calling avformat_open_input (may block waiting for data)...");
            var ctx = _formatContext;
            var result = ffmpeg.avformat_open_input(&ctx, url: null, inputFormat, options: null);
            if (result < 0)
            {
                Logger?.PluginLogError("Failed to open input: {Error}", GetErrorMessage(result));
                CleanupFFmpeg();
                return false;
            }

            Logger?.LogDebugIfEnabled("FFmpeg demuxer: avformat_open_input succeeded");

            _formatContext = ctx;

            // Check if we should abort due to disposal
            if (IsDisposed)
            {
                return false;
            }

            // Try to find stream info - this may block briefly but that's OK
            // because we're in a background task now
            result = ffmpeg.avformat_find_stream_info(_formatContext, options: null);
            if (result < 0)
            {
                // Log but continue - streams will be discovered incrementally
                Logger?.LogDebugIfEnabled(
                    "avformat_find_stream_info returned {Error}, streams will be discovered incrementally",
                    GetErrorMessage(result)
                );
            }

            // Allocate reusable packet for the processing loop
            // This avoids per-packet allocation overhead in ProcessOnePacket
            _packet = ffmpeg.av_packet_alloc();
            if (_packet == null)
            {
                Logger?.PluginLogError("Failed to allocate packet");
                CleanupFFmpeg();
                return false;
            }

            _initialized = true;
            UpdateProgramInfo();

            Logger?.PluginLogInformation(
                "FFmpeg demuxer initialized, {StreamCount} streams detected initially",
                _formatContext->nb_streams
            );

            return true;
        }
        catch (Exception ex)
        {
            Logger?.PluginLogError(ex, "Failed to initialize FFmpeg demuxer");
            CleanupFFmpeg();
            return false;
        }
    }

    private void UpdateProgramInfo()
    {
        if (_formatContext == null)
        {
            return;
        }

        // Extract program information from AVFormatContext
        var programCount = (int)_formatContext->nb_programs;
        var currentSnapshot = _programSnapshot;

        if (programCount == 0)
        {
            // No programs defined, create a default program from streams
            var (defaultProgram, newSnapshot) = CreateDefaultProgram(currentSnapshot);
            if (defaultProgram != null && !currentSnapshot.Programs.ContainsKey(defaultProgram.ProgramNumber))
            {
                _programSnapshot = newSnapshot;
                ProgramDetected?.Invoke(this, CreateEventArgs(defaultProgram));
            }

            return;
        }

        var snapshotBuilder = new ProgramSnapshotBuilder(currentSnapshot);
        var newPrograms = new List<FFmpegProgramInfo>();

        for (var i = 0; i < programCount; i++)
        {
            var program = _formatContext->programs[i];
            var programNumber = program->id;

            if (currentSnapshot.Programs.ContainsKey(programNumber))
            {
                continue;
            }

            var videoPid = -1;
            var pcrPid = program->pcr_pid;
            var audioPids = new List<int>();
            byte[]? videoExtradata = null;
            var videoCodecId = 0;

            // Iterate program streams
            for (uint j = 0; j < program->nb_stream_indexes; j++)
            {
                var streamIndex = (int)program->stream_index[j];
                if (streamIndex < 0 || streamIndex >= (int)_formatContext->nb_streams)
                {
                    continue;
                }

                var stream = _formatContext->streams[streamIndex];
                var codecpar = stream->codecpar;

                // Map stream index to program and PID
                snapshotBuilder.AddStreamMapping(streamIndex, programNumber, stream->id);

                if (codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && videoPid < 0)
                {
                    videoPid = stream->id;
                    snapshotBuilder.AddVideoStream(streamIndex);

                    // Extract codec extradata (SPS/PPS for H.264, VPS/SPS/PPS for H.265)
                    videoCodecId = (int)codecpar->codec_id;
                    if (codecpar->extradata != null && codecpar->extradata_size > 0)
                    {
                        videoExtradata = new byte[codecpar->extradata_size];
                        Marshal.Copy((IntPtr)codecpar->extradata, videoExtradata, 0, codecpar->extradata_size);
                    }
                }
                else if (codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    audioPids.Add(stream->id);
                    snapshotBuilder.AddAudioStream(streamIndex);
                }
            }

            var programInfo = new FFmpegProgramInfo
            {
                ProgramNumber = programNumber,
                PmtPid = program->pmt_pid,
                PcrPid = pcrPid,
                VideoPid = videoPid,
                AudioPids = [.. audioPids],
                VideoExtradata = videoExtradata,
                VideoCodecId = videoCodecId,
            };

            snapshotBuilder.AddProgram(programInfo);
            newPrograms.Add(programInfo);

            Logger?.LogDebugIfEnabled(
                "Program {Number} detected: Video={VideoPid}, Audio={AudioPids}, PCR={PcrPid}",
                programNumber,
                videoPid,
                string.Join(',', audioPids),
                pcrPid
            );
        }

        // Update snapshot atomically
        if (newPrograms.Count > 0)
        {
            _programSnapshot = snapshotBuilder.Build();

            // Fire events for new programs
            foreach (var prog in newPrograms)
            {
                ProgramDetected?.Invoke(this, CreateEventArgs(prog));
            }
        }
    }

    private (FFmpegProgramInfo? Program, ProgramSnapshot Snapshot) CreateDefaultProgram(ProgramSnapshot currentSnapshot)
    {
        if (_formatContext == null || _formatContext->nb_streams == 0)
        {
            return (null, currentSnapshot);
        }

        var videoPid = -1;
        var audioPids = new List<int>();
        byte[]? videoExtradata = null;
        var videoCodecId = 0;
        const int defaultProgramNumber = 1;
        var builder = new ProgramSnapshotBuilder(currentSnapshot);

        for (var i = 0; i < (int)_formatContext->nb_streams; i++)
        {
            var stream = _formatContext->streams[i];
            var codecpar = stream->codecpar;

            // Map stream index to program and PID
            builder.AddStreamMapping(i, defaultProgramNumber, stream->id);

            if (codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && videoPid < 0)
            {
                videoPid = stream->id;
                builder.AddVideoStream(i);

                // Extract codec extradata (SPS/PPS for H.264, VPS/SPS/PPS for H.265)
                videoCodecId = (int)codecpar->codec_id;
                if (codecpar->extradata != null && codecpar->extradata_size > 0)
                {
                    videoExtradata = new byte[codecpar->extradata_size];
                    Marshal.Copy((IntPtr)codecpar->extradata, videoExtradata, 0, codecpar->extradata_size);
                }
            }
            else if (codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
            {
                audioPids.Add(stream->id);
                builder.AddAudioStream(i);
            }
        }

        var programInfo = new FFmpegProgramInfo
        {
            ProgramNumber = defaultProgramNumber,
            PmtPid = -1,
            PcrPid = videoPid, // Use video PID as PCR
            VideoPid = videoPid,
            AudioPids = [.. audioPids],
            VideoExtradata = videoExtradata,
            VideoCodecId = videoCodecId,
        };

        builder.AddProgram(programInfo);
        return (programInfo, builder.Build());
    }

    // Rate-limit ReadPacket debug logging
    private DateTime _lastReadPacketLog = DateTime.MinValue;
    private int _readPacketCallCount;

    private int ReadPacket(void* opaque, byte* buf, int bufSize)
    {
        _readPacketCallCount++;

        // Check disposed first - this is the critical early exit for safe shutdown
        if (IsDisposed)
        {
            return ffmpeg.AVERROR_EOF;
        }

        // Lock-free read from circular buffer (SPSC pattern)
        // With spin-wait when no data is available
        var bufferSize = InputBuffer.Length;
        var bufferMask = bufferSize - 1;

        // Check if data is available immediately
        var tail = ReadInputTail();
        var head = ReadInputHead();
        var available = head - tail;

        if (available > 0)
        {
            return ReadFromBuffer(buf, bufSize, tail, available, bufferMask);
        }

        // Log first wait and then periodically during init
        var now = DateTime.UtcNow;
        if (!_initialized && (now - _lastReadPacketLog).TotalSeconds >= 1.0)
        {
            _lastReadPacketLog = now;
            Logger?.LogDebugIfEnabled(
                "FFmpeg demuxer: ReadPacket waiting for data (call #{Count}, available=0, bufferSize={BufSize}KB)",
                _readPacketCallCount,
                bufferSize / 1024
            );
        }

        // No data available immediately - use short waits with cancellation
        // During initialization (avformat_open_input), FFmpeg expects blocking I/O and will
        // retry indefinitely on EAGAIN, causing hangs. Use a longer timeout during init.
        // After initialization, shorter timeout is OK as FFmpeg handles EAGAIN better.
        // TsIndexer now feeds data every 16KB during init, so 2s should be plenty.
        var maxSpins = _initialized ? 100 : 200; // 1s steady-state, 2s during init
        var spinCount = 0;

        while (spinCount < maxSpins)
        {
            // Check disposed flag first
            if (IsDisposed)
            {
                return ffmpeg.AVERROR_EOF;
            }

            // Wait briefly for data signal with cancellation support
            try
            {
                if (_dataAvailable.Wait(10, _cts.Token))
                {
                    // Got signal, check for data
                    tail = ReadInputTail();
                    head = ReadInputHead();
                    available = head - tail;

                    if (available > 0)
                    {
                        return ReadFromBuffer(buf, bufSize, tail, available, bufferMask);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return ffmpeg.AVERROR_EOF;
            }
            catch (ObjectDisposedException)
            {
                return ffmpeg.AVERROR_EOF;
            }

            spinCount++;
        }

        // Timeout - check disposed one more time before returning EAGAIN
        return IsDisposed ? ffmpeg.AVERROR_EOF : ffmpeg.AVERROR(ffmpeg.EAGAIN);
    }

    /// <summary>
    /// Reads data from circular buffer to FFmpeg's native buffer.
    /// Uses Unsafe.CopyBlockUnaligned for optimal native interop performance,
    /// avoiding Marshal.Copy overhead (no array bounds checks, no pinning).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ReadFromBuffer(byte* buf, int bufSize, long tail, long available, int bufferMask)
    {
        var bufferSize = InputBuffer.Length;
        var toRead = (int)Math.Min(bufSize, available);

        // Read from circular buffer using direct memory copy
        // Unsafe.CopyBlockUnaligned is faster than Marshal.Copy because:
        // 1. No managed array bounds checking
        // 2. No intermediate pinning (we're already in unsafe context)
        // 3. Uses optimized memory copy intrinsics (rep movsb on x64)
        var readPos = (int)(tail & bufferMask);
        var firstCopy = Math.Min(toRead, bufferSize - readPos);

        fixed (byte* srcPtr = &InputBuffer[readPos])
        {
            Unsafe.CopyBlockUnaligned(buf, srcPtr, (uint)firstCopy);
        }

        if (firstCopy < toRead)
        {
            // Wrap around - copy from beginning of buffer
            var remaining = toRead - firstCopy;
            fixed (byte* srcPtr = &InputBuffer[0])
            {
                Unsafe.CopyBlockUnaligned(buf + firstCopy, srcPtr, (uint)remaining);
            }
        }

        // Advance tail position with release semantics
        _ = AddInputTail(toRead);

        return toRead;
    }

    private void CleanupFFmpeg()
    {
        lock (_ffmpegLock)
        {
            CleanupFFmpegUnsafe();
        }
    }

    /// <summary>
    /// Cleans up FFmpeg resources. Caller must hold _ffmpegLock.
    /// </summary>
    private void CleanupFFmpegUnsafe()
    {
        // Free the reusable packet first
        if (_packet != null)
        {
            var pkt = _packet;
            ffmpeg.av_packet_free(&pkt);
            _packet = null;
        }

        if (_formatContext != null)
        {
            // Don't free IO context - avformat_close_input will handle it
            // if we set AVFMT_FLAG_CUSTOM_IO
            var ctx = _formatContext;
            ffmpeg.avformat_close_input(&ctx);
            _formatContext = null;
            _ioContext = null;
            _ioBuffer = null;
        }
        else
        {
            // Clean up IO context manually if format context wasn't created
            if (_ioContext != null)
            {
                // The buffer is freed with the context
                var ctx = _ioContext;
                ffmpeg.avio_context_free(&ctx);
                _ioContext = null;
                _ioBuffer = null;
            }
            else if (_ioBuffer != null)
            {
                ffmpeg.av_free(_ioBuffer);
                _ioBuffer = null;
            }
        }
    }

    private static DemuxerProgramEventArgs CreateEventArgs(FFmpegProgramInfo program)
    {
        return new DemuxerProgramEventArgs
        {
            ProgramNumber = program.ProgramNumber,
            PmtPid = program.PmtPid,
            PcrPid = program.PcrPid,
            VideoPid = program.VideoPid,
            AudioPids = program.AudioPids,
        };
    }
}

/// <summary>
/// Program information detected by FFmpeg demuxer.
/// </summary>
public sealed class FFmpegProgramInfo
{
    /// <summary>
    /// Gets or sets the program number.
    /// </summary>
    public int ProgramNumber { get; init; }

    /// <summary>
    /// Gets or sets the PMT PID.
    /// </summary>
    public int PmtPid { get; init; }

    /// <summary>
    /// Gets or sets the PCR PID.
    /// </summary>
    public int PcrPid { get; init; }

    /// <summary>
    /// Gets or sets the video PID.
    /// </summary>
    public int VideoPid { get; init; }

    /// <summary>
    /// Gets or sets the audio PIDs.
    /// </summary>
    public int[] AudioPids { get; init; } = [];

    /// <summary>
    /// Gets or sets the video codec extradata (contains SPS/PPS for H.264, VPS/SPS/PPS for H.265).
    /// This is in AVCC/HVCC format from FFmpeg's codecpar->extradata.
    /// </summary>
    public byte[]? VideoExtradata { get; init; }

    /// <summary>
    /// Gets or sets the video codec ID (e.g., AV_CODEC_ID_H264, AV_CODEC_ID_HEVC).
    /// </summary>
    public int VideoCodecId { get; init; }
}

/// <summary>
/// Immutable snapshot of program data for lock-free access.
/// Uses copy-on-write pattern - replaced atomically when data changes.
/// </summary>
internal sealed class ProgramSnapshot(
    ImmutableDictionary<int, FFmpegProgramInfo> programs,
    ImmutableDictionary<int, int> streamIndexToProgram,
    ImmutableDictionary<int, int> streamIndexToPid,
    ImmutableHashSet<int> videoStreamIndices,
    ImmutableHashSet<int> audioStreamIndices
)
{
    public static readonly ProgramSnapshot Empty = new(
        ImmutableDictionary<int, FFmpegProgramInfo>.Empty,
        ImmutableDictionary<int, int>.Empty,
        ImmutableDictionary<int, int>.Empty,
        [],
        []
    );

    public ImmutableDictionary<int, FFmpegProgramInfo> Programs { get; } = programs;
    public ImmutableDictionary<int, int> StreamIndexToProgram { get; } = streamIndexToProgram;
    public ImmutableDictionary<int, int> StreamIndexToPid { get; } = streamIndexToPid;
    public ImmutableHashSet<int> VideoStreamIndices { get; } = videoStreamIndices;
    public ImmutableHashSet<int> AudioStreamIndices { get; } = audioStreamIndices;

    /// <summary>
    /// Creates a new snapshot with an updated stream-to-program mapping.
    /// </summary>
    public ProgramSnapshot WithStreamProgramMapping(int streamIndex, int programNumber)
    {
        return new ProgramSnapshot(
            Programs,
            StreamIndexToProgram.SetItem(streamIndex, programNumber),
            StreamIndexToPid,
            VideoStreamIndices,
            AudioStreamIndices
        );
    }
}

/// <summary>
/// Builder for constructing ProgramSnapshot instances.
/// </summary>
internal sealed class ProgramSnapshotBuilder(ProgramSnapshot source)
{
    private readonly ImmutableDictionary<int, FFmpegProgramInfo>.Builder _programs = source.Programs.ToBuilder();
    private readonly ImmutableDictionary<int, int>.Builder _streamIndexToProgram =
        source.StreamIndexToProgram.ToBuilder();
    private readonly ImmutableDictionary<int, int>.Builder _streamIndexToPid = source.StreamIndexToPid.ToBuilder();
    private readonly ImmutableHashSet<int>.Builder _videoStreamIndices = source.VideoStreamIndices.ToBuilder();
    private readonly ImmutableHashSet<int>.Builder _audioStreamIndices = source.AudioStreamIndices.ToBuilder();

    public void AddProgram(FFmpegProgramInfo program) => _programs[program.ProgramNumber] = program;

    public void AddStreamMapping(int streamIndex, int programNumber, int pid)
    {
        _streamIndexToProgram[streamIndex] = programNumber;
        _streamIndexToPid[streamIndex] = pid;
    }

    public void AddVideoStream(int streamIndex) => _videoStreamIndices.Add(streamIndex);

    public void AddAudioStream(int streamIndex) => _audioStreamIndices.Add(streamIndex);

    public ProgramSnapshot Build()
    {
        return new ProgramSnapshot(
            _programs.ToImmutable(),
            _streamIndexToProgram.ToImmutable(),
            _streamIndexToPid.ToImmutable(),
            _videoStreamIndices.ToImmutable(),
            _audioStreamIndices.ToImmutable()
        );
    }
}
