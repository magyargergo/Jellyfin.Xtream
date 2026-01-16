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
using Jellyfin.Xtream.Service.MpegTs.Parsing;

namespace Jellyfin.Xtream.Service.MpegTs.Infrastructure;

/// <summary>
/// Result of a discontinuity injection operation.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct DiscontinuityInjectionResult : IEquatable<DiscontinuityInjectionResult>
{
    /// <summary>Gets the number of packets modified.</summary>
    public int PacketsModified { get; init; }

    /// <summary>Gets whether the injection was successful.</summary>
    public bool Success { get; init; }

    /// <summary>Gets the byte offset of the first modified packet.</summary>
    public int FirstModifiedOffset { get; init; }

    /// <inheritdoc />
    public bool Equals(DiscontinuityInjectionResult other) =>
        PacketsModified == other.PacketsModified && Success == other.Success;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DiscontinuityInjectionResult other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(PacketsModified, Success);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(DiscontinuityInjectionResult left, DiscontinuityInjectionResult right) =>
        left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(DiscontinuityInjectionResult left, DiscontinuityInjectionResult right) =>
        !left.Equals(right);
}

/// <summary>
/// Injects MPEG-TS discontinuity indicators into the stream to signal decoders
/// that a timeline reset has occurred (e.g., provider switch).
/// </summary>
/// <remarks>
/// The discontinuity_indicator in the adaptation field signals to decoders that:
/// <list type="bullet">
/// <item><description>PCR may have an unexpected jump</description></item>
/// <item><description>Continuity counter may reset</description></item>
/// <item><description>Decoder should resync its clock</description></item>
/// </list>
/// This is essential for seamless provider switching.
/// </remarks>
public static class DiscontinuityInjector
{
    /// <summary>
    /// Sets the discontinuity indicator on all packets in the first GOP of data.
    /// Typically you only need to mark the first few packets after a switch.
    /// </summary>
    /// <param name="data">MPEG-TS data (must be packet-aligned).</param>
    /// <param name="maxPackets">Maximum number of packets to mark (default: 5).</param>
    /// <returns>Result indicating success and number of packets modified.</returns>
    public static DiscontinuityInjectionResult InjectDiscontinuity(Span<byte> data, int maxPackets = 5)
    {
        if (data.Length < TsConstants.PacketSize)
        {
            return new DiscontinuityInjectionResult { Success = false };
        }

        var modified = 0;
        var firstOffset = -1;
        var offset = 0;

        while (offset + TsConstants.PacketSize <= data.Length && modified < maxPackets)
        {
            if (data[offset] != TsConstants.SyncByte)
            {
                offset++;
                continue;
            }

            var packet = data.Slice(offset, TsConstants.PacketSize);
            if (TrySetDiscontinuityIndicator(packet))
            {
                if (firstOffset < 0)
                {
                    firstOffset = offset;
                }

                modified++;
            }

            offset += TsConstants.PacketSize;
        }

        return new DiscontinuityInjectionResult
        {
            Success = modified > 0,
            PacketsModified = modified,
            FirstModifiedOffset = firstOffset,
        };
    }

    /// <summary>
    /// Sets the discontinuity indicator on a single MPEG-TS packet.
    /// If the packet has no adaptation field, one is created.
    /// </summary>
    /// <param name="packet">A 188-byte MPEG-TS packet.</param>
    /// <returns>True if the indicator was set successfully.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TrySetDiscontinuityIndicator(Span<byte> packet)
    {
        if (packet.Length != TsConstants.PacketSize || packet[0] != TsConstants.SyncByte)
        {
            return false;
        }

        var adaptationControl = (packet[3] >> 4) & 0x03;

        return adaptationControl switch
        {
            0 => false, // Reserved - cannot modify
            1 => AddAdaptationFieldWithDiscontinuity(packet), // Payload only - add adaptation field
            2 => SetDiscontinuityInExistingField(packet), // Adaptation field only
            3 => SetDiscontinuityInExistingField(packet), // Both adaptation and payload
            _ => false,
        };
    }

    /// <summary>
    /// Adds a minimal adaptation field to a payload-only packet
    /// and sets the discontinuity indicator.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool AddAdaptationFieldWithDiscontinuity(Span<byte> packet)
    {
        // Change adaptation_field_control from 01 (payload only) to 11 (both)
        packet[3] = (byte)((packet[3] & 0xCF) | 0x30);

        // We need to insert a 2-byte adaptation field:
        // Byte 4: adaptation_field_length = 1
        // Byte 5: flags with discontinuity_indicator = 1

        // First, shift payload right by 2 bytes (losing last 2 bytes - acceptable for signaling)
        // This is a trade-off: we sacrifice 2 bytes of payload for signaling
        for (var i = TsConstants.PacketSize - 1; i >= 6; i--)
        {
            packet[i] = packet[i - 2];
        }

        // Set adaptation field
        packet[4] = 0x01; // Length = 1 byte
        packet[5] = 0x80; // discontinuity_indicator = 1, all other flags = 0

        return true;
    }

