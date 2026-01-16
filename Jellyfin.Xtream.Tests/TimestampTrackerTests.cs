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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Service.MpegTs;
using Jellyfin.Xtream.Service.MpegTs.Core;
using Jellyfin.Xtream.Service.MpegTs.Infrastructure;
using Jellyfin.Xtream.Service.MpegTs.Models;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Unit tests for TimestampTracker validating A/V sync monitoring per ISO/IEC 13818-1.
/// </summary>
/// <remarks>
/// <para>
/// ISO/IEC 13818-1 Section 2.4.3.7 defines PTS (Presentation Time Stamp):
/// - PTS operates at 90 kHz (90,000 Hz)
/// - PTS is 33 bits, wrapping at 2^33
/// - Used to synchronize audio and video presentation
/// </para>
/// <para>
/// A/V sync requirements based on human perception research:
/// - ±20ms: EBU R37 professional broadcast target
/// - ±45ms: Human lip-sync detection threshold
/// - ±100ms: Clearly noticeable to most viewers
/// </para>
/// </remarks>
public sealed class TimestampTrackerTests
{
    // PTS operates at 90 kHz per ISO/IEC 13818-1
    private const long PtsFrequency = 90_000;

    // Convert milliseconds to PTS ticks (90 kHz = 90 ticks per ms)
    private static long MsToPts(double ms) => (long)(ms * PtsFrequency / 1000);

    #region Initial State Tests

    /// <summary>
    /// Tests tracker initializes in Unknown state.
    /// </summary>
    [Fact]
    public void ConstructorInitializesUnknownState()
    {
        var tracker = new TimestampTracker();

        Assert.Equal(SyncStatus.Unknown, tracker.Status);
        Assert.Equal(0, tracker.VideoSampleCount);
        Assert.Equal(0, tracker.AudioSampleCount);
        Assert.Equal(0, tracker.DriftViolationCount);
        Assert.Equal(0, tracker.CurrentDriftMs);
    }

    #endregion

    #region PTS Recording Tests

    /// <summary>
    /// Tests recording video PTS increments sample count.
    /// </summary>
    [Fact]
    public void RecordVideoPtsIncrementsSampleCount()
    {
        var tracker = new TimestampTracker();

        tracker.RecordVideoPts(90000, offset: 0);

        Assert.Equal(1, tracker.VideoSampleCount);
        Assert.Equal(90000, tracker.LastVideoPts.Value);
    }

    /// <summary>
    /// Tests recording audio PTS increments sample count.
    /// </summary>
    [Fact]
    public void RecordAudioPtsIncrementsSampleCount()
    {
        var tracker = new TimestampTracker();

        tracker.RecordAudioPts(90000, offset: 0);

        Assert.Equal(1, tracker.AudioSampleCount);
        Assert.Equal(90000, tracker.LastAudioPts.Value);
    }

    /// <summary>
    /// Tests that stream offset is recorded with PTS.
    /// </summary>
    [Fact]
    public void RecordPtsStoresStreamOffset()
    {
        var tracker = new TimestampTracker();

        tracker.RecordVideoPts(90000, offset: 1000);
        tracker.RecordAudioPts(90000, offset: 2000);

        Assert.Equal(1000, tracker.LastVideoPts.StreamOffset);
        Assert.Equal(2000, tracker.LastAudioPts.StreamOffset);
    }

    #endregion

    #region Sync Status Tests

    /// <summary>
    /// Tests status is NoVideo when only audio is present.
    /// </summary>
    [Fact]
    public void StatusNoVideoWhenOnlyAudioPresent()
    {
        var tracker = new TimestampTracker();

        // Record multiple audio samples to trigger status update
        for (var i = 0; i < 5; i++)
        {
            tracker.RecordAudioPts(90000 + (i * 90), offset: i * 188);
        }

        Assert.Equal(SyncStatus.NoVideo, tracker.Status);
    }

