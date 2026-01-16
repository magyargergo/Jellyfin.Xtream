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
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Jellyfin.Xtream.Service.ProviderManagement;

/// <summary>
/// Tracks health score trends over time for predictive provider switching.
/// Uses exponential moving average and trend analysis to predict future health.
/// </summary>
public sealed class HealthTrendTracker : IHealthTrendTracker
{
    private const int MaxSamples = 30; // ~5 minutes at 10s intervals
    private const double AlphaFast = 0.3; // Fast EMA for recent changes
    private const double AlphaSlow = 0.1; // Slow EMA for baseline

    private readonly ConcurrentDictionary<string, ProviderTrendData> _trends = new(StringComparer.Ordinal);

    /// <summary>
    /// Records a health score sample for a provider.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="healthScore">The current health score (0-100).</param>
    public void RecordSample(string providerId, int healthScore)
    {
        var trend = _trends.GetOrAdd(providerId, _ => new ProviderTrendData());
        trend.AddSample(healthScore);
    }

    /// <summary>
    /// Gets the predicted health score for a provider based on trend analysis.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="secondsAhead">How far ahead to predict (default 60 seconds).</param>
    /// <returns>Predicted health score, or current score if insufficient data.</returns>
    public int GetPredictedScore(string providerId, int secondsAhead = 60)
    {
        if (!_trends.TryGetValue(providerId, out var trend))
        {
            return 50; // Neutral if no data
        }

        return trend.PredictScore(secondsAhead);
    }

    /// <summary>
    /// Gets the health trend direction for a provider.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>The trend direction.</returns>
    public HealthTrend GetTrend(string providerId) =>
        !_trends.TryGetValue(providerId, out var trend) ? HealthTrend.Stable : trend.GetTrend();

    /// <summary>
    /// Gets the trend snapshot for a provider.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>The trend snapshot, or default if no data.</returns>
    public HealthTrendSnapshot GetSnapshot(string providerId) =>
        !_trends.TryGetValue(providerId, out var trend) ? HealthTrendSnapshot.Default : trend.GetSnapshot();

    /// <summary>
    /// Gets all provider trends.
    /// </summary>
    /// <returns>Dictionary of provider IDs to trend snapshots.</returns>
    public IReadOnlyDictionary<string, HealthTrendSnapshot> GetAllSnapshots()
    {
        var result = new Dictionary<string, HealthTrendSnapshot>(StringComparer.Ordinal);
        foreach (var kvp in _trends)
        {
            result[kvp.Key] = kvp.Value.GetSnapshot();
        }

        return result;
    }

    /// <summary>
    /// Clears trend data for a specific provider.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    public void Clear(string providerId) => _trends.TryRemove(providerId, out _);

    /// <summary>
    /// Clears all trend data.
    /// </summary>
    public void ClearAll() => _trends.Clear();

    private sealed class ProviderTrendData
    {
        private readonly object _lock = new();
        private readonly Queue<TimestampedScore> _samples = new();
        private double _emaFast;
        private double _emaSlow;
        private double _velocity; // Score change per sample
        private int _lastScore;
        private DateTime _lastUpdate;
        private bool _initialized;

        public void AddSample(int score)
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                _samples.Enqueue(new TimestampedScore(score, now));

                // Trim old samples
                while (_samples.Count > MaxSamples)
                {
                    _ = _samples.Dequeue();
                }

                if (!_initialized)
                {
                    _emaFast = score;
                    _emaSlow = score;
                    _velocity = 0;
                    _initialized = true;
                }
                else
                {
                    // Update EMAs
                    _emaFast = (AlphaFast * score) + ((1 - AlphaFast) * _emaFast);
                    _emaSlow = (AlphaSlow * score) + ((1 - AlphaSlow) * _emaSlow);

                    // Calculate velocity (rate of change)
                    _velocity = (0.3 * (score - _lastScore)) + (0.7 * _velocity);
                }

                _lastScore = score;
                _lastUpdate = now;
            }
        }

        public int PredictScore(int secondsAhead)
        {
            lock (_lock)
            {
                if (!_initialized)
                {
                    return 50;
                }

                // Assume samples come every 10 seconds
                var samplesAhead = secondsAhead / 10.0;
                var predicted = _emaFast + (_velocity * samplesAhead);

                return Math.Clamp((int)predicted, 0, 100);
            }
        }

        public HealthTrend GetTrend()
        {
            lock (_lock)
            {
                if (!_initialized || _samples.Count < 3)
                {
                    return HealthTrend.Stable;
                }

                // Compare fast and slow EMAs
                var divergence = _emaFast - _emaSlow;

                if (divergence > 5 && _velocity > 0.5)
                {
                    return HealthTrend.Improving;
                }

                return divergence < -5 && _velocity < -0.5 ? HealthTrend.Degrading
                    : divergence < -10 && _velocity < -1.0 ? HealthTrend.RapidlyDegrading
                    : HealthTrend.Stable;
            }
        }

        public HealthTrendSnapshot GetSnapshot()
        {
            lock (_lock)
            {
                return new HealthTrendSnapshot
                {
                    CurrentScore = _lastScore,
                    FastEma = _emaFast,
                    SlowEma = _emaSlow,
                    Velocity = _velocity,
                    Trend = GetTrend(),
                    SampleCount = _samples.Count,
                    LastUpdate = _lastUpdate,
                    PredictedScore30s = PredictScore(30),
                    PredictedScore60s = PredictScore(60),
                };
            }
        }
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct TimestampedScore(int Score, DateTime Timestamp);
}

/// <summary>
/// Health trend direction.
/// </summary>
public enum HealthTrend
{
    /// <summary>Health is stable.</summary>
    Stable,

    /// <summary>Health is improving.</summary>
    Improving,

    /// <summary>Health is degrading.</summary>
    Degrading,

    /// <summary>Health is rapidly degrading (requires immediate action).</summary>
    RapidlyDegrading,
}

/// <summary>
/// Immutable snapshot of provider health trend data.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct HealthTrendSnapshot
{
    /// <summary>Gets the default (empty) snapshot.</summary>
    public static readonly HealthTrendSnapshot Default = new()
    {
        CurrentScore = 50,
        FastEma = 50,
        SlowEma = 50,
        Velocity = 0,
        Trend = HealthTrend.Stable,
        SampleCount = 0,
        PredictedScore30s = 50,
        PredictedScore60s = 50,
    };

    /// <summary>Gets the current health score.</summary>
    public int CurrentScore { get; init; }

    /// <summary>Gets the fast exponential moving average.</summary>
    public double FastEma { get; init; }

    /// <summary>Gets the slow exponential moving average.</summary>
    public double SlowEma { get; init; }

    /// <summary>Gets the velocity (score change per sample period).</summary>
    public double Velocity { get; init; }

    /// <summary>Gets the trend direction.</summary>
    public HealthTrend Trend { get; init; }

    /// <summary>Gets the number of samples in history.</summary>
    public int SampleCount { get; init; }

    /// <summary>Gets the last update time.</summary>
    public DateTime LastUpdate { get; init; }

    /// <summary>Gets the predicted score 30 seconds from now.</summary>
    public int PredictedScore30s { get; init; }

    /// <summary>Gets the predicted score 60 seconds from now.</summary>
    public int PredictedScore60s { get; init; }

    /// <summary>Gets a value indicating whether the trend suggests imminent failure.</summary>
    public bool SuggestsImminentFailure =>
        Trend == HealthTrend.RapidlyDegrading || (Trend == HealthTrend.Degrading && PredictedScore60s < 25);
}
