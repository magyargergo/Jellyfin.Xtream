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
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Jellyfin.Xtream.Service.MpegTs.Core;

namespace Jellyfin.Xtream.Service.MpegTs.Models;

/// <summary>
/// Structured metrics for a single program in an MPEG-TS stream.
/// </summary>
/// <remarks>
/// <para>
/// TR 101 290 compliance metrics (CC errors, PCR jitter, transport errors) are provided
/// by TsDuck via <see cref="TsDuck.ITsDuckAnalyzer.GetMetrics"/>. This struct focuses
/// on indexing-specific metrics needed for seeking and playback.
/// </para>
/// <para>
/// A/V sync and PCR timing metrics are now provided by the native TsDuck analyzer.
/// Use <see cref="TsDuck.ITsDuckAnalyzer.GetAvSyncAnalysis"/> for sync monitoring.
/// </para>
/// </remarks>
/// <param name="ProgramNumber">The program number.</param>
/// <param name="VideoPid">The video PID, or -1 if no video.</param>
/// <param name="AudioPid">The audio PID, or -1 if no audio.</param>
/// <param name="AudioCodec">The audio codec type.</param>
/// <param name="PcrPid">The PCR PID.</param>
/// <param name="KeyframeCount">Number of keyframes indexed.</param>
/// <param name="AverageGopDuration">Average GOP duration.</param>
/// <param name="AudioFrameCount">Number of audio frames detected.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct ProgramMetrics(
    int ProgramNumber,
    int VideoPid,
    int AudioPid,
    AudioCodec AudioCodec,
    int PcrPid,
    int KeyframeCount,
    TimeSpan AverageGopDuration,
    long AudioFrameCount
);

/// <summary>
/// Structured metrics for the TsIndexer.
/// Provides packet-level and program information in a strongly-typed format.
/// </summary>
/// <remarks>
/// <para>
/// TR 101 290 compliance metrics (transport errors, continuity errors, PCR jitter, etc.)
/// are provided by TsDuck via <see cref="TsDuck.ITsDuckAnalyzer.GetMetrics"/>.
/// </para>
/// <para>
/// This struct focuses on indexing-specific metrics: program structure, encryption
/// detection, and basic packet statistics.
/// </para>
/// </remarks>
/// <param name="ProgramCount">Number of programs detected.</param>
/// <param name="ProgramsWithVideoCount">Number of programs with video.</param>
/// <param name="TotalPacketsParsed">Total TS packets parsed.</param>
/// <param name="TotalBytesProcessed">Total bytes processed.</param>
/// <param name="IsEncrypted">Whether the stream is encrypted.</param>
/// <param name="ScrambledPidCount">Number of scrambled PIDs.</param>
/// <param name="CaSystemCount">Number of CA systems detected.</param>
/// <param name="CaSystemIds">Detected CA system IDs with vendor names.</param>
/// <param name="Programs">Per-program metrics.</param>
public readonly record struct TsIndexerMetrics(
    int ProgramCount,
    int ProgramsWithVideoCount,
    long TotalPacketsParsed,
    long TotalBytesProcessed,
    bool IsEncrypted,
    int ScrambledPidCount,
    int CaSystemCount,
    IReadOnlyDictionary<int, string> CaSystemIds,
    IReadOnlyList<ProgramMetrics> Programs
);
