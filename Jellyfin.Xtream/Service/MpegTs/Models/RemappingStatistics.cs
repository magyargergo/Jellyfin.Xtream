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
/// Statistics about remapping operations.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct RemappingStatistics : IEquatable<RemappingStatistics>
{
    /// <summary>Gets the total packets processed.</summary>
    public long TotalPacketsProcessed { get; init; }

    /// <summary>Gets the total timestamps remapped.</summary>
    public long TotalTimestampsRemapped { get; init; }

    /// <summary>Gets the total discontinuities injected.</summary>
    public long TotalDiscontinuitiesInjected { get; init; }

    /// <summary>Gets the number of switches performed.</summary>
    public int SwitchCount { get; init; }

    /// <summary>Gets the last offset applied.</summary>
    public TimestampOffset LastOffset { get; init; }

    /// <summary>Gets the average processing time per 1000 packets in microseconds.</summary>
    public double AvgProcessingTimePer1000Us { get; init; }

    /// <inheritdoc />
    public bool Equals(RemappingStatistics other) =>
        TotalPacketsProcessed == other.TotalPacketsProcessed && SwitchCount == other.SwitchCount;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RemappingStatistics other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(TotalPacketsProcessed, SwitchCount);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(RemappingStatistics left, RemappingStatistics right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(RemappingStatistics left, RemappingStatistics right) => !left.Equals(right);
}
