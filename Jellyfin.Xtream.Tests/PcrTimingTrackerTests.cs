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

using Jellyfin.Xtream.Service.MpegTs;
using Jellyfin.Xtream.Service.MpegTs.Core;
using Jellyfin.Xtream.Service.MpegTs.Infrastructure;
using Jellyfin.Xtream.Service.MpegTs.Models;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Unit tests for PcrTimingTracker validating ISO/IEC 13818-1 compliance.
/// </summary>
/// <remarks>
/// <para>
/// ISO/IEC 13818-1 Section 2.4.2.1 defines PCR (Program Clock Reference):
/// - PCR operates at 27 MHz (27,000,000 Hz)
/// - PCR consists of a 33-bit base and 9-bit extension
/// - Maximum PCR interval: 100ms per clause 2.7.2
/// - PCR wrap value: 2^33 * 300 (33-bit base with 300 multiplier for extension)
/// </para>
/// <para>
/// TR 101 290 (ETSI) section 5.2.2 defines PCR accuracy threshold:
/// - PCR_accuracy_error: ±500 nanoseconds
/// </para>
/// <para>
/// ISO/IEC 13818-1 clock drift tolerance:
/// - Maximum ±30 PPM for the 27 MHz system clock
/// </para>
/// </remarks>
public sealed class PcrTimingTrackerTests
{
    // ISO/IEC 13818-1 constants
    private const long PcrFrequency = 27_000_000; // 27 MHz
    private const long PcrWrapValue = (1L << 33) * 300; // 2^33 * 300
    #region PCR Frequency and Constants Tests

    /// <summary>
    /// Verifies PCR frequency constant matches ISO/IEC 13818-1 specification (27 MHz).
    /// </summary>
    [Fact]
    public void PcrFrequencyConstantMatches27MHz() => Assert.Equal(27_000_000, PcrTimingTracker.PcrFrequency);

    /// <summary>
    /// Verifies PCR wrap value matches ISO/IEC 13818-1 specification.
    /// PCR = PCR_base * 300 + PCR_ext where PCR_base is 33 bits.
    /// </summary>
    [Fact]
    public void PcrWrapValueMatchesIsoSpec()
    {
        // 2^33 * 300 = 8,589,934,592 * 300 = 2,576,980,377,600
        Assert.Equal((1L << 33) * 300, PcrTimingTracker.PcrWrapValue);
        Assert.Equal(2_576_980_377_600L, PcrTimingTracker.PcrWrapValue);
    }

    /// <summary>
    /// Verifies IPTV-appropriate jitter threshold constant is 50ms.
    /// Software decoders tolerate network jitter well - 50ms is a reasonable threshold for IPTV.
    /// </summary>
    [Fact]
    public void JitterThresholdMatches50Milliseconds() => Assert.Equal(50_000_000, PcrTimingTracker.JitterThresholdNs);

    /// <summary>
    /// Verifies TR 101 290 broadcast jitter threshold is preserved for reference (500ns).
    /// Per section 5.2.2: "PCR_accuracy_error: Accuracy of PCR ±500 ns".
    /// </summary>
    [Fact]
    public void BroadcastJitterThresholdMatches500Nanoseconds() =>
        Assert.Equal(500, PcrTimingTracker.BroadcastJitterThresholdNs);

    /// <summary>
    /// Verifies IPTV-appropriate PCR interval constant (500ms for network buffering).
    /// </summary>
    [Fact]
    public void MaxPcrIntervalMatches500Ms() => Assert.Equal(500, PcrTimingTracker.MaxPcrIntervalMs);

    /// <summary>
    /// Verifies IPTV-appropriate drift threshold (10,000 PPM / 1%).
    /// Non-broadcast encoders often have higher drift than broadcast spec.
    /// </summary>
    [Fact]
    public void MaxDriftPpmMatches10000Ppm() => Assert.Equal(10_000.0, PcrTimingTracker.MaxDriftPpm);

    /// <summary>
    /// Verifies ISO/IEC 13818-1 broadcast drift threshold is preserved for reference (30 PPM).
    /// </summary>
    [Fact]
    public void BroadcastDriftPpmMatches30Ppm() => Assert.Equal(30.0, PcrTimingTracker.BroadcastDriftPpm);

