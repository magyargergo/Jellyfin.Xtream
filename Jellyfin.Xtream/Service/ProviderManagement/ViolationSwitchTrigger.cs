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
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Jellyfin.Xtream.Service.ProviderManagement;

/// <summary>
/// Configuration for violation-triggered switching thresholds.
/// Follows OCP: extend by creating new configuration, not modifying this class.
/// </summary>
public sealed record ViolationSwitchConfiguration
{
    /// <summary>Gets the number of consecutive PAT violations before triggering switch.</summary>
    public int PatViolationsThreshold { get; init; } = 3;

    /// <summary>Gets the number of consecutive PCR violations before triggering switch.</summary>
    public int PcrViolationsThreshold { get; init; } = 5;

    /// <summary>Gets the number of consecutive continuity counter violations before triggering switch.</summary>
    public int ContinuityViolationsThreshold { get; init; } = 3;

    /// <summary>Gets the number of consecutive PCR jitter violations before triggering switch (TR 101 290 Priority 2).</summary>
    public int JitterViolationsThreshold { get; init; } = 5;

    /// <summary>Gets the A/V drift threshold in milliseconds (300ms is audible threshold).</summary>
    public double DriftThresholdMs { get; init; } = 300.0;

    /// <summary>Gets the number of consecutive A/V drift violations before triggering switch.</summary>
    /// <remarks>
    /// During stream startup, timestamps are unstable and can produce spurious drift readings.
    /// Requiring multiple consecutive violations prevents false-positive switches.
    /// </remarks>
    public int DriftViolationsThreshold { get; init; } = 3;

    /// <summary>Gets the warmup period in seconds during which violations are ignored.</summary>
    /// <remarks>
    /// IPTV streams require stabilization time after connection. During warmup:
    /// - PCR timing is unreliable (high jitter, clock drift measurements)
    /// - A/V sync calculations are based on incomplete data
    /// - Triggering switches during this period causes playback loops
    /// </remarks>
    public int WarmupPeriodSeconds { get; init; } = 5;

    /// <summary>Gets the base cooldown period in seconds between violation-triggered switches.</summary>
    public int BaseCooldownSeconds { get; init; } = 30;

    /// <summary>Gets the maximum cooldown period in seconds after repeated failures.</summary>
    public int MaxCooldownSeconds { get; init; } = 180;

    /// <summary>Gets the cooldown multiplier applied after each consecutive failure.</summary>
    public double CooldownMultiplier { get; init; } = 2.0;

    /// <summary>Gets the maximum consecutive failures before disabling violation-triggered switching.</summary>
    public int MaxConsecutiveFailures { get; init; } = 5;

    /// <summary>Default configuration based on TR 101 290 recommendations.</summary>
    public static readonly ViolationSwitchConfiguration Default = new();
}

/// <summary>
/// Evaluates stream quality violations and determines if provider switching should be triggered.
/// Follows SRP: only responsible for violation counting and threshold evaluation.
/// Follows DRY: centralizes violation logic instead of duplicating in Restream.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ViolationSwitchTrigger"/> class.
/// </remarks>
/// <param name="config">Optional configuration. Uses defaults if not provided.</param>
public sealed class ViolationSwitchTrigger(ViolationSwitchConfiguration? config = null) : IViolationSwitchTrigger
{
    private readonly ViolationSwitchConfiguration _config = config ?? ViolationSwitchConfiguration.Default;
    private readonly ConcurrentDictionary<string, StreamViolationState> _states = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ViolationEvaluationResult RecordViolation(string streamId, string violationType)
    {
        var state = GetOrCreateState(streamId);

        // During warmup period, count violations but don't trigger switches
        // This prevents false-positive switches during stream stabilization
        var inWarmup = IsInWarmupPeriod(state);

        // Categorize and count violation
        if (violationType.Contains("PAT", StringComparison.OrdinalIgnoreCase))
        {
            var count = Interlocked.Increment(ref state.PatViolations);
            if (!inWarmup && count >= _config.PatViolationsThreshold)
            {
                return EvaluateSwitch(state, $"PAT violations ({count} consecutive)");
            }
        }
        else if (violationType.Contains("PCR", StringComparison.OrdinalIgnoreCase))
        {
            var count = Interlocked.Increment(ref state.PcrViolations);
            if (!inWarmup && count >= _config.PcrViolationsThreshold)
            {
                return EvaluateSwitch(state, $"PCR violations ({count} consecutive)");
            }
        }
        else if (violationType.Contains("Continuity", StringComparison.OrdinalIgnoreCase))
        {
            var count = Interlocked.Increment(ref state.ContinuityViolations);
            if (!inWarmup && count >= _config.ContinuityViolationsThreshold)
            {
                return EvaluateSwitch(state, $"Continuity counter violations ({count} consecutive)");
            }
        }
        else if (violationType.Contains("Jitter", StringComparison.OrdinalIgnoreCase))
        {
            var count = Interlocked.Increment(ref state.JitterViolations);
            if (!inWarmup && count >= _config.JitterViolationsThreshold)
            {
                return EvaluateSwitch(state, $"PCR jitter violations ({count} consecutive, TR 101 290 Priority 2)");
            }
        }

        return ViolationEvaluationResult.NoSwitch;
    }

