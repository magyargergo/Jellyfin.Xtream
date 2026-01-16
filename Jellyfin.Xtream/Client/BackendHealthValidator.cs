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
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Service.ProviderManagement;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Client;

/// <summary>
/// Result of a backend health validation check.
/// </summary>
public readonly record struct BackendHealthResult
{
    /// <summary>
    /// Gets a value indicating whether the backend is healthy (responded to HTTP).
    /// </summary>
    public bool IsHealthy { get; init; }

    /// <summary>
    /// Gets the HTTP status code if a response was received, or null if timeout/error.
    /// </summary>
    public int? StatusCode { get; init; }

    /// <summary>
    /// Gets the response time in milliseconds, or null if no response.
    /// </summary>
    public double? ResponseTimeMs { get; init; }

    /// <summary>
    /// Gets the error message if the check failed.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Gets a value indicating whether the failure was due to timeout (zombie backend).
    /// </summary>
    public bool IsZombieBackend { get; init; }

    /// <summary>
    /// Gets a value indicating whether the failure was due to DNS resolution.
    /// </summary>
    public bool IsDnsFailure { get; init; }

    /// <summary>
    /// Gets a value indicating whether the failure was due to connection refused.
    /// </summary>
    public bool IsConnectionRefused { get; init; }

    /// <summary>
    /// Gets the number of retry attempts made before this result.
    /// </summary>
    public int RetryAttempts { get; init; }

    /// <summary>
    /// Gets the inner exception type name if available, for detailed diagnostics.
    /// </summary>
    public string? InnerExceptionType { get; init; }

    /// <summary>
    /// Creates a healthy result.
    /// </summary>
    public static BackendHealthResult Healthy(int statusCode, double responseTimeMs, int retryAttempts = 0) =>
        new()
        {
            IsHealthy = true,
            StatusCode = statusCode,
            ResponseTimeMs = responseTimeMs,
            IsZombieBackend = false,
            RetryAttempts = retryAttempts,
        };

    /// <summary>
    /// Creates a zombie backend result (timeout).
    /// </summary>
    public static BackendHealthResult Zombie(
        string error,
        double? partialResponseTimeMs = null,
        int retryAttempts = 0
    ) =>
        new()
        {
            IsHealthy = false,
            IsZombieBackend = true,
            Error = error,
            ResponseTimeMs = partialResponseTimeMs,
            RetryAttempts = retryAttempts,
        };

    /// <summary>
    /// Creates a failed result (non-timeout error) with detailed exception analysis.
    /// </summary>
    public static BackendHealthResult Failed(string error, Exception? exception = null, int retryAttempts = 0)
    {
        var (isDns, isConnectionRefused, innerType) = AnalyzeException(exception);

        // If no exception was provided, analyze the error message directly
        if (exception == null && !isDns && !isConnectionRefused)
        {
            (isDns, isConnectionRefused) = AnalyzeErrorMessage(error);
        }

        return new BackendHealthResult
        {
            IsHealthy = false,
            IsZombieBackend = false,
            Error = error,
            IsDnsFailure = isDns,
            IsConnectionRefused = isConnectionRefused,
            InnerExceptionType = innerType,
            RetryAttempts = retryAttempts,
        };
    }

    private static (bool IsDns, bool IsConnectionRefused) AnalyzeErrorMessage(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return (false, false);
        }

        var upperMessage = message.ToUpperInvariant();

        var isDns =
            upperMessage.Contains("HOST NOT FOUND", StringComparison.Ordinal)
            || upperMessage.Contains("NO SUCH HOST", StringComparison.Ordinal)
            || upperMessage.Contains("NAME OR SERVICE NOT KNOWN", StringComparison.Ordinal)
            || upperMessage.Contains("DNS", StringComparison.Ordinal);

        var isConnectionRefused =
            upperMessage.Contains("CONNECTION REFUSED", StringComparison.Ordinal)
            || upperMessage.Contains("ACTIVELY REFUSED", StringComparison.Ordinal);

        return (isDns, isConnectionRefused);
    }

    private static (bool IsDns, bool IsConnectionRefused, string? InnerType) AnalyzeException(Exception? ex)
    {
        if (ex == null)
        {
            return (false, false, null);
        }

        var innerType = ex.InnerException?.GetType().Name;

        // Check for SocketException with specific error codes
        if (ex.InnerException is SocketException socketEx)
        {
            return socketEx.SocketErrorCode switch
            {
                SocketError.HostNotFound => (true, false, innerType),
                SocketError.HostUnreachable => (true, false, innerType),
                SocketError.TryAgain => (true, false, innerType),
                SocketError.NoData => (true, false, innerType),
                SocketError.ConnectionRefused => (false, true, innerType),
                SocketError.ConnectionReset => (false, true, innerType),
                _ => (false, false, innerType),
            };
        }

        // Check error message patterns as fallback
        var (isDns, isConnectionRefused) = AnalyzeErrorMessage(ex.Message);

        return (isDns, isConnectionRefused, innerType);
    }

    /// <summary>
    /// Gets the appropriate <see cref="ProviderFailureReason"/> for this result.
    /// Uses exception analysis for accurate categorization.
    /// </summary>
    /// <returns>The failure reason, or null if the result is healthy.</returns>
    public ProviderFailureReason? ToFailureReason()
    {
        if (IsHealthy)
        {
            return null;
        }

        if (IsZombieBackend)
        {
            return ProviderFailureReason.ZombieBackend;
        }

        if (IsDnsFailure)
        {
            return ProviderFailureReason.NetworkError;
        }

        if (IsConnectionRefused)
        {
            return ProviderFailureReason.NetworkError;
        }

        // Check error message for additional patterns
        var error = Error?.ToUpperInvariant() ?? string.Empty;

        // HTTP status code based failures
        if (error.Contains("407", StringComparison.Ordinal))
        {
            return ProviderFailureReason.ProxyAuthenticationError;
        }

        if (error.Contains("429", StringComparison.Ordinal) || error.Contains("RATE LIMIT", StringComparison.Ordinal))
        {
            return ProviderFailureReason.RateLimited;
        }

        if (
            error.Contains("408", StringComparison.Ordinal)
            || error.Contains("504", StringComparison.Ordinal)
            || error.Contains("GATEWAY TIMEOUT", StringComparison.Ordinal)
        )
        {
            return ProviderFailureReason.Timeout;
        }

        if (
            error.Contains("503", StringComparison.Ordinal)
            || error.Contains("SERVICE UNAVAILABLE", StringComparison.Ordinal)
        )
        {
            return ProviderFailureReason.ServerError;
        }

        // Network-level patterns
        if (
            error.Contains("CONNECTION REFUSED", StringComparison.Ordinal)
            || error.Contains("HOST NOT FOUND", StringComparison.Ordinal)
            || error.Contains("NO SUCH HOST", StringComparison.Ordinal)
            || error.Contains("NETWORK UNREACHABLE", StringComparison.Ordinal)
        )
        {
            return ProviderFailureReason.NetworkError;
        }

        return ProviderFailureReason.Unknown;
    }
}