    #endregion

    #region Unit Conversion Tests

    /// <summary>
    /// Tests conversion from clock ticks to PCR units.
    /// </summary>
    [Fact]
    public void TicksToPcrConvertsCorrectly()
    {
        // 1 second worth of ticks should equal 27,000,000 PCR units
        const long ticksPerSecond = 10_000_000; // Standard test clock frequency
        var pcrUnits = PcrTimingTracker.TicksToPcr(ticksPerSecond, ticksPerSecond);

        Assert.Equal(PcrFrequency, pcrUnits);
    }

    /// <summary>
    /// Tests conversion from PCR units to nanoseconds.
    /// </summary>
    [Fact]
    public void PcrToNanosecondsConvertsCorrectly()
    {
        // 27 PCR units = 1000 nanoseconds (1 µs)
        // 1 PCR tick = 1000/27 ≈ 37.037 ns
        Assert.Equal(1000, PcrTimingTracker.PcrToNanoseconds(27));
        Assert.Equal(1_000_000, PcrTimingTracker.PcrToNanoseconds(27_000)); // 1ms
    }

    /// <summary>
    /// Tests conversion from PCR units to microseconds.
    /// </summary>
    [Fact]
    public void PcrToMicrosecondsConvertsCorrectly()
    {
        // 27 PCR units = 1 microsecond (27 MHz → 27 cycles per µs)
        Assert.Equal(1, PcrTimingTracker.PcrToMicroseconds(27));
        Assert.Equal(1000, PcrTimingTracker.PcrToMicroseconds(27_000));
        Assert.Equal(1_000_000, PcrTimingTracker.PcrToMicroseconds(27_000_000));
    }

    /// <summary>
    /// Tests conversion from PCR units to milliseconds.
    /// </summary>
    [Fact]
    public void PcrToMillisecondsConvertsCorrectly()
    {
        // 27,000 PCR units = 1 millisecond
        Assert.Equal(1, PcrTimingTracker.PcrToMilliseconds(27_000));
        Assert.Equal(100, PcrTimingTracker.PcrToMilliseconds(2_700_000));
        Assert.Equal(1000, PcrTimingTracker.PcrToMilliseconds(27_000_000));
    }

    /// <summary>
    /// Tests conversion from nanoseconds to PCR units.
    /// </summary>
    [Fact]
    public void NanosecondsToPcrConvertsCorrectly()
    {
        // 500 ns = 500 * 27 / 1000 = 13.5 → 13 PCR units (truncated)
        Assert.Equal(13, PcrTimingTracker.NanosecondsToPcr(500));
        Assert.Equal(27, PcrTimingTracker.NanosecondsToPcr(1000)); // 1µs
    }

    #endregion

    #region PCR Wrap-Around Tests (ISO/IEC 13818-1 Section 2.4.2.1)

    /// <summary>
    /// Tests PCR delta normalization handles positive wrap-around.
    /// When PCR wraps from max to 0, the delta calculation must account for this.
    /// </summary>
    [Fact]
    public void NormalizePcrDeltaHandlesPositiveWrapAround()
    {
        // PCR near max value wraps to near 0
        const long beforeWrap = PcrWrapValue - 1_000_000;
        const long afterWrap = 1_000_000;
        var rawDelta = afterWrap - beforeWrap; // Large negative number

        var normalized = PcrTimingTracker.NormalizePcrDelta(rawDelta);

        // Should be a small positive delta (2,000,000 PCR units)
        Assert.Equal(2_000_000, normalized);
    }

    /// <summary>
    /// Tests PCR delta normalization handles negative wrap-around.
    /// Edge case where PCR appears to go backwards due to wrap.
    /// </summary>
    [Fact]
    public void NormalizePcrDeltaHandlesNegativeWrapAround()
    {
        // Simulate backward wrap scenario
        const long before = 1_000_000;
        const long after = PcrWrapValue - 1_000_000;
        var rawDelta = after - before; // Large positive number near wrap

        var normalized = PcrTimingTracker.NormalizePcrDelta(rawDelta);

        // Should be a small negative delta
        Assert.Equal(-2_000_000, normalized);
    }

