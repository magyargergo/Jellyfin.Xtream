using System.Runtime.InteropServices;

namespace Jellyfin.Xtream.Service.MpegTs;

/// <summary>
/// Represents a detected audio frame boundary in the stream.
/// </summary>
/// <param name="Offset">The absolute byte offset of the audio frame.</param>
/// <param name="Pts">The presentation timestamp of the audio frame.</param>
/// <param name="Codec">The audio codec type.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct AudioFrameInfo(long Offset, StreamTimestamp Pts, AudioCodec Codec);
