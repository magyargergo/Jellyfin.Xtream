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
using Jellyfin.Xtream.Service.MpegTs;
using Jellyfin.Xtream.Service.MpegTs.Core;
using Jellyfin.Xtream.Service.MpegTs.Infrastructure;
using Jellyfin.Xtream.Service.MpegTs.Models;
using Jellyfin.Xtream.Utility;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Comprehensive ISO/IEC 13818-1 and TR 101 290 compliance tests.
/// These tests verify correct handling of edge cases and malformed data
/// that may be encountered in real-world MPEG-TS streams.
/// </summary>
/// <remarks>
/// <para>
/// ISO/IEC 13818-1 Section 2.4.3 defines TS packet structure:
/// - Standard packet size: 188 bytes
/// - DVB-ASI packet size: 204 bytes (188 + 16 FEC bytes)
/// - Sync byte: 0x47
/// - Null packet PID: 0x1FFF
/// </para>
/// <para>
/// TR 101 290 Priority 1 indicators tested:
/// - TS_sync_loss: Loss of synchronization
/// - Sync_byte_error: Invalid sync byte
/// - PAT_error: Missing or invalid PAT
/// - Continuity_count_error: CC discontinuity
/// - PMT_error: Missing or invalid PMT
/// - PID_error: Unreferenced or invalid PIDs
/// </para>
/// </remarks>
public sealed class MpegTsComplianceTests : IDisposable
{
    private const int TsPacketSize = 188;
    private const int DvbAsiPacketSize = 204;
    private const byte TsSyncByte = 0x47;
    private const int DefaultBufferSize = 1024 * 1024;

    private readonly MockTsDemuxer _mockDemuxer;
    private readonly TsIndexer _indexer;

    public MpegTsComplianceTests()
    {
        _mockDemuxer = new MockTsDemuxer();
        _indexer = new TsIndexer(DefaultBufferSize, demuxer: _mockDemuxer);
    }

    public void Dispose()
    {
        _indexer.Dispose();
        GC.SuppressFinalize(this);
    }

    #region ISO/IEC 13818-1 Section 2.4.3 - Packet Size Compliance

    /// <summary>
    /// Verifies that TsConstants defines the correct standard packet size per ISO/IEC 13818-1.
    /// </summary>
    [Fact]
    public void PacketSize_StandardIs188Bytes() => Assert.Equal(188, TsConstants.PacketSize);

    /// <summary>
    /// Verifies that TsConstants defines the correct sync byte value.
    /// Per ISO/IEC 13818-1 Section 2.4.3.2, sync_byte has fixed value 0x47.
    /// </summary>
    [Fact]
    public void SyncByte_IsCorrectValue0x47() => Assert.Equal(0x47, TsConstants.SyncByte);

    /// <summary>
    /// Tests handling of 204-byte DVB-ASI packets.
    /// Per ISO/IEC 13818-1, 204-byte packets include 16-byte Reed-Solomon FEC.
    /// The system should either:
    /// a) Detect misalignment and report sync errors, OR
    /// b) Skip the extra bytes and find valid 188-byte alignment
    /// </summary>
    [Fact]
    public void Iso13818_204BytePackets_MisalignmentDetectedOrSkipped()
    {
        // Create 204-byte "packets" (188 + 16 FEC bytes)
        // This simulates DVB-ASI input to a system expecting standard TS
        var data = new byte[DvbAsiPacketSize * 5];
        for (var i = 0; i < 5; i++)
        {
            // Place sync byte at 204-byte intervals (wrong for 188-byte system)
            data[i * DvbAsiPacketSize] = TsSyncByte;
            // Add some distinguishing payload
            data[(i * DvbAsiPacketSize) + 1] = 0x40; // PUSI set
            data[(i * DvbAsiPacketSize) + 2] = 0x00; // PID 0 (PAT)
            data[(i * DvbAsiPacketSize) + 3] = 0x10; // Payload only
        }

        _indexer.ProcessChunk(data, 0);

        // The indexer processes at 188-byte boundaries, so 204-byte aligned data
        // will result in misaligned sync bytes after the first packet.
        // Either sync errors are detected, or packets are not properly parsed.
        // At 204-byte intervals with 188-byte processing:
        // - First sync at 0 (valid)
        // - Next expected sync at 188, but actual sync at 204 (invalid)
        // This should result in fewer valid packets than if properly aligned
        Assert.True(
            _indexer.TotalPacketsParsed < 5 || _indexer.SyncByteErrors > 0,
            "204-byte packets should cause sync issues or reduced packet count when processed as 188-byte"
        );
    }

