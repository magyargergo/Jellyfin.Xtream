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
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service;
using Jellyfin.Xtream.Service.ProviderManagement;
using Jellyfin.Xtream.Service.Switching;
using Jellyfin.Xtream.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Unit tests for HotSwapStreamManager.
/// Tests stream registration, switching, and statistics.
/// </summary>
public sealed class HotSwapStreamManagerTests : IDisposable
{
    private readonly HotSwapStreamManager _manager;
    private readonly MockResilienceService _resilienceService;
    private readonly AutomaticFailoverService _failoverService;
    private readonly PreconnectPool _preconnectPool;

    public HotSwapStreamManagerTests()
    {
        PluginLogger.SetDebugEnabledProvider(() => false);

        _resilienceService = new MockResilienceService();
        _failoverService = new AutomaticFailoverService(
            _resilienceService,
            _resilienceService,
            new ProviderMetricsTracker(),
            new HealthTrendTracker(),
            NullLogger<AutomaticFailoverService>.Instance,
            configProvider: null
        );

        var httpClient = new HttpClient();
        _preconnectPool = new PreconnectPool(httpClient, _resilienceService);

        _manager = new HotSwapStreamManager(
            _resilienceService,
            _failoverService,
            _preconnectPool,
            NullLogger<HotSwapStreamManager>.Instance
        );
    }

    public void Dispose()
    {
        _manager.Dispose();
        _preconnectPool.Dispose();
        PluginLogger.SetDebugEnabledProvider(null);
    }

    private static ProviderStreamInfo CreateProvider(string id, int streamId = 1)
    {
        var provider = new XtreamProvider
        {
            Id = id,
            Name = $"Provider {id}",
            BaseUrl = $"http://{id}.example.com",
            Username = "user",
            Password = "pass",
        };
        var stream = new Jellyfin.Xtream.Client.Models.StreamInfo { StreamId = streamId, Name = "Test Channel" };
        return new ProviderStreamInfo(provider, stream);
    }

    [Fact]
    public void NewManager_HasZeroActiveStreams()
    {
        Assert.Equal(0, _manager.ActiveStreamCount);
    }

    [Fact]
    public void NewManager_HasZeroSwitches()
    {
        Assert.Equal(0, _manager.TotalSwitches);
        Assert.Equal(0, _manager.SuccessfulSwitches);
        Assert.Equal(0, _manager.FailedSwitches);
    }

    [Fact]
    public void RegisterStream_IncreasesActiveCount()
    {
        var provider = CreateProvider("p1");

        _manager.RegisterStream("session-1", 100, provider);

        Assert.Equal(1, _manager.ActiveStreamCount);
    }

    [Fact]
    public void RegisterStream_DuplicateSession_DoesNotIncrease()
    {
        var provider = CreateProvider("p1");

        _manager.RegisterStream("session-1", 100, provider);
        _manager.RegisterStream("session-1", 100, provider);

        Assert.Equal(1, _manager.ActiveStreamCount);
    }

    [Fact]
    public void UnregisterStream_DecreasesActiveCount()
    {
        var provider = CreateProvider("p1");
        _manager.RegisterStream("session-1", 100, provider);

        _manager.UnregisterStream("session-1");

        Assert.Equal(0, _manager.ActiveStreamCount);
    }

    [Fact]
    public void UnregisterStream_UnknownSession_DoesNotThrow()
    {
        _manager.UnregisterStream("unknown");

        Assert.Equal(0, _manager.ActiveStreamCount);
    }

    [Fact]
    public void GetStreamStats_RegisteredSession_ReturnsStats()
    {
        var provider = CreateProvider("p1");
        _manager.RegisterStream("session-1", 100, provider);

        var stats = _manager.GetStreamStats("session-1");

        Assert.NotNull(stats);
        Assert.Equal("session-1", stats.Value.SessionId);
        Assert.Equal(100, stats.Value.ChannelId);
        Assert.Equal("p1", stats.Value.CurrentProviderId);
        Assert.Equal(0, stats.Value.SwitchCount);
    }

    [Fact]
    public void GetStreamStats_UnknownSession_ReturnsNull()
    {
        var stats = _manager.GetStreamStats("unknown");

        Assert.Null(stats);
    }

    [Fact]
    public void GetOverallStats_ReturnsValidStats()
    {
        var provider = CreateProvider("p1");
        _manager.RegisterStream("session-1", 100, provider);
        _manager.RegisterStream("session-2", 200, provider);

        var stats = _manager.GetOverallStats();

        Assert.Equal(2, stats.ActiveStreams);
        Assert.Equal(0, stats.TotalSwitches);
    }

