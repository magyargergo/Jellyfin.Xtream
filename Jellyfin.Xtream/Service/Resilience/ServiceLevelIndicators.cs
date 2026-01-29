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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Jellyfin.Xtream.Service.Resilience;

/// <summary>
/// Service Level Indicators (SLIs) for stream health monitoring.
/// Thread-safe implementation for concurrent access from multiple streams.
/// </summary>
public sealed class ServiceLevelIndicators
{
    private long _totalStreamRequests;
    private long _successfulStreamStarts;
    private long _failedStreamStarts;
    private long _streamMinutesRequested;
    private long _streamMinutesDelivered;
    private long _totalFailovers;
    private long _successfulFailovers;
    private long _totalRebufferingMs;
    private long _totalPlaybackMs;
    private long _tr101290Priority1Errors;
    private long _tr101290Priority2Errors;

    private readonly ConcurrentQueue<TimedValue<double>> _streamStartTimes = new();
    private readonly ConcurrentQueue<TimedValue<double>> _failoverTimes = new();
    private readonly ConcurrentQueue<TimedValue<int>> _qualityScores = new();

    private const int MaxSamples = 1000;

    /// <summary>
    /// Gets the total number of stream requests.
    /// </summary>
    public long TotalStreamRequests => Interlocked.Read(ref _totalStreamRequests);

    /// <summary>
    /// Gets the number of successful stream starts.
    /// </summary>
    public long SuccessfulStreamStarts => Interlocked.Read(ref _successfulStreamStarts);

    /// <summary>
    /// Gets the number of failed stream starts.
    /// </summary>
    public long FailedStreamStarts => Interlocked.Read(ref _failedStreamStarts);

    /// <summary>
    /// Gets the total stream minutes requested.
    /// </summary>
    public long StreamMinutesRequested => Interlocked.Read(ref _streamMinutesRequested);

    /// <summary>
    /// Gets the total stream minutes successfully delivered.
    /// </summary>
    public long StreamMinutesDelivered => Interlocked.Read(ref _streamMinutesDelivered);

    /// <summary>
    /// Gets the total number of failover attempts.
    /// </summary>
    public long TotalFailovers => Interlocked.Read(ref _totalFailovers);

    /// <summary>
    /// Gets the number of successful failovers.
    /// </summary>
    public long SuccessfulFailovers => Interlocked.Read(ref _successfulFailovers);

    /// <summary>
    /// Gets the TR 101 290 Priority 1 error count.
    /// </summary>
    public long Tr101290Priority1Errors => Interlocked.Read(ref _tr101290Priority1Errors);

    /// <summary>
    /// Gets the TR 101 290 Priority 2 error count.
    /// </summary>
    public long Tr101290Priority2Errors => Interlocked.Read(ref _tr101290Priority2Errors);

    /// <summary>
    /// Records a stream request.
    /// </summary>
    public void RecordStreamRequest() => Interlocked.Increment(ref _totalStreamRequests);

    /// <summary>
    /// Records a successful stream start with the startup duration.
    /// </summary>
    /// <param name="startupDuration">Time from request to first frame.</param>
    public void RecordStreamStartSuccess(TimeSpan startupDuration)
    {
        Interlocked.Increment(ref _successfulStreamStarts);
        AddTimedSample(_streamStartTimes, startupDuration.TotalSeconds);
    }

    /// <summary>
    /// Records a failed stream start.
    /// </summary>
    /// <param name="reason">The failure reason.</param>
    public void RecordStreamStartFailure(string reason)
    {
        Interlocked.Increment(ref _failedStreamStarts);
    }

    /// <summary>
    /// Records stream minutes for availability calculation.
    /// </summary>
    /// <param name="minutesRequested">Minutes the user wanted to watch.</param>
    /// <param name="minutesDelivered">Minutes actually delivered without issues.</param>
    public void RecordStreamMinutes(long minutesRequested, long minutesDelivered)
    {
        Interlocked.Add(ref _streamMinutesRequested, minutesRequested);
        Interlocked.Add(ref _streamMinutesDelivered, minutesDelivered);
    }

    /// <summary>
    /// Records a failover attempt and its result.
    /// </summary>
    /// <param name="success">Whether the failover succeeded.</param>
    /// <param name="duration">Time taken for the failover.</param>
    public void RecordFailover(bool success, TimeSpan duration)
    {
        Interlocked.Increment(ref _totalFailovers);
        if (success)
        {
            Interlocked.Increment(ref _successfulFailovers);
            AddTimedSample(_failoverTimes, duration.TotalSeconds);
        }
    }

