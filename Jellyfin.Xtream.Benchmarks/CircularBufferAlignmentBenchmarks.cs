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
using Jellyfin.Xtream.Service;

namespace Jellyfin.Xtream.Benchmarks;

/// <summary>
/// Benchmarks for MPEG-TS alignment and sync detection in CircularBufferReadStream.
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
public class CircularBufferAlignmentBenchmarks
{
    private const int BufferSize = 10 * 1024 * 1024; // 10MB
    private const int TsPacketSize = 188;
    private const byte TsSyncByte = 0x47;

    private CircularBufferWriteStream? _writeStream;
    private byte[]? _tsData;

    /// <summary>
    /// Setup for benchmarks.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _writeStream = new CircularBufferWriteStream(BufferSize);
        _tsData = new byte[TsPacketSize * 1000]; // 1000 packets

        // Create valid MPEG-TS stream
        for (int i = 0; i < _tsData.Length; i += TsPacketSize)
        {
            _tsData[i] = TsSyncByte; // Sync byte
        }

        Random.Shared.NextBytes(_tsData.AsSpan(1));
    }

    /// <summary>
    /// Benchmark: First read with alignment (cold start).
    /// Tests the MPEG-TS sync detection on first read.
    /// </summary>
    [Benchmark]
    public void FirstRead_WithAlignment()
    {
        var writeStream = new CircularBufferWriteStream(BufferSize);
        var tsData = new byte[TsPacketSize * 1000];

        // Create misaligned MPEG-TS stream (offset by 50 bytes)
        var offset = 50;
        for (int i = offset; i < tsData.Length; i += TsPacketSize)
        {
            if (i < tsData.Length)
            {
                tsData[i] = TsSyncByte;
            }
        }

        Random.Shared.NextBytes(tsData);
        writeStream.Write(tsData, 0, tsData.Length);

        using var readStream = new CircularBufferReadStream(writeStream, streamId: "alignment-test");

        var buffer = new byte[TsPacketSize];
        var bytesRead = readStream.Read(buffer, 0, buffer.Length);
    }

    /// <summary>
    /// Benchmark: Reading already-aligned stream (hot path).
    /// </summary>
    [Benchmark]
    public void Read_AlreadyAligned()
    {
        _writeStream!.Reset();
        _writeStream.Write(_tsData!, 0, _tsData!.Length);

        using var readStream = new CircularBufferReadStream(_writeStream, streamId: "aligned-test");

        var buffer = new byte[TsPacketSize];

        // First read triggers alignment
        readStream.Read(buffer, 0, buffer.Length);

        // Second read is already aligned (hot path)
        readStream.Read(buffer, 0, buffer.Length);
    }

    /// <summary>
    /// Benchmark: Multiple sequential reads (typical playback pattern).
    /// </summary>
    [Benchmark]
    public void Read_Sequential_100Packets()
    {
        _writeStream!.Reset();
        _writeStream.Write(_tsData!, 0, _tsData!.Length);

        using var readStream = new CircularBufferReadStream(_writeStream, streamId: "sequential-test");

        var buffer = new byte[TsPacketSize];

        for (int i = 0; i < 100; i++)
        {
            readStream.Read(buffer, 0, buffer.Length);
        }
    }

    /// <summary>
    /// Benchmark: Reading large chunks (multiple packets at once).
    /// </summary>
    [Benchmark]
    public void Read_LargeChunk_50Packets()
    {
        _writeStream!.Reset();
        _writeStream.Write(_tsData!, 0, _tsData!.Length);

        using var readStream = new CircularBufferReadStream(_writeStream, streamId: "chunk-test");

        var buffer = new byte[TsPacketSize * 50];
        readStream.Read(buffer, 0, buffer.Length);
    }

    /// <summary>
    /// Cleanup after benchmarks.
    /// </summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _writeStream?.Dispose();
    }
}
