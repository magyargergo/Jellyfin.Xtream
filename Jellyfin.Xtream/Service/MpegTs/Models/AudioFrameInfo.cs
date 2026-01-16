using System.Runtime.InteropServices;
using Jellyfin.Xtream.Service.MpegTs.Core;

namespace Jellyfin.Xtream.Service.MpegTs.Models;

/// <summary>
/// Represents a detected audio frame boundary in the stream.
/// </summary>
/// <param name="Offset">The absolute byte offset of the audio frame.</param>
/// <param name="Pts">The presentation timestamp of the audio frame.</param>
/// <param name="Codec">The audio codec type.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct AudioFrameInfo(long Offset, StreamTimestamp Pts, AudioCodec Codec);
