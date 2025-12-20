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
using System.Net.Http;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Jellyfin.Xtream.Utility;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Client;

/// <summary>
/// HTTP message handler that implements rate limiting for outgoing requests.
/// Retry logic is now handled by Polly policies in the HTTP client pipeline.
/// </summary>
public sealed class RateLimitingHandler : DelegatingHandler
{
    private readonly RateLimiter _rateLimiter;
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RateLimitingHandler"/> class.
    /// </summary>
    /// <param name="rateLimiter">The rate limiter to use.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public RateLimitingHandler(RateLimiter rateLimiter, ILogger? logger = null)
    {
        _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        // CRITICAL: Skip rate limiting for live stream requests
        // Live streams are long-lived connections that should not be rate-limited
        // Detect streaming by checking the URL path (contains stream ID format or /live/)
        var path = request.RequestUri?.PathAndQuery ?? string.Empty;
        bool isStreamingRequest =
            path.Contains("/live/", StringComparison.OrdinalIgnoreCase)
            || path.Contains(".ts", StringComparison.OrdinalIgnoreCase)
            || path.Contains(".m3u", StringComparison.OrdinalIgnoreCase)
            || path.Contains("stream", StringComparison.OrdinalIgnoreCase);

        if (isStreamingRequest)
        {
            _logger?.LogDebugIfEnabled(
                "Bypassing rate limiter for streaming request: {RequestUri}",
                request.RequestUri
            );
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        // Add small random jitter before API request to avoid burst patterns (5-50ms)
        await Task.Delay(Random.Shared.Next(5, 50), cancellationToken).ConfigureAwait(false);

        // Wait for rate limit permit with timeout (60 seconds max wait)
        // This is more lenient than throwing - settings page can take time to load
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));

        RateLimitLease? lease = null;
        int waitAttempts = 0;
        const int maxWaitAttempts = 30; // 30 attempts * 200ms = 6 seconds max wait before first warning

        while (lease == null || !lease.IsAcquired)
        {
            lease?.Dispose();
            lease = await _rateLimiter.AcquireAsync(permitCount: 1, timeoutCts.Token).ConfigureAwait(false);

            if (!lease.IsAcquired)
            {
                waitAttempts++;

                if (waitAttempts == 1)
                {
                    _logger?.LogDebugIfEnabled(
                        "Rate limit reached for {RequestUri}, waiting for permit...",
                        request.RequestUri
                    );
                }
                else if (waitAttempts % 10 == 0)
                {
                    _logger?.LogWarning(
                        "Still waiting for rate limit permit ({Attempts} attempts) for {RequestUri}",
                        waitAttempts,
                        request.RequestUri
                    );
                }

                if (waitAttempts >= maxWaitAttempts * 2)
                {
                    lease.Dispose();
                    throw new HttpRequestException(
                        $"Rate limit timeout. Too many requests queued for {request.RequestUri?.Host}"
                    );
                }

                // Wait 200ms before retry
                await Task.Delay(200, timeoutCts.Token).ConfigureAwait(false);
            }
        }

        _logger?.LogDebugIfEnabled("Rate limit permit acquired for request to {RequestUri}", request.RequestUri);

        try
        {
            // Delegate to the next handler in the pipeline (Polly will handle retries)
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lease.Dispose();
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _rateLimiter?.Dispose();
        }

        base.Dispose(disposing);
    }
}
