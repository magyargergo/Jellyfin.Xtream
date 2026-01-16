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
using System.Runtime.InteropServices;

namespace Jellyfin.Xtream.Service.MpegTs.Core;

/// <summary>
/// Represents a presentation or decoding timestamp from a PES header.
/// Timestamps use a 90 kHz clock per MPEG-2 specification.
/// </summary>
/// <param name="Value">The raw 33-bit timestamp value (90 kHz units).</param>
/// <param name="StreamOffset">The absolute byte offset where this timestamp was found.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct StreamTimestamp(long Value, long StreamOffset)
{
    private const long TicksPerSecond = 90_000;
    private const long WrapValue = 1L << 33;

    /// <summary>
    /// Gets the timestamp as a TimeSpan relative to stream start.
    /// </summary>
    public TimeSpan AsTimeSpan => TimeSpan.FromTicks(Value * TimeSpan.TicksPerSecond / TicksPerSecond);

    /// <summary>
    /// Gets the timestamp in milliseconds.
    /// </summary>
    public double Milliseconds => Value * 1000.0 / TicksPerSecond;

    /// <summary>
    /// Calculates the difference between two timestamps, handling wrap-around.
    /// </summary>
    /// <param name="other">The timestamp to subtract.</param>
    /// <returns>The difference in 90 kHz units.</returns>
    public long DifferenceFrom(StreamTimestamp other)
    {
        var diff = Value - other.Value;

        if (diff < -(WrapValue / 2))
        {
            diff += WrapValue;
        }
        else if (diff > WrapValue / 2)
        {
            diff -= WrapValue;
        }

        return diff;
    }

    /// <summary>
    /// Calculates the difference in milliseconds.
    /// </summary>
    /// <param name="other">The timestamp to subtract.</param>
    /// <returns>The difference in milliseconds.</returns>
    public double DifferenceInMsFrom(StreamTimestamp other) => DifferenceFrom(other) * 1000.0 / TicksPerSecond;
}
