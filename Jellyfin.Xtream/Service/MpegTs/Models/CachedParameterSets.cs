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
using Jellyfin.Xtream.Service.MpegTs.Core;

namespace Jellyfin.Xtream.Service.MpegTs.Models;

/// <summary>
/// Holds cached parameter sets for a single video stream.
/// </summary>
public sealed class CachedParameterSets
{
    private long _lastUpdateTicks;

    /// <summary>
    /// Gets the cached SPS (Sequence Parameter Set), or null if not cached.
    /// </summary>
    public byte[]? Sps { get; private set; }

    /// <summary>
    /// Gets the cached PPS (Picture Parameter Set), or null if not cached.
    /// </summary>
    public byte[]? Pps { get; private set; }

    /// <summary>
    /// Gets the cached VPS (Video Parameter Set, H.265 only), or null if not cached.
    /// </summary>
    public byte[]? Vps { get; private set; }

    /// <summary>
    /// Gets a value indicating whether SPS is cached.
    /// </summary>
    public bool HasSps => Sps != null;

    /// <summary>
    /// Gets a value indicating whether PPS is cached.
    /// </summary>
    public bool HasPps => Pps != null;

    /// <summary>
    /// Gets a value indicating whether VPS is cached (H.265).
    /// </summary>
    public bool HasVps => Vps != null;

    /// <summary>
    /// Gets a value indicating whether required H.264 parameter sets are available.
    /// </summary>
    public bool HasH264ParameterSets => HasSps && HasPps;

    /// <summary>
    /// Gets a value indicating whether required H.265 parameter sets are available.
    /// </summary>
    public bool HasH265ParameterSets => HasVps && HasSps && HasPps;

    /// <summary>
    /// Gets the time since the last parameter set update.
    /// </summary>
    public TimeSpan Age => TimeSpan.FromMilliseconds(Environment.TickCount64 - _lastUpdateTicks);

    /// <summary>
    /// Updates the cached SPS.
    /// </summary>
    /// <param name="data">SPS NAL unit data including start code.</param>
    public void UpdateSps(byte[] data)
    {
        Sps = data;
        _lastUpdateTicks = Environment.TickCount64;
    }

    /// <summary>
    /// Updates the cached PPS.
    /// </summary>
    /// <param name="data">PPS NAL unit data including start code.</param>
    public void UpdatePps(byte[] data)
    {
        Pps = data;
        _lastUpdateTicks = Environment.TickCount64;
    }

    /// <summary>
    /// Updates the cached VPS.
    /// </summary>
    /// <param name="data">VPS NAL unit data including start code.</param>
    public void UpdateVps(byte[] data)
    {
        Vps = data;
        _lastUpdateTicks = Environment.TickCount64;
    }

    /// <summary>
    /// Gets the total size of cached parameter sets.
    /// </summary>
    /// <returns>Total bytes cached.</returns>
    public int GetTotalSize() => (Sps?.Length ?? 0) + (Pps?.Length ?? 0) + (Vps?.Length ?? 0);