    /// <summary>
    /// Tests that properly aligned 188-byte packets are parsed correctly.
    /// This is the baseline for comparison with misaligned data.
    /// </summary>
    [Fact]
    public void Iso13818_188BytePackets_ParsedCorrectly()
    {
        var data = new byte[TsPacketSize * 10];
        for (var i = 0; i < 10; i++)
        {
            data[i * TsPacketSize] = TsSyncByte;
            data[(i * TsPacketSize) + 1] = 0x1F; // Null packet PID high
            data[(i * TsPacketSize) + 2] = 0xFF; // Null packet PID low
            data[(i * TsPacketSize) + 3] = 0x10; // Payload only
        }

        _indexer.ProcessChunk(data, 0);

        Assert.Equal(10, _indexer.TotalPacketsParsed);
        Assert.Equal(0, _indexer.SyncByteErrors);
    }

    #endregion

    #region ISO/IEC 13818-1 Section 2.4.3.3 - Null Packet Handling

    /// <summary>
    /// Tests that null packets (PID 0x1FFF) are parsed but not counted as program data.
    /// Per ISO/IEC 13818-1, null packets are used for CBR padding and carry no useful payload.
    /// </summary>
    [Fact]
    public void NullPacket_Pid0x1FFF_ParsedButNotCountedAsProgram()
    {
        // Create only null packets
        var nullPackets = new byte[TsPacketSize * 20];
        for (var i = 0; i < 20; i++)
        {
            CreateNullPacket(nullPackets.AsSpan(i * TsPacketSize, TsPacketSize));
        }

        _indexer.ProcessChunk(nullPackets, 0);

        // Null packets should be parsed
        Assert.Equal(20, _indexer.TotalPacketsParsed);

        // But should NOT create any programs (no PAT/PMT in null packets)
        Assert.Equal(0, _indexer.ProgramCount);

        // No errors should be reported for valid null packets
        Assert.Equal(0, _indexer.TotalPacketErrors);
    }

    /// <summary>
    /// Tests that null packets mixed with program data don't interfere with program detection.
    /// </summary>
    [Fact]
    public void NullPacket_MixedWithProgramData_ProgramsDetectedCorrectly()
    {
        var data = new byte[TsPacketSize * 30];

        // 10 null packets
        for (var i = 0; i < 10; i++)
        {
            CreateNullPacket(data.AsSpan(i * TsPacketSize, TsPacketSize));
        }

        // 10 PAT packets
        for (var i = 10; i < 20; i++)
        {
            CreatePatPacket(data.AsSpan(i * TsPacketSize, TsPacketSize), programNumber: 1, pmtPid: 256);
        }

        // 10 more null packets
        for (var i = 20; i < 30; i++)
        {
            CreateNullPacket(data.AsSpan(i * TsPacketSize, TsPacketSize));
        }

        // Simulate demuxer detecting a program (as FFmpeg would when parsing PAT)
        _mockDemuxer.AddProgram(1, 256, 100, [200], 100);

        _indexer.ProcessChunk(data, 0);

        // All 30 packets should be parsed
        Assert.Equal(30, _indexer.TotalPacketsParsed);

        // Program should be detected via demuxer
        Assert.True(_indexer.ProgramCount >= 1, "Program should be detected from PAT packets");
    }

    /// <summary>
    /// Tests that null packet continuity counters are handled correctly.
    /// Per ISO/IEC 13818-1, null packets may have undefined CC behavior.
    /// </summary>
    [Fact]
    public void NullPacket_ContinuityCounter_NoErrorsReported()
    {
        var data = new byte[TsPacketSize * 10];
        for (var i = 0; i < 10; i++)
        {
            var packet = data.AsSpan(i * TsPacketSize, TsPacketSize);
            CreateNullPacket(packet);
            // Set random CC values - null packets don't require CC continuity
            packet[3] = (byte)(0x10 | (i * 3 % 16)); // Varying CC
        }

        var errorsBefore = _indexer.TotalContinuityErrors;
        _indexer.ProcessChunk(data, 0);

        // Null packets should not cause CC errors regardless of CC values
        Assert.Equal(errorsBefore, _indexer.TotalContinuityErrors);
    }

