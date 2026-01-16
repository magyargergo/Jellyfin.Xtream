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
/// Tests for <see cref="StreamQualityMonitor"/> degradation detection.
/// Validates throughput-based quality assessment and switch triggers.
/// Uses TestClock for deterministic timing behavior.
/// </summary>
public sealed class DegradationDetectionTests
{
    // Minimum baseline throughput constant from StreamQualityMonitor
    private const long MinBaselineBytesPerSecond = 100_000;

    // Standard interval for samples (100ms) - enough to pass the 50ms minimum
    private const int SampleIntervalMs = 100;

    #region Initialization Tests

    [Fact]
    public void NewMonitor_StartsHealthy()
    {
        // Arrange & Act
        var clock = new TestClock();
        var monitor = new StreamQualityMonitor(clock);

        // Assert
        Assert.Equal(StreamQuality.Healthy, monitor.CurrentQuality);
        Assert.Equal(0, monitor.SampleCount);
        Assert.False(monitor.HasBaseline);
    }

    [Fact]
    public void NewMonitor_DoesNotTriggerSwitch()
    {
        // Arrange
        var clock = new TestClock();
        var monitor = new StreamQualityMonitor(clock);

        // Act & Assert
        Assert.False(monitor.ShouldTriggerSwitch());
        Assert.False(monitor.ShouldPreconnect());
    }

    #endregion

    #region Baseline Establishment Tests

    [Fact]
    public void HasBaseline_FalseUntilMinSamples()
    {
        // Arrange
        var clock = new TestClock();
        var monitor = new StreamQualityMonitor(clock);

        // Act & Assert - Need 10 samples for baseline
        Assert.False(monitor.HasBaseline);

        // Record 9 samples (not enough for baseline)
        long cumulativeBytes = 0;
        for (var i = 0; i < 9; i++)
        {
            clock.AdvanceMs(SampleIntervalMs);
            cumulativeBytes += 100_000; // 1 MB/s at 100ms intervals
            monitor.RecordSample(cumulativeBytes);
        }

        Assert.False(monitor.HasBaseline);
        Assert.Equal(9, monitor.SampleCount);

        // Record 10th sample - baseline should be established
        clock.AdvanceMs(SampleIntervalMs);
        cumulativeBytes += 100_000;
        monitor.RecordSample(cumulativeBytes);

        Assert.True(monitor.HasBaseline);
        Assert.Equal(10, monitor.SampleCount);
    }

    [Fact]
    public void SetBaseline_EstablishesManualBaseline()
    {
        // Arrange
        var clock = new TestClock();
        var monitor = new StreamQualityMonitor(clock);

        // Act
        monitor.SetBaseline(1_000_000); // 1 MB/s

        // Assert
        Assert.Equal(1_000_000, monitor.BaselineThroughput);
    }

    [Fact]
    public void SetBaseline_EnforcesMinimum()
    {
        // Arrange
        var clock = new TestClock();
        var monitor = new StreamQualityMonitor(clock);

        // Act - Set below minimum
        monitor.SetBaseline(50_000); // 50 KB/s

        // Assert - Should use minimum
        Assert.Equal(MinBaselineBytesPerSecond, monitor.BaselineThroughput);
    }

    #endregion

    #region Quality Degradation Tests

    [Fact]
    public void Quality_RemainsHealthyAbove50Percent()
    {
        // Arrange
        var clock = new TestClock();
        var monitor = new StreamQualityMonitor(clock);
        long cumulativeBytes = 0;

        // Establish baseline at 1 MB/s
        EstablishBaseline(monitor, clock, 1_000_000, ref cumulativeBytes);

        // Fill window with 60% of baseline throughput (above 50% threshold)
        SimulateThroughputSamples(monitor, clock, 600_000, 20, ref cumulativeBytes);

        // Assert
        Assert.Equal(StreamQuality.Healthy, monitor.CurrentQuality);
    }

    [Fact]
    public void Quality_DegradingBelow50Percent()
    {
        // Arrange
        var clock = new TestClock();
        var monitor = new StreamQualityMonitor(clock);
        long cumulativeBytes = 0;

        // Establish baseline at 1 MB/s
        EstablishBaseline(monitor, clock, 1_000_000, ref cumulativeBytes);

        // Fill entire window with 40% of baseline throughput (below 50% threshold)
        SimulateThroughputSamples(monitor, clock, 400_000, 20, ref cumulativeBytes);

        // Assert
        Assert.Equal(StreamQuality.Degrading, monitor.CurrentQuality);
    }

