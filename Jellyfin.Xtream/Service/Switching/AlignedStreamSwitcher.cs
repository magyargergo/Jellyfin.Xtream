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
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Service.MpegTs;
using Jellyfin.Xtream.Utility;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.Switching;

/// <summary>
/// Result of an aligned stream switch operation.
/// </summary>
internal readonly struct AlignedSwitchResult : IEquatable<AlignedSwitchResult>
{
    /// <summary>Gets a value indicating whether the switch succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Gets the number of bytes discarded for alignment.</summary>
    public int BytesDiscarded { get; init; }

    /// <summary>Gets the time taken for alignment in milliseconds.</summary>
    public long AlignmentTimeMs { get; init; }

    /// <summary>Gets a value indicating whether a keyframe was found for alignment.</summary>
    public bool AlignedToKeyframe { get; init; }

    /// <summary>Gets a value indicating whether the keyframe is an IDR frame.</summary>
    public bool IsIdrFrame { get; init; }

    /// <summary>Gets the detected NAL unit type if applicable.</summary>
    public NalUnitType NalType { get; init; }

    /// <summary>Gets any error message.</summary>
    public string? Error { get; init; }

    /// <summary>Creates a successful result.</summary>
    public static AlignedSwitchResult Succeeded(
        int bytesDiscarded,
        long alignmentTimeMs,
        bool alignedToKeyframe,
        bool isIdrFrame = false,
        NalUnitType nalType = NalUnitType.Unknown
    ) =>
        new()
        {
            Success = true,
            BytesDiscarded = bytesDiscarded,
            AlignmentTimeMs = alignmentTimeMs,
            AlignedToKeyframe = alignedToKeyframe,
            IsIdrFrame = isIdrFrame,
            NalType = nalType,
        };

    /// <summary>Creates a failed result.</summary>
    public static AlignedSwitchResult Failed(string error) => new() { Success = false, Error = error };

    /// <summary>Equality operator.</summary>
    public static bool operator ==(AlignedSwitchResult left, AlignedSwitchResult right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(AlignedSwitchResult left, AlignedSwitchResult right) => !left.Equals(right);

    /// <inheritdoc />
    public bool Equals(AlignedSwitchResult other) => Success == other.Success && Error == other.Error;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is AlignedSwitchResult other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Success, Error);
}

/// <summary>
/// Handles seamless stream switching with MPEG-TS byte alignment.
/// Ensures the new stream starts at a valid packet boundary, ideally at a keyframe.
/// </summary>
/// <remarks>
/// MPEG-TS packets are 188 bytes starting with sync byte 0x47.
/// For seamless switching:
/// 1. Find sync byte alignment in new stream
/// 2. Optionally wait for a keyframe (I-frame) for best visual transition
/// 3. Signal discontinuity in the buffer for readers to handle
/// </remarks>
/// <remarks>
/// Initializes a new instance of the <see cref="AlignedStreamSwitcher"/> class.
/// </remarks>
/// <param name="logger">Optional logger.</param>
internal sealed class AlignedStreamSwitcher(ILogger? logger = null)
{
    private const int TsPacketSize = 188;
    private const byte TsSyncByte = 0x47;
    private const int MaxSyncSearchBytes = TsPacketSize * 10; // Search up to 10 packets
    private const int KeyframeSearchTimeoutMs = 500; // Max time to search for keyframe
    private const int AlignmentBufferSize = TsPacketSize * 8; // Read ahead buffer

    private readonly ILogger? _logger = logger;
    private readonly byte[] _alignmentBuffer = new byte[AlignmentBufferSize];

