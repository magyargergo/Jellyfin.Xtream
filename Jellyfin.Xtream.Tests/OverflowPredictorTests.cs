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
using System.Threading;
using Jellyfin.Xtream.Service;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Tests for <see cref="OverflowPredictor"/>.
/// Validates EMA-based rate tracking, risk level determination, and gap trend detection.
/// </summary>
public sealed class OverflowPredictorTests
{
    private const int BufferSize = 1_000_000; // 1MB
    #region Initial State

    /// <summary>
    /// Verifies first sample returns Low risk (initialization only).
    /// </summary>
    [Fact]
    public void RecordSample_FirstSample_ReturnsLowRisk()
    {
        var predictor = new OverflowPredictor(BufferSize);

        var risk = predictor.RecordSample(0, 0);

        Assert.Equal(OverflowRisk.Low, risk);
        Assert.True(double.IsNaN(predictor.SecondsToOverflow));
    }

    #endregion

    #region Unknown Risk (Insufficient Samples)

    /// <summary>
    /// Verifies Unknown risk when fewer than 5 samples collected.
    /// </summary>
    [Fact]
    public void RecordSample_FewSamples_ReturnsUnknown()
    {
        var predictor = new OverflowPredictor(BufferSize);

        // Record a few samples with small delays to ensure elapsed > 10ms
        predictor.RecordSample(0, 0);
        Thread.Sleep(20);
        predictor.RecordSample(10000, 5000);
        Thread.Sleep(20);
        var risk = predictor.RecordSample(20000, 10000);

        // Should be Unknown until we have 5+ data points
        Assert.Equal(OverflowRisk.Unknown, risk);
    }

    #endregion

    #region Low Risk

    /// <summary>
    /// Verifies Low or Elevated risk when reader keeps up with writer.
    /// Timing jitter can cause the gap trend to be slightly positive,
    /// resulting in Elevated rather than Low.
    /// </summary>
    [Fact]
    public void RecordSample_ReaderKeepsUp_ReturnsLowOrElevated()
    {
        var predictor = new OverflowPredictor(BufferSize);

        // Record enough samples where reader keeps pace with writer
        for (var i = 0; i < 8; i++)
        {
            var writeHead = (long)i * 10000;
            var readHead = writeHead - 1000; // Small constant gap
            predictor.RecordSample(writeHead, readHead);
            Thread.Sleep(20);
        }

        Assert.True(
            predictor.CurrentRisk <= OverflowRisk.Elevated,
            $"Expected Low or Elevated, got {predictor.CurrentRisk}"
        );
        Assert.True(
            double.IsPositiveInfinity(predictor.SecondsToOverflow) || predictor.SecondsToOverflow > 10,
            $"Expected large SecondsToOverflow, got {predictor.SecondsToOverflow}"
        );
    }

    #endregion

    #region Rate Tracking

    /// <summary>
    /// Verifies write and read rates are tracked.
    /// </summary>
    [Fact]
    public void RecordSample_TracksRates()
    {
        var predictor = new OverflowPredictor(BufferSize);

        predictor.RecordSample(0, 0);
        Thread.Sleep(50);
        predictor.RecordSample(50000, 40000);

        Assert.True(predictor.WriteRateBps > 0, "WriteRateBps should be positive");
        Assert.True(predictor.ReadRateBps > 0, "ReadRateBps should be positive");
    }

    /// <summary>
    /// Verifies gap percentage is calculated correctly.
    /// </summary>
    [Fact]
    public void RecordSample_CalculatesGapPercentage()
    {
        var predictor = new OverflowPredictor(BufferSize);

        // Record enough samples to get past Unknown phase
        for (var i = 0; i < 6; i++)
        {
            var writeHead = (long)(i + 1) * 100000;
            var readHead = writeHead - 500000; // 50% gap
            predictor.RecordSample(writeHead, readHead);
            Thread.Sleep(20);
        }

        // Gap should be approximately 50%
        Assert.True(
            predictor.CurrentGapPercentage > 0.3 && predictor.CurrentGapPercentage < 0.7,
            $"Expected ~50% gap, got {predictor.CurrentGapPercentage * 100:F1}%"
        );
    }

