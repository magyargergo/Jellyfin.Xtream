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
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Service.MpegTs.Core;
using Jellyfin.Xtream.Service.MpegTs.Infrastructure;
using Jellyfin.Xtream.Service.MpegTs.Infrastructure.Pooling;
using Jellyfin.Xtream.Service.MpegTs.Models;
using Jellyfin.Xtream.Service.MpegTs.UseCases;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.ProviderManagement;

/// <summary>
/// Result of an aligned stream switch operation.
/// </summary>
internal readonly struct AlignedSwitchResult : IEquatable<AlignedSwitchResult>
{
    /// <summary>Gets a value indicating whether the switch succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Gets the number of bytes discarded for alignment.</summary>
    public int BytesDiscarded { get; init; }

    /// <summary>Gets the time taken for alignment in milliseconds.</summary>
    public long AlignmentTimeMs { get; init; }

    /// <summary>Gets a value indicating whether a keyframe was found for alignment.</summary>
    public bool AlignedToKeyframe { get; init; }

    /// <summary>Gets a value indicating whether the keyframe is an IDR frame.</summary>
    public bool IsIdrFrame { get; init; }

    /// <summary>Gets the detected NAL unit type if applicable.</summary>
    public NalUnitType NalType { get; init; }

    /// <summary>Gets a value indicating whether SPS was found before the IDR frame.</summary>
    public bool HasSps { get; init; }

    /// <summary>Gets a value indicating whether PPS was found before the IDR frame.</summary>
    public bool HasPps { get; init; }

    /// <summary>Gets a value indicating whether VPS was found before the IDR frame (H.265 only).</summary>
    public bool HasVps { get; init; }

    /// <summary>Gets any error message.</summary>
    public string? Error { get; init; }

    /// <summary>Gets the first video PTS extracted from the aligned data (90 kHz, -1 if not found).</summary>
    public long FirstVideoPts { get; init; }

    /// <summary>Gets the first audio PTS extracted from the aligned data (90 kHz, -1 if not found).</summary>
    public long FirstAudioPts { get; init; }

    /// <summary>Gets the first video PTS in milliseconds, or -1 if not found.</summary>
    public double FirstVideoPtsMs => FirstVideoPts >= 0 ? FirstVideoPts / 90.0 : -1;

    /// <summary>Gets the first audio PTS in milliseconds, or -1 if not found.</summary>
    public double FirstAudioPtsMs => FirstAudioPts >= 0 ? FirstAudioPts / 90.0 : -1;

    /// <summary>
    /// Gets a value indicating whether all required parameter sets are present for clean decoder initialization.
    /// For H.264: requires SPS and PPS.
    /// For H.265: requires VPS, SPS, and PPS.
    /// </summary>
    public bool HasRequiredParameterSets =>
        NalType switch
        {
            NalUnitType.H264Idr => HasSps && HasPps,
            NalUnitType.H265Idr or NalUnitType.H265Cra => HasVps && HasSps && HasPps,
            _ => false,
        };

    /// <summary>Creates a successful result.</summary>
    public static AlignedSwitchResult Succeeded(
        int bytesDiscarded,
        long alignmentTimeMs,
        bool alignedToKeyframe,
        bool isIdrFrame = false,
        NalUnitType nalType = NalUnitType.Unknown,
        long firstVideoPts = -1,
        long firstAudioPts = -1,
        bool hasSps = false,
        bool hasPps = false,
        bool hasVps = false
    ) =>
        new()
        {
            Success = true,
            BytesDiscarded = bytesDiscarded,
            AlignmentTimeMs = alignmentTimeMs,
            AlignedToKeyframe = alignedToKeyframe,
            IsIdrFrame = isIdrFrame,
            NalType = nalType,
            FirstVideoPts = firstVideoPts,
            FirstAudioPts = firstAudioPts,
            HasSps = hasSps,
            HasPps = hasPps,
            HasVps = hasVps,
        };

    /// <summary>Creates a failed result.</summary>
    public static AlignedSwitchResult Failed(string error) =>
        new()
        {
            Success = false,
            Error = error,
            FirstVideoPts = -1,
            FirstAudioPts = -1,
        };

    /// <summary>Equality operator.</summary>
    public static bool operator ==(AlignedSwitchResult left, AlignedSwitchResult right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(AlignedSwitchResult left, AlignedSwitchResult right) => !left.Equals(right);

    /// <inheritdoc />
    public bool Equals(AlignedSwitchResult other) => Success == other.Success && Error == other.Error;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is AlignedSwitchResult other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Success, Error);
}

