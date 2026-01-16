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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Jellyfin.Xtream.Service.MpegTs.Core;

namespace Jellyfin.Xtream.Service.MpegTs.Models;

/// <summary>
/// Result of IDR frame detection.
/// </summary>
/// <remarks>
/// <para>
/// Optimized for cache efficiency with packed flags.
/// The 5 bool fields are packed into a single byte to reduce struct size.
/// </para>
/// <para>Layout: _flags (1) + NalType (1) + padding (2) + Offset (4) = 8 bytes.</para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct IdrDetectionResult : IEquatable<IdrDetectionResult>
{
    // Packed flags - reduces 5 bools to 1 byte
    private const byte FoundFlag = 0x01;
    private const byte IsIdrFlag = 0x02;
    private const byte HasSpsFlag = 0x04;
    private const byte HasPpsFlag = 0x08;
    private const byte HasVpsFlag = 0x10;

    private readonly byte _flags;

    /// <summary>Gets the detected NAL unit type.</summary>
    public NalUnitType NalType { get; }

    /// <summary>Gets the offset to the keyframe packet.</summary>
    public int Offset { get; }

    /// <summary>
    /// Gets a result indicating no IDR frame was found.
    /// </summary>
    public static readonly IdrDetectionResult NotFound = new(flags: 0, -1, NalUnitType.Unknown);

    /// <summary>
    /// Gets a result indicating IDR was not detected in the current packet.
    /// </summary>
    public static readonly IdrDetectionResult NotDetected = new(flags: 0, 0, NalUnitType.Unknown);

    /// <summary>
    /// Gets a result indicating an IDR frame was detected.
    /// </summary>
    public static readonly IdrDetectionResult IdrDetected = new(FoundFlag | IsIdrFlag, 0, NalUnitType.Unknown);

    /// <summary>
    /// Initializes a new instance of the <see cref="IdrDetectionResult"/> struct.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private IdrDetectionResult(byte flags, int offset, NalUnitType nalType)
    {
        _flags = flags;
        Offset = offset;
        NalType = nalType;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="IdrDetectionResult"/> struct.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IdrDetectionResult(bool found, int offset, NalUnitType nalType, bool isIdr)
        : this(found, offset, nalType, isIdr, hasSps: false, hasPps: false, hasVps: false) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="IdrDetectionResult"/> struct with parameter set information.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IdrDetectionResult(
        bool found,
        int offset,
        NalUnitType nalType,
        bool isIdr,
        bool hasSps,
        bool hasPps,
        bool hasVps
    )
    {
        byte flags = 0;
        if (found)
        {
            flags |= FoundFlag;
        }

        if (isIdr)
        {
            flags |= IsIdrFlag;
        }

        if (hasSps)
        {
            flags |= HasSpsFlag;
        }

        if (hasPps)
        {
            flags |= HasPpsFlag;
        }

        if (hasVps)
        {
            flags |= HasVpsFlag;
        }

        _flags = flags;
        Offset = offset;
        NalType = nalType;
    }

    /// <summary>Gets a value indicating whether a keyframe was found.</summary>
    public bool Found => (_flags & FoundFlag) != 0;

    /// <summary>Gets a value indicating whether the frame is specifically an IDR frame.</summary>
    public bool IsIdr => (_flags & IsIdrFlag) != 0;

    /// <summary>Gets a value indicating whether an SPS (Sequence Parameter Set) was found before the IDR.</summary>
    public bool HasSps => (_flags & HasSpsFlag) != 0;

    /// <summary>Gets a value indicating whether a PPS (Picture Parameter Set) was found before the IDR.</summary>
    public bool HasPps => (_flags & HasPpsFlag) != 0;

    /// <summary>Gets a value indicating whether a VPS (Video Parameter Set) was found before the IDR (H.265 only).</summary>
    public bool HasVps => (_flags & HasVpsFlag) != 0;

    /// <summary>
    /// Gets a value indicating whether all required parameter sets are present for clean decoder initialization.
    /// For H.264: requires SPS and PPS.
    /// For H.265: requires VPS, SPS, and PPS.
    /// </summary>
    public bool HasRequiredParameterSets =>
        NalType switch
        {
            NalUnitType.H264Idr => HasSps && HasPps,
            NalUnitType.H265Idr or NalUnitType.H265Cra => HasVps && HasSps && HasPps,
            _ => false,
        };

    /// <summary>Equality operator.</summary>
    public static bool operator ==(IdrDetectionResult left, IdrDetectionResult right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(IdrDetectionResult left, IdrDetectionResult right) => !left.Equals(right);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(IdrDetectionResult other) =>
        _flags == other._flags && Offset == other.Offset && NalType == other.NalType;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is IdrDetectionResult other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_flags, Offset, NalType);
}