    /// <summary>
    /// Aligns a new stream to MPEG-TS packet boundaries.
    /// </summary>
    /// <param name="newStream">The new stream to align.</param>
    /// <param name="waitForKeyframe">Whether to wait for a keyframe.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Alignment result with aligned data ready to write.</returns>
    public async Task<(AlignedSwitchResult Result, ReadOnlyMemory<byte> AlignedData)> AlignStreamAsync(
        Stream newStream,
        bool waitForKeyframe = false,
        CancellationToken cancellationToken = default
    )
    {
        long startTicks = Environment.TickCount64;
        int totalBytesRead = 0;
        int bytesDiscarded = 0;

        try
        {
            // Read initial data for alignment
            int bytesRead = await ReadWithTimeoutAsync(newStream, _alignmentBuffer, cancellationToken)
                .ConfigureAwait(false);

            if (bytesRead == 0)
            {
                return (AlignedSwitchResult.Failed("No data from new stream"), ReadOnlyMemory<byte>.Empty);
            }

            totalBytesRead = bytesRead;

            // Find sync byte position
            int syncOffset = FindSyncOffset(_alignmentBuffer.AsSpan(0, bytesRead));

            if (syncOffset < 0)
            {
                return (AlignedSwitchResult.Failed("Could not find MPEG-TS sync byte"), ReadOnlyMemory<byte>.Empty);
            }

            bytesDiscarded = syncOffset;

            // Get aligned data
            int alignedLength = bytesRead - syncOffset;
            var alignedData = new byte[alignedLength];
            Array.Copy(_alignmentBuffer, syncOffset, alignedData, 0, alignedLength);

            var foundKeyframe = false;
            var isIdrFrame = false;
            var nalType = NalUnitType.Unknown;

            if (waitForKeyframe)
            {
                var detection = DetectKeyframe(alignedData.AsSpan());
                foundKeyframe = detection.Found;
                isIdrFrame = detection.IsIdr;
                nalType = detection.NalType;

                if (!foundKeyframe)
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(KeyframeSearchTimeoutMs);

                    try
                    {
                        var (result, additionalData) = await SearchForIdrFrameAsync(newStream, timeoutCts.Token)
                            .ConfigureAwait(false);

                        foundKeyframe = result.Found;
                        isIdrFrame = result.IsIdr;
                        nalType = result.NalType;

                        if (additionalData.Length > 0)
                        {
                            var combined = new byte[alignedData.Length + additionalData.Length];
                            alignedData.CopyTo(combined, 0);
                            additionalData.CopyTo(combined.AsSpan(alignedData.Length));
                            alignedData = combined;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        _logger?.LogDebug("Keyframe search timed out, proceeding without keyframe alignment");
                    }
                }
            }

            long elapsedMs = Environment.TickCount64 - startTicks;

            _logger?.LogDebug(
                "Stream aligned: {BytesDiscarded} bytes discarded, {AlignedBytes} bytes ready, keyframe={Keyframe}, IDR={IsIdr}, NAL={NalType}, took {ElapsedMs}ms",
                bytesDiscarded,
                alignedData.Length,
                foundKeyframe,
                isIdrFrame,
                nalType,
                elapsedMs
            );

            return (
                AlignedSwitchResult.Succeeded(bytesDiscarded, elapsedMs, foundKeyframe, isIdrFrame, nalType),
                new ReadOnlyMemory<byte>(alignedData)
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (AlignedSwitchResult.Failed($"Alignment error: {ex.Message}"), ReadOnlyMemory<byte>.Empty);
        }
    }

    /// <summary>
    /// Quickly aligns to packet boundary without waiting for keyframe.
    /// Use this for fastest possible switching.
    /// </summary>
    /// <param name="data">Data to align.</param>
    /// <returns>Offset to first valid packet, or -1 if not found.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FindSyncOffset(ReadOnlySpan<byte> data)
    {
        if (data.Length < TsPacketSize)
        {
            return -1;
        }

        int maxSearch = Math.Min(data.Length - TsPacketSize, MaxSyncSearchBytes);

        for (int i = 0; i <= maxSearch; i++)
        {
            if (data[i] == TsSyncByte)
            {
                // Validate this is a real sync point by checking next packet
                int nextPacketOffset = i + TsPacketSize;
                if (nextPacketOffset >= data.Length || data[nextPacketOffset] == TsSyncByte)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// Checks if a span contains an MPEG-TS keyframe (RAI flag or IDR NAL).
    /// </summary>
    /// <param name="data">MPEG-TS data (must be packet aligned).</param>
    /// <returns>True if a keyframe indicator is found.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ContainsKeyframe(ReadOnlySpan<byte> data)
    {
        var result = IdrFrameDetector.Detect(data);
        return result.Found;
    }

    /// <summary>
    /// Detects keyframe with detailed IDR information.
    /// </summary>
    /// <param name="data">MPEG-TS data (must be packet aligned).</param>
    /// <returns>Detection result with IDR details.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IdrDetectionResult DetectKeyframe(ReadOnlySpan<byte> data) => IdrFrameDetector.Detect(data);

    /// <summary>
    /// Calculates the byte offset to align to the next packet boundary.
    /// </summary>
    /// <param name="currentPosition">Current byte position.</param>
    /// <returns>Bytes to skip to reach next packet boundary.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CalculatePacketAlignmentOffset(long currentPosition)
    {
        int remainder = (int)(currentPosition % TsPacketSize);
        return remainder == 0 ? 0 : TsPacketSize - remainder;
    }

    private static async Task<int> ReadWithTimeoutAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken
    )
    {
        int totalRead = 0;
        int minRead = TsPacketSize * 2; // Need at least 2 packets for alignment verification

        while (totalRead < minRead && totalRead < buffer.Length)
        {
            int bytesRead = await stream.ReadAsync(buffer.AsMemory(totalRead), cancellationToken).ConfigureAwait(false);

            if (bytesRead == 0)
            {
                break;
            }

            totalRead += bytesRead;
        }

        return totalRead;
    }

    private async Task<(IdrDetectionResult Result, byte[] AdditionalData)> SearchForIdrFrameAsync(
        Stream stream,
        CancellationToken cancellationToken
    )
    {
        var buffer = new byte[TsPacketSize * 50];
        int bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

        if (bytesRead == 0)
        {
            return (IdrDetectionResult.NotFound, Array.Empty<byte>());
        }

        var result = DetectKeyframe(buffer.AsSpan(0, bytesRead));

        var data = new byte[bytesRead];
        Array.Copy(buffer, data, bytesRead);

        return (result, data);
    }
}
