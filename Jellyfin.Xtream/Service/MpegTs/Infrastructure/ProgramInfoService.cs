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
using Jellyfin.Xtream.Service.MpegTs.Core;
using Jellyfin.Xtream.Service.MpegTs.Models;
using Jellyfin.Xtream.Service.MpegTs.UseCases;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.MpegTs.Infrastructure;

/// <summary>
/// Manages timing services (PCR, timestamps) for programs.
/// Keeps ProgramInfo as a pure DTO by owning service instances externally.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ProgramInfoService"/> class.
/// </remarks>
/// <param name="logger">Optional logger for diagnostics.</param>
public sealed class ProgramInfoService(ILogger? logger = null) : IProgramInfoService
{
    private readonly ConcurrentDictionary<int, PcrTimingTracker> _pcrTimingServices = new();
    private readonly ConcurrentDictionary<int, TimestampTracker> _timestampTrackers = new();
    private readonly ILogger? _logger = logger;

    /// <summary>
    /// Event raised when a PCR jitter violation is detected in any program.
    /// </summary>
    public event EventHandler<StreamQualityViolationEventArgs>? JitterViolationDetected;

    /// <summary>
    /// Gets or creates the PCR timing service for a program.
    /// </summary>
    /// <param name="programNumber">The program number.</param>
    /// <returns>The PCR timing service instance.</returns>
    public PcrTimingTracker GetOrCreatePcrTimingTracker(int programNumber)
    {
        return _pcrTimingServices.GetOrAdd(
            programNumber,
            pn =>
            {
                var service = new PcrTimingTracker(pn, _logger);
                service.JitterViolationDetected += OnJitterViolationDetected;
                return service;
            }
        );
    }

    private void OnJitterViolationDetected(object? sender, StreamQualityViolationEventArgs e) =>
        JitterViolationDetected?.Invoke(this, e);

    /// <summary>
    /// Gets or creates the timestamp tracker for a program.
    /// </summary>
    /// <param name="programNumber">The program number.</param>
    /// <returns>The timestamp tracker instance.</returns>
    public TimestampTracker GetOrCreateTimestampTracker(int programNumber) =>
        _timestampTrackers.GetOrAdd(programNumber, _ => new TimestampTracker());

    /// <summary>
    /// Gets the clock status for a program.
    /// </summary>
    /// <param name="programNumber">The program number.</param>
    /// <returns>The clock status, or Initializing if no service exists.</returns>
    public ClockStatus GetClockStatus(int programNumber)
    {
        return _pcrTimingServices.TryGetValue(programNumber, out var service)
            ? service.ClockStatus
            : ClockStatus.Initializing;
    }

    /// <summary>
    /// Gets the clock drift in PPM for a program.
    /// </summary>
    /// <param name="programNumber">The program number.</param>
    /// <returns>The drift in PPM, or 0 if no service exists.</returns>
    public double GetClockDriftPpm(int programNumber) =>
        _pcrTimingServices.TryGetValue(programNumber, out var service) ? service.AccumulatedDriftPpm : 0;

    /// <summary>
    /// Gets the sync status for a program.
    /// </summary>
    /// <param name="programNumber">The program number.</param>
    /// <param name="hasVideo">Whether the program has video.</param>
    /// <param name="hasAudio">Whether the program has audio.</param>
    /// <returns>The sync status.</returns>
    public SyncStatus GetSyncStatus(int programNumber, bool hasVideo, bool hasAudio)
    {
        return !hasVideo ? SyncStatus.NoVideo
            : !hasAudio ? SyncStatus.NoAudio
            : _timestampTrackers.TryGetValue(programNumber, out var tracker) ? tracker.Status
            : SyncStatus.Unknown;
    }

    /// <summary>
    /// Gets the current A/V drift in milliseconds for a program.
    /// </summary>
    /// <param name="programNumber">The program number.</param>
    /// <returns>The drift in milliseconds, or 0 if no tracker exists.</returns>
    public double GetCurrentDriftMs(int programNumber) =>
        _timestampTrackers.TryGetValue(programNumber, out var tracker) ? tracker.CurrentDriftMs : 0;

    /// <summary>
    /// Resets timing state for all programs.
    /// Used when reconnecting to prevent false drift readings from stale timestamps.
    /// </summary>
    public void ResetTimingState()
    {
        foreach (var service in _pcrTimingServices.Values)
        {
            service.Reset();
        }

        foreach (var tracker in _timestampTrackers.Values)
        {
            tracker.Reset();
        }
    }

    /// <summary>
    /// Resets timing state for a specific program.
    /// </summary>
    /// <param name="programNumber">The program number.</param>
    public void ResetTimingState(int programNumber)
    {
        if (_pcrTimingServices.TryGetValue(programNumber, out var pcrService))
        {
            pcrService.Reset();
        }

        if (_timestampTrackers.TryGetValue(programNumber, out var tracker))
        {
            tracker.Reset();
        }
    }

    /// <summary>
    /// Removes all services for a specific program.
    /// </summary>
    /// <param name="programNumber">The program number.</param>
    public void RemoveProgram(int programNumber)
    {
        _ = _pcrTimingServices.TryRemove(programNumber, out _);
        _ = _timestampTrackers.TryRemove(programNumber, out _);
    }

    /// <summary>
    /// Clears all services.
    /// </summary>
    public void Clear()
    {
        _pcrTimingServices.Clear();
        _timestampTrackers.Clear();
    }
}
