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

using Jellyfin.Xtream.Service.MpegTs.Core;

namespace Jellyfin.Xtream.Service.MpegTs.UseCases;

/// <summary>
/// Monitors audio/video synchronization and drift in MPEG-TS streams.
/// This is a focused interface following the Interface Segregation Principle.
/// </summary>
public interface ISyncMonitor
{
    /// <summary>
    /// Gets the current A/V sync status for a program.
    /// </summary>
    /// <param name="programNumber">The program number (-1 or 0 for first/default program).</param>
    /// <returns>The sync status.</returns>
    SyncStatus GetSyncStatus(int programNumber = -1);

    /// <summary>
    /// Gets the current A/V drift in milliseconds for a program.
    /// </summary>
    /// <param name="programNumber">The program number (-1 or 0 for first/default program).</param>
    /// <returns>The drift in milliseconds.</returns>
    double GetCurrentDriftMs(int programNumber = -1);
}
