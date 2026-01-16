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
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using FFmpeg.AutoGen.Abstractions;
using Jellyfin.Xtream.Service.MpegTs.UseCases;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.MpegTs.Infrastructure;

/// <summary>
/// FFmpeg-based MPEG-TS stream remuxer for A/V synchronization and timestamp correction.
/// </summary>
/// <remarks>
/// <para>
/// This remuxer provides seamless provider switching capabilities:
/// </para>
/// <list type="bullet">
///   <item><description>Timestamp correction for continuous playback across provider switches</description></item>
///   <item><description>Automatic PTS generation via GENPTS flag</description></item>
///   <item><description>DTS discontinuity tolerance via IGNDTS flag</description></item>
///   <item><description>Clean PAT/PMT/PCR regeneration in output</description></item>
/// </list>
/// </remarks>
public sealed unsafe class FFmpegStreamRemuxer : FFmpegProcessorBase, IInProcessRemuxer
{
    private const int IoBufferSize = 64 * 1024; // 64KB IO buffer
    private const int DefaultInputBufferSize = 4 * 1024 * 1024; // 4MB input circular buffer
    private const int OutputBufferSize = 2 * 1024 * 1024; // 2MB output buffer
    private const int MinDataForInit = 512 * 1024; // 512KB minimum before initialization
    private const int ProbeSize = 128 * 1024; // 128KB probesize - enough to detect codec params
    private const int MaxAnalyzeDuration = 250_000; // 0.25 seconds max analyze

    // Input demuxing
    private AVFormatContext* _inputContext;
    private AVIOContext* _inputIoContext;
    private byte* _inputIoBuffer;
    private avio_alloc_context_read_packet? _inputReadCallback;

    // Output muxing
    private AVFormatContext* _outputContext;
    private AVIOContext* _outputIoContext;
    private byte* _outputIoBuffer;
    private avio_alloc_context_write_packet? _outputWriteCallback;

    // Output data queue (lock-free) - uses pooled buffers to reduce GC pressure
    private readonly ConcurrentQueue<PooledOutputChunk> _outputQueue = new();
    private long _outputQueueBytes;

    // Stream mapping (input stream index → output stream index)
    private int[]? _streamMapping;

    // Timestamp correction
    private long _timestampOffset; // Offset to apply to all timestamps (90kHz units)
    private long _lastOutputPts; // Last PTS written to output (90kHz units)
    private long _lastInputPts; // Last PTS from input (for offset calculation)
    private bool _firstPacketReceived;
    private long _firstInputPts; // First input PTS (for initial offset calculation)

    // Statistics
    private long _bytesWritten;
    private long _bytesRead;
    private long _packetsProcessed;
    private int _providerSwitches;
    private long _corruptPacketsDiscarded;

    // State
    private string _streamId = string.Empty;
    private volatile bool _initialized;
    private readonly object _initLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="FFmpegStreamRemuxer"/> class.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <param name="ffmpegContext">Optional FFmpeg context. If null, uses the default adapter.</param>
    public FFmpegStreamRemuxer(ILogger<FFmpegStreamRemuxer>? logger = null, IFFmpegContext? ffmpegContext = null)
        : base(DefaultInputBufferSize, logger, ffmpegContext)
    {
        Logger?.LogDebugIfEnabled(
            "FFmpegStreamRemuxer created with {InputBufferSize} input, {OutputBufferSize} output buffers",
            InputBuffer.Length,
            OutputBufferSize
        );
    }

    /// <inheritdoc />
    public bool IsRunning => _initialized && !IsDisposed;

    /// <inheritdoc />
    public InProcessRemuxerStatistics Statistics =>
        new()
        {
            BytesWritten = Interlocked.Read(ref _bytesWritten),
            BytesRead = Interlocked.Read(ref _bytesRead),
            PacketsProcessed = Interlocked.Read(ref _packetsProcessed),
            ProviderSwitches = Volatile.Read(ref _providerSwitches),
            CurrentTimestampOffset = Volatile.Read(ref _timestampOffset),
            LastOutputPts = Volatile.Read(ref _lastOutputPts),
            IsRunning = IsRunning,
            CorruptPacketsDiscarded = Interlocked.Read(ref _corruptPacketsDiscarded),
        };