    [Fact]
    public void Quality_CriticalBelow25Percent()
    {
        // Arrange
        var clock = new TestClock();
        var monitor = new StreamQualityMonitor(clock);
        long cumulativeBytes = 0;

        // Establish baseline at 1 MB/s
        EstablishBaseline(monitor, clock, 1_000_000, ref cumulativeBytes);

        // Fill entire window with 20% of baseline throughput (below 25% threshold)
        SimulateThroughputSamples(monitor, clock, 200_000, 20, ref cumulativeBytes);

        // Assert
        Assert.Equal(StreamQuality.Critical, monitor.CurrentQuality);
    }

    #endregion

    #region Switch Trigger Tests

    [Fact]
    public void ShouldTriggerSwitch_FalseWhenHealthy()
    {
        // Arrange
        var clock = new TestClock();
        var monitor = new StreamQualityMonitor(clock);
        long cumulativeBytes = 0;

        // Establish baseline with healthy throughput
        EstablishBaseline(monitor, clock, 1_000_000, ref cumulativeBytes);

        // Act & Assert
        Assert.False(monitor.ShouldTriggerSwitch());
    }

    [Fact]
    public void ShouldTriggerSwitch_FalseWhenDegrading()
    {
        // Arrange
        var clock = new TestClock();
        var monitor = new StreamQualityMonitor(clock);
        long cumulativeBytes = 0;

        // Establish baseline
        EstablishBaseline(monitor, clock, 1_000_000, ref cumulativeBytes);

        // Fill window with 40% of baseline (degrading)
        SimulateThroughputSamples(monitor, clock, 400_000, 20, ref cumulativeBytes);

        // Act & Assert - Degrading doesn't trigger switch, only preconnect
        Assert.Equal(StreamQuality.Degrading, monitor.CurrentQuality);
        Assert.False(monitor.ShouldTriggerSwitch());
    }

    [Fact]
    public void ShouldTriggerSwitch_TrueWhenCritical()
    {
        // Arrange
        var clock = new TestClock();
        var monitor = new StreamQualityMonitor(clock);
        long cumulativeBytes = 0;

        // Establish baseline
        EstablishBaseline(monitor, clock, 1_000_000, ref cumulativeBytes);

        // Fill window with 20% of baseline (critical)
        SimulateThroughputSamples(monitor, clock, 200_000, 20, ref cumulativeBytes);

        // Act & Assert
        Assert.Equal(StreamQuality.Critical, monitor.CurrentQuality);
        Assert.True(monitor.ShouldTriggerSwitch());
    }

    [Fact]
    public void ShouldTriggerSwitch_TrueWhenStalled()
    {
        // Arrange
        var clock = new TestClock();
        var monitor = new StreamQualityMonitor(clock);
        monitor.SetBaseline(1_000_000);

        // Fill the sliding window with zero throughput samples to simulate ongoing stall
        const long totalBytes = 100_000; // Start with some bytes
        clock.AdvanceMs(100);
        monitor.RecordSample(totalBytes);

        // Fill rest of window with no new bytes (zero throughput)
        for (var i = 0; i < 19; i++)
        {
            clock.AdvanceMs(100);
            monitor.RecordSample(totalBytes); // No new bytes
        }

        // Now simulate a prolonged stall: advance time > 2 seconds with still no new bytes
        clock.AdvanceMs(2100);
        monitor.RecordSample(totalBytes); // Still no new bytes

        // Act & Assert - Quality should be Stalled (throughput is 0 and interval > 2s)
        Assert.Equal(StreamQuality.Stalled, monitor.CurrentQuality);
        Assert.True(monitor.ShouldTriggerSwitch());
    }

    #endregion

    #region Preconnect Tests

    [Fact]
    public void ShouldPreconnect_FalseWhenHealthy()
    {
        // Arrange
        var clock = new TestClock();
        var monitor = new StreamQualityMonitor(clock);
        long cumulativeBytes = 0;

        // Establish baseline with healthy throughput
        EstablishBaseline(monitor, clock, 1_000_000, ref cumulativeBytes);

        // Assert
        Assert.False(monitor.ShouldPreconnect());
    }

    [Fact]
    public void ShouldPreconnect_TrueWhenDegrading()
    {
        // Arrange
        var clock = new TestClock();
        var monitor = new StreamQualityMonitor(clock);
        long cumulativeBytes = 0;

        // Establish baseline
        EstablishBaseline(monitor, clock, 1_000_000, ref cumulativeBytes);

        // Fill window with 40% of baseline (degrading)
        SimulateThroughputSamples(monitor, clock, 400_000, 20, ref cumulativeBytes);

        // Assert
        Assert.Equal(StreamQuality.Degrading, monitor.CurrentQuality);
        Assert.True(monitor.ShouldPreconnect());
    }

