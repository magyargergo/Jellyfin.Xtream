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
