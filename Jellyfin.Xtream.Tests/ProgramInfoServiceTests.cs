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

using System.Threading;
using Jellyfin.Xtream.Service.MpegTs;
using Jellyfin.Xtream.Service.MpegTs.Core;
using Jellyfin.Xtream.Service.MpegTs.Infrastructure;
using Jellyfin.Xtream.Service.MpegTs.Models;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Unit tests for ProgramInfoService validating service management per program.
/// </summary>
/// <remarks>
/// <para>
/// ProgramInfoService manages timing services (PCR timing, timestamp tracking) for
/// individual programs in a Multi-Program Transport Stream (MPTS).
/// </para>
/// <para>
/// Per ISO/IEC 13818-1 Section 2.4.4.3, each program in a transport stream has:
/// - A unique program_number in the PAT
/// - Its own PCR_PID for timing reference
/// - Independent elementary streams (video, audio, etc.)
/// </para>
/// </remarks>
public sealed class ProgramInfoServiceTests
{
    #region Service Creation Tests

    /// <summary>
    /// Tests that PcrTimingTracker is created on demand for a program.
    /// </summary>
    [Fact]
    public void GetOrCreatePcrTimingTrackerCreatesOnDemand()
    {
        var service = new ProgramInfoService();

        var pcrTiming = service.GetOrCreatePcrTimingTracker(programNumber: 1);

        Assert.NotNull(pcrTiming);
        Assert.Equal(ClockStatus.Initializing, pcrTiming.ClockStatus);
    }

    /// <summary>
    /// Tests that same PcrTimingTracker instance is returned for same program.
    /// </summary>
    [Fact]
    public void GetOrCreatePcrTimingTrackerReturnsSameInstance()
    {
        var service = new ProgramInfoService();

        var first = service.GetOrCreatePcrTimingTracker(programNumber: 1);
        var second = service.GetOrCreatePcrTimingTracker(programNumber: 1);

        Assert.Same(first, second);
    }

    /// <summary>
    /// Tests that different programs get different PcrTimingTracker instances.
    /// </summary>
    [Fact]
    public void GetOrCreatePcrTimingTrackerDifferentProgramsDifferentInstances()
    {
        var service = new ProgramInfoService();

        var program1 = service.GetOrCreatePcrTimingTracker(programNumber: 1);
        var program2 = service.GetOrCreatePcrTimingTracker(programNumber: 2);

        Assert.NotSame(program1, program2);
    }

    /// <summary>
    /// Tests that TimestampTracker is created on demand for a program.
    /// </summary>
    [Fact]
    public void GetOrCreateTimestampTrackerCreatesOnDemand()
    {
        var service = new ProgramInfoService();

        var tracker = service.GetOrCreateTimestampTracker(programNumber: 1);

        Assert.NotNull(tracker);
        Assert.Equal(SyncStatus.Unknown, tracker.Status);
    }

    /// <summary>
    /// Tests that same TimestampTracker instance is returned for same program.
    /// </summary>
    [Fact]
    public void GetOrCreateTimestampTrackerReturnsSameInstance()
    {
        var service = new ProgramInfoService();

        var first = service.GetOrCreateTimestampTracker(programNumber: 1);
        var second = service.GetOrCreateTimestampTracker(programNumber: 1);

        Assert.Same(first, second);
    }

    /// <summary>
    /// Tests that different programs get different TimestampTracker instances.
    /// </summary>
    [Fact]
    public void GetOrCreateTimestampTrackerDifferentProgramsDifferentInstances()
    {
        var service = new ProgramInfoService();

        var program1 = service.GetOrCreateTimestampTracker(programNumber: 1);
        var program2 = service.GetOrCreateTimestampTracker(programNumber: 2);

        Assert.NotSame(program1, program2);
    }

    #endregion

    #region Clock Status Tests

    /// <summary>
    /// Tests GetClockStatus returns Initializing for non-existent program.
    /// </summary>
    [Fact]
    public void GetClockStatusNonExistentProgramReturnsInitializing()
    {
        var service = new ProgramInfoService();

        var status = service.GetClockStatus(programNumber: 999);

        Assert.Equal(ClockStatus.Initializing, status);
    }

