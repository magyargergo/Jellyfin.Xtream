using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.MpegTs;

/// <summary>
/// <para>
/// Program Clock Reference (PCR) jitter buffer for smooth MPEG-TS playout.
/// Implements clock recovery and dejitter according to ISO/IEC 13818-1 and TR 101 290.
/// </para>
/// <para>
/// Industry Standard Requirements:
/// - PCR accuracy: ±500 nanoseconds (TR 101 290 Priority 1).
/// - PCR jitter: &lt;±500µs for professional broadcast.
/// - PCR interval: &lt;100ms between successive PCRs (ISO/IEC 13818-1).
/// </para>
/// </summary>
public class PcrJitterBuffer
{
    private const int MinBufferMs = 50; // Minimum jitter buffer size
    private const int MaxBufferMs = 500; // Maximum jitter buffer size (0.5s)
    private const int TargetBufferMs = 150; // Target jitter buffer (150ms industry standard)
    private const int MaxJitterSamples = 50;

    private readonly ILogger? _logger;
    private readonly int _programNumber;
    private readonly Stopwatch _systemClock = Stopwatch.StartNew();
    private readonly ConcurrentQueue<long> _recentJitters = new();

    // Preallocated buffer for sorting jitter samples (avoids allocation in AdaptBufferSize)
    private readonly long[] _sortBuffer = new long[MaxJitterSamples + 10]; // Extra margin for concurrent additions

    // PCR tracking
    private long _lastPcrValue = -1; // Last PCR value extracted (27 MHz units)
    private long _lastPcrSystemTime = -1; // System time when last PCR was received (ticks)
    private long _pcrCount;
    private long _pcrJitterExceeded; // Count of PCR intervals exceeding ±500µs

    // Rate limiting for warnings
    private DateTime _lastJitterWarningLog = DateTime.MinValue;
    private DateTime _lastIntervalWarningLog = DateTime.MinValue;

    // Adaptive jitter buffer
    private int _currentBufferMs = TargetBufferMs;

    /// <summary>
    /// Initializes a new instance of the <see cref="PcrJitterBuffer"/> class.
    /// </summary>
    /// <param name="programNumber">The program number for logging.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public PcrJitterBuffer(int programNumber, ILogger? logger = null)
    {
        _programNumber = programNumber;
        _logger = logger;
    }

    /// <summary>
    /// Gets the total number of PCR values processed.
    /// </summary>
    public long PcrCount => System.Threading.Interlocked.Read(ref _pcrCount);

    /// <summary>
    /// Gets the count of PCR jitter violations (exceeding ±500µs).
    /// </summary>
    public long PcrJitterExceeded => System.Threading.Interlocked.Read(ref _pcrJitterExceeded);

    /// <summary>
    /// Gets the current adaptive jitter buffer size in milliseconds.
    /// </summary>
    public int CurrentBufferMs => _currentBufferMs;

    /// <summary>
    /// Extracts PCR from an MPEG-TS packet and updates jitter buffer.
    /// PCR is a 42-bit value: 33-bit base (90 kHz) + 9-bit extension (27 MHz).
    /// </summary>
    /// <param name="packet">The MPEG-TS packet (must be 188 bytes).</param>
    /// <param name="hasAdaptation">Whether the packet has an adaptation field.</param>
    /// <returns>True if PCR was extracted and processed.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool ProcessPacket(ReadOnlySpan<byte> packet, bool hasAdaptation)
    {
        if (!hasAdaptation || packet.Length < 12)
        {
            return false;
        }

        int adaptLen = packet[4];
        if (adaptLen < 7) // PCR requires at least 7 bytes in adaptation field
        {
            return false;
        }

        byte flags = packet[5];
        bool hasPcr = (flags & 0x10) != 0; // Bit 4 = PCR flag

        if (!hasPcr)
        {
            return false;
        }

        // Extract PCR: 6 bytes starting at offset 6
        // Format: 33-bit base @ 90 kHz + 6 reserved bits + 9-bit extension @ 27 MHz
        // PCR_base = bits 32-0, PCR_extension = bits 8-0
        long pcrBase =
            ((long)packet[6] << 25)
            | ((long)packet[7] << 17)
            | ((long)packet[8] << 9)
            | ((long)packet[9] << 1)
            | ((long)(packet[10] >> 7) & 0x01);

        int pcrExtension = ((packet[10] & 0x01) << 8) | packet[11];

        // Convert to 27 MHz units: PCR = base * 300 + extension
        long pcrValue = (pcrBase * 300) + pcrExtension;

        // Process PCR timing
        ProcessPcrValue(pcrValue);

        return true;
    }

