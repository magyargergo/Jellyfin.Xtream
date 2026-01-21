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

namespace Jellyfin.Xtream.Service.MpegTs.TsDuck.Native;

/// <summary>
/// Native TR 101 290 Priority 1 structure.
/// Layout must match Tr101290Priority1Native in tsduck_interop.h exactly.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Tr101290Priority1Native
{
    public long SyncByteError;
    public long SyncLoss;
    public long PatError;
    public long PatError2;
    public long ContinuityCountError;
    public long PmtError;
    public long PmtError2;
    public long PidError;

    /// <summary>
    /// Converts to managed Tr101290Priority1.
    /// </summary>
    public readonly Tr101290Priority1 ToManaged() =>
        new(SyncByteError, SyncLoss, PatError, PatError2, ContinuityCountError, PmtError, PmtError2, PidError);
}

/// <summary>
/// Native TR 101 290 Priority 2 structure.
/// Layout must match Tr101290Priority2Native in tsduck_interop.h exactly.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Tr101290Priority2Native
{
    public long TransportError;
    public long CrcError;
    public long PcrRepetitionError;
    public long PcrDiscontinuityError;
    public long PcrAccuracyError;
    public long PtsError;
    public long CatError;

    /// <summary>
    /// Converts to managed Tr101290Priority2.
    /// </summary>
    public readonly Tr101290Priority2 ToManaged() =>
        new(TransportError, CrcError, PcrRepetitionError, PcrDiscontinuityError, PcrAccuracyError, PtsError, CatError);
}

/// <summary>
/// Native metrics structure.
/// Layout must match TsDuckMetricsNative in tsduck_interop.h exactly.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TsDuckMetricsNative
{
    public long TimestampTicks;
    public long TsBitrate;
    public int ServiceCount;
    public int PidCount;
    public Tr101290Priority1Native Priority1;
    public Tr101290Priority2Native Priority2;

    /// <summary>
    /// Converts to managed TsDuckMetrics.
    /// </summary>
    public readonly TsDuckMetrics ToManaged() =>
        new()
        {
            Timestamp = new DateTime(TimestampTicks, DateTimeKind.Utc),
            TsBitrate = TsBitrate,
            ServiceCount = ServiceCount,
            PidCount = PidCount,
            Priority1 = Priority1.ToManaged(),
            Priority2 = Priority2.ToManaged(),
            Services = [],
            Pids = [],
        };
}

/// <summary>
/// Native configuration structure.
/// Layout must match TsDuckConfigNative in tsduck_interop.h exactly.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct TsDuckConfigNative
{
    public readonly int MetricsIntervalMs;
    public readonly int EnableTr101290;
    public readonly int SampleSizeBytes;
    public readonly int Reserved;

    /// <summary>
    /// Initializes a new instance of the <see cref="TsDuckConfigNative"/> struct.
    /// </summary>
    private TsDuckConfigNative(int metricsIntervalMs, int enableTr101290, int sampleSizeBytes, int reserved)
    {
        MetricsIntervalMs = metricsIntervalMs;
        EnableTr101290 = enableTr101290;
        SampleSizeBytes = sampleSizeBytes;
        Reserved = reserved;
    }

    /// <summary>
    /// Creates native config from managed configuration.
    /// </summary>
    public static TsDuckConfigNative FromManaged(TsDuckConfiguration config) =>
        new(config.MetricsIntervalSeconds * 1000, config.EnableTr101290 ? 1 : 0, config.SampleSizeBytes, 0);
}

/// <summary>
/// Native PCR analysis structure.
/// Layout must match PcrAnalysisNative in tsduck_interop.h exactly.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PcrAnalysisNative
{
    public double PcrJitterUs;
    public double PcrJitterMaxUs;
    public double PcrJitterAvgUs;
    public long PcrIntervalPackets;
    public double PcrIntervalMs;
    public double PcrDriftPpm;
    public long PcrCount;
    public long PcrValidCount;

    /// <summary>
    /// Converts to managed PcrAnalysis.
    /// </summary>
    public readonly PcrAnalysis ToManaged() =>
        new(
            PcrJitterUs,
            PcrJitterMaxUs,
            PcrJitterAvgUs,
            PcrIntervalPackets,
            PcrIntervalMs,
            PcrDriftPpm,
            PcrCount,
            PcrValidCount
        );
}

