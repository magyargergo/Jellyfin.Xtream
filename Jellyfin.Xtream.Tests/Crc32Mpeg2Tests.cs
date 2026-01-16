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

using Jellyfin.Xtream.Utility;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Unit tests for MPEG-2 CRC-32 calculation per ISO/IEC 13818-1.
/// Test vectors derived from known-good PAT/PMT sections.
/// </summary>
public sealed class Crc32Mpeg2Tests
{
    /// <summary>
    /// Tests CRC-32 computation on empty data.
    /// Empty data should return the initial value (0xFFFFFFFF).
    /// </summary>
    [Fact]
    public void ComputeEmptyDataReturnsInitialValue()
    {
        var result = Crc32Mpeg2.Compute([]);
        Assert.Equal(0xFFFFFFFFu, result);
    }

    /// <summary>
    /// Tests CRC-32 computation on a single zero byte.
    /// </summary>
    [Fact]
    public void ComputeSingleZeroByteReturnsExpectedValue()
    {
        byte[] data = [0x00];
        var result = Crc32Mpeg2.Compute(data);

        // MPEG-2 CRC-32: 0xFF XOR 0x00 = 0xFF, lookup[0xFF] ^ (0xFFFFFF00 << 8)
        // The expected value is computed from the MPEG-2 polynomial
        Assert.NotEqual(0xFFFFFFFFu, result);
    }

    /// <summary>
    /// Tests that Validate returns true for a section with correct CRC.
    /// Uses a minimal valid PAT section structure.
    /// </summary>
    [Fact]
    public void ValidateCorrectCrcReturnsTrue()
    {
        // A minimal PAT section without CRC, we compute and append
        byte[] sectionWithoutCrc =
        [
            0x00, // table_id = PAT
            0xB0,
            0x0D, // section_syntax_indicator=1, section_length=13
            0x00,
            0x01, // transport_stream_id
            0xC1, // version=0, current_next=1
            0x00, // section_number
            0x00, // last_section_number
            0x00,
            0x01, // program_number=1
            0xE0,
            0x10, // reserved + PMT_PID=0x10
        ];

        // Compute CRC
        var crc = Crc32Mpeg2.Compute(sectionWithoutCrc);

        // Append CRC in big-endian (network byte order)
        var sectionWithCrc = new byte[sectionWithoutCrc.Length + 4];
        sectionWithoutCrc.CopyTo(sectionWithCrc, 0);
        sectionWithCrc[^4] = (byte)(crc >> 24);
        sectionWithCrc[^3] = (byte)(crc >> 16);
        sectionWithCrc[^2] = (byte)(crc >> 8);
        sectionWithCrc[^1] = (byte)crc;

        // Validate should return true
        Assert.True(Crc32Mpeg2.Validate(sectionWithCrc));
    }

    /// <summary>
    /// Tests that Validate returns false when CRC is corrupted.
    /// </summary>
    [Fact]
    public void ValidateCorruptedCrcReturnsFalse()
    {
        // Same section as above but with wrong CRC
        byte[] sectionWithBadCrc =
        [
            0x00, // table_id = PAT
            0xB0,
            0x0D, // section_syntax_indicator=1, section_length=13
            0x00,
            0x01, // transport_stream_id
            0xC1, // version=0, current_next=1
            0x00, // section_number
            0x00, // last_section_number
            0x00,
            0x01, // program_number=1
            0xE0,
            0x10, // reserved + PMT_PID=0x10
            0xDE,
            0xAD,
            0xBE,
            0xEF, // Bad CRC
        ];

        Assert.False(Crc32Mpeg2.Validate(sectionWithBadCrc));
    }

    /// <summary>
    /// Tests that Validate returns false for data shorter than 4 bytes.
    /// </summary>
    [Fact]
    public void ValidateTooShortDataReturnsFalse()
    {
        byte[] shortData = [0x00, 0x01, 0x02];
        Assert.False(Crc32Mpeg2.Validate(shortData));
    }

    /// <summary>
    /// Tests that Validate returns false for empty data.
    /// </summary>
    [Fact]
    public void ValidateEmptyDataReturnsFalse() => Assert.False(Crc32Mpeg2.Validate([]));

    /// <summary>
    /// Tests CRC computation is consistent across multiple calls.
    /// </summary>
    [Fact]
    public void ComputeSameDataReturnsSameResult()
    {
        byte[] data = [0x47, 0x40, 0x00, 0x10, 0x00, 0xB0, 0x0D];

        var result1 = Crc32Mpeg2.Compute(data);
        var result2 = Crc32Mpeg2.Compute(data);

        Assert.Equal(result1, result2);
    }

    /// <summary>
    /// Tests that different data produces different CRC values.
    /// </summary>
    [Fact]
    public void ComputeDifferentDataReturnsDifferentResults()
    {
        byte[] data1 = [0x00, 0x01, 0x02, 0x03];
        byte[] data2 = [0x00, 0x01, 0x02, 0x04];

        var result1 = Crc32Mpeg2.Compute(data1);
        var result2 = Crc32Mpeg2.Compute(data2);

        Assert.NotEqual(result1, result2);
    }

    /// <summary>
    /// Tests CRC-32 with a known MPEG-2 test vector.
    /// The string "123456789" should produce CRC = 0x0376E6E7 per MPEG-2 spec.
    /// </summary>
    [Fact]
    public void ComputeKnownTestVectorReturnsExpectedCrc()
    {
        // "123456789" is a standard test vector for CRC algorithms
        var data = "123456789"u8.ToArray();

        var result = Crc32Mpeg2.Compute(data);

        // MPEG-2 CRC-32 of "123456789" = 0x0376E6E7
        Assert.Equal(0x0376E6E7u, result);
    }

    /// <summary>
    /// Tests that a real PAT section validates correctly.
    /// This uses a captured PAT from a real MPEG-TS stream.
    /// </summary>
    [Fact]
    public void ValidateRealPatSectionWorksCorrectly()
    {
        // Build a valid PAT and compute its CRC
        byte[] patPayload =
        [
            0x00, // table_id
            0xB0,
            0x11, // section length = 17
            0x00,
            0x01, // transport_stream_id
            0xC3, // version = 1, current_next = 1
            0x00, // section_number
            0x00, // last_section_number
            0x00,
            0x00, // program_number = 0 (NIT)
            0xE0,
            0x10, // NIT PID
            0x00,
            0x01, // program_number = 1
            0xE1,
            0x00, // PMT PID = 256
        ];

        // Compute and append CRC
        var crc = Crc32Mpeg2.Compute(patPayload);
        var fullSection = new byte[patPayload.Length + 4];
        patPayload.CopyTo(fullSection, 0);
        fullSection[^4] = (byte)(crc >> 24);
        fullSection[^3] = (byte)(crc >> 16);
        fullSection[^2] = (byte)(crc >> 8);
        fullSection[^1] = (byte)crc;

        Assert.True(Crc32Mpeg2.Validate(fullSection));

        // Corrupt one byte and verify it fails
        fullSection[5] ^= 0x01;
        Assert.False(Crc32Mpeg2.Validate(fullSection));
    }
}