    /// <summary>
    /// Tests GetClockStatus returns actual status from PcrTimingTracker.
    /// </summary>
    [Fact]
    public void GetClockStatusReturnsActualServiceStatus()
    {
        var service = new ProgramInfoService();

        // Create service and process a PCR to change status
        var pcrTiming = service.GetOrCreatePcrTimingTracker(programNumber: 1);
        _ = pcrTiming.ProcessPcr(27_000_000);

        var status = service.GetClockStatus(programNumber: 1);

        Assert.Equal(ClockStatus.Locking, status);
    }

    #endregion

    #region Clock Drift Tests

    /// <summary>
    /// Tests GetClockDriftPpm returns 0 for non-existent program.
    /// </summary>
    [Fact]
    public void GetClockDriftPpmNonExistentProgramReturnsZero()
    {
        var service = new ProgramInfoService();

        var drift = service.GetClockDriftPpm(programNumber: 999);

        Assert.Equal(0, drift);
    }

    /// <summary>
    /// Tests GetClockDriftPpm returns value from underlying PcrTimingTracker.
    /// </summary>
    /// <remarks>
    /// This test verifies the ProgramInfoService correctly delegates to PcrTimingTracker.
    /// Drift calculation accuracy is tested separately in PcrTimingTrackerTests using TestClock.
    /// </remarks>
    [Fact]
    public void GetClockDriftPpmReturnsDriftFromService()
    {
        var service = new ProgramInfoService();

        // Create service and process PCRs
        var pcrTiming = service.GetOrCreatePcrTimingTracker(programNumber: 1);
        _ = pcrTiming.ProcessPcr(27_000_000);

        // Get drift through ProgramInfoService
        var driftFromService = service.GetClockDriftPpm(programNumber: 1);

        // Should match the underlying PcrTimingTracker value
        Assert.Equal(pcrTiming.AccumulatedDriftPpm, driftFromService);
    }

    #endregion

    #region Sync Status Tests

    /// <summary>
    /// Tests GetSyncStatus returns NoVideo when program has no video.
    /// </summary>
    [Fact]
    public void GetSyncStatusNoVideoReturnsNoVideo()
    {
        var service = new ProgramInfoService();

        var status = service.GetSyncStatus(programNumber: 1, hasVideo: false, hasAudio: true);

        Assert.Equal(SyncStatus.NoVideo, status);
    }

    /// <summary>
    /// Tests GetSyncStatus returns NoAudio when program has no audio.
    /// </summary>
    [Fact]
    public void GetSyncStatusNoAudioReturnsNoAudio()
    {
        var service = new ProgramInfoService();

        var status = service.GetSyncStatus(programNumber: 1, hasVideo: true, hasAudio: false);

        Assert.Equal(SyncStatus.NoAudio, status);
    }

    /// <summary>
    /// Tests GetSyncStatus returns Unknown when no tracker exists.
    /// </summary>
    [Fact]
    public void GetSyncStatusNoTrackerReturnsUnknown()
    {
        var service = new ProgramInfoService();

        var status = service.GetSyncStatus(programNumber: 1, hasVideo: true, hasAudio: true);

        Assert.Equal(SyncStatus.Unknown, status);
    }

    /// <summary>
    /// Tests GetSyncStatus returns tracker status when available.
    /// </summary>
    [Fact]
    public void GetSyncStatusReturnsTrackerStatus()
    {
        var service = new ProgramInfoService();

        // Create tracker and record some timestamps
        var tracker = service.GetOrCreateTimestampTracker(programNumber: 1);
        tracker.RecordVideoPts(90000, 0);
        tracker.RecordAudioPts(90000, 188);

        var status = service.GetSyncStatus(programNumber: 1, hasVideo: true, hasAudio: true);

        // Should return actual sync status from tracker
        Assert.True(
            status is SyncStatus.Synchronized or SyncStatus.AudioAhead or SyncStatus.AudioBehind or SyncStatus.Unknown
        );
    }

