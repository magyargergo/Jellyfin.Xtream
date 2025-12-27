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
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Service.ProviderManagement;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.Switching;

/// <summary>
/// Configuration for the hot-swap service.
/// </summary>
public sealed class HotSwapConfiguration
{
    /// <summary>
    /// Gets the cooldown between hot-swap attempts in milliseconds.
    /// </summary>
    public int CooldownMs { get; init; } = 3000;

    /// <summary>
    /// Gets the maximum hot-swap attempts per stream session.
    /// </summary>
    public int MaxAttemptsPerSession { get; init; } = 10;

    /// <summary>
    /// Gets the timeout for a single hot-swap attempt in milliseconds.
    /// </summary>
    public int TimeoutMs { get; init; } = 2000;

    /// <summary>
    /// Gets a value indicating whether to use MPEG-TS byte alignment during switching.
    /// </summary>
    public bool UseByteAlignment { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether to wait for keyframes during aligned switching.
    /// </summary>
    public bool WaitForKeyframe { get; init; } = true;

    /// <summary>
    /// Default configuration.
    /// </summary>
    public static readonly HotSwapConfiguration Default = new();
}

/// <summary>
/// Implements hot-swap logic with optimized timing and thread safety.
/// Manages per-stream state including cooldown periods, attempt counts, and timeouts.
/// Uses IHotSwapStreamManager for MPEG-TS byte-aligned switching when available.
/// </summary>
public sealed class StreamHotSwapService : IStreamHotSwapService
{
    private readonly IProviderUrlResolver _urlResolver;
    private readonly IHotSwapStreamManager? _streamManager;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly ILogger<StreamHotSwapService> _logger;
    private readonly HotSwapConfiguration _config;

