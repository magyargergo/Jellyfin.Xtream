using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Jellyfin.Xtream.Service.MpegTs;

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
    private readonly int _capacity;
    private readonly int _mask;
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
        _capacity = (int)BitOperations.RoundUpToPowerOf2((uint)capacity);
        _mask = _capacity - 1;
        _buffer = new T[_capacity];
    }

    /// <summary>
    /// Gets the current number of items in the buffer.
    /// </summary>
    public int Count => Math.Min(Volatile.Read(ref _count), _capacity);

    /// <summary>
    /// Gets the capacity of the buffer.
    /// </summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Gets a value indicating whether the buffer is empty.
    /// </summary>
    public bool IsEmpty => Count == 0;

    /// <summary>
    /// Gets the mask used for index calculations (for enumerator optimization).
    /// </summary>
    internal int Mask => _mask;

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
            int count = Count;
            if ((uint)index >= (uint)count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            int startIndex = count >= _capacity ? Volatile.Read(ref _writeIndex) & _mask : 0;
            int actualIndex = (startIndex + index) & _mask;
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
        int index = Interlocked.Increment(ref _writeIndex) - 1;
        int actualIndex = index & _mask; // Bitwise AND is ~5x faster than modulo

        _buffer[actualIndex] = item;
        Interlocked.Increment(ref _count);
    }

    /// <summary>
    /// Gets the most recently added item.
    /// </summary>
    /// <param name="item">The most recent item if available.</param>
    /// <returns>True if an item was available.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetLatest(out T item)
    {
        int count = Count;
        if (count == 0)
        {
            item = default;
            return false;
        }

        int lastIndex = (Volatile.Read(ref _writeIndex) - 1) & _mask;
        item = _buffer[lastIndex];
        return true;
    }

    /// <summary>
    /// Iterates over all items in the buffer from oldest to newest.
    /// </summary>
    /// <returns>An enumerator for the buffer contents.</returns>
    public RingBufferEnumerator<T> GetEnumerator()
    {
        return new RingBufferEnumerator<T>(_buffer, _mask, Count, Volatile.Read(ref _writeIndex));
    }

    /// <summary>
    /// Clears the buffer.
    /// </summary>
    public void Clear()
    {
        Volatile.Write(ref _writeIndex, 0);
        Volatile.Write(ref _count, 0);
        Array.Clear(_buffer, 0, _capacity);
    }
}