    #endregion

    #region High Risk Levels

    /// <summary>
    /// Verifies Imminent risk when gap exceeds 95% of buffer.
    /// </summary>
    [Fact]
    public void RecordSample_GapExceeds95Percent_ReturnsImminent()
    {
        var predictor = new OverflowPredictor(BufferSize);

        // Record samples with growing gap that reaches 96% of buffer
        for (var i = 0; i < 8; i++)
        {
            var writeHead = (long)(i + 1) * 120000;
            var readHead = writeHead - 960000; // 96% gap
            predictor.RecordSample(writeHead, readHead);
            Thread.Sleep(20);
        }

        Assert.Equal(OverflowRisk.Imminent, predictor.CurrentRisk);
    }

    /// <summary>
    /// Verifies Warning risk when gap exceeds 80% of buffer.
    /// </summary>
    [Fact]
    public void RecordSample_GapExceeds80Percent_ReturnsWarningOrHigher()
    {
        var predictor = new OverflowPredictor(BufferSize);

        // Record samples with gap at ~85%
        for (var i = 0; i < 8; i++)
        {
            var writeHead = (long)(i + 1) * 106250;
            var readHead = writeHead - 850000; // 85% gap
            predictor.RecordSample(writeHead, readHead);
            Thread.Sleep(20);
        }

        Assert.True(
            predictor.CurrentRisk >= OverflowRisk.Warning,
            $"Expected Warning or higher, got {predictor.CurrentRisk}"
        );
    }

    #endregion

    #region Reset

    /// <summary>
    /// Verifies Reset clears all predictor state.
    /// </summary>
    [Fact]
    public void Reset_ClearsAllState()
    {
        var predictor = new OverflowPredictor(BufferSize);

        // Build up some state
        predictor.RecordSample(0, 0);
        Thread.Sleep(20);
        predictor.RecordSample(100000, 50000);

        predictor.Reset();

        Assert.Equal(OverflowRisk.Unknown, predictor.CurrentRisk);
        Assert.True(double.IsNaN(predictor.SecondsToOverflow));
        Assert.Equal(0.0, predictor.WriteRateBps);
        Assert.Equal(0.0, predictor.ReadRateBps);
        Assert.Equal(0.0, predictor.CurrentGapPercentage);
        Assert.Equal(0.0, predictor.GapTrendBps);
    }

    /// <summary>
    /// Verifies predictor works correctly after Reset.
    /// </summary>
    [Fact]
    public void Reset_ThenRecordSample_WorksCorrectly()
    {
        var predictor = new OverflowPredictor(BufferSize);

        predictor.RecordSample(0, 0);
        Thread.Sleep(20);
        predictor.RecordSample(100000, 50000);

        predictor.Reset();

        // Should behave like a fresh predictor
        var risk = predictor.RecordSample(0, 0);
        Assert.Equal(OverflowRisk.Low, risk);
    }

    #endregion

    #region GetDiagnostics

    /// <summary>
    /// Verifies GetDiagnostics returns formatted string.
    /// </summary>
    [Fact]
    public void GetDiagnostics_ReturnsFormattedString()
    {
        var predictor = new OverflowPredictor(BufferSize);

        predictor.RecordSample(0, 0);

        var diagnostics = predictor.GetDiagnostics();

        Assert.Contains("OverflowPredictor:", diagnostics);
        Assert.Contains("Risk:", diagnostics);
        Assert.Contains("Gap:", diagnostics);
        Assert.Contains("Write Rate:", diagnostics);
        Assert.Contains("Read Rate:", diagnostics);
    }

    #endregion
}
