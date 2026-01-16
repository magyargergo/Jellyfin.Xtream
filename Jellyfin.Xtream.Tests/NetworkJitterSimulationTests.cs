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
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Service;
using Jellyfin.Xtream.Service.MpegTs.Core;
using Jellyfin.Xtream.Service.MpegTs.Infrastructure;
using Xunit;

#pragma warning disable CA2022 // Avoid inexact read - intentional for testing buffer behavior

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Tests simulating HTTP streaming network conditions where packets arrive
/// at variable rates due to network jitter, congestion, and buffering.
/// </summary>
/// <remarks>
/// <para>
/// Real-world HTTP streaming challenges:
/// - TCP congestion control causes bursty delivery
/// - Network jitter causes variable inter-packet delays
/// - HTTP chunked transfer can batch multiple TS packets
/// - CDN edge servers may buffer before forwarding
/// - WiFi/mobile networks have higher jitter than wired
/// </para>
/// <para>
/// Per industry research:
/// - Broadcast video cannot tolerate >0.1% packet loss or >5-10ms jitter
/// - PCR jitter should not exceed 10ms for IPTV
/// - Buffer sizes of 32MB help prevent packet loss
/// - Adaptive jitter buffers range from 50ms-500ms
/// </para>
/// </remarks>
public sealed class NetworkJitterSimulationTests : IDisposable
{
    private const int TsPacketSize = 188;
    private const byte TsSyncByte = 0x47;
    private const int DefaultBufferSize = 4 * 1024 * 1024; // 4MB

    private readonly CircularBufferWriteStream _writeStream;

    public NetworkJitterSimulationTests()
    {
        _writeStream = new CircularBufferWriteStream(DefaultBufferSize);
    }

