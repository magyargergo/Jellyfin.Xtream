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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Jellyfin.Xtream.Service.Switching;

/// <summary>
/// Quality state indicating current stream health.
/// </summary>
public enum StreamQuality
{
    /// <summary>Stream is healthy with good throughput.</summary>
    Healthy,

    /// <summary>Throughput is degrading but acceptable.</summary>
    Degrading,

    /// <summary>Throughput is critically low, switch recommended.</summary>
    Critical,

    /// <summary>Stream has stalled, immediate switch required.</summary>
    Stalled,
}

/// <summary>
/// Monitors stream quality in real-time for proactive provider switching.
/// Uses sliding window throughput analysis with configurable thresholds.
/// </summary>
/// <remarks>
/// This monitor is designed to detect quality degradation BEFORE the user notices.
/// It maintains a rolling throughput average and triggers warnings when:
/// - Throughput drops below 50% of baseline (Degrading)
/// - Throughput drops below 25% of baseline (Critical)
/// - No data received for threshold period (Stalled)
/// </remarks>
public sealed class StreamQualityMonitor
{
    // Thresholds for quality assessment
    private const double DegradingThresholdRatio = 0.5; // 50% of baseline
    private const double CriticalThresholdRatio = 0.25; // 25% of baseline
    private const int StallThresholdMs = 2000; // 2 seconds without data = stall
    private const int MinSamplesForBaseline = 10;
    private const int SlidingWindowSize = 20;
    private const long MinBaselineBytesPerSecond = 100_000; // 100 KB/s minimum baseline

    // Sliding window for throughput samples (bytes per second)
    private readonly long[] _throughputSamples;
    private int _sampleIndex;
    private int _sampleCount;

    // State tracking
    private long _lastBytesWritten;
    private long _lastSampleTicks;
    private long _baselineThroughput;
    private int _currentQuality;
    private int _qualityChangedFlag;

    // Callback for quality changes
    private Action<StreamQuality, long>? _onQualityChanged;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamQualityMonitor"/> class.
    /// </summary>
    public StreamQualityMonitor()
    {
        _throughputSamples = new long[SlidingWindowSize];
        _lastSampleTicks = Environment.TickCount64;
        _currentQuality = (int)StreamQuality.Healthy;
    }

    /// <summary>
    /// Gets the current stream quality.
    /// </summary>
    public StreamQuality CurrentQuality => (StreamQuality)Volatile.Read(ref _currentQuality);

    /// <summary>
    /// Gets the baseline throughput in bytes per second.
    /// </summary>
    public long BaselineThroughput => Interlocked.Read(ref _baselineThroughput);

    /// <summary>
    /// Gets the current average throughput in bytes per second.
    /// </summary>
    public long CurrentThroughput => CalculateAverageThroughput();

    /// <summary>
    /// Gets a value indicating whether quality changed since last check.
    /// </summary>
    public bool HasQualityChanged => Interlocked.CompareExchange(ref _qualityChangedFlag, 0, 1) == 1;

    /// <summary>
    /// Gets the number of samples collected.
    /// </summary>
    public int SampleCount => Volatile.Read(ref _sampleCount);

    /// <summary>
    /// Gets a value indicating whether baseline is established.
    /// </summary>
    public bool HasBaseline => SampleCount >= MinSamplesForBaseline;

    /// <summary>
    /// Sets the callback for quality changes.
    /// </summary>
    /// <param name="callback">Callback receiving quality and current throughput.</param>
    public void SetQualityChangedCallback(Action<StreamQuality, long> callback)
    {
        _onQualityChanged = callback;
    }

    /// <summary>
    /// Records a throughput sample. Call this periodically (e.g., every 100-500ms).
    /// </summary>
    /// <param name="totalBytesWritten">Total bytes written to buffer.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordSample(long totalBytesWritten)
    {
        long now = Environment.TickCount64;
        long lastTicks = Interlocked.Read(ref _lastSampleTicks);
        long elapsedMs = now - lastTicks;

        // Avoid division by zero and too-frequent sampling
        if (elapsedMs < 50)
        {
            return;
        }

        long lastBytes = Interlocked.Exchange(ref _lastBytesWritten, totalBytesWritten);
        Interlocked.Exchange(ref _lastSampleTicks, now);

        long bytesThisPeriod = totalBytesWritten - lastBytes;
        long bytesPerSecond = (bytesThisPeriod * 1000) / elapsedMs;

        AddSample(bytesPerSecond, elapsedMs);
    }

    /// <summary>
    /// Checks if a preemptive switch should be initiated.
    /// Returns true if quality is Critical or Stalled.
    /// </summary>
    /// <returns>True if switch is recommended.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ShouldTriggerSwitch()
    {
        var quality = CurrentQuality;
        return quality == StreamQuality.Critical || quality == StreamQuality.Stalled;
    }

    /// <summary>
    /// Checks if quality is degrading and preconnection should be initiated.
    /// </summary>
    /// <returns>True if preconnection is recommended.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ShouldPreconnect()
    {
        return CurrentQuality >= StreamQuality.Degrading;
    }

