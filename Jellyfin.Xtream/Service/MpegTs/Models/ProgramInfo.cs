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
using System.Threading;
using Jellyfin.Xtream.Service.MpegTs.Core;

namespace Jellyfin.Xtream.Service.MpegTs.Models;

/// <summary>
/// Data transfer object representing program metadata and streaming state.
/// Contains only data fields - no service references.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ProgramInfo"/> class.
/// </remarks>
/// <param name="programNumber">The program number from PAT.</param>
/// <param name="pmtPid">The PID of the PMT for this program.</param>
public class ProgramInfo(int programNumber, int pmtPid)
{
    private readonly ConcurrentDictionary<int, int> _continuityCounters = new();
    private readonly ConcurrentDictionary<int, long> _packetLossCount = new();

    private int _keyframeCount;

    // SPS/PPS offset tracking for keyframe alignment
    // When we detect SPS/PPS, we record its offset so keyframes can start from parameter sets
    // Currently used by TsIndexer.AddKeyframe() - call RecordSpsOffset() when detecting SPS NAL units
    // to enable alignment to parameter sets instead of just IDR frames
    private long _lastSpsOffset = -1;
    private long _lastPpsOffset = -1;

    // Cached SPS/PPS NAL units for decoder initialization
    // These are updated whenever we see fresh SPS/PPS in the stream
    private byte[]? _cachedSpsNalUnit;
    private byte[]? _cachedPpsNalUnit;
    private long _lastPmtTimeTicks;
    private long _pmtIntervalViolations;

    /// <summary>
    /// Gets the program number (from PAT).
    /// </summary>
    public int ProgramNumber { get; } = programNumber;

    /// <summary>
    /// Gets the PMT PID for this program.
    /// </summary>
    public int PmtPid { get; } = pmtPid;

    /// <summary>
    /// Gets or sets the video PID for this program (-1 if not detected).
    /// </summary>
    public int VideoPid { get; set; } = -1;

    /// <summary>
    /// Gets or sets the video stream type from PMT (e.g., 0x1B for H.264, 0x24 for HEVC).
    /// </summary>
    public int VideoStreamType { get; set; }

    /// <summary>
    /// Gets or sets the PCR PID for this program (used for timing).
    /// </summary>
    public int PcrPid { get; set; } = -1;

    /// <summary>
    /// Gets or sets the PAT version number (0-31, or 0xFF if not yet received).
    /// </summary>
    public byte PatVersion { get; set; } = 0xFF;

    /// <summary>
    /// Gets or sets the PMT version number (0-31, or 0xFF if not yet received).
    /// </summary>
    public byte PmtVersion { get; set; } = 0xFF;

    /// <summary>
    /// Gets or sets the count of PCR packets received on the declared PCR PID.
    /// </summary>
    public long PcrPacketsReceived { get; set; }

    /// <summary>
    /// Gets or sets the count of video packets processed (for diagnostics).
    /// </summary>
    public long VideoPacketCount { get; set; }

    /// <summary>
    /// Gets the count of PMT interval violations for this program.
    /// </summary>
    public long PmtIntervalViolations => Interlocked.Read(ref _pmtIntervalViolations);

    /// <summary>
    /// Gets the keyframes detected for this program's video stream.
    /// </summary>
    public ConcurrentQueue<KeyframeInfo> Keyframes { get; } = new();

    /// <summary>
    /// Gets or sets the timestamp of the first detected keyframe.
    /// </summary>
    public DateTime FirstKeyframeTime { get; set; } = DateTime.MinValue;

    /// <summary>
    /// Gets or sets the timestamp of the last detected keyframe.
    /// </summary>
    public DateTime LastKeyframeTime { get; set; } = DateTime.MinValue;

    /// <summary>
    /// Gets a value indicating whether this program has a detected video stream.
    /// </summary>
    public bool HasVideo => VideoPid != -1;

    /// <summary>
    /// Gets the audio stream information for this program.
    /// </summary>
    public AudioStreamInfo Audio { get; } = new();

    /// <summary>
    /// Gets a value indicating whether this program has a detected audio stream.
    /// </summary>
    public bool HasAudio => Audio.HasAudio;

