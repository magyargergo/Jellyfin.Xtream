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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.MpegTs.Infrastructure.Pooling;

/// <summary>
/// High-performance generic object pool for <see cref="IPoolable"/> instances.
/// </summary>
/// <typeparam name="T">The poolable type.</typeparam>
/// <remarks>
/// <para>
/// Design principles:
/// - Lock-free hot path using ConcurrentQueue
/// - Expiration-based cleanup for memory pressure
/// - RAII lease pattern ensures proper return
/// - Background maintenance for pool health
/// - Native-friendly with minimal allocations
/// </para>
/// </remarks>
public sealed class ObjectPool<T> : IDisposable
    where T : class, IPoolable
{
    /// <summary>
    /// Entry tracking a pooled instance with metadata.
    /// Using readonly struct for zero-allocation enqueue/dequeue.
    /// </summary>
    private readonly struct PoolEntry
    {
        public readonly T Instance;
        public readonly long ReturnedTicks;

        public PoolEntry(T instance)
        {
            Instance = instance;
            ReturnedTicks = Stopwatch.GetTimestamp();
        }

        public TimeSpan IdleTime
        {
            get
            {
                var elapsed = Stopwatch.GetTimestamp() - ReturnedTicks;
                return TimeSpan.FromSeconds((double)elapsed / Stopwatch.Frequency);
            }
        }
    }

    // Configuration
    private readonly PoolOptions _options;
    private readonly Func<T> _factory;
    private readonly ILogger? _logger;
    private readonly string _poolName;

    // Pool state - lock-free concurrent collections
    private readonly ConcurrentQueue<PoolEntry> _available = new();
    private readonly ConcurrentDictionary<Guid, T> _inUse = new();

    // Maintenance
    private readonly Timer _maintenanceTimer;
    private readonly SemaphoreSlim _warmupLock = new(1, 1);
    private volatile bool _warmedUp;

    // Metrics - using Interlocked for thread-safe updates
    private long _totalRented;
    private long _totalReturned;
    private long _totalCreated;
    private long _totalDisposed;
    private long _totalExpired;
    private long _poolHits;
    private long _poolMisses;

    // Lifecycle
    private volatile bool _disposed;

    /// <summary>
    /// Gets a value indicating whether the pool is disposed.
    /// </summary>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ObjectPool{T}"/> class.
    /// </summary>
    /// <param name="factory">Factory function to create new instances.</param>
    /// <param name="options">Pool configuration options.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="poolName">Optional name for logging (defaults to type name).</param>
    public ObjectPool(Func<T> factory, PoolOptions? options = null, ILogger? logger = null, string? poolName = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _options = options ?? new PoolOptions();
        _options.Validate();

        _logger = logger;
        _poolName = poolName ?? typeof(T).Name;

        // Start maintenance timer
        _maintenanceTimer = new Timer(
            _ => PerformMaintenance(),
            state: null,
            _options.MaintenanceInterval,
            _options.MaintenanceInterval
        );

        _logger?.PluginLogInformation(
            "{PoolName} pool created: min={Min}, max={Max}, idleTime={Idle}",
            _poolName,
            _options.MinPoolSize,
            _options.MaxPoolSize,
            _options.MaxIdleTime
        );
    }

    /// <summary>
    /// Warms the pool by pre-creating instances up to MinPoolSize.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the warmup operation.</returns>
    public async Task WarmPoolAsync(CancellationToken cancellationToken = default)
    {
        if (_warmedUp)
        {
            return;
        }

        // Ensure only one warmup runs
        if (!await _warmupLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            // Another warmup in progress, wait for it
            await _warmupLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            _warmupLock.Release();
            return;
        }

        try
        {
            if (_warmedUp)
            {
                return;
            }

            _logger?.PluginLogInformation(
                "Warming {PoolName} pool with {Count} instances",
                _poolName,
                _options.MinPoolSize
            );

            // Create instances in parallel
            var tasks = new Task[_options.MinPoolSize];
            for (int i = 0; i < _options.MinPoolSize; i++)
            {
                tasks[i] = Task.Run(
                    () =>
                    {
                        var instance = CreateInstance();
                        if (instance != null)
                        {
                            _available.Enqueue(new PoolEntry(instance));
                        }
                    },
                    cancellationToken
                );
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);

            _warmedUp = true;
            _logger?.PluginLogInformation("{PoolName} pool warmed with {Count} instances", _poolName, _available.Count);
        }
        catch (Exception ex)
        {
            _logger?.PluginLogError(ex, "Failed to warm {PoolName} pool", _poolName);
            throw;
        }
        finally
        {
            _warmupLock.Release();
        }
    }

    /// <summary>
    /// Rents an instance from the pool.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A lease wrapping the rented instance.</returns>
    public async ValueTask<PoolLease<T>> RentAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Interlocked.Increment(ref _totalRented);

        // Ensure pool is warmed if configured
        if (_options.WarmOnFirstUse && !_warmedUp)
        {
            await WarmPoolAsync(cancellationToken).ConfigureAwait(false);
        }

        // Fast path: try to get from pool
        var instance = TryAcquireFromPool();
        if (instance != null)
        {
            Interlocked.Increment(ref _poolHits);
            return new PoolLease<T>(instance, this, isPooled: true);
        }

        Interlocked.Increment(ref _poolMisses);

        // Check if we can create new (under max limit)
        var totalActive = _inUse.Count + _available.Count;
        if (totalActive < _options.MaxPoolSize)
        {
            instance = CreateAndTrack();
            if (instance != null)
            {
                return new PoolLease<T>(instance, this, isPooled: true);
            }
        }

        // Pool exhausted - wait with timeout
        _logger?.LogDebugIfEnabled(
            "{PoolName} pool exhausted (inUse={InUse}, available={Available}), waiting...",
            _poolName,
            _inUse.Count,
            _available.Count
        );

        return await WaitForAvailableAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets a prepared instance from the pool (with health check and PrepareForReuse called).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A prepared instance, or null if unavailable.</returns>
    public async ValueTask<T?> GetPreparedInstanceAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return null;
        }

        const int MaxRetries = 2;

        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            T? instance;
            try
            {
                instance = TryAcquireFromPool();
                if (instance == null)
                {
                    // Try to create new if under limit
                    var totalActive = _inUse.Count + _available.Count;
                    if (totalActive < _options.MaxPoolSize)
                    {
                        instance = CreateAndTrack();
                    }
                }

                if (instance == null)
                {
                    // Pool exhausted, wait for one to become available
                    var lease = await WaitForAvailableAsync(cancellationToken).ConfigureAwait(false);
                    instance = lease.Instance;
                }
            }
            catch (TimeoutException)
            {
                _logger?.PluginLogWarning("{PoolName} pool exhausted while trying to get prepared instance", _poolName);
                return null;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }

            // Check health and prepare for reuse
            if (instance.IsHealthy && instance.PrepareForReuse())
            {
                Interlocked.Increment(ref _totalRented);
                return instance;
            }

            // Instance unhealthy or failed to prepare, return it and retry
            _logger?.LogDebugIfEnabled(
                "{PoolName} instance {Id} unhealthy or failed PrepareForReuse on attempt {Attempt}, retrying",
                _poolName,
                instance.InstanceId,
                attempt + 1
            );

            await ReturnAsync(instance).ConfigureAwait(false);
        }

        _logger?.PluginLogWarning(
            "{PoolName}: Failed to get prepared instance after {MaxRetries} attempts",
            _poolName,
            MaxRetries
        );
        return null;
    }

    /// <summary>
    /// Returns an instance to the pool.
    /// </summary>
    /// <param name="instance">The instance to return.</param>
    public void Return(T instance)
    {
        if (instance == null)
        {
            return;
        }

        Interlocked.Increment(ref _totalReturned);

        // Remove from in-use tracking
        if (!_inUse.TryRemove(instance.InstanceId, out _))
        {
            _logger?.PluginLogWarning(
                "{PoolName}: Returned instance {Id} was not tracked as in-use",
                _poolName,
                instance.InstanceId
            );
        }

        if (_disposed)
        {
            DisposeInstanceSafely(instance);
            return;
        }

        // Check pool capacity
        if (_available.Count >= _options.MaxPoolSize)
        {
            _logger?.LogDebugIfEnabled(
                "{PoolName}: Pool full, disposing returned instance {Id}",
                _poolName,
                instance.InstanceId
            );
            DisposeInstanceSafely(instance);
            return;
        }

        // Prepare for reuse
        if (!instance.PrepareForReuse())
        {
            _logger?.LogDebugIfEnabled(
                "{PoolName}: Instance {Id} failed PrepareForReuse, disposing",
                _poolName,
                instance.InstanceId
            );
            DisposeInstanceSafely(instance);
            return;
        }

        // Return to pool
        _available.Enqueue(new PoolEntry(instance));
        _logger?.LogTrace("{PoolName}: Returned instance {Id} to pool", _poolName, instance.InstanceId);
    }

    /// <summary>
    /// Returns an instance to the pool asynchronously.
    /// </summary>
    /// <param name="instance">The instance to return.</param>
    /// <returns>A task representing the return operation.</returns>
    internal ValueTask ReturnAsync(T instance)
    {
        Return(instance);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Gets pool statistics.
    /// </summary>
    /// <returns>Current pool statistics.</returns>
    public PoolStatistics GetStatistics()
    {
        var hits = Volatile.Read(ref _poolHits);
        var misses = Volatile.Read(ref _poolMisses);
        var total = hits + misses;

        return new PoolStatistics
        {
            Available = _available.Count,
            InUse = _inUse.Count,
            TotalCreated = Volatile.Read(ref _totalCreated),
            TotalDisposed = Volatile.Read(ref _totalDisposed),
            TotalRented = Volatile.Read(ref _totalRented),
            TotalReturned = Volatile.Read(ref _totalReturned),
            TotalExpired = Volatile.Read(ref _totalExpired),
            HitRate = total == 0 ? 0.0 : (double)hits / total,
        };
    }

    private T? CreateInstance()
    {
        try
        {
            var instance = _factory();
            Interlocked.Increment(ref _totalCreated);

            _logger?.LogDebugIfEnabled(
                "{PoolName}: Created instance {Id} (total: {Total})",
                _poolName,
                instance.InstanceId,
                Volatile.Read(ref _totalCreated)
            );

            return instance;
        }
        catch (Exception ex)
        {
            _logger?.PluginLogError(ex, "{PoolName}: Failed to create instance", _poolName);
            return null;
        }
    }

    private T? TryAcquireFromPool()
    {
        var maxIdleTicks = (long)(_options.MaxIdleTime.TotalSeconds * Stopwatch.Frequency);
        var now = Stopwatch.GetTimestamp();

        while (_available.TryDequeue(out var entry))
        {
            // Check expiration
            if (now - entry.ReturnedTicks > maxIdleTicks)
            {
                _logger?.LogDebugIfEnabled(
                    "{PoolName}: Disposing expired instance {Id}",
                    _poolName,
                    entry.Instance.InstanceId
                );
                DisposeInstanceSafely(entry.Instance);
                Interlocked.Increment(ref _totalExpired);
                continue;
            }

            // Check health
            if (!entry.Instance.IsHealthy)
            {
                _logger?.LogDebugIfEnabled(
                    "{PoolName}: Disposing unhealthy instance {Id}",
                    _poolName,
                    entry.Instance.InstanceId
                );
                DisposeInstanceSafely(entry.Instance);
                continue;
            }

            // Track as in-use
            _inUse.TryAdd(entry.Instance.InstanceId, entry.Instance);
            return entry.Instance;
        }

        return null;
    }

    private T? CreateAndTrack()
    {
        var instance = CreateInstance();
        if (instance != null)
        {
            _inUse.TryAdd(instance.InstanceId, instance);
        }

        return instance;
    }

    private async ValueTask<PoolLease<T>> WaitForAvailableAsync(CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.AcquisitionTimeout);

        var pollInterval = TimeSpan.FromMilliseconds(50);
        var startTime = Stopwatch.GetTimestamp();

        try
        {
            while (!timeoutCts.Token.IsCancellationRequested)
            {
                await Task.Delay(pollInterval, timeoutCts.Token).ConfigureAwait(false);

                var instance = TryAcquireFromPool();
                if (instance != null)
                {
                    var waitTime = TimeSpan.FromSeconds(
                        (double)(Stopwatch.GetTimestamp() - startTime) / Stopwatch.Frequency
                    );
                    _logger?.LogDebugIfEnabled(
                        "{PoolName}: Acquired instance after waiting {WaitMs}ms",
                        _poolName,
                        waitTime.TotalMilliseconds
                    );
                    return new PoolLease<T>(instance, this, isPooled: true);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout, not external cancellation
        }

        throw new TimeoutException(
            $"{_poolName}: Failed to acquire instance within {_options.AcquisitionTimeout}. "
                + $"Pool: {_available.Count} available, {_inUse.Count} in use, {_options.MaxPoolSize} max."
        );
    }

    private void DisposeInstanceSafely(T instance)
    {
        try
        {
            // Dispose on background thread to avoid blocking
            _ = Task.Run(() =>
            {
                try
                {
                    instance.Dispose();
                    Interlocked.Increment(ref _totalDisposed);
                }
                catch (Exception ex)
                {
                    _logger?.PluginLogError(
                        ex,
                        "{PoolName}: Error disposing instance {Id}",
                        _poolName,
                        instance.InstanceId
                    );
                }
            });
        }
        catch (Exception ex)
        {
            _logger?.PluginLogError(ex, "{PoolName}: Failed to schedule instance disposal", _poolName);
        }
    }

    private void PerformMaintenance()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var expiredCount = 0;
            var maxIdleTicks = (long)(_options.MaxIdleTime.TotalSeconds * Stopwatch.Frequency);
            var now = Stopwatch.GetTimestamp();

            // Drain and filter pool
            var snapshot = new List<PoolEntry>();
            while (_available.TryDequeue(out var entry))
            {
                snapshot.Add(entry);
            }

            foreach (var entry in snapshot)
            {
                if (now - entry.ReturnedTicks > maxIdleTicks)
                {
                    // Expired
                    DisposeInstanceSafely(entry.Instance);
                    Interlocked.Increment(ref _totalExpired);
                    expiredCount++;
                }
                else if (_available.Count < _options.MaxPoolSize)
                {
                    // Still valid, re-enqueue
                    _available.Enqueue(entry);
                }
                else
                {
                    // Pool too large
                    DisposeInstanceSafely(entry.Instance);
                }
            }

            if (expiredCount > 0)
            {
                _logger?.LogDebugIfEnabled(
                    "{PoolName}: Maintenance disposed {Count} expired instances",
                    _poolName,
                    expiredCount
                );
            }

            // Replenish to minimum if needed
            while (_available.Count < _options.MinPoolSize && !_disposed)
            {
                var instance = CreateInstance();
                if (instance != null)
                {
                    _available.Enqueue(new PoolEntry(instance));
                    _logger?.LogDebugIfEnabled(
                        "{PoolName}: Maintenance replenished pool with instance {Id}",
                        _poolName,
                        instance.InstanceId
                    );
                }
                else
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.PluginLogError(ex, "{PoolName}: Error during pool maintenance", _poolName);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Stop maintenance
        _maintenanceTimer.Dispose();

        // Dispose pooled instances
        while (_available.TryDequeue(out var entry))
        {
            try
            {
                entry.Instance.Dispose();
                Interlocked.Increment(ref _totalDisposed);
            }
            catch (Exception ex)
            {
                _logger?.PluginLogError(ex, "{PoolName}: Error disposing pooled instance", _poolName);
            }
        }

        // Log warning for any still in-use
        if (!_inUse.IsEmpty)
        {
            _logger?.PluginLogWarning(
                "{PoolName}: Pool disposed with {Count} instances still in use - they will be disposed when returned",
                _poolName,
                _inUse.Count
            );
        }

        _warmupLock.Dispose();

        _logger?.PluginLogInformation("{PoolName}: Pool disposed. Final stats: {Stats}", _poolName, GetStatistics());
    }
}
