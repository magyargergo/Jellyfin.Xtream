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

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Predicts buffer overflow by tracking read/write rates and gap trends.
/// Provides early warning when a reader is likely to fall behind.
/// </summary>
/// <remarks>
/// <para>
/// Uses exponential moving average (EMA) to smooth rate calculations
/// and linear regression on gap samples to detect overflow trends.
/// </para>
/// <para>
/// Thresholds are based on typical IPTV streaming characteristics:
/// </para>
/// <list type="bullet">
/// <item><description>Warning: Gap > 80% of buffer size, or gap increasing faster than it's decreasing</description></item>
/// <item><description>Critical: Predicted overflow within 10 seconds</description></item>
/// </list>
/// </remarks>
/// <remarks>
/// Initializes a new instance of the <see cref="OverflowPredictor"/> class.
/// </remarks>
/// <param name="bufferSize">The circular buffer size in bytes.</param>
public sealed class OverflowPredictor(int bufferSize)
{
    private const int MaxSamples = 30;
    private const double EmaAlpha = 0.2; // Weight for new samples
    private const double WarningGapPercentage = 0.80;
    private const double CriticalSecondsToOverflow = 10.0;

    private readonly int _bufferSize = bufferSize;
    private readonly GapSample[] _gapSamples = new GapSample[MaxSamples];
    private int _gapSampleCount;
    private int _gapSampleWriteIndex;
    private long _lastWriteHead;
    private long _lastReadHead;
    private long _lastSampleTicks;
    private int _sampleCount;

    /// <summary>
    /// Gets the current overflow risk level.
    /// </summary>
    public OverflowRisk CurrentRisk { get; private set; }

    /// <summary>
    /// Gets the estimated seconds until overflow (NaN if not applicable).
    /// </summary>
    public double SecondsToOverflow { get; private set; }

    /// <summary>
    /// Gets the current write rate in bytes per second.
    /// </summary>
    public double WriteRateBps { get; private set; }

    /// <summary>
    /// Gets the current read rate in bytes per second.
    /// </summary>
    public double ReadRateBps { get; private set; }

    /// <summary>
    /// Gets the current gap as a percentage of buffer size.
    /// </summary>
    public double CurrentGapPercentage { get; private set; }

    /// <summary>
    /// Gets the gap trend (positive = increasing, negative = decreasing).
    /// </summary>
    public double GapTrendBps { get; private set; }