    /// <inheritdoc />
    public bool Initialize(string streamId)
    {
        if (IsDisposed)
        {
            return false;
        }

        _streamId = streamId;
        Logger?.LogDebugIfEnabled("FFmpegStreamRemuxer initializing for stream {StreamId}", streamId);
        return true;
    }

    /// <inheritdoc />
    public RemuxResult ProcessData(ReadOnlySpan<byte> input)
    {
        if (IsDisposed)
        {
            return RemuxResult.Fail("Remuxer is disposed");
        }

        if (input.IsEmpty)
        {
            return RemuxResult.Ok(null, 0);
        }

        // Feed input data to buffer (using base class method)
        FeedInputData(input);
        Interlocked.Add(ref _bytesWritten, input.Length);

        // Try to initialize if not already done
        if (!_initialized)
        {
            if (AvailableBytes >= MinDataForInit)
            {
                lock (_initLock)
                {
                    if (!_initialized && !TryInitialize())
                    {
                        // Not enough data yet, or initialization failed
                        return RemuxResult.Initializing(input.Length);
                    }
                }
            }
            else
            {
                return RemuxResult.Initializing(input.Length);
            }
        }

        // Process available packets
        ProcessAvailablePackets();

        // Collect output
        var output = CollectOutput();

        if (output != null)
        {
            Interlocked.Add(ref _bytesRead, output.Length);
        }

        return RemuxResult.Ok(output, input.Length);
    }

    /// <inheritdoc />
    public void NotifyProviderSwitch(string? fromProvider, string? toProvider)
    {
        Interlocked.Increment(ref _providerSwitches);

        // Calculate offset for timestamp continuity
        // The next packet's PTS should continue from where we left off
        var lastOutput = Volatile.Read(ref _lastOutputPts);
        var lastInput = Volatile.Read(ref _lastInputPts);

        if (lastOutput > 0)
        {
            // Next packet will have its PTS adjusted by this offset
            // When the next packet arrives with newPts, output will be: newPts + offset = lastOutput + delta
            // We'll calculate the exact offset when we receive the first packet from the new provider
            _firstPacketReceived = false;
        }

        Logger?.PluginLogInformation(
            "Provider switch for stream {StreamId}: {From} -> {To} (lastOutput={LastOutput}, lastInput={LastInput})",
            _streamId,
            fromProvider ?? "unknown",
            toProvider ?? "unknown",
            lastOutput,
            lastInput
        );
    }

    /// <inheritdoc />
    public byte[]? Flush()
    {
        if (!_initialized || _outputContext == null)
        {
            return null;
        }

        // Write trailer
        var result = ffmpeg.av_write_trailer(_outputContext);
        if (result < 0)
        {
            Logger?.LogDebugIfEnabled("av_write_trailer failed: {Error}", GetErrorMessage(result));
        }

        // Flush output IO
        ffmpeg.avio_flush(_outputIoContext);

        return CollectOutput();
    }