    [Fact]
    public async Task SwitchAsync_UnknownSession_ReturnsFailed()
    {
        var target = CreateProvider("p2");

        var result = await _manager.SwitchAsync(
            "unknown",
            target,
            SwitchReason.HealthDegraded,
            (_, _) => Task.FromResult(true)
        );

        Assert.False(result.Success);
        Assert.Contains("not found", result.FailureReason);
    }

    [Fact]
    public async Task SwitchAsync_SuccessfulSwitch_UpdatesStats()
    {
        var provider1 = CreateProvider("p1");
        var provider2 = CreateProvider("p2");
        _manager.RegisterStream("session-1", 100, provider1);

        var result = await _manager.SwitchAsync(
            "session-1",
            provider2,
            SwitchReason.HealthDegraded,
            (_, _) => Task.FromResult(true)
        );

        Assert.True(result.Success);
        Assert.Equal(1, _manager.SuccessfulSwitches);
        Assert.Equal(1, _manager.TotalSwitches);
        Assert.Equal(0, _manager.FailedSwitches);
    }

    [Fact]
    public async Task SwitchAsync_FailedCallback_ReturnsFailed()
    {
        var provider1 = CreateProvider("p1");
        var provider2 = CreateProvider("p2");
        _manager.RegisterStream("session-1", 100, provider1);

        var result = await _manager.SwitchAsync(
            "session-1",
            provider2,
            SwitchReason.ConnectionFailed,
            (_, _) => Task.FromResult(false)
        );

        Assert.False(result.Success);
        Assert.Equal(1, _manager.FailedSwitches);
    }

    [Fact]
    public async Task SwitchAsync_UpdatesCurrentProvider()
    {
        var provider1 = CreateProvider("p1");
        var provider2 = CreateProvider("p2");
        _manager.RegisterStream("session-1", 100, provider1);

        await _manager.SwitchAsync(
            "session-1",
            provider2,
            SwitchReason.BetterProviderAvailable,
            (_, _) => Task.FromResult(true)
        );

        var stats = _manager.GetStreamStats("session-1");
        Assert.Equal("p2", stats!.Value.CurrentProviderId);
        Assert.Equal(1, stats.Value.SwitchCount);
    }

    [Fact]
    public async Task SwitchAsync_ConcurrentSwitches_OnlyOneSucceeds()
    {
        var provider1 = CreateProvider("p1");
        var provider2 = CreateProvider("p2");
        _manager.RegisterStream("session-1", 100, provider1);

        var tcs = new TaskCompletionSource<bool>();

        // Start first switch that will block
        var task1 = _manager.SwitchAsync(
            "session-1",
            provider2,
            SwitchReason.HealthDegraded,
            async (_, ct) =>
            {
                await tcs.Task.WaitAsync(ct);
                return true;
            }
        );

        // Give first task time to acquire lock
        await Task.Delay(50);

        // Try second switch immediately
        var task2 = _manager.SwitchAsync(
            "session-1",
            provider2,
            SwitchReason.HealthDegraded,
            (_, _) => Task.FromResult(true)
        );

        // Second should fail immediately (lock not available)
        var result2 = await task2;
        Assert.False(result2.Success);
        Assert.Contains("in progress", result2.FailureReason);

        // Complete first switch
        tcs.SetResult(true);
        var result1 = await task1;
        Assert.True(result1.Success);
    }

    [Fact]
    public void ShouldSwitch_UnknownSession_ReturnsNull()
    {
        var result = _manager.ShouldSwitch("unknown", Array.Empty<ProviderStreamInfo>());

        Assert.Null(result);
    }

    [Fact]
    public void ShouldSwitch_WithinCooldown_ReturnsNull()
    {
        var provider = CreateProvider("p1");
        _manager.RegisterStream("session-1", 100, provider);

        // ShouldSwitch should return null during cooldown (just after registration)
        var result = _manager.ShouldSwitch("session-1", new[] { CreateProvider("p2") });

        // Depends on failover service behavior, but cooldown should prevent switch
        // After first switch, cooldown applies
        Assert.Null(result);
    }

    [Fact]
    public void AverageSwitchLatencyMs_NoSwitches_ReturnsZero()
    {
        Assert.Equal(0, _manager.AverageSwitchLatencyMs);
    }

    [Fact]
    public async Task AverageSwitchLatencyMs_AfterSwitches_CalculatesAverage()
    {
        var provider1 = CreateProvider("p1");
        var provider2 = CreateProvider("p2");
        _manager.RegisterStream("session-1", 100, provider1);
        _manager.RegisterStream("session-2", 200, provider1);

        // Perform two switches
        await _manager.SwitchAsync(
            "session-1",
            provider2,
            SwitchReason.HealthDegraded,
            async (_, _) =>
            {
                await Task.Delay(10);
                return true;
            }
        );

        Assert.True(_manager.AverageSwitchLatencyMs > 0);
    }

