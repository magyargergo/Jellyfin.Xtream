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
/// Parsed MPEG-TS packet structure.
/// Replaces Cinegy.TsDecoder.TransportStream.TsPacket.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct TsPacket
{
    /// <summary>
    /// Gets the packet identifier (13-bit PID).
    /// </summary>
    public int Pid { get; init; }

    /// <summary>
    /// Gets the continuity counter (4-bit, 0-15).
    /// </summary>
    public byte ContinuityCounter { get; init; }

    /// <summary>
    /// Gets a value indicating whether the Transport Error Indicator is set.
    /// </summary>
    public bool TransportErrorIndicator { get; init; }

    /// <summary>
    /// Gets a value indicating whether this is the start of a payload unit.
    /// </summary>
    public bool PayloadUnitStartIndicator { get; init; }

    /// <summary>
    /// Gets a value indicating whether the packet contains payload data.
    /// </summary>
    public bool ContainsPayload { get; init; }

    /// <summary>
    /// Gets a value indicating whether an adaptation field exists.
    /// </summary>
    public bool AdaptationFieldExists { get; init; }

    /// <summary>
    /// Gets the scrambling control value (0 = not scrambled).
    /// </summary>
    public byte ScramblingControl { get; init; }

    /// <summary>
    /// Gets the adaptation field data.
    /// </summary>
    public TsAdaptationField AdaptationField { get; init; }

    /// <summary>
    /// Gets the PES header data.
    /// </summary>
    public TsPesHeader PesHeader { get; init; }

    /// <summary>
    /// Gets the raw source data of the packet.
    /// </summary>
    public byte[]? SourceData { get; init; }

    /// <summary>
    /// Gets the payload data starting offset within SourceData.
    /// </summary>
    public int PayloadOffset { get; init; }

    /// <summary>
    /// Gets the payload length.
    /// </summary>
    public int PayloadLength { get; init; }

    /// <summary>
    /// Parses a TS packet from raw data.
    /// </summary>
    /// <param name="data">188-byte MPEG-TS packet.</param>
    /// <returns>Parsed packet structure.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TsPacket Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < TsConstants.PacketSize || data[0] != TsConstants.SyncByte)
        {
            return default;
        }

        // Parse header bytes
        var header1 = data[1];
        var header2 = data[2];
        var header3 = data[3];

        var tei = (header1 & 0x80) != 0;
        var pusi = (header1 & 0x40) != 0;
        var pid = ((header1 & 0x1F) << 8) | header2;

        var scrambling = (byte)((header3 >> 6) & 0x03);
        var adaptationControl = (byte)((header3 >> 4) & 0x03);
        var cc = (byte)(header3 & 0x0F);

        var hasAdaptation = (adaptationControl & 0x02) != 0;
        var hasPayload = (adaptationControl & 0x01) != 0;

        var payloadOffset = 4;
        TsAdaptationField adaptationField = default;

        if (hasAdaptation && data.Length > 4)
        {
            int adaptationLength = data[4];
            if (adaptationLength > 0 && data.Length > 5)
            {
                adaptationField = TsAdaptationField.Parse(data[4..]);
            }

            payloadOffset = 5 + adaptationLength;
        }

        TsPesHeader pesHeader = default;
        var payloadLength = 0;

        if (hasPayload && payloadOffset < TsConstants.PacketSize)
        {
            payloadLength = TsConstants.PacketSize - payloadOffset;

            // Parse PES header if PUSI is set and we have a PES start code
            if (pusi && payloadOffset + 9 <= TsConstants.PacketSize)
            {
                var payload = data[payloadOffset..];
                if (payload.Length >= 9 && payload[0] == 0x00 && payload[1] == 0x00 && payload[2] == 0x01)
                {
                    pesHeader = TsPesHeader.Parse(payload);
                }
            }
        }

        // Copy source data for later reference
        var sourceData = data[..TsConstants.PacketSize].ToArray();

        return new TsPacket
        {
            Pid = pid,
            ContinuityCounter = cc,
            TransportErrorIndicator = tei,
            PayloadUnitStartIndicator = pusi,
            ContainsPayload = hasPayload,
            AdaptationFieldExists = hasAdaptation,
            ScramblingControl = scrambling,
            AdaptationField = adaptationField,
            PesHeader = pesHeader,
            SourceData = sourceData,
            PayloadOffset = payloadOffset,
            PayloadLength = payloadLength,
        };
    }
}

