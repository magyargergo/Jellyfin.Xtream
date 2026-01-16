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
using Jellyfin.Xtream.Service.MpegTs.Core;
using Jellyfin.Xtream.Service.MpegTs.Models;

namespace Jellyfin.Xtream.Service.MpegTs.UseCases;

/// <summary>
/// Interface for managing timing services for MPEG-TS programs.
/// Enables dependency injection and testability for program information services.
/// </summary>
public interface IProgramInfoService
{
    /// <summary>
    /// Event raised when a PCR jitter violation is detected in any program.
    /// </summary>
    event EventHandler<StreamQualityViolationEventArgs>? JitterViolationDetected;

    /// <summary>
    /// Gets the clock status for a program.
    /// </summary>
    /// <param name="programNumber">The program number.</param>
    /// <returns>The clock status, or Initializing if no service exists.</returns>
    ClockStatus GetClockStatus(int programNumber);

    /// <summary>
    /// Gets the clock drift in PPM for a program.
    /// </summary>
    /// <param name="programNumber">The program number.</param>
    /// <returns>The drift in PPM, or 0 if no service exists.</returns>
    double GetClockDriftPpm(int programNumber);

    /// <summary>
    /// Gets the sync status for a program.
    /// </summary>
    /// <param name="programNumber">The program number.</param>
    /// <param name="hasVideo">Whether the program has video.</param>
    /// <param name="hasAudio">Whether the program has audio.</param>
    /// <returns>The sync status.</returns>
    SyncStatus GetSyncStatus(int programNumber, bool hasVideo, bool hasAudio);

    /// <summary>
    /// Gets the current A/V drift in milliseconds for a program.
    /// </summary>
    /// <param name="programNumber">The program number.</param>
    /// <returns>The drift in milliseconds, or 0 if no tracker exists.</returns>
    double GetCurrentDriftMs(int programNumber);

    /// <summary>
    /// Resets timing state for all programs.
    /// Used when reconnecting to prevent false drift readings from stale timestamps.
    /// </summary>
    void ResetTimingState();

    /// <summary>
    /// Resets timing state for a specific program.
    /// </summary>
    /// <param name="programNumber">The program number.</param>
    void ResetTimingState(int programNumber);

    /// <summary>
    /// Removes all services for a specific program.
    /// </summary>
    /// <param name="programNumber">The program number.</param>
    void RemoveProgram(int programNumber);

    /// <summary>
    /// Clears all services.
    /// </summary>
    void Clear();
}
