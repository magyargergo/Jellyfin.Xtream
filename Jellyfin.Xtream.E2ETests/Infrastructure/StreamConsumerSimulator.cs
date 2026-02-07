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
using Jellyfin.Xtream.Service;

namespace Jellyfin.Xtream.E2ETests.Infrastructure;

/// <summary>
/// Simulates Jellyfin client behavior (FFprobe then FFmpeg) for realistic E2E testing.
/// Tracks metrics like bytes read, latency, overflows, and discontinuities.
/// </summary>
public sealed class StreamConsumerSimulator : IDisposable
{
    private const int TsPacketSize = 188;
    private const int DefaultReadBufferSize = 188 * 100; // 100 TS packets

    private Stream? _stream;
    private readonly List<TimeSpan> _readLatencies = new();
    private readonly List<int> _readSizes = new();
    private bool _disposed;

    /// <summary>
    /// Gets the total bytes read from the stream.
    /// </summary>
    public long TotalBytesRead { get; private set; }

    /// <summary>
    /// Gets the number of successful read operations.
    /// </summary>
    public int ReadCount { get; private set; }

    /// <summary>
    /// Gets the number of zero-byte reads (potential buffer underruns).
    /// </summary>
    public int ZeroReadCount { get; private set; }

    /// <summary>
    /// Gets whether any data has been received.
    /// </summary>
    public bool HasReceivedData => TotalBytesRead > 0;

    /// <summary>
    /// Gets the read latencies for analysis.
    /// </summary>
    public IReadOnlyList<TimeSpan> ReadLatencies => _readLatencies;

    /// <summary>
    /// Gets the read sizes for analysis.
    /// </summary>
    public IReadOnlyList<int> ReadSizes => _readSizes;

    /// <summary>
    /// Gets the time to first byte (null if no data received).
    /// </summary>
    public TimeSpan? TimeToFirstByte { get; private set; }

    /// <summary>
    /// Gets the maximum gap between reads.
    /// </summary>
    public TimeSpan MaxReadGap { get; private set; }

    /// <summary>
    /// Gets the number of invalid sync bytes encountered.
    /// </summary>
    public int InvalidSyncByteCount { get; private set; }

    /// <summary>
    /// Gets the number of valid TS packets read.
    /// </summary>
    public int ValidPacketCount { get; private set; }

    /// <summary>
    /// Gets whether the consumer is currently reading.
    /// </summary>
    public bool IsReading { get; private set; }

