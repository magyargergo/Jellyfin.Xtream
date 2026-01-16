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

using Jellyfin.Xtream.Service.MpegTs.UseCases;
using Jellyfin.Xtream.Service.ProviderManagement;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Tests for TR 101 290 MPEG-TS stream quality validation.
/// Validates violation detection, event args, and integration with switch triggers.
/// </summary>
public sealed class Tr101290ValidatorTests
{
    /// <summary>
    /// Creates a ViolationSwitchTrigger with warmup disabled for immediate testing.
    /// </summary>
    private static ViolationSwitchTrigger CreateTestTrigger() =>
        new(new ViolationSwitchConfiguration { WarmupPeriodSeconds = 0 });

    #region StreamQualityViolationEventArgs Tests

    [Fact]
    public void StreamQualityViolationEventArgs_StoresViolationType()
    {
        // Arrange & Act
        var args = new StreamQualityViolationEventArgs("PAT Missing", "PAT not received for 500ms");

        // Assert
        Assert.Equal("PAT Missing", args.ViolationType);
    }

    [Fact]
    public void StreamQualityViolationEventArgs_StoresDetails()
    {
        // Arrange & Act
        var args = new StreamQualityViolationEventArgs("PCR Jitter", "PCR jitter exceeded 500ns threshold");

        // Assert
        Assert.Equal("PCR jitter exceeded 500ns threshold", args.Details);
    }

    [Fact]
    public void StreamQualityViolationEventArgs_InheritsFromEventArgs()
    {
        // Arrange & Act
        var args = new StreamQualityViolationEventArgs("Test", "Test details");

        // Assert
        _ = Assert.IsAssignableFrom<EventArgs>(args);
    }

    #endregion

    #region TR 101 290 Priority 1 Violation Tests

    [Theory]
    [InlineData("TS_sync_loss", "Sync byte 0x47 not found")]
    [InlineData("Sync_byte_error", "Invalid sync byte detected")]
    [InlineData("PAT_error", "PAT table not received within 500ms")]
    [InlineData("Continuity_count_error", "CC discontinuity on PID 256")]
    [InlineData("PMT_error", "PMT table not received for program 1")]
    [InlineData("PID_error", "Referenced PID not present in stream")]
    public void Priority1Violations_CorrectlyFormatted(string violationType, string details)
    {
        // Arrange & Act
        var args = new StreamQualityViolationEventArgs(violationType, details);

        // Assert
        Assert.NotNull(args.ViolationType);
        Assert.NotNull(args.Details);
        Assert.NotEmpty(args.ViolationType);
        Assert.NotEmpty(args.Details);
    }

    #endregion

    #region TR 101 290 Priority 2 Violation Tests

    [Theory]
    [InlineData("Transport_error", "TEI flag set on packet")]
    [InlineData("CRC_error", "CRC-32 mismatch in PAT")]
    [InlineData("PCR_error", "PCR discontinuity indicator not set")]
    [InlineData("PCR_accuracy_error", "PCR drift exceeded 500ns")]
    [InlineData("PTS_error", "PTS not present in required PES")]
    [InlineData("CAT_error", "CAT table CRC failure")]
    public void Priority2Violations_CorrectlyFormatted(string violationType, string details)
    {
        // Arrange & Act
        var args = new StreamQualityViolationEventArgs(violationType, details);

        // Assert
        Assert.NotNull(args.ViolationType);
        Assert.NotNull(args.Details);
    }

    #endregion

    #region ViolationSwitchTrigger - TR 101 290 Integration Tests

    [Fact]
    public void ViolationSwitchTrigger_RecognizesPatViolations()
    {
        // Arrange
        var trigger = CreateTestTrigger();
        const string streamId = "test-stream";

        // Act - Various PAT-related violation types
        _ = trigger.RecordViolation(streamId, "PAT_error");
        _ = trigger.RecordViolation(streamId, "PAT Missing");
        var result = trigger.RecordViolation(streamId, "PAT interval violation");

        // Assert - All PAT violations counted together
        Assert.True(result.ShouldSwitch);
        Assert.Contains("PAT", result.Reason);
    }

    [Fact]
    public void ViolationSwitchTrigger_RecognizesPcrViolations()
    {
        // Arrange
        var trigger = CreateTestTrigger();
        const string streamId = "test-stream";

        // Act - Various PCR-related violation types
        _ = trigger.RecordViolation(streamId, "PCR_error");
        _ = trigger.RecordViolation(streamId, "PCR_accuracy_error");
        _ = trigger.RecordViolation(streamId, "PCR discontinuity");
        _ = trigger.RecordViolation(streamId, "PCR jitter");
        var result = trigger.RecordViolation(streamId, "PCR PID Invalid");

        // Assert - All PCR violations counted together
        Assert.True(result.ShouldSwitch);
        Assert.Contains("PCR", result.Reason);
    }

