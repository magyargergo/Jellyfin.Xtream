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
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Xtream.Service.ProviderManagement;

/// <summary>
/// Orchestrates proactive channel warmup for Fast Channel Change (FCC).
/// </summary>
/// <remarks>
/// <para>
/// This service predicts which channels the user is likely to switch to and
/// pre-warms connections to those providers. Warmup triggers include:
/// <list type="bullet">
///   <item>EPG guide navigation (warm channels visible in guide)</item>
///   <item>Channel number proximity (warm ±3 channels from current)</item>
///   <item>Historical patterns (warm frequently-watched channels)</item>
///   <item>Time-based patterns (warm channels popular at current time)</item>
/// </list>
/// </para>
/// <para>
/// Industry research shows that 80% of channel changes are to adjacent channels
/// or recently-viewed channels, making predictive warmup highly effective.
/// </para>
/// <para>
/// Target: Sub-200ms channel change latency (industry gold standard).
/// </para>
/// </remarks>
public interface IChannelWarmupService : IDisposable
{
    /// <summary>
    /// Gets warmup statistics for monitoring and optimization.
    /// </summary>
    ChannelWarmupStatistics Statistics { get; }

    /// <summary>
    /// Notifies the service that a channel is currently being viewed.
    /// Triggers warmup of adjacent and related channels.
    /// </summary>
    /// <param name="channelId">The current channel ID.</param>
    /// <param name="providerUrls">URLs for all providers serving this channel.</param>
    void OnChannelViewing(string channelId, IReadOnlyList<string> providerUrls);

    /// <summary>
    /// Notifies the service that the user is browsing the EPG guide.
    /// Triggers warmup of channels visible in the guide.
    /// </summary>
    /// <param name="visibleChannelIds">Channel IDs currently visible in the guide.</param>
    void OnGuideNavigation(IReadOnlyList<string> visibleChannelIds);

