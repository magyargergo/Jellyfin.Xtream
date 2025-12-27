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
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service.ProviderManagement;

namespace Jellyfin.Xtream.Service.Switching;

/// <summary>
/// Represents a pooled connection to a provider.
/// </summary>
public sealed class PooledConnection : IDisposable
{
    private int _disposed;

    /// <summary>
    /// Gets the provider ID.
    /// </summary>
    public required string ProviderId { get; init; }

    /// <summary>
    /// Gets the provider info.
    /// </summary>
    public required ProviderStreamInfo Provider { get; init; }

    /// <summary>
    /// Gets the stream ID this connection is for.
    /// </summary>
    public required int StreamId { get; init; }

    /// <summary>
    /// Gets the HTTP response stream.
    /// </summary>
    public required Stream ResponseStream { get; init; }

    /// <summary>
    /// Gets the time when this connection was created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Gets a value indicating whether this connection has been claimed.
    /// </summary>
    public bool IsClaimed => Volatile.Read(ref _disposed) == 1;

    /// <summary>
    /// Gets the age of this connection in milliseconds.
    /// </summary>
    public long AgeMs => (long)(DateTime.UtcNow - CreatedAt).TotalMilliseconds;

    /// <summary>
    /// Attempts to claim this connection for use.
    /// </summary>
    /// <returns>True if successfully claimed.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryClaim()
    {
        return Interlocked.CompareExchange(ref _disposed, 1, 0) == 0;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            ResponseStream.Dispose();
        }
    }
}

/// <summary>
/// Maintains warm connections to backup providers for ultra-fast switching.
/// Pre-connects to top N alternative providers based on health scores.
/// </summary>
public sealed class PreconnectPool : IPreconnectPool
{
    private const int DefaultPoolSize = 2;
    private const int MaxPoolSize = 5;
    private const int ConnectionMaxAgeMs = 30000;
    private const int RefreshThresholdMs = 20000;
    private const int RefreshIntervalMs = 5000;

    private readonly HttpClient _httpClient;
    private readonly IProviderAvailabilityService _resilienceService;
    private readonly ConcurrentDictionary<string, PooledConnection> _connections;
    private readonly SemaphoreSlim _refreshLock;
    private readonly CancellationTokenSource _disposeCts;

    private volatile bool _disposed;
    private volatile bool _refreshRunning;
    private int _poolSize;
    private long _totalConnections;
    private long _connectionHits;
    private long _connectionMisses;
    private long _proactiveRefreshes;
    private CancellationTokenSource? _refreshCts;
    private Task? _refreshTask;
    private readonly object _refreshLockObject = new();

