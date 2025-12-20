namespace Jellyfin.Xtream.Service.MpegTs;

/// <summary>
/// Categories of MPEG-TS elementary streams.
/// </summary>
public enum StreamCategory
{
    /// <summary>
    /// Unknown or unsupported stream type.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Video elementary stream.
    /// </summary>
    Video = 1,

    /// <summary>
    /// Audio elementary stream.
    /// </summary>
    Audio = 2,

    /// <summary>
    /// Private data stream.
    /// </summary>
    PrivateData = 3,

    /// <summary>
    /// Subtitle or teletext stream.
    /// </summary>
    Subtitle = 4,
}