    /// <summary>
    /// Tests that normal small deltas are not modified.
    /// </summary>
    [Fact]
    public void NormalizePcrDeltaPreservesSmallDeltas()
    {
        // Normal 40ms PCR interval = 1,080,000 PCR units
        const long normalDelta = 40 * 27_000; // 40ms at 27MHz

        var normalized = PcrTimingTracker.NormalizePcrDelta(normalDelta);

        Assert.Equal(normalDelta, normalized);
    }

    /// <summary>
    /// Tests wrap-around at exactly half wrap value boundary.
    /// </summary>
    [Fact]
    public void NormalizePcrDeltaAtHalfWrapBoundary()
    {
        const long halfWrap = PcrWrapValue / 2;

        // Exactly at boundary - should not be modified
        Assert.Equal(halfWrap, PcrTimingTracker.NormalizePcrDelta(halfWrap));

        // Just past boundary - should wrap
        var justPast = halfWrap + 1;
        var normalized = PcrTimingTracker.NormalizePcrDelta(justPast);
        Assert.True(normalized < 0, "Delta just past half-wrap should become negative");
    }

    #endregion

    #region Initial State Tests

    /// <summary>
    /// Tests service initializes in correct state.
    /// </summary>
    [Fact]
    public void ConstructorInitializesCorrectState()
    {
        var clock = new TestClock();
        var service = new PcrTimingTracker(programNumber: 1, clock);

        Assert.Equal(0, service.PcrCount);
        Assert.Equal(0, service.JitterViolations);
        Assert.Equal(0, service.IntervalViolations);
        Assert.Equal(0, service.DiscontinuityCount);
        Assert.Equal(ClockStatus.Initializing, service.ClockStatus);
        Assert.Equal(0, service.AccumulatedDriftPpm);
    }

    /// <summary>
    /// Tests first PCR transitions to Locking state.
    /// </summary>
    [Fact]
    public void FirstPcrTransitionsToLockingState()
    {
        var clock = new TestClock();
        var service = new PcrTimingTracker(programNumber: 1, clock);

        // Process first PCR (arbitrary value)
        var result = service.ProcessPcr(1_000_000);

        Assert.True(result);
        Assert.Equal(1, service.PcrCount);
        Assert.Equal(ClockStatus.Locking, service.ClockStatus);
    }

    /// <summary>
    /// Tests that zero PCR value is rejected.
    /// </summary>
    [Fact]
    public void ZeroPcrValueIsRejected()
    {
        var clock = new TestClock();
        var service = new PcrTimingTracker(programNumber: 1, clock);

        var result = service.ProcessPcr(0);

        Assert.False(result);
        Assert.Equal(0, service.PcrCount);
    }

    #endregion

    #region IPTV Jitter Threshold Tests (50ms)

    /// <summary>
    /// Tests that jitter within 50ms threshold does not trigger violation.
    /// IPTV streams experience network jitter - software decoders handle 10-50ms well.
    /// </summary>
    [Fact]
    public void JitterWithin50MsDoesNotTriggerViolation()
    {
        var clock = new TestClock();
        var service = new PcrTimingTracker(programNumber: 1, clock);

        // Initialize with first PCR
        _ = service.ProcessPcr(27_000_000);

        // Advance clock by exactly 40ms and send matching PCR (zero jitter)
        clock.AdvanceMs(40);
        const long expectedPcr = 27_000_000 + (40 * 27_000); // 40ms = 1,080,000 PCR units
        _ = service.ProcessPcr(expectedPcr);

        // Should have no jitter violations since PCR delta matches system delta exactly
        Assert.Equal(0, service.JitterViolations);
    }

    /// <summary>
    /// Tests that jitter exceeding 50ms threshold triggers violation.
    /// </summary>
    [Fact]
    public void JitterExceeding50MsTriggersViolation()
    {
        var clock = new TestClock();
        var service = new PcrTimingTracker(programNumber: 1, clock);

        // Initialize with first PCR
        _ = service.ProcessPcr(27_000_000);

        // Advance clock by 40ms but send PCR indicating 40ms + 60ms (60ms jitter, exceeds 50ms threshold)
        // 60ms = 60 * 27,000 = 1,620,000 PCR units
        clock.AdvanceMs(40);
        const long pcrWith60MsJitter = 27_000_000 + (40 * 27_000) + (60 * 27_000); // +60ms jitter
        _ = service.ProcessPcr(pcrWith60MsJitter);

        // Should trigger jitter violation since 60ms > 50ms threshold
        Assert.Equal(1, service.JitterViolations);
    }

