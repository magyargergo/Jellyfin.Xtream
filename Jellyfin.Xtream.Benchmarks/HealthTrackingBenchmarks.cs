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
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Jellyfin.Xtream.Service.ProviderManagement;

namespace Jellyfin.Xtream.Benchmarks;

/// <summary>
/// Benchmarks for health trend tracking and EMA calculations.
/// Measures the performance of HealthTrendTracker operations including
/// sample recording, trend prediction, and snapshot retrieval.
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
public class HealthTrackingBenchmarks
{
    private HealthTrendTracker _tracker = null!;
    private ProviderMetricsTracker _metricsTracker = null!;
    private string[] _providerIds = null!;
    private const int ProviderCount = 20;

    /// <summary>
    /// Setup for benchmark initialization.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _tracker = new HealthTrendTracker();
        _metricsTracker = new ProviderMetricsTracker();
        _providerIds = new string[ProviderCount];

        for (int i = 0; i < ProviderCount; i++)
        {
            _providerIds[i] = $"provider_{i}";
        }

        // Warm up with historical data
        for (int sample = 0; sample < 30; sample++)
        {
            for (int i = 0; i < ProviderCount; i++)
            {
                int score = 50 + Random.Shared.Next(-20, 20);
                _tracker.RecordSample(_providerIds[i], score);

                // Also record metrics for comparison
                _metricsTracker.RecordLatency(_providerIds[i], 100 + Random.Shared.Next(200));
                _metricsTracker.RecordThroughput(_providerIds[i], 1024 * 1024, 1000);
            }
        }
    }

    /// <summary>
    /// Benchmark: Record a single health sample.
    /// </summary>
    [Benchmark(Baseline = true)]
    public void RecordSample_Single()
    {
        _tracker.RecordSample(_providerIds[0], 75);
    }

    /// <summary>
    /// Benchmark: Record health samples for 5 providers.
    /// </summary>
    [Benchmark]
    public void RecordSample_5Providers()
    {
        for (int i = 0; i < 5; i++)
        {
            _tracker.RecordSample(_providerIds[i], 75);
        }
    }

    /// <summary>
    /// Benchmark: Record health samples for 20 providers.
    /// </summary>
    [Benchmark]
    public void RecordSample_20Providers()
    {
        for (int i = 0; i < 20; i++)
        {
            _tracker.RecordSample(_providerIds[i], 75);
        }
    }

    /// <summary>
    /// Benchmark: Get predicted score 30 seconds ahead.
    /// </summary>
    [Benchmark]
    public int GetPredictedScore_30s()
    {
        return _tracker.GetPredictedScore(_providerIds[0], 30);
    }

    /// <summary>
    /// Benchmark: Get predicted score 60 seconds ahead.
    /// </summary>
    [Benchmark]
    public int GetPredictedScore_60s()
    {
        return _tracker.GetPredictedScore(_providerIds[0], 60);
    }

    /// <summary>
    /// Benchmark: Get predicted scores for 20 providers 30 seconds ahead.
    /// </summary>
    [Benchmark]
    public int GetPredictedScore_20Providers_30s()
    {
        int total = 0;
        for (int i = 0; i < 20; i++)
        {
            total += _tracker.GetPredictedScore(_providerIds[i], 30);
        }

        return total;
    }

    /// <summary>
    /// Benchmark: Get trend for a single provider.
    /// </summary>
    [Benchmark]
    public HealthTrend GetTrend_Single()
    {
        return _tracker.GetTrend(_providerIds[0]);
    }

    /// <summary>
    /// Benchmark: Get trends for 20 providers and count degrading.
    /// </summary>
    [Benchmark]
    public int GetTrend_20Providers()
    {
        int degrading = 0;
        for (int i = 0; i < 20; i++)
        {
            if (_tracker.GetTrend(_providerIds[i]) == HealthTrend.Degrading)
            {
                degrading++;
            }
        }

        return degrading;
    }

    /// <summary>
    /// Benchmark: Get snapshot for a single provider.
    /// </summary>
    [Benchmark]
    public HealthTrendSnapshot GetSnapshot_Single()
    {
        return _tracker.GetSnapshot(_providerIds[0]);
    }

    /// <summary>
    /// Benchmark: Get snapshots for 20 providers and count at-risk.
    /// </summary>
    [Benchmark]
    public int GetSnapshot_20Providers()
    {
        int atRisk = 0;
        for (int i = 0; i < 20; i++)
        {
            var snapshot = _tracker.GetSnapshot(_providerIds[i]);
            if (snapshot.SuggestsImminentFailure)
            {
                atRisk++;
            }
        }

        return atRisk;
    }

    /// <summary>
    /// Benchmark: Get all snapshots at once.
    /// </summary>
    [Benchmark]
    public IReadOnlyDictionary<string, HealthTrendSnapshot> GetAllSnapshots()
    {
        return _tracker.GetAllSnapshots();
    }

    /// <summary>
    /// Benchmark: Record latency for a single provider.
    /// </summary>
    [Benchmark]
    public void RecordLatency_Single()
    {
        _metricsTracker.RecordLatency(_providerIds[0], 150.0);
    }

    /// <summary>
    /// Benchmark: Record throughput for a single provider.
    /// </summary>
    [Benchmark]
    public void RecordThroughput_Single()
    {
        _metricsTracker.RecordThroughput(_providerIds[0], 1048576, 1000.0);
    }

    /// <summary>
    /// Benchmark: Record error for a single provider.
    /// </summary>
    [Benchmark]
    public void RecordError_Single()
    {
        _metricsTracker.RecordError(_providerIds[0], StreamErrorType.NetworkError);
    }

    /// <summary>
    /// Benchmark: Record disconnection for a single provider.
    /// </summary>
    [Benchmark]
    public void RecordDisconnection_Single()
    {
        _metricsTracker.RecordDisconnection(_providerIds[0], 60000.0);
    }

    /// <summary>
    /// Benchmark: Record all metric types for a single provider.
    /// </summary>
    [Benchmark]
    public void RecordAllMetrics_Single()
    {
        var id = _providerIds[0];
        _metricsTracker.RecordLatency(id, 150.0);
        _metricsTracker.RecordThroughput(id, 1048576, 1000.0);
        _metricsTracker.RecordError(id, StreamErrorType.ContinuityError);
    }

    /// <summary>
    /// Benchmark: Record latency and throughput for 20 providers.
    /// </summary>
    [Benchmark]
    public void RecordAllMetrics_20Providers()
    {
        for (int i = 0; i < 20; i++)
        {
            var id = _providerIds[i];
            _metricsTracker.RecordLatency(id, 150.0);
            _metricsTracker.RecordThroughput(id, 1048576, 1000.0);
        }
    }

    /// <summary>
    /// Benchmark: Calculate health score for a single provider.
    /// </summary>
    [Benchmark]
    public int CalculateHealthScore_Single()
    {
        return _metricsTracker.CalculateHealthScore(_providerIds[0]);
    }

    /// <summary>
    /// Benchmark: Calculate health scores for 20 providers.
    /// </summary>
    [Benchmark]
    public int CalculateHealthScore_20Providers()
    {
        int total = 0;
        for (int i = 0; i < 20; i++)
        {
            total += _metricsTracker.CalculateHealthScore(_providerIds[i]);
        }

        return total;
    }

    /// <summary>
    /// Benchmark: Calculate health score from a snapshot.
    /// </summary>
    [Benchmark]
    public int CalculateHealthScore_FromSnapshot()
    {
        var snapshot = _metricsTracker.GetSnapshot(_providerIds[0]);
        return ProviderMetricsTracker.CalculateHealthScore(snapshot);
    }

    /// <summary>
    /// Benchmark: Full analysis for a single provider (realistic scenario).
    /// </summary>
    [Benchmark]
    public (HealthTrend Trend, int PredictedScore, bool AtRisk) FullAnalysis_Single()
    {
        var snapshot = _tracker.GetSnapshot(_providerIds[0]);
        return (snapshot.Trend, snapshot.PredictedScore60s, snapshot.SuggestsImminentFailure);
    }

    /// <summary>
    /// Benchmark: Find best provider across 20 providers (realistic scenario).
    /// </summary>
    [Benchmark]
    public int FullAnalysis_FindBestProvider()
    {
        int bestScore = int.MinValue;
        int bestIndex = -1;

        for (int i = 0; i < 20; i++)
        {
            var snapshot = _tracker.GetSnapshot(_providerIds[i]);
            if (!snapshot.SuggestsImminentFailure && snapshot.PredictedScore60s > bestScore)
            {
                bestScore = snapshot.PredictedScore60s;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    /// <summary>
    /// Benchmark: Combined health check for 20 providers.
    /// </summary>
    [Benchmark]
    public int CombinedHealthCheck_20Providers()
    {
        int healthyCount = 0;
        for (int i = 0; i < 20; i++)
        {
            var trendSnapshot = _tracker.GetSnapshot(_providerIds[i]);
            var metricsScore = _metricsTracker.CalculateHealthScore(_providerIds[i]);

            if (!trendSnapshot.SuggestsImminentFailure && metricsScore >= 50)
            {
                healthyCount++;
            }
        }

        return healthyCount;
    }

    /// <summary>
    /// Benchmark: Simulate rapid score changes to test EMA responsiveness.
    /// </summary>
    [Benchmark]
    public void SimulateRapidScoreChanges()
    {
        var id = _providerIds[0];
        for (int i = 0; i < 10; i++)
        {
            _tracker.RecordSample(id, i % 2 == 0 ? 80 : 20);
        }
    }

    /// <summary>
    /// Benchmark: Simulate steady degradation to test trend detection.
    /// </summary>
    [Benchmark]
    public void SimulateSteadyDegradation()
    {
        var id = _providerIds[1];
        int score = 100;
        for (int i = 0; i < 10; i++)
        {
            _tracker.RecordSample(id, score);
            score = Math.Max(0, score - 8);
        }
    }
}
