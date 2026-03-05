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
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Jellyfin.Xtream.Service;

namespace Jellyfin.Xtream.Benchmarks;

/// <summary>
/// Benchmarks for concurrent read/write scenarios simulating real-world streaming.
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
[ThreadingDiagnoser]
public class ConcurrentCircularBufferBenchmarks
{
    private const int BufferSize = 50 * 1024 * 1024; // 50MB
    private const int ChunkSize = 64 * 1024; // 64KB chunks

    /// <summary>
    /// Gets or sets the number of concurrent readers.
    /// </summary>
    [Params(1, 2, 4, 8)]
    public int ReaderCount { get; set; }

    /// <summary>
    /// Benchmark: Single writer with multiple concurrent readers.
    /// Simulates a typical IPTV restreaming scenario.
    /// </summary>
    /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous operation.</placeholder></returns>
    [Benchmark]
    public async Task ConcurrentReadWrite_MultipleReaders()
    {
        using var writeStream = new CircularBufferWriteStream(BufferSize);
        using var cts = new CancellationTokenSource();

        // Start writer task
        var writerTask = Task.Run(
            async () =>
            {
                var buffer = new byte[ChunkSize];
                Random.Shared.NextBytes(buffer);

                // Write for 1 second
                var endTime = DateTime.UtcNow.AddSeconds(1);
                while (DateTime.UtcNow < endTime && !cts.Token.IsCancellationRequested)
                {
                    await writeStream.WriteAsync(buffer, 0, buffer.Length, cts.Token).ConfigureAwait(false);
                    await Task.Delay(1, cts.Token).ConfigureAwait(false); // Simulate network delay
                }
            },
            cts.Token
        );

        // Start reader tasks
        var readerTasks = new List<Task>();
        for (int i = 0; i < ReaderCount; i++)
        {
            var readerId = i;
            var readerTask = Task.Run(
                async () =>
                {
                    using var readStream = new CircularBufferReadStream(writeStream, streamId: $"reader-{readerId}");

                    var buffer = new byte[ChunkSize];
                    var endTime = DateTime.UtcNow.AddSeconds(1);

                    while (DateTime.UtcNow < endTime && !cts.Token.IsCancellationRequested)
                    {
                        var bytesRead = await readStream
                            .ReadAsync(buffer, 0, buffer.Length, cts.Token)
                            .ConfigureAwait(false);
                        if (bytesRead == 0)
                        {
                            break;
                        }
                    }
                },
                cts.Token
            );

            readerTasks.Add(readerTask);
        }

        // Wait for writer to complete
        await writerTask.ConfigureAwait(false);

        // Give readers a moment to catch up
        await Task.Delay(100).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);

        // Wait for all readers
        await Task.WhenAll(readerTasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Benchmark: High-frequency small writes (simulating packet-based streaming).
    /// </summary>
    /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous operation.</placeholder></returns>
    [Benchmark]
    public async Task ConcurrentReadWrite_SmallPackets()
    {
        using var writeStream = new CircularBufferWriteStream(BufferSize);
        using var cts = new CancellationTokenSource();

        const int packetSize = 188; // MPEG-TS packet size

        // Start writer task
        var writerTask = Task.Run(
            async () =>
            {
                var packet = new byte[packetSize];
                packet[0] = 0x47; // Sync byte
                Random.Shared.NextBytes(packet.AsSpan(1));

                var endTime = DateTime.UtcNow.AddSeconds(1);
                while (DateTime.UtcNow < endTime && !cts.Token.IsCancellationRequested)
                {
                    await writeStream.WriteAsync(packet, 0, packet.Length, cts.Token).ConfigureAwait(false);
                }
            },
            cts.Token
        );

        // Start reader tasks
        var readerTasks = new List<Task>();
        for (int i = 0; i < ReaderCount; i++)
        {
            var readerId = i;
            var readerTask = Task.Run(
                async () =>
                {
                    using var readStream = new CircularBufferReadStream(
                        writeStream,
                        streamId: $"packet-reader-{readerId}"
                    );

                    var buffer = new byte[packetSize * 7]; // Read multiple packets at once
                    var endTime = DateTime.UtcNow.AddSeconds(1);

                    while (DateTime.UtcNow < endTime && !cts.Token.IsCancellationRequested)
                    {
                        var bytesRead = await readStream
                            .ReadAsync(buffer, 0, buffer.Length, cts.Token)
                            .ConfigureAwait(false);
                        if (bytesRead == 0)
                        {
                            break;
                        }
                    }
                },
                cts.Token
            );

            readerTasks.Add(readerTask);
        }

        // Wait for completion
        await writerTask.ConfigureAwait(false);
        await Task.Delay(100).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(readerTasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Benchmark: Burst write pattern with slow readers.
    /// Tests buffer overflow handling.
    /// </summary>
    /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous operation.</placeholder></returns>
    [Benchmark]
    public async Task ConcurrentReadWrite_SlowReaders()
    {
        using var writeStream = new CircularBufferWriteStream(BufferSize);
        using var cts = new CancellationTokenSource();

        // Fast writer
        var writerTask = Task.Run(
            async () =>
            {
                var buffer = new byte[ChunkSize];
                Random.Shared.NextBytes(buffer);

                var endTime = DateTime.UtcNow.AddSeconds(1);
                while (DateTime.UtcNow < endTime && !cts.Token.IsCancellationRequested)
                {
                    await writeStream.WriteAsync(buffer, 0, buffer.Length, cts.Token).ConfigureAwait(false);
                    // No delay - write as fast as possible
                }
            },
            cts.Token
        );

        // Slow readers
        var readerTasks = new List<Task>();
        for (int i = 0; i < ReaderCount; i++)
        {
            var readerId = i;
            var readerTask = Task.Run(
                async () =>
                {
                    using var readStream = new CircularBufferReadStream(
                        writeStream,
                        streamId: $"slow-reader-{readerId}"
                    );

                    var buffer = new byte[ChunkSize];
                    var endTime = DateTime.UtcNow.AddSeconds(1);

                    while (DateTime.UtcNow < endTime && !cts.Token.IsCancellationRequested)
                    {
                        var bytesRead = await readStream
                            .ReadAsync(buffer, 0, buffer.Length, cts.Token)
                            .ConfigureAwait(false);
                        if (bytesRead == 0)
                        {
                            break;
                        }

                        // Simulate slow processing
                        await Task.Delay(10, cts.Token).ConfigureAwait(false);
                    }

                    // Capture overflow metrics
                    var overflows = readStream.OverflowCount;
                    var totalLost = readStream.TotalOverflowBytes;
                },
                cts.Token
            );

            readerTasks.Add(readerTask);
        }

        await writerTask.ConfigureAwait(false);
        await Task.Delay(100).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(readerTasks).ConfigureAwait(false);
    }
}
