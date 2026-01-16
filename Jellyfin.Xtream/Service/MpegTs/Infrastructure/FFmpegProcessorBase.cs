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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen.Abstractions;
using Jellyfin.Xtream.Service.MpegTs.UseCases;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.MpegTs.Infrastructure;

/// <summary>
/// Base class for FFmpeg-based MPEG-TS processors providing shared functionality.
/// </summary>
/// <remarks>
/// <para>
/// This base class provides common functionality for both demuxers and remuxers:
/// </para>
/// <list type="bullet">
///   <item><description>Lock-free circular buffer for input data</description></item>
///   <item><description>FFmpeg error message conversion</description></item>
///   <item><description>Power-of-two buffer sizing</description></item>
///   <item><description>FFmpeg context validation</description></item>
///   <item><description>MPEG-TS timestamp conversion utilities</description></item>
/// </list>
/// </remarks>
public abstract class FFmpegProcessorBase : IDisposable
{
    /// <summary>
    /// MPEG-TS standard clock frequency (90 kHz).
    /// All PTS/DTS timestamps in MPEG-TS are in 90kHz units.
    /// </summary>
    protected const int MpegTsClockRate = 90_000;

    /// <summary>
    /// Represents an invalid or missing timestamp (equivalent to AV_NOPTS_VALUE).
    /// </summary>
    protected const long NoTimestamp = long.MinValue;
    private long _inputHead;
    private long _inputTail;
    private volatile bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="FFmpegProcessorBase"/> class.
    /// </summary>
    /// <param name="inputBufferSize">Size of the circular input buffer.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <param name="ffmpegContext">Optional FFmpeg context. If null, uses the default adapter.</param>
    protected FFmpegProcessorBase(int inputBufferSize, ILogger? logger = null, IFFmpegContext? ffmpegContext = null)
    {
        Logger = logger;
        Context = ffmpegContext ?? FFmpegContextAdapter.Instance;

        if (!Context.IsAvailable)
        {
            throw new InvalidOperationException(
                "FFmpeg is not available. Ensure FFmpegContext.Initialize() is called during plugin startup."
            );
        }

        // Ensure buffer size is power of 2 for efficient modulo using bitwise AND
        inputBufferSize = RoundUpToPowerOfTwo(inputBufferSize);
        InputBuffer = new byte[inputBufferSize];
    }

    /// <summary>
    /// Gets the logger instance.
    /// </summary>
    protected ILogger? Logger { get; }

    /// <summary>
    /// Gets the FFmpeg context adapter.
    /// </summary>
    protected IFFmpegContext Context { get; }

    /// <summary>
    /// Gets a value indicating whether the processor has been disposed.
    /// </summary>
    protected bool IsDisposed => _disposed;

    /// <summary>
    /// Gets the input buffer.
    /// </summary>
    protected byte[] InputBuffer { get; }

    /// <summary>
    /// Gets the buffer size mask for efficient modulo operations.
    /// </summary>
    protected int BufferMask => InputBuffer.Length - 1;

    /// <summary>
    /// Gets the number of bytes available in the input buffer.
    /// </summary>
    protected long AvailableBytes => Interlocked.Read(ref _inputHead) - Volatile.Read(ref _inputTail);

    /// <summary>
    /// Reads the current input head position atomically.
    /// </summary>
    /// <returns>The current head position.</returns>
    protected long ReadInputHead() => Interlocked.Read(ref _inputHead);

    /// <summary>
    /// Reads the current input tail position with volatile semantics.
    /// </summary>
    /// <returns>The current tail position.</returns>
    protected long ReadInputTail() => Volatile.Read(ref _inputTail);

    /// <summary>
    /// Atomically adds a value to the input head and returns the new value.
    /// </summary>
    /// <param name="value">The value to add.</param>
    /// <returns>The new head position.</returns>
    protected long AddInputHead(long value) => Interlocked.Add(ref _inputHead, value);

    /// <summary>
    /// Atomically adds a value to the input tail and returns the new value.
    /// </summary>
    /// <param name="value">The value to add.</param>
    /// <returns>The new tail position.</returns>
    protected long AddInputTail(long value) => Interlocked.Add(ref _inputTail, value);

    /// <summary>
    /// Atomically sets the input head to a specified value and returns the original value.
    /// </summary>
    /// <param name="value">The value to set.</param>
    /// <returns>The original head position.</returns>
    protected long ExchangeInputHead(long value) => Interlocked.Exchange(ref _inputHead, value);

    /// <summary>
    /// Atomically sets the input tail to a specified value and returns the original value.
    /// </summary>
    /// <param name="value">The value to set.</param>
    /// <returns>The original tail position.</returns>
    protected long ExchangeInputTail(long value) => Interlocked.Exchange(ref _inputTail, value);

    /// <summary>
    /// Feeds input data to the circular buffer.
    /// </summary>
    /// <param name="data">The input data to feed.</param>
    protected void FeedInputData(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || _disposed)
        {
            return;
        }

        var bufferSize = InputBuffer.Length;
        var bufferMask = bufferSize - 1;

        // Read current positions atomically
        var head = Interlocked.Read(ref _inputHead);
        var tail = Volatile.Read(ref _inputTail);

        // Calculate available space
        var used = head - tail;
        var freeSpace = bufferSize - used;

        if (data.Length > freeSpace)
        {
            // Buffer overflow - discard oldest data
            var discardAmount = data.Length - freeSpace;
            _ = Interlocked.Add(ref _inputTail, discardAmount);
            Logger?.LogDebugIfEnabled("Input buffer overflow, discarded {Bytes} bytes", discardAmount);
        }

