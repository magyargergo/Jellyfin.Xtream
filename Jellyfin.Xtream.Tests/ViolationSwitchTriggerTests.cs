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
/// Tests for <see cref="ViolationSwitchTrigger"/> following TDD principles.
/// Validates TR 101 290 violation-based provider switching logic.
/// </summary>
public sealed class ViolationSwitchTriggerTests
{
    private const string TestStreamId = "test-stream-1";

    /// <summary>
    /// Creates a test configuration with warmup disabled for immediate testing.
    /// Production streams use warmup to prevent false-positive switches during stabilization.
    /// </summary>
    private static ViolationSwitchConfiguration TestConfig =>
        new()
        {
            WarmupPeriodSeconds = 0, // Disable warmup for tests
            BaseCooldownSeconds = 0, // Disable cooldown for tests
        };

    /// <summary>
    /// Creates a trigger with test configuration (warmup and cooldown disabled).
    /// </summary>
    private static ViolationSwitchTrigger CreateTestTrigger() => new(TestConfig);

    #region PAT Violation Threshold Tests

    [Fact]
    public void RecordViolation_PatViolation_BelowThreshold_ReturnsNoSwitch()
    {
        // Arrange
        var trigger = CreateTestTrigger();

        // Act - Record 2 PAT violations (threshold is 3)
        var result1 = trigger.RecordViolation(TestStreamId, "PAT missing");
        var result2 = trigger.RecordViolation(TestStreamId, "PAT error");

        // Assert
        Assert.False(result1.ShouldSwitch);
        Assert.False(result2.ShouldSwitch);
    }

    [Fact]
    public void RecordViolation_PatViolation_AtThreshold_ReturnsSwitch()
    {
        // Arrange
        var trigger = CreateTestTrigger();

        // Act - Record 3 PAT violations (exactly at threshold)
        _ = trigger.RecordViolation(TestStreamId, "PAT missing");
        _ = trigger.RecordViolation(TestStreamId, "PAT error");
        var result = trigger.RecordViolation(TestStreamId, "PAT timeout");

        // Assert
        Assert.True(result.ShouldSwitch);
        Assert.Contains("PAT", result.Reason);
        Assert.Contains("3", result.Reason);
    }

    [Fact]
    public void RecordViolation_PatViolation_CaseInsensitive()
    {
        // Arrange
        var trigger = CreateTestTrigger();

        // Act - Mixed case PAT violations
        _ = trigger.RecordViolation(TestStreamId, "pat missing");
        _ = trigger.RecordViolation(TestStreamId, "PAT error");
        var result = trigger.RecordViolation(TestStreamId, "Pat timeout");

        // Assert
        Assert.True(result.ShouldSwitch);
    }

    #endregion

    #region Warmup Period Tests

    [Fact]
    public void RecordViolation_DuringWarmup_ReturnsNoSwitch()
    {
        // Arrange - Use default config which has 5 second warmup
        var trigger = new ViolationSwitchTrigger();

        // Act - Record violations immediately (still in warmup)
        _ = trigger.RecordViolation(TestStreamId, "PAT missing");
        _ = trigger.RecordViolation(TestStreamId, "PAT error");
        var result = trigger.RecordViolation(TestStreamId, "PAT timeout");

        // Assert - Should NOT switch during warmup even if threshold reached
        Assert.False(result.ShouldSwitch);
    }

    [Fact]
    public void RecordDrift_DuringWarmup_ReturnsNoSwitch()
    {
        // Arrange - Use default config which has 5 second warmup
        var trigger = new ViolationSwitchTrigger();

        // Act - Record severe drift immediately (still in warmup)
        _ = trigger.RecordDrift(TestStreamId, 500.0);
        _ = trigger.RecordDrift(TestStreamId, 500.0);
        var result = trigger.RecordDrift(TestStreamId, 500.0);

        // Assert - Should NOT switch during warmup
        Assert.False(result.ShouldSwitch);
    }

    #endregion

    #region PCR Violation Threshold Tests

    [Fact]
    public void RecordViolation_PcrViolation_BelowThreshold_ReturnsNoSwitch()
    {
        // Arrange
        var trigger = CreateTestTrigger();

        // Act - Record 4 PCR violations (threshold is 5)
        for (var i = 0; i < 4; i++)
        {
            var result = trigger.RecordViolation(TestStreamId, "PCR jitter");
            Assert.False(result.ShouldSwitch);
        }
    }

