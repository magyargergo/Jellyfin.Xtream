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

namespace Jellyfin.Xtream.Service.MpegTs.UseCases;

/// <summary>
/// Interface for MPEG-TS demuxer implementations.
/// </summary>
public interface ITsDemuxer : IDisposable
{
    /// <summary>
    /// Gets a value indicating whether the demuxer has been initialized with stream data.
    /// </summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Gets the number of detected programs.
    /// </summary>
    int ProgramCount { get; }

    /// <summary>
    /// Gets the detected program numbers.
    /// </summary>
    IEnumerable<int> GetProgramNumbers();

    /// <summary>
    /// Feeds data to the demuxer for processing.
    /// </summary>
    /// <param name="data">MPEG-TS data chunk.</param>
    void FeedData(ReadOnlySpan<byte> data);

    /// <summary>
    /// Processes available data and updates program information.
    /// </summary>
    /// <returns>True if processing was successful.</returns>
    bool Process();

    /// <summary>
    /// Gets the video PID for a program.
    /// </summary>
    /// <param name="programNumber">The program number.</param>
    /// <returns>Video PID or -1 if not found.</returns>
    int GetVideoPid(int programNumber);

    /// <summary>
    /// Gets the audio PIDs for a program.
    /// </summary>
    /// <param name="programNumber">The program number.</param>
    /// <returns>Audio PIDs or empty array.</returns>
    int[] GetAudioPids(int programNumber);

    /// <summary>
    /// Gets the PCR PID for a program.
    /// </summary>
    /// <param name="programNumber">The program number.</param>
    /// <returns>PCR PID or -1 if not found.</returns>
    int GetPcrPid(int programNumber);

    /// <summary>
    /// Gets the PMT PID for a program.
    /// </summary>
    /// <param name="programNumber">The program number.</param>
    /// <returns>PMT PID or -1 if not found.</returns>
    int GetPmtPid(int programNumber);

    /// <summary>
    /// Resets the demuxer state.
    /// </summary>
    void Reset();

    /// <summary>
    /// Occurs when program information is updated.
    /// </summary>
    event EventHandler<DemuxerProgramEventArgs>? ProgramDetected;

    /// <summary>
    /// Occurs when a packet is demuxed from the stream.
    /// </summary>
    event EventHandler<DemuxedPacketEventArgs>? PacketDemuxed;
}
