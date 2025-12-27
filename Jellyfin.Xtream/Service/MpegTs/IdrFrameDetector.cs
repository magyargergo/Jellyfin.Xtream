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

namespace Jellyfin.Xtream.Service.MpegTs;

/// <summary>
/// Detects IDR (Instantaneous Decoder Refresh) frames in H.264/AVC and H.265/HEVC streams.
/// IDR frames guarantee decoder refresh and are the optimal splice points for seamless switching.
/// </summary>
internal static class IdrFrameDetector
{
    /// <summary>
    /// Scans MPEG-TS aligned data for IDR frame indicators.
    /// Checks both the RAI flag and NAL unit types for comprehensive detection.
    /// </summary>
    /// <param name="data">MPEG-TS packet-aligned data.</param>
    /// <returns>Detection result with offset if found.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IdrDetectionResult Detect(ReadOnlySpan<byte> data)
    {
        if (data.Length < TsConstants.PacketSize)
        {
            return IdrDetectionResult.NotFound;
        }

        for (int offset = 0; offset + TsConstants.PacketSize <= data.Length; offset += TsConstants.PacketSize)
        {
            if (data[offset] != TsConstants.SyncByte)
            {
                continue;
            }

            var packet = data.Slice(offset, TsConstants.PacketSize);

            if (HasRandomAccessIndicator(packet))
            {
                var nalType = DetectNalUnitType(packet);
                if (nalType is NalUnitType.H264Idr or NalUnitType.H265Idr or NalUnitType.H265Cra)
                {
                    return new IdrDetectionResult(found: true, offset, nalType, isIdr: true);
                }

                return new IdrDetectionResult(found: true, offset, nalType, isIdr: false);
            }

            var detectedNal = DetectNalUnitType(packet);
            if (detectedNal is NalUnitType.H264Idr or NalUnitType.H265Idr or NalUnitType.H265Cra)
            {
                return new IdrDetectionResult(found: true, offset, detectedNal, isIdr: true);
            }
        }

        return IdrDetectionResult.NotFound;
    }

    /// <summary>
    /// Finds the first IDR frame in the data, scanning up to maxBytes.
    /// </summary>
    /// <param name="data">Data to scan.</param>
    /// <param name="maxBytes">Maximum bytes to scan.</param>
    /// <returns>Offset to IDR frame, or -1 if not found.</returns>
    public static int FindIdrOffset(ReadOnlySpan<byte> data, int maxBytes)
    {
        int searchLimit = Math.Min(data.Length, maxBytes);
        var searchData = data.Slice(0, searchLimit);
        var result = Detect(searchData);
        return result.Found ? result.Offset : -1;
    }

    /// <summary>
    /// Checks if a packet has the Random Access Indicator flag set.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HasRandomAccessIndicator(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 6)
        {
            return false;
        }

        int adaptationControl = (packet[3] >> 4) & 0x03;
        if (adaptationControl < 2)
        {
            return false;
        }

        int adaptationLength = packet[4];
        if (adaptationLength == 0 || adaptationLength > 183)
        {
            return false;
        }