    /// <summary>
    /// Records rebuffering time for rebuffering ratio calculation.
    /// </summary>
    /// <param name="rebufferingMs">Time spent rebuffering in milliseconds.</param>
    /// <param name="playbackMs">Total playback time in milliseconds.</param>
    public void RecordRebuffering(long rebufferingMs, long playbackMs)
    {
        Interlocked.Add(ref _totalRebufferingMs, rebufferingMs);
        Interlocked.Add(ref _totalPlaybackMs, playbackMs);
    }

    /// <summary>
    /// Records a quality score sample.
    /// </summary>
    /// <param name="score">Quality score from 0-100.</param>
    public void RecordQualityScore(int score)
    {
        AddTimedSample(_qualityScores, score);
    }

    /// <summary>
    /// Records TR 101 290 errors.
    /// </summary>
    /// <param name="priority1Errors">Priority 1 error count.</param>
    /// <param name="priority2Errors">Priority 2 error count.</param>
    public void RecordTr101290Errors(long priority1Errors, long priority2Errors)
    {
        Interlocked.Add(ref _tr101290Priority1Errors, priority1Errors);
        Interlocked.Add(ref _tr101290Priority2Errors, priority2Errors);
    }

    /// <summary>
    /// Calculates the availability SLO compliance percentage.
    /// </summary>
    /// <returns>Availability percentage (0-100).</returns>
    public double CalculateAvailability()
    {
        var requested = Interlocked.Read(ref _streamMinutesRequested);
        var delivered = Interlocked.Read(ref _streamMinutesDelivered);
        return requested > 0 ? (double)delivered / requested * 100.0 : 100.0;
    }

    /// <summary>
    /// Calculates the rebuffering ratio percentage.
    /// </summary>
    /// <returns>Rebuffering ratio (0-100).</returns>
    public double CalculateRebufferingRatio()
    {
        var rebuffering = Interlocked.Read(ref _totalRebufferingMs);
        var playback = Interlocked.Read(ref _totalPlaybackMs);
        return playback > 0 ? (double)rebuffering / playback * 100.0 : 0.0;
    }

    /// <summary>
    /// Calculates stream start time percentiles.
    /// </summary>
    /// <returns>Percentile values (P50, P95, P99) in seconds.</returns>
    public PercentileResult CalculateStreamStartTimePercentiles()
    {
        return CalculatePercentiles(_streamStartTimes);
    }

    /// <summary>
    /// Calculates failover time percentiles.
    /// </summary>
    /// <returns>Percentile values (P50, P95, P99) in seconds.</returns>
    public PercentileResult CalculateFailoverTimePercentiles()
    {
        return CalculatePercentiles(_failoverTimes);
    }

    /// <summary>
    /// Calculates the average quality score.
    /// </summary>
    /// <returns>Average quality score (0-100).</returns>
    public double CalculateAverageQualityScore()
    {
        var samples = _qualityScores.ToArray();
        return samples.Length > 0 ? samples.Average(s => s.Value) : 0.0;
    }

    /// <summary>
    /// Gets a complete SLO compliance snapshot.
    /// </summary>
    /// <returns>Current SLO status.</returns>
    public SloSnapshot GetSnapshot()
    {
        var startTimePercentiles = CalculateStreamStartTimePercentiles();
        var failoverPercentiles = CalculateFailoverTimePercentiles();

        return new SloSnapshot
        {
            Timestamp = DateTime.UtcNow,
            TotalStreamRequests = TotalStreamRequests,
            SuccessfulStreamStarts = SuccessfulStreamStarts,
            FailedStreamStarts = FailedStreamStarts,
            AvailabilityPercent = CalculateAvailability(),
            StreamStartTimeP50Seconds = startTimePercentiles.P50,
            StreamStartTimeP95Seconds = startTimePercentiles.P95,
            StreamStartTimeP99Seconds = startTimePercentiles.P99,
            FailoverTimeP95Seconds = failoverPercentiles.P95,
            RebufferingRatioPercent = CalculateRebufferingRatio(),
            AverageQualityScore = CalculateAverageQualityScore(),
            TotalFailovers = TotalFailovers,
            SuccessfulFailovers = SuccessfulFailovers,
            Tr101290Priority1Errors = Tr101290Priority1Errors,
            Tr101290Priority2Errors = Tr101290Priority2Errors,
        };
    }