/// <summary>
/// Native IAT (Inter-packet Arrival Time) analysis structure.
/// Layout must match IatAnalysisNative in tsduck_interop.h exactly.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct IatAnalysisNative
{
    public double IatAvgUs;
    public double IatMinUs;
    public double IatMaxUs;
    public double IatJitterUs;
    public double IatStddevUs;
    public long LatePackets;
    public long EarlyPackets;
    public long BurstCount;

    /// <summary>
    /// Converts to managed IatAnalysis.
    /// </summary>
    public readonly IatAnalysis ToManaged() =>
        new(IatAvgUs, IatMinUs, IatMaxUs, IatJitterUs, IatStddevUs, LatePackets, EarlyPackets, BurstCount);
}

/// <summary>
/// Native bitrate analysis structure.
/// Layout must match BitrateAnalysisNative in tsduck_interop.h exactly.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BitrateAnalysisNative
{
    public long TsBitrateNominal;
    public long TsBitratePcr;
    public long TsBitrateDts;
    public double BitrateAccuracy;
    public long NullPacketBitrate;
    public double NullPacketRatio;
    public long UsefulBitrate;

    /// <summary>
    /// Converts to managed BitrateAnalysis.
    /// </summary>
    public readonly BitrateAnalysis ToManaged() =>
        new(
            TsBitrateNominal,
            TsBitratePcr,
            TsBitrateDts,
            BitrateAccuracy,
            NullPacketBitrate,
            NullPacketRatio,
            UsefulBitrate
        );
}

/// <summary>
/// Native extended PID information structure (Phase 2b).
/// Layout must match TsDuckPidInfoExtended in tsduck_interop.h exactly.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TsDuckPidInfoExtendedNative
{
    public int Pid;
    public int StreamType;
    public long Packets;
    public long Bitrate;
    public long ContinuityErrors;
    public long DuplicatePackets;
    public long ScrambledPackets;
    public int IsScrambled;
    public int IsPcrPid;
    public double PcrJitterUs;
    public int IsVideo;
    public int IsAudio;

    /// <summary>
    /// Converts to managed TsDuckPidInfoExtended.
    /// </summary>
    public readonly TsDuckPidInfoExtended ToManaged() =>
        new(
            Pid,
            StreamType,
            Packets,
            Bitrate,
            ContinuityErrors,
            DuplicatePackets,
            ScrambledPackets,
            IsScrambled != 0,
            IsPcrPid != 0,
            PcrJitterUs,
            IsVideo != 0,
            IsAudio != 0
        );
}

/// <summary>
/// Error codes returned by native functions.
/// </summary>
internal enum TsDuckNativeError
{
    Internal = -5,
    InvalidData = -4,
    NotInitialized = -3,
    InvalidConfig = -2,
    NullHandle = -1,
    Ok = 0,
}

// =============================================================================
// A/V Sync Analysis Structures (Phase 3 - Restamping)
// =============================================================================

/// <summary>
/// A/V synchronization status.
/// </summary>
public enum AvSyncStatus
{
    /// <summary>Not enough data to determine sync status.</summary>
    Unknown = 0,

    /// <summary>Audio and video are synchronized within tolerance (±20ms).</summary>
    Synchronized = 1,

    /// <summary>Drift detected but within correctable range.</summary>
    Drifting = 2,

    /// <summary>Severe desync detected (>100ms).</summary>
    Desync = 3,

    /// <summary>No audio PTS detected in stream.</summary>
    NoAudio = 4,

    /// <summary>No video PTS detected in stream.</summary>
    NoVideo = 5,
}

