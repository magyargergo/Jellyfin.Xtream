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
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Jellyfin.Xtream.Service.ProviderManagement;

/// <summary>
/// Tracks provider performance metrics for enhanced health scoring.
/// Captures latency, throughput, error rates, and connection quality.
/// </summary>
public sealed class ProviderMetricsTracker : IProviderMetricsTracker
{
    private readonly ConcurrentDictionary<string, ProviderMetrics> _metrics = new(StringComparer.Ordinal);

    /// <summary>
    /// Records the latency of a connection attempt.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="latencyMs">Connection latency in milliseconds.</param>
    public void RecordLatency(string providerId, double latencyMs)
    {
        var metrics = GetOrCreate(providerId);
        metrics.AddLatencySample(latencyMs);
    }

    /// <summary>
    /// Records bytes transferred for throughput calculation.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="bytes">Bytes transferred.</param>
    /// <param name="durationMs">Duration in milliseconds.</param>
    public void RecordThroughput(string providerId, long bytes, double durationMs)
    {
        if (durationMs <= 0)
        {
            return;
        }

        var metrics = GetOrCreate(providerId);
        var mbps = bytes / 1024.0 / 1024.0 / (durationMs / 1000.0);
        metrics.AddThroughputSample(mbps);
    }

    /// <summary>
    /// Records a stream error.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="errorType">Type of error.</param>
    public void RecordError(string providerId, StreamErrorType errorType)
    {
        var metrics = GetOrCreate(providerId);
        metrics.IncrementError(errorType);
    }

    /// <summary>
    /// Records a stream disconnection.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="streamDurationMs">How long the stream was active before disconnection.</param>
    public void RecordDisconnection(string providerId, double streamDurationMs)
    {
        var metrics = GetOrCreate(providerId);
        metrics.AddDisconnection(streamDurationMs);
    }

    /// <summary>
    /// Records a TsDuck TR 101 290 quality score sample.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="qualityScore">TsDuck quality score (0-100).</param>
    public void RecordTsDuckQuality(string providerId, int qualityScore)
    {
        var metrics = GetOrCreate(providerId);
        metrics.AddTsDuckQualitySample(qualityScore);
    }

    /// <summary>
    /// Gets the current metrics snapshot for a provider.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>The metrics snapshot, or default values if not tracked.</returns>
    public ProviderMetricsSnapshot GetSnapshot(string providerId) =>
        !_metrics.TryGetValue(providerId, out var metrics) ? ProviderMetricsSnapshot.Default : metrics.GetSnapshot();

    /// <summary>
    /// Calculates a health score based on metrics (0-100, higher is better).
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>Health score from 0 to 100.</returns>
    public int CalculateHealthScore(string providerId)
    {
        var snapshot = GetSnapshot(providerId);
        return CalculateHealthScore(snapshot);
    }

    /// <summary>
    /// Calculates a priority score based on metrics (0-100, lower is better).
    /// This is the inverse of health score - a provider with excellent health
    /// gets a low priority number (first choice for streaming).
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>Priority score from 0 (best) to 100 (worst).</returns>
    public int CalculatePriority(string providerId)
    {
        var healthScore = CalculateHealthScore(providerId);
        return 100 - healthScore;
    }

    /// <summary>
    /// Calculates a priority score from a metrics snapshot.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CalculatePriority(ProviderMetricsSnapshot snapshot)
    {
        var healthScore = CalculateHealthScore(snapshot);
        return 100 - healthScore;
    }

    // Lookup tables for O(1) score calculation - avoids branch mispredictions
    // Format: (threshold, score) pairs, sorted descending by threshold for binary search
    private static readonly (double Threshold, int Score)[] LatencyLookup =
    [
        (2000, 0),
        (1000, 4),
        (500, 9),
        (200, 14),
        (100, 17),
        (0, 20),
    ];

    private static readonly (double Threshold, int Score)[] ThroughputLookup =
    [
        (5.0, 20),
        (3.0, 17),
        (1.5, 14),
        (0.5, 9),
        (0.1, 4),
        (0, 0),
    ];

