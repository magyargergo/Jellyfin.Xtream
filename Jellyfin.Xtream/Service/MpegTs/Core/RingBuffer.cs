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
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Jellyfin.Xtream.Service.MpegTs.Core;

/// <summary>
/// Lock-free, fixed-size ring buffer for high-performance timestamp tracking.
/// Overwrites oldest entries when full. Thread-safe for single writer, multiple readers.
/// Uses power-of-2 capacity for bitwise AND masking instead of modulo (5x faster).
/// </summary>
/// <typeparam name="T">The type of elements stored.</typeparam>
public sealed class RingBuffer<T>
    where T : struct
{
    private readonly T[] _buffer;
    private int _writeIndex;
    private int _count;

    /// <summary>
    /// Initializes a new instance of the <see cref="RingBuffer{T}"/> class.
    /// Capacity is rounded up to the nearest power of 2 for optimal performance.
    /// </summary>
    /// <param name="capacity">The minimum capacity of the buffer.</param>
    public RingBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        }

        // Round up to power of 2 for fast bitwise AND instead of modulo
        Capacity = (int)BitOperations.RoundUpToPowerOf2((uint)capacity);
        Mask = Capacity - 1;
        _buffer = new T[Capacity];
    }

    /// <summary>
    /// Gets the current number of items in the buffer.
    /// </summary>
    public int Count => Math.Min(Volatile.Read(ref _count), Capacity);

    /// <summary>
    /// Gets the capacity of the buffer.
    /// </summary>
    public int Capacity { get; }

    /// <summary>
    /// Gets a value indicating whether the buffer is empty.
    /// </summary>
    public bool IsEmpty => Count == 0;

    /// <summary>
    /// Gets the mask used for index calculations (for enumerator optimization).
    /// </summary>
    internal int Mask { get; }

    /// <summary>
    /// Gets an item by index (0 = oldest, Count-1 = newest).
    /// </summary>
    /// <param name="index">The zero-based index from oldest to newest.</param>
    /// <returns>The item at the specified index.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Index is out of range.</exception>
    public T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var count = Count;
            if ((uint)index >= (uint)count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            var startIndex = count >= Capacity ? Volatile.Read(ref _writeIndex) & Mask : 0;
            var actualIndex = (startIndex + index) & Mask;
            return _buffer[actualIndex];
        }
    }

    /// <summary>
    /// Adds an item to the buffer, overwriting the oldest if full.
    /// </summary>
    /// <param name="item">The item to add.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(T item)
    {
        var index = Interlocked.Increment(ref _writeIndex) - 1;
        var actualIndex = index & Mask; // Bitwise AND is ~5x faster than modulo

        _buffer[actualIndex] = item;
        _ = Interlocked.Increment(ref _count);
    }

    /// <summary>
    /// Gets the most recently added item.
    /// </summary>
    /// <param name="item">The most recent item if available.</param>
    /// <returns>True if an item was available.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetLatest(out T item)
    {
        var count = Count;
        if (count == 0)
        {
            item = default;
            return false;
        }

        var lastIndex = (Volatile.Read(ref _writeIndex) - 1) & Mask;
        item = _buffer[lastIndex];
        return true;
    }

    /// <summary>
    /// Iterates over all items in the buffer from oldest to newest.
    /// </summary>
    /// <returns>An enumerator for the buffer contents.</returns>
    public RingBufferEnumerator<T> GetEnumerator() => new(_buffer, Mask, Count, Volatile.Read(ref _writeIndex));

    /// <summary>
    /// Clears the buffer.
    /// </summary>
    public void Clear()
    {
        Volatile.Write(ref _writeIndex, 0);
        Volatile.Write(ref _count, 0);
        Array.Clear(_buffer, 0, Capacity);
    }
}
