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
using System.Diagnostics;
using Jellyfin.Xtream.Service.MpegTs.UseCases;

namespace Jellyfin.Xtream.Service.MpegTs.Infrastructure;

/// <summary>
/// Default implementation using Stopwatch for high-precision timing.
/// </summary>
public sealed class StopwatchClock : ISystemClock
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    /// <inheritdoc />
    public long ElapsedTicks => _stopwatch.ElapsedTicks;

    /// <inheritdoc />
    public long ElapsedMilliseconds => _stopwatch.ElapsedMilliseconds;

    /// <inheritdoc />
    public long Frequency => Stopwatch.Frequency;

    /// <inheritdoc />
    public DateTime UtcNow => DateTime.UtcNow;

    /// <inheritdoc />
    public void Restart() => _stopwatch.Restart();
}