    [Fact]
    public void ViolationSwitchTrigger_IgnoresUnknownViolationTypes()
    {
        // Arrange
        var trigger = CreateTestTrigger();
        const string streamId = "test-stream";

        // Act - Non-PAT/PCR violations should not accumulate
        for (var i = 0; i < 10; i++)
        {
            var result = trigger.RecordViolation(streamId, "Transport_error");
            Assert.False(result.ShouldSwitch);
        }
    }

    #endregion

    #region A/V Drift Tests

    [Fact]
    public void ViolationSwitchTrigger_DetectsExcessiveDrift()
    {
        // Arrange
        var trigger = CreateTestTrigger();
        const string streamId = "test-stream";

        // Act - 3 consecutive drift violations above 300ms threshold (default DriftViolationsThreshold is 3)
        _ = trigger.RecordDrift(streamId, 750.0);
        _ = trigger.RecordDrift(streamId, 750.0);
        var result = trigger.RecordDrift(streamId, 750.0);

        // Assert
        Assert.True(result.ShouldSwitch);
        Assert.Contains("drift", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ViolationSwitchTrigger_AllowsAcceptableDrift()
    {
        // Arrange
        var trigger = CreateTestTrigger();
        const string streamId = "test-stream";

        // Act - Drift within acceptable range (below 300ms threshold)
        _ = trigger.RecordDrift(streamId, 200.0);
        _ = trigger.RecordDrift(streamId, 200.0);
        var result = trigger.RecordDrift(streamId, 200.0);

        // Assert
        Assert.False(result.ShouldSwitch);
    }

    [Fact]
    public void ViolationSwitchTrigger_CustomDriftThreshold()
    {
        // Arrange - Custom configuration with 500ms threshold and single violation trigger
        var config = new ViolationSwitchConfiguration
        {
            DriftThresholdMs = 500.0,
            DriftViolationsThreshold = 1, // Trigger on first violation
            WarmupPeriodSeconds = 0,
        };
        var trigger = new ViolationSwitchTrigger(config);
        const string streamId = "test-stream";

        // Act - 600ms drift (above custom threshold)
        var result = trigger.RecordDrift(streamId, 600.0);

        // Assert
        Assert.True(result.ShouldSwitch);
    }

    #endregion

    #region ViolationSwitchConfiguration Tests

    [Fact]
    public void ViolationSwitchConfiguration_DefaultValues()
    {
        // Arrange & Act
        var config = ViolationSwitchConfiguration.Default;

        // Assert - Default values with adaptive cooldown
        Assert.Equal(3, config.PatViolationsThreshold);
        Assert.Equal(5, config.PcrViolationsThreshold);
        Assert.Equal(300.0, config.DriftThresholdMs); // 300ms is audible threshold
        Assert.Equal(30, config.BaseCooldownSeconds);
        Assert.Equal(180, config.MaxCooldownSeconds);
        Assert.Equal(2.0, config.CooldownMultiplier);
        Assert.Equal(5, config.MaxConsecutiveFailures);
    }

    [Fact]
    public void ViolationSwitchConfiguration_RecordInitializer()
    {
        // Arrange & Act
        var config = new ViolationSwitchConfiguration
        {
            PatViolationsThreshold = 5,
            PcrViolationsThreshold = 10,
            DriftThresholdMs = 500.0,
            BaseCooldownSeconds = 60,
        };

        // Assert
        Assert.Equal(5, config.PatViolationsThreshold);
        Assert.Equal(10, config.PcrViolationsThreshold);
        Assert.Equal(500.0, config.DriftThresholdMs);
        Assert.Equal(60, config.BaseCooldownSeconds);
    }

    [Fact]
    public void ViolationSwitchConfiguration_DefaultIsStatic()
    {
        // Arrange & Act
        var config1 = ViolationSwitchConfiguration.Default;
        var config2 = ViolationSwitchConfiguration.Default;

        // Assert - Same instance
        Assert.Same(config1, config2);
    }

    #endregion

    #region ViolationEvaluationResult Tests

    [Fact]
    public void ViolationEvaluationResult_NoSwitch()
    {
        // Arrange & Act
        var result = ViolationEvaluationResult.NoSwitch;

        // Assert
        Assert.False(result.ShouldSwitch);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void ViolationEvaluationResult_CooldownActive()
    {
        // Arrange & Act
        var result = ViolationEvaluationResult.CooldownActive;

        // Assert
        Assert.False(result.ShouldSwitch);
        Assert.Equal("Cooldown active", result.Reason);
    }

    [Fact]
    public void ViolationEvaluationResult_Switch()
    {
        // Arrange & Act
        var result = new ViolationEvaluationResult(ShouldSwitch: true, "PAT violations (3 consecutive)");

        // Assert
        Assert.True(result.ShouldSwitch);
        Assert.Equal("PAT violations (3 consecutive)", result.Reason);
    }

    [Fact]
    public void ViolationEvaluationResult_RecordStruct()
    {
        // Arrange
        var result1 = new ViolationEvaluationResult(ShouldSwitch: true, "Test");
        var result2 = new ViolationEvaluationResult(ShouldSwitch: true, "Test");
        var result3 = new ViolationEvaluationResult(ShouldSwitch: false, "Test");

        // Assert - Record struct equality
        Assert.Equal(result1, result2);
        Assert.NotEqual(result1, result3);
    }

    #endregion

    #region Multi-Stream Isolation Tests

    [Fact]
    public void MultiStream_ViolationsAreIsolated()
    {
        // Arrange
        var trigger = CreateTestTrigger();

        // Act - Record violations on different streams
        _ = trigger.RecordViolation("stream-1", "PAT");
        _ = trigger.RecordViolation("stream-1", "PAT");
        _ = trigger.RecordViolation("stream-2", "PAT");
        _ = trigger.RecordViolation("stream-2", "PAT");

        // Stream 1 triggers first
        var result1 = trigger.RecordViolation("stream-1", "PAT");

        // Stream 2 triggers independently
        var result2 = trigger.RecordViolation("stream-2", "PAT");

        // Assert
        Assert.True(result1.ShouldSwitch);
        Assert.True(result2.ShouldSwitch);
    }

    [Fact]
    public void MultiStream_CooldownsAreIsolated()
    {
        // Arrange
        var trigger = CreateTestTrigger();

        // Trigger switch on stream-1
        _ = trigger.RecordViolation("stream-1", "PAT");
        _ = trigger.RecordViolation("stream-1", "PAT");
        _ = trigger.RecordViolation("stream-1", "PAT");

        // Trigger switch on stream-2 (should not be blocked by stream-1's cooldown)
        _ = trigger.RecordViolation("stream-2", "PAT");
        _ = trigger.RecordViolation("stream-2", "PAT");
        var result = trigger.RecordViolation("stream-2", "PAT");

        // Assert - Stream-2 should trigger despite stream-1's cooldown
        Assert.True(result.ShouldSwitch);
    }

    [Fact]
    public void MultiStream_ResetOnlyAffectsSpecifiedStream()
    {
        // Arrange
        var trigger = CreateTestTrigger();

        // Record 2 violations on each stream
        _ = trigger.RecordViolation("stream-1", "PAT");
        _ = trigger.RecordViolation("stream-1", "PAT");
        _ = trigger.RecordViolation("stream-2", "PAT");
        _ = trigger.RecordViolation("stream-2", "PAT");

        // Reset only stream-1
        trigger.ResetCounters("stream-1");

        // Act
        var result1 = trigger.RecordViolation("stream-1", "PAT"); // Only 1st after reset
        var result2 = trigger.RecordViolation("stream-2", "PAT"); // 3rd, triggers

        // Assert
        Assert.False(result1.ShouldSwitch);
        Assert.True(result2.ShouldSwitch);
    }

    #endregion

    #region Edge Case Tests

    [Fact]
    public void EmptyViolationType_DoesNotCrash()
    {
        // Arrange
        var trigger = CreateTestTrigger();

        // Act & Assert - Should not throw
        var result = trigger.RecordViolation("stream", string.Empty);
        Assert.False(result.ShouldSwitch);
    }

    [Fact]
    public void NullStreamId_ThrowsArgumentNullException()
    {
        // Arrange
        var trigger = CreateTestTrigger();

        // Act & Assert - Null stream ID throws ArgumentNullException
        _ = Assert.Throws<ArgumentNullException>(() => trigger.RecordViolation(null!, "PAT"));
    }

    [Fact]
    public void ZeroDrift_DoesNotTrigger()
    {
        // Arrange
        var trigger = CreateTestTrigger();

        // Act
        var result = trigger.RecordDrift("stream", 0.0);

        // Assert
        Assert.False(result.ShouldSwitch);
    }

    [Fact]
    public void ExactThresholdDrift_DoesNotTrigger()
    {
        // Arrange - Default threshold is 300ms
        var trigger = CreateTestTrigger();

        // Act - Exactly at threshold (using 299.9 to be just below)
        var result = trigger.RecordDrift("stream", 299.9);

        // Assert
        Assert.False(result.ShouldSwitch);
    }

    #endregion
}
