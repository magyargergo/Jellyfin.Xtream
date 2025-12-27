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
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Service.ProviderManagement;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.Switching;

/// <summary>
/// Manages hot-swap stream switching for active streams.
/// Coordinates preconnect pool, switch strategies, and stream handoff.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="HotSwapStreamManager"/> class.
/// </remarks>
public sealed class HotSwapStreamManager(
    IProviderAvailabilityService resilienceService,
    IAutomaticFailoverService failoverService,
    IPreconnectPool preconnectPool,
    ILogger<HotSwapStreamManager> logger
) : IHotSwapStreamManager
{
    private const int DefaultSwitchTimeoutMs = 5000;

    private readonly IProviderAvailabilityService _resilienceService = resilienceService;
    private readonly IAutomaticFailoverService _failoverService = failoverService;
    private readonly IPreconnectPool _preconnectPool = preconnectPool;
    private readonly ILogger<HotSwapStreamManager> _logger = logger;
    private readonly ConcurrentDictionary<string, ActiveStream> _activeStreams = new ConcurrentDictionary<
        string,
        ActiveStream
    >(StringComparer.Ordinal);
    private readonly SemaphoreSlim _switchLock = new SemaphoreSlim(1, 1);

    private volatile bool _disposed;
    private long _totalSwitches;
    private long _successfulSwitches;
    private long _failedSwitches;
    private long _totalSwitchLatencyMs;

    /// <summary>
    /// Represents an active stream being managed for hot-swap.
    /// </summary>
    private sealed class ActiveStream : IDisposable
    {
        public required string SessionId { get; init; }
        public required int ChannelId { get; init; }
        public required ProviderStreamInfo CurrentProvider { get; set; }
        public DateTime LastSwitchTime { get; set; }
        public int SwitchCount { get; set; }
        public long LastSwitchLatencyMs { get; set; }
        public SemaphoreSlim SwitchLock { get; } = new(1, 1);

        /// <summary>
        /// Gets the quality monitor for proactive switching detection.
        /// </summary>
        public StreamQualityMonitor QualityMonitor { get; } = new();

        /// <summary>
        /// Gets the aligned stream switcher for seamless MPEG-TS switching.
        /// </summary>
        public AlignedStreamSwitcher StreamSwitcher { get; init; } = null!;

        /// <summary>
        /// Gets the adaptive cooldown strategy for this stream.
        /// </summary>
        public AdaptiveCooldownStrategy CooldownStrategy { get; } = new();

        public void Dispose() => SwitchLock.Dispose();
    }

    /// <summary>
    /// Gets the number of active streams being managed.
    /// </summary>
    public int ActiveStreamCount => _activeStreams.Count;

    /// <summary>
    /// Gets the total number of switches attempted.
    /// </summary>
    public long TotalSwitches => Interlocked.Read(ref _totalSwitches);

    /// <summary>
    /// Gets the number of successful switches.
    /// </summary>
    public long SuccessfulSwitches => Interlocked.Read(ref _successfulSwitches);

    /// <summary>
    /// Gets the number of failed switches.
    /// </summary>
    public long FailedSwitches => Interlocked.Read(ref _failedSwitches);

    /// <summary>
    /// Gets the average switch latency in milliseconds.
    /// </summary>
    public double AverageSwitchLatencyMs
    {
        get
        {
            var successful = _successfulSwitches;
            return successful > 0 ? (double)_totalSwitchLatencyMs / successful : 0;
        }
    }

    /// <summary>
    /// Registers a stream for hot-swap management.
    /// </summary>
    /// <param name="sessionId">Unique session ID.</param>
    /// <param name="channelId">The channel being streamed.</param>
    /// <param name="provider">The initial provider.</param>
    public void RegisterStream(string sessionId, int channelId, ProviderStreamInfo provider)
    {
        var stream = new ActiveStream
        {
            SessionId = sessionId,
            ChannelId = channelId,
            CurrentProvider = provider,
            LastSwitchTime = DateTime.MinValue,
            StreamSwitcher = new AlignedStreamSwitcher(_logger),
        };

        _activeStreams.TryAdd(sessionId, stream);

        _logger.LogDebug(
            "Registered stream {SessionId} for channel {ChannelId} with provider {ProviderId}",
            sessionId,
            channelId,
            provider.Provider.Id
        );
    }

    /// <summary>
    /// Unregisters a stream from hot-swap management.
    /// </summary>
    /// <param name="sessionId">The session ID to unregister.</param>
    public void UnregisterStream(string sessionId)
    {
        if (_activeStreams.TryRemove(sessionId, out var stream))
        {
            var switchCount = stream.SwitchCount;
            stream.Dispose();

            _logger.LogDebug("Unregistered stream {SessionId}, total switches: {SwitchCount}", sessionId, switchCount);
        }
    }

    /// <summary>
    /// Checks if a switch should be initiated for a stream.
    /// Considers cooldown period, quality degradation, and provider health.
    /// </summary>
    /// <param name="sessionId">The session ID to check.</param>
    /// <param name="alternativeProviders">Available alternative providers.</param>
    /// <returns>The recommended provider to switch to, or null if no switch needed.</returns>
    public ProviderStreamInfo? ShouldSwitch(string sessionId, IEnumerable<ProviderStreamInfo> alternativeProviders)
    {
        if (!_activeStreams.TryGetValue(sessionId, out var stream))
        {
            return null;
        }

        if (!stream.CooldownStrategy.HasCooldownElapsed(stream.LastSwitchTime))
        {
            return null;
        }

        if (stream.QualityMonitor.ShouldTriggerSwitch())
        {
            var quality = stream.QualityMonitor.CurrentQuality;
            _logger.LogWarning(
                "Quality-triggered switch for session {SessionId}: quality={Quality}, throughput={Throughput}bps, cooldown={CooldownMs}ms",
                sessionId,
                quality,
                stream.QualityMonitor.CurrentThroughput,
                stream.CooldownStrategy.CurrentCooldownMs
            );

            var ordered = _failoverService.GetOrderedProviders(alternativeProviders);
            if (ordered.Count > 0 && ordered[0].Provider.Id != stream.CurrentProvider.Provider.Id)
            {
                return ordered[0];
            }
        }

        return _failoverService.ShouldSwitchProvider(stream.CurrentProvider.Provider.Id, alternativeProviders);
    }

    /// <summary>
    /// Records a throughput sample for quality monitoring.
    /// Call this periodically (every 100-500ms) during streaming.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="totalBytesWritten">Total bytes written to the stream buffer.</param>
    public void RecordThroughputSample(string sessionId, long totalBytesWritten)
    {
        if (_activeStreams.TryGetValue(sessionId, out var stream))
        {
            stream.QualityMonitor.RecordSample(totalBytesWritten);
        }
    }

    /// <summary>
    /// Gets the current stream quality for a session.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <returns>The current quality, or null if session not found.</returns>
    public StreamQuality? GetStreamQuality(string sessionId) =>
        _activeStreams.TryGetValue(sessionId, out var stream) ? stream.QualityMonitor.CurrentQuality : null;

    /// <summary>
    /// Checks if preconnection to backup providers should be initiated.
    /// Returns true if quality is degrading but not yet critical.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <returns>True if preconnection is recommended.</returns>
    public bool ShouldPreconnect(string sessionId) =>
        _activeStreams.TryGetValue(sessionId, out var stream) && stream.QualityMonitor.ShouldPreconnect();

    /// <summary>
    /// Initiates a hot-swap to a new provider.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="targetProvider">The provider to switch to.</param>
    /// <param name="reason">The reason for the switch.</param>
    /// <param name="switchCallback">Callback to perform the actual stream handoff.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the switch operation.</returns>
    public async Task<SwitchResult> SwitchAsync(
        string sessionId,
        ProviderStreamInfo targetProvider,
        SwitchReason reason,
        Func<ProviderStreamInfo, CancellationToken, Task<bool>> switchCallback,
        CancellationToken cancellationToken = default
    )
    {
        if (!_activeStreams.TryGetValue(sessionId, out var stream))
        {
            return SwitchResult.Failed(targetProvider, "Stream session not found");
        }

        // Acquire per-stream lock to prevent concurrent switches
        if (!await stream.SwitchLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return SwitchResult.Failed(stream.CurrentProvider, "Switch already in progress");
        }

        var stopwatch = Stopwatch.StartNew();
        Interlocked.Increment(ref _totalSwitches);

        try
        {
            var previousProvider = stream.CurrentProvider;

            _logger.LogInformation(
                "Initiating hot-swap for session {SessionId}: {FromProvider} -> {ToProvider} (reason: {Reason})",
                sessionId,
                previousProvider.Provider.Id,
                targetProvider.Provider.Id,
                reason
            );

            // Try to use preconnected stream first
            if (
                _preconnectPool.TryGetConnection(
                    targetProvider.Provider.Id,
                    targetProvider.Stream.StreamId,
                    out var pooledConnection
                )
            )
            {
                _logger.LogDebug("Using preconnected stream (age: {AgeMs}ms)", pooledConnection!.AgeMs);

                // Preconnected path - very fast
                try
                {
                    var success = await switchCallback(targetProvider, cancellationToken).ConfigureAwait(false);

                    if (success)
                    {
                        return CompleteSuccessfulSwitch(
                            stream,
                            previousProvider,
                            targetProvider,
                            stopwatch.ElapsedMilliseconds,
                            false
                        );
                    }
                }
                finally
                {
                    pooledConnection.Dispose();
                }
            }

            // Cold connect path
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(DefaultSwitchTimeoutMs);

            try
            {
                var success = await switchCallback(targetProvider, timeoutCts.Token).ConfigureAwait(false);

                if (success)
                {
                    return CompleteSuccessfulSwitch(
                        stream,
                        previousProvider,
                        targetProvider,
                        stopwatch.ElapsedMilliseconds,
                        true
                    );
                }
                else
                {
                    return CompleteFailedSwitch(stream, previousProvider, "Switch callback returned false");
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                return CompleteFailedSwitch(stream, previousProvider, "Switch timed out");
            }
        }
        catch (Exception ex)
        {
            return CompleteFailedSwitch(stream, stream.CurrentProvider, $"Switch failed: {ex.Message}", ex);
        }
        finally
        {
            stream.SwitchLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<AlignedSwitchInfo> SwitchWithAlignmentAsync(
        string sessionId,
        ProviderStreamInfo targetProvider,
        Stream newStream,
        SwitchReason reason,
        bool waitForKeyframe = true,
        CancellationToken cancellationToken = default
    )
    {
        if (!_activeStreams.TryGetValue(sessionId, out var stream))
        {
            return AlignedSwitchInfo.Failed(targetProvider, "Stream session not found");
        }

        if (!await stream.SwitchLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return AlignedSwitchInfo.Failed(stream.CurrentProvider, "Switch already in progress");
        }

        var stopwatch = Stopwatch.StartNew();
        Interlocked.Increment(ref _totalSwitches);

        try
        {
            var previousProvider = stream.CurrentProvider;

            _logger.LogInformation(
                "Initiating aligned hot-swap for session {SessionId}: {FromProvider} -> {ToProvider} (reason: {Reason}, waitForKeyframe: {WaitForKeyframe})",
                sessionId,
                previousProvider.Provider.Id,
                targetProvider.Provider.Id,
                reason,
                waitForKeyframe
            );

            var (alignResult, alignedData) = await stream
                .StreamSwitcher.AlignStreamAsync(newStream, waitForKeyframe, cancellationToken)
                .ConfigureAwait(false);

            if (!alignResult.Success)
            {
                stream.CooldownStrategy.RecordResult(success: false);
                Interlocked.Increment(ref _failedSwitches);

                _logger.LogWarning(
                    "Aligned switch failed for session {SessionId}: {Error}",
                    sessionId,
                    alignResult.Error
                );

                return AlignedSwitchInfo.Failed(previousProvider, alignResult.Error ?? "Alignment failed");
            }

            stream.CurrentProvider = targetProvider;
            stream.LastSwitchTime = DateTime.UtcNow;
            stream.SwitchCount++;
            stream.LastSwitchLatencyMs = stopwatch.ElapsedMilliseconds;
            stream.CooldownStrategy.RecordResult(success: true);

            Interlocked.Increment(ref _successfulSwitches);
            Interlocked.Add(ref _totalSwitchLatencyMs, stopwatch.ElapsedMilliseconds);

            _resilienceService.RecordSuccess(targetProvider.Provider.Id);

            _logger.LogInformation(
                "Aligned hot-swap successful for session {SessionId}: {FromProvider} -> {ToProvider} in {LatencyMs}ms (keyframe: {Keyframe}, discarded: {Discarded} bytes)",
                sessionId,
                previousProvider.Provider.Id,
                targetProvider.Provider.Id,
                stopwatch.ElapsedMilliseconds,
                alignResult.AlignedToKeyframe,
                alignResult.BytesDiscarded
            );

            return AlignedSwitchInfo.Succeeded(
                previousProvider,
                targetProvider,
                alignedData,
                alignResult.AlignedToKeyframe,
                alignResult.BytesDiscarded,
                (int)stopwatch.ElapsedMilliseconds
            );
        }
        catch (Exception ex)
        {
            stream.CooldownStrategy.RecordResult(success: false);
            Interlocked.Increment(ref _failedSwitches);

            _logger.LogWarning(ex, "Aligned switch failed for session {SessionId}: {Error}", sessionId, ex.Message);

            return AlignedSwitchInfo.Failed(stream.CurrentProvider, $"Switch failed: {ex.Message}");
        }
        finally
        {
            stream.SwitchLock.Release();
        }
    }

    /// <summary>
    /// Triggers preconnection to backup providers for a stream.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="alternativeProviders">Available alternative providers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of providers preconnected.</returns>
    public async Task<int> PreconnectBackupsAsync(
        string sessionId,
        IEnumerable<ProviderStreamInfo> alternativeProviders,
        CancellationToken cancellationToken = default
    )
    {
        if (!_activeStreams.TryGetValue(sessionId, out var stream))
        {
            return 0;
        }

        return await _preconnectPool
            .PreconnectToBackupsAsync(
                stream.CurrentProvider.Provider.Id,
                stream.CurrentProvider.Stream.StreamId,
                alternativeProviders,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Gets statistics for a specific stream.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <returns>Stream statistics, or null if not found.</returns>
    public StreamSwitchStats? GetStreamStats(string sessionId)
    {
        if (!_activeStreams.TryGetValue(sessionId, out var stream))
        {
            return null;
        }

        return new StreamSwitchStats
        {
            SessionId = sessionId,
            ChannelId = stream.ChannelId,
            CurrentProviderId = stream.CurrentProvider.Provider.Id,
            SwitchCount = stream.SwitchCount,
            LastSwitchTime = stream.LastSwitchTime,
            LastSwitchLatencyMs = stream.LastSwitchLatencyMs,
            CurrentCooldownMs = stream.CooldownStrategy.CurrentCooldownMs,
            CooldownSuccessRate = stream.CooldownStrategy.SuccessRate,
        };
    }

    /// <summary>
    /// Gets overall hot-swap statistics.
    /// </summary>
    /// <returns>Overall statistics.</returns>
    public HotSwapStats GetOverallStats()
    {
        return new HotSwapStats
        {
            ActiveStreams = _activeStreams.Count,
            TotalSwitches = TotalSwitches,
            SuccessfulSwitches = SuccessfulSwitches,
            FailedSwitches = FailedSwitches,
            AverageSwitchLatencyMs = AverageSwitchLatencyMs,
            PreconnectPoolSize = _preconnectPool.ActiveConnections,
            PreconnectHitRate = _preconnectPool.HitRatePercent,
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var kvp in _activeStreams)
        {
            kvp.Value.Dispose();
        }

        _activeStreams.Clear();
        _switchLock.Dispose();
    }

    private SwitchResult CompleteSuccessfulSwitch(
        ActiveStream stream,
        ProviderStreamInfo previousProvider,
        ProviderStreamInfo newProvider,
        long latencyMs,
        bool hadDiscontinuity
    )
    {
        stream.CurrentProvider = newProvider;
        stream.LastSwitchTime = DateTime.UtcNow;
        stream.SwitchCount++;
        stream.LastSwitchLatencyMs = latencyMs;
        stream.CooldownStrategy.RecordResult(success: true);

        Interlocked.Increment(ref _successfulSwitches);
        Interlocked.Add(ref _totalSwitchLatencyMs, latencyMs);

        _resilienceService.RecordSuccess(newProvider.Provider.Id);

        _logger.LogInformation(
            "Hot-swap successful for session {SessionId}: {FromProvider} -> {ToProvider} in {LatencyMs}ms (discontinuity: {HadDiscontinuity}, nextCooldown: {CooldownMs}ms)",
            stream.SessionId,
            previousProvider.Provider.Id,
            newProvider.Provider.Id,
            latencyMs,
            hadDiscontinuity,
            stream.CooldownStrategy.CurrentCooldownMs
        );

        return SwitchResult.Succeeded(previousProvider, newProvider, (int)latencyMs, hadDiscontinuity);
    }

    private SwitchResult CompleteFailedSwitch(
        ActiveStream stream,
        ProviderStreamInfo previousProvider,
        string errorMessage,
        Exception? exception = null
    )
    {
        stream.CooldownStrategy.RecordResult(success: false);
        Interlocked.Increment(ref _failedSwitches);

        _logger.LogWarning(
            exception,
            "Hot-swap failed for session {SessionId}: {Error} (nextCooldown: {CooldownMs}ms)",
            stream.SessionId,
            errorMessage,
            stream.CooldownStrategy.CurrentCooldownMs
        );

        return SwitchResult.Failed(previousProvider, errorMessage, exception);
    }
}

/// <summary>
/// Statistics for a single stream's switching behavior.
/// </summary>
public readonly struct StreamSwitchStats : IEquatable<StreamSwitchStats>
{
    /// <summary>Gets the session ID.</summary>
    public required string SessionId { get; init; }

    /// <summary>Gets the channel ID.</summary>
    public required int ChannelId { get; init; }

    /// <summary>Gets the current provider ID.</summary>
    public required string CurrentProviderId { get; init; }

    /// <summary>Gets the total number of switches.</summary>
    public required int SwitchCount { get; init; }

    /// <summary>Gets the last switch time.</summary>
    public required DateTime LastSwitchTime { get; init; }

    /// <summary>Gets the last switch latency in milliseconds.</summary>
    public required long LastSwitchLatencyMs { get; init; }

    /// <summary>Gets the current adaptive cooldown in milliseconds.</summary>
    public required int CurrentCooldownMs { get; init; }

    /// <summary>Gets the recent switch success rate (0.0 to 1.0).</summary>
    public required double CooldownSuccessRate { get; init; }

    /// <summary>Equality operator.</summary>
    public static bool operator ==(StreamSwitchStats left, StreamSwitchStats right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(StreamSwitchStats left, StreamSwitchStats right) => !left.Equals(right);

    /// <inheritdoc />
    public bool Equals(StreamSwitchStats other) => SessionId == other.SessionId && ChannelId == other.ChannelId;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is StreamSwitchStats other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(SessionId, ChannelId);
}

/// <summary>
/// Overall hot-swap statistics.
/// </summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
public readonly struct HotSwapStats : IEquatable<HotSwapStats>
{
    /// <summary>Gets the number of active streams.</summary>
    public required int ActiveStreams { get; init; }

    /// <summary>Gets the total number of switches.</summary>
    public required long TotalSwitches { get; init; }

    /// <summary>Gets the number of successful switches.</summary>
    public required long SuccessfulSwitches { get; init; }

    /// <summary>Gets the number of failed switches.</summary>
    public required long FailedSwitches { get; init; }

    /// <summary>Gets the average switch latency in milliseconds.</summary>
    public required double AverageSwitchLatencyMs { get; init; }

    /// <summary>Gets the preconnect pool size.</summary>
    public required int PreconnectPoolSize { get; init; }

    /// <summary>Gets the preconnect hit rate percentage.</summary>
    public required double PreconnectHitRate { get; init; }

    /// <summary>Equality operator.</summary>
    public static bool operator ==(HotSwapStats left, HotSwapStats right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(HotSwapStats left, HotSwapStats right) => !left.Equals(right);

    /// <inheritdoc />
    public bool Equals(HotSwapStats other) =>
        TotalSwitches == other.TotalSwitches && ActiveStreams == other.ActiveStreams;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is HotSwapStats other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(TotalSwitches, ActiveStreams);
}
