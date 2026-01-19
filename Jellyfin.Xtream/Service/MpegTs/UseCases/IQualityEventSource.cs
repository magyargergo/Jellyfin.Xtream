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
/// Provides stream quality violation events following TR 101 290.
/// This is a focused interface following the Interface Segregation Principle.
/// </summary>
public interface IQualityEventSource
{
    /// <summary>
    /// Event raised when a TR 101 290 stream quality violation is detected.
    /// </summary>
    event EventHandler<StreamQualityViolationEventArgs>? StreamQualityViolation;

    /// <summary>
    /// Event raised when A/V synchronization drift is detected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Note: TsDuck analyzers (NativeTsDuckAnalyzer) do not raise this event as they don't
    /// track PTS/DTS correlation needed for A/V sync detection.
    /// </para>
    /// <para>
    /// For A/V sync drift monitoring, use <c>TsIndexer.TimestampTracker.DriftDetected</c> instead,
    /// which provides real-time drift detection with 20ms professional broadcast threshold (EBU R37).
    /// </para>
    /// </remarks>
    event EventHandler<SyncDriftEventArgs>? SyncDriftDetected;
}
