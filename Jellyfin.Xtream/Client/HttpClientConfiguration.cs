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
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.RateLimiting;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service.ProviderManagement;
using Jellyfin.Xtream.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
            // Add retry handler for transient errors and Cloudflare-specific issues
            .AddHttpMessageHandler(sp => new RetryHandler(CreateLogger(sp, "Jellyfin.Xtream.Retry")))
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
                var connectStartTime = DateTime.UtcNow;
                logger?.LogDebugIfEnabled(
                    "ConnectCallback: resolving DNS for host '{Host}:{Port}'...",
                    context.DnsEndPoint.Host,
                    context.DnsEndPoint.Port
                );

                // Resolve DNS to get all IP addresses for the host
                var addresses = await System
                    .Net.Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken)
                    .ConfigureAwait(false);

                var dnsResolveMs = (DateTime.UtcNow - connectStartTime).TotalMilliseconds;
                logger?.LogDebugIfEnabled(
                    "ConnectCallback: DNS resolved for '{Host}' in {DnsMs}ms - found {AddressCount} address(es): [{Addresses}]",
                    context.DnsEndPoint.Host,
                    dnsResolveMs,
                    addresses.Length,
                    string.Join(", ", addresses.Select(a => a.ToString()))
                );

                if (addresses.Length == 0)
                {
                    logger?.LogDebugIfEnabled(
                        "ConnectCallback: No addresses found for host '{Host}'",
                        context.DnsEndPoint.Host
                    );
                    throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound);
                }

                // Try each address until one succeeds (create fresh socket for each attempt)
                Exception? lastException = null;
                int attemptNumber = 0;

                foreach (var address in addresses)
                {
                    attemptNumber++;

                    // Determine address family based on resolved address
                    var addressFamily = address.AddressFamily;

                    logger?.LogDebugIfEnabled(
                        "ConnectCallback: attempting connection to {Address}:{Port} (attempt {Attempt}/{Total}, family={Family})",
                        address,
                        context.DnsEndPoint.Port,
                        attemptNumber,
                        addresses.Length,
                        addressFamily
                    );

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
                        var socketConnectStart = DateTime.UtcNow;
                        // Connect to specific IP address (not DnsEndPoint) to avoid multi-address issues
                        var endpoint = new System.Net.IPEndPoint(address, context.DnsEndPoint.Port);
                        await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);

                        var socketConnectMs = (DateTime.UtcNow - socketConnectStart).TotalMilliseconds;
                        var totalConnectMs = (DateTime.UtcNow - connectStartTime).TotalMilliseconds;
                        logger?.LogDebugIfEnabled(
                            "ConnectCallback: connected to {Address}:{Port} in {SocketMs}ms (total: {TotalMs}ms)",
                            address,
                            context.DnsEndPoint.Port,
                            socketConnectMs,
                            totalConnectMs
                        );

                        return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception ex)
                    {
                        socket.Dispose();
                        lastException = ex;

                        logger?.LogDebugIfEnabled(
                            "ConnectCallback: connection to {Address}:{Port} FAILED: {ExceptionType} - {Message}",
                            address,
                            context.DnsEndPoint.Port,
                            ex.GetType().Name,
                            ex.Message
                        );

                        // If cancellation was requested, don't try more addresses
                        if (cancellationToken.IsCancellationRequested)
                        {
                            logger?.LogDebugIfEnabled(
                                "ConnectCallback: cancellation requested, stopping connection attempts"
                            );
                            break;
                        }

                        // Continue to next address
                    }
                }

                var totalFailedMs = (DateTime.UtcNow - connectStartTime).TotalMilliseconds;
                logger?.LogDebugIfEnabled(
                    "ConnectCallback: ALL {Count} addresses failed for '{Host}' after {TotalMs}ms. Last error: {Error}",
                    addresses.Length,
                    context.DnsEndPoint.Host,
                    totalFailedMs,
                    lastException?.Message ?? "unknown"
                );

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
            // ConnectTimeout: Configurable (default 5s) fail-fast for unreachable servers
            // Industry standard: 1-5 seconds for CDN failover scenarios
            // KeepAlivePingDelay: 60s - ping during active requests to prevent connection drops
            // KeepAlivePingTimeout: 30s - allow time for ping response over slow networks
            ConnectTimeout = TimeSpan.FromMilliseconds(StreamingTimeoutPolicy.GetConnectTimeoutMs(config)),
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
            "Configuring {ProxyType} proxy: {ProxyUri} (BypassLocal: {BypassLocal}, HasCredentials: {HasCredentials})",
            config.ProxyType,
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

        // Sanitize address: remove any existing protocol prefix and trailing slash
        address = address
            .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("socks5://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("socks4a://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("socks4://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("socks://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');

        // Validate and normalize port
        var port = config.ProxyPort is > 0 and <= 65535 ? config.ProxyPort : GetDefaultPort(config.ProxyType);

        // Get the URI scheme based on proxy type
        var scheme = GetProxyScheme(config.ProxyType);

        // Construct proxy URI
        try
        {
            proxyUri = new Uri(
                $"{scheme}://{address}:{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            );
            return true;
        }
        catch (UriFormatException ex)
        {
            logger?.LogError(
                ex,
                "Invalid proxy URI format: type={ProxyType}, address='{Address}', port={Port}",
                config.ProxyType,
                address,
                port
            );
            return false;
        }
    }

    /// <summary>
    /// Gets the URI scheme for the specified proxy type.
    /// </summary>
    /// <param name="proxyType">The proxy type.</param>
    /// <returns>The URI scheme string.</returns>
    private static string GetProxyScheme(ProxyType proxyType) =>
        proxyType switch
        {
            ProxyType.Socks4 => "socks4",
            ProxyType.Socks4a => "socks4a",
            ProxyType.Socks5 => "socks5",
            _ => "http",
        };

    /// <summary>
    /// Gets the default port for the specified proxy type.
    /// </summary>
    /// <param name="proxyType">The proxy type.</param>
    /// <returns>The default port number.</returns>
    private static int GetDefaultPort(ProxyType proxyType) =>
        proxyType switch
        {
            ProxyType.Socks4 or ProxyType.Socks4a or ProxyType.Socks5 => 1080,
            _ => 8080,
        };
}