    /// <summary>
    /// Resets the monitor state. Call when switching providers.
    /// </summary>
    public void Reset()
    {
        Array.Clear(_throughputSamples, 0, _throughputSamples.Length);
        _sampleIndex = 0;
        _sampleCount = 0;
        _lastBytesWritten = 0;
        _lastSampleTicks = Environment.TickCount64;
        Interlocked.Exchange(ref _baselineThroughput, 0);
        Volatile.Write(ref _currentQuality, (int)StreamQuality.Healthy);
        Volatile.Write(ref _qualityChangedFlag, 0);
    }

    /// <summary>
    /// Manually sets the baseline throughput. Useful when inheriting from previous provider.
    /// </summary>
    /// <param name="bytesPerSecond">Baseline throughput in bytes per second.</param>
    public void SetBaseline(long bytesPerSecond)
    {
        Interlocked.Exchange(ref _baselineThroughput, Math.Max(bytesPerSecond, MinBaselineBytesPerSecond));
    }

    private void AddSample(long bytesPerSecond, long elapsedMs)
    {
        // Store in sliding window
        int idx = _sampleIndex;
        _throughputSamples[idx] = bytesPerSecond;
        _sampleIndex = (idx + 1) % SlidingWindowSize;

        int count = Volatile.Read(ref _sampleCount);
        if (count < SlidingWindowSize)
        {
            Interlocked.Increment(ref _sampleCount);
            count++;
        }

        // Update baseline from early samples
        if (count == MinSamplesForBaseline)
        {
            long baseline = CalculateAverageThroughput();
            Interlocked.Exchange(ref _baselineThroughput, Math.Max(baseline, MinBaselineBytesPerSecond));
        }

        // Assess quality
        AssessQuality(elapsedMs);
    }

    private void AssessQuality(long sampleIntervalMs)
    {
        var previousQuality = (StreamQuality)Volatile.Read(ref _currentQuality);
        StreamQuality newQuality;

        long baseline = Interlocked.Read(ref _baselineThroughput);
        long currentThroughput = CalculateAverageThroughput();

        // Check for stall
        if (currentThroughput == 0 && sampleIntervalMs > StallThresholdMs)
        {
            newQuality = StreamQuality.Stalled;
        }
        else if (baseline == 0 || !HasBaseline)
        {
            // No baseline yet, assume healthy
            newQuality = StreamQuality.Healthy;
        }
        else
        {
            double ratio = (double)currentThroughput / baseline;

            if (ratio < CriticalThresholdRatio)
            {
                newQuality = StreamQuality.Critical;
            }
            else if (ratio < DegradingThresholdRatio)
            {
                newQuality = StreamQuality.Degrading;
            }
            else
            {
                newQuality = StreamQuality.Healthy;
            }
        }

        if (newQuality != previousQuality)
        {
            Volatile.Write(ref _currentQuality, (int)newQuality);
            Volatile.Write(ref _qualityChangedFlag, 1);
            _onQualityChanged?.Invoke(newQuality, currentThroughput);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long CalculateAverageThroughput()
    {
        int count = Volatile.Read(ref _sampleCount);
        if (count == 0)
        {
            return 0;
        }

        long sum = 0;
        int samplesToUse = Math.Min(count, SlidingWindowSize);

        for (int i = 0; i < samplesToUse; i++)
        {
            sum += Volatile.Read(ref _throughputSamples[i]);
        }

        return sum / samplesToUse;
    }
}

/// <summary>
/// Result of a proactive switch attempt.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct ProactiveSwitchResult : IEquatable<ProactiveSwitchResult>
{
    /// <summary>Gets a value indicating whether the switch was initiated.</summary>
    public bool Initiated { get; init; }

    /// <summary>Gets the quality that triggered the switch.</summary>
    public StreamQuality TriggerQuality { get; init; }

    /// <summary>Gets the throughput at switch time.</summary>
    public long ThroughputAtSwitch { get; init; }

    /// <summary>Gets the baseline throughput.</summary>
    public long BaselineThroughput { get; init; }

    /// <summary>Creates a result indicating no switch was needed.</summary>
    public static readonly ProactiveSwitchResult NotNeeded = new() { Initiated = false };

    /// <summary>Creates a result for an initiated switch.</summary>
    public static ProactiveSwitchResult SwitchInitiated(StreamQuality quality, long throughput, long baseline) =>
        new()
        {
            Initiated = true,
            TriggerQuality = quality,
            ThroughputAtSwitch = throughput,
            BaselineThroughput = baseline,
        };

    /// <summary>Equality operator.</summary>
    public static bool operator ==(ProactiveSwitchResult left, ProactiveSwitchResult right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(ProactiveSwitchResult left, ProactiveSwitchResult right) => !left.Equals(right);

    /// <inheritdoc />
    public bool Equals(ProactiveSwitchResult other) =>
        Initiated == other.Initiated && TriggerQuality == other.TriggerQuality;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ProactiveSwitchResult other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Initiated, TriggerQuality);
}
