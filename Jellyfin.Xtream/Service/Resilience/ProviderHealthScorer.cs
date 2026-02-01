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
/// Scores providers based on historical performance for intelligent provider selection.
/// Uses a sliding window approach to weight recent performance more heavily.
/// </summary>
public sealed class ProviderHealthScorer
{
    private readonly ConcurrentDictionary<string, ProviderScoreData> _scores = new(StringComparer.Ordinal);
    private readonly TimeSpan _windowDuration;
    private readonly int _maxSamplesPerWindow;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderHealthScorer"/> class.
    /// </summary>
    /// <param name="windowDuration">Duration of the sliding window for score calculation.</param>
    /// <param name="maxSamplesPerWindow">Maximum samples to retain per window.</param>
    public ProviderHealthScorer(TimeSpan? windowDuration = null, int maxSamplesPerWindow = 100)
    {
        _windowDuration = windowDuration ?? TimeSpan.FromMinutes(5);
        _maxSamplesPerWindow = maxSamplesPerWindow;
    }

    /// <summary>
    /// Gets the health score for a provider (0-100).
    /// Unknown providers receive a neutral score of 50.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>Health score from 0 (worst) to 100 (best).</returns>
    public double GetScore(string providerId)
    {
        return _scores.TryGetValue(providerId, out var data) ? data.ComputeScore(_windowDuration) : 50.0; // Neutral score for unknown providers
    }

    /// <summary>
    /// Gets the health status for a provider.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>Health status including score and contributing factors.</returns>
    public ProviderHealthStatus GetStatus(string providerId)
    {
        if (!_scores.TryGetValue(providerId, out var data))
        {
            return new ProviderHealthStatus
            {
                ProviderId = providerId,
                Score = 50.0,
                Level = HealthLevel.Unknown,
                ConnectionSuccessRate = 0,
                AverageQualityScore = 0,
                AverageConnectionTimeMs = 0,
                ConsecutiveFailures = 0,
                LastSuccessTime = null,
                LastFailureTime = null,
                SampleCount = 0,
            };
        }

        var score = data.ComputeScore(_windowDuration);
        var metrics = data.GetMetrics(_windowDuration);

        return new ProviderHealthStatus
        {
            ProviderId = providerId,
            Score = score,
            Level = ClassifyHealthLevel(score),
            ConnectionSuccessRate = metrics.SuccessRate,
            AverageQualityScore = metrics.AverageQuality,
            AverageConnectionTimeMs = metrics.AverageConnectionTimeMs,
            ConsecutiveFailures = data.ConsecutiveFailures,
            LastSuccessTime = data.LastSuccessTime,
            LastFailureTime = data.LastFailureTime,
            SampleCount = metrics.SampleCount,
        };
    }

    /// <summary>
    /// Records a successful stream operation.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="qualityScore">Quality score of the stream (0-100).</param>
    /// <param name="connectionTimeMs">Time to establish connection in milliseconds.</param>
    public void RecordSuccess(string providerId, int qualityScore, double connectionTimeMs)
    {
        var data = _scores.GetOrAdd(providerId, _ => new ProviderScoreData(_maxSamplesPerWindow));
        data.RecordSuccess(qualityScore, connectionTimeMs);
    }

    /// <summary>
    /// Records a failed stream operation.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="failureType">The type of failure.</param>
    public void RecordFailure(string providerId, FailureType failureType)
    {
        var data = _scores.GetOrAdd(providerId, _ => new ProviderScoreData(_maxSamplesPerWindow));
        data.RecordFailure(failureType);
    }

    /// <summary>
    /// Gets all provider IDs with recorded scores.
    /// </summary>
    /// <returns>Collection of provider IDs.</returns>
    public IEnumerable<string> GetTrackedProviders() => _scores.Keys;

    /// <summary>
    /// Gets providers ordered by health score (best first).
    /// </summary>
    /// <returns>Ordered list of provider IDs with their scores.</returns>
    public IReadOnlyList<(string ProviderId, double Score)> GetProvidersByScore()
    {
        return _scores
            .Select(kvp => (kvp.Key, Score: kvp.Value.ComputeScore(_windowDuration)))
            .OrderByDescending(x => x.Score)
            .ToList();
    }