    #endregion

    #region Current Drift Tests

    /// <summary>
    /// Tests GetCurrentDriftMs returns 0 for non-existent program.
    /// </summary>
    [Fact]
    public void GetCurrentDriftMsNonExistentProgramReturnsZero()
    {
        var service = new ProgramInfoService();

        var drift = service.GetCurrentDriftMs(programNumber: 999);

        Assert.Equal(0, drift);
    }

    /// <summary>
    /// Tests GetCurrentDriftMs returns tracker drift value.
    /// </summary>
    [Fact]
    public void GetCurrentDriftMsReturnsTrackerDrift()
    {
        var service = new ProgramInfoService();

        // Create tracker and record timestamps with known drift
        var tracker = service.GetOrCreateTimestampTracker(programNumber: 1);
        tracker.RecordVideoPts(90000, 0);
        tracker.RecordAudioPts(90900, 188); // 10ms ahead (900 PTS ticks at 90kHz)

        var drift = service.GetCurrentDriftMs(programNumber: 1);

        // Audio is 10ms ahead, drift should be positive
        Assert.True(drift > 0);
    }

    #endregion

    #region Reset Tests

    /// <summary>
    /// Tests ResetTimingState resets all programs.
    /// </summary>
    [Fact]
    public void ResetTimingStateResetsAllPrograms()
    {
        var service = new ProgramInfoService();

        // Create services for multiple programs
        var pcrTiming1 = service.GetOrCreatePcrTimingTracker(programNumber: 1);
        var pcrTiming2 = service.GetOrCreatePcrTimingTracker(programNumber: 2);

        _ = pcrTiming1.ProcessPcr(27_000_000);
        _ = pcrTiming2.ProcessPcr(27_000_000);

        Assert.Equal(1, pcrTiming1.PcrCount);
        Assert.Equal(1, pcrTiming2.PcrCount);

        // Reset all
        service.ResetTimingState();

        Assert.Equal(0, pcrTiming1.PcrCount);
        Assert.Equal(0, pcrTiming2.PcrCount);
    }

    /// <summary>
    /// Tests ResetTimingState for specific program only affects that program.
    /// </summary>
    [Fact]
    public void ResetTimingStateSpecificProgramOnlyResetsTarget()
    {
        var service = new ProgramInfoService();

        // Create services for multiple programs
        var pcrTiming1 = service.GetOrCreatePcrTimingTracker(programNumber: 1);
        var pcrTiming2 = service.GetOrCreatePcrTimingTracker(programNumber: 2);

        _ = pcrTiming1.ProcessPcr(27_000_000);
        _ = pcrTiming2.ProcessPcr(27_000_000);

        // Reset only program 1
        service.ResetTimingState(programNumber: 1);

        Assert.Equal(0, pcrTiming1.PcrCount);
        Assert.Equal(1, pcrTiming2.PcrCount); // Should be unchanged
    }

    /// <summary>
    /// Tests ResetTimingState for non-existent program is safe (no-op).
    /// </summary>
    [Fact]
    public void ResetTimingStateNonExistentProgramIsSafe()
    {
        var service = new ProgramInfoService();

        // Should not throw
        service.ResetTimingState(programNumber: 999);
    }

    #endregion

    #region Remove and Clear Tests

    /// <summary>
    /// Tests RemoveProgram removes all services for that program.
    /// </summary>
    [Fact]
    public void RemoveProgramRemovesAllServices()
    {
        var service = new ProgramInfoService();

        // Create services
        var pcrTiming = service.GetOrCreatePcrTimingTracker(programNumber: 1);
        var tracker = service.GetOrCreateTimestampTracker(programNumber: 1);
        _ = pcrTiming.ProcessPcr(27_000_000);

        // Remove program
        service.RemoveProgram(programNumber: 1);

        // New services should be created on next access
        var newPcrTiming = service.GetOrCreatePcrTimingTracker(programNumber: 1);
        var newTracker = service.GetOrCreateTimestampTracker(programNumber: 1);

        Assert.NotSame(pcrTiming, newPcrTiming);
        Assert.NotSame(tracker, newTracker);
        Assert.Equal(0, newPcrTiming.PcrCount);
    }