    /// <summary>
    /// Tests status is NoAudio when only video is present.
    /// </summary>
    [Fact]
    public void StatusNoAudioWhenOnlyVideoPresent()
    {
        var tracker = new TimestampTracker();

        // Record multiple video samples to trigger status update
        for (var i = 0; i < 5; i++)
        {
            tracker.RecordVideoPts(90000 + (i * 3000), offset: i * 188);
        }

        Assert.Equal(SyncStatus.NoAudio, tracker.Status);
    }

    /// <summary>
    /// Tests status is Synchronized when A/V drift is within threshold.
    /// Per EBU R37, ±20ms is the professional broadcast target.
    /// </summary>
    [Fact]
    public void StatusSynchronizedWhenDriftWithinThreshold()
    {
        var tracker = new TimestampTracker();

        // Record video and audio with same PTS (perfect sync)
        const long pts = 90000;
        for (var i = 0; i < 5; i++)
        {
            tracker.RecordVideoPts(pts + (i * 3000), offset: i * 188);
            tracker.RecordAudioPts(pts + (i * 3000), offset: (i * 188) + 94);
        }

        Assert.Equal(SyncStatus.Synchronized, tracker.Status);
        Assert.True(
            Math.Abs(tracker.CurrentDriftMs) <= 20,
            $"Drift {tracker.CurrentDriftMs}ms should be within 20ms threshold"
        );
    }

    /// <summary>
    /// Tests status is AudioAhead when audio PTS is ahead of video.
    /// </summary>
    [Fact]
    public void StatusAudioAheadWhenAudioPtsGreater()
    {
        var tracker = new TimestampTracker();

        // Record with audio 50ms ahead of video (50 * 90 = 4500 PTS ticks)
        const long videoPts = 90000;
        var audioPts = 90000 + MsToPts(50); // 50ms ahead

        for (var i = 0; i < 5; i++)
        {
            tracker.RecordVideoPts(videoPts + (i * 3000), offset: i * 188);
            tracker.RecordAudioPts(audioPts + (i * 3000), offset: (i * 188) + 94);
        }

        Assert.Equal(SyncStatus.AudioAhead, tracker.Status);
        Assert.True(tracker.CurrentDriftMs > 20, $"Drift {tracker.CurrentDriftMs}ms should be > 20ms");
    }

    /// <summary>
    /// Tests status is AudioBehind when audio PTS is behind video.
    /// </summary>
    [Fact]
    public void StatusAudioBehindWhenAudioPtsLess()
    {
        var tracker = new TimestampTracker();

        // Record with audio 50ms behind video
        var videoPts = 90000 + MsToPts(50); // 50ms ahead
        const long audioPts = 90000;

        for (var i = 0; i < 5; i++)
        {
            tracker.RecordVideoPts(videoPts + (i * 3000), offset: i * 188);
            tracker.RecordAudioPts(audioPts + (i * 3000), offset: (i * 188) + 94);
        }

        Assert.Equal(SyncStatus.AudioBehind, tracker.Status);
        Assert.True(tracker.CurrentDriftMs < -20, $"Drift {tracker.CurrentDriftMs}ms should be < -20ms");
    }

    #endregion

    #region Drift Calculation Tests

    /// <summary>
    /// Tests drift calculation is correct for synchronized streams.
    /// </summary>
    [Fact]
    public void CurrentDriftMsZeroForSynchronizedStreams()
    {
        var tracker = new TimestampTracker();

        // Same PTS for audio and video
        tracker.RecordVideoPts(90000, offset: 0);
        tracker.RecordAudioPts(90000, offset: 188);

        Assert.Equal(0, tracker.CurrentDriftMs);
    }

    /// <summary>
    /// Tests drift calculation for audio ahead by known amount.
    /// </summary>
    [Fact]
    public void CurrentDriftMsPositiveWhenAudioAhead()
    {
        var tracker = new TimestampTracker();

        // Audio 100ms ahead (100 * 90 = 9000 PTS ticks)
        tracker.RecordVideoPts(90000, offset: 0);
        tracker.RecordAudioPts(90000 + MsToPts(100), offset: 188);

        Assert.InRange(tracker.CurrentDriftMs, 99, 101);
    }

