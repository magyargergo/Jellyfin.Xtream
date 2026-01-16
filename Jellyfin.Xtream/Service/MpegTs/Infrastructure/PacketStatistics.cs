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
/// Thread-safe counters for packet counts, errors, and processing metrics.
/// </summary>
public sealed class PacketStatistics : IStreamStatistics, IContinuityMonitor
{
    private long _totalPacketsParsed;
    private long _totalBytesProcessed;
    private long _totalPacketErrors;
    private long _totalContinuityErrors;
    private long _syncByteErrors;
    private long _syncRecoveries;

    /// <inheritdoc />
    public long TotalPacketsParsed => Interlocked.Read(ref _totalPacketsParsed);

    /// <inheritdoc />
    public long TotalBytesProcessed => Interlocked.Read(ref _totalBytesProcessed);

    /// <summary>
    /// Gets the number of times the parser had to resynchronize due to corruption.
    /// </summary>
    /// <remarks>
    /// Cinegy handles sync internally, so this is always 0.
    /// </remarks>
    public long ResyncCount => 0;

    /// <inheritdoc />
    public long TotalPacketErrors => Interlocked.Read(ref _totalPacketErrors);

    /// <inheritdoc />
    public long TotalContinuityErrors => Interlocked.Read(ref _totalContinuityErrors);

    /// <summary>
    /// Gets the number of sync byte errors (packets not starting with 0x47).
    /// </summary>
    public long SyncByteErrors => Interlocked.Read(ref _syncByteErrors);

    /// <summary>
    /// Gets the number of successful sync recoveries.
    /// </summary>
    public long SyncRecoveries => Interlocked.Read(ref _syncRecoveries);

    /// <inheritdoc />
    public long PatIntervalViolations { get; private set; }

    /// <inheritdoc />
    public int ProgramCount { get; set; }

    /// <summary>
    /// Sets the PAT interval violations count from an external source.
    /// </summary>
    /// <param name="count">The violation count.</param>
    public void SetPatIntervalViolations(long count) => PatIntervalViolations = count;

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
    /// Increments the packet error counter.
    /// </summary>
    public void IncrementPacketErrors() => Interlocked.Increment(ref _totalPacketErrors);

    /// <summary>
    /// Increments the continuity error counter.
    /// </summary>
    public void IncrementContinuityErrors() => Interlocked.Increment(ref _totalContinuityErrors);

    /// <summary>
    /// Increments the sync byte error counter.
    /// </summary>
    public void IncrementSyncByteErrors() => Interlocked.Increment(ref _syncByteErrors);

    /// <summary>
    /// Increments the sync recovery counter.
    /// </summary>
    public void IncrementSyncRecoveries() => Interlocked.Increment(ref _syncRecoveries);

    /// <summary>
    /// Resets all counters to zero.
    /// </summary>
    public void Reset()
    {
        _ = Interlocked.Exchange(ref _totalPacketsParsed, 0);
        _ = Interlocked.Exchange(ref _totalBytesProcessed, 0);
        _ = Interlocked.Exchange(ref _totalPacketErrors, 0);
        _ = Interlocked.Exchange(ref _totalContinuityErrors, 0);
        _ = Interlocked.Exchange(ref _syncByteErrors, 0);
        _ = Interlocked.Exchange(ref _syncRecoveries, 0);
        PatIntervalViolations = 0;
        ProgramCount = 0;
    }
}