    [Fact]
    public void ShouldPreconnect_TrueWhenCritical()
    {
        // Arrange
        var clock = new TestClock();
        var monitor = new StreamQualityMonitor(clock);
        long cumulativeBytes = 0;

        // Establish baseline
        EstablishBaseline(monitor, clock, 1_000_000, ref cumulativeBytes);

        // Fill window with 20% of baseline (critical)
        SimulateThroughputSamples(monitor, clock, 200_000, 20, ref cumulativeBytes);

        // Assert
        Assert.Equal(StreamQuality.Critical, monitor.CurrentQuality);
        Assert.True(monitor.ShouldPreconnect());
    }

    #endregion

    #region Quality Change Notification Tests

    [Fact]
    public void HasQualityChanged_FalseInitially()
    {
        // Arrange
        var clock = new TestClock();
        var monitor = new StreamQualityMonitor(clock);

        // Assert
        Assert.False(monitor.HasQualityChanged);
    }

    [Fact]
    public void HasQualityChanged_TrueAfterDegradation()
    {
        // Arrange
        var clock = new TestClock();
        var monitor = new StreamQualityMonitor(clock);
        long cumulativeBytes = 0;

        // Establish baseline
        EstablishBaseline(monitor, clock, 1_000_000, ref cumulativeBytes);

        // Clear any quality changed flag from baseline establishment
        _ = monitor.HasQualityChanged;

        // Fill window with 40% of baseline (degrading)
        SimulateThroughputSamples(monitor, clock, 400_000, 20, ref cumulativeBytes);

        // Assert - First check returns true (quality changed from Healthy to Degrading)
        Assert.True(monitor.HasQualityChanged);
        // Second check returns false (flag cleared)
        Assert.False(monitor.HasQualityChanged);
    }

    [Fact]
    public void QualityChangedCallback_InvokedOnChange()
    {
        // Arrange
        var clock = new TestClock();
        var monitor = new StreamQualityMonitor(clock);
        StreamQuality? reportedQuality = null;
        long? reportedThroughput = null;

        monitor.SetQualityChangedCallback(
            (quality, throughput) =>
            {
                reportedQuality = quality;
                reportedThroughput = throughput;
            }
        );

        // Establish baseline first
        long cumulativeBytes = 0;
        EstablishBaseline(monitor, clock, 1_000_000, ref cumulativeBytes);

        // Clear any callback from baseline establishment
        reportedQuality = null;
        reportedThroughput = null;

        // Fill window with 20% of baseline (critical)
        SimulateThroughputSamples(monitor, clock, 200_000, 20, ref cumulativeBytes);

        // Assert - callback should have been invoked
        _ = Assert.NotNull(reportedQuality);
        _ = Assert.NotNull(reportedThroughput);
        Assert.Equal(StreamQuality.Critical, reportedQuality);
    }

    #endregion

    #region Reset Tests

    [Fact]
    public void Reset_ClearsAllState()
    {
        // Arrange
        var clock = new TestClock();
        var monitor = new StreamQualityMonitor(clock);
        long cumulativeBytes = 0;

        // Establish baseline
        EstablishBaseline(monitor, clock, 1_000_000, ref cumulativeBytes);

        // Fill window with 20% of baseline (critical)
        SimulateThroughputSamples(monitor, clock, 200_000, 20, ref cumulativeBytes);

        Assert.Equal(StreamQuality.Critical, monitor.CurrentQuality);

        // Act
        monitor.Reset();

        // Assert
        Assert.Equal(StreamQuality.Healthy, monitor.CurrentQuality);
        Assert.Equal(0, monitor.SampleCount);
        Assert.Equal(0, monitor.BaselineThroughput);
        Assert.False(monitor.HasBaseline);
        Assert.False(monitor.HasQualityChanged);
    }

    [Fact]
    public void Reset_AllowsReestablishingBaseline()
    {
        // Arrange
        var clock = new TestClock();
        var monitor = new StreamQualityMonitor(clock);
        long cumulativeBytes = 0;

        // Establish baseline
        EstablishBaseline(monitor, clock, 1_000_000, ref cumulativeBytes);

        monitor.Reset();

        // Act - Establish new baseline
        cumulativeBytes = 0;
        SimulateThroughputSamples(monitor, clock, 500_000, 10, ref cumulativeBytes);

        // Assert
        Assert.True(monitor.HasBaseline);
        Assert.True(monitor.BaselineThroughput >= MinBaselineBytesPerSecond);
    }

    #endregion

    #region Recovery Tests

    [Fact]
    public void Quality_RecoversFromDegrading()
    {
        // Arrange
        var clock = new TestClock();
        var monitor = new StreamQualityMonitor(clock);
        long cumulativeBytes = 0;

        // Establish baseline
        EstablishBaseline(monitor, clock, 1_000_000, ref cumulativeBytes);

        // Fill window with 40% of baseline (degrading)
        SimulateThroughputSamples(monitor, clock, 400_000, 20, ref cumulativeBytes);

        Assert.Equal(StreamQuality.Degrading, monitor.CurrentQuality);

        // Act - Recover with healthy throughput
        SimulateThroughputSamples(monitor, clock, 1_000_000, 20, ref cumulativeBytes);

        // Assert
        Assert.Equal(StreamQuality.Healthy, monitor.CurrentQuality);
    }