    /// <inheritdoc />
    public void Reset()
    {
        lock (_initLock)
        {
            CleanupFFmpeg();

            // Reset buffers (using base class method)
            ClearInputBuffer();

            // Clear output queue and return pooled buffers
            while (_outputQueue.TryDequeue(out var chunk))
            {
                ArrayPool<byte>.Shared.Return(chunk.Buffer);
            }

            Interlocked.Exchange(ref _outputQueueBytes, 0);

            // Reset state
            _initialized = false;
            _firstPacketReceived = false;
            _timestampOffset = 0;
            _lastOutputPts = 0;
            _lastInputPts = 0;
            _firstInputPts = 0;
            _streamMapping = null;
        }

        Logger?.LogDebugIfEnabled("FFmpegStreamRemuxer reset for stream {StreamId}", _streamId);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (IsDisposed)
        {
            return;
        }

        if (disposing)
        {
            CleanupFFmpeg();

            Logger?.LogDebugIfEnabled(
                "FFmpegStreamRemuxer disposed for stream {StreamId} (written={Written}KB, read={Read}KB, packets={Packets}, switches={Switches})",
                _streamId,
                _bytesWritten / 1024,
                _bytesRead / 1024,
                _packetsProcessed,
                _providerSwitches
            );
        }

        base.Dispose(disposing);
    }

