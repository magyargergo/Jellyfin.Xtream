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
using System.Runtime.InteropServices;

namespace Jellyfin.Xtream.Service.Streaming.Native;

/// <summary>
/// TSDuck process status enumeration.
/// </summary>
public enum TsDuckProcessStatus
{
    /// <summary>Process has not been started.</summary>
    NotStarted,

    /// <summary>Process is starting up.</summary>
    Starting,

    /// <summary>Process is running and healthy.</summary>
    Running,

    /// <summary>Process is restarting after a crash.</summary>
    Restarting,

    /// <summary>Process has been stopped gracefully.</summary>
    Stopped,

    /// <summary>Process has failed and cannot be restarted.</summary>
    Failed,

    /// <summary>TSDuck is not installed or not found.</summary>
    Unavailable,
}

/// <summary>
/// TR 101 290 Priority 1 indicators (most critical).
/// These indicate fundamental transport stream errors.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct Tr101290Priority1(
    long SyncByteError,
    long SyncLoss,
    long PatError,
    long PatError2,
    long ContinuityCountError,
    long PmtError,
    long PmtError2,
    long PidError
)
{
    /// <summary>
    /// Gets a value indicating whether any Priority 1 errors have occurred.
    /// </summary>
    public bool HasErrors =>
        SyncByteError > 0
        || SyncLoss > 0
        || PatError > 0
        || PatError2 > 0
        || ContinuityCountError > 0
        || PmtError > 0
        || PmtError2 > 0
        || PidError > 0;

    /// <summary>
    /// Gets the total error count across all Priority 1 indicators.
    /// </summary>
    public long TotalErrors =>
        SyncByteError + SyncLoss + PatError + PatError2 + ContinuityCountError + PmtError + PmtError2 + PidError;
}

/// <summary>
/// TR 101 290 Priority 2 indicators.
/// These indicate transport and timing issues.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct Tr101290Priority2(
    long TransportError,
    long CrcError,
    long PcrRepetitionError,
    long PcrDiscontinuityError,
    long PcrAccuracyError,
    long PtsError,
    long CatError
)
{
    /// <summary>
    /// Gets a value indicating whether any Priority 2 errors have occurred.
    /// </summary>
    public bool HasErrors =>
        TransportError > 0
        || CrcError > 0
        || PcrRepetitionError > 0
        || PcrDiscontinuityError > 0
        || PcrAccuracyError > 0
        || PtsError > 0
        || CatError > 0;

    /// <summary>
    /// Gets the total error count across all Priority 2 indicators.
    /// </summary>
    public long TotalErrors =>
        TransportError + CrcError + PcrRepetitionError + PcrDiscontinuityError + PcrAccuracyError + PtsError + CatError;
}

