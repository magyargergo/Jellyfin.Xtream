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

using Jellyfin.Xtream.Service.ProviderManagement;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Tests for provider switch service types and result structs.
/// </summary>
public sealed class ProviderSwitchServiceTests
{
    #region ProviderSwitchResult Tests

    [Fact]
    public void ProviderSwitchResult_Succeeded_HasCorrectValues()
    {
        var result = ProviderSwitchResult.Succeeded(
            "http://new.url",
            150,
            timestampRemappingActive: true,
            alignedToKeyframe: true
        );

        Assert.True(result.Success);
        Assert.Equal("http://new.url", result.NewUrl);
        Assert.Equal(150, result.DurationMs);
        Assert.True(result.TimestampRemappingActive);
        Assert.True(result.AlignedToKeyframe);
        Assert.Equal(SwitchFailureReason.None, result.FailureReason);
    }

    [Fact]
    public void ProviderSwitchResult_Succeeded_DefaultFlags_AreFalse()
    {
        var result = ProviderSwitchResult.Succeeded("http://new.url", 100);

        Assert.True(result.Success);
        Assert.False(result.TimestampRemappingActive);
        Assert.False(result.AlignedToKeyframe);
    }

    [Fact]
    public void ProviderSwitchResult_Failed_HasCorrectValues()
    {
        var result = ProviderSwitchResult.Failed(SwitchFailureReason.Timeout, "Timed out");

        Assert.False(result.Success);
        Assert.Null(result.NewUrl);
        Assert.Equal(SwitchFailureReason.Timeout, result.FailureReason);
        Assert.Equal("Timed out", result.FailureMessage);
    }

    [Fact]
    public void ProviderSwitchResult_Failed_WithoutMessage_HasNullMessage()
    {
        var result = ProviderSwitchResult.Failed(SwitchFailureReason.Error);

        Assert.False(result.Success);
        Assert.Equal(SwitchFailureReason.Error, result.FailureReason);
        Assert.Null(result.FailureMessage);
    }

    [Fact]
    public void ProviderSwitchResult_CooldownActive_HasCorrectReason()
    {
        var result = ProviderSwitchResult.CooldownActive;

        Assert.False(result.Success);
        Assert.Equal(SwitchFailureReason.CooldownActive, result.FailureReason);
    }

    [Fact]
    public void ProviderSwitchResult_MaxAttemptsReached_HasCorrectReason()
    {
        var result = ProviderSwitchResult.MaxAttemptsReached;

        Assert.False(result.Success);
        Assert.Equal(SwitchFailureReason.MaxAttemptsReached, result.FailureReason);
    }

    [Fact]
    public void ProviderSwitchResult_StreamNotRegistered_HasCorrectReason()
    {
        var result = ProviderSwitchResult.StreamNotRegistered;

        Assert.False(result.Success);
        Assert.Equal(SwitchFailureReason.StreamNotRegistered, result.FailureReason);
    }

    [Fact]
    public void ProviderSwitchResult_Equality_SameValues_AreEqual()
    {
        var result1 = ProviderSwitchResult.Succeeded("http://test", 100);
        var result2 = ProviderSwitchResult.Succeeded("http://test", 100);

        Assert.Equal(result1, result2);
        Assert.True(result1 == result2);
        Assert.False(result1 != result2);
    }

    [Fact]
    public void ProviderSwitchResult_Equality_DifferentSuccess_AreNotEqual()
    {
        var result1 = ProviderSwitchResult.Succeeded("http://test", 100);
        var result2 = ProviderSwitchResult.Failed(SwitchFailureReason.Error);

        Assert.NotEqual(result1, result2);
        Assert.False(result1 == result2);
        Assert.True(result1 != result2);
    }

    #endregion

    #region SwitchStatistics Tests

    [Fact]
    public void SwitchStatistics_Default_AllZero()
    {
        var stats = new SwitchStatistics();

        Assert.Equal(0, stats.ActiveStreams);
        Assert.Equal(0, stats.TotalSwitches);
        Assert.Equal(0, stats.SuccessfulSwitches);
        Assert.Equal(0, stats.FailedSwitches);
        Assert.Equal(0.0, stats.AverageSwitchLatencyMs);
    }

