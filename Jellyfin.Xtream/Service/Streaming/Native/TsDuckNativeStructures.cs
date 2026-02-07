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

namespace Jellyfin.Xtream.Service.Streaming.Native;

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
    public readonly int EnableAutoRestamp;

    // Integrated restamping settings
    public readonly int RestampMode;
    public readonly int SmoothPcr;
    public readonly int FixDiscontinuities;
    public readonly int Reserved;

    public readonly double CorrectionThresholdMs;
    public readonly double MaxCorrectionRateMs;
    public readonly double HysteresisThresholdMs;
    public readonly long StreamBitrateHint;

    /// <summary>
    /// Initializes a new instance of the <see cref="TsDuckConfigNative"/> struct.
    /// </summary>
    private TsDuckConfigNative(
        int metricsIntervalMs,
        int enableTr101290,
        int sampleSizeBytes,
        int enableAutoRestamp,
        int restampMode,
        int smoothPcr,
        int fixDiscontinuities,
        int reserved,
        double correctionThresholdMs,
        double maxCorrectionRateMs,
        double hysteresisThresholdMs,
        long streamBitrateHint
    )
    {
        MetricsIntervalMs = metricsIntervalMs;
        EnableTr101290 = enableTr101290;
        SampleSizeBytes = sampleSizeBytes;
        EnableAutoRestamp = enableAutoRestamp;
        RestampMode = restampMode;
        SmoothPcr = smoothPcr;
        FixDiscontinuities = fixDiscontinuities;
        Reserved = reserved;
        CorrectionThresholdMs = correctionThresholdMs;
        MaxCorrectionRateMs = maxCorrectionRateMs;
        HysteresisThresholdMs = hysteresisThresholdMs;
        StreamBitrateHint = streamBitrateHint;
    }

    /// <summary>
    /// Creates native config from managed configuration.
    /// </summary>
    public static TsDuckConfigNative FromManaged(TsDuckConfiguration config) =>
        new(
            metricsIntervalMs: config.MetricsIntervalSeconds * 1000,
            enableTr101290: config.EnableTr101290 ? 1 : 0,
            sampleSizeBytes: config.SampleSizeBytes,
            enableAutoRestamp: config.EnableAutoRestamp ? 1 : 0,
            restampMode: (int)config.RestampMode,
            smoothPcr: config.SmoothPcr ? 1 : 0,
            fixDiscontinuities: config.FixDiscontinuities ? 1 : 0,
            reserved: 0,
            correctionThresholdMs: config.CorrectionThresholdMs,
            maxCorrectionRateMs: config.MaxCorrectionRateMs,
            hysteresisThresholdMs: config.HysteresisThresholdMs,
            streamBitrateHint: config.StreamBitrateHint
        );
}

/// <summary>
/// Native PCR analysis structure.
/// Layout must match PcrAnalysisNative in tsduck_interop.h exactly.
/// Tracks PCR timing per ISO/IEC 13818-1 requirements:
/// - Accuracy: +/-500ns phase tolerance
/// - Frequency offset: +/-30 ppm (+/-810 Hz at 27MHz)
/// - Drift rate: 75 mHz/sec (10 ppm/hr)
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

    // ISO/IEC 13818-1 compliance tracking (new fields)
    public double PcrFrequencyOffsetPpm;
    public double PcrDriftRatePpmHr;
    public int FrequencyOffsetValid;
    public int DriftRateValid;
    public double PcrAccuracyNs;
    public int AccuracyValid;
    public int Reserved;

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
            PcrValidCount,
            PcrFrequencyOffsetPpm,
            PcrDriftRatePpmHr,
            FrequencyOffsetValid != 0,
            DriftRateValid != 0,
            PcrAccuracyNs,
            AccuracyValid != 0
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
// A/V Sync Analysis Structures
// =============================================================================

/// <summary>
/// A/V synchronization status.
/// </summary>
public enum AvSyncStatus
{
    /// <summary>Not enough data to determine sync status.</summary>
    Unknown = 0,