/// <summary>
/// PCR (Program Clock Reference) analysis metrics.
/// Provides detailed timing analysis for decoder buffer management.
/// </summary>
/// <remarks>
/// <para>PCR jitter exceeding 500ns violates TR 101 290 Priority 2.4.</para>
/// <para>PCR interval exceeding 100ms violates TR 101 290 Priority 2.3.</para>
/// <para>ISO/IEC 13818-1 requirements tracked:</para>
/// <list type="bullet">
/// <item>Accuracy: ±500ns phase tolerance</item>
/// <item>Frequency offset: ±30 ppm (±810 Hz at 27MHz)</item>
/// <item>Drift rate: 75 mHz/sec (10 ppm/hr)</item>
/// </list>
/// </remarks>
[StructLayout(LayoutKind.Auto)]
public readonly record struct PcrAnalysis(
    double PcrJitterUs,
    double PcrJitterMaxUs,
    double PcrJitterAvgUs,
    long PcrIntervalPackets,
    double PcrIntervalMs,
    double PcrDriftPpm,
    long PcrCount,
    long PcrValidCount,
    double PcrFrequencyOffsetPpm,
    double PcrDriftRatePpmHr,
    bool IsFrequencyOffsetValid,
    bool IsDriftRateValid,
    double PcrAccuracyNs,
    bool IsAccuracyValid
)
{
    /// <summary>
    /// TR 101 290 PCR jitter threshold in microseconds (500ns = 0.5us).
    /// </summary>
    public const double JitterThresholdUs = 0.5;

    /// <summary>
    /// TR 101 290 PCR interval threshold in milliseconds.
    /// </summary>
    public const double IntervalThresholdMs = 100.0;

    /// <summary>
    /// ISO/IEC 13818-1 frequency offset limit in ppm (±30 ppm).
    /// </summary>
    public const double FrequencyOffsetLimitPpm = 30.0;

    /// <summary>
    /// ISO/IEC 13818-1 drift rate limit in ppm/hour (10 ppm/hr = 75 mHz/sec).
    /// </summary>
    public const double DriftRateLimitPpmHr = 10.0;

    /// <summary>
    /// ISO/IEC 13818-1 PCR accuracy limit in nanoseconds (±500ns).
    /// </summary>
    public const double AccuracyLimitNs = 500.0;

    /// <summary>
    /// Gets a value indicating whether PCR jitter exceeds TR 101 290 limit (500ns).
    /// </summary>
    public bool HasJitterViolation => PcrJitterUs > JitterThresholdUs;

    /// <summary>
    /// Gets a value indicating whether PCR interval exceeds TR 101 290 limit (100ms).
    /// </summary>
    public bool HasIntervalViolation => PcrIntervalMs > IntervalThresholdMs;

    /// <summary>
    /// Gets the PCR validity ratio (0.0 to 1.0).
    /// </summary>
    public double ValidityRatio => PcrCount > 0 ? (double)PcrValidCount / PcrCount : 1.0;

    /// <summary>
    /// Gets a value indicating whether PCR drift is significant (>100 ppm).
    /// </summary>
    public bool HasSignificantDrift => Math.Abs(PcrDriftPpm) > 100;

    /// <summary>
    /// Gets a value indicating whether the PCR frequency offset exceeds ISO 13818-1 limit (±30 ppm).
    /// </summary>
    public bool HasFrequencyOffsetViolation => !IsFrequencyOffsetValid;

    /// <summary>
    /// Gets a value indicating whether the PCR drift rate exceeds ISO 13818-1 limit (10 ppm/hr).
    /// </summary>
    public bool HasDriftRateViolation => !IsDriftRateValid;

    /// <summary>
    /// Gets a value indicating whether the PCR accuracy exceeds ISO 13818-1 limit (±500ns).
    /// </summary>
    public bool HasAccuracyViolation => !IsAccuracyValid;

    /// <summary>
    /// Gets a value indicating whether the PCR complies with all ISO 13818-1 requirements.
    /// </summary>
    public bool IsIso13818Compliant => IsFrequencyOffsetValid && IsDriftRateValid && IsAccuracyValid;
}

/// <summary>
/// Inter-packet Arrival Time (IAT) analysis metrics.
/// Provides network jitter analysis for UDP/IP streams.
/// </summary>
/// <remarks>
/// High IAT jitter indicates network congestion or QoS issues.
/// This is a leading indicator - problems appear before TS-level errors.
/// Uses TsDuck's iat plugin logic.
/// </remarks>
[StructLayout(LayoutKind.Auto)]
public readonly record struct IatAnalysis(
    double IatAvgUs,
    double IatMinUs,
    double IatMaxUs,
    double IatJitterUs,
    double IatStddevUs,
    long LatePackets,
    long EarlyPackets,
    long BurstCount
)
{
    /// <summary>
    /// Default IAT jitter threshold in microseconds (1ms = 1000us).
    /// </summary>
    public const double JitterThresholdUs = 1000.0;

    /// <summary>
    /// Burst count threshold for significant bursting.
    /// </summary>
    public const int BurstThreshold = 10;

    /// <summary>
    /// Gets a value indicating whether network jitter is high (>1ms).
    /// </summary>
    public bool HasHighJitter => IatJitterUs > JitterThresholdUs;

    /// <summary>
    /// Gets a value indicating whether significant packet bursting is detected.
    /// </summary>
    public bool HasBursting => BurstCount > BurstThreshold;

    /// <summary>
    /// Gets the total out-of-order packets (late + early).
    /// </summary>
    public long OutOfOrderPackets => LatePackets + EarlyPackets;

    /// <summary>
    /// Gets a value indicating whether network delivery is unstable.
    /// </summary>
    public bool IsUnstable => HasHighJitter || HasBursting || OutOfOrderPackets > 100;
}

/// <summary>
/// Bitrate analysis metrics with PCR-based and null packet tracking.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct BitrateAnalysis(
    long TsBitrateNominal,
    long TsBitratePcr,
    long TsBitrateDts,
    double BitrateAccuracy,
    long NullPacketBitrate,
    double NullPacketRatio,
    long UsefulBitrate
)
{
    /// <summary>
    /// Gets a value indicating whether null packet ratio is high (>30% stuffing).
    /// </summary>
    public bool HasHighNullRatio => NullPacketRatio > 0.3;

    /// <summary>
    /// Gets the bandwidth utilization (1.0 - null packet ratio).
    /// </summary>
    public double BandwidthUtilization => 1.0 - NullPacketRatio;
}

