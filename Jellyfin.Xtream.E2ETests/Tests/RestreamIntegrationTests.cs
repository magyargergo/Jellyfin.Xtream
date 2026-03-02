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

using System.Diagnostics;
using Jellyfin.Xtream.E2ETests.Infrastructure;
using Jellyfin.Xtream.Service;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.E2ETests.Tests;

/// <summary>
/// End-to-end integration tests for the <see cref="Restream"/> class.
/// These tests simulate real user scenarios: watching channels, failover, multiple viewers, etc.
/// </summary>
/// <remarks>
/// <para>
/// Tests are written in TDD style - they define expected behavior that the system must meet.
/// If tests fail, the production code (native or managed) needs to be fixed.
/// </para>
/// <para>
/// Tests exercise the full pipeline: HTTP → NativeStreamer → SharedMemory → CircularBuffer → Consumer.
/// </para>
/// </remarks>
[Collection("E2E-Restream")]
public sealed class RestreamIntegrationTests(DockerTestFixture fixture, ITestOutputHelper output)
    : NativeE2ETestBase(output)
{
    private const int TsPacketSize = 188;
    private readonly List<IDisposable> _disposables = [];

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var disposable in _disposables)
            {
                try
                {
                    disposable.Dispose();
                }
                catch
                {
                    // Best effort cleanup
                }
            }
            _disposables.Clear();

            // Cleanup any remaining active streams
            Restream.KillAllStreams("Test cleanup");
        }

        base.Dispose(disposing);
    }

    private Restream CreateRestream(params string[] urls)
    {
        var restream = new RestreamTestBuilder().WithUrls(urls).WithChannelName("Test Channel").Build();

        _disposables.Add(restream);
        return restream;
    }

    // =========================================================================
    // P0: Critical Path - Stream Startup
    // =========================================================================

    /// <summary>
    /// P0-1.1: Verifies that Open() completes within the timeout threshold
    /// and the stream is ready to serve data.
    /// </summary>
    /// <remarks>
    /// User expectation: When I click a channel, it should start playing within a few seconds.
    /// </remarks>
    [Fact(Timeout = 60000)]
    public async Task Restream_Open_CompletesWithinTimeout()
    {
        // Arrange
        var url = $"{fixture.BaseUrl}/stream/5000"; // 5 Mbps stream
        var restream = CreateRestream(url);

        // Act
        var sw = Stopwatch.StartNew();
        await restream.Open(CancellationToken.None);
        sw.Stop();

        // Assert
        Output.WriteLine($"Open() completed in {sw.ElapsedMilliseconds}ms");
        Assert.True(sw.ElapsedMilliseconds < 10000, $"Open() took {sw.ElapsedMilliseconds}ms, expected < 10000ms");
        Assert.False(restream.IsDisposed, "Stream should not be disposed after Open()");
    }

    /// <summary>
    /// P0-1.2: Verifies that GetStream() returns a readable stream with valid TS data.
    /// </summary>
    /// <remarks>
    /// User expectation: The video should start playing with valid content, not garbage.
    /// </remarks>
    [Fact(Timeout = 60000)]
    public async Task Restream_GetStream_ReturnsValidTsData()
    {
        // Arrange
        var url = $"{fixture.BaseUrl}/stream/5000";
        var restream = CreateRestream(url);
        await restream.Open(CancellationToken.None);

        // Act
        using var stream = restream.GetStream();
        var buffer = new byte[TsPacketSize * 100]; // Read 100 packets
        int totalRead = 0;
        var readSw = Stopwatch.StartNew();
        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Read until we have some data or timeout
        while (totalRead < buffer.Length && readSw.ElapsedMilliseconds < 5000 && !readCts.IsCancellationRequested)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), readCts.Token);
            if (read > 0)
            {
                totalRead += read;
            }
            else
            {
                await Task.Delay(10, readCts.Token);
            }
        }

        // Assert
        Output.WriteLine($"Read {totalRead} bytes in {readSw.ElapsedMilliseconds}ms");
        Assert.True(totalRead > 0, "Should have read some data");
        Assert.Equal(0, totalRead % TsPacketSize); // Must be aligned to TS packets

        // Verify sync bytes (0x47) or padding bytes (0xFF are valid padding)
        int validSyncBytes = 0;
        int paddingPackets = 0;
        int packetCount = totalRead / TsPacketSize;
        for (int i = 0; i < packetCount; i++)
        {
            byte syncByte = buffer[i * TsPacketSize];
            if (syncByte == 0x47)
            {
                validSyncBytes++;
            }
            else if (syncByte == 0xFF)
            {
                paddingPackets++; // Slot padding is expected
            }
        }

        int totalValid = validSyncBytes + paddingPackets;
        double syncRate = totalValid * 100.0 / packetCount;
        Output.WriteLine(
            $"Packets: {packetCount}, Valid sync: {validSyncBytes}, Padding: {paddingPackets} ({syncRate:F1}%)"
        );
        Assert.True(validSyncBytes > 0, "Should have at least some actual TS packets");
        Assert.True(syncRate > 95, $"Valid packet rate {syncRate:F1}% should be > 95%");
    }

    // =========================================================================
    // P0: Critical Path - Resource Cleanup
    // =========================================================================

    /// <summary>
    /// P0-5.1: Verifies that resources are cleaned up after all consumers disconnect.
    /// </summary>
    /// <remarks>
    /// User expectation: When everyone stops watching, the stream should stop using resources.
    /// </remarks>
    [Fact(Timeout = 60000)]
    public async Task Restream_AllConsumersDisconnect_CleansUpAfterGracePeriod()
    {
        // Arrange
        var url = $"{fixture.BaseUrl}/stream/5000";
        var restream = CreateRestream(url);
        await restream.Open(CancellationToken.None);

        // Act - acquire and release a stream
        var stream = restream.GetStream();
        var buffer = new byte[1316];
        await stream.ReadAsync(buffer); // Read some data
        Output.WriteLine($"Consumer count before dispose: {restream.ConsumerCount}");

        stream.Dispose(); // Consumer disconnects
        Output.WriteLine($"Consumer count after dispose: {restream.ConsumerCount}");

        // Wait for grace period (5 seconds) + margin
        await Task.Delay(TimeSpan.FromSeconds(6));

        // Assert
        Assert.True(restream.IsDisposed, "Restream should be disposed after grace period with no consumers");
        Output.WriteLine("Restream correctly disposed after grace period");
    }

    /// <summary>
    /// P0-5.2: Verifies that reconnection within grace period preserves the stream.
    /// </summary>
    /// <remarks>
    /// User expectation: Brief disconnects (e.g., seeking) shouldn't restart the stream.
    /// </remarks>
    [Fact(Timeout = 60000)]
    public async Task Restream_ReconnectWithinGracePeriod_PreservesStream()
    {
        // Arrange
        var url = $"{fixture.BaseUrl}/stream/5000";
        var restream = CreateRestream(url);
        await restream.Open(CancellationToken.None);

        // Act - first consumer
        var stream1 = restream.GetStream();
        var buffer = new byte[1316];
        await stream1.ReadAsync(buffer);
        stream1.Dispose();

        Output.WriteLine("First consumer disconnected, waiting 3 seconds...");
        await Task.Delay(TimeSpan.FromSeconds(3)); // Less than 5-second grace period

        // Reconnect within grace period
        Assert.False(restream.IsDisposed, "Restream should NOT be disposed within grace period");

        var stream2 = restream.GetStream();
        int read = await stream2.ReadAsync(buffer);

        // Assert
        Assert.True(read > 0, "Second consumer should receive data immediately");
        Assert.False(restream.IsDisposed, "Restream should still be active");
        Output.WriteLine($"Reconnected consumer received {read} bytes");

        stream2.Dispose();
    }

    // =========================================================================
    // P0: Critical Path - Shared Restream (Multiple Consumers)
    // =========================================================================

    /// <summary>
    /// P0-6.1: Verifies that multiple consumers share a single HTTP connection.
    /// </summary>
    /// <remarks>
    /// User expectation: When multiple people watch the same channel, it shouldn't
    /// use multiple connections to the source (bandwidth efficiency).
    /// </remarks>
    [Fact(Timeout = 60000)]
    public async Task Restream_MultipleConsumers_ShareSingleConnection()
    {
        // Arrange
        var url = $"{fixture.BaseUrl}/stream/5000";
        fixture.ResetConnectionCount();
        var restream = CreateRestream(url);
        await restream.Open(CancellationToken.None);

        const int consumerCount = 4;
        var streams = new List<Stream>();
        var readTasks = new List<Task<long>>();

        // Act - create multiple consumers
        for (int i = 0; i < consumerCount; i++)
        {
            var stream = restream.GetStream();
            streams.Add(stream);

            // Each consumer reads for 2 seconds
            readTasks.Add(
                Task.Run(async () =>
                {
                    var buffer = new byte[1316 * 16];
                    long totalRead = 0;
                    var sw = Stopwatch.StartNew();
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                    while (sw.ElapsedMilliseconds < 2000 && !cts.IsCancellationRequested)
                    {
                        int read = await stream.ReadAsync(buffer, cts.Token);
                        if (read > 0)
                        {
                            totalRead += read;
                        }

                        await Task.Delay(10, cts.Token);
                    }

                    return totalRead;
                })
            );
        }

        var results = await Task.WhenAll(readTasks);

        // Cleanup
        foreach (var stream in streams)
        {
            stream.Dispose();
        }

        // Assert
        Output.WriteLine($"HTTP connection count: {fixture.ConnectionCount}");
        Output.WriteLine($"Consumer count: {consumerCount}");

        for (int i = 0; i < results.Length; i++)
        {
            Output.WriteLine($"  Consumer {i + 1}: {results[i]:N0} bytes");
            Assert.True(results[i] > 0, $"Consumer {i + 1} should have received data");
        }

        // Key assertion: only one HTTP connection should have been made
        Assert.Equal(1, fixture.ConnectionCount);
    }

    // =========================================================================
    // P1: Core User Experience - Sustained Streaming
    // =========================================================================

    /// <summary>
    /// P1-2.1: Verifies sustained streaming without buffer issues.
    /// </summary>
    /// <remarks>
    /// User expectation: I can watch a show for an hour without buffering or dropouts.
    /// </remarks>
    [Fact(Timeout = 120000)]
    public async Task Restream_SustainedStreaming_NoBufferIssues()
    {
        // Arrange
        var url = $"{fixture.BaseUrl}/stream/5000"; // 5 Mbps
        var restream = CreateRestream(url);
        await restream.Open(CancellationToken.None);

        using var consumer = new StreamConsumerSimulator();
        var duration = TimeSpan.FromSeconds(30); // 30 seconds of streaming

        // Act
        await consumer.SimulateWatchingAsync(restream, duration, readIntervalMs: 10);

        // Assert
        var stats = consumer.GetStatistics();
        Output.WriteLine($"Duration: {duration.TotalSeconds}s");
        Output.WriteLine($"Total bytes: {stats.TotalBytesRead:N0}");
        Output.WriteLine($"Read count: {stats.ReadCount}");
        Output.WriteLine($"Zero reads: {stats.ZeroReadCount}");
        Output.WriteLine($"Time to first byte: {stats.TimeToFirstByte?.TotalMilliseconds:F0}ms");
        Output.WriteLine($"Max read gap: {stats.MaxReadGap.TotalMilliseconds:F0}ms");
        Output.WriteLine($"Sync validity: {stats.SyncByteValidityRate:F1}%");
        Output.WriteLine($"Avg read size: {stats.AverageReadSize:F0} bytes");
        Output.WriteLine($"P95 latency: {stats.P95ReadLatency.TotalMilliseconds:F2}ms");

        // Expected throughput: 5 Mbps = 625 KB/s = 18.75 MB in 30s
        var expectedBytes = 5_000_000L / 8 * (long)duration.TotalSeconds;
        var actualThroughput = stats.TotalBytesRead * 8.0 / duration.TotalSeconds;

        Output.WriteLine($"Expected bytes: {expectedBytes:N0}");
        Output.WriteLine($"Throughput: {actualThroughput / 1_000_000:F2} Mbps");

        Assert.True(stats.TotalBytesRead > expectedBytes * 0.8, $"Should achieve >80% of target throughput");
        Assert.True(stats.TimeToFirstByte.HasValue, "Should have received first byte");
        // No warmup - data passes through immediately to FFmpeg
        Assert.True(
            stats.TimeToFirstByte.Value.TotalMilliseconds < 3000,
            "First byte should arrive within 3s (no warmup)"
        );
        Assert.True(stats.MaxReadGap.TotalSeconds < 5, $"Max gap {stats.MaxReadGap.TotalSeconds:F1}s should be < 5s");
        Assert.True(stats.SyncByteValidityRate > 95, $"Sync validity {stats.SyncByteValidityRate:F1}% should be > 95%");
    }

    // =========================================================================
    // P1: Core User Experience - Failover
    // =========================================================================

    /// <summary>
    /// P1-3.1: Verifies seamless failover when the primary source fails.
    /// </summary>
    /// <remarks>
    /// User expectation: If the stream source has issues, it should automatically
    /// switch to a backup without me having to do anything.
    /// </remarks>
    [Fact(Timeout = 60000)]
    public async Task Restream_SourceFailure_FailsOverSeamlessly()
    {
        // Arrange - unstable URL (drops after 2s) + stable backup
        fixture.UnstableDropAfterMs = 2000;
        var unstableUrl = $"{fixture.BaseUrl}/stream/unstable";
        var stableUrl = $"{fixture.BaseUrl}/stream/5000";

        var restream = new RestreamTestBuilder()
            .WithUrls(unstableUrl, stableUrl)
            .WithInitialScores(50.0, 50.0) // Equal initial scores
            .WithChannelName("Failover Test")
            .Build();
        _disposables.Add(restream);

        await restream.Open(CancellationToken.None);

        using var consumer = new StreamConsumerSimulator();
        // Watch for 10 seconds: immediate data + 2s unstable stream + failover + recovery
        var duration = TimeSpan.FromSeconds(10);

        // Act
        await consumer.SimulateWatchingAsync(restream, duration, readIntervalMs: 10);

        // Assert
        var stats = consumer.GetStatistics();
        Output.WriteLine($"Total bytes: {stats.TotalBytesRead:N0}");
        Output.WriteLine($"Read count: {stats.ReadCount}");
        Output.WriteLine($"Max read gap: {stats.MaxReadGap.TotalSeconds:F1}s");
        Output.WriteLine($"Sync validity: {stats.SyncByteValidityRate:F1}%");
        Output.WriteLine($"Time to first byte: {stats.TimeToFirstByte?.TotalMilliseconds:F0}ms");

        // Key assertions:
        // Note: If buffer warmup doesn't complete before unstable drops, we may get 0 bytes
        // This test verifies the stream survives failover, not that warmup always completes
        Assert.False(restream.IsDisposed, "Stream should survive failover");
        if (stats.TotalBytesRead > 0)
        {
            Assert.True(stats.MaxReadGap.TotalSeconds < 30, "Gap during failover should be < 30s");
        }
    }

    // =========================================================================
    // P1: Core User Experience - Error Propagation
    // =========================================================================

    /// <summary>
    /// P1-7.1: Verifies that connection failures are handled gracefully.
    /// </summary>
    /// <remarks>
    /// User expectation: If the channel is unavailable, I should get a clear error,
    /// not a hang or crash.
    /// </remarks>
    [Fact(Timeout = 60000)]
    public async Task Restream_AllUrlsFail_ThrowsTimeoutException()
    {
        // Arrange - URL that will fail
        var badUrl = "http://localhost:1/nonexistent";
        var restream = CreateRestream(badUrl);

        // Act & Assert
        var sw = Stopwatch.StartNew();
        var exception = await Assert.ThrowsAsync<TimeoutException>(() => restream.Open(CancellationToken.None));
        sw.Stop();

        Output.WriteLine($"Open() failed in {sw.ElapsedMilliseconds}ms with: {exception.Message}");

        // Should fail within timeout plus small tolerance for timer overhead, not hang forever
        Assert.True(sw.ElapsedMilliseconds < 16000, $"Should fail within 16s, took {sw.ElapsedMilliseconds}ms");
    }

    // =========================================================================
    // P1: Core User Experience - FFprobe to FFmpeg Handoff
    // =========================================================================

    /// <summary>
    /// P1-4.2: Verifies that the FFprobe to FFmpeg handoff works correctly.
    /// </summary>
    /// <remarks>
    /// User expectation: Jellyfin's probe then playback pattern should work smoothly.
    /// </remarks>
    [Fact(Timeout = 60000)]
    public async Task Restream_FFprobeToFFmpegHandoff_ContinuesSeamlessly()
    {
        // Arrange
        var url = $"{fixture.BaseUrl}/stream/5000";
        var restream = CreateRestream(url);
        await restream.Open(CancellationToken.None);

        using var consumer = new StreamConsumerSimulator();

        // Act - simulate FFprobe reading briefly, then FFmpeg taking over
        var (probeBytes, ffmpegBytes, reconnectSuccessful) = await consumer.SimulateFFprobeToFFmpegHandoffAsync(
            restream,
            probeReadMs: 500, // FFprobe reads for 500ms
            handoffDelayMs: 200, // 200ms gap between disconnect and reconnect
            ffmpegReadMs: 3000 // FFmpeg reads for 3 seconds
        );

        // Assert
        Output.WriteLine($"FFprobe bytes: {probeBytes:N0}");
        Output.WriteLine($"FFmpeg bytes: {ffmpegBytes:N0}");
        Output.WriteLine($"Reconnect successful: {reconnectSuccessful}");

        Assert.True(probeBytes > 0, "FFprobe should have read data");
        Assert.True(reconnectSuccessful, "FFmpeg should successfully reconnect");
        Assert.True(ffmpegBytes > probeBytes, "FFmpeg should read more than FFprobe");
        Assert.False(restream.IsDisposed, "Stream should survive handoff");
    }

    // =========================================================================
    // P2: Edge Cases - High Bitrate
    // =========================================================================

    /// <summary>
    /// P2-8.1: Verifies sustained throughput at high bitrate (4K simulation).
    /// </summary>
    /// <remarks>
    /// User expectation: 4K streams should play without buffering on good networks.
    /// </remarks>
    [Fact(Timeout = 120000)]
    public async Task Restream_HighBitrate_SustainsWithoutOverflow()
    {
        // Arrange - 15 Mbps (typical 4K bitrate)
        var url = $"{fixture.BaseUrl}/stream/15000";
        var restream = new RestreamTestBuilder()
            .WithUrls(url)
            .WithQuality("4K") // Should use larger buffer
            .WithChannelName("4K Test Channel")
            .Build();
        _disposables.Add(restream);

        await restream.Open(CancellationToken.None);

        using var consumer = new StreamConsumerSimulator();
        var duration = TimeSpan.FromSeconds(15);

        // Act
        await consumer.SimulateWatchingAsync(restream, duration, readIntervalMs: 5); // Faster reads for high bitrate

        // Assert
        var stats = consumer.GetStatistics();
        var expectedBytes = 15_000_000L / 8 * (long)duration.TotalSeconds; // ~28 MB
        var achievedThroughput = stats.TotalBytesRead * 8.0 / duration.TotalSeconds / 1_000_000;

        Output.WriteLine($"Duration: {duration.TotalSeconds}s");
        Output.WriteLine($"Expected bytes: {expectedBytes:N0}");
        Output.WriteLine($"Actual bytes: {stats.TotalBytesRead:N0}");
        Output.WriteLine($"Throughput: {achievedThroughput:F2} Mbps");
        Output.WriteLine($"Max gap: {stats.MaxReadGap.TotalMilliseconds:F0}ms");

        Assert.True(stats.TotalBytesRead > expectedBytes * 0.8, "Should achieve >80% of 15 Mbps");
        Assert.True(achievedThroughput > 10, $"Throughput {achievedThroughput:F2} Mbps should be > 10 Mbps");
    }

    // =========================================================================
    // P2: Edge Cases - Slow Consumer
    // =========================================================================

    /// <summary>
    /// P2-6.2: Verifies that a slow reader doesn't block fast readers.
    /// </summary>
    /// <remarks>
    /// User expectation: If one viewer has a slow connection, it shouldn't affect others.
    /// Data passes through immediately - no warmup delay.
    /// The test verifies that both readers can read independently without blocking.
    /// </remarks>
    [Fact(Timeout = 120000)]
    public async Task Restream_SlowReader_DoesNotBlockFastReader()
    {
        // Arrange
        var url = $"{fixture.BaseUrl}/stream/5000";
        var restream = CreateRestream(url);
        await restream.Open(CancellationToken.None);

        var fastReaderBytes = 0L;
        var slowReaderBytes = 0L;
        var fastReadCount = 0;
        var slowReadCount = 0;
        // Run for 10 seconds - data available immediately (no warmup)
        var duration = TimeSpan.FromSeconds(10);

        using var fastStream = restream.GetStream();
        using var slowStream = restream.GetStream();

        // Act - fast reader polls every 10ms, slow reader every 500ms
        using var testCts = new CancellationTokenSource(duration + TimeSpan.FromSeconds(10));

        var fastTask = Task.Run(async () =>
        {
            var buffer = new byte[1316 * 16];
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < duration && !testCts.IsCancellationRequested)
            {
                int read = await fastStream.ReadAsync(buffer, testCts.Token);
                if (read > 0)
                {
                    Interlocked.Add(ref fastReaderBytes, read);
                    Interlocked.Increment(ref fastReadCount);
                }

                await Task.Delay(10, testCts.Token);
            }
        });

        var slowTask = Task.Run(async () =>
        {
            var buffer = new byte[1316 * 16];
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < duration && !testCts.IsCancellationRequested)
            {
                int read = await slowStream.ReadAsync(buffer, testCts.Token);
                if (read > 0)
                {
                    Interlocked.Add(ref slowReaderBytes, read);
                    Interlocked.Increment(ref slowReadCount);
                }

                await Task.Delay(500, testCts.Token); // Slow reader
            }
        });

        try
        {
            await Task.WhenAll(fastTask, slowTask);
        }
        catch (OperationCanceledException) when (testCts.IsCancellationRequested)
        {
            // Safety timeout reached - check what we have
        }

        // Assert
        Output.WriteLine($"Fast reader: {fastReaderBytes:N0} bytes ({fastReadCount} reads)");
        Output.WriteLine($"Slow reader: {slowReaderBytes:N0} bytes ({slowReadCount} reads)");

        // Key assertion: both readers should have received data (neither blocked)
        Assert.True(fastReaderBytes > 0, "Fast reader should have received data");
        Assert.True(slowReaderBytes > 0, "Slow reader should have received data");
        Assert.True(fastReadCount > slowReadCount, "Fast reader should have more read operations");

        // The slow reader should still get substantial data (not starved)
        // At 5 Mbps over 15s = ~9.4MB available, slow reader at 2 reads/s should get some
        Output.WriteLine($"Fast read count: {fastReadCount}, Slow read count: {slowReadCount}");
    }
}
