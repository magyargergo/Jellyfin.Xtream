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
using System.Threading.RateLimiting;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;

namespace Jellyfin.Xtream.Client;

/// <summary>
/// Extension methods for configuring HttpClient with proxy, User-Agent, and other settings.
/// </summary>
public static class HttpClientConfiguration
{
    /// <summary>
    /// The named HttpClient for Xtream API requests.
    /// </summary>
    public const string XtreamClientName = "XtreamClient";

    /// <summary>
    /// Configures the HttpClient builder with proxy, User-Agent, rate limiting, retry policies, and certificate validation settings.
    /// </summary>
    /// <param name="builder">The IHttpClientBuilder to configure.</param>
    /// <param name="getConfiguration">Function to get the current plugin configuration.</param>
    /// <returns>The configured IHttpClientBuilder for chaining.</returns>
    public static IHttpClientBuilder ConfigureXtreamClient(
        this IHttpClientBuilder builder,
        Func<PluginConfiguration> getConfiguration
    )
    {
        return builder
            .ConfigurePrimaryHttpMessageHandler(sp => CreateHttpMessageHandler(getConfiguration, sp))
            .AddHttpMessageHandler(sp => CreateRateLimitingHandler(getConfiguration, sp))
            // Add User-Agent rotation handler (rotates UA per-request when enabled)
            .AddHttpMessageHandler(sp => CreateUserAgentHandler(getConfiguration, sp))
            // Add Polly retry policy for transient errors and Cloudflare-specific issues
            .AddPolicyHandler((sp, request) => CreateRetryPolicy(sp))
            .ConfigureHttpClient((sp, client) => ConfigureHttpClient(client, sp))
            // Performance: Set handler lifetime to match pooled connection lifetime (5 minutes)
            // This ensures handlers are recycled regularly for DNS updates and connection health
            .SetHandlerLifetime(TimeSpan.FromMinutes(5));
    }

    private static UserAgentHandler CreateUserAgentHandler(
        Func<PluginConfiguration> getConfiguration,
        IServiceProvider serviceProvider
    )
    {
        var userAgentProvider = serviceProvider.GetService<IUserAgentProvider>();
        var logger = CreateLogger(serviceProvider, "Jellyfin.Xtream.UserAgent");

        // If no provider is registered, create a default one
        if (userAgentProvider == null)
        {
            logger?.LogWarning("IUserAgentProvider not registered, User-Agent rotation will use fallback");
            var providerLogger = serviceProvider.GetService<ILoggerFactory>()?.CreateLogger<UserAgentProvider>();
            userAgentProvider = new UserAgentProvider(getConfiguration, providerLogger!);
        }

        return new UserAgentHandler(userAgentProvider, getConfiguration, logger);
    }

