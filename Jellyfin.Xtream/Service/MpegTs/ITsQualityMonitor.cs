// Copyright (C) 2025  Gergo Magyar

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;

namespace Jellyfin.Xtream.Service.MpegTs;

/// <summary>
/// Interface for MPEG-TS stream quality monitoring per TR 101 290.
/// Provides read-only access to stream quality metrics without exposing indexing functionality.
/// </summary>
public interface ITsQualityMonitor
{
    /// <summary>
    /// Event raised when a TR 101 290 stream quality violation is detected.
    /// </summary>
    event EventHandler<StreamQualityViolationEventArgs>? StreamQualityViolation;

    /// <summary>
    /// Event raised when A/V synchronization drift is detected.
    /// </summary>
    event EventHandler<SyncDriftEventArgs>? SyncDriftDetected;

    /// <summary>
    /// Gets the total number of TS packets successfully parsed.
    /// </summary>
    long TotalPacketsParsed { get; }

    /// <summary>
    /// Gets the total bytes processed by the indexer.
    /// </summary>
    long TotalBytesProcessed { get; }

    /// <summary>
    /// Gets the number of times the parser had to resynchronize due to corruption.
    /// </summary>
    long ResyncCount { get; }

    /// <summary>
    /// Gets the total number of packets with Transport Error Indicator set.
    /// </summary>
    long TotalPacketErrors { get; }

    /// <summary>
    /// Gets the total number of continuity counter discontinuities detected.
    /// </summary>
    long TotalContinuityErrors { get; }

    /// <summary>
    /// Gets the number of PAT interval violations (interval >500ms per ISO 13818-1).
    /// </summary>
    long PatIntervalViolations { get; }

    /// <summary>
    /// Gets the number of sync byte errors detected.
    /// </summary>
    long SyncByteErrors { get; }

    /// <summary>
    /// Gets the number of successful sync recoveries after sync byte errors.
    /// </summary>
    long SyncRecoveries { get; }

    /// <summary>
    /// Gets the number of PAT CRC-32 validation failures.
    /// </summary>
    long PatCrcErrors { get; }

    /// <summary>
    /// Gets the number of PMT CRC-32 validation failures.
    /// </summary>
    long PmtCrcErrors { get; }

    /// <summary>
    /// Gets the number of CAT CRC-32 validation failures.
    /// </summary>
    long CatCrcErrors { get; }

    /// <summary>
    /// Gets the PIDs that are currently scrambled (encrypted).
    /// </summary>
    int[] ScrambledPids { get; }

    /// <summary>
    /// Gets the detected Conditional Access System IDs from CAT.
    /// </summary>
    IReadOnlyDictionary<int, string> CaSystemIds { get; }

    /// <summary>
    /// Gets a value indicating whether the stream is encrypted.
    /// </summary>
    bool IsEncrypted { get; }

    /// <summary>
    /// Gets the count of programs detected in the stream.
    /// </summary>
    int ProgramCount { get; }

    /// <summary>
    /// Gets comprehensive diagnostics about the stream quality.
    /// </summary>
    /// <returns>Formatted diagnostics string.</returns>
    string GetDiagnostics();

    /// <summary>
    /// Gets the current A/V sync status for a program.
    /// </summary>
    /// <param name="programNumber">The program number (-1 or 0 for first/default program).</param>
    /// <returns>The sync status.</returns>
    SyncStatus GetSyncStatus(int programNumber = -1);

    /// <summary>
    /// Gets the current A/V drift in milliseconds for a program.
    /// </summary>
    /// <param name="programNumber">The program number (-1 or 0 for first/default program).</param>
    /// <returns>The drift in milliseconds.</returns>
    double GetCurrentDriftMs(int programNumber = -1);

    /// <summary>
    /// Gets program information for a specific program.
    /// </summary>
    /// <param name="programNumber">The program number.</param>
    /// <returns>The program information, or null if not found.</returns>
    ProgramInfo? GetProgramInfo(int programNumber);
}
