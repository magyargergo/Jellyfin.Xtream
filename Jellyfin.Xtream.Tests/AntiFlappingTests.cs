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
using System.Reflection;
using Jellyfin.Xtream.Service.ProviderManagement;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Tests for anti-flapping mechanisms including <see cref="AdaptiveCooldownStrategy"/>
/// and <see cref="ViolationSwitchTrigger"/> cooldown behavior.
/// </summary>
public sealed class AntiFlappingTests
{
    #region AdaptiveCooldownStrategy - Initialization Tests

    [Fact]
    public void AdaptiveCooldown_DefaultInitialization()
    {
        // Arrange & Act
        var strategy = CreateAdaptiveCooldownStrategy();

        // Assert
        Assert.Equal(5000, strategy.CurrentCooldownMs);
        Assert.Equal(1.0, strategy.SuccessRate);
        Assert.Equal(0, strategy.SampleCount);
    }

    [Fact]
    public void AdaptiveCooldown_CustomInitialization()
    {
        // Arrange & Act
        var strategy = CreateAdaptiveCooldownStrategy(10000);

        // Assert
        Assert.Equal(10000, strategy.CurrentCooldownMs);
    }

    [Fact]
    public void AdaptiveCooldown_InitializationClampsToMin()
    {
        // Arrange & Act - Below minimum of 2000ms
        var strategy = CreateAdaptiveCooldownStrategy(500);

        // Assert - Clamped to minimum
        Assert.Equal(2000, strategy.CurrentCooldownMs);
    }

    [Fact]
    public void AdaptiveCooldown_InitializationClampsToMax()
    {
        // Arrange & Act - Above maximum of 30000ms
        var strategy = CreateAdaptiveCooldownStrategy(60000);

        // Assert - Clamped to maximum
        Assert.Equal(30000, strategy.CurrentCooldownMs);
    }

    #endregion

    #region AdaptiveCooldownStrategy - Result Recording Tests

    [Fact]
    public void AdaptiveCooldown_RecordResult_IncrementsSampleCount()
    {
        // Arrange
        var strategy = CreateAdaptiveCooldownStrategy();

        // Act
        strategy.RecordResult(true);
        strategy.RecordResult(false);
        strategy.RecordResult(true);

        // Assert
        Assert.Equal(3, strategy.SampleCount);
    }

    [Fact]
    public void AdaptiveCooldown_SuccessRate_AllSuccesses()
    {
        // Arrange
        var strategy = CreateAdaptiveCooldownStrategy();

        // Act - Record 10 successes to fill the entire history buffer
        for (var i = 0; i < 10; i++)
        {
            strategy.RecordResult(true);
        }

        // Assert
        Assert.Equal(1.0, strategy.SuccessRate);
    }

    [Fact]
    public void AdaptiveCooldown_SuccessRate_AllFailures()
    {
        // Arrange
        var strategy = CreateAdaptiveCooldownStrategy();

        // Act
        for (var i = 0; i < 5; i++)
        {
            strategy.RecordResult(false);
        }

        // Assert
        Assert.Equal(0.0, strategy.SuccessRate);
    }

    [Fact]
    public void AdaptiveCooldown_SuccessRate_MixedResults()
    {
        // Arrange
        var strategy = CreateAdaptiveCooldownStrategy();

        // Act - 3 successes, 2 failures
        strategy.RecordResult(true);
        strategy.RecordResult(true);
        strategy.RecordResult(false);
        strategy.RecordResult(true);
        strategy.RecordResult(false);

        // Assert - 3/5 = 0.6
        Assert.Equal(0.6, strategy.SuccessRate);
    }

    [Fact]
    public void AdaptiveCooldown_SampleCount_CapsAtHistorySize()
    {
        // Arrange
        var strategy = CreateAdaptiveCooldownStrategy();

        // Act - Record more than history size (10)
        for (var i = 0; i < 15; i++)
        {
            strategy.RecordResult(i % 2 == 0);
        }

        // Assert - Sample count capped at 10
        Assert.Equal(10, strategy.SampleCount);
    }

    #endregion

    #region AdaptiveCooldownStrategy - Cooldown Elapsed Tests

    [Fact]
    public void AdaptiveCooldown_HasCooldownElapsed_TrueAfterPeriod()
    {
        // Arrange
        var strategy = CreateAdaptiveCooldownStrategy(100); // 100ms cooldown (clamped to 2000)
        var pastTime = DateTime.UtcNow.AddMilliseconds(-3000); // 3 seconds ago

        // Act & Assert - 2000ms cooldown should have elapsed
        Assert.True(strategy.HasCooldownElapsed(pastTime));
    }

    [Fact]
    public void AdaptiveCooldown_HasCooldownElapsed_FalseDuringPeriod()
    {
        // Arrange
        var strategy = CreateAdaptiveCooldownStrategy(); // 5000ms cooldown
        var recentTime = DateTime.UtcNow.AddMilliseconds(-100); // 100ms ago

        // Act & Assert
        Assert.False(strategy.HasCooldownElapsed(recentTime));
    }

