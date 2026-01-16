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
/// Represents the current A/V synchronization status.
/// </summary>
public enum SyncStatus
{
    /// <summary>
    /// Synchronization state is unknown (not enough data).
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Audio and video are well synchronized (within ±40ms).
    /// </summary>
    Synchronized = 1,

    /// <summary>
    /// Audio is ahead of video (lipsync issue - audio leads).
    /// </summary>
    AudioAhead = 2,

    /// <summary>
    /// Audio is behind video (lipsync issue - video leads).
    /// </summary>
    AudioBehind = 3,

    /// <summary>
    /// Drift is accumulating over time (clock mismatch).
    /// </summary>
    Drifting = 4,

    /// <summary>
    /// No audio stream detected.
    /// </summary>
    NoAudio = 5,

    /// <summary>
    /// No video stream detected.
    /// </summary>
    NoVideo = 6,
}