/// <summary>
/// Cached health check result with timestamp for TTL enforcement.
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct CachedHealthResult
{
    public BackendHealthResult Result { get; init; }
    public DateTime Timestamp { get; init; }

    public bool IsValid(TimeSpan ttl) => DateTime.UtcNow - Timestamp < ttl;
}

/// <summary>
/// Utility class for validating IPTV backend server health.
/// Detects "zombie backend" scenarios where TCP connects but HTTP never responds.
/// </summary>
/// <remarks>
/// <para>
/// <b>Thread Safety:</b> This class is thread-safe when the provided HttpClient is thread-safe.
/// The underlying HttpClient should be shared across requests for connection pooling efficiency.
/// </para>
/// <para>
/// IPTV providers often use load balancers that route to backend servers.
/// When a backend is dead but TCP still accepts connections (common with nginx/haproxy),
/// the HTTP request is sent but no response headers are ever received.
/// </para>
/// <para>
/// Network analysis of provider 161.123.116.21 showed this pattern:
/// - Load balancer at :80 responds with 302 redirect ✓
/// - Backend at redirect target accepts TCP connection ✓
/// - Backend NEVER sends HTTP response headers ✗ (hangs indefinitely)
/// </para>
/// <para>
/// This class provides methods to detect and handle this failure mode with:
/// - Configurable timeouts
/// - Automatic retry with exponential backoff
/// - Health check result caching
/// - Comprehensive exception handling
/// </para>
/// </remarks>
public class BackendHealthValidator
{
    /// <summary>
    /// Default timeout for health check requests (5 seconds).
    /// Aggressive timeout since we just want to know if the backend responds at all.
    /// </summary>
    public const int DefaultHealthCheckTimeoutMs = 5000;

