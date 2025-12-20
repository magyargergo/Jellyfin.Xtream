using System.Runtime.CompilerServices;

namespace Jellyfin.Xtream.Service.MpegTs;

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
    public static StreamCategory GetCategory(int streamType)
    {
        return streamType >= 0 && streamType < 256 ? _categoryTable[streamType] : StreamCategory.Unknown;
    }

    /// <summary>
    /// Gets the audio codec for a PMT stream_type value.
    /// </summary>
    /// <param name="streamType">The stream_type from PMT.</param>
    /// <returns>The audio codec, or Unknown if not an audio stream.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AudioCodec GetAudioCodec(int streamType)
    {
        return streamType >= 0 && streamType < 256 ? _audioCodecTable[streamType] : AudioCodec.Unknown;
    }

    /// <summary>
    /// Determines if a stream_type represents video.
    /// </summary>
    /// <param name="streamType">The stream_type from PMT.</param>
    /// <returns>True if video stream.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsVideo(int streamType)
    {
        return GetCategory(streamType) == StreamCategory.Video;
    }

    /// <summary>
    /// Determines if a stream_type represents audio.
    /// </summary>
    /// <param name="streamType">The stream_type from PMT.</param>
    /// <returns>True if audio stream.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsAudio(int streamType)
    {
        return GetCategory(streamType) == StreamCategory.Audio;
    }

    private static void InitializeVideoTypes()
    {
        _categoryTable[0x01] = StreamCategory.Video; // MPEG-1 Video
        _categoryTable[0x02] = StreamCategory.Video; // MPEG-2 Video
        _categoryTable[0x10] = StreamCategory.Video; // MPEG-4 Visual
        _categoryTable[0x1B] = StreamCategory.Video; // H.264/AVC
        _categoryTable[0x24] = StreamCategory.Video; // H.265/HEVC
        _categoryTable[0x42] = StreamCategory.Video; // AVS Video
        _categoryTable[0xD1] = StreamCategory.Video; // DIRAC
        _categoryTable[0xEA] = StreamCategory.Video; // VC-1
    }

    private static void InitializeAudioTypes()
    {
        // MPEG Audio
        _categoryTable[0x03] = StreamCategory.Audio;
        _categoryTable[0x04] = StreamCategory.Audio;
        _audioCodecTable[0x03] = AudioCodec.MpegAudio;
        _audioCodecTable[0x04] = AudioCodec.MpegAudio;

        // AAC
        _categoryTable[0x0F] = StreamCategory.Audio;
        _categoryTable[0x11] = StreamCategory.Audio;
        _audioCodecTable[0x0F] = AudioCodec.Aac;
        _audioCodecTable[0x11] = AudioCodec.Aac;

        // AC-3
        _categoryTable[0x81] = StreamCategory.Audio;
        _audioCodecTable[0x81] = AudioCodec.Ac3;

        // E-AC-3
        _categoryTable[0x84] = StreamCategory.Audio;
        _categoryTable[0x87] = StreamCategory.Audio;
        _audioCodecTable[0x84] = AudioCodec.Eac3;
        _audioCodecTable[0x87] = AudioCodec.Eac3;

        // DTS variants
        _categoryTable[0x82] = StreamCategory.Audio;
        _categoryTable[0x85] = StreamCategory.Audio;
        _categoryTable[0x86] = StreamCategory.Audio;
    }
}
