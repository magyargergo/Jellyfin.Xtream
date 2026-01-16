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

namespace Jellyfin.Xtream.Utility;

/// <summary>
/// MPEG-2 CRC-32 calculator for PSI table validation (PAT, PMT, CAT, etc.).
/// Implements ISO/IEC 13818-1 Annex A CRC-32 calculation.
/// Polynomial: 0x04C11DB7 (normal form), initial value: 0xFFFFFFFF.
/// </summary>
/// <remarks>
/// This class is only used in tests and benchmarks, not in production code.
/// It is kept in the main project for framework compatibility (net8.0).
/// </remarks>
internal static class Crc32Mpeg2
{
    private const uint Polynomial = 0x04C11DB7;
    private const uint InitialValue = 0xFFFFFFFF;

    private static readonly uint[] LookupTable = GenerateLookupTable();

    private static uint[] GenerateLookupTable()
    {
        var table = new uint[256];

        for (uint i = 0; i < 256; i++)
        {
            var crc = i << 24;

            for (var j = 0; j < 8; j++)
            {
                if ((crc & 0x80000000) != 0)
                {
                    crc = (crc << 1) ^ Polynomial;
                }
                else
                {
                    crc <<= 1;
                }
            }

            table[i] = crc;
        }

        return table;
    }

    /// <summary>
    /// Computes the CRC-32 for MPEG-2 PSI sections.
    /// </summary>
    /// <param name="data">The section data including the CRC-32 bytes at the end.</param>
    /// <returns>The computed CRC-32 value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = InitialValue;

        for (var i = 0; i < data.Length; i++)
        {
            var index = (byte)((crc >> 24) ^ data[i]);
            crc = (crc << 8) ^ LookupTable[index];
        }

        return crc;
    }

    /// <summary>
    /// Validates a PSI section by checking if the CRC-32 is correct.
    /// The section must include the 4-byte CRC at the end.
    /// A valid section will have a computed CRC of 0x00000000.
    /// </summary>
    /// <param name="sectionWithCrc">The complete section including the trailing CRC-32.</param>
    /// <returns>True if the CRC is valid, false otherwise.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Validate(ReadOnlySpan<byte> sectionWithCrc) =>
        sectionWithCrc.Length >= 4 && Compute(sectionWithCrc) == 0;

    /// <summary>
    /// Validates a PSI section by comparing computed CRC with the embedded CRC.
    /// </summary>
    /// <param name="sectionWithoutCrc">The section data excluding the CRC-32 bytes.</param>
    /// <param name="embeddedCrc">The CRC-32 value read from the section.</param>
    /// <returns>True if the computed CRC matches the embedded CRC.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Validate(ReadOnlySpan<byte> sectionWithoutCrc, uint embeddedCrc) =>
        Compute(sectionWithoutCrc) == embeddedCrc;

    /// <summary>
    /// Extracts the CRC-32 value from the last 4 bytes of a section.
    /// </summary>
    /// <param name="section">The section data.</param>
    /// <returns>The extracted CRC-32 value, or 0 if section is too short.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ExtractCrc(ReadOnlySpan<byte> section)
    {
        if (section.Length < 4)
        {
            return 0;
        }

        var offset = section.Length - 4;
        return ((uint)section[offset] << 24)
            | ((uint)section[offset + 1] << 16)
            | ((uint)section[offset + 2] << 8)
            | section[offset + 3];
    }
}
