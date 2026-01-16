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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using Jellyfin.Xtream.Service.MpegTs.Core;
using Jellyfin.Xtream.Service.MpegTs.Models;
using Jellyfin.Xtream.Service.MpegTs.UseCases;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.MpegTs.Infrastructure;

/// <summary>
/// FFmpeg-based frame detector for identifying keyframes and NAL units in video streams.
/// Uses FFmpeg's parser context for accurate codec-aware frame detection.
/// </summary>
internal sealed unsafe class FFmpegFrameDetector : IDisposable
{
    private readonly ILogger? _logger;
    private readonly object _lock = new();
    private AVCodecParserContext* _parserContext;
    private AVCodecContext* _codecContext;
    private volatile bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="FFmpegFrameDetector"/> class.
    /// </summary>
    /// <param name="streamType">MPEG-TS stream type from PMT.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <param name="ffmpegContext">Optional FFmpeg context. If null, uses the default adapter.</param>
    public FFmpegFrameDetector(byte streamType, ILogger? logger = null, IFFmpegContext? ffmpegContext = null)
    {
        _logger = logger;
        CodecId = MapStreamTypeToCodecId(streamType);

        ffmpegContext ??= FFmpegContextAdapter.Instance;
        if (!ffmpegContext.IsAvailable)
        {
            throw new InvalidOperationException(
                "FFmpeg is not available. Ensure FFmpegContext.Initialize() is called during plugin startup."
            );
        }

        Initialize();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FFmpegFrameDetector"/> class.
    /// </summary>
    /// <param name="codecId">FFmpeg codec ID.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <param name="ffmpegContext">Optional FFmpeg context. If null, uses the default adapter.</param>
    public FFmpegFrameDetector(AVCodecID codecId, ILogger? logger = null, IFFmpegContext? ffmpegContext = null)
    {
        _logger = logger;
        CodecId = codecId;

        ffmpegContext ??= FFmpegContextAdapter.Instance;
        if (!ffmpegContext.IsAvailable)
        {
            throw new InvalidOperationException(
                "FFmpeg is not available. Ensure FFmpegContext.Initialize() is called during plugin startup."
            );
        }

        Initialize();
    }

    /// <summary>
    /// Gets a value indicating whether this detector is valid and ready for use.
    /// </summary>
    public bool IsValid => _parserContext != null && _codecContext != null;

    /// <summary>
    /// Gets the codec ID this detector is configured for.
    /// </summary>
    public AVCodecID CodecId { get; }

    /// <summary>
    /// Detects frame information from MPEG-TS packet payload.
    /// </summary>
    /// <param name="data">Raw video elementary stream data (after TS/PES demux).</param>
    /// <returns>Frame detection result.</returns>
    public FrameDetectionResult DetectFrame(ReadOnlySpan<byte> data)
    {
        if (_disposed || data.IsEmpty)
        {
            return FrameDetectionResult.NotFound;
        }

        lock (_lock)
        {
            if (_disposed || !IsValid)
            {
                return FrameDetectionResult.NotFound;
            }

            fixed (byte* dataPtr = data)
            {
                byte* outData = null;
                var outSize = 0;

                var consumed = ffmpeg.av_parser_parse2(
                    _parserContext,
                    _codecContext,
                    &outData,
                    &outSize,
                    dataPtr,
                    data.Length,
                    ffmpeg.AV_NOPTS_VALUE,
                    ffmpeg.AV_NOPTS_VALUE,
                    0
                );

                if (consumed < 0)
                {
                    return FrameDetectionResult.NotFound;
                }

                // Check if we have a complete frame
                if (outSize > 0)
                {
                    var pictType = (AVPictureType)_parserContext->pict_type;
                    var isKeyframe = _parserContext->key_frame != 0 || pictType == AVPictureType.AV_PICTURE_TYPE_I;

                    return new FrameDetectionResult(
                        Found: true,
                        IsKeyframe: isKeyframe,
                        PictureType: pictType,
                        FrameSize: outSize,
                        BytesConsumed: consumed
                    );
                }

                // Need more data
                return new FrameDetectionResult(
                    Found: false,
                    IsKeyframe: false,
                    PictureType: AVPictureType.AV_PICTURE_TYPE_NONE,
                    FrameSize: 0,
                    BytesConsumed: consumed
                );
            }
        }
    }

    /// <summary>
    /// Scans MPEG-TS data for keyframes, extracting video payload and detecting frame types.
    /// </summary>
    /// <param name="tsData">MPEG-TS packet-aligned data.</param>
    /// <param name="videoPid">Video PID to filter, or -1 for any video.</param>
    /// <returns>Detection result with offset if keyframe found.</returns>
    public IdrDetectionResult ScanForKeyframe(ReadOnlySpan<byte> tsData, int videoPid = -1)
    {
        if (_disposed || !IsValid || tsData.Length < TsConstants.PacketSize)
        {
            return IdrDetectionResult.NotFound;
        }

        for (var offset = 0; offset + TsConstants.PacketSize <= tsData.Length; offset += TsConstants.PacketSize)
        {
            if (tsData[offset] != TsConstants.SyncByte)
            {
                continue;
            }

            var packet = tsData.Slice(offset, TsConstants.PacketSize);

            // Extract PID
            var pid = ((packet[1] & 0x1F) << 8) | packet[2];
            if (videoPid >= 0 && pid != videoPid)
            {
                continue;
            }

            // Check RAI flag first (quick check)
            var hasRai = HasRandomAccessIndicator(packet);

            // Extract payload
            var payloadStart = GetPayloadStart(packet);
            if (payloadStart is < 0 or >= (TsConstants.PacketSize - 4))
            {
                continue;
            }

            var payload = packet[payloadStart..];

            // Skip PES header if present
            var pesHeaderSize = GetPesHeaderSize(payload);
            if (pesHeaderSize > 0 && pesHeaderSize < payload.Length)
            {
                payload = payload[pesHeaderSize..];
            }

            if (payload.IsEmpty)
            {
                continue;
            }

            // Use FFmpeg to detect frame type
            var result = DetectFrame(payload);

            if (result.Found && result.IsKeyframe)
            {
                return new IdrDetectionResult(
                    found: true,
                    offset: offset,
                    nalType: MapCodecToNalType(CodecId, isIdr: true),
                    isIdr: true,
                    hasSps: false, // FFmpeg handles this internally
                    hasPps: false,
                    hasVps: false
                );
            }

            // If RAI is set but FFmpeg didn't detect keyframe yet, trust RAI
            if (hasRai)
            {
                return new IdrDetectionResult(
                    found: true,
                    offset: offset,
                    nalType: NalUnitType.Unknown,
                    isIdr: false, // Not confirmed as IDR by parser
                    hasSps: false,
                    hasPps: false,
                    hasVps: false
                );
            }
        }

        return IdrDetectionResult.NotFound;
    }

    /// <summary>
    /// Resets the parser state. Call this when switching streams or after discontinuity.
    /// </summary>
    public void Reset()
    {
        if (_disposed)
        {
            return;
        }

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            if (_parserContext != null)
            {
                ffmpeg.av_parser_close(_parserContext);
                _parserContext = null;
            }

            if (_codecContext != null)
            {
                fixed (AVCodecContext** ctx = &_codecContext)
                {
                    ffmpeg.avcodec_free_context(ctx);
                }
            }

            Initialize();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_parserContext != null)
            {
                ffmpeg.av_parser_close(_parserContext);
                _parserContext = null;
            }

            if (_codecContext != null)
            {
                fixed (AVCodecContext** ctx = &_codecContext)
                {
                    ffmpeg.avcodec_free_context(ctx);
                }
            }
        }
    }

    private void Initialize()
    {
        if (CodecId == AVCodecID.AV_CODEC_ID_NONE)
        {
            _logger?.PluginLogWarning("Unknown codec ID, frame detection may not work");
            return;
        }

        // Create parser context
        _parserContext = ffmpeg.av_parser_init((int)CodecId);
        if (_parserContext == null)
        {
            _logger?.PluginLogWarning("Failed to create parser context for codec {CodecId}", CodecId);
            return;
        }

        // Create codec context (needed for parser)
        var codec = ffmpeg.avcodec_find_decoder(CodecId);
        if (codec == null)
        {
            _logger?.PluginLogWarning("Failed to find decoder for codec {CodecId}", CodecId);
            ffmpeg.av_parser_close(_parserContext);
            _parserContext = null;
            return;
        }

        _codecContext = ffmpeg.avcodec_alloc_context3(codec);
        if (_codecContext == null)
        {
            _logger?.PluginLogWarning("Failed to allocate codec context for {CodecId}", CodecId);
            ffmpeg.av_parser_close(_parserContext);
            _parserContext = null;
            return;
        }

        _logger?.LogDebugIfEnabled("FFmpeg frame detector initialized for codec {CodecId}", CodecId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static AVCodecID MapStreamTypeToCodecId(byte streamType)
    {
        return streamType switch
        {
            0x01 or 0x02 => AVCodecID.AV_CODEC_ID_MPEG2VIDEO,
            0x1B => AVCodecID.AV_CODEC_ID_H264,
            0x24 => AVCodecID.AV_CODEC_ID_HEVC,
            _ => AVCodecID.AV_CODEC_ID_NONE,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static NalUnitType MapCodecToNalType(AVCodecID codecId, bool isIdr)
    {
        return !isIdr
            ? NalUnitType.Unknown
            : codecId switch
            {
                AVCodecID.AV_CODEC_ID_H264 => NalUnitType.H264Idr,
                AVCodecID.AV_CODEC_ID_HEVC => NalUnitType.H265Idr,
                AVCodecID.AV_CODEC_ID_MPEG2VIDEO => NalUnitType.Unknown, // MPEG-2 doesn't use NAL
                _ => NalUnitType.Unknown,
            };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasRandomAccessIndicator(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 6)
        {
            return false;
        }

        var adaptationControl = (packet[3] >> 4) & 0x03;
        if (adaptationControl < 2)
        {
            return false;
        }

        int adaptationLength = packet[4];
        return !(adaptationLength is 0 or > 183) && (packet[5] & 0x40) != 0;
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
            1 => 4, // Payload only
            3 => packet.Length > 4 ? 5 + packet[4] : -1, // Adaptation + payload
            _ => -1, // No payload or reserved
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetPesHeaderSize(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 9)
        {
            return 0;
        }

        // Check for PES start code (00 00 01)
        if (payload[0] != 0x00 || payload[1] != 0x00 || payload[2] != 0x01)
        {
            return 0;
        }

        var streamId = payload[3];

        // Video stream IDs: E0-EF
        if (streamId is < 0xE0 or > 0xEF)
        {
            return 0;
        }

        // PES header structure: 00 00 01 [stream_id] [length:2] [flags:2] [header_data_length:1] [header_data]
        int headerDataLength = payload[8];
        return 9 + headerDataLength;
    }
}

/// <summary>
/// Result of FFmpeg frame detection.
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct FrameDetectionResult(
    bool Found,
    bool IsKeyframe,
    AVPictureType PictureType,
    int FrameSize,
    int BytesConsumed
)
{
    /// <summary>
    /// Gets a result indicating no frame was detected.
    /// </summary>
    public static readonly FrameDetectionResult NotFound = new(
        Found: false,
        IsKeyframe: false,
        PictureType: AVPictureType.AV_PICTURE_TYPE_NONE,
        FrameSize: 0,
        BytesConsumed: 0
    );
}
