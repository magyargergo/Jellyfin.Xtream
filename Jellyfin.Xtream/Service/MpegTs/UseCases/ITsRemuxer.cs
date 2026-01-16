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
/// Interface for MPEG-TS remuxers that handle timestamp correction.
/// Implementations use FFmpeg's battle-tested libavformat for proper
/// PCR regeneration and PTS/DTS handling.
/// </summary>
public interface ITsRemuxer : IDisposable
{
    /// <summary>
    /// Gets a value indicating whether the remuxer is initialized.
    /// </summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Gets the number of packets processed.
    /// </summary>
    long PacketsProcessed { get; }

    /// <summary>
    /// Gets the number of bytes written to output.
    /// </summary>
    long BytesWritten { get; }

    /// <summary>
    /// Gets the amount of output data available to read.
    /// </summary>
    int OutputAvailable { get; }

    /// <summary>
    /// Sets a timestamp offset to apply during remuxing.
    /// Use this for provider switches to maintain timeline continuity.
    /// </summary>
    /// <param name="offset90Khz">Offset in 90kHz units to add to all timestamps.</param>
    void SetTimestampOffset(long offset90Khz);

    /// <summary>
    /// Handles a provider switch by calculating and applying the appropriate timestamp offset.
    /// </summary>
    /// <param name="lastOutputPts90Khz">Last PTS written to output before switch.</param>
    /// <param name="newInputFirstPts90Khz">First PTS from new provider.</param>
    /// <param name="frameDuration90Khz">Frame duration for continuity (default ~30fps).</param>
    void HandleProviderSwitch(long lastOutputPts90Khz, long newInputFirstPts90Khz, long frameDuration90Khz = 3003);

    /// <summary>
    /// Feeds MPEG-TS data to the remuxer for processing.
    /// </summary>
    /// <param name="data">Input MPEG-TS data.</param>
    /// <returns>Number of bytes accepted.</returns>
    int FeedData(ReadOnlySpan<byte> data);

    /// <summary>
    /// Reads processed output data.
    /// </summary>
    /// <param name="buffer">Buffer to write output to.</param>
    /// <returns>Number of bytes read.</returns>
    int ReadOutput(Span<byte> buffer);

    /// <summary>
    /// Resets the remuxer state for a new stream.
    /// </summary>
    void Reset();
}
