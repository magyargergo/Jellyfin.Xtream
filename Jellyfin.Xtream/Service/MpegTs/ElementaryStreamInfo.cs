using System.Runtime.InteropServices;

namespace Jellyfin.Xtream.Service.MpegTs;

/// <summary>
/// Information about an elementary stream detected in PMT.
/// </summary>
/// <param name="Pid">The packet identifier for this stream.</param>
/// <param name="StreamType">The stream_type value from PMT.</param>
/// <param name="Category">The classified category (video, audio, etc.).</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct ElementaryStreamInfo(int Pid, int StreamType, StreamCategory Category)
{
    /// <summary>
    /// Gets the audio codec if this is an audio stream.
    /// </summary>
    public AudioCodec AudioCodec => StreamTypeClassifier.GetAudioCodec(StreamType);

    /// <summary>
    /// Gets a value indicating whether this is a video stream.
    /// </summary>
    public bool IsVideo => Category == StreamCategory.Video;

    /// <summary>
    /// Gets a value indicating whether this is an audio stream.
    /// </summary>
    public bool IsAudio => Category == StreamCategory.Audio;
}