    private static readonly (double Threshold, int Score)[] ErrorRateLookup =
    [
        (10.0, 0),
        (5.0, 4),
        (3.0, 8),
        (1.0, 15),
        (0.1, 21),
        (0, 25),
    ];

    private static readonly (double Threshold, int Score)[] UptimeLookup =
    [
        (60, 15),
        (30, 12),
        (15, 9),
        (5, 5),
        (0, 2),
    ];

    private static readonly (double Threshold, int Score)[] TsDuckLookup =
    [
        (90, 20),
        (75, 17),
        (60, 14),
        (50, 10),
        (25, 5),
        (0, 0),
    ];

    /// <summary>
    /// Calculates health score from a metrics snapshot.
    /// Uses lookup tables for O(1) threshold evaluation with minimal branching.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static int CalculateHealthScore(ProviderMetricsSnapshot snapshot)
    {
        // Weight distribution (total 100):
        // - Latency: 20% (lower is better)
        // - Throughput: 20% (higher is better)
        // - Error rate: 25% (lower is better)
        // - Uptime: 15% (higher is better)
        // - TsDuck TR 101 290 Quality: 20% (higher is better)

        // Latency score (0-20): lower latency = higher score
        var latencyScore = LookupScoreLessThan(snapshot.AvgLatencyMs, LatencyLookup);

        // Throughput score (0-20): higher throughput = higher score
        var throughputScore = LookupScoreGreaterThan(snapshot.AvgThroughputMBps, ThroughputLookup);

        // Error rate score (0-25): lower error rate = higher score
        var errorRate = snapshot.TotalSamples > 0 ? (double)snapshot.TotalErrors / snapshot.TotalSamples * 100.0 : 0.0;
        var errorScore = LookupScoreLessThan(errorRate, ErrorRateLookup);

        // Uptime score (0-15): higher MTBD = higher score
        var mtbdMinutes =
            snapshot.DisconnectionCount > 0 ? snapshot.TotalStreamTimeMs / 60000.0 / snapshot.DisconnectionCount : 60.0;
        var uptimeScore = LookupScoreGreaterThan(mtbdMinutes, UptimeLookup);

        // TsDuck score (0-20): higher quality = higher score, neutral if no data
        var tsDuckScore =
            (snapshot.AvgTsDuckQuality < 0 || snapshot.TsDuckSampleCount == 0)
                ? 10
                : LookupScoreGreaterThan(snapshot.AvgTsDuckQuality, TsDuckLookup);

        return Math.Clamp(latencyScore + throughputScore + errorScore + uptimeScore + tsDuckScore, 0, 100);
    }

    /// <summary>
    /// Lookup score where value must be less than threshold (for metrics where lower is better).
    /// Uses linear scan optimized for small arrays (typically 5-6 elements).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LookupScoreLessThan(double value, ReadOnlySpan<(double Threshold, int Score)> lookup)
    {
        // Linear scan is faster than binary search for small arrays (< 8 elements)
        // due to better cache locality and branch prediction
        foreach (var (threshold, score) in lookup)
        {
            if (value >= threshold)
            {
                return score;
            }
        }

        return lookup[^1].Score;
    }

    /// <summary>
    /// Lookup score where value must be greater than threshold (for metrics where higher is better).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LookupScoreGreaterThan(double value, ReadOnlySpan<(double Threshold, int Score)> lookup)
    {
        foreach (var (threshold, score) in lookup)
        {
            if (value >= threshold)
            {
                return score;
            }
        }

        return lookup[^1].Score;
    }

    /// <summary>
    /// Resets metrics for a provider.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    public void Reset(string providerId)
    {
        if (_metrics.TryGetValue(providerId, out var metrics))
        {
            metrics.Reset();
        }
    }

    /// <summary>
    /// Clears all tracked metrics.
    /// </summary>
    public void Clear() => _metrics.Clear();

    private ProviderMetrics GetOrCreate(string providerId) => _metrics.GetOrAdd(providerId, _ => new ProviderMetrics());
}

/// <summary>
/// Types of stream errors for categorization.
/// </summary>
public enum StreamErrorType
{
    /// <summary>Packet-level errors (CRC, corruption).</summary>
    PacketError,