    /// <summary>
    /// Tests jitter at 50ms threshold boundary - should trigger (boundary is exclusive).
    /// </summary>
    [Fact]
    public void JitterAt50MsThresholdTriggersViolation()
    {
        var clock = new TestClock();
        var service = new PcrTimingTracker(programNumber: 1, clock);

        // Initialize with first PCR
        _ = service.ProcessPcr(27_000_000);

        // Advance clock by 40ms, send PCR with exactly 51ms jitter (just over threshold)
        // 51ms = 51 * 27,000 = 1,377,000 PCR units
        clock.AdvanceMs(40);
        const long pcrWith51MsJitter = 27_000_000 + (40 * 27_000) + (51 * 27_000); // +51ms jitter
        _ = service.ProcessPcr(pcrWith51MsJitter);

        // 51ms > 50ms threshold - should trigger
        Assert.Equal(1, service.JitterViolations);
    }

    /// <summary>
    /// Tests jitter just below 50ms threshold - should not trigger.
    /// </summary>
    [Fact]
    public void JitterJustBelow50MsThreshold()
    {
        var clock = new TestClock();
        var service = new PcrTimingTracker(programNumber: 1, clock);

        // Initialize with first PCR
        _ = service.ProcessPcr(27_000_000);

        // Advance clock by 40ms, send PCR with 40ms jitter (within 50ms threshold)
        clock.AdvanceMs(40);
        const long pcrWith40MsJitter = 27_000_000 + (40 * 27_000) + (40 * 27_000); // +40ms jitter
        _ = service.ProcessPcr(pcrWith40MsJitter);

        // 40ms < 50ms threshold - should not trigger
        Assert.Equal(0, service.JitterViolations);
    }

    #endregion

    #region IPTV PCR Interval Tests (500ms)

    /// <summary>
    /// Tests PCR interval within 500ms does not trigger violation.
    /// For IPTV, we allow up to 500ms due to network buffering (vs 100ms broadcast spec).
    /// </summary>
    [Fact]
    public void PcrIntervalWithin500MsDoesNotTriggerViolation()
    {
        var clock = new TestClock();
        var service = new PcrTimingTracker(programNumber: 1, clock);

        // First PCR
        _ = service.ProcessPcr(27_000_000);

        // Advance by 400ms (within IPTV spec)
        clock.AdvanceMs(400);
        _ = service.ProcessPcr(27_000_000 + (400 * 27_000));

        Assert.Equal(0, service.IntervalViolations);
    }

    /// <summary>
    /// Tests PCR interval exceeding 500ms triggers violation.
    /// </summary>
    [Fact]
    public void PcrIntervalExceeding500MsTriggersViolation()
    {
        var clock = new TestClock();
        var service = new PcrTimingTracker(programNumber: 1, clock);

        // First PCR
        _ = service.ProcessPcr(27_000_000);

        // Advance by 600ms (exceeds 500ms IPTV threshold)
        clock.AdvanceMs(600);
        _ = service.ProcessPcr(27_000_000 + (600 * 27_000));

        Assert.Equal(1, service.IntervalViolations);
    }

    /// <summary>
    /// Tests PCR interval exactly at 500ms boundary.
    /// </summary>
    [Fact]
    public void PcrIntervalExactlyAt500Ms()
    {
        var clock = new TestClock();
        var service = new PcrTimingTracker(programNumber: 1, clock);

        // First PCR
        _ = service.ProcessPcr(27_000_000);

        // Advance by exactly 500ms
        clock.AdvanceMs(500);
        _ = service.ProcessPcr(27_000_000 + (500 * 27_000));

        // 500ms is the limit, not a violation
        Assert.Equal(0, service.IntervalViolations);
    }

    #endregion

    #region Clock Drift Detection Tests (IPTV: 10,000 PPM)

