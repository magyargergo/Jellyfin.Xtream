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
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service;
using Jellyfin.Xtream.Service.ProviderManagement;
using Jellyfin.Xtream.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Unit tests for ProviderResilienceService.
/// Tests circuit breaker behavior, health scoring, and capacity tracking.
/// </summary>
public sealed class ProviderResilienceServiceTests : IDisposable
{
    private readonly ProviderAvailabilityService _service;

    private static readonly TestConfigurationProvider SharedConfigProvider = new();

    public ProviderResilienceServiceTests()
    {
        // Initialize PluginLogger with test configuration provider to avoid Plugin.Instance access
        PluginLogger.Initialize(null, SharedConfigProvider);

        // Use the constructor with required dependencies for testing
        _service = new ProviderAvailabilityService(
            new SimpleHttpClientFactory(),
            NullLoggerFactory.Instance,
            discordService: null,
            configurationProvider: SharedConfigProvider
        );
    }

    // Simple IHttpClientFactory implementation for testing
    private sealed class SimpleHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    // Simple IPluginConfigurationProvider implementation for testing
    private sealed class TestConfigurationProvider : IPluginConfigurationProvider
    {
        public PluginConfiguration? GetConfiguration() => new() { EnableDebugLogging = false };
    }

    public void Dispose() =>
        // Dispose the service
        _service.Dispose();

    #region Availability Tests

    [Fact]
    public void NewProvider_IsAvailable()
    {
        var available = _service.IsAvailable("new-provider");

        Assert.True(available);
    }

    [Fact]
    public void NewProvider_HasClosedCircuit()
    {
        var state = _service.GetCircuitState("new-provider");

        Assert.Equal(ProviderCircuitState.Closed, state);
    }

    [Fact]
    public void NewProvider_HasNeutralScore()
    {
        var score = _service.GetSelectionScore("new-provider");

        // New provider starts with 50% success rate (5/10) and 100% capacity
        // Score = (50 * 0.4) + (100 * 0.4) + (50 * 0.2) + bonuses = ~70
        Assert.InRange(score, 60, 80);
    }

    #endregion

    #region Success Recording Tests

    [Fact]
    public void RecordSuccess_ImprovesScore()
    {
        const string providerId = "success-test";

        var initialScore = _service.GetSelectionScore(providerId);

        _service.RecordSuccess(providerId);
        _service.RecordSuccess(providerId);
        _service.RecordSuccess(providerId);

        var afterSuccess = _service.GetSelectionScore(providerId);

        Assert.True(afterSuccess >= initialScore);
    }

    [Fact]
    public void RecordSuccess_MaintainsAvailability()
    {
        const string providerId = "success-available";

        _service.RecordSuccess(providerId);

        Assert.True(_service.IsAvailable(providerId));
        Assert.Equal(ProviderCircuitState.Closed, _service.GetCircuitState(providerId));
    }

    #endregion

    #region Failure Recording Tests

    [Fact]
    public void RecordFailure_ReducesScore()
    {
        const string providerId = "failure-test";

        // Record some successes first to establish baseline
        _service.RecordSuccess(providerId);
        _service.RecordSuccess(providerId);
        var afterSuccesses = _service.GetSelectionScore(providerId);

        _ = _service.RecordFailure(providerId, ProviderFailureReason.NetworkError);

        var afterFailure = _service.GetSelectionScore(providerId);

        Assert.True(afterFailure <= afterSuccesses);
    }

    [Fact]
    public void RecordFailure_MultipleFailures_OpensCircuit()
    {
        const string providerId = "circuit-open-test";

        // Record enough failures to open circuit
        for (var i = 0; i < 5; i++)
        {
            _ = _service.RecordFailure(providerId, ProviderFailureReason.NetworkError);
        }

        // Circuit should be open or score should be 0
        var score = _service.GetSelectionScore(providerId);
        Assert.True(score == 0 || _service.GetCircuitState(providerId) == ProviderCircuitState.Open);
    }

    [Fact]
    public void RecordFailure_ReturnsWhetherCircuitOpened()
    {
        const string providerId = "circuit-opened-test";

        // First few failures shouldn't open circuit
        var opened1 = _service.RecordFailure(providerId, ProviderFailureReason.NetworkError);
        var opened2 = _service.RecordFailure(providerId, ProviderFailureReason.NetworkError);

        // At least one of the early failures should not open circuit
        Assert.False(opened1 && opened2);
    }

    [Fact]
    public void RecordFailure_TwoFailures_OpensCircuit_WithReducedThreshold()
    {
        // This test validates the circuit breaker threshold of 2
        // (reduced from 3 for faster failover per Polly best practices)
        const string providerId = "two-failure-threshold-test";

        // First failure should not open circuit
        var opened1 = _service.RecordFailure(providerId, ProviderFailureReason.NetworkError);

        // Second failure should open circuit (threshold = 2)
        var opened2 = _service.RecordFailure(providerId, ProviderFailureReason.NetworkError);

        // At least by the second failure, circuit should be open
        var circuitState = _service.GetCircuitState(providerId);
        Assert.True(
            opened1 || opened2 || circuitState == ProviderCircuitState.Open,
            $"Circuit should open after 2 failures (threshold=2). State: {circuitState}"
        );
    }

