using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Threading;
using Jellyfin.Xtream.Utility;
using Microsoft.Extensions.Logging;

// Performance: Skip zero-initialization of local variables in this module.
// All locals are explicitly initialized before use in hot paths.
// See: https://www.meziantou.net/csharp-9-improve-performance-using-skiplocalsinit.htm
[module: SkipLocalsInit]

namespace Jellyfin.Xtream.Service.MpegTs;

/// <summary>
/// Production-grade MPEG-TS indexer with Multi-Program Transport Stream (MPTS) support.
/// Handles fragmented packets, bounds checking, and concurrent access safely.
/// Indexes keyframes per-program to support multiple simultaneous channels.
/// </summary>
public class TsIndexer : ITsQualityMonitor
{
    private const int PatPid = 0;
    private const int CatPid = 1;

    // TR 101 290 validation: Check PCR PID validity after this many packets
    private const int PcrValidationThreshold = 5000;

    // Lookup table for fast stream type detection (O(1) vs O(4) comparisons)
    // Industry pattern from FFmpeg - eliminates branching in PMT parsing
    private static readonly bool[] _isVideoStreamType = new bool[256];
    private static readonly bool[] _isAudioStreamType = new bool[256];

    // Readonly fields
    private readonly int _bufferSize;
    private readonly byte[] _partialPacket = new byte[TsConstants.PacketSize];
    private readonly ILogger<TsIndexer>? _logger;

    // Multi-program support: Map of program number → ProgramInfo
    private readonly ConcurrentDictionary<int, ProgramInfo> _programs = new();

    // Packet reassembly state for handling TCP fragmentation
    private int _partialLength;
    private long _partialStartOffset;

    // Telemetry
    private long _totalPacketsParsed;
    private long _totalBytesProcessed;
    private long _resyncCount;
    private long _totalPacketErrors; // Transport Error Indicator count
    private long _totalContinuityErrors; // Continuity Counter discontinuities

    // TR 101 290 validation state
    private bool _pcrPidValidationDone;

    // ISO 13818-1: PAT interval monitoring (must be ≤500ms)
    private long _lastPatTimeTicks;
    private long _patIntervalViolations;
    private DateTime _lastPatIntervalWarning = DateTime.MinValue;

    // TR 101 290 Priority 1: Sync byte error tracking
    private long _syncByteErrors;
    private long _syncRecoveries;

    // TR 101 290 Priority 2: CRC validation errors
    private long _patCrcErrors;
    private long _pmtCrcErrors;

    // Scrambled PID tracking
    private readonly ConcurrentDictionary<int, bool> _scrambledPids = new();

    // CAT (Conditional Access Table) tracking - ISO 13818-1 Section 2.4.4.6
    private readonly ConcurrentDictionary<int, string> _caSystemIds = new();
    private long _catCrcErrors;
    private byte _catVersion = 0xFF;

    // Cache for first video program to avoid LINQ allocations in hot path
    private ProgramInfo? _cachedFirstVideoProgram;

    // Performance: Cached array of programs to avoid ConcurrentDictionary enumeration in hot path
    // Updated when programs are added/modified
    private volatile ProgramInfo[] _programsCache = Array.Empty<ProgramInfo>();

    // Performance: PID → ProgramInfo lookup table for O(1) routing (13-bit PID = 8192 entries)
    // null = not a known program PID, non-null = the program that owns this PID
    private readonly ProgramInfo?[] _pidToProgram = new ProgramInfo?[8192];

    // Performance: PID type classification for fast routing
    // 0 = unknown, 1 = video, 2 = audio, 3 = PCR-only
    private readonly byte[] _pidType = new byte[8192];

    private const byte PidTypeUnknown = 0;
    private const byte PidTypeVideo = 1;
    private const byte PidTypeAudio = 2;
    private const byte PidTypePcr = 3;