    /// <summary>
    /// Tests clock status transitions to Locked after sufficient samples with low drift.
    /// </summary>
    [Fact]
    public void ClockStatusTransitionsToLockedWithLowDrift()
    {
        var clock = new TestClock();
        var service = new PcrTimingTracker(programNumber: 1, clock);

        Assert.Equal(ClockStatus.Initializing, service.ClockStatus);

        // First PCR transitions to Locking
        _ = service.ProcessPcr(27_000_000);
        Assert.Equal(ClockStatus.Locking, service.ClockStatus);

        // Process enough PCRs with zero drift to transition to Locked
        // MinDriftSamples = 10, needs MinDriftSamples * 2 = 20 for Locked
        for (var i = 1; i <= 25; i++)
        {
            clock.AdvanceMs(40);
            _ = service.ProcessPcr(27_000_000 + (i * 40 * 27_000)); // Perfect timing
        }

        Assert.Equal(ClockStatus.Locked, service.ClockStatus);
        Assert.InRange(service.AccumulatedDriftPpm, -1, 1); // Near zero drift
    }

    /// <summary>
    /// Tests clock status transitions to Drifting when drift exceeds 10,000 PPM (1%).
    /// For IPTV, we use a much higher threshold than broadcast (30 PPM).
    /// </summary>
    [Fact]
    public void ClockStatusTransitionsToDriftingWhenDriftExceeds10000Ppm()
    {
        var clock = new TestClock();
        var service = new PcrTimingTracker(programNumber: 1, clock);

        // First PCR
        _ = service.ProcessPcr(27_000_000);

        // Process PCRs with 50,000 PPM fast drift (5% - well above 1% threshold)
        // 50,000 PPM = 50,000/1,000,000 = 0.05 extra per unit time
        // For 40ms: 40 * 27_000 * 50,000 / 1_000_000 = 54,000 extra PCR units per interval
        for (var i = 1; i <= 25; i++)
        {
            clock.AdvanceMs(40);
            long expectedPcr = 27_000_000 + (i * 40 * 27_000);
            var driftPcr = (long)(i * 40L * 27_000 * 50_000 / 1_000_000.0); // 50,000 PPM cumulative
            _ = service.ProcessPcr(expectedPcr + driftPcr);
        }

        Assert.Equal(ClockStatus.Drifting, service.ClockStatus);
        Assert.True(
            service.AccumulatedDriftPpm > 10_000,
            $"Expected drift > 10,000 PPM, got {service.AccumulatedDriftPpm}"
        );
    }

    /// <summary>
    /// Tests drift detection for source clock running fast.
    /// </summary>
    [Fact]
    public void DriftDetectsSourceClockRunningFast()
    {
        var clock = new TestClock();
        var service = new PcrTimingTracker(programNumber: 1, clock);

        const long firstPcr = 27_000_000;
        _ = service.ProcessPcr(firstPcr);
        var firstTicks = clock.ElapsedTicks;

        // Simulate source clock running 100 PPM fast
        // 100 PPM = 0.01% = source produces 1.0001x expected PCR
        long lastPcr = 0;
        for (var i = 1; i <= 20; i++)
        {
            clock.AdvanceMs(40);
            var basePcr = firstPcr + (i * 40L * 27_000);
            var extraDrift = (long)(i * 40L * 27_000 * 100 / 1_000_000.0);
            lastPcr = basePcr + extraDrift;
            _ = service.ProcessPcr(lastPcr);
        }

        // Debug info
        var totalSystemTicks = clock.ElapsedTicks - firstTicks;
        var totalPcrDelta = lastPcr - firstPcr;
        var expectedPcrDelta = (double)totalSystemTicks * 27_000_000 / clock.Frequency;
        var calculatedDrift = (totalPcrDelta - expectedPcrDelta) / expectedPcrDelta * 1_000_000;

        // Drift should be positive (source faster)
        Assert.True(
            service.AccumulatedDriftPpm > 0,
            "Drift should be positive for fast source.\n"
                + $"  Actual drift: {service.AccumulatedDriftPpm:F4} PPM\n"
                + $"  Expected (calculated): {calculatedDrift:F4} PPM\n"
                + $"  totalSystemTicks: {totalSystemTicks}\n"
                + $"  totalPcrDelta: {totalPcrDelta}\n"
                + $"  expectedPcrDelta: {expectedPcrDelta:F0}\n"
                + $"  firstTicks: {firstTicks}\n"
                + $"  clock.Frequency: {clock.Frequency}\n"
                + $"  PCR count: {service.PcrCount}"
        );
    }