    private bool TryInitialize()
    {
        if (_initialized)
        {
            return true;
        }

        try
        {
            // === Setup Input (Demuxer) ===
            _inputIoBuffer = (byte*)ffmpeg.av_malloc((ulong)IoBufferSize);
            if (_inputIoBuffer == null)
            {
                Logger?.PluginLogError("Failed to allocate input IO buffer");
                return false;
            }

            _inputReadCallback = InputReadPacket;
            _inputIoContext = ffmpeg.avio_alloc_context(
                _inputIoBuffer,
                IoBufferSize,
                0, // read-only
                opaque: null,
                _inputReadCallback,
                write_packet: null,
                seek: null
            );

            if (_inputIoContext == null)
            {
                Logger?.PluginLogError("Failed to create input AVIO context");
                Cleanup();
                return false;
            }

            _inputContext = ffmpeg.avformat_alloc_context();
            if (_inputContext == null)
            {
                Logger?.PluginLogError("Failed to allocate input format context");
                Cleanup();
                return false;
            }

            _inputContext->pb = _inputIoContext;
            _inputContext->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;

            // Use minimal probing for MPEG-TS - we know the format
            _inputContext->probesize = ProbeSize;
            _inputContext->max_analyze_duration = MaxAnalyzeDuration;

            // Critical flags for low-latency live streaming:
            // GENPTS: Generate missing PTS
            // IGNDTS: Be tolerant of DTS discontinuities
            // DISCARD_CORRUPT: Drop corrupt packets instead of failing
            // NOBUFFER: Reduce internal buffering for lower latency
            _inputContext->flags |= ffmpeg.AVFMT_FLAG_GENPTS;
            _inputContext->flags |= ffmpeg.AVFMT_FLAG_IGNDTS;
            _inputContext->flags |= ffmpeg.AVFMT_FLAG_DISCARD_CORRUPT;
            _inputContext->flags |= ffmpeg.AVFMT_FLAG_NOBUFFER;

            var inputFormat = ffmpeg.av_find_input_format("mpegts");
            var ctx = _inputContext;
            int result;

            // Set demuxer options for live streaming
            AVDictionary* demuxerOptions = null;
            try
            {
                // Scan all PMTs for streams (don't stop at first)
                _ = ffmpeg.av_dict_set(&demuxerOptions, "scan_all_pmts", "1", 0);

                result = ffmpeg.avformat_open_input(&ctx, url: null, inputFormat, &demuxerOptions);
                if (result < 0)
                {
                    Logger?.PluginLogError("Failed to open input: {Error}", GetErrorMessage(result));
                    Cleanup();
                    return false;
                }

                _inputContext = ctx;

                result = ffmpeg.avformat_find_stream_info(_inputContext, options: null);
                if (result < 0)
                {
                    Logger?.LogDebugIfEnabled("avformat_find_stream_info: {Error}", GetErrorMessage(result));
                }
            }
            finally
            {
                if (demuxerOptions != null)
                {
                    ffmpeg.av_dict_free(&demuxerOptions);
                }
            }

            // === Setup Output (Muxer) ===
            _outputIoBuffer = (byte*)ffmpeg.av_malloc((ulong)IoBufferSize);
            if (_outputIoBuffer == null)
            {
                Logger?.PluginLogError("Failed to allocate output IO buffer");
                Cleanup();
                return false;
            }

            _outputWriteCallback = OutputWritePacket;
            _outputIoContext = ffmpeg.avio_alloc_context(
                _outputIoBuffer,
                IoBufferSize,
                1, // write mode
                opaque: null,
                read_packet: null,
                _outputWriteCallback,
                seek: null
            );

            if (_outputIoContext == null)
            {
                Logger?.PluginLogError("Failed to create output AVIO context");
                Cleanup();
                return false;
            }

            // Allocate output format context for MPEG-TS
            var outputFormat = ffmpeg.av_guess_format("mpegts", filename: null, mime_type: null);
            AVFormatContext* outputCtx = null;
            result = ffmpeg.avformat_alloc_output_context2(&outputCtx, outputFormat, format_name: null, filename: null);
            if (result < 0 || outputCtx == null)
            {
                Logger?.PluginLogError("Failed to allocate output context: {Error}", GetErrorMessage(result));
                Cleanup();
                return false;
            }

            _outputContext = outputCtx;

            _outputContext->pb = _outputIoContext;
            _outputContext->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;

            // Create output streams matching input streams
            _streamMapping = new int[_inputContext->nb_streams];
            var outputStreamIndex = 0;

            for (var i = 0; i < (int)_inputContext->nb_streams; i++)
            {
                var inStream = _inputContext->streams[i];
                var codecType = inStream->codecpar->codec_type;

                // Only copy video and audio streams
                if (codecType != AVMediaType.AVMEDIA_TYPE_VIDEO && codecType != AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    _streamMapping[i] = -1;
                    continue;
                }

                var outStream = ffmpeg.avformat_new_stream(_outputContext, c: null);
                if (outStream == null)
                {
                    Logger?.PluginLogWarning("Failed to create output stream for input stream {Index}", i);
                    _streamMapping[i] = -1;
                    continue;
                }

                result = ffmpeg.avcodec_parameters_copy(outStream->codecpar, inStream->codecpar);
                if (result < 0)
                {
                    Logger?.PluginLogWarning("Failed to copy codec parameters: {Error}", GetErrorMessage(result));
                }

                outStream->codecpar->codec_tag = 0;
                outStream->time_base = inStream->time_base;

                _streamMapping[i] = outputStreamIndex++;
            }

            // Configure muxer options for low-latency live streaming
            AVDictionary* muxerOptions = null;
            try
            {
                // Flush after each packet write for immediate output
                _ = ffmpeg.av_dict_set(&muxerOptions, "flush_packets", "1", 0);

                // Minimize internal interleaving buffer (100ms max between output packets)
                _ = ffmpeg.av_dict_set(&muxerOptions, "max_interleave_delta", "100000", 0);

                // Write header with options
                result = ffmpeg.avformat_write_header(_outputContext, &muxerOptions);
                if (result < 0)
                {
                    Logger?.PluginLogError("Failed to write header: {Error}", GetErrorMessage(result));
                    Cleanup();
                    return false;
                }
            }
            finally
            {
                if (muxerOptions != null)
                {
                    ffmpeg.av_dict_free(&muxerOptions);
                }
            }

            _initialized = true;

            Logger?.PluginLogInformation(
                "FFmpegStreamRemuxer initialized for stream {StreamId} ({InputStreams} input -> {OutputStreams} output streams)",
                _streamId,
                _inputContext->nb_streams,
                outputStreamIndex
            );

            return true;
        }
        catch (Exception ex)
        {
            Logger?.PluginLogError(ex, "Failed to initialize FFmpegStreamRemuxer");
            Cleanup();
            return false;
        }
    }

