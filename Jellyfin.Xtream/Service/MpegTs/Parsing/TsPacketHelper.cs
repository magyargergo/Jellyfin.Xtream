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
using Jellyfin.Xtream.Service.MpegTs.Core;
using Jellyfin.Xtream.Service.MpegTs.Models;

namespace Jellyfin.Xtream.Service.MpegTs.Parsing;

/// <summary>
/// Low-level TS packet parsing utilities used across the MPEG-TS module.
/// All methods are static, pure, and designed for hot-path performance.
/// </summary>
/// <remarks>
/// These utilities consolidate packet parsing logic that was previously
/// duplicated across IdrFrameDetector, ParameterSetCache, Mpeg2FrameDetector,
/// and DiscontinuityInjector. All consumers should use this class.
/// </remarks>
internal static class TsPacketHelper
{
    /// <summary>
    /// Gets the payload start offset within a TS packet.
    /// </summary>
    /// <param name="packet">A 188-byte TS packet.</param>
    /// <returns>Byte offset to payload, or -1 if no payload exists.</returns>
    /// <remarks>
    /// Handles adaptation_field_control:
    /// - 00: Reserved (no payload)
    /// - 01: Payload only, starts at byte 4
    /// - 10: Adaptation field only (no payload)
    /// - 11: Adaptation field + payload
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetPayloadStart(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 5)
        {
            return -1;
        }

        var adaptationControl = (packet[3] >> 4) & 0x03;

        // No payload if adaptation_field_control is 00 (reserved) or 10 (adaptation only)
        if (adaptationControl is 0 or 2)
        {
            return -1;
        }

        var offset = 4;
        if (adaptationControl == 3)
        {
            // Adaptation field present: skip 1 + adaptation_field_length bytes
            int adaptationLength = packet[4];
            offset = 5 + adaptationLength;
        }

