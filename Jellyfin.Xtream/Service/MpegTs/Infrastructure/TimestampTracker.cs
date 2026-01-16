// Copyright (C) 2025  Gergo Magyar

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Threading;
using Jellyfin.Xtream.Service.MpegTs.Core;
using Jellyfin.Xtream.Service.MpegTs.Models;
using Jellyfin.Xtream.Service.MpegTs.UseCases;

namespace Jellyfin.Xtream.Service.MpegTs.Infrastructure;

/// <summary>
/// Tracks PTS/DTS timestamps for audio and video streams to detect A/V drift.
/// This is a monitor-only class - it does not modify stream data.
/// Thread-safe for concurrent access from the indexer.
/// </summary>
/// <remarks>
/// <para>
/// A/V sync correction is intentionally NOT performed server-side because:
/// </para>
/// <list type="bullet">
/// <item><description>Players have sophisticated A/V sync algorithms that work best with original timestamps</description></item>
/// <item><description>Modifying audio PTS without video/PCR creates timing inconsistency</description></item>
/// <item><description>We cannot distinguish encoder drift from network jitter</description></item>
/// </list>
/// <para>Drift thresholds based on human perception research:</para>
/// <list type="bullet">
/// <item><description>±20ms: Professional broadcast target (EBU R37)</description></item>
/// <item><description>±45ms: Human lip-sync detection threshold</description></item>
/// <item><description>±100ms: Clearly noticeable to most viewers</description></item>
/// </list>
/// </remarks>
public sealed class TimestampTracker
{
    private const int MaxSamples = 50;

    /// <summary>
    /// Drift threshold for status reporting.
    /// Set to 20ms (professional broadcast standard) for proactive detection.
    /// Human lip-sync detection starts at ~45ms, so 20ms gives us headroom.
    /// </summary>
    private const double DriftThresholdMs = 20.0;

    /// <summary>
    /// Severe drift threshold requiring attention.
    /// At 100ms, most viewers will notice lip-sync issues.
    /// </summary>
    private const double SevereDriftThresholdMs = 100.0;

    /// <summary>
    /// PTS discontinuity threshold in 90kHz units.
    /// 1 second = 90,000 units. A jump > 1 second forward or any backward jump > 100ms
    /// indicates a potential discontinuity in the source stream.
    /// </summary>
    private const long PtsDiscontinuityThreshold90Khz = 90000; // 1 second forward

    /// <summary>
    /// Backward PTS jump threshold in 90kHz units.
    /// Set to 500ms (45,000 units) to avoid false positives from:
    /// - B-frame reordering (typically &lt;100ms)
    /// - Provider encoder quirks (some streams have systematic 120-160ms backward jumps)
    /// - Network jitter causing out-of-order packets
    /// Only trigger on substantial backward jumps that would cause visible video looping.
    /// </summary>
    private const long PtsBackwardThreshold90Khz = 45000; // 500ms

    /// <summary>
    /// Update sync status every 4 samples for faster response.
    /// </summary>
    private const int UpdateIntervalSamples = 4;

    private readonly RingBuffer<StreamTimestamp> _videoTimestamps = new(MaxSamples);
    private readonly RingBuffer<StreamTimestamp> _audioTimestamps = new(MaxSamples);
    private readonly RingBuffer<double> _driftSamples = new(MaxSamples);

    // Lock-free timestamp storage using separate atomic fields.
    // We use Interlocked operations on individual longs for thread-safe access.
    // Reading both fields atomically as a pair is not required because:
    // 1. The offset gap check handles stale reads gracefully (returns 0 drift)
    // 2. Worst case: we see mismatched Value/Offset temporarily, but the gap check
    //    will detect this as an invalid comparison and return 0
    private long _lastVideoPtsValue;
    private long _lastVideoPtsOffset;
    private long _lastAudioPtsValue;
    private long _lastAudioPtsOffset;
    private long _videoSampleCount;
    private long _audioSampleCount;
    private long _driftViolationCount;
    private long _ptsDiscontinuityCount;
    private long _previousVideoPts; // For discontinuity detection