    /// <summary>
    /// Checks if the current metrics are within the error budget for a given SLO target.
    /// </summary>
    /// <param name="availabilityTarget">Target availability percentage (e.g., 99.9).</param>
    /// <returns>Error budget status.</returns>
    public ErrorBudgetStatus CheckErrorBudget(double availabilityTarget = 99.9)
    {
        var availability = CalculateAvailability();
        var targetMinutes = Interlocked.Read(ref _streamMinutesRequested);
        var allowedDowntimeMinutes = targetMinutes * (100.0 - availabilityTarget) / 100.0;
        var actualDowntimeMinutes = targetMinutes - Interlocked.Read(ref _streamMinutesDelivered);
        var budgetConsumedPercent =
            allowedDowntimeMinutes > 0 ? actualDowntimeMinutes / allowedDowntimeMinutes * 100.0 : 0.0;

        return new ErrorBudgetStatus
        {
            TargetAvailability = availabilityTarget,
            CurrentAvailability = availability,
            AllowedDowntimeMinutes = allowedDowntimeMinutes,
            ActualDowntimeMinutes = actualDowntimeMinutes,
            BudgetConsumedPercent = budgetConsumedPercent,
            BudgetRemainingPercent = Math.Max(0, 100.0 - budgetConsumedPercent),
            Status = GetBudgetStatus(budgetConsumedPercent),
        };
    }

    /// <summary>
    /// Resets all metrics. Use with caution - typically for testing or period reset.
    /// </summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _totalStreamRequests, 0);
        Interlocked.Exchange(ref _successfulStreamStarts, 0);
        Interlocked.Exchange(ref _failedStreamStarts, 0);
        Interlocked.Exchange(ref _streamMinutesRequested, 0);
        Interlocked.Exchange(ref _streamMinutesDelivered, 0);
        Interlocked.Exchange(ref _totalFailovers, 0);
        Interlocked.Exchange(ref _successfulFailovers, 0);
        Interlocked.Exchange(ref _totalRebufferingMs, 0);
        Interlocked.Exchange(ref _totalPlaybackMs, 0);
        Interlocked.Exchange(ref _tr101290Priority1Errors, 0);
        Interlocked.Exchange(ref _tr101290Priority2Errors, 0);

        while (_streamStartTimes.TryDequeue(out _)) { }

        while (_failoverTimes.TryDequeue(out _)) { }

        while (_qualityScores.TryDequeue(out _)) { }
    }

    private static void AddTimedSample<T>(ConcurrentQueue<TimedValue<T>> queue, T value)
    {
        queue.Enqueue(new TimedValue<T>(DateTime.UtcNow, value));

        // Trim old samples
        while (queue.Count > MaxSamples && queue.TryDequeue(out _)) { }
    }

    private static PercentileResult CalculatePercentiles(ConcurrentQueue<TimedValue<double>> queue)
    {
        var values = queue.Select(tv => tv.Value).OrderBy(v => v).ToList();
        if (values.Count == 0)
        {
            return new PercentileResult(0, 0, 0);
        }

        return new PercentileResult(
            P50: GetPercentile(values, 50),
            P95: GetPercentile(values, 95),
            P99: GetPercentile(values, 99)
        );
    }

    private static double GetPercentile(List<double> sortedValues, int percentile)
    {
        if (sortedValues.Count == 0)
        {
            return 0;
        }

        var index = (int)Math.Ceiling(percentile / 100.0 * sortedValues.Count) - 1;
        return sortedValues[Math.Max(0, Math.Min(index, sortedValues.Count - 1))];
    }

    private static ErrorBudgetLevel GetBudgetStatus(double consumed)
    {
        return consumed switch
        {
            < 50 => ErrorBudgetLevel.Healthy,
            < 75 => ErrorBudgetLevel.Warning,
            < 90 => ErrorBudgetLevel.Critical,
            < 100 => ErrorBudgetLevel.Exhausting,
            _ => ErrorBudgetLevel.Exhausted,
        };
    }
}

/// <summary>
/// A timestamped value for time-series tracking.
/// </summary>
/// <typeparam name="T">The value type.</typeparam>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
public readonly record struct TimedValue<T>(DateTime Timestamp, T Value);

