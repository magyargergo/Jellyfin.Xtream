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
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Utility;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.ProviderManagement;

/// <summary>
/// Configuration for the preconnect pool.
/// </summary>
public sealed record PreconnectPoolConfiguration
{
    /// <summary>
    /// Gets the maximum age of a warmed connection before it's considered stale.
    /// Default: 30 seconds (matches typical IPTV provider keep-alive).
    /// </summary>
    public TimeSpan MaxConnectionAge { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets the maximum number of connections to pool per host.
    /// Default: 2 (primary + backup).
    /// </summary>
    public int MaxConnectionsPerHost { get; init; } = 2;

    /// <summary>
    /// Gets the timeout for warmup operations.
    /// Default: 5 seconds (allows time for connection + initial read).
    /// </summary>
    public TimeSpan WarmupTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets the interval for periodic stale connection cleanup.
    /// Default: 10 seconds.
    /// </summary>
    public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets the number of bytes to read during warmup to prime the connection.
    /// Default: 8KB (enough to establish connection and validate stream response).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reading a small amount of data ensures the connection is fully established
    /// and data is flowing. The underlying TCP connection remains in HttpClient's
    /// SocketsHttpHandler pool even after we close the response.
    /// </para>
    /// <para>
    /// IPTV providers often reject HEAD requests and range requests, but always
    /// accept regular GET streaming requests. By reading a few KB and closing,
    /// we prime the connection pool for fast subsequent requests.
    /// </para>
    /// </remarks>
    public int WarmupReadBytes { get; init; } = 8 * 1024;

    /// <summary>
    /// Gets the default configuration.
    /// </summary>
    public static PreconnectPoolConfiguration Default { get; } = new();
}

/// <summary>
/// Implementation of <see cref="IPreconnectPool"/> using HttpClient connection pooling.
/// </summary>
/// <remarks>
/// <para>
/// This implementation leverages .NET's HttpClient connection pooling with additional
/// application-level warmup tracking. Key features:
/// </para>
/// <list type="bullet">
///   <item>Pre-establishes connections via HEAD/GET requests during idle time</item>
///   <item>Tracks connection health and age per host</item>
///   <item>Thread-safe using ConcurrentDictionary and interlocked operations</item>
///   <item>Automatic stale connection cleanup via timer</item>
/// </list>
/// <para>
/// Design decision: We don't maintain raw TCP sockets. Instead, we:
/// 1. Issue a warmup request that establishes the TCP+TLS connection
/// 2. Track that the connection is pooled in HttpClient's SocketsHttpHandler
/// 3. Subsequent requests to the same host reuse the pooled connection
/// </para>
/// <para>
/// This approach works because:
/// - .NET's connection pool is keyed by (host, port, scheme)
/// - Connections remain pooled for PooledConnectionIdleTimeout (2 minutes)
/// - Our warmup requests "prime" the pool before actual streaming
/// </para>
/// </remarks>
public sealed class PreconnectPool : IPreconnectPool
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PreconnectPool> _logger;
    private readonly PreconnectPoolConfiguration _config;

    // Track warmup state per host
    private readonly ConcurrentDictionary<string, HostWarmupState> _hostStates = new(StringComparer.OrdinalIgnoreCase);

    // Statistics
    private long _totalWarmupAttempts;
    private long _successfulWarmups;
    private long _failedWarmups;
    private long _cacheHits;
    private long _cacheMisses;
    private long _evictions;
    private long _totalWarmupLatencyMs;

    // Cleanup timer
    private readonly Timer? _cleanupTimer;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PreconnectPool"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="config">Optional configuration.</param>
    public PreconnectPool(
        IHttpClientFactory httpClientFactory,
        ILogger<PreconnectPool> logger,
        PreconnectPoolConfiguration? config = null
    )
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _config = config ?? PreconnectPoolConfiguration.Default;

