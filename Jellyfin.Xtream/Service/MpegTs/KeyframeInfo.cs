using System;
using System.Runtime.InteropServices;

namespace Jellyfin.Xtream.Service.MpegTs;

/// <summary>
/// Represents a detected keyframe in the stream.
/// </summary>
/// <param name="Offset">The absolute byte offset of the keyframe.</param>
/// <param name="Timestamp">The time when the keyframe was detected.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct KeyframeInfo(long Offset, DateTime Timestamp);
