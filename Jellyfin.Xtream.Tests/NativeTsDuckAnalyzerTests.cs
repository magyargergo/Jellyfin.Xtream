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
using System.Runtime.InteropServices;
using Jellyfin.Xtream.Service.MpegTs.TsDuck;
using Jellyfin.Xtream.Service.MpegTs.TsDuck.Native;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Tests for native TsDuck analyzer lifecycle and integration.
/// These tests verify the P/Invoke layer and factory fallback behavior.
/// </summary>
public sealed class NativeTsDuckAnalyzerTests
{
    #region Factory Fallback Tests

    /// <summary>
    /// Factory should return a valid analyzer instance (null analyzer if native unavailable).
    /// </summary>
    [Fact]
    public void Factory_Create_ReturnsValidAnalyzer()
    {
        using var analyzer = TsDuckAnalyzerFactory.Create();

        Assert.NotNull(analyzer);
    }

    /// <summary>
    /// Factory should return NullTsDuckAnalyzer when native library is unavailable.
    /// </summary>
    [Fact]
    public void Factory_WhenNativeUnavailable_ReturnsNullAnalyzer()
    {
        using var analyzer = TsDuckAnalyzerFactory.Create();

        // On Windows without native library, should be NullTsDuckAnalyzer
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Assert.IsType<NullTsDuckAnalyzer>(analyzer);
        }
    }

    /// <summary>
    /// Factory should accept configuration without throwing.
    /// </summary>
    [Fact]
    public void Factory_WithConfiguration_DoesNotThrow()
    {
        var config = new TsDuckConfiguration { MetricsIntervalSeconds = 5, EnableTr101290 = true };

        using var analyzer = TsDuckAnalyzerFactory.Create(config);
        Assert.NotNull(analyzer);
    }

    #endregion

    #region NullTsDuckAnalyzer Tests

    /// <summary>
    /// NullTsDuckAnalyzer should report unavailable status.
    /// </summary>
    [Fact]
    public void NullAnalyzer_IsAvailable_ReturnsFalse()
    {
        using var analyzer = new NullTsDuckAnalyzer();
        Assert.False(analyzer.IsAvailable);
    }

    /// <summary>
    /// NullTsDuckAnalyzer should report as initialized (ready to accept data, just discards it).
    /// </summary>
    [Fact]
    public void NullAnalyzer_IsInitialized_ReturnsTrue()
    {
        using var analyzer = new NullTsDuckAnalyzer();
        Assert.True(analyzer.IsInitialized);
    }

    /// <summary>
    /// NullTsDuckAnalyzer should report unavailable process status.
    /// </summary>
    [Fact]
    public void NullAnalyzer_ProcessStatus_ReturnsUnavailable()
    {
        using var analyzer = new NullTsDuckAnalyzer();
        Assert.Equal(TsDuckProcessStatus.Unavailable, analyzer.ProcessStatus);
    }

    /// <summary>
    /// NullTsDuckAnalyzer should accept data without throwing.
    /// </summary>
    [Fact]
    public void NullAnalyzer_FeedData_DoesNotThrow()
    {
        using var analyzer = new NullTsDuckAnalyzer();
        var data = new byte[188 * 10]; // 10 TS packets

        var exception = Record.Exception(() => analyzer.FeedData(data));

        Assert.Null(exception);
    }

    /// <summary>
    /// NullTsDuckAnalyzer should return null metrics.
    /// </summary>
    [Fact]
    public void NullAnalyzer_GetMetrics_ReturnsNull()
    {
        using var analyzer = new NullTsDuckAnalyzer();
        Assert.Null(analyzer.GetMetrics());
    }

    /// <summary>
    /// NullTsDuckAnalyzer reset should not throw.
    /// </summary>
    [Fact]
    public void NullAnalyzer_Reset_DoesNotThrow()
    {
        using var analyzer = new NullTsDuckAnalyzer();

        var exception = Record.Exception(() => analyzer.Reset());

        Assert.Null(exception);
    }

    /// <summary>
    /// NullTsDuckAnalyzer should be safely disposable multiple times.
    /// </summary>
    [Fact]
    public void NullAnalyzer_MultipleDispose_DoesNotThrow()
    {
        var analyzer = new NullTsDuckAnalyzer();

        var exception = Record.Exception(() =>
        {
            analyzer.Dispose();
            analyzer.Dispose();
            analyzer.Dispose();
        });

        Assert.Null(exception);
    }

    #endregion

    #region ITsDuckAnalyzer Interface Contract Tests

    /// <summary>
    /// All analyzers should implement consistent interface behavior for metrics events.
    /// </summary>
    [Fact]
    public void Analyzer_MetricsUpdatedEvent_CanSubscribeAndUnsubscribe()
    {
        using var analyzer = TsDuckAnalyzerFactory.Create();
        var handler = new EventHandler<TsDuckMetricsEventArgs>((_, _) => { });

        // Subscribe
        analyzer.MetricsUpdated += handler;

        // Feed some data
        var data = CreateValidTsPackets(10);
        analyzer.FeedData(data);

        // Unsubscribe
        analyzer.MetricsUpdated -= handler;

        // Just verify no exception
        Assert.NotNull(analyzer);
    }

    #endregion

    #region Native Structure Tests

    /// <summary>
    /// TsDuckMetricsNative structure should have correct layout size.
    /// </summary>
    [Fact]
    public void TsDuckMetricsNative_HasCorrectSize()
    {
        // TsDuckMetricsNative: 2 longs + 2 ints + Priority1 (8 longs) + Priority2 (7 longs)
        // = 16 + 8 + 64 + 56 = 144 bytes
        var size = Marshal.SizeOf<TsDuckMetricsNative>();

        Assert.Equal(144, size);
    }

    /// <summary>
    /// Tr101290Priority1Native structure should have correct layout size.
    /// </summary>
    [Fact]
    public void Tr101290Priority1Native_HasCorrectSize()
    {
        // 8 long fields = 64 bytes
        var size = Marshal.SizeOf<Tr101290Priority1Native>();

        Assert.Equal(64, size);
    }

    /// <summary>
    /// Tr101290Priority2Native structure should have correct layout size.
    /// </summary>
    [Fact]
    public void Tr101290Priority2Native_HasCorrectSize()
    {
        // 7 long fields = 56 bytes
        var size = Marshal.SizeOf<Tr101290Priority2Native>();

        Assert.Equal(56, size);
    }

    /// <summary>
    /// TsDuckConfigNative structure should have correct layout size.
    /// </summary>
    [Fact]
    public void TsDuckConfigNative_HasCorrectSize()
    {
        // 4 int fields: MetricsIntervalMs, EnableTr101290, SampleSizeBytes, Reserved
        // Layout: 4 + 4 + 4 + 4 = 16 bytes
        var size = Marshal.SizeOf<TsDuckConfigNative>();

        Assert.Equal(16, size);
    }

    /// <summary>
    /// PcrAnalysisNative structure should have correct layout size.
    /// </summary>
    [Fact]
    public void PcrAnalysisNative_HasCorrectSize()
    {
        // 3 doubles + 1 long + 1 double + 1 double + 2 longs = 8*3 + 8 + 8 + 8 + 16 = 64 bytes
        var size = Marshal.SizeOf<PcrAnalysisNative>();

        Assert.Equal(64, size);
    }

    /// <summary>
    /// IatAnalysisNative structure should have correct layout size.
    /// </summary>
    [Fact]
    public void IatAnalysisNative_HasCorrectSize()
    {
        // 5 doubles + 3 longs = 40 + 24 = 64 bytes
        var size = Marshal.SizeOf<IatAnalysisNative>();

        Assert.Equal(64, size);
    }

    /// <summary>
    /// BitrateAnalysisNative structure should have correct layout size.
    /// </summary>
    [Fact]
    public void BitrateAnalysisNative_HasCorrectSize()
    {
        // 3 longs + 1 double + 1 long + 1 double + 1 long = 24 + 8 + 8 + 8 + 8 = 56 bytes
        var size = Marshal.SizeOf<BitrateAnalysisNative>();

        Assert.Equal(56, size);
    }

    /// <summary>
    /// TsDuckPidInfoExtendedNative structure should have correct layout size.
    /// </summary>
    [Fact]
    public void TsDuckPidInfoExtendedNative_HasCorrectSize()
    {
        // 2 ints + 5 longs + 2 ints + 1 double + 2 ints
        // = 8 + 40 + 8 + 8 + 8 = 72 bytes
        var size = Marshal.SizeOf<TsDuckPidInfoExtendedNative>();

        Assert.Equal(72, size);
    }

    #endregion

    #region Native Structure Conversion Tests

    /// <summary>
    /// Tr101290Priority1Native.ToManaged should correctly convert all fields.
    /// </summary>
    [Fact]
    public void Tr101290Priority1Native_ToManaged_ConvertsAllFields()
    {
        var native = new Tr101290Priority1Native
        {
            SyncByteError = 1,
            SyncLoss = 2,
            PatError = 3,
            PatError2 = 4,
            ContinuityCountError = 5,
            PmtError = 6,
            PmtError2 = 7,
            PidError = 8,
        };

        var managed = native.ToManaged();

        Assert.Equal(1, managed.SyncByteError);
        Assert.Equal(2, managed.SyncLoss);
        Assert.Equal(3, managed.PatError);
        Assert.Equal(4, managed.PatError2);
        Assert.Equal(5, managed.ContinuityCountError);
        Assert.Equal(6, managed.PmtError);
        Assert.Equal(7, managed.PmtError2);
        Assert.Equal(8, managed.PidError);
    }

    /// <summary>
    /// Tr101290Priority2Native.ToManaged should correctly convert all fields.
    /// </summary>
    [Fact]
    public void Tr101290Priority2Native_ToManaged_ConvertsAllFields()
    {
        var native = new Tr101290Priority2Native
        {
            TransportError = 10,
            CrcError = 20,
            PcrRepetitionError = 30,
            PcrDiscontinuityError = 40,
            PcrAccuracyError = 50,
            PtsError = 60,
            CatError = 70,
        };

        var managed = native.ToManaged();

        Assert.Equal(10, managed.TransportError);
        Assert.Equal(20, managed.CrcError);
        Assert.Equal(30, managed.PcrRepetitionError);
        Assert.Equal(40, managed.PcrDiscontinuityError);
        Assert.Equal(50, managed.PcrAccuracyError);
        Assert.Equal(60, managed.PtsError);
        Assert.Equal(70, managed.CatError);
    }

    /// <summary>
    /// PcrAnalysisNative.ToManaged should correctly convert all fields.
    /// </summary>
    [Fact]
    public void PcrAnalysisNative_ToManaged_ConvertsAllFields()
    {
        var native = new PcrAnalysisNative
        {
            PcrJitterUs = 0.5,
            PcrJitterMaxUs = 1.0,
            PcrJitterAvgUs = 0.3,
            PcrIntervalPackets = 1000,
            PcrIntervalMs = 40.0,
            PcrDriftPpm = 5.5,
            PcrCount = 100,
            PcrValidCount = 98,
        };

        var managed = native.ToManaged();

        Assert.Equal(0.5, managed.PcrJitterUs);
        Assert.Equal(1.0, managed.PcrJitterMaxUs);
        Assert.Equal(0.3, managed.PcrJitterAvgUs);
        Assert.Equal(1000, managed.PcrIntervalPackets);
        Assert.Equal(40.0, managed.PcrIntervalMs);
        Assert.Equal(5.5, managed.PcrDriftPpm);
        Assert.Equal(100, managed.PcrCount);
        Assert.Equal(98, managed.PcrValidCount);
    }

    /// <summary>
    /// IatAnalysisNative.ToManaged should correctly convert all fields.
    /// </summary>
    [Fact]
    public void IatAnalysisNative_ToManaged_ConvertsAllFields()
    {
        var native = new IatAnalysisNative
        {
            IatAvgUs = 100.0,
            IatMinUs = 50.0,
            IatMaxUs = 200.0,
            IatJitterUs = 75.0,
            IatStddevUs = 25.0,
            LatePackets = 10,
            EarlyPackets = 5,
            BurstCount = 3,
        };

        var managed = native.ToManaged();

        Assert.Equal(100.0, managed.IatAvgUs);
        Assert.Equal(50.0, managed.IatMinUs);
        Assert.Equal(200.0, managed.IatMaxUs);
        Assert.Equal(75.0, managed.IatJitterUs);
        Assert.Equal(25.0, managed.IatStddevUs);
        Assert.Equal(10, managed.LatePackets);
        Assert.Equal(5, managed.EarlyPackets);
        Assert.Equal(3, managed.BurstCount);
    }

    /// <summary>
    /// BitrateAnalysisNative.ToManaged should correctly convert all fields.
    /// </summary>
    [Fact]
    public void BitrateAnalysisNative_ToManaged_ConvertsAllFields()
    {
        var native = new BitrateAnalysisNative
        {
            TsBitrateNominal = 5000000,
            TsBitratePcr = 4900000,
            TsBitrateDts = 4950000,
            BitrateAccuracy = 0.98,
            NullPacketBitrate = 100000,
            NullPacketRatio = 0.02,
            UsefulBitrate = 4800000,
        };

        var managed = native.ToManaged();

        Assert.Equal(5000000, managed.TsBitrateNominal);
        Assert.Equal(4900000, managed.TsBitratePcr);
        Assert.Equal(4950000, managed.TsBitrateDts);
        Assert.Equal(0.98, managed.BitrateAccuracy);
        Assert.Equal(100000, managed.NullPacketBitrate);
        Assert.Equal(0.02, managed.NullPacketRatio);
        Assert.Equal(4800000, managed.UsefulBitrate);
    }

    /// <summary>
    /// TsDuckPidInfoExtendedNative.ToManaged should correctly convert all fields.
    /// </summary>
    [Fact]
    public void TsDuckPidInfoExtendedNative_ToManaged_ConvertsAllFields()
    {
        var native = new TsDuckPidInfoExtendedNative
        {
            Pid = 256,
            StreamType = 0x1B, // H.264 video
            Packets = 10000,
            Bitrate = 3000000,
            ContinuityErrors = 2,
            DuplicatePackets = 1,
            ScrambledPackets = 0,
            IsScrambled = 0,
            IsPcrPid = 1,
            PcrJitterUs = 0.3,
            IsVideo = 1,
            IsAudio = 0,
        };

        var managed = native.ToManaged();

        Assert.Equal(256, managed.Pid);
        Assert.Equal(0x1B, managed.StreamType);
        Assert.Equal(10000, managed.Packets);
        Assert.Equal(3000000, managed.Bitrate);
        Assert.Equal(2, managed.ContinuityErrors);
        Assert.Equal(1, managed.DuplicatePackets);
        Assert.Equal(0, managed.ScrambledPackets);
        Assert.False(managed.IsScrambled);
        Assert.True(managed.IsPcrPid);
        Assert.Equal(0.3, managed.PcrJitterUs);
        Assert.True(managed.IsVideo);
        Assert.False(managed.IsAudio);
    }

    #endregion

    #region Metrics Staleness Tests

    /// <summary>
    /// TsDuckMetrics.Age should return correct duration since creation.
    /// </summary>
    [Fact]
    public void TsDuckMetrics_Age_ReturnsCorrectDuration()
    {
        var metrics = new TsDuckMetrics { Timestamp = DateTime.UtcNow.AddSeconds(-3) };

        Assert.True(metrics.Age.TotalSeconds >= 3);
        Assert.True(metrics.Age.TotalSeconds < 5); // Allow some tolerance
    }

    /// <summary>
    /// TsDuckMetrics.IsStale should return true for old metrics.
    /// </summary>
    [Fact]
    public void TsDuckMetrics_IsStale_TrueWhenOlderThanThreshold()
    {
        var staleMetrics = new TsDuckMetrics { Timestamp = DateTime.UtcNow.AddSeconds(-10) };
        var freshMetrics = new TsDuckMetrics { Timestamp = DateTime.UtcNow.AddSeconds(-1) };

        Assert.True(staleMetrics.IsStale);
        Assert.False(freshMetrics.IsStale);
    }

    /// <summary>
    /// NullTsDuckAnalyzer.MetricsAge should return null.
    /// </summary>
    [Fact]
    public void NullAnalyzer_MetricsAge_ReturnsNull()
    {
        using var analyzer = new NullTsDuckAnalyzer();
        Assert.Null(analyzer.MetricsAge);
    }

    /// <summary>
    /// NullTsDuckAnalyzer.HasFreshMetrics should return false.
    /// </summary>
    [Fact]
    public void NullAnalyzer_HasFreshMetrics_ReturnsFalse()
    {
        using var analyzer = new NullTsDuckAnalyzer();
        Assert.False(analyzer.HasFreshMetrics);
    }

    #endregion

    #region Quality Score Caching Tests

    /// <summary>
    /// CalculateQualityScore should return consistent cached value.
    /// </summary>
    [Fact]
    public void TsDuckMetrics_CalculateQualityScore_ReturnsCachedValue()
    {
        var metrics = new TsDuckMetrics
        {
            Priority1 = new Tr101290Priority1(0, 0, 0, 0, 0, 0, 0, 0),
            Priority2 = new Tr101290Priority2(0, 0, 0, 0, 0, 0, 0),
        };

        var score1 = metrics.CalculateQualityScore();
        var score2 = metrics.CalculateQualityScore();

        Assert.Equal(100, score1);
        Assert.Equal(score1, score2);
    }

    /// <summary>
    /// CalculateQualityScoreEnhanced should return consistent cached value.
    /// </summary>
    [Fact]
    public void TsDuckMetrics_CalculateQualityScoreEnhanced_ReturnsCachedValue()
    {
        var metrics = new TsDuckMetrics
        {
            Priority1 = new Tr101290Priority1(0, 0, 0, 0, 0, 0, 0, 0),
            Priority2 = new Tr101290Priority2(0, 0, 0, 0, 0, 0, 0),
        };

        var score1 = metrics.CalculateQualityScoreEnhanced();
        var score2 = metrics.CalculateQualityScoreEnhanced();

        Assert.Equal(100, score1);
        Assert.Equal(score1, score2);
    }

    /// <summary>
    /// Quality score should apply correct penalties for Priority 1 errors.
    /// </summary>
    [Fact]
    public void TsDuckMetrics_CalculateQualityScore_AppliesPriority1Penalties()
    {
        var metrics = new TsDuckMetrics
        {
            Priority1 = new Tr101290Priority1(
                SyncByteError: 1,
                SyncLoss: 1,
                PatError: 0,
                PatError2: 0,
                ContinuityCountError: 0,
                PmtError: 0,
                PmtError2: 0,
                PidError: 0
            ),
            Priority2 = new Tr101290Priority2(0, 0, 0, 0, 0, 0, 0),
        };

        var score = metrics.CalculateQualityScore();

        // SyncLoss = -50, SyncByteError = -30, so 100 - 50 - 30 = 20
        Assert.Equal(20, score);
    }

    #endregion

    #region Quality Score Tests

    /// <summary>
    /// PcrAnalysis should detect jitter violations correctly.
    /// </summary>
    [Fact]
    public void PcrAnalysis_HasJitterViolation_DetectsHighJitter()
    {
        var lowJitter = new PcrAnalysis(0.3, 0.5, 0.4, 1000, 40, 0, 100, 100);
        var highJitter = new PcrAnalysis(600, 700, 650, 1000, 40, 0, 100, 100);

        Assert.False(lowJitter.HasJitterViolation);
        Assert.True(highJitter.HasJitterViolation);
    }

    /// <summary>
    /// PcrAnalysis should detect interval violations correctly.
    /// </summary>
    [Fact]
    public void PcrAnalysis_HasIntervalViolation_DetectsLongInterval()
    {
        var normalInterval = new PcrAnalysis(0.3, 0.5, 0.4, 1000, 40, 0, 100, 100);
        var longInterval = new PcrAnalysis(0.3, 0.5, 0.4, 1000, 150, 0, 100, 100);

        Assert.False(normalInterval.HasIntervalViolation);
        Assert.True(longInterval.HasIntervalViolation);
    }

    /// <summary>
    /// IatAnalysis should detect high jitter correctly.
    /// </summary>
    [Fact]
    public void IatAnalysis_HasHighJitter_DetectsHighJitter()
    {
        var lowJitter = new IatAnalysis(100, 50, 200, 500, 100, 0, 0, 0);
        var highJitter = new IatAnalysis(100, 50, 200, 1500, 500, 10, 5, 3);

        Assert.False(lowJitter.HasHighJitter);
        Assert.True(highJitter.HasHighJitter);
    }

    /// <summary>
    /// IatAnalysis should detect packet bursting correctly.
    /// </summary>
    [Fact]
    public void IatAnalysis_HasBursting_DetectsBurstCount()
    {
        var noBursting = new IatAnalysis(100, 50, 200, 500, 100, 0, 0, 5);
        var hasBursting = new IatAnalysis(100, 50, 200, 500, 100, 0, 0, 15);

        Assert.False(noBursting.HasBursting);
        Assert.True(hasBursting.HasBursting);
    }

    #endregion

    #region Metrics History Tests

    /// <summary>
    /// MetricsHistory should store and retrieve metrics correctly.
    /// </summary>
    [Fact]
    public void MetricsHistory_Add_StoresMetrics()
    {
        var history = new TsDuckMetricsHistory();
        var metrics = new TsDuckMetrics { TsBitrate = 5000000 };

        history.Add(metrics);

        Assert.Equal(1, history.Count);
        Assert.Equal(metrics, history.GetLatest());
    }

    /// <summary>
    /// MetricsHistory should wrap around when capacity is reached.
    /// </summary>
    [Fact]
    public void MetricsHistory_Add_WrapsAtCapacity()
    {
        var history = new TsDuckMetricsHistory(5);

        for (var i = 0; i < 10; i++)
        {
            history.Add(new TsDuckMetrics { TsBitrate = i * 1000000 });
        }

        Assert.Equal(5, history.Count);
        // Latest should be the last added (bitrate = 9M)
        Assert.Equal(9000000, history.GetLatest()!.TsBitrate);
    }

    /// <summary>
    /// MetricsHistory.GetWindow should return metrics within time window.
    /// </summary>
    [Fact]
    public void MetricsHistory_GetWindow_ReturnsRecentMetrics()
    {
        var history = new TsDuckMetricsHistory();
        var now = DateTime.UtcNow;

        // Add metrics at different timestamps
        history.Add(new TsDuckMetrics { Timestamp = now.AddSeconds(-60), TsBitrate = 1000000 });
        history.Add(new TsDuckMetrics { Timestamp = now.AddSeconds(-30), TsBitrate = 2000000 });
        history.Add(new TsDuckMetrics { Timestamp = now.AddSeconds(-10), TsBitrate = 3000000 });
        history.Add(new TsDuckMetrics { Timestamp = now, TsBitrate = 4000000 });

        var window = history.GetWindow(TimeSpan.FromSeconds(20));

        Assert.Equal(2, window.Count);
        Assert.Equal(3000000, window[0].TsBitrate);
        Assert.Equal(4000000, window[1].TsBitrate);
    }

    /// <summary>
    /// MetricsHistory.Clear should reset the history.
    /// </summary>
    [Fact]
    public void MetricsHistory_Clear_ResetsState()
    {
        var history = new TsDuckMetricsHistory();
        history.Add(new TsDuckMetrics());
        history.Add(new TsDuckMetrics());

        history.Clear();

        Assert.Equal(0, history.Count);
        Assert.Null(history.GetLatest());
    }

    /// <summary>
    /// MetricsHistory.CalculateErrorRate should return null with insufficient data.
    /// </summary>
    [Fact]
    public void MetricsHistory_CalculateErrorRate_NullWithInsufficientData()
    {
        var history = new TsDuckMetricsHistory();
        history.Add(new TsDuckMetrics());

        var stats = history.CalculateErrorRate(TimeSpan.FromSeconds(30));

        Assert.Null(stats);
    }

    /// <summary>
    /// MetricsHistory.CalculateErrorRate should compute correct error rate.
    /// </summary>
    [Fact]
    public void MetricsHistory_CalculateErrorRate_ComputesCorrectRate()
    {
        var history = new TsDuckMetricsHistory();
        var now = DateTime.UtcNow;

        // First snapshot: 0 errors
        history.Add(
            new TsDuckMetrics
            {
                Timestamp = now.AddSeconds(-10),
                Priority1 = new Tr101290Priority1(0, 0, 0, 0, 0, 0, 0, 0),
                Priority2 = new Tr101290Priority2(0, 0, 0, 0, 0, 0, 0),
            }
        );

        // Second snapshot: 10 P1 errors, 20 P2 errors after 10 seconds
        history.Add(
            new TsDuckMetrics
            {
                Timestamp = now,
                Priority1 = new Tr101290Priority1(5, 0, 0, 0, 5, 0, 0, 0), // 10 total
                Priority2 = new Tr101290Priority2(10, 5, 0, 0, 0, 5, 0), // 20 total
            }
        );

        var stats = history.CalculateErrorRate(TimeSpan.FromSeconds(30));

        Assert.NotNull(stats);
        Assert.Equal(1.0, stats.Value.Priority1ErrorsPerSecond, 1); // 10 errors / 10 seconds
        Assert.Equal(2.0, stats.Value.Priority2ErrorsPerSecond, 1); // 20 errors / 10 seconds
    }

    /// <summary>
    /// ErrorRateStatistics.IsHealthy should detect high error rates.
    /// </summary>
    [Fact]
    public void ErrorRateStatistics_IsHealthy_DetectsHighErrorRate()
    {
        var healthy = new ErrorRateStatistics(0.05, 0.1, 0.15, 30, 10, 95, 90, 100);
        var unhealthy = new ErrorRateStatistics(0.5, 1.0, 1.5, 30, 10, 50, 40, 60);

        Assert.True(healthy.IsHealthy);
        Assert.False(unhealthy.IsHealthy);
    }

    /// <summary>
    /// MetricsHistory.CalculateQualityTrend should detect degrading quality.
    /// </summary>
    [Fact]
    public void MetricsHistory_CalculateQualityTrend_DetectsDegradation()
    {
        var history = new TsDuckMetricsHistory();
        var now = DateTime.UtcNow;

        // Add metrics showing declining quality (increasing errors)
        for (var i = 0; i < 5; i++)
        {
            history.Add(
                new TsDuckMetrics
                {
                    Timestamp = now.AddSeconds(-20 + (i * 5)),
                    // Increasing sync byte errors cause declining quality score
                    Priority1 = new Tr101290Priority1(i * 10, 0, 0, 0, 0, 0, 0, 0),
                    Priority2 = new Tr101290Priority2(0, 0, 0, 0, 0, 0, 0),
                }
            );
        }

        var trend = history.CalculateQualityTrend(TimeSpan.FromSeconds(30));

        // Negative trend indicates degrading quality
        Assert.True(trend < 0);
    }

    #endregion

    #region Helper Methods

    private static byte[] CreateValidTsPackets(int count)
    {
        const int packetSize = 188;
        const byte syncByte = 0x47;
        var data = new byte[packetSize * count];

        for (int i = 0; i < count; i++)
        {
            int offset = i * packetSize;
            data[offset] = syncByte;
            // Set PID to null packet (0x1FFF)
            data[offset + 1] = 0x1F;
            data[offset + 2] = 0xFF;
            // Set adaptation field control to payload only
            data[offset + 3] = 0x10;
        }

        return data;
    }

    #endregion
}
