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
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Jellyfin.Xtream.Service;

namespace Jellyfin.Xtream.Benchmarks;

/// <summary>
/// Benchmarks for CircularBufferWriteStream write performance.
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
[ThreadingDiagnoser]
public class CircularBufferWriteStreamBenchmarks
{
    private CircularBufferWriteStream? _stream;
    private byte[]? _smallBuffer;
    private byte[]? _mediumBuffer;
    private byte[]? _largeBuffer;
    private byte[]? _tsPacket;

    /// <summary>
    /// Gets or sets the buffer size for the circular buffer.
    /// </summary>
    [Params(1 * 1024 * 1024, 10 * 1024 * 1024, 50 * 1024 * 1024)] // 1MB, 10MB, 50MB
    public int BufferSize { get; set; }

    /// <summary>
    /// Setup for each benchmark iteration.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _stream = new CircularBufferWriteStream(BufferSize);

        // Typical MPEG-TS packet
        _tsPacket = new byte[188];
        _tsPacket[0] = 0x47; // Sync byte
        Random.Shared.NextBytes(_tsPacket.AsSpan(1));

        // Various buffer sizes for different scenarios
        _smallBuffer = new byte[4 * 1024]; // 4KB
        Random.Shared.NextBytes(_smallBuffer);

        _mediumBuffer = new byte[64 * 1024]; // 64KB
        Random.Shared.NextBytes(_mediumBuffer);

        _largeBuffer = new byte[1 * 1024 * 1024]; // 1MB
        Random.Shared.NextBytes(_largeBuffer);
    }

    /// <summary>
    /// Benchmark: Write single MPEG-TS packet (188 bytes).
    /// </summary>
    [Benchmark]
    public void Write_TsPacket()
    {
        _stream!.Write(_tsPacket!, 0, _tsPacket!.Length);
    }

    /// <summary>
    /// Benchmark: Write small buffer (4KB).
    /// </summary>
    [Benchmark]
    public void Write_Small_4KB()
    {
        _stream!.Write(_smallBuffer!, 0, _smallBuffer!.Length);
    }

    /// <summary>
    /// Benchmark: Write medium buffer (64KB).
    /// </summary>
    [Benchmark]
    public void Write_Medium_64KB()
    {
        _stream!.Write(_mediumBuffer!, 0, _mediumBuffer!.Length);
    }

    /// <summary>
    /// Benchmark: Write large buffer (1MB).
    /// </summary>
    [Benchmark]
    public void Write_Large_1MB()
    {
        _stream!.Write(_largeBuffer!, 0, _largeBuffer!.Length);
    }

    /// <summary>
    /// Benchmark: Write using Span API (small buffer).
    /// </summary>
    [Benchmark]
    public void WriteSpan_Small_4KB()
    {
        _stream!.Write(_smallBuffer.AsSpan());
    }

    /// <summary>
    /// Benchmark: Write using Span API (medium buffer).
    /// </summary>
    [Benchmark]
    public void WriteSpan_Medium_64KB()
    {
        _stream!.Write(_mediumBuffer.AsSpan());
    }

    /// <summary>
    /// Benchmark: Async write (small buffer).
    /// </summary>
    /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous operation.</placeholder></returns>
    [Benchmark]
    public async Task WriteAsync_Small_4KB()
    {
        await _stream!.WriteAsync(_smallBuffer!, 0, _smallBuffer!.Length).ConfigureAwait(false);
    }

    /// <summary>
    /// Benchmark: Async write (medium buffer).
    /// </summary>
    /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous operation.</placeholder></returns>
    [Benchmark]
    public async Task WriteAsync_Medium_64KB()
    {
        await _stream!.WriteAsync(_mediumBuffer!, 0, _mediumBuffer!.Length).ConfigureAwait(false);
    }

    /// <summary>
    /// Benchmark: ValueTask WriteAsync using Memory API.
    /// </summary>
    /// <returns></returns>
    [Benchmark]
    public async ValueTask WriteAsyncMemory_Small_4KB()
    {
        await _stream!.WriteAsync(_smallBuffer.AsMemory()).ConfigureAwait(false);
    }

    /// <summary>
    /// Benchmark: Continuous writing to test buffer wrap-around.
    /// This simulates a typical streaming scenario.
    /// </summary>
    [Benchmark]
    public void Write_Continuous_BufferWrap()
    {
        var stream = new CircularBufferWriteStream(BufferSize);
        var buffer = new byte[64 * 1024]; // 64KB chunks
        Random.Shared.NextBytes(buffer);

        // Write enough to wrap around the buffer multiple times
        long totalToWrite = (long)BufferSize * 3;
        long written = 0;

        while (written < totalToWrite)
        {
            stream.Write(buffer, 0, buffer.Length);
            written += buffer.Length;
        }
    }

    /// <summary>
    /// Benchmark: Reset operation performance.
    /// </summary>
    [Benchmark]
    public void Reset_Operation()
    {
        _stream!.Reset();
    }

    /// <summary>
    /// Cleanup after benchmarks.
    /// </summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _stream?.Dispose();
    }
}