    [Fact]
    public void SwitchResult_Succeeded_HasCorrectProperties()
    {
        var from = CreateProvider("p1");
        var to = CreateProvider("p2");

        var result = SwitchResult.Succeeded(from, to, 50, true);

        Assert.True(result.Success);
        Assert.Equal(from, result.PreviousProvider);
        Assert.Equal(to, result.NewProvider);
        Assert.Equal(50, result.DurationMs);
        Assert.True(result.HadDiscontinuity);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public void SwitchResult_Failed_HasCorrectProperties()
    {
        var from = CreateProvider("p1");
        var error = "Connection timeout";

        var result = SwitchResult.Failed(from, error);

        Assert.False(result.Success);
        Assert.Equal(from, result.PreviousProvider);
        Assert.Null(result.NewProvider);
        Assert.Equal(error, result.FailureReason);
    }

    [Fact]
    public void StreamSwitchStats_Equality_SameSessionAndChannel()
    {
        var stats1 = new StreamSwitchStats
        {
            SessionId = "s1",
            ChannelId = 100,
            CurrentProviderId = "p1",
            SwitchCount = 5,
            LastSwitchTime = DateTime.UtcNow,
            LastSwitchLatencyMs = 50,
            CurrentCooldownMs = 5000,
            CooldownSuccessRate = 1.0,
        };
        var stats2 = new StreamSwitchStats
        {
            SessionId = "s1",
            ChannelId = 100,
            CurrentProviderId = "p2",
            SwitchCount = 10,
            LastSwitchTime = DateTime.UtcNow.AddHours(1),
            LastSwitchLatencyMs = 100,
            CurrentCooldownMs = 3000,
            CooldownSuccessRate = 0.8,
        };

        Assert.True(stats1 == stats2);
        Assert.Equal(stats1.GetHashCode(), stats2.GetHashCode());
    }

    [Fact]
    public void HotSwapStats_Equality_SameTotalAndActive()
    {
        var stats1 = new HotSwapStats
        {
            ActiveStreams = 5,
            TotalSwitches = 100,
            SuccessfulSwitches = 90,
            FailedSwitches = 10,
            AverageSwitchLatencyMs = 50.0,
            PreconnectPoolSize = 2,
            PreconnectHitRate = 75.0,
        };
        var stats2 = new HotSwapStats
        {
            ActiveStreams = 5,
            TotalSwitches = 100,
            SuccessfulSwitches = 80,
            FailedSwitches = 20,
            AverageSwitchLatencyMs = 100.0,
            PreconnectPoolSize = 3,
            PreconnectHitRate = 50.0,
        };

        Assert.True(stats1 == stats2);
    }

    // Mock implementations for testing
    private sealed class MockResilienceService : IProviderAvailabilityService
    {
        public bool IsAvailable(string providerId) => true;

        public Polly.CircuitBreaker.CircuitState GetCircuitState(string providerId) =>
            Polly.CircuitBreaker.CircuitState.Closed;

        public int GetSelectionScore(string providerId) => 50;

        public IReadOnlyList<ProviderStreamInfo> GetSortedProviders(
            IEnumerable<ProviderStreamInfo> providers,
            bool forceIncludeAll = false
        ) => new List<ProviderStreamInfo>(providers);

        public void RecordSuccess(string providerId) { }

        public bool RecordFailure(string providerId, ProviderFailureReason reason, string? providerName = null) =>
            false;

        public Task RecordConnectionLimitAsync(string providerId) => Task.CompletedTask;

        public Task ResetCircuitAsync(string providerId) => Task.CompletedTask;

        public IReadOnlyDictionary<string, ProviderResilienceState> GetSnapshot() =>
            new Dictionary<string, ProviderResilienceState>(StringComparer.Ordinal);

        public void UpdateCapacity(string providerId, int availableSlots, int maxConnections) { }

        public bool HasCapacity(string providerId) => true;

        public bool IsCircuitAvailable(string providerId) => true;

        public Task IsolateCircuitAsync(string providerId) => Task.CompletedTask;

        public int GetAvailableSlots(string providerId) => -1;

        public int GetMaxConnections(string providerId) => 0;

        public ProviderResilienceState? GetStatus(string providerId) => null;

        public Task RefreshAsync(
            IEnumerable<XtreamProvider> providers,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask;

        public bool NeedsRefresh() => false;
    }
}