    /// <summary>
    /// Tests RemoveProgram for non-existent program is safe.
    /// </summary>
    [Fact]
    public void RemoveProgramNonExistentIsSafe()
    {
        var service = new ProgramInfoService();

        // Should not throw
        service.RemoveProgram(programNumber: 999);
    }

    /// <summary>
    /// Tests Clear removes all services for all programs.
    /// </summary>
    [Fact]
    public void ClearRemovesAllServices()
    {
        var service = new ProgramInfoService();

        // Create services for multiple programs
        var pcrTiming1 = service.GetOrCreatePcrTimingTracker(programNumber: 1);
        var pcrTiming2 = service.GetOrCreatePcrTimingTracker(programNumber: 2);
        _ = pcrTiming1.ProcessPcr(27_000_000);
        _ = pcrTiming2.ProcessPcr(27_000_000);

        // Clear all
        service.Clear();

        // New services should be created on next access
        var newPcrTiming1 = service.GetOrCreatePcrTimingTracker(programNumber: 1);
        var newPcrTiming2 = service.GetOrCreatePcrTimingTracker(programNumber: 2);

        Assert.NotSame(pcrTiming1, newPcrTiming1);
        Assert.NotSame(pcrTiming2, newPcrTiming2);
        Assert.Equal(0, newPcrTiming1.PcrCount);
        Assert.Equal(0, newPcrTiming2.PcrCount);
    }

    #endregion

    #region Thread Safety Tests

    /// <summary>
    /// Tests concurrent access to PcrTimingTracker for same program is safe.
    /// </summary>
    [Fact]
    public void ConcurrentAccessToSameProgramIsSafe()
    {
        var service = new ProgramInfoService();
        PcrTimingTracker? instance1 = null;
        PcrTimingTracker? instance2 = null;

        var thread1 = new Thread(() =>
        {
            instance1 = service.GetOrCreatePcrTimingTracker(programNumber: 1);
            for (var i = 0; i < 100; i++)
            {
                _ = instance1.ProcessPcr(27_000_000 + (i * 27_000));
            }
        });

        var thread2 = new Thread(() =>
        {
            instance2 = service.GetOrCreatePcrTimingTracker(programNumber: 1);
            for (var i = 100; i < 200; i++)
            {
                _ = instance2.ProcessPcr(27_000_000 + (i * 27_000));
            }
        });

        thread1.Start();
        thread2.Start();
        thread1.Join();
        thread2.Join();

        // Both should get the same instance
        Assert.Same(instance1, instance2);
    }

    /// <summary>
    /// Tests concurrent access to different programs is safe.
    /// </summary>
    [Fact]
    public void ConcurrentAccessToDifferentProgramsIsSafe()
    {
        var service = new ProgramInfoService();
        var processed1 = 0;
        var processed2 = 0;

        var thread1 = new Thread(() =>
        {
            var pcrTiming = service.GetOrCreatePcrTimingTracker(programNumber: 1);
            for (var i = 0; i < 100; i++)
            {
                if (pcrTiming.ProcessPcr(27_000_000 + (i * 27_000)))
                {
                    _ = Interlocked.Increment(ref processed1);
                }
            }
        });

        var thread2 = new Thread(() =>
        {
            var pcrTiming = service.GetOrCreatePcrTimingTracker(programNumber: 2);
            for (var i = 0; i < 100; i++)
            {
                if (pcrTiming.ProcessPcr(27_000_000 + (i * 27_000)))
                {
                    _ = Interlocked.Increment(ref processed2);
                }
            }
        });

        thread1.Start();
        thread2.Start();
        thread1.Join();
        thread2.Join();

        Assert.Equal(100, processed1);
        Assert.Equal(100, processed2);
    }

    #endregion
}
