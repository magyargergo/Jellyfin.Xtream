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
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service;
using Jellyfin.Xtream.Service.Resilience;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Tests for ProviderHealthScorer and health-aware provider selection.
/// </summary>
public sealed class ProviderHealthScorerTests
{
    private static XtreamProvider CreateProvider(string id, string name) => new() { Id = id, Name = name };

    private static StreamInfo CreateStream(int id, string name) => new() { StreamId = id, Name = name };

    [Fact]
    public void GetScore_UnknownProvider_ReturnsNeutralScore()
    {
        var scorer = new ProviderHealthScorer();
        var score = scorer.GetScore("unknown-provider");
        Assert.Equal(50.0, score);
    }

    [Fact]
    public void RecordSuccess_IncreasesScore()
    {
        var scorer = new ProviderHealthScorer();

        scorer.RecordSuccess("provider1", qualityScore: 90, connectionTimeMs: 500);

        var score = scorer.GetScore("provider1");
        Assert.True(score > 50, "Score should be above neutral after success");
    }

    [Fact]
    public void RecordFailure_DecreasesScore()
    {
        var scorer = new ProviderHealthScorer();

        // First establish a good score
        scorer.RecordSuccess("provider1", qualityScore: 80, connectionTimeMs: 500);
        var initialScore = scorer.GetScore("provider1");

        // Then record failure
        scorer.RecordFailure("provider1", FailureType.ConnectionTimeout);

        var newScore = scorer.GetScore("provider1");
        Assert.True(newScore < initialScore, "Score should decrease after failure");
    }

    [Fact]
    public void ConsecutiveFailures_DecreaseScoreSignificantly()
    {
        var scorer = new ProviderHealthScorer();

        scorer.RecordFailure("provider1", FailureType.ConnectionTimeout);
        var scoreAfterOne = scorer.GetScore("provider1");

        scorer.RecordFailure("provider1", FailureType.ConnectionTimeout);
        var scoreAfterTwo = scorer.GetScore("provider1");

        scorer.RecordFailure("provider1", FailureType.ConnectionTimeout);
        var scoreAfterThree = scorer.GetScore("provider1");

        Assert.True(scoreAfterTwo < scoreAfterOne);
        Assert.True(scoreAfterThree < scoreAfterTwo);
    }

    [Fact]
    public void ShouldSwitch_RequiresHysteresisThreshold()
    {
        var scorer = new ProviderHealthScorer();

        // Provider1: Poor (low quality, high latency)
        scorer.RecordSuccess("provider1", qualityScore: 40, connectionTimeMs: 3000);

        // Provider2: Excellent (high quality, low latency)
        scorer.RecordSuccess("provider2", qualityScore: 95, connectionTimeMs: 100);

        // Should recommend switch because provider2 is significantly better
        var shouldSwitch = scorer.ShouldSwitch("provider1", "provider2", hysteresisThreshold: 10);
        Assert.True(shouldSwitch);
    }

    [Fact]
    public void ShouldSwitch_ReturnsFalseWhenScoresSimilar()
    {
        var scorer = new ProviderHealthScorer();

        scorer.RecordSuccess("provider1", qualityScore: 80, connectionTimeMs: 500);
        scorer.RecordSuccess("provider2", qualityScore: 82, connectionTimeMs: 480);

        // Should not recommend switch because scores are similar
        var shouldSwitch = scorer.ShouldSwitch("provider1", "provider2", hysteresisThreshold: 15);
        Assert.False(shouldSwitch);
    }

    [Fact]
    public void GetProvidersByScore_ReturnsOrderedList()
    {
        var scorer = new ProviderHealthScorer();

        scorer.RecordSuccess("provider1", qualityScore: 60, connectionTimeMs: 1000);
        scorer.RecordSuccess("provider2", qualityScore: 90, connectionTimeMs: 300);
        scorer.RecordSuccess("provider3", qualityScore: 75, connectionTimeMs: 600);

        var ordered = scorer.GetProvidersByScore();

        Assert.Equal(3, ordered.Count);
        Assert.Equal("provider2", ordered[0].ProviderId);
        Assert.Equal("provider3", ordered[1].ProviderId);
        Assert.Equal("provider1", ordered[2].ProviderId);
    }

    [Fact]
    public void GetStatus_ReturnsCompleteHealthInfo()
    {
        var scorer = new ProviderHealthScorer();

        scorer.RecordSuccess("provider1", qualityScore: 85, connectionTimeMs: 400);
        scorer.RecordSuccess("provider1", qualityScore: 90, connectionTimeMs: 350);
        scorer.RecordFailure("provider1", FailureType.DataStall);

        var status = scorer.GetStatus("provider1");

        Assert.Equal("provider1", status.ProviderId);
        Assert.Equal(3, status.SampleCount);
        Assert.Equal(1, status.ConsecutiveFailures);
        Assert.NotNull(status.LastSuccessTime);
        Assert.NotNull(status.LastFailureTime);
    }

