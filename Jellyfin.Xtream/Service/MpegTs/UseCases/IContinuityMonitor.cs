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

namespace Jellyfin.Xtream.Service.MpegTs.UseCases;

/// <summary>
/// Monitors MPEG-TS continuity counter errors.
/// This is a focused interface following the Interface Segregation Principle.
/// </summary>
/// <remarks>
/// Continuity counter error tracking is now handled by TsDuck via TR 101 290 Priority 1.
/// This interface returns 0 for backward compatibility.
/// See <see cref="TsDuck.ITsDuckAnalyzer"/> for comprehensive TR 101 290 metrics.
/// </remarks>
public interface IContinuityMonitor
{
    /// <summary>
    /// Gets the total number of continuity counter discontinuities detected.
    /// </summary>
    /// <remarks>Returns 0. Use TsDuck TR 101 290 Priority 1 for CC error tracking.</remarks>
    long TotalContinuityErrors { get; }
}