/// <summary>
/// Extended per-PID information from native TsDuck analysis (Phase 2b).
/// Provides detailed per-PID statistics including continuity errors, scrambling, and stream type.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct TsDuckPidInfoExtended(
    int Pid,
    int StreamType,
    long Packets,
    long Bitrate,
    long ContinuityErrors,
    long DuplicatePackets,
    long ScrambledPackets,
    bool IsScrambled,
    bool IsPcrPid,
    double PcrJitterUs,
    bool IsVideo,
    bool IsAudio
)
{
    /// <summary>
    /// Gets a value indicating whether this PID has continuity errors.
    /// </summary>
    public bool HasContinuityErrors => ContinuityErrors > 0;

    /// <summary>
    /// Gets a value indicating whether this PID is currently scrambled.
    /// </summary>
    public bool HasScrambling => IsScrambled || ScrambledPackets > 0;

    /// <summary>
    /// Gets a value indicating whether this is a media PID (video or audio).
    /// </summary>
    public bool IsMediaPid => IsVideo || IsAudio;

    /// <summary>
    /// Gets a value indicating whether this PID has PCR jitter issues.
    /// </summary>
    public bool HasPcrJitterIssues => IsPcrPid && PcrJitterUs > 0.5;
}

/// <summary>
/// Per-PID statistics from TSDuck analysis.
/// </summary>
public readonly record struct TsDuckPidInfo(
    int Pid,
    string Description,
    long Bitrate,
    long Packets,
    long ContinuityErrors
);

/// <summary>
/// Per-service statistics from TSDuck analysis.
/// </summary>
public readonly record struct TsDuckServiceInfo(int ServiceId, string? Name, int PmtPid, int PcrPid, long Bitrate);

/// <summary>
/// Aggregated metrics from TSDuck analysis.
/// </summary>
public sealed class TsDuckMetrics
{
    /// <summary>
    /// Gets or sets the timestamp when metrics were captured.
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets the age of these metrics since they were captured.
    /// </summary>
    public TimeSpan Age => DateTime.UtcNow - Timestamp;

    /// <summary>
    /// Gets a value indicating whether these metrics are stale (older than 5 seconds).
    /// </summary>
    /// <remarks>
    /// Consumers should treat stale metrics with caution as they may not reflect
    /// the current stream state.
    /// </remarks>
    public bool IsStale => Age.TotalSeconds > 5;

    /// <summary>
    /// Gets or sets the total transport stream bitrate in bits per second.
    /// </summary>
    public long TsBitrate { get; set; }

    /// <summary>
    /// Gets or sets the number of detected services.
    /// </summary>
    public int ServiceCount { get; set; }

    /// <summary>
    /// Gets or sets the number of detected PIDs.
    /// </summary>
    public int PidCount { get; set; }

    /// <summary>
    /// Gets or sets the TR 101 290 Priority 1 indicators.
    /// </summary>
    public Tr101290Priority1 Priority1 { get; set; }

    /// <summary>
    /// Gets or sets the TR 101 290 Priority 2 indicators.
    /// </summary>
    public Tr101290Priority2 Priority2 { get; set; }

    /// <summary>
    /// Gets or sets the per-service information.
    /// </summary>
    public IReadOnlyList<TsDuckServiceInfo> Services { get; set; } = [];

    /// <summary>
    /// Gets or sets the per-PID information.
    /// </summary>
    public IReadOnlyList<TsDuckPidInfo> Pids { get; set; } = [];

    /// <summary>
    /// Gets or sets the PCR analysis metrics.
    /// </summary>
    /// <remarks>
    /// Populated when PCR analysis is enabled in configuration.
    /// Provides detailed timing metrics for decoder buffer management.
    /// </remarks>
    public PcrAnalysis? PcrAnalysis { get; set; }

    /// <summary>
    /// Gets or sets the IAT (Inter-packet Arrival Time) analysis metrics.
    /// </summary>
    /// <remarks>
    /// Populated when IAT analysis is enabled in configuration.
    /// Provides network jitter metrics for UDP/IP streams.
    /// </remarks>
    public IatAnalysis? IatAnalysis { get; set; }

