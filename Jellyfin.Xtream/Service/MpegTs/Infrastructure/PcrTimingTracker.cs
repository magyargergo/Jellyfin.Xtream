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
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using Jellyfin.Xtream.Service.MpegTs.Core;
using Jellyfin.Xtream.Service.MpegTs.Models;
using Jellyfin.Xtream.Service.MpegTs.UseCases;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.MpegTs.Infrastructure;

/// <summary>
/// Per-program PCR timing tracker that handles jitter detection and clock drift tracking.
/// Combines the functionality of PcrJitterBuffer and ClockRecoveryService into a single tracker.
/// </summary>
/// <remarks>
/// <para>
/// This is NOT a DI service - instances are created per-program by <see cref="ProgramInfoService"/>.
/// </para>
/// <para>
/// MPEG-TS PCR (Program Clock Reference) serves two purposes per ISO/IEC 13818-1:
/// 1. Short-term: Jitter measurement for TR 101 290 compliance (±500ns accuracy threshold)
/// 2. Long-term: Clock drift detection for source quality assessment (±30 PPM per spec)
/// </para>
/// <para>
/// PCR accuracy per TR 101 290 section 5.2.2 (Priority 2 indicator 2.4):
/// "PCR_accuracy_error occurs when a transmitted PCR value differs from what is expected
/// by more than ±500 nanoseconds."
/// </para>
/// <para>
/// PCR interval per ISO/IEC 13818-1 clause 2.7.2:
/// "The maximum interval between PCRs shall be 100 ms."
/// </para>
/// </remarks>
/// <remarks>
/// Initializes a new instance of the <see cref="PcrTimingTracker"/> class with a custom clock.
/// </remarks>
/// <param name="programNumber">The program number for logging context.</param>
/// <param name="systemClock">The system clock implementation for timing.</param>
/// <param name="logger">Optional logger for diagnostics.</param>
public sealed class PcrTimingTracker(int programNumber, ISystemClock systemClock, ILogger? logger = null)
{
    /// <summary>
    /// PCR frequency in Hz (27 MHz as per ISO/IEC 13818-1 clause 2.4.2.2).
    /// The system clock frequency is 27,000,000 Hz.
    /// </summary>
    public const long PcrFrequency = 27_000_000;

    /// <summary>
    /// PCR wrap value for 33-bit base counter with 9-bit extension (300 factor).
    /// Per ISO/IEC 13818-1, PCR = PCR_base * 300 + PCR_ext, where PCR_base is 33 bits.
    /// </summary>
    public const long PcrWrapValue = (1L << 33) * 300;

    /// <summary>
    /// TR 101 290 PCR accuracy threshold in nanoseconds (for broadcast compliance).
    /// Per section 5.2.2: "PCR_accuracy_error: Accuracy of PCR ±500 ns".
    /// NOTE: This is only relevant for hardware IRDs. Software decoders tolerate much higher jitter.
    /// </summary>
    public const long BroadcastJitterThresholdNs = 500;

    /// <summary>
    /// IPTV-appropriate PCR jitter threshold in nanoseconds.
    /// Internet streaming typically has 10-50ms of network jitter.
    /// Software decoders (like FFmpeg) handle this well with de-jitter buffering.
    /// We use 50ms as the threshold - anything higher indicates serious stream issues.
    /// </summary>
    public const long JitterThresholdNs = 50_000_000; // 50ms for IPTV tolerance

    /// <summary>
    /// IPTV-appropriate PCR jitter threshold in PCR units (27 MHz).
    /// 50ms * 27,000 = 1,350,000 PCR units.
    /// </summary>
    public const long JitterThresholdPcr = 1_350_000; // 50ms * 27,000 ticks/ms

    /// <summary>
    /// Maximum PCR interval per ISO/IEC 13818-1 clause 2.7.2.
    /// "The maximum interval between PCRs shall be 100 ms."
    /// For IPTV, we allow up to 500ms due to network buffering.
    /// </summary>
    public const long MaxPcrIntervalMs = 500;

    /// <summary>
    /// ISO/IEC 13818-1 clock drift threshold in PPM (for broadcast compliance).
    /// The spec allows ±30 PPM for the 27 MHz system clock.
    /// NOTE: IPTV encoders often have much higher drift - this is informational only.
    /// </summary>
    public const double BroadcastDriftPpm = 30.0;