    [Fact]
    public void Quality_RecoversFromCritical()
    {
        // Arrange
        var clock = new TestClock();
        var monitor = new StreamQualityMonitor(clock);
        long cumulativeBytes = 0;

        // Establish baseline
        EstablishBaseline(monitor, clock, 1_000_000, ref cumulativeBytes);

        // Fill window with 20% of baseline (critical)
        SimulateThroughputSamples(monitor, clock, 200_000, 20, ref cumulativeBytes);

        Assert.Equal(StreamQuality.Critical, monitor.CurrentQuality);

        // Act - Recover with healthy throughput
        SimulateThroughputSamples(monitor, clock, 1_000_000, 20, ref cumulativeBytes);

        // Assert
        Assert.Equal(StreamQuality.Healthy, monitor.CurrentQuality);
    }

    #endregion

    #region ProactiveSwitchResult Tests

    [Fact]
    public void ProactiveSwitchResult_NotNeeded_HasCorrectValues()
    {
        // Arrange & Act
        var result = ProactiveSwitchResult.NotNeeded;

        // Assert
        Assert.False(result.Initiated);
    }

    [Fact]
    public void ProactiveSwitchResult_SwitchInitiated_HasCorrectValues()
    {
        // Arrange & Act
        var result = ProactiveSwitchResult.SwitchInitiated(
            StreamQuality.Critical,
            throughput: 200_000,
            baseline: 1_000_000
        );

        // Assert
        Assert.True(result.Initiated);
        Assert.Equal(StreamQuality.Critical, result.TriggerQuality);
        Assert.Equal(200_000, result.ThroughputAtSwitch);
        Assert.Equal(1_000_000, result.BaselineThroughput);
    }

    [Fact]
    public void ProactiveSwitchResult_Equality()
    {
        // Arrange
        var result1 = ProactiveSwitchResult.SwitchInitiated(StreamQuality.Critical, 200_000, 1_000_000);
        var result2 = ProactiveSwitchResult.SwitchInitiated(StreamQuality.Critical, 300_000, 1_000_000);
        var result3 = ProactiveSwitchResult.SwitchInitiated(StreamQuality.Degrading, 200_000, 1_000_000);

        // Assert - Equality based on Initiated and TriggerQuality only
        Assert.True(result1 == result2);
        Assert.False(result1 == result3);
        Assert.True(result1 != result3);
    }

    #endregion

    #region StreamQuality Enum Tests

    [Fact]
    public void StreamQuality_OrderingIsCorrect()
    {
        // Assert - Quality levels should be in increasing severity order
        Assert.True(StreamQuality.Healthy < StreamQuality.Degrading);
        Assert.True(StreamQuality.Degrading < StreamQuality.Critical);
        Assert.True(StreamQuality.Critical < StreamQuality.Stalled);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Simulates throughput samples by recording cumulative bytes that produce the desired throughput.
    /// Uses TestClock for deterministic timing.
    /// </summary>
    /// <param name="monitor">The monitor to record samples to.</param>
    /// <param name="clock">The test clock to advance.</param>
    /// <param name="bytesPerSecond">Desired throughput in bytes per second.</param>
    /// <param name="count">Number of samples to record.</param>
    /// <param name="cumulativeBytes">Reference to track cumulative bytes across calls.</param>
    private static void SimulateThroughputSamples(
        StreamQualityMonitor monitor,
        TestClock clock,
        long bytesPerSecond,
        int count,
        ref long cumulativeBytes
    )
    {
        // Calculate bytes to add per interval to achieve desired throughput
        // bytesPerSecond * intervalMs / 1000 = bytes per interval
        var bytesPerInterval = bytesPerSecond * SampleIntervalMs / 1000;

        for (var i = 0; i < count; i++)
        {
            clock.AdvanceMs(SampleIntervalMs);
            cumulativeBytes += bytesPerInterval;
            monitor.RecordSample(cumulativeBytes);
        }
    }

    /// <summary>
    /// Establishes baseline by recording 10 samples at the specified throughput.
    /// Uses TestClock for deterministic timing.
    /// </summary>
    private static void EstablishBaseline(
        StreamQualityMonitor monitor,
        TestClock clock,
        long bytesPerSecond,
        ref long cumulativeBytes
    ) =>
        // Record 10 samples at baseline throughput (minimum for HasBaseline to be true)
        SimulateThroughputSamples(monitor, clock, bytesPerSecond, 10, ref cumulativeBytes);

    #endregion
}