    /// <summary>
    /// Gets or sets the bitrate analysis metrics.
    /// </summary>
    /// <remarks>
    /// Populated when bitrate analysis is enabled in configuration.
    /// Provides detailed bitrate breakdown including null packet ratio.
    /// </remarks>
    public BitrateAnalysis? BitrateAnalysis { get; set; }

    /// <summary>
    /// Gets or sets the extended per-PID information.
    /// </summary>
    /// <remarks>
    /// Populated from native analyzer when available.
    /// Provides detailed per-PID metrics including continuity errors, scrambling, and stream types.
    /// </remarks>
    public IReadOnlyList<TsDuckPidInfoExtended> PidsExtended { get; set; } = [];

    // Quality score caching - scores are computed once and cached
    private int? _cachedQualityScore;
    private int? _cachedEnhancedScore;

    /// <summary>
    /// Calculates a quality score from 0-100 based on TR 101 290 error indicators only.
    /// </summary>
    /// <returns>Quality score where 100 is perfect.</returns>
    /// <remarks>
    /// The result is cached for performance. For enhanced scoring including PCR and IAT analysis,
    /// use <see cref="CalculateQualityScoreEnhanced"/>.
    /// </remarks>
    public int CalculateQualityScore()
    {
        if (_cachedQualityScore.HasValue)
        {
            return _cachedQualityScore.Value;
        }

        var score = 100;

        // Priority 1 errors are critical - heavy penalty
        if (Priority1.SyncLoss > 0)
        {
            score -= 50;
        }

        if (Priority1.SyncByteError > 0)
        {
            score -= 30;
        }

        if (Priority1.PatError > 0 || Priority1.PatError2 > 0)
        {
            score -= 20;
        }

        if (Priority1.ContinuityCountError > 0)
        {
            score -= Math.Min(20, (int)(Priority1.ContinuityCountError / 10));
        }

        if (Priority1.PmtError > 0 || Priority1.PmtError2 > 0)
        {
            score -= 15;
        }

        if (Priority1.PidError > 0)
        {
            score -= 10;
        }

        // Priority 2 errors are important - moderate penalty
        if (Priority2.TransportError > 0)
        {
            score -= Math.Min(15, (int)(Priority2.TransportError / 5));
        }

        if (Priority2.CrcError > 0)
        {
            score -= 10;
        }

        if (Priority2.PcrAccuracyError > 0)
        {
            score -= Math.Min(10, (int)(Priority2.PcrAccuracyError / 3));
        }

        if (Priority2.PtsError > 0)
        {
            score -= 5;
        }

        _cachedQualityScore = Math.Max(0, score);
        return _cachedQualityScore.Value;
    }

    /// <summary>
    /// Calculates an enhanced quality score from 0-100 including PCR and IAT analysis.
    /// </summary>
    /// <returns>Quality score where 100 is perfect.</returns>
    /// <remarks>
    /// The result is cached for performance.
    /// This method extends <see cref="CalculateQualityScore"/> with additional penalties for:
    /// <list type="bullet">
    /// <item>PCR jitter exceeding TR 101 290 threshold (500ns)</item>
    /// <item>PCR interval exceeding TR 101 290 threshold (100ms)</item>
    /// <item>PCR drift indicating clock synchronization issues</item>
    /// <item>IAT jitter indicating network congestion</item>
    /// <item>Late packet delivery</item>
    /// <item>High null packet ratio indicating bandwidth issues</item>
    /// </list>
    /// </remarks>
    public int CalculateQualityScoreEnhanced()
    {
        if (_cachedEnhancedScore.HasValue)
        {
            return _cachedEnhancedScore.Value;
        }

        var score = CalculateQualityScore();

        // PCR Analysis penalties
        // Constants from PcrAnalysis struct (TR 101 290 thresholds)
        const double pcrJitterThresholdUs = 0.5; // PcrAnalysis.JitterThresholdUs
        const double pcrIntervalThresholdMs = 100.0; // PcrAnalysis.IntervalThresholdMs
        const double iatJitterThresholdUs = 1000.0; // IatAnalysis.JitterThresholdUs
        const int iatBurstThreshold = 10; // IatAnalysis.BurstThreshold

        if (PcrAnalysis.HasValue)
        {
            var pcr = PcrAnalysis.Value;

            // PCR jitter >500ns (0.5us) violates TR 101 290
            if (pcr.PcrJitterUs > pcrJitterThresholdUs)
            {
                score -= Math.Min(15, (int)((pcr.PcrJitterUs - pcrJitterThresholdUs) / 0.1));
            }

            // PCR interval >100ms violates TR 101 290
            if (pcr.PcrIntervalMs > pcrIntervalThresholdMs)
            {
                score -= Math.Min(10, (int)((pcr.PcrIntervalMs - pcrIntervalThresholdMs) / 10));
            }

            // PCR drift >100ppm indicates clock sync issues
            if (Math.Abs(pcr.PcrDriftPpm) > 100)
            {
                score -= Math.Min(10, (int)(Math.Abs(pcr.PcrDriftPpm) / 50));
            }
        }

        // IAT Analysis penalties
        if (IatAnalysis.HasValue)
        {
            var iat = IatAnalysis.Value;

            // IAT jitter >1ms indicates network congestion
            if (iat.IatJitterUs > iatJitterThresholdUs)
            {
                score -= Math.Min(10, (int)((iat.IatJitterUs - iatJitterThresholdUs) / 500));
            }

            // Late packets indicate delivery issues
            if (iat.LatePackets > 100)
            {
                score -= Math.Min(5, (int)(iat.LatePackets / 50));
            }

            // Packet bursting indicates network instability
            if (iat.BurstCount > iatBurstThreshold)
            {
                score -= Math.Min(5, (int)(iat.BurstCount - iatBurstThreshold));
            }
        }

        // Bitrate Analysis penalties
        if (BitrateAnalysis.HasValue)
        {
            var bitrate = BitrateAnalysis.Value;

            // High null packet ratio (>30%) indicates bandwidth underutilization
            if (bitrate.HasHighNullRatio)
            {
                score -= 5;
            }
        }

        _cachedEnhancedScore = Math.Max(0, score);
        return _cachedEnhancedScore.Value;
    }
}

