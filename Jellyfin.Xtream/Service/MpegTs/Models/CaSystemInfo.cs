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

namespace Jellyfin.Xtream.Service.MpegTs.Models;

/// <summary>
/// Information about a Conditional Access system detected in a stream.
/// Per ISO/IEC 13818-1 Section 2.6.16 and ETSI TS 101 162.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="CaSystemInfo"/> struct.
/// </remarks>
/// <param name="systemId">The CA System ID.</param>
/// <param name="emPid">The EMM/ECM PID.</param>
public readonly struct CaSystemInfo(int systemId, int emPid) : IEquatable<CaSystemInfo>
{
    /// <summary>
    /// Direct array lookup table for vendor names by high byte (0x00-0xFF).
    /// O(1) array index access - faster than any dictionary lookup.
    /// null entries indicate unknown vendors for that high byte range.
    /// Based on DVB CA System ID allocation: https://www.dvbservices.com/identifiers/ca_system_id
    /// </summary>
    private static readonly string?[] _vendorByHighByte = CreateHighByteLookupTable();

    /// <summary>
    /// Pre-computed "Unknown (0xNNNN)" strings for all possible CA System IDs.
    /// Avoids string allocation on the hot path - 64KB of pre-allocated strings.
    /// </summary>
    private static readonly string[] _unknownStrings = CreateUnknownStringsTable();

    // Vendor name constants - interned strings for zero-allocation returns
    // Based on DVB CA System ID allocation: https://www.dvbservices.com/identifiers/ca_system_id
    private const string Mediaguard = "Mediaguard/SECA";
    private const string Viaccess = "Viaccess";
    private const string Irdeto = "Irdeto";
    private const string NdsVideoGuard = "NDS/VideoGuard";
    private const string Conax = "Conax";
    private const string Cryptoworks = "Cryptoworks";
    private const string PowerVu = "PowerVu";
    private const string BetaCrypt = "BetaCrypt";
    private const string Nagravision = "Nagravision";
    private const string Tongfang = "Tongfang";
    private const string Biss = "BISS";
    private const string SoftCell = "SoftCell";
    private const string DRECrypt = "DRECrypt";
    private const string Latens = "Latens";
    private const string Codicrypt = "Codicrypt";
    private const string GeneralInstrument = "General Instrument";
    private const string RusCrypto = "RusCrypto";
    private const string Bulcrypt = "Bulcrypt";
    private const string Griffin = "Griffin";

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string?[] CreateHighByteLookupTable()
    {
        var table = new string?[256];
        table[0x01] = Mediaguard;
        table[0x05] = Viaccess;
        table[0x06] = Irdeto;
        table[0x09] = NdsVideoGuard;
        table[0x0B] = Conax;
        table[0x0D] = Cryptoworks;
        table[0x0E] = PowerVu;
        table[0x17] = BetaCrypt;
        table[0x18] = Nagravision;
        table[0x22] = Codicrypt;
        table[0x41] = RusCrypto;
        table[0x47] = GeneralInstrument;
        table[0x4A] = Latens; // Default for 0x4Axx range (sub-ranges handled separately)
        table[0x4B] = Tongfang;
        table[0x55] = Bulcrypt;
        table[0x56] = Griffin;
        return table;
    }

    private static string[] CreateUnknownStringsTable()
    {
        // Pre-allocate all 65536 possible "Unknown (0xNNNN)" strings
        // This trades 64KB memory for zero allocation on unknown ID lookup
        var table = new string[65536];
        for (var i = 0; i < 65536; i++)
        {
            table[i] = $"Unknown (0x{i:X4})";
        }

        return table;
    }

    /// <summary>
    /// Gets the CA System ID (16-bit value per ISO/IEC 13818-1).
    /// </summary>
    public int SystemId { get; } = systemId;

    /// <summary>
    /// Gets the ECM/EMM PID associated with this CA system.
    /// </summary>
    public int EmPid { get; } = emPid;

    /// <summary>
    /// Gets the vendor name for this CA system.
    /// </summary>
    public string VendorName { get; } = GetVendorName(systemId);

    /// <summary>
    /// Gets the vendor name for a CA System ID based on DVB CA System ID allocation.
    /// See: https://www.dvbservices.com/identifiers/ca_system_id
    /// Optimized for minimal branching and zero allocation on hot path.
    /// </summary>
    /// <param name="systemId">The CA System ID.</param>
    /// <returns>The vendor name or "Unknown" if not recognized.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string GetVendorName(int systemId)
    {
        // Mask to 16-bit for safety (CA System IDs are 16-bit per ISO/IEC 13818-1)
        var id = (uint)systemId & 0xFFFF;
        var highByte = id >> 8;

        // Check exact ID matches first using switch expression (JIT optimizes to jump table)
        switch (id)
        {
            case 0x2600:
                return Biss;
            case 0x4AD2:
            case 0x4AD3:
                return SoftCell;
        }

        // Special handling for 0x4Axx range with sub-ranges
        if (highByte == 0x4A)
        {
            // DRECrypt sub-range: 0x4AE0-0x4AEF
            // Branchless: (id - 0x4AE0) <= (0x4AEF - 0x4AE0) using unsigned comparison
            return id - 0x4AE0 <= 0x4AEF - 0x4AE0 ? DRECrypt : Latens;
        }

        // O(1) array lookup by high byte - no bounds check needed as highByte is 0-255
        var vendor = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_vendorByHighByte), (nint)highByte);
        return vendor ?? _unknownStrings[id];
    }

    /// <inheritdoc />
    public bool Equals(CaSystemInfo other) => SystemId == other.SystemId && EmPid == other.EmPid;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is CaSystemInfo other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(SystemId, EmPid);

    /// <inheritdoc />
    public override string ToString() => $"CA System 0x{SystemId:X4} ({VendorName}) on PID {EmPid}";

    /// <summary>
    /// Equality operator.
    /// </summary>
    public static bool operator ==(CaSystemInfo left, CaSystemInfo right) => left.Equals(right);

    /// <summary>
    /// Inequality operator.
    /// </summary>
    public static bool operator !=(CaSystemInfo left, CaSystemInfo right) => !left.Equals(right);
}
