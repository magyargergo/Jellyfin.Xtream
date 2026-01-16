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

namespace Jellyfin.Xtream.Service.MpegTs.Models;

/// <summary>
/// Represents a timestamp offset for remapping PTS/DTS/PCR values during provider switches.
/// </summary>
/// <remarks>
/// All timestamp values use the 90 kHz clock (PTS/DTS) or 27 MHz clock (PCR).
/// Offset is calculated as: newTimestamp = originalTimestamp + Offset
/// </remarks>
/// <remarks>
/// Initializes a new instance of the <see cref="TimestampOffset"/> struct.
/// </remarks>
/// <param name="pts90Khz">The PTS/DTS offset in 90 kHz ticks.</param>
/// <param name="pcr27Mhz">The PCR offset in 27 MHz ticks.</param>
[StructLayout(LayoutKind.Auto)]
public readonly struct TimestampOffset(long pts90Khz, long pcr27Mhz) : IEquatable<TimestampOffset>
{
    /// <summary>
    /// Gets a zero offset (no remapping).
    /// </summary>
    public static readonly TimestampOffset Zero = new(0, 0);

    /// <summary>
    /// Gets the PTS/DTS offset in 90 kHz ticks.
    /// </summary>
    public long Pts90Khz { get; } = pts90Khz;

    /// <summary>
    /// Gets the PCR offset in 27 MHz ticks.
    /// </summary>
    public long Pcr27Mhz { get; } = pcr27Mhz;

    /// <summary>
    /// Gets the offset in milliseconds.
    /// </summary>
    public double OffsetMs => Pts90Khz / 90.0;

    /// <summary>
    /// Gets a value indicating whether this offset is zero (no remapping needed).
    /// </summary>
    public bool IsZero => Pts90Khz == 0 && Pcr27Mhz == 0;

    /// <summary>
    /// Creates a timestamp offset from milliseconds.
    /// </summary>
    /// <param name="offsetMs">The offset in milliseconds.</param>
    /// <returns>A new TimestampOffset.</returns>
    public static TimestampOffset FromMilliseconds(double offsetMs)
    {
        var pts = (long)(offsetMs * 90);
        var pcr = (long)(offsetMs * 27000);
        return new TimestampOffset(pts, pcr);
    }

    /// <summary>
    /// Creates a timestamp offset from PTS values (90 kHz).
    /// </summary>
    /// <param name="oldPts">The last PTS from old stream.</param>
    /// <param name="newPts">The first PTS from new stream.</param>
    /// <param name="frameDuration90Khz">Expected frame duration to add for continuity.</param>
    /// <returns>A new TimestampOffset that maps new → old timeline.</returns>
    public static TimestampOffset FromPtsDelta(long oldPts, long newPts, long frameDuration90Khz = 3003)
    {
        // Calculate offset so that: newPts + offset ≈ oldPts + frameDuration
        // This ensures continuous playback across the switch
        var ptsOffset = oldPts + frameDuration90Khz - newPts;
        var pcrOffset = ptsOffset * 300; // PCR is 300x higher resolution
        return new TimestampOffset(ptsOffset, pcrOffset);
    }

    /// <inheritdoc />
    public bool Equals(TimestampOffset other) => Pts90Khz == other.Pts90Khz && Pcr27Mhz == other.Pcr27Mhz;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is TimestampOffset other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Pts90Khz, Pcr27Mhz);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(TimestampOffset left, TimestampOffset right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(TimestampOffset left, TimestampOffset right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => $"TimestampOffset(PTS: {Pts90Khz} ticks, {OffsetMs:F2}ms)";
}
