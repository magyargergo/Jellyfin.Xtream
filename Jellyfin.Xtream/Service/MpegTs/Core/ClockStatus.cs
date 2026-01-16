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
/// Clock recovery status.
/// </summary>
public enum ClockStatus
{
    /// <summary>
    /// Waiting for first PCR.
    /// </summary>
    Initializing,

    /// <summary>
    /// Collecting samples to establish clock relationship.
    /// </summary>
    Locking,

    /// <summary>
    /// Clock relationship established and stable.
    /// </summary>
    Locked,

    /// <summary>
    /// Significant clock drift detected.
    /// </summary>
    Drifting,

    /// <summary>
    /// Clock recovery failed or disabled.
    /// </summary>
    Failed,
}
