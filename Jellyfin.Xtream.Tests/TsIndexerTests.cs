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
using System.Linq;
using Jellyfin.Xtream.Service.MpegTs;
using Jellyfin.Xtream.Utility;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Unit tests for TsIndexer MPEG-TS parsing functionality.
/// </summary>
public class TsIndexerTests
{
    private const int DefaultBufferSize = 1024 * 1024; // 1MB

    /// <summary>
    /// Creates a valid TS packet with specified PID.
    /// </summary>
    private static byte[] CreateTsPacket(
        int pid,
        bool hasPayload = true,
        bool hasAdaptation = false,
        int continuityCounter = 0
    )
    {
        byte[] packet = new byte[188];
        packet[0] = 0x47; // Sync byte

        // PID is split across bytes 1-2
        packet[1] = (byte)((pid >> 8) & 0x1F);
        packet[2] = (byte)(pid & 0xFF);

        // Adaptation field control and continuity counter
        int afc = (hasAdaptation ? 0x20 : 0x00) | (hasPayload ? 0x10 : 0x00);
        packet[3] = (byte)(afc | (continuityCounter & 0x0F));

        if (hasAdaptation)
        {
            packet[4] = 0x07; // Adaptation field length
            packet[5] = 0x00; // Flags (no PCR, no RAI, etc.)
        }

        return packet;
    }

    /// <summary>
    /// Creates a valid PAT packet with CRC.
    /// </summary>
    private static byte[] CreatePatPacket(int programNumber = 1, int pmtPid = 256)
    {
        byte[] packet = new byte[188];
        packet[0] = 0x47; // Sync byte
        packet[1] = 0x40; // PUSI=1, PID=0 (PAT)
        packet[2] = 0x00;
        packet[3] = 0x10; // Payload only, CC=0

        // Pointer field
        packet[4] = 0x00;

        // PAT section (without CRC)
        byte[] patSection =
        [
            0x00, // table_id = PAT
            0xB0,
            0x0D, // section_syntax_indicator=1, section_length=13
            0x00,
            0x01, // transport_stream_id
            0xC1, // version=0, current_next=1
            0x00, // section_number
            0x00, // last_section_number
            (byte)(programNumber >> 8),
            (byte)(programNumber & 0xFF), // program_number
            (byte)(0xE0 | ((pmtPid >> 8) & 0x1F)),
            (byte)(pmtPid & 0xFF), // PMT PID
        ];

        // Compute CRC
        uint crc = Crc32Mpeg2.Compute(patSection);

        // Copy section to packet
        Array.Copy(patSection, 0, packet, 5, patSection.Length);

        // Append CRC
        int crcOffset = 5 + patSection.Length;
        packet[crcOffset] = (byte)(crc >> 24);
        packet[crcOffset + 1] = (byte)(crc >> 16);
        packet[crcOffset + 2] = (byte)(crc >> 8);
        packet[crcOffset + 3] = (byte)crc;

        // Fill rest with stuffing
        for (int i = crcOffset + 4; i < 188; i++)
        {
            packet[i] = 0xFF;
        }

        return packet;
    }

    /// <summary>
    /// Tests that TsIndexer initializes with zero counts.
    /// </summary>
    [Fact]
    public void ConstructorInitializesWithZeroCounts()
    {
        var indexer = new TsIndexer(DefaultBufferSize);

        Assert.Equal(0, indexer.TotalPacketsParsed);
        Assert.Equal(0, indexer.TotalBytesProcessed);
        Assert.Equal(0, indexer.ResyncCount);
        Assert.Equal(0, indexer.TotalPacketErrors);
        Assert.Equal(0, indexer.TotalContinuityErrors);
        Assert.Equal(0, indexer.ProgramCount);
    }

    /// <summary>
    /// Tests that empty data does not cause errors.
    /// </summary>
    [Fact]
    public void ProcessChunkEmptyDataNoErrors()
    {
        var indexer = new TsIndexer(DefaultBufferSize);

        indexer.ProcessChunk(ReadOnlySpan<byte>.Empty, 0);

        Assert.Equal(0, indexer.TotalPacketsParsed);
        Assert.Equal(0, indexer.TotalBytesProcessed);
    }

