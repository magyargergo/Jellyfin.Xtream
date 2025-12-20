// Copyright (C) 2025  Gergo Magyar

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;

namespace Jellyfin.Xtream.Service.MpegTs;

/// <summary>
/// Event arguments for TR 101 290 stream quality violations.
/// </summary>
/// <param name="violationType">The type of violation (e.g., "PCR PID Invalid", "PAT Version Change").</param>
/// <param name="details">Detailed description of the violation.</param>
public sealed class StreamQualityViolationEventArgs(string violationType, string details) : EventArgs
{
    /// <summary>
    /// Gets the type of violation (e.g., "PCR PID Invalid", "PAT Version Change").
    /// </summary>
    public string ViolationType { get; } = violationType;

    /// <summary>
    /// Gets the detailed description of the violation.
    /// </summary>
    public string Details { get; } = details;
}
