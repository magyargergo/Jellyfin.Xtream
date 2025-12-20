using System;
using System.Collections.Concurrent;

namespace Jellyfin.Xtream.Service.MpegTs;

/// <summary>
/// Represents a single program in an MPEG-TS stream (SPTS or MPTS).
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ProgramInfo"/> class.
/// </remarks>
/// <param name="programNumber">The program number from PAT.</param>
/// <param name="pmtPid">The PID of the PMT for this program.</param>
public class ProgramInfo(int programNumber, int pmtPid)
{
    /// <summary>
    /// Per-PID continuity counter tracking for packet loss detection (ISO/IEC 13818-1).
    /// Maps PID → last seen continuity counter (0-15).
    /// </summary>
    private readonly ConcurrentDictionary<int, int> _continuityCounters = new();

    /// <summary>
    /// Tracks packet loss statistics per PID.
    /// Maps PID → number of discontinuities detected.
    /// </summary>
    private readonly ConcurrentDictionary<int, long> _packetLossCount = new();

    /// <summary>
    /// PCR jitter buffer for clock recovery and smooth playout.
    /// </summary>
    private PcrJitterBuffer? _pcrJitterBuffer;

    /// <summary>
    /// Timestamp tracker for A/V synchronization monitoring.
    /// </summary>
    private TimestampTracker? _timestampTracker;

    private int _keyframeCount;

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
    /// Gets or sets the PCR PID for this program (used for timing).
    /// </summary>
    public int PcrPid { get; set; } = -1;

    /// <summary>
    /// Gets or sets the PAT version number (0-31, or 0xFF if not yet received).
    /// TR 101 290 Priority 2: Version changes indicate program structure updates.
    /// </summary>
    public byte PatVersion { get; set; } = 0xFF;

    /// <summary>
    /// Gets or sets the PMT version number (0-31, or 0xFF if not yet received).
    /// TR 101 290 Priority 2: Version changes indicate stream configuration updates.
    /// </summary>
    public byte PmtVersion { get; set; } = 0xFF;

    /// <summary>
    /// Gets or sets the count of PCR packets received on the declared PCR PID.
    /// Used for TR 101 290 Priority 1 validation that PCR PID actually carries PCR values.
    /// </summary>
    public long PcrPacketsReceived { get; set; }

    /// <summary>
    /// Last PMT reception time in ticks for interval monitoring.
    /// ISO 13818-1 requires PMT interval ≤500ms.
    /// </summary>
    private long _lastPmtTimeTicks;

    /// <summary>
    /// Count of PMT interval violations for this program.
    /// </summary>
    private long _pmtIntervalViolations;

    /// <summary>
    /// Gets the count of PMT interval violations for this program.
    /// </summary>
    public long PmtIntervalViolations => System.Threading.Interlocked.Read(ref _pmtIntervalViolations);