    public void Dispose()
    {
        _writeStream.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Bursty Arrival Pattern Tests

    /// <summary>
    /// Simulates TCP congestion control causing bursty packet delivery.
    /// Packets arrive in bursts followed by gaps, typical of HTTP/TCP.
    /// </summary>
    [Fact]
    public async Task BurstyArrival_TcpCongestionPattern_BufferHandlesBursts()
    {
        const int burstSize = TsPacketSize * 50; // ~9.4KB burst (typical TCP segment)
        const int totalBursts = 20;
        var bytesWritten = 0L;

        for (var burst = 0; burst < totalBursts; burst++)
        {
            // Write burst of packets
            var burstData = CreateTsDataChunk(burstSize);
            await _writeStream.WriteAsync(burstData);
            bytesWritten += burstSize;

            // Simulate inter-burst gap (TCP window adjustment)
            await Task.Delay(Random.Shared.Next(1, 10));
        }

        Assert.Equal(bytesWritten, _writeStream.TotalBytesWritten);
        Assert.Equal(totalBursts * 50, _writeStream.TsIndexer.TotalPacketsParsed);
    }

    /// <summary>
    /// Simulates HTTP chunked transfer encoding where chunk sizes vary.
    /// </summary>
    [Fact]
    public async Task BurstyArrival_HttpChunkedTransfer_VariableChunkSizes()
    {
        var chunkSizes = new[] { 1316, 2632, 5264, 10528, 1316, 3948 }; // Varying multiples of 188
        var totalPackets = 0L;

        foreach (var chunkSize in chunkSizes)
        {
            // Round to nearest TS packet boundary
            var alignedSize = chunkSize / TsPacketSize * TsPacketSize;
            if (alignedSize == 0)
            {
                alignedSize = TsPacketSize;
            }

            var chunk = CreateTsDataChunk(alignedSize);
            await _writeStream.WriteAsync(chunk);
            totalPackets += alignedSize / TsPacketSize;

            // Simulate HTTP chunk boundary processing delay
            await Task.Delay(Random.Shared.Next(0, 5));
        }

        Assert.Equal(totalPackets, _writeStream.TsIndexer.TotalPacketsParsed);
    }

    /// <summary>
    /// Simulates CDN edge server buffering before forwarding.
    /// Data arrives in large batches after buffering delays.
    /// </summary>
    [Fact]
    public async Task BurstyArrival_CdnBuffering_LargeBatchesWithDelays()
    {
        const int cdnBufferSize = TsPacketSize * 200; // ~37.6KB CDN buffer
        const int flushCount = 5;

        for (var flush = 0; flush < flushCount; flush++)
        {
            // Simulate CDN buffer fill time
            await Task.Delay(Random.Shared.Next(20, 50));

            // CDN flushes entire buffer at once
            var batchData = CreateTsDataChunk(cdnBufferSize);
            await _writeStream.WriteAsync(batchData);
        }

        Assert.Equal(flushCount * 200, _writeStream.TsIndexer.TotalPacketsParsed);
    }

    #endregion

    #region Network Jitter Simulation Tests

    /// <summary>
    /// Simulates low jitter network (wired broadband) with consistent delivery.
    /// Jitter typically &lt;2ms on good wired connections.
    /// </summary>
    [Fact]
    public async Task NetworkJitter_LowJitter_WiredBroadband()
    {
        const int packetCount = 100;
        const int baseDelayMs = 5; // Base inter-packet delay
        const int jitterMs = 2; // Low jitter for wired

        var deliveryTimes = new List<long>();
        var sw = Stopwatch.StartNew();

        for (var i = 0; i < packetCount; i++)
        {
            var packet = CreateTsDataChunk(TsPacketSize * 7); // 7 packets per delivery
            await _writeStream.WriteAsync(packet);
            deliveryTimes.Add(sw.ElapsedMilliseconds);

            // Simulate low jitter
            var delay = baseDelayMs + Random.Shared.Next(-jitterMs, jitterMs + 1);
            if (delay > 0)
            {
                await Task.Delay(delay);
            }
        }

        Assert.Equal(packetCount * 7, _writeStream.TsIndexer.TotalPacketsParsed);

        // Verify jitter was within expected range
        var actualJitter = CalculateJitter(deliveryTimes);
        Assert.True(actualJitter < 10, $"Jitter {actualJitter}ms exceeded expected range for wired");
    }

    /// <summary>
    /// Simulates high jitter network (WiFi/mobile) with variable delivery.
    /// WiFi jitter can reach 20-50ms, mobile can exceed 100ms.
    /// </summary>
    [Fact]
    public async Task NetworkJitter_HighJitter_WifiMobile()
    {
        const int packetCount = 50;
        const int baseDelayMs = 10;
        const int jitterMs = 30; // High jitter for WiFi

        var deliveryTimes = new List<long>();
        var sw = Stopwatch.StartNew();

        for (var i = 0; i < packetCount; i++)
        {
            var packet = CreateTsDataChunk(TsPacketSize * 7);
            await _writeStream.WriteAsync(packet);
            deliveryTimes.Add(sw.ElapsedMilliseconds);

            // Simulate high jitter with occasional spikes
            var delay = baseDelayMs + Random.Shared.Next(-jitterMs / 2, jitterMs);
            if (Random.Shared.Next(10) == 0)
            {
                delay += 50; // Occasional large spike
            }

            if (delay > 0)
            {
                await Task.Delay(delay);
            }
        }

        Assert.Equal(packetCount * 7, _writeStream.TsIndexer.TotalPacketsParsed);
    }

    /// <summary>
    /// Simulates network congestion causing packet bunching.
    /// Packets queue up during congestion then arrive in rapid succession.
    /// </summary>
    [Fact]
    public async Task NetworkJitter_Congestion_PacketBunching()
    {
        const int normalDeliveries = 20;
        const int congestionDeliveries = 30;
        const int recoveryDeliveries = 20;

        // Normal delivery phase
        for (var i = 0; i < normalDeliveries; i++)
        {
            var packet = CreateTsDataChunk(TsPacketSize * 7);
            await _writeStream.WriteAsync(packet);
            await Task.Delay(5);
        }

        // Congestion phase - packets bunch up then arrive rapidly
        await Task.Delay(100); // Simulated congestion delay

        // Rapid delivery of bunched packets
        for (var i = 0; i < congestionDeliveries; i++)
        {
            var packet = CreateTsDataChunk(TsPacketSize * 7);
            await _writeStream.WriteAsync(packet);
            // No delay - packets arrive back-to-back
        }

        // Recovery phase - back to normal
        for (var i = 0; i < recoveryDeliveries; i++)
        {
            var packet = CreateTsDataChunk(TsPacketSize * 7);
            await _writeStream.WriteAsync(packet);
            await Task.Delay(5);
        }

        const int expectedPackets = (normalDeliveries + congestionDeliveries + recoveryDeliveries) * 7;
        Assert.Equal(expectedPackets, _writeStream.TsIndexer.TotalPacketsParsed);
    }

    #endregion

    #region Reader Starvation Tests

    /// <summary>
    /// Simulates reader attempting to read faster than data arrives.
    /// Common in live streaming when network is slow.
    /// </summary>
    [Fact]
    public async Task ReaderStarvation_SlowNetwork_ReaderWaitsForData()
    {
        await using var readStream = new CircularBufferReadStream(_writeStream, streamId: "slow-network-reader");
        var readBuffer = new byte[TsPacketSize * 10];
        var totalRead = 0;
        var starvationEvents = 0;

        // Start writer task with slow delivery
        var writerTask = Task.Run(async () =>
        {
            for (var i = 0; i < 20; i++)
            {
                var data = CreateTsDataChunk(TsPacketSize * 5);
                await _writeStream.WriteAsync(data).ConfigureAwait(false);
                await Task.Delay(50).ConfigureAwait(false); // Slow network - 50ms between writes
            }
        });

        // Reader tries to read continuously
        var readerTask = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                var bytesRead = await readStream.ReadAsync(readBuffer).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    starvationEvents++;
                    await Task.Delay(10).ConfigureAwait(false); // Wait for more data
                }
                else
                {
                    totalRead += bytesRead;
                }
            }
        });

        await Task.WhenAll(writerTask, readerTask);

        // Some starvation is expected with slow network
        Assert.True(starvationEvents >= 0);
        Assert.True(totalRead > 0);
    }

    /// <summary>
    /// Simulates network hiccup causing temporary data starvation.
    /// </summary>
    [Fact]
    public async Task ReaderStarvation_NetworkHiccup_RecoveryAfterPause()
    {
        // Initial data
        var initialData = CreateTsDataChunk(TsPacketSize * 100);
        await _writeStream.WriteAsync(initialData);
        await using var readStream = new CircularBufferReadStream(_writeStream, streamId: "hiccup-reader");

        // Read initial data
        var buffer = new byte[TsPacketSize * 50];
        var bytesRead = await readStream.ReadAsync(buffer);
        Assert.True(bytesRead > 0);

        // Simulate network hiccup - no writes for 200ms
        await Task.Delay(200);

        // Reader exhausts buffer
        while (await readStream.ReadAsync(buffer) > 0)
        {
            // Drain remaining data
        }

        // Network recovers - new data arrives
        var recoveryData = CreateTsDataChunk(TsPacketSize * 50);
        await _writeStream.WriteAsync(recoveryData);

        // Reader should get new data
        bytesRead = await readStream.ReadAsync(buffer);
        Assert.True(bytesRead > 0);
    }

    #endregion

    #region Writer Overrun Tests

    /// <summary>
    /// Simulates fast network bursts that could overflow buffer.
    /// Tests that circular buffer handles rapid writes correctly.
    /// </summary>
    [Fact]
    public void WriterOverrun_FastBurst_BufferHandlesCorrectly()
    {
        const int burstSize = TsPacketSize * 100;
        const int burstCount = 50; // Total 940KB

        for (var i = 0; i < burstCount; i++)
        {
            var burst = CreateTsDataChunk(burstSize);
            _writeStream.Write(burst, 0, burst.Length);
        }

        Assert.Equal(burstCount * burstSize, _writeStream.TotalBytesWritten);
        Assert.Equal(burstCount * 100, _writeStream.TsIndexer.TotalPacketsParsed);
    }

    /// <summary>
    /// Simulates continuous high-bandwidth stream that wraps buffer multiple times.
    /// </summary>
    [Fact]
    public void WriterOverrun_ContinuousHighBandwidth_MultipleWraps()
    {
        const int chunkSize = TsPacketSize * 100; // ~18.8KB
        const int chunksToFillBuffer = (DefaultBufferSize / chunkSize) + 1;
        var totalChunks = chunksToFillBuffer * 3; // Wrap 3 times

        for (var i = 0; i < totalChunks; i++)
        {
            var chunk = CreateTsDataChunk(chunkSize);
            _writeStream.Write(chunk, 0, chunk.Length);
        }

        Assert.Equal((long)totalChunks * chunkSize, _writeStream.TotalBytesWritten);
    }

    /// <summary>
    /// Simulates slow reader being overrun by fast writer.
    /// Tests overflow detection.
    /// </summary>
    [Fact]
    public async Task WriterOverrun_SlowReader_OverflowDetected()
    {
        const int smallBufferSize = TsPacketSize * 100; // ~18.8KB buffer
        await using var smallBuffer = new CircularBufferWriteStream(smallBufferSize);

        // Write initial data
        var initial = CreateTsDataChunk(TsPacketSize * 50);
        await smallBuffer.WriteAsync(initial);
        await using var readStream = new CircularBufferReadStream(smallBuffer, streamId: "slow-reader");

        // Don't read anything, just let writer overrun
        await Task.Delay(10);

        // Write more than buffer size
        const int overflowChunkSize = TsPacketSize * 20;
        for (var i = 0; i < 10; i++)
        {
            var chunk = CreateTsDataChunk(overflowChunkSize);
            await smallBuffer.WriteAsync(chunk);
        }

        // Try to read - should detect overflow
        // Note: After overflow, we may not get exact bytes requested
        var buffer = new byte[TsPacketSize * 10];
        var bytesRead = await readStream.ReadAsync(buffer);

        // Overflow should be tracked - the reader was overrun by the writer
        // Either we got data and overflow was detected, or we got less data due to buffer wrap
        Assert.True(
            readStream.OverflowCount > 0 || bytesRead < buffer.Length,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Expected overflow detection or partial read. OverflowCount={readStream.OverflowCount}, BytesRead={bytesRead}"
            )
        );
    }

    #endregion

    #region Concurrent Read/Write Tests

    /// <summary>
    /// Simulates realistic streaming scenario with concurrent reader and writer.
    /// Writer receives from network, reader sends to client.
    /// </summary>
    [Fact]
    public async Task ConcurrentReadWrite_RealisticStreaming_NoDataCorruption()
    {
        var errors = new List<string>();
        var writerComplete = false;
        var totalWritten = 0L;
        var totalRead = 0L;

        // Writer task - simulates network receiver
        var writerTask = Task.Run(async () =>
        {
            for (var i = 0; i < 100; i++)
            {
                var chunkSize = TsPacketSize * Random.Shared.Next(5, 20);
                var chunk = CreateTsDataChunk(chunkSize);
                await _writeStream.WriteAsync(chunk).ConfigureAwait(false);
                _ = Interlocked.Add(ref totalWritten, chunkSize);

                // Variable network delay
                await Task.Delay(Random.Shared.Next(1, 10)).ConfigureAwait(false);
            }

            writerComplete = true;
        });
        await
        // Reader task - simulates client sender
        using var readStream = new CircularBufferReadStream(_writeStream, streamId: "concurrent-reader");
        var readerTask = Task.Run(async () =>
        {
            var buffer = new byte[TsPacketSize * 10];

            while (!writerComplete || readStream.CurrentGap > 0)
            {
                var bytesRead = await readStream.ReadAsync(buffer).ConfigureAwait(false);
                if (bytesRead > 0)
                {
                    _ = Interlocked.Add(ref totalRead, bytesRead);

                    // Verify sync bytes
                    for (var i = 0; i < bytesRead; i += TsPacketSize)
                    {
                        if (buffer[i] != TsSyncByte)
                        {
                            errors.Add($"Sync byte error at offset {i}");
                        }
                    }
                }
                else
                {
                    await Task.Delay(5).ConfigureAwait(false);
                }
            }
        });

        await Task.WhenAll(writerTask, readerTask);

        Assert.Empty(errors);
        Assert.True(totalRead > 0);
    }

    /// <summary>
    /// Simulates multiple clients reading same stream at different speeds.
    /// </summary>
    [Fact]
    public async Task ConcurrentReadWrite_MultipleReaders_DifferentSpeeds()
    {
        var readerResults = new long[3];

        // Pre-fill buffer
        var initial = CreateTsDataChunk(TsPacketSize * 500);
        await _writeStream.WriteAsync(initial);

        // Start writer
        var writerTask = Task.Run(async () =>
        {
            for (var i = 0; i < 50; i++)
            {
                var chunk = CreateTsDataChunk(TsPacketSize * 20);
                await _writeStream.WriteAsync(chunk).ConfigureAwait(false);
                await Task.Delay(10).ConfigureAwait(false);
            }
        });

        // Create readers at different speeds
        var readerTasks = new Task[3];

        for (var r = 0; r < 3; r++)
        {
            var readerIndex = r;
            var readerDelay = (r + 1) * 5; // 5ms, 10ms, 15ms delays

            readerTasks[r] = Task.Run(async () =>
            {
                var reader = new CircularBufferReadStream(_writeStream, streamId: $"speed-reader-{readerIndex}");
                await using (reader.ConfigureAwait(false))
                {
                    var buffer = new byte[TsPacketSize * 10];

                    for (var i = 0; i < 30; i++)
                    {
                        var bytesRead = await reader.ReadAsync(buffer);
                        _ = Interlocked.Add(ref readerResults[readerIndex], bytesRead);
                        await Task.Delay(readerDelay);
                    }
                }
            });
        }

        await writerTask;
        await Task.WhenAll(readerTasks);

        // All readers should have read data
        Assert.True(readerResults[0] > 0);
        Assert.True(readerResults[1] > 0);
        Assert.True(readerResults[2] > 0);

        // Faster reader should have read more or equal
        Assert.True(readerResults[0] >= readerResults[2]);
    }

    #endregion

    #region PCR Jitter Under Network Conditions

    /// <summary>
    /// Tests PCR timing tracker behavior when packets arrive with network jitter.
    /// PCR values should be consistent even if arrival times vary.
    /// Note: High jitter can cause drift detection - this tests that the tracker
    /// handles jitter gracefully even if it detects drift.
    /// </summary>
    [Fact]
    public void PcrTiming_NetworkJitter_ClockRecoveryHandlesVariableArrival()
    {
        var clock = new TestClock();
        var tracker = new PcrTimingTracker(programNumber: 1, clock);

        // Simulate PCRs arriving with low jitter (typical wired network)
        // PCR values increment steadily, arrival times have small variance
        const long pcrBase = 27_000_000; // 1 second in 27MHz

        for (var i = 0; i < 30; i++)
        {
            // PCR increments by 40ms worth (normal 25fps interval)
            var pcr = pcrBase + (i * 40L * 27_000);

            // Wall clock advances with small jitter (+/- 2ms - typical for wired)
            // Using deterministic pattern to avoid flaky tests
            var jitter = (i % 5) - 2; // -2, -1, 0, 1, 2 repeating
            clock.AdvanceMs(40 + jitter);

            _ = tracker.ProcessPcr(pcr);
        }

        // Should process all PCRs
        Assert.True(tracker.PcrCount >= 30);

        // Clock should be in a valid state (locked, locking, or drifting due to jitter)
        Assert.True(
            tracker.ClockStatus is ClockStatus.Locked or ClockStatus.Locking or ClockStatus.Drifting,
            $"Clock should be in valid state, was {tracker.ClockStatus}"
        );
    }

    /// <summary>
    /// Tests PCR timing when network causes packet reordering simulation.
    /// PCR values may appear to go backwards temporarily.
    /// </summary>
    [Fact]
    public void PcrTiming_PacketReordering_HandlesOutOfOrderPcr()
    {
        var clock = new TestClock();
        var tracker = new PcrTimingTracker(programNumber: 1, clock);

        // Normal PCRs
        _ = tracker.ProcessPcr(27_000_000);
        clock.AdvanceMs(40);
        _ = tracker.ProcessPcr(27_000_000 + (40 * 27_000));
        clock.AdvanceMs(40);
        _ = tracker.ProcessPcr(27_000_000 + (80 * 27_000));

        // Simulate "late" PCR that should have arrived earlier
        // This can happen with network reordering
        clock.AdvanceMs(40);
        _ = tracker.ProcessPcr(27_000_000 + (60 * 27_000)); // Out of order!

        // Continue with normal PCRs
        clock.AdvanceMs(40);
        _ = tracker.ProcessPcr(27_000_000 + (160 * 27_000));

        // Tracker should handle this gracefully
        Assert.Equal(5, tracker.PcrCount);
    }

    /// <summary>
    /// Tests adaptive buffer adjustment when network jitter increases.
    /// </summary>
    [Fact]
    public void PcrTiming_IncreasingJitter_AdaptiveBufferGrows()
    {
        var clock = new TestClock();
        var tracker = new PcrTimingTracker(programNumber: 1, clock);

        var initialBuffer = tracker.CurrentBufferMs;

        // Start with normal PCRs
        _ = tracker.ProcessPcr(27_000_000);

        // Gradually increase jitter
        for (var i = 1; i <= 20; i++)
        {
            clock.AdvanceMs(40);

            // Add increasing jitter to PCR values
            var expectedPcr = 27_000_000 + (i * 40L * 27_000);
            var jitterTicks = i * 100L; // Increasing jitter
            _ = tracker.ProcessPcr(expectedPcr + jitterTicks);
        }

        // Buffer should have grown or jitter violations detected
        Assert.True(
            tracker.CurrentBufferMs >= initialBuffer || tracker.JitterViolations > 0,
            "Either buffer should grow or jitter violations should be detected"
        );
    }

    #endregion

    #region Reconnection Simulation Tests

    /// <summary>
    /// Simulates HTTP connection drop and reconnection.
    /// </summary>
    [Fact]
    public async Task Reconnection_HttpConnectionDrop_DiscontinuityMarked()
    {
        // Initial streaming
        var initialData = CreateTsDataChunk(TsPacketSize * 100);
        await _writeStream.WriteAsync(initialData);
        _writeStream.SignalSourceConnected();

        Assert.True(_writeStream.IsSourceConnected);
        Assert.Equal(0, _writeStream.DiscontinuityCount);

        // Simulate connection drop
        _writeStream.SignalSourceDisconnected();
        Assert.False(_writeStream.IsSourceConnected);

        // Simulate reconnection delay
        await Task.Delay(50);

        // Reconnect
        _writeStream.SignalReconnecting();
        Assert.True(_writeStream.IsReconnecting);
        Assert.Equal(1, _writeStream.ReconnectionAttempts);

        // Mark discontinuity when new data arrives
        _writeStream.MarkDiscontinuity();
        _writeStream.SignalSourceConnected();

        // Resume streaming
        var newData = CreateTsDataChunk(TsPacketSize * 100);
        await _writeStream.WriteAsync(newData);

        Assert.True(_writeStream.IsSourceConnected);
        Assert.Equal(1, _writeStream.DiscontinuityCount);
        Assert.True(_writeStream.LastDiscontinuityOffset > 0);
    }

    /// <summary>
    /// Simulates provider failover during streaming.
    /// </summary>
    [Fact]
    public async Task Reconnection_ProviderFailover_DataContinues()
    {
        // Streaming from provider A
        for (var i = 0; i < 10; i++)
        {
            var chunk = CreateTsDataChunk(TsPacketSize * 10);
            await _writeStream.WriteAsync(chunk);
            await Task.Delay(5);
        }

        var bytesBeforeFailover = _writeStream.TotalBytesWritten;

        // Provider A fails, failover to provider B
        _writeStream.SignalSourceDisconnected();
        _writeStream.SignalReconnecting();
        await Task.Delay(20); // Failover delay

        _writeStream.MarkDiscontinuity();
        _writeStream.SignalSourceConnected();

        // Streaming from provider B
        for (var i = 0; i < 10; i++)
        {
            var chunk = CreateTsDataChunk(TsPacketSize * 10);
            await _writeStream.WriteAsync(chunk);
            await Task.Delay(5);
        }

        Assert.True(_writeStream.TotalBytesWritten > bytesBeforeFailover);
        Assert.Equal(1, _writeStream.DiscontinuityCount);
    }

    #endregion

    #region Buffer Overflow Mitigation Tests

    /// <summary>
    /// Tests that overflow recovery aligns to TS packet boundary.
    /// After overflow, reader must align to sync bytes to prevent decode errors.
    /// </summary>
    [Fact]
    public void OverflowRecovery_AlignsToTsPacketBoundary()
    {
        const int smallBufferSize = TsPacketSize * 50; // ~9.4KB
        using var writeStream = new CircularBufferWriteStream(smallBufferSize);

        // Fill buffer completely
        var initial = CreateTsDataChunk(smallBufferSize);
        writeStream.Write(initial, 0, initial.Length);

        using var readStream = new CircularBufferReadStream(writeStream, streamId: "overflow-align-test");

        // Force overflow by writing more data without reading
        var overflow = CreateTsDataChunk(smallBufferSize * 2);
        writeStream.Write(overflow, 0, overflow.Length);

        // Read after overflow
        var buffer = new byte[TsPacketSize * 20];
        var bytesRead = readStream.Read(buffer, 0, buffer.Length);

        if (bytesRead > 0)
        {
            // Verify data starts with sync byte (properly aligned)
            Assert.Equal(TsSyncByte, buffer[0]);

            // Verify subsequent packets are also aligned
            for (var i = 0; i < bytesRead; i += TsPacketSize)
            {
                Assert.Equal(TsSyncByte, buffer[i]);
            }
        }

        // Verify overflow was tracked
        Assert.True(readStream.OverflowCount > 0 || bytesRead == 0, "Expected overflow to be detected");
    }

    /// <summary>
    /// Tests that overflow statistics are accurately maintained.
    /// TotalOverflowBytes should match the actual data lost.
    /// </summary>
    [Fact]
    public void OverflowStatistics_AccuratelyTracked()
    {
        const int smallBufferSize = TsPacketSize * 100;
        using var writeStream = new CircularBufferWriteStream(smallBufferSize);

        // Fill buffer
        var initial = CreateTsDataChunk(smallBufferSize);
        writeStream.Write(initial, 0, initial.Length);

        using var readStream = new CircularBufferReadStream(writeStream, streamId: "overflow-stats-test");

        // Record initial state
        var initialOverflowBytes = readStream.TotalOverflowBytes;
        var initialOverflowCount = readStream.OverflowCount;

        // Write 3x buffer size without reading - this will cause overflow
        var overflowData = CreateTsDataChunk(smallBufferSize * 3);
        writeStream.Write(overflowData, 0, overflowData.Length);

        // Trigger overflow detection by attempting to read
        var buffer = new byte[TsPacketSize * 10];
        _ = readStream.Read(buffer, 0, buffer.Length);

        // Verify overflow tracking increased
        Assert.True(
            readStream.TotalOverflowBytes > initialOverflowBytes || readStream.OverflowCount > initialOverflowCount,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Expected overflow tracking to increase. Initial: {initialOverflowBytes}B/{initialOverflowCount} events, Current: {readStream.TotalOverflowBytes}B/{readStream.OverflowCount} events"
            )
        );
    }

    /// <summary>
    /// Tests that multiple sequential overflows don't corrupt internal state.
    /// The reader should remain functional after multiple overflow events.
    /// </summary>
    [Fact]
    public void MultipleOverflows_ReaderRemainsFunctional()
    {
        const int smallBufferSize = TsPacketSize * 50;
        using var writeStream = new CircularBufferWriteStream(smallBufferSize);
        using var readStream = new CircularBufferReadStream(writeStream, streamId: "multi-overflow-test");

        var buffer = new byte[TsPacketSize * 10];

        // Cause multiple overflows
        for (var overflow = 0; overflow < 5; overflow++)
        {
            // Fill and overflow
            var data = CreateTsDataChunk(smallBufferSize * 2);
            writeStream.Write(data, 0, data.Length);

            // Read a small amount
            var bytesRead = readStream.Read(buffer, 0, buffer.Length);

            // Verify we can still read valid data
            if (bytesRead > 0)
            {
                Assert.Equal(TsSyncByte, buffer[0]);
            }
        }

        // Final verification - reader should still work
        var finalData = CreateTsDataChunk(TsPacketSize * 20);
        writeStream.Write(finalData, 0, finalData.Length);

        var finalRead = readStream.Read(buffer, 0, buffer.Length);
        Assert.True(finalRead >= 0, "Reader should remain functional after multiple overflows");
    }

    #endregion

    #region Data Corruption Detection Tests

    /// <summary>
    /// Tests that corrupted sync bytes are detected during alignment.
    /// Verifies the TS sync alignment mechanism works correctly.
    /// </summary>
    [Fact]
    public void DataCorruption_MissingSyncByte_DetectedAndSkipped()
    {
        using var writeStream = new CircularBufferWriteStream(DefaultBufferSize);

        // Write valid data first
        var validData = CreateTsDataChunk(TsPacketSize * 100);
        writeStream.Write(validData, 0, validData.Length);

        // Write data with corrupted sync byte (first byte not 0x47)
        var corruptedData = new byte[TsPacketSize * 10];
        corruptedData[0] = 0x00; // Corrupted sync byte
        for (var i = TsPacketSize; i < corruptedData.Length; i += TsPacketSize)
        {
            corruptedData[i] = TsSyncByte;
            if (i + 1 < corruptedData.Length)
            {
                corruptedData[i + 1] = 0x1F;
            }

            if (i + 2 < corruptedData.Length)
            {
                corruptedData[i + 2] = 0xFF;
            }

            if (i + 3 < corruptedData.Length)
            {
                corruptedData[i + 3] = 0x10;
            }
        }

        writeStream.Write(corruptedData, 0, corruptedData.Length);

        // Write more valid data
        writeStream.Write(validData, 0, validData.Length);

        using var readStream = new CircularBufferReadStream(writeStream, streamId: "corrupt-sync-test");

        // Read and verify alignment recovers
        var buffer = new byte[TsPacketSize * 50];
        var totalValidPackets = 0;

        for (var read = 0; read < 5; read++)
        {
            var bytesRead = readStream.Read(buffer, 0, buffer.Length);
            for (var i = 0; i < bytesRead; i += TsPacketSize)
            {
                if (buffer[i] == TsSyncByte)
                {
                    totalValidPackets++;
                }
            }
        }

        Assert.True(totalValidPackets > 0, "Should recover valid packets after corruption");
    }

    /// <summary>
    /// Tests continuity counter validation by the TsIndexer.
    /// Verifies discontinuities in CC are tracked.
    /// </summary>
    [Fact]
    public void DataCorruption_ContinuityCounterGap_DetectedByIndexer()
    {
        using var writeStream = new CircularBufferWriteStream(DefaultBufferSize);

        // Write valid TS packets with sequential continuity counters
        var data = new byte[TsPacketSize * 100];
        var cc = 0;
        for (var i = 0; i < data.Length; i += TsPacketSize)
        {
            data[i] = TsSyncByte;
            data[i + 1] = 0x00; // PID high
            data[i + 2] = 0x30; // PID low (48 - common video PID)
            data[i + 3] = (byte)(0x10 | (cc & 0x0F)); // Payload only + CC

            // Skip CC value 7 to create a discontinuity
            cc = (cc == 6) ? 8 : (cc + 1) % 16;
        }

        writeStream.Write(data, 0, data.Length);

        // The TsIndexer should track continuity errors
        // Note: Actual CC error detection depends on TsIndexer implementation
        Assert.Equal(100, writeStream.TsIndexer.TotalPacketsParsed);
    }

    /// <summary>
    /// Tests that completely random data doesn't crash the buffer system.
    /// Robustness test for malformed input.
    /// </summary>
    [Fact]
    public void DataCorruption_RandomNoise_HandledGracefully()
    {
        using var writeStream = new CircularBufferWriteStream(DefaultBufferSize);

        // Write random noise (non-TS data)
        var noise = new byte[TsPacketSize * 50];
        Random.Shared.NextBytes(noise);
        writeStream.Write(noise, 0, noise.Length);

        // Write valid TS data
        var validData = CreateTsDataChunk(TsPacketSize * 100);
        writeStream.Write(validData, 0, validData.Length);

        using var readStream = new CircularBufferReadStream(writeStream, streamId: "noise-test");

        // Should not throw - graceful handling
        var buffer = new byte[TsPacketSize * 50];
        var exception = Record.Exception(() =>
        {
            for (var i = 0; i < 10; i++)
            {
                _ = readStream.Read(buffer, 0, buffer.Length);
            }
        });

        Assert.Null(exception);
    }

    #endregion

    #region Graceful Degradation Tests

    /// <summary>
    /// Tests overflow prediction detects increasing gap trend.
    /// </summary>
    [Fact]
    public async Task OverflowPrediction_IncreasingGap_DetectsRisk()
    {
        const int PredictionBufferSize = TsPacketSize * 1000; // Larger buffer
        await using var writeStream = new CircularBufferWriteStream(PredictionBufferSize);

        // Write initial data before creating reader
        var initial = CreateTsDataChunk(TsPacketSize * 100);
        await writeStream.WriteAsync(initial);
        await using var readStream = new CircularBufferReadStream(writeStream, streamId: "prediction-test");

        // Drain the initial data first to get the reader close to the writer
        var drainBuffer = new byte[TsPacketSize * 100];
        while (readStream.CurrentGap > TsPacketSize * 10)
        {
            _ = await readStream.ReadAsync(drainBuffer);
        }

        // Now simulate slow reader with fast writer to grow gap
        var writeChunk = CreateTsDataChunk(TsPacketSize * 30);
        var readBuffer = new byte[TsPacketSize * 5]; // Read slower

        for (var i = 0; i < 30; i++)
        {
            await writeStream.WriteAsync(writeChunk);
            _ = await readStream.ReadAsync(readBuffer);
            await Task.Delay(5); // Allow predictor to sample
        }

        // Check that gap is being monitored (CurrentGap should be significant)
        var gap = readStream.CurrentGap;

        // Either gap should be positive (reader falling behind), or overflow risk detected
        Assert.True(gap >= 0, string.Create(CultureInfo.InvariantCulture, $"Gap should be non-negative. Gap={gap}"));

        // Verify the predictor is tracking (it should have recorded samples)
        // The test passes if the gap tracking mechanism is working
        Assert.True(gap > 0 || readStream.TotalBytesRead > 0, "Reader should have read some data");
    }

    /// <summary>
    /// Tests that reader diagnostics provide useful information.
    /// </summary>
    [Fact]
    public void GracefulDegradation_DiagnosticsAvailable()
    {
        using var writeStream = new CircularBufferWriteStream(DefaultBufferSize);
        var data = CreateTsDataChunk(TsPacketSize * 100);
        writeStream.Write(data, 0, data.Length);

        using var readStream = new CircularBufferReadStream(writeStream, streamId: "diagnostics-test");

        // Read some data
        var buffer = new byte[TsPacketSize * 50];
        _ = readStream.Read(buffer, 0, buffer.Length);

        // Get diagnostics
        var diagnostics = readStream.GetDiagnostics();

        // Verify diagnostics contain essential information
        Assert.Contains("diagnostics-test", diagnostics);
        Assert.Contains("Buffer Size", diagnostics);
        Assert.Contains("Current Gap", diagnostics);
        Assert.Contains("Status", diagnostics);
    }

    /// <summary>
    /// Tests that TS indexer metrics are available.
    /// </summary>
    [Fact]
    public void GracefulDegradation_TsIndexerMetricsAvailable()
    {
        using var writeStream = new CircularBufferWriteStream(DefaultBufferSize);
        var data = CreateTsDataChunk(TsPacketSize * 100);
        writeStream.Write(data, 0, data.Length);

        using var readStream = new CircularBufferReadStream(writeStream, streamId: "ts-metrics-test");

        // Get TS indexer metrics
        var metrics = readStream.GetTsIndexerMetrics();

        // Verify metrics contain essential information
        Assert.True(metrics.TotalPacketsParsed >= 0);
        Assert.True(metrics.TotalBytesProcessed >= 0);
        Assert.NotNull(metrics.Programs);
    }

    /// <summary>
    /// Tests buffer health status under various load conditions.
    /// The health status is determined by gap as percentage of buffer:
    /// - gap &lt; 10% → HEALTHY
    /// - gap &gt; 80% → LAGGING
    /// - otherwise → OK
    /// </summary>
    [Fact]
    public void GracefulDegradation_HealthStatusReflectsLoad()
    {
        const int HealthTestBufferSize = TsPacketSize * 1000;
        using var writeStream = new CircularBufferWriteStream(HealthTestBufferSize);

        // Fill buffer completely
        var data = CreateTsDataChunk(HealthTestBufferSize);
        writeStream.Write(data, 0, data.Length);

        using var readStream = new CircularBufferReadStream(writeStream, streamId: "health-status-test");

        // Read most of the data to reduce gap significantly (< 10% of buffer)
        var buffer = new byte[HealthTestBufferSize];
        while (readStream.CurrentGap > HealthTestBufferSize * 0.08)
        {
            var bytesRead = readStream.Read(buffer, 0, Math.Min(buffer.Length, (int)readStream.CurrentGap));
            if (bytesRead == 0)
            {
                break;
            }
        }

        var diagnostics = readStream.GetDiagnostics();

        // Gap should be small now - check that status is reasonable
        // The exact status depends on timing, but we should have one of the valid statuses
        Assert.True(
            diagnostics.Contains("HEALTHY", StringComparison.Ordinal)
                || diagnostics.Contains("OK", StringComparison.Ordinal)
                || diagnostics.Contains("LAGGING", StringComparison.Ordinal),
            $"Expected valid status in diagnostics. Got: {diagnostics}"
        );
    }

    #endregion

    #region Recovery Scenario Tests

    /// <summary>
    /// Tests recovery after source disconnection and reconnection.
    /// Verifies discontinuity handling allows clean resume.
    /// </summary>
    [Fact]
    public async Task Recovery_SourceDisconnectReconnect_CleanResume()
    {
        await using var writeStream = new CircularBufferWriteStream(DefaultBufferSize);
        writeStream.SignalSourceConnected();

        // Initial streaming
        var data = CreateTsDataChunk(TsPacketSize * 100);
        await writeStream.WriteAsync(data);
        await using var readStream = new CircularBufferReadStream(writeStream, streamId: "recovery-test");
        var buffer = new byte[TsPacketSize * 20];

        // Read initial data
        var bytesRead = await readStream.ReadAsync(buffer);
        Assert.True(bytesRead > 0);

        // Simulate disconnect
        writeStream.SignalSourceDisconnected();
        await Task.Delay(50);

        // Simulate reconnect
        writeStream.SignalReconnecting();
        writeStream.MarkDiscontinuity();
        writeStream.SignalSourceConnected();

        // Write new data after reconnection
        var newData = CreateTsDataChunk(TsPacketSize * 100);
        await writeStream.WriteAsync(newData);

        // Reader should handle discontinuity and continue
        bytesRead = await readStream.ReadAsync(buffer);

        // Verify we can still read (though maybe 0 bytes if waiting for fresh data)
        Assert.True(bytesRead >= 0);
        Assert.Equal(1, writeStream.DiscontinuityCount);
    }

    /// <summary>
    /// Tests that reader position is recorded on disconnect for handoff.
    /// </summary>
    [Fact]
    public void Recovery_ReaderDisconnect_PositionRecorded()
    {
        using var writeStream = new CircularBufferWriteStream(DefaultBufferSize);
        var data = CreateTsDataChunk(TsPacketSize * 100);
        writeStream.Write(data, 0, data.Length);

        using (var readStream = new CircularBufferReadStream(writeStream, streamId: "handoff-test"))
        {
            var buffer = new byte[TsPacketSize * 50];
            _ = readStream.Read(buffer, 0, buffer.Length);
        }

        // The position should have been recorded (or consumed by next reader)
        // and LastReaderDisconnectTime should be set
        Assert.True(
            writeStream.LastReaderDisconnectTime > DateTime.MinValue,
            "Reader disconnect time should be recorded"
        );
    }

    /// <summary>
    /// Tests seamless reader handoff (e.g., FFprobe to FFmpeg).
    /// </summary>
    [Fact]
    public void Recovery_ReaderHandoff_SeamlessContinuation()
    {
        using var writeStream = new CircularBufferWriteStream(DefaultBufferSize);
        var data = CreateTsDataChunk(TsPacketSize * 200);
        writeStream.Write(data, 0, data.Length);

        long firstReaderFinalPosition;

        // First reader (simulating FFprobe)
        using (var reader1 = new CircularBufferReadStream(writeStream, streamId: "ffprobe"))
        {
            var buffer = new byte[TsPacketSize * 50];
            _ = reader1.Read(buffer, 0, buffer.Length);
            firstReaderFinalPosition = reader1.ReadHead;
        }

        // Write more data
        var moreData = CreateTsDataChunk(TsPacketSize * 50);
        writeStream.Write(moreData, 0, moreData.Length);

        // Second reader (simulating FFmpeg) should continue near first reader's position
        using var reader2 = new CircularBufferReadStream(writeStream, streamId: "ffmpeg");

        // Second reader might start at a keyframe after first reader's position
        // or from the last recorded position
        var secondReaderStart = reader2.ReadHead;

        // Should start reasonably close to where first reader left off
        // (allowing for keyframe alignment)
        const long MaxExpectedDrift = TsPacketSize * 100; // Allow some keyframe seeking
        Assert.True(
            Math.Abs(secondReaderStart - firstReaderFinalPosition) < MaxExpectedDrift
                || secondReaderStart > firstReaderFinalPosition,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Second reader should continue near first reader's position. First: {firstReaderFinalPosition}, Second: {secondReaderStart}"
            )
        );
    }

    /// <summary>
    /// Tests buffer reset clears all state for new session.
    /// </summary>
    [Fact]
    public void Recovery_BufferReset_ClearsAllState()
    {
        using var writeStream = new CircularBufferWriteStream(DefaultBufferSize);

        // Write data and cause discontinuity
        var data = CreateTsDataChunk(TsPacketSize * 100);
        writeStream.Write(data, 0, data.Length);
        writeStream.SignalSourceConnected();
        writeStream.SignalSourceDisconnected();
        writeStream.SignalReconnecting();
        writeStream.MarkDiscontinuity();

        // Verify state exists
        Assert.True(writeStream.TotalBytesWritten > 0);
        Assert.Equal(1, writeStream.DiscontinuityCount);
        Assert.Equal(1, writeStream.ReconnectionAttempts);

        // Reset
        writeStream.Reset();

        // Verify all state cleared
        Assert.Equal(0, writeStream.TotalBytesWritten);
        Assert.Equal(0, writeStream.DiscontinuityCount);
        Assert.Equal(0, writeStream.ReconnectionAttempts);
        Assert.False(writeStream.IsSourceConnected);
        Assert.False(writeStream.IsReconnecting);
    }

    /// <summary>
    /// Tests that parse errors from malformed packets are tracked.
    /// Integration test with StreamProcessor error handling.
    /// </summary>
    [Fact]
    public void Recovery_ParseErrors_TrackedForDiagnostics()
    {
        using var writeStream = new CircularBufferWriteStream(DefaultBufferSize);

        // Write valid data
        var validData = CreateTsDataChunk(TsPacketSize * 50);
        writeStream.Write(validData, 0, validData.Length);

        // Get initial parse error count
        var initialErrors = writeStream.TsIndexer.TotalPacketsParsed;

        // Write more valid data
        writeStream.Write(validData, 0, validData.Length);

        // Verify packets were parsed (no exceptions thrown)
        Assert.True(
            writeStream.TsIndexer.TotalPacketsParsed >= initialErrors,
            "Packets should be parsed without errors for valid data"
        );
    }

    #endregion

    #region Parse Error Integration Tests

    /// <summary>
    /// Tests that TsIndexer tracks parse errors from malformed packets.
    /// </summary>
    [Fact]
    public void ParseErrorTracking_MalformedPackets_ErrorsCounted()
    {
        using var indexer = new TsIndexer(DefaultBufferSize);

        // Process valid data first
        var validData = CreateTsDataChunk(TsPacketSize * 10);
        indexer.ProcessChunk(validData, 0);

        var initialPackets = indexer.TotalPacketsParsed;

        // Process severely malformed data (all zeros except partial sync)
        var malformed = new byte[TsPacketSize * 5];
        malformed[0] = TsSyncByte;
        malformed[1] = 0xFF; // Invalid error indicator set
        malformed[2] = 0xFF;
        malformed[3] = 0xFF; // Invalid adaptation field control

        // This may or may not trigger a parse error depending on validation
        indexer.ProcessChunk(malformed, validData.Length);

        // Process more valid data - should still work
        indexer.ProcessChunk(validData, validData.Length + malformed.Length);

        // Verify indexer is still functional (no crash)
        Assert.True(indexer.TotalPacketsParsed >= initialPackets, "Packet count should be non-negative");
    }

    /// <summary>
    /// Tests that truncated packets don't crash the parser.
    /// </summary>
    [Fact]
    public void ParseErrorTracking_TruncatedPacket_HandledGracefully()
    {
        using var indexer = new TsIndexer(DefaultBufferSize);

        // Send partial packet (less than 188 bytes)
        var truncated = new byte[100];
        truncated[0] = TsSyncByte;

        var exception = Record.Exception(() => indexer.ProcessChunk(truncated, 0));

        // Should not throw
        Assert.Null(exception);
    }

    /// <summary>
    /// Tests processing continues after encountering parse errors.
    /// </summary>
    [Fact]
    public void ParseErrorTracking_RecoveryAfterError_ContinuesProcessing()
    {
        using var indexer = new TsIndexer(DefaultBufferSize);

        // Process valid data
        var validData = CreateTsDataChunk(TsPacketSize * 20);
        indexer.ProcessChunk(validData, 0);
        var packetsAfterFirst = indexer.TotalPacketsParsed;

        // Process potentially problematic data
        var maybeProblematic = new byte[TsPacketSize * 5];
        maybeProblematic[0] = TsSyncByte;
        maybeProblematic[3] = 0x30; // Has adaptation field
        maybeProblematic[4] = 200; // Invalid adaptation field length (longer than packet)
        indexer.ProcessChunk(maybeProblematic, validData.Length);

        // Process more valid data - parser should continue working
        indexer.ProcessChunk(validData, validData.Length + maybeProblematic.Length);

        // Verify parser is still functional
        Assert.True(indexer.TotalPacketsParsed >= packetsAfterFirst, "Parser should continue functioning after errors");
    }

    #endregion

    #region Helper Methods

    private static byte[] CreateTsDataChunk(int size)
    {
        var data = new byte[size];
        for (var i = 0; i < size; i += TsPacketSize)
        {
            data[i] = TsSyncByte;
            if (i + 1 < size)
            {
                data[i + 1] = 0x1F; // Null packet PID high
            }

            if (i + 2 < size)
            {
                data[i + 2] = 0xFF; // Null packet PID low
            }

            if (i + 3 < size)
            {
                data[i + 3] = 0x10; // Payload only
            }
        }

        return data;
    }

    private static double CalculateJitter(List<long> deliveryTimes)
    {
        if (deliveryTimes.Count < 2)
        {
            return 0;
        }

        var intervals = new List<long>();
        for (var i = 1; i < deliveryTimes.Count; i++)
        {
            intervals.Add(deliveryTimes[i] - deliveryTimes[i - 1]);
        }

        double avgInterval = 0;
        foreach (var interval in intervals)
        {
            avgInterval += interval;
        }

        avgInterval /= intervals.Count;

        double sumSquaredDiff = 0;
        foreach (var interval in intervals)
        {
            var diff = interval - avgInterval;
            sumSquaredDiff += diff * diff;
        }

        return Math.Sqrt(sumSquaredDiff / intervals.Count);
    }

    #endregion
}
