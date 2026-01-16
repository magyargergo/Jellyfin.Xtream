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
using Jellyfin.Xtream.Service.MpegTs.Core;
using Jellyfin.Xtream.Service.MpegTs.Infrastructure;
using Jellyfin.Xtream.Service.MpegTs.Models;

namespace Jellyfin.Xtream.Benchmarks;

/// <summary>
/// Benchmarks for TsIndexer optimizations including SIMD sync byte search.
/// Tests the performance improvements from industry-grade optimizations.
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
[DisassemblyDiagnoser(maxDepth: 3)]
public class TsIndexerBenchmarks
{
    private TsIndexer? _indexer;
    private byte[]? _validTsStream;
    private byte[]? _corruptedStream;
    private byte[]? _smallChunk;
    private byte[]? _mediumChunk;
    private byte[]? _largeChunk;

    /// <summary>
    /// Gets or sets the buffer size for the indexer.
    /// </summary>
    [Params(16 * 1024 * 1024)] // 16MB typical buffer
    public int BufferSize { get; set; }

    /// <summary>
    /// Setup for each benchmark iteration.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _indexer = new TsIndexer(BufferSize);

        // Generate valid MPEG-TS stream (with proper sync bytes every 188 bytes)
        _validTsStream = GenerateValidMpegTsStream(1 * 1024 * 1024); // 1MB

        // Generate corrupted stream (sync byte at the end to test worst-case search)
        _corruptedStream = new byte[100 * 1024]; // 100KB
        Random.Shared.NextBytes(_corruptedStream);
        _corruptedStream[^1] = 0x47; // Sync byte at the very end