    /// <summary>
    /// Tests drift detection for source clock running slow.
    /// </summary>
    [Fact]
    public void DriftDetectsSourceClockRunningSlow()
    {
        var clock = new TestClock();
        var service = new PcrTimingTracker(programNumber: 1, clock);

        const long firstPcr = 27_000_000;
        _ = service.ProcessPcr(firstPcr);

        // Simulate source clock running 100 PPM slow
        // 100 PPM = 0.01% = source produces 0.9999x expected PCR
        for (var i = 1; i <= 20; i++)
        {
            clock.AdvanceMs(40);
            var basePcr = firstPcr + (i * 40L * 27_000);
            var driftLoss = (long)(i * 40L * 27_000 * 100 / 1_000_000.0);
            _ = service.ProcessPcr(basePcr - driftLoss);
        }

        // Drift should be negative (source slower)
        Assert.True(service.AccumulatedDriftPpm < 0, "Drift should be negative for slow source");
    }

    #endregion

    #region Discontinuity Detection Tests

    /// <summary>
    /// Tests that large PCR jumps are detected as discontinuities.
    /// A discontinuity occurs when PCR jumps by more than 5 seconds.
    /// </summary>
    [Fact]
    public void LargePcrJumpDetectedAsDiscontinuity()
    {
        var clock = new TestClock();
        var service = new PcrTimingTracker(programNumber: 1, clock);

        // First PCR
        _ = service.ProcessPcr(27_000_000);
        clock.AdvanceMs(40);

        // Second PCR with normal interval
        _ = service.ProcessPcr(27_000_000 + (40 * 27_000));
        clock.AdvanceMs(40);

        // Third PCR with 10 second jump (discontinuity)
        // 10 seconds = 270,000,000 PCR units
        const long jumpedPcr = 27_000_000 + (80 * 27_000) + (10 * 27_000_000);
        _ = service.ProcessPcr(jumpedPcr);

        Assert.Equal(1, service.DiscontinuityCount);
    }

    /// <summary>
    /// Tests that discontinuity resets clock recovery state.
    /// </summary>
    [Fact]
    public void DiscontinuityResetsClockRecoveryState()
    {
        var clock = new TestClock();
        var service = new PcrTimingTracker(programNumber: 1, clock);

        // Build up some state
        _ = service.ProcessPcr(27_000_000);
        clock.AdvanceMs(40);
        _ = service.ProcessPcr(27_000_000 + (40 * 27_000));

        // Trigger discontinuity
        clock.AdvanceMs(40);
        _ = service.ProcessPcr(27_000_000 + (80 * 27_000) + (10 * 27_000_000));

        // Clock should be back to Locking after discontinuity
        Assert.Equal(ClockStatus.Locking, service.ClockStatus);
    }

    /// <summary>
    /// Tests normal PCR progression does not trigger discontinuity.
    /// </summary>
    [Fact]
    public void NormalPcrProgressionDoesNotTriggerDiscontinuity()
    {
        var clock = new TestClock();
        var service = new PcrTimingTracker(programNumber: 1, clock);

        _ = service.ProcessPcr(27_000_000);

        // Process many normal PCRs
        for (var i = 1; i <= 100; i++)
        {
            clock.AdvanceMs(40);
            _ = service.ProcessPcr(27_000_000 + (i * 40 * 27_000));
        }

        Assert.Equal(0, service.DiscontinuityCount);
    }

    #endregion

    #region Reset Tests

    /// <summary>
    /// Tests Reset clears all counters and state.
    /// </summary>
    [Fact]
    public void ResetClearsAllState()
    {
        var clock = new TestClock();
        var service = new PcrTimingTracker(programNumber: 1, clock);

        // Build up state
        _ = service.ProcessPcr(27_000_000);
        clock.AdvanceMs(40);
        _ = service.ProcessPcr(27_000_000 + (40 * 27_000));

        Assert.Equal(2, service.PcrCount);

        // Reset
        service.Reset();

        Assert.Equal(0, service.PcrCount);
        Assert.Equal(0, service.JitterViolations);
        Assert.Equal(0, service.IntervalViolations);
        Assert.Equal(ClockStatus.Initializing, service.ClockStatus);
        Assert.Equal(0, service.AccumulatedDriftPpm);
    }

