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

using System.Threading;
using Jellyfin.Xtream.Service.MpegTs.UseCases;

namespace Jellyfin.Xtream.Service.MpegTs.Infrastructure;

/// <summary>
/// Tracks packet-level statistics for MPEG-TS streams.
/// Thread-safe counters for packet and byte counts.
/// </summary>
/// <remarks>
/// TR 101 290 error tracking (transport errors, continuity errors, sync errors, CRC errors)
/// is handled by TsDuck. This class only tracks basic packet and byte counts.
/// </remarks>
public sealed class PacketStatistics : IStreamStatistics, IContinuityMonitor
{
    private long _totalPacketsParsed;
    private long _totalBytesProcessed;

    /// <inheritdoc />
    public long TotalPacketsParsed => Interlocked.Read(ref _totalPacketsParsed);

    /// <inheritdoc />
    public long TotalBytesProcessed => Interlocked.Read(ref _totalBytesProcessed);

    /// <summary>
    /// Gets the number of times the parser had to resynchronize due to corruption.
    /// </summary>
    /// <remarks>TsDuck handles sync error tracking via TR 101 290.</remarks>
    public long ResyncCount => 0;

    /// <inheritdoc />
    /// <remarks>TsDuck handles transport error tracking via TR 101 290 Priority 2.</remarks>
    public long TotalPacketErrors => 0;

    /// <inheritdoc />
    /// <remarks>TsDuck handles continuity error tracking via TR 101 290 Priority 1.</remarks>
    public long TotalContinuityErrors => 0;

    /// <summary>
    /// Gets the number of sync byte errors (packets not starting with 0x47).
    /// </summary>
    /// <remarks>TsDuck handles sync byte error tracking via TR 101 290 Priority 1.</remarks>
    public long SyncByteErrors => 0;

    /// <summary>
    /// Gets the number of successful sync recoveries.
    /// </summary>
    /// <remarks>TsDuck handles sync tracking via TR 101 290.</remarks>
    public long SyncRecoveries => 0;

    /// <inheritdoc />
    public int ProgramCount { get; set; }

    /// <summary>
    /// Increments the parsed packet counter.
    /// </summary>
    /// <returns>The new packet count.</returns>
    public long IncrementPacketsParsed() => Interlocked.Increment(ref _totalPacketsParsed);

    /// <summary>
    /// Adds to the bytes processed counter.
    /// </summary>
    /// <param name="bytes">The number of bytes to add.</param>
    public void AddBytesProcessed(long bytes) => Interlocked.Add(ref _totalBytesProcessed, bytes);

    /// <summary>
    /// Resets all counters to zero.
    /// </summary>
    public void Reset()
    {
        _ = Interlocked.Exchange(ref _totalPacketsParsed, 0);
        _ = Interlocked.Exchange(ref _totalBytesProcessed, 0);
        ProgramCount = 0;
    }
}