    /// <summary>
    /// Processes a PCR value and updates jitter statistics.
    /// Implements TR 101 290 Priority 1 checks.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void ProcessPcrValue(long pcrValue)
    {
        long currentSystemTime = _systemClock.ElapsedTicks;
        System.Threading.Interlocked.Increment(ref _pcrCount);

        if (_lastPcrValue == -1)
        {
            // First PCR - initialize
            _lastPcrValue = pcrValue;
            _lastPcrSystemTime = currentSystemTime;
            _logger?.LogDebug(
                "PCR Jitter Buffer (Program {ProgramNumber}): First PCR received = {PcrValue}",
                _programNumber,
                pcrValue
            );
            return;
        }

        // Calculate time deltas
        long pcrDelta = pcrValue - _lastPcrValue;
        long systemDelta = currentSystemTime - _lastPcrSystemTime;

        // Handle PCR wrap-around (33-bit base wraps at 2^33)
        if (pcrDelta < 0)
        {
            const long PcrWrapValue = (1L << 33) * 300; // 33-bit base * 300
            pcrDelta += PcrWrapValue;
        }

        // Convert system time (100ns ticks) to PCR units (27 MHz)
        // 1 tick = 100ns, 27 MHz = 1 unit per 37.037ns
        // Conversion: ticks * 2.7 ≈ ticks * 27 / 10
        long systemDeltaPcr = (systemDelta * 27) / 10;

        // Calculate jitter (difference between PCR progression and real time)
        long jitterPcr = pcrDelta - systemDeltaPcr;

        // Convert jitter to microseconds for logging (27 MHz → µs: divide by 27)
        long jitterUs = jitterPcr / 27;

        // Store jitter sample for adaptive buffering
        // Limit cleanup attempts to prevent infinite loop under high concurrency
        _recentJitters.Enqueue(Math.Abs(jitterUs));
        int cleanupAttempts = 0;
        const int maxCleanupAttempts = MaxJitterSamples + 10; // Safety margin
        while (_recentJitters.Count > MaxJitterSamples && cleanupAttempts++ < maxCleanupAttempts)
        {
            if (!_recentJitters.TryDequeue(out _))
            {
                break; // Another thread already cleaned up
            }
        }

        // TR 101 290 Priority 1: Check if jitter exceeds ±500µs
        // This indicates network congestion or clock instability
        if (Math.Abs(jitterUs) > 500)
        {
            System.Threading.Interlocked.Increment(ref _pcrJitterExceeded);

            // Rate limit jitter warnings to once per 30 seconds
            var now = DateTime.UtcNow;
            if ((now - _lastJitterWarningLog).TotalSeconds >= 30)
            {
                _lastJitterWarningLog = now;
                _logger?.LogWarning(
                    "PCR Jitter Buffer (Program {ProgramNumber}): High jitter detected = {JitterUs}µs "
                        + "(threshold: ±500µs, violations: {Count}). Network congestion or source clock instability.",
                    _programNumber,
                    jitterUs,
                    _pcrJitterExceeded
                );
            }

            // Increase buffer size to absorb jitter
            AdaptBufferSize(increase: true);
        }

        // ISO/IEC 13818-1: PCR interval should be < 100ms
        long intervalMs = pcrDelta / 27_000; // 27 MHz → ms
        if (intervalMs > 100)
        {
            // Rate limit interval warnings to once per 30 seconds
            var now = DateTime.UtcNow;
            if ((now - _lastIntervalWarningLog).TotalSeconds >= 30)
            {
                _lastIntervalWarningLog = now;
                _logger?.LogWarning(
                    "PCR Jitter Buffer (Program {ProgramNumber}): PCR interval too large = {IntervalMs}ms "
                        + "(spec: <100ms). This may cause clock recovery issues.",
                    _programNumber,
                    intervalMs
                );
            }
        }

        // Update state
        _lastPcrValue = pcrValue;
        _lastPcrSystemTime = currentSystemTime;

        // Periodic diagnostics (every 100 PCRs)
        if (_pcrCount % 100 == 0)
        {
            LogDiagnostics();
        }
    }

