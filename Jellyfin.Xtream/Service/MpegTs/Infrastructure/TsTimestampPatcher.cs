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
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using Jellyfin.Xtream.Service.MpegTs.Core;
using Jellyfin.Xtream.Service.MpegTs.UseCases;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.MpegTs.Infrastructure;

/// <summary>
/// Binary-level MPEG-TS timestamp patcher.
/// Patches PCR/PTS/DTS directly in packet bytes without demuxing/remuxing.
/// </summary>
/// <remarks>
/// <para>
/// Timestamp formats in MPEG-TS:
/// </para>
/// <list type="bullet">
///   <item><description>PCR: 33-bit base (90kHz) + 9-bit extension (27MHz), 6 bytes in adaptation field</description></item>
///   <item><description>PTS/DTS: 33-bit values (90kHz), 5 bytes each in PES header</description></item>
/// </list>
/// <para>
/// This patcher modifies timestamps in-place, preserving packet structure.
/// </para>
/// </remarks>
public sealed class TsTimestampPatcher : ITsTimestampPatcher
{
    // 33-bit max value for timestamp wraparound
    private const long MaxTimestamp33Bit = (1L << 33) - 1;

    // Gap to add after last PTS to avoid collision (100ms in 90kHz = 9000 ticks)
    private const long SwitchGap90Khz = 9000;

    private readonly ConcurrentDictionary<string, StreamState> _streams = new(StringComparer.Ordinal);
    private readonly ILogger<TsTimestampPatcher>? _logger;

