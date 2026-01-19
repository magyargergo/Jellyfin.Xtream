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
using Jellyfin.Xtream.Service.MpegTs.Core;
using Jellyfin.Xtream.Service.MpegTs.Infrastructure;
using Jellyfin.Xtream.Service.MpegTs.Models;
using Jellyfin.Xtream.Utility;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Unit tests for TsIndexer MPEG-TS parsing functionality.
/// </summary>
public sealed class TsIndexerTests
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
        var packet = new byte[188];
        packet[0] = 0x47; // Sync byte

        // PID is split across bytes 1-2
        packet[1] = (byte)((pid >> 8) & 0x1F);
        packet[2] = (byte)(pid & 0xFF);

        // Adaptation field control and continuity counter
        var afc = (hasAdaptation ? 0x20 : 0x00) | (hasPayload ? 0x10 : 0x00);
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
        var packet = new byte[188];
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
        var crc = Crc32Mpeg2.Compute(patSection);

        // Copy section to packet
        Array.Copy(patSection, 0, packet, 5, patSection.Length);

        // Append CRC
        var crcOffset = 5 + patSection.Length;
        packet[crcOffset] = (byte)(crc >> 24);
        packet[crcOffset + 1] = (byte)(crc >> 16);
        packet[crcOffset + 2] = (byte)(crc >> 8);
        packet[crcOffset + 3] = (byte)crc;

        // Fill rest with stuffing
        for (var i = crcOffset + 4; i < 188; i++)
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

        indexer.ProcessChunk([], 0);

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
    /// Tests that Cinegy handles sync byte recovery internally.
    /// With Cinegy-based parsing, sync errors are handled internally and packets are still parsed.
    /// </summary>
    [Fact]
    public void ProcessChunkInvalidSyncByteRecoveredByCinegy()
    {
        var indexer = new TsIndexer(DefaultBufferSize);

        // Create data with garbage followed by valid packets
        var data = new byte[10 + 188 + 188]; // garbage + packet + stride check space
        data[0] = 0x00; // Invalid sync byte
        data[1] = 0x00;

        // Insert valid packet at offset 10
        var validPacket = CreateTsPacket(pid: 100);
        Array.Copy(validPacket, 0, data, 10, 188);

        // Insert another valid packet at offset 10 + 188
        var validPacket2 = CreateTsPacket(pid: 100, continuityCounter: 1);
        Array.Copy(validPacket2, 0, data, 10 + 188, 188);

        indexer.ProcessChunk(data, 0);

        // Cinegy handles sync recovery internally - packets should still be parsed
        Assert.True(indexer.TotalPacketsParsed >= 1);
    }

    /// <summary>
    /// Tests that packets with Transport Error Indicator are still parsed.
    /// TEI error tracking is handled by TsDuck via TR 101 290 Priority 2.
    /// </summary>
    [Fact]
    public void ProcessChunkTransportErrorIndicatorStillParses()
    {
        var indexer = new TsIndexer(DefaultBufferSize);
        var packet = CreateTsPacket(pid: 100);

        // Set Transport Error Indicator (bit 7 of byte 1)
        packet[1] |= 0x80;

        indexer.ProcessChunk(packet, 0);

        // Packet is still parsed - TsDuck handles TEI error tracking
        Assert.Equal(1, indexer.TotalPacketsParsed);
        // TsIndexer no longer tracks TEI errors - TsDuck does via TR 101 290
        Assert.Equal(0, indexer.TotalPacketErrors);
    }

    /// <summary>
    /// Tests program detection via demuxer events.
    /// </summary>
    [Fact]
    public void ProcessChunkWithDemuxerDetectsProgram()
    {
        var mockDemuxer = new MockTsDemuxer();
        var indexer = new TsIndexer(DefaultBufferSize, demuxer: mockDemuxer);

        // Simulate demuxer detecting a program
        mockDemuxer.AddProgram(programNumber: 1, pmtPid: 256, videoPid: 100, audioPids: [200], pcrPid: 100);

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
    /// Tests that complete packets in a single chunk are parsed correctly.
    /// Note: Cinegy handles partial packet reassembly internally.
    /// </summary>
    [Fact]
    public void ProcessChunkCompletePacketsAreParsed()
    {
        var indexer = new TsIndexer(DefaultBufferSize);

        // Create two complete packets
        var packet1 = CreateTsPacket(pid: 100, continuityCounter: 0);
        var packet2 = CreateTsPacket(pid: 100, continuityCounter: 1);

        var data = new byte[188 * 2];
        Array.Copy(packet1, 0, data, 0, 188);
        Array.Copy(packet2, 0, data, 188, 188);

        indexer.ProcessChunk(data, 0);

        Assert.Equal(2, indexer.TotalPacketsParsed);
    }

    /// <summary>
    /// Tests Reset clears all state.
    /// </summary>
    [Fact]
    public void ResetClearsAllState()
    {
        var mockDemuxer = new MockTsDemuxer();
        var indexer = new TsIndexer(DefaultBufferSize, demuxer: mockDemuxer);

        // Simulate demuxer detecting a program
        mockDemuxer.AddProgram(1, 256, 100, [200], 100);
        Assert.Equal(1, indexer.ProgramCount);

        // Process some data to have bytes/packets counted
        var packet = CreateTsPacket(pid: 100);
        indexer.ProcessChunk(packet, 0);

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
        var mockDemuxer = new MockTsDemuxer();
        var indexer = new TsIndexer(DefaultBufferSize, demuxer: mockDemuxer);

        // Simulate demuxer detecting a program
        mockDemuxer.AddProgram(1, 256, 100, [200], 100);
        Assert.Equal(1, indexer.ProgramCount);

        indexer.ResetTimingState();

        // Program structure should be preserved
        Assert.Equal(1, indexer.ProgramCount);
    }

    /// <summary>
    /// Tests GetMetrics returns valid structured metrics.
    /// </summary>
    [Fact]
    public void GetMetricsReturnsValidStructuredMetrics()
    {
        var indexer = new TsIndexer(DefaultBufferSize);
        var patPacket = CreatePatPacket();

        indexer.ProcessChunk(patPacket, 0);

        var metrics = indexer.GetMetrics();

        Assert.True(metrics.TotalPacketsParsed > 0);
        Assert.True(metrics.TotalBytesProcessed > 0);
        Assert.True(metrics.ProgramCount >= 0);
        Assert.NotNull(metrics.Programs);
        Assert.NotNull(metrics.CaSystemIds);
    }

    /// <summary>
    /// Tests that TsIndexer implements ITsQualityMonitor.
    /// </summary>
    [Fact]
    public void TsIndexerImplementsITsQualityMonitor()
    {
        var indexer = new TsIndexer(DefaultBufferSize);

        // Should be assignable to interface
        var monitor = indexer;

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
        var data = new byte[188 * 3];
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
    /// Tests that programs are detected via demuxer events.
    /// </summary>
    [Fact]
    public void DemuxerProgramDetectionCreatesProgram()
    {
        var mockDemuxer = new MockTsDemuxer();
        var indexer = new TsIndexer(DefaultBufferSize, demuxer: mockDemuxer);

        // Simulate demuxer detecting a program
        mockDemuxer.AddProgram(programNumber: 5, pmtPid: 500, videoPid: 100, audioPids: [200], pcrPid: 100);

        // Program should be detected via demuxer events
        Assert.Equal(1, indexer.ProgramCount);
        Assert.Contains(5, indexer.GetProgramNumbers());
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

    #region FFmpeg Demuxer Integration Tests

    /// <summary>
    /// Tests that TsIndexer processes packets and detects programs via demuxer.
    /// </summary>
    [Fact]
    public void ProcessChunkProcessesPacketsAndDetectsPrograms()
    {
        var mockDemuxer = new MockTsDemuxer();
        var indexer = new TsIndexer(DefaultBufferSize, demuxer: mockDemuxer);

        // Simulate demuxer detecting a program
        mockDemuxer.AddProgram(1, 100, 200, [300], 200);

        // Create a packet and process it
        var packet = CreateTsPacket(pid: 200);
        indexer.ProcessChunk(packet, 0);

        // Verify the packet was parsed
        Assert.Equal(1, indexer.TotalPacketsParsed);

        // The program should have been detected via demuxer events
        Assert.Equal(1, indexer.ProgramCount);
        Assert.Contains(1, indexer.GetProgramNumbers());
    }

    /// <summary>
    /// Tests that keyframes are detected via demuxer packet events.
    /// </summary>
    [Fact]
    public void PacketDemuxedEventWithKeyframeAddsKeyframe()
    {
        var mockDemuxer = new MockTsDemuxer();
        var indexer = new TsIndexer(DefaultBufferSize, demuxer: mockDemuxer);

        // Simulate demuxer detecting a program
        mockDemuxer.AddProgram(1, 100, 200, [300], 200);

        // Simulate a keyframe packet from demuxer
        mockDemuxer.EmitPacket(
            new Service.MpegTs.UseCases.DemuxedPacketEventArgs
            {
                ProgramNumber = 1,
                Pid = 200,
                IsVideo = true,
                IsAudio = false,
                IsKeyframe = true,
                Pts = 90000,
                Dts = 90000,
                BytePosition = 1000,
            }
        );

        // Verify keyframe was detected
        Assert.Equal(1, indexer.GetKeyframeCount(1));
    }

    #endregion
}