/// <summary>
/// Native PTS/DTS sample structure.
/// Layout must match PtsDtsSampleNative in tsduck_interop.h exactly.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PtsDtsSampleNative
{
    public int Pid;
    public int StreamType;
    public long Pts90Khz;
    public long Dts90Khz;
    public long Pcr90Khz;
    public long PacketIndex;
    public long ByteOffset;
    public int IsVideo;
    public int IsAudio;
    public int IsKeyframe;
    public int Reserved;

    /// <summary>
    /// Converts to managed PtsDtsSample.
    /// </summary>
    public readonly PtsDtsSample ToManaged() =>
        new(
            Pid,
            StreamType,
            Pts90Khz,
            Dts90Khz,
            Pcr90Khz,
            PacketIndex,
            ByteOffset,
            IsVideo != 0,
            IsAudio != 0,
            IsKeyframe != 0
        );
}

/// <summary>
/// Native A/V synchronization analysis structure.
/// Layout must match AvSyncAnalysisNative in tsduck_interop.h exactly.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AvSyncAnalysisNative
{
    // Current drift state
    public double VideoAudioDriftMs;
    public double DriftRateMsPerSec;
    public double PeakDriftMs;
    public double AvgDriftMs;

    // PCR-PTS relationship
    public double PcrVideoOffsetMs;
    public double PcrAudioOffsetMs;

    // Sample counts
    public long VideoPtsCount;
    public long AudioPtsCount;
    public long PcrCount;

    // Discontinuity tracking
    public long VideoDiscontinuities;
    public long AudioDiscontinuities;
    public long PcrDiscontinuities;

    // Timing
    public long LastVideoPts;
    public long LastAudioPts;
    public long LastPcr;

    // Status
    public int SyncStatus;
    public int Reserved;

    /// <summary>
    /// Converts to managed AvSyncAnalysis.
    /// </summary>
    public readonly AvSyncAnalysis ToManaged() =>
        new(
            VideoAudioDriftMs,
            DriftRateMsPerSec,
            PeakDriftMs,
            AvgDriftMs,
            PcrVideoOffsetMs,
            PcrAudioOffsetMs,
            VideoPtsCount,
            AudioPtsCount,
            PcrCount,
            VideoDiscontinuities,
            AudioDiscontinuities,
            PcrDiscontinuities,
            LastVideoPts,
            LastAudioPts,
            LastPcr,
            (AvSyncStatus)SyncStatus
        );
}

/// <summary>
/// Restamping mode.
/// </summary>
public enum RestampingMode
{
    /// <summary>Restamping disabled.</summary>
    Disabled = 0,

    /// <summary>Monitor and detect drift but don't correct.</summary>
    Monitor = 1,

    /// <summary>Detect drift and apply corrections.</summary>
    Correct = 2,
}

/// <summary>
/// Native restamping configuration structure.
/// Layout must match RestampingConfigNative in tsduck_interop.h exactly.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct RestampingConfigNative
{
    public readonly int Mode;
    public readonly int SmoothPcr;
    public readonly int FixDiscontinuities;
    public readonly int Reserved;
    public readonly double CorrectionThresholdMs;
    public readonly double MaxCorrectionRateMs;
    public readonly double HysteresisThresholdMs;
    public readonly long StreamBitrateHint;

    /// <summary>
    /// Creates native config from managed configuration.
    /// </summary>
    public static RestampingConfigNative FromManaged(RestampingConfiguration config) =>
        new(
            (int)config.Mode,
            config.SmoothPcr ? 1 : 0,
            config.FixDiscontinuities ? 1 : 0,
            0,
            config.CorrectionThresholdMs,
            config.MaxCorrectionRateMs,
            config.HysteresisThresholdMs,
            config.StreamBitrateHint
        );

    private RestampingConfigNative(
        int mode,
        int smoothPcr,
        int fixDiscontinuities,
        int reserved,
        double correctionThresholdMs,
        double maxCorrectionRateMs,
        double hysteresisThresholdMs,
        long streamBitrateHint
    )
    {
        Mode = mode;
        SmoothPcr = smoothPcr;
        FixDiscontinuities = fixDiscontinuities;
        Reserved = reserved;
        CorrectionThresholdMs = correctionThresholdMs;
        MaxCorrectionRateMs = maxCorrectionRateMs;
        HysteresisThresholdMs = hysteresisThresholdMs;
        StreamBitrateHint = streamBitrateHint;
    }
}