    /// <summary>
    /// Records current positions and updates predictions.
    /// Should be called periodically (e.g., every second).
    /// </summary>
    /// <param name="writeHead">Current write head position.</param>
    /// <param name="readHead">Current read head position.</param>
    /// <returns>The current overflow risk level.</returns>
    public OverflowRisk RecordSample(long writeHead, long readHead)
    {
        var currentTicks = DateTime.UtcNow.Ticks;

        if (_lastSampleTicks > 0)
        {
            var elapsedSeconds = (currentTicks - _lastSampleTicks) / (double)TimeSpan.TicksPerSecond;
            if (elapsedSeconds > 0.01) // Minimum 10ms between samples
            {
                UpdateRates(writeHead, readHead, elapsedSeconds);
                UpdatePrediction(writeHead, readHead);
            }
        }
        else
        {
            // First sample - initialize
            _lastWriteHead = writeHead;
            _lastReadHead = readHead;
            CurrentRisk = OverflowRisk.Low;
            SecondsToOverflow = double.NaN;
        }

        _lastSampleTicks = currentTicks;
        _sampleCount++;

        return CurrentRisk;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdateRates(long writeHead, long readHead, double elapsedSeconds)
    {
        // Calculate instantaneous rates
        var bytesWritten = writeHead - _lastWriteHead;
        var bytesRead = readHead - _lastReadHead;

        var instantWriteRate = bytesWritten / elapsedSeconds;
        var instantReadRate = bytesRead / elapsedSeconds;

        // Apply EMA smoothing
        if (_sampleCount < 3)
        {
            // Use simple average for first few samples
            WriteRateBps = instantWriteRate;
            ReadRateBps = instantReadRate;
        }
        else
        {
            WriteRateBps = (EmaAlpha * instantWriteRate) + ((1 - EmaAlpha) * WriteRateBps);
            ReadRateBps = (EmaAlpha * instantReadRate) + ((1 - EmaAlpha) * ReadRateBps);
        }

        _lastWriteHead = writeHead;
        _lastReadHead = readHead;

        // Record gap sample (circular overwrite when full)
        var gap = writeHead - readHead;
        _gapSamples[_gapSampleWriteIndex] = new GapSample(DateTime.UtcNow.Ticks, gap);
        _gapSampleWriteIndex = (_gapSampleWriteIndex + 1) % MaxSamples;
        if (_gapSampleCount < MaxSamples)
        {
            _gapSampleCount++;
        }
    }

    private void UpdatePrediction(long writeHead, long readHead)
    {
        var gap = writeHead - readHead;
        CurrentGapPercentage = (double)gap / _bufferSize;

        // Calculate gap trend using linear regression on recent samples
        GapTrendBps = CalculateGapTrend();

        // Determine risk level
        if (_sampleCount < 5)
        {
            // Not enough data yet
            CurrentRisk = OverflowRisk.Unknown;
            SecondsToOverflow = double.NaN;
            return;
        }

        // Calculate seconds until gap reaches buffer size
        var remainingSpace = _bufferSize - gap;
        var gapGrowthRate = WriteRateBps - ReadRateBps;

        SecondsToOverflow = gapGrowthRate > 0 ? remainingSpace / gapGrowthRate : double.PositiveInfinity;

        // Determine risk level based on multiple factors
        if (CurrentGapPercentage > 0.95 || SecondsToOverflow < 3.0)
        {
            CurrentRisk = OverflowRisk.Imminent;
        }
        else if (SecondsToOverflow < CriticalSecondsToOverflow || CurrentGapPercentage > 0.90)
        {
            CurrentRisk = OverflowRisk.Critical;
        }
        else
        {
            CurrentRisk =
                CurrentGapPercentage > WarningGapPercentage || GapTrendBps > 100000 ? OverflowRisk.Warning
                : GapTrendBps > 0 ? OverflowRisk.Elevated
                : OverflowRisk.Low;
        }
    }

    private double CalculateGapTrend()
    {
        var count = _gapSampleCount;
        if (count < 3)
        {
            return 0;
        }

        // Simple linear regression: gap = a + b * time
        // We just need the slope (b) to know if gap is increasing
        double sumX = 0,
            sumY = 0,
            sumXY = 0,
            sumX2 = 0;

        // Determine the start index for oldest sample in circular buffer
        var startIndex = count >= MaxSamples ? _gapSampleWriteIndex : 0;
        var baseTime = _gapSamples[startIndex].Ticks;

        for (var i = 0; i < count; i++)
        {
            var sample = _gapSamples[(startIndex + i) % MaxSamples];
            var x = (sample.Ticks - baseTime) / (double)TimeSpan.TicksPerSecond;
            double y = sample.Gap;

            sumX += x;
            sumY += y;
            sumXY += x * y;
            sumX2 += x * x;
        }

        var denominator = (count * sumX2) - (sumX * sumX);
        if (Math.Abs(denominator) < 0.0001)
        {
            return 0;
        }

        var slope = ((count * sumXY) - (sumX * sumY)) / denominator;
        return slope; // bytes per second trend
    }

    /// <summary>
    /// Resets the predictor state.
    /// </summary>
    public void Reset()
    {
        Array.Clear(_gapSamples, 0, MaxSamples);
        _gapSampleCount = 0;
        _gapSampleWriteIndex = 0;
        WriteRateBps = 0;
        ReadRateBps = 0;
        _lastWriteHead = 0;
        _lastReadHead = 0;
        _lastSampleTicks = 0;
        CurrentRisk = OverflowRisk.Unknown;
        SecondsToOverflow = double.NaN;
        _sampleCount = 0;
        CurrentGapPercentage = 0;
        GapTrendBps = 0;
    }

    /// <summary>
    /// Gets diagnostic information.
    /// </summary>
    /// <returns>Formatted diagnostic string.</returns>
    public string GetDiagnostics()
    {
        var overflow =
            double.IsNaN(SecondsToOverflow) || double.IsPositiveInfinity(SecondsToOverflow)
                ? "N/A"
                : $"{SecondsToOverflow:F1}s";

        return "OverflowPredictor:\n"
            + $"  Risk: {CurrentRisk}\n"
            + $"  Gap: {CurrentGapPercentage * 100:F1}%\n"
            + $"  Gap Trend: {GapTrendBps / 1024:F1} KB/s\n"
            + $"  Write Rate: {WriteRateBps / 1048576:F2} MB/s\n"
            + $"  Read Rate: {ReadRateBps / 1048576:F2} MB/s\n"
            + $"  Time to Overflow: {overflow}\n"
            + $"  Samples: {_sampleCount}";
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly struct GapSample(long ticks, long gap)
    {
        public readonly long Ticks = ticks;
        public readonly long Gap = gap;
    }
}

/// <summary>
/// Overflow risk levels.
/// </summary>
public enum OverflowRisk
{
    /// <summary>
    /// Not enough data to determine risk.
    /// </summary>
    Unknown,

    /// <summary>
    /// Gap is stable or decreasing, no overflow expected.
    /// </summary>
    Low,

    /// <summary>
    /// Gap is slightly increasing but overflow is not imminent.
    /// </summary>
    Elevated,

    /// <summary>
    /// Gap exceeds 80% or is growing faster than expected.
    /// </summary>
    Warning,

    /// <summary>
    /// Overflow predicted within 10 seconds.
    /// </summary>
    Critical,

    /// <summary>
    /// Overflow imminent (within 3 seconds) or gap > 95%.
    /// </summary>
    Imminent,
}