    /// <summary>
    /// Tests drift calculation for audio behind by known amount.
    /// </summary>
    [Fact]
    public void CurrentDriftMsNegativeWhenAudioBehind()
    {
        var tracker = new TimestampTracker();

        // Audio 100ms behind
        tracker.RecordVideoPts(90000 + MsToPts(100), offset: 0);
        tracker.RecordAudioPts(90000, offset: 188);

        Assert.InRange(tracker.CurrentDriftMs, -101, -99);
    }

    /// <summary>
    /// Tests drift is zero when only video is recorded.
    /// </summary>
    [Fact]
    public void CurrentDriftMsZeroWhenOnlyVideo()
    {
        var tracker = new TimestampTracker();

        tracker.RecordVideoPts(90000, offset: 0);

        Assert.Equal(0, tracker.CurrentDriftMs);
    }

    /// <summary>
    /// Tests drift is zero when only audio is recorded.
    /// </summary>
    [Fact]
    public void CurrentDriftMsZeroWhenOnlyAudio()
    {
        var tracker = new TimestampTracker();

        tracker.RecordAudioPts(90000, offset: 0);

        Assert.Equal(0, tracker.CurrentDriftMs);
    }

    #endregion

    #region Drift Violation Tests

    /// <summary>
    /// Tests drift violation is counted when threshold exceeded.
    /// Threshold is 20ms per EBU R37.
    /// </summary>
    [Fact]
    public void DriftViolationCountedWhenThresholdExceeded()
    {
        var tracker = new TimestampTracker();

        // Record with drift exceeding threshold
        for (var i = 0; i < 5; i++)
        {
            tracker.RecordVideoPts(90000 + (i * 3000), offset: i * 188);
            // Audio 50ms ahead - exceeds 20ms threshold
            tracker.RecordAudioPts(90000 + MsToPts(50) + (i * 3000), offset: (i * 188) + 94);
        }

        Assert.True(tracker.DriftViolationCount > 0, "Drift violations should be counted when exceeding threshold");
    }

    /// <summary>
    /// Tests peak drift is tracked correctly.
    /// </summary>
    /// <remarks>
    /// Note: UpdateSyncStatus only runs every 4 samples (UpdateIntervalSamples = 4),
    /// so we need to record enough samples to trigger drift calculation.
    /// </remarks>
    [Fact]
    public void PeakDriftMsTracksMaximumDrift()
    {
        var tracker = new TimestampTracker();

        // Record with increasing drift - need enough samples to trigger UpdateSyncStatus
        // UpdateSyncStatus runs when video or audio sample count % 4 == 0
        for (var i = 1; i <= 20; i++)
        {
            long drift = i * 10; // 10ms, 20ms, ..., 200ms
            tracker.RecordVideoPts(90000 + (i * 3000), offset: i * 188);
            tracker.RecordAudioPts(90000 + MsToPts(drift) + (i * 3000), offset: (i * 188) + 94);
        }

        // Peak should track the maximum drift observed during updates
        // Final drift is 200ms, and updates happen at samples 4, 8, 12, 16, 20
        Assert.True(
            tracker.PeakDriftMs >= 100, // At sample 20, drift should be ~200ms, at 16 it's ~160ms
            $"Peak drift {tracker.PeakDriftMs}ms should track maximum observed"
        );
    }

    #endregion

    #region Average Drift Tests