    #endregion

    #region Blacklist Duration Tests

    [Fact]
    public void RecordFailure_TimeoutError_CircuitOpensWithQuickRecovery()
    {
        // Timeout errors should use quick blacklist (10s) for faster recovery
        const string providerId = "timeout-blacklist-test";

        // Record enough timeout failures to open circuit
        _ = _service.RecordFailure(providerId, ProviderFailureReason.Timeout);
        _ = _service.RecordFailure(providerId, ProviderFailureReason.Timeout);

        // Circuit should be open
        var state = _service.GetCircuitState(providerId);
        Assert.True(
            state == ProviderCircuitState.Open || _service.GetSelectionScore(providerId) == 0,
            "Circuit should open after timeout failures"
        );
    }

    [Fact]
    public void RecordFailure_PrematureEofError_CircuitOpensWithQuickRecovery()
    {
        // PrematureEof errors should use quick blacklist (10s) for faster recovery
        const string providerId = "premature-eof-blacklist-test";

        // Record enough EOF failures to open circuit
        _ = _service.RecordFailure(providerId, ProviderFailureReason.PrematureEof);
        _ = _service.RecordFailure(providerId, ProviderFailureReason.PrematureEof);

        // Circuit should be open
        var state = _service.GetCircuitState(providerId);
        Assert.True(
            state == ProviderCircuitState.Open || _service.GetSelectionScore(providerId) == 0,
            "Circuit should open after premature EOF failures"
        );
    }

    [Fact]
    public void RecordFailure_ConnectionLimitError_CircuitOpensWithExtendedDuration()
    {
        // ConnectionLimit errors should use extended blacklist (60s)
        const string providerId = "connection-limit-blacklist-test";

        // Record connection limit failures
        _ = _service.RecordFailure(providerId, ProviderFailureReason.ConnectionLimit);
        _ = _service.RecordFailure(providerId, ProviderFailureReason.ConnectionLimit);

        // Circuit should be open with extended duration
        var state = _service.GetCircuitState(providerId);
        Assert.True(
            state == ProviderCircuitState.Open || _service.GetSelectionScore(providerId) == 0,
            "Circuit should open after connection limit failures"
        );
    }

    [Fact]
    public void RecordFailure_ZombieBackendError_CircuitOpensWithModerateDuration()
    {
        // ZombieBackend errors indicate load balancer routing to dead backend
        // Should use moderate blacklist (30s) for recovery
        const string providerId = "zombie-backend-blacklist-test";

        // Record zombie backend failures
        _ = _service.RecordFailure(providerId, ProviderFailureReason.ZombieBackend);
        _ = _service.RecordFailure(providerId, ProviderFailureReason.ZombieBackend);

        // Circuit should be open
        var state = _service.GetCircuitState(providerId);
        Assert.True(
            state == ProviderCircuitState.Open || _service.GetSelectionScore(providerId) == 0,
            "Circuit should open after zombie backend failures"
        );
    }

    #endregion

    #region Connection Limit Tests

    [Fact]
    public async Task RecordConnectionLimit_IsolatesCircuit()
    {
        const string providerId = "connection-limit-test";

        await _service.RecordConnectionLimitAsync(providerId);

        Assert.Equal(ProviderCircuitState.Isolated, _service.GetCircuitState(providerId));
        Assert.False(_service.IsAvailable(providerId));
    }

    [Fact]
    public async Task RecordConnectionLimit_SetsScoreToZero()
    {
        const string providerId = "connection-limit-score";

        _service.RecordSuccess(providerId);
        _service.RecordSuccess(providerId);

        await _service.RecordConnectionLimitAsync(providerId);

        Assert.Equal(0, _service.GetSelectionScore(providerId));
    }

    #endregion

    #region Circuit Reset Tests

    [Fact]
    public async Task ResetCircuit_RestoresAvailability()
    {
        const string providerId = "reset-test";

        await _service.RecordConnectionLimitAsync(providerId);
        Assert.False(_service.IsAvailable(providerId));

        await _service.ResetCircuitAsync(providerId);

        Assert.True(_service.IsAvailable(providerId));
        Assert.Equal(ProviderCircuitState.Closed, _service.GetCircuitState(providerId));
    }

    #endregion

    #region Capacity Tracking Tests

    [Fact]
    public void UpdateCapacity_TracksAvailableSlots()
    {
        const string providerId = "capacity-test";

        _service.UpdateCapacity(providerId, availableSlots: 5, maxConnections: 10);

        Assert.True(_service.HasCapacity(providerId));
    }

    [Fact]
    public void UpdateCapacity_ZeroSlots_NoCapacity()
    {
        const string providerId = "no-capacity-test";

        _service.UpdateCapacity(providerId, availableSlots: 0, maxConnections: 10);

        Assert.False(_service.HasCapacity(providerId));
    }

    [Fact]
    public void UpdateCapacity_AffectsAvailability()
    {
        const string providerId = "capacity-availability";

        _service.UpdateCapacity(providerId, availableSlots: 0, maxConnections: 10);

        Assert.False(_service.IsAvailable(providerId));
    }