    /// <summary>
    /// Determines if a provider switch would be beneficial.
    /// Uses hysteresis to prevent oscillation between providers.
    /// </summary>
    /// <param name="currentProviderId">Current provider ID.</param>
    /// <param name="candidateProviderId">Candidate provider ID to switch to.</param>
    /// <param name="hysteresisThreshold">Minimum score improvement required (default: 15).</param>
    /// <returns>True if switching would be beneficial.</returns>
    public bool ShouldSwitch(string currentProviderId, string candidateProviderId, double hysteresisThreshold = 15.0)
    {
        var currentScore = GetScore(currentProviderId);
        var candidateScore = GetScore(candidateProviderId);
        return candidateScore > currentScore + hysteresisThreshold;
    }

    /// <summary>
    /// Resets all scores. Typically used for testing.
    /// </summary>
    public void Reset() => _scores.Clear();

    private static HealthLevel ClassifyHealthLevel(double score)
    {
        return score switch
        {
            >= 90 => HealthLevel.Excellent,
            >= 70 => HealthLevel.Good,
            >= 50 => HealthLevel.Fair,
            >= 30 => HealthLevel.Poor,
            _ => HealthLevel.Critical,
        };
    }

    /// <summary>
    /// Internal data structure for tracking provider performance.
    /// </summary>
    private sealed class ProviderScoreData
    {
        private readonly ConcurrentQueue<PerformanceSample> _samples;
        private readonly int _maxSamples;
        private int _consecutiveFailures;

        public ProviderScoreData(int maxSamples)
        {
            _maxSamples = maxSamples;
            _samples = new ConcurrentQueue<PerformanceSample>();
        }

        public DateTime? LastSuccessTime { get; private set; }

        public DateTime? LastFailureTime { get; private set; }

        public int ConsecutiveFailures => Volatile.Read(ref _consecutiveFailures);

        public void RecordSuccess(int qualityScore, double connectionTimeMs)
        {
            _samples.Enqueue(
                new PerformanceSample(DateTime.UtcNow, Success: true, qualityScore, connectionTimeMs, FailureType: null)
            );

            LastSuccessTime = DateTime.UtcNow;
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            TrimSamples();
        }

        public void RecordFailure(FailureType failureType)
        {
            _samples.Enqueue(new PerformanceSample(DateTime.UtcNow, Success: false, 0, 0, failureType));

            LastFailureTime = DateTime.UtcNow;
            Interlocked.Increment(ref _consecutiveFailures);
            TrimSamples();
        }

        public double ComputeScore(TimeSpan window)
        {
            var cutoff = DateTime.UtcNow - window;
            var recentSamples = _samples.Where(s => s.Timestamp >= cutoff).ToList();

            if (recentSamples.Count == 0)
            {
                return 50.0; // Neutral score if no recent data
            }

            // Weight factors
            const double QualityWeight = 0.35;
            const double ReliabilityWeight = 0.35;
            const double LatencyWeight = 0.15;
            const double RecencyWeight = 0.15;

            // Quality score (average of successful stream quality)
            var successfulSamples = recentSamples.Where(s => s.Success).ToList();
            var qualityScore = successfulSamples.Count > 0 ? successfulSamples.Average(s => s.QualityScore) : 0.0;

            // Reliability score (success rate)
            var reliabilityScore = recentSamples.Count(s => s.Success) * 100.0 / recentSamples.Count;

            // Latency score (inverse of average connection time, normalized)
            var latencyScore = 100.0;
            if (successfulSamples.Count > 0)
            {
                var avgConnectionTime = successfulSamples.Average(s => s.ConnectionTimeMs);
                // Score decreases as latency increases: 100 at 0ms, 50 at 3000ms, 0 at 6000ms+
                latencyScore = Math.Max(0, 100 - (avgConnectionTime / 60.0));
            }

            // Recency penalty (consecutive failures penalize score)
            var recencyPenalty = Math.Min(30, ConsecutiveFailures * 10);

            var finalScore =
                (qualityScore * QualityWeight)
                + (reliabilityScore * ReliabilityWeight)
                + (latencyScore * LatencyWeight)
                + (100 * RecencyWeight)
                - // Base recency bonus
                recencyPenalty;

            return Math.Clamp(finalScore, 0, 100);
        }

