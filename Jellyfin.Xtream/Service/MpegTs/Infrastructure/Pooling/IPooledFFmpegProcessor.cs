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
using Jellyfin.Xtream.Service.MpegTs.UseCases;

namespace Jellyfin.Xtream.Service.MpegTs.Infrastructure.Pooling;

/// <summary>
/// Unified interface for pooled FFmpeg processors (demuxers and remuxers).
/// </summary>
/// <remarks>
/// <para>
/// This interface provides both demuxing and remuxing capabilities in a single type,
/// differentiated by <see cref="Kind"/>.
/// </para>
/// <para>
/// Usage depends on Kind:
/// <list type="bullet">
///   <item>
///     <term>Demuxer</term>
///     <description>
///       Use FeedData + Process for synchronous processing.
///       Subscribe to ProgramDetected and PacketDemuxed events for output.
///     </description>
///   </item>
///   <item>
///     <term>Remuxer</term>
///     <description>
///       Use TryQueueData (non-blocking) for input.
///       Use TryReadOutput (non-blocking) for output.
///       Background worker handles FFmpeg processing.
///     </description>
///   </item>
/// </list>
/// </para>
/// </remarks>
public interface IPooledFFmpegProcessor : IPoolable
{
    /// <summary>
    /// Gets the processor kind (demuxer or remuxer).
    /// </summary>
    FFmpegProcessorKind Kind { get; }

    /// <summary>
    /// Gets a value indicating whether the processor is initialized.
    /// </summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Gets a value indicating whether the processor is running (has valid FFmpeg context).
    /// </summary>
    bool IsRunning { get; }

    #region Input Methods

    /// <summary>
    /// Feeds data to the processor synchronously.
    /// </summary>
    /// <remarks>
    /// For Demuxer: Call this then Process() to demux.
    /// For Remuxer: Prefer TryQueueData() for non-blocking operation.
    /// </remarks>
    /// <param name="data">Input MPEG-TS data.</param>
    void FeedData(ReadOnlySpan<byte> data);

    /// <summary>
    /// Queues data for asynchronous processing. Non-blocking.
    /// </summary>
    /// <remarks>
    /// Primarily for Remuxer mode. In Demuxer mode, delegates to FeedData.
    /// </remarks>
    /// <param name="data">Input MPEG-TS data.</param>
    /// <returns>True if data was queued; false if queue is full or disposed.</returns>
    bool TryQueueData(ReadOnlySpan<byte> data);

    /// <summary>
    /// Gets the number of bytes currently queued for processing.
    /// </summary>
    long QueuedBytes { get; }

    #endregion

    #region Processing

    /// <summary>
    /// Processes pending data synchronously.
    /// </summary>
    /// <remarks>
    /// For Demuxer: Drives the demux loop, fires events.
    /// For Remuxer: No-op (background worker handles processing).
    /// </remarks>
    /// <returns>True if processing occurred; false if no data available.</returns>
    bool Process();

    /// <summary>
    /// Resets the processor state.
    /// </summary>
    void Reset();

    /// <summary>
    /// Sets the stream ID for logging and tracking.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    void SetStreamId(string streamId);

    #endregion

    #region Demuxer Output (events)

    /// <summary>
    /// Occurs when program information is detected (PAT/PMT parsed).
    /// </summary>
    /// <remarks>Only fires in Demuxer mode.</remarks>
    event EventHandler<DemuxerProgramEventArgs>? ProgramDetected;

    /// <summary>
    /// Occurs when a packet is demuxed.
    /// </summary>
    /// <remarks>Only fires in Demuxer mode.</remarks>
    event EventHandler<DemuxedPacketEventArgs>? PacketDemuxed;

    /// <summary>
    /// Gets the number of detected programs.
    /// </summary>
    int ProgramCount { get; }

    /// <summary>
    /// Gets the program numbers from the transport stream.
    /// </summary>
    /// <returns>Enumerable of program numbers.</returns>
    IEnumerable<int> GetProgramNumbers();

    /// <summary>
    /// Gets the video PID for a program.
    /// </summary>
    /// <param name="programNumber">The program number.</param>
    /// <returns>The video PID, or -1 if not found.</returns>
    int GetVideoPid(int programNumber);

    /// <summary>
    /// Gets the audio PIDs for a program.
    /// </summary>
    /// <param name="programNumber">The program number.</param>
    /// <returns>Array of audio PIDs.</returns>
    int[] GetAudioPids(int programNumber);

    /// <summary>
    /// Gets the PCR PID for a program.
    /// </summary>
    /// <param name="programNumber">The program number.</param>
    /// <returns>The PCR PID, or -1 if not found.</returns>
    int GetPcrPid(int programNumber);

    /// <summary>
    /// Gets the PMT PID for a program.
    /// </summary>
    /// <param name="programNumber">The program number.</param>
    /// <returns>The PMT PID, or -1 if not found.</returns>
    int GetPmtPid(int programNumber);

    #endregion

    #region Remuxer Output (queue)

    /// <summary>
    /// Gets a value indicating whether output data is available.
    /// </summary>
    bool HasOutput { get; }

    /// <summary>
    /// Tries to read processed output data. Non-blocking.
    /// </summary>
    /// <param name="output">The output data, or null if none available.</param>
    /// <returns>True if output was available; false otherwise.</returns>
    bool TryReadOutput(out byte[]? output);

    /// <summary>
    /// Gets remuxer statistics.
    /// </summary>
    /// <remarks>Returns default statistics in Demuxer mode.</remarks>
    InProcessRemuxerStatistics Statistics { get; }

    /// <summary>
    /// Notifies the processor of a provider switch.
    /// </summary>
    /// <remarks>Only relevant in Remuxer mode for timestamp continuity.</remarks>
    /// <param name="fromProvider">Previous provider identifier.</param>
    /// <param name="toProvider">New provider identifier.</param>
    void NotifyProviderSwitch(string? fromProvider, string? toProvider);

    #endregion
}