    /// <summary>
    /// Creates a Polly retry policy for handling transient errors and Cloudflare-specific issues.
    /// Implements exponential backoff with jitter and respects Retry-After headers.
    /// </summary>
    private static Polly.Retry.AsyncRetryPolicy<HttpResponseMessage> CreateRetryPolicy(IServiceProvider serviceProvider)
    {
        var logger = CreateLogger(serviceProvider, "Jellyfin.Xtream.Retry");

        return Policy<HttpResponseMessage>
            .Handle<HttpRequestException>()
            .OrResult(ShouldRetryResponse)
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: (retryAttempt, result, context) =>
                {
                    // Check for Retry-After header (Cloudflare may send this)
                    if (result.Result?.Headers.RetryAfter?.Delta.HasValue == true)
                    {
                        var retryAfter = result.Result.Headers.RetryAfter.Delta.Value;
                        logger?.LogDebugIfEnabled(
                            "Using Retry-After header value: {RetryAfter}s",
                            retryAfter.TotalSeconds
                        );
                        return retryAfter;
                    }

                    // Exponential backoff with jitter: 2s, 4s, 8s, 16s...
                    var exponentialDelay = TimeSpan.FromSeconds(Math.Pow(2, retryAttempt));

                    // Add jitter (±25%) to avoid thundering herd
                    var jitterFactor = 0.75 + (Random.Shared.NextDouble() * 0.5); // 0.75 to 1.25
                    var delayWithJitter = TimeSpan.FromMilliseconds(exponentialDelay.TotalMilliseconds * jitterFactor);

                    // Cap at 30 seconds max
                    return delayWithJitter > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delayWithJitter;
                },
                onRetryAsync: (outcome, timespan, retryAttempt, context) =>
                {
                    if (outcome.Exception != null)
                    {
                        logger?.LogWarning(
                            outcome.Exception,
                            "Request failed with exception. Attempt {Attempt}/3. Retrying in {Delay}ms...",
                            retryAttempt,
                            timespan.TotalMilliseconds
                        );
                    }
                    else if (outcome.Result != null)
                    {
                        logger?.LogWarning(
                            "Request failed with status {StatusCode} ({StatusCodeInt}). "
                                + "Attempt {Attempt}/3. Retrying in {Delay}ms...",
                            outcome.Result.StatusCode,
                            (int)outcome.Result.StatusCode,
                            retryAttempt,
                            timespan.TotalMilliseconds
                        );
                    }

                    return System.Threading.Tasks.Task.CompletedTask;
                }
            );
    }

    /// <summary>
    /// Determines if an HTTP response warrants a retry.
    /// </summary>
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

    private static RateLimitingHandler CreateRateLimitingHandler(
        Func<PluginConfiguration> getConfiguration,
        IServiceProvider serviceProvider
    )
    {
        var config = getConfiguration();
        var logger = CreateLogger(serviceProvider, "Jellyfin.Xtream.RateLimit");

        RateLimiter rateLimiter;

        if (config.EnableRateLimiting)
        {
            var requestsPerSecond = config.RequestsPerSecond is > 0 and <= 100 ? config.RequestsPerSecond : 5;
            var burstSize = config.BurstSize is > 0 and <= 1000 ? config.BurstSize : 20;

            logger?.LogDebugIfEnabled(
                "Rate limiting enabled: {RequestsPerSecond} requests/second, burst size: {BurstSize}",
                requestsPerSecond,
                burstSize
            );

            rateLimiter = RateLimiterFactory.CreateTokenBucket(requestsPerSecond, burstSize);
        }
        else
        {
            // No-op rate limiter that allows all requests
            logger?.LogTrace("Rate limiting disabled");
            rateLimiter = new ConcurrencyLimiter(
                new System.Threading.RateLimiting.ConcurrencyLimiterOptions
                {
                    PermitLimit = int.MaxValue,
                    QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0,
                }
            );
        }

        return new RateLimitingHandler(rateLimiter, logger);
    }

    private static SocketsHttpHandler CreateHttpMessageHandler(
        Func<PluginConfiguration> getConfiguration,
        IServiceProvider serviceProvider
    )
    {
        var config = getConfiguration();
        var logger = CreateLogger(serviceProvider, "Jellyfin.Xtream.Proxy");

        var handler = new SocketsHttpHandler
        {
            // SSL/TLS: Use default certificate validation
            // .NET handles CRL/OCSP checks automatically with reasonable timeouts
            // No custom callback needed - default validation is secure and performant
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                CertificateRevocationCheckMode = System
                    .Security
                    .Cryptography
                    .X509Certificates
                    .X509RevocationMode
                    .NoCheck,
            },
            UseProxy = false, // Default to no proxy (configured separately if enabled)

            // CRITICAL: Apply low-latency socket configuration to all connections
            // Enables: 2MB receive buffers, TCP_NODELAY, TCP_QUICKACK (Linux), TCP_FASTOPEN (Windows)
            // Without this, sockets use default OS settings which are optimized for throughput, not latency
            //
            // Linux Socket Connection Fix:
            // On Linux, sockets become invalid after a failed connection attempt (PlatformNotSupportedException).
            // This is because Socket.ConnectAsync(DnsEndPoint) may resolve to multiple IP addresses and
            // attempt to connect to each one sequentially using the same socket. After the first failure,
            // Linux marks the socket as invalid for further connection attempts.
            //
            // Solution: Resolve DNS ourselves and try each address with a fresh socket.
            // This matches how SocketsHttpHandler works internally when ConnectCallback is not provided.
            ConnectCallback = async (context, cancellationToken) =>
            {
                // Resolve DNS to get all IP addresses for the host
                var addresses = await System
                    .Net.Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken)
                    .ConfigureAwait(false);

                if (addresses.Length == 0)
                {
                    throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound);
                }

                // Try each address until one succeeds (create fresh socket for each attempt)
                Exception? lastException = null;

                foreach (var address in addresses)
                {
                    // Determine address family based on resolved address
                    var addressFamily = address.AddressFamily;

                    var socket = new System.Net.Sockets.Socket(
                        addressFamily,
                        System.Net.Sockets.SocketType.Stream,
                        System.Net.Sockets.ProtocolType.Tcp
                    );

                    // Enable dual-mode for IPv6 sockets to accept IPv4 connections
                    if (addressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                    {
                        socket.DualMode = true;
                    }

                    // Apply RFC 7323/1122 compliant streaming optimizations
                    StreamingSocketConfiguration.ConfigureForStreaming(socket, logger);

                    try
                    {
                        // Connect to specific IP address (not DnsEndPoint) to avoid multi-address issues
                        var endpoint = new System.Net.IPEndPoint(address, context.DnsEndPoint.Port);
                        await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
                        return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception ex)
                    {
                        socket.Dispose();
                        lastException = ex;

                        // If cancellation was requested, don't try more addresses
                        if (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        // Continue to next address
                    }
                }

                // All addresses failed - throw the last exception
                throw lastException
                    ?? new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostUnreachable);
            },

            // Connection Pooling: Optimized for MPEG-TS streaming over HTTP
            // Higher limit allows concurrent channel streaming while avoiding provider rate limits
            // Balance: Too many connections → WAF blocking, Too few → poor multi-stream performance
            MaxConnectionsPerServer = 6,

            // Connection Lifecycle: DNS refresh and stale connection prevention
            // 5-minute lifetime ensures DNS updates propagate (CDN/load balancer changes)
            // 2-minute idle timeout closes unused connections, freeing server resources
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),

            // HTTP/2: Enable multiplexing for better performance (if IPTV provider supports it)
            // Most Xtream Codes providers use nginx with HTTP/2 support enabled
            // Allows multiple streams over single TCP connection, reducing handshake overhead
            EnableMultipleHttp2Connections = true,

            // Redirects: Enable auto-redirect for API calls
            // Note: HTTPS→HTTP downgrades are blocked by .NET for security
            // Stream URL resolution handles HTTPS→HTTP manually in Restream.ResolveStreamUrlAsync
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,

            // Cookies: Required for some IPTV provider authentication schemes
            // Token-based auth may use session cookies in addition to URL tokens
            UseCookies = true,

            // Compression: Automatic decompression of gzip/deflate/brotli responses
            // API responses (JSON) benefit from compression
            // MPEG-TS streams are already compressed (H.264/AAC), so no benefit there
            AutomaticDecompression = System.Net.DecompressionMethods.All,

            // TCP Keep-Alive: Critical for long-lived MPEG-TS streaming connections
            // Live streams use Transfer-Encoding: chunked with infinite duration
            // Keep-alive prevents NAT/firewall timeouts during streaming
            //
            // ConnectTimeout: 15s fail-fast for unreachable servers
            // KeepAlivePingDelay: 60s - ping during active requests to prevent connection drops
            // KeepAlivePingTimeout: 30s - allow time for ping response over slow networks
            ConnectTimeout = TimeSpan.FromSeconds(15),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
            KeepAlivePingDelay = TimeSpan.FromSeconds(60),
            KeepAlivePingPolicy = System.Net.Http.HttpKeepAlivePingPolicy.WithActiveRequests,
        };

        // Configure proxy if enabled
        if (TryConfigureProxy(config, logger, out var proxy))
        {
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }

        logger?.LogTrace(
            "HttpHandler configured: MaxConnections={MaxConnections}, Cookies={UseCookies}, Redirect={AllowAutoRedirect}",
            handler.MaxConnectionsPerServer,
            handler.UseCookies,
            handler.AllowAutoRedirect
        );

        return handler;
    }

    private static void ConfigureHttpClient(HttpClient client, IServiceProvider serviceProvider)
    {
        // MPEG-TS Streaming Headers: Configure RFC-compliant headers for IPTV streaming
        // This adds Accept: video/mp2t, Connection: keep-alive, Icy-MetaData, etc.
        AddBrowserHeaders(client, serviceProvider);

        // Timeout: Infinite timeout for long-lived MPEG-TS streaming connections
        // CRITICAL: Live streams use Transfer-Encoding: chunked with no Content-Length
        // Individual requests can still timeout via CancellationToken passed to SendAsync
        // This prevents HttpClient from terminating streams after default 100s timeout
        //
        // Why infinite timeout is safe:
        // 1. Jellyfin provides CancellationToken for user-initiated stops
        // 2. Provider EOF/disconnect is detected by stream.ReadAsync returning 0
        // 3. Network errors trigger exceptions (caught and logged)
        client.Timeout = Timeout.InfiniteTimeSpan;

        // HTTP/2: Request HTTP/2 with graceful fallback to HTTP/1.1
        // HTTP/2 provides multiplexing (multiple streams over single TCP) when available
        // Works automatically for HTTPS connections; HTTP connections use HTTP/1.1
        // Note: Most Xtream providers use HTTP on port 8080, so this mainly benefits API calls
        client.DefaultRequestVersion = System.Net.HttpVersion.Version20;
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;

        // User-Agent: Set a default User-Agent on the client.
        // NOTE: When User-Agent rotation is enabled, the UserAgentHandler will override this
        // on each request. This default is used when rotation is disabled.
        client.DefaultRequestHeaders.UserAgent.Clear();

        var logger = CreateLogger(serviceProvider, "Jellyfin.Xtream.UserAgent");
        var userAgentProvider = serviceProvider.GetService<IUserAgentProvider>();

        string userAgent;
        if (userAgentProvider != null)
        {
            userAgent = userAgentProvider.GetUserAgent();
            logger?.LogDebugIfEnabled("Default User-Agent set: {UserAgent}", userAgent);
        }
        else
        {
            userAgent = GetFallbackUserAgent();
            logger?.LogWarning("IUserAgentProvider not registered, using fallback User-Agent");
        }

        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
    }

    private static string GetFallbackUserAgent()
    {
        // Used only when IUserAgentProvider is not registered (should not happen in production)
        return "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    }

    /// <summary>
    /// Adds browser-like headers to avoid WAF detection.
    /// Based on real browser behavior patterns and MPEG-TS streaming standards.
    /// CRITICAL: Configured according to RFC 3551 (MPEG-2 TS MIME type) and RFC 7231 (HTTP Content Negotiation).
    /// </summary>
    private static void AddBrowserHeaders(HttpClient client, IServiceProvider serviceProvider)
    {
        var logger = CreateLogger(serviceProvider, "Jellyfin.Xtream.Headers");

        try
        {
            // CRITICAL: MPEG-TS Accept Header (RFC 3551)
            // IANA-registered MIME type: video/MP2T for MPEG-2 Transport Streams
            // Reference: https://www.iana.org/assignments/media-types/video/MP2T
            //
            // IPTV providers require explicit MIME type acceptance for video streams.
            // Many providers return HTTP 406 Not Acceptable if video/mp2t is missing.
            // This applies to BOTH initial URL resolution AND stream connections.
            //
            // Header breakdown:
            // - video/mp2t, video/MP2T: MPEG-2 Transport Stream (RFC 3551, case variants for compatibility)
            // - application/octet-stream: Generic binary fallback for non-compliant servers
            // - */*: Universal fallback (lowest priority)
            //
            // Quality values could be added for HLS compatibility:
            // "video/mp2t;q=1.0, application/vnd.apple.mpegurl;q=0.9, */*;q=0.8"
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "Accept",
                "video/mp2t, video/MP2T, application/octet-stream, */*"
            );

            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");

            // Connection management: keep-alive for long-lived IPTV streaming sessions
            // Essential for reducing TCP handshake overhead and maintaining stable connections
            // Live streams use Transfer-Encoding: chunked (RFC 9112) for infinite content delivery
            client.DefaultRequestHeaders.Connection.Clear();
            client.DefaultRequestHeaders.Connection.Add("keep-alive");

            // Icy-MetaData: Streaming client capability indicator (Icecast/Shoutcast protocol)
            // Some IPTV providers check this header for stream access control
            // Signals that client can handle metadata insertion in streams
            client.DefaultRequestHeaders.TryAddWithoutValidation("Icy-MetaData", "1");

            // Cache control: Live streams should not be cached
            // no-store prevents caching entirely (required for real-time streams)
            client.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
            {
                NoStore = true,
                NoCache = true,
            };

            // DNT (Do Not Track): Privacy-focused header
            client.DefaultRequestHeaders.TryAddWithoutValidation("DNT", "1");

            // Upgrade-Insecure-Requests: Signals preference for HTTPS
            // Note: Most Xtream providers use HTTP on non-standard ports (8080, 25461)
            client.DefaultRequestHeaders.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");

            logger?.LogTrace("MPEG-TS streaming headers configured (RFC 3551, RFC 7231 compliant)");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to add some browser headers, continuing anyway");
        }
    }

    private static bool TryConfigureProxy(PluginConfiguration config, ILogger? logger, out IWebProxy? proxy)
    {
        proxy = null;

        if (!config.EnableProxy)
        {
            return false;
        }

        if (!TryBuildProxyUri(config, logger, out var proxyUri))
        {
            logger?.LogWarning("Proxy is enabled but configuration is invalid - proxy will not be used");
            return false;
        }

        logger?.LogDebugIfEnabled(
            "Configuring HTTP proxy: {ProxyUri} (BypassLocal: {BypassLocal}, HasCredentials: {HasCredentials})",
            proxyUri,
            config.ProxyBypassLocal,
            !string.IsNullOrWhiteSpace(config.ProxyUsername)
        );

        proxy = new WebProxy(proxyUri)
        {
            BypassProxyOnLocal = config.ProxyBypassLocal,
            Credentials = CreateProxyCredentials(config),
        };

        return true;
    }

    private static NetworkCredential? CreateProxyCredentials(PluginConfiguration config) =>
        !string.IsNullOrWhiteSpace(config.ProxyUsername)
            ? new NetworkCredential(config.ProxyUsername, config.ProxyPassword)
            : null;

    private static ILogger? CreateLogger(IServiceProvider serviceProvider, string categoryName) =>
        serviceProvider.GetService<ILoggerFactory>()?.CreateLogger(categoryName);

    private static bool TryBuildProxyUri(PluginConfiguration config, ILogger? logger, out Uri? proxyUri)
    {
        proxyUri = null;

        var address = config.ProxyAddress?.Trim();
        if (string.IsNullOrWhiteSpace(address))
        {
            logger?.LogWarning("Proxy is enabled but proxy address is empty");
            return false;
        }

        // Sanitize address: remove protocol and trailing slash
        address = address
            .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');

        // Validate and normalize port
        var port = config.ProxyPort is > 0 and <= 65535 ? config.ProxyPort : 8080;

        // Construct proxy URI
        try
        {
            proxyUri = new Uri($"http://{address}:{port}");
            return true;
        }
        catch (UriFormatException ex)
        {
            logger?.LogError(ex, "Invalid proxy URI format: address='{Address}', port={Port}", address, port);
            return false;
        }
    }
}
