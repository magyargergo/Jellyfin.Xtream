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
/// Result of a packet processing operation.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct RemappingResult : IEquatable<RemappingResult>
{
    /// <summary>Gets the number of packets processed.</summary>
    public int PacketsProcessed { get; init; }

    /// <summary>Gets the number of timestamps remapped.</summary>
    public int TimestampsRemapped { get; init; }

    /// <summary>Gets the number of discontinuity indicators injected.</summary>
    public int DiscontinuitiesInjected { get; init; }

    /// <summary>Gets whether any errors occurred.</summary>
    public bool HasErrors { get; init; }

    /// <summary>Gets processing time in microseconds.</summary>
    public long ProcessingTimeUs { get; init; }

    /// <inheritdoc />
    public bool Equals(RemappingResult other) =>
        PacketsProcessed == other.PacketsProcessed && TimestampsRemapped == other.TimestampsRemapped;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RemappingResult other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(PacketsProcessed, TimestampsRemapped);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(RemappingResult left, RemappingResult right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(RemappingResult left, RemappingResult right) => !left.Equals(right);
}
