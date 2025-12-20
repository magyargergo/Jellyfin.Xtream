using System;
using System.Runtime.CompilerServices;

namespace Jellyfin.Xtream.Service.MpegTs;

/// <summary>
/// High-performance PES (Packetized Elementary Stream) header parser.
/// Extracts PTS and DTS timestamps for A/V synchronization.
/// </summary>
public static class PesParser
{
    private const int MinPesHeaderLength = 9;

    /// <summary>
    /// Attempts to extract PTS from a PES header within a TS packet payload.
    /// </summary>
    /// <param name="payload">The TS packet payload containing PES data.</param>
    /// <param name="pts">The extracted PTS value if found.</param>
    /// <returns>True if PTS was successfully extracted.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryExtractPts(ReadOnlySpan<byte> payload, out long pts)
    {
        pts = 0;

        if (payload.Length < MinPesHeaderLength)
        {
            return false;
        }

        if (!HasPesStartCode(payload))
        {
            return false;
        }

        byte streamId = payload[3];
        if (!IsTimestampedStream(streamId))
        {
            return false;
        }

        if (payload.Length < 9)
        {
            return false;
        }

        byte flags = payload[7];
        bool hasPts = (flags & 0x80) != 0;

        if (!hasPts)
        {
            return false;
        }

        if (payload.Length < 14)
        {
            return false;
        }

        pts = ExtractTimestamp(payload.Slice(9));
        return true;
    }

    /// <summary>
    /// Attempts to extract both PTS and DTS from a PES header.
    /// </summary>
    /// <param name="payload">The TS packet payload containing PES data.</param>
    /// <param name="pts">The extracted PTS value if found.</param>
    /// <param name="dts">The extracted DTS value if found.</param>
    /// <returns>True if at least PTS was successfully extracted.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryExtractTimestamps(ReadOnlySpan<byte> payload, out long pts, out long dts)
    {
        pts = 0;
        dts = 0;

        if (payload.Length < MinPesHeaderLength)
        {
            return false;
        }

        if (!HasPesStartCode(payload))
        {
            return false;
        }

        byte streamId = payload[3];
        if (!IsTimestampedStream(streamId))
        {
            return false;
        }

        if (payload.Length < 9)
        {
            return false;
        }

        byte flags = payload[7];
        bool hasPts = (flags & 0x80) != 0;
        bool hasDts = (flags & 0x40) != 0;

        if (!hasPts)
        {
            return false;
        }

        if (payload.Length < 14)
        {
            return false;
        }

        pts = ExtractTimestamp(payload.Slice(9));

        if (hasDts && payload.Length >= 19)
        {
            dts = ExtractTimestamp(payload.Slice(14));
        }
        else
        {
            dts = pts;
        }

        return true;
    }

    /// <summary>
    /// Determines if a stream ID represents a timestamped elementary stream.
    /// </summary>
    /// <param name="streamId">The PES stream ID.</param>
    /// <returns>True if the stream can carry PTS/DTS.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsTimestampedStream(byte streamId)
    {
        return streamId >= 0xC0 && streamId <= 0xEF;
    }

    /// <summary>
    /// Determines if a stream ID represents a video stream.
    /// </summary>
    /// <param name="streamId">The PES stream ID.</param>
    /// <returns>True if the stream is a video stream.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsVideoStream(byte streamId)
    {
        return streamId >= 0xE0 && streamId <= 0xEF;
    }

    /// <summary>
    /// Determines if a stream ID represents an audio stream.
    /// </summary>
    /// <param name="streamId">The PES stream ID.</param>
    /// <returns>True if the stream is an audio stream.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsAudioStream(byte streamId)
    {
        return streamId >= 0xC0 && streamId <= 0xDF;
    }

    /// <summary>
    /// Gets the PES header length including optional fields.
    /// </summary>
    /// <param name="payload">The TS packet payload containing PES data.</param>
    /// <returns>The total PES header length, or -1 if invalid.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetPesHeaderLength(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 9)
        {
            return -1;
        }

        if (!HasPesStartCode(payload))
        {
            return -1;
        }

        byte streamId = payload[3];
        if (!IsTimestampedStream(streamId))
        {
            return 6;
        }

        int headerDataLength = payload[8];
        return 9 + headerDataLength;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasPesStartCode(ReadOnlySpan<byte> data)
    {
        return data[0] == 0x00 && data[1] == 0x00 && data[2] == 0x01;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ExtractTimestamp(ReadOnlySpan<byte> data)
    {
        long ts = ((long)(data[0] & 0x0E)) << 29;
        ts |= ((long)data[1]) << 22;
        ts |= ((long)(data[2] & 0xFE)) << 14;
        ts |= ((long)data[3]) << 7;
        ts |= ((long)(data[4] & 0xFE)) >> 1;
        return ts;
    }
}