    /// <summary>
    /// Records a PMT reception and checks for interval violations.
    /// </summary>
    /// <param name="currentTicks">Current time in ticks.</param>
    /// <returns>True if interval exceeded 500ms (violation), false otherwise.</returns>
    public bool RecordPmtReception(long currentTicks)
    {
        long lastTicks = System.Threading.Interlocked.Exchange(ref _lastPmtTimeTicks, currentTicks);
        if (lastTicks > 0)
        {
            long intervalMs = (currentTicks - lastTicks) / TimeSpan.TicksPerMillisecond;
            if (intervalMs > 500)
            {
                System.Threading.Interlocked.Increment(ref _pmtIntervalViolations);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the keyframes detected for this program's video stream.
    /// </summary>
    public ConcurrentQueue<KeyframeInfo> Keyframes { get; } = new ConcurrentQueue<KeyframeInfo>();

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
    public AudioStreamInfo Audio { get; } = new AudioStreamInfo();

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

            int count = GetKeyframeCount();
            if (count < 2)
            {
                return TimeSpan.Zero;
            }

            var totalDuration = LastKeyframeTime - FirstKeyframeTime;
            return TimeSpan.FromTicks(totalDuration.Ticks / (count - 1));
        }
    }

    /// <summary>
    /// Gets the keyframe count (maintained separately for performance).
    /// This field is accessed with Interlocked operations for thread safety.
    /// </summary>
    /// <returns>The current keyframe count.</returns>
    public int GetKeyframeCount() => System.Threading.Interlocked.CompareExchange(ref _keyframeCount, 0, 0);

    /// <summary>
    /// Increments the keyframe count atomically.
    /// </summary>
    /// <returns>The new keyframe count.</returns>
    public int IncrementKeyframeCount() => System.Threading.Interlocked.Increment(ref _keyframeCount);

    /// <summary>
    /// Decrements the keyframe count atomically.
    /// </summary>
    /// <returns>The new keyframe count.</returns>
    public int DecrementKeyframeCount() => System.Threading.Interlocked.Decrement(ref _keyframeCount);

    /// <summary>
    /// Validates the continuity counter for a PID and detects packet loss.
    /// Implements ISO/IEC 13818-1 Section 2.4.3.2 - Transport Stream packet layer.
    /// </summary>
    /// <param name="pid">The packet identifier.</param>
    /// <param name="continuityCounter">The continuity counter from the packet (0-15).</param>
    /// <param name="hasPayload">Whether the packet has a payload (CC only increments with payload).</param>
    /// <returns>True if continuity is valid, false if packet loss detected.</returns>
    public bool ValidateContinuityCounter(int pid, int continuityCounter, bool hasPayload)
    {
        // First packet for this PID - initialize
        if (!_continuityCounters.TryGetValue(pid, out int lastCC))
        {
            _continuityCounters[pid] = continuityCounter;
            return true;
        }

        // Continuity counter only increments for packets with payload
        if (!hasPayload)
        {
            // Duplicate packet (same CC) is allowed for stuffing/adaptation-only packets
            return continuityCounter == lastCC;
        }

        // Calculate expected CC (wraps at 16: 0-15)
        int expectedCC = (lastCC + 1) & 0x0F;

        if (continuityCounter != expectedCC)
        {
            // Discontinuity detected - increment loss counter
            long lossCount = _packetLossCount.AddOrUpdate(pid, 1, (_, count) => count + 1);

            // Calculate estimated packets lost (accounting for wrap-around)
            int packetsLost =
                continuityCounter >= expectedCC
                    ? continuityCounter - expectedCC
                    : (16 - expectedCC) + continuityCounter;

            // Update to current CC to continue tracking
            _continuityCounters[pid] = continuityCounter;

            return false; // Packet loss detected
        }

        // Valid continuity - update last seen CC
        _continuityCounters[pid] = continuityCounter;
        return true;
    }

    /// <summary>
    /// Gets the total packet loss count for a specific PID.
    /// </summary>
    /// <param name="pid">The packet identifier.</param>
    /// <returns>Number of discontinuities detected for this PID.</returns>
    public long GetPacketLossCount(int pid)
    {
        return _packetLossCount.TryGetValue(pid, out long count) ? count : 0;
    }

    /// <summary>
    /// Gets the total packet loss count across all PIDs in this program.
    /// </summary>
    /// <returns>Total discontinuities detected.</returns>
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
    /// Initializes or retrieves the PCR jitter buffer for this program.
    /// </summary>
    /// <param name="logger">Optional logger for PCR diagnostics.</param>
    /// <returns>The PCR jitter buffer instance.</returns>
    public PcrJitterBuffer GetOrCreatePcrJitterBuffer(Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        if (_pcrJitterBuffer == null)
        {
            _pcrJitterBuffer = new PcrJitterBuffer(ProgramNumber, logger);
        }

        return _pcrJitterBuffer;
    }

    /// <summary>
    /// Initializes or retrieves the timestamp tracker for A/V sync monitoring.
    /// </summary>
    /// <returns>The timestamp tracker instance.</returns>
    public TimestampTracker GetOrCreateTimestampTracker()
    {
        return _timestampTracker ??= new TimestampTracker();
    }

    /// <summary>
    /// Resets continuity counter tracking for a new stream session.
    /// </summary>
    public void ResetContinuityCounters()
    {
        _continuityCounters.Clear();
        _packetLossCount.Clear();
        _pcrJitterBuffer?.Reset();
    }

    /// <summary>
    /// Resets timing state for a reconnection scenario.
    /// This resets PCR jitter buffer, timestamp tracker, and continuity counters
    /// to prevent false drift/jitter readings when stream timing jumps on reconnect.
    /// Does NOT clear keyframe history or program structure.
    /// </summary>
    public void ResetTimingState()
    {
        // Reset PCR jitter buffer - prevents false jitter from PCR discontinuity
        _pcrJitterBuffer?.Reset();

        // Reset timestamp tracker - prevents false A/V drift readings
        _timestampTracker?.Reset();

        // Reset continuity counters - new connection may start at different CC value
        _continuityCounters.Clear();
        _packetLossCount.Clear();
    }

    /// <summary>
    /// Fully resets the program state, clearing all accumulated data.
    /// Call this when disposing or switching streams to prevent memory leaks.
    /// </summary>
    public void Reset()
    {
        ResetContinuityCounters();

        // Clear the keyframe queue to release memory
        while (Keyframes.TryDequeue(out _))
        {
            // Drain the queue
        }

        // Reset keyframe tracking
        System.Threading.Interlocked.Exchange(ref _keyframeCount, 0);
        FirstKeyframeTime = DateTime.MinValue;
        LastKeyframeTime = DateTime.MinValue;

        // Reset TR 101 290 tracking
        PatVersion = 0xFF;
        PmtVersion = 0xFF;
        PcrPacketsReceived = 0;

        // Reset audio and sync tracking
        Audio.Reset();
        _timestampTracker?.Reset();
    }

    /// <summary>
    /// Gets the current A/V synchronization status.
    /// </summary>
    /// <returns>The sync status, or Unknown if no tracker exists.</returns>
    public SyncStatus GetSyncStatus()
    {
        return _timestampTracker?.Status ?? SyncStatus.Unknown;
    }

    /// <summary>
    /// Gets the current A/V drift in milliseconds.
    /// </summary>
    /// <returns>The drift value, or 0 if unavailable.</returns>
    public double GetCurrentDriftMs()
    {
        return _timestampTracker?.CurrentDriftMs ?? 0;
    }
}
