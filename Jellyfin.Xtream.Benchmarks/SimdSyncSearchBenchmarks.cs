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
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace Jellyfin.Xtream.Benchmarks;

/// <summary>
/// Dedicated benchmarks for SIMD sync byte search optimization.
/// Compares scalar vs SIMD performance across different data sizes and corruption patterns.
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
[DisassemblyDiagnoser(maxDepth: 2)]
[HardwareCounters(
    BenchmarkDotNet.Diagnosers.HardwareCounter.CacheMisses,
    BenchmarkDotNet.Diagnosers.HardwareCounter.BranchMispredictions,
    BenchmarkDotNet.Diagnosers.HardwareCounter.TotalCycles
)]
public class SimdSyncSearchBenchmarks
{
    private const byte SyncByte = 0x47;

    private byte[]? _searchData_Small;
    private byte[]? _searchData_Medium;
    private byte[]? _searchData_Large;
    private byte[]? _searchData_NoMatch;

    /// <summary>
    /// Gets or sets position of sync byte in test data.
    /// </summary>
    [Params(10, 100, 1000, 10000, 50000)] // Test various distances
    public int SyncBytePosition { get; set; }

    /// <inheritdoc/>
    [GlobalSetup]
    public void Setup()
    {
        // Small: 1KB
        _searchData_Small = new byte[1024];
        Random.Shared.NextBytes(_searchData_Small);
        if (SyncBytePosition < _searchData_Small.Length)
        {
            _searchData_Small[SyncBytePosition] = SyncByte;
        }

        // Medium: 100KB
        _searchData_Medium = new byte[100 * 1024];
        Random.Shared.NextBytes(_searchData_Medium);
        if (SyncBytePosition < _searchData_Medium.Length)
        {
            _searchData_Medium[SyncBytePosition] = SyncByte;
        }

        // Large: 1MB
        _searchData_Large = new byte[1024 * 1024];
        Random.Shared.NextBytes(_searchData_Large);
        if (SyncBytePosition < _searchData_Large.Length)
        {
            _searchData_Large[SyncBytePosition] = SyncByte;
        }

        // No match case (worst case - full scan)
        _searchData_NoMatch = new byte[100 * 1024];
        Random.Shared.NextBytes(_searchData_NoMatch);
        // Ensure no sync bytes
        for (int i = 0; i < _searchData_NoMatch.Length; i++)
        {
            if (_searchData_NoMatch[i] == SyncByte)
            {
                _searchData_NoMatch[i] = 0x00;
            }
        }
    }

    /// <summary>
    /// Baseline: Scalar search (original implementation).
    /// </summary>
    /// <returns></returns>
    [Benchmark(Baseline = true)]
    public int Scalar_Medium_100KB()
    {
        return FindSyncByteScalar(_searchData_Medium!, 0);
    }

    /// <summary>
    /// Baseline: Scalar search on large data.
    /// </summary>
    /// <returns></returns>
    [Benchmark]
    public int Scalar_Large_1MB()
    {
        return FindSyncByteScalar(_searchData_Large!, 0);
    }

    /// <summary>
    /// Baseline: Scalar search with no match (worst case).
    /// </summary>
    /// <returns></returns>
    [Benchmark]
    public int Scalar_NoMatch_100KB()
    {
        return FindSyncByteScalar(_searchData_NoMatch!, 0);
    }

    /// <summary>
    /// Optimized: SIMD search (AVX2/SSE2).
    /// </summary>
    /// <returns></returns>
    [Benchmark]
    public int SIMD_Medium_100KB()
    {
        return FindSyncByteSIMD(_searchData_Medium!, 0);
    }

    /// <summary>
    /// Optimized: SIMD search on large data.
    /// </summary>
    /// <returns></returns>
    [Benchmark]
    public int SIMD_Large_1MB()
    {
        return FindSyncByteSIMD(_searchData_Large!, 0);
    }

    /// <summary>
    /// Optimized: SIMD search with no match (worst case).
    /// </summary>
    /// <returns></returns>
    [Benchmark]
    public int SIMD_NoMatch_100KB()
    {
        return FindSyncByteSIMD(_searchData_NoMatch!, 0);
    }

    /// <summary>
    /// Optimized: SIMD search on small data (may not benefit from SIMD).
    /// </summary>
    /// <returns></returns>
    [Benchmark]
    public int SIMD_Small_1KB()
    {
        return FindSyncByteSIMD(_searchData_Small!, 0);
    }

    /// <summary>
    /// Scalar implementation (baseline for comparison).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static int FindSyncByteScalar(ReadOnlySpan<byte> data, int startOffset)
    {
        for (int i = startOffset; i < data.Length; i++)
        {
            if (data[i] == SyncByte)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// SIMD implementation (optimized).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static unsafe int FindSyncByteSIMD(ReadOnlySpan<byte> data, int startOffset)
    {
        int remaining = data.Length - startOffset;

        if (remaining <= 0)
        {
            return -1;
        }

        fixed (byte* ptr = data)
        {
            byte* current = ptr + startOffset;

            // AVX2 path: 32 bytes at once
            if (Avx2.IsSupported && remaining >= 32)
            {
                Vector256<byte> syncPattern = Vector256.Create(SyncByte);
                int vectorLength = remaining & ~31;

                for (int i = 0; i < vectorLength; i += 32)
                {
                    Vector256<byte> chunk = Avx.LoadVector256(current + i);
                    Vector256<byte> cmp = Avx2.CompareEqual(chunk, syncPattern);
                    int mask = Avx2.MoveMask(cmp);

                    if (mask != 0)
                    {
                        int offset = BitOperations.TrailingZeroCount((uint)mask);
                        return startOffset + i + offset;
                    }
                }

                return FindSyncByteScalar(data, startOffset + vectorLength);
            }

            // SSE2 path: 16 bytes at once
            if (Sse2.IsSupported && remaining >= 16)
            {
                Vector128<byte> syncPattern = Vector128.Create(SyncByte);
                int vectorLength = remaining & ~15;

                for (int i = 0; i < vectorLength; i += 16)
                {
                    Vector128<byte> chunk = Sse2.LoadVector128(current + i);
                    Vector128<byte> cmp = Sse2.CompareEqual(chunk, syncPattern);
                    int mask = Sse2.MoveMask(cmp);

                    if (mask != 0)
                    {
                        int offset = BitOperations.TrailingZeroCount((uint)mask);
                        return startOffset + i + offset;
                    }
                }

                return FindSyncByteScalar(data, startOffset + vectorLength);
            }

            return FindSyncByteScalar(data, startOffset);
        }
    }
}
