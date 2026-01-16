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

namespace Jellyfin.Xtream.Service.MpegTs.Models;

/// <summary>
/// Context captured before a stream switch for calculating timestamp offsets.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="StreamSwitchContext"/> struct.
/// </remarks>
[StructLayout(LayoutKind.Auto)]
public readonly struct StreamSwitchContext(
    long lastVideoPts,
    long lastAudioPts,
    long lastPcr,
    StreamSyncPoint? lastSyncPoint,
    DateTime capturedAt
) : IEquatable<StreamSwitchContext>
{
    /// <summary>Gets the last video PTS before switch.</summary>
    public long LastVideoPts { get; } = lastVideoPts;

    /// <summary>Gets the last audio PTS before switch.</summary>
    public long LastAudioPts { get; } = lastAudioPts;

    /// <summary>Gets the last PCR before switch.</summary>
    public long LastPcr { get; } = lastPcr;

    /// <summary>Gets the last sync point before switch.</summary>
    public StreamSyncPoint? LastSyncPoint { get; } = lastSyncPoint;

    /// <summary>Gets when this context was captured.</summary>
    public DateTime CapturedAt { get; } = capturedAt;

    /// <summary>
    /// Calculates the timestamp offset needed to maintain continuity with a new stream.
    /// </summary>
    /// <param name="newFirstVideoPts">First video PTS from new stream.</param>
    /// <param name="frameDuration90Khz">Expected frame duration for gap.</param>
    /// <returns>The calculated timestamp offset.</returns>
    public TimestampOffset CalculateOffset(long newFirstVideoPts, long frameDuration90Khz = 3003) =>
        TimestampOffset.FromPtsDelta(LastVideoPts, newFirstVideoPts, frameDuration90Khz);

    /// <inheritdoc />
    public bool Equals(StreamSwitchContext other) =>
        LastVideoPts == other.LastVideoPts && LastAudioPts == other.LastAudioPts;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is StreamSwitchContext other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(LastVideoPts, LastAudioPts);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(StreamSwitchContext left, StreamSwitchContext right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(StreamSwitchContext left, StreamSwitchContext right) => !left.Equals(right);
}