    [Fact]
    public void AdaptiveCooldown_GetRemainingCooldownMs_ReturnsRemaining()
    {
        // Arrange
        var strategy = CreateAdaptiveCooldownStrategy(); // 5000ms cooldown
        var halfwayTime = DateTime.UtcNow.AddMilliseconds(-2500); // 2.5 seconds ago

        // Act
        int remaining = strategy.GetRemainingCooldownMs(halfwayTime);

        // Assert - Should be around 2500ms remaining (with some tolerance for timing)
        Assert.InRange(remaining, 2000, 2600);
    }

    [Fact]
    public void AdaptiveCooldown_GetRemainingCooldownMs_ReturnsZeroWhenElapsed()
    {
        // Arrange
        var strategy = CreateAdaptiveCooldownStrategy(); // 5000ms cooldown
        var longAgo = DateTime.UtcNow.AddSeconds(-10); // 10 seconds ago

        // Act
        int remaining = strategy.GetRemainingCooldownMs(longAgo);

        // Assert
        Assert.Equal(0, remaining);
    }

    #endregion

    #region AdaptiveCooldownStrategy - Reset Tests

    [Fact]
    public void AdaptiveCooldown_Reset_ClearsAllState()
    {
        // Arrange
        var strategy = CreateAdaptiveCooldownStrategy(10000);

        // Record some results
        for (var i = 0; i < 5; i++)
        {
            strategy.RecordResult(false);
        }

        // Act
        strategy.Reset();

        // Assert
        Assert.Equal(5000, strategy.CurrentCooldownMs); // Reset to default
        Assert.Equal(1.0, strategy.SuccessRate);
        Assert.Equal(0, strategy.SampleCount);
    }

    #endregion

    #region ViolationSwitchTrigger - Cooldown Tests

    [Fact]
    public void ViolationSwitch_ConsecutiveViolations_TriggersCooldown()
    {
        // Arrange - Disable warmup, enable cooldown
        var config = new ViolationSwitchConfiguration { WarmupPeriodSeconds = 0, BaseCooldownSeconds = 30 };
        var trigger = new ViolationSwitchTrigger(config);
        const string streamId = "test-stream";

        // Act - Trigger a switch
        _ = trigger.RecordViolation(streamId, "PAT");
        _ = trigger.RecordViolation(streamId, "PAT");
        var firstSwitch = trigger.RecordViolation(streamId, "PAT");

        // Try to trigger again immediately
        _ = trigger.RecordViolation(streamId, "PAT");
        _ = trigger.RecordViolation(streamId, "PAT");
        var secondSwitch = trigger.RecordViolation(streamId, "PAT");

        // Assert
        Assert.True(firstSwitch.ShouldSwitch);
        Assert.False(secondSwitch.ShouldSwitch);
        Assert.Equal("Cooldown active", secondSwitch.Reason);
    }

    [Fact]
    public void ViolationSwitch_DriftBasedSwitch_TriggersCooldown()
    {
        // Arrange - Disable warmup, enable cooldown
        var config = new ViolationSwitchConfiguration { WarmupPeriodSeconds = 0, BaseCooldownSeconds = 30 };
        var trigger = new ViolationSwitchTrigger(config);
        const string streamId = "test-stream";

        // Act - Trigger via 3 consecutive drift violations
        _ = trigger.RecordDrift(streamId, 800.0);
        _ = trigger.RecordDrift(streamId, 800.0);
        var firstSwitch = trigger.RecordDrift(streamId, 800.0);

        // Try to trigger again (but cooldown blocks it)
        _ = trigger.RecordDrift(streamId, 900.0);
        _ = trigger.RecordDrift(streamId, 900.0);
        var secondSwitch = trigger.RecordDrift(streamId, 900.0);

        // Assert
        Assert.True(firstSwitch.ShouldSwitch);
        Assert.False(secondSwitch.ShouldSwitch);
        Assert.Equal("Cooldown active", secondSwitch.Reason);
    }

    [Fact]
    public void ViolationSwitch_CooldownIsConfigurable()
    {
        // Arrange - 0 second cooldown
        var config = new ViolationSwitchConfiguration { WarmupPeriodSeconds = 0, BaseCooldownSeconds = 0 };
        var trigger = new ViolationSwitchTrigger(config);
        const string streamId = "test-stream";

        // Act - Trigger multiple switches
        _ = trigger.RecordViolation(streamId, "PAT");
        _ = trigger.RecordViolation(streamId, "PAT");
        var firstSwitch = trigger.RecordViolation(streamId, "PAT");

        _ = trigger.RecordViolation(streamId, "PAT");
        _ = trigger.RecordViolation(streamId, "PAT");
        var secondSwitch = trigger.RecordViolation(streamId, "PAT");

        // Assert - Both should trigger with 0 cooldown
        Assert.True(firstSwitch.ShouldSwitch);
        Assert.True(secondSwitch.ShouldSwitch);
    }

