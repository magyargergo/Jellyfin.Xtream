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
using System.Runtime.InteropServices;

namespace Jellyfin.Xtream.Service.MpegTs.UseCases;

/// <summary>
/// Statistics from the in-process remuxer.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct InProcessRemuxerStatistics : IEquatable<InProcessRemuxerStatistics>
{
    /// <summary>Gets the total bytes written to the remuxer.</summary>
    public long BytesWritten { get; init; }

    /// <summary>Gets the total bytes read from the remuxer.</summary>
    public long BytesRead { get; init; }

    /// <summary>Gets the number of packets processed.</summary>
    public long PacketsProcessed { get; init; }

    /// <summary>Gets the number of provider switches handled.</summary>
    public int ProviderSwitches { get; init; }

    /// <summary>Gets the current timestamp offset being applied (in 90kHz units).</summary>
    public long CurrentTimestampOffset { get; init; }

    /// <summary>Gets the last output PTS value (in 90kHz units).</summary>
    public long LastOutputPts { get; init; }

    /// <summary>Gets a value indicating whether the remuxer is running.</summary>
    public bool IsRunning { get; init; }

    /// <summary>Gets the number of corrupt packets discarded.</summary>
    public long CorruptPacketsDiscarded { get; init; }

    /// <inheritdoc />
    public bool Equals(InProcessRemuxerStatistics other) =>
        BytesWritten == other.BytesWritten
        && BytesRead == other.BytesRead
        && PacketsProcessed == other.PacketsProcessed
        && IsRunning == other.IsRunning;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is InProcessRemuxerStatistics other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(BytesWritten, BytesRead, PacketsProcessed, IsRunning);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(InProcessRemuxerStatistics left, InProcessRemuxerStatistics right) =>
        left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(InProcessRemuxerStatistics left, InProcessRemuxerStatistics right) =>
        !left.Equals(right);
}

/// <summary>
/// Result of processing data through the in-process remuxer.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct RemuxResult : IEquatable<RemuxResult>
{
    /// <summary>Gets a value indicating whether processing was successful.</summary>
    public bool Success { get; init; }

    /// <summary>Gets the remuxed output data, or null if no output is available yet.</summary>
    public byte[]? Output { get; init; }

    /// <summary>Gets the number of bytes consumed from input.</summary>
    public int BytesConsumed { get; init; }

    /// <summary>Gets the error message if processing failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Creates a successful result with output data.</summary>
    public static RemuxResult Ok(byte[]? output, int bytesConsumed) =>
        new()
        {
            Success = true,
            Output = output,
            BytesConsumed = bytesConsumed,
        };

    /// <summary>Creates a failure result.</summary>
    public static RemuxResult Fail(string error) => new() { Success = false, ErrorMessage = error };

    /// <summary>Creates a result indicating the remuxer is still initializing.</summary>
    public static RemuxResult Initializing(int bytesConsumed) =>
        new()
        {
            Success = true,
            Output = null,
            BytesConsumed = bytesConsumed,
        };

    /// <inheritdoc />
    public bool Equals(RemuxResult other) => Success == other.Success && BytesConsumed == other.BytesConsumed;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RemuxResult other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Success, BytesConsumed);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(RemuxResult left, RemuxResult right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(RemuxResult left, RemuxResult right) => !left.Equals(right);
}

/// <summary>
/// In-process MPEG-TS remuxer using FFmpeg.AutoGen for proper A/V synchronization.
/// </summary>
/// <remarks>
/// <para>
/// This interface provides in-process timestamp correction and remuxing without
/// spawning a subprocess. It uses FFmpeg's libavformat directly via FFmpeg.AutoGen
/// to achieve:
/// </para>
/// <list type="bullet">
///   <item><description>Proper audio/video synchronization via genpts/igndts</description></item>
///   <item><description>Timestamp continuity across provider switches</description></item>
///   <item><description>Clean PAT/PMT/PCR regeneration</description></item>
///   <item><description>33-bit timestamp wraparound handling</description></item>
///   <item><description>Low latency (no subprocess/pipe overhead)</description></item>
/// </list>
/// </remarks>
public interface IInProcessRemuxer : IDisposable
{
    /// <summary>
    /// Gets a value indicating whether the remuxer is initialized and running.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Gets the current remuxer statistics.
    /// </summary>
    InProcessRemuxerStatistics Statistics { get; }

    /// <summary>
    /// Initializes the remuxer for the specified stream.
    /// </summary>
    /// <param name="streamId">Unique identifier for the stream.</param>
    /// <returns>True if initialization was successful.</returns>
    bool Initialize(string streamId);

    /// <summary>
    /// Processes input MPEG-TS data and returns remuxed output.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method feeds input data through the FFmpeg demuxer/muxer pipeline.
    /// The demuxer applies genpts/igndts to correct incoming timestamps,
    /// then the muxer outputs clean MPEG-TS with proper PAT/PMT/PCR.
    /// </para>
    /// <para>
    /// Due to FFmpeg's internal buffering and the need to detect stream parameters,
    /// output may not be available immediately. Call this method repeatedly with
    /// input data until output becomes available.
    /// </para>
    /// </remarks>
    /// <param name="input">Input MPEG-TS data from the provider.</param>
    /// <returns>Result containing remuxed output or status information.</returns>
    RemuxResult ProcessData(ReadOnlySpan<byte> input);

    /// <summary>
    /// Signals a provider switch to the remuxer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When switching providers, the remuxer will:
    /// </para>
    /// <list type="number">
    ///   <item><description>Record the last output PTS as the continuation point</description></item>
    ///   <item><description>Calculate the offset needed to make new provider timestamps continuous</description></item>
    ///   <item><description>Apply the offset to all subsequent packets</description></item>
    /// </list>
    /// <para>
    /// This ensures seamless playback across provider switches without timestamp jumps.
    /// </para>
    /// </remarks>
    /// <param name="fromProvider">Previous provider identifier (for logging).</param>
    /// <param name="toProvider">New provider identifier (for logging).</param>
    void NotifyProviderSwitch(string? fromProvider, string? toProvider);

    /// <summary>
    /// Flushes any buffered data and returns remaining output.
    /// </summary>
    /// <returns>Any remaining buffered output data.</returns>
    byte[]? Flush();

    /// <summary>
    /// Resets the remuxer state for reuse with a new stream.
    /// </summary>
    void Reset();
}