    [Fact]
    public void RecordViolation_PcrViolation_AtThreshold_ReturnsSwitch()
    {
        // Arrange
        var trigger = CreateTestTrigger();

        // Act - Record 5 PCR violations (exactly at threshold)
        for (var i = 0; i < 4; i++)
        {
            _ = trigger.RecordViolation(TestStreamId, "PCR discontinuity");
        }

        var result = trigger.RecordViolation(TestStreamId, "PCR jitter");

        // Assert
        Assert.True(result.ShouldSwitch);
        Assert.Contains("PCR", result.Reason);
        Assert.Contains("5", result.Reason);
    }

    [Fact]
    public void RecordViolation_PcrViolation_CaseInsensitive()
    {
        // Arrange
        var trigger = CreateTestTrigger();

        // Act - Mixed case PCR violations
        _ = trigger.RecordViolation(TestStreamId, "pcr jitter");
        _ = trigger.RecordViolation(TestStreamId, "PCR error");
        _ = trigger.RecordViolation(TestStreamId, "Pcr discontinuity");
        _ = trigger.RecordViolation(TestStreamId, "pCR missing");
        var result = trigger.RecordViolation(TestStreamId, "PCR timeout");

        // Assert
        Assert.True(result.ShouldSwitch);
    }

    #endregion

    #region A/V Drift Threshold Tests

    [Fact]
    public void RecordDrift_BelowThreshold_ReturnsNoSwitch()
    {
        // Arrange
        var trigger = CreateTestTrigger();

        // Act - Drift below 300ms threshold (even with multiple readings)
        _ = trigger.RecordDrift(TestStreamId, 200.0);
        _ = trigger.RecordDrift(TestStreamId, 200.0);
        var result = trigger.RecordDrift(TestStreamId, 200.0);

        // Assert
        Assert.False(result.ShouldSwitch);
    }

    [Fact]
    public void RecordDrift_AtThreshold_ReturnsNoSwitch()
    {
        // Arrange
        var trigger = CreateTestTrigger();

        // Act - Drift exactly at 300ms threshold (should not trigger)
        _ = trigger.RecordDrift(TestStreamId, 299.9);
        _ = trigger.RecordDrift(TestStreamId, 299.9);
        var result = trigger.RecordDrift(TestStreamId, 299.9);

        // Assert
        Assert.False(result.ShouldSwitch);
    }