    /// <summary>Continuity counter errors (missing packets).</summary>
    ContinuityError,

    /// <summary>Timing synchronization errors.</summary>
    SyncError,

    /// <summary>Connection timeout.</summary>
    Timeout,

    /// <summary>Network-level errors.</summary>
    NetworkError,

    /// <summary>Data stall - no new data received for extended period.</summary>
    DataStall,
}

/// <summary>
/// Internal metrics container for a single provider.
/// Thread-safe using interlocked operations.
/// </summary>
internal sealed class ProviderMetrics
{
    private const int MaxSamples = 100;

    // Latency tracking (exponential moving average)
    private double _latencySum;
    private long _latencyCount;
    private double _latencyMin = double.MaxValue;
    private double _latencyMax;

    // Throughput tracking
    private double _throughputSum;
    private long _throughputCount;
    private double _throughputMax;

    // Error tracking
    private long _packetErrors;
    private long _continuityErrors;
    private long _syncErrors;
    private long _timeoutErrors;
    private long _networkErrors;
    private long _dataStallErrors;

    // Uptime tracking
    private long _disconnectionCount;
    private double _totalStreamTimeMs;
    private long _totalSamples;

    // TsDuck TR 101 290 quality tracking
    private double _tsDuckQualitySum;
    private long _tsDuckQualityCount;

    public void AddLatencySample(double latencyMs)
    {
        // Use exponential decay when over max samples
        if (Interlocked.Read(ref _latencyCount) >= MaxSamples)
        {
            // Decay by 10%
            var oldSum = Interlocked.CompareExchange(ref _latencySum, 0, 0);
            _ = Interlocked.Exchange(ref _latencySum, oldSum * 0.9);
            _ = Interlocked.Exchange(ref _latencyCount, (long)(Interlocked.Read(ref _latencyCount) * 0.9));
        }

        // Add new sample
        double currentSum;
        double newSum;
        do
        {
            currentSum = Interlocked.CompareExchange(ref _latencySum, 0, 0);
            newSum = currentSum + latencyMs;
        } while (Interlocked.CompareExchange(ref _latencySum, newSum, currentSum) != currentSum);

        _ = Interlocked.Increment(ref _latencyCount);
        _ = Interlocked.Increment(ref _totalSamples);

        // Update min/max
        UpdateMin(ref _latencyMin, latencyMs);
        UpdateMax(ref _latencyMax, latencyMs);
    }

    public void AddThroughputSample(double mbps)
    {
        if (Interlocked.Read(ref _throughputCount) >= MaxSamples)
        {
            var oldSum = Interlocked.CompareExchange(ref _throughputSum, 0, 0);
            _ = Interlocked.Exchange(ref _throughputSum, oldSum * 0.9);
            _ = Interlocked.Exchange(ref _throughputCount, (long)(Interlocked.Read(ref _throughputCount) * 0.9));
        }

        double currentSum;
        double newSum;
        do
        {
            currentSum = Interlocked.CompareExchange(ref _throughputSum, 0, 0);
            newSum = currentSum + mbps;
        } while (Interlocked.CompareExchange(ref _throughputSum, newSum, currentSum) != currentSum);

        _ = Interlocked.Increment(ref _throughputCount);

        UpdateMax(ref _throughputMax, mbps);
    }

    public void IncrementError(StreamErrorType errorType)
    {
        switch (errorType)
        {
            case StreamErrorType.PacketError:
                _ = Interlocked.Increment(ref _packetErrors);
                break;
            case StreamErrorType.ContinuityError:
                _ = Interlocked.Increment(ref _continuityErrors);
                break;
            case StreamErrorType.SyncError:
                _ = Interlocked.Increment(ref _syncErrors);
                break;
            case StreamErrorType.Timeout:
                _ = Interlocked.Increment(ref _timeoutErrors);
                break;
            case StreamErrorType.NetworkError:
                _ = Interlocked.Increment(ref _networkErrors);
                break;
            case StreamErrorType.DataStall:
                _ = Interlocked.Increment(ref _dataStallErrors);
                break;
        }
    }