    /// <summary>
    /// Simulates a client watching a stream for the specified duration.
    /// </summary>
    /// <param name="restream">The restream to watch.</param>
    /// <param name="duration">How long to watch.</param>
    /// <param name="readIntervalMs">Interval between reads in milliseconds (simulates player buffering).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when watching is done.</returns>
    public async Task SimulateWatchingAsync(
        Restream restream,
        TimeSpan duration,
        int readIntervalMs = 10,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _stream = restream.GetStream();
        IsReading = true;

        var buffer = new byte[DefaultReadBufferSize];
        var sw = Stopwatch.StartNew();
        var overallStart = sw.Elapsed;
        DateTime? lastReadTime = null;

        // Create a timeout-based CTS that enforces the duration limit
        // Add 5 second margin to allow graceful completion
        using var timeoutCts = new CancellationTokenSource(duration + TimeSpan.FromSeconds(5));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            while (sw.Elapsed < duration && !linkedCts.Token.IsCancellationRequested)
            {
                var readStart = Stopwatch.GetTimestamp();

                // Use per-read timeout to prevent ReadAsync from blocking beyond remaining duration
                var remainingTime = duration - sw.Elapsed;
                if (remainingTime <= TimeSpan.Zero)
                {
                    break;
                }

                // Limit each ReadAsync to at most 5 seconds or remaining duration, whichever is smaller
                var readTimeout = TimeSpan.FromSeconds(Math.Min(5, remainingTime.TotalSeconds + 1));
                using var readCts = new CancellationTokenSource(readTimeout);
                using var readLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    linkedCts.Token,
                    readCts.Token
                );

                int bytesRead;
                try
                {
                    bytesRead = await _stream.ReadAsync(buffer, readLinkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (readCts.Token.IsCancellationRequested && !linkedCts.Token.IsCancellationRequested)
                {
                    // Read timed out but overall test not cancelled - just continue to check duration
                    bytesRead = 0;
                }

                // Check duration again after ReadAsync returns (it may have blocked)
                if (sw.Elapsed >= duration)
                {
                    break;
                }

                var readLatency = Stopwatch.GetElapsedTime(readStart);
                _readLatencies.Add(readLatency);
                _readSizes.Add(bytesRead);

                if (bytesRead > 0)
                {
                    TotalBytesRead += bytesRead;
                    ReadCount++;

                    // Track time to first byte
                    TimeToFirstByte ??= sw.Elapsed - overallStart;

                    // Track max gap
                    if (lastReadTime.HasValue)
                    {
                        var gap = DateTime.UtcNow - lastReadTime.Value;
                        if (gap > MaxReadGap)
                        {
                            MaxReadGap = gap;
                        }
                    }

                    lastReadTime = DateTime.UtcNow;

                    // Validate TS packets
                    ValidatePackets(buffer, bytesRead);
                }
                else
                {
                    ZeroReadCount++;
                }

                if (readIntervalMs > 0 && sw.Elapsed < duration && !linkedCts.Token.IsCancellationRequested)
                {
                    // Use remaining time for delay too
                    var delayTime = Math.Min(readIntervalMs, (int)(duration - sw.Elapsed).TotalMilliseconds);
                    if (delayTime > 0)
                    {
                        await Task.Delay(delayTime, linkedCts.Token).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
        {
            // Test duration timeout reached - this is expected, not an error
        }
        finally
        {
            IsReading = false;
        }
    }

    /// <summary>
    /// Simulates the FFprobe to FFmpeg handoff pattern (short read, disconnect, reconnect).
    /// </summary>
    /// <param name="restream">The restream to test.</param>
    /// <param name="probeReadMs">How long FFprobe reads.</param>
    /// <param name="handoffDelayMs">Delay between FFprobe disconnect and FFmpeg connect.</param>
    /// <param name="ffmpegReadMs">How long FFmpeg reads after reconnection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Tuple of (probeBytes, ffmpegBytes, reconnectSuccessful).</returns>
    public async Task<(
        long ProbeBytes,
        long FfmpegBytes,
        bool ReconnectSuccessful
    )> SimulateFFprobeToFFmpegHandoffAsync(
        Restream restream,
        int probeReadMs = 500,
        int handoffDelayMs = 200,
        int ffmpegReadMs = 2000,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var buffer = new byte[DefaultReadBufferSize];
        long probeBytes = 0;
        long ffmpegBytes = 0;

        // Total timeout for entire operation with margin
        var totalTimeout = TimeSpan.FromMilliseconds(probeReadMs + handoffDelayMs + ffmpegReadMs + 10000);
        using var timeoutCts = new CancellationTokenSource(totalTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        // Phase 1: FFprobe reads briefly
        using (var probeStream = restream.GetStream())
        {
            var probeSw = Stopwatch.StartNew();
            while (probeSw.ElapsedMilliseconds < probeReadMs && !linkedCts.Token.IsCancellationRequested)
            {
                // Per-read timeout
                var remainingMs = probeReadMs - (int)probeSw.ElapsedMilliseconds;
                if (remainingMs <= 0)
                    break;

                using var readCts = new CancellationTokenSource(Math.Min(2000, remainingMs + 500));
                using var readLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    linkedCts.Token,
                    readCts.Token
                );

                int read;
                try
                {
                    read = await probeStream.ReadAsync(buffer, readLinkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (readCts.Token.IsCancellationRequested && !linkedCts.Token.IsCancellationRequested)
                {
                    read = 0;
                }

                if (read > 0)
                {
                    probeBytes += read;
                }

                if (probeSw.ElapsedMilliseconds < probeReadMs && !linkedCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(10, linkedCts.Token).ConfigureAwait(false);
                }
            }
        }
        // probeStream disposed here (simulates FFprobe exit)

        // Handoff delay (simulates FFprobe finishing and FFmpeg starting)
        await Task.Delay(handoffDelayMs, linkedCts.Token).ConfigureAwait(false);

        // Phase 2: FFmpeg reconnects
        bool reconnectSuccessful;
        try
        {
            using var ffmpegStream = restream.GetStream();
            reconnectSuccessful = true;

            var ffmpegSw = Stopwatch.StartNew();
            while (ffmpegSw.ElapsedMilliseconds < ffmpegReadMs && !linkedCts.Token.IsCancellationRequested)
            {
                // Per-read timeout
                var remainingMs = ffmpegReadMs - (int)ffmpegSw.ElapsedMilliseconds;
                if (remainingMs <= 0)
                    break;

                using var readCts = new CancellationTokenSource(Math.Min(2000, remainingMs + 500));
                using var readLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    linkedCts.Token,
                    readCts.Token
                );

                int read;
                try
                {
                    read = await ffmpegStream.ReadAsync(buffer, readLinkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (readCts.Token.IsCancellationRequested && !linkedCts.Token.IsCancellationRequested)
                {
                    read = 0;
                }

                if (read > 0)
                {
                    ffmpegBytes += read;
                    TotalBytesRead += read;
                    ReadCount++;
                }

                if (ffmpegSw.ElapsedMilliseconds < ffmpegReadMs && !linkedCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(10, linkedCts.Token).ConfigureAwait(false);
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Stream was disposed during handoff
            reconnectSuccessful = false;
        }

        return (probeBytes, ffmpegBytes, reconnectSuccessful);
    }

    /// <summary>
    /// Validates TS packets in the buffer.
    /// </summary>
    private void ValidatePackets(byte[] buffer, int length)
    {
        int packetCount = length / TsPacketSize;

        for (int i = 0; i < packetCount; i++)
        {
            int offset = i * TsPacketSize;
            byte syncByte = buffer[offset];

            if (syncByte == 0x47)
            {
                ValidPacketCount++;
            }
            else if (syncByte != 0xFF) // 0xFF is padding, not an error
            {
                InvalidSyncByteCount++;
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream?.Dispose();
        _stream = null;
    }

    /// <summary>
    /// Gets statistics summary for test assertions.
    /// </summary>
    public ConsumerStatistics GetStatistics()
    {
        var avgLatency =
            _readLatencies.Count > 0 ? TimeSpan.FromTicks((long)_readLatencies.Average(l => l.Ticks)) : TimeSpan.Zero;

        var p95Latency =
            _readLatencies.Count > 0
                ? _readLatencies.OrderBy(l => l).ElementAt((int)(_readLatencies.Count * 0.95))
                : TimeSpan.Zero;

        var avgReadSize = _readSizes.Count > 0 ? _readSizes.Average() : 0;

        return new ConsumerStatistics
        {
            TotalBytesRead = TotalBytesRead,
            ReadCount = ReadCount,
            ZeroReadCount = ZeroReadCount,
            ValidPacketCount = ValidPacketCount,
            InvalidSyncByteCount = InvalidSyncByteCount,
            TimeToFirstByte = TimeToFirstByte,
            MaxReadGap = MaxReadGap,
            AverageReadLatency = avgLatency,
            P95ReadLatency = p95Latency,
            AverageReadSize = avgReadSize,
        };
    }
}

/// <summary>
/// Statistics collected by the consumer simulator.
/// </summary>
public sealed record ConsumerStatistics
{
    /// <summary>Total bytes read.</summary>
    public long TotalBytesRead { get; init; }

    /// <summary>Number of successful reads.</summary>
    public int ReadCount { get; init; }

    /// <summary>Number of zero-byte reads.</summary>
    public int ZeroReadCount { get; init; }

    /// <summary>Valid TS packets read.</summary>
    public int ValidPacketCount { get; init; }

    /// <summary>Invalid sync bytes encountered.</summary>
    public int InvalidSyncByteCount { get; init; }

    /// <summary>Time to first byte.</summary>
    public TimeSpan? TimeToFirstByte { get; init; }

    /// <summary>Maximum gap between reads.</summary>
    public TimeSpan MaxReadGap { get; init; }

    /// <summary>Average read latency.</summary>
    public TimeSpan AverageReadLatency { get; init; }

    /// <summary>95th percentile read latency.</summary>
    public TimeSpan P95ReadLatency { get; init; }

    /// <summary>Average bytes per read.</summary>
    public double AverageReadSize { get; init; }

    /// <summary>Sync byte validity rate (0-100).</summary>
    public double SyncByteValidityRate =>
        ValidPacketCount + InvalidSyncByteCount > 0
            ? ValidPacketCount * 100.0 / (ValidPacketCount + InvalidSyncByteCount)
            : 100.0;
}
