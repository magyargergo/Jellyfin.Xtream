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
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Jellyfin.Xtream.Service.ProviderManagement;

/// <summary>
/// High-performance provider index using array-based lookups instead of dictionary.
/// Assigns stable integer indices to providers for O(1) access patterns.
/// Thread-safe with lock-free reads and minimal locking for registration.
/// </summary>
public sealed class FastProviderIndex
{
    private const int InitialCapacity = 16;
    private const int MaxProviders = 256;

    private readonly object _registrationLock = new();
    private readonly Dictionary<string, int> _idToIndex;

    private string[] _indexToId;
    private int[] _scores;
    private long[] _lastUpdateTicks;
    private int _count;

    /// <summary>
    /// Initializes a new instance of the <see cref="FastProviderIndex"/> class.
    /// </summary>
    public FastProviderIndex()
    {
        _idToIndex = new Dictionary<string, int>(InitialCapacity, StringComparer.Ordinal);
        _indexToId = new string[InitialCapacity];
        _scores = new int[InitialCapacity];
        _lastUpdateTicks = new long[InitialCapacity];
        _count = 0;
    }

    /// <summary>
    /// Gets the number of registered providers.
    /// </summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>
    /// Gets or registers a provider and returns its index.
    /// Thread-safe: uses lock for registration, lock-free for existing lookups.
    /// </summary>
    /// <param name="providerId">The provider ID string.</param>
    /// <returns>The integer index for this provider.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetOrRegisterIndex(string providerId)
    {
        // Fast path: check if already registered (lock-free read)
        if (_idToIndex.TryGetValue(providerId, out var existingIndex))
        {
            return existingIndex;
        }

        // Slow path: register new provider
        return RegisterProvider(providerId);
    }

    /// <summary>
    /// Tries to get the index for a provider ID without registering.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="index">The output index if found.</param>
    /// <returns>True if the provider was found.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetIndex(string providerId, out int index) => _idToIndex.TryGetValue(providerId, out index);

    /// <summary>
    /// Gets the provider ID for a given index.
    /// </summary>
    /// <param name="index">The provider index.</param>
    /// <returns>The provider ID, or null if index is invalid.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string? GetProviderId(int index)
    {
        var ids = _indexToId;
        return (uint)index >= (uint)Volatile.Read(ref _count) ? null : ids[index];
    }

    /// <summary>
    /// Gets the score for a provider by index. O(1) array access.
    /// </summary>
    /// <param name="index">The provider index.</param>
    /// <returns>The provider score, or 0 if index is invalid.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetScore(int index)
    {
        var scores = _scores;
        return (uint)index >= (uint)Volatile.Read(ref _count) ? 0 : Volatile.Read(ref scores[index]);
    }

    /// <summary>
    /// Gets the score for a provider by ID. O(1) dictionary + array access.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>The provider score, or 0 if not found.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetScore(string providerId) => !TryGetIndex(providerId, out var index) ? 0 : GetScore(index);

    /// <summary>
    /// Sets the score for a provider by index. Thread-safe using volatile writes.
    /// </summary>
    /// <param name="index">The provider index.</param>
    /// <param name="score">The new score value.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetScore(int index, int score)
    {
        var scores = _scores;
        var updateTicks = _lastUpdateTicks;

        if ((uint)index >= (uint)Volatile.Read(ref _count))
        {
            return;
        }

        Volatile.Write(ref scores[index], score);
        Volatile.Write(ref updateTicks[index], DateTime.UtcNow.Ticks);
    }

    /// <summary>
    /// Sets the score for a provider by ID. Thread-safe.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="score">The new score value.</param>
    /// <returns>True if the provider was found and updated.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SetScore(string providerId, int score)
    {
        if (!TryGetIndex(providerId, out var index))
        {
            return false;
        }

        SetScore(index, score);
        return true;
    }

    /// <summary>
    /// Gets the time since last score update in milliseconds.
    /// </summary>
    /// <param name="index">The provider index.</param>
    /// <returns>Milliseconds since last update, or long.MaxValue if never updated.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetMillisSinceUpdate(int index)
    {
        var updateTicks = _lastUpdateTicks;
        if ((uint)index >= (uint)Volatile.Read(ref _count))
        {
            return long.MaxValue;
        }

        var lastTicks = Volatile.Read(ref updateTicks[index]);
        return lastTicks == 0 ? long.MaxValue : (DateTime.UtcNow.Ticks - lastTicks) / TimeSpan.TicksPerMillisecond;
    }

    /// <summary>
    /// Gets the indices of the top N providers by score.
    /// Uses insertion sort for small N (optimal for N less than 10).
    /// </summary>
    /// <param name="topN">Maximum number of providers to return.</param>
    /// <returns>Array of indices sorted by score descending.</returns>
    public int[] GetTopProviderIndices(int topN)
    {
        var count = Volatile.Read(ref _count);
        if (count == 0)
        {
            return [];
        }

        topN = Math.Min(topN, count);
        var scores = _scores;

        // For small arrays, use simple sorting
        if (count <= 16)
        {
            Span<(int Index, int Score)> items = stackalloc (int, int)[count];
            for (var i = 0; i < count; i++)
            {
                items[i] = (i, Volatile.Read(ref scores[i]));
            }

            // Sort descending by score using insertion sort (optimal for small N)
            for (var i = 1; i < count; i++)
            {
                var key = items[i];
                var j = i - 1;
                while (j >= 0 && items[j].Score < key.Score)
                {
                    items[j + 1] = items[j];
                    j--;
                }

                items[j + 1] = key;
            }

            var result = new int[topN];
            for (var i = 0; i < topN; i++)
            {
                result[i] = items[i].Index;
            }

            return result;
        }

        // For larger arrays, use partial selection
        return GetTopNPartialSort(topN, count, scores);
    }

