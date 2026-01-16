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
using System.Net.Http;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service;
using Jellyfin.Xtream.Service.ProviderManagement;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Xtream.Benchmarks;

/// <summary>
/// Benchmarks for provider selection and ordering algorithms.
/// Measures the performance of GetOrderedProviders, GetSelectionScore, and related operations.
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
public class ProviderSelectionBenchmarks
{
    private ProviderAvailabilityService _availabilityService = null!;
    private ProviderMetricsTracker _metricsTracker = null!;
    private HealthTrendTracker _trendTracker = null!;
    private AutomaticFailoverService _failoverService = null!;
    private List<ProviderStreamInfo> _providers5 = null!;
    private List<ProviderStreamInfo> _providers10 = null!;
    private List<ProviderStreamInfo> _providers20 = null!;
    private string[] _providerIds = null!;

    /// <summary>
    /// Setup for benchmark initialization.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        var httpClientFactory = new NullHttpClientFactory();
        var loggerFactory = NullLoggerFactory.Instance;
        var configProvider = new PluginConfigurationProvider();

        _availabilityService = new ProviderAvailabilityService(httpClientFactory, loggerFactory, null, configProvider);
        _metricsTracker = new ProviderMetricsTracker();
        _trendTracker = new HealthTrendTracker();
        _failoverService = new AutomaticFailoverService(
            _availabilityService,
            _metricsTracker,
            _trendTracker,
            NullLogger<AutomaticFailoverService>.Instance,
            configProvider
        );

        _providers5 = CreateProviders(5);
        _providers10 = CreateProviders(10);
        _providers20 = CreateProviders(20);
        _providerIds = _providers20.Select(p => p.Provider.Id).ToArray();