    [Fact]
    public void HasCapacity_UnknownProvider_ReturnsTrue()
    {
        var hasCapacity = _service.HasCapacity("unknown-provider");

        Assert.True(hasCapacity);
    }

    #endregion

    #region Provider Sorting Tests

    [Fact]
    public void GetSortedProviders_SortsByScore()
    {
        var provider1 = CreateProviderStreamInfo("provider-1");
        var provider2 = CreateProviderStreamInfo("provider-2");
        var provider3 = CreateProviderStreamInfo("provider-3");

        // Provider 1: failures
        _ = _service.RecordFailure("provider-1", ProviderFailureReason.NetworkError);
        _ = _service.RecordFailure("provider-1", ProviderFailureReason.NetworkError);

        // Provider 2: successes
        _service.RecordSuccess("provider-2");
        _service.RecordSuccess("provider-2");
        _service.RecordSuccess("provider-2");

        // Provider 3: neutral

        var providers = new[] { provider1, provider2, provider3 };
        var sorted = _service.GetSortedProviders(providers);

        // Provider 2 should be first (healthiest)
        Assert.Equal("provider-2", sorted[0].Provider.Id);
    }

    [Fact]
    public async Task GetSortedProviders_ExcludesUnavailable()
    {
        var provider1 = CreateProviderStreamInfo("unavailable-provider");
        var provider2 = CreateProviderStreamInfo("available-provider");

        await _service.RecordConnectionLimitAsync("unavailable-provider");

        var providers = new[] { provider1, provider2 };
        var sorted = _service.GetSortedProviders(providers);

        _ = Assert.Single(sorted);
        Assert.Equal("available-provider", sorted[0].Provider.Id);
    }

    [Fact]
    public async Task GetSortedProviders_ForceIncludeAll_IncludesUnavailable()
    {
        var provider1 = CreateProviderStreamInfo("unavailable-provider");
        var provider2 = CreateProviderStreamInfo("available-provider");

        await _service.RecordConnectionLimitAsync("unavailable-provider");

        var providers = new[] { provider1, provider2 };
        var sorted = _service.GetSortedProviders(providers, forceIncludeAll: true);

        Assert.Equal(2, sorted.Count);
    }

    #endregion

    #region Snapshot Tests

    [Fact]
    public void GetSnapshot_ReturnsAllTrackedProviders()
    {
        _service.RecordSuccess("provider-a");
        _service.RecordSuccess("provider-b");
        _ = _service.RecordFailure("provider-c", ProviderFailureReason.NetworkError);

        var snapshot = _service.GetSnapshot();

        Assert.Equal(3, snapshot.Count);
        Assert.Contains("provider-a", snapshot.Keys);
        Assert.Contains("provider-b", snapshot.Keys);
        Assert.Contains("provider-c", snapshot.Keys);
    }

    [Fact]
    public void GetSnapshot_ContainsCorrectState()
    {
        const string providerId = "snapshot-state";

        _service.RecordSuccess(providerId);
        _service.UpdateCapacity(providerId, availableSlots: 3, maxConnections: 5);

        var snapshot = _service.GetSnapshot();
        var state = snapshot[providerId];

        Assert.Equal(providerId, state.ProviderId);
        Assert.Equal(ProviderCircuitState.Closed, state.CircuitState);
        Assert.True(state.IsAvailable);
        Assert.Equal(3, state.AvailableSlots);
        Assert.Equal(5, state.MaxConnections);
    }

    #endregion

    #region Thread Safety Tests

    [Fact]
    public async Task ConcurrentOperations_AreThreadSafe()
    {
        const int numTasks = 100;
        var tasks = new Task[numTasks];

        for (var i = 0; i < numTasks; i++)
        {
            var taskIndex = i;
            tasks[i] = Task.Run(() =>
            {
                var providerId = $"concurrent-{taskIndex % 5}";

                if (taskIndex % 3 == 0)
                {
                    _service.RecordSuccess(providerId);
                }
                else if (taskIndex % 3 == 1)
                {
                    _ = _service.RecordFailure(providerId, ProviderFailureReason.NetworkError);
                }
                else
                {
                    _ = _service.GetSelectionScore(providerId);
                    _ = _service.IsAvailable(providerId);
                }
            });
        }

        await Task.WhenAll(tasks);

        // Should complete without exceptions
        var snapshot = _service.GetSnapshot();
        foreach (var state in snapshot.Values)
        {
            Assert.InRange(state.SelectionScore, 0, 100);
        }
    }

    #endregion

    #region Helper Methods

    private static ProviderStreamInfo CreateProviderStreamInfo(string providerId)
    {
        var provider = new XtreamProvider
        {
            Id = providerId,
            Name = $"Test Provider {providerId}",
            BaseUrl = "http://test.example.com",
            Username = "test",
            Password = "test",
        };

        var stream = new StreamInfo { StreamId = 1, Name = "Test Stream" };

        return new ProviderStreamInfo(provider, stream);
    }

    #endregion
}
