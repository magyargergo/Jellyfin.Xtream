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
using Jellyfin.Xtream.Service.ProviderManagement;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Tests for <see cref="IChannelWarmupService"/> related types.
/// </summary>
public sealed class ChannelWarmupServiceTests
{
    #region ChannelWarmupConfiguration Tests

    [Fact]
    public void ChannelWarmupConfiguration_Default_HasReasonableValues()
    {
        var config = ChannelWarmupConfiguration.Default;

        Assert.Equal(3, config.AdjacentChannelCount);
        Assert.Equal(10, config.MaxWarmedChannels);
        Assert.Equal(TimeSpan.FromSeconds(60), config.WarmupExpiry);
        Assert.Equal(TimeSpan.FromMilliseconds(500), config.GuideNavigationDebounce);
        Assert.True(config.EnablePredictiveWarmup);
    }

    [Fact]
    public void ChannelWarmupConfiguration_CustomValues_ArePreserved()
    {
        var config = new ChannelWarmupConfiguration
        {
            AdjacentChannelCount = 5,
            MaxWarmedChannels = 20,
            WarmupExpiry = TimeSpan.FromSeconds(120),
            GuideNavigationDebounce = TimeSpan.FromMilliseconds(1000),
            EnablePredictiveWarmup = false,
        };

        Assert.Equal(5, config.AdjacentChannelCount);
        Assert.Equal(20, config.MaxWarmedChannels);
        Assert.Equal(TimeSpan.FromSeconds(120), config.WarmupExpiry);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), config.GuideNavigationDebounce);
        Assert.False(config.EnablePredictiveWarmup);
    }

    #endregion

    #region ChannelWarmupResult Tests

    [Fact]
    public void ChannelWarmupResult_Succeeded_HasCorrectValues()
    {
        var result = ChannelWarmupResult.Succeeded(
            channelId: "channel123",
            warmedProviders: 3,
            totalProviders: 5,
            durationMs: 250
        );

        Assert.True(result.Success);
        Assert.Equal("channel123", result.ChannelId);
        Assert.Equal(3, result.WarmedProviders);
        Assert.Equal(5, result.TotalProviders);
        Assert.Equal(250, result.DurationMs);
        Assert.Null(result.FailureMessage);
    }

    [Fact]
    public void ChannelWarmupResult_Failed_HasCorrectValues()
    {
        var result = ChannelWarmupResult.Failed("channel456", "Connection timeout");

        Assert.False(result.Success);
        Assert.Equal("channel456", result.ChannelId);
        Assert.Equal(0, result.WarmedProviders);
        Assert.Equal(0, result.TotalProviders);
        Assert.Equal("Connection timeout", result.FailureMessage);
    }

    [Fact]
    public void ChannelWarmupResult_Equality_SameValues_AreEqual()
    {
        var result1 = ChannelWarmupResult.Succeeded("channel1", 2, 3, 100);
        var result2 = ChannelWarmupResult.Succeeded("channel1", 2, 3, 100);

        Assert.Equal(result1, result2);
        Assert.True(result1 == result2);
        Assert.False(result1 != result2);
    }

    [Fact]
    public void ChannelWarmupResult_Equality_DifferentChannelId_AreNotEqual()
    {
        var result1 = ChannelWarmupResult.Succeeded("channel1", 2, 3, 100);
        var result2 = ChannelWarmupResult.Succeeded("channel2", 2, 3, 100);

        Assert.NotEqual(result1, result2);
        Assert.False(result1 == result2);
        Assert.True(result1 != result2);
    }

    [Fact]
    public void ChannelWarmupResult_Equality_DifferentSuccess_AreNotEqual()
    {
        var result1 = ChannelWarmupResult.Succeeded("channel1", 2, 3, 100);
        var result2 = ChannelWarmupResult.Failed("channel1", "Error");

        Assert.NotEqual(result1, result2);
    }

    [Fact]
    public void ChannelWarmupResult_GetHashCode_ConsistentWithEquality()
    {
        var result1 = ChannelWarmupResult.Succeeded("channel1", 2, 3, 100);
        var result2 = ChannelWarmupResult.Succeeded("channel1", 2, 3, 100);

        Assert.Equal(result1.GetHashCode(), result2.GetHashCode());
    }

    #endregion

    #region ChannelWarmupRequest Tests

    [Fact]
    public void ChannelWarmupRequest_Constructor_SetsProperties()
    {
        var providerUrls = new[] { "http://provider1.com", "http://provider2.com" };
        var request = new ChannelWarmupRequest("channel123", providerUrls, 5);

        Assert.Equal("channel123", request.ChannelId);
        Assert.Equal(providerUrls, request.ProviderUrls);
        Assert.Equal(5, request.Priority);
    }

    [Fact]
    public void ChannelWarmupRequest_DefaultPriority_IsZero()
    {
        var request = new ChannelWarmupRequest("channel123", Array.Empty<string>());

        Assert.Equal(0, request.Priority);
    }

    #endregion

    #region ChannelWarmupStatus Tests

    [Fact]
    public void ChannelWarmupStatus_AllValues_AreDefined()
    {
        Assert.True(Enum.IsDefined(ChannelWarmupStatus.None));
        Assert.True(Enum.IsDefined(ChannelWarmupStatus.InProgress));
        Assert.True(Enum.IsDefined(ChannelWarmupStatus.Ready));
        Assert.True(Enum.IsDefined(ChannelWarmupStatus.Failed));
        Assert.True(Enum.IsDefined(ChannelWarmupStatus.Expired));
    }

    #endregion

    #region ChannelWarmupStatistics Tests

    [Fact]
    public void ChannelWarmupStatistics_Default_AllZero()
    {
        var stats = new ChannelWarmupStatistics();

        Assert.Equal(0, stats.TotalWarmupRequests);
        Assert.Equal(0, stats.SuccessfulWarmups);
        Assert.Equal(0, stats.FailedWarmups);
        Assert.Equal(0, stats.CancelledWarmups);
        Assert.Equal(0, stats.PredictionHits);
        Assert.Equal(0, stats.PredictionMisses);
        Assert.Equal(0, stats.AverageWarmupLatencyMs);
        Assert.Equal(0, stats.AverageTimeSavedMs);
    }

    [Fact]
    public void ChannelWarmupStatistics_SuccessRatePercent_CalculatesCorrectly()
    {
        var stats = new ChannelWarmupStatistics { TotalWarmupRequests = 100, SuccessfulWarmups = 85 };

        Assert.Equal(85.0, stats.SuccessRatePercent);
    }

    [Fact]
    public void ChannelWarmupStatistics_SuccessRatePercent_ZeroRequests_ReturnsZero()
    {
        var stats = new ChannelWarmupStatistics { TotalWarmupRequests = 0, SuccessfulWarmups = 0 };

        Assert.Equal(0.0, stats.SuccessRatePercent);
    }

    [Fact]
    public void ChannelWarmupStatistics_PredictionAccuracyPercent_CalculatesCorrectly()
    {
        var stats = new ChannelWarmupStatistics { PredictionHits = 80, PredictionMisses = 20 };

        Assert.Equal(80.0, stats.PredictionAccuracyPercent);
    }

    [Fact]
    public void ChannelWarmupStatistics_PredictionAccuracyPercent_NoPredictions_ReturnsZero()
    {
        var stats = new ChannelWarmupStatistics { PredictionHits = 0, PredictionMisses = 0 };

        Assert.Equal(0.0, stats.PredictionAccuracyPercent);
    }

    [Fact]
    public void ChannelWarmupStatistics_PredictionAccuracyPercent_AllHits_Returns100()
    {
        var stats = new ChannelWarmupStatistics { PredictionHits = 50, PredictionMisses = 0 };

        Assert.Equal(100.0, stats.PredictionAccuracyPercent);
    }

    [Fact]
    public void ChannelWarmupStatistics_Equality_SameValues_AreEqual()
    {
        var stats1 = new ChannelWarmupStatistics { TotalWarmupRequests = 100, PredictionHits = 80 };
        var stats2 = new ChannelWarmupStatistics { TotalWarmupRequests = 100, PredictionHits = 80 };

        Assert.Equal(stats1, stats2);
        Assert.True(stats1 == stats2);
    }

    [Fact]
    public void ChannelWarmupStatistics_Equality_DifferentValues_AreNotEqual()
    {
        var stats1 = new ChannelWarmupStatistics { TotalWarmupRequests = 100 };
        var stats2 = new ChannelWarmupStatistics { TotalWarmupRequests = 200 };

        Assert.NotEqual(stats1, stats2);
        Assert.True(stats1 != stats2);
    }

    [Fact]
    public void ChannelWarmupStatistics_GetHashCode_ConsistentWithEquality()
    {
        var stats1 = new ChannelWarmupStatistics { TotalWarmupRequests = 100, PredictionHits = 80 };
        var stats2 = new ChannelWarmupStatistics { TotalWarmupRequests = 100, PredictionHits = 80 };

        Assert.Equal(stats1.GetHashCode(), stats2.GetHashCode());
    }

    #endregion
}