    private void ProcessAvailablePackets()
    {
        if (_inputContext == null || _outputContext == null || _streamMapping == null)
        {
            return;
        }

        var packet = ffmpeg.av_packet_alloc();
        try
        {
            // Process packets until we run out or hit the limit
            // Use a generous limit to ensure we keep up with input rate
            // A 64KB input chunk could contain ~340 TS packets
            const int maxPacketsPerCall = 500;
            var eagainCount = 0;
            const int maxEagainRetries = 3;

            // === OPTIMIZATION: Batch statistics ===
            // Accumulate locally, update atomics once at the end
            // This reduces Interlocked overhead from 2 ops/packet to 2 ops/batch
            var localPacketsProcessed = 0;
            var localCorruptPackets = 0;

            // === OPTIMIZATION: Cache timestamp offset ===
            // Read once at loop start; only changes during provider switch (rare)
            var cachedOffset = Volatile.Read(ref _timestampOffset);

            // === OPTIMIZATION: Batch PTS tracking ===
            // Only write final values at end of loop instead of per-packet
            var lastInputPtsLocal = NoTimestamp;
            var lastOutputPtsLocal = NoTimestamp;

            for (var i = 0; i < maxPacketsPerCall; i++)
            {
                var result = ffmpeg.av_read_frame(_inputContext, packet);

                if (result < 0)
                {
                    if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    {
                        // Demuxer needs more data to return a complete packet
                        // After a few consecutive EAGAINs, we're truly out of data
                        eagainCount++;
                        if (eagainCount >= maxEagainRetries)
                        {
                            break;
                        }

                        continue;
                    }

                    if (result == ffmpeg.AVERROR_EOF)
                    {
                        break;
                    }

                    // Error - skip and continue
                    localCorruptPackets++;
                    continue;
                }

                // Successfully read a packet - reset EAGAIN counter
                eagainCount = 0;

                // Check if this stream is mapped
                var inputStreamIndex = packet->stream_index;
                if (inputStreamIndex < 0 || inputStreamIndex >= _streamMapping.Length)
                {
                    ffmpeg.av_packet_unref(packet);
                    continue;
                }

                var outputStreamIndex = _streamMapping[inputStreamIndex];
                if (outputStreamIndex < 0)
                {
                    ffmpeg.av_packet_unref(packet);
                    continue;
                }

                // Apply timestamp correction
                var inStream = _inputContext->streams[inputStreamIndex];
                var outStream = _outputContext->streams[outputStreamIndex];

                // Convert to 90kHz for our tracking
                var pts90Khz = ConvertToMpegTsTimestamp(packet->pts, inStream->time_base);
                var dts90Khz = ConvertToMpegTsTimestamp(packet->dts, inStream->time_base);

                // Handle first packet from a new provider (rare path)
                if (!_firstPacketReceived && pts90Khz != NoTimestamp)
                {
                    _firstPacketReceived = true;
                    _firstInputPts = pts90Khz;

                    var lastOutput = Volatile.Read(ref _lastOutputPts);
                    if (lastOutput > 0 && _providerSwitches > 0)
                    {
                        // Calculate offset to make timestamps continuous
                        // newOutputPts = inputPts + offset = lastOutputPts + small_delta
                        // offset = lastOutputPts - inputPts + small_delta
                        // Use 90000 (1 second) as delta to ensure monotonic increase
                        cachedOffset = lastOutput - pts90Khz + 90000;
                        Interlocked.Exchange(ref _timestampOffset, cachedOffset);

                        Logger?.LogDebugIfEnabled(
                            "Provider switch timestamp offset calculated: {Offset} (lastOutput={LastOutput}, firstInput={FirstInput})",
                            cachedOffset,
                            lastOutput,
                            pts90Khz
                        );
                    }
                }

                // Track last input PTS (locally, will batch-update at end)
                if (pts90Khz != NoTimestamp)
                {
                    lastInputPtsLocal = pts90Khz;
                }

                // Apply cached offset
                if (packet->pts != ffmpeg.AV_NOPTS_VALUE)
                {
                    var correctedPts90Khz = pts90Khz + cachedOffset;
                    packet->pts = ConvertFromMpegTsTimestamp(correctedPts90Khz, outStream->time_base);

                    // Track output PTS (locally)
                    lastOutputPtsLocal = correctedPts90Khz;
                }

                if (packet->dts != ffmpeg.AV_NOPTS_VALUE)
                {
                    var correctedDts90Khz = dts90Khz + cachedOffset;
                    packet->dts = ConvertFromMpegTsTimestamp(correctedDts90Khz, outStream->time_base);
                }

                // Update stream index for output
                packet->stream_index = outputStreamIndex;

                // Note: We already converted timestamps to output timebase above
                // Do NOT call av_packet_rescale_ts here as that would double-convert

                // Write to output using av_write_frame (immediate, no interleaving buffer)
                // This is better for live streaming where we want low latency
                result = ffmpeg.av_write_frame(_outputContext, packet);
                if (result < 0)
                {
                    Logger?.LogTrace("av_write_frame failed: {Error}", GetErrorMessage(result));
                    localCorruptPackets++;
                }
                else
                {
                    localPacketsProcessed++;
                }

                ffmpeg.av_packet_unref(packet);
            }

            // === Batch update statistics ===
            // Single atomic update for entire batch instead of per-packet
            if (localPacketsProcessed > 0)
            {
                Interlocked.Add(ref _packetsProcessed, localPacketsProcessed);
            }

            if (localCorruptPackets > 0)
            {
                Interlocked.Add(ref _corruptPacketsDiscarded, localCorruptPackets);
            }

            // === Batch update PTS tracking ===
            if (lastInputPtsLocal != NoTimestamp)
            {
                Volatile.Write(ref _lastInputPts, lastInputPtsLocal);
            }

            if (lastOutputPtsLocal != NoTimestamp)
            {
                Volatile.Write(ref _lastOutputPts, lastOutputPtsLocal);
            }

            // Flush the IO buffer to ensure data reaches our output queue
            if (_outputIoContext != null)
            {
                ffmpeg.avio_flush(_outputIoContext);
            }
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
        }
    }