/// <summary>
/// Percentile calculation results.
/// </summary>
/// <param name="P50">50th percentile (median).</param>
/// <param name="P95">95th percentile.</param>
/// <param name="P99">99th percentile.</param>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
public readonly record struct PercentileResult(double P50, double P95, double P99);

/// <summary>
/// Complete SLO snapshot for reporting.
/// </summary>
public sealed record SloSnapshot
{
    /// <summary>
    /// Gets the timestamp when this snapshot was taken.
    /// </summary>
    public required DateTime Timestamp { get; init; }

    /// <summary>
    /// Gets the total number of stream requests.
    /// </summary>
    public required long TotalStreamRequests { get; init; }

    /// <summary>
    /// Gets the number of successful stream starts.
    /// </summary>
    public required long SuccessfulStreamStarts { get; init; }

    /// <summary>
    /// Gets the number of failed stream starts.
    /// </summary>
    public required long FailedStreamStarts { get; init; }

    /// <summary>
    /// Gets the availability percentage.
    /// </summary>
    public required double AvailabilityPercent { get; init; }

    /// <summary>
    /// Gets the P50 stream start time in seconds.
    /// </summary>
    public required double StreamStartTimeP50Seconds { get; init; }

    /// <summary>
    /// Gets the P95 stream start time in seconds.
    /// </summary>
    public required double StreamStartTimeP95Seconds { get; init; }

    /// <summary>
    /// Gets the P99 stream start time in seconds.
    /// </summary>
    public required double StreamStartTimeP99Seconds { get; init; }

    /// <summary>
    /// Gets the P95 failover time in seconds.
    /// </summary>
    public required double FailoverTimeP95Seconds { get; init; }

    /// <summary>
    /// Gets the rebuffering ratio percentage.
    /// </summary>
    public required double RebufferingRatioPercent { get; init; }

    /// <summary>
    /// Gets the average quality score.
    /// </summary>
    public required double AverageQualityScore { get; init; }

    /// <summary>
    /// Gets the total number of failover attempts.
    /// </summary>
    public required long TotalFailovers { get; init; }

    /// <summary>
    /// Gets the number of successful failovers.
    /// </summary>
    public required long SuccessfulFailovers { get; init; }

    /// <summary>
    /// Gets the TR 101 290 Priority 1 error count.
    /// </summary>
    public required long Tr101290Priority1Errors { get; init; }

    /// <summary>
    /// Gets the TR 101 290 Priority 2 error count.
    /// </summary>
    public required long Tr101290Priority2Errors { get; init; }
}

/// <summary>
/// Error budget status for SLO compliance tracking.
/// </summary>
public sealed record ErrorBudgetStatus
{
    /// <summary>
    /// Gets the target availability percentage.
    /// </summary>
    public required double TargetAvailability { get; init; }

    /// <summary>
    /// Gets the current availability percentage.
    /// </summary>
    public required double CurrentAvailability { get; init; }

    /// <summary>
    /// Gets the allowed downtime in minutes based on the SLO target.
    /// </summary>
    public required double AllowedDowntimeMinutes { get; init; }

    /// <summary>
    /// Gets the actual downtime in minutes.
    /// </summary>
    public required double ActualDowntimeMinutes { get; init; }

    /// <summary>
    /// Gets the percentage of error budget consumed.
    /// </summary>
    public required double BudgetConsumedPercent { get; init; }

    /// <summary>
    /// Gets the percentage of error budget remaining.
    /// </summary>
    public required double BudgetRemainingPercent { get; init; }

    /// <summary>
    /// Gets the error budget status level.
    /// </summary>
    public required ErrorBudgetLevel Status { get; init; }
}

/// <summary>
/// Error budget consumption levels for operational decisions.
/// </summary>
public enum ErrorBudgetLevel
{
    /// <summary>
    /// Less than 50% consumed - normal operations.
    /// </summary>
    Healthy,

    /// <summary>
    /// 50-75% consumed - increased monitoring.
    /// </summary>
    Warning,

    /// <summary>
    /// 75-90% consumed - feature freeze recommended.
    /// </summary>
    Critical,

    /// <summary>
    /// 90-100% consumed - all hands on reliability.
    /// </summary>
    Exhausting,

    /// <summary>
    /// Over 100% consumed - SLO violated.
    /// </summary>
    Exhausted,
}