        // Different chunk sizes for processing
        _smallChunk = GenerateValidMpegTsStream(4 * 1024); // 4KB
        _mediumChunk = GenerateValidMpegTsStream(64 * 1024); // 64KB
        _largeChunk = GenerateValidMpegTsStream(1 * 1024 * 1024); // 1MB
    }

    /// <summary>
    /// Benchmark: Process small chunk (4KB) - typical network packet size.
    /// </summary>
    [Benchmark]
    public void ProcessChunk_Small_4KB()
    {
        _indexer!.ProcessChunk(_smallChunk!, 0);
    }

    /// <summary>
    /// Benchmark: Process medium chunk (64KB) - common buffer size.
    /// </summary>
    [Benchmark]
    public void ProcessChunk_Medium_64KB()
    {
        _indexer!.ProcessChunk(_mediumChunk!, 0);
    }

    /// <summary>
    /// Benchmark: Process large chunk (1MB) - high throughput scenario.
    /// </summary>
    [Benchmark]
    public void ProcessChunk_Large_1MB()
    {
        _indexer!.ProcessChunk(_largeChunk!, 0);
    }

    /// <summary>
    /// Benchmark: Continuous processing to test steady-state performance.
    /// This simulates real streaming with multiple chunks.
    /// </summary>
    [Benchmark]
    public void ProcessChunk_Continuous()
    {
        var indexer = new TsIndexer(BufferSize);
        long offset = 0;

        // Process 10MB of data in 64KB chunks
        for (int i = 0; i < 160; i++)
        {
            indexer.ProcessChunk(_mediumChunk!, offset);
            offset += _mediumChunk!.Length;
        }
    }

    /// <summary>
    /// Benchmark: GetVideoPid (cached path - zero allocations).
    /// Tests the performance of the optimized cache-based lookup.
    /// </summary>
    /// <returns></returns>
    [Benchmark]
    public int GetVideoPid_Cached()
    {
        // First call populates cache
        _ = _indexer!.GetVideoPid();

        // Subsequent calls hit cache (zero allocations)
        return _indexer.GetVideoPid();
    }

    /// <summary>
    /// Benchmark: GetKeyframeCount (cached path - zero allocations).
    /// </summary>
    /// <returns></returns>
    [Benchmark]
    public int GetKeyframeCount_Cached()
    {
        _ = _indexer!.GetKeyframeCount();
        return _indexer.GetKeyframeCount();
    }

    /// <summary>
    /// Benchmark: GetFirstProgramWithVideo (cached path - zero allocations).
    /// </summary>
    /// <returns></returns>
    [Benchmark]
    public ProgramInfo? GetFirstProgramWithVideo_Cached()
    {
        _ = _indexer!.GetFirstProgramWithVideo();
        return _indexer.GetFirstProgramWithVideo();
    }

    /// <summary>
    /// Benchmark: Reset operation (should be near-instantaneous).
    /// </summary>
    [Benchmark]
    public void Reset_Operation()
    {
        _indexer!.Reset();
    }

    /// <summary>
    /// Benchmark: Sync recovery with corruption near start (best case).
    /// SIMD should find sync byte quickly.
    /// </summary>
    [Benchmark]
    public void SyncRecovery_BestCase()
    {
        var indexer = new TsIndexer(BufferSize);
        var data = new byte[10000];

        // Corrupt first packet, valid sync at position 200
        Random.Shared.NextBytes(data);
        data[200] = 0x47; // Sync byte
        data[200 + 188] = 0x47; // Next packet sync (for stride verification)

        indexer.ProcessChunk(data, 0);
    }

    /// <summary>
    /// Benchmark: Sync recovery with corruption throughout (worst case).
    /// SIMD advantage is maximized when scanning large corrupted regions.
    /// </summary>
    [Benchmark]
    public void SyncRecovery_WorstCase()
    {
        var indexer = new TsIndexer(BufferSize);
        indexer.ProcessChunk(_corruptedStream!, 0);
    }

    /// <summary>
    /// Generates a valid MPEG-TS stream with proper sync bytes and structure.
    /// </summary>
    private static byte[] GenerateValidMpegTsStream(int sizeBytes)
    {
        const int packetSize = 188;
        int numPackets = sizeBytes / packetSize;
        var stream = new byte[numPackets * packetSize];

        for (int i = 0; i < numPackets; i++)
        {
            int offset = i * packetSize;

            // Sync byte
            stream[offset] = 0x47;

            // Transport Error Indicator (0), Payload Unit Start (varies), Transport Priority (0)
            stream[offset + 1] = (byte)(i % 10 == 0 ? 0x40 : 0x00); // PUSI every 10 packets

            // PID (13 bits) - use different PIDs
            int pid = i % 3 == 0 ? 0 : (i % 3 == 1 ? 256 : 512); // PAT, Video, Audio
            stream[offset + 1] |= (byte)((pid >> 8) & 0x1F);
            stream[offset + 2] = (byte)(pid & 0xFF);

            // Scrambling (00), Adaptation (01 = payload only), Continuity (4 bits)
            stream[offset + 3] = (byte)(0x10 | (i & 0x0F));

            // Fill rest with random data (payload)
            Random.Shared.NextBytes(stream.AsSpan(offset + 4, packetSize - 4));

            // Add some PAT/PMT packets for realism
            if (pid == 0 && i % 10 == 0)
            {
                GeneratePatPacket(stream.AsSpan(offset, packetSize));
            }
        }

        return stream;
    }

    /// <summary>
    /// Generates a minimal PAT packet for realistic stream structure.
    /// </summary>
    private static void GeneratePatPacket(Span<byte> packet)
    {
        // Already has sync byte from GenerateValidMpegTsStream
        // Add minimal PAT structure

        packet[1] = 0x40; // PUSI
        packet[2] = 0x00; // PID = 0 (PAT)
        packet[3] = 0x10; // No adaptation, payload only

        // Pointer field (for PUSI)
        packet[4] = 0x00;

        // Table ID (0x00 for PAT)
        packet[5] = 0x00;

        // Section syntax indicator, section length
        packet[6] = 0x80 | 0x00; // Syntax indicator set
        packet[7] = 0x0D; // Section length = 13 bytes

        // Transport stream ID
        packet[8] = 0x00;
        packet[9] = 0x01;

        // Version, current/next
        packet[10] = 0xC1; // Version 0, current

        // Section number, last section number
        packet[11] = 0x00;
        packet[12] = 0x00;

        // Program number (1), PMT PID (256)
        packet[13] = 0x00;
        packet[14] = 0x01;
        packet[15] = 0xE1; // Reserved bits + PID high
        packet[16] = 0x00; // PID low = 256

        // CRC32 (fake - not validated in indexer)
        packet[17] = 0xFF;
        packet[18] = 0xFF;
        packet[19] = 0xFF;
        packet[20] = 0xFF;

        // Fill rest with stuffing
        packet[21..].Fill(0xFF);
    }

    // ========================================
    // TR 101 290 Quality Monitoring Benchmarks
    // ========================================

    /// <summary>
    /// Benchmark: Access quality monitoring properties.
    /// Tests the overhead of ITsQualityMonitor interface.
    /// </summary>
    [Benchmark]
    public long TR101290_ReadQualityMetrics()
    {
        return _indexer!.TotalPacketsParsed
            + _indexer.TotalPacketErrors
            + _indexer.TotalContinuityErrors
            + _indexer.PatIntervalViolations
            + _indexer.SyncByteErrors
            + _indexer.SyncRecoveries
            + _indexer.PatCrcErrors
            + _indexer.PmtCrcErrors
            + _indexer.CatCrcErrors;
    }

    /// <summary>
    /// Benchmark: Check encryption status (scrambled PID detection).
    /// </summary>
    [Benchmark]
    public bool TR101290_CheckEncryption()
    {
        return _indexer!.IsEncrypted;
    }

    /// <summary>
    /// Benchmark: Get scrambled PIDs array allocation.
    /// </summary>
    [Benchmark]
    public int[] TR101290_GetScrambledPids()
    {
        return _indexer!.ScrambledPids;
    }

    /// <summary>
    /// Benchmark: Get sync status for A/V drift monitoring.
    /// </summary>
    [Benchmark]
    public SyncStatus TR101290_GetSyncStatus()
    {
        return _indexer!.GetSyncStatus();
    }

    /// <summary>
    /// Benchmark: Get A/V drift in milliseconds.
    /// </summary>
    [Benchmark]
    public double TR101290_GetCurrentDriftMs()
    {
        return _indexer!.GetCurrentDriftMs();
    }

    /// <summary>
    /// Benchmark: Full TR 101 290 compliance check cycle.
    /// Simulates periodic quality monitoring.
    /// </summary>
    [Benchmark]
    public int TR101290_FullComplianceCheck()
    {
        int score = 0;

        // Check Priority 1 indicators
        if (_indexer!.SyncByteErrors == 0)
        {
            score++;
        }

        if (_indexer.PatIntervalViolations == 0)
        {
            score++;
        }

        if (_indexer.TotalContinuityErrors == 0)
        {
            score++;
        }

        // Check Priority 2 indicators
        if (_indexer.TotalPacketErrors == 0)
        {
            score++;
        }

        if (_indexer.PatCrcErrors == 0)
        {
            score++;
        }

        if (_indexer.PmtCrcErrors == 0)
        {
            score++;
        }

        if (_indexer.CatCrcErrors == 0)
        {
            score++;
        }

        // Check encryption
        if (!_indexer.IsEncrypted)
        {
            score++;
        }

        // Check A/V sync
        if (_indexer.GetSyncStatus() == SyncStatus.Synchronized)
        {
            score++;
        }

        return score;
    }

    /// <summary>
    /// Benchmark: Process chunk and extract quality metrics.
    /// Full streaming scenario with quality monitoring.
    /// </summary>
    [Benchmark]
    public (long Packets, long Errors, string Diag) TR101290_ProcessAndMonitor()
    {
        var indexer = new TsIndexer(BufferSize);
        indexer.ProcessChunk(_largeChunk!, 0);

        return (
            indexer.TotalPacketsParsed,
            indexer.TotalContinuityErrors + indexer.TotalPacketErrors,
            indexer.SyncRecoveries.ToString()
        );
    }

    /// <summary>
    /// Cleanup after benchmarks.
    /// </summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        // TsIndexer doesn't need explicit cleanup
    }
}
