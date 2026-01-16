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

namespace Jellyfin.Xtream.Service.MpegTs.UseCases;

/// <summary>
/// Provides basic stream statistics including packet and byte counts.
/// This is a focused interface following the Interface Segregation Principle.
/// </summary>
public interface IStreamStatistics
{
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
    /// Gets the number of sync byte errors detected.
    /// </summary>
    long SyncByteErrors { get; }

    /// <summary>
    /// Gets the number of successful sync recoveries after sync byte errors.
    /// </summary>
    long SyncRecoveries { get; }

    /// <summary>
    /// Gets the count of programs detected in the stream.
    /// </summary>
    int ProgramCount { get; }
}