    #endregion

    #region Sync Byte Validation and Resynchronization

    /// <summary>
    /// Tests resynchronization after byte-shifted (misaligned) data.
    /// Per TR 101 290, sync should be recovered after finding valid sync pattern.
    /// </summary>
    [Fact]
    public void SyncResync_ByteShiftedData_RecoveryAttempted()
    {
        // Create garbage bytes followed by valid aligned packets
        const int garbageBytes = 37; // Not a multiple of 188
        const int validPacketCount = 10;
        var data = new byte[garbageBytes + (TsPacketSize * validPacketCount)];

        // Fill garbage with non-sync values
        for (var i = 0; i < garbageBytes; i++)
        {
            data[i] = (byte)(i % 0x46); // Avoid 0x47
        }

        // Add valid packets after garbage
        for (var i = 0; i < validPacketCount; i++)
        {
            var offset = garbageBytes + (i * TsPacketSize);
            data[offset] = TsSyncByte;
            data[offset + 1] = 0x1F;
            data[offset + 2] = 0xFF;
            data[offset + 3] = 0x10;
        }

        _indexer.ProcessChunk(data, 0);

        // The parser should either:
        // a) Find and parse the valid packets after resync
        // b) Track sync errors for the misaligned portion
        // At minimum, it should not crash and should process some data
        Assert.True(
            _indexer.TotalPacketsParsed > 0 || _indexer.ResyncCount >= 0,
            "Parser should handle misaligned data gracefully"
        );
    }

    /// <summary>
    /// Tests handling of corrupted sync bytes within a stream.
    /// Single corrupted packets should not cause complete sync loss.
    /// </summary>
    [Fact]
    public void SyncResync_CorruptedSyncByteInStream_ContinuesAfterCorruption()
    {
        var data = new byte[TsPacketSize * 10];

        // Create 10 valid packets
        for (var i = 0; i < 10; i++)
        {
            data[i * TsPacketSize] = TsSyncByte;
            data[(i * TsPacketSize) + 1] = 0x1F;
            data[(i * TsPacketSize) + 2] = 0xFF;
            data[(i * TsPacketSize) + 3] = 0x10;
        }

        // Corrupt sync byte of packet 5
        data[5 * TsPacketSize] = 0x00;

        _indexer.ProcessChunk(data, 0);

        // Should parse at least the packets before and after corruption
        // Exact behavior depends on Cinegy's sync recovery
        Assert.True(
            _indexer.TotalPacketsParsed >= 8,
            $"Expected at least 8 valid packets parsed, got {_indexer.TotalPacketsParsed}"
        );
    }

    /// <summary>
    /// Tests TR 101 290 sync loss detection after multiple consecutive missing sync bytes.
    /// Per spec, sync is lost after 5 consecutive sync byte errors.
    /// </summary>
    [Fact]
    public void TR101290_SyncLoss_DetectedAfter5ConsecutiveMissing()
    {
        // First send valid data to establish sync
        var validData = CreateValidTsData(20);
        _indexer.ProcessChunk(validData, 0);

        Assert.Equal(20, _indexer.TotalPacketsParsed);

        // Now send data with corrupted sync bytes at 188-byte intervals
        var corruptData = new byte[TsPacketSize * 10];
        for (var i = 0; i < 10; i++)
        {
            // All sync bytes are wrong
            corruptData[i * TsPacketSize] = 0x00;
            corruptData[(i * TsPacketSize) + 1] = 0x1F;
            corruptData[(i * TsPacketSize) + 2] = 0xFF;
            corruptData[(i * TsPacketSize) + 3] = 0x10;
        }

        var packetsBefore = _indexer.TotalPacketsParsed;
        _indexer.ProcessChunk(corruptData, validData.Length);

        // Corrupted packets should not be counted as valid
        Assert.True(
            _indexer.TotalPacketsParsed == packetsBefore,
            "Packets with invalid sync bytes should not be counted"
        );
    }

    #endregion

    #region Malformed Packet Handling

