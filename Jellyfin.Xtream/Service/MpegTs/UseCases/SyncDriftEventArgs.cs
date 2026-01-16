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

namespace Jellyfin.Xtream.Service.MpegTs.UseCases;

/// <summary>
/// Event arguments for A/V sync drift detection.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="SyncDriftEventArgs"/> class.
/// </remarks>
/// <param name="driftMs">The current drift in milliseconds.</param>
/// <param name="status">The current sync status.</param>
public sealed class SyncDriftEventArgs(double driftMs, SyncStatus status) : EventArgs
{
    /// <summary>
    /// Gets the drift in milliseconds. Positive = audio ahead.
    /// </summary>
    public double DriftMs { get; } = driftMs;

    /// <summary>
    /// Gets the current sync status.
    /// </summary>
    public SyncStatus Status { get; } = status;
}