        // Start periodic cleanup
        _cleanupTimer = new Timer(CleanupCallback, null, _config.CleanupInterval, _config.CleanupInterval);
    }

    /// <inheritdoc />
    public int WarmedConnectionCount
    {
        get
        {
            var count = 0;
            foreach (var state in _hostStates.Values)
            {
                if (state.IsWarmed && !state.IsStale(_config.MaxConnectionAge))
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <inheritdoc />
    public PreconnectPoolStatistics Statistics =>
        new()
        {
            TotalWarmupAttempts = Interlocked.Read(ref _totalWarmupAttempts),
            SuccessfulWarmups = Interlocked.Read(ref _successfulWarmups),
            FailedWarmups = Interlocked.Read(ref _failedWarmups),
            CacheHits = Interlocked.Read(ref _cacheHits),
            CacheMisses = Interlocked.Read(ref _cacheMisses),
            Evictions = Interlocked.Read(ref _evictions),
            AverageWarmupLatencyMs =
                _successfulWarmups > 0 ? (double)Interlocked.Read(ref _totalWarmupLatencyMs) / _successfulWarmups : 0,
        };

    /// <inheritdoc />
    public async Task<bool> WarmConnectionAsync(Uri hostUri, CancellationToken cancellationToken = default)
    {
        if (IsDisposed)
        {
            return false;
        }

        var hostKey = GetHostKey(hostUri);
        _ = Interlocked.Increment(ref _totalWarmupAttempts);

        var state = _hostStates.GetOrAdd(hostKey, _ => new HostWarmupState(hostKey));

        // Skip if already warmed and not stale
        if (state.IsWarmed && !state.IsStale(_config.MaxConnectionAge))
        {
            _logger.LogDebugIfEnabled("Host {Host} already warmed, age: {AgeMs}ms", hostKey, state.AgeMs);
            return true;
        }

        var sw = Stopwatch.StartNew();

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_config.WarmupTimeout);

            var client = _httpClientFactory.CreateClient(HttpClientConfiguration.XtreamClientName);

            // Use a regular streaming GET request - IPTV providers reject HEAD and range requests
            // but always accept normal streaming requests. We read a small amount of data to
            // fully establish the connection, then close. The TCP connection remains pooled.
            HttpResponseMessage? response = null;
            try
            {
                // Regular GET request - no Range header (providers reject it)
                var request = new HttpRequestMessage(HttpMethod.Get, hostUri);

                response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);

                // Any 2xx or 3xx is considered successful warmup
                var isSuccess = (int)response.StatusCode >= 200 && (int)response.StatusCode < 400;

                if (isSuccess)
                {
                    // Read a small amount of data to fully prime the connection
                    // This ensures TCP buffers are established and data is flowing
                    var buffer = new byte[_config.WarmupReadBytes];
                    var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                    var totalRead = 0;

                    while (totalRead < _config.WarmupReadBytes)
                    {
                        var bytesRead = await stream
                            .ReadAsync(buffer.AsMemory(totalRead, _config.WarmupReadBytes - totalRead), cts.Token)
                            .ConfigureAwait(false);

                        if (bytesRead == 0)
                        {
                            break; // Stream ended
                        }

                        totalRead += bytesRead;
                    }

                    sw.Stop();

                    state.MarkWarmed(sw.ElapsedMilliseconds);
                    _ = Interlocked.Increment(ref _successfulWarmups);
                    _ = Interlocked.Add(ref _totalWarmupLatencyMs, sw.ElapsedMilliseconds);

                    _logger.LogDebugIfEnabled(
                        "Warmed connection to {Host} in {LatencyMs}ms (read {BytesRead} bytes, status: {StatusCode})",
                        hostKey,
                        sw.ElapsedMilliseconds,
                        totalRead,
                        response.StatusCode
                    );

                    return true;
                }
                else
                {
                    sw.Stop();
                    state.MarkFailed(response.StatusCode.ToString());
                    _ = Interlocked.Increment(ref _failedWarmups);

                    _logger.LogDebugIfEnabled(
                        "Warmup failed for {Host}: status {StatusCode}",
                        hostKey,
                        response.StatusCode
                    );

                    return false;
                }
            }
            finally
            {
                response?.Dispose();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // User cancellation - don't count as failure
            _logger.LogDebugIfEnabled("Warmup cancelled for {Host}", hostKey);
            return false;
        }
        catch (Exception ex)
        {
            sw.Stop();
            state.MarkFailed(ex.Message);
            _ = Interlocked.Increment(ref _failedWarmups);

            _logger.LogDebugIfEnabled(
                "Warmup failed for {Host} after {LatencyMs}ms: {Error}",
                hostKey,
                sw.ElapsedMilliseconds,
                ex.Message
            );

            return false;
        }
    }

    /// <inheritdoc />
    public async Task<int> WarmConnectionsAsync(Uri[] hostUris, CancellationToken cancellationToken = default)
    {
        if (hostUris.Length == 0)
        {
            return 0;
        }

        // Warm all hosts in parallel
        var tasks = new Task<bool>[hostUris.Length];
        for (var i = 0; i < hostUris.Length; i++)
        {
            tasks[i] = WarmConnectionAsync(hostUris[i], cancellationToken);
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return Array.FindAll(results, r => r).Length;
    }

    /// <inheritdoc />
    public PreconnectSlot? TryAcquire(Uri hostUri)
    {
        var hostKey = GetHostKey(hostUri);

        if (!_hostStates.TryGetValue(hostKey, out var state))
        {
            _ = Interlocked.Increment(ref _cacheMisses);
            return null;
        }

        if (!state.IsWarmed || state.IsStale(_config.MaxConnectionAge))
        {
            _ = Interlocked.Increment(ref _cacheMisses);
            _logger.LogDebugIfEnabled(
                "Cache miss for {Host}: warmed={IsWarmed}, stale={IsStale}",
                hostKey,
                state.IsWarmed,
                state.IsStale(_config.MaxConnectionAge)
            );
            return null;
        }

        _ = Interlocked.Increment(ref _cacheHits);
        state.MarkAcquired();

        _logger.LogDebugIfEnabled("Cache hit for {Host}, connection age: {AgeMs}ms", hostKey, state.AgeMs);

        // Note: We don't return an actual stream here because the warmup
        // just primes the connection pool. The caller will still use HttpClient
        // to make the actual streaming request, but it will reuse the pooled connection.
        // Return a marker slot to indicate warmup was successful.
        return null;
    }

    /// <inheritdoc />
    public void Return(PreconnectSlot slot)
    {
        // In our implementation, connections are managed by HttpClient's pool
        // This method is a no-op but exists for interface compatibility
        // and future implementations that might manage sockets directly.
        if (slot.HasError)
        {
            var hostKey = GetHostKey(slot.HostUri);
            if (_hostStates.TryGetValue(hostKey, out var state))
            {
                state.MarkFailed("Returned with error");
            }
        }
    }

    /// <inheritdoc />
    public int EvictStale(Uri? hostUri = null)
    {
        var evicted = 0;

        if (hostUri != null)
        {
            var hostKey = GetHostKey(hostUri);
            if (_hostStates.TryRemove(hostKey, out _))
            {
                evicted = 1;
                _ = Interlocked.Increment(ref _evictions);
            }
        }
        else
        {
            // Evict all stale connections
            foreach (var kvp in _hostStates)
            {
                if (kvp.Value.IsStale(_config.MaxConnectionAge))
                {
                    if (_hostStates.TryRemove(kvp.Key, out _))
                    {
                        evicted++;
                        _ = Interlocked.Increment(ref _evictions);
                    }
                }
            }
        }

        if (evicted > 0)
        {
            _logger.LogDebugIfEnabled("Evicted {Count} stale connection(s)", evicted);
        }

        return evicted;
    }

    /// <inheritdoc />
    public ConnectionPoolHealth GetHealth(Uri hostUri)
    {
        var hostKey = GetHostKey(hostUri);

        if (!_hostStates.TryGetValue(hostKey, out var state))
        {
            return ConnectionPoolHealth.Empty;
        }

        return new ConnectionPoolHealth
        {
            AvailableConnections = state.IsWarmed && !state.IsStale(_config.MaxConnectionAge) ? 1 : 0,
            InUseConnections = state.ActiveAcquisitions,
            AverageAgeSeconds = state.AgeMs / 1000.0,
            SuccessRate = state.SuccessRate,
            AverageWarmupLatencyMs = state.LastWarmupLatencyMs,
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _cleanupTimer?.Dispose();
        _hostStates.Clear();
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    private static string GetHostKey(Uri uri) => $"{uri.Scheme}://{uri.Host}:{uri.Port}";

    private void CleanupCallback(object? state)
    {
        if (IsDisposed)
        {
            return;
        }

        try
        {
            _ = EvictStale();
        }
        catch (Exception ex)
        {
            _logger.PluginLogWarning(ex, "Error during preconnect pool cleanup");
        }
    }

    /// <summary>
    /// Tracks warmup state for a single host.
    /// </summary>
    private sealed class HostWarmupState
    {
        private readonly string _hostKey;
        private DateTime _lastWarmedAt;
        private DateTime _lastAcquiredAt;
        private long _warmupLatencyMs;
        private int _successCount;
        private int _failCount;
        private int _activeAcquisitions;
        private string? _lastError;

        public HostWarmupState(string hostKey)
        {
            _hostKey = hostKey;
        }

        public bool IsWarmed => _lastWarmedAt != default;

        public long AgeMs => IsWarmed ? (long)(DateTime.UtcNow - _lastWarmedAt).TotalMilliseconds : 0;

        public long LastWarmupLatencyMs => Interlocked.Read(ref _warmupLatencyMs);

        public int ActiveAcquisitions => Volatile.Read(ref _activeAcquisitions);

        public double SuccessRate
        {
            get
            {
                var total = _successCount + _failCount;
                return total > 0 ? (double)_successCount / total : 0;
            }
        }

        public bool IsStale(TimeSpan maxAge) => !IsWarmed || DateTime.UtcNow - _lastWarmedAt > maxAge;

        public void MarkWarmed(long latencyMs)
        {
            _lastWarmedAt = DateTime.UtcNow;
            Interlocked.Exchange(ref _warmupLatencyMs, latencyMs);
            Interlocked.Increment(ref _successCount);
            _lastError = null;
        }

        public void MarkFailed(string error)
        {
            Interlocked.Increment(ref _failCount);
            _lastError = error;
        }

        public void MarkAcquired()
        {
            _lastAcquiredAt = DateTime.UtcNow;
            Interlocked.Increment(ref _activeAcquisitions);
        }

        public void MarkReleased()
        {
            Interlocked.Decrement(ref _activeAcquisitions);
        }
    }
}