    /// <summary>
    /// Gets the average GOP duration for this program.
    /// </summary>
    public TimeSpan AverageGopDuration
    {
        get
        {
            if (FirstKeyframeTime == DateTime.MinValue || LastKeyframeTime == DateTime.MinValue)
            {
                return TimeSpan.Zero;
            }

            var count = GetKeyframeCount();
            if (count < 2)
            {
                return TimeSpan.Zero;
            }

            var totalDuration = LastKeyframeTime - FirstKeyframeTime;
            return TimeSpan.FromTicks(totalDuration.Ticks / (count - 1));
        }
    }

    /// <summary>
    /// Records a PMT reception and checks for interval violations.
    /// </summary>
    /// <param name="currentTicks">Current time in ticks.</param>
    /// <returns>True if interval exceeded 500ms (violation).</returns>
    public bool RecordPmtReception(long currentTicks)
    {
        var lastTicks = Interlocked.Exchange(ref _lastPmtTimeTicks, currentTicks);
        if (lastTicks > 0)
        {
            var intervalMs = (currentTicks - lastTicks) / TimeSpan.TicksPerMillisecond;
            if (intervalMs > 500)
            {
                _ = Interlocked.Increment(ref _pmtIntervalViolations);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the keyframe count.
    /// </summary>
    public int GetKeyframeCount() => Interlocked.CompareExchange(ref _keyframeCount, 0, 0);

    /// <summary>
    /// Increments the keyframe count atomically.
    /// </summary>
    public int IncrementKeyframeCount() => Interlocked.Increment(ref _keyframeCount);

    /// <summary>
    /// Decrements the keyframe count atomically.
    /// </summary>
    public int DecrementKeyframeCount() => Interlocked.Decrement(ref _keyframeCount);

    /// <summary>
    /// Records the offset of an SPS NAL unit for keyframe alignment.
    /// When an IDR is detected, the keyframe can start from the SPS instead.
    /// </summary>
    /// <param name="offset">The byte offset of the SPS NAL unit.</param>
    public void RecordSpsOffset(long offset) => Interlocked.Exchange(ref _lastSpsOffset, offset);

    /// <summary>
    /// Records the offset of a PPS NAL unit for keyframe alignment.
    /// </summary>
    /// <param name="offset">The byte offset of the PPS NAL unit.</param>
    public void RecordPpsOffset(long offset) => Interlocked.Exchange(ref _lastPpsOffset, offset);

    /// <summary>
    /// Gets the last SPS offset without clearing it.
    /// Use this to check if a valid SPS offset exists before recording a keyframe.
    /// </summary>
    /// <returns>The last recorded SPS offset, or -1 if none.</returns>
    public long PeekLastSpsOffset() => Interlocked.Read(ref _lastSpsOffset);

    /// <summary>
    /// Gets the last PPS offset without clearing it.
    /// </summary>
    /// <returns>The last recorded PPS offset, or -1 if none.</returns>
    public long PeekLastPpsOffset() => Interlocked.Read(ref _lastPpsOffset);

    /// <summary>
    /// Gets and clears the last SPS offset for keyframe alignment.
    /// </summary>
    /// <returns>The last recorded SPS offset, or -1 if none.</returns>
    public long ConsumeLastSpsOffset() => Interlocked.Exchange(ref _lastSpsOffset, -1);

    /// <summary>
    /// Gets and clears the last PPS offset for keyframe alignment.
    /// </summary>
    /// <returns>The last recorded PPS offset, or -1 if none.</returns>
    public long ConsumeLastPpsOffset() => Interlocked.Exchange(ref _lastPpsOffset, -1);

    /// <summary>
    /// Caches an SPS NAL unit for decoder initialization.
    /// Called when a fresh SPS is detected in the stream.
    /// </summary>
    /// <param name="spsNalUnit">The complete SPS NAL unit including start code.</param>
    public void CacheSpsNalUnit(byte[] spsNalUnit) => Interlocked.Exchange(ref _cachedSpsNalUnit, spsNalUnit);

    /// <summary>
    /// Caches a PPS NAL unit for decoder initialization.
    /// Called when a fresh PPS is detected in the stream.
    /// </summary>
    /// <param name="ppsNalUnit">The complete PPS NAL unit including start code.</param>
    public void CachePpsNalUnit(byte[] ppsNalUnit) => Interlocked.Exchange(ref _cachedPpsNalUnit, ppsNalUnit);

    /// <summary>
    /// Gets the cached SPS NAL unit, or null if none cached.
    /// </summary>
    public byte[]? GetCachedSpsNalUnit() => Volatile.Read(ref _cachedSpsNalUnit);

    /// <summary>
    /// Gets the cached PPS NAL unit, or null if none cached.
    /// </summary>
    public byte[]? GetCachedPpsNalUnit() => Volatile.Read(ref _cachedPpsNalUnit);

    /// <summary>
    /// Gets whether cached SPS/PPS NAL units are available for decoder initialization.
    /// </summary>
    public bool HasCachedParameterSets =>
        Volatile.Read(ref _cachedSpsNalUnit) != null && Volatile.Read(ref _cachedPpsNalUnit) != null;

    /// <summary>
    /// Gets the combined cached SPS and PPS NAL units as a single byte array.
    /// Returns null if either is missing.
    /// </summary>
    public byte[]? GetCachedParameterSetsAsBytes()
    {
        var sps = Volatile.Read(ref _cachedSpsNalUnit);
        var pps = Volatile.Read(ref _cachedPpsNalUnit);

        if (sps == null || pps == null)
        {
            return null;
        }

        var result = new byte[sps.Length + pps.Length];
        System.Buffer.BlockCopy(sps, 0, result, 0, sps.Length);
        System.Buffer.BlockCopy(pps, 0, result, sps.Length, pps.Length);
        return result;
    }

    /// <summary>
    /// Validates the continuity counter for a PID and detects packet loss.
    /// </summary>
    /// <param name="pid">The packet identifier.</param>
    /// <param name="continuityCounter">The continuity counter (0-15).</param>
    /// <param name="hasPayload">Whether the packet has a payload.</param>
    /// <returns>True if continuity is valid.</returns>
    public bool ValidateContinuityCounter(int pid, int continuityCounter, bool hasPayload)
    {
        if (!_continuityCounters.TryGetValue(pid, out var lastCC))
        {
            _continuityCounters[pid] = continuityCounter;
            return true;
        }

        if (!hasPayload)
        {
            return continuityCounter == lastCC;
        }

        var expectedCC = (lastCC + 1) & 0x0F;

        if (continuityCounter != expectedCC)
        {
            _ = _packetLossCount.AddOrUpdate(pid, 1, (_, count) => count + 1);
            _continuityCounters[pid] = continuityCounter;
            return false;
        }

        _continuityCounters[pid] = continuityCounter;
        return true;
    }

    /// <summary>
    /// Gets the total packet loss count for a specific PID.
    /// </summary>
    public long GetPacketLossCount(int pid) => _packetLossCount.TryGetValue(pid, out var count) ? count : 0;

    /// <summary>
    /// Gets the total packet loss count across all PIDs.
    /// </summary>
    public long GetTotalPacketLoss()
    {
        long total = 0;
        foreach (var count in _packetLossCount.Values)
        {
            total += count;
        }

        return total;
    }

    /// <summary>
    /// Resets continuity counter tracking.
    /// </summary>
    public void ResetContinuityCounters()
    {
        _continuityCounters.Clear();
        _packetLossCount.Clear();
    }

    /// <summary>
    /// Fully resets all program state except program number and PMT PID.
    /// </summary>
    public void Reset()
    {
        ResetContinuityCounters();

        while (Keyframes.TryDequeue(out _)) { }

        _ = Interlocked.Exchange(ref _keyframeCount, 0);
        FirstKeyframeTime = DateTime.MinValue;
        LastKeyframeTime = DateTime.MinValue;

        PatVersion = 0xFF;
        PmtVersion = 0xFF;
        PcrPacketsReceived = 0;

        Audio.Reset();
    }
}