        public ProviderMetrics GetMetrics(TimeSpan window)
        {
            var cutoff = DateTime.UtcNow - window;
            var recentSamples = _samples.Where(s => s.Timestamp >= cutoff).ToList();
            var successfulSamples = recentSamples.Where(s => s.Success).ToList();

            return new ProviderMetrics
            {
                SampleCount = recentSamples.Count,
                SuccessRate =
                    recentSamples.Count > 0 ? recentSamples.Count(s => s.Success) * 100.0 / recentSamples.Count : 0,
                AverageQuality = successfulSamples.Count > 0 ? successfulSamples.Average(s => s.QualityScore) : 0,
                AverageConnectionTimeMs =
                    successfulSamples.Count > 0 ? successfulSamples.Average(s => s.ConnectionTimeMs) : 0,
            };
        }

        private void TrimSamples()
        {
            while (_samples.Count > _maxSamples && _samples.TryDequeue(out _)) { }
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
    private readonly record struct PerformanceSample(
        DateTime Timestamp,
        bool Success,
        int QualityScore,
        double ConnectionTimeMs,
        FailureType? FailureType
    );

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
    private readonly record struct ProviderMetrics
    {
        public int SampleCount { get; init; }
        public double SuccessRate { get; init; }
        public double AverageQuality { get; init; }
        public double AverageConnectionTimeMs { get; init; }
    }
}

/// <summary>
/// Types of failures that can be recorded.
/// </summary>
public enum FailureType
{
    /// <summary>
    /// Connection timed out.
    /// </summary>
    ConnectionTimeout,

    /// <summary>
    /// HTTP error response (4xx, 5xx).
    /// </summary>
    HttpError,

    /// <summary>
    /// Data stall (no data received for extended period).
    /// </summary>
    DataStall,

    /// <summary>
    /// Quality degradation below threshold.
    /// </summary>
    QualityDegradation,

    /// <summary>
    /// Provider at connection capacity.
    /// </summary>
    CapacityExceeded,

    /// <summary>
    /// Authentication failure.
    /// </summary>
    AuthenticationFailed,

    /// <summary>
    /// Unknown or unclassified error.
    /// </summary>
    Unknown,
}

/// <summary>
/// Health level classification.
/// </summary>
public enum HealthLevel
{
    /// <summary>
    /// No data available for this provider.
    /// </summary>
    Unknown,

    /// <summary>
    /// Score below 30 - severe issues.
    /// </summary>
    Critical,

    /// <summary>
    /// Score 30-49 - significant issues.
    /// </summary>
    Poor,

    /// <summary>
    /// Score 50-69 - acceptable but not optimal.
    /// </summary>
    Fair,

    /// <summary>
    /// Score 70-89 - good performance.
    /// </summary>
    Good,

    /// <summary>
    /// Score 90+ - excellent performance.
    /// </summary>
    Excellent,
}

/// <summary>
/// Complete health status for a provider.
/// </summary>
public sealed record ProviderHealthStatus
{
    /// <summary>
    /// Gets the provider ID.
    /// </summary>
    public required string ProviderId { get; init; }

    /// <summary>
    /// Gets the health score (0-100).
    /// </summary>
    public required double Score { get; init; }

    /// <summary>
    /// Gets the health level classification.
    /// </summary>
    public required HealthLevel Level { get; init; }

    /// <summary>
    /// Gets the connection success rate percentage.
    /// </summary>
    public required double ConnectionSuccessRate { get; init; }

    /// <summary>
    /// Gets the average quality score of successful streams.
    /// </summary>
    public required double AverageQualityScore { get; init; }

    /// <summary>
    /// Gets the average connection time in milliseconds.
    /// </summary>
    public required double AverageConnectionTimeMs { get; init; }

    /// <summary>
    /// Gets the number of consecutive failures.
    /// </summary>
    public required int ConsecutiveFailures { get; init; }

    /// <summary>
    /// Gets the time of the last successful operation.
    /// </summary>
    public required DateTime? LastSuccessTime { get; init; }

    /// <summary>
    /// Gets the time of the last failed operation.
    /// </summary>
    public required DateTime? LastFailureTime { get; init; }

    /// <summary>
    /// Gets the number of samples used in the calculation.
    /// </summary>
    public required int SampleCount { get; init; }
}
