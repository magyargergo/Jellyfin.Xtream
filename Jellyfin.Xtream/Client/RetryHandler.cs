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
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Utility;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Client;

/// <summary>
/// HTTP message handler that implements retry logic with exponential backoff
/// for transient errors and Cloudflare-specific issues.
/// </summary>
public class RetryHandler : DelegatingHandler
{
    private const int MaxRetries = 3;
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RetryHandler"/> class.
    /// </summary>
    /// <param name="logger">Optional logger for retry diagnostics.</param>
    public RetryHandler(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        HttpResponseMessage? response = null;
        Exception? lastException = null;

        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            HttpRequestMessage? clonedRequest = null;
            var shouldDisposeClone = true;
            try
            {
                // Clone the request for retry attempts (request can only be sent once)
                // On first attempt, use original; on retries, create a clone
                var requestToSend =
                    attempt == 0 ? request : (clonedRequest = await CloneRequestAsync(request).ConfigureAwait(false));

                response = await base.SendAsync(requestToSend, cancellationToken).ConfigureAwait(false);

                if (!ShouldRetryResponse(response))
                {
                    // Success - don't dispose the clone yet as it may be referenced by response
                    shouldDisposeClone = false;
#pragma warning disable IDISP011 // Don't return disposed instance - response is not disposed here
                    return response;
#pragma warning restore IDISP011
                }

                // Log retry
                if (attempt < MaxRetries)
                {
                    var delay = GetRetryDelay(attempt, response);
                    _logger?.LogWarning(
                        "Request to {Uri} failed with status {StatusCode}. Attempt {Attempt}/{MaxRetries}. Retrying in {Delay}ms...",
                        request.RequestUri,
                        response.StatusCode,
                        attempt + 1,
                        MaxRetries,
                        delay.TotalMilliseconds
                    );

                    response.Dispose();
                    response = null;
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
                if (attempt < MaxRetries)
                {
                    var delay = GetRetryDelay(attempt, response: null);

                    // Log without stack trace for expected connection failures
                    if (IsExpectedConnectionFailure(ex))
                    {
                        _logger?.LogDebug(
                            "Request to {Uri} failed: {Message}. Attempt {Attempt}/{MaxRetries}. Retrying in {Delay}ms...",
                            request.RequestUri,
                            ex.Message,
                            attempt + 1,
                            MaxRetries,
                            delay.TotalMilliseconds
                        );
                    }
                    else
                    {
                        _logger?.LogWarning(
                            ex,
                            "Request to {Uri} failed with exception. Attempt {Attempt}/{MaxRetries}. Retrying in {Delay}ms...",
                            request.RequestUri,
                            attempt + 1,
                            MaxRetries,
                            delay.TotalMilliseconds
                        );
                    }

                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Timeout, not user cancellation
                lastException = ex;
                if (attempt < MaxRetries)
                {
                    var delay = GetRetryDelay(attempt, response: null);
                    _logger?.LogWarning(
                        "Request to {Uri} timed out. Attempt {Attempt}/{MaxRetries}. Retrying in {Delay}ms...",
                        request.RequestUri,
                        attempt + 1,
                        MaxRetries,
                        delay.TotalMilliseconds
                    );

                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                // Dispose the cloned request only if we're retrying (not on successful return)
                if (shouldDisposeClone)
                {
                    clonedRequest?.Dispose();
                }
            }
        }

        // All retries exhausted - return last response or throw
        return response ?? throw (lastException ?? new HttpRequestException("Request failed after all retries"));
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version };

        // Copy headers
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // Copy content if present
        if (request.Content != null)
        {
            var contentBytes = await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            clone.Content = new ByteArrayContent(contentBytes);

            foreach (var header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }

    private static bool ShouldRetryResponse(HttpResponseMessage response)
    {
        // IMPORTANT: Do NOT retry streaming connections (video/mp2t content type)
        // Retrying a live stream causes playback issues (jumping/repeating)
        var contentType = response.Content?.Headers?.ContentType?.MediaType;
        if (
            contentType != null
            && (
                contentType.Contains("video", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("octet-stream", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return false; // Never retry streaming responses
        }

        // Retry on Cloudflare-specific and transient errors (for API calls only)
        return response.StatusCode switch
        {
            (HttpStatusCode)522 => true, // Connection timed out (Cloudflare)
            (HttpStatusCode)524 => true, // A timeout occurred (Cloudflare)
            (HttpStatusCode)429 => true, // Too Many Requests (rate limit)
            HttpStatusCode.ServiceUnavailable => true, // 503
            HttpStatusCode.BadGateway => true, // 502
            HttpStatusCode.GatewayTimeout => true, // 504
            _ => false,
        };
    }

    private static bool IsExpectedConnectionFailure(HttpRequestException ex)
    {
        // Check for common expected failures that don't need stack traces
        var message = ex.Message.ToUpperInvariant();

        // Connection refused, host not found, network unreachable
        if (
            message.Contains("CONNECTION REFUSED", StringComparison.Ordinal)
            || message.Contains("NO SUCH HOST", StringComparison.Ordinal)
            || message.Contains("HOST NOT FOUND", StringComparison.Ordinal)
            || message.Contains("NAME OR SERVICE NOT KNOWN", StringComparison.Ordinal)
            || message.Contains("NETWORK IS UNREACHABLE", StringComparison.Ordinal)
            || message.Contains("NODENAME NOR SERVNAME", StringComparison.Ordinal)
            || message.Contains("ACTIVELY REFUSED", StringComparison.Ordinal)
        )
        {
            return true;
        }

        // Check HTTP status codes that are expected failures
        if (ex.StatusCode.HasValue)
        {
            var code = (int)ex.StatusCode.Value;
            // 404 Not Found, 401 Unauthorized, 403 Forbidden are expected for invalid providers
            return code is 404 or 401 or 403 or 406;
        }

        return false;
    }

    private static TimeSpan GetRetryDelay(int attempt, HttpResponseMessage? response)
    {
        // Check for Retry-After header (Cloudflare may send this)
        if (response?.Headers.RetryAfter?.Delta.HasValue == true)
        {
            var retryAfter = response.Headers.RetryAfter.Delta.Value;
            // Cap at 30 seconds
            return retryAfter > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : retryAfter;
        }

        // Exponential backoff with jitter: 2s, 4s, 8s, 16s...
        var exponentialDelay = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));

        // Add jitter (±25%) to avoid thundering herd
        var jitterFactor = 0.75 + (Random.Shared.NextDouble() * 0.5); // 0.75 to 1.25
        var delayWithJitter = TimeSpan.FromMilliseconds(exponentialDelay.TotalMilliseconds * jitterFactor);

        // Cap at 30 seconds max
        return delayWithJitter > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delayWithJitter;
    }
}