    /// <summary>
    /// IPTV-appropriate clock drift threshold in PPM.
    /// Non-broadcast encoders and network effects can cause significant drift.
    /// 10,000 PPM (1%) is a reasonable threshold for triggering warnings.
    /// Higher drift may cause A/V sync issues over time.
    /// </summary>
    public const double MaxDriftPpm = 10_000.0;

    // Configuration
    private const int MaxBufferMs = 500;
    private const int TargetBufferMs = 150;
    private const int MaxJitterSamples = 50;
    private const int MinDriftSamples = 10;
    private const long DiscontinuityThresholdPcr = 5 * PcrFrequency; // 5 seconds

    /// <summary>
    /// Cooldown period in seconds after Reset() during which discontinuity warnings are suppressed.
    /// After reconnection, source streams often have inherent PCR jumps that would trigger false warnings.
    /// This allows the PCR timing to stabilize before reporting issues.
    /// </summary>
    private const int PostResetCooldownSeconds = 15;

    private readonly ILogger? _logger = logger;
    private readonly int _programNumber = programNumber;
    private readonly ISystemClock _systemClock = systemClock ?? throw new ArgumentNullException(nameof(systemClock));

    // Fixed-size ring buffer for jitter samples (zero allocation after init)
    private readonly long[] _jitterSamples = new long[MaxJitterSamples];
    private int _jitterWriteIndex;
    private int _jitterCount;

    // PCR tracking state (shared between jitter and drift calculations)
    private long _firstPcrValue;
    private long _firstPcrSystemTicks;
    private long _lastPcrValue = -1;
    private long _lastPcrSystemTicks = -1;

    // Counters
    private long _pcrCount;
    private long _jitterViolations;
    private long _intervalViolations;
    private long _discontinuityCount;

    // Rate limiting for warnings
    private DateTime _lastJitterWarning = DateTime.MinValue;
    private DateTime _lastIntervalWarning = DateTime.MinValue;
    private DateTime _lastDriftWarning = DateTime.MinValue;

    // Post-reset cooldown tracking
    // Initialize to MinValue so cooldown is NOT active on fresh construction
    // Cooldown only starts after explicit Reset() call (reconnection scenario)
    private DateTime _lastResetTime = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="PcrTimingTracker"/> class.
    /// </summary>
    /// <param name="programNumber">The program number for logging context.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public PcrTimingTracker(int programNumber, ILogger? logger = null)
        : this(programNumber, new StopwatchClock(), logger) { }

    /// <summary>
    /// Gets the total number of PCR values processed.
    /// </summary>
    public long PcrCount => Interlocked.Read(ref _pcrCount);

    /// <summary>
    /// Gets the count of jitter violations (exceeding ±500ns per TR 101 290).
    /// </summary>
    public long JitterViolations => Interlocked.Read(ref _jitterViolations);

    /// <summary>
    /// Gets the count of PCR interval violations (>100ms per ISO/IEC 13818-1).
    /// </summary>
    public long IntervalViolations => Interlocked.Read(ref _intervalViolations);

    /// <summary>
    /// Gets the count of clock discontinuities detected.
    /// </summary>
    public long DiscontinuityCount => Interlocked.Read(ref _discontinuityCount);

    /// <summary>
    /// Event raised when a TR 101 290 jitter violation is detected.
    /// </summary>
    public event EventHandler<StreamQualityViolationEventArgs>? JitterViolationDetected;

    /// <summary>
    /// Gets the current adaptive jitter buffer size in milliseconds.
    /// </summary>
    public int CurrentBufferMs { get; private set; } = TargetBufferMs;

    /// <summary>
    /// Gets the current clock recovery status.
    /// </summary>
    public ClockStatus ClockStatus { get; private set; } = ClockStatus.Initializing;

    /// <summary>
    /// Gets the accumulated clock drift in parts per million (PPM).
    /// Positive = source faster than local, Negative = source slower.
    /// </summary>
    public double AccumulatedDriftPpm { get; private set; }

    /// <summary>
    /// Gets the instantaneous drift rate in PPM.
    /// </summary>
    public double InstantDriftPpm { get; private set; }

    /// <summary>
    /// Gets the peak drift observed in PPM.
    /// </summary>
    public double PeakDriftPpm { get; private set; }