        byte flags = packet[5];
        return (flags & 0x40) != 0;
    }

    /// <summary>
    /// Detects the NAL unit type from a TS packet payload.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static NalUnitType DetectNalUnitType(ReadOnlySpan<byte> packet)
    {
        int payloadStart = GetPayloadStart(packet);
        if (payloadStart < 0 || payloadStart >= packet.Length - 4)
        {
            return NalUnitType.Unknown;
        }

        var payload = packet.Slice(payloadStart);

        int startCodeOffset = FindNalStartCode(payload);
        if (startCodeOffset < 0)
        {
            return NalUnitType.Unknown;
        }

        int nalOffset = startCodeOffset + (IsLongStartCode(payload, startCodeOffset) ? 4 : 3);
        if (nalOffset >= payload.Length)
        {
            return NalUnitType.Unknown;
        }

        byte nalHeader = payload[nalOffset];
        return ClassifyNalUnit(nalHeader, payload, nalOffset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetPayloadStart(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 5)
        {
            return -1;
        }

        int adaptationControl = (packet[3] >> 4) & 0x03;
        if (adaptationControl == 0 || adaptationControl == 2)
        {
            return -1;
        }

        int offset = 4;
        if (adaptationControl == 3)
        {
            int adaptationLength = packet[4];
            offset = 5 + adaptationLength;
        }

        return offset < packet.Length ? offset : -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindNalStartCode(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length - 3; i++)
        {
            if (data[i] == 0x00 && data[i + 1] == 0x00)
            {
                if (data[i + 2] == 0x01)
                {
                    return i;
                }

                if (i + 3 < data.Length && data[i + 2] == 0x00 && data[i + 3] == 0x01)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsLongStartCode(ReadOnlySpan<byte> data, int offset)
    {
        return offset + 3 < data.Length
            && data[offset] == 0x00
            && data[offset + 1] == 0x00
            && data[offset + 2] == 0x00
            && data[offset + 3] == 0x01;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static NalUnitType ClassifyNalUnit(byte nalHeader, ReadOnlySpan<byte> payload, int nalOffset)
    {
        int forbiddenBit = (nalHeader >> 7) & 0x01;
        if (forbiddenBit != 0)
        {
            return NalUnitType.Unknown;
        }

        int nalType = nalHeader & 0x1F;

        if (nalType <= 23)
        {
            return nalType switch
            {
                5 => NalUnitType.H264Idr,
                1 => NalUnitType.H264NonIdr,
                7 => NalUnitType.H264Sps,
                8 => NalUnitType.H264Pps,
                9 => NalUnitType.H264Aud,
                _ => NalUnitType.Unknown,
            };
        }

        if (nalOffset + 1 >= payload.Length)
        {
            return NalUnitType.Unknown;
        }

        int h265NalType = (nalHeader >> 1) & 0x3F;
        return h265NalType switch
        {
            19 or 20 => NalUnitType.H265Idr,
            21 => NalUnitType.H265Cra,
            32 => NalUnitType.H265Vps,
            33 => NalUnitType.H265Sps,
            34 => NalUnitType.H265Pps,
            35 => NalUnitType.H265Aud,
            0 or 1 => NalUnitType.H265NonIdr,
            _ => NalUnitType.Unknown,
        };
    }
}

/// <summary>
/// NAL unit types for H.264 and H.265 video streams.
/// </summary>
internal enum NalUnitType
{
    /// <summary>Unknown or unrecognized NAL unit.</summary>
    Unknown = 0,

    /// <summary>H.264 IDR (Instantaneous Decoder Refresh) slice.</summary>
    H264Idr = 1,

    /// <summary>H.264 non-IDR slice.</summary>
    H264NonIdr = 2,

    /// <summary>H.264 Sequence Parameter Set.</summary>
    H264Sps = 3,

    /// <summary>H.264 Picture Parameter Set.</summary>
    H264Pps = 4,

    /// <summary>H.264 Access Unit Delimiter.</summary>
    H264Aud = 5,

    /// <summary>H.265 IDR slice (IDR_W_RADL or IDR_N_LP).</summary>
    H265Idr = 10,

    /// <summary>H.265 CRA (Clean Random Access) slice.</summary>
    H265Cra = 11,

    /// <summary>H.265 non-IDR slice.</summary>
    H265NonIdr = 12,

    /// <summary>H.265 Video Parameter Set.</summary>
    H265Vps = 13,

    /// <summary>H.265 Sequence Parameter Set.</summary>
    H265Sps = 14,

    /// <summary>H.265 Picture Parameter Set.</summary>
    H265Pps = 15,

    /// <summary>H.265 Access Unit Delimiter.</summary>
    H265Aud = 16,
}

/// <summary>
/// Result of IDR frame detection.
/// </summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
internal readonly struct IdrDetectionResult : IEquatable<IdrDetectionResult>
{
    /// <summary>
    /// Gets a result indicating no IDR frame was found.
    /// </summary>
    public static readonly IdrDetectionResult NotFound = new(found: false, -1, NalUnitType.Unknown, isIdr: false);

    /// <summary>
    /// Initializes a new instance of the <see cref="IdrDetectionResult"/> struct.
    /// </summary>
    public IdrDetectionResult(bool found, int offset, NalUnitType nalType, bool isIdr)
    {
        Found = found;
        Offset = offset;
        NalType = nalType;
        IsIdr = isIdr;
    }

    /// <summary>Gets a value indicating whether a keyframe was found.</summary>
    public bool Found { get; }

    /// <summary>Gets the offset to the keyframe packet.</summary>
    public int Offset { get; }

    /// <summary>Gets the detected NAL unit type.</summary>
    public NalUnitType NalType { get; }

    /// <summary>Gets a value indicating whether the frame is specifically an IDR frame.</summary>
    public bool IsIdr { get; }

    /// <summary>Equality operator.</summary>
    public static bool operator ==(IdrDetectionResult left, IdrDetectionResult right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(IdrDetectionResult left, IdrDetectionResult right) => !left.Equals(right);

    /// <inheritdoc />
    public bool Equals(IdrDetectionResult other) =>
        Found == other.Found && Offset == other.Offset && NalType == other.NalType;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is IdrDetectionResult other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Found, Offset, NalType);
}
