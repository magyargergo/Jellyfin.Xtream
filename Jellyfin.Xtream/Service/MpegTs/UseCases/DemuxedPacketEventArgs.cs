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

namespace Jellyfin.Xtream.Service.MpegTs.UseCases;

/// <summary>
/// Event arguments for demuxed packets.
/// </summary>
public sealed class DemuxedPacketEventArgs : EventArgs
{
    /// <summary>
    /// Gets the stream index within FFmpeg's format context.
    /// </summary>
    public int StreamIndex { get; init; }

    /// <summary>
    /// Gets the PID (Packet Identifier) from the MPEG-TS stream.
    /// </summary>
    public int Pid { get; init; }

    /// <summary>
    /// Gets the presentation timestamp in 90kHz units.
    /// </summary>
    public long Pts { get; init; }

    /// <summary>
    /// Gets the decoding timestamp in 90kHz units.
    /// </summary>
    public long Dts { get; init; }

    /// <summary>
    /// Gets a value indicating whether this packet contains a keyframe.
    /// </summary>
    public bool IsKeyframe { get; init; }

    /// <summary>
    /// Gets the byte position in the input stream.
    /// </summary>
    public long BytePosition { get; init; }

    /// <summary>
    /// Gets the program number this packet belongs to.
    /// </summary>
    public int ProgramNumber { get; init; }

    /// <summary>
    /// Gets a value indicating whether this is a video packet.
    /// </summary>
    public bool IsVideo { get; init; }

    /// <summary>
    /// Gets a value indicating whether this is an audio packet.
    /// </summary>
    public bool IsAudio { get; init; }
}
