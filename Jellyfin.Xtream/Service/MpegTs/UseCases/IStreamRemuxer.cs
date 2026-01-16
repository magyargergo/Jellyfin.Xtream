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
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Xtream.Service.MpegTs.UseCases;

/// <summary>
/// Statistics from the stream remuxer.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct RemuxerStatistics : IEquatable<RemuxerStatistics>
{
    /// <summary>Gets the total bytes written to the remuxer.</summary>
    public long BytesWritten { get; init; }

    /// <summary>Gets the total bytes read from the remuxer.</summary>
    public long BytesRead { get; init; }

    /// <summary>Gets the number of provider switches handled.</summary>
    public int ProviderSwitches { get; init; }

    /// <summary>Gets a value indicating whether the remuxer is running.</summary>
    public bool IsRunning { get; init; }

    /// <inheritdoc />
    public bool Equals(RemuxerStatistics other) =>
        BytesWritten == other.BytesWritten && BytesRead == other.BytesRead && IsRunning == other.IsRunning;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RemuxerStatistics other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(BytesWritten, BytesRead, IsRunning);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(RemuxerStatistics left, RemuxerStatistics right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(RemuxerStatistics left, RemuxerStatistics right) => !left.Equals(right);
}

/// <summary>
/// Interface for stream remuxers that handle A/V synchronization during provider switches.
/// </summary>
/// <remarks>
/// Implementations use FFmpeg subprocess to properly remux MPEG-TS streams,
/// handling timestamp discontinuities and A/V sync across provider switches.
/// </remarks>
public interface IStreamRemuxer : IDisposable
{
    /// <summary>
    /// Gets a value indicating whether the remuxer is currently running.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Gets the current remuxer statistics.
    /// </summary>
    RemuxerStatistics Statistics { get; }

    /// <summary>
    /// Starts the remuxer for the specified stream.
    /// </summary>
    /// <param name="streamId">Unique identifier for the stream.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if started successfully.</returns>
    Task<bool> StartAsync(string streamId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the remuxer.
    /// </summary>
    void Stop();

    /// <summary>
    /// Writes data to the remuxer input.
    /// </summary>
    /// <param name="data">Data to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of bytes written.</returns>
    ValueTask<int> WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads remuxed data from the output.
    /// </summary>
    /// <param name="buffer">Buffer to read into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of bytes read.</returns>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Signals a provider switch to the remuxer.
    /// FFmpeg will handle timestamp discontinuity with genpts/igndts flags.
    /// </summary>
    /// <param name="fromProvider">Previous provider identifier.</param>
    /// <param name="toProvider">New provider identifier.</param>
    void NotifyProviderSwitch(string? fromProvider, string? toProvider);
}
