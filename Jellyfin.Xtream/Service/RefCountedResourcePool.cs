// Copyright (C) 2025  Gergo Magyar

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// A reference-counted resource pool that tracks consumers and notifies when count changes.
/// Implements the Reference Counting Pattern for shared IDisposable ownership.
/// </summary>
/// <remarks>
/// <para>
/// Design Pattern: Reference Counting (similar to C++ shared_ptr).
/// </para>
/// <para>
/// Use Case: Multiple consumers share a single stream source. The owner needs to know
/// when all consumers have disconnected to clean up resources.
/// </para>
/// <para>
/// Thread Safety: All operations are thread-safe via interlocked operations.
/// </para>
/// </remarks>
/// <typeparam name="T">The type of stream being wrapped.</typeparam>
/// <remarks>
/// Initializes a new instance of the <see cref="RefCountedResourcePool{T}"/> class.
/// </remarks>
/// <param name="streamFactory">Factory function to create new stream instances for each consumer.</param>
/// <param name="onConsumerCountChanged">Optional callback when consumer count changes. Receives new count.</param>
public sealed class RefCountedResourcePool<T>(Func<T> streamFactory, Action<int>? onConsumerCountChanged = null)
    : IDisposable
    where T : Stream
{
    private readonly Func<T> _streamFactory = streamFactory;
    private readonly Action<int>? _onConsumerCountChanged = onConsumerCountChanged;
    private readonly ConcurrentBag<WeakReference<Stream>> _createdStreams = [];
    private int _consumerCount;
    private bool _isDisposed;

    /// <summary>
    /// Gets the current number of active consumers.
    /// </summary>
    public int ConsumerCount => Volatile.Read(ref _consumerCount);

    /// <summary>
    /// Gets a value indicating whether this pool has been disposed.
    /// </summary>
    public bool IsDisposed => Volatile.Read(ref _isDisposed);

    /// <summary>
    /// Acquires a new stream reference for a consumer.
    /// </summary>
    /// <returns>A stream handle that decrements the consumer count when disposed, or null if the pool is disposed.</returns>
    public Stream? Acquire()
    {
        // Return null instead of throwing when disposed - allows graceful handling
        // during race conditions when the stream is being torn down
        if (Volatile.Read(ref _isDisposed))
        {
            return null;
        }

        var stream = _streamFactory();
        int newCount = Interlocked.Increment(ref _consumerCount);
        _onConsumerCountChanged?.Invoke(newCount);

        var wrapper = new RefCountedReaderStream<T>(stream, Release);

        // Track the wrapper with a weak reference for cleanup on pool disposal
        _createdStreams.Add(new WeakReference<Stream>(wrapper));

        return wrapper;
    }

    /// <summary>
    /// Disposes the pool and attempts to dispose any still-active streams.
    /// Marks the pool as disposed, preventing new acquisitions.
    /// </summary>
    public void Dispose()
    {
        if (Volatile.Read(ref _isDisposed))
        {
            return;
        }

        Volatile.Write(ref _isDisposed, true);

        // Attempt to dispose any streams that are still alive
        // This is a safety net - consumers should dispose their own streams
        foreach (var weakRef in _createdStreams)
        {
            if (weakRef.TryGetTarget(out var stream))
            {
                try
                {
                    stream.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // Already disposed - ignore
                }
            }
        }
    }

    private void Release()
    {
        int newCount = Interlocked.Decrement(ref _consumerCount);
        _onConsumerCountChanged?.Invoke(newCount);
    }
}