/// <summary>
/// Handles seamless stream switching with MPEG-TS byte alignment using FFmpeg for frame detection.
/// Ensures the new stream starts at a valid packet boundary, ideally at a keyframe.
/// </summary>
/// <remarks>
/// <para>
/// MPEG-TS packets are 188 bytes starting with sync byte 0x47.
/// For seamless switching:
/// 1. Find sync byte alignment in new stream
/// 2. Use FFmpeg to detect keyframes (I-frames/IDR) for best visual transition
/// 3. Signal discontinuity in the buffer for readers to handle
/// </para>
/// <para>
/// This class uses FFmpeg's parser for codec-aware frame detection, supporting:
/// - MPEG-2 Video (I/P/B frame detection)
/// - H.264/AVC (IDR, SPS, PPS detection)
/// - H.265/HEVC (IDR, CRA, VPS, SPS, PPS detection)
/// </para>
/// </remarks>
/// <remarks>
/// Initializes a new instance of the <see cref="AlignedStreamSwitcher"/> class.
/// </remarks>
/// <param name="logger">Optional logger.</param>
/// <param name="ffmpegContext">Optional FFmpeg context. If null, uses the default adapter.</param>
/// <param name="demuxerPool">Optional demuxer pool for efficient PTS extraction. If null, creates demuxers directly.</param>
internal sealed class AlignedStreamSwitcher(
    ILogger? logger = null,
    IFFmpegContext? ffmpegContext = null,
    IFFmpegProcessorPool? demuxerPool = null
) : IDisposable
{
    private const int TsPacketSize = 188;
    private const byte TsSyncByte = 0x47;
    private const int MaxSyncSearchBytes = TsPacketSize * 10;
    private const int KeyframeSearchTimeoutMs = 3000;
    private const int AlignmentBufferSize = TsPacketSize * 1000;

    private readonly ILogger? _logger = logger;
    private readonly IFFmpegContext _ffmpegContext = ffmpegContext ?? FFmpegContextAdapter.Instance;
    private readonly IFFmpegProcessorPool? _demuxerPool = demuxerPool;
    private readonly byte[] _alignmentBuffer = new byte[AlignmentBufferSize];

    // Pooled processor for PTS extraction (managed by pool, not disposed directly)
    private IPooledFFmpegProcessor? _pooledDemuxer;

    private long _firstVideoPts;
    private long _firstAudioPts;

    // FFmpeg frame detector - lazily initialized when video stream type is known
    private FFmpegFrameDetector? _frameDetector;
    private byte _currentStreamType;
    private bool _disposed;

    /// <summary>
    /// Aligns a new stream to MPEG-TS packet boundaries using FFmpeg for keyframe detection.
    /// </summary>
    /// <param name="newStream">The new stream to align.</param>
    /// <param name="waitForKeyframe">Whether to wait for a keyframe.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Alignment result with aligned data ready to write.</returns>
    public async Task<(AlignedSwitchResult Result, ReadOnlyMemory<byte> AlignedData)> AlignStreamAsync(
        Stream newStream,
        bool waitForKeyframe = false,
        CancellationToken cancellationToken = default
    )
    {
        var startTicks = Environment.TickCount64;
        var bytesDiscarded = 0;

        try
        {
            // Read initial data for alignment
            var bytesRead = await ReadWithTimeoutAsync(newStream, _alignmentBuffer, cancellationToken)
                .ConfigureAwait(false);

            if (bytesRead == 0)
            {
                return (AlignedSwitchResult.Failed("No data from new stream"), ReadOnlyMemory<byte>.Empty);
            }

            // Find sync byte position
            var syncOffset = FindSyncOffset(_alignmentBuffer.AsSpan(0, bytesRead));

            if (syncOffset < 0)
            {
                return (AlignedSwitchResult.Failed("Could not find MPEG-TS sync byte"), ReadOnlyMemory<byte>.Empty);
            }

            bytesDiscarded = syncOffset;

            // Get aligned data
            var alignedLength = bytesRead - syncOffset;
            var alignedData = new byte[alignedLength];
            Array.Copy(_alignmentBuffer, syncOffset, alignedData, 0, alignedLength);

            var foundKeyframe = false;
            var isIdrFrame = false;
            var nalType = NalUnitType.Unknown;

            if (waitForKeyframe)
            {
                // Detect video stream type from PMT or first video packet
                var streamType = DetectVideoStreamType(alignedData);
                EnsureFrameDetector(streamType);

                // Use FFmpeg to scan for keyframes
                var detection = ScanForKeyframe(alignedData);
                foundKeyframe = detection.Found;
                isIdrFrame = detection.IsIdr;
                nalType = detection.NalType;

                if (!foundKeyframe)
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(KeyframeSearchTimeoutMs);

                    try
                    {
                        var (result, additionalData) = await SearchForKeyframeAsync(newStream, timeoutCts.Token)
                            .ConfigureAwait(false);

                        foundKeyframe = result.Found;
                        isIdrFrame = result.IsIdr;
                        nalType = result.NalType;

                        if (additionalData.Length > 0)
                        {
                            var combined = new byte[alignedData.Length + additionalData.Length];
                            alignedData.CopyTo(combined, 0);
                            additionalData.CopyTo(combined.AsSpan(alignedData.Length));
                            alignedData = combined;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        _logger?.LogDebugIfEnabled("Keyframe search timed out, proceeding without keyframe alignment");
                    }
                }
            }

            var elapsedMs = Environment.TickCount64 - startTicks;

            // Extract first video and audio PTS using FFmpeg demuxer
            var (firstVideoPts, firstAudioPts) = ExtractPtsWithDemuxer(alignedData);

            _logger?.LogDebugIfEnabled(
                "Stream aligned: {BytesDiscarded} bytes discarded, {AlignedBytes} bytes ready, keyframe={Keyframe}, IDR={IsIdr}, NAL={NalType}, videoPts={VideoPts}, audioPts={AudioPts}, took {ElapsedMs}ms",
                bytesDiscarded,
                alignedData.Length,
                foundKeyframe,
                isIdrFrame,
                nalType,
                firstVideoPts,
                firstAudioPts,
                elapsedMs
            );

            return (
                AlignedSwitchResult.Succeeded(
                    bytesDiscarded,
                    elapsedMs,
                    foundKeyframe,
                    isIdrFrame,
                    nalType,
                    firstVideoPts,
                    firstAudioPts
                ),
                new ReadOnlyMemory<byte>(alignedData)
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (AlignedSwitchResult.Failed($"Alignment error: {ex.Message}"), ReadOnlyMemory<byte>.Empty);
        }
    }

    /// <summary>
    /// Finds the byte offset to the first valid MPEG-TS packet boundary.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FindSyncOffset(ReadOnlySpan<byte> data)
    {
        if (data.Length < TsPacketSize)
        {
            return -1;
        }

        var maxSearch = Math.Min(data.Length - TsPacketSize, MaxSyncSearchBytes);

        for (var i = 0; i <= maxSearch; i++)
        {
            if (data[i] == TsSyncByte)
            {
                var nextPacketOffset = i + TsPacketSize;
                if (nextPacketOffset >= data.Length || data[nextPacketOffset] == TsSyncByte)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// Checks if MPEG-TS data contains a keyframe using FFmpeg detection.
    /// </summary>
    /// <param name="data">MPEG-TS packet-aligned data to scan.</param>
    /// <returns>True if a keyframe was found.</returns>
    public bool ContainsKeyframe(ReadOnlySpan<byte> data) => ScanForKeyframe(data).Found;

    /// <summary>
    /// Scans for keyframes using FFmpeg's parser.
    /// </summary>
    public IdrDetectionResult ScanForKeyframe(ReadOnlySpan<byte> data)
    {
        if (data.Length < TsPacketSize)
        {
            return IdrDetectionResult.NotFound;
        }

        // Use FFmpeg detector for keyframe detection
        if (_frameDetector?.IsValid == true)
        {
            return _frameDetector.ScanForKeyframe(data);
        }

        // FFmpeg not available - return not found
        // This should only happen if FFmpeg initialization failed during startup
        return IdrDetectionResult.NotFound;
    }

    /// <summary>
    /// Calculates the byte offset to align to the next packet boundary.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CalculatePacketAlignmentOffset(long currentPosition)
    {
        var remainder = (int)(currentPosition % TsPacketSize);
        return remainder == 0 ? 0 : TsPacketSize - remainder;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _frameDetector?.Dispose();
        _frameDetector = null;

        // Return pooled demuxer to pool (if any)
        if (_pooledDemuxer != null)
        {
            _demuxerPool?.ReturnProcessor(_pooledDemuxer);
            _pooledDemuxer = null;
        }

        _disposed = true;
    }

    /// <summary>
    /// Extracts first video and audio PTS using FFmpeg demuxer.
    /// Uses pooled demuxer when available, falls back to direct creation.
    /// </summary>
    /// <param name="data">MPEG-TS data to scan.</param>
    /// <returns>Tuple of (VideoPts, AudioPts), each -1 if not found.</returns>
    private (long VideoPts, long AudioPts) ExtractPtsWithDemuxer(byte[] data)
    {
        if (data.Length < TsPacketSize)
        {
            return (-1, -1);
        }

        // Reset state for new extraction
        _firstVideoPts = -1;
        _firstAudioPts = -1;

        // Get demuxer (from pool or fallback)
        var demuxer = GetDemuxer();

        if (demuxer == null)
        {
            _logger?.LogDebugIfEnabled("FFmpeg demuxer not available for PTS extraction");
            return (-1, -1);
        }

        // Subscribe to packet events
        demuxer.PacketDemuxed += OnPacketDemuxed;

        try
        {
            // Feed data to demuxer
            demuxer.FeedData(data);

            // Process packets until we find both video and audio PTS
            var maxIterations = (data.Length / TsPacketSize) + 10;
            var iterations = 0;

            while (iterations++ < maxIterations && demuxer.Process())
            {
                if (_firstVideoPts >= 0 && _firstAudioPts >= 0)
                {
                    break;
                }
            }

            return (_firstVideoPts, _firstAudioPts);
        }
        finally
        {
            demuxer.PacketDemuxed -= OnPacketDemuxed;
        }
    }

    private void OnPacketDemuxed(object? sender, DemuxedPacketEventArgs e)
    {
        if (e.Pts <= 0)
        {
            return;
        }

        if (e.IsVideo && _firstVideoPts < 0)
        {
            _firstVideoPts = e.Pts;
            _logger?.LogDebugIfEnabled("FFmpeg extracted first video PTS: {Pts} ({PtsMs:F2}ms)", e.Pts, e.Pts / 90.0);
        }
        else if (e.IsAudio && _firstAudioPts < 0)
        {
            _firstAudioPts = e.Pts;
            _logger?.LogDebugIfEnabled("FFmpeg extracted first audio PTS: {Pts} ({PtsMs:F2}ms)", e.Pts, e.Pts / 90.0);
        }
    }

    /// <summary>
    /// Gets a demuxer for PTS extraction from the pool.
    /// The pool provides better performance by reusing demuxers and avoids SIGSEGV
    /// issues during rapid switching by using the "replace don't reset" pattern.
    /// </summary>
    /// <returns>A demuxer instance, or null if FFmpeg or pool is not available.</returns>
    private IPooledFFmpegProcessor? GetDemuxer()
    {
        if (_demuxerPool == null)
        {
            return null;
        }

        // If we already have a healthy demuxer, reuse it
        if (_pooledDemuxer != null && _pooledDemuxer.IsHealthy && _pooledDemuxer.PrepareForReuse())
        {
            return _pooledDemuxer;
        }

        // Return unhealthy demuxer to pool
        if (_pooledDemuxer != null)
        {
            _demuxerPool.ReturnProcessor(_pooledDemuxer);
            _pooledDemuxer = null;
        }

        // Get a prepared processor from pool (synchronous for simplicity in this context)
        _pooledDemuxer = _demuxerPool
            .GetPreparedProcessorAsync(CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        return _pooledDemuxer;
    }

    private void EnsureFrameDetector(byte streamType)
    {
        if (streamType == 0 || !_ffmpegContext.IsAvailable)
        {
            return;
        }

        if (_frameDetector != null && _currentStreamType == streamType)
        {
            return;
        }

        _frameDetector?.Dispose();

        try
        {
            _frameDetector = new FFmpegFrameDetector(streamType, _logger, _ffmpegContext);
            _currentStreamType = streamType;
            _logger?.LogDebugIfEnabled("Created FFmpeg frame detector for stream type 0x{StreamType:X2}", streamType);
        }
        catch (Exception ex)
        {
            _logger?.PluginLogError(
                ex,
                "Failed to create FFmpeg frame detector for stream type 0x{StreamType:X2}",
                streamType
            );
            // _frameDetector is already null or was disposed above
        }
    }

    private static byte DetectVideoStreamType(ReadOnlySpan<byte> data)
    {
        // Scan for PMT to get video stream type
        // For now, return common defaults based on heuristics
        // This could be enhanced to actually parse PMT

        for (var offset = 0; offset + TsPacketSize <= data.Length; offset += TsPacketSize)
        {
            if (data[offset] != TsSyncByte)
            {
                continue;
            }

            var packet = data.Slice(offset, TsPacketSize);

            // Check for video PES start (00 00 01 E0-EF)
            var payloadStart = GetPayloadStart(packet);
            if (payloadStart < 0 || payloadStart + 9 >= TsPacketSize)
            {
                continue;
            }

            var payload = packet[payloadStart..];
            if (payload[0] == 0x00 && payload[1] == 0x00 && payload[2] == 0x01)
            {
                var streamId = payload[3];
                if (streamId is >= 0xE0 and <= 0xEF)
                {
                    // Found video - try to detect codec from NAL headers
                    var pesHeaderLen = 9 + payload[8];
                    if (pesHeaderLen < payload.Length - 4)
                    {
                        var nalData = payload[pesHeaderLen..];
                        return DetectCodecFromNal(nalData);
                    }
                }
            }
        }

        // Default to H.264 as most common
        return 0x1B;
    }

    private static byte DetectCodecFromNal(ReadOnlySpan<byte> data)
    {
        // Find NAL start code
        for (var i = 0; i < data.Length - 4; i++)
        {
            if (data[i] == 0x00 && data[i + 1] == 0x00)
            {
                var nalOffset = -1;
                if (data[i + 2] == 0x01)
                {
                    nalOffset = i + 3;
                }
                else if (data[i + 2] == 0x00 && i + 3 < data.Length && data[i + 3] == 0x01)
                {
                    nalOffset = i + 4;
                }

                if (nalOffset >= 0 && nalOffset + 1 < data.Length)
                {
                    var nalHeader = data[nalOffset];

                    // Check for H.265 signature (2-byte header)
                    if (nalOffset + 1 < data.Length)
                    {
                        var nalHeader2 = data[nalOffset + 1];
                        var temporalIdPlus1 = nalHeader2 & 0x07;

                        if (temporalIdPlus1 >= 1)
                        {
                            var h265Type = (nalHeader >> 1) & 0x3F;
                            if (h265Type is 32 or 33 or 34 or 19 or 20 or 21)
                            {
                                return 0x24; // HEVC
                            }
                        }
                    }

                    // Check for H.264
                    var h264Type = nalHeader & 0x1F;
                    if (h264Type is 5 or 7 or 8 or 1)
                    {
                        return 0x1B; // H.264
                    }
                }
            }
        }

        return 0x1B; // Default to H.264
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetPayloadStart(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 4)
        {
            return -1;
        }

        var adaptationControl = (packet[3] >> 4) & 0x03;

        return adaptationControl switch
        {
            1 => 4,
            3 => packet.Length > 4 ? 5 + packet[4] : -1,
            _ => -1,
        };
    }

    /// <summary>
    /// Array-based wrapper for DetectVideoStreamType to avoid Span in async methods (C# 12 limitation).
    /// </summary>
    private static byte DetectVideoStreamTypeFromArray(byte[] buffer, int length) =>
        DetectVideoStreamType(buffer.AsSpan(0, length));

    /// <summary>
    /// Array-based wrapper for ScanForKeyframe to avoid Span in async methods (C# 12 limitation).
    /// </summary>
    private IdrDetectionResult ScanForKeyframeFromArray(byte[] buffer, int offset, int length) =>
        ScanForKeyframe(buffer.AsSpan(offset, length));

    private static async Task<int> ReadWithTimeoutAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken
    )
    {
        var totalRead = 0;
        var minRead = Math.Min(TsPacketSize * 300, buffer.Length);

        while (totalRead < minRead)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(totalRead), cancellationToken).ConfigureAwait(false);

            if (bytesRead == 0)
            {
                break;
            }

            totalRead += bytesRead;
        }

        return totalRead;
    }

    private async Task<(IdrDetectionResult Result, byte[] AdditionalData)> SearchForKeyframeAsync(
        Stream stream,
        CancellationToken cancellationToken
    )
    {
        const int ChunkSize = TsPacketSize * 680;
        const int MaxTotalBytes = 3 * 1024 * 1024;

        var pool = ArrayPool<byte>.Shared;
        var buffer = pool.Rent(MaxTotalBytes);
        var chunkBuffer = pool.Rent(ChunkSize);

        try
        {
            var totalBytes = 0;
            var lastScanEnd = 0;

            while (totalBytes < MaxTotalBytes && !cancellationToken.IsCancellationRequested)
            {
                var bytesRead = await stream
                    .ReadAsync(chunkBuffer.AsMemory(0, ChunkSize), cancellationToken)
                    .ConfigureAwait(false);

                if (bytesRead == 0)
                {
                    break;
                }

                Buffer.BlockCopy(chunkBuffer, 0, buffer, totalBytes, bytesRead);
                totalBytes += bytesRead;

                // Detect stream type on first chunk if not yet known
                if (_frameDetector == null && totalBytes >= TsPacketSize * 10)
                {
                    var streamType = DetectVideoStreamTypeFromArray(buffer, totalBytes);
                    EnsureFrameDetector(streamType);
                }

                var scanStart = Math.Max(lastScanEnd - TsPacketSize, 0);
                var scanLength = totalBytes - scanStart;
                var result = ScanForKeyframeFromArray(buffer, scanStart, scanLength);
                if (result.Found && result.IsIdr)
                {
                    _logger?.LogDebugIfEnabled(
                        "Found keyframe at offset {Offset} after reading {TotalKB}KB",
                        result.Offset + scanStart,
                        totalBytes / 1024
                    );

                    var resultData = new byte[totalBytes];
                    Buffer.BlockCopy(buffer, 0, resultData, 0, totalBytes);

                    return (
                        new IdrDetectionResult(
                            found: true,
                            result.Offset + scanStart,
                            result.NalType,
                            isIdr: true,
                            result.HasSps,
                            result.HasPps,
                            result.HasVps
                        ),
                        resultData
                    );
                }

                lastScanEnd = totalBytes;
            }

            // Final scan
            var finalResult = ScanForKeyframeFromArray(buffer, 0, totalBytes);

            _logger?.LogDebugIfEnabled(
                "Keyframe search complete: found={Found}, isIdr={IsIdr}, read {TotalKB}KB",
                finalResult.Found,
                finalResult.IsIdr,
                totalBytes / 1024
            );

            var finalData = new byte[totalBytes];
            Buffer.BlockCopy(buffer, 0, finalData, 0, totalBytes);

            return (finalResult, finalData);
        }
        finally
        {
            pool.Return(buffer);
            pool.Return(chunkBuffer);
        }
    }
}