/// <summary>
/// MPEG-TS adaptation field structure.
/// </summary>
/// <remarks>
/// <para>
/// Optimized for cache efficiency with packed flags and sequential layout.
/// The bool fields are packed into a single byte to reduce struct size from ~24 bytes to 16 bytes.
/// </para>
/// <para>Layout: _flags (1) + padding (3) + FieldSize (4) + Pcr (8) = 16 bytes.</para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct TsAdaptationField
{
    // Packed flags byte - reduces 3 bools (with padding) to 1 byte
    private const byte DiscontinuityFlag = 0x01;
    private const byte RandomAccessFlag = 0x02;
    private const byte PcrPresentFlag = 0x04;

    private readonly byte _flags;

    /// <summary>
    /// Gets the adaptation field length.
    /// </summary>
    public int FieldSize { get; init; }

    /// <summary>
    /// Gets the PCR value (27 MHz).
    /// </summary>
    public ulong Pcr { get; init; }

    /// <summary>
    /// Gets a value indicating whether the discontinuity indicator is set.
    /// </summary>
    public bool DiscontinuityIndicator => (_flags & DiscontinuityFlag) != 0;

    /// <summary>
    /// Gets a value indicating whether the random access indicator is set.
    /// </summary>
    public bool RandomAccessIndicator => (_flags & RandomAccessFlag) != 0;

    /// <summary>
    /// Gets a value indicating whether PCR is present.
    /// </summary>
    public bool PcrFlag => (_flags & PcrPresentFlag) != 0;

    /// <summary>
    /// Initializes a new instance of the <see cref="TsAdaptationField"/> struct.
    /// </summary>
    private TsAdaptationField(byte flags, int fieldSize, ulong pcr)
    {
        _flags = flags;
        FieldSize = fieldSize;
        Pcr = pcr;
    }

    /// <summary>
    /// Parses an adaptation field from raw data.
    /// </summary>
    /// <param name="data">Data starting at adaptation field length byte.</param>
    /// <returns>Parsed adaptation field.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TsAdaptationField Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return default;
        }

        int length = data[0];
        if (length == 0 || length > 183 || data.Length < length + 1)
        {
            return new TsAdaptationField(0, length, 0);
        }

        var rawFlags = data[1];

        // Pack the relevant flags into our compact format
        byte packedFlags = 0;
        if ((rawFlags & 0x80) != 0)
        {
            packedFlags |= DiscontinuityFlag;
        }

        if ((rawFlags & 0x40) != 0)
        {
            packedFlags |= RandomAccessFlag;
        }

        var hasPcr = (rawFlags & 0x10) != 0;
        if (hasPcr)
        {
            packedFlags |= PcrPresentFlag;
        }

        ulong pcr = 0;
        if (hasPcr && data.Length >= 8)
        {
            // PCR is 33-bit base + 9-bit extension (27 MHz)
            // Bytes 2-7: PCR
            var pcrBase =
                ((ulong)data[2] << 25)
                | ((ulong)data[3] << 17)
                | ((ulong)data[4] << 9)
                | ((ulong)data[5] << 1)
                | ((ulong)(data[6] >> 7) & 0x01);

            var pcrExt = ((data[6] & 0x01) << 8) | data[7];
            pcr = (pcrBase * 300) + (ulong)pcrExt;
        }

        return new TsAdaptationField(packedFlags, length, pcr);
    }
}

/// <summary>
/// PES header structure for PTS/DTS extraction.
/// </summary>
/// <remarks>
/// <para>Layout: Pts (8) + Dts (8) + HeaderLength (4) + StreamId (1) + padding (3) = 24 bytes.</para>
/// <para>Ordered by size for optimal alignment and cache efficiency.</para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct TsPesHeader
{
    /// <summary>
    /// Gets the PTS value (90 kHz).
    /// </summary>
    public long Pts { get; init; }

    /// <summary>
    /// Gets the DTS value (90 kHz).
    /// </summary>
    public long Dts { get; init; }

    /// <summary>
    /// Gets the PES header length.
    /// </summary>
    public int HeaderLength { get; init; }

    /// <summary>
    /// Gets the stream ID.
    /// </summary>
    public byte StreamId { get; init; }

    /// <summary>
    /// Parses a PES header from raw data.
    /// </summary>
    /// <param name="data">Data starting at PES start code (00 00 01).</param>
    /// <returns>Parsed PES header.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TsPesHeader Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 9 || data[0] != 0x00 || data[1] != 0x00 || data[2] != 0x01)
        {
            return default;
        }

        var streamId = data[3];

        // Skip non-video/audio stream IDs that don't have PTS/DTS
        // Video: E0-EF, Audio: C0-DF
        if (streamId is < 0xC0 or (>= 0xF0 and not 0xFD))
        {
            return new TsPesHeader { StreamId = streamId };
        }

        // Bytes 4-5: PES packet length (ignored for parsing)
        // Byte 6: Flags (10xxxxxx for PES)
        // Byte 7: PTS/DTS flags (bits 7-6)
        // Byte 8: PES header data length

        var ptsFlags = (byte)((data[7] >> 6) & 0x03);
        int headerDataLength = data[8];
        var totalHeaderLength = 9 + headerDataLength;

        long pts = 0;
        long dts = 0;

        if (ptsFlags >= 2 && data.Length >= 14)
        {
            // PTS present (5 bytes starting at offset 9)
            pts = ParseTimestamp(data[9..]);

            if (ptsFlags == 3 && data.Length >= 19)
            {
                // DTS also present (5 bytes starting at offset 14)
                dts = ParseTimestamp(data[14..]);
            }
        }

        return new TsPesHeader
        {
            StreamId = streamId,
            Pts = pts,
            Dts = dts,
            HeaderLength = totalHeaderLength,
        };
    }

    /// <summary>
    /// Parses a 33-bit timestamp from 5 bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ParseTimestamp(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5)
        {
            return 0;
        }

        // 33-bit timestamp packed in 5 bytes
        // Format: 0010xxx1 xxxxxxxx xxxxxxx1 xxxxxxxx xxxxxxx1
        return ((long)(data[0] & 0x0E) << 29)
            | ((long)data[1] << 22)
            | ((long)(data[2] & 0xFE) << 14)
            | ((long)data[3] << 7)
            | ((long)(data[4] & 0xFE) >> 1);
    }
}
