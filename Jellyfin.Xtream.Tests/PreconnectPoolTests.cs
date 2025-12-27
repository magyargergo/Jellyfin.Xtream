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
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service;
using Jellyfin.Xtream.Service.ProviderManagement;
using Jellyfin.Xtream.Service.Switching;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Unit tests for PreconnectPool.
/// Tests connection pooling, claiming, and lifecycle management.
/// </summary>
public sealed class PreconnectPoolTests : IDisposable
{
    private readonly PreconnectPool _pool;
    private readonly MockResilienceService _resilienceService;
    private readonly HttpClient _httpClient;

    public PreconnectPoolTests()
    {
        _resilienceService = new MockResilienceService();
        _httpClient = new HttpClient();
        _pool = new PreconnectPool(_httpClient, _resilienceService, poolSize: 3);
    }

    public void Dispose()
    {
        _pool.Dispose();
        _httpClient.Dispose();
    }

    [Fact]
    public void NewPool_HasConfiguredPoolSize()
    {
        Assert.Equal(3, _pool.PoolSize);
    }

    [Fact]
    public void NewPool_HasZeroActiveConnections()
    {
        Assert.Equal(0, _pool.ActiveConnections);
    }

    [Fact]
    public void NewPool_HasZeroStats()
    {
        Assert.Equal(0, _pool.TotalConnections);
        Assert.Equal(0, _pool.ConnectionHits);
        Assert.Equal(0, _pool.ConnectionMisses);
    }

    [Fact]
    public void TryGetConnection_EmptyPool_ReturnsFalse()
    {
        bool found = _pool.TryGetConnection("provider-1", out var connection);

        Assert.False(found);
        Assert.Null(connection);
        Assert.Equal(1, _pool.ConnectionMisses);
    }

    [Fact]
    public void TryGetConnection_WithStreamId_EmptyPool_ReturnsFalse()
    {
        bool found = _pool.TryGetConnection("provider-1", 100, out var connection);

        Assert.False(found);
        Assert.Null(connection);
    }

    [Fact]
    public void SetPoolSize_ClampsToValidRange()
    {
        _pool.SetPoolSize(0);
        Assert.Equal(1, _pool.PoolSize);

        _pool.SetPoolSize(10);
        Assert.Equal(5, _pool.PoolSize); // MaxPoolSize is 5
    }

    [Fact]
    public void PruneStaleConnections_EmptyPool_ReturnsZero()
    {
        int removed = _pool.PruneStaleConnections();

        Assert.Equal(0, removed);
    }

    [Fact]
    public void Clear_EmptyPool_DoesNotThrow()
    {
        _pool.Clear();

        Assert.Equal(0, _pool.ActiveConnections);
    }

    [Fact]
    public void HitRatePercent_NoAttempts_ReturnsZero()
    {
        Assert.Equal(0, _pool.HitRatePercent);
    }

    [Fact]
    public void HitRatePercent_AfterMisses_ReturnsZero()
    {
        _pool.TryGetConnection("p1", out _);
        _pool.TryGetConnection("p2", out _);

        Assert.Equal(0, _pool.HitRatePercent);
    }

    [Fact]
    public void PooledConnection_CreatedAt_IsUtcNow()
    {
        var before = DateTime.UtcNow;
        var connection = new PooledConnection
        {
            ProviderId = "p1",
            Provider = CreateProvider("p1"),
            StreamId = 100,
            ResponseStream = new MemoryStream(),
        };
        var after = DateTime.UtcNow;

        Assert.True(connection.CreatedAt >= before);
        Assert.True(connection.CreatedAt <= after);
    }

    [Fact]
    public void PooledConnection_AgeMs_IncreasesOverTime()
    {
        using var connection = new PooledConnection
        {
            ProviderId = "p1",
            Provider = CreateProvider("p1"),
            StreamId = 100,
            ResponseStream = new MemoryStream(),
        };

        long age1 = connection.AgeMs;
        System.Threading.Thread.Sleep(10);
        long age2 = connection.AgeMs;

        Assert.True(age2 > age1);
    }

    [Fact]
    public void PooledConnection_TryClaim_FirstCallSucceeds()
    {
        using var connection = new PooledConnection
        {
            ProviderId = "p1",
            Provider = CreateProvider("p1"),
            StreamId = 100,
            ResponseStream = new MemoryStream(),
        };

        bool claimed = connection.TryClaim();

        Assert.True(claimed);
        Assert.True(connection.IsClaimed);
    }

    [Fact]
    public void PooledConnection_TryClaim_SecondCallFails()
    {
        using var connection = new PooledConnection
        {
            ProviderId = "p1",
            Provider = CreateProvider("p1"),
            StreamId = 100,
            ResponseStream = new MemoryStream(),
        };

        connection.TryClaim();
        bool secondClaim = connection.TryClaim();

        Assert.False(secondClaim);
    }

    [Fact]
    public void PooledConnection_Dispose_MarksAsClaimed()
    {
        // Create connection, track IsClaimed state before/after using block
        bool wasClaimedBefore;
        bool wasClaimedAfter;

        // Create the connection to test
        var connection = new PooledConnection
        {
            ProviderId = "p1",
            Provider = CreateProvider("p1"),
            StreamId = 100,
            ResponseStream = new MemoryStream(),
        };

        wasClaimedBefore = connection.IsClaimed;

        // Dispose the connection
        ((IDisposable)connection).Dispose();

        wasClaimedAfter = connection.IsClaimed;

        // Verify state transition
        Assert.False(wasClaimedBefore);
        Assert.True(wasClaimedAfter);
    }

    [Fact]
    public void PooledConnection_Dispose_DisposesStream()
    {
        // Create stream outside so we can verify it's disposed after connection disposes
        var stream = new MemoryStream();
        bool streamWasDisposed;

        // Create and dispose connection in a using block
        using (
            var connection = new PooledConnection
            {
                ProviderId = "p1",
                Provider = CreateProvider("p1"),
                StreamId = 100,
                ResponseStream = stream,
            }
        )
        {
            // Connection and stream are valid here
            Assert.NotNull(connection.ResponseStream);
        }

        // After the using block, stream should be disposed
        try
        {
            stream.WriteByte(0);
            streamWasDisposed = false;
        }
        catch (ObjectDisposedException)
        {
            streamWasDisposed = true;
        }

        Assert.True(streamWasDisposed);
    }

    [Fact]
    public void PooledConnection_DoubleDispose_DoesNotThrow()
    {
        // Create connection
        var connection = new PooledConnection
        {
            ProviderId = "p1",
            Provider = CreateProvider("p1"),
            StreamId = 100,
            ResponseStream = new MemoryStream(),
        };

        // First dispose
        ((IDisposable)connection).Dispose();

        // Second dispose should not throw
        var exception = Record.Exception(() => ((IDisposable)connection).Dispose());
        Assert.Null(exception);
    }

    private static ProviderStreamInfo CreateProvider(string id)
    {
        var provider = new XtreamProvider
        {
            Id = id,
            Name = $"Provider {id}",
            BaseUrl = $"http://{id}.example.com",
            Username = "user",
            Password = "pass",
        };
        var stream = new Jellyfin.Xtream.Client.Models.StreamInfo { StreamId = 1, Name = "Test Channel" };
        return new ProviderStreamInfo(provider, stream);
    }

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