    /// <summary>
    /// Gets the estimated current PCR time using linear interpolation.
    /// </summary>
    public long EstimatedPcrTime
    {
        get
        {
            if (_pcrCount < 2)
            {
                return _lastPcrValue;
            }

            var elapsedTicks = _systemClock.ElapsedTicks - _lastPcrSystemTicks;
            var elapsedPcr = TicksToPcr(elapsedTicks, _systemClock.Frequency);

            // Apply drift correction if significant
            if (Math.Abs(AccumulatedDriftPpm) > 1)
            {
                elapsedPcr = (long)(elapsedPcr * (1.0 + (AccumulatedDriftPpm / 1_000_000.0)));
            }

            return _lastPcrValue + elapsedPcr;
        }
    }

    /// <summary>
    /// Processes a PCR value and updates both jitter and drift statistics.
    /// </summary>
    /// <param name="pcrValue">The PCR value in 27 MHz units.</param>
    /// <returns>True if PCR was processed successfully.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ProcessPcr(long pcrValue)
    {
        if (pcrValue == 0)
        {
            return false;
        }

        var systemTicks = _systemClock.ElapsedTicks;
        _ = Interlocked.Increment(ref _pcrCount);

        // First PCR - initialize
        if (_lastPcrValue == -1)
        {
            _firstPcrValue = pcrValue;
            _firstPcrSystemTicks = systemTicks;
            _lastPcrValue = pcrValue;
            _lastPcrSystemTicks = systemTicks;
            ClockStatus = ClockStatus.Locking;

            _logger?.LogDebugIfEnabled(
                "PCR Timing (Program {ProgramNumber}): First PCR = {PcrValue}",
                _programNumber,
                pcrValue
            );
            return true;
        }

        // Calculate deltas
        var pcrDelta = NormalizePcrDelta(pcrValue - _lastPcrValue);
        var systemDelta = systemTicks - _lastPcrSystemTicks;
        var expectedPcrDelta = TicksToPcr(systemDelta, _systemClock.Frequency);

        // Check for discontinuity
        var discrepancy = Math.Abs(pcrDelta - expectedPcrDelta);
        if (discrepancy > DiscontinuityThresholdPcr)
        {
            HandleDiscontinuity(pcrValue, systemTicks, pcrDelta);
            return true;
        }

        // Process jitter (short-term) - TR 101 290 compliance
        ProcessJitter(pcrDelta, expectedPcrDelta);

        // Process interval compliance - ISO/IEC 13818-1
        ProcessInterval(pcrDelta);

        // Process drift (long-term)
        ProcessDrift(pcrValue, systemTicks, pcrDelta, systemDelta);

        // Update state
        _lastPcrValue = pcrValue;
        _lastPcrSystemTicks = systemTicks;

        // Periodic diagnostics
        if (_pcrCount % 100 == 0)
        {
            LogDiagnostics();
        }

        return true;
    }

    /// <summary>
    /// Resets all timing state for a new stream session.
    /// </summary>
    public void Reset()
    {
        _firstPcrValue = 0;
        _firstPcrSystemTicks = 0;
        _lastPcrValue = -1;
        _lastPcrSystemTicks = -1;

        _ = Interlocked.Exchange(ref _pcrCount, 0);
        _ = Interlocked.Exchange(ref _jitterViolations, 0);
        _ = Interlocked.Exchange(ref _intervalViolations, 0);
        // Note: Don't reset discontinuity count - it's cumulative for the session

        AccumulatedDriftPpm = 0;
        InstantDriftPpm = 0;
        CurrentBufferMs = TargetBufferMs;
        ClockStatus = ClockStatus.Initializing;

        // Reset ring buffer (just reset indices, no allocation)
        _jitterWriteIndex = 0;
        _jitterCount = 0;
        _systemClock.Restart();

        // Start cooldown period to suppress false discontinuity warnings after reconnection
        _lastResetTime = DateTime.UtcNow;
    }

    /// <summary>
    /// Gets a value indicating whether we're in the post-reset cooldown period.
    /// During this period, discontinuity warnings are suppressed as they're likely
    /// due to source-inherent timing issues rather than actual stream problems.
    /// </summary>
    public bool IsInPostResetCooldown =>
        _lastResetTime != DateTime.MinValue
        && (DateTime.UtcNow - _lastResetTime).TotalSeconds < PostResetCooldownSeconds;

