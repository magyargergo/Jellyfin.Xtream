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
/// Represents a synchronization point where both audio and video can safely begin playback.
/// </summary>
/// <param name="Offset">The absolute byte offset of the sync point.</param>
/// <param name="VideoPts">The video presentation timestamp at this point.</param>
/// <param name="AudioPts">The audio presentation timestamp at this point.</param>
/// <param name="DetectedAt">The wall-clock time when this sync point was detected.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct SyncPoint(
    long Offset,
    StreamTimestamp VideoPts,
    StreamTimestamp AudioPts,
    DateTime DetectedAt
)
{
    /// <summary>
    /// Gets the A/V drift in milliseconds (positive = audio ahead, negative = audio behind).
    /// </summary>
    public double DriftMs => AudioPts.DifferenceInMsFrom(VideoPts);

    /// <summary>
    /// Gets a value indicating whether the sync point has acceptable A/V alignment.
    /// Threshold is ±40ms (approximately 1 video frame at 25fps).
    /// </summary>
    public bool IsWellSynced => Math.Abs(DriftMs) <= 40.0;
}
