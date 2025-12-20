using System;
using System.Runtime.InteropServices;

namespace Jellyfin.Xtream.Service.MpegTs;

/// <summary>
/// Represents a presentation or decoding timestamp from a PES header.
/// Timestamps use a 90 kHz clock per MPEG-2 specification.
/// </summary>
/// <param name="Value">The raw 33-bit timestamp value (90 kHz units).</param>
/// <param name="StreamOffset">The absolute byte offset where this timestamp was found.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct StreamTimestamp(long Value, long StreamOffset)
{
    private const long TicksPerSecond = 90_000;
    private const long WrapValue = 1L << 33;

    /// <summary>
    /// Gets the timestamp as a TimeSpan relative to stream start.
    /// </summary>
    public TimeSpan AsTimeSpan => TimeSpan.FromTicks((Value * TimeSpan.TicksPerSecond) / TicksPerSecond);

    /// <summary>
    /// Gets the timestamp in milliseconds.
    /// </summary>
    public double Milliseconds => (Value * 1000.0) / TicksPerSecond;

    /// <summary>
    /// Calculates the difference between two timestamps, handling wrap-around.
    /// </summary>
    /// <param name="other">The timestamp to subtract.</param>
    /// <returns>The difference in 90 kHz units.</returns>
    public long DifferenceFrom(StreamTimestamp other)
    {
        long diff = Value - other.Value;

        if (diff < -(WrapValue / 2))
        {
            diff += WrapValue;
        }
        else if (diff > WrapValue / 2)
        {
            diff -= WrapValue;
        }

        return diff;
    }

    /// <summary>
    /// Calculates the difference in milliseconds.
    /// </summary>
    /// <param name="other">The timestamp to subtract.</param>
    /// <returns>The difference in milliseconds.</returns>
    public double DifferenceInMsFrom(StreamTimestamp other)
    {
        return (DifferenceFrom(other) * 1000.0) / TicksPerSecond;
    }
}
