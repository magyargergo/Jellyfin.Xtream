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

namespace Jellyfin.Xtream.Service.MpegTs.Infrastructure.Pooling;

/// <summary>
/// Specifies the kind of FFmpeg processing to perform.
/// </summary>
/// <remarks>
/// <para>
/// Based on FFmpeg's libavformat architecture:
/// - Demuxing uses AVInputFormat to read container formats and extract packets
/// - Remuxing uses both AVInputFormat (read) and AVOutputFormat (write) to
///   repackage streams without re-encoding
/// </para>
/// <para>
/// See: https://www.ffmpeg.org/libavformat.html
/// </para>
/// </remarks>
public enum FFmpegProcessorKind
{
    /// <summary>
    /// Demuxer mode: Reads container format, extracts program info and packets.
    /// Uses synchronous event-driven processing model.
    /// </summary>
    /// <remarks>
    /// Output via events: ProgramDetected, PacketDemuxed.
    /// Use Process() to drive the demuxing loop.
    /// </remarks>
    Demuxer,

    /// <summary>
    /// Remuxer mode: Reads container, corrects timestamps, writes new container.
    /// Uses asynchronous background worker with producer-consumer queues.
    /// </summary>
    /// <remarks>
    /// Input via TryQueueData() (non-blocking).
    /// Output via TryReadOutput() (non-blocking).
    /// Background worker handles FFmpeg initialization which can block for ~500ms.
    /// </remarks>
    Remuxer,
}