    /// <summary>Audio and video are synchronized within tolerance (+/-20ms).</summary>
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

// =============================================================================
// HTTP Streamer Structures
// =============================================================================

/// <summary>
/// Streamer state enumeration.
/// </summary>
public enum StreamerState
{
    /// <summary>Not started.</summary>
    Idle = 0,

    /// <summary>Establishing connection.</summary>
    Connecting = 1,

    /// <summary>Actively receiving data.</summary>
    Streaming = 2,

    /// <summary>Backoff before retry on same URL.</summary>
    Reconnecting = 3,

    /// <summary>Mid-stream URL switch in progress.</summary>
    Switching = 4,

    /// <summary>No data received within threshold.</summary>
    Stalled = 5,

    /// <summary>Explicitly stopped.</summary>
    Stopped = 6,

    /// <summary>Unrecoverable error (max retries exhausted).</summary>
    Failed = 7,
}

/// <summary>
/// Streamer event types.
/// </summary>
public enum StreamerEvent
{
    /// <summary>Successfully connected to URL.</summary>
    Connected = 0,

    /// <summary>Connection lost.</summary>
    Disconnected = 1,

    /// <summary>Starting reconnection attempt.</summary>
    Reconnecting = 2,

    /// <summary>Successfully switched to new URL.</summary>
    Switched = 3,

    /// <summary>Data stall detected.</summary>
    Stalled = 4,

    /// <summary>First data received after connect/reconnect.</summary>
    DataReceived = 5,

    /// <summary>HTTP or network error.</summary>
    Error = 6,

    /// <summary>Streaming stopped.</summary>
    Stopped = 7,