    /// <summary>
    /// Maximum offset gap between video and audio PTS for valid drift calculation.
    /// If the gap exceeds this, the PTS values are likely from different stream segments
    /// (e.g., before/after a reconnection) and should not be compared.
    /// </summary>
    /// <remarks>
    /// The 2MB threshold is derived from typical IPTV stream characteristics:
    /// <list type="bullet">
    /// <item><description>At 16 Mbps (HD stream), 2MB ≈ 1 second of data</description></item>
    /// <item><description>Audio and video PTS within the same GOP should be within ~2-5 seconds</description></item>
    /// <item><description>After HTTP reconnection, old PTS from pre-disconnect may have offsets
    /// differing by gigabytes from new stream data</description></item>
    /// <item><description>This threshold catches reconnection scenarios while allowing normal
    /// interleaving delays between audio/video PIDs</description></item>
    /// </list>
    /// </remarks>
    private const long MaxOffsetGapForValidDrift = 2 * 1024 * 1024; // 2MB ≈ 1 second at 16 Mbps

    /// <summary>
    /// Event raised when A/V drift exceeds acceptable threshold.
    /// </summary>
    public event EventHandler<SyncDriftEventArgs>? DriftDetected;

    /// <summary>
    /// Event raised when a PTS discontinuity is detected in the source stream.
    /// This indicates the source stream has a timestamp jump that could cause
    /// playback issues like frame jumping or looping.
    /// </summary>
    public event EventHandler<PtsDiscontinuityEventArgs>? PtsDiscontinuityDetected;

    /// <summary>
    /// Gets the current synchronization status.
    /// </summary>
    public SyncStatus Status { get; private set; } = SyncStatus.Unknown;

    /// <summary>
    /// Gets the number of video PTS samples received.
    /// </summary>
    public long VideoSampleCount => Interlocked.Read(ref _videoSampleCount);

    /// <summary>
    /// Gets the number of audio PTS samples received.
    /// </summary>
    public long AudioSampleCount => Interlocked.Read(ref _audioSampleCount);

    /// <summary>
    /// Gets the number of times A/V drift exceeded threshold.
    /// </summary>
    public long DriftViolationCount => Interlocked.Read(ref _driftViolationCount);

    /// <summary>
    /// Gets the number of PTS discontinuities detected in the source stream.
    /// </summary>
    public long PtsDiscontinuityCount => Interlocked.Read(ref _ptsDiscontinuityCount);

    /// <summary>
    /// Gets the peak drift observed in milliseconds.
    /// </summary>
    public double PeakDriftMs { get; private set; }

    /// <summary>
    /// Gets the most recent video PTS.
    /// </summary>
    public StreamTimestamp LastVideoPts =>
        new(Interlocked.Read(ref _lastVideoPtsValue), Interlocked.Read(ref _lastVideoPtsOffset));

    /// <summary>
    /// Gets the most recent audio PTS.
    /// </summary>
    public StreamTimestamp LastAudioPts =>
        new(Interlocked.Read(ref _lastAudioPtsValue), Interlocked.Read(ref _lastAudioPtsOffset));

    /// <summary>
    /// Gets the current A/V drift in milliseconds.
    /// Positive = audio ahead, negative = audio behind.
    /// Returns 0 if timestamps are from different stream segments (after reconnection).
    /// </summary>
    public double CurrentDriftMs
    {
        get
        {
            // Read all fields atomically (individual reads are atomic for longs on 64-bit)
            var videoValue = Interlocked.Read(ref _lastVideoPtsValue);
            var videoOffset = Interlocked.Read(ref _lastVideoPtsOffset);
            var audioValue = Interlocked.Read(ref _lastAudioPtsValue);
            var audioOffset = Interlocked.Read(ref _lastAudioPtsOffset);

            if (videoValue == 0 || audioValue == 0)
            {
                return 0;
            }

            // After a reset/reconnection, ensure both PTS values are from the same segment
            // by checking that their stream offsets are within a reasonable range.
            // This prevents comparing old video PTS with new audio PTS (or vice versa).
            var offsetGap = Math.Abs(videoOffset - audioOffset);
            if (offsetGap > MaxOffsetGapForValidDrift)
            {
                return 0;
            }

            var videoPts = new StreamTimestamp(videoValue, videoOffset);
            var audioPts = new StreamTimestamp(audioValue, audioOffset);
            return audioPts.DifferenceInMsFrom(videoPts);
        }
    }