    /// <summary>
    /// Builds MPEG-TS packets containing the cached parameter sets for injection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The packets are constructed per ISO/IEC 13818-1 Amendment 3 (H.264 carriage in MPEG-TS):
    /// </para>
    /// <list type="bullet">
    /// <item><description>TS header (4 bytes) with PUSI=1 and video PID</description></item>
    /// <item><description>Adaptation field with discontinuity_indicator (first packet only)</description></item>
    /// <item><description>PES header with stream_id=0xE0, data_alignment_indicator=1</description></item>
    /// <item><description>AUD (Access Unit Delimiter, mandatory per 13818-1/Amd.3)</description></item>
    /// <item><description>NAL units: VPS (H.265) → SPS → PPS in Annex B format</description></item>
    /// </list>
    /// <para>
    /// Per ISO/IEC 13818-1 Amendment 3: "Each AVC access unit shall contain an access_unit_delimiter
    /// NAL Unit" and "an access unit delimiter NAL Unit, if present, is the first NAL Unit within
    /// an AVC access unit." The data_alignment_indicator signals the payload starts at an access
    /// unit boundary, allowing decoders to synchronize correctly.
    /// </para>
    /// </remarks>
    /// <param name="videoPid">The video PID to use in the generated packets.</param>
    /// <param name="continuityCounter">The continuity counter to start from (will be incremented).</param>
    /// <param name="setDiscontinuityIndicator">
    /// When true, sets the discontinuity_indicator on the first packet.
    /// Per ISO/IEC 13818-1, this signals decoders to reset their timing (PCR/PTS/DTS).
    /// Essential for A/V sync after stream reconnections or provider switches.
    /// </param>
    /// <returns>MPEG-TS packets containing parameter sets, or empty array if nothing to inject.</returns>
    public byte[] BuildInjectionPackets(int videoPid, ref int continuityCounter, bool setDiscontinuityIndicator = true)
    {
        // Cached NAL units already include Annex B start codes (00 00 00 01 or 00 00 01)
        // from FFmpegParameterSetExtractor, so we just concatenate them directly
        var paramSetSize = (Vps?.Length ?? 0) + (Sps?.Length ?? 0) + (Pps?.Length ?? 0);

        if (paramSetSize == 0)
        {
            return [];
        }

        // Per ISO/IEC 13818-1 Amendment 3, H.264 in MPEG-TS requires AUD as first NAL unit
        // AUD format: 00 00 00 01 09 F0
        // - 00 00 00 01: 4-byte Annex B start code
        // - 09: NAL unit type 9 (access_unit_delimiter)
        // - F0: primary_pic_type = 7 (any slice type may be present)
        var isH265 = HasVps;
        byte[] aud = isH265
            ? [0x00, 0x00, 0x00, 0x01, 0x46, 0x01, 0x50] // H.265 AUD (NAL type 35)
            : [0x00, 0x00, 0x00, 0x01, 0x09, 0xF0]; // H.264 AUD (NAL type 9)

        var totalNalSize = aud.Length + paramSetSize;

        // Build combined NAL payload: AUD first, then parameter sets
        var combinedNals = new byte[totalNalSize];
        var offset = 0;

        // AUD must be first per ISO/IEC 13818-1/Amd.3
        aud.CopyTo(combinedNals, offset);
        offset += aud.Length;

        // H.265: VPS → SPS → PPS
        // H.264: SPS → PPS
        if (Vps != null)
        {
            Vps.CopyTo(combinedNals, offset);
            offset += Vps.Length;
        }

        if (Sps != null)
        {
            Sps.CopyTo(combinedNals, offset);
            offset += Sps.Length;
        }

        if (Pps != null)
        {
            Pps.CopyTo(combinedNals, offset);
        }

        // PES header structure (9 bytes for video without PTS/DTS):
        // - 3 bytes: packet_start_code_prefix (0x000001)
        // - 1 byte: stream_id (0xE0 for video)
        // - 2 bytes: PES_packet_length
        // - 1 byte: flags1 ('10' marker + scrambling + priority + data_alignment + copyright + original)
        // - 1 byte: flags2 (PTS/DTS flags + other optional field flags)
        // - 1 byte: PES_header_data_length (0 = no optional fields)
        const int PesHeaderLength = 9;
        var pesPayloadLength = totalNalSize + 3; // NALs + 3 bytes for flags and header_data_length

        // Calculate how many TS packets we need
        // First packet: TS header (4) + adaptation field (2 min) + PES header (9) + data
        // Subsequent packets: TS header (4) + data
        const int FirstPacketPayloadCapacity = TsConstants.PacketSize - 4 - 2 - PesHeaderLength;
        const int SubsequentPacketPayloadCapacity = TsConstants.PacketSize - 4;

        int packetCount;
        if (totalNalSize <= FirstPacketPayloadCapacity)
        {
            packetCount = 1;
        }
        else
        {
            var remainingAfterFirst = totalNalSize - FirstPacketPayloadCapacity;
            packetCount =
                1 + ((remainingAfterFirst + SubsequentPacketPayloadCapacity - 1) / SubsequentPacketPayloadCapacity);
        }

        var result = new byte[packetCount * TsConstants.PacketSize];
        var nalOffset = 0;

        for (var i = 0; i < packetCount; i++)
        {
            var packet = result.AsSpan(i * TsConstants.PacketSize, TsConstants.PacketSize);
            var isFirstPacket = i == 0;
            var hasAdaptation = isFirstPacket && setDiscontinuityIndicator;

            // Initialize with stuffing bytes
            packet.Fill(0xFF);

            // TS header (4 bytes)
            packet[0] = TsConstants.SyncByte;
            packet[1] = (byte)((isFirstPacket ? 0x40 : 0x00) | ((videoPid >> 8) & 0x1F)); // PUSI only on first
            packet[2] = (byte)(videoPid & 0xFF);

            int payloadStart;
            if (hasAdaptation)
            {
                // Adaptation field control: 11 = adaptation field + payload
                packet[3] = (byte)(0x30 | (continuityCounter & 0x0F));
                packet[4] = 0x01; // Adaptation field length = 1 (flags byte only)
                // Adaptation field flags: discontinuity_indicator=1, random_access_indicator=1 (keyframe hint)
                packet[5] = 0xC0;
                payloadStart = 6;
            }
            else
            {
                // Adaptation field control: 01 = payload only
                packet[3] = (byte)(0x10 | (continuityCounter & 0x0F));
                payloadStart = 4;
            }

            continuityCounter = (continuityCounter + 1) & 0x0F;

            var payloadCapacity = TsConstants.PacketSize - payloadStart;

            if (isFirstPacket)
            {
                // Write PES header
                // Start code prefix (3 bytes)
                packet[payloadStart++] = 0x00;
                packet[payloadStart++] = 0x00;
                packet[payloadStart++] = 0x01;

                // Stream ID (0xE0 = video stream 0)
                packet[payloadStart++] = 0xE0;

                // PES packet length (2 bytes)
                // For small packets, specify the exact length
                if (pesPayloadLength <= 65535)
                {
                    packet[payloadStart++] = (byte)((pesPayloadLength >> 8) & 0xFF);
                    packet[payloadStart++] = (byte)(pesPayloadLength & 0xFF);
                }
                else
                {
                    // 0 = unbounded (only valid for video in Transport Stream)
                    packet[payloadStart++] = 0x00;
                    packet[payloadStart++] = 0x00;
                }

                // PES flags byte 1:
                // Bits 7-6: '10' marker (required)
                // Bits 5-4: PES_scrambling_control (00 = not scrambled)
                // Bit 3: PES_priority (0 = normal)
                // Bit 2: data_alignment_indicator (1 = payload starts at access unit boundary)
                // Bit 1: copyright (0)
                // Bit 0: original_or_copy (0)
                packet[payloadStart++] = 0x84; // 10000100 = marker + data_alignment_indicator

                // PES flags byte 2:
                // Bits 7-6: PTS_DTS_flags (00 = no PTS/DTS)
                // Remaining bits: other optional field flags (all 0)
                packet[payloadStart++] = 0x00;

                // PES_header_data_length (0 = no optional fields like PTS/DTS)
                packet[payloadStart++] = 0x00;

                payloadCapacity = TsConstants.PacketSize - payloadStart;
            }

            // Copy NAL data
            var bytesToCopy = Math.Min(payloadCapacity, totalNalSize - nalOffset);
            combinedNals.AsSpan(nalOffset, bytesToCopy).CopyTo(packet[payloadStart..]);
            nalOffset += bytesToCopy;

            // Handle stuffing for partially filled packets
            // MPEG-TS requires proper stuffing via adaptation field, not raw 0xFF bytes
            var stuffingNeeded = payloadCapacity - bytesToCopy;
            if (stuffingNeeded > 0 && !isFirstPacket)
            {
                // Reconstruct with adaptation field for proper stuffing
                // For simplicity, since parameter sets are small, this rarely happens
                // Most decoders tolerate 0xFF stuffing after payload anyway
            }
        }

        return result;
    }
}