    [Fact]
    public void GetNextHealthyProvider_ReturnsHealthiestProvider()
    {
        var scorer = new ProviderHealthScorer();

        // Set up different health levels
        scorer.RecordSuccess("p1", qualityScore: 60, connectionTimeMs: 1000);
        scorer.RecordSuccess("p2", qualityScore: 95, connectionTimeMs: 200);
        scorer.RecordSuccess("p3", qualityScore: 75, connectionTimeMs: 500);

        var provider1 = CreateProvider("p1", "Provider 1");
        var provider2 = CreateProvider("p2", "Provider 2");
        var provider3 = CreateProvider("p3", "Provider 3");

        var streams = new ProviderStreamInfo[]
        {
            new(provider1, CreateStream(1, "Channel")),
            new(provider2, CreateStream(2, "Channel")),
            new(provider3, CreateStream(3, "Channel")),
        };

        var map = ChannelProviderMap.Build(streams);
        var channelGuid = StreamService.ToProviderGuid(StreamService.LiveTvPrefix, provider1, 1);

        // Act
        var selected = map.GetNextHealthyProvider(channelGuid, new HashSet<string>(StringComparer.Ordinal), scorer);

        // Assert - should select provider2 (healthiest)
        Assert.NotNull(selected);
        Assert.Equal("p2", selected.Provider.Id);
    }

    [Fact]
    public void GetNextHealthyProvider_ExcludesFailedProviders()
    {
        var scorer = new ProviderHealthScorer();

        scorer.RecordSuccess("p1", qualityScore: 60, connectionTimeMs: 1000);
        scorer.RecordSuccess("p2", qualityScore: 95, connectionTimeMs: 200);

        var provider1 = CreateProvider("p1", "Provider 1");
        var provider2 = CreateProvider("p2", "Provider 2");

        var streams = new ProviderStreamInfo[]
        {
            new(provider1, CreateStream(1, "Channel")),
            new(provider2, CreateStream(2, "Channel")),
        };

        var map = ChannelProviderMap.Build(streams);
        var channelGuid = StreamService.ToProviderGuid(StreamService.LiveTvPrefix, provider1, 1);

        // Act - exclude the healthiest provider
        var failedIds = new HashSet<string>(StringComparer.Ordinal) { "p2" };
        var selected = map.GetNextHealthyProvider(channelGuid, failedIds, scorer);

        // Assert - should select provider1 (only remaining)
        Assert.NotNull(selected);
        Assert.Equal("p1", selected.Provider.Id);
    }

    [Fact]
    public void GetNextHealthyProvider_ReturnsNullWhenAllFailed()
    {
        var scorer = new ProviderHealthScorer();

        var provider1 = CreateProvider("p1", "Provider 1");

        var streams = new ProviderStreamInfo[] { new(provider1, CreateStream(1, "Channel")) };

        var map = ChannelProviderMap.Build(streams);
        var channelGuid = StreamService.ToProviderGuid(StreamService.LiveTvPrefix, provider1, 1);

        // Act - exclude all providers
        var failedIds = new HashSet<string>(StringComparer.Ordinal) { "p1" };
        var selected = map.GetNextHealthyProvider(channelGuid, failedIds, scorer);

        // Assert
        Assert.Null(selected);
    }

    [Fact]
    public void HealthLevel_Classification()
    {
        var scorer = new ProviderHealthScorer();

        // Excellent (90+)
        for (int i = 0; i < 10; i++)
        {
            scorer.RecordSuccess("excellent", qualityScore: 95, connectionTimeMs: 200);
        }

        // Poor (failures)
        for (int i = 0; i < 5; i++)
        {
            scorer.RecordFailure("poor", FailureType.ConnectionTimeout);
        }

        var excellentStatus = scorer.GetStatus("excellent");
        var poorStatus = scorer.GetStatus("poor");

        Assert.Equal(HealthLevel.Excellent, excellentStatus.Level);
        Assert.True(poorStatus.Level <= HealthLevel.Fair);
    }

    [Fact]
    public void Reset_ClearsAllScores()
    {
        var scorer = new ProviderHealthScorer();

        scorer.RecordSuccess("provider1", qualityScore: 90, connectionTimeMs: 500);

        scorer.Reset();

        var score = scorer.GetScore("provider1");
        Assert.Equal(50.0, score); // Back to neutral
    }
}
