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
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Jellyfin.Xtream.Service.MpegTs.Infrastructure.Pooling;

/// <summary>
/// RAII wrapper that returns a pooled instance on dispose.
/// Implements IAsyncDisposable for proper async cleanup.
/// </summary>
/// <typeparam name="T">The poolable type.</typeparam>
/// <remarks>
/// <para>
/// This struct ensures instances are always returned to the pool, even if exceptions occur.
/// Usage pattern:
/// <code>
/// await using var lease = await pool.RentAsync();
/// var instance = lease.Instance;
/// // Use instance...
/// </code>
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Auto)]
public readonly struct PoolLease<T> : IDisposable, IAsyncDisposable, IEquatable<PoolLease<T>>
    where T : class, IPoolable
{
    private readonly ObjectPool<T>? _pool;
    private readonly bool _isPooled;

    /// <summary>
    /// Gets the rented instance.
    /// </summary>
    public T Instance { get; }

    /// <summary>
    /// Gets a value indicating whether this lease is valid (has an instance).
    /// </summary>
    public bool IsValid => Instance != null;

    /// <summary>
    /// Initializes a new instance of the <see cref="PoolLease{T}"/> struct.
    /// </summary>
    /// <param name="instance">The rented instance.</param>
    /// <param name="pool">The pool to return to.</param>
    /// <param name="isPooled">Whether this instance should be returned to pool.</param>
    internal PoolLease(T instance, ObjectPool<T>? pool, bool isPooled)
    {
        Instance = instance;
        _pool = pool;
        _isPooled = isPooled;
    }

    /// <summary>
    /// Returns the instance to the pool synchronously.
    /// Prefer DisposeAsync() when in async context for proper cleanup.
    /// </summary>
    public void Dispose()
    {
        if (Instance == null)
        {
            return;
        }

        if (_isPooled && _pool != null)
        {
            _pool.Return(Instance);
        }
        else
        {
            Instance.Dispose();
        }
    }

    /// <summary>
    /// Returns the instance to the pool asynchronously.
    /// Allows for proper async cleanup of resources.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (Instance == null)
        {
            return ValueTask.CompletedTask;
        }

        if (_isPooled && _pool != null)
        {
            return _pool.ReturnAsync(Instance);
        }

        Instance.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public bool Equals(PoolLease<T> other) =>
        ReferenceEquals(Instance, other.Instance) && ReferenceEquals(_pool, other._pool);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is PoolLease<T> other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Instance, _pool);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(PoolLease<T> left, PoolLease<T> right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(PoolLease<T> left, PoolLease<T> right) => !left.Equals(right);
}