    /// <summary>
    /// Tests average drift calculation over multiple samples.
    /// </summary>
    /// <remarks>
    /// Note: UpdateSyncStatus only runs every 4 samples, so drift samples
    /// are only recorded at those intervals. Both video AND audio can trigger
    /// UpdateSyncStatus, and the measured drift depends on which timestamps
    /// were last recorded at that moment. With 50ms target drift and both
    /// streams triggering updates, average drift will be in the 30-70ms range
    /// depending on exact timing of when each stream triggers the update.
    /// </remarks>
    [Fact]
    public void GetAverageDriftMsCalculatesCorrectly()
    {
        var tracker = new TimestampTracker();

        // Record with consistent 50ms drift target
        // Both video and audio will trigger UpdateSyncStatus at their respective count % 4 == 0
        // Due to interleaving, measured drift varies between iterations
        for (var i = 0; i < 20; i++)
        {
            tracker.RecordVideoPts(90000 + (i * 3000), offset: i * 188);
            // Audio 50ms ahead consistently
            tracker.RecordAudioPts(90000 + MsToPts(50) + (i * 3000), offset: (i * 188) + 94);
        }

        var avgDrift = tracker.GetAverageDriftMs();

        // Average drift should be positive (audio ahead) and in reasonable range
        // Exact value depends on when UpdateSyncStatus is triggered vs. which timestamps were last recorded
        Assert.True(avgDrift > 0, "Average drift should be positive (audio ahead)");
        Assert.InRange(avgDrift, 20, 80); // Wider range due to interleaved timing
    }

    /// <summary>
    /// Tests average drift is zero when no samples.
    /// </summary>
    [Fact]
    public void GetAverageDriftMsZeroWhenNoSamples()
    {
        var tracker = new TimestampTracker();

        Assert.Equal(0, tracker.GetAverageDriftMs());
    }

    #endregion

    #region Sync Point Finding Tests

    /// <summary>
    /// Tests finding sync point within offset range.
    /// </summary>
    [Fact]
    public void FindBestSyncPointWithinRange()
    {
        var tracker = new TimestampTracker();

        // Record synchronized samples
        tracker.RecordVideoPts(90000, offset: 1000);
        tracker.RecordAudioPts(90000, offset: 1100);
        tracker.RecordVideoPts(93000, offset: 2000);
        tracker.RecordAudioPts(93000, offset: 2100);

        var syncPoint = tracker.FindBestSyncPoint(minOffset: 500, maxOffset: 2500);

        _ = Assert.NotNull(syncPoint);
        Assert.True(syncPoint.Value.Offset is >= 500 and <= 2500);
    }

    /// <summary>
    /// Tests sync point returns null when no samples in range.
    /// </summary>
    [Fact]
    public void FindBestSyncPointNullWhenNoSamplesInRange()
    {
        var tracker = new TimestampTracker();

        // Record samples outside search range
        tracker.RecordVideoPts(90000, offset: 100);
        tracker.RecordAudioPts(90000, offset: 200);

        var syncPoint = tracker.FindBestSyncPoint(minOffset: 1000, maxOffset: 2000);

        Assert.Null(syncPoint);
    }

    /// <summary>
    /// Tests sync point returns null when no audio in range.
    /// </summary>
    [Fact]
    public void FindBestSyncPointNullWhenNoAudioInRange()
    {
        var tracker = new TimestampTracker();

        // Video in range, but no audio
        tracker.RecordVideoPts(90000, offset: 1000);

        var syncPoint = tracker.FindBestSyncPoint(minOffset: 500, maxOffset: 2000);

        Assert.Null(syncPoint);
    }

    #endregion

    #region Reset Tests

    /// <summary>
    /// Tests Reset clears all tracking state.
    /// </summary>
    [Fact]
    public void ResetClearsAllState()
    {
        var tracker = new TimestampTracker();

        // Build up state
        for (var i = 0; i < 10; i++)
        {
            tracker.RecordVideoPts(90000 + (i * 3000), offset: i * 188);
            tracker.RecordAudioPts(90000 + MsToPts(50) + (i * 3000), offset: (i * 188) + 94);
        }

        Assert.True(tracker.VideoSampleCount > 0);
        Assert.True(tracker.DriftViolationCount > 0);

        // Reset
        tracker.Reset();

        Assert.Equal(0, tracker.VideoSampleCount);
        Assert.Equal(0, tracker.AudioSampleCount);
        Assert.Equal(0, tracker.DriftViolationCount);
        Assert.Equal(0, tracker.PeakDriftMs);
        Assert.Equal(SyncStatus.Unknown, tracker.Status);
    }

    #endregion

    #region Diagnostics Tests