    private byte[]? CollectOutput()
    {
        if (_outputQueue.IsEmpty)
        {
            return null;
        }

        // Collect all available output
        var totalSize = (int)Interlocked.Read(ref _outputQueueBytes);
        if (totalSize == 0)
        {
            return null;
        }

        var result = new byte[totalSize];
        var offset = 0;

        while (_outputQueue.TryDequeue(out var chunk))
        {
            Interlocked.Add(ref _outputQueueBytes, -chunk.Length);
            Buffer.BlockCopy(chunk.Buffer, 0, result, offset, chunk.Length);
            offset += chunk.Length;

            // Return buffer to pool after copying data
            ArrayPool<byte>.Shared.Return(chunk.Buffer);
        }

        return offset > 0 ? result[..offset] : null;
    }

    private int InputReadPacket(void* opaque, byte* buf, int bufSize)
    {
        if (IsDisposed)
        {
            return ffmpeg.AVERROR_EOF;
        }

        // Use base class method for reading from circular buffer
        return ReadFromCircularBuffer(buf, bufSize);
    }

    private int OutputWritePacket(void* opaque, byte* buf, int bufSize)
    {
        if (IsDisposed || bufSize <= 0)
        {
            return 0;
        }

        // Rent buffer from pool to reduce GC pressure
        // ArrayPool may return a larger buffer than requested
        var buffer = ArrayPool<byte>.Shared.Rent(bufSize);
        fixed (byte* dstPtr = buffer)
        {
            Unsafe.CopyBlockUnaligned(dstPtr, buf, (uint)bufSize);
        }

        // Wrap in struct to track actual length (buffer may be larger than bufSize)
        _outputQueue.Enqueue(new PooledOutputChunk(buffer, bufSize));
        Interlocked.Add(ref _outputQueueBytes, bufSize);

        // Limit queue size - return discarded buffers to pool
        while (Interlocked.Read(ref _outputQueueBytes) > OutputBufferSize && _outputQueue.TryDequeue(out var old))
        {
            Interlocked.Add(ref _outputQueueBytes, -old.Length);
            ArrayPool<byte>.Shared.Return(old.Buffer);
        }

        return bufSize;
    }