    /// <summary>
    /// Gets comprehensive diagnostics about PCR timing status.
    /// </summary>
    /// <returns>Formatted diagnostics string.</returns>
    public string GetDiagnostics()
    {
        var count = PcrCount;
        var jitterViols = JitterViolations;
        var intervalViols = IntervalViolations;
        var jitterRate = count > 0 ? jitterViols * 100.0 / count : 0;

        var avgJitterNs = CalculateAverageJitterNs();

        return $"PCR Timing (Program {_programNumber}):\n"
            + $"  PCRs Processed: {count:N0}\n"
            + $"  Clock Status: {ClockStatus}\n"
            + $"  Drift: {AccumulatedDriftPpm:+0.0;-0.0;0} PPM (peak: {PeakDriftPpm:+0.0;-0.0;0})\n"
            + $"  Jitter Violations: {jitterViols:N0} ({jitterRate:F2}%)\n"
            + $"  Interval Violations: {intervalViols:N0}\n"
            + $"  Discontinuities: {DiscontinuityCount}\n"
            + $"  Avg Jitter: {avgJitterNs}ns\n"
            + $"  Adaptive Buffer: {CurrentBufferMs}ms\n"
            + $"  Health: {GetHealthStatus(jitterRate, avgJitterNs)}";
    }

    /// <summary>
    /// Converts clock ticks to PCR units (27 MHz).
    /// </summary>
    /// <param name="ticks">The number of clock ticks.</param>
    /// <param name="ticksPerSecond">The clock frequency in ticks per second.</param>
    /// <returns>The equivalent PCR value in 27 MHz units.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long TicksToPcr(long ticks, long ticksPerSecond) => ticks * PcrFrequency / ticksPerSecond;

    /// <summary>
    /// Converts PCR units to nanoseconds.
    /// </summary>
    /// <param name="pcrUnits">The PCR value in 27 MHz units.</param>
    /// <returns>The equivalent time in nanoseconds.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long PcrToNanoseconds(long pcrUnits) =>
        // 27 MHz = 27 ticks per microsecond = 0.027 ticks per nanosecond
        // 1 PCR tick = 1000/27 ≈ 37.037ns
        pcrUnits * 1000 / 27;

    /// <summary>
    /// Converts PCR units to microseconds.
    /// </summary>
    /// <param name="pcrUnits">The PCR value in 27 MHz units.</param>
    /// <returns>The equivalent time in microseconds.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long PcrToMicroseconds(long pcrUnits) => pcrUnits / 27; // 27 MHz → µs

    /// <summary>
    /// Converts PCR units to milliseconds.
    /// </summary>
    /// <param name="pcrUnits">The PCR value in 27 MHz units.</param>
    /// <returns>The equivalent time in milliseconds.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long PcrToMilliseconds(long pcrUnits) => pcrUnits / 27_000; // 27 MHz → ms

    /// <summary>
    /// Converts nanoseconds to PCR units.
    /// </summary>
    /// <param name="nanoseconds">The time in nanoseconds.</param>
    /// <returns>The equivalent PCR value in 27 MHz units.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long NanosecondsToPcr(long nanoseconds) => nanoseconds * 27 / 1000;

    /// <summary>
    /// Normalizes a PCR delta to handle wrap-around.
    /// </summary>
    /// <param name="delta">The raw PCR delta.</param>
    /// <returns>The normalized delta accounting for wrap-around.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long NormalizePcrDelta(long delta)
    {
        return delta < -PcrWrapValue / 2 ? delta + PcrWrapValue
            : delta > PcrWrapValue / 2 ? delta - PcrWrapValue
            : delta;
    }