/// <summary>
/// Native restamping statistics structure.
/// Layout must match RestampingStatisticsNative in tsduck_interop.h exactly.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RestampingStatisticsNative
{
    public long PacketsProcessed;
    public long PcrSmoothed;
    public long PtsCorrected;
    public long DtsCorrected;
    public long DiscontinuitiesFixed;
    public double TotalCorrectionMs;
    public double CurrentOffsetMs;
    public long LastCorrectionTimeTicks;
    public int CorrectionActive;
    public int Reserved;

    /// <summary>
    /// Converts to managed RestampingStatistics.
    /// </summary>
    public readonly RestampingStatistics ToManaged() =>
        new(
            PacketsProcessed,
            PcrSmoothed,
            PtsCorrected,
            DtsCorrected,
            DiscontinuitiesFixed,
            TotalCorrectionMs,
            CurrentOffsetMs,
            LastCorrectionTimeTicks > 0 ? new DateTime(LastCorrectionTimeTicks, DateTimeKind.Utc) : null,
            CorrectionActive != 0
        );
}

// =============================================================================
// Managed Record Types
// =============================================================================

/// <summary>
/// PTS/DTS sample from stream analysis.
/// </summary>
/// <param name="Pid">PID carrying this timestamp.</param>
/// <param name="StreamType">MPEG stream type.</param>
/// <param name="Pts90Khz">Presentation timestamp (90kHz).</param>
/// <param name="Dts90Khz">Decoding timestamp (-1 if not present).</param>
/// <param name="Pcr90Khz">Reference PCR at sample time (-1 if N/A).</param>
/// <param name="PacketIndex">Packet position in stream.</param>
/// <param name="ByteOffset">Byte offset in stream.</param>
/// <param name="IsVideo">True if video stream.</param>
/// <param name="IsAudio">True if audio stream.</param>
/// <param name="IsKeyframe">True if keyframe (video only).</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct PtsDtsSample(
    int Pid,
    int StreamType,
    long Pts90Khz,
    long Dts90Khz,
    long Pcr90Khz,
    long PacketIndex,
    long ByteOffset,
    bool IsVideo,
    bool IsAudio,
    bool IsKeyframe
)
{
    /// <summary>
    /// Gets the PTS in milliseconds.
    /// </summary>
    public double PtsMs => Pts90Khz / 90.0;

    /// <summary>
    /// Gets the DTS in milliseconds, or null if not present.
    /// </summary>
    public double? DtsMs => Dts90Khz >= 0 ? Dts90Khz / 90.0 : null;
}