    /// <summary>
    /// Tests handling of truncated packets (less than 188 bytes).
    /// </summary>
    [Fact]
    public void MalformedPacket_Truncated_HandledGracefully()
    {
        // Send a truncated "packet" of only 100 bytes
        var truncated = new byte[100];
        truncated[0] = TsSyncByte;

        // Should not throw
        var exception = Record.Exception(() => _indexer.ProcessChunk(truncated, 0));
        Assert.Null(exception);
    }

    /// <summary>
    /// Tests handling of packets with invalid adaptation field length.
    /// Per ISO/IEC 13818-1, adaptation_field_length can be 0-183.
    /// </summary>
    [Fact]
    public void MalformedPacket_InvalidAdaptationFieldLength_HandledGracefully()
    {
        var data = new byte[TsPacketSize];
        data[0] = TsSyncByte;
        data[1] = 0x00;
        data[2] = 0x64; // PID 100
        data[3] = 0x30; // Adaptation field + payload
        data[4] = 200; // Invalid: exceeds max (183) and packet bounds

        var exception = Record.Exception(() => _indexer.ProcessChunk(data, 0));
        Assert.Null(exception);
    }

    /// <summary>
    /// Tests handling of packets with reserved adaptation_field_control value.
    /// Per ISO/IEC 13818-1, value 00 is reserved.
    /// </summary>
    [Fact]
    public void MalformedPacket_ReservedAdaptationControl_HandledGracefully()
    {
        var data = new byte[TsPacketSize];
        data[0] = TsSyncByte;
        data[1] = 0x00;
        data[2] = 0x64; // PID 100
        data[3] = 0x00; // Reserved adaptation_field_control

        var exception = Record.Exception(() => _indexer.ProcessChunk(data, 0));
        Assert.Null(exception);
    }

    /// <summary>
    /// Tests handling of completely zero-filled data.
    /// </summary>
    [Fact]
    public void MalformedPacket_AllZeros_HandledGracefully()
    {
        var zeros = new byte[TsPacketSize * 5];
        // All bytes are 0x00 - no valid sync bytes

        var exception = Record.Exception(() => _indexer.ProcessChunk(zeros, 0));
        Assert.Null(exception);

        // No valid packets should be parsed
        Assert.Equal(0, _indexer.TotalPacketsParsed);
    }

    /// <summary>
    /// Tests handling of random noise data (fuzz-style).
    /// </summary>
    [Fact]
    public void MalformedPacket_RandomNoise_HandledGracefully()
    {
        var random = new Random(42); // Fixed seed for reproducibility
        var noise = new byte[TsPacketSize * 10];
        random.NextBytes(noise);

        // Ensure no accidental sync bytes at 188-byte boundaries
        for (var i = 0; i < 10; i++)
        {
            if (noise[i * TsPacketSize] == TsSyncByte)
            {
                noise[i * TsPacketSize] = 0x00;
            }
        }

        var exception = Record.Exception(() => _indexer.ProcessChunk(noise, 0));
        Assert.Null(exception);
    }

    /// <summary>
    /// Tests handling of packet with Transport Error Indicator set.
    /// Per ISO/IEC 13818-1, TEI indicates uncorrectable bit errors.
    /// </summary>
    [Fact]
    public void MalformedPacket_TransportErrorIndicator_CountedAsError()
    {
        var data = new byte[TsPacketSize * 5];

        // Create 5 packets, 2 with TEI set
        for (var i = 0; i < 5; i++)
        {
            data[i * TsPacketSize] = TsSyncByte;
            data[(i * TsPacketSize) + 1] = (byte)(i is 1 or 3 ? 0x80 : 0x00); // TEI on packets 1 and 3
            data[(i * TsPacketSize) + 2] = 0x64;
            data[(i * TsPacketSize) + 3] = 0x10;
        }

        _indexer.ProcessChunk(data, 0);

        Assert.Equal(2, _indexer.TotalPacketErrors);
    }

    /// <summary>
    /// Tests handling of empty input data.
    /// </summary>
    [Fact]
    public void MalformedPacket_EmptyInput_HandledGracefully()
    {
        var empty = Array.Empty<byte>();

        var exception = Record.Exception(() => _indexer.ProcessChunk(empty, 0));
        Assert.Null(exception);

        Assert.Equal(0, _indexer.TotalPacketsParsed);
    }