        // Copy data to circular buffer (may wrap around)
        var writePos = (int)(head & bufferMask);
        var firstCopy = Math.Min(data.Length, bufferSize - writePos);

        data[..firstCopy].CopyTo(InputBuffer.AsSpan(writePos));

        if (firstCopy < data.Length)
        {
            // Wrap around to beginning of buffer
            data[firstCopy..].CopyTo(InputBuffer.AsSpan(0));
        }

        // Publish new head position
        _ = Interlocked.Add(ref _inputHead, data.Length);
    }

    /// <summary>
    /// Reads data from the circular buffer to a native buffer.
    /// </summary>
    /// <param name="buf">Destination native buffer.</param>
    /// <param name="bufSize">Maximum bytes to read.</param>
    /// <returns>Number of bytes read, or FFmpeg error code.</returns>
    protected unsafe int ReadFromCircularBuffer(byte* buf, int bufSize)
    {
        if (_disposed)
        {
            return ffmpeg.AVERROR_EOF;
        }

        var bufferSize = InputBuffer.Length;
        var bufferMask = bufferSize - 1;

        var tail = Volatile.Read(ref _inputTail);
        var head = Interlocked.Read(ref _inputHead);
        var available = head - tail;

        if (available <= 0)
        {
            return ffmpeg.AVERROR(ffmpeg.EAGAIN);
        }

        var toRead = (int)Math.Min(bufSize, available);
        var readPos = (int)(tail & bufferMask);
        var firstCopy = Math.Min(toRead, bufferSize - readPos);

        fixed (byte* srcPtr = &InputBuffer[readPos])
        {
            Unsafe.CopyBlockUnaligned(buf, srcPtr, (uint)firstCopy);
        }

        if (firstCopy < toRead)
        {
            var remaining = toRead - firstCopy;
            fixed (byte* srcPtr = &InputBuffer[0])
            {
                Unsafe.CopyBlockUnaligned(buf + firstCopy, srcPtr, (uint)remaining);
            }
        }

        _ = Interlocked.Add(ref _inputTail, toRead);
        return toRead;
    }

    /// <summary>
    /// Clears the input buffer by resetting head and tail positions.
    /// </summary>
    protected void ClearInputBuffer()
    {
        _ = Interlocked.Exchange(ref _inputHead, 0);
        _ = Interlocked.Exchange(ref _inputTail, 0);
    }

    /// <summary>
    /// Sets the disposed flag.
    /// </summary>
    protected void MarkDisposed() => _disposed = true;

    /// <summary>
    /// Rounds up a value to the nearest power of two.
    /// </summary>
    /// <param name="value">The value to round.</param>
    /// <returns>The next power of two greater than or equal to value.</returns>
    protected static int RoundUpToPowerOfTwo(int value)
    {
        value--;
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        return value + 1;
    }

    /// <summary>
    /// Gets the error message for an FFmpeg error code.
    /// </summary>
    /// <param name="errorCode">The FFmpeg error code.</param>
    /// <returns>The error message string.</returns>
    protected static unsafe string GetErrorMessage(int errorCode)
    {
        var buffer = stackalloc byte[256];
        _ = ffmpeg.av_strerror(errorCode, buffer, 256);
        return Marshal.PtrToStringAnsi((IntPtr)buffer) ?? $"Error {errorCode}";
    }

    /// <summary>
    /// Converts a timestamp from FFmpeg time base to MPEG-TS 90kHz units.
    /// </summary>
    /// <param name="timestamp">The timestamp in FFmpeg time base units.</param>
    /// <param name="timeBase">The FFmpeg time base (num/den fraction).</param>
    /// <returns>The timestamp in 90kHz units, or <see cref="NoTimestamp"/> if input is AV_NOPTS_VALUE.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected static long ConvertToMpegTsTimestamp(long timestamp, AVRational timeBase)
    {
        if (timestamp == ffmpeg.AV_NOPTS_VALUE)
        {
            return NoTimestamp;
        }

        // Convert to 90kHz: timestamp * timeBase.num * 90000 / timeBase.den
        // Reordered to avoid overflow for common cases
        return timeBase.den == 0 ? timestamp : timestamp * timeBase.num * MpegTsClockRate / timeBase.den;
    }

    /// <summary>
    /// Converts a timestamp from MPEG-TS 90kHz units to FFmpeg time base.
    /// </summary>
    /// <param name="timestamp90Khz">The timestamp in 90kHz units.</param>
    /// <param name="timeBase">The target FFmpeg time base (num/den fraction).</param>
    /// <returns>The timestamp in FFmpeg time base units, or AV_NOPTS_VALUE if input is <see cref="NoTimestamp"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected static long ConvertFromMpegTsTimestamp(long timestamp90Khz, AVRational timeBase)
    {
        if (timestamp90Khz == NoTimestamp)
        {
            return ffmpeg.AV_NOPTS_VALUE;
        }

        // Convert from 90kHz: timestamp * timeBase.den / (timeBase.num * 90000)
        return timeBase.num == 0 ? timestamp90Khz : timestamp90Khz * timeBase.den / (timeBase.num * MpegTsClockRate);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes resources used by the processor.
    /// </summary>
    /// <param name="disposing">True if called from Dispose(), false if from finalizer.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (disposing)
        {
            // Dispose managed resources in derived classes
        }

        // Native resources cleaned up in derived classes
    }
}