    public void AddDisconnection(double streamDurationMs)
    {
        _ = Interlocked.Increment(ref _disconnectionCount);

        double currentTotal;
        double newTotal;
        do
        {
            currentTotal = Interlocked.CompareExchange(ref _totalStreamTimeMs, 0, 0);
            newTotal = currentTotal + streamDurationMs;
        } while (Interlocked.CompareExchange(ref _totalStreamTimeMs, newTotal, currentTotal) != currentTotal);
    }

    public void AddTsDuckQualitySample(int qualityScore)
    {
        // Use exponential decay when over max samples
        if (Interlocked.Read(ref _tsDuckQualityCount) >= MaxSamples)
        {
            var oldSum = Interlocked.CompareExchange(ref _tsDuckQualitySum, 0, 0);
            _ = Interlocked.Exchange(ref _tsDuckQualitySum, oldSum * 0.9);
            _ = Interlocked.Exchange(ref _tsDuckQualityCount, (long)(Interlocked.Read(ref _tsDuckQualityCount) * 0.9));
        }

        double currentSum;
        double newSum;
        do
        {
            currentSum = Interlocked.CompareExchange(ref _tsDuckQualitySum, 0, 0);
            newSum = currentSum + qualityScore;
        } while (Interlocked.CompareExchange(ref _tsDuckQualitySum, newSum, currentSum) != currentSum);

        _ = Interlocked.Increment(ref _tsDuckQualityCount);
    }

    public ProviderMetricsSnapshot GetSnapshot()
    {
        var latencyCount = Interlocked.Read(ref _latencyCount);
        var throughputCount = Interlocked.Read(ref _throughputCount);
        var tsDuckQualityCount = Interlocked.Read(ref _tsDuckQualityCount);

        return new ProviderMetricsSnapshot
        {
            AvgLatencyMs = latencyCount > 0 ? Interlocked.CompareExchange(ref _latencySum, 0, 0) / latencyCount : 0,
            MinLatencyMs = _latencyMin == double.MaxValue ? 0 : _latencyMin,
            MaxLatencyMs = _latencyMax,
            AvgThroughputMBps =
                throughputCount > 0 ? Interlocked.CompareExchange(ref _throughputSum, 0, 0) / throughputCount : 0,
            MaxThroughputMBps = _throughputMax,
            PacketErrors = Interlocked.Read(ref _packetErrors),
            ContinuityErrors = Interlocked.Read(ref _continuityErrors),
            SyncErrors = Interlocked.Read(ref _syncErrors),
            TimeoutErrors = Interlocked.Read(ref _timeoutErrors),
            NetworkErrors = Interlocked.Read(ref _networkErrors),
            DataStallErrors = Interlocked.Read(ref _dataStallErrors),
            TotalErrors =
                Interlocked.Read(ref _packetErrors)
                + Interlocked.Read(ref _continuityErrors)
                + Interlocked.Read(ref _syncErrors)
                + Interlocked.Read(ref _timeoutErrors)
                + Interlocked.Read(ref _networkErrors)
                + Interlocked.Read(ref _dataStallErrors),
            DisconnectionCount = Interlocked.Read(ref _disconnectionCount),
            TotalStreamTimeMs = Interlocked.CompareExchange(ref _totalStreamTimeMs, 0, 0),
            TotalSamples = Interlocked.Read(ref _totalSamples),
            AvgTsDuckQuality =
                tsDuckQualityCount > 0
                    ? Interlocked.CompareExchange(ref _tsDuckQualitySum, 0, 0) / tsDuckQualityCount
                    : -1, // -1 indicates no TsDuck data
            TsDuckSampleCount = tsDuckQualityCount,
            Timestamp = DateTime.UtcNow,
        };
    }