    /// <summary>
    /// Tests that discontinuity count is preserved across reset.
    /// This is intentional as discontinuities are session-cumulative.
    /// </summary>
    [Fact]
    public void ResetPreservesDiscontinuityCount()
    {
        var clock = new TestClock();
        var service = new PcrTimingTracker(programNumber: 1, clock);

        // Trigger a discontinuity
        _ = service.ProcessPcr(27_000_000);
        clock.AdvanceMs(40);
        _ = service.ProcessPcr(27_000_000 + (40 * 27_000) + (10 * 27_000_000)); // 10 second jump

        var discountBeforeReset = service.DiscontinuityCount;
        Assert.Equal(1, discountBeforeReset);

        service.Reset();

        // Discontinuity count should be preserved
        Assert.Equal(discountBeforeReset, service.DiscontinuityCount);
    }

    #endregion

    #region Diagnostics Tests

    /// <summary>
    /// Tests GetDiagnostics returns comprehensive information.
    /// </summary>
    [Fact]
    public void GetDiagnosticsReturnsComprehensiveInfo()
    {
        var clock = new TestClock();
        var service = new PcrTimingTracker(programNumber: 42, clock);

        _ = service.ProcessPcr(27_000_000);

        var diagnostics = service.GetDiagnostics();

        Assert.Contains("Program 42", diagnostics);
        Assert.Contains("PCRs Processed", diagnostics);
        Assert.Contains("Clock Status", diagnostics);
        Assert.Contains("Drift", diagnostics);
        Assert.Contains("Jitter Violations", diagnostics);
        Assert.Contains("Interval Violations", diagnostics);
        Assert.Contains("Health", diagnostics);
        Assert.Contains("ns", diagnostics); // Should use nanoseconds now
    }

    #endregion

    #region EstimatedPcrTime Tests

    /// <summary>
    /// Tests estimated PCR time interpolation.
    /// </summary>
    [Fact]
    public void EstimatedPcrTimeInterpolatesCorrectly()
    {
        var clock = new TestClock();
        var service = new PcrTimingTracker(programNumber: 1, clock);

        const long basePcr = 27_000_000;
        _ = service.ProcessPcr(basePcr);
        clock.AdvanceMs(50);
        _ = service.ProcessPcr(basePcr + (50 * 27_000)); // 50ms later

        // Advance clock by 10ms more
        clock.AdvanceMs(10);
        var estimated = service.EstimatedPcrTime;

        // Should be approximately basePcr + 60ms worth of PCR units
        var expected = basePcr + (60 * 27_000);
        Assert.InRange(estimated, expected - 1000, expected + 1000);
    }

    #endregion

    #region Adaptive Buffer Tests

    /// <summary>
    /// Tests that adaptive buffer starts at target value.
    /// </summary>
    [Fact]
    public void AdaptiveBufferStartsAtTarget()
    {
        var clock = new TestClock();
        var service = new PcrTimingTracker(programNumber: 1, clock);

        Assert.Equal(150, service.CurrentBufferMs);
    }

    /// <summary>
    /// Tests adaptive buffer increases when jitter violations occur.
    /// </summary>
    [Fact]
    public void AdaptiveBufferIncreasesOnJitterViolation()
    {
        var clock = new TestClock();
        var service = new PcrTimingTracker(programNumber: 1, clock);

        var initialBuffer = service.CurrentBufferMs;

        // Process PCRs with high jitter to trigger violations (need >50ms for IPTV threshold)
        _ = service.ProcessPcr(27_000_000);
        clock.AdvanceMs(40);
        // +60ms jitter (over 50ms IPTV threshold): 60ms * 27,000 = 1,620,000 PCR units
        _ = service.ProcessPcr(27_000_000 + (40 * 27_000) + (60 * 27_000));

        Assert.True(service.JitterViolations > 0);
        Assert.True(service.CurrentBufferMs > initialBuffer, "Buffer should increase on jitter violation");
    }

    #endregion
}
