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

using Jellyfin.Xtream.Service.MpegTs.Models;

namespace Jellyfin.Xtream.Service.MpegTs.UseCases;

/// <summary>
/// Composite interface for MPEG-TS stream quality monitoring per TR 101 290.
/// Extends focused interfaces following the Interface Segregation Principle.
/// </summary>
/// <remarks>
/// <para>
/// This interface combines all quality monitoring capabilities for backward compatibility.
/// New consumers should prefer the focused interfaces:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="IStreamStatistics"/> - Packet and byte counts</description></item>
///   <item><description><see cref="IContinuityMonitor"/> - Continuity counter errors</description></item>
///   <item><description><see cref="ICrcMonitor"/> - CRC-32 validation errors</description></item>
///   <item><description><see cref="IEncryptionDetector"/> - Scrambling detection</description></item>
///   <item><description><see cref="ISyncMonitor"/> - A/V synchronization</description></item>
///   <item><description><see cref="IQualityEventSource"/> - Quality violation events</description></item>
/// </list>
/// </remarks>
public interface ITsQualityMonitor
    : IStreamStatistics,
        IContinuityMonitor,
        ICrcMonitor,
        IEncryptionDetector,
        ISyncMonitor,
        IQualityEventSource
{
    /// <summary>
    /// Gets structured metrics about the stream quality.
    /// </summary>
    /// <returns>Structured metrics record.</returns>
    TsIndexerMetrics GetMetrics();

    /// <summary>
    /// Gets program information for a specific program.
    /// </summary>
    /// <param name="programNumber">The program number.</param>
    /// <returns>The program information, or null if not found.</returns>
    ProgramInfo? GetProgramInfo(int programNumber);
}
