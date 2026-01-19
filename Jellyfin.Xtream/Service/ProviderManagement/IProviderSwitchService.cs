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
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Service.MpegTs.TsDuck;

namespace Jellyfin.Xtream.Service.ProviderManagement;

/// <summary>
/// Unified service for provider switching during active streams.
/// Handles stream lifecycle, provider failover, and seamless A/V transitions.
/// </summary>
/// <remarks>
/// This is the single entry point for all provider switching needs:
/// - Stream lifecycle management (register/unregister)
/// - Provider switching with MPEG-TS byte alignment
/// - Timestamp remapping for seamless A/V sync
/// - Cooldown and rate limiting
/// </remarks>
public interface IProviderSwitchService : IDisposable
{
    /// <summary>
    /// Gets the number of active streams being managed.
    /// </summary>
    int ActiveStreamCount { get; }

    /// <summary>
    /// Gets aggregate statistics about switch operations.
    /// </summary>
    SwitchStatistics Statistics { get; }

    /// <summary>
    /// Registers a stream for provider switching management.
    /// Call this when a stream starts.
    /// </summary>
    /// <param name="streamId">Unique stream/session identifier.</param>
    /// <param name="currentUrl">The current stream URL.</param>
    void RegisterStream(string streamId, string currentUrl);

    /// <summary>
    /// Unregisters a stream from provider switching management.
    /// Call this when a stream ends or is disposed.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    void UnregisterStream(string streamId);

