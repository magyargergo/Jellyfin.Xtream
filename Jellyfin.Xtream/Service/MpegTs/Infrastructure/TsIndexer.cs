using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Jellyfin.Xtream.Service.MpegTs.Core;
using Jellyfin.Xtream.Service.MpegTs.Models;
using Jellyfin.Xtream.Service.MpegTs.Parsing;
using Jellyfin.Xtream.Service.MpegTs.UseCases;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.MpegTs.Infrastructure;

/// <summary>
/// Production-grade MPEG-TS indexer with Multi-Program Transport Stream (MPTS) support.
/// Uses FFmpeg demuxer for program detection, PTS extraction, and keyframe detection.
/// Performs minimal packet scanning for error detection, scrambling, and discontinuity indicators.
/// </summary>
/// <remarks>
/// <para>
/// This class acts as an orchestrator that delegates to focused components:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="PacketStatistics"/> - Packet and byte counting</description></item>
///   <item><description><see cref="Tr101290Monitor"/> - TR 101 290 compliance checking</description></item>
///   <item><description><see cref="ProgramInfoService"/> - Timing services per program</description></item>
///   <item><description><see cref="ITsDemuxer"/> - FFmpeg-based demuxing and packet events</description></item>
/// </list>
/// </remarks>
public sealed class TsIndexer : ITsQualityMonitor, IDisposable
{
    // PID type classification for fast routing
    private const byte PidTypeVideo = 1;
    private const byte PidTypeAudio = 2;
    private const byte PidTypePcr = 3;

    // Readonly fields
    private readonly int _bufferSize;
    private readonly ILogger<TsIndexer>? _logger;
    private readonly ITsDemuxer? _demuxer;
    private readonly ProgramInfoService _programInfoService;
    private readonly PacketStatistics _statistics;
    private readonly Tr101290Monitor _tr101290Monitor;

    // Multi-program support: Map of program number → ProgramInfo
    private readonly ConcurrentDictionary<int, ProgramInfo> _programs = new();

    // Performance: PID → ProgramInfo lookup table for O(1) routing (13-bit PID = 8192 entries)
    private readonly ProgramInfo?[] _pidToProgram = new ProgramInfo?[8192];
    private readonly byte[] _pidType = new byte[8192];

    // Continuity counter tracking per PID
    private readonly byte[] _lastContinuityCounter = new byte[8192];
    private readonly bool[] _continuityInitialized = new bool[8192];

    // Scrambled PID tracking
    private readonly ConcurrentDictionary<int, bool> _scrambledPids = new();

    // CAT parsing for CA System ID detection (TR 101 290 Priority 2.6)
    private readonly CatParser _catParser = new();

    // Cache for first video program
    private ProgramInfo? _cachedFirstVideoProgram;
    private volatile ProgramInfo[] _programsCache = [];

    // Packet parsing state
    private long _currentBaseOffset;

    // Demuxer sampling to prevent overflow on high-bitrate streams
    // Feed data every N bytes instead of continuously to let FFmpeg catch up
    private const long DemuxerSampleIntervalBytes = 2 * 1024 * 1024; // 2MB between samples
    private const int DemuxerSampleSizeBytes = 256 * 1024; // 256KB per sample (enough for keyframe detection)
    private const int DemuxerInitSampleIntervalBytes = 16 * 1024; // 16KB during init (feed every chunk)
    private long _lastDemuxerFeedOffset;

    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="TsIndexer"/> class.
    /// </summary>
    /// <param name="bufferSize">The size of the circular buffer.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <param name="demuxer">Optional demuxer for program detection (e.g., FFmpegStreamDemuxer).</param>
    public TsIndexer(int bufferSize, ILogger<TsIndexer>? logger = null, ITsDemuxer? demuxer = null)
    {
        _bufferSize = bufferSize;
        _logger = logger;
        _demuxer = demuxer;

        // Initialize focused components
        _statistics = new PacketStatistics();
        _tr101290Monitor = new Tr101290Monitor(logger);
        _tr101290Monitor.StreamQualityViolation += OnStreamQualityViolation;

        _programInfoService = new ProgramInfoService(logger);
        _programInfoService.JitterViolationDetected += OnJitterViolationDetected;

        // Subscribe to demuxer events if available
        if (_demuxer != null)
        {
            _demuxer.ProgramDetected += OnDemuxerProgramDetected;
            _demuxer.PacketDemuxed += OnPacketDemuxed;
            _logger?.LogDebugIfEnabled("FFmpeg demuxer enabled for program detection and packet demuxing");
        }
    }

    private void OnJitterViolationDetected(object? sender, StreamQualityViolationEventArgs e) =>
        StreamQualityViolation?.Invoke(this, e);

    private void OnStreamQualityViolation(object? sender, StreamQualityViolationEventArgs e) =>
        StreamQualityViolation?.Invoke(this, e);

    /// <summary>
    /// Event raised when a TR 101 290 stream quality violation is detected.
    /// </summary>
    public event EventHandler<StreamQualityViolationEventArgs>? StreamQualityViolation;

