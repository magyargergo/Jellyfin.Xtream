// Copyright (C) 2025  Gergo Magyar

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Jellyfin.Xtream.Service.MpegTs;

/// <summary>
/// High-performance PES (Packetized Elementary Stream) header parser.
/// Extracts PTS and DTS timestamps for A/V synchronization.
/// </summary>
/// <remarks>
/// Performance optimizations:
/// <list type="bullet">
/// <item><description>Branchless PES start code detection using single 32-bit comparison</description></item>
/// <item><description>Unsafe.ReadUnaligned for efficient multi-byte reads</description></item>
/// <item><description>Branchless stream ID range checks using unsigned subtraction</description></item>
/// <item><description>AggressiveInlining on all hot path methods</description></item>
/// </list>
/// </remarks>
public static class PesParser
{
    // PES start code: 0x000001 in big-endian (first 3 bytes)
    // When read as little-endian uint32, bytes [0x00, 0x00, 0x01, XX] become 0xXX010000
    private const uint PesStartCodeMask = 0x00FFFFFF;
    private const uint PesStartCodeValue = 0x00010000; // Little-endian representation

    /// <summary>
    /// Attempts to extract PTS from a PES header within a TS packet payload.
    /// </summary>
    /// <param name="payload">The TS packet payload containing PES data.</param>
    /// <param name="pts">The extracted PTS value if found.</param>
    /// <returns>True if PTS was successfully extracted.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryExtractPts(ReadOnlySpan<byte> payload, out long pts)
    {
        pts = 0;

        // Fast path: Single length check for minimum required bytes (14 for PTS extraction)
        if (payload.Length < 14)
        {
            return false;
        }

        // Branchless PES start code check (0x000001)
        if (!HasPesStartCodeFast(payload))
        {
            return false;
        }

        byte streamId = payload[3];

        // Branchless range check: streamId in [0xC0, 0xEF]
        if (!IsTimestampedStreamFast(streamId))
        {
            return false;
        }

        // Check PTS flag (bit 7 of flags byte)
        byte flags = payload[7];
        if ((flags & 0x80) == 0)
        {
            return false;
        }

        pts = ExtractTimestampFast(payload.Slice(9));
        return true;
    }

    /// <summary>
    /// Attempts to extract both PTS and DTS from a PES header.
    /// </summary>
    /// <param name="payload">The TS packet payload containing PES data.</param>
    /// <param name="pts">The extracted PTS value if found.</param>
    /// <param name="dts">The extracted DTS value if found.</param>
    /// <returns>True if at least PTS was successfully extracted.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryExtractTimestamps(ReadOnlySpan<byte> payload, out long pts, out long dts)
    {
        pts = 0;
        dts = 0;

        if (payload.Length < 14)
        {
            return false;
        }

        if (!HasPesStartCodeFast(payload))
        {
            return false;
        }

        byte streamId = payload[3];
        if (!IsTimestampedStreamFast(streamId))
        {
            return false;
        }

        byte flags = payload[7];
        if ((flags & 0x80) == 0)
        {
            return false;
        }

        pts = ExtractTimestampFast(payload.Slice(9));

        // Check DTS flag (bit 6) and sufficient length
        if ((flags & 0x40) != 0 && payload.Length >= 19)
        {
            dts = ExtractTimestampFast(payload.Slice(14));
        }
        else
        {
            dts = pts;
        }

        return true;
    }

    /// <summary>
    /// Determines if a stream ID represents a timestamped elementary stream.
    /// Uses branchless unsigned subtraction for range check.
    /// </summary>
    /// <param name="streamId">The PES stream ID.</param>
    /// <returns>True if the stream can carry PTS/DTS.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsTimestampedStream(byte streamId)
    {
        return IsTimestampedStreamFast(streamId);
    }

    /// <summary>
    /// Determines if a stream ID represents a video stream.
    /// Uses branchless unsigned subtraction for range check.
    /// </summary>
    /// <param name="streamId">The PES stream ID.</param>
    /// <returns>True if the stream is a video stream.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsVideoStream(byte streamId)
    {
        // Branchless: (streamId - 0xE0) <= (0xEF - 0xE0) using unsigned comparison
        return (uint)(streamId - 0xE0) <= 0x0F;
    }

    /// <summary>
    /// Determines if a stream ID represents an audio stream.
    /// Uses branchless unsigned subtraction for range check.
    /// </summary>
    /// <param name="streamId">The PES stream ID.</param>
    /// <returns>True if the stream is an audio stream.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsAudioStream(byte streamId)
    {
        // Branchless: (streamId - 0xC0) <= (0xDF - 0xC0) using unsigned comparison
        return (uint)(streamId - 0xC0) <= 0x1F;
    }