        // Warm up the services with some data
        foreach (var provider in _providers20)
        {
            var id = provider.Provider.Id;

            // Record some successes and failures to create realistic state
            for (int i = 0; i < 5; i++)
            {
                _availabilityService.RecordSuccess(id);
            }

            // Record some metrics
            _metricsTracker.RecordLatency(id, 100 + Random.Shared.Next(200));
            _metricsTracker.RecordThroughput(id, 1024 * 1024, 1000);

            // Update capacity
            _availabilityService.UpdateCapacity(id, Random.Shared.Next(1, 5), 5);
        }
    }

    private static List<ProviderStreamInfo> CreateProviders(int count)
    {
        var providers = new List<ProviderStreamInfo>(count);
        for (int i = 0; i < count; i++)
        {
            var provider = new XtreamProvider
            {
                Id = $"provider_{i}",
                Name = $"Provider {i}",
                BaseUrl = $"http://provider{i}.example.com",
                Username = "user",
                Password = "pass",
                Enabled = true,
            };

            var streamInfo = new StreamInfo
            {
                StreamId = i + 1,
                Name = $"Channel {i}",
                StreamType = "live",
                CategoryId = 1,
            };

            providers.Add(new ProviderStreamInfo(provider, streamInfo));
        }

        return providers;
    }

    /// <summary>
    /// Null HTTP client factory for benchmarks.
    /// </summary>
    private sealed class NullHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    /// <summary>
    /// Benchmark: Get selection score for a single provider.
    /// </summary>
    [Benchmark(Baseline = true)]
    public int GetSelectionScore_Single()
    {
        return _availabilityService.GetSelectionScore(_providerIds[0]);
    }

    /// <summary>
    /// Benchmark: Get selection scores for 5 providers.
    /// </summary>
    [Benchmark]
    public int GetSelectionScore_5Providers()
    {
        int total = 0;
        for (int i = 0; i < 5; i++)
        {
            total += _availabilityService.GetSelectionScore(_providerIds[i]);
        }

        return total;
    }

    /// <summary>
    /// Benchmark: Get selection scores for 20 providers.
    /// </summary>
    [Benchmark]
    public int GetSelectionScore_20Providers()
    {
        int total = 0;
        for (int i = 0; i < 20; i++)
        {
            total += _availabilityService.GetSelectionScore(_providerIds[i]);
        }

        return total;
    }

    /// <summary>
    /// Benchmark: Get sorted providers list for 5 providers.
    /// </summary>
    [Benchmark]
    public IReadOnlyList<ProviderStreamInfo> GetSortedProviders_5()
    {
        return _availabilityService.GetSortedProviders(_providers5);
    }

    /// <summary>
    /// Benchmark: Get sorted providers list for 10 providers.
    /// </summary>
    [Benchmark]
    public IReadOnlyList<ProviderStreamInfo> GetSortedProviders_10()
    {
        return _availabilityService.GetSortedProviders(_providers10);
    }

    /// <summary>
    /// Benchmark: Get sorted providers list for 20 providers.
    /// </summary>
    [Benchmark]
    public IReadOnlyList<ProviderStreamInfo> GetSortedProviders_20()
    {
        return _availabilityService.GetSortedProviders(_providers20);
    }

    /// <summary>
    /// Benchmark: Failover service get ordered providers for 5 providers.
    /// </summary>
    [Benchmark]
    public IReadOnlyList<ProviderStreamInfo> FailoverService_GetOrdered_5()
    {
        return _failoverService.GetOrderedProviders(_providers5);
    }

    /// <summary>
    /// Benchmark: Failover service get ordered providers for 10 providers.
    /// </summary>
    [Benchmark]
    public IReadOnlyList<ProviderStreamInfo> FailoverService_GetOrdered_10()
    {
        return _failoverService.GetOrderedProviders(_providers10);
    }

    /// <summary>
    /// Benchmark: Failover service get ordered providers for 20 providers.
    /// </summary>
    [Benchmark]
    public IReadOnlyList<ProviderStreamInfo> FailoverService_GetOrdered_20()
    {
        return _failoverService.GetOrderedProviders(_providers20);
    }

    /// <summary>
    /// Benchmark: Check availability for a single provider.
    /// </summary>
    [Benchmark]
    public bool IsAvailable_Single()
    {
        return _availabilityService.IsAvailable(_providerIds[0]);
    }

    /// <summary>
    /// Benchmark: Count available providers across 20 providers.
    /// </summary>
    [Benchmark]
    public int IsAvailable_Count_20()
    {
        int available = 0;
        for (int i = 0; i < 20; i++)
        {
            if (_availabilityService.IsAvailable(_providerIds[i]))
            {
                available++;
            }
        }

        return available;
    }

    /// <summary>
    /// Benchmark: Select best provider from 5 providers (realistic scenario).
    /// </summary>
    [Benchmark]
    public ProviderStreamInfo? SelectBestProvider_5()
    {
        var ordered = _failoverService.GetOrderedProviders(_providers5);
        return ordered.Count > 0 ? ordered[0] : null;
    }

    /// <summary>
    /// Benchmark: Select best provider from 20 providers (realistic scenario).
    /// </summary>
    [Benchmark]
    public ProviderStreamInfo? SelectBestProvider_20()
    {
        var ordered = _failoverService.GetOrderedProviders(_providers20);
        return ordered.Count > 0 ? ordered[0] : null;
    }

    /// <summary>
    /// Benchmark: Select best provider and get available count from 20 providers.
    /// </summary>
    [Benchmark]
    public (ProviderStreamInfo? Best, int AvailableCount) SelectWithCount_20()
    {
        var ordered = _failoverService.GetOrderedProviders(_providers20);
        return (ordered.Count > 0 ? ordered[0] : null, ordered.Count);
    }

    /// <summary>
    /// Benchmark: Calculate metrics health score for a single provider.
    /// </summary>
    [Benchmark]
    public int MetricsHealthScore_Single()
    {
        return _metricsTracker.CalculateHealthScore(_providerIds[0]);
    }

    /// <summary>
    /// Benchmark: Calculate metrics health score for 20 providers.
    /// </summary>
    [Benchmark]
    public int MetricsHealthScore_20Providers()
    {
        int total = 0;
        for (int i = 0; i < 20; i++)
        {
            total += _metricsTracker.CalculateHealthScore(_providerIds[i]);
        }

        return total;
    }

    /// <summary>
    /// Benchmark: Get resilience state snapshot for all providers.
    /// </summary>
    [Benchmark]
    public IReadOnlyDictionary<string, ProviderResilienceState> GetSnapshot()
    {
        return _availabilityService.GetSnapshot();
    }

    /// <summary>
    /// Benchmark: Get metrics snapshot for a single provider.
    /// </summary>
    [Benchmark]
    public ProviderMetricsSnapshot GetMetricsSnapshot_Single()
    {
        return _metricsTracker.GetSnapshot(_providerIds[0]);
    }

    /// <summary>
    /// Benchmark: Check capacity for a single provider.
    /// </summary>
    [Benchmark]
    public bool HasCapacity_Single()
    {
        return _availabilityService.HasCapacity(_providerIds[0]);
    }

    /// <summary>
    /// Benchmark: Count providers with capacity across 20 providers.
    /// </summary>
    [Benchmark]
    public int HasCapacity_Count_20()
    {
        int count = 0;
        for (int i = 0; i < 20; i++)
        {
            if (_availabilityService.HasCapacity(_providerIds[i]))
            {
                count++;
            }
        }

        return count;
    }
}