    static TsIndexer()
    {
        // Initialize video stream type lookup table
        _isVideoStreamType[0x01] = true; // MPEG-1 Video
        _isVideoStreamType[0x02] = true; // MPEG-2 Video
        _isVideoStreamType[0x1B] = true; // H.264 / AVC
        _isVideoStreamType[0x24] = true; // HEVC / H.265

        // Initialize audio stream type lookup table
        _isAudioStreamType[0x03] = true; // MPEG-1 Audio
        _isAudioStreamType[0x04] = true; // MPEG-2 Audio
        _isAudioStreamType[0x0F] = true; // AAC ADTS
        _isAudioStreamType[0x11] = true; // AAC LATM
        _isAudioStreamType[0x81] = true; // AC-3
        _isAudioStreamType[0x84] = true; // E-AC-3
        _isAudioStreamType[0x87] = true; // E-AC-3
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TsIndexer"/> class.
    /// </summary>
    /// <param name="bufferSize">The size of the circular buffer.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public TsIndexer(int bufferSize, ILogger<TsIndexer>? logger = null)
    {
        _bufferSize = bufferSize;
        _logger = logger;
    }

    /// <summary>
    /// Event raised when a TR 101 290 stream quality violation is detected.
    /// Subscribers can use this to send notifications (e.g., Discord).
    /// </summary>
    public event EventHandler<StreamQualityViolationEventArgs>? StreamQualityViolation;

    /// <summary>
    /// Event raised when A/V synchronization drift is detected.
    /// </summary>
    public event EventHandler<SyncDriftEventArgs>? SyncDriftDetected;

    /// <summary>
    /// Gets the count of programs detected in the stream.
    /// </summary>
    public int ProgramCount => _programs.Count;

    /// <summary>
    /// Gets the total number of TS packets successfully parsed.
    /// </summary>
    public long TotalPacketsParsed => Interlocked.Read(ref _totalPacketsParsed);

    /// <summary>
    /// Gets the total bytes processed by the indexer.
    /// </summary>
    public long TotalBytesProcessed => Interlocked.Read(ref _totalBytesProcessed);

    /// <summary>
    /// Gets the number of times the parser had to resynchronize due to corruption.
    /// </summary>
    public long ResyncCount => Interlocked.Read(ref _resyncCount);

    /// <summary>
    /// Gets the total number of packets with Transport Error Indicator set.
    /// </summary>
    public long TotalPacketErrors => Interlocked.Read(ref _totalPacketErrors);

    /// <summary>
    /// Gets the total number of continuity counter discontinuities detected across all programs.
    /// </summary>
    public long TotalContinuityErrors => Interlocked.Read(ref _totalContinuityErrors);

    /// <summary>
    /// Gets the number of PAT interval violations (interval >500ms per ISO 13818-1).
    /// </summary>
    public long PatIntervalViolations => Interlocked.Read(ref _patIntervalViolations);

    /// <summary>
    /// Gets the number of sync byte errors detected (packets not starting with 0x47).
    /// </summary>
    public long SyncByteErrors => Interlocked.Read(ref _syncByteErrors);

    /// <summary>
    /// Gets the number of successful sync recoveries after sync byte errors.
    /// </summary>
    public long SyncRecoveries => Interlocked.Read(ref _syncRecoveries);

    /// <summary>
    /// Gets the number of PAT CRC-32 validation failures.
    /// </summary>
    public long PatCrcErrors => Interlocked.Read(ref _patCrcErrors);

    /// <summary>
    /// Gets the number of PMT CRC-32 validation failures.
    /// </summary>
    public long PmtCrcErrors => Interlocked.Read(ref _pmtCrcErrors);

    /// <summary>
    /// Gets the PIDs that are currently scrambled (encrypted).
    /// </summary>
    public int[] ScrambledPids => [.. _scrambledPids.Keys];

    /// <summary>
    /// Gets the number of CAT CRC-32 validation failures.
    /// </summary>
    public long CatCrcErrors => Interlocked.Read(ref _catCrcErrors);

    /// <summary>
    /// Gets the detected Conditional Access System IDs from CAT.
    /// Key is CA_system_id, value is the CA system name (if known).
    /// </summary>
    public IReadOnlyDictionary<int, string> CaSystemIds => _caSystemIds;

    /// <summary>
    /// Gets a value indicating whether the stream is encrypted (has CA systems).
    /// </summary>
    public bool IsEncrypted => !_caSystemIds.IsEmpty || !_scrambledPids.IsEmpty;

    /// <summary>
    /// Gets all detected program numbers.
    /// </summary>
    /// <returns>Array of program numbers.</returns>
    public int[] GetProgramNumbers() => [.. _programs.Keys];

    /// <summary>
    /// Gets the video PID for a specific program.
    /// Zero-allocation: uses cache to avoid LINQ iterator allocation.
    /// </summary>
    /// <param name="programNumber">The program number (-1 or 0 for first/default program).</param>
    /// <returns>The video PID, or -1 if not detected.</returns>
    public int GetVideoPid(int programNumber = -1)
    {
        if (programNumber <= 0)
        {
            // Fast path: return cached first video program
            var cached = _cachedFirstVideoProgram;
            if (cached != null && cached.HasVideo)
            {
                return cached.VideoPid;
            }

            // Slow path: find and cache (no LINQ - manual enumeration)
            foreach (var prog in _programs.Values)
            {
                if (prog.HasVideo)
                {
                    _cachedFirstVideoProgram = prog;
                    return prog.VideoPid;
                }
            }

            return -1;
        }

        return _programs.TryGetValue(programNumber, out var program) ? program.VideoPid : -1;
    }

    /// <summary>
    /// Gets whether a specific program has video detected.
    /// </summary>
    /// <param name="programNumber">The program number (-1 or 0 for first/default program).</param>
    /// <returns>True if video PID detected for this program.</returns>
    public bool HasVideoPid(int programNumber = -1)
    {
        return GetVideoPid(programNumber) != -1;
    }

    /// <summary>
    /// Gets the keyframe count for a specific program.
    /// Zero-allocation: uses cache to avoid LINQ iterator allocation.
    /// </summary>
    /// <param name="programNumber">The program number (-1 or 0 for first/default program).</param>
    /// <returns>The number of keyframes indexed for this program.</returns>
    public int GetKeyframeCount(int programNumber = -1)
    {
        if (programNumber <= 0)
        {
            // Use cached first video program (no LINQ)
            var cached = _cachedFirstVideoProgram;
            if (cached != null && cached.HasVideo)
            {
                return cached.GetKeyframeCount();
            }

            // Fallback: manual search (rare - only if cache miss)
            foreach (var prog in _programs.Values)
            {
                if (prog.HasVideo)
                {
                    _cachedFirstVideoProgram = prog;
                    return prog.GetKeyframeCount();
                }
            }

            return 0;
        }

        return _programs.TryGetValue(programNumber, out var program) ? program.GetKeyframeCount() : 0;
    }

    /// <summary>
    /// Gets program information for a specific program.
    /// </summary>
    /// <param name="programNumber">The program number.</param>
    /// <returns>The program information, or null if not found.</returns>
    public ProgramInfo? GetProgramInfo(int programNumber)
    {
        return _programs.TryGetValue(programNumber, out var program) ? program : null;
    }

    /// <summary>
    /// Gets program information for the first detected program with video.
    /// Zero-allocation: uses cache to avoid LINQ iterator allocation.
    /// </summary>
    /// <returns>The first program with video, or null if none found.</returns>
    public ProgramInfo? GetFirstProgramWithVideo()
    {
        // Use cache (no LINQ allocation)
        var cached = _cachedFirstVideoProgram;
        if (cached != null && cached.HasVideo)
        {
            return cached;
        }

        // Manual search and cache
        foreach (var prog in _programs.Values)
        {
            if (prog.HasVideo)
            {
                _cachedFirstVideoProgram = prog;
                return prog;
            }
        }

        return null;
    }

    /// <summary>
    /// Processes a chunk of MPEG-TS data with full packet reassembly support.
    /// Handles fragmented packets from TCP streams correctly.
    /// </summary>
    /// <param name="data">The data chunk.</param>
    /// <param name="baseOffset">The absolute stream offset where this chunk starts.</param>
    public void ProcessChunk(ReadOnlySpan<byte> data, long baseOffset)
    {
        if (data.IsEmpty)
        {
            return;
        }

        Interlocked.Add(ref _totalBytesProcessed, data.Length);

        int offset = 0;

        // Step 1: Complete any partial packet from previous chunk
        if (_partialLength > 0)
        {
            int needed = TsConstants.PacketSize - _partialLength;
            int available = Math.Min(needed, data.Length);

            data[..available].CopyTo(_partialPacket.AsSpan(_partialLength));
            _partialLength += available;
            offset += available;

            if (_partialLength == TsConstants.PacketSize)
            {
                // Complete packet ready - use the original start offset
                ParsePacket(_partialPacket, _partialStartOffset);
                _partialLength = 0;
            }
            else
            {
                // Still incomplete, need more data
                return;
            }
        }

        // Step 2: Process all complete packets in this chunk
        while (offset + TsConstants.PacketSize <= data.Length)
        {
            if (data[offset] != TsConstants.SyncByte)
            {
                // TR 101 290 Priority 1: Sync byte error - track it
                Interlocked.Increment(ref _syncByteErrors);
                Interlocked.Increment(ref _resyncCount);

                int resyncOffset = FindSyncByte(data, offset + 1);

                if (resyncOffset == -1)
                {
                    // No sync found in remaining data
                    break;
                }

                // Successfully recovered sync
                Interlocked.Increment(ref _syncRecoveries);
                offset = resyncOffset;

                if (offset + TsConstants.PacketSize > data.Length)
                {
                    break;
                }
            }

            // Verify sync with stride check (optional but recommended for robustness)
            if (offset + TsConstants.PacketSize < data.Length)
            {
                if (data[offset + TsConstants.PacketSize] != TsConstants.SyncByte)
                {
                    // False sync - advance and retry
                    offset++;
                    continue;
                }
            }

            ParsePacket(data.Slice(offset, TsConstants.PacketSize), baseOffset + offset);
            offset += TsConstants.PacketSize;
        }

        // Step 3: Store any trailing partial packet for next chunk
        int remaining = data.Length - offset;
        if (remaining is > 0 and < TsConstants.PacketSize)
        {
            // Only store partial data if it's less than a full packet
            // (remaining >= PacketSize would indicate a bug in the loop above)
            data.Slice(offset, remaining).CopyTo(_partialPacket.AsSpan(0, remaining));
            _partialLength = remaining;
            _partialStartOffset = baseOffset + offset;
        }
        else if (remaining >= TsConstants.PacketSize)
        {
            // This shouldn't happen - indicates a bug in the processing loop
            // Log and discard to avoid corruption
            _logger?.LogWarning(
                "TsIndexer: Unexpected remaining bytes ({Remaining}) at end of chunk - discarding to prevent buffer overflow",
                remaining
            );
        }

        // Step 4: Prune old keyframes outside buffer window for all programs
        PruneOldKeyframes(baseOffset + data.Length);
    }

    /// <summary>
    /// Finds the best start offset (keyframe) for a specific program closest to the target offset.
    /// </summary>
    /// <param name="targetOffset">The target offset to start from.</param>
    /// <param name="programNumber">The program number (-1 or 0 for first/default program).</param>
    /// <returns>The offset of the nearest keyframe before the target, or -1 if none found.</returns>
    public long GetBestStartOffset(long targetOffset, int programNumber = -1)
    {
        ProgramInfo? program;

        if (programNumber <= 0)
        {
            // Use first program with video (for -1, 0, or any negative value)
            program = GetFirstProgramWithVideo();
        }
        else
        {
            _programs.TryGetValue(programNumber, out program);
        }

        if (program == null || !program.HasVideo)
        {
            return -1;
        }

        long bestOffset = -1;
        long minDistance = long.MaxValue;

        // Iterate through keyframes to find closest one before target
        foreach (var kf in program.Keyframes)
        {
            if (kf.Offset <= targetOffset)
            {
                long distance = targetOffset - kf.Offset;
                if (distance < minDistance)
                {
                    minDistance = distance;
                    bestOffset = kf.Offset;
                }
            }
        }

        // If no keyframe before target, return the first one (earliest in buffer)
        if (bestOffset == -1 && program.Keyframes.TryPeek(out var firstKf))
        {
            bestOffset = firstKf.Offset;
        }

        return bestOffset;
    }

    /// <summary>
    /// Finds a sync point where both audio and video can safely start.
    /// </summary>
    /// <param name="targetOffset">The target offset to start from.</param>
    /// <param name="programNumber">The program number (-1 or 0 for first/default program).</param>
    /// <returns>A sync point if found, or null.</returns>
    public SyncPoint? GetBestSyncPoint(long targetOffset, int programNumber = -1)
    {
        ProgramInfo? program;

        if (programNumber <= 0)
        {
            program = GetFirstProgramWithVideo();
        }
        else
        {
            _programs.TryGetValue(programNumber, out program);
        }

        if (program == null)
        {
            return null;
        }

        // First, find the best keyframe
        long keyframeOffset = GetBestStartOffset(targetOffset, programNumber);
        if (keyframeOffset == -1)
        {
            return null;
        }

        // If we have audio, try to find a good sync point
        if (program.HasAudio)
        {
            var tracker = program.GetOrCreateTimestampTracker();
            long minOffset = keyframeOffset - (256 * 1024); // 256KB before keyframe
            long maxOffset = keyframeOffset + (256 * 1024); // 256KB after keyframe

            var syncPoint = tracker.FindBestSyncPoint(minOffset, maxOffset);
            if (syncPoint.HasValue)
            {
                return syncPoint;
            }
        }

        // Fallback: return keyframe offset with estimated timestamps
        return new SyncPoint(
            keyframeOffset,
            new StreamTimestamp(0, keyframeOffset),
            new StreamTimestamp(0, keyframeOffset),
            DateTime.UtcNow
        );
    }

    /// <summary>
    /// Gets the current A/V sync status for a program.
    /// </summary>
    /// <param name="programNumber">The program number (-1 or 0 for first/default program).</param>
    /// <returns>The sync status.</returns>
    public SyncStatus GetSyncStatus(int programNumber = -1)
    {
        ProgramInfo? program;

        if (programNumber <= 0)
        {
            program = GetFirstProgramWithVideo();
        }
        else
        {
            _programs.TryGetValue(programNumber, out program);
        }

        return program?.GetSyncStatus() ?? SyncStatus.Unknown;
    }

    /// <summary>
    /// Gets the current A/V drift in milliseconds for a program.
    /// </summary>
    /// <param name="programNumber">The program number (-1 or 0 for first/default program).</param>
    /// <returns>The drift in milliseconds.</returns>
    public double GetCurrentDriftMs(int programNumber = -1)
    {
        ProgramInfo? program;

        if (programNumber <= 0)
        {
            program = GetFirstProgramWithVideo();
        }
        else
        {
            _programs.TryGetValue(programNumber, out program);
        }

        return program?.GetCurrentDriftMs() ?? 0;
    }

    /// <summary>
    /// Resets timing state for a reconnection scenario.
    /// This resets PCR jitter buffers, timestamp trackers, and continuity counters
    /// across all programs to prevent false drift/jitter readings when stream
    /// timing values jump discontinuously on reconnect.
    /// Does NOT clear program structure, keyframe history, or packet statistics.
    /// </summary>
    /// <remarks>
    /// Call this when an HTTP connection is re-established after EOF or timeout.
    /// Without this reset, the indexer would compare old PCR/PTS values from before
    /// the disconnect with new values after reconnect, causing impossible jitter
    /// readings (e.g., 95 million ms PCR intervals, 31 second A/V drift).
    /// </remarks>
    public void ResetTimingState()
    {
        foreach (var program in _programs.Values)
        {
            program.ResetTimingState();
        }

        // Clear partial packet buffer to ensure clean start
        _partialLength = 0;
        _partialStartOffset = 0;

        _logger?.LogDebug(
            "Timing state reset for {ProgramCount} programs (PCR jitter, A/V drift, continuity counters)",
            _programs.Count
        );
    }

    /// <summary>
    /// Resets the indexer state for a new stream session.
    /// </summary>
    public void Reset()
    {
        // Fully reset all programs (clears keyframes, counters, PCR buffers)
        foreach (var program in _programs.Values)
        {
            program.Reset();
        }

        _programs.Clear();

        // Clear cache
        _cachedFirstVideoProgram = null;

        // Clear partial packet
        _partialLength = 0;
        _partialStartOffset = 0;

        // Reset telemetry
        Interlocked.Exchange(ref _totalPacketsParsed, 0);
        Interlocked.Exchange(ref _totalBytesProcessed, 0);
        Interlocked.Exchange(ref _resyncCount, 0);
        Interlocked.Exchange(ref _totalPacketErrors, 0);
        Interlocked.Exchange(ref _totalContinuityErrors, 0);
    }

    /// <summary>
    /// Gets comprehensive diagnostics about the indexer state.
    /// Uses StringBuilder to reduce string allocations.
    /// </summary>
    /// <returns>Formatted diagnostics string.</returns>
    public string GetDiagnostics()
    {
        // Count programs with video manually (no LINQ allocation)
        int programsWithVideoCount = 0;
        foreach (var prog in _programs.Values)
        {
            if (prog.HasVideo)
            {
                programsWithVideoCount++;
            }
        }

        // Calculate error rates
        long totalParsed = TotalPacketsParsed;
        double teiErrorRate = totalParsed > 0 ? (TotalPacketErrors * 100.0) / totalParsed : 0;
        double ccErrorRate = totalParsed > 0 ? (TotalContinuityErrors * 100.0) / totalParsed : 0;

        var sb = new StringBuilder(512); // Preallocate reasonable capacity
        sb.AppendLine("TS Indexer Diagnostics:");
        sb.Append("  Programs Detected: ").Append(ProgramCount).AppendLine();
        sb.Append("  Programs with Video: ").Append(programsWithVideoCount).AppendLine();
        sb.Append("  Packets Parsed: ").AppendFormat(CultureInfo.InvariantCulture, "{0:N0}", totalParsed).AppendLine();
        sb.Append("  Bytes Processed: ")
            .AppendFormat(CultureInfo.InvariantCulture, "{0:N1}", TotalBytesProcessed / (1024 * 1024))
            .AppendLine(" MB");
        sb.Append("  Resync Events: ").Append(ResyncCount).AppendLine();
        sb.Append("  Transport Errors (TEI): ")
            .AppendFormat(CultureInfo.InvariantCulture, "{0:N0}", TotalPacketErrors)
            .AppendFormat(CultureInfo.InvariantCulture, " ({0:F4}%)", teiErrorRate)
            .AppendLine();
        sb.Append("  Continuity Errors (CC): ")
            .AppendFormat(CultureInfo.InvariantCulture, "{0:N0}", TotalContinuityErrors)
            .AppendFormat(CultureInfo.InvariantCulture, " ({0:F4}%)", ccErrorRate)
            .AppendLine();
        sb.Append("  Partial Packet Buffered: ").Append(_partialLength).AppendLine(" bytes");

        // TR 101 290 compliance metrics
        sb.AppendLine().AppendLine("  TR 101 290 Compliance:");
        sb.Append("    Sync Byte Errors: ").Append(SyncByteErrors).AppendLine();
        sb.Append("    Sync Recoveries: ").Append(SyncRecoveries).AppendLine();
        sb.Append("    PAT Interval Violations: ").Append(PatIntervalViolations).AppendLine();
        sb.Append("    PAT CRC Errors: ").Append(PatCrcErrors).AppendLine();
        sb.Append("    PMT CRC Errors: ").Append(PmtCrcErrors).AppendLine();

        sb.Append("    CAT CRC Errors: ").Append(CatCrcErrors).AppendLine();

        var scrambledPidList = ScrambledPids;
        if (scrambledPidList.Length > 0)
        {
            sb.Append("    Scrambled PIDs: ").Append(string.Join(", ", scrambledPidList)).AppendLine();
        }

        if (!_caSystemIds.IsEmpty)
        {
            sb.AppendLine("    CA Systems Detected:");
            foreach (var ca in _caSystemIds)
            {
                sb.Append("      0x")
                    .AppendFormat(CultureInfo.InvariantCulture, "{0:X4}", ca.Key)
                    .Append(": ")
                    .Append(ca.Value)
                    .AppendLine();
            }
        }

        sb.Append("    Stream Encrypted: ").Append(IsEncrypted ? "Yes" : "No").AppendLine();

        if (programsWithVideoCount > 0)
        {
            sb.AppendLine().AppendLine("  Programs:");
            foreach (var program in _programs.Values)
            {
                if (!program.HasVideo)
                {
                    continue;
                }

                var avgGop = program.AverageGopDuration;
                var programLoss = program.GetTotalPacketLoss();
                var pcrBuffer = program.GetOrCreatePcrJitterBuffer(_logger);

                sb.Append("    Program ").Append(program.ProgramNumber).AppendLine(":");
                sb.Append("      Video PID: ").Append(program.VideoPid).AppendLine();
                sb.Append("      PCR PID: ").Append(program.PcrPid).AppendLine();
                sb.Append("      Keyframes: ").Append(program.GetKeyframeCount()).AppendLine();
                sb.Append("      Avg GOP: ")
                    .Append(avgGop > TimeSpan.Zero ? $"{avgGop.TotalSeconds:F2}s" : "N/A")
                    .AppendLine();
                sb.Append("      Packet Loss: ").Append(programLoss).AppendLine(" discontinuities");
                sb.Append("      PCR Status: ")
                    .Append(pcrBuffer.PcrCount)
                    .Append(" PCRs, ")
                    .Append(pcrBuffer.PcrJitterExceeded)
                    .Append(" jitter violations, buffer: ")
                    .Append(pcrBuffer.CurrentBufferMs)
                    .AppendLine("ms");

                // TR 101 290 compliance info
                sb.Append("      PAT Version: ")
                    .Append(
                        program.PatVersion == 0xFF ? "N/A" : program.PatVersion.ToString(CultureInfo.InvariantCulture)
                    )
                    .AppendLine();
                sb.Append("      PMT Version: ")
                    .Append(
                        program.PmtVersion == 0xFF ? "N/A" : program.PmtVersion.ToString(CultureInfo.InvariantCulture)
                    )
                    .AppendLine();
                sb.Append("      PCR PID Valid: ")
                    .Append(program.PcrPacketsReceived > 0 ? "Yes" : "No")
                    .Append(" (")
                    .Append(program.PcrPacketsReceived)
                    .AppendLine(" PCRs received)");
                sb.Append("      PMT Interval Violations: ").Append(program.PmtIntervalViolations).AppendLine();

                // Audio stream info
                if (program.HasAudio)
                {
                    sb.Append("      Audio PID: ")
                        .Append(program.Audio.Pid)
                        .Append(" (")
                        .Append(program.Audio.Codec)
                        .AppendLine(")");
                    sb.Append("      Audio Frames: ").Append(program.Audio.FrameCount).AppendLine();
                }

                // A/V sync status
                var syncStatus = program.GetSyncStatus();
                var driftMs = program.GetCurrentDriftMs();
                sb.Append("      A/V Sync: ").Append(syncStatus);
                if (syncStatus is not SyncStatus.Unknown and not SyncStatus.NoAudio and not SyncStatus.NoVideo)
                {
                    sb.AppendFormat(CultureInfo.InvariantCulture, " (drift: {0:+0.0;-0.0;0}ms)", driftMs);
                }

                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// SIMD-accelerated sync byte search - 10-40x faster than scalar for large scans.
    /// Falls back to scalar search for small data or non-SIMD hardware.
    /// Supports AVX-512 (64 bytes), AVX2 (32 bytes), and SSE2 (16 bytes) per iteration.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static unsafe int FindSyncByte(ReadOnlySpan<byte> data, int startOffset)
    {
        int remaining = data.Length - startOffset;

        // Early exit for tiny searches
        if (remaining <= 0)
        {
            return -1;
        }

        fixed (byte* ptr = data)
        {
            byte* current = ptr + startOffset;

            // AVX-512 path: Process 64 bytes at once (modern server CPUs: Xeon, EPYC)
            // 2x faster than AVX2 for large resync scans after stream corruption
            if (Avx512BW.IsSupported && remaining >= 64)
            {
                Vector512<byte> syncPattern = Vector512.Create(TsConstants.SyncByte);
                int vectorLength = remaining & ~63; // Round down to multiple of 64

                for (int i = 0; i < vectorLength; i += 64)
                {
                    Vector512<byte> chunk = Avx512F.LoadVector512(current + i);
                    Vector512<byte> cmp = Avx512BW.CompareEqual(chunk, syncPattern);
                    ulong mask = cmp.ExtractMostSignificantBits();

                    if (mask != 0)
                    {
                        // Found match - get exact position using trailing zero count
                        int offset = BitOperations.TrailingZeroCount(mask);
                        return startOffset + i + offset;
                    }
                }

                // Continue with remaining bytes using AVX2/SSE2/scalar
                startOffset += vectorLength;
                remaining -= vectorLength;
                current += vectorLength;
            }

            // AVX2 path: Process 32 bytes at once (requires AVX2 CPU support)
            if (Avx2.IsSupported && remaining >= 32)
            {
                Vector256<byte> syncPattern = Vector256.Create(TsConstants.SyncByte);
                int vectorLength = remaining & ~31; // Round down to multiple of 32

                for (int i = 0; i < vectorLength; i += 32)
                {
                    Vector256<byte> chunk = Avx.LoadVector256(current + i);
                    Vector256<byte> cmp = Avx2.CompareEqual(chunk, syncPattern);
                    int mask = Avx2.MoveMask(cmp);

                    if (mask != 0)
                    {
                        // Found match - get exact position using trailing zero count
                        int offset = BitOperations.TrailingZeroCount((uint)mask);
                        return startOffset + i + offset;
                    }
                }

                // Process remaining bytes with scalar
                return FindSyncByteScalar(data, startOffset + vectorLength);
            }

            // SSE2 path: Process 16 bytes at once (fallback for older CPUs)
            if (Sse2.IsSupported && remaining >= 16)
            {
                Vector128<byte> syncPattern = Vector128.Create(TsConstants.SyncByte);
                int vectorLength = remaining & ~15; // Round down to multiple of 16

                for (int i = 0; i < vectorLength; i += 16)
                {
                    Vector128<byte> chunk = Sse2.LoadVector128(current + i);
                    Vector128<byte> cmp = Sse2.CompareEqual(chunk, syncPattern);
                    int mask = Sse2.MoveMask(cmp);

                    if (mask != 0)
                    {
                        int offset = BitOperations.TrailingZeroCount((uint)mask);
                        return startOffset + i + offset;
                    }
                }

                return FindSyncByteScalar(data, startOffset + vectorLength);
            }

            // No SIMD support - use scalar
            return FindSyncByteScalar(data, startOffset);
        }
    }

    /// <summary>
    /// Scalar (non-SIMD) sync byte search for tail bytes or non-SIMD hardware.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static int FindSyncByteScalar(ReadOnlySpan<byte> data, int startOffset)
    {
        for (int i = startOffset; i < data.Length; i++)
        {
            if (data[i] == TsConstants.SyncByte)
            {
                return i;
            }
        }

        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private unsafe void ParsePacket(ReadOnlySpan<byte> packet, long absoluteOffset)
    {
        // Bounds check
        if (packet.Length < 4)
        {
            return;
        }

        long packetCount = Interlocked.Increment(ref _totalPacketsParsed);

        // TR 101 290 Priority 1: Validate that declared PCR PID actually carries PCR values
        // Do this check once after processing enough packets to be meaningful
        if (!_pcrPidValidationDone && packetCount == PcrValidationThreshold)
        {
            _pcrPidValidationDone = true;
            ValidatePcrPids();
        }

        // Performance: Read all 4 header bytes in a single operation
        // This is faster than 4 separate byte reads on modern CPUs
        uint header = Unsafe.ReadUnaligned<uint>(ref MemoryMarshal.GetReference(packet));

        // INDUSTRY STANDARD: Check Transport Error Indicator (TEI) - ISO/IEC 13818-1 Section 2.4.3.2
        // Bit 7 of byte 1: Set when uncorrectable error detected at transmission layer
        // In little-endian: byte 1 is at bits 8-15 of uint, TEI is bit 15
        if ((header & 0x8000) != 0)
        {
            Interlocked.Increment(ref _totalPacketErrors);
            _logger?.LogDebugIfEnabled("TS Indexer: Transport error detected at offset {Offset}", absoluteOffset);
            return; // Drop corrupted packet - cannot trust its contents
        }

        // Header: 4 bytes (little-endian layout)
        // Byte 0: sync byte (0x47)
        // Byte 1: TEI(1) | PUSI(1) | Priority(1) | PID_hi(5)
        // Byte 2: PID_lo(8)
        // Byte 3: Scramble(2) | Adaptation(2) | CC(4)
        // In little-endian uint value 0xDDCCBBAA: byte0=AA, byte1=BB, byte2=CC, byte3=DD
        // PID = (byte1 & 0x1F) << 8 | byte2
        // byte1 is at bits 8-15, byte2 is at bits 16-23
        int pid = (int)((((header >> 8) & 0x1F) << 8) | ((header >> 16) & 0xFF));

        // TR 101 290 Priority 1: PID validation (must be 0-8191)
        // Branchless: unsigned comparison catches negative values too
        if ((uint)pid > 8191)
        {
            return; // Invalid PID - skip
        }

        // Performance: Prefetch PID type lookup table while we do other work
        // This hides memory latency by starting the cache line fetch early
        // Note: Can only prefetch unmanaged types (byte[]), not managed ProgramInfo[]
        if (Sse.IsSupported)
        {
            fixed (byte* pidTypePtr = _pidType)
            {
                Sse.Prefetch0(pidTypePtr + pid);
            }
        }

        // Extract byte 3 fields
        int byte3 = (int)(header >> 24);
        int adaptationFieldControl = (byte3 & 0x30) >> 4;

        bool hasAdaptation = (adaptationFieldControl & 0x02) != 0;
        bool hasPayload = (adaptationFieldControl & 0x01) != 0;

        // INDUSTRY STANDARD: Extract Continuity Counter (ISO/IEC 13818-1)
        // Bits 0-3 of byte 3: Wraps 0-15, increments only for packets with payload
        int continuityCounter = byte3 & 0x0F;

        // TR 101 290: Check scrambling control bits (bits 6-7 of byte 3)
        // 0x00 = not scrambled, 0x01 = reserved, 0x02/0x03 = scrambled
        int scrambleControl = (byte3 & 0xC0) >> 6;
        if (scrambleControl != 0)
        {
            // Track scrambled PIDs for reporting
            _scrambledPids.TryAdd(pid, true);
            return; // Scrambled packet - skip parsing
        }

        // Fast path: O(1) PID lookup for known video/audio/PCR PIDs
        var knownProgram = _pidToProgram[pid];
        if (knownProgram != null)
        {
            // Validate continuity counter for this PID
            if (!knownProgram.ValidateContinuityCounter(pid, continuityCounter, hasPayload))
            {
                Interlocked.Increment(ref _totalContinuityErrors);
                _logger?.LogDebugIfEnabled(
                    "TS Indexer: Continuity error on PID {Pid} (program {ProgramNumber}) at offset {Offset}",
                    pid,
                    knownProgram.ProgramNumber,
                    absoluteOffset
                );
            }

            // Route based on PID type (branchless using lookup table)
            byte pidType = _pidType[pid];

            // Handle PCR extraction (PCR PID may also be video PID)
            if (pidType == PidTypePcr || (pidType == PidTypeVideo && pid == knownProgram.PcrPid))
            {
                if (hasAdaptation)
                {
                    var pcrBuffer = knownProgram.GetOrCreatePcrJitterBuffer(_logger);
                    if (pcrBuffer.ProcessPacket(packet, hasAdaptation))
                    {
                        knownProgram.PcrPacketsReceived++;
                    }
                }
            }

            if (pidType == PidTypeVideo)
            {
                HandleVideoPacket(packet, absoluteOffset, knownProgram, hasAdaptation, hasPayload);
                return;
            }

            if (pidType == PidTypeAudio)
            {
                HandleAudioPacket(packet, absoluteOffset, knownProgram, hasPayload);
                return;
            }

            // PCR-only PID (no video/audio data)
            return;
        }

        // Slow path: System tables and PMT discovery
        if (pid == PatPid)
        {
            if (hasPayload)
            {
                ParsePat(packet, hasAdaptation);
            }

            return;
        }

        if (pid == CatPid)
        {
            if (hasPayload)
            {
                ParseCat(packet, hasAdaptation);
            }

            return;
        }

        // Check PMT PIDs (not in fast lookup since they're rarely hit after initial parsing)
        var programs = _programsCache;
        for (int i = 0; i < programs.Length; i++)
        {
            var program = programs[i];
            if (pid == program.PmtPid)
            {
                // Validate CC for PMT
                if (!program.ValidateContinuityCounter(pid, continuityCounter, hasPayload))
                {
                    Interlocked.Increment(ref _totalContinuityErrors);
                }

                if (hasPayload)
                {
                    ParsePmt(packet, hasAdaptation, program);
                }

                return;
            }
        }
    }

    /// <summary>
    /// Handles video PID packets - extracts keyframes and PTS.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void HandleVideoPacket(
        ReadOnlySpan<byte> packet,
        long absoluteOffset,
        ProgramInfo program,
        bool hasAdaptation,
        bool hasPayload
    )
    {
        // Check for Random Access Indicator (RAI) in adaptation field
        if (hasAdaptation && packet.Length > 5)
        {
            int adaptLen = packet[4];

            // Bounds check: adaptation length must be valid
            if (adaptLen > 0 && adaptLen < 184 && adaptLen + 5 <= packet.Length)
            {
                byte flags = packet[5];
                bool randomAccess = (flags & 0x40) != 0; // Bit 6 = RAI

                if (randomAccess)
                {
                    AddKeyframe(program, absoluteOffset);
                }
            }
        }

        // Extract video PTS for sync tracking (only on PES start)
        bool pusi = (packet[1] & 0x40) != 0;
        if (pusi && hasPayload)
        {
            ExtractAndRecordPts(packet, hasAdaptation, program, absoluteOffset, isVideo: true);
        }
    }

    /// <summary>
    /// Handles audio PID packets - extracts PTS for sync tracking.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void HandleAudioPacket(ReadOnlySpan<byte> packet, long absoluteOffset, ProgramInfo program, bool hasPayload)
    {
        bool pusi = (packet[1] & 0x40) != 0;
        if (pusi && hasPayload)
        {
            ExtractAndRecordPts(packet, hasAdaptation: false, program, absoluteOffset, isVideo: false);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ExtractAndRecordPts(
        ReadOnlySpan<byte> packet,
        bool hasAdaptation,
        ProgramInfo program,
        long absoluteOffset,
        bool isVideo
    )
    {
        int payloadStart = 4;
        if (hasAdaptation)
        {
            if (payloadStart >= packet.Length)
            {
                return;
            }

            int adaptLen = packet[payloadStart];
            payloadStart += 1 + adaptLen;
        }

        if (payloadStart >= packet.Length)
        {
            return;
        }

        var payload = packet.Slice(payloadStart);
        if (PesParser.TryExtractPts(payload, out long pts))
        {
            var tracker = program.GetOrCreateTimestampTracker();

            // Wire up drift detection events on first use
            if (tracker.VideoSampleCount == 0 && tracker.AudioSampleCount == 0)
            {
                tracker.DriftDetected += OnSyncDriftDetected;
            }

            if (isVideo)
            {
                tracker.RecordVideoPts(pts, absoluteOffset);
            }
            else
            {
                tracker.RecordAudioPts(pts, absoluteOffset);
                program.Audio.RecordFrame(absoluteOffset, pts);
            }
        }
    }

    private void OnSyncDriftDetected(object? sender, SyncDriftEventArgs e) => SyncDriftDetected?.Invoke(this, e);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void ParsePat(ReadOnlySpan<byte> packet, bool hasAdaptation)
    {
        // ISO 13818-1: PAT interval monitoring (must be ≤500ms)
        long currentTicks = DateTime.UtcNow.Ticks;
        long lastTicks = Interlocked.Exchange(ref _lastPatTimeTicks, currentTicks);
        if (lastTicks > 0)
        {
            long intervalMs = (currentTicks - lastTicks) / TimeSpan.TicksPerMillisecond;
            if (intervalMs > 500)
            {
                Interlocked.Increment(ref _patIntervalViolations);

                // Rate-limit warnings to once per 30 seconds
                var now = DateTime.UtcNow;
                if ((now - _lastPatIntervalWarning).TotalSeconds >= 30)
                {
                    _lastPatIntervalWarning = now;
                    OnStreamQualityViolation(
                        "PAT Interval Violation",
                        $"PAT interval {intervalMs}ms exceeds ISO 13818-1 limit of 500ms"
                    );
                }
            }
        }

        // Skip Header (4) + Adaptation Field if present
        int offset = 4;
        if (hasAdaptation)
        {
            if (offset >= packet.Length)
            {
                return;
            }

            int adaptLen = packet[offset];
            offset += 1 + adaptLen;
        }

        if (offset >= packet.Length)
        {
            return;
        }

        // PUSI means pointer field is present at start of payload
        bool pusi = (packet[1] & 0x40) != 0;
        if (pusi)
        {
            if (offset >= packet.Length)
            {
                return;
            }

            int pointerField = packet[offset];
            offset += 1 + pointerField;
        }

        if (offset + 3 > packet.Length)
        {
            return;
        }

        // Table ID (8) should be 0x00 for PAT
        if (packet[offset] != 0x00)
        {
            return;
        }

        // Section Length (includes everything after this field up to and including CRC)
        int sectionLength = ((packet[offset + 1] & 0x0F) << 8) | packet[offset + 2];

        // Bounds check: section must fit in remaining packet
        int sectionEnd = offset + 3 + sectionLength;
        if (sectionEnd > packet.Length)
        {
            return;
        }

        // TR 101 290 Priority 2: CRC-32 validation
        var section = packet.Slice(offset, 3 + sectionLength);
        if (!Crc32Mpeg2.Validate(section))
        {
            Interlocked.Increment(ref _patCrcErrors);
            return;
        }

        // TR 101 290 Priority 2: Extract PAT version number (5 bits at offset+5, bits 1-5)
        // Format: reserved(2) | version_number(5) | current_next_indicator(1)
        if (offset + 6 > packet.Length)
        {
            return;
        }

        byte patVersion = (byte)((packet[offset + 5] & 0x3E) >> 1);

        // Skip to Program Loop
        offset += 8;

        // Each program entry is 4 bytes: Program Number (16), Reserved(3), PID (13)
        int remainingBytes = sectionLength - 5 - 4; // 5 syntax bytes, 4 CRC bytes

        while (remainingBytes >= 4 && offset + 4 <= packet.Length)
        {
            int programNum = (packet[offset] << 8) | packet[offset + 1];
            int pmtPid = ((packet[offset + 2] & 0x1F) << 8) | packet[offset + 3];

            if (programNum != 0) // 0 is Network PID
            {
                // Add or update program
                if (_programs.TryAdd(programNum, new ProgramInfo(programNum, pmtPid)))
                {
                    _logger?.LogDebugIfEnabled(
                        "TS Indexer: Detected new program {ProgramNumber} with PMT PID {PmtPid}",
                        programNum,
                        pmtPid
                    );
                }

                // TR 101 290: Check for PAT version changes
                if (_programs.TryGetValue(programNum, out var program))
                {
                    if (program.PatVersion != 0xFF && program.PatVersion != patVersion)
                    {
                        var message =
                            $"PAT version changed for program {programNum}: {program.PatVersion} -> {patVersion}. Program structure may have changed.";
                        _logger?.LogWarning("TS Indexer: {Message}", message);

                        // Raise event for notification
                        OnStreamQualityViolation("PAT Version Change", message);

                        // Invalidate cached program info - PMT may need re-parsing
                        program.VideoPid = -1;
                        program.PcrPid = -1;
                        program.PmtVersion = 0xFF;
                        _cachedFirstVideoProgram = null;
                    }

                    program.PatVersion = patVersion;
                }
            }

            offset += 4;
            remainingBytes -= 4;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void ParsePmt(ReadOnlySpan<byte> packet, bool hasAdaptation, ProgramInfo program)
    {
        // ISO 13818-1: PMT interval monitoring (must be ≤500ms per program)
        program.RecordPmtReception(DateTime.UtcNow.Ticks);

        int offset = 4;
        if (hasAdaptation)
        {
            if (offset >= packet.Length)
            {
                return;
            }

            int adaptLen = packet[offset];
            offset += 1 + adaptLen;
        }

        if (offset >= packet.Length)
        {
            return;
        }

        bool pusi = (packet[1] & 0x40) != 0;
        if (pusi)
        {
            if (offset >= packet.Length)
            {
                return;
            }

            int pointerField = packet[offset];
            offset += 1 + pointerField;
        }

        if (offset + 3 > packet.Length)
        {
            return;
        }

        // Table ID should be 0x02 for PMT
        if (packet[offset] != 0x02)
        {
            return;
        }

        int sectionLength = ((packet[offset + 1] & 0x0F) << 8) | packet[offset + 2];

        // Bounds check: section must fit in remaining packet
        int sectionEnd = offset + 3 + sectionLength;
        if (sectionEnd > packet.Length)
        {
            return;
        }

        // TR 101 290 Priority 2: CRC-32 validation
        var section = packet.Slice(offset, 3 + sectionLength);
        if (!Crc32Mpeg2.Validate(section))
        {
            Interlocked.Increment(ref _pmtCrcErrors);
            return;
        }

        // TR 101 290 Priority 2: Extract PMT version number (5 bits at offset+5, bits 1-5)
        // Format: reserved(2) | version_number(5) | current_next_indicator(1)
        if (offset + 6 > packet.Length)
        {
            return;
        }

        byte pmtVersion = (byte)((packet[offset + 5] & 0x3E) >> 1);

        // Check for PMT version changes (TR 101 290)
        if (program.PmtVersion != 0xFF && program.PmtVersion != pmtVersion)
        {
            var message =
                $"PMT version changed for program {program.ProgramNumber}: {program.PmtVersion} -> {pmtVersion}. Stream configuration may have changed.";
            _logger?.LogWarning("TS Indexer: {Message}", message);

            // Raise event for notification
            OnStreamQualityViolation("PMT Version Change", message);

            // Invalidate cached video PID - need to re-detect
            program.VideoPid = -1;
            _cachedFirstVideoProgram = null;
        }

        program.PmtVersion = pmtVersion;

        // Extract PCR PID
        if (offset + 12 > packet.Length)
        {
            return;
        }

        int pcrPid = ((packet[offset + 8] & 0x1F) << 8) | packet[offset + 9];
        program.PcrPid = pcrPid;

        int programInfoLength = ((packet[offset + 10] & 0x0F) << 8) | packet[offset + 11];
        offset += 12 + programInfoLength;

        int remainingBytes = sectionLength - 9 - programInfoLength - 4; // CRC

        while (remainingBytes >= 5 && offset + 5 <= packet.Length)
        {
            int streamType = packet[offset];
            int elemPid = ((packet[offset + 1] & 0x1F) << 8) | packet[offset + 2];
            int esInfoLength = ((packet[offset + 3] & 0x0F) << 8) | packet[offset + 4];

            // Fast lookup table check (O(1) vs O(4) comparisons)
            if (_isVideoStreamType[streamType])
            {
                // Found video PID for this program
                if (program.VideoPid == -1)
                {
                    program.VideoPid = elemPid;

                    // Register in PID lookup table for O(1) routing in ParsePacket
                    RegisterPidMapping(elemPid, program, PidTypeVideo);

                    _logger?.LogDebugIfEnabled(
                        "TS Indexer: Program {ProgramNumber} video PID detected: {VideoPid} (stream type: 0x{StreamType:X2})",
                        program.ProgramNumber,
                        elemPid,
                        streamType
                    );
                }
            }
            else if (_isAudioStreamType[streamType])
            {
                // Found audio PID for this program
                if (!program.Audio.HasAudio)
                {
                    program.Audio.Pid = elemPid;
                    program.Audio.StreamType = streamType;

                    // Register in PID lookup table for O(1) routing in ParsePacket
                    RegisterPidMapping(elemPid, program, PidTypeAudio);

                    _logger?.LogDebugIfEnabled(
                        "TS Indexer: Program {ProgramNumber} audio PID detected: {AudioPid} (stream type: 0x{StreamType:X2}, codec: {Codec})",
                        program.ProgramNumber,
                        elemPid,
                        streamType,
                        program.Audio.Codec
                    );
                }
            }

            offset += 5 + esInfoLength;
            remainingBytes -= 5 + esInfoLength;
        }

        // Register PCR PID if different from video PID
        if (program.PcrPid >= 0 && program.PcrPid != program.VideoPid)
        {
            RegisterPidMapping(program.PcrPid, program, PidTypePcr);
        }

        // Update programs cache for hot path iteration
        UpdateProgramsCache();
    }

    /// <summary>
    /// Registers a PID to program mapping for O(1) lookup in packet routing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RegisterPidMapping(int pid, ProgramInfo program, byte pidType)
    {
        if ((uint)pid < 8192)
        {
            _pidToProgram[pid] = program;
            _pidType[pid] = pidType;
        }
    }

    /// <summary>
    /// Clears PID mappings for a program (called on version change).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClearProgramPidMappings(ProgramInfo program)
    {
        if (program.VideoPid >= 0 && program.VideoPid < 8192)
        {
            _pidToProgram[program.VideoPid] = null;
            _pidType[program.VideoPid] = PidTypeUnknown;
        }

        if (program.Audio.Pid >= 0 && program.Audio.Pid < 8192)
        {
            _pidToProgram[program.Audio.Pid] = null;
            _pidType[program.Audio.Pid] = PidTypeUnknown;
        }

        if (program.PcrPid >= 0 && program.PcrPid < 8192)
        {
            _pidToProgram[program.PcrPid] = null;
            _pidType[program.PcrPid] = PidTypeUnknown;
        }
    }

    /// <summary>
    /// Updates the cached programs array for allocation-free iteration in hot path.
    /// </summary>
    private void UpdateProgramsCache()
    {
        // Copy to array without LINQ to avoid allocation in builds without System.Linq
        var values = _programs.Values;
        var cache = new ProgramInfo[values.Count];
        values.CopyTo(cache, 0);
        _programsCache = cache;
    }

    /// <summary>
    /// Parses the Conditional Access Table (CAT) per ISO/IEC 13818-1 Section 2.4.4.6.
    /// The CAT contains CA descriptors that identify encryption systems in the stream.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ParseCat(ReadOnlySpan<byte> packet, bool hasAdaptation)
    {
        int offset = 4;
        if (hasAdaptation)
        {
            if (offset >= packet.Length)
            {
                return;
            }

            int adaptLen = packet[offset];
            offset += 1 + adaptLen;
        }

        if (offset >= packet.Length)
        {
            return;
        }

        bool pusi = (packet[1] & 0x40) != 0;
        if (pusi)
        {
            if (offset >= packet.Length)
            {
                return;
            }

            int pointerField = packet[offset];
            offset += 1 + pointerField;
        }

        if (offset + 3 > packet.Length)
        {
            return;
        }

        // Table ID should be 0x01 for CAT
        if (packet[offset] != 0x01)
        {
            return;
        }

        int sectionLength = ((packet[offset + 1] & 0x0F) << 8) | packet[offset + 2];

        // Bounds check
        int sectionEnd = offset + 3 + sectionLength;
        if (sectionEnd > packet.Length)
        {
            return;
        }

        // CRC-32 validation
        var section = packet.Slice(offset, 3 + sectionLength);
        if (!Crc32Mpeg2.Validate(section))
        {
            Interlocked.Increment(ref _catCrcErrors);
            return;
        }

        // Extract version number
        if (offset + 6 > packet.Length)
        {
            return;
        }

        byte catVersion = (byte)((packet[offset + 5] & 0x3E) >> 1);

        // Check for version change
        if (_catVersion != 0xFF && _catVersion != catVersion)
        {
            _logger?.LogDebugIfEnabled("TS Indexer: CAT version changed from {Old} to {New}", _catVersion, catVersion);
        }

        _catVersion = catVersion;

        // Skip fixed header (8 bytes: table_id + section_syntax_indicator + section_length +
        // transport_stream_id + version + section_number + last_section_number)
        offset += 8;

        // Parse CA descriptors until we hit the CRC (last 4 bytes)
        int descriptorEnd = sectionEnd - 4;

        while (offset + 2 <= descriptorEnd)
        {
            int descriptorTag = packet[offset];
            int descriptorLength = packet[offset + 1];

            if (offset + 2 + descriptorLength > descriptorEnd)
            {
                break;
            }

            // CA_descriptor has tag 0x09 (ISO/IEC 13818-1 Section 2.6.16)
            if (descriptorTag == 0x09 && descriptorLength >= 4)
            {
                int caSystemId = (packet[offset + 2] << 8) | packet[offset + 3];
                int emPid = ((packet[offset + 4] & 0x1F) << 8) | packet[offset + 5];

                // Map known CA system IDs to names
                string caSystemName = GetCaSystemName(caSystemId);

                if (_caSystemIds.TryAdd(caSystemId, caSystemName))
                {
                    _logger?.LogInformation(
                        "TS Indexer: Detected CA system: {CaSystemName} (ID: 0x{CaSystemId:X4}, EMM PID: {EmPid})",
                        caSystemName,
                        caSystemId,
                        emPid
                    );

                    OnStreamQualityViolation(
                        "Encrypted Stream Detected",
                        $"CA system {caSystemName} (0x{caSystemId:X4}) detected. Stream may require decryption."
                    );
                }
            }

            offset += 2 + descriptorLength;
        }
    }

    /// <summary>
    /// Gets the human-readable name for a CA system ID.
    /// Based on DVB allocations and common broadcast systems.
    /// </summary>
    private static string GetCaSystemName(int caSystemId)
    {
        return caSystemId switch
        {
            >= 0x0100 and <= 0x01FF => "SECA/Mediaguard",
            >= 0x0500 and <= 0x05FF => "Viaccess",
            >= 0x0600 and <= 0x06FF => "Irdeto",
            >= 0x0900 and <= 0x09FF => "NDS/Videoguard",
            >= 0x0B00 and <= 0x0BFF => "Conax",
            >= 0x0D00 and <= 0x0DFF => "Cryptoworks",
            >= 0x0E00 and <= 0x0EFF => "PowerVu",
            >= 0x1000 and <= 0x10FF => "Tandberg",
            >= 0x1700 and <= 0x17FF => "BetaCrypt",
            >= 0x1800 and <= 0x18FF => "Nagravision",
            >= 0x2600 and <= 0x26FF => "BISS",
            >= 0x4A00 and <= 0x4AFF => "DRE-Crypt",
            >= 0x4B00 and <= 0x4BFF => "Tongfang",
            >= 0x5500 and <= 0x55FF => "Griffin",
            >= 0x5601 and <= 0x5604 => "Verimatrix",
            _ => $"Unknown (0x{caSystemId:X4})",
        };
    }

    /// <summary>
    /// TR 101 290 Priority 1: Validates that declared PCR PIDs actually carry PCR values.
    /// Called once after processing enough packets to be meaningful.
    /// </summary>
    private void ValidatePcrPids()
    {
        foreach (var program in _programs.Values)
        {
            // Only check programs with a declared PCR PID
            if (program.PcrPid == -1)
            {
                continue;
            }

            if (program.PcrPacketsReceived == 0)
            {
                var message =
                    $"Program {program.ProgramNumber} declares PCR PID {program.PcrPid} but no PCR values received after {PcrValidationThreshold} packets. Clock recovery may be impaired.";
                _logger?.LogWarning("TR 101 290 VIOLATION: {Message}", message);

                // Raise event for notification
                OnStreamQualityViolation("PCR PID Invalid", message);
            }
            else
            {
                _logger?.LogDebugIfEnabled(
                    "TR 101 290: Program {ProgramNumber} PCR PID {PcrPid} validated - {PcrCount} PCR values received",
                    program.ProgramNumber,
                    program.PcrPid,
                    program.PcrPacketsReceived
                );
            }
        }
    }

    /// <summary>
    /// Raises the StreamQualityViolation event.
    /// </summary>
    /// <param name="violationType">The type of violation (e.g., "PCR PID Invalid", "PAT Version Change").</param>
    /// <param name="details">Detailed description of the violation.</param>
    private void OnStreamQualityViolation(string violationType, string details)
    {
        StreamQualityViolation?.Invoke(this, new StreamQualityViolationEventArgs(violationType, details));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void AddKeyframe(ProgramInfo program, long offset)
    {
        // Simple debouncing: check last keyframe in queue
        if (program.Keyframes.TryPeek(out var last))
        {
            // Peek at the last item (ConcurrentQueue doesn't have Last)
            // For better debouncing, we could use a separate field
            if (offset - last.Offset < 1000)
            {
                return; // Too close to previous keyframe
            }
        }

        var now = DateTime.UtcNow;
        program.Keyframes.Enqueue(new KeyframeInfo(offset, now));
        var keyframeCount = program.IncrementKeyframeCount();

        // Track timing for GOP duration calculation
        if (program.FirstKeyframeTime == DateTime.MinValue)
        {
            program.FirstKeyframeTime = now;
            _logger?.LogDebugIfEnabled(
                "TS Indexer: First keyframe detected for program {ProgramNumber} at offset {Offset}",
                program.ProgramNumber,
                offset
            );
        }

        program.LastKeyframeTime = now;

        // Log keyframe detection periodically (every 10th keyframe to avoid log spam)
        if (keyframeCount % 10 == 0)
        {
            var gopDuration = program.AverageGopDuration;
            _logger?.LogDebugIfEnabled(
                "TS Indexer: Program {ProgramNumber} - {Count} keyframes indexed, avg GOP: {GopSeconds:F2}s",
                program.ProgramNumber,
                keyframeCount,
                gopDuration.TotalSeconds
            );
        }
    }

    private void PruneOldKeyframes(long currentMaxOffset)
    {
        long minValidOffset = currentMaxOffset - _bufferSize;

        // Prune keyframes for all programs
        foreach (var program in _programs.Values)
        {
            while (program.Keyframes.TryPeek(out var oldest))
            {
                if (oldest.Offset >= minValidOffset)
                {
                    break; // This keyframe is still valid
                }

                // Try to remove it
                if (program.Keyframes.TryDequeue(out var removed))
                {
                    program.DecrementKeyframeCount();
                }
                else
                {
                    break; // Another thread dequeued it
                }
            }
        }
    }
}
