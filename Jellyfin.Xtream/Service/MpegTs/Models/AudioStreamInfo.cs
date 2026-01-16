using System;
using System.Threading;
using Jellyfin.Xtream.Service.MpegTs.Core;
using Jellyfin.Xtream.Service.MpegTs.Parsing;

namespace Jellyfin.Xtream.Service.MpegTs.Models;

/// <summary>
/// Tracks information about an audio elementary stream within a program.
/// </summary>
public sealed class AudioStreamInfo
{
    private const int MaxFrames = 100;

    private readonly RingBuffer<AudioFrameInfo> _recentFrames = new(MaxFrames);
    private int _frameCount;
    private long _lastPts;
    private long _firstPts;
    private bool _hasFirstPts;

    /// <summary>
    /// Gets or sets the audio PID.
    /// </summary>
    public int Pid { get; set; } = -1;

    /// <summary>
    /// Gets or sets the stream type from PMT.
    /// </summary>
    public int StreamType { get; set; }

    /// <summary>
    /// Gets the audio codec.
    /// </summary>
    public AudioCodec Codec => StreamTypeClassifier.GetAudioCodec(StreamType);

    /// <summary>
    /// Gets the total number of audio frames detected.
    /// </summary>
    public int FrameCount => Interlocked.CompareExchange(ref _frameCount, 0, 0);

    /// <summary>
    /// Gets the most recent audio PTS value.
    /// </summary>
    public long LastPts => Interlocked.Read(ref _lastPts);

    /// <summary>
    /// Gets the first audio PTS value detected.
    /// </summary>
    public long FirstPts => _hasFirstPts ? _firstPts : 0;

    /// <summary>
    /// Gets a value indicating whether audio has been detected.
    /// </summary>
    public bool HasAudio => Pid != -1;

    /// <summary>
    /// Records an audio frame at the given offset with PTS.
    /// </summary>
    /// <param name="offset">The absolute stream offset.</param>
    /// <param name="pts">The presentation timestamp.</param>
    public void RecordFrame(long offset, long pts)
    {
        if (!_hasFirstPts)
        {
            _firstPts = pts;
            _hasFirstPts = true;
        }

        _ = Interlocked.Exchange(ref _lastPts, pts);
        _ = Interlocked.Increment(ref _frameCount);

        var timestamp = new StreamTimestamp(pts, offset);
        var frameInfo = new AudioFrameInfo(offset, timestamp, Codec);
        _recentFrames.Add(frameInfo);
    }

    /// <summary>
    /// Finds the audio frame closest to the target offset.
    /// Uses indexed access for optimal cache locality.
    /// </summary>
    /// <param name="targetOffset">The target stream offset.</param>
    /// <param name="maxDistance">Maximum acceptable distance in bytes.</param>
    /// <returns>The audio frame info if found, or null.</returns>
    public AudioFrameInfo? FindNearestFrame(long targetOffset, long maxDistance = 1024 * 1024)
    {
        var count = _recentFrames.Count;
        if (count == 0)
        {
            return null;
        }

        AudioFrameInfo best = default;
        var bestDistance = long.MaxValue;
        var hasMatch = false;

        for (var i = 0; i < count; i++)
        {
            var frame = _recentFrames[i];
            var distance = Math.Abs(frame.Offset - targetOffset);
            if (distance < bestDistance && distance <= maxDistance)
            {
                bestDistance = distance;
                best = frame;
                hasMatch = true;
            }
        }

        return hasMatch ? best : null;
    }

    /// <summary>
    /// Resets all audio stream state.
    /// </summary>
    public void Reset()
    {
        _recentFrames.Clear();
        _ = Interlocked.Exchange(ref _frameCount, 0);
        _ = Interlocked.Exchange(ref _lastPts, 0);
        _firstPts = 0;
        _hasFirstPts = false;
    }
}
