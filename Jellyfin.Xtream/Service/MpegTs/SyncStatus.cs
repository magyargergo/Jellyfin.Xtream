namespace Jellyfin.Xtream.Service.MpegTs;

/// <summary>
/// Represents the current A/V synchronization status.
/// </summary>
public enum SyncStatus
{
    /// <summary>
    /// Synchronization state is unknown (not enough data).
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Audio and video are well synchronized (within ±40ms).
    /// </summary>
    Synchronized = 1,

    /// <summary>
    /// Audio is ahead of video (lipsync issue - audio leads).
    /// </summary>
    AudioAhead = 2,

    /// <summary>
    /// Audio is behind video (lipsync issue - video leads).
    /// </summary>
    AudioBehind = 3,

    /// <summary>
    /// Drift is accumulating over time (clock mismatch).
    /// </summary>
    Drifting = 4,

    /// <summary>
    /// No audio stream detected.
    /// </summary>
    NoAudio = 5,

    /// <summary>
    /// No video stream detected.
    /// </summary>
    NoVideo = 6,
}
