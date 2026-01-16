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
using Jellyfin.Xtream.Service.MpegTs.UseCases;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Test clock that allows manual control of time for deterministic tests.
/// </summary>
/// <remarks>
/// This class is only used in tests, not in production code.
/// It is kept in the main project for framework compatibility.
/// </remarks>
/// <remarks>
/// Initializes a new instance of the <see cref="TestClock"/> class.
/// </remarks>
/// <param name="frequency">The clock frequency in ticks per second. Defaults to 10,000,000 (100ns precision like Stopwatch on Windows).</param>
internal sealed class TestClock(long frequency = 10_000_000) : ISystemClock
{
    /// <inheritdoc />
    public long ElapsedTicks { get; private set; }

    /// <inheritdoc />
    public long ElapsedMilliseconds => ElapsedTicks * 1000 / Frequency;

    /// <inheritdoc />
    public long Frequency { get; } = frequency;

    /// <inheritdoc />
    public DateTime UtcNow { get; private set; } = DateTime.UtcNow;

    /// <summary>
    /// Advances the clock by the specified number of ticks.
    /// </summary>
    /// <param name="ticks">The number of ticks to advance.</param>
    public void AdvanceTicks(long ticks)
    {
        ElapsedTicks += ticks;
        UtcNow = UtcNow.AddTicks(ticks * TimeSpan.TicksPerSecond / Frequency);
    }

    /// <summary>
    /// Advances the clock by the specified duration.
    /// </summary>
    /// <param name="duration">The duration to advance.</param>
    public void Advance(TimeSpan duration)
    {
        var ticks = (long)(duration.TotalSeconds * Frequency);
        AdvanceTicks(ticks);
    }

    /// <summary>
    /// Advances the clock by the specified number of milliseconds.
    /// </summary>
    /// <param name="milliseconds">The number of milliseconds to advance.</param>
    public void AdvanceMs(double milliseconds) => Advance(TimeSpan.FromMilliseconds(milliseconds));

    /// <summary>
    /// Advances the clock by the specified number of microseconds.
    /// </summary>
    /// <param name="microseconds">The number of microseconds to advance.</param>
    public void AdvanceUs(double microseconds) => Advance(TimeSpan.FromMicroseconds(microseconds));

    /// <inheritdoc />
    public void Restart() => ElapsedTicks = 0;

    /// <summary>
    /// Sets the current UTC time.
    /// </summary>
    /// <param name="utcNow">The UTC time to set.</param>
    public void SetUtcNow(DateTime utcNow) => UtcNow = utcNow;
}