    /// <summary>TR 101 290 error rate exceeded threshold, switching URL.</summary>
    QualityDegraded = 8,
}

/// <summary>
/// Native streamer configuration structure.
/// Layout must match TsDuckStreamerConfigNative in tsduck_interop.h exactly.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TsDuckStreamerConfigNative
{
    public int ConnectTimeoutMs;
    public int ResponseTimeoutMs;
    public int StallTimeoutMs;
    public int MaxRetries;

    public int InitialBackoffMs;
    public int MaxBackoffMs;
    public double BackoffMultiplier;
    public int BackoffJitterMs;

    public int OutputFd;
    public int AlignmentBufferPackets;

    public int EnableRestamp;
    public int RestampMode;

    public int LowSpeedLimitBytes;
    public int LowSpeedTimeSec;

    public int StallsBeforeSwitch;
    public int TimeoutImmediateSwitch;

    // Quality-based switching (TR 101 290 error rate thresholds)
    public int EnableQualitySwitch;
    public int QualityCheckIntervalMs;
    public int QualityWindowSeconds;
    public int MaxSyncErrorsPerWindow;
    public int MaxContinuityErrorsPerSec;
    public int MaxTransportErrorsPerSec;
    public int MaxPcrErrorsPerSec;

    // Health-based URL selection settings
    public int QuarantineDurationMs;
    public int MaxQuarantineDurationMs;
    public double QuarantineBackoffMultiplier;
    public double ScoreBoostOnSuccess;
    public double ScorePenaltyOnFailure;
    public double DefaultHealthScore;

    // Circuit breaker settings (for E2E testing - use small values for fast tests)
    public int CircuitBreakerShortWindowSize;
    public int CircuitBreakerLongWindowSize;
    public int CircuitBreakerShortWindowErrorPercent;
    public int CircuitBreakerLongWindowErrorPercent;

    // DNS failure settings
    public int DnsRetryCount;
    public long DnsEjectionDurationMs;

    // Load balancer settings (P2C, EWMA, outlier detection)
    public int EnableP2C;
    public int EnableOutlierDetection;
    public int EwmaDecaySeconds;
    public int ProbationSuccessThreshold;
    public int MinSamplesForOutlier;
    public int ReservedLb;
    public double OutlierStddevFactor;

    /// <summary>
    /// Creates a default configuration with industry-standard timeout values.
    /// </summary>
    /// <remarks>
    /// Timeouts based on industry standards research (TR 101 290, TVHeadend, etc.):
    /// - Connect: 10s for slow IPTV providers
    /// - Response: 15s for Xtream API variability
    /// - Stall: 30s industry standard.
    /// </remarks>
    public static TsDuckStreamerConfigNative Default =>
        new()
        {
            ConnectTimeoutMs = 10000,
            ResponseTimeoutMs = 15000,
            StallTimeoutMs = 30000,
            MaxRetries = 10,
            InitialBackoffMs = 500,
            MaxBackoffMs = 30000,
            BackoffMultiplier = 2.0,
            BackoffJitterMs = 200,
            OutputFd = -1,
            AlignmentBufferPackets = 32,
            EnableRestamp = 1,
            RestampMode = (int)RestampingMode.Correct,
            LowSpeedLimitBytes = 1000,
            LowSpeedTimeSec = 10,
            StallsBeforeSwitch = 2,
            TimeoutImmediateSwitch = 1,
            EnableQualitySwitch = 1,
            QualityCheckIntervalMs = 1000,
            QualityWindowSeconds = 10,
            MaxSyncErrorsPerWindow = 1,
            MaxContinuityErrorsPerSec = 20,
            MaxTransportErrorsPerSec = 10,
            MaxPcrErrorsPerSec = 5,
            QuarantineDurationMs = 30000,
            MaxQuarantineDurationMs = 300000,
            QuarantineBackoffMultiplier = 2.0,
            ScoreBoostOnSuccess = 0.5,
            ScorePenaltyOnFailure = 5.0,
            DefaultHealthScore = 50.0,
            CircuitBreakerShortWindowSize = 1500,
            CircuitBreakerLongWindowSize = 3000,
            CircuitBreakerShortWindowErrorPercent = 10,
            CircuitBreakerLongWindowErrorPercent = 5,
            DnsRetryCount = 3,
            DnsEjectionDurationMs = 300000, // 5 minutes
            EnableP2C = 1,
            EnableOutlierDetection = 1,
            EwmaDecaySeconds = 10,
            ProbationSuccessThreshold = 3,
            MinSamplesForOutlier = 10,
            ReservedLb = 0,
            OutlierStddevFactor = 1.9,
        };
}

/// <summary>
/// Native streamer status snapshot.
/// Layout must match TsDuckStreamerStatusNative in tsduck_interop.h exactly.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TsDuckStreamerStatusNative
{
    public int State;
    public int CurrentUrlIndex;
    public int UrlCount;
    public int RetryCount;
    public long BytesReceived;
    public long PacketsOutput;
    public long SwitchesCompleted;
    public long Reconnections;
    public long LastDataTimeTicks;
    public long SessionStartTicks;
    public int LastHttpStatus;
    public int LastCurlError;
    public long QualitySwitches;

    /// <summary>
    /// Converts to managed StreamerStatus.
    /// </summary>
    public readonly StreamerStatus ToManaged() =>
        new(
            (StreamerState)State,
            CurrentUrlIndex,
            UrlCount,
            RetryCount,
            BytesReceived,
            PacketsOutput,
            SwitchesCompleted,
            Reconnections,
            LastDataTimeTicks > 0 ? new DateTime(LastDataTimeTicks, DateTimeKind.Utc) : null,
            SessionStartTicks > 0 ? new DateTime(SessionStartTicks, DateTimeKind.Utc) : null,
            LastHttpStatus,
            LastCurlError,
            QualitySwitches
        );
}

// =============================================================================
// Managed Record Types
// =============================================================================

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
/// Managed streamer status.
/// </summary>
/// <param name="State">Current streamer state.</param>
/// <param name="CurrentUrlIndex">Active URL index (0-based).</param>
/// <param name="UrlCount">Total URLs configured.</param>
/// <param name="RetryCount">Current retry attempt number.</param>
/// <param name="BytesReceived">Total bytes received from network.</param>
/// <param name="PacketsOutput">Total TS packets written to output.</param>
/// <param name="SwitchesCompleted">Number of URL switches performed.</param>
/// <param name="Reconnections">Number of reconnection attempts.</param>
/// <param name="LastDataTime">Time of last data received.</param>
/// <param name="SessionStart">Session start time.</param>
/// <param name="LastHttpStatus">Last HTTP response code.</param>
/// <param name="LastCurlError">Last libcurl error code.</param>
/// <param name="QualitySwitches">Switches triggered by quality degradation (TR 101 290).</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct StreamerStatus(
    StreamerState State,
    int CurrentUrlIndex,
    int UrlCount,
    int RetryCount,
    long BytesReceived,
    long PacketsOutput,
    long SwitchesCompleted,
    long Reconnections,
    DateTime? LastDataTime,
    DateTime? SessionStart,
    int LastHttpStatus,
    int LastCurlError,
    long QualitySwitches
)
{
    /// <summary>
    /// Gets whether the streamer is actively streaming or reconnecting.
    /// </summary>
    public bool IsActive =>
        State
            is StreamerState.Connecting
                or StreamerState.Streaming
                or StreamerState.Reconnecting
                or StreamerState.Switching;

    /// <summary>
    /// Gets whether the streamer has reached a terminal state.
    /// </summary>
    public bool IsTerminal => State is StreamerState.Stopped or StreamerState.Failed;
}