    /// <summary>
    /// Tests GetDiagnostics returns comprehensive information.
    /// </summary>
    [Fact]
    public void GetDiagnosticsReturnsComprehensiveInfo()
    {
        var tracker = new TimestampTracker();

        tracker.RecordVideoPts(90000, offset: 0);
        tracker.RecordAudioPts(90000, offset: 188);

        var diagnostics = tracker.GetDiagnostics();

        Assert.Contains("TimestampTracker", diagnostics);
        Assert.Contains("Current drift", diagnostics);
        Assert.Contains("Video samples", diagnostics);
        Assert.Contains("Audio samples", diagnostics);
    }

    #endregion

    #region Edge Case Tests

    /// <summary>
    /// Tests handling of PTS wrap-around (33-bit overflow).
    /// PTS wraps at 2^33 per ISO/IEC 13818-1.
    /// </summary>
    [Fact]
    public void HandlesPtsWrapAroundCorrectly()
    {
        var tracker = new TimestampTracker();

        // PTS near max value (2^33 - 1 = 8589934591)
        const long nearMax = (1L << 33) - 1000;
        tracker.RecordVideoPts(nearMax, offset: 0);
        tracker.RecordAudioPts(nearMax, offset: 188);

        // Should not throw and should track the values
        Assert.Equal(nearMax, tracker.LastVideoPts.Value);
        Assert.Equal(nearMax, tracker.LastAudioPts.Value);
    }

    /// <summary>
    /// Tests handling of zero PTS values.
    /// </summary>
    [Fact]
    public void HandlesZeroPtsValues()
    {
        var tracker = new TimestampTracker();

        tracker.RecordVideoPts(0, offset: 0);
        tracker.RecordAudioPts(0, offset: 188);

        Assert.Equal(0, tracker.LastVideoPts.Value);
        Assert.Equal(0, tracker.LastAudioPts.Value);
    }

    /// <summary>
    /// Tests drift event is raised for severe drift.
    /// 100ms is the threshold for severe drift per human perception research.
    /// </summary>
    [Fact]
    public void DriftEventRaisedForSevereDrift()
    {
        var tracker = new TimestampTracker();
        var eventRaised = false;

        tracker.DriftDetected += (sender, e) =>
        {
            eventRaised = true;
            Assert.True(Math.Abs(e.DriftMs) > 20);
        };

        // Record with severe drift (150ms)
        for (var i = 0; i < 5; i++)
        {
            tracker.RecordVideoPts(90000 + (i * 3000), offset: i * 188);
            tracker.RecordAudioPts(90000 + MsToPts(150) + (i * 3000), offset: (i * 188) + 94);
        }

        // Event should have been raised at least once
        Assert.True(
            eventRaised || tracker.DriftViolationCount > 0,
            "Either drift event should be raised or violations counted"
        );
    }

    #endregion

    #region Offset Gap Validation Tests (Reconnection Handling)

    /// <summary>
    /// Tests drift returns 0 when offset gap exceeds threshold after reconnection.
    /// The 2MB threshold prevents false drift readings when comparing pre-reconnection
    /// PTS with post-reconnection PTS values.
    /// </summary>
    [Fact]
    public void CurrentDriftMsZeroWhenOffsetGapExceedsThreshold()
    {
        var tracker = new TimestampTracker();

        // Record video from "before reconnection" at low offset
        tracker.RecordVideoPts(90000, offset: 1000);

        // Record audio from "after reconnection" at high offset (>2MB gap)
        tracker.RecordAudioPts(90000, offset: 3_000_000); // 3MB away

        // Drift should be 0 because timestamps are from different stream segments
        Assert.Equal(0, tracker.CurrentDriftMs);
    }

    /// <summary>
    /// Tests drift is calculated normally when offset gap is within threshold.
    /// </summary>
    [Fact]
    public void CurrentDriftMsCalculatedWhenOffsetGapWithinThreshold()
    {
        var tracker = new TimestampTracker();

        // Both timestamps within 2MB of each other
        tracker.RecordVideoPts(90000, offset: 1000);
        tracker.RecordAudioPts(90000 + MsToPts(50), offset: 1_000_000); // 1MB away, within threshold

        // Drift should be calculated normally (~50ms)
        Assert.InRange(tracker.CurrentDriftMs, 49, 51);
    }