    /// <summary>
    /// Attempts to switch to an alternative provider.
    /// Handles URL resolution, MPEG-TS alignment, and timestamp remapping internally.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="currentUrl">The current stream URL.</param>
    /// <param name="reason">The reason for the switch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the switch attempt.</returns>
    Task<ProviderSwitchResult> TrySwitchAsync(
        string streamId,
        string currentUrl,
        SwitchReason reason,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Checks if a switch can be attempted for the given stream.
    /// Returns false if cooldown is active or max attempts reached.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <returns>True if a switch can be attempted.</returns>
    bool CanSwitch(string streamId);

    /// <summary>
    /// Records a throughput sample for quality monitoring.
    /// Call this periodically during streaming to enable proactive switching.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="bytesWritten">Total bytes written since stream start.</param>
    void RecordThroughput(string streamId, long bytesWritten);

    /// <summary>
    /// Forwards TsDuck TR 101 290 metrics to the stream's quality monitor.
    /// Call this when TsDuck metrics are updated to enable combined quality assessment.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="metrics">The TsDuck metrics.</param>
    void RecordTsDuckMetrics(string streamId, TsDuckMetrics metrics);

    /// <summary>
    /// Updates the internal timing state used for timestamp remapping during provider switches.
    /// Call this periodically during normal streaming to keep the remapping service aware
    /// of current PTS/PCR values.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="videoPts">The last video PTS (90kHz clock), or 0 if unknown.</param>
    /// <param name="audioPts">The last audio PTS (90kHz clock), or 0 if unknown.</param>
    /// <param name="pcr">The last PCR (27MHz clock), or 0 if unknown.</param>
    void UpdateTimingState(string streamId, long videoPts, long audioPts, long pcr);

    /// <summary>
    /// Updates the parameter set cache (SPS/PPS/VPS) from streaming data.
    /// Call this periodically to keep cached parameter sets fresh for injection during switches.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="data">MPEG-TS data to scan for parameter sets.</param>
    void UpdateParameterSetCache(string streamId, ReadOnlySpan<byte> data);

    /// <summary>
    /// Activates timestamp remapping for a same-provider reconnection.
    /// Call this after reconnecting when receiving the first data chunk from the new connection.
    /// This extracts the first video PTS from the data and activates remapping based on
    /// the timing state previously set via <see cref="UpdateTimingState"/>.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="firstDataChunk">The first data chunk from the reconnected stream.</param>
    /// <returns>True if remapping was activated, false otherwise.</returns>
    bool ActivateReconnectionRemapping(string streamId, ReadOnlySpan<byte> firstDataChunk);

    /// <summary>
    /// Activates timestamp remapping immediately when a PTS discontinuity is detected mid-stream.
    /// Unlike <see cref="ActivateReconnectionRemapping"/>, this uses known PTS values directly
    /// rather than extracting from a data chunk.
    /// </summary>
    /// <remarks>
    /// Call this when the stream parser detects a PTS jump that would cause video jumping/looping.
    /// The remapping service will calculate an offset to make timestamps continuous.
    /// </remarks>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="previousPts">The PTS value before the discontinuity (baseline).</param>
    /// <param name="newPts">The PTS value after the discontinuity (will be remapped).</param>
    void ActivateDiscontinuityRemapping(string streamId, long previousPts, long newPts);

    /// <summary>
    /// Processes MPEG-TS data through FFmpeg subprocess for proper A/V synchronization.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method feeds input data to an FFmpeg remuxing subprocess, which provides:
    /// <list type="bullet">
    ///   <item><description>Proper audio/video synchronization via genpts/igndts flags</description></item>
    ///   <item><description>PCR regeneration and timestamp correction</description></item>
    ///   <item><description>Automatic discontinuity handling</description></item>
    ///   <item><description>33-bit timestamp wraparound handling</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Due to FFmpeg's internal buffering, output data is returned via the out parameter
    /// and may differ in size from the input.
    /// </para>
    /// </remarks>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="input">Input MPEG-TS data.</param>
    /// <param name="output">Remuxed output data, or null if no output is available yet.</param>
    /// <returns>True if the pipeline is active and processing, false otherwise.</returns>
    bool ProcessDataForRemapping(string streamId, ReadOnlySpan<byte> input, out byte[]? output);

    /// <summary>
    /// Gets a value indicating whether timestamp remapping is currently active for a stream.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <returns>True if remapping is active for the stream.</returns>
    bool IsRemappingActive(string streamId);

    /// <summary>
    /// Resets the in-process remuxer for a stream.
    /// Call this during reconnection to allow the FFmpeg context to reinitialize
    /// with fresh PAT/PMT from the new stream.
    /// </summary>
    /// <remarks>
    /// Without calling this during reconnection, the FFmpeg demuxer retains stale
    /// stream mapping from before the disconnection. When the new stream arrives
    /// (potentially with different PIDs or stream layout), the demuxer cannot
    /// produce output, causing all data to be silently dropped.
    /// </remarks>
    /// <param name="streamId">The stream identifier.</param>
    void ResetRemuxer(string streamId);

    /// <summary>
    /// Gets whether the remuxer is producing output at a healthy rate.
    /// </summary>
    /// <remarks>
    /// Returns false if the remuxer has consumed significant input (&gt;1MB) but output ratio is &lt;5%.
    /// This indicates the remuxer is stuck and should be reset.
    /// </remarks>
    /// <param name="streamId">The stream identifier.</param>
    /// <returns>True if remuxer is healthy or not yet initialized; false if stuck.</returns>
    bool IsRemuxerHealthy(string streamId);

    /// <summary>
    /// Disables the remuxer for a stream session after repeated failures.
    /// </summary>
    /// <remarks>
    /// When called, the remuxer will be bypassed and raw data will flow directly
    /// to the buffer for the remainder of this stream session. This prevents
    /// repeated init/stuck/reset cycles that waste data and delay playback.
    /// </remarks>
    /// <param name="streamId">The stream identifier.</param>
    void DisableRemuxer(string streamId);
}

/// <summary>
/// Result of a provider switch attempt.
/// </summary>
public readonly struct ProviderSwitchResult : IEquatable<ProviderSwitchResult>
{
    /// <summary>
    /// Gets a value indicating whether the switch was successful.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Gets the new URL to use (if successful).
    /// </summary>
    public string? NewUrl { get; init; }

    /// <summary>
    /// Gets the switch duration in milliseconds.
    /// </summary>
    public long DurationMs { get; init; }

    /// <summary>
    /// Gets a value indicating whether timestamp remapping was activated.
    /// </summary>
    public bool TimestampRemappingActive { get; init; }

    /// <summary>
    /// Gets a value indicating whether the switch aligned to a keyframe.
    /// </summary>
    public bool AlignedToKeyframe { get; init; }

    /// <summary>
    /// Gets the aligned data from the new provider that should be written to the buffer.
    /// This data has been processed for timestamp remapping and starts at a keyframe boundary.
    /// CRITICAL: Callers MUST write this data to the buffer to avoid video jumping and decoder errors.
    /// </summary>
    public byte[]? AlignedData { get; init; }

    /// <summary>
    /// Gets the failure reason if not successful.
    /// </summary>
    public SwitchFailureReason FailureReason { get; init; }

    /// <summary>
    /// Gets the failure message (if any).
    /// </summary>
    public string? FailureMessage { get; init; }

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static ProviderSwitchResult Succeeded(
        string newUrl,
        long durationMs,
        bool timestampRemappingActive = false,
        bool alignedToKeyframe = false,
        byte[]? alignedData = null
    ) =>
        new()
        {
            Success = true,
            NewUrl = newUrl,
            DurationMs = durationMs,
            TimestampRemappingActive = timestampRemappingActive,
            AlignedToKeyframe = alignedToKeyframe,
            AlignedData = alignedData,
            FailureReason = SwitchFailureReason.None,
        };

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    public static ProviderSwitchResult Failed(SwitchFailureReason reason, string? message = null) =>
        new()
        {
            Success = false,
            FailureReason = reason,
            FailureMessage = message,
        };

    /// <summary>Cached result for cooldown active.</summary>
    public static readonly ProviderSwitchResult CooldownActive = Failed(SwitchFailureReason.CooldownActive);

    /// <summary>Cached result for max attempts reached.</summary>
    public static readonly ProviderSwitchResult MaxAttemptsReached = Failed(SwitchFailureReason.MaxAttemptsReached);

    /// <summary>Cached result for stream not registered.</summary>
    public static readonly ProviderSwitchResult StreamNotRegistered = Failed(SwitchFailureReason.StreamNotRegistered);

    /// <inheritdoc />
    public bool Equals(ProviderSwitchResult other) => Success == other.Success && FailureReason == other.FailureReason;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ProviderSwitchResult other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Success, FailureReason);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(ProviderSwitchResult left, ProviderSwitchResult right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(ProviderSwitchResult left, ProviderSwitchResult right) => !left.Equals(right);
}

/// <summary>
/// Reasons for switch failure.
/// </summary>
public enum SwitchFailureReason
{
    /// <summary>No failure.</summary>
    None,

