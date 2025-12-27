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
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Service.ProviderManagement;

namespace Jellyfin.Xtream.Service.Switching;

/// <summary>
/// Manages hot-swap stream switching for active streams.
/// </summary>
public interface IHotSwapStreamManager : IDisposable
{
    /// <summary>
    /// Gets the number of active streams being managed.
    /// </summary>
    int ActiveStreamCount { get; }

    /// <summary>
    /// Gets the total number of switches attempted.
    /// </summary>
    long TotalSwitches { get; }

    /// <summary>
    /// Gets the number of successful switches.
    /// </summary>
    long SuccessfulSwitches { get; }

    /// <summary>
    /// Gets the number of failed switches.
    /// </summary>
    long FailedSwitches { get; }

    /// <summary>
    /// Gets the average switch latency in milliseconds.
    /// </summary>
    double AverageSwitchLatencyMs { get; }

    /// <summary>
    /// Registers a stream for hot-swap management.
    /// </summary>
    /// <param name="sessionId">Unique session ID.</param>
    /// <param name="channelId">The channel being streamed.</param>
    /// <param name="provider">The initial provider.</param>
    void RegisterStream(string sessionId, int channelId, ProviderStreamInfo provider);

    /// <summary>
    /// Unregisters a stream from hot-swap management.
    /// </summary>
    /// <param name="sessionId">The session ID to unregister.</param>
    void UnregisterStream(string sessionId);

    /// <summary>
    /// Checks if a switch should be initiated for a stream.
    /// </summary>
    /// <param name="sessionId">The session ID to check.</param>
    /// <param name="alternativeProviders">Available alternative providers.</param>
    /// <returns>The recommended provider to switch to, or null if no switch needed.</returns>
    ProviderStreamInfo? ShouldSwitch(string sessionId, IEnumerable<ProviderStreamInfo> alternativeProviders);

    /// <summary>
    /// Initiates a hot-swap to a new provider.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="targetProvider">The provider to switch to.</param>
    /// <param name="reason">The reason for the switch.</param>
    /// <param name="switchCallback">Callback to perform the actual stream handoff.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the switch operation.</returns>
    Task<SwitchResult> SwitchAsync(
        string sessionId,
        ProviderStreamInfo targetProvider,
        SwitchReason reason,
        Func<ProviderStreamInfo, CancellationToken, Task<bool>> switchCallback,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Initiates a hot-swap with MPEG-TS byte alignment for seamless switching.
    /// Aligns the new stream to packet boundaries and optionally waits for a keyframe.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="targetProvider">The provider to switch to.</param>
    /// <param name="newStream">The new stream to align and switch to.</param>
    /// <param name="reason">The reason for the switch.</param>
    /// <param name="waitForKeyframe">Whether to wait for a keyframe before switching.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result including aligned data ready for seamless playback.</returns>
    Task<AlignedSwitchInfo> SwitchWithAlignmentAsync(
        string sessionId,
        ProviderStreamInfo targetProvider,
        Stream newStream,
        SwitchReason reason,
        bool waitForKeyframe = true,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Gets switching statistics for a specific stream/session.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <returns>The switch stats, or null if session not found.</returns>
    StreamSwitchStats? GetStreamStats(string sessionId);

    /// <summary>
    /// Records a throughput sample for quality monitoring.
    /// Call this periodically (every 100-500ms) during streaming.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="totalBytesWritten">Total bytes written to the stream buffer.</param>
    void RecordThroughputSample(string sessionId, long totalBytesWritten);

    /// <summary>
    /// Gets the current stream quality for a session.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <returns>The current quality, or null if session not found.</returns>
    StreamQuality? GetStreamQuality(string sessionId);

    /// <summary>
    /// Checks if preconnection to backup providers should be initiated.
    /// Returns true if quality is degrading but not yet critical.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <returns>True if preconnection is recommended.</returns>
    bool ShouldPreconnect(string sessionId);
}

/// <summary>
/// Result of an aligned switch operation with MPEG-TS byte alignment.
/// </summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
public readonly struct AlignedSwitchInfo : IEquatable<AlignedSwitchInfo>
{
    /// <summary>Gets a value indicating whether the switch was successful.</summary>
    public bool Success { get; init; }

    /// <summary>Gets the previous provider.</summary>
    public ProviderStreamInfo? PreviousProvider { get; init; }

    /// <summary>Gets the new provider.</summary>
    public ProviderStreamInfo? NewProvider { get; init; }

    /// <summary>Gets the aligned data ready for seamless playback.</summary>
    public ReadOnlyMemory<byte> AlignedData { get; init; }

    /// <summary>Gets a value indicating whether the data is aligned to a keyframe.</summary>
    public bool AlignedToKeyframe { get; init; }

    /// <summary>Gets the number of bytes discarded for alignment.</summary>
    public int BytesDiscarded { get; init; }

    /// <summary>Gets the switch duration in milliseconds.</summary>
    public int DurationMs { get; init; }

    /// <summary>Gets the failure reason if not successful.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Creates a successful aligned switch result.</summary>
    public static AlignedSwitchInfo Succeeded(
        ProviderStreamInfo previousProvider,
        ProviderStreamInfo newProvider,
        ReadOnlyMemory<byte> alignedData,
        bool alignedToKeyframe,
        int bytesDiscarded,
        int durationMs
    ) =>
        new()
        {
            Success = true,
            PreviousProvider = previousProvider,
            NewProvider = newProvider,
            AlignedData = alignedData,
            AlignedToKeyframe = alignedToKeyframe,
            BytesDiscarded = bytesDiscarded,
            DurationMs = durationMs,
        };

    /// <summary>Creates a failed switch result.</summary>
    public static AlignedSwitchInfo Failed(ProviderStreamInfo provider, string reason) =>
        new()
        {
            Success = false,
            PreviousProvider = provider,
            FailureReason = reason,
        };

    /// <summary>Equality operator.</summary>
    public static bool operator ==(AlignedSwitchInfo left, AlignedSwitchInfo right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(AlignedSwitchInfo left, AlignedSwitchInfo right) => !left.Equals(right);

    /// <inheritdoc />
    public bool Equals(AlignedSwitchInfo other) => Success == other.Success && DurationMs == other.DurationMs;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is AlignedSwitchInfo other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Success, DurationMs);
}
