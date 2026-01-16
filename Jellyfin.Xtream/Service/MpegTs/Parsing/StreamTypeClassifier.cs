using System.Runtime.CompilerServices;
using Jellyfin.Xtream.Service.MpegTs.Core;
using Jellyfin.Xtream.Service.MpegTs.Models;

namespace Jellyfin.Xtream.Service.MpegTs.Parsing;

/// <summary>
/// Classifies MPEG-TS stream types from PMT stream_type values.
/// Uses lookup tables for O(1) classification.
/// </summary>
public static class StreamTypeClassifier
{
    private static readonly StreamCategory[] _categoryTable = new StreamCategory[256];
    private static readonly AudioCodec[] _audioCodecTable = new AudioCodec[256];

    static StreamTypeClassifier()
    {
        InitializeVideoTypes();
        InitializeAudioTypes();
    }

    /// <summary>
    /// Gets the stream category for a PMT stream_type value.
    /// </summary>
    /// <param name="streamType">The stream_type from PMT.</param>
    /// <returns>The stream category.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StreamCategory GetCategory(int streamType) =>
        streamType is >= 0 and < 256 ? _categoryTable[streamType] : StreamCategory.Unknown;

    /// <summary>
    /// Gets the audio codec for a PMT stream_type value.
    /// </summary>
    /// <param name="streamType">The stream_type from PMT.</param>
    /// <returns>The audio codec, or Unknown if not an audio stream.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AudioCodec GetAudioCodec(int streamType) =>
        streamType is >= 0 and < 256 ? _audioCodecTable[streamType] : AudioCodec.Unknown;

    /// <summary>
    /// Determines if a stream_type represents video.
    /// </summary>
    /// <param name="streamType">The stream_type from PMT.</param>
    /// <returns>True if video stream.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsVideoStreamType(int streamType) => GetCategory(streamType) == StreamCategory.Video;

    /// <summary>
    /// Determines if a stream_type represents audio.
    /// </summary>
    /// <param name="streamType">The stream_type from PMT.</param>
    /// <returns>True if audio stream.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsAudioStreamType(int streamType) => GetCategory(streamType) == StreamCategory.Audio;

    private static void InitializeVideoTypes()
    {
        // ISO/IEC 13818-1 standard video types
        _categoryTable[0x01] = StreamCategory.Video; // MPEG-1 Video
        _categoryTable[0x02] = StreamCategory.Video; // MPEG-2 Video
        _categoryTable[0x10] = StreamCategory.Video; // MPEG-4 Visual
        _categoryTable[0x1B] = StreamCategory.Video; // H.264/AVC
        _categoryTable[0x24] = StreamCategory.Video; // H.265/HEVC

        // Newer video codecs per FFmpeg mpegts.h
        _categoryTable[0x32] = StreamCategory.Video; // JPEG-XS (ISO/IEC 21122-3)
        _categoryTable[0x33] = StreamCategory.Video; // VVC/H.266 (ISO/IEC 23090-3)

        // Regional/proprietary video types
        _categoryTable[0x42] = StreamCategory.Video; // AVS Video (China)
        _categoryTable[0xD1] = StreamCategory.Video; // DIRAC
        _categoryTable[0xD2] = StreamCategory.Video; // AVS2 Video
        _categoryTable[0xD4] = StreamCategory.Video; // AVS3 Video
        _categoryTable[0xEA] = StreamCategory.Video; // VC-1

        // AV1 per AOM AV1-MPEG2-TS draft spec (uses private stream type)
        // Note: 0x06 is typically private data, AV1 detection may require descriptor check
    }

    private static void InitializeAudioTypes()
    {
        // MPEG Audio (ISO/IEC 13818-3)
        _categoryTable[0x03] = StreamCategory.Audio; // MPEG-1 Audio Layer I/II
        _categoryTable[0x04] = StreamCategory.Audio; // MPEG-2 Audio Layer I/II
        _audioCodecTable[0x03] = AudioCodec.MpegAudio;
        _audioCodecTable[0x04] = AudioCodec.MpegAudio;

        // AAC (ISO/IEC 13818-7, ISO/IEC 14496-3)
        _categoryTable[0x0F] = StreamCategory.Audio; // AAC ADTS
        _categoryTable[0x11] = StreamCategory.Audio; // AAC LATM
        _audioCodecTable[0x0F] = AudioCodec.Aac;
        _audioCodecTable[0x11] = AudioCodec.Aac;

        // AC-3 (Dolby Digital) - ATSC A/52
        _categoryTable[0x81] = StreamCategory.Audio;
        _audioCodecTable[0x81] = AudioCodec.Ac3;

        // E-AC-3 (Dolby Digital Plus) - ATSC A/52B
        _categoryTable[0x84] = StreamCategory.Audio; // E-AC-3 (ATSC)
        _categoryTable[0x87] = StreamCategory.Audio; // E-AC-3 (DVB)
        _audioCodecTable[0x84] = AudioCodec.Eac3;
        _audioCodecTable[0x87] = AudioCodec.Eac3;

        // DTS variants (Digital Theater Systems)
        _categoryTable[0x82] = StreamCategory.Audio; // DTS Audio
        _categoryTable[0x85] = StreamCategory.Audio; // DTS-HD High Resolution Audio
        _categoryTable[0x86] = StreamCategory.Audio; // DTS-HD Master Audio
        _audioCodecTable[0x82] = AudioCodec.Dts;
        _audioCodecTable[0x85] = AudioCodec.DtsHd;
        _audioCodecTable[0x86] = AudioCodec.DtsHd;

        // Dolby TrueHD (Blu-ray)
        _categoryTable[0x83] = StreamCategory.Audio;
        _audioCodecTable[0x83] = AudioCodec.TrueHd;
    }
}