    /// <summary>
    /// Gets the PES header length including optional fields.
    /// </summary>
    /// <param name="payload">The TS packet payload containing PES data.</param>
    /// <returns>The total PES header length, or -1 if invalid.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetPesHeaderLength(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 9)
        {
            return -1;
        }

        if (!HasPesStartCodeFast(payload))
        {
            return -1;
        }

        byte streamId = payload[3];
        if (!IsTimestampedStreamFast(streamId))
        {
            return 6;
        }

        int headerDataLength = payload[8];
        return 9 + headerDataLength;
    }

    /// <summary>
    /// Writes a corrected PTS value into a PES header.
    /// The payload must already have a valid PTS field.
    /// </summary>
    /// <param name="payload">The mutable TS packet payload containing PES data.</param>
    /// <param name="newPts">The new PTS value to write.</param>
    /// <returns>True if the PTS was successfully written.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryWritePts(Span<byte> payload, long newPts)
    {
        if (payload.Length < 14)
        {
            return false;
        }

        // Check start code using span overload
        uint header = Unsafe.ReadUnaligned<uint>(ref MemoryMarshal.GetReference(payload));
        if ((header & PesStartCodeMask) != PesStartCodeValue)
        {
            return false;
        }

        byte streamId = payload[3];
        if (!IsTimestampedStreamFast(streamId))
        {
            return false;
        }

        byte flags = payload[7];
        if ((flags & 0x80) == 0)
        {
            return false;
        }

        WriteTimestamp(payload.Slice(9), newPts, payload[9] & 0xF0);
        return true;
    }

    /// <summary>
    /// Writes a corrected PTS value at a specific offset in the payload.
    /// Use when you already know the PTS field location.
    /// </summary>
    /// <param name="ptsField">The 5-byte span where PTS is stored.</param>
    /// <param name="pts">The new PTS value.</param>
    /// <param name="markerBits">The marker bits from the first byte (typically 0x20 or 0x30).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteTimestamp(Span<byte> ptsField, long pts, int markerBits)
    {
        ptsField[0] = (byte)((markerBits & 0xF0) | (int)((pts >> 29) & 0x0E) | 0x01);
        ptsField[1] = (byte)((pts >> 22) & 0xFF);
        ptsField[2] = (byte)((int)((pts >> 14) & 0xFE) | 0x01);
        ptsField[3] = (byte)((pts >> 7) & 0xFF);
        ptsField[4] = (byte)((int)((pts << 1) & 0xFE) | 0x01);
    }

    /// <summary>
    /// Branchless PES start code check using single 32-bit comparison.
    /// Checks for 0x000001 prefix in the first 3 bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasPesStartCodeFast(ReadOnlySpan<byte> data)
    {
        // Read 4 bytes as uint32, mask off the 4th byte, compare against start code
        // This is branchless - single comparison instead of 3 separate byte comparisons
        uint header = Unsafe.ReadUnaligned<uint>(ref MemoryMarshal.GetReference(data));
        return (header & PesStartCodeMask) == PesStartCodeValue;
    }

    /// <summary>
    /// Branchless timestamped stream check using unsigned subtraction.
    /// Range: 0xC0-0xEF (audio 0xC0-0xDF, video 0xE0-0xEF).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsTimestampedStreamFast(byte streamId)
    {
        // Branchless range check: (streamId - 0xC0) <= (0xEF - 0xC0)
        // If streamId < 0xC0, the subtraction underflows to a large unsigned value > 0x2F
        // If streamId > 0xEF, the result > 0x2F
        // Only values in [0xC0, 0xEF] produce results in [0x00, 0x2F]
        return (uint)(streamId - 0xC0) <= 0x2F;
    }

    /// <summary>
    /// Optimized 33-bit PTS/DTS timestamp extraction.
    /// PTS is stored in 5 bytes with marker bits interspersed.
    /// </summary>
    /// <remarks>
    /// PTS format (33 bits across 5 bytes):
    /// Byte 0: [0011 PTS32-30 1] - marker bits 0011, 3 PTS bits, marker 1
    /// Byte 1: [PTS29-22] - 8 PTS bits
    /// Byte 2: [PTS21-15 1] - 7 PTS bits, marker 1
    /// Byte 3: [PTS14-7] - 8 PTS bits
    /// Byte 4: [PTS6-0 1] - 7 PTS bits, marker 1
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ExtractTimestampFast(ReadOnlySpan<byte> data)
    {
        // Read bytes individually - this is actually well-optimized by JIT
        // and avoids endianness complexity of multi-byte reads for this format
        long ts = ((long)(data[0] & 0x0E)) << 29;
        ts |= ((long)data[1]) << 22;
        ts |= ((long)(data[2] & 0xFE)) << 14;
        ts |= ((long)data[3]) << 7;
        ts |= ((long)(data[4] & 0xFE)) >> 1;
        return ts;
    }
}
