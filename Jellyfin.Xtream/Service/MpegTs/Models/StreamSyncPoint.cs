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
using Jellyfin.Xtream.Service.MpegTs.Core;

namespace Jellyfin.Xtream.Service.MpegTs.Models;

/// <summary>
/// Represents a sync point where seamless switching is possible.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="StreamSyncPoint"/> struct.
/// </remarks>
[StructLayout(LayoutKind.Auto)]
public readonly struct StreamSyncPoint(
    long byteOffset,
    long videoPts,
    long audioPts,
    long pcr,
    NalUnitType nalType,
    DateTime detectedAt
) : IEquatable<StreamSyncPoint>
{
    /// <summary>Gets the byte offset of this sync point.</summary>
    public long ByteOffset { get; } = byteOffset;

    /// <summary>Gets the video PTS at this sync point (90 kHz).</summary>
    public long VideoPts { get; } = videoPts;

    /// <summary>Gets the audio PTS at this sync point (90 kHz).</summary>
    public long AudioPts { get; } = audioPts;

    /// <summary>Gets the PCR at this sync point (27 MHz).</summary>
    public long Pcr { get; } = pcr;

    /// <summary>Gets the NAL unit type (IDR, CRA, etc).</summary>
    public NalUnitType NalType { get; } = nalType;

    /// <summary>Gets when this sync point was detected.</summary>
    public DateTime DetectedAt { get; } = detectedAt;

    /// <summary>Gets the video PTS in milliseconds.</summary>
    public double VideoPtsMs => VideoPts / 90.0;

    /// <summary>Gets the audio PTS in milliseconds.</summary>
    public double AudioPtsMs => AudioPts / 90.0;

    /// <summary>Gets the A/V drift in milliseconds.</summary>
    public double DriftMs => (AudioPts - VideoPts) / 90.0;

    /// <summary>Gets a value indicating whether this is a true IDR frame.</summary>
    public bool IsIdr => NalType is NalUnitType.H264Idr or NalUnitType.H265Idr;

    /// <inheritdoc />
    public bool Equals(StreamSyncPoint other) => ByteOffset == other.ByteOffset && VideoPts == other.VideoPts;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is StreamSyncPoint other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(ByteOffset, VideoPts);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(StreamSyncPoint left, StreamSyncPoint right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(StreamSyncPoint left, StreamSyncPoint right) => !left.Equals(right);
}