    /// <summary>
    /// Checks if a score is stale (older than specified milliseconds).
    /// </summary>
    /// <param name="index">The provider index.</param>
    /// <param name="staleThresholdMs">The staleness threshold in milliseconds.</param>
    /// <returns>True if the score is stale or never updated.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsScoreStale(int index, long staleThresholdMs) => GetMillisSinceUpdate(index) > staleThresholdMs;

    /// <summary>
    /// Gets all provider IDs as a read-only span.
    /// </summary>
    /// <returns>ReadOnlySpan of provider IDs.</returns>
    public ReadOnlySpan<string> GetAllProviderIds()
    {
        var count = Volatile.Read(ref _count);
        return new ReadOnlySpan<string>(_indexToId, 0, count);
    }

    /// <summary>
    /// Gets all scores as a read-only span.
    /// Note: This is a snapshot; scores may change during enumeration.
    /// </summary>
    /// <returns>ReadOnlySpan of scores.</returns>
    public ReadOnlySpan<int> GetAllScores()
    {
        var count = Volatile.Read(ref _count);
        return new ReadOnlySpan<int>(_scores, 0, count);
    }

    /// <summary>
    /// Clears all cached scores without removing registrations.
    /// </summary>
    public void ClearScores()
    {
        var scores = _scores;
        var updateTicks = _lastUpdateTicks;
        var count = Volatile.Read(ref _count);

        for (var i = 0; i < count; i++)
        {
            Volatile.Write(ref scores[i], 0);
            Volatile.Write(ref updateTicks[i], 0);
        }
    }

    private int RegisterProvider(string providerId)
    {
        lock (_registrationLock)
        {
            // Double-check after acquiring lock
            if (_idToIndex.TryGetValue(providerId, out var existingIndex))
            {
                return existingIndex;
            }

            var currentCount = Volatile.Read(ref _count);
            if (currentCount >= MaxProviders)
            {
                throw new InvalidOperationException($"Maximum provider limit ({MaxProviders}) exceeded");
            }

            // Grow arrays if needed
            if (currentCount >= _indexToId.Length)
            {
                GrowArrays();
            }

            var newIndex = currentCount;
            _indexToId[newIndex] = providerId;
            _idToIndex[providerId] = newIndex;

            // Publish the new count after arrays are updated
            Volatile.Write(ref _count, currentCount + 1);

            return newIndex;
        }
    }

    private void GrowArrays()
    {
        var newCapacity = Math.Min(_indexToId.Length * 2, MaxProviders);

        var newIds = new string[newCapacity];
        var newScores = new int[newCapacity];
        var newUpdateTicks = new long[newCapacity];

        Array.Copy(_indexToId, newIds, _count);
        Array.Copy(_scores, newScores, _count);
        Array.Copy(_lastUpdateTicks, newUpdateTicks, _count);

        _indexToId = newIds;
        _scores = newScores;
        _lastUpdateTicks = newUpdateTicks;
    }

    private static int[] GetTopNPartialSort(int topN, int count, int[] scores)
    {
        // Use a min-heap approach for larger arrays
        var heap = new (int Score, int Index)[topN];
        var heapSize = 0;

        for (var i = 0; i < count; i++)
        {
            var score = Volatile.Read(ref scores[i]);

            if (heapSize < topN)
            {
                // Add to heap
                heap[heapSize] = (score, i);
                heapSize++;

                // Heapify up
                var child = heapSize - 1;
                while (child > 0)
                {
                    var parent = (child - 1) / 2;
                    if (heap[parent].Score <= heap[child].Score)
                    {
                        break;
                    }

                    (heap[parent], heap[child]) = (heap[child], heap[parent]);
                    child = parent;
                }
            }
            else if (score > heap[0].Score)
            {
                // Replace minimum
                heap[0] = (score, i);

                // Heapify down
                var parent = 0;
                while (true)
                {
                    var left = (2 * parent) + 1;
                    var right = (2 * parent) + 2;
                    var smallest = parent;

                    if (left < heapSize && heap[left].Score < heap[smallest].Score)
                    {
                        smallest = left;
                    }

                    if (right < heapSize && heap[right].Score < heap[smallest].Score)
                    {
                        smallest = right;
                    }

                    if (smallest == parent)
                    {
                        break;
                    }

                    (heap[parent], heap[smallest]) = (heap[smallest], heap[parent]);
                    parent = smallest;
                }
            }
        }

        // Extract results in descending order
        var result = new int[heapSize];
        for (var i = heapSize - 1; i >= 0; i--)
        {
            result[i] = heap[0].Index;

            // Remove min and heapify
            heap[0] = heap[heapSize - 1];
            heapSize--;

            var parent = 0;
            while (true)
            {
                var left = (2 * parent) + 1;
                var right = (2 * parent) + 2;
                var smallest = parent;

                if (left < heapSize && heap[left].Score < heap[smallest].Score)
                {
                    smallest = left;
                }

                if (right < heapSize && heap[right].Score < heap[smallest].Score)
                {
                    smallest = right;
                }

                if (smallest == parent)
                {
                    break;
                }

                (heap[parent], heap[smallest]) = (heap[smallest], heap[parent]);
                parent = smallest;
            }
        }

        return result;
    }
}