    /// <summary>Stream is not registered with the service.</summary>
    StreamNotRegistered,

    /// <summary>Cooldown period is active.</summary>
    CooldownActive,

    /// <summary>Maximum switch attempts reached for this session.</summary>
    MaxAttemptsReached,

    /// <summary>No alternative provider available.</summary>
    NoAlternativeProvider,

    /// <summary>Switch operation timed out.</summary>
    Timeout,

    /// <summary>Connection to new provider failed.</summary>
    ConnectionFailed,

    /// <summary>Stream alignment failed.</summary>
    AlignmentFailed,

    /// <summary>Unexpected error occurred.</summary>
    Error,
}

/// <summary>
/// Reasons for initiating a provider switch.
/// </summary>
public enum SwitchReason
{
    /// <summary>Current provider health is degraded.</summary>
    HealthDegraded,

    /// <summary>Connection to current provider failed.</summary>
    ConnectionFailed,

    /// <summary>Current provider capacity reached.</summary>
    CapacityReached,

    /// <summary>A better provider became available.</summary>
    BetterProviderAvailable,

    /// <summary>Manual switch requested.</summary>
    Manual,
}

/// <summary>
/// Aggregate statistics about switch operations.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct SwitchStatistics : IEquatable<SwitchStatistics>
{
    /// <summary>Gets the number of active streams.</summary>
    public int ActiveStreams { get; init; }

    /// <summary>Gets the total switches attempted.</summary>
    public long TotalSwitches { get; init; }

    /// <summary>Gets the number of successful switches.</summary>
    public long SuccessfulSwitches { get; init; }

    /// <summary>Gets the number of failed switches.</summary>
    public long FailedSwitches { get; init; }

    /// <summary>Gets the average switch latency in milliseconds.</summary>
    public double AverageSwitchLatencyMs { get; init; }

    /// <summary>Gets the success rate as a percentage.</summary>
    public double SuccessRatePercent => TotalSwitches > 0 ? (double)SuccessfulSwitches / TotalSwitches * 100 : 0;

    /// <inheritdoc />
    public bool Equals(SwitchStatistics other) =>
        TotalSwitches == other.TotalSwitches && ActiveStreams == other.ActiveStreams;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SwitchStatistics other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(TotalSwitches, ActiveStreams);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(SwitchStatistics left, SwitchStatistics right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(SwitchStatistics left, SwitchStatistics right) => !left.Equals(right);
}
