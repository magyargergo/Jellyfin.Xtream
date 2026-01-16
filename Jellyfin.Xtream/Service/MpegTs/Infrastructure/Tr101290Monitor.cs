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
using System.Threading;
using Jellyfin.Xtream.Service.MpegTs.Models;
using Jellyfin.Xtream.Service.MpegTs.UseCases;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.MpegTs.Infrastructure;

/// <summary>
/// Monitors TR 101 290 stream quality compliance.
/// Handles PAT interval validation, continuity error tracking, and PCR PID validation.
/// </summary>
/// <remarks>
/// <para>
/// TR 101 290 defines three priority levels of measurements:
/// </para>
/// <list type="bullet">
///   <item><description>Priority 1: TS sync, sync byte, PAT, continuity count, PMT, PID</description></item>
///   <item><description>Priority 2: Transport error, CRC, PCR repetition, PCR discontinuity, PCR accuracy, PTS, CAT</description></item>
///   <item><description>Priority 3: NIT, SI repetition, buffer errors</description></item>
/// </list>
/// </remarks>
/// <remarks>
/// Initializes a new instance of the <see cref="Tr101290Monitor"/> class.
/// </remarks>
/// <param name="logger">Optional logger for diagnostics.</param>
public sealed class Tr101290Monitor(ILogger? logger = null)
{
    /// <summary>
    /// TR 101 290 validation: Check PCR PID validity after this many packets.
    /// </summary>
    public const int PcrValidationThreshold = 5000;

    /// <summary>
    /// Continuity error threshold: raise violation after this many consecutive errors.
    /// </summary>
    private const int ContinuityErrorThreshold = 10;

    private readonly ILogger? _logger = logger;
    private long _lastPatTimeTicks;
    private long _patIntervalViolations;
    private DateTime _lastPatIntervalWarning = DateTime.MinValue;

    // Continuity error tracking for violation events
    private int _consecutiveContinuityErrors;
    private DateTime _lastContinuityViolationWarning = DateTime.MinValue;

    /// <summary>
    /// Event raised when a TR 101 290 stream quality violation is detected.
    /// </summary>
    public event EventHandler<StreamQualityViolationEventArgs>? StreamQualityViolation;

    /// <summary>
    /// Gets the number of PAT interval violations.
    /// </summary>
    public long PatIntervalViolations => Interlocked.Read(ref _patIntervalViolations);

    /// <summary>
    /// Gets a value indicating whether PCR PID validation has been completed.
    /// </summary>
    public bool PcrPidValidationDone { get; private set; }

    /// <summary>
    /// Checks if PCR PID validation should run based on packet count.
    /// </summary>
    /// <param name="totalPacketsParsed">The total number of packets parsed.</param>
    /// <returns>True if validation should run now, false otherwise.</returns>
    public bool ShouldValidatePcrPids(long totalPacketsParsed)
    {
        if (!PcrPidValidationDone && totalPacketsParsed == PcrValidationThreshold)
        {
            PcrPidValidationDone = true;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Monitors PAT interval and raises violation if exceeds 500ms (ISO 13818-1).
    /// </summary>
    public void MonitorPatInterval()
    {
        var currentTicks = DateTime.UtcNow.Ticks;
        var lastTicks = Interlocked.Exchange(ref _lastPatTimeTicks, currentTicks);

        if (lastTicks > 0)
        {
            var intervalMs = (currentTicks - lastTicks) / TimeSpan.TicksPerMillisecond;
            if (intervalMs > 500)
            {
                _ = Interlocked.Increment(ref _patIntervalViolations);

                var now = DateTime.UtcNow;
                if ((now - _lastPatIntervalWarning).TotalSeconds >= 30)
                {
                    _lastPatIntervalWarning = now;
                    OnViolationDetected(
                        "PAT Interval Violation",
                        $"PAT interval {intervalMs}ms exceeds ISO 13818-1 limit of 500ms"
                    );
                }
            }
        }
    }

    /// <summary>
    /// Handles a continuity counter error and raises violation if threshold exceeded.
    /// </summary>
    /// <param name="pid">The PID where the error occurred.</param>
    public void HandleContinuityError(int pid)
    {
        var consecutive = Interlocked.Increment(ref _consecutiveContinuityErrors);

        if (consecutive >= ContinuityErrorThreshold)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastContinuityViolationWarning).TotalSeconds >= 30)
            {
                _lastContinuityViolationWarning = now;
                OnViolationDetected(
                    "Continuity Counter Violation",
                    $"PID {pid}: {consecutive} consecutive CC errors (TR 101 290 Priority 2)"
                );

                // Reset after raising event to avoid spamming
                _ = Interlocked.Exchange(ref _consecutiveContinuityErrors, 0);
            }
        }
    }

    /// <summary>
    /// Resets the consecutive continuity error counter.
    /// Called when a valid packet is received.
    /// </summary>
    public void ResetConsecutiveContinuityErrors() => Interlocked.Exchange(ref _consecutiveContinuityErrors, 0);

    /// <summary>
    /// Validates that declared PCR PIDs are actually receiving PCR values.
    /// </summary>
    /// <param name="programNumber">The program number.</param>
    /// <param name="pcrPid">The declared PCR PID.</param>
    /// <param name="pcrPacketsReceived">The number of PCR packets received.</param>
    public void ValidatePcrPid(int programNumber, int pcrPid, long pcrPacketsReceived)
    {
        if (pcrPid == -1)
        {
            return;
        }

        if (pcrPacketsReceived == 0)
        {
            var message = $"Program {programNumber} declares PCR PID {pcrPid} but no PCR values received.";
            _logger?.PluginLogWarning("TR 101 290 VIOLATION: {Message}", message);
            OnViolationDetected("PCR PID Invalid", message);
        }
    }

    /// <summary>
    /// Called when a stream quality violation is detected.
    /// </summary>
    /// <param name="violationType">The type of violation.</param>
    /// <param name="details">Details about the violation.</param>
    private void OnViolationDetected(string violationType, string details) =>
        StreamQualityViolation?.Invoke(this, new StreamQualityViolationEventArgs(violationType, details));

    /// <summary>
    /// Resets the monitor state.
    /// </summary>
    public void Reset()
    {
        PcrPidValidationDone = false;
        _ = Interlocked.Exchange(ref _lastPatTimeTicks, 0);
        _ = Interlocked.Exchange(ref _patIntervalViolations, 0);
        _lastPatIntervalWarning = DateTime.MinValue;
        _ = Interlocked.Exchange(ref _consecutiveContinuityErrors, 0);
        _lastContinuityViolationWarning = DateTime.MinValue;
    }
}