    public void Reset()
    {
        _ = Interlocked.Exchange(ref _latencySum, 0);
        _ = Interlocked.Exchange(ref _latencyCount, 0L);
        _latencyMin = double.MaxValue;
        _ = Interlocked.Exchange(ref _latencyMax, 0);
        _ = Interlocked.Exchange(ref _throughputSum, 0);
        _ = Interlocked.Exchange(ref _throughputCount, 0L);
        _ = Interlocked.Exchange(ref _throughputMax, 0);
        _ = Interlocked.Exchange(ref _packetErrors, 0L);
        _ = Interlocked.Exchange(ref _continuityErrors, 0L);
        _ = Interlocked.Exchange(ref _syncErrors, 0L);
        _ = Interlocked.Exchange(ref _timeoutErrors, 0L);
        _ = Interlocked.Exchange(ref _networkErrors, 0L);
        _ = Interlocked.Exchange(ref _dataStallErrors, 0L);
        _ = Interlocked.Exchange(ref _disconnectionCount, 0L);
        _ = Interlocked.Exchange(ref _totalStreamTimeMs, 0);
        _ = Interlocked.Exchange(ref _totalSamples, 0L);
        _ = Interlocked.Exchange(ref _tsDuckQualitySum, 0);
        _ = Interlocked.Exchange(ref _tsDuckQualityCount, 0L);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UpdateMin(ref double location, double value)
    {
        double currentMin;
        do
        {
            currentMin = Interlocked.CompareExchange(ref location, value, location);
            if (currentMin <= value)
            {
                return;
            }
        } while (Interlocked.CompareExchange(ref location, value, currentMin) != currentMin);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UpdateMax(ref double location, double value)
    {
        double currentMax;
        do
        {
            currentMax = Interlocked.CompareExchange(ref location, value, location);
            if (currentMax >= value)
            {
                return;
            }
        } while (Interlocked.CompareExchange(ref location, value, currentMax) != currentMax);
    }
}

/// <summary>
/// Immutable snapshot of provider performance metrics.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct ProviderMetricsSnapshot
{
    /// <summary>Gets the default (empty) metrics snapshot.</summary>
    public static readonly ProviderMetricsSnapshot Default = new()
    {
        AvgLatencyMs = 500, // Assume moderate latency
        AvgThroughputMBps = 1.0, // Assume 1 MB/s
        TotalSamples = 0,
        Timestamp = DateTime.UtcNow,
    };

    /// <summary>Gets the average connection latency in milliseconds.</summary>
    public double AvgLatencyMs { get; init; }

    /// <summary>Gets the minimum observed latency.</summary>
    public double MinLatencyMs { get; init; }

    /// <summary>Gets the maximum observed latency.</summary>
    public double MaxLatencyMs { get; init; }

    /// <summary>Gets the average throughput in MB/s.</summary>
    public double AvgThroughputMBps { get; init; }

    /// <summary>Gets the maximum observed throughput.</summary>
    public double MaxThroughputMBps { get; init; }

    /// <summary>Gets the count of packet-level errors.</summary>
    public long PacketErrors { get; init; }

    /// <summary>Gets the count of continuity errors.</summary>
    public long ContinuityErrors { get; init; }

    /// <summary>Gets the count of sync errors.</summary>
    public long SyncErrors { get; init; }

    /// <summary>Gets the count of timeout errors.</summary>
    public long TimeoutErrors { get; init; }

    /// <summary>Gets the count of network errors.</summary>
    public long NetworkErrors { get; init; }

    /// <summary>Gets the count of data stall errors.</summary>
    public long DataStallErrors { get; init; }

    /// <summary>Gets the total error count.</summary>
    public long TotalErrors { get; init; }

    /// <summary>Gets the number of stream disconnections.</summary>
    public long DisconnectionCount { get; init; }

    /// <summary>Gets the total stream time in milliseconds.</summary>
    public double TotalStreamTimeMs { get; init; }

    /// <summary>Gets the total number of samples collected.</summary>
    public long TotalSamples { get; init; }

    /// <summary>Gets the average TsDuck TR 101 290 quality score (0-100), or -1 if no data.</summary>
    public double AvgTsDuckQuality { get; init; }

    /// <summary>Gets the number of TsDuck quality samples.</summary>
    public long TsDuckSampleCount { get; init; }

    /// <summary>Gets the snapshot timestamp.</summary>
    public DateTime Timestamp { get; init; }
}