    /// <summary>
    /// Pre-warms connections for a specific channel across all its providers.
    /// </summary>
    /// <param name="channelId">The channel ID to warm.</param>
    /// <param name="providerUrls">URLs for all providers serving this channel.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The warmup result.</returns>
    Task<ChannelWarmupResult> WarmChannelAsync(
        string channelId,
        IReadOnlyList<string> providerUrls,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Pre-warms connections for multiple channels in priority order.
    /// Useful for warming adjacent channels or guide-visible channels.
    /// </summary>
    /// <param name="channels">Channels to warm with their provider URLs, in priority order.</param>
    /// <param name="maxConcurrent">Maximum concurrent warmup operations.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Warmup results for each channel.</returns>
    Task<IReadOnlyList<ChannelWarmupResult>> WarmChannelsAsync(
        IReadOnlyList<ChannelWarmupRequest> channels,
        int maxConcurrent = 3,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Cancels any pending warmup operations for a channel.
    /// Called when user starts watching a different channel.
    /// </summary>
    /// <param name="channelId">The channel ID to cancel warmup for.</param>
    void CancelWarmup(string channelId);

    /// <summary>
    /// Gets the warmup status for a specific channel.
    /// </summary>
    /// <param name="channelId">The channel ID.</param>
    /// <returns>The warmup status, or null if no warmup in progress or completed.</returns>
    ChannelWarmupStatus? GetWarmupStatus(string channelId);

    /// <summary>
    /// Gets the list of channels currently being warmed or ready.
    /// </summary>
    /// <returns>Channel IDs with active warmup.</returns>
    IReadOnlyList<string> GetWarmedChannels();
}

/// <summary>
/// Request to warm a specific channel.
/// </summary>
/// <param name="ChannelId">The channel ID.</param>
/// <param name="ProviderUrls">Provider URLs for this channel.</param>
/// <param name="Priority">Warmup priority (higher = more important).</param>
public sealed record ChannelWarmupRequest(string ChannelId, IReadOnlyList<string> ProviderUrls, int Priority = 0);

/// <summary>
/// Result of a channel warmup operation.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct ChannelWarmupResult : IEquatable<ChannelWarmupResult>
{
    /// <summary>Gets the channel ID.</summary>
    public string ChannelId { get; init; }

    /// <summary>Gets a value indicating whether warmup succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Gets the number of providers successfully warmed.</summary>
    public int WarmedProviders { get; init; }

    /// <summary>Gets the total number of providers attempted.</summary>
    public int TotalProviders { get; init; }

    /// <summary>Gets the warmup duration in milliseconds.</summary>
    public long DurationMs { get; init; }

    /// <summary>Gets the failure message if warmup failed.</summary>
    public string? FailureMessage { get; init; }

    /// <summary>Creates a successful result.</summary>
    public static ChannelWarmupResult Succeeded(
        string channelId,
        int warmedProviders,
        int totalProviders,
        long durationMs
    ) =>
        new()
        {
            ChannelId = channelId,
            Success = true,
            WarmedProviders = warmedProviders,
            TotalProviders = totalProviders,
            DurationMs = durationMs,
        };

    /// <summary>Creates a failed result.</summary>
    public static ChannelWarmupResult Failed(string channelId, string message) =>
        new()
        {
            ChannelId = channelId,
            Success = false,
            FailureMessage = message,
        };

    /// <inheritdoc />
    public bool Equals(ChannelWarmupResult other) => ChannelId == other.ChannelId && Success == other.Success;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ChannelWarmupResult other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(ChannelId, Success);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(ChannelWarmupResult left, ChannelWarmupResult right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(ChannelWarmupResult left, ChannelWarmupResult right) => !left.Equals(right);
}

/// <summary>
/// Current warmup status for a channel.
/// </summary>
public enum ChannelWarmupStatus
{
    /// <summary>No warmup in progress.</summary>
    None,

    /// <summary>Warmup is in progress.</summary>
    InProgress,

    /// <summary>Warmup completed, connections are ready.</summary>
    Ready,

    /// <summary>Warmup failed.</summary>
    Failed,

    /// <summary>Warmed connections have expired.</summary>
    Expired,
}

/// <summary>
/// Aggregate statistics for channel warmup operations.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct ChannelWarmupStatistics : IEquatable<ChannelWarmupStatistics>
{
    /// <summary>Gets the total number of channel warmup requests.</summary>
    public long TotalWarmupRequests { get; init; }

    /// <summary>Gets the number of successful channel warmups.</summary>
    public long SuccessfulWarmups { get; init; }

    /// <summary>Gets the number of failed channel warmups.</summary>
    public long FailedWarmups { get; init; }

    /// <summary>Gets the number of cancelled warmups.</summary>
    public long CancelledWarmups { get; init; }

    /// <summary>Gets the number of times a pre-warmed channel was used (prediction hit).</summary>
    public long PredictionHits { get; init; }

    /// <summary>Gets the number of times a non-warmed channel was requested (prediction miss).</summary>
    public long PredictionMisses { get; init; }

    /// <summary>Gets the average warmup latency in milliseconds.</summary>
    public double AverageWarmupLatencyMs { get; init; }

    /// <summary>Gets the average time saved per channel switch in milliseconds.</summary>
    public double AverageTimeSavedMs { get; init; }

    /// <summary>Gets the warmup success rate as a percentage.</summary>
    public double SuccessRatePercent =>
        TotalWarmupRequests > 0 ? (double)SuccessfulWarmups / TotalWarmupRequests * 100 : 0;

    /// <summary>Gets the prediction accuracy as a percentage.</summary>
    public double PredictionAccuracyPercent =>
        PredictionHits + PredictionMisses > 0 ? (double)PredictionHits / (PredictionHits + PredictionMisses) * 100 : 0;

    /// <inheritdoc />
    public bool Equals(ChannelWarmupStatistics other) =>
        TotalWarmupRequests == other.TotalWarmupRequests && PredictionHits == other.PredictionHits;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ChannelWarmupStatistics other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(TotalWarmupRequests, PredictionHits);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(ChannelWarmupStatistics left, ChannelWarmupStatistics right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(ChannelWarmupStatistics left, ChannelWarmupStatistics right) => !left.Equals(right);
}