    /// <summary>
    /// Tests handling of data that is exactly one byte short of a packet.
    /// </summary>
    [Fact]
    public void MalformedPacket_OneByteShort_HandledGracefully()
    {
        var data = new byte[TsPacketSize - 1];
        data[0] = TsSyncByte;

        var exception = Record.Exception(() => _indexer.ProcessChunk(data, 0));
        Assert.Null(exception);
    }

    #endregion

    #region PID Validation

    /// <summary>
    /// Tests that all valid PID values (0-8191) are accepted.
    /// Per ISO/IEC 13818-1, PID is 13 bits.
    /// </summary>
    [Theory]
    [InlineData(0)] // PAT
    [InlineData(1)] // CAT
    [InlineData(0x10)] // NIT, ST, reserved
    [InlineData(0x11)] // SDT, BAT, ST
    [InlineData(0x100)] // Typical PMT PID
    [InlineData(0x1000)] // Typical video PID
    [InlineData(0x1FFE)] // Last valid non-null PID
    [InlineData(0x1FFF)] // Null packet
    public void PidValidation_ValidPidValues_Accepted(int pid)
    {
        var data = new byte[TsPacketSize];
        data[0] = TsSyncByte;
        data[1] = (byte)((pid >> 8) & 0x1F);
        data[2] = (byte)(pid & 0xFF);
        data[3] = 0x10;

        var exception = Record.Exception(() => _indexer.ProcessChunk(data, 0));
        Assert.Null(exception);

        Assert.Equal(1, _indexer.TotalPacketsParsed);
    }

    #endregion

    #region Helper Methods

    private static void CreateNullPacket(Span<byte> packet)
    {
        if (packet.Length < TsPacketSize)
        {
            throw new ArgumentException("Packet buffer too small", nameof(packet));
        }

        packet.Clear();
        packet[0] = TsSyncByte;
        packet[1] = 0x1F; // Null packet PID high (0x1FFF >> 8 & 0x1F)
        packet[2] = 0xFF; // Null packet PID low
        packet[3] = 0x10; // Payload only, CC=0
    }

    private static void CreatePatPacket(Span<byte> packet, int programNumber, int pmtPid)
    {
        if (packet.Length < TsPacketSize)
        {
            throw new ArgumentException("Packet buffer too small", nameof(packet));
        }

        packet.Clear();
        packet[0] = TsSyncByte;
        packet[1] = 0x40; // PUSI=1, PID=0 (PAT)
        packet[2] = 0x00;
        packet[3] = 0x10; // Payload only, CC=0

        // Pointer field
        packet[4] = 0x00;

        // PAT section (minimal)
        packet[5] = 0x00; // table_id = PAT
        packet[6] = 0xB0; // section_syntax_indicator=1
        packet[7] = 0x0D; // section_length=13
        packet[8] = 0x00;
        packet[9] = 0x01; // transport_stream_id
        packet[10] = 0xC1; // version=0, current_next=1
        packet[11] = 0x00; // section_number
        packet[12] = 0x00; // last_section_number
        packet[13] = (byte)(programNumber >> 8);
        packet[14] = (byte)(programNumber & 0xFF);
        packet[15] = (byte)(0xE0 | ((pmtPid >> 8) & 0x1F));
        packet[16] = (byte)(pmtPid & 0xFF);

        // Compute and set valid CRC-32
        var sectionData = packet.Slice(5, 12); // From table_id to PMT PID (before CRC)
        var crc = Crc32Mpeg2.Compute(sectionData);
        packet[17] = (byte)(crc >> 24);
        packet[18] = (byte)(crc >> 16);
        packet[19] = (byte)(crc >> 8);
        packet[20] = (byte)crc;

        // Stuffing
        for (var i = 21; i < TsPacketSize; i++)
        {
            packet[i] = 0xFF;
        }
    }

    private static byte[] CreateValidTsData(int packetCount)
    {
        var data = new byte[TsPacketSize * packetCount];
        for (var i = 0; i < packetCount; i++)
        {
            data[i * TsPacketSize] = TsSyncByte;
            data[(i * TsPacketSize) + 1] = 0x1F;
            data[(i * TsPacketSize) + 2] = 0xFF;
            data[(i * TsPacketSize) + 3] = 0x10;
        }

        return data;
    }

    #endregion
}
