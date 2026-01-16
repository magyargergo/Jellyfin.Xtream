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

namespace Jellyfin.Xtream.Service.MpegTs.UseCases;

/// <summary>
/// Event arguments for PTS discontinuity detection.
/// This event is raised when a significant PTS jump is detected in the source stream,
/// which could cause playback issues like frame jumping or video looping.
/// </summary>
/// <remarks>
/// <para>
/// PTS discontinuities in IPTV streams commonly occur due to:
/// </para>
/// <list type="bullet">
///   <item><description>Provider encoder restarts</description></item>
///   <item><description>Content loop points in playlists</description></item>
///   <item><description>Ad insertion boundaries</description></item>
///   <item><description>Server-side stream switching</description></item>
/// </list>
/// <para>
/// Forward jumps indicate the provider stream jumped ahead (missing content).
/// Backward jumps indicate the stream looped back (repeated content).
/// </para>
/// </remarks>
/// <param name="previousPts">The PTS value before the jump (90kHz).</param>
/// <param name="newPts">The PTS value after the jump (90kHz).</param>
/// <param name="deltaMs">The PTS delta in milliseconds. Positive = forward jump, negative = backward jump.</param>
/// <param name="offset">The stream byte offset where the discontinuity was detected.</param>
/// <param name="isBackwardJump">True if this is a backward PTS jump (potential video loop).</param>
public sealed class PtsDiscontinuityEventArgs(
    long previousPts,
    long newPts,
    double deltaMs,
    long offset,
    bool isBackwardJump
) : EventArgs
{
    /// <summary>
    /// Gets the PTS value before the discontinuity (90kHz clock).
    /// </summary>
    public long PreviousPts { get; } = previousPts;

    /// <summary>
    /// Gets the PTS value after the discontinuity (90kHz clock).
    /// </summary>
    public long NewPts { get; } = newPts;

    /// <summary>
    /// Gets the PTS delta in milliseconds.
    /// Positive values indicate a forward jump (missing content).
    /// Negative values indicate a backward jump (content loop).
    /// </summary>
    public double DeltaMs { get; } = deltaMs;

    /// <summary>
    /// Gets the stream byte offset where the discontinuity was detected.
    /// </summary>
    public long Offset { get; } = offset;

    /// <summary>
    /// Gets a value indicating whether this is a backward PTS jump.
    /// Backward jumps typically indicate video looping or repeated content.
    /// </summary>
    public bool IsBackwardJump { get; } = isBackwardJump;

    /// <summary>
    /// Gets a value indicating whether this is a forward PTS jump.
    /// Forward jumps typically indicate missing content or stream gaps.
    /// </summary>
    public bool IsForwardJump => !IsBackwardJump;

    /// <summary>
    /// Gets the absolute delta magnitude in milliseconds.
    /// </summary>
    public double AbsoluteDeltaMs => Math.Abs(DeltaMs);
}