    private void HandleDiscontinuity(long pcrValue, long systemTicks, long pcrDelta)
    {
        _ = Interlocked.Increment(ref _discontinuityCount);

        var jumpMs = PcrToMilliseconds(pcrDelta);

        // During post-reset cooldown, log at Debug level instead of Warning
        // Source streams often have inherent PCR jumps after reconnection that aren't actual problems
        if (IsInPostResetCooldown)
        {
            _logger?.LogDebugIfEnabled(
                "PCR Timing (Program {ProgramNumber}): Discontinuity detected during cooldown - PCR jumped {JumpMs:F1}ms (suppressed, {SecondsRemaining:F0}s remaining)",
                _programNumber,
                jumpMs,
                PostResetCooldownSeconds - (DateTime.UtcNow - _lastResetTime).TotalSeconds
            );
        }
        else
        {
            _logger?.PluginLogWarning(
                "PCR Timing (Program {ProgramNumber}): Discontinuity detected - PCR jumped {JumpMs:F1}ms",
                _programNumber,
                jumpMs
            );
        }

        // Reset to new reference point
        _firstPcrValue = pcrValue;
        _firstPcrSystemTicks = systemTicks;
        _lastPcrValue = pcrValue;
        _lastPcrSystemTicks = systemTicks;
        ClockStatus = ClockStatus.Locking;
        AccumulatedDriftPpm = 0;
        InstantDriftPpm = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ProcessJitter(long pcrDelta, long expectedPcrDelta)
    {
        // Calculate jitter as the difference between actual and expected PCR delta
        var jitterPcr = pcrDelta - expectedPcrDelta;
        var jitterNs = PcrToNanoseconds(jitterPcr);
        var absJitterNs = Math.Abs(jitterNs);

        // Store absolute jitter in ring buffer (zero allocation, O(1))
        _jitterSamples[_jitterWriteIndex] = absJitterNs;
        _jitterWriteIndex = (_jitterWriteIndex + 1) % MaxJitterSamples;
        if (_jitterCount < MaxJitterSamples)
        {
            _jitterCount++;
        }

        // During post-reset cooldown, don't count or log jitter violations
        // Source streams need time to stabilize after reconnection
        if (IsInPostResetCooldown)
        {
            return;
        }

        // IPTV-appropriate jitter threshold (50ms) - software decoders handle network jitter well
        // Only count as violation and log if jitter is truly excessive
        if (absJitterNs > JitterThresholdNs)
        {
            _ = Interlocked.Increment(ref _jitterViolations);

            // Rate-limit logging to once per 2 minutes for IPTV streams
            // High jitter is expected in internet streaming - don't spam logs
            var now = _systemClock.UtcNow;
            if ((now - _lastJitterWarning).TotalSeconds >= 120)
            {
                _lastJitterWarning = now;
                var jitterMs = absJitterNs / 1_000_000.0;
                _logger?.PluginLogWarning(
                    "PCR Timing (Program {ProgramNumber}): High jitter {JitterMs:F1}ms (IPTV threshold: {ThresholdMs}ms). This may indicate network congestion.",
                    _programNumber,
                    jitterMs,
                    JitterThresholdNs / 1_000_000.0
                );

                // Only raise event for truly severe jitter (>100ms) that affects playback
                if (absJitterNs > 100_000_000) // 100ms
                {
                    JitterViolationDetected?.Invoke(
                        this,
                        new StreamQualityViolationEventArgs(
                            "PCR Jitter Violation",
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"Program {_programNumber}: PCR jitter {jitterMs:F1}ms - severe network issues detected"
                            )
                        )
                    );
                }
            }

            // Increase adaptive buffer for high jitter
            CurrentBufferMs = Math.Min(CurrentBufferMs + 10, MaxBufferMs);
        }
    }

    private void ProcessInterval(long pcrDelta)
    {
        // During post-reset cooldown, skip interval validation
        // Source streams need time to stabilize after reconnection
        if (IsInPostResetCooldown)
        {
            return;
        }

        var intervalMs = PcrToMilliseconds(pcrDelta);

        if (intervalMs > MaxPcrIntervalMs)
        {
            _ = Interlocked.Increment(ref _intervalViolations);

            var now = _systemClock.UtcNow;
            if ((now - _lastIntervalWarning).TotalSeconds >= 30)
            {
                _lastIntervalWarning = now;
                _logger?.PluginLogWarning(
                    "PCR Timing (Program {ProgramNumber}): PCR interval {IntervalMs}ms exceeds ISO/IEC 13818-1 spec (<{MaxMs}ms)",
                    _programNumber,
                    intervalMs,
                    MaxPcrIntervalMs
                );
            }
        }
    }

