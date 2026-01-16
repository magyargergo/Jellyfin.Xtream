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
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Jellyfin.Xtream.Service.MpegTs.Core;
using Jellyfin.Xtream.Service.MpegTs.Models;
using Jellyfin.Xtream.Utility;

namespace Jellyfin.Xtream.Service.MpegTs.Parsing;

/// <summary>
/// Parser for the Conditional Access Table (CAT) per ISO/IEC 13818-1 Section 2.4.4.6.
/// Extracts CA System IDs and their associated EMM PIDs.
/// Lock-free implementation using copy-on-write for thread safety.
/// </summary>
public sealed class CatParser
{
    // Lock-free copy-on-write: atomic reference swap on version change
    // Reads are always wait-free, writes are rare (only on CAT version change)
    private volatile Dictionary<int, CaSystemInfo> _caSystemsById = [];
    private long _catCrcErrors;
    private int _catVersion = -1;

    /// <summary>
    /// Gets the detected CA systems. Wait-free read via volatile reference.
    /// </summary>
    public IReadOnlyDictionary<int, CaSystemInfo> CaSystems
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _caSystemsById;
    }

    /// <summary>
    /// Gets the number of CAT CRC errors.
    /// </summary>
    public long CatCrcErrors
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _catCrcErrors);
    }

    /// <summary>
    /// Gets the current CAT version.
    /// </summary>
    public int CatVersion
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _catVersion);
    }

    /// <summary>
    /// Parses a CAT section from a TS packet payload.
    /// Optimized for minimal branching and early exit on version match.
    /// </summary>
    /// <param name="payload">The payload containing the CAT section.</param>
    /// <returns>True if the CAT was parsed successfully.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ParseCatSection(ReadOnlySpan<byte> payload)
    {
        // Minimum CAT section: header (8) + CRC (4) = 12 bytes
        var len = payload.Length;
        if (len < 12)
        {
            return false;
        }

        // Get reference for direct indexing without bounds checks
        ref var p = ref MemoryMarshal.GetReference(payload);

        // Skip pointer field if present (first byte after TS header in PUSI packet)
        var offset = 0;
        var firstByte = p;
        if (firstByte != TsConstants.CatTableId)
        {
            // Pointer field present - use unchecked add
            offset = firstByte + 1;
            if ((uint)offset >= (uint)len)
            {
                return false;
            }
        }

        // Validate table_id using Unsafe.Add to avoid bounds check
        if (Unsafe.Add(ref p, offset) != TsConstants.CatTableId)
        {
            return false;
        }

        // Read bytes 1-2 for section_syntax_indicator and section_length
        var byte1 = Unsafe.Add(ref p, offset + 1);
        var byte2 = Unsafe.Add(ref p, offset + 2);

        // section_syntax_indicator must be 1 (top bit of byte1)
        if ((byte1 & 0x80) == 0)
        {
            return false;
        }

        // section_length from bottom 4 bits of byte1 + all of byte2
        var sectionLength = ((byte1 & 0x0F) << 8) | byte2;
        if (sectionLength < 9 || (uint)(offset + 3 + sectionLength) > (uint)len)
        {
            return false;
        }

        // Read byte 5 for version and current_next_indicator
        var byte5 = Unsafe.Add(ref p, offset + 5);
        var version = (byte5 >> 1) & 0x1F;

        // current_next_indicator must be 1 (bottom bit)
        if ((byte5 & 0x01) == 0)
        {
            return false;
        }

        // Fast path: version unchanged, skip parsing entirely
        if (version == _catVersion)
        {
            return true;
        }

        // Validate CRC-32 before parsing (section starts at offset, length = 3 + sectionLength)
        // The CRC covers table_id through CRC itself, result should be 0 for valid section
        var sectionTotalLength = 3 + sectionLength; // 3 bytes header + section_length
        var sectionData = payload.Slice(offset, sectionTotalLength);
        if (!Crc32Mpeg2.Validate(sectionData))
        {
            _ = Interlocked.Increment(ref _catCrcErrors);
            return false;
        }

        // Parse CA descriptors: start at offset+8, end 4 bytes before section end (CRC)
        var descriptorStart = offset + 8;
        var descriptorLength = sectionLength - 9; // 5 (header after length) + 4 (CRC) = 9

        if (descriptorLength > 0)
        {
            ParseCaDescriptorsUnsafe(ref Unsafe.Add(ref p, descriptorStart), descriptorLength);
        }

        _catVersion = version;
        return true;
    }

    /// <summary>
    /// Parses CA descriptors using unsafe pointer arithmetic for maximum performance.
    /// Lock-free: collects new entries then atomically swaps dictionary.
    /// </summary>
    /// <param name="start">Reference to start of descriptor data.</param>
    /// <param name="length">Total length of descriptor data.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ParseCaDescriptorsUnsafe(ref byte start, int length)
    {
        // Collect all new CA systems first (stack-allocated for small counts)
        Span<(int Id, int Pid)> newEntries = stackalloc (int, int)[16]; // CAT rarely has >16 CA descriptors
        var newCount = 0;

        var offset = 0;

        // Parse all descriptors without any synchronization
        while ((uint)(offset + 2) <= (uint)length)
        {
            var tag = Unsafe.Add(ref start, offset);
            var descLen = Unsafe.Add(ref start, offset + 1);

            // Check bounds for descriptor content
            if ((uint)(offset + 2 + descLen) > (uint)length)
            {
                break;
            }

            // CA descriptor tag = 0x09, minimum length = 4
            if (tag == 0x09 && descLen >= 4) // Use & instead of && to avoid branch
            {
                // Read CA_system_ID (16-bit big-endian)
                var caSystemId = (Unsafe.Add(ref start, offset + 2) << 8) | Unsafe.Add(ref start, offset + 3);

                // Read CA_PID (13-bit, masked from 16-bit big-endian)
                var caPid = ((Unsafe.Add(ref start, offset + 4) & 0x1F) << 8) | Unsafe.Add(ref start, offset + 5);

                // Store for batch insert (bounds check for safety)
                if (newCount < newEntries.Length)
                {
                    newEntries[newCount++] = (caSystemId, caPid);
                }
            }

            offset += 2 + descLen;
        }

        // Nothing to add
        if (newCount == 0)
        {
            return;
        }

        // Single-writer optimization: CAT parsing is single-threaded, only readers are concurrent
        // Just copy, add, and atomic publish - no CAS loop needed
        var snapshot = _caSystemsById;
        var updated = new Dictionary<int, CaSystemInfo>(snapshot.Count + newCount);

        // Copy existing entries
        foreach (var kvp in snapshot)
        {
            updated[kvp.Key] = kvp.Value;
        }

        // Add new entries (TryAdd to skip duplicates)
        var added = false;
        for (var i = 0; i < newCount; i++)
        {
            var (id, pid) = newEntries[i];
            if (updated.TryAdd(id, new CaSystemInfo(id, pid)))
            {
                added = true;
            }
        }

        // Only publish if we actually added something
        if (added)
        {
            // Volatile write ensures readers see complete dictionary before reference
            _caSystemsById = updated;
        }
    }

    /// <summary>
    /// Clears all parsed CA systems. Lock-free atomic swap.
    /// </summary>
    public void Clear()
    {
        // Atomic swap to empty dictionary
        _ = Interlocked.Exchange(ref _caSystemsById, []);
        Volatile.Write(ref _catVersion, -1);
    }
}
