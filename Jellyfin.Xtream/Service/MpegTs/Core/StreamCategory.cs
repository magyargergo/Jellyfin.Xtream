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