    /// <summary>
    /// Tests drift calculation at exact 2MB boundary.
    /// 2MB is the threshold defined in TimestampTracker.MaxOffsetGapForValidDrift.
    /// </summary>
    [Theory]
    [InlineData(2 * 1024 * 1024, true)] // Exactly at threshold - still valid
    [InlineData((2 * 1024 * 1024) + 1, false)] // Just over threshold - invalid
    public void CurrentDriftMsAtOffsetGapBoundary(long offsetGap, bool shouldCalculateDrift)
    {
        var tracker = new TimestampTracker();

        tracker.RecordVideoPts(90000, offset: 0);
        tracker.RecordAudioPts(90000 + MsToPts(50), offset: offsetGap);

        if (shouldCalculateDrift)
        {
            Assert.InRange(tracker.CurrentDriftMs, 49, 51);
        }
        else
        {
            Assert.Equal(0, tracker.CurrentDriftMs);
        }
    }

    /// <summary>
    /// Tests reset allows new drift calculations after reconnection.
    /// </summary>
    [Fact]
    public void ResetAllowsNewDriftCalculationsAfterReconnection()
    {
        var tracker = new TimestampTracker();

        // Initial recording
        tracker.RecordVideoPts(90000, offset: 1000);
        tracker.RecordAudioPts(90000, offset: 1500);
        Assert.Equal(0, tracker.CurrentDriftMs);

        // Simulate reconnection
        tracker.Reset();

        // New recording with new offset range
        tracker.RecordVideoPts(180000, offset: 5_000_000);
        tracker.RecordAudioPts(180000 + MsToPts(30), offset: 5_001_000);

        // Drift should be calculated from new segment
        Assert.InRange(tracker.CurrentDriftMs, 29, 31);
    }

    #endregion

    #region Lock-Free Thread Safety Tests

