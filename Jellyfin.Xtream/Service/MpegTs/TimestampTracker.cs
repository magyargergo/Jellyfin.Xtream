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
using System.Runtime.CompilerServices;
using System.Threading;

namespace Jellyfin.Xtream.Service.MpegTs;

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
    /// Update sync status every 4 samples for faster response.
    /// </summary>
    private const int UpdateIntervalSamples = 4;

    private readonly RingBuffer<StreamTimestamp> _videoTimestamps = new(MaxSamples);
    private readonly RingBuffer<StreamTimestamp> _audioTimestamps = new(MaxSamples);
    private readonly RingBuffer<double> _driftSamples = new(MaxSamples);

    private StreamTimestamp _lastVideoPts;
    private StreamTimestamp _lastAudioPts;
    private long _videoSampleCount;
    private long _audioSampleCount;
    private long _driftViolationCount;
    private double _peakDriftMs;
    private SyncStatus _currentStatus = SyncStatus.Unknown;

    /// <summary>
    /// Event raised when A/V drift exceeds acceptable threshold.
    /// </summary>
    public event EventHandler<SyncDriftEventArgs>? DriftDetected;

    /// <summary>
    /// Gets the current synchronization status.
    /// </summary>
    public SyncStatus Status => _currentStatus;

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
    /// Gets the peak drift observed in milliseconds.
    /// </summary>
    public double PeakDriftMs => _peakDriftMs;

    /// <summary>
    /// Gets the most recent video PTS.
    /// </summary>
    public StreamTimestamp LastVideoPts => _lastVideoPts;

    /// <summary>
    /// Gets the most recent audio PTS.
    /// </summary>
    public StreamTimestamp LastAudioPts => _lastAudioPts;

    /// <summary>
    /// Gets the current A/V drift in milliseconds.
    /// Positive = audio ahead, negative = audio behind.
    /// </summary>
    public double CurrentDriftMs
    {
        get
        {
            if (_lastVideoPts.Value == 0 || _lastAudioPts.Value == 0)
            {
                return 0;
            }

            return _lastAudioPts.DifferenceInMsFrom(_lastVideoPts);
        }
    }

    /// <summary>
    /// Records a video PTS sample.
    /// </summary>
    /// <param name="pts">The video presentation timestamp.</param>
    /// <param name="offset">The stream offset where this was found.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordVideoPts(long pts, long offset)
    {
        var timestamp = new StreamTimestamp(pts, offset);
        _lastVideoPts = timestamp;
        long count = Interlocked.Increment(ref _videoSampleCount);

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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordAudioPts(long pts, long offset)
    {
        var timestamp = new StreamTimestamp(pts, offset);
        _lastAudioPts = timestamp;
        long count = Interlocked.Increment(ref _audioSampleCount);

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
        int count = _driftSamples.Count;
        if (count == 0)
        {
            return 0;
        }

        double sum = 0;
        for (int i = 0; i < count; i++)
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
        int videoCount = _videoTimestamps.Count;
        StreamTimestamp bestVideo = default;
        bool hasVideo = false;

        for (int i = 0; i < videoCount; i++)
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

        int audioCount = _audioTimestamps.Count;
        StreamTimestamp bestAudio = default;
        double bestDrift = double.MaxValue;
        bool hasAudio = false;

        for (int i = 0; i < audioCount; i++)
        {
            var audio = _audioTimestamps[i];
            if (audio.StreamOffset < minOffset || audio.StreamOffset > maxOffset)
            {
                continue;
            }

            double drift = Math.Abs(audio.DifferenceInMsFrom(bestVideo));
            if (drift < bestDrift)
            {
                bestDrift = drift;
                bestAudio = audio;
                hasAudio = true;
            }
        }

        if (hasAudio)
        {
            long offset = Math.Min(bestVideo.StreamOffset, bestAudio.StreamOffset);
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

        _lastVideoPts = default;
        _lastAudioPts = default;
        Interlocked.Exchange(ref _videoSampleCount, 0);
        Interlocked.Exchange(ref _audioSampleCount, 0);
        Interlocked.Exchange(ref _driftViolationCount, 0);
        _peakDriftMs = 0;
        _currentStatus = SyncStatus.Unknown;
    }

    /// <summary>
    /// Gets diagnostic information about tracking state.
    /// </summary>
    /// <returns>Formatted diagnostic string.</returns>
    public string GetDiagnostics()
    {
        string status = _currentStatus.ToString();
        double drift = CurrentDriftMs;
        double avgDrift = GetAverageDriftMs();
        double peak = _peakDriftMs;
        long violations = DriftViolationCount;

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
        if (_lastVideoPts.Value == 0)
        {
            _currentStatus = SyncStatus.NoVideo;
            return;
        }

        if (_lastAudioPts.Value == 0)
        {
            _currentStatus = SyncStatus.NoAudio;
            return;
        }

        double driftMs = CurrentDriftMs;
        double absDrift = Math.Abs(driftMs);

        _driftSamples.Add(driftMs);

        if (absDrift > _peakDriftMs)
        {
            _peakDriftMs = absDrift;
        }

        if (absDrift <= DriftThresholdMs)
        {
            _currentStatus = SyncStatus.Synchronized;
        }
        else if (driftMs > 0)
        {
            _currentStatus = SyncStatus.AudioAhead;
            HandleDriftViolation(driftMs);
        }
        else
        {
            _currentStatus = SyncStatus.AudioBehind;
            HandleDriftViolation(driftMs);
        }
    }

    private void HandleDriftViolation(double driftMs)
    {
        long count = Interlocked.Increment(ref _driftViolationCount);

        if (count == 1 || count % 100 == 0 || Math.Abs(driftMs) > SevereDriftThresholdMs)
        {
            DriftDetected?.Invoke(this, new SyncDriftEventArgs(driftMs, _currentStatus));
        }
    }
}
