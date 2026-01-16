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
/// Binary-level MPEG-TS timestamp patcher.
/// Applies timestamp offsets directly to PCR/PTS/DTS fields in TS packets
/// without full demuxing/remuxing.
/// </summary>
/// <remarks>
/// <para>
/// This approach is used instead of full remuxing because:
/// </para>
/// <list type="bullet">
///   <item><description>No blocking FFmpeg calls (avformat_find_stream_info)</description></item>
///   <item><description>Works with partial/incomplete stream data</description></item>
///   <item><description>Lower latency - no buffering for interleaving</description></item>
///   <item><description>Preserves original packet structure</description></item>
/// </list>
/// </remarks>
public interface ITsTimestampPatcher : IDisposable
{
    /// <summary>
    /// Gets whether the patcher has an active timestamp offset.
    /// </summary>
    bool HasActiveOffset { get; }

    /// <summary>
    /// Gets the current timestamp offset in 90kHz units.
    /// </summary>
    long CurrentOffset90Khz { get; }

    /// <summary>
    /// Gets the number of packets patched.
    /// </summary>
    long PatchedPacketCount { get; }

    /// <summary>
    /// Sets the timestamp offset to apply to all timestamps.
    /// </summary>
    /// <param name="streamId">Stream identifier.</param>
    /// <param name="offset90Khz">Offset in 90kHz units (add to timestamps).</param>
    void SetOffset(string streamId, long offset90Khz);

    /// <summary>
    /// Handles a provider switch by calculating the offset needed to maintain continuity.
    /// </summary>
    /// <param name="streamId">Stream identifier.</param>
    /// <param name="lastOutputPts90Khz">Last PTS written before switch.</param>
    /// <param name="newInputFirstPts90Khz">First PTS from new provider.</param>
    void HandleProviderSwitch(string streamId, long lastOutputPts90Khz, long newInputFirstPts90Khz);

    /// <summary>
    /// Patches timestamps in MPEG-TS data in-place.
    /// Applies the current offset to all PCR, PTS, and DTS values found.
    /// </summary>
    /// <param name="streamId">Stream identifier.</param>
    /// <param name="data">MPEG-TS data to patch (modified in-place).</param>
    /// <returns>Number of timestamps patched.</returns>
    int PatchTimestamps(string streamId, Span<byte> data);

    /// <summary>
    /// Updates the last known PTS for offset calculations.
    /// </summary>
    /// <param name="streamId">Stream identifier.</param>
    /// <param name="pts90Khz">Last PTS in 90kHz units.</param>
    void UpdateLastPts(string streamId, long pts90Khz);

    /// <summary>
    /// Resets state for a stream.
    /// </summary>
    /// <param name="streamId">Stream identifier.</param>
    void Reset(string streamId);
}