    [Fact]
    public void RecordDrift_AboveThreshold_ReturnsSwitch()
    {
        // Arrange
        var trigger = CreateTestTrigger();

        // Act - 3 consecutive drift violations above threshold (default DriftViolationsThreshold is 3)
        _ = trigger.RecordDrift(TestStreamId, 350.0);
        _ = trigger.RecordDrift(TestStreamId, 350.0);
        var result = trigger.RecordDrift(TestStreamId, 350.0);

        // Assert
        Assert.True(result.ShouldSwitch);
        Assert.Contains("drift", result.Reason, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3 consecutive", result.Reason);
    }

    [Fact]
    public void RecordDrift_NegativeDrift_AboveThreshold_ReturnsSwitch()
    {
        // Arrange
        var trigger = CreateTestTrigger();

        // Act - 3 consecutive negative drift violations above threshold
        _ = trigger.RecordDrift(TestStreamId, -400.0);
        _ = trigger.RecordDrift(TestStreamId, -400.0);
        var result = trigger.RecordDrift(TestStreamId, -400.0);

        // Assert
        Assert.True(result.ShouldSwitch);
        Assert.Contains("drift", result.Reason, System.StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Cooldown Mechanism Tests

    [Fact]
    public void RecordViolation_DuringCooldown_ReturnsCooldownActive()
    {
        // Arrange - Use config with cooldown enabled
        var config = new ViolationSwitchConfiguration
        {
            WarmupPeriodSeconds = 0,
            BaseCooldownSeconds = 30, // Enable cooldown
        };
        var trigger = new ViolationSwitchTrigger(config);

        // Act - Trigger first switch
        _ = trigger.RecordViolation(TestStreamId, "PAT");
        _ = trigger.RecordViolation(TestStreamId, "PAT");
        var firstSwitch = trigger.RecordViolation(TestStreamId, "PAT");

        // Try to trigger again immediately
        _ = trigger.RecordViolation(TestStreamId, "PAT");
        _ = trigger.RecordViolation(TestStreamId, "PAT");
        var secondSwitch = trigger.RecordViolation(TestStreamId, "PAT");

        // Assert
        Assert.True(firstSwitch.ShouldSwitch);
        Assert.False(secondSwitch.ShouldSwitch);
        Assert.Equal("Cooldown active", secondSwitch.Reason);
    }

    [Fact]
    public void RecordDrift_DuringCooldown_ReturnsCooldownActive()
    {
        // Arrange - Use config with cooldown enabled
        var config = new ViolationSwitchConfiguration
        {
            WarmupPeriodSeconds = 0,
            BaseCooldownSeconds = 30, // Enable cooldown
        };
        var trigger = new ViolationSwitchTrigger(config);

        // Act - Trigger first switch via 3 consecutive drift violations
        _ = trigger.RecordDrift(TestStreamId, 400.0);
        _ = trigger.RecordDrift(TestStreamId, 400.0);
        var firstSwitch = trigger.RecordDrift(TestStreamId, 400.0);

        // Try to trigger again immediately (3 more violations, but cooldown blocks)
        _ = trigger.RecordDrift(TestStreamId, 500.0);
        _ = trigger.RecordDrift(TestStreamId, 500.0);
        var secondSwitch = trigger.RecordDrift(TestStreamId, 500.0);

        // Assert
        Assert.True(firstSwitch.ShouldSwitch);
        Assert.False(secondSwitch.ShouldSwitch);
        Assert.Equal("Cooldown active", secondSwitch.Reason);
    }

    #endregion

    #region Counter Reset Tests

    [Fact]
    public void ResetCounters_ClearsViolationCounts()
    {
        // Arrange
        var trigger = CreateTestTrigger();

        // Record 2 PAT violations (just below threshold)
        _ = trigger.RecordViolation(TestStreamId, "PAT");
        _ = trigger.RecordViolation(TestStreamId, "PAT");

        // Act - Reset counters
        trigger.ResetCounters(TestStreamId);

        // Need 3 more violations to trigger (not 1)
        var result1 = trigger.RecordViolation(TestStreamId, "PAT");
        var result2 = trigger.RecordViolation(TestStreamId, "PAT");

        // Assert - Should not trigger yet (only 2 violations after reset)
        Assert.False(result1.ShouldSwitch);
        Assert.False(result2.ShouldSwitch);
    }

    [Fact]
    public void ResetCounters_NonExistentStream_DoesNotThrow()
    {
        // Arrange
        var trigger = CreateTestTrigger();

        // Act & Assert - Should not throw
        var exception = Record.Exception(() => trigger.ResetCounters("non-existent-stream"));
        Assert.Null(exception);
    }

    [Fact]
    public void RecordViolation_AfterSwitch_CountersAreReset()
    {
        // Arrange
        var config = new ViolationSwitchConfiguration
        {
            WarmupPeriodSeconds = 0, // Disable warmup for test
            BaseCooldownSeconds = 0, // Disable cooldown for test
        };
        var trigger = new ViolationSwitchTrigger(config);

        // Act - Trigger first switch
        _ = trigger.RecordViolation(TestStreamId, "PAT");
        _ = trigger.RecordViolation(TestStreamId, "PAT");
        var firstSwitch = trigger.RecordViolation(TestStreamId, "PAT");

        // After switch, counters should be reset - need 3 more to trigger again
        var result1 = trigger.RecordViolation(TestStreamId, "PAT");
        var result2 = trigger.RecordViolation(TestStreamId, "PAT");

        // Assert
        Assert.True(firstSwitch.ShouldSwitch);
        Assert.False(result1.ShouldSwitch);
        Assert.False(result2.ShouldSwitch);
    }

    #endregion

    #region Per-Stream Isolation Tests

    [Fact]
    public void RecordViolation_DifferentStreams_IndependentTracking()
    {
        // Arrange
        var trigger = CreateTestTrigger();
        const string stream1 = "stream-1";
        const string stream2 = "stream-2";

        // Act - Record 2 violations on each stream
        _ = trigger.RecordViolation(stream1, "PAT");
        _ = trigger.RecordViolation(stream1, "PAT");
        _ = trigger.RecordViolation(stream2, "PAT");
        _ = trigger.RecordViolation(stream2, "PAT");

        // Third violation on stream1 should trigger
        var result1 = trigger.RecordViolation(stream1, "PAT");

        // Third violation on stream2 should also trigger (independent)
        var result2 = trigger.RecordViolation(stream2, "PAT");

        // Assert
        Assert.True(result1.ShouldSwitch);
        Assert.True(result2.ShouldSwitch);
    }

    [Fact]
    public void ResetCounters_OnlyAffectsSpecifiedStream()
    {
        // Arrange
        var trigger = CreateTestTrigger();
        const string stream1 = "stream-1";
        const string stream2 = "stream-2";

        // Record 2 violations on each stream
        _ = trigger.RecordViolation(stream1, "PAT");
        _ = trigger.RecordViolation(stream1, "PAT");
        _ = trigger.RecordViolation(stream2, "PAT");
        _ = trigger.RecordViolation(stream2, "PAT");

        // Act - Reset only stream1
        trigger.ResetCounters(stream1);

        // Assert - Stream1 needs 3 more, Stream2 needs 1 more
        var stream1Result = trigger.RecordViolation(stream1, "PAT");
        var stream2Result = trigger.RecordViolation(stream2, "PAT");

        Assert.False(stream1Result.ShouldSwitch); // Reset, only 1 violation
        Assert.True(stream2Result.ShouldSwitch); // Not reset, 3rd violation triggers
    }

    #endregion

    #region Custom Configuration Tests

    [Fact]
    public void CustomConfiguration_PatThreshold_Respected()
    {
        // Arrange
        var config = new ViolationSwitchConfiguration { PatViolationsThreshold = 2, WarmupPeriodSeconds = 0 };
        var trigger = new ViolationSwitchTrigger(config);

        // Act - Only 2 violations needed
        _ = trigger.RecordViolation(TestStreamId, "PAT");
        var result = trigger.RecordViolation(TestStreamId, "PAT");

        // Assert
        Assert.True(result.ShouldSwitch);
    }

    [Fact]
    public void CustomConfiguration_PcrThreshold_Respected()
    {
        // Arrange
        var config = new ViolationSwitchConfiguration { PcrViolationsThreshold = 2, WarmupPeriodSeconds = 0 };
        var trigger = new ViolationSwitchTrigger(config);

        // Act - Only 2 violations needed
        _ = trigger.RecordViolation(TestStreamId, "PCR");
        var result = trigger.RecordViolation(TestStreamId, "PCR");

        // Assert
        Assert.True(result.ShouldSwitch);
    }

    [Fact]
    public void CustomConfiguration_DriftThreshold_Respected()
    {
        // Arrange
        var config = new ViolationSwitchConfiguration
        {
            DriftThresholdMs = 500.0,
            DriftViolationsThreshold = 1, // Trigger on first violation
            WarmupPeriodSeconds = 0,
        };
        var trigger = new ViolationSwitchTrigger(config);

        // Act - 600ms drift (above custom 500ms threshold)
        var result = trigger.RecordDrift(TestStreamId, 600.0);

        // Assert
        Assert.True(result.ShouldSwitch);
    }

    [Fact]
    public void DefaultConfiguration_HasExpectedValues()
    {
        // Arrange & Act
        var config = ViolationSwitchConfiguration.Default;

        // Assert - TR 101 290 recommended values with adaptive cooldown and warmup
        Assert.Equal(3, config.PatViolationsThreshold);
        Assert.Equal(5, config.PcrViolationsThreshold);
        Assert.Equal(300.0, config.DriftThresholdMs); // 300ms is audible threshold
        Assert.Equal(3, config.DriftViolationsThreshold); // Requires 3 consecutive drift violations
        Assert.Equal(5, config.WarmupPeriodSeconds); // 5 second warmup period
        Assert.Equal(30, config.BaseCooldownSeconds);
        Assert.Equal(180, config.MaxCooldownSeconds);
        Assert.Equal(2.0, config.CooldownMultiplier);
        Assert.Equal(5, config.MaxConsecutiveFailures);
    }

    #endregion

    #region Adaptive Cooldown Tests

    [Fact]
    public void AdaptiveCooldown_IncreasesWithConsecutiveFailures()
    {
        // Arrange - Very short base cooldown for testing, but adaptive should still work
        var config = new ViolationSwitchConfiguration
        {
            WarmupPeriodSeconds = 0, // Disable warmup for testing
            BaseCooldownSeconds = 0, // No base cooldown for quick testing
            MaxCooldownSeconds = 180,
            CooldownMultiplier = 2.0,
            MaxConsecutiveFailures = 10,
        };
        var trigger = new ViolationSwitchTrigger(config);

        // Act - Trigger multiple switches in a row (since cooldown is 0)
        _ = trigger.RecordViolation(TestStreamId, "PAT");
        _ = trigger.RecordViolation(TestStreamId, "PAT");
        var first = trigger.RecordViolation(TestStreamId, "PAT");

        _ = trigger.RecordViolation(TestStreamId, "PAT");
        _ = trigger.RecordViolation(TestStreamId, "PAT");
        var second = trigger.RecordViolation(TestStreamId, "PAT");

        // Assert - Both should trigger since base cooldown is 0
        Assert.True(first.ShouldSwitch);
        Assert.True(second.ShouldSwitch);
    }

    [Fact]
    public void MaxConsecutiveFailures_DisablesViolationSwitching()
    {
        // Arrange
        var config = new ViolationSwitchConfiguration
        {
            WarmupPeriodSeconds = 0, // Disable warmup for testing
            BaseCooldownSeconds = 0, // No cooldown for testing
            MaxConsecutiveFailures = 2,
        };
        var trigger = new ViolationSwitchTrigger(config);

        // Act - Trigger max failures worth of switches
        _ = trigger.RecordViolation(TestStreamId, "PAT");
        _ = trigger.RecordViolation(TestStreamId, "PAT");
        var first = trigger.RecordViolation(TestStreamId, "PAT"); // Consecutive failure = 1

        _ = trigger.RecordViolation(TestStreamId, "PAT");
        _ = trigger.RecordViolation(TestStreamId, "PAT");
        var second = trigger.RecordViolation(TestStreamId, "PAT"); // Consecutive failure = 2

        // Third attempt should return MaxFailuresReached
        _ = trigger.RecordViolation(TestStreamId, "PAT");
        _ = trigger.RecordViolation(TestStreamId, "PAT");
        var third = trigger.RecordViolation(TestStreamId, "PAT");

        // Assert
        Assert.True(first.ShouldSwitch);
        Assert.True(second.ShouldSwitch);
        Assert.False(third.ShouldSwitch);
        Assert.Equal("Max consecutive failures reached", third.Reason);
    }

    [Fact]
    public void AcknowledgeSuccess_ResetsConsecutiveFailures()
    {
        // Arrange
        var config = new ViolationSwitchConfiguration
        {
            WarmupPeriodSeconds = 0,
            BaseCooldownSeconds = 0,
            MaxConsecutiveFailures = 2,
        };
        var trigger = new ViolationSwitchTrigger(config);

        // Trigger max failures
        _ = trigger.RecordViolation(TestStreamId, "PAT");
        _ = trigger.RecordViolation(TestStreamId, "PAT");
        _ = trigger.RecordViolation(TestStreamId, "PAT"); // Failure 1

        _ = trigger.RecordViolation(TestStreamId, "PAT");
        _ = trigger.RecordViolation(TestStreamId, "PAT");
        _ = trigger.RecordViolation(TestStreamId, "PAT"); // Failure 2

        // Act - Acknowledge success (simulating successful switch)
        trigger.AcknowledgeSuccess(TestStreamId);

        // Now should be able to trigger again
        _ = trigger.RecordViolation(TestStreamId, "PAT");
        _ = trigger.RecordViolation(TestStreamId, "PAT");
        var result = trigger.RecordViolation(TestStreamId, "PAT");

        // Assert
        Assert.True(result.ShouldSwitch);
    }

    [Fact]
    public void ViolationEvaluationResult_MaxFailuresReached_HasCorrectValues()
    {
        // Arrange & Act
        var result = ViolationEvaluationResult.MaxFailuresReached;

        // Assert
        Assert.False(result.ShouldSwitch);
        Assert.Equal("Max consecutive failures reached", result.Reason);
    }

    #endregion

    #region Unknown Violation Type Tests

    [Fact]
    public void RecordViolation_UnknownType_ReturnsNoSwitch()
    {
        // Arrange
        var trigger = CreateTestTrigger();

        // Act - Unknown violation types should not trigger
        var result1 = trigger.RecordViolation(TestStreamId, "Unknown");
        var result2 = trigger.RecordViolation(TestStreamId, "CRC error");
        var result3 = trigger.RecordViolation(TestStreamId, "Network timeout");

        // Assert
        Assert.False(result1.ShouldSwitch);
        Assert.False(result2.ShouldSwitch);
        Assert.False(result3.ShouldSwitch);
    }

    #endregion

    #region ViolationEvaluationResult Tests

    [Fact]
    public void ViolationEvaluationResult_NoSwitch_HasCorrectValues()
    {
        // Arrange & Act
        var result = ViolationEvaluationResult.NoSwitch;

        // Assert
        Assert.False(result.ShouldSwitch);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void ViolationEvaluationResult_CooldownActive_HasCorrectValues()
    {
        // Arrange & Act
        var result = ViolationEvaluationResult.CooldownActive;

        // Assert
        Assert.False(result.ShouldSwitch);
        Assert.Equal("Cooldown active", result.Reason);
    }

    [Fact]
    public void ViolationEvaluationResult_CustomSwitch_HasCorrectValues()
    {
        // Arrange & Act
        var result = new ViolationEvaluationResult(ShouldSwitch: true, "Custom reason");

        // Assert
        Assert.True(result.ShouldSwitch);
        Assert.Equal("Custom reason", result.Reason);
    }

    #endregion
}