// =============================================================================
// Provider Health System Structures
// =============================================================================

/// <summary>
/// Provider health state for the three-state model.
/// </summary>
public enum ProviderState
{
    /// <summary>Fully healthy, eligible for selection.</summary>
    Active = 0,

    /// <summary>Recently recovered, under observation.</summary>
    Probation = 1,

    /// <summary>Circuit open, temporarily unavailable.</summary>
    Ejected = 2,
}

/// <summary>
/// DNS failure policy returned by the health manager.
/// Indicates how to handle a DNS failure for failover decisions.
/// </summary>
public enum DnsFailurePolicy
{
    /// <summary>Transient failure - switch to next URL but don't eject.</summary>
    Switch = 0,

    /// <summary>Persistent failure - eject provider for 5 minutes, then switch.</summary>
    EjectAndSwitch = 1,
}

/// <summary>
/// Native provider health snapshot.
/// Layout must match ProviderHealthSnapshotNative in tsduck_interop.h exactly.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ProviderHealthSnapshotNative
{
    public int ProviderIndex;
    public int State;
    public double SuccessRate;
    public double LatencyEwmaMs;
    public int ActiveRequests;
    public int IsolatedTimes;
    public int IsolationDurationMs;
    public int Reserved;

    /// <summary>
    /// Converts to managed ProviderHealthSnapshot.
    /// </summary>
    public readonly ProviderHealthSnapshot ToManaged() =>
        new(
            ProviderIndex,
            (ProviderState)State,
            SuccessRate,
            LatencyEwmaMs,
            ActiveRequests,
            IsolatedTimes,
            IsolationDurationMs
        );
}

/// <summary>
/// Managed provider health snapshot for diagnostics and testing.
/// </summary>
/// <param name="ProviderIndex">Provider index.</param>
/// <param name="State">Current provider state.</param>
/// <param name="SuccessRate">Success rate (0.0 to 1.0).</param>
/// <param name="LatencyEwmaMs">Current EWMA latency in milliseconds.</param>
/// <param name="ActiveRequests">Current active requests.</param>
/// <param name="IsolatedTimes">Number of times circuit has opened.</param>
/// <param name="IsolationDurationMs">Current isolation duration.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct ProviderHealthSnapshot(
    int ProviderIndex,
    ProviderState State,
    double SuccessRate,
    double LatencyEwmaMs,
    int ActiveRequests,
    int IsolatedTimes,
    int IsolationDurationMs
)
{
    /// <summary>
    /// Gets whether this provider is currently ejected.
    /// </summary>
    public bool IsEjected => State == ProviderState.Ejected;

    /// <summary>
    /// Gets whether this provider is under probation.
    /// </summary>
    public bool IsInProbation => State == ProviderState.Probation;

    /// <summary>
    /// Gets whether this provider is fully healthy.
    /// </summary>
    public bool IsHealthy => State == ProviderState.Active && SuccessRate >= 0.9;
}
