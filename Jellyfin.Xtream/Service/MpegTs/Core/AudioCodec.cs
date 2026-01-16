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

namespace Jellyfin.Xtream.Service.MpegTs.Core;

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

    /// <summary>
    /// MPEG-1 Audio Layer III (MP3).
    /// </summary>
    Mp3 = 5,

    /// <summary>
    /// MPEG-1 Audio Layer II (MP2).
    /// </summary>
    Mp2 = 6,

    /// <summary>
    /// DTS (Digital Theater Systems).
    /// </summary>
    Dts = 7,

    /// <summary>
    /// DTS-HD (High Definition).
    /// </summary>
    DtsHd = 8,

    /// <summary>
    /// Dolby TrueHD.
    /// </summary>
    TrueHd = 9,
}