    /// <summary>
    /// Records a video PTS sample.
    /// </summary>
    /// <param name="pts">The video presentation timestamp.</param>
    /// <param name="offset">The stream offset where this was found.</param>
    public void RecordVideoPts(long pts, long offset)
    {
        // Check for PTS discontinuity before updating state
        var previousPts = Interlocked.Read(ref _previousVideoPts);
        if (previousPts > 0)
        {
            var delta = pts - previousPts;

            // Detect discontinuity: large forward jump (> 1 second) or backward jump (> 100ms)
            // Small backward jumps are normal due to B-frame reordering
            var isForwardDiscontinuity = delta > PtsDiscontinuityThreshold90Khz;
            var isBackwardDiscontinuity = delta < -PtsBackwardThreshold90Khz;

            if (isForwardDiscontinuity || isBackwardDiscontinuity)
            {
                _ = Interlocked.Increment(ref _ptsDiscontinuityCount);

                // Fire event for interested listeners (e.g., timestamp remapping service)
                var deltaMs = delta / 90.0;
                PtsDiscontinuityDetected?.Invoke(
                    this,
                    new PtsDiscontinuityEventArgs(previousPts, pts, deltaMs, offset, isBackwardDiscontinuity)
                );
            }
        }

        // Update previous PTS for next comparison
        _ = Interlocked.Exchange(ref _previousVideoPts, pts);

        // Write offset first, then value. Readers check value != 0 first,
        // so they'll only use offset after value is set.
        _ = Interlocked.Exchange(ref _lastVideoPtsOffset, offset);
        _ = Interlocked.Exchange(ref _lastVideoPtsValue, pts);

        var timestamp = new StreamTimestamp(pts, offset);
        var count = Interlocked.Increment(ref _videoSampleCount);
        _videoTimestamps.Add(timestamp);

        if ((count & (UpdateIntervalSamples - 1)) == 0)
        {
            UpdateSyncStatus();
        }
    }

    /// <summary>
    /// Records an audio PTS sample.
    /// </summary>
    /// <param name="pts">The audio presentation timestamp.</param>
    /// <param name="offset">The stream offset where this was found.</param>
    public void RecordAudioPts(long pts, long offset)
    {
        // Write offset first, then value. Readers check value != 0 first,
        // so they'll only use offset after value is set.
        _ = Interlocked.Exchange(ref _lastAudioPtsOffset, offset);
        _ = Interlocked.Exchange(ref _lastAudioPtsValue, pts);

        var timestamp = new StreamTimestamp(pts, offset);
        var count = Interlocked.Increment(ref _audioSampleCount);
        _audioTimestamps.Add(timestamp);

        if ((count & (UpdateIntervalSamples - 1)) == 0)
        {
            UpdateSyncStatus();
        }
    }

    /// <summary>
    /// Gets the average drift over recent samples.
    /// </summary>
    /// <returns>The average drift in milliseconds.</returns>
    public double GetAverageDriftMs()
    {
        var count = _driftSamples.Count;
        if (count == 0)
        {
            return 0;
        }

        double sum = 0;
        for (var i = 0; i < count; i++)
        {
            sum += _driftSamples[i];
        }

        return sum / count;
    }