    [Fact]
    public void SwitchStatistics_SuccessRatePercent_CalculatesCorrectly()
    {
        var stats = new SwitchStatistics { TotalSwitches = 100, SuccessfulSwitches = 75 };

        Assert.Equal(75.0, stats.SuccessRatePercent);
    }

    [Fact]
    public void SwitchStatistics_SuccessRatePercent_ZeroTotal_ReturnsZero()
    {
        var stats = new SwitchStatistics { TotalSwitches = 0, SuccessfulSwitches = 0 };

        Assert.Equal(0.0, stats.SuccessRatePercent);
    }

    [Fact]
    public void SwitchStatistics_SuccessRatePercent_AllSuccessful_Returns100()
    {
        var stats = new SwitchStatistics { TotalSwitches = 50, SuccessfulSwitches = 50 };

        Assert.Equal(100.0, stats.SuccessRatePercent);
    }

    [Fact]
    public void SwitchStatistics_Equality_SameValues_AreEqual()
    {
        var stats1 = new SwitchStatistics { ActiveStreams = 5, TotalSwitches = 100 };
        var stats2 = new SwitchStatistics { ActiveStreams = 5, TotalSwitches = 100 };

        Assert.Equal(stats1, stats2);
        Assert.True(stats1 == stats2);
    }

    [Fact]
    public void SwitchStatistics_Equality_DifferentTotalSwitches_AreNotEqual()
    {
        var stats1 = new SwitchStatistics { TotalSwitches = 100 };
        var stats2 = new SwitchStatistics { TotalSwitches = 200 };

        Assert.NotEqual(stats1, stats2);
        Assert.True(stats1 != stats2);
    }

    #endregion

    #region SwitchReason Tests

    [Fact]
    public void SwitchReason_AllValues_AreDefined()
    {
        Assert.True(Enum.IsDefined(SwitchReason.HealthDegraded));
        Assert.True(Enum.IsDefined(SwitchReason.ConnectionFailed));
        Assert.True(Enum.IsDefined(SwitchReason.CapacityReached));
        Assert.True(Enum.IsDefined(SwitchReason.BetterProviderAvailable));
        Assert.True(Enum.IsDefined(SwitchReason.Manual));
    }

    #endregion

    #region SwitchFailureReason Tests

    [Fact]
    public void SwitchFailureReason_AllValues_AreDefined()
    {
        Assert.True(Enum.IsDefined(SwitchFailureReason.None));
        Assert.True(Enum.IsDefined(SwitchFailureReason.StreamNotRegistered));
        Assert.True(Enum.IsDefined(SwitchFailureReason.CooldownActive));
        Assert.True(Enum.IsDefined(SwitchFailureReason.MaxAttemptsReached));
        Assert.True(Enum.IsDefined(SwitchFailureReason.NoAlternativeProvider));
        Assert.True(Enum.IsDefined(SwitchFailureReason.Timeout));
        Assert.True(Enum.IsDefined(SwitchFailureReason.ConnectionFailed));
        Assert.True(Enum.IsDefined(SwitchFailureReason.AlignmentFailed));
        Assert.True(Enum.IsDefined(SwitchFailureReason.Error));
    }

    #endregion

    #region ProviderSwitchConfiguration Tests

    [Fact]
    public void ProviderSwitchConfiguration_Default_HasReasonableValues()
    {
        var config = ProviderSwitchConfiguration.Default;

        Assert.True(config.CooldownMs > 0);
        Assert.True(config.MaxAttemptsPerSession > 0);
        Assert.True(config.TimeoutMs > 0);
        Assert.True(config.UseByteAlignment);
        Assert.True(config.WaitForKeyframe);
        Assert.True(config.UseTimestampRemapping);
    }

    [Fact]
    public void ProviderSwitchConfiguration_CustomValues_ArePreserved()
    {
        var config = new ProviderSwitchConfiguration
        {
            CooldownMs = 5000,
            MaxAttemptsPerSession = 20,
            TimeoutMs = 10000,
            UseByteAlignment = false,
            WaitForKeyframe = false,
            UseTimestampRemapping = false,
        };

        Assert.Equal(5000, config.CooldownMs);
        Assert.Equal(20, config.MaxAttemptsPerSession);
        Assert.Equal(10000, config.TimeoutMs);
        Assert.False(config.UseByteAlignment);
        Assert.False(config.WaitForKeyframe);
        Assert.False(config.UseTimestampRemapping);
    }

    #endregion
}