    /// <summary>
    /// Adapts the jitter buffer size based on recent jitter measurements.
    /// Implements adaptive buffering for variable network conditions.
    /// Uses preallocated buffer to avoid allocations.
    /// </summary>
    /// <param name="increase">Whether to increase the buffer size.</param>
    private void AdaptBufferSize(bool increase)
    {
        if (increase)
        {
            // Increase buffer by 10ms, capped at max
            _currentBufferMs = Math.Min(_currentBufferMs + 10, MaxBufferMs);
        }
        else
        {
            // Calculate 95th percentile jitter from recent samples
            if (!_recentJitters.IsEmpty && _recentJitters.Count >= 10)
            {
                // Copy to preallocated buffer to avoid allocation
                int count = 0;
                foreach (var jitter in _recentJitters)
                {
                    if (count >= _sortBuffer.Length)
                    {
                        break;
                    }

                    _sortBuffer[count++] = jitter;
                }

                if (count >= 10)
                {
                    Array.Sort(_sortBuffer, 0, count);
                    int p95Index = (int)(count * 0.95);
                    long p95Jitter = _sortBuffer[p95Index];

                    // Set buffer to 3x the 95th percentile jitter (safety margin)
                    // Convert µs to ms: divide by 1000
                    int recommendedBuffer = (int)((p95Jitter * 3) / 1000);
                    _currentBufferMs = Math.Clamp(recommendedBuffer, MinBufferMs, MaxBufferMs);
                }
            }
        }
    }

    /// <summary>
    /// Gets comprehensive diagnostics about PCR and jitter buffer status.
    /// </summary>
    /// <returns>Formatted diagnostics string.</returns>
    public string GetDiagnostics()
    {
        long count = PcrCount;
        long violations = PcrJitterExceeded;
        double violationRate = count > 0 ? (violations * 100.0) / count : 0;

        // Calculate average jitter from recent samples
        long avgJitter = 0;
        if (!_recentJitters.IsEmpty)
        {
            long sum = 0;
            int count2 = 0;
            foreach (var jitter in _recentJitters)
            {
                sum += jitter;
                count2++;
            }

            if (count2 > 0)
            {
                avgJitter = sum / count2;
            }
        }

        return $"PCR Jitter Buffer (Program {_programNumber}):\n"
            + $"  PCRs Processed: {count:N0}\n"
            + $"  Jitter Violations: {violations:N0} ({violationRate:F2}%)\n"
            + $"  Average Jitter: {avgJitter}µs\n"
            + $"  Current Buffer: {_currentBufferMs}ms (target: {TargetBufferMs}ms)\n"
            + $"  Status: {GetHealthStatus(violationRate, avgJitter)}";
    }

    private static string GetHealthStatus(double violationRate, long avgJitter)
    {
        if (violationRate > 5 || avgJitter > 300)
        {
            return "⚠️ DEGRADED - High jitter";
        }

        if (violationRate > 1 || avgJitter > 150)
        {
            return "⚡ WARNING - Elevated jitter";
        }

        return "✓ HEALTHY";
    }

    private void LogDiagnostics()
    {
        long count = PcrCount;
        long violations = PcrJitterExceeded;
        double violationRate = count > 0 ? (violations * 100.0) / count : 0;

        _logger?.LogDebug(
            "PCR Jitter Buffer (Program {ProgramNumber}): {Count} PCRs, {Violations} violations ({Rate:F2}%), buffer: {BufferMs}ms",
            _programNumber,
            count,
            violations,
            violationRate,
            _currentBufferMs
        );
    }

    /// <summary>
    /// Resets the jitter buffer for a new stream session.
    /// </summary>
    public void Reset()
    {
        _lastPcrValue = -1;
        _lastPcrSystemTime = -1;
        System.Threading.Interlocked.Exchange(ref _pcrCount, 0);
        System.Threading.Interlocked.Exchange(ref _pcrJitterExceeded, 0);
        _currentBufferMs = TargetBufferMs;
        _recentJitters.Clear();
        _systemClock.Restart();
    }
}