    [Fact]
    public void ViolationSwitch_DifferentStreams_IndependentCooldowns()
    {
        // Arrange - Disable warmup, enable cooldown
        var config = new ViolationSwitchConfiguration { WarmupPeriodSeconds = 0, BaseCooldownSeconds = 30 };
        var trigger = new ViolationSwitchTrigger(config);
        const string stream1 = "stream-1";
        const string stream2 = "stream-2";

        // Act - Trigger switch on stream1
        _ = trigger.RecordViolation(stream1, "PAT");
        _ = trigger.RecordViolation(stream1, "PAT");
        var switch1 = trigger.RecordViolation(stream1, "PAT");

        // Trigger switch on stream2 (should not be affected by stream1's cooldown)
        _ = trigger.RecordViolation(stream2, "PAT");
        _ = trigger.RecordViolation(stream2, "PAT");
        var switch2 = trigger.RecordViolation(stream2, "PAT");

        // Assert - Both should trigger (independent cooldowns)
        Assert.True(switch1.ShouldSwitch);
        Assert.True(switch2.ShouldSwitch);
    }

    #endregion

    #region Anti-Flapping Behavior Tests

    [Fact]
    public void AntiFlapping_BurstViolations_OnlyFirstTriggers()
    {
        // Arrange - Disable warmup, enable cooldown
        var config = new ViolationSwitchConfiguration { WarmupPeriodSeconds = 0, BaseCooldownSeconds = 30 };
        var trigger = new ViolationSwitchTrigger(config);
        const string streamId = "test-stream";
        var switchCount = 0;

        // Act - Simulate a burst of violations
        for (var burst = 0; burst < 3; burst++)
        {
            for (var i = 0; i < 5; i++)
            {
                var result = trigger.RecordViolation(streamId, "PAT");
                if (result.ShouldSwitch)
                {
                    switchCount++;
                }
            }
        }

        // Assert - Only one switch should have triggered (first threshold hit)
        Assert.Equal(1, switchCount);
    }

    [Fact]
    public void AntiFlapping_MixedViolationTypes_IndependentThresholds()
    {
        // Arrange - Disable warmup
        var config = new ViolationSwitchConfiguration { WarmupPeriodSeconds = 0 };
        var trigger = new ViolationSwitchTrigger(config);
        const string streamId = "test-stream";

        // Act - Record PAT violations (below threshold)
        _ = trigger.RecordViolation(streamId, "PAT");
        _ = trigger.RecordViolation(streamId, "PAT");

        // Switch to PCR violations (independent counter)
        for (var i = 0; i < 4; i++)
        {
            var result = trigger.RecordViolation(streamId, "PCR");
            Assert.False(result.ShouldSwitch); // 4 PCR violations, threshold is 5
        }

        // Third PAT violation should trigger (continues PAT count)
        var patResult = trigger.RecordViolation(streamId, "PAT");
        Assert.True(patResult.ShouldSwitch);
    }

    [Fact]
    public void AntiFlapping_ResetAfterSwitch_CountersCleared()
    {
        // Arrange - No cooldown or warmup for testing
        var config = new ViolationSwitchConfiguration { WarmupPeriodSeconds = 0, BaseCooldownSeconds = 0 };
        var trigger = new ViolationSwitchTrigger(config);
        const string streamId = "test-stream";

        // Act - Trigger first switch
        _ = trigger.RecordViolation(streamId, "PAT");
        _ = trigger.RecordViolation(streamId, "PAT");
        var firstSwitch = trigger.RecordViolation(streamId, "PAT");

        // After switch, counters should be reset
        var afterFirst = trigger.RecordViolation(streamId, "PAT");
        var afterSecond = trigger.RecordViolation(streamId, "PAT");

        // Assert
        Assert.True(firstSwitch.ShouldSwitch);
        Assert.False(afterFirst.ShouldSwitch); // Only 1 violation after reset
        Assert.False(afterSecond.ShouldSwitch); // Only 2 violations after reset
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Creates an instance of AdaptiveCooldownStrategy using reflection since it's internal.
    /// </summary>
    private static dynamic CreateAdaptiveCooldownStrategy(int initialCooldownMs = 5000)
    {
        var assembly = typeof(ViolationSwitchTrigger).Assembly;
        var type =
            assembly.GetType("Jellyfin.Xtream.Service.ProviderManagement.AdaptiveCooldownStrategy")
            ?? throw new InvalidOperationException("AdaptiveCooldownStrategy type not found");

        var constructor =
            type.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                [typeof(int)],
                modifiers: null
            ) ?? throw new InvalidOperationException("AdaptiveCooldownStrategy constructor not found");

        return constructor.Invoke([initialCooldownMs]);
    }

    #endregion
}