/// <summary>
/// A/V synchronization analysis result.
/// </summary>
/// <param name="VideoAudioDriftMs">Current A/V drift (positive = audio ahead).</param>
/// <param name="DriftRateMsPerSec">Drift trend (positive = increasing drift).</param>
/// <param name="PeakDriftMs">Maximum drift observed.</param>
/// <param name="AvgDriftMs">Average drift over analysis window.</param>
/// <param name="PcrVideoOffsetMs">PCR to video PTS offset.</param>
/// <param name="PcrAudioOffsetMs">PCR to audio PTS offset.</param>
/// <param name="VideoPtsCount">Video PTS samples collected.</param>
/// <param name="AudioPtsCount">Audio PTS samples collected.</param>
/// <param name="PcrCount">PCR samples for reference.</param>
/// <param name="VideoDiscontinuities">Video PTS discontinuities detected.</param>
/// <param name="AudioDiscontinuities">Audio PTS discontinuities detected.</param>
/// <param name="PcrDiscontinuities">PCR discontinuities detected.</param>
/// <param name="LastVideoPts">Most recent video PTS (90kHz).</param>
/// <param name="LastAudioPts">Most recent audio PTS (90kHz).</param>
/// <param name="LastPcr">Most recent PCR (90kHz base).</param>
/// <param name="Status">Current sync status.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct AvSyncAnalysis(
    double VideoAudioDriftMs,
    double DriftRateMsPerSec,
    double PeakDriftMs,
    double AvgDriftMs,
    double PcrVideoOffsetMs,
    double PcrAudioOffsetMs,
    long VideoPtsCount,
    long AudioPtsCount,
    long PcrCount,
    long VideoDiscontinuities,
    long AudioDiscontinuities,
    long PcrDiscontinuities,
    long LastVideoPts,
    long LastAudioPts,
    long LastPcr,
    AvSyncStatus Status
)
{
    /// <summary>
    /// Gets whether the stream is currently synchronized within tolerance.
    /// </summary>
    public bool IsSynchronized => Status == AvSyncStatus.Synchronized;

    /// <summary>
    /// Gets the absolute drift in milliseconds.
    /// </summary>
    public double AbsoluteDriftMs => Math.Abs(VideoAudioDriftMs);

    /// <summary>
    /// Gets a human-readable description of the sync status.
    /// </summary>
    public string StatusDescription =>
        Status switch
        {
            AvSyncStatus.Synchronized => string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"Synchronized (drift: {VideoAudioDriftMs:F1}ms)"
            ),
            AvSyncStatus.Drifting => string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"Drifting ({VideoAudioDriftMs:F1}ms, rate: {DriftRateMsPerSec:F2}ms/s)"
            ),
            AvSyncStatus.Desync => string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"Desync ({VideoAudioDriftMs:F1}ms)"
            ),
            AvSyncStatus.NoAudio => "No audio",
            AvSyncStatus.NoVideo => "No video",
            _ => "Unknown",
        };
}

/// <summary>
/// Restamping configuration.
/// </summary>
/// <param name="Mode">Restamping mode.</param>
/// <param name="CorrectionThresholdMs">Start correcting at this drift (default: 45ms).</param>
/// <param name="MaxCorrectionRateMs">Max correction per second (default: 10ms).</param>
/// <param name="HysteresisThresholdMs">Stop correcting below this (default: 20ms).</param>
/// <param name="SmoothPcr">Enable PCR jitter smoothing.</param>
/// <param name="FixDiscontinuities">Repair PTS discontinuities.</param>
/// <param name="StreamBitrateHint">Hint for CBR PCR smoothing (0=auto-detect).</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct RestampingConfiguration(
    RestampingMode Mode = RestampingMode.Monitor,
    double CorrectionThresholdMs = 45.0,
    double MaxCorrectionRateMs = 10.0,
    double HysteresisThresholdMs = 20.0,
    bool SmoothPcr = true,
    bool FixDiscontinuities = true,
    long StreamBitrateHint = 0
)
{
    /// <summary>
    /// Gets the default configuration (Monitor mode, standard thresholds).
    /// </summary>
    public static RestampingConfiguration Default => new();
}

/// <summary>
/// Restamping statistics.
/// </summary>
/// <param name="PacketsProcessed">Total packets analyzed.</param>
/// <param name="PcrSmoothed">PCRs that were smoothed.</param>
/// <param name="PtsCorrected">PTS values corrected.</param>
/// <param name="DtsCorrected">DTS values corrected.</param>
/// <param name="DiscontinuitiesFixed">Discontinuities repaired.</param>
/// <param name="TotalCorrectionMs">Cumulative correction applied.</param>
/// <param name="CurrentOffsetMs">Current correction offset.</param>
/// <param name="LastCorrectionTime">Time of last correction.</param>
/// <param name="CorrectionActive">Currently applying corrections.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct RestampingStatistics(
    long PacketsProcessed,
    long PcrSmoothed,
    long PtsCorrected,
    long DtsCorrected,
    long DiscontinuitiesFixed,
    double TotalCorrectionMs,
    double CurrentOffsetMs,
    DateTime? LastCorrectionTime,
    bool CorrectionActive
)
{
    /// <summary>
    /// Gets the total number of timestamps modified.
    /// </summary>
    public long TotalModified => PcrSmoothed + PtsCorrected + DtsCorrected;
}
