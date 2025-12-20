using System.Runtime.CompilerServices;

namespace Jellyfin.Xtream.Service.MpegTs;

/// <summary>
/// High-performance enumerator for ring buffer contents.
/// Uses bitwise AND masking instead of modulo for ~5x faster index calculation.
/// </summary>
/// <typeparam name="T">The type of elements stored.</typeparam>
public ref struct RingBufferEnumerator<T>
    where T : struct
{
    private readonly T[] _buffer;
    private readonly int _mask;
    private readonly int _count;
    private readonly int _startIndex;
    private int _currentOffset;

    /// <summary>
    /// Initializes a new instance of the <see cref="RingBufferEnumerator{T}"/> struct.
    /// </summary>
    /// <param name="buffer">The underlying array.</param>
    /// <param name="mask">The bitmask for index calculation (capacity - 1).</param>
    /// <param name="count">The current item count.</param>
    /// <param name="writeIndex">The current write index.</param>
    internal RingBufferEnumerator(T[] buffer, int mask, int count, int writeIndex)
    {
        _buffer = buffer;
        _mask = mask;
        _count = count;

        // Calculate start index using bitwise AND
        int capacity = mask + 1;
        _startIndex = count >= capacity ? writeIndex & mask : 0;

        _currentOffset = -1;
    }

    /// <summary>
    /// Gets the current item.
    /// </summary>
    public readonly T Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            // Bitwise AND is ~5x faster than modulo
            int index = (_startIndex + _currentOffset) & _mask;
            return _buffer[index];
        }
    }

    /// <summary>
    /// Advances to the next item.
    /// </summary>
    /// <returns>True if there is a next item.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        _currentOffset++;
        return _currentOffset < _count;
    }
}