    /// <summary>
    /// Default number of retry attempts for transient failures.
    /// </summary>
    public const int DefaultMaxRetries = 2;

    /// <summary>
    /// Default base delay for exponential backoff (100ms).
    /// </summary>
    public const int DefaultRetryBaseDelayMs = 100;

    /// <summary>
    /// Default TTL for cached health check results (30 seconds).
    /// </summary>
    public static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, CachedHealthResult> _healthCache = new(StringComparer.Ordinal);
    private readonly Random _jitterRandom = new();

    /// <summary>
    /// Gets or sets the maximum number of retry attempts for transient failures.
    /// </summary>
    public int MaxRetries { get; set; } = DefaultMaxRetries;

    /// <summary>
    /// Gets or sets the base delay in milliseconds for exponential backoff.
    /// </summary>
    public int RetryBaseDelayMs { get; set; } = DefaultRetryBaseDelayMs;

    /// <summary>
    /// Gets or sets the TTL for cached health check results.
    /// Set to TimeSpan.Zero to disable caching.
    /// </summary>
    public TimeSpan CacheTtl { get; set; } = DefaultCacheTtl;

    /// <summary>
    /// Initializes a new instance of the <see cref="BackendHealthValidator"/> class.
    /// </summary>
    /// <param name="httpClient">The HTTP client to use for requests. Should be thread-safe.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public BackendHealthValidator(HttpClient httpClient, ILogger? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger;
    }

    /// <summary>
    /// Validates that a backend server can respond to HTTP requests.
    /// Uses HTTP HEAD request with aggressive timeout to detect zombie backends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The goal is NOT to validate the stream content, but to detect completely dead backends
    /// that accept TCP connections but never send HTTP responses.
    /// </para>
    /// <para>
    /// ANY HTTP response (even 404, 403, 500) means the backend is alive.
    /// Only timeouts indicate a zombie backend.
    /// </para>
    /// <para>
    /// Results are cached per host for <see cref="CacheTtl"/> to avoid redundant checks.
    /// </para>
    /// </remarks>
    /// <param name="url">The URL to validate.</param>
    /// <param name="timeoutMs">Timeout in milliseconds (default: 5000).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The health check result.</returns>
    public async Task<BackendHealthResult> ValidateAsync(
        Uri url,
        int timeoutMs = DefaultHealthCheckTimeoutMs,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(url);

        var cacheKey = $"{url.Host}:{url.Port}";

        // Check cache first
        if (CacheTtl > TimeSpan.Zero && _healthCache.TryGetValue(cacheKey, out var cached) && cached.IsValid(CacheTtl))
        {
            _logger?.LogDebugIfEnabled(
                "Health check cache hit for {Host}: IsHealthy={IsHealthy}, Age={AgeMs}ms",
                url.Host,
                cached.Result.IsHealthy,
                (DateTime.UtcNow - cached.Timestamp).TotalMilliseconds
            );

            return cached.Result;
        }

        var result = await ValidateWithRetryAsync(url, timeoutMs, cancellationToken).ConfigureAwait(false);

        // Cache the result
        if (CacheTtl > TimeSpan.Zero)
        {
            _healthCache[cacheKey] = new CachedHealthResult { Result = result, Timestamp = DateTime.UtcNow };
        }

        return result;
    }

    private async Task<BackendHealthResult> ValidateWithRetryAsync(
        Uri url,
        int timeoutMs,
        CancellationToken cancellationToken
    )
    {
        var startTime = DateTime.UtcNow;
        Exception? lastException = null;

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            if (attempt > 0)
            {
                // Exponential backoff with jitter (0.8x to 1.2x)
                var baseDelay = RetryBaseDelayMs * (1 << (attempt - 1)); // 100, 200, 400...
                var jitter = 0.8 + (_jitterRandom.NextDouble() * 0.4);
                var delayMs = (int)(baseDelay * jitter);

                _logger?.LogDebugIfEnabled(
                    "Health check retry {Attempt}/{MaxRetries} for {Host} after {DelayMs}ms",
                    attempt,
                    MaxRetries,
                    url.Host,
                    delayMs
                );

                try
                {
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Cancellation during delay - exit retry loop
                    break;
                }
            }

            var result = await ValidateSingleAttemptAsync(url, timeoutMs, attempt, cancellationToken)
                .ConfigureAwait(false);

            // Return immediately on success or non-retryable failure
            if (result.IsHealthy || !ShouldRetry(result, lastException))
            {
                return result;
            }

            // Track for retry decision
            lastException = new InvalidOperationException(result.Error);
        }

        // All retries exhausted
        var totalElapsedMs = (DateTime.UtcNow - startTime).TotalMilliseconds;

        _logger?.PluginLogWarning(
            "Health check failed for {Host} after {Retries} retries, total time {ElapsedMs}ms: {Error}",
            url.Host,
            MaxRetries,
            totalElapsedMs,
            lastException?.Message
        );

        return BackendHealthResult.Failed(
            lastException?.Message ?? "Health check failed after retries",
            lastException,
            MaxRetries
        );
    }

    private static bool ShouldRetry(BackendHealthResult result, Exception? lastException)
    {
        // Don't retry zombie backends - they're definitively dead
        if (result.IsZombieBackend)
        {
            return false;
        }

        // Retry DNS failures (could be transient)
        if (result.IsDnsFailure)
        {
            return true;
        }

        // Retry connection refused (server may be restarting)
        if (result.IsConnectionRefused)
        {
            return true;
        }

        // Default: retry unknown errors
        return result.ToFailureReason() == ProviderFailureReason.Unknown;
    }

    private async Task<BackendHealthResult> ValidateSingleAttemptAsync(
        Uri url,
        int timeoutMs,
        int attemptNumber,
        CancellationToken cancellationToken
    )
    {
        var startTime = DateTime.UtcNow;
        CancellationTokenSource? timeoutCts = null;

        try
        {
            timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMs);

            // Use HEAD request for minimal overhead - we only care about response headers
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);

            var responseTimeMs = (DateTime.UtcNow - startTime).TotalMilliseconds;

            _logger?.LogDebugIfEnabled(
                "Backend health check for {Host}:{Port} [attempt {Attempt}]: status={StatusCode}, responseTime={ResponseTimeMs:F0}ms",
                url.Host,
                url.Port,
                attemptNumber + 1,
                (int)response.StatusCode,
                responseTimeMs
            );

            // ANY response means the backend is alive
            return BackendHealthResult.Healthy((int)response.StatusCode, responseTimeMs, attemptNumber);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Health check timeout - zombie backend detected
            var elapsedMs = (DateTime.UtcNow - startTime).TotalMilliseconds;

            _logger?.PluginLogWarning(
                "Backend health check TIMEOUT for {Host}:{Port} [attempt {Attempt}]: no response within {TimeoutMs}ms (elapsed: {ElapsedMs:F0}ms)",
                url.Host,
                url.Port,
                attemptNumber + 1,
                timeoutMs,
                elapsedMs
            );

            return BackendHealthResult.Zombie(
                $"No response within {timeoutMs}ms (zombie backend)",
                elapsedMs,
                attemptNumber
            );
        }
        catch (OperationCanceledException)
        {
            // User cancellation - propagate
            throw;
        }
        catch (HttpRequestException ex)
        {
            // Connection refused, DNS failure, etc.
            var elapsedMs = (DateTime.UtcNow - startTime).TotalMilliseconds;

            _logger?.PluginLogWarning(
                ex,
                "Backend health check FAILED for {Host}:{Port} [attempt {Attempt}]: {Error}, InnerException: {InnerType}",
                url.Host,
                url.Port,
                attemptNumber + 1,
                ex.Message,
                ex.InnerException?.GetType().Name ?? "none"
            );

            return BackendHealthResult.Failed(ex.Message, ex, attemptNumber);
        }
        catch (IOException ex)
        {
            // Network stream errors
            _logger?.PluginLogWarning(
                ex,
                "Backend health check IO error for {Host}:{Port}: {Error}",
                url.Host,
                url.Port,
                ex.Message
            );

            return BackendHealthResult.Failed($"IO error: {ex.Message}", ex, attemptNumber);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Catch-all for unexpected exceptions
            _logger?.PluginLogError(
                ex,
                "Backend health check unexpected error for {Host}:{Port}: {ExceptionType}: {Error}",
                url.Host,
                url.Port,
                ex.GetType().Name,
                ex.Message
            );

            return BackendHealthResult.Failed($"Unexpected error: {ex.Message}", ex, attemptNumber);
        }
        finally
        {
            timeoutCts?.Dispose();
        }
    }

    /// <summary>
    /// Validates that a backend server can respond to HTTP requests using string URL.
    /// </summary>
    /// <param name="url">The URL to validate.</param>
    /// <param name="timeoutMs">Timeout in milliseconds (default: 5000).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The health check result.</returns>
    public Task<BackendHealthResult> ValidateAsync(
        string url,
        int timeoutMs = DefaultHealthCheckTimeoutMs,
        CancellationToken cancellationToken = default
    ) => ValidateAsync(new Uri(url), timeoutMs, cancellationToken);

    /// <summary>
    /// Sends an HTTP GET request with zombie backend detection.
    /// Wraps the request in a timeout that detects backends that accept TCP but never send HTTP headers.
    /// </summary>
    /// <param name="url">The URL to request.</param>
    /// <param name="responseHeadersTimeoutMs">Timeout for receiving HTTP response headers (default: from config).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The HTTP response message.</returns>
    /// <exception cref="HttpRequestException">Thrown when the backend times out (zombie backend detected).</exception>
    public async Task<HttpResponseMessage> SendWithZombieDetectionAsync(
        Uri url,
        int? responseHeadersTimeoutMs = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(url);

        var timeoutMs = responseHeadersTimeoutMs ?? StreamingTimeoutPolicy.GetResponseHeadersTimeoutMs();
        var startTime = DateTime.UtcNow;
        CancellationTokenSource? timeoutCts = null;

        try
        {
            timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMs);

            var response = await _httpClient
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);

            var responseTimeMs = (DateTime.UtcNow - startTime).TotalMilliseconds;

            _logger?.LogDebugIfEnabled(
                "Request to {Host}:{Port} succeeded: status={StatusCode}, responseTime={ResponseTimeMs:F0}ms",
                url.Host,
                url.Port,
                (int)response.StatusCode,
                responseTimeMs
            );

            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Response headers timeout - zombie backend detected
            var elapsedMs = (DateTime.UtcNow - startTime).TotalMilliseconds;

            _logger?.PluginLogWarning(
                "Zombie backend detected: {Host}:{Port} did not respond within {TimeoutMs}ms (elapsed: {ElapsedMs:F0}ms). "
                    + "TCP connected but no HTTP response headers received.",
                url.Host,
                url.Port,
                timeoutMs,
                elapsedMs
            );

            throw new HttpRequestException(
                $"Backend {url.Host}:{url.Port} did not respond within {timeoutMs}ms (zombie backend detected)"
            );
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // TaskCanceledException from timeout
            var elapsedMs = (DateTime.UtcNow - startTime).TotalMilliseconds;

            _logger?.PluginLogWarning(
                "Zombie backend detected (TaskCanceled): {Host}:{Port} timed out after {ElapsedMs:F0}ms",
                url.Host,
                url.Port,
                elapsedMs
            );

            throw new HttpRequestException(
                $"Backend {url.Host}:{url.Port} timed out (zombie backend detected): {ex.Message}",
                ex
            );
        }
        catch (HttpRequestException ex)
        {
            // Log with full exception details for troubleshooting
            _logger?.PluginLogWarning(
                ex,
                "Request to {Host}:{Port} failed: {Error}, InnerException: {InnerType}",
                url.Host,
                url.Port,
                ex.Message,
                ex.InnerException?.GetType().Name ?? "none"
            );

            throw;
        }
        catch (IOException ex)
        {
            _logger?.PluginLogWarning(ex, "Request to {Host}:{Port} IO error: {Error}", url.Host, url.Port, ex.Message);

            throw new HttpRequestException($"IO error connecting to {url.Host}:{url.Port}: {ex.Message}", ex);
        }
        finally
        {
            timeoutCts?.Dispose();
        }
    }

    /// <summary>
    /// Sends an HTTP GET request with zombie backend detection using string URL.
    /// </summary>
    /// <param name="url">The URL to request.</param>
    /// <param name="responseHeadersTimeoutMs">Timeout for receiving HTTP response headers (default: from config).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The HTTP response message.</returns>
    public Task<HttpResponseMessage> SendWithZombieDetectionAsync(
        string url,
        int? responseHeadersTimeoutMs = null,
        CancellationToken cancellationToken = default
    ) => SendWithZombieDetectionAsync(new Uri(url), responseHeadersTimeoutMs, cancellationToken);

    /// <summary>
    /// Checks if a redirect target is healthy before following the redirect.
    /// This is a pre-flight check to catch zombie backends BEFORE committing to the redirect.
    /// </summary>
    /// <remarks>
    /// Results are cached to avoid redundant checks when following redirect chains.
    /// </remarks>
    /// <param name="currentUrl">The current URL (before redirect).</param>
    /// <param name="redirectUrl">The redirect target URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the redirect target is healthy or same host; false if zombie backend detected.</returns>
    public async Task<bool> ValidateRedirectTargetAsync(
        Uri currentUrl,
        Uri redirectUrl,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(currentUrl);
        ArgumentNullException.ThrowIfNull(redirectUrl);

        // Only validate if redirecting to a different host
        if (
            string.Equals(currentUrl.Host, redirectUrl.Host, StringComparison.OrdinalIgnoreCase)
            && currentUrl.Port == redirectUrl.Port
        )
        {
            return true; // Same host:port, no need to validate
        }

        _logger?.LogDebugIfEnabled(
            "Validating redirect target: {CurrentHost}:{CurrentPort} -> {RedirectHost}:{RedirectPort}",
            currentUrl.Host,
            currentUrl.Port,
            redirectUrl.Host,
            redirectUrl.Port
        );

        var result = await ValidateAsync(redirectUrl, DefaultHealthCheckTimeoutMs, cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsHealthy)
        {
            _logger?.PluginLogWarning(
                "Redirect target {Host}:{Port} failed health check: {Error} (IsZombie: {IsZombie}, Retries: {Retries})",
                redirectUrl.Host,
                redirectUrl.Port,
                result.Error,
                result.IsZombieBackend,
                result.RetryAttempts
            );
        }

        return result.IsHealthy;
    }

    /// <summary>
    /// Clears the health check result cache.
    /// </summary>
    public void ClearCache()
    {
        _healthCache.Clear();
        _logger?.LogDebugIfEnabled("Health check cache cleared");
    }

    /// <summary>
    /// Invalidates a specific host from the cache.
    /// </summary>
    /// <param name="host">The host to invalidate.</param>
    /// <param name="port">The port (default: 80).</param>
    public void InvalidateCache(string host, int port = 80)
    {
        var cacheKey = $"{host}:{port}";
        if (_healthCache.TryRemove(cacheKey, out _))
        {
            _logger?.LogDebugIfEnabled("Invalidated cache for {Host}:{Port}", host, port);
        }
    }

    /// <summary>
    /// Gets the current cache size (number of cached hosts).
    /// </summary>
    public int CacheSize => _healthCache.Count;
}