    /// <summary>
    /// Event raised when A/V synchronization drift is detected.
    /// </summary>
    public event EventHandler<SyncDriftEventArgs>? SyncDriftDetected;

    /// <summary>
    /// Event raised when a PTS discontinuity is detected in the source stream.
    /// This indicates a timestamp jump that could cause playback issues.
    /// </summary>
    public event EventHandler<PtsDiscontinuityEventArgs>? PtsDiscontinuityDetected;

    /// <summary>
    /// Gets the count of programs detected in the stream.
    /// </summary>
    public int ProgramCount => _programs.Count;

    /// <summary>
    /// Gets the total number of TS packets successfully parsed.
    /// </summary>
    public long TotalPacketsParsed => _statistics.TotalPacketsParsed;

    /// <summary>
    /// Gets the total bytes processed by the indexer.
    /// </summary>
    public long TotalBytesProcessed => _statistics.TotalBytesProcessed;

    /// <summary>
    /// Gets the number of times the parser had to resynchronize due to corruption.
    /// </summary>
    public long ResyncCount => _statistics.ResyncCount;

    /// <summary>
    /// Gets the total number of packets with Transport Error Indicator set.
    /// </summary>
    public long TotalPacketErrors => _statistics.TotalPacketErrors;

    /// <summary>
    /// Gets the total number of continuity counter discontinuities detected.
    /// </summary>
    public long TotalContinuityErrors => _statistics.TotalContinuityErrors;

    /// <summary>
    /// Gets the number of PAT interval violations.
    /// </summary>
    public long PatIntervalViolations => _tr101290Monitor.PatIntervalViolations;

    /// <summary>
    /// Gets the number of sync byte errors.
    /// </summary>
    public long SyncByteErrors => _statistics.SyncByteErrors;

    /// <summary>
    /// Gets the number of successful sync recoveries.
    /// </summary>
    public long SyncRecoveries => _statistics.SyncRecoveries;

    /// <summary>
    /// Gets the number of PAT CRC-32 validation failures.
    /// </summary>
    public long PatCrcErrors => 0; // FFmpeg handles CRC validation

    /// <summary>
    /// Gets the number of PMT CRC-32 validation failures.
    /// </summary>
    public long PmtCrcErrors => 0; // FFmpeg handles CRC validation

    /// <summary>
    /// Gets the PIDs that are currently scrambled.
    /// </summary>
    public int[] ScrambledPids => [.. _scrambledPids.Keys];

    /// <summary>
    /// Gets the number of CAT CRC-32 validation failures.
    /// </summary>
    public long CatCrcErrors => _catParser.CatCrcErrors;

    /// <summary>
    /// Gets the detected Conditional Access System IDs with vendor names.
    /// </summary>
    public IReadOnlyDictionary<int, string> CaSystemIds
    {
        get
        {
            var result = new Dictionary<int, string>();
            foreach (var kvp in _catParser.CaSystems)
            {
                result[kvp.Key] = kvp.Value.VendorName;
            }

            return result;
        }
    }

    /// <summary>
    /// Gets detailed information about detected CA systems.
    /// </summary>
    public IReadOnlyDictionary<int, CaSystemInfo> CaSystemDetails => _catParser.CaSystems;

    /// <summary>
    /// Gets a value indicating whether the stream is encrypted.
    /// </summary>
    public bool IsEncrypted => !_scrambledPids.IsEmpty || _catParser.CaSystems.Count > 0;

    /// <summary>
    /// Gets all detected program numbers.
    /// </summary>
    public int[] GetProgramNumbers() => [.. _programs.Keys];

