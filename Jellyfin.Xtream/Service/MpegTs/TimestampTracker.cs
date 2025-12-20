using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Jellyfin.Xtream.Service.MpegTs;

/// <summary>
/// Tracks PTS/DTS timestamps for audio and video streams to detect A/V drift.
/// Thread-safe for concurrent access from the indexer.
/// </summary>
public sealed class TimestampTracker
{
    private const int MaxSamples = 50;
    private const double DriftThresholdMs = 40.0;
    private const double SevereDriftThresholdMs = 100.0;
    private const int UpdateIntervalSamples = 8;

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