    /// <summary>
    /// Tests sync byte detection at packet start.
    /// </summary>
    [Fact]
    public void ProcessChunkValidSyncByteParsesPacket()
    {
        var indexer = new TsIndexer(DefaultBufferSize);
        var packet = CreateTsPacket(pid: 100);

        indexer.ProcessChunk(packet, 0);

        Assert.Equal(1, indexer.TotalPacketsParsed);
        Assert.Equal(188, indexer.TotalBytesProcessed);
        Assert.Equal(0, indexer.SyncByteErrors);
    }

    /// <summary>
    /// Tests sync byte error detection and recovery.
    /// </summary>
    [Fact]
    public void ProcessChunkInvalidSyncByteDetectsAndRecovers()
    {
        var indexer = new TsIndexer(DefaultBufferSize);

        // Create data with garbage followed by valid packet
        // Need enough space for valid packet (188 bytes) plus garbage prefix plus stride check
        byte[] data = new byte[10 + 188 + 188]; // garbage + packet + stride check space
        data[0] = 0x00; // Invalid sync byte
        data[1] = 0x00;

        // Insert valid packet at offset 10
        var validPacket = CreateTsPacket(pid: 100);
        Array.Copy(validPacket, 0, data, 10, 188);

        // Insert another valid packet at offset 10 + 188 for stride verification
        var validPacket2 = CreateTsPacket(pid: 100, continuityCounter: 1);
        Array.Copy(validPacket2, 0, data, 10 + 188, 188);

        indexer.ProcessChunk(data, 0);

        Assert.True(indexer.SyncByteErrors > 0);
        Assert.True(indexer.SyncRecoveries > 0);
    }

    /// <summary>
    /// Tests that Transport Error Indicator causes packet to be skipped.
    /// </summary>
    [Fact]
    public void ProcessChunkTransportErrorIndicatorSkipsPacket()
    {
        var indexer = new TsIndexer(DefaultBufferSize);
        var packet = CreateTsPacket(pid: 100);

        // Set Transport Error Indicator (bit 7 of byte 1)
        packet[1] |= 0x80;

        indexer.ProcessChunk(packet, 0);

        Assert.Equal(1, indexer.TotalPacketsParsed);
        Assert.Equal(1, indexer.TotalPacketErrors);
    }

    /// <summary>
    /// Tests PAT parsing and program detection.
    /// </summary>
    [Fact]
    public void ProcessChunkValidPatDetectsProgram()
    {
        var indexer = new TsIndexer(DefaultBufferSize);
        var patPacket = CreatePatPacket(programNumber: 1, pmtPid: 256);

        indexer.ProcessChunk(patPacket, 0);

        Assert.Equal(1, indexer.ProgramCount);
        var programs = indexer.GetProgramNumbers();
        Assert.Contains(1, programs);
    }

    /// <summary>
    /// Tests that scrambled packets are detected.
    /// </summary>
    [Fact]
    public void ProcessChunkScrambledPacketDetectsScrambling()
    {
        var indexer = new TsIndexer(DefaultBufferSize);
        var packet = CreateTsPacket(pid: 100);

        // Set scrambling control bits (bits 6-7 of byte 3)
        packet[3] |= 0x80; // Scrambling control = 10 (even key)

        indexer.ProcessChunk(packet, 0);

        Assert.True(indexer.ScrambledPids.Length > 0);
        Assert.Contains(100, indexer.ScrambledPids);
    }

    /// <summary>
    /// Tests IsEncrypted property when scrambled PIDs detected.
    /// </summary>
    [Fact]
    public void IsEncryptedWithScrambledPidsReturnsTrue()
    {
        var indexer = new TsIndexer(DefaultBufferSize);
        var packet = CreateTsPacket(pid: 100);
        packet[3] |= 0xC0; // Scrambling control = 11 (odd key)

        indexer.ProcessChunk(packet, 0);

        Assert.True(indexer.IsEncrypted);
    }

    /// <summary>
    /// Tests IsEncrypted property when no encryption.
    /// </summary>
    [Fact]
    public void IsEncryptedNoEncryptionReturnsFalse()
    {
        var indexer = new TsIndexer(DefaultBufferSize);
        var packet = CreateTsPacket(pid: 100);

        indexer.ProcessChunk(packet, 0);

        Assert.False(indexer.IsEncrypted);
    }

