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
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Jellyfin.Xtream.Utility;

namespace Jellyfin.Xtream.Benchmarks;

/// <summary>
/// Benchmarks for MPEG-2 CRC-32 calculation per ISO/IEC 13818-1.
/// Measures performance of PSI section validation for TR 101 290 compliance.
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
[DisassemblyDiagnoser(maxDepth: 2)]
public class Crc32Mpeg2Benchmarks
{
    private byte[]? _patSection;
    private byte[]? _pmtSection;
    private byte[]? _catSection;
    private byte[]? _largePmtSection;
    private byte[]? _minimalSection;

    /// <summary>
    /// Setup test data for CRC benchmarks.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        // Minimal PAT section (17 bytes + 4 CRC = 21 bytes total)
        _patSection = BuildPatSection();

        // Typical PMT section (~50 bytes)
        _pmtSection = BuildPmtSection(3); // 3 elementary streams

        // CAT section for scrambled streams
        _catSection = BuildCatSection();

        // Large PMT with many elementary streams (~200 bytes)
        _largePmtSection = BuildPmtSection(15); // 15 elementary streams

        // Minimal section (just 4 bytes CRC)
        _minimalSection = new byte[4];
        uint crc = Crc32Mpeg2.Compute(ReadOnlySpan<byte>.Empty);
        _minimalSection[0] = (byte)(crc >> 24);
        _minimalSection[1] = (byte)(crc >> 16);
        _minimalSection[2] = (byte)(crc >> 8);
        _minimalSection[3] = (byte)crc;
    }

    /// <summary>
    /// Benchmark: Compute CRC-32 for PAT section (17 bytes).
    /// PAT sections are parsed for every program discovery.
    /// </summary>
    [Benchmark(Baseline = true)]
    public uint Compute_PatSection_17Bytes()
    {
        return Crc32Mpeg2.Compute(_patSection.AsSpan(0, _patSection!.Length - 4));
    }

    /// <summary>
    /// Benchmark: Validate PAT section (includes CRC comparison).
    /// </summary>
    [Benchmark]
    public bool Validate_PatSection_21Bytes()
    {
        return Crc32Mpeg2.Validate(_patSection);
    }

    /// <summary>
    /// Benchmark: Compute CRC-32 for typical PMT section (~50 bytes).
    /// </summary>
    [Benchmark]
    public uint Compute_PmtSection_50Bytes()
    {
        return Crc32Mpeg2.Compute(_pmtSection.AsSpan(0, _pmtSection!.Length - 4));
    }

    /// <summary>
    /// Benchmark: Validate PMT section.
    /// </summary>
    [Benchmark]
    public bool Validate_PmtSection()
    {
        return Crc32Mpeg2.Validate(_pmtSection);
    }

    /// <summary>
    /// Benchmark: Compute CRC-32 for CAT section.
    /// CAT parsing is critical for encrypted stream detection.
    /// </summary>
    [Benchmark]
    public uint Compute_CatSection()
    {
        return Crc32Mpeg2.Compute(_catSection.AsSpan(0, _catSection!.Length - 4));
    }

    /// <summary>
    /// Benchmark: Validate CAT section.
    /// </summary>
    [Benchmark]
    public bool Validate_CatSection()
    {
        return Crc32Mpeg2.Validate(_catSection);
    }

    /// <summary>
    /// Benchmark: Compute CRC-32 for large PMT (~200 bytes).
    /// Stress test for complex multi-stream programs.
    /// </summary>
    [Benchmark]
    public uint Compute_LargePmt_200Bytes()
    {
        return Crc32Mpeg2.Compute(_largePmtSection.AsSpan(0, _largePmtSection!.Length - 4));
    }

    /// <summary>
    /// Benchmark: Validate large PMT section.
    /// </summary>
    [Benchmark]
    public bool Validate_LargePmt()
    {
        return Crc32Mpeg2.Validate(_largePmtSection);
    }

    /// <summary>
    /// Benchmark: Extract CRC from section (big-endian read).
    /// </summary>
    [Benchmark]
    public uint ExtractCrc_FromSection()
    {
        return Crc32Mpeg2.ExtractCrc(_pmtSection);
    }

    /// <summary>
    /// Benchmark: Batch validation of multiple sections (simulating stream processing).
    /// Tests cache efficiency when processing multiple PSI tables.
    /// </summary>
    [Benchmark]
    public int BatchValidate_10Sections()
    {
        int validCount = 0;

        for (int i = 0; i < 10; i++)
        {
            if (Crc32Mpeg2.Validate(_patSection))
            {
                validCount++;
            }

            if (Crc32Mpeg2.Validate(_pmtSection))
            {
                validCount++;
            }
        }

        return validCount;
    }

    /// <summary>
    /// Benchmark: Rapid PAT/PMT validation cycle (typical stream startup).
    /// </summary>
    [Benchmark]
    public bool StreamStartup_PatPmtValidation()
    {
        return Crc32Mpeg2.Validate(_patSection) && Crc32Mpeg2.Validate(_pmtSection);
    }

    /// <summary>
    /// Builds a minimal PAT section with CRC.
    /// </summary>
    private static byte[] BuildPatSection()
    {
        byte[] section =
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
            0xE1,
            0x00, // reserved + PMT_PID=256
        ];

        return AppendCrc(section);
    }

    /// <summary>
    /// Builds a PMT section with specified number of elementary streams.
    /// </summary>
    private static byte[] BuildPmtSection(int streamCount)
    {
        // PMT header: 12 bytes, each ES entry: 5 bytes, CRC: 4 bytes
        int sectionLength = 9 + (streamCount * 5);
        var section = new byte[3 + sectionLength];

        section[0] = 0x02; // table_id = PMT
        section[1] = (byte)(0xB0 | ((sectionLength >> 8) & 0x0F));
        section[2] = (byte)(sectionLength & 0xFF);
        section[3] = 0x00;
        section[4] = 0x01; // program_number
        section[5] = 0xC1; // version=0, current_next=1
        section[6] = 0x00; // section_number
        section[7] = 0x00; // last_section_number
        section[8] = 0xE1;
        section[9] = 0x00; // PCR_PID = 256
        section[10] = 0xF0;
        section[11] = 0x00; // program_info_length = 0

        int offset = 12;
        for (int i = 0; i < streamCount; i++)
        {
            // stream_type
            section[offset++] = (byte)(i == 0 ? 0x1B : 0x0F); // H.264 video, AAC audio

            // elementary_PID (13 bits)
            int pid = 256 + i + 1;
            section[offset++] = (byte)(0xE0 | ((pid >> 8) & 0x1F));
            section[offset++] = (byte)(pid & 0xFF);

            // ES_info_length = 0
            section[offset++] = 0xF0;
            section[offset++] = 0x00;
        }

        return AppendCrc(section.AsSpan(0, offset).ToArray());
    }

    /// <summary>
    /// Builds a CAT section with CA descriptors.
    /// </summary>
    private static byte[] BuildCatSection()
    {
        byte[] section =
        [
            0x01, // table_id = CAT
            0xB0,
            0x12, // section_syntax_indicator=1, section_length=18
            0xFF,
            0xFF, // reserved
            0xC1, // version=0, current_next=1
            0x00, // section_number
            0x00, // last_section_number
            // CA descriptor
            0x09, // descriptor_tag
            0x04, // descriptor_length
            0x18,
            0x00, // CA_system_ID (Nagravision)
            0xE0,
            0x20, // reserved + CA_PID
        ];

        return AppendCrc(section);
    }

    /// <summary>
    /// Appends CRC-32 to section data.
    /// </summary>
    private static byte[] AppendCrc(byte[] section)
    {
        uint crc = Crc32Mpeg2.Compute(section);
        var result = new byte[section.Length + 4];
        section.CopyTo(result, 0);
        result[^4] = (byte)(crc >> 24);
        result[^3] = (byte)(crc >> 16);
        result[^2] = (byte)(crc >> 8);
        result[^1] = (byte)crc;
        return result;
    }
}