/// <summary>
/// Event arguments for TSDuck metrics updates.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="TsDuckMetricsEventArgs"/> class.
/// </remarks>
/// <param name="metrics">The updated metrics.</param>
public sealed class TsDuckMetricsEventArgs(TsDuckMetrics metrics) : EventArgs
{
    /// <summary>
    /// Gets the updated metrics.
    /// </summary>
    public TsDuckMetrics Metrics { get; } = metrics;
}

/// <summary>
/// Thread-safe ring buffer for storing metrics history and computing trends.
/// Enables time-windowed aggregation and error rate calculation.
/// </summary>
/// <remarks>
/// The buffer stores a fixed number of snapshots (default 60) representing
/// approximately 1 minute of history at 1-second intervals.
/// </remarks>
public sealed class TsDuckMetricsHistory
{
    private readonly TsDuckMetrics?[] _buffer;
    private readonly object _lock = new();
    private int _head;
    private int _count;

    /// <summary>
    /// Default capacity - stores ~1 minute of history at 1-second intervals.
    /// </summary>
    public const int DefaultCapacity = 60;

    /// <summary>
    /// Initializes a new instance of the <see cref="TsDuckMetricsHistory"/> class.
    /// </summary>
    /// <param name="capacity">Maximum number of snapshots to retain.</param>
    public TsDuckMetricsHistory(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        _buffer = new TsDuckMetrics?[capacity];
    }

    /// <summary>
    /// Gets the maximum capacity of the history buffer.
    /// </summary>
    public int Capacity => _buffer.Length;