    private void ProcessDrift(long pcrValue, long systemTicks, long pcrDelta, long systemDelta)
    {
        // During post-reset cooldown, skip drift processing
        // Drift calculations are unreliable during stream stabilization
        if (IsInPostResetCooldown)
        {
            return;
        }

        // Calculate instantaneous drift
        if (systemDelta > 0)
        {
            double expectedDelta = TicksToPcr(systemDelta, _systemClock.Frequency);
            var driftRatio = (pcrDelta - expectedDelta) / expectedDelta;
            InstantDriftPpm = driftRatio * 1_000_000.0;
        }

        // Calculate accumulated drift over session
        var count = PcrCount;
        if (count >= MinDriftSamples)
        {
            var totalPcrDelta = NormalizePcrDelta(pcrValue - _firstPcrValue);
            var totalSystemDelta = systemTicks - _firstPcrSystemTicks;

            if (totalSystemDelta > 0)
            {
                double expectedTotal = TicksToPcr(totalSystemDelta, _systemClock.Frequency);
                var driftRatio = (totalPcrDelta - expectedTotal) / expectedTotal;
                AccumulatedDriftPpm = driftRatio * 1_000_000.0;

                if (Math.Abs(AccumulatedDriftPpm) > Math.Abs(PeakDriftPpm))
                {
                    PeakDriftPpm = AccumulatedDriftPpm;
                }

                // Update clock status - using IPTV-appropriate threshold (10,000 PPM = 1%)
                // Broadcast spec is ±30 PPM, but IPTV encoders often have much higher drift
                if (Math.Abs(AccumulatedDriftPpm) > MaxDriftPpm)
                {
                    ClockStatus = ClockStatus.Drifting;
                    LogDriftWarning();
                }
                else if (count >= MinDriftSamples * 2)
                {
                    ClockStatus = ClockStatus.Locked;
                }
            }
        }
    }

    private void LogDriftWarning()
    {
        var now = _systemClock.UtcNow;

        // Rate-limit drift warnings to once per 5 minutes for IPTV
        // High drift is common in internet streaming and doesn't affect software playback
        if ((now - _lastDriftWarning).TotalSeconds < 300)
        {
            return;
        }

        _lastDriftWarning = now;

        // Only log as warning if drift is truly severe (>1%)
        // Lower drift is just logged at debug level
        var driftPercent = Math.Abs(AccumulatedDriftPpm) / 10_000.0;
        if (driftPercent > 1.0)
        {
            _logger?.PluginLogWarning(
                "PCR Timing (Program {ProgramNumber}): Clock drift {DriftPercent:F2}% ({DriftPpm:F0} PPM) - source encoder clock may be unstable",
                _programNumber,
                driftPercent,
                AccumulatedDriftPpm
            );
        }
        else
        {
            _logger?.LogDebugIfEnabled(
                "PCR Timing (Program {ProgramNumber}): Clock drift {DriftPpm:F1} PPM (within IPTV tolerance)",
                _programNumber,
                AccumulatedDriftPpm
            );
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long CalculateAverageJitterNs()
    {
        var count = _jitterCount;
        if (count == 0)
        {
            return 0;
        }

        // Sum only valid samples in the ring buffer
        long sum = 0;
        for (var i = 0; i < count; i++)
        {
            sum += _jitterSamples[i];
        }

        return sum / count;
    }

    private static string GetHealthStatus(double jitterViolationRate, long avgJitterNs)
    {
        // Health based on IPTV-appropriate thresholds (not broadcast TR 101 290)
        // Software decoders handle jitter well - only flag severe issues
        var avgJitterMs = avgJitterNs / 1_000_000.0;
        return jitterViolationRate > 50 || avgJitterMs > 100 ? "DEGRADED"
            : jitterViolationRate > 20 || avgJitterMs > 50 ? "WARNING"
            : "HEALTHY";
    }

    private void LogDiagnostics()
    {
        var count = PcrCount;
        var jitterViols = JitterViolations;
        var jitterRate = count > 0 ? jitterViols * 100.0 / count : 0;

        _logger?.LogDebugIfEnabled(
            "PCR Timing (Program {ProgramNumber}): {Count} PCRs, {JitterViols} jitter ({Rate:F2}%), drift: {Drift:F1} PPM, buffer: {Buffer}ms",
            _programNumber,
            count,
            jitterViols,
            jitterRate,
            AccumulatedDriftPpm,
            CurrentBufferMs
        );
    }
}