    /// <summary>
    /// Finds the best sync point within the given offset range.
    /// Uses indexed access for optimal cache locality and branch prediction.
    /// </summary>
    /// <param name="minOffset">Minimum acceptable offset.</param>
    /// <param name="maxOffset">Maximum acceptable offset.</param>
    /// <returns>A sync point if found, or null.</returns>
    public SyncPoint? FindBestSyncPoint(long minOffset, long maxOffset)
    {
        // Use indexed access for better cache locality
        var videoCount = _videoTimestamps.Count;
        StreamTimestamp bestVideo = default;
        var hasVideo = false;

        for (var i = 0; i < videoCount; i++)
        {
            var video = _videoTimestamps[i];
            if (video.StreamOffset >= minOffset && video.StreamOffset <= maxOffset)
            {
                if (!hasVideo || video.StreamOffset < bestVideo.StreamOffset)
                {
                    bestVideo = video;
                    hasVideo = true;
                }
            }
        }

        if (!hasVideo)
        {
            return null;
        }

        var audioCount = _audioTimestamps.Count;
        StreamTimestamp bestAudio = default;
        var bestDrift = double.MaxValue;
        var hasAudio = false;

        for (var i = 0; i < audioCount; i++)
        {
            var audio = _audioTimestamps[i];
            if (audio.StreamOffset < minOffset || audio.StreamOffset > maxOffset)
            {
                continue;
            }

            var drift = Math.Abs(audio.DifferenceInMsFrom(bestVideo));
            if (drift < bestDrift)
            {
                bestDrift = drift;
                bestAudio = audio;
                hasAudio = true;
            }
        }

        if (hasAudio)
        {
            var offset = Math.Min(bestVideo.StreamOffset, bestAudio.StreamOffset);
            return new SyncPoint(offset, bestVideo, bestAudio, DateTime.UtcNow);
        }

        return null;
    }

    /// <summary>
    /// Resets all tracking state.
    /// </summary>
    public void Reset()
    {
        _videoTimestamps.Clear();
        _audioTimestamps.Clear();
        _driftSamples.Clear();

        // Reset timestamp fields atomically
        _ = Interlocked.Exchange(ref _lastVideoPtsValue, 0);
        _ = Interlocked.Exchange(ref _lastVideoPtsOffset, 0);
        _ = Interlocked.Exchange(ref _lastAudioPtsValue, 0);
        _ = Interlocked.Exchange(ref _lastAudioPtsOffset, 0);

        _ = Interlocked.Exchange(ref _videoSampleCount, 0);
        _ = Interlocked.Exchange(ref _audioSampleCount, 0);
        _ = Interlocked.Exchange(ref _driftViolationCount, 0);
        _ = Interlocked.Exchange(ref _ptsDiscontinuityCount, 0);
        _ = Interlocked.Exchange(ref _previousVideoPts, 0);
        PeakDriftMs = 0;
        Status = SyncStatus.Unknown;
    }

    /// <summary>
    /// Gets diagnostic information about tracking state.
    /// </summary>
    /// <returns>Formatted diagnostic string.</returns>
    public string GetDiagnostics()
    {
        var status = Status.ToString();
        var drift = CurrentDriftMs;
        var avgDrift = GetAverageDriftMs();
        var peak = PeakDriftMs;
        var violations = DriftViolationCount;

        return $"TimestampTracker [{status}]:\n"
            + $"  Current drift: {drift:F2}ms\n"
            + $"  Average drift: {avgDrift:F2}ms\n"
            + $"  Peak drift: {peak:F2}ms\n"
            + $"  Violations: {violations}\n"
            + $"  Video samples: {VideoSampleCount}\n"
            + $"  Audio samples: {AudioSampleCount}";
    }

    private void UpdateSyncStatus()
    {
        if (Interlocked.Read(ref _lastVideoPtsValue) == 0)
        {
            Status = SyncStatus.NoVideo;
            return;
        }

        if (Interlocked.Read(ref _lastAudioPtsValue) == 0)
        {
            Status = SyncStatus.NoAudio;
            return;
        }

        var driftMs = CurrentDriftMs;
        var absDrift = Math.Abs(driftMs);

        _driftSamples.Add(driftMs);

        if (absDrift > PeakDriftMs)
        {
            PeakDriftMs = absDrift;
        }

        if (absDrift <= DriftThresholdMs)
        {
            Status = SyncStatus.Synchronized;
        }
        else if (driftMs > 0)
        {
            Status = SyncStatus.AudioAhead;
            HandleDriftViolation(driftMs);
        }
        else
        {
            Status = SyncStatus.AudioBehind;
            HandleDriftViolation(driftMs);
        }
    }

    private void HandleDriftViolation(double driftMs)
    {
        var count = Interlocked.Increment(ref _driftViolationCount);

        if (count == 1 || count % 100 == 0 || Math.Abs(driftMs) > SevereDriftThresholdMs)
        {
            DriftDetected?.Invoke(this, new SyncDriftEventArgs(driftMs, Status));
        }
    }
}