    /// <summary>
    /// Gets the video PID for a specific program.
    /// </summary>
    public int GetVideoPid(int programNumber = -1)
    {
        if (programNumber <= 0)
        {
            var cached = _cachedFirstVideoProgram;
            if (cached?.HasVideo == true)
            {
                return cached.VideoPid;
            }

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
    public bool HasVideoPid(int programNumber = -1) => GetVideoPid(programNumber) != -1;

    /// <summary>
    /// Gets the keyframe count for a specific program.
    /// </summary>
    public int GetKeyframeCount(int programNumber = -1)
    {
        if (programNumber <= 0)
        {
            var cached = _cachedFirstVideoProgram;
            if (cached?.HasVideo == true)
            {
                return cached.GetKeyframeCount();
            }

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
    public ProgramInfo? GetProgramInfo(int programNumber) =>
        _programs.TryGetValue(programNumber, out var program) ? program : null;

    /// <summary>
    /// Gets program information for the first detected program with video.
    /// </summary>
    public ProgramInfo? GetFirstProgramWithVideo()
    {
        var cached = _cachedFirstVideoProgram;
        if (cached?.HasVideo == true)
        {
            return cached;
        }

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
    /// Processes a chunk of MPEG-TS data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method is non-blocking. Data is fed to the FFmpeg demuxer which processes
    /// it in a background task. Events (PacketDemuxed, ProgramDetected) are fired
    /// asynchronously as packets are processed.
    /// </para>
    /// <para>
    /// Minimal error scanning (sync bytes, TEI, scrambling, continuity) is performed
    /// synchronously since FFmpeg doesn't expose these.
    /// </para>
    /// </remarks>
    public void ProcessChunk(ReadOnlySpan<byte> data, long baseOffset)
    {
        if (data.IsEmpty)
        {
            return;
        }

        _statistics.AddBytesProcessed(data.Length);
        _currentBaseOffset = baseOffset;

        // Feed data to demuxer with SAMPLING to prevent overflow on high-bitrate streams.
        // At 10 Mbps, feeding all data overwhelms FFmpeg's processing capacity.
        //
        // During initialization (before FFmpeg detects programs), we feed EVERY chunk
        // (16KB interval) to ensure avformat_open_input() gets continuous data without
        // ReadPacket timing out. After initialization, we sample every 2MB: feed 256KB,
        // skip 1.75MB. This gives FFmpeg time to process while still detecting keyframes.
        if (_demuxer != null)
        {
            var bytesSinceLastFeed = baseOffset - _lastDemuxerFeedOffset;

            // During init, feed every chunk (16KB). After init, sample every 2MB.
            var interval = _demuxer.IsInitialized ? DemuxerSampleIntervalBytes : DemuxerInitSampleIntervalBytes;

            if (bytesSinceLastFeed >= interval || _lastDemuxerFeedOffset == 0)
            {
                // Time to feed a sample to the demuxer
                var sampleSize = Math.Min(data.Length, DemuxerSampleSizeBytes);
                _demuxer.FeedData(data[..sampleSize]);
                _lastDemuxerFeedOffset = baseOffset;
            }
            // Else: skip this chunk - demuxer will catch up on next sample interval
        }

        // Minimal packet scanning for things FFmpeg doesn't expose:
        // - Sync byte errors
        // - TEI (Transport Error Indicator)
        // - Scrambling control
        // - Discontinuity indicator
        // - Continuity counter validation
        // - PAT interval monitoring
        // - CAT parsing
        ScanForErrorsAndMetadata(data, baseOffset);

        // Prune old keyframes
        PruneOldKeyframes(baseOffset + data.Length);
    }

    /// <summary>
    /// Minimal packet scanning for error detection and metadata extraction.
    /// FFmpeg handles the heavy lifting; this just catches what it doesn't expose.
    /// </summary>
    private void ScanForErrorsAndMetadata(ReadOnlySpan<byte> data, long baseOffset)
    {
        var offset = 0;
        var packetIndex = 0;

        while (offset + TsConstants.PacketSize <= data.Length)
        {
            // Check sync byte
            if (data[offset] != TsConstants.SyncByte)
            {
                _statistics.IncrementSyncByteErrors();
                var syncOffset = FindSyncByte(data[offset..]);
                if (syncOffset < 0)
                {
                    break;
                }

                offset += syncOffset;
                _statistics.IncrementSyncRecoveries();
                continue;
            }

            // Parse header (4 bytes)
            var header1 = data[offset + 1];
            var header2 = data[offset + 2];
            var header3 = data[offset + 3];

            var tei = (header1 & 0x80) != 0;
            var pusi = (header1 & 0x40) != 0;
            var pid = ((header1 & 0x1F) << 8) | header2;
            var scrambling = (byte)((header3 >> 6) & 0x03);
            var adaptationControl = (byte)((header3 >> 4) & 0x03);
            var cc = (byte)(header3 & 0x0F);

            var hasAdaptation = (adaptationControl & 0x02) != 0;
            var hasPayload = (adaptationControl & 0x01) != 0;

            var packetCount = _statistics.IncrementPacketsParsed();

            // TR 101 290: Validate PCR PIDs after threshold
            if (_tr101290Monitor.ShouldValidatePcrPids(packetCount))
            {
                ValidatePcrPids();
            }

            // Check Transport Error Indicator
            if (tei)
            {
                _statistics.IncrementPacketErrors();
                offset += TsConstants.PacketSize;
                packetIndex++;
                continue;
            }

            // Check scrambling
            if (scrambling != 0)
            {
                _ = _scrambledPids.TryAdd(pid, value: true);
                offset += TsConstants.PacketSize;
                packetIndex++;
                continue;
            }

            // Handle PAT for interval monitoring
            if (pid == TsConstants.PatPid)
            {
                _tr101290Monitor.MonitorPatInterval();
            }

            // Handle CAT for CA System ID detection
            if (pid == TsConstants.CatPid && pusi)
            {
                var payloadStart = 4;
                if (hasAdaptation && offset + 5 < data.Length)
                {
                    payloadStart = 5 + data[offset + 4];
                }

                if (payloadStart < TsConstants.PacketSize)
                {
                    var payload = data.Slice(offset + payloadStart, TsConstants.PacketSize - payloadStart);
                    _ = _catParser.ParseCatSection(payload);
                }
            }

            // Validate continuity counter for known PIDs
            var knownProgram = pid < 8192 ? _pidToProgram[pid] : null;
            if (knownProgram != null && hasPayload)
            {
                if (!ValidateContinuityCounter(pid, cc))
                {
                    _statistics.IncrementContinuityErrors();
                    _tr101290Monitor.HandleContinuityError(pid);
                }
                else
                {
                    _tr101290Monitor.ResetConsecutiveContinuityErrors();
                }
            }

            // Check for discontinuity indicator
            if (hasAdaptation && offset + 5 < data.Length)
            {
                int adaptationLength = data[offset + 4];
                // Valid adaptation_field_length is 0-183 (184 bytes max payload - 4 byte header)
                // Length 0 means no flags byte present, >183 is invalid per ISO/IEC 13818-1
                if (adaptationLength > 0 && adaptationLength <= 183 && offset + 5 < data.Length)
                {
                    var adaptationFlags = data[offset + 5];
                    var discontinuity = (adaptationFlags & 0x80) != 0;

                    if (discontinuity && knownProgram != null)
                    {
                        var pcrTiming = _programInfoService.GetOrCreatePcrTimingTracker(knownProgram.ProgramNumber);
                        pcrTiming.Reset();
                        _logger?.LogDebugIfEnabled(
                            "Discontinuity indicator detected for program {ProgramNumber}",
                            knownProgram.ProgramNumber
                        );
                    }

                    // Extract PCR if present
                    var pcrFlag = (adaptationFlags & 0x10) != 0;
                    if (pcrFlag && adaptationLength >= 7 && knownProgram != null)
                    {
                        var pcrData = data.Slice(offset + 6, 6);
                        var pcrBase =
                            ((ulong)pcrData[0] << 25)
                            | ((ulong)pcrData[1] << 17)
                            | ((ulong)pcrData[2] << 9)
                            | ((ulong)pcrData[3] << 1)
                            | ((ulong)(pcrData[4] >> 7) & 0x01);
                        var pcrExt = ((pcrData[4] & 0x01) << 8) | pcrData[5];
                        var pcr = (long)((pcrBase * 300) + (ulong)pcrExt);

                        var pcrTiming = _programInfoService.GetOrCreatePcrTimingTracker(knownProgram.ProgramNumber);
                        if (pcrTiming.ProcessPcr(pcr))
                        {
                            knownProgram.PcrPacketsReceived++;
                        }
                    }

                    // RAI-based keyframe fallback: when FFmpeg can't detect keyframes (missing SPS/PPS),
                    // use the Random Access Indicator flag as a fallback. Many IPTV streams don't include
                    // SPS/PPS at stream start, preventing FFmpeg from detecting AV_PKT_FLAG_KEY.
                    // RAI flag (0x40) in adaptation field indicates a random access point.
                    var hasRai = (adaptationFlags & 0x40) != 0;
                    if (hasRai && knownProgram != null && pid == knownProgram.VideoPid)
                    {
                        // Only use RAI fallback if FFmpeg hasn't detected any keyframes yet
                        // (indicates missing SPS/PPS scenario)
                        if (knownProgram.GetKeyframeCount() == 0 || _statistics.TotalPacketsParsed > 10000)
                        {
                            AddKeyframe(knownProgram, baseOffset + offset);
                        }
                    }
                }
            }

            offset += TsConstants.PacketSize;
            packetIndex++;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool ValidateContinuityCounter(int pid, byte cc)
    {
        if (!_continuityInitialized[pid])
        {
            _continuityInitialized[pid] = true;
            _lastContinuityCounter[pid] = cc;
            return true;
        }

        var expected = (byte)((_lastContinuityCounter[pid] + 1) & 0x0F);
        _lastContinuityCounter[pid] = cc;

        return cc == expected;
    }

    /// <summary>
    /// Handles demuxed packet events from FFmpeg.
    /// </summary>
    private void OnPacketDemuxed(object? sender, DemuxedPacketEventArgs e)
    {
        // Get or create program
        var program = GetOrCreateProgram(e.ProgramNumber, e.Pid, e.IsVideo, e.IsAudio);
        if (program == null)
        {
            return;
        }

        // Handle keyframes (FFmpeg detects via AV_PKT_FLAG_KEY)
        if (e.IsKeyframe && e.IsVideo && e.BytePosition >= 0)
        {
            AddKeyframe(program, e.BytePosition);
        }

        // Track PTS for A/V sync (FFmpeg already converted to 90kHz)
        if (e.Pts > 0)
        {
            var tracker = _programInfoService.GetOrCreateTimestampTracker(program.ProgramNumber);
            if (tracker.VideoSampleCount == 0 && tracker.AudioSampleCount == 0)
            {
                tracker.DriftDetected += OnSyncDriftDetected;
                tracker.PtsDiscontinuityDetected += OnPtsDiscontinuityDetected;
            }

            if (e.IsVideo)
            {
                program.VideoPacketCount++;
                tracker.RecordVideoPts(e.Pts, e.BytePosition);
            }
            else if (e.IsAudio)
            {
                tracker.RecordAudioPts(e.Pts, e.BytePosition);
                program.Audio.RecordFrame(e.BytePosition, e.Pts);
            }
        }
    }

    private ProgramInfo? GetOrCreateProgram(int programNumber, int pid, bool isVideo, bool isAudio)
    {
        if (programNumber <= 0)
        {
            // Find program by PID
            var existing = pid < 8192 ? _pidToProgram[pid] : null;
            if (existing != null)
            {
                return existing;
            }

            // Create default program
            programNumber = 1;
        }

        if (!_programs.TryGetValue(programNumber, out var program))
        {
            program = new ProgramInfo(programNumber, -1);
            if (!_programs.TryAdd(programNumber, program))
            {
                _ = _programs.TryGetValue(programNumber, out program);
            }
            else
            {
                UpdateProgramsCache();
            }
        }

        if (program == null)
        {
            return null;
        }

        // Update PIDs if not set
        if (isVideo && program.VideoPid == -1)
        {
            program.VideoPid = pid;
            RegisterPidMapping(pid, program, PidTypeVideo);
            _cachedFirstVideoProgram = null; // Invalidate cache
            _logger?.LogDebugIfEnabled("Program {ProgramNumber} video PID: {VideoPid}", programNumber, pid);
        }
        else if (isAudio && !program.Audio.HasAudio)
        {
            program.Audio.Pid = pid;
            RegisterPidMapping(pid, program, PidTypeAudio);
            _logger?.LogDebugIfEnabled("Program {ProgramNumber} audio PID: {AudioPid}", programNumber, pid);
        }

        return program;
    }

    private void OnDemuxerProgramDetected(object? sender, DemuxerProgramEventArgs e)
    {
        _logger?.LogDebugIfEnabled(
            "Demuxer detected program {Number}: Video={VideoPid}, PCR={PcrPid}",
            e.ProgramNumber,
            e.VideoPid,
            e.PcrPid
        );

        if (!_programs.TryGetValue(e.ProgramNumber, out var program))
        {
            program = new ProgramInfo(e.ProgramNumber, e.PmtPid);
            if (_programs.TryAdd(e.ProgramNumber, program))
            {
                _logger?.LogDebugIfEnabled(
                    "TS Indexer: Detected program {ProgramNumber} with PMT PID {PmtPid}",
                    e.ProgramNumber,
                    e.PmtPid
                );
            }
            else
            {
                _ = _programs.TryGetValue(e.ProgramNumber, out program);
            }
        }

        if (program == null)
        {
            return;
        }

        // Update program info from demuxer
        if (e.PcrPid >= 0 && program.PcrPid == -1)
        {
            program.PcrPid = e.PcrPid;
        }

        if (e.VideoPid >= 0 && program.VideoPid == -1)
        {
            program.VideoPid = e.VideoPid;
            RegisterPidMapping(e.VideoPid, program, PidTypeVideo);
        }

        if (e.AudioPids.Length > 0 && !program.Audio.HasAudio)
        {
            program.Audio.Pid = e.AudioPids[0];
            RegisterPidMapping(e.AudioPids[0], program, PidTypeAudio);
        }

        // Register PCR PID if different from video
        if (program.PcrPid >= 0 && program.PcrPid != program.VideoPid)
        {
            RegisterPidMapping(program.PcrPid, program, PidTypePcr);
        }

        UpdateProgramsCache();
    }

    /// <summary>
    /// Finds the best start offset (keyframe) for a program.
    /// </summary>
    /// <param name="targetOffset">The target offset to find the closest keyframe before.</param>
    /// <param name="programNumber">The program number (0 or -1 for first video program).</param>
    /// <returns>The offset of the best keyframe, or -1 if none found.</returns>
    public long GetBestStartOffset(long targetOffset, int programNumber = -1)
    {
        return GetBestStartOffset(targetOffset, programNumber, minValidOffset: 0);
    }

    /// <summary>
    /// Finds the best start offset (keyframe) for a program, ignoring keyframes before a minimum offset.
    /// </summary>
    /// <param name="targetOffset">The target offset to find the closest keyframe before.</param>
    /// <param name="programNumber">The program number (0 or -1 for first video program).</param>
    /// <param name="minValidOffset">
    /// Minimum valid offset. Keyframes before this offset are ignored.
    /// This is critical after HTTP reconnections to avoid reading stale data.
    /// </param>
    /// <returns>The offset of the best keyframe, or -1 if none found.</returns>
    public long GetBestStartOffset(long targetOffset, int programNumber, long minValidOffset)
    {
        var program = programNumber <= 0 ? GetFirstProgramWithVideo() : _programs.GetValueOrDefault(programNumber);

        if (program?.HasVideo != true)
        {
            return -1;
        }

        long bestOffset = -1;
        var minDistance = long.MaxValue;

        foreach (var kf in program.Keyframes)
        {
            // Skip keyframes before the minimum valid offset
            // This prevents reading stale data after HTTP reconnections
            if (kf.Offset < minValidOffset)
            {
                continue;
            }

            if (kf.Offset <= targetOffset)
            {
                var distance = targetOffset - kf.Offset;
                if (distance < minDistance)
                {
                    minDistance = distance;
                    bestOffset = kf.Offset;
                }
            }
        }

        // If no keyframe found before targetOffset, try to find the first valid keyframe after minValidOffset
        if (bestOffset == -1)
        {
            foreach (var kf in program.Keyframes)
            {
                if (kf.Offset >= minValidOffset)
                {
                    bestOffset = kf.Offset;
                    break;
                }
            }
        }

        return bestOffset;
    }

    /// <summary>
    /// Finds a sync point where both audio and video can safely start.
    /// </summary>
    public SyncPoint? GetBestSyncPoint(long targetOffset, int programNumber = -1) =>
        GetBestSyncPoint(targetOffset, programNumber, minValidOffset: 0);

    /// <summary>
    /// Finds a sync point where both audio and video can safely start,
    /// ignoring sync points before a minimum offset.
    /// </summary>
    /// <param name="targetOffset">The target offset to find the closest sync point before.</param>
    /// <param name="programNumber">The program number (0 or -1 for first video program).</param>
    /// <param name="minValidOffset">
    /// Minimum valid offset. Sync points before this offset are ignored.
    /// This is critical after HTTP reconnections to avoid reading stale data.
    /// </param>
    /// <returns>A sync point where audio and video can safely start, or null if none found.</returns>
    public SyncPoint? GetBestSyncPoint(long targetOffset, int programNumber, long minValidOffset)
    {
        var program = programNumber <= 0 ? GetFirstProgramWithVideo() : _programs.GetValueOrDefault(programNumber);

        if (program == null)
        {
            return null;
        }

        // Use the minValidOffset-aware overload to find a video keyframe in fresh data
        var keyframeOffset = GetBestStartOffset(targetOffset, programNumber, minValidOffset);
        if (keyframeOffset == -1)
        {
            return null;
        }

        if (program.HasAudio)
        {
            var tracker = _programInfoService.GetOrCreateTimestampTracker(program.ProgramNumber);

            // CRITICAL: Don't search BEFORE minValidOffset - that's stale data from the old connection!
            // Only search forward from the keyframe to find a safe audio start point
            var minOffset = Math.Max(keyframeOffset, minValidOffset);
            var maxOffset = keyframeOffset + (256 * 1024);

            var syncPoint = tracker.FindBestSyncPoint(minOffset, maxOffset);
            if (syncPoint.HasValue && syncPoint.Value.Offset >= minValidOffset)
            {
                return syncPoint;
            }
        }

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
    public SyncStatus GetSyncStatus(int programNumber = -1)
    {
        var program = programNumber <= 0 ? GetFirstProgramWithVideo() : _programs.GetValueOrDefault(programNumber);

        return program == null
            ? SyncStatus.Unknown
            : _programInfoService.GetSyncStatus(program.ProgramNumber, program.HasVideo, program.HasAudio);
    }

    /// <summary>
    /// Gets the current A/V drift in milliseconds.
    /// </summary>
    public double GetCurrentDriftMs(int programNumber = -1)
    {
        var program = programNumber <= 0 ? GetFirstProgramWithVideo() : _programs.GetValueOrDefault(programNumber);

        return program == null ? 0 : _programInfoService.GetCurrentDriftMs(program.ProgramNumber);
    }

    /// <summary>
    /// Gets the peak A/V drift observed in milliseconds.
    /// </summary>
    public double GetPeakDriftMs(int programNumber = -1)
    {
        var program = programNumber <= 0 ? GetFirstProgramWithVideo() : _programs.GetValueOrDefault(programNumber);

        if (program == null)
        {
            return 0;
        }

        var tracker = _programInfoService.GetOrCreateTimestampTracker(program.ProgramNumber);
        return tracker.PeakDriftMs;
    }

    /// <summary>
    /// Gets the number of A/V drift violations detected.
    /// </summary>
    public long GetDriftViolationCount(int programNumber = -1)
    {
        var program = programNumber <= 0 ? GetFirstProgramWithVideo() : _programs.GetValueOrDefault(programNumber);

        if (program == null)
        {
            return 0;
        }

        var tracker = _programInfoService.GetOrCreateTimestampTracker(program.ProgramNumber);
        return tracker.DriftViolationCount;
    }

    /// <summary>
    /// Resets timing state for a reconnection scenario.
    /// This also resets the TR 101 290 monitor to prevent false PAT/PCR violations
    /// caused by stale timestamps spanning the reconnection gap.
    /// </summary>
    public void ResetTimingState()
    {
        _programInfoService.ResetTimingState();
        _tr101290Monitor.Reset();
        _logger?.LogDebugIfEnabled("Timing state reset for {ProgramCount} programs", _programs.Count);
    }

    /// <summary>
    /// Gets cached SPS/PPS parameter sets for decoder initialization.
    /// Note: With FFmpeg-based keyframe detection, parameter set caching is not needed.
    /// </summary>
    /// <param name="programNumber">The program number (0 for first video program).</param>
    /// <returns>Always returns null as NAL unit parsing is handled by FFmpeg.</returns>
    [Obsolete("Parameter set caching is no longer supported with FFmpeg-based demuxing.")]
    public static byte[]? GetCachedParameterSets(int programNumber = -1) => null;

    /// <summary>
    /// Gets the current timing state for the first video program.
    /// Returns video PTS, audio PTS, and PCR for use in timestamp remapping.
    /// </summary>
    /// <returns>Tuple of (videoPts, audioPts, pcr) in 90kHz/27MHz, or zeros if not available.</returns>
    public (long VideoPts, long AudioPts, long Pcr) GetCurrentTimingState()
    {
        var program = GetFirstProgramWithVideo();
        if (program == null)
        {
            return (0, 0, 0);
        }

        var tracker = _programInfoService.GetOrCreateTimestampTracker(program.ProgramNumber);
        var pcrService = _programInfoService.GetOrCreatePcrTimingTracker(program.ProgramNumber);

        return (tracker.LastVideoPts.Value, tracker.LastAudioPts.Value, pcrService.EstimatedPcrTime);
    }

    /// <summary>
    /// Resets the indexer state for a new stream session.
    /// </summary>
    public void Reset()
    {
        foreach (var program in _programs.Values)
        {
            program.Reset();
        }

        _programs.Clear();
        _programInfoService.Clear();
        _catParser.Clear();
        _scrambledPids.Clear();
        _cachedFirstVideoProgram = null;
        _programsCache = [];

        // Clear PID mappings
        Array.Clear(_pidToProgram);
        Array.Clear(_pidType);
        Array.Clear(_lastContinuityCounter);
        Array.Clear(_continuityInitialized);

        // Reset focused components
        _statistics.Reset();
        _tr101290Monitor.Reset();

        // Reset demuxer on background thread to avoid blocking and potential crashes
        // if FFmpeg is still in a blocking call (avformat_open_input/avformat_find_stream_info)
        if (_demuxer != null)
        {
            var demuxer = _demuxer;
            _ = Task.Run(() =>
            {
                try
                {
                    demuxer.Reset();
                }
                catch (Exception ex)
                {
                    _logger?.LogDebugIfEnabled(ex, "Error resetting FFmpeg demuxer (non-fatal)");
                }
            });
        }

        _currentBaseOffset = 0;
    }

    /// <summary>
    /// Gets structured metrics about the indexer state.
    /// Use this for programmatic access to quality metrics.
    /// </summary>
    /// <returns>Structured metrics record.</returns>
    public TsIndexerMetrics GetMetrics()
    {
        var programMetrics = new List<ProgramMetrics>();
        var programsWithVideoCount = 0;

        foreach (var program in _programs.Values)
        {
            if (!program.HasVideo)
            {
                continue;
            }

            programsWithVideoCount++;
            var pcrTiming = _programInfoService.GetOrCreatePcrTimingTracker(program.ProgramNumber);
            var syncStatus = _programInfoService.GetSyncStatus(
                program.ProgramNumber,
                program.HasVideo,
                program.HasAudio
            );
            var driftMs = _programInfoService.GetCurrentDriftMs(program.ProgramNumber);
            var clockStatus = _programInfoService.GetClockStatus(program.ProgramNumber);
            var clockDriftPpm = _programInfoService.GetClockDriftPpm(program.ProgramNumber);

            programMetrics.Add(
                new ProgramMetrics(
                    ProgramNumber: program.ProgramNumber,
                    VideoPid: program.VideoPid,
                    AudioPid: program.Audio.Pid,
                    AudioCodec: program.Audio.Codec,
                    PcrPid: program.PcrPid,
                    KeyframeCount: program.GetKeyframeCount(),
                    AverageGopDuration: program.AverageGopDuration,
                    PacketLossCount: program.GetTotalPacketLoss(),
                    PcrCount: pcrTiming.PcrCount,
                    PcrJitterViolations: pcrTiming.JitterViolations,
                    PcrBufferMs: pcrTiming.CurrentBufferMs,
                    AudioFrameCount: program.Audio.FrameCount,
                    SyncStatus: syncStatus,
                    DriftMs: driftMs,
                    ClockStatus: clockStatus,
                    ClockDriftPpm: clockDriftPpm
                )
            );
        }

        var totalParsed = TotalPacketsParsed;
        var teiErrorRate = totalParsed > 0 ? TotalPacketErrors / (double)totalParsed : 0;
        var ccErrorRate = totalParsed > 0 ? TotalContinuityErrors / (double)totalParsed : 0;

        return new TsIndexerMetrics(
            ProgramCount: ProgramCount,
            ProgramsWithVideoCount: programsWithVideoCount,
            TotalPacketsParsed: totalParsed,
            TotalBytesProcessed: TotalBytesProcessed,
            TransportErrorCount: TotalPacketErrors,
            TransportErrorRate: teiErrorRate,
            ContinuityErrorCount: TotalContinuityErrors,
            ContinuityErrorRate: ccErrorRate,
            PatIntervalViolations: PatIntervalViolations,
            IsEncrypted: IsEncrypted,
            ScrambledPidCount: _scrambledPids.Count,
            CaSystemCount: _catParser.CaSystems.Count,
            CaSystemIds: CaSystemIds,
            Programs: programMetrics
        );
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_demuxer != null)
        {
            _demuxer.ProgramDetected -= OnDemuxerProgramDetected;
            _demuxer.PacketDemuxed -= OnPacketDemuxed;
        }

        _disposed = true;

        GC.SuppressFinalize(this);
    }

    private void OnSyncDriftDetected(object? sender, SyncDriftEventArgs e) => SyncDriftDetected?.Invoke(this, e);

    private void OnPtsDiscontinuityDetected(object? sender, PtsDiscontinuityEventArgs e)
    {
        _logger?.PluginLogWarning(
            "PTS discontinuity detected: {Direction} jump of {DeltaMs:F1}ms (PTS: {PrevPts} -> {NewPts}) at offset {Offset}",
            e.IsBackwardJump ? "BACKWARD" : "FORWARD",
            e.AbsoluteDeltaMs,
            e.PreviousPts,
            e.NewPts,
            e.Offset
        );
        PtsDiscontinuityDetected?.Invoke(this, e);
    }

    private void ValidatePcrPids()
    {
        foreach (var program in _programs.Values)
        {
            _tr101290Monitor.ValidatePcrPid(program.ProgramNumber, program.PcrPid, program.PcrPacketsReceived);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RegisterPidMapping(int pid, ProgramInfo program, byte pidType)
    {
        if ((uint)pid < 8192)
        {
            _pidToProgram[pid] = program;
            _pidType[pid] = pidType;
        }
    }

    private void UpdateProgramsCache()
    {
        var values = _programs.Values;
        var cache = new ProgramInfo[values.Count];
        values.CopyTo(cache, 0);
        _programsCache = cache;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddKeyframe(ProgramInfo program, long offset)
    {
        if (program.Keyframes.TryPeek(out var last))
        {
            if (offset - last.Offset < 1000)
            {
                return;
            }
        }

        // Check if we have a recent SPS offset to use instead of IDR offset
        // This ensures decoders receive parameter sets before the IDR frame
        var spsOffset = program.PeekLastSpsOffset();
        var effectiveOffset = offset;

        // Use SPS offset if it's valid and within 64KB before the IDR
        // (typical access unit: VPS/SPS/PPS/SEI/IDR is usually < 64KB)
        var spsDistance = offset - spsOffset;
        if (spsOffset >= 0 && spsDistance > 0 && spsDistance < 65536)
        {
            effectiveOffset = spsOffset;
        }

        var now = DateTime.UtcNow;
        program.Keyframes.Enqueue(new KeyframeInfo(effectiveOffset, now));
        var keyframeCount = program.IncrementKeyframeCount();

        if (program.FirstKeyframeTime == DateTime.MinValue)
        {
            program.FirstKeyframeTime = now;
            _logger?.LogDebugIfEnabled(
                "TS Indexer: First keyframe for program {ProgramNumber} at offset {Offset}",
                program.ProgramNumber,
                offset
            );
        }

        program.LastKeyframeTime = now;

        if (keyframeCount % 10 == 0)
        {
            var gopDuration = program.AverageGopDuration;
            _logger?.LogDebugIfEnabled(
                "TS Indexer: Program {ProgramNumber} - {Count} keyframes, avg GOP: {GopSeconds:F2}s",
                program.ProgramNumber,
                keyframeCount,
                gopDuration.TotalSeconds
            );
        }
    }

    private void PruneOldKeyframes(long currentMaxOffset)
    {
        var minValidOffset = currentMaxOffset - _bufferSize;

        foreach (var program in _programs.Values)
        {
            while (program.Keyframes.TryPeek(out var oldest))
            {
                if (oldest.Offset >= minValidOffset)
                {
                    break;
                }

                if (program.Keyframes.TryDequeue(out _))
                {
                    _ = program.DecrementKeyframeCount();
                }
                else
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Finds the next sync byte in the data.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindSyncByte(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] == TsConstants.SyncByte)
            {
                // Verify it's a real sync by checking next packet
                if (i + TsConstants.PacketSize < data.Length)
                {
                    if (data[i + TsConstants.PacketSize] == TsConstants.SyncByte)
                    {
                        return i;
                    }
                }
                else
                {
                    // Can't verify, assume it's valid
                    return i;
                }
            }
        }

        return -1;
    }
}