    private long _totalPatchedPackets;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="TsTimestampPatcher"/> class.
    /// </summary>
    /// <param name="logger">Optional logger.</param>
    public TsTimestampPatcher(ILogger<TsTimestampPatcher>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public bool HasActiveOffset
    {
        get
        {
            foreach (var state in _streams.Values)
            {
                if (state.Offset90Khz != 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <inheritdoc />
    public long CurrentOffset90Khz
    {
        get
        {
            foreach (var state in _streams.Values)
            {
                if (state.Offset90Khz != 0)
                {
                    return state.Offset90Khz;
                }
            }

            return 0;
        }
    }

    /// <inheritdoc />
    public long PatchedPacketCount => Interlocked.Read(ref _totalPatchedPackets);

    /// <inheritdoc />
    public void SetOffset(string streamId, long offset90Khz)
    {
        var state = GetOrCreateState(streamId);
        state.Offset90Khz = offset90Khz;

        _logger?.PluginLogInformation(
            "Set timestamp offset for stream {StreamId}: {OffsetMs:F2}ms ({Offset90Khz} ticks)",
            streamId,
            offset90Khz / 90.0,
            offset90Khz
        );
    }

    /// <inheritdoc />
    public void HandleProviderSwitch(string streamId, long lastOutputPts90Khz, long newInputFirstPts90Khz)
    {
        var state = GetOrCreateState(streamId);

        // Calculate offset: new timestamps should continue from last output + gap
        // offset = (lastOutput + gap) - newInput
        // So: newOutput = newInput + offset = newInput + (lastOutput + gap - newInput) = lastOutput + gap
        var targetPts = lastOutputPts90Khz + SwitchGap90Khz;
        var newOffset = targetPts - newInputFirstPts90Khz;

        // Handle wraparound: if offset would cause negative timestamps, adjust
        if (newOffset < -MaxTimestamp33Bit / 2)
        {
            newOffset += MaxTimestamp33Bit + 1;
        }
        else if (newOffset > MaxTimestamp33Bit / 2)
        {
            newOffset -= MaxTimestamp33Bit + 1;
        }

        state.Offset90Khz = newOffset;

        _logger?.PluginLogInformation(
            "Provider switch for stream {StreamId}: lastPts={LastMs:F2}ms, newFirstPts={NewMs:F2}ms, offset={OffsetMs:F2}ms",
            streamId,
            lastOutputPts90Khz / 90.0,
            newInputFirstPts90Khz / 90.0,
            newOffset / 90.0
        );
    }

    /// <inheritdoc />
    public int PatchTimestamps(string streamId, Span<byte> data)
    {
        if (_disposed || data.IsEmpty)
        {
            return 0;
        }

        if (!_streams.TryGetValue(streamId, out var state) || state.Offset90Khz == 0)
        {
            return 0; // No offset set, nothing to patch
        }

        var patchCount = 0;
        var offset = 0;

        while (offset + TsConstants.PacketSize <= data.Length)
        {
            // Verify sync byte
            if (data[offset] != TsConstants.SyncByte)
            {
                offset++;
                continue;
            }

            var packet = data.Slice(offset, TsConstants.PacketSize);
            var patched = PatchPacket(packet, state.Offset90Khz);
            patchCount += patched;

            offset += TsConstants.PacketSize;
        }

        if (patchCount > 0)
        {
            Interlocked.Add(ref _totalPatchedPackets, patchCount);
        }

        return patchCount;
    }

    /// <inheritdoc />
    public void UpdateLastPts(string streamId, long pts90Khz)
    {
        var state = GetOrCreateState(streamId);
        state.LastPts90Khz = pts90Khz;
    }

    /// <inheritdoc />
    public void Reset(string streamId)
    {
        if (_streams.TryRemove(streamId, out _))
        {
            _logger?.LogDebugIfEnabled("Reset stream {StreamId}", streamId);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _streams.Clear();
    }

    private StreamState GetOrCreateState(string streamId)
    {
        return _streams.GetOrAdd(streamId, _ => new StreamState());
    }

    /// <summary>
    /// Patches timestamps in a single TS packet.
    /// </summary>
    /// <returns>Number of timestamps patched (0-3: PCR, PTS, DTS).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int PatchPacket(Span<byte> packet, long offset90Khz)
    {
        var patchCount = 0;

        // Parse header
        var adaptationControl = (packet[3] >> 4) & 0x03;
        var hasAdaptation = (adaptationControl & 0x02) != 0;
        var hasPayload = (adaptationControl & 0x01) != 0;

        // Patch PCR in adaptation field
        if (hasAdaptation && packet[4] > 0)
        {
            var adaptationLength = packet[4];
            if (adaptationLength >= 7) // Minimum for PCR
            {
                var flags = packet[5];
                var hasPcr = (flags & 0x10) != 0;

                if (hasPcr)
                {
                    PatchPcr(packet.Slice(6, 6), offset90Khz);
                    patchCount++;
                }
            }
        }

        // Check for PES header with PTS/DTS
        if (hasPayload)
        {
            var payloadOffset = 4;
            if (hasAdaptation)
            {
                payloadOffset += 1 + packet[4];
            }

            // Check for PUSI (Payload Unit Start Indicator) and PES header
            var hasPusi = (packet[1] & 0x40) != 0;
            if (hasPusi && payloadOffset + 9 < TsConstants.PacketSize)
            {
                var payload = packet[payloadOffset..];

                // Check for PES start code (0x00 0x00 0x01)
                if (payload.Length >= 14 && payload[0] == 0x00 && payload[1] == 0x00 && payload[2] == 0x01)
                {
                    var streamId = payload[3];

                    // Check if this is a video/audio stream (not padding, etc.)
                    // Video: 0xE0-0xEF, Audio: 0xC0-0xDF, Private: 0xBD
                    if ((streamId >= 0xBD && streamId <= 0xEF) && payload.Length >= 14)
                    {
                        var pesHeaderLength = payload[8];
                        var ptsFlags = (payload[7] >> 6) & 0x03;

                        // PTS present (flags 2 or 3)
                        if (ptsFlags >= 2 && payload.Length >= 14 && pesHeaderLength >= 5)
                        {
                            PatchPts(payload.Slice(9, 5), offset90Khz);
                            patchCount++;

                            // DTS present (flags 3)
                            if (ptsFlags == 3 && payload.Length >= 19 && pesHeaderLength >= 10)
                            {
                                PatchPts(payload.Slice(14, 5), offset90Khz);
                                patchCount++;
                            }
                        }
                    }
                }
            }
        }

        return patchCount;
    }

    /// <summary>
    /// Patches a 6-byte PCR field in-place.
    /// PCR format: 33-bit base (90kHz) + 6 reserved bits + 9-bit extension (27MHz).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PatchPcr(Span<byte> pcr, long offset90Khz)
    {
        // Extract current PCR base (33 bits) - use ulong to avoid sign extension
        var pcrBase = (long)(
            ((ulong)pcr[0] << 25)
            | ((ulong)pcr[1] << 17)
            | ((ulong)pcr[2] << 9)
            | ((ulong)pcr[3] << 1)
            | ((uint)(pcr[4] >> 7) & 0x01)
        );

        // Extract extension (9 bits) - keep unchanged
        var pcrExt = ((pcr[4] & 0x01) << 8) | pcr[5];

        // Apply offset to base
        pcrBase = (pcrBase + offset90Khz) & MaxTimestamp33Bit;

        // Write back
        pcr[0] = (byte)(pcrBase >> 25);
        pcr[1] = (byte)(pcrBase >> 17);
        pcr[2] = (byte)(pcrBase >> 9);
        pcr[3] = (byte)(pcrBase >> 1);
        pcr[4] = (byte)(((pcrBase & 0x01) << 7) | 0x7E | ((uint)(pcrExt >> 8) & 0x01)); // 0x7E = reserved bits
        pcr[5] = (byte)(pcrExt & 0xFF);
    }

    /// <summary>
    /// Patches a 5-byte PTS/DTS field in-place.
    /// Format: '00xx' marker (4 bits) + PTS[32..30] (3 bits) + marker (1 bit) +
    ///         PTS[29..15] (15 bits) + marker (1 bit) + PTS[14..0] (15 bits) + marker (1 bit).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PatchPts(Span<byte> pts, long offset90Khz)
    {
        // Extract current PTS (33 bits) - use uint/ulong to avoid sign extension
        var value = (long)(
            ((ulong)((uint)(pts[0] >> 1) & 0x07) << 30)
            | ((ulong)(uint)pts[1] << 22)
            | ((ulong)((uint)(pts[2] >> 1)) << 15)
            | ((ulong)(uint)pts[3] << 7)
            | ((ulong)(uint)(pts[4] >> 1))
        );

        // Apply offset
        value = (value + offset90Khz) & MaxTimestamp33Bit;

        // Write back preserving marker bits and prefix
        var prefix = (uint)(pts[0] & 0xF0); // Preserve '00xx' prefix (identifies PTS vs DTS)
        pts[0] = (byte)(prefix | ((uint)(value >> 29) & 0x0E) | 0x01); // marker bit
        pts[1] = (byte)(value >> 22);
        pts[2] = (byte)(((uint)(value >> 14) & 0xFE) | 0x01); // marker bit
        pts[3] = (byte)(value >> 7);
        pts[4] = (byte)(((uint)(value << 1) & 0xFE) | 0x01); // marker bit
    }

    /// <summary>
    /// Per-stream state.
    /// </summary>
    private sealed class StreamState
    {
        public long Offset90Khz { get; set; }
        public long LastPts90Khz { get; set; }
    }
}
