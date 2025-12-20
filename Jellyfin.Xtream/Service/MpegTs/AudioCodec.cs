namespace Jellyfin.Xtream.Service.MpegTs;

/// <summary>
/// Audio codec types detected in MPEG-TS streams.
/// </summary>
public enum AudioCodec
{
    /// <summary>
    /// Unknown or unsupported codec.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// MPEG-1 Audio Layer II.
    /// </summary>
    MpegAudio = 1,

    /// <summary>
    /// AAC (Advanced Audio Coding).
    /// </summary>
    Aac = 2,

    /// <summary>
    /// AC-3 (Dolby Digital).
    /// </summary>
    Ac3 = 3,

    /// <summary>
    /// E-AC-3 (Dolby Digital Plus).
    /// </summary>
    Eac3 = 4,
}