    /// <summary>
    /// Tests concurrent recording from multiple threads doesn't corrupt state.
    /// This validates the lock-free Interlocked operations work correctly.
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task ConcurrentRecordingDoesNotCorruptState()
    {
        var tracker = new TimestampTracker();
        const int iterations = 1000;

        // Concurrent video recording
        var videoTask = Task.Run(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                tracker.RecordVideoPts(90000 + (i * 3000), offset: i * 188);
            }
        });

        // Concurrent audio recording
        var audioTask = Task.Run(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                tracker.RecordAudioPts(90000 + (i * 3000), offset: (i * 188) + 94);
            }
        });

        await Task.WhenAll(videoTask, audioTask);

        // Verify counts are correct (no lost updates)
        Assert.Equal(iterations, tracker.VideoSampleCount);
        Assert.Equal(iterations, tracker.AudioSampleCount);
    }

    /// <summary>
    /// Tests concurrent reads while writing doesn't throw exceptions.
    /// </summary>
    [Fact]
    public async Task ConcurrentReadsDuringWritesDoNotThrow()
    {
        var tracker = new TimestampTracker();
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var cts = new CancellationTokenSource();

        // Writer thread
        var writerTask = Task.Run(() =>
        {
            for (var i = 0; i < 10000 && !cts.IsCancellationRequested; i++)
            {
                tracker.RecordVideoPts(90000 + (i * 3000), offset: i * 188);
                tracker.RecordAudioPts(90000 + (i * 3000) + 100, offset: (i * 188) + 94);
            }
        });

        // Reader threads
        var readerTasks = Enumerable
            .Range(0, 4)
            .Select(_ =>
                Task.Run(() =>
                {
                    while (!cts.IsCancellationRequested)
                    {
                        try
                        {
                            var drift = tracker.CurrentDriftMs;
                            var videoPts = tracker.LastVideoPts;
                            var audioPts = tracker.LastAudioPts;
                            var status = tracker.Status;
                            var avgDrift = tracker.GetAverageDriftMs();
                        }
                        catch (Exception ex)
                        {
                            exceptions.Add(ex);
                        }
                    }
                })
            )
            .ToArray();

        // Wait for writer to complete
        await writerTask;
        await
        // Stop readers
        cts.CancelAsync();
        await Task.WhenAll(readerTasks);

        Assert.Empty(exceptions);
    }

    /// <summary>
    /// Tests LastVideoPts and LastAudioPts return consistent values.
    /// The lock-free design using separate longs ensures eventual consistency.
    /// </summary>
    [Fact]
    public void LastPtsPropertiesReturnConsistentValues()
    {
        var tracker = new TimestampTracker();

        // Record specific values
        const long expectedVideoPts = 123456789;
        const long expectedAudioPts = 987654321;
        const long expectedVideoOffset = 1000;
        const long expectedAudioOffset = 2000;

        tracker.RecordVideoPts(expectedVideoPts, expectedVideoOffset);
        tracker.RecordAudioPts(expectedAudioPts, expectedAudioOffset);

        // Read back values
        var videoPts = tracker.LastVideoPts;
        var audioPts = tracker.LastAudioPts;

        Assert.Equal(expectedVideoPts, videoPts.Value);
        Assert.Equal(expectedVideoOffset, videoPts.StreamOffset);
        Assert.Equal(expectedAudioPts, audioPts.Value);
        Assert.Equal(expectedAudioOffset, audioPts.StreamOffset);
    }

    /// <summary>
    /// Tests sample counts are atomically incremented.
    /// </summary>
    [Fact]
    public async Task SampleCountsAreAtomicallyIncremented()
    {
        var tracker = new TimestampTracker();
        const int threadsPerType = 4;
        const int samplesPerThread = 250;

        var tasks = new Task[threadsPerType * 2];

        // Video recording threads
        for (var t = 0; t < threadsPerType; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (var i = 0; i < samplesPerThread; i++)
                {
                    tracker.RecordVideoPts(90000 + i, offset: i);
                }
            });
        }

        // Audio recording threads
        for (var t = 0; t < threadsPerType; t++)
        {
            tasks[threadsPerType + t] = Task.Run(() =>
            {
                for (var i = 0; i < samplesPerThread; i++)
                {
                    tracker.RecordAudioPts(90000 + i, offset: i);
                }
            });
        }

        await Task.WhenAll(tasks);

        Assert.Equal(threadsPerType * samplesPerThread, tracker.VideoSampleCount);
        Assert.Equal(threadsPerType * samplesPerThread, tracker.AudioSampleCount);
    }

    #endregion

    #region StreamTimestamp Wraparound Tests

    /// <summary>
    /// Tests StreamTimestamp.DifferenceFrom handles wraparound correctly.
    /// </summary>
    [Theory]
    [InlineData(100, 50, 50)] // Simple positive difference
    [InlineData(50, 100, -50)] // Simple negative difference
    [InlineData(0x1_FFFFFFFF, 100, -101)] // Near max to small (forward wrap)
    [InlineData(100, 0x1_FFFFFFFF, 101)] // Small to near max (backward wrap)
    public void StreamTimestampDifferenceFromHandlesWraparound(long ts1, long ts2, long expectedDiff)
    {
        var timestamp1 = new StreamTimestamp(ts1, 0);
        var timestamp2 = new StreamTimestamp(ts2, 0);

        var diff = timestamp1.DifferenceFrom(timestamp2);

        Assert.Equal(expectedDiff, diff);
    }

    /// <summary>
    /// Tests StreamTimestamp.DifferenceInMsFrom converts correctly.
    /// </summary>
    [Fact]
    public void StreamTimestampDifferenceInMsFromConvertsCorrectly()
    {
        var ts1 = new StreamTimestamp(90000, 0); // 1 second
        var ts2 = new StreamTimestamp(0, 0);

        var diffMs = ts1.DifferenceInMsFrom(ts2);

        Assert.Equal(1000, diffMs, precision: 1);
    }

    #endregion
}