    /// <summary>
    /// Sets the discontinuity indicator in an existing adaptation field.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool SetDiscontinuityInExistingField(Span<byte> packet)
    {
        int adaptationLength = packet[4];
        if (adaptationLength == 0)
        {
            // Adaptation field is empty - expand it
            return ExpandAndSetDiscontinuity(packet);
        }

        // Set discontinuity_indicator (bit 7 of flags byte)
        packet[5] |= 0x80;
        return true;
    }

    /// <summary>
    /// Expands a zero-length adaptation field to include the discontinuity indicator.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ExpandAndSetDiscontinuity(Span<byte> packet)
    {
        // adaptation_field_length is 0, meaning flags byte doesn't exist
        // We need to expand it to length=1 and add the flags byte

        var adaptationControl = (packet[3] >> 4) & 0x03;

        if (adaptationControl == 2)
        {
            // Adaptation field only - can safely expand
            packet[4] = 0x01;
            packet[5] = 0x80;
            return true;
        }

        if (adaptationControl == 3)
        {
            // Both - need to shift payload
            // Shift payload right by 1 byte
            for (var i = TsConstants.PacketSize - 1; i >= 6; i--)
            {
                packet[i] = packet[i - 1];
            }

            packet[4] = 0x01;
            packet[5] = 0x80;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if a packet has the discontinuity indicator set.
    /// </summary>
    /// <param name="packet">A 188-byte MPEG-TS packet.</param>
    /// <returns>True if the discontinuity indicator is set.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HasDiscontinuityIndicator(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 6 || packet[0] != TsConstants.SyncByte)
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

    /// <summary>
    /// Clears the discontinuity indicator from all packets.
    /// </summary>
    /// <param name="data">MPEG-TS data.</param>
    /// <returns>Number of indicators cleared.</returns>
    public static int ClearDiscontinuityIndicators(Span<byte> data)
    {
        var cleared = 0;
        var offset = 0;

        while (offset + TsConstants.PacketSize <= data.Length)
        {
            if (data[offset] != TsConstants.SyncByte)
            {
                offset++;
                continue;
            }

            var packet = data.Slice(offset, TsConstants.PacketSize);
            var adaptationControl = (packet[3] >> 4) & 0x03;

            if (adaptationControl >= 2)
            {
                int adaptationLength = packet[4];
                if (adaptationLength > 0 && (packet[5] & 0x80) != 0)
                {
                    packet[5] &= 0x7F; // Clear bit 7
                    cleared++;
                }
            }

            offset += TsConstants.PacketSize;
        }

        return cleared;
    }

    /// <summary>
    /// Updates PAT/PMT version numbers to signal table change.
    /// This helps decoders recognize that stream parameters may have changed.
    /// </summary>
    /// <param name="packet">A PAT or PMT packet.</param>
    /// <param name="newVersion">New version number (0-31).</param>
    /// <returns>True if version was updated.</returns>
    public static bool UpdateTableVersion(Span<byte> packet, byte newVersion)
    {
        if (packet.Length != TsConstants.PacketSize || packet[0] != TsConstants.SyncByte)
        {
            return false;
        }

        // Get PID
        var pid = ((packet[1] & 0x1F) << 8) | packet[2];

        // Only process PAT (PID 0) or PMT (typically PID 0x1000-0x1FFE)
        if (pid is not 0 and (< 0x10 or >= 0x2000))
        {
            return false;
        }

        // Find payload start
        var payloadStart = TsPacketHelper.GetPayloadStart(packet);
        if (payloadStart < 0 || payloadStart + 8 > packet.Length)
        {
            return false;
        }

        // Check pointer field
        int pointerField = packet[payloadStart];
        var tableStart = payloadStart + 1 + pointerField;

        if (tableStart + 6 > packet.Length)
        {
            return false;
        }

        // Version is in byte 5 of the table (bits 1-5)
        // Byte structure: reserved(2) | version_number(5) | current_next_indicator(1)
        var versionByte = packet[tableStart + 5];
        var currentNext = versionByte & 0x01;
        packet[tableStart + 5] = (byte)(0xC0 | ((newVersion & 0x1F) << 1) | currentNext);

        return true;
    }
}