        return offset < packet.Length ? offset : -1;
    }

    /// <summary>
    /// Gets the PES header size if the payload starts with a PES packet.
    /// </summary>
    /// <param name="payload">Packet payload (starting after TS header).</param>
    /// <returns>PES header size (9 + header_data_length), or 0 if not a video PES packet.</returns>
    /// <remarks>
    /// Only detects video PES headers (stream_id 0xE0-0xEF).
    /// Audio PES headers (stream_id 0xC0-0xDF) are not detected by this method.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetPesHeaderSize(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 9)
        {
            return 0;
        }

        // Check for PES start code: 00 00 01
        if (payload[0] != 0x00 || payload[1] != 0x00 || payload[2] != 0x01)
        {
            return 0;
        }

        var streamId = payload[3];

        // Video stream IDs: 0xE0-0xEF
        if (streamId is < 0xE0 or > 0xEF)
        {
            return 0;
        }

        // PES header length is at byte 8, total header = 9 + header_data_length
        int headerDataLength = payload[8];
        return 9 + headerDataLength;
    }

    /// <summary>
    /// Finds the next NAL unit start code (00 00 01 or 00 00 00 01).
    /// </summary>
    /// <param name="data">Data to search.</param>
    /// <param name="startOffset">Offset to begin search (default 0).</param>
    /// <returns>Offset of start code, or -1 if not found.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FindNalStartCode(ReadOnlySpan<byte> data, int startOffset = 0)
    {
        for (var i = startOffset; i < data.Length - 3; i++)
        {
            if (data[i] == 0x00 && data[i + 1] == 0x00)
            {
                // Check for 3-byte start code: 00 00 01
                if (data[i + 2] == 0x01)
                {
                    return i;
                }

                // Check for 4-byte start code: 00 00 00 01
                if (i + 3 < data.Length && data[i + 2] == 0x00 && data[i + 3] == 0x01)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// Checks if the start code at offset is the 4-byte variant (00 00 00 01).
    /// </summary>
    /// <param name="data">Data containing the start code.</param>
    /// <param name="offset">Offset of the start code.</param>
    /// <returns>True if 4-byte start code, false if 3-byte.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsLongStartCode(ReadOnlySpan<byte> data, int offset)
    {
        return offset + 3 < data.Length
            && data[offset] == 0x00
            && data[offset + 1] == 0x00
            && data[offset + 2] == 0x00
            && data[offset + 3] == 0x01;
    }

    /// <summary>
    /// Classifies a NAL unit type from its header byte(s).
    /// Supports both H.264 (AVC) and H.265 (HEVC).
    /// </summary>
    /// <param name="nalHeader">First byte of NAL unit.</param>
    /// <param name="payload">Full payload for H.265 second-byte check.</param>
    /// <param name="nalOffset">Offset of NAL header within payload.</param>
    /// <returns>The NAL unit type.</returns>
    /// <remarks>
    /// <para>
    /// H.264 NAL header: forbidden_zero_bit (1) | nal_ref_idc (2) | nal_unit_type (5)
    /// H.265 NAL header: forbidden_zero_bit (1) | nal_unit_type (6) | nuh_layer_id (6) | nuh_temporal_id_plus1 (3)
    /// </para>
    /// <para>
    /// Disambiguation: Tries H.265 first (checks temporal_id_plus1 >= 1), falls back to H.264.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static NalUnitType ClassifyNalUnit(byte nalHeader, ReadOnlySpan<byte> payload, int nalOffset)
    {
        // Per ITU-T H.264/H.265, forbidden_zero_bit must be 0
        var forbiddenBit = (nalHeader >> 7) & 0x01;
        if (forbiddenBit != 0)
        {
            return NalUnitType.Unknown;
        }

        // Try H.265 first - it has a 2-byte header with temporal_id_plus1 validation
        if (nalOffset + 1 < payload.Length)
        {
            var nalHeader2 = payload[nalOffset + 1];
            var temporalIdPlus1 = nalHeader2 & 0x07;

            // Valid H.265: temporal_id_plus1 must be 1-7 (never 0)
            if (temporalIdPlus1 >= 1)
            {
                var h265NalType = (nalHeader >> 1) & 0x3F;

                var h265Result = h265NalType switch
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

                // If we got a recognized H.265 type, return it
                if (h265Result != NalUnitType.Unknown)
                {
                    return h265Result;
                }
            }
        }

        // Fall back to H.264 interpretation
        var h264NalType = nalHeader & 0x1F;

        return h264NalType switch
        {
            5 => NalUnitType.H264Idr,
            1 => NalUnitType.H264NonIdr,
            7 => NalUnitType.H264Sps,
            8 => NalUnitType.H264Pps,
            9 => NalUnitType.H264Aud,
            _ => NalUnitType.Unknown,
        };
    }

    /// <summary>
    /// Checks if a NAL unit type is a parameter set (SPS, PPS, or VPS).
    /// </summary>
    /// <param name="nalType">The NAL unit type.</param>
    /// <returns>True if the type is a parameter set.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsParameterSetNal(NalUnitType nalType)
    {
        return nalType
            is NalUnitType.H264Sps
                or NalUnitType.H264Pps
                or NalUnitType.H265Sps
                or NalUnitType.H265Pps
                or NalUnitType.H265Vps;
    }

    /// <summary>
    /// Checks if a NAL unit type is an IDR or random access point.
    /// </summary>
    /// <param name="nalType">The NAL unit type.</param>
    /// <returns>True if the type is an IDR or CRA frame.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsIdrNal(NalUnitType nalType) =>
        nalType is NalUnitType.H264Idr or NalUnitType.H265Idr or NalUnitType.H265Cra;

    /// <summary>
    /// Gets the PID from a TS packet header.
    /// </summary>
    /// <param name="packet">A TS packet (must be at least 3 bytes).</param>
    /// <returns>The 13-bit PID value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetPid(ReadOnlySpan<byte> packet) => ((packet[1] & 0x1F) << 8) | packet[2];

    /// <summary>
    /// Checks if a packet has the Payload Unit Start Indicator set.
    /// </summary>
    /// <param name="packet">A TS packet (must be at least 2 bytes).</param>
    /// <returns>True if PUSI is set (bit 6 of byte 1).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HasPusi(ReadOnlySpan<byte> packet) => (packet[1] & 0x40) != 0;

    /// <summary>
    /// Checks if a packet has the Transport Error Indicator set.
    /// </summary>
    /// <param name="packet">A TS packet (must be at least 2 bytes).</param>
    /// <returns>True if TEI is set (bit 7 of byte 1).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HasTransportError(ReadOnlySpan<byte> packet) => (packet[1] & 0x80) != 0;

    /// <summary>
    /// Gets the continuity counter from a TS packet.
    /// </summary>
    /// <param name="packet">A TS packet (must be at least 4 bytes).</param>
    /// <returns>The 4-bit continuity counter (0-15).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetContinuityCounter(ReadOnlySpan<byte> packet) => packet[3] & 0x0F;

    /// <summary>
    /// Gets the scrambling control bits from a TS packet.
    /// </summary>
    /// <param name="packet">A TS packet (must be at least 4 bytes).</param>
    /// <returns>The 2-bit scrambling control (0=not scrambled, 2=even key, 3=odd key).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetScramblingControl(ReadOnlySpan<byte> packet) => (packet[3] >> 6) & 0x03;

    /// <summary>
    /// Checks if a packet has an adaptation field.
    /// </summary>
    /// <param name="packet">A TS packet (must be at least 4 bytes).</param>
    /// <returns>True if adaptation field is present.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HasAdaptationField(ReadOnlySpan<byte> packet)
    {
        var adaptationControl = (packet[3] >> 4) & 0x03;
        return adaptationControl >= 2;
    }

    /// <summary>
    /// Checks if a packet has the Random Access Indicator flag set.
    /// </summary>
    /// <param name="packet">A TS packet (must be at least 6 bytes).</param>
    /// <returns>True if RAI flag is set in adaptation field.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HasRandomAccessIndicator(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 6)
        {
            return false;
        }

        var adaptationControl = (packet[3] >> 4) & 0x03;
        if (adaptationControl < 2)
        {
            return false;
        }

        int adaptationLength = packet[4];
        if (adaptationLength is 0 or > 183)
        {
            return false;
        }

        var flags = packet[5];
        return (flags & 0x40) != 0;
    }

    /// <summary>
    /// Checks if a packet has the discontinuity indicator set.
    /// </summary>
    /// <param name="packet">A TS packet (must be at least 6 bytes).</param>
    /// <returns>True if discontinuity indicator is set.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HasDiscontinuityIndicator(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 6)
        {
            return false;
        }

        var adaptationControl = (packet[3] >> 4) & 0x03;
        if (adaptationControl < 2)
        {
            return false;
        }

        int adaptationLength = packet[4];
        return adaptationLength != 0 && (packet[5] & 0x80) != 0;
    }
}
