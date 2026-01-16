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

using System.Runtime.CompilerServices;

namespace Jellyfin.Xtream.Service.MpegTs.Core;

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
        var capacity = mask + 1;
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
            var index = (_startIndex + _currentOffset) & _mask;
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