    private void Cleanup()
    {
        // Mark as not initialized first to prevent callbacks from doing work
        _initialized = false;

        // Output cleanup - must write trailer before freeing context
        if (_outputContext != null)
        {
            // Try to write trailer to flush buffered data (ignore errors during cleanup)
            try
            {
                _ = ffmpeg.av_write_trailer(_outputContext);
            }
            catch
            {
                // Ignore - we're cleaning up
            }

            // Detach our custom IO before freeing context
            _outputContext->pb = null;
            ffmpeg.avformat_free_context(_outputContext);
            _outputContext = null;
        }

        // Free output IO context
        // Note: avio_context_free will free the buffer, so we don't free it separately
        if (_outputIoContext != null)
        {
            // Let avio_context_free handle the buffer - don't set to null
            var outputIoCtx = _outputIoContext;
            ffmpeg.avio_context_free(&outputIoCtx);
            _outputIoContext = null;
            _outputIoBuffer = null; // Mark as freed (avio_context_free freed it)
        }
        else if (_outputIoBuffer != null)
        {
            // Only free buffer manually if IO context wasn't created
            ffmpeg.av_free(_outputIoBuffer);
            _outputIoBuffer = null;
        }

        // Input cleanup
        if (_inputContext != null)
        {
            // Detach custom IO before closing
            _inputContext->pb = null;
            var inputCtx = _inputContext;
            ffmpeg.avformat_close_input(&inputCtx);
            _inputContext = null;
        }

        // Free input IO context
        if (_inputIoContext != null)
        {
            var inputIoCtx = _inputIoContext;
            ffmpeg.avio_context_free(&inputIoCtx);
            _inputIoContext = null;
            _inputIoBuffer = null; // Mark as freed
        }
        else if (_inputIoBuffer != null)
        {
            ffmpeg.av_free(_inputIoBuffer);
            _inputIoBuffer = null;
        }

        // Clear callbacks to allow GC
        _inputReadCallback = null;
        _outputWriteCallback = null;

        // Return any remaining pooled buffers to the pool
        while (_outputQueue.TryDequeue(out var chunk))
        {
            ArrayPool<byte>.Shared.Return(chunk.Buffer);
        }

        Interlocked.Exchange(ref _outputQueueBytes, 0);
    }

    private void CleanupFFmpeg()
    {
        lock (_initLock)
        {
            Cleanup();
        }
    }
}

/// <summary>
/// Represents a pooled output buffer with its actual data length.
/// </summary>
/// <remarks>
/// <para>
/// Used to track buffers rented from <see cref="ArrayPool{T}"/> which may
/// be larger than the requested size. The <see cref="Length"/> field stores
/// the actual data length to use when copying/processing.
/// </para>
/// <para>Layout: Length (4) + padding (4 on x64) + Buffer ref (8) = 16 bytes on x64.</para>
/// </remarks>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
internal readonly struct PooledOutputChunk
{
    /// <summary>
    /// The actual data length in the buffer.
    /// </summary>
    public readonly int Length;

    /// <summary>
    /// The buffer rented from ArrayPool (may be larger than Length).
    /// </summary>
    public readonly byte[] Buffer;

    /// <summary>
    /// Initializes a new instance of the <see cref="PooledOutputChunk"/> struct.
    /// </summary>
    /// <param name="buffer">The buffer from ArrayPool.</param>
    /// <param name="length">The actual data length.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PooledOutputChunk(byte[] buffer, int length)
    {
        Length = length;
        Buffer = buffer;
    }
}
