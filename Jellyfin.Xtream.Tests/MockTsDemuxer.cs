// Copyright (C) 2025  Gergo Magyar

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using Jellyfin.Xtream.Service.MpegTs.UseCases;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Mock implementation of ITsDemuxer for testing purposes.
/// Allows manual control over program detection and packet events.
/// </summary>
internal sealed class MockTsDemuxer : ITsDemuxer
{
    private readonly Dictionary<int, MockProgramInfo> _programs = [];
    private readonly List<DemuxedPacketEventArgs> _pendingPackets = [];
    private long _bytesProcessed;

    /// <inheritdoc/>
    public bool IsInitialized { get; set; }

    /// <inheritdoc/>
    public int ProgramCount => _programs.Count;

    /// <inheritdoc/>
    public event EventHandler<DemuxerProgramEventArgs>? ProgramDetected;

    /// <inheritdoc/>
    public event EventHandler<DemuxedPacketEventArgs>? PacketDemuxed;

    /// <inheritdoc/>
    public IEnumerable<int> GetProgramNumbers() => _programs.Keys;

    /// <inheritdoc/>
    public int GetVideoPid(int programNumber) =>
        _programs.TryGetValue(programNumber, out var program) ? program.VideoPid : -1;

    /// <inheritdoc/>
    public int[] GetAudioPids(int programNumber) =>
        _programs.TryGetValue(programNumber, out var program) ? program.AudioPids : [];

    /// <inheritdoc/>
    public int GetPcrPid(int programNumber) =>
        _programs.TryGetValue(programNumber, out var program) ? program.PcrPid : -1;

    /// <inheritdoc/>
    public int GetPmtPid(int programNumber) =>
        _programs.TryGetValue(programNumber, out var program) ? program.PmtPid : -1;

    /// <inheritdoc/>
    public void FeedData(ReadOnlySpan<byte> data) => _bytesProcessed += data.Length;

    /// <inheritdoc/>
    public bool Process()
    {
        if (_pendingPackets.Count > 0)
        {
            var packet = _pendingPackets[0];
            _pendingPackets.RemoveAt(0);
            PacketDemuxed?.Invoke(this, packet);
            return true;
        }

        return false;
    }

    /// <inheritdoc/>
    public void Reset()
    {
        _programs.Clear();
        _pendingPackets.Clear();
        _bytesProcessed = 0;
        IsInitialized = false;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // Nothing to dispose
    }

    /// <summary>
    /// Adds a program to the mock demuxer and fires the ProgramDetected event.
    /// </summary>
    public void AddProgram(int programNumber, int pmtPid, int videoPid, int[] audioPids, int pcrPid)
    {
        var program = new MockProgramInfo
        {
            ProgramNumber = programNumber,
            PmtPid = pmtPid,
            VideoPid = videoPid,
            AudioPids = audioPids,
            PcrPid = pcrPid,
        };

        _programs[programNumber] = program;
        IsInitialized = true;

        ProgramDetected?.Invoke(
            this,
            new DemuxerProgramEventArgs
            {
                ProgramNumber = programNumber,
                PmtPid = pmtPid,
                PcrPid = pcrPid,
                VideoPid = videoPid,
                AudioPids = audioPids,
            }
        );
    }

    /// <summary>
    /// Queues a packet to be emitted during the next Process() call.
    /// </summary>
    public void QueuePacket(DemuxedPacketEventArgs packet) => _pendingPackets.Add(packet);

    /// <summary>
    /// Emits a packet event immediately.
    /// </summary>
    public void EmitPacket(DemuxedPacketEventArgs packet) => PacketDemuxed?.Invoke(this, packet);

    private sealed class MockProgramInfo
    {
        public int ProgramNumber { get; init; }
        public int PmtPid { get; init; }
        public int VideoPid { get; init; }
        public int[] AudioPids { get; init; } = [];
        public int PcrPid { get; init; }
    }
}