    /// <inheritdoc />
    public ViolationEvaluationResult RecordDrift(string streamId, double driftMs)
    {
        var state = GetOrCreateState(streamId);

        // During warmup period, ignore all drift violations
        // IPTV streams need time to stabilize after connection
        if (IsInWarmupPeriod(state))
        {
            return ViolationEvaluationResult.NoSwitch;
        }

        // If drift is below threshold, reset consecutive counter and return
        if (Math.Abs(driftMs) < _config.DriftThresholdMs)
        {
            // Good drift reading - reset consecutive violations
            _ = Interlocked.Exchange(ref state.DriftViolations, 0);
            return ViolationEvaluationResult.NoSwitch;
        }

        // Count consecutive drift violations (like PAT/PCR violations)
        var count = Interlocked.Increment(ref state.DriftViolations);
        if (count >= _config.DriftViolationsThreshold)
        {
            return EvaluateSwitch(state, $"A/V drift {driftMs:F0}ms ({count} consecutive violations)");
        }

        return ViolationEvaluationResult.NoSwitch;
    }

    /// <summary>
    /// Checks if the stream is still in the warmup period.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsInWarmupPeriod(StreamViolationState state)
    {
        return (DateTime.UtcNow - state.StreamStartTime).TotalSeconds < _config.WarmupPeriodSeconds;
    }

    /// <inheritdoc />
    public void ResetCounters(string streamId)
    {
        if (_states.TryGetValue(streamId, out var state))
        {
            _ = Interlocked.Exchange(ref state.PatViolations, 0);
            _ = Interlocked.Exchange(ref state.PcrViolations, 0);
            _ = Interlocked.Exchange(ref state.ContinuityViolations, 0);
            _ = Interlocked.Exchange(ref state.JitterViolations, 0);
            _ = Interlocked.Exchange(ref state.DriftViolations, 0);

            // Reset warmup timer for new stream connection
            state.StreamStartTime = DateTime.UtcNow;
        }
    }

    /// <inheritdoc />
    public void AcknowledgeSuccess(string streamId)
    {
        if (_states.TryGetValue(streamId, out var state))
        {
            // Reset consecutive failures on successful switch - allows normal cooldown again
            _ = Interlocked.Exchange(ref state.ConsecutiveFailures, 0);
        }
    }

    /// <summary>
    /// Evaluates whether a switch should occur, considering adaptive cooldown and max failures.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ViolationEvaluationResult EvaluateSwitch(StreamViolationState state, string reason)
    {
        var now = DateTime.UtcNow;
        var lastAttempt = state.LastSwitchAttempt;
        var consecutiveFailures = state.ConsecutiveFailures;

        // Check if max consecutive failures reached - disable violation-triggered switching
        if (consecutiveFailures >= _config.MaxConsecutiveFailures)
        {
            return ViolationEvaluationResult.MaxFailuresReached;
        }

        // Calculate adaptive cooldown: base * (multiplier ^ consecutiveFailures), capped at max
        var adaptiveCooldown = Math.Min(
            _config.BaseCooldownSeconds * Math.Pow(_config.CooldownMultiplier, consecutiveFailures),
            _config.MaxCooldownSeconds
        );

        if ((now - lastAttempt).TotalSeconds < adaptiveCooldown)
        {
            return ViolationEvaluationResult.CooldownActive;
        }

        // Atomically update last attempt time and increment consecutive failures
        state.LastSwitchAttempt = now;
        _ = Interlocked.Increment(ref state.ConsecutiveFailures);

        // Reset violation counters after triggering switch (but keep consecutive failures)
        _ = Interlocked.Exchange(ref state.PatViolations, 0);
        _ = Interlocked.Exchange(ref state.PcrViolations, 0);
        _ = Interlocked.Exchange(ref state.ContinuityViolations, 0);
        _ = Interlocked.Exchange(ref state.JitterViolations, 0);
        _ = Interlocked.Exchange(ref state.DriftViolations, 0);

        // Reset warmup timer so the new connection gets stabilization time
        state.StreamStartTime = now;

        return new ViolationEvaluationResult(ShouldSwitch: true, reason);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private StreamViolationState GetOrCreateState(string streamId) =>
        _states.GetOrAdd(streamId, static _ => new StreamViolationState());

    /// <summary>
    /// Per-stream violation tracking state.
    /// </summary>
    private sealed class StreamViolationState
    {
        public int PatViolations;
        public int PcrViolations;
        public int ContinuityViolations;
        public int JitterViolations;
        public int DriftViolations;
        public int ConsecutiveFailures;
        public DateTime StreamStartTime = DateTime.UtcNow;
        public DateTime LastSwitchAttempt = DateTime.MinValue;
    }
}
