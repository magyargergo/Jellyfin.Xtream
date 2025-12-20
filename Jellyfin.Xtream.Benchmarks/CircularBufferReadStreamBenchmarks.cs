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
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Jellyfin.Xtream.Service;

namespace Jellyfin.Xtream.Benchmarks;

/// <summary>
/// Benchmarks for CircularBufferReadStream read performance.
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
[ThreadingDiagnoser]
public class CircularBufferReadStreamBenchmarks
{
    private CircularBufferWriteStream? _writeStream;
    private CircularBufferReadStream? _readStream;
    private byte[]? _readBuffer;
    private byte[]? _sourceData;

    /// <summary>
    /// Gets or sets the buffer size for the circular buffer.
    /// </summary>
    [Params(10 * 1024 * 1024, 50 * 1024 * 1024)] // 10MB, 50MB
    public int BufferSize { get; set; }

    /// <summary>
    /// Gets or sets the read chunk size.
    /// </summary>
    [Params(4 * 1024, 64 * 1024, 1 * 1024 * 1024)] // 4KB, 64KB, 1MB
    public int ReadChunkSize { get; set; }

    /// <summary>
    /// Setup for each benchmark iteration.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _writeStream = new CircularBufferWriteStream(BufferSize);
        _readBuffer = new byte[ReadChunkSize];

        // Pre-populate buffer with realistic streaming data
        _sourceData = new byte[BufferSize];

        // Create MPEG-TS-like data with sync bytes
        for (int i = 0; i < _sourceData.Length; i += 188)
        {
            if (i < _sourceData.Length)
            {
                _sourceData[i] = 0x47; // MPEG-TS sync byte
            }
        }

        Random.Shared.NextBytes(_sourceData.AsSpan(1));

        // Fill the buffer
        _writeStream.Write(_sourceData, 0, _sourceData.Length);

        _readStream = new CircularBufferReadStream(_writeStream);
    }

    /// <summary>
    /// Benchmark: Synchronous read with data available.
    /// </summary>
    /// <returns></returns>
    [Benchmark]
    public int Read_Synchronous()
    {
        // Pre-write data to ensure it's available
        _writeStream!.Write(_readBuffer!, 0, ReadChunkSize);
        return _readStream!.Read(_readBuffer!, 0, ReadChunkSize);
    }

    /// <summary>
    /// Benchmark: Read using Span API.
    /// </summary>
    /// <returns></returns>
    [Benchmark]
    public int ReadSpan()
    {
        // Pre-write data to ensure it's available
        _writeStream!.Write(_readBuffer!, 0, ReadChunkSize);
        return _readStream!.Read(_readBuffer.AsSpan());
    }

    /// <summary>
    /// Benchmark: Asynchronous read.
    /// </summary>
    /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous operation.</placeholder></returns>
    [Benchmark]
    public async Task<int> ReadAsync()
    {
        // Pre-write data to ensure it's available
        await _writeStream!.WriteAsync(_readBuffer!, 0, ReadChunkSize).ConfigureAwait(false);
        return await _readStream!.ReadAsync(_readBuffer!, 0, ReadChunkSize).ConfigureAwait(false);
    }

    /// <summary>
    /// Benchmark: ValueTask ReadAsync with Memory API.
    /// </summary>
    /// <returns></returns>
    [Benchmark]
    public async ValueTask<int> ReadAsyncMemory()
    {
        // Pre-write data to ensure it's available
        await _writeStream!.WriteAsync(_readBuffer!, 0, ReadChunkSize).ConfigureAwait(false);
        return await _readStream!.ReadAsync(_readBuffer.AsMemory()).ConfigureAwait(false);
    }

    /// <summary>
    /// Cleanup after benchmarks.
    /// </summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _readStream?.Dispose();
        _writeStream?.Dispose();
    }
}
