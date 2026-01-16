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
using System.Runtime.InteropServices;

namespace Jellyfin.Xtream.Service.MpegTs.Core;

/// <summary>
/// Represents a detected keyframe in the stream.
/// </summary>
/// <param name="Offset">The absolute byte offset of the keyframe.</param>
/// <param name="Timestamp">The time when the keyframe was detected.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct KeyframeInfo(long Offset, DateTime Timestamp);