    /// <summary>
    /// Gets the current number of stored snapshots.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _count;
            }
        }
    }

    /// <summary>
    /// Adds a metrics snapshot to the history.
    /// </summary>
    /// <param name="metrics">The metrics to add.</param>
    public void Add(TsDuckMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        lock (_lock)
        {
            _buffer[_head] = metrics;
            _head = (_head + 1) % _buffer.Length;
            if (_count < _buffer.Length)
            {
                _count++;
            }
        }
    }

    /// <summary>
    /// Gets the most recent metrics snapshot.
    /// </summary>
    /// <returns>The latest metrics, or null if empty.</returns>
    public TsDuckMetrics? GetLatest()
    {
        lock (_lock)
        {
            if (_count == 0)
            {
                return null;
            }

            var index = (_head - 1 + _buffer.Length) % _buffer.Length;
            return _buffer[index];
        }
    }

    /// <summary>
    /// Gets all snapshots within a time window.
    /// </summary>
    /// <param name="window">Time window to retrieve.</param>
    /// <returns>List of snapshots within the window, ordered oldest to newest.</returns>
    public IReadOnlyList<TsDuckMetrics> GetWindow(TimeSpan window)
    {
        var cutoff = DateTime.UtcNow - window;
        var result = new List<TsDuckMetrics>();

        lock (_lock)
        {
            for (var i = 0; i < _count; i++)
            {
                var index = (_head - _count + i + _buffer.Length) % _buffer.Length;
                var snapshot = _buffer[index];
                if (snapshot is not null && snapshot.Timestamp >= cutoff)
                {
                    result.Add(snapshot);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Calculates the error rate (errors per second) over a time window.
    /// </summary>
    /// <param name="window">Time window for calculation.</param>
    /// <returns>Error rate statistics, or null if insufficient data.</returns>
    public ErrorRateStatistics? CalculateErrorRate(TimeSpan window)
    {
        var snapshots = GetWindow(window);
        if (snapshots.Count < 2)
        {
            return null;
        }

        var first = snapshots[0];
        var last = snapshots[^1];
        var elapsed = (last.Timestamp - first.Timestamp).TotalSeconds;

        if (elapsed <= 0)
        {
            return null;
        }

        var p1ErrorDelta = last.Priority1.TotalErrors - first.Priority1.TotalErrors;
        var p2ErrorDelta = last.Priority2.TotalErrors - first.Priority2.TotalErrors;

        return new ErrorRateStatistics(
            Priority1ErrorsPerSecond: p1ErrorDelta / elapsed,
            Priority2ErrorsPerSecond: p2ErrorDelta / elapsed,
            TotalErrorsPerSecond: (p1ErrorDelta + p2ErrorDelta) / elapsed,
            WindowSeconds: elapsed,
            SampleCount: snapshots.Count,
            AverageQualityScore: snapshots.Average(s => s.CalculateQualityScore()),
            MinQualityScore: snapshots.Min(s => s.CalculateQualityScore()),
            MaxQualityScore: snapshots.Max(s => s.CalculateQualityScore())
        );
    }

    /// <summary>
    /// Calculates trend direction for the quality score over a time window.
    /// </summary>
    /// <param name="window">Time window for trend analysis.</param>
    /// <returns>Trend direction: positive = improving, negative = degrading, 0 = stable.</returns>
    public double CalculateQualityTrend(TimeSpan window)
    {
        var snapshots = GetWindow(window);
        if (snapshots.Count < 3)
        {
            return 0;
        }

        // Simple linear regression slope on quality scores
        var n = snapshots.Count;
        double sumX = 0,
            sumY = 0,
            sumXY = 0,
            sumX2 = 0;

        for (var i = 0; i < n; i++)
        {
            var x = i;
            var y = snapshots[i].CalculateQualityScore();
            sumX += x;
            sumY += y;
            sumXY += x * y;
            sumX2 += x * x;
        }

        var denominator = (n * sumX2) - (sumX * sumX);
        return Math.Abs(denominator) < 0.0001 ? 0 : ((n * sumXY) - (sumX * sumY)) / denominator;
    }

    /// <summary>
    /// Clears all stored history.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_buffer);
            _head = 0;
            _count = 0;
        }
    }
}

/// <summary>
/// Error rate statistics calculated over a time window.
/// </summary>
/// <param name="Priority1ErrorsPerSecond">TR 101 290 Priority 1 errors per second.</param>
/// <param name="Priority2ErrorsPerSecond">TR 101 290 Priority 2 errors per second.</param>
/// <param name="TotalErrorsPerSecond">Total errors per second.</param>
/// <param name="WindowSeconds">Duration of the analysis window.</param>
/// <param name="SampleCount">Number of samples in the window.</param>
/// <param name="AverageQualityScore">Average quality score in the window.</param>
/// <param name="MinQualityScore">Minimum quality score in the window.</param>
/// <param name="MaxQualityScore">Maximum quality score in the window.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct ErrorRateStatistics(
    double Priority1ErrorsPerSecond,
    double Priority2ErrorsPerSecond,
    double TotalErrorsPerSecond,
    double WindowSeconds,
    int SampleCount,
    double AverageQualityScore,
    int MinQualityScore,
    int MaxQualityScore
)
{
    /// <summary>
    /// Gets a value indicating whether the stream is healthy (low error rate).
    /// </summary>
    /// <remarks>
    /// A stream is considered healthy if Priority 1 errors are rare (less than 0.1/sec).
    /// </remarks>
    public bool IsHealthy => Priority1ErrorsPerSecond < 0.1;

    /// <summary>
    /// Gets a value indicating whether the stream is degrading (quality trend negative).
    /// </summary>
    public bool IsDegrading => MinQualityScore < AverageQualityScore - 10;
}