    // Per-stream state tracking using zero-allocation timing
    private readonly ConcurrentDictionary<string, StreamHotSwapState> _streamStates;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamHotSwapService"/> class.
    /// </summary>
    /// <param name="urlResolver">The provider URL resolver.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="config">Optional configuration.</param>
    public StreamHotSwapService(
        IProviderUrlResolver urlResolver,
        ILogger<StreamHotSwapService> logger,
        HotSwapConfiguration? config = null
    )
        : this(urlResolver, null, null, logger, config) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamHotSwapService"/> class with stream manager.
    /// </summary>
    /// <param name="urlResolver">The provider URL resolver.</param>
    /// <param name="streamManager">Optional stream manager for MPEG-TS byte alignment.</param>
    /// <param name="httpClientFactory">HTTP client factory for creating new stream connections.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="config">Optional configuration.</param>
    public StreamHotSwapService(
        IProviderUrlResolver urlResolver,
        IHotSwapStreamManager? streamManager,
        IHttpClientFactory? httpClientFactory,
        ILogger<StreamHotSwapService> logger,
        HotSwapConfiguration? config = null
    )
    {
        _urlResolver = urlResolver ?? throw new ArgumentNullException(nameof(urlResolver));
        _streamManager = streamManager;
        _httpClientFactory = httpClientFactory;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? HotSwapConfiguration.Default;
        _streamStates = new ConcurrentDictionary<string, StreamHotSwapState>(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool CanSwitch(HotSwapContext context)
    {
        var state = GetOrCreateState(context.StreamId);
        return !state.IsCooldownActive(_config.CooldownMs)
            && !state.IsMaxAttemptsReached(_config.MaxAttemptsPerSession);
    }

    /// <inheritdoc />
    public async Task<HotSwapResult> TrySwitchAsync(
        HotSwapContext context,
        SwitchReason reason,
        CancellationToken cancellationToken
    )
    {
        var state = GetOrCreateState(context.StreamId);

        // Fast-path checks (zero allocation)
        if (state.IsCooldownActive(_config.CooldownMs))
        {
            return HotSwapResult.CooldownActive;
        }

        if (state.IsMaxAttemptsReached(_config.MaxAttemptsPerSession))
        {
            return HotSwapResult.MaxAttemptsReached;
        }

        // Record attempt timing
        long startTicks = Environment.TickCount64;
        state.RecordAttempt(startTicks);

        _logger.LogInformation(
            "Hot-swap #{Attempt} for {StreamId} (reason: {Reason})",
            state.AttemptCount,
            context.StreamId,
            reason
        );

        try
        {
            // Apply timeout for fast-fail
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_config.TimeoutMs);

            var newUrl = await _urlResolver
                .GetAlternativeUrlAsync(context.StreamId, context.CurrentUrl, reason, timeoutCts.Token)
                .ConfigureAwait(false);

            if (string.IsNullOrEmpty(newUrl))
            {
                return HotSwapResult.Failed(HotSwapFailureReason.NoAlternativeProvider);
            }

            // Use aligned switching if stream manager is available and enabled
            if (_config.UseByteAlignment && _streamManager != null && _httpClientFactory != null)
            {
                var alignedResult = await TrySwitchWithAlignmentAsync(
                        context,
                        newUrl,
                        reason,
                        startTicks,
                        timeoutCts.Token
                    )
                    .ConfigureAwait(false);

                if (alignedResult.HasValue)
                {
                    return alignedResult.Value;
                }
            }

            long elapsedMs = Environment.TickCount64 - startTicks;

            _logger.LogInformation("Hot-swap successful for {StreamId} in {ElapsedMs}ms", context.StreamId, elapsedMs);
            return HotSwapResult.Succeeded(newUrl, elapsedMs);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Hot-swap timeout ({TimeoutMs}ms) for {StreamId}", _config.TimeoutMs, context.StreamId);
            return HotSwapResult.Failed(HotSwapFailureReason.Timeout);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hot-swap error for {StreamId}", context.StreamId);
            return HotSwapResult.Failed(HotSwapFailureReason.Error);
        }
    }

    /// <summary>
    /// Attempts aligned switching using IHotSwapStreamManager for MPEG-TS byte alignment.
    /// </summary>
    private async Task<HotSwapResult?> TrySwitchWithAlignmentAsync(
        HotSwapContext context,
        string newUrl,
        SwitchReason reason,
        long startTicks,
        CancellationToken cancellationToken
    )
    {
        try
        {
            // Create HTTP connection to new provider
            var httpClient = _httpClientFactory!.CreateClient("XtreamClient");
            using var request = new HttpRequestMessage(HttpMethod.Get, newUrl);
            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Aligned switch connection failed for {StreamId}: HTTP {StatusCode}",
                    context.StreamId,
                    (int)response.StatusCode
                );
                return null;
            }

            var newStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var _ = newStream.ConfigureAwait(false);

            // Use stream manager's aligned switching capability
            var alignedResult = await _streamManager!
                .SwitchWithAlignmentAsync(
                    context.StreamId,
                    CreateDummyProviderInfo(newUrl),
                    newStream,
                    reason,
                    _config.WaitForKeyframe,
                    cancellationToken
                )
                .ConfigureAwait(false);

            long elapsedMs = Environment.TickCount64 - startTicks;

            if (alignedResult.Success)
            {
                _logger.LogInformation(
                    "Aligned hot-swap successful for {StreamId} in {ElapsedMs}ms (keyframe: {Keyframe}, discarded: {Discarded} bytes)",
                    context.StreamId,
                    elapsedMs,
                    alignedResult.AlignedToKeyframe,
                    alignedResult.BytesDiscarded
                );
                return HotSwapResult.Succeeded(newUrl, elapsedMs);
            }

            _logger.LogWarning(
                "Aligned switch failed for {StreamId}: {Reason}",
                context.StreamId,
                alignedResult.FailureReason
            );
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Aligned switch failed for {StreamId}, falling back to URL-only switch",
                context.StreamId
            );
            return null;
        }
    }

    /// <summary>
    /// Creates a minimal ProviderStreamInfo for the aligned switch operation.
    /// </summary>
    private static ProviderStreamInfo CreateDummyProviderInfo(string url)
    {
        var uri = new Uri(url);
        var pathParts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var streamIdStr =
            pathParts.Length > 0 ? pathParts[^1].Replace(".ts", string.Empty, StringComparison.OrdinalIgnoreCase) : "0";
        _ = int.TryParse(streamIdStr, out var streamId);

        var provider = new Configuration.XtreamProvider
        {
            Id = uri.Host,
            Name = uri.Host,
            BaseUrl = $"{uri.Scheme}://{uri.Host}:{uri.Port}",
        };

        var streamInfo = new Client.Models.StreamInfo { StreamId = streamId };

        return new ProviderStreamInfo(provider, streamInfo);
    }

    /// <summary>
    /// Resets the state for a stream (call when stream ends).
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    public void ResetStream(string streamId)
    {
        _streamStates.TryRemove(streamId, out _);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private StreamHotSwapState GetOrCreateState(string streamId)
    {
        return _streamStates.GetOrAdd(streamId, static _ => new StreamHotSwapState());
    }

    /// <summary>
    /// Per-stream hot-swap state. Thread-safe with lock-free operations.
    /// </summary>
    private sealed class StreamHotSwapState
    {
        private long _lastAttemptTicks;
        private int _attemptCount;

        public int AttemptCount => Volatile.Read(ref _attemptCount);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsCooldownActive(int cooldownMs)
        {
            long lastTicks = Interlocked.Read(ref _lastAttemptTicks);
            if (lastTicks == 0)
            {
                return false;
            }

            return (Environment.TickCount64 - lastTicks) < cooldownMs;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsMaxAttemptsReached(int maxAttempts)
        {
            return Volatile.Read(ref _attemptCount) >= maxAttempts;
        }

        public void RecordAttempt(long ticks)
        {
            Interlocked.Exchange(ref _lastAttemptTicks, ticks);
            Interlocked.Increment(ref _attemptCount);
        }
    }
}
