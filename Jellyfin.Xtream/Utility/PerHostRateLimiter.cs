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
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Utility;

/// <summary>
/// Adaptive per-host rate limiter with circuit breaker and jittered pacing.
/// Prevents WAF detection and IP blacklisting by enforcing conservative request patterns.
/// </summary>
/// <remarks>
/// This limiter enforces:
/// - Single concurrent request per host
/// - 8-20 second jittered delays between requests
/// - Exponential backoff on errors
/// - 2-hour quarantine on 401/403/429 status codes.
/// </remarks>
public sealed class PerHostRateLimiter(ILogger<PerHostRateLimiter> logger) : IDisposable
{
    private const int MinJitterMs = 8000;
    private const int MaxJitterMs = 20000;
    private readonly ConcurrentDictionary<string, HostState> _hostStates = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>
    /// Waits until the next request to the specified host is allowed.
    /// Enforces quarantine, concurrency limits, and jittered pacing.
    /// </summary>
    /// <param name="host">The target host (domain name).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous wait operation.</returns>
    public async Task WaitForSlotAsync(string host, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var state = GetOrCreateHostState(host);

        // Enforce quarantine if active
        if (state.QuarantineUntilUtc is { } quarantineEnd && quarantineEnd > DateTimeOffset.UtcNow)
        {
            var remainingQuarantine = quarantineEnd - DateTimeOffset.UtcNow;
            logger.PluginLogWarning(
                "Host {Host} is quarantined for {Seconds:N0} seconds due to previous failures",
                host,
                remainingQuarantine.TotalSeconds
            );

            await Task.Delay(remainingQuarantine, cancellationToken).ConfigureAwait(false);
            state.QuarantineUntilUtc = null;
        }

        // Enforce single concurrent request per host
        await state.Concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);

        // Enforce pacing delay
        var waitTime = state.NextAvailableUtc - DateTimeOffset.UtcNow;
        if (waitTime > TimeSpan.Zero)
        {
            logger.LogDebugIfEnabled("Pacing delay for {Host}: {Ms:N0}ms", host, waitTime.TotalMilliseconds);
            await Task.Delay(waitTime, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Records a successful request and schedules the next available window with jitter.
    /// </summary>
    /// <param name="host">The target host (domain name).</param>
    public void RecordSuccess(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        if (!_hostStates.TryGetValue(host, out var state))
        {
            return;
        }

        // Decay error score on success
        state.ConsecutiveErrors = Math.Max(0, state.ConsecutiveErrors - 1);
        state.QuarantineUntilUtc = null;

        // Apply full jitter for next window
        var jitterMs = Random.Shared.Next(MinJitterMs, MaxJitterMs);
        state.NextAvailableUtc = DateTimeOffset.UtcNow.AddMilliseconds(jitterMs);

        _ = state.Concurrency.Release();

        logger.LogDebugIfEnabled("Host {Host}: Success recorded. Next window in {Jitter:N0}ms", host, jitterMs);
    }

    /// <summary>
    /// Records an HTTP failure and applies adaptive backoff or quarantine based on status code.
    /// </summary>
    /// <param name="host">The target host (domain name).</param>
    /// <param name="statusCode">The HTTP status code received.</param>
    public void RecordHttpFailure(string host, HttpStatusCode statusCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        if (!_hostStates.TryGetValue(host, out var state))
        {
            return;
        }

        state.ConsecutiveErrors++;

        // Circuit breaker: Immediate quarantine for auth/rate-limit failures
        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            var quarantineDuration = TimeSpan.FromHours(2);
            state.QuarantineUntilUtc = DateTimeOffset.UtcNow.Add(quarantineDuration);
            state.NextAvailableUtc = state.QuarantineUntilUtc.Value;

            logger.PluginLogError(
                "⛔ Host {Host}: Status {Code} ({Name}) → QUARANTINE for {Hours:N1} hours to prevent blacklisting",
                host,
                (int)statusCode,
                statusCode,
                quarantineDuration.TotalHours
            );
        }
        // Exponential backoff for server errors
        else if ((int)statusCode >= 500)
        {
            var backoffMs = CalculateExponentialBackoff(state.ConsecutiveErrors);
            state.NextAvailableUtc = DateTimeOffset.UtcNow.AddMilliseconds(backoffMs);

            logger.PluginLogWarning(
                "Host {Host}: Status {Code} ({Name}) → Exponential backoff {Ms:N0}ms (errors: {Count})",
                host,
                (int)statusCode,
                statusCode,
                backoffMs,
                state.ConsecutiveErrors
            );
        }
        // Linear backoff for client errors
        else
        {
            const int backoffMs = 30000; // 30 seconds
            state.NextAvailableUtc = DateTimeOffset.UtcNow.AddMilliseconds(backoffMs);

            logger.PluginLogWarning(
                "Host {Host}: Status {Code} ({Name}) → Backoff {Ms:N0}ms",
                host,
                (int)statusCode,
                statusCode,
                backoffMs
            );
        }

        _ = state.Concurrency.Release();
    }

    /// <summary>
    /// Records a non-HTTP failure (timeout, network error, etc.) with modest backoff.
    /// </summary>
    /// <param name="host">The target host (domain name).</param>
    public void RecordFailure(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        if (!_hostStates.TryGetValue(host, out var state))
        {
            return;
        }

        state.ConsecutiveErrors++;

        // Random backoff between 20-45 seconds
        var backoffMs = Random.Shared.Next(20000, 45000);
        state.NextAvailableUtc = DateTimeOffset.UtcNow.AddMilliseconds(backoffMs);

        _ = state.Concurrency.Release();

        logger.PluginLogWarning(
            "Host {Host}: Non-HTTP failure → Backoff {Ms:N0}ms (errors: {Count})",
            host,
            backoffMs,
            state.ConsecutiveErrors
        );
    }

    /// <summary>
    /// Gets current quarantine state for a host (for diagnostics/testing).
    /// </summary>
    /// <param name="host">The target host (domain name).</param>
    /// <param name="remaining">The remaining quarantine duration, if quarantined.</param>
    /// <returns>True if the host is currently quarantined, false otherwise.</returns>
    public bool IsQuarantined(string host, out TimeSpan? remaining)
    {
        remaining = null;

        if (!_hostStates.TryGetValue(host, out var state))
        {
            return false;
        }

        if (state.QuarantineUntilUtc is { } end && end > DateTimeOffset.UtcNow)
        {
            remaining = end - DateTimeOffset.UtcNow;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Releases all resources used by the rate limiter.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var state in _hostStates.Values)
        {
            state.Dispose();
        }

        _hostStates.Clear();
        _disposed = true;
    }

    private HostState GetOrCreateHostState(string host) =>
        _hostStates.GetOrAdd(host, _ => new HostState { Concurrency = new SemaphoreSlim(1, 1) });

    private static int CalculateExponentialBackoff(int errorCount)
    {
        // 2^min(errorCount, 8) * 1000ms, capped at 5 minutes
        var backoffMs = (int)Math.Pow(2, Math.Min(errorCount, 8)) * 1000;
        return Math.Min(backoffMs, 300000);
    }

    /// <summary>
    /// Per-host state tracking for rate limiting and circuit breaking.
    /// </summary>
    private sealed record HostState : IDisposable
    {
        public required SemaphoreSlim Concurrency { get; init; }

        public DateTimeOffset NextAvailableUtc { get; set; } = DateTimeOffset.UtcNow;

        public int ConsecutiveErrors { get; set; }

        public DateTimeOffset? QuarantineUntilUtc { get; set; }

        public void Dispose() => Concurrency.Dispose();
    }
}