    /// <summary>
    /// Tests partial packet handling across chunks.
    /// </summary>
    [Fact]
    public void ProcessChunkPartialPacketReassemblesCorrectly()
    {
        var indexer = new TsIndexer(DefaultBufferSize);
        var packet = CreateTsPacket(pid: 100);

        // Split packet into two chunks
        byte[] chunk1 = new byte[100];
        byte[] chunk2 = new byte[88];
        Array.Copy(packet, 0, chunk1, 0, 100);
        Array.Copy(packet, 100, chunk2, 0, 88);

        indexer.ProcessChunk(chunk1, 0);
        Assert.Equal(0, indexer.TotalPacketsParsed); // Not yet complete

        indexer.ProcessChunk(chunk2, 100);
        Assert.Equal(1, indexer.TotalPacketsParsed); // Now complete
    }

    /// <summary>
    /// Tests Reset clears all state.
    /// </summary>
    [Fact]
    public void ResetClearsAllState()
    {
        var indexer = new TsIndexer(DefaultBufferSize);
        var patPacket = CreatePatPacket();

        indexer.ProcessChunk(patPacket, 0);
        Assert.Equal(1, indexer.ProgramCount);

        indexer.Reset();

        Assert.Equal(0, indexer.TotalPacketsParsed);
        Assert.Equal(0, indexer.TotalBytesProcessed);
        Assert.Equal(0, indexer.ProgramCount);
    }

    /// <summary>
    /// Tests ResetTimingState preserves program structure.
    /// </summary>
    [Fact]
    public void ResetTimingStatePreservesProgramStructure()
    {
        var indexer = new TsIndexer(DefaultBufferSize);
        var patPacket = CreatePatPacket();

        indexer.ProcessChunk(patPacket, 0);
        Assert.Equal(1, indexer.ProgramCount);

        indexer.ResetTimingState();

        // Program structure should be preserved
        Assert.Equal(1, indexer.ProgramCount);
    }

    /// <summary>
    /// Tests GetDiagnostics returns non-empty string.
    /// </summary>
    [Fact]
    public void GetDiagnosticsReturnsNonEmptyString()
    {
        var indexer = new TsIndexer(DefaultBufferSize);
        var patPacket = CreatePatPacket();

        indexer.ProcessChunk(patPacket, 0);

        string diagnostics = indexer.GetDiagnostics();

        Assert.NotEmpty(diagnostics);
        Assert.Contains("TS Indexer Diagnostics", diagnostics, StringComparison.Ordinal);
        Assert.Contains("Programs Detected", diagnostics, StringComparison.Ordinal);
        Assert.Contains("TR 101 290 Compliance", diagnostics, StringComparison.Ordinal);
    }

    /// <summary>
    /// Tests that TsIndexer implements ITsQualityMonitor.
    /// </summary>
    [Fact]
    public void TsIndexerImplementsITsQualityMonitor()
    {
        var indexer = new TsIndexer(DefaultBufferSize);

        // Should be assignable to interface
        TsIndexer monitor = indexer;

        Assert.NotNull(monitor);
        Assert.Equal(indexer.TotalPacketsParsed, monitor.TotalPacketsParsed);
        Assert.Equal(indexer.IsEncrypted, monitor.IsEncrypted);
    }

    /// <summary>
    /// Tests multiple packets in single chunk.
    /// </summary>
    [Fact]
    public void ProcessChunkMultiplePacketsParsesAll()
    {
        var indexer = new TsIndexer(DefaultBufferSize);

        // Create 3 packets in one chunk
        byte[] data = new byte[188 * 3];
        var packet1 = CreateTsPacket(pid: 100, continuityCounter: 0);
        var packet2 = CreateTsPacket(pid: 100, continuityCounter: 1);
        var packet3 = CreateTsPacket(pid: 100, continuityCounter: 2);

        Array.Copy(packet1, 0, data, 0, 188);
        Array.Copy(packet2, 0, data, 188, 188);
        Array.Copy(packet3, 0, data, 376, 188);

        indexer.ProcessChunk(data, 0);

        Assert.Equal(3, indexer.TotalPacketsParsed);
        Assert.Equal(564, indexer.TotalBytesProcessed);
    }