    private Func<string, int, IEnumerable<ProviderStreamInfo>>? _providerResolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="PreconnectPool"/> class.
    /// </summary>
    /// <param name="httpClient">The HTTP client for making connections.</param>
    /// <param name="resilienceService">The resilience service for provider health.</param>
    /// <param name="poolSize">Maximum number of preconnected providers to maintain.</param>
    public PreconnectPool(
        HttpClient httpClient,
        IProviderAvailabilityService resilienceService,
        int poolSize = DefaultPoolSize
    )
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _resilienceService = resilienceService ?? throw new ArgumentNullException(nameof(resilienceService));
        _poolSize = Math.Clamp(poolSize, 1, MaxPoolSize);
        _connections = new ConcurrentDictionary<string, PooledConnection>(StringComparer.Ordinal);
        _refreshLock = new SemaphoreSlim(1, 1);
        _disposeCts = new CancellationTokenSource();
    }

    /// <summary>
    /// Gets the current pool size.
    /// </summary>
    public int PoolSize => _poolSize;

    /// <summary>
    /// Gets the number of active connections.
    /// </summary>
    public int ActiveConnections => _connections.Count;

    /// <summary>
    /// Gets the total number of connections made.
    /// </summary>
    public long TotalConnections => Interlocked.Read(ref _totalConnections);

    /// <summary>
    /// Gets the number of connection hits (found in pool).
    /// </summary>
    public long ConnectionHits => Interlocked.Read(ref _connectionHits);

    /// <summary>
    /// Gets the number of connection misses (not found in pool).
    /// </summary>
    public long ConnectionMisses => Interlocked.Read(ref _connectionMisses);

    /// <summary>
    /// Gets the hit rate percentage (0-100).
    /// </summary>
    public double HitRatePercent
    {
        get
        {
            var total = _connectionHits + _connectionMisses;
            return total > 0 ? (_connectionHits * 100.0) / total : 0;
        }
    }

    /// <summary>
    /// Gets the number of proactive connection refreshes performed.
    /// </summary>
    public long ProactiveRefreshes => Interlocked.Read(ref _proactiveRefreshes);

    /// <summary>
    /// Gets a value indicating whether background refresh is running.
    /// </summary>
    public bool IsRefreshRunning => _refreshRunning;

    /// <summary>
    /// Tries to get a preconnected stream for a provider.
    /// If found, the connection is claimed and removed from the pool.
    /// </summary>
    /// <param name="providerId">The provider ID to get connection for.</param>
    /// <param name="connection">The connection if found.</param>
    /// <returns>True if a valid connection was found.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetConnection(string providerId, out PooledConnection? connection)
    {
        if (_connections.TryRemove(providerId, out connection))
        {
            if (connection.TryClaim() && connection.AgeMs < ConnectionMaxAgeMs)
            {
                Interlocked.Increment(ref _connectionHits);
                return true;
            }

            // Connection is stale or already claimed
            connection.Dispose();
            connection = null;
        }

        Interlocked.Increment(ref _connectionMisses);
        connection = null;
        return false;
    }

    /// <summary>
    /// Tries to get a preconnected stream for a provider and stream.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="streamId">The stream ID.</param>
    /// <param name="connection">The connection if found.</param>
    /// <returns>True if a matching connection was found.</returns>
    public bool TryGetConnection(string providerId, int streamId, out PooledConnection? connection)
    {
        if (TryGetConnection(providerId, out connection))
        {
            if (connection != null && connection.StreamId == streamId)
            {
                return true;
            }

            // Wrong stream ID, dispose and return miss
            connection?.Dispose();
            Interlocked.Decrement(ref _connectionHits);
            Interlocked.Increment(ref _connectionMisses);
            connection = null;
        }

        return false;
    }

    /// <summary>
    /// Preconnects to backup providers for a specific stream.
    /// Uses health scores to select the best backup providers.
    /// </summary>
    /// <param name="currentProviderId">The current provider ID to exclude.</param>
    /// <param name="streamId">The stream ID to preconnect to.</param>
    /// <param name="alternativeProviders">Available alternative providers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of successful preconnections.</returns>
    public async Task<int> PreconnectToBackupsAsync(
        string currentProviderId,
        int streamId,
        IEnumerable<ProviderStreamInfo> alternativeProviders,
        CancellationToken cancellationToken
    )
    {
        if (_disposed)
        {
            return 0;
        }

        // Get sorted providers by health
        var sortedProviders = _resilienceService.GetSortedProviders(alternativeProviders);
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);

        int connected = 0;
        var tasks = new List<Task<bool>>();

        foreach (var provider in sortedProviders)
        {
            if (connected >= _poolSize)
            {
                break;
            }

            if (provider.Provider.Id == currentProviderId)
            {
                continue;
            }

            if (_connections.ContainsKey(provider.Provider.Id))
            {
                // Already have a connection to this provider
                connected++;
                continue;
            }

            tasks.Add(PreconnectSingleAsync(provider, streamId, linkedCts.Token));
        }

        if (tasks.Count > 0)
        {
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            foreach (var result in results)
            {
                if (result)
                {
                    connected++;
                }
            }
        }

        return connected;
    }

    /// <summary>
    /// Removes and disposes all stale connections.
    /// </summary>
    /// <returns>Number of connections removed.</returns>
    public int PruneStaleConnections()
    {
        int removed = 0;
        var keysToRemove = new List<string>();

        foreach (var kvp in _connections)
        {
            if (kvp.Value.AgeMs >= ConnectionMaxAgeMs || kvp.Value.IsClaimed)
            {
                keysToRemove.Add(kvp.Key);
            }
        }

        foreach (var key in keysToRemove)
        {
            if (_connections.TryRemove(key, out var conn))
            {
                conn.Dispose();
                removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// Clears all connections from the pool.
    /// </summary>
    public void Clear()
    {
        foreach (var kvp in _connections)
        {
            if (_connections.TryRemove(kvp.Key, out var conn))
            {
                conn.Dispose();
            }
        }
    }

    /// <summary>
    /// Sets the pool size. Takes effect on next preconnect cycle.
    /// </summary>
    /// <param name="size">The new pool size (1-5).</param>
    public void SetPoolSize(int size)
    {
        _poolSize = Math.Clamp(size, 1, MaxPoolSize);

        // Prune excess connections
        while (_connections.Count > _poolSize)
        {
            // Remove oldest connection
            string? oldestKey = null;
            long oldestAge = 0;

            foreach (var kvp in _connections)
            {
                if (kvp.Value.AgeMs > oldestAge)
                {
                    oldestAge = kvp.Value.AgeMs;
                    oldestKey = kvp.Key;
                }
            }

            if (oldestKey != null && _connections.TryRemove(oldestKey, out var conn))
            {
                conn.Dispose();
            }
        }
    }

    /// <summary>
    /// Sets the provider resolver for background refresh operations.
    /// </summary>
    /// <param name="resolver">Function that returns providers for a given current provider ID and stream ID.</param>
    public void SetProviderResolver(Func<string, int, IEnumerable<ProviderStreamInfo>> resolver)
    {
        _providerResolver = resolver;
    }

    /// <summary>
    /// Starts background refresh of connections before they expire.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to stop refreshing.</param>
    public void StartBackgroundRefresh(CancellationToken cancellationToken)
    {
        lock (_refreshLockObject)
        {
            if (_refreshRunning || _disposed)
            {
                return;
            }

            _refreshCts?.Dispose();
            _refreshCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
            _refreshRunning = true;
            _refreshTask = Task.Run(() => BackgroundRefreshLoopAsync(_refreshCts.Token), _refreshCts.Token);
        }
    }

    /// <summary>
    /// Stops background refresh of connections.
    /// </summary>
    public void StopBackgroundRefresh()
    {
        lock (_refreshLockObject)
        {
            if (!_refreshRunning)
            {
                return;
            }

            _refreshRunning = false;
            _refreshCts?.Cancel();
            _refreshCts?.Dispose();
            _refreshCts = null;
        }
    }

    private async Task BackgroundRefreshLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !_disposed)
        {
            try
            {
                await Task.Delay(RefreshIntervalMs, cancellationToken).ConfigureAwait(false);
                await RefreshAgingConnectionsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Continue on other errors
            }
        }

        _refreshRunning = false;
    }

    private async Task RefreshAgingConnectionsAsync(CancellationToken cancellationToken)
    {
        var connectionsToRefresh = new List<(string ProviderId, int StreamId, ProviderStreamInfo Provider)>();

        foreach (var kvp in _connections)
        {
            if (kvp.Value.AgeMs >= RefreshThresholdMs && !kvp.Value.IsClaimed)
            {
                connectionsToRefresh.Add((kvp.Key, kvp.Value.StreamId, kvp.Value.Provider));
            }
        }

        if (connectionsToRefresh.Count == 0)
        {
            return;
        }

        foreach (var (providerId, streamId, provider) in connectionsToRefresh)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (_connections.TryRemove(providerId, out var oldConnection))
            {
                oldConnection.Dispose();

                var success = await PreconnectSingleAsync(provider, streamId, cancellationToken).ConfigureAwait(false);

                if (success)
                {
                    Interlocked.Increment(ref _proactiveRefreshes);
                }
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopBackgroundRefresh();
        _disposeCts.Cancel();
        _disposeCts.Dispose();
        Clear();
        _refreshLock.Dispose();
    }

    private async Task<bool> PreconnectSingleAsync(
        ProviderStreamInfo provider,
        int streamId,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var url = BuildStreamUrl(provider.Provider, streamId);
            var request = new HttpRequestMessage(HttpMethod.Get, url);

            var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                return false;
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            var connection = new PooledConnection
            {
                ProviderId = provider.Provider.Id,
                Provider = provider,
                StreamId = streamId,
                ResponseStream = stream,
            };

            if (_connections.TryAdd(provider.Provider.Id, connection))
            {
                Interlocked.Increment(ref _totalConnections);
                return true;
            }
            else
            {
                // Another connection was added first
                connection.Dispose();
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string BuildStreamUrl(XtreamProvider provider, int streamId)
    {
        return $"{provider.BaseUrl}/live/{provider.Username}/{provider.Password}/{streamId}.ts";
    }
}
