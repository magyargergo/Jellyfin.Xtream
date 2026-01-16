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
using System.IO;
using System.Net.Http;
using Jellyfin.Xtream.Service.ProviderManagement;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Tests for <see cref="IPreconnectPool"/> related types.
/// </summary>
public sealed class PreconnectPoolTests
{
    #region PreconnectPoolConfiguration Tests

    [Fact]
    public void PreconnectPoolConfiguration_Default_HasReasonableValues()
    {
        var config = PreconnectPoolConfiguration.Default;

        Assert.Equal(TimeSpan.FromSeconds(30), config.MaxConnectionAge);
        Assert.Equal(2, config.MaxConnectionsPerHost);
        Assert.Equal(TimeSpan.FromSeconds(5), config.WarmupTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), config.CleanupInterval);
        Assert.Equal(8 * 1024, config.WarmupReadBytes);
    }

    [Fact]
    public void PreconnectPoolConfiguration_CustomValues_ArePreserved()
    {
        var config = new PreconnectPoolConfiguration
        {
            MaxConnectionAge = TimeSpan.FromSeconds(60),
            MaxConnectionsPerHost = 5,
            WarmupTimeout = TimeSpan.FromSeconds(10),
            CleanupInterval = TimeSpan.FromSeconds(20),
            WarmupReadBytes = 16 * 1024,
        };

        Assert.Equal(TimeSpan.FromSeconds(60), config.MaxConnectionAge);
        Assert.Equal(5, config.MaxConnectionsPerHost);
        Assert.Equal(TimeSpan.FromSeconds(10), config.WarmupTimeout);
        Assert.Equal(TimeSpan.FromSeconds(20), config.CleanupInterval);
        Assert.Equal(16 * 1024, config.WarmupReadBytes);
    }

    #endregion

    #region ConnectionPoolHealth Tests

    [Fact]
    public void ConnectionPoolHealth_Empty_HasZeroValues()
    {
        var health = ConnectionPoolHealth.Empty;

        Assert.Equal(0, health.AvailableConnections);
        Assert.Equal(0, health.InUseConnections);
        Assert.Equal(0, health.AverageAgeSeconds);
        Assert.Equal(0, health.SuccessRate);
        Assert.Equal(0, health.AverageWarmupLatencyMs);
        Assert.False(health.IsHealthy);
    }

    [Fact]
    public void ConnectionPoolHealth_IsHealthy_RequiresBothConditions()
    {
        // Not healthy - no available connections
        var health1 = new ConnectionPoolHealth { AvailableConnections = 0, SuccessRate = 1.0 };
        Assert.False(health1.IsHealthy);

        // Not healthy - low success rate
        var health2 = new ConnectionPoolHealth { AvailableConnections = 2, SuccessRate = 0.5 };
        Assert.False(health2.IsHealthy);

        // Healthy - both conditions met
        var health3 = new ConnectionPoolHealth { AvailableConnections = 2, SuccessRate = 0.8 };
        Assert.True(health3.IsHealthy);
    }

    [Fact]
    public void ConnectionPoolHealth_IsHealthy_RequiresExactly80PercentSuccessRate()
    {
        // Exactly 80% should be healthy
        var health = new ConnectionPoolHealth { AvailableConnections = 1, SuccessRate = 0.8 };
        Assert.True(health.IsHealthy);

        // Just below 80% should not be healthy
        var healthLow = new ConnectionPoolHealth { AvailableConnections = 1, SuccessRate = 0.79 };
        Assert.False(healthLow.IsHealthy);
    }

    [Fact]
    public void ConnectionPoolHealth_Equality_SameValues_AreEqual()
    {
        var health1 = new ConnectionPoolHealth { AvailableConnections = 5, InUseConnections = 2 };
        var health2 = new ConnectionPoolHealth { AvailableConnections = 5, InUseConnections = 2 };

        Assert.Equal(health1, health2);
        Assert.True(health1 == health2);
        Assert.False(health1 != health2);
    }

    [Fact]
    public void ConnectionPoolHealth_Equality_DifferentValues_AreNotEqual()
    {
        var health1 = new ConnectionPoolHealth { AvailableConnections = 5 };
        var health2 = new ConnectionPoolHealth { AvailableConnections = 3 };

        Assert.NotEqual(health1, health2);
        Assert.False(health1 == health2);
        Assert.True(health1 != health2);
    }

    [Fact]
    public void ConnectionPoolHealth_GetHashCode_ConsistentWithEquality()
    {
        var health1 = new ConnectionPoolHealth { AvailableConnections = 5, InUseConnections = 2 };
        var health2 = new ConnectionPoolHealth { AvailableConnections = 5, InUseConnections = 2 };

        Assert.Equal(health1.GetHashCode(), health2.GetHashCode());
    }

    #endregion

    #region PreconnectPoolStatistics Tests

    [Fact]
    public void PreconnectPoolStatistics_Default_AllZero()
    {
        var stats = new PreconnectPoolStatistics();

        Assert.Equal(0, stats.TotalWarmupAttempts);
        Assert.Equal(0, stats.SuccessfulWarmups);
        Assert.Equal(0, stats.FailedWarmups);
        Assert.Equal(0, stats.CacheHits);
        Assert.Equal(0, stats.CacheMisses);
        Assert.Equal(0, stats.Evictions);
        Assert.Equal(0, stats.AverageWarmupLatencyMs);
    }

    [Fact]
    public void PreconnectPoolStatistics_WarmupSuccessRatePercent_CalculatesCorrectly()
    {
        var stats = new PreconnectPoolStatistics { TotalWarmupAttempts = 100, SuccessfulWarmups = 80 };

        Assert.Equal(80.0, stats.WarmupSuccessRatePercent);
    }

    [Fact]
    public void PreconnectPoolStatistics_WarmupSuccessRatePercent_ZeroAttempts_ReturnsZero()
    {
        var stats = new PreconnectPoolStatistics { TotalWarmupAttempts = 0, SuccessfulWarmups = 0 };

        Assert.Equal(0.0, stats.WarmupSuccessRatePercent);
    }

    [Fact]
    public void PreconnectPoolStatistics_CacheHitRatePercent_CalculatesCorrectly()
    {
        var stats = new PreconnectPoolStatistics { CacheHits = 75, CacheMisses = 25 };

        Assert.Equal(75.0, stats.CacheHitRatePercent);
    }

    [Fact]
    public void PreconnectPoolStatistics_CacheHitRatePercent_NoAccesses_ReturnsZero()
    {
        var stats = new PreconnectPoolStatistics { CacheHits = 0, CacheMisses = 0 };

        Assert.Equal(0.0, stats.CacheHitRatePercent);
    }

    [Fact]
    public void PreconnectPoolStatistics_CacheHitRatePercent_AllHits_Returns100()
    {
        var stats = new PreconnectPoolStatistics { CacheHits = 50, CacheMisses = 0 };

        Assert.Equal(100.0, stats.CacheHitRatePercent);
    }

    [Fact]
    public void PreconnectPoolStatistics_Equality_SameValues_AreEqual()
    {
        var stats1 = new PreconnectPoolStatistics { TotalWarmupAttempts = 100, CacheHits = 50 };
        var stats2 = new PreconnectPoolStatistics { TotalWarmupAttempts = 100, CacheHits = 50 };

        Assert.Equal(stats1, stats2);
        Assert.True(stats1 == stats2);
    }

    [Fact]
    public void PreconnectPoolStatistics_Equality_DifferentValues_AreNotEqual()
    {
        var stats1 = new PreconnectPoolStatistics { TotalWarmupAttempts = 100 };
        var stats2 = new PreconnectPoolStatistics { TotalWarmupAttempts = 200 };

        Assert.NotEqual(stats1, stats2);
        Assert.True(stats1 != stats2);
    }

    #endregion

    #region PreconnectSlot Tests

    [Fact]
    public void PreconnectSlot_Constructor_SetsProperties()
    {
        var hostUri = new Uri("http://example.com:8080");
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        var stream = new MemoryStream();
        var warmedAt = DateTime.UtcNow.AddMinutes(-1);

        var slot = new PreconnectSlot(hostUri, response, stream, warmedAt);

        Assert.Equal(hostUri, slot.HostUri);
        Assert.Equal(response, slot.Response);
        Assert.Equal(stream, slot.Stream);
        Assert.Equal(warmedAt, slot.WarmedAt);
        Assert.False(slot.HasError);
        Assert.False(slot.IsDisposed);
    }

    [Fact]
    public void PreconnectSlot_Age_CalculatesCorrectly()
    {
        var warmedAt = DateTime.UtcNow.AddSeconds(-10);
        using var slot = new PreconnectSlot(
            new Uri("http://example.com"),
            new HttpResponseMessage(),
            new MemoryStream(),
            warmedAt
        );

        // Allow some tolerance for test execution time
        Assert.True(slot.Age.TotalSeconds >= 10);
        Assert.True(slot.Age.TotalSeconds < 12);
    }

    [Fact]
    public void PreconnectSlot_HasError_DefaultFalse()
    {
        using var slot = new PreconnectSlot(
            new Uri("http://example.com"),
            new HttpResponseMessage(),
            new MemoryStream(),
            DateTime.UtcNow
        );

        Assert.False(slot.HasError);

        slot.HasError = true;
        Assert.True(slot.HasError);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "IDisposableAnalyzers.Correctness",
        "IDISP016:Don't use disposed instance",
        Justification = "Testing dispose behavior"
    )]
    public void PreconnectSlot_Dispose_SetsIsDisposed()
    {
        // These tests intentionally test Dispose behavior
        // Use a helper to verify state after dispose
        var isDisposedBefore = false;
        var isDisposedAfter = false;

        using (
            var slot = new PreconnectSlot(
                new Uri("http://example.com"),
                new HttpResponseMessage(),
                new MemoryStream(),
                DateTime.UtcNow
            )
        )
        {
            isDisposedBefore = slot.IsDisposed;
            slot.HasError = true;
            slot.Dispose();
            isDisposedAfter = slot.IsDisposed;
        }

        Assert.False(isDisposedBefore);
        Assert.True(isDisposedAfter);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "IDisposableAnalyzers.Correctness",
        "IDISP016:Don't use disposed instance",
        Justification = "Testing dispose idempotency"
    )]
    public void PreconnectSlot_Dispose_Idempotent()
    {
        // Verify multiple dispose calls are safe by capturing state
        var disposeCount = 0;
        var isDisposedFinal = false;

        using (
            var slot = new PreconnectSlot(
                new Uri("http://example.com"),
                new HttpResponseMessage(),
                new MemoryStream(),
                DateTime.UtcNow
            )
        )
        {
            slot.HasError = true;

            // Multiple dispose calls should be safe - the using block calls dispose too
            slot.Dispose();
            disposeCount++;
            isDisposedFinal = slot.IsDisposed;
        }

        disposeCount++; // using block disposed again
        Assert.True(isDisposedFinal);
        Assert.Equal(2, disposeCount);
    }

    [Fact]
    public void PreconnectSlot_Dispose_CallsCallback()
    {
        var callbackInvoked = false;

        using (
            var slot = new PreconnectSlot(
                new Uri("http://example.com"),
                new HttpResponseMessage(),
                new MemoryStream(),
                DateTime.UtcNow,
                _ => callbackInvoked = true
            )
        )
        {
            // Without error, callback should be invoked on dispose
            _ = slot; // Suppress unused variable warning
        }

        Assert.True(callbackInvoked);
    }

    [Fact]
    public void PreconnectSlot_Dispose_WithError_DoesNotCallCallback()
    {
        var callbackInvoked = false;

        using (
            var slot = new PreconnectSlot(
                new Uri("http://example.com"),
                new HttpResponseMessage(),
                new MemoryStream(),
                DateTime.UtcNow,
                _ => callbackInvoked = true
            )
        )
        {
            slot.HasError = true;
        }

        Assert.False(callbackInvoked);
    }

    #endregion
}
