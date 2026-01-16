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
/// Event arguments for program detection from demuxer.
/// </summary>
public sealed class DemuxerProgramEventArgs : EventArgs
{
    /// <summary>
    /// Gets the program number.
    /// </summary>
    public int ProgramNumber { get; init; }

    /// <summary>
    /// Gets the PMT PID.
    /// </summary>
    public int PmtPid { get; init; }

    /// <summary>
    /// Gets the PCR PID.
    /// </summary>
    public int PcrPid { get; init; }

    /// <summary>
    /// Gets the video PID.
    /// </summary>
    public int VideoPid { get; init; }

    /// <summary>
    /// Gets the audio PIDs.
    /// </summary>
    public int[] AudioPids { get; init; } = [];
}