    /// <summary>
    /// Tests CaSystemIds is empty by default.
    /// </summary>
    [Fact]
    public void CaSystemIdsEmptyByDefault()
    {
        var indexer = new TsIndexer(DefaultBufferSize);

        Assert.Empty(indexer.CaSystemIds);
    }

    /// <summary>
    /// Tests PAT CRC error detection.
    /// </summary>
    [Fact]
    public void ProcessChunkPatWithBadCrcIncrementsPatCrcErrors()
    {
        var indexer = new TsIndexer(DefaultBufferSize);
        var patPacket = CreatePatPacket();

        // Corrupt the CRC
        patPacket[20] ^= 0xFF;

        indexer.ProcessChunk(patPacket, 0);

        Assert.Equal(1, indexer.PatCrcErrors);
        Assert.Equal(0, indexer.ProgramCount); // Should not detect program with bad CRC
    }

    /// <summary>
    /// Tests GetSyncStatus returns Unknown when no programs.
    /// </summary>
    [Fact]
    public void GetSyncStatusNoProgramsReturnsUnknown()
    {
        var indexer = new TsIndexer(DefaultBufferSize);

        var status = indexer.GetSyncStatus();

        Assert.Equal(SyncStatus.Unknown, status);
    }

    /// <summary>
    /// Tests GetCurrentDriftMs returns zero when no programs.
    /// </summary>
    [Fact]
    public void GetCurrentDriftMsNoProgramsReturnsZero()
    {
        var indexer = new TsIndexer(DefaultBufferSize);

        var drift = indexer.GetCurrentDriftMs();

        Assert.Equal(0, drift);
    }

    /// <summary>
    /// Tests GetProgramInfo returns null for non-existent program.
    /// </summary>
    [Fact]
    public void GetProgramInfoNonExistentReturnsNull()
    {
        var indexer = new TsIndexer(DefaultBufferSize);

        var info = indexer.GetProgramInfo(999);

        Assert.Null(info);
    }

    /// <summary>
    /// Tests GetFirstProgramWithVideo returns null when no programs.
    /// </summary>
    [Fact]
    public void GetFirstProgramWithVideoNoProgramsReturnsNull()
    {
        var indexer = new TsIndexer(DefaultBufferSize);

        var program = indexer.GetFirstProgramWithVideo();

        Assert.Null(program);
    }

    /// <summary>
    /// Tests HasVideoPid returns false when no programs.
    /// </summary>
    [Fact]
    public void HasVideoPidNoProgramsReturnsFalse()
    {
        var indexer = new TsIndexer(DefaultBufferSize);

        Assert.False(indexer.HasVideoPid());
    }

    /// <summary>
    /// Tests GetVideoPid returns -1 when no programs.
    /// </summary>
    [Fact]
    public void GetVideoPidNoProgramsReturnsMinusOne()
    {
        var indexer = new TsIndexer(DefaultBufferSize);

        Assert.Equal(-1, indexer.GetVideoPid());
    }

    /// <summary>
    /// Tests GetKeyframeCount returns 0 when no programs.
    /// </summary>
    [Fact]
    public void GetKeyframeCountNoProgramsReturnsZero()
    {
        var indexer = new TsIndexer(DefaultBufferSize);

        Assert.Equal(0, indexer.GetKeyframeCount());
    }

    /// <summary>
    /// Tests GetBestStartOffset returns -1 when no programs.
    /// </summary>
    [Fact]
    public void GetBestStartOffsetNoProgramsReturnsMinusOne()
    {
        var indexer = new TsIndexer(DefaultBufferSize);

        Assert.Equal(-1, indexer.GetBestStartOffset(1000));
    }

    /// <summary>
    /// Tests GetBestSyncPoint returns null when no programs.
    /// </summary>
    [Fact]
    public void GetBestSyncPointNoProgramsReturnsNull()
    {
        var indexer = new TsIndexer(DefaultBufferSize);

        Assert.Null(indexer.GetBestSyncPoint(1000));
    }
}
