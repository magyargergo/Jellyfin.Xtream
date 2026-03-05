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

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Shared SIMD-accelerated memory copy utility.
/// Provides hardware-accelerated memory copy using the best available instruction set:
/// AVX-512, AVX2, SSE2, ARM NEON, or portable Vector fallback.
/// Supports non-temporal stores for large transfers to bypass cache pollution.
/// </summary>
internal static class SimdMemoryCopy
{
    /// <summary>
    /// Minimum copy length (in bytes) at which SIMD instructions provide a benefit
    /// over <see cref="System.Buffer.MemoryCopy(void*, void*, long, long)"/>. Below this threshold, the setup
    /// overhead of SIMD registers outweighs the throughput gain.
    /// </summary>
    internal static readonly int SimdThreshold = DetermineSimdThreshold();

    /// <summary>
    /// Number of bytes to prefetch ahead of the current read position.
    /// Tuned per instruction set to balance latency hiding against cache pollution.
    /// </summary>
    internal static readonly int PrefetchDistance = DeterminePrefetchDistance();

    private static readonly bool _avx512Supported = Avx512F.IsSupported;
    private static readonly bool _avx2Supported = Avx2.IsSupported;
    private static readonly bool _sse2Supported = Sse2.IsSupported;
    private static readonly bool _advSimdSupported = AdvSimd.IsSupported;
    private static readonly bool _advSimdArm64Supported = AdvSimd.Arm64.IsSupported;

    /// <summary>
    /// Determines optimal SIMD threshold based on CPU capabilities.
    /// Lower-end CPUs get higher threshold to avoid SIMD overhead.
    /// ARM NEON has similar characteristics to SSE2 for threshold selection.
    /// </summary>
    private static int DetermineSimdThreshold()
    {
        return Avx2.IsSupported ? 512
            : Sse2.IsSupported ? 1024
            : AdvSimd.IsSupported ? 1024
            : 4096;
    }

    /// <summary>
    /// Determines optimal prefetch distance based on CPU capabilities.
    /// Smaller caches on low-end CPUs need shorter prefetch distance to avoid cache pollution.
    /// AVX2 systems typically have larger caches supporting longer prefetch distances.
    /// </summary>
    private static int DeterminePrefetchDistance() => Avx2.IsSupported ? 256 : 128;

    /// <summary>
    /// Hardware-accelerated memory copy using SIMD instructions.
    /// Optimized for multi-core systems with AVX-512/AVX2/SSE2/ARM NEON support.
    /// Uses non-temporal stores for large copies to bypass cache pollution when requested.
    /// </summary>
    /// <param name="src">Source pointer.</param>
    /// <param name="dst">Destination pointer.</param>
    /// <param name="length">Number of bytes to copy.</param>
    /// <param name="useNonTemporal">
    /// When true, uses non-temporal (streaming) stores for aligned destinations.
    /// This bypasses the CPU cache hierarchy and is beneficial for transfers larger
    /// than the last-level cache (typically 256KB+). The write stream sets this to
    /// true for large writes; the read stream always passes false.
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal static unsafe void Copy(byte* src, byte* dst, int length, bool useNonTemporal = false)
    {
        var offset = 0;

        if (_avx512Supported && length >= 64)
        {
            offset = CopyAvx512(src, dst, length, offset, useNonTemporal);
        }
        else if (_avx2Supported && length >= 32)
        {
            offset = CopyAvx2(src, dst, length, offset, useNonTemporal);
        }
        else if (_sse2Supported && length >= 16)
        {
            offset = CopySse2(src, dst, length, offset, useNonTemporal);
        }
        else if (_advSimdSupported && length >= 16)
        {
            offset = CopyAdvSimd(src, dst, length, offset);
        }
        else if (Vector.IsHardwareAccelerated && length >= Vector<byte>.Count)
        {
            offset = CopyVector(src, dst, length, offset);
        }

        // Handle remaining bytes using progressively smaller copy sizes.
        // This avoids the byte-by-byte loop overhead for small remainders.
        CopyRemainder(src, dst, length, offset);

        if (useNonTemporal && Sse2.IsSupported)
        {
            Sse2.MemoryFence();
        }
    }

    /// <summary>
    /// AVX-512 path: 512-bit (64-byte) vectors.
    /// Processes 128 bytes at a time using two vector registers for better pipelining.
    /// Interleaving loads before stores hides memory latency and utilizes out-of-order execution.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static unsafe int CopyAvx512(byte* src, byte* dst, int length, int offset, bool useNonTemporal)
    {
        // Check alignment for non-temporal stores (64-byte alignment for AVX-512)
        var isAligned = ((nuint)(dst + offset) & 63) == 0;
        var useNt = useNonTemporal && isAligned;

        if (useNt && length >= 128)
        {
            // 128-byte non-temporal with 2x pipelining
            var avx512x2Length = length & -128;
            for (; offset < avx512x2Length; offset += 128)
            {
                // Prefetch 2-3 iterations ahead for 128-byte stride
                if (offset + 384 < length)
                {
                    Sse.Prefetch0(src + offset + 256);
                    Sse.Prefetch0(src + offset + 320);
                }

                var vec0 = Avx512F.LoadVector512(src + offset);
                var vec1 = Avx512F.LoadVector512(src + offset + 64);
                Avx512F.StoreAlignedNonTemporal(dst + offset, vec0);
                Avx512F.StoreAlignedNonTemporal(dst + offset + 64, vec1);
            }

            // Handle remaining 64-byte chunk with non-temporal
            var avx512Length = length & -64;
            for (; offset < avx512Length; offset += 64)
            {
                var vec = Avx512F.LoadVector512(src + offset);
                Avx512F.StoreAlignedNonTemporal(dst + offset, vec);
            }
        }
        else if (useNt)
        {
            // 64-byte non-temporal (smaller buffers)
            var avx512Length = length & -64;
            for (; offset < avx512Length; offset += 64)
            {
                if (offset + PrefetchDistance < length)
                {
                    Sse.Prefetch0(src + offset + PrefetchDistance);
                }

                var vec = Avx512F.LoadVector512(src + offset);
                Avx512F.StoreAlignedNonTemporal(dst + offset, vec);
            }
        }
        else if (length >= 128)
        {
            // Regular stores with 128-byte pipelining
            var avx512x2Length = length & -128;
            for (; offset < avx512x2Length; offset += 128)
            {
                // Prefetch 2-3 iterations ahead for 128-byte stride
                if (offset + 384 < length)
                {
                    Sse.Prefetch0(src + offset + 256);
                    Sse.Prefetch0(src + offset + 320);
                }

                var vec0 = Avx512F.LoadVector512(src + offset);
                var vec1 = Avx512F.LoadVector512(src + offset + 64);
                Avx512F.Store(dst + offset, vec0);
                Avx512F.Store(dst + offset + 64, vec1);
            }

            // Handle remaining 64-byte chunk
            var avx512Length = length & -64;
            for (; offset < avx512Length; offset += 64)
            {
                var vec = Avx512F.LoadVector512(src + offset);
                Avx512F.Store(dst + offset, vec);
            }
        }
        else
        {
            // 64-byte only (smaller buffers)
            var avx512Length = length & -64;
            for (; offset < avx512Length; offset += 64)
            {
                if (offset + PrefetchDistance < length)
                {
                    Sse.Prefetch0(src + offset + PrefetchDistance);
                }

                var vec = Avx512F.LoadVector512(src + offset);
                Avx512F.Store(dst + offset, vec);
            }
        }

        return offset;
    }

    /// <summary>
    /// AVX2 path: 256-bit (32-byte) vectors.
    /// Processes 64 bytes at a time using two vector registers for better pipelining.
    /// This matches cache line size (64 bytes) for optimal memory throughput.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static unsafe int CopyAvx2(byte* src, byte* dst, int length, int offset, bool useNonTemporal)
    {
        if (useNonTemporal && Sse2.IsSupported)
        {
            // Non-temporal stores for large copies to avoid cache pollution
            var isAligned = ((nuint)(dst + offset) & 15) == 0;

            if (isAligned && length >= 64)
            {
                // 64-byte non-temporal with pipelining
                var avx2x2Length = length & -64;
                for (; offset < avx2x2Length; offset += 64)
                {
                    if (offset + PrefetchDistance < length)
                    {
                        Sse.Prefetch0(src + offset + PrefetchDistance);
                    }

                    var vec0 = Avx.LoadVector256(src + offset);
                    var vec1 = Avx.LoadVector256(src + offset + 32);
                    var lo0 = vec0.GetLower();
                    var hi0 = vec0.GetUpper();
                    var lo1 = vec1.GetLower();
                    var hi1 = vec1.GetUpper();
                    Sse2.StoreAlignedNonTemporal(dst + offset, lo0);
                    Sse2.StoreAlignedNonTemporal(dst + offset + 16, hi0);
                    Sse2.StoreAlignedNonTemporal(dst + offset + 32, lo1);
                    Sse2.StoreAlignedNonTemporal(dst + offset + 48, hi1);
                }

                // Handle remaining 32-byte chunk
                var avx2Length = length & -32;
                for (; offset < avx2Length; offset += 32)
                {
                    var vec = Avx.LoadVector256(src + offset);
                    var lo = vec.GetLower();
                    var hi = vec.GetUpper();
                    Sse2.StoreAlignedNonTemporal(dst + offset, lo);
                    Sse2.StoreAlignedNonTemporal(dst + offset + 16, hi);
                }
            }
            else if (isAligned)
            {
                // 32-byte non-temporal (smaller buffers)
                var avx2Length = length & -32;
                for (; offset < avx2Length; offset += 32)
                {
                    if (offset + PrefetchDistance < length)
                    {
                        Sse.Prefetch0(src + offset + PrefetchDistance);
                    }

                    var vec = Avx.LoadVector256(src + offset);
                    var lo = vec.GetLower();
                    var hi = vec.GetUpper();
                    Sse2.StoreAlignedNonTemporal(dst + offset, lo);
                    Sse2.StoreAlignedNonTemporal(dst + offset + 16, hi);
                }
            }
            else
            {
                // Unaligned destination - use regular stores with 64-byte pipelining
                offset = CopyAvx2Regular(src, dst, length, offset);
            }
        }
        else
        {
            // Regular stores with 64-byte pipelining
            offset = CopyAvx2Regular(src, dst, length, offset);
        }

        return offset;
    }

    /// <summary>
    /// AVX2 regular (temporal) store path. Used when NT stores are not requested
    /// or when the destination is not properly aligned.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static unsafe int CopyAvx2Regular(byte* src, byte* dst, int length, int offset)
    {
        // Note: AVX2 implies SSE support, so no need to check Sse.IsSupported
        if (length >= 64)
        {
            var avx2x2Length = length & -64;
            for (; offset < avx2x2Length; offset += 64)
            {
                if (offset + PrefetchDistance < length)
                {
                    Sse.Prefetch0(src + offset + PrefetchDistance);
                }

                var vec0 = Avx.LoadVector256(src + offset);
                var vec1 = Avx.LoadVector256(src + offset + 32);
                Avx.Store(dst + offset, vec0);
                Avx.Store(dst + offset + 32, vec1);
            }
        }

        // Handle remaining 32-byte chunk
        var avx2Length = length & -32;
        for (; offset < avx2Length; offset += 32)
        {
            if (offset + PrefetchDistance < length)
            {
                Sse.Prefetch0(src + offset + PrefetchDistance);
            }

            var vec = Avx.LoadVector256(src + offset);
            Avx.Store(dst + offset, vec);
        }

        return offset;
    }

    /// <summary>
    /// SSE2 path: 128-bit (16-byte) vectors.
    /// Processes 64 bytes at a time using four vector registers to match cache line size.
    /// This improves memory throughput by better utilizing the memory subsystem.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static unsafe int CopySse2(byte* src, byte* dst, int length, int offset, bool useNonTemporal)
    {
        if (useNonTemporal)
        {
            var isAligned = ((nuint)(dst + offset) & 15) == 0;

            if (isAligned && length >= 64)
            {
                // 64-byte non-temporal with 4x pipelining
                var sse2x4Length = length & -64;
                for (; offset < sse2x4Length; offset += 64)
                {
                    if (offset + PrefetchDistance < length)
                    {
                        Sse.Prefetch0(src + offset + PrefetchDistance);
                    }

                    var vec0 = Sse2.LoadVector128(src + offset);
                    var vec1 = Sse2.LoadVector128(src + offset + 16);
                    var vec2 = Sse2.LoadVector128(src + offset + 32);
                    var vec3 = Sse2.LoadVector128(src + offset + 48);
                    Sse2.StoreAlignedNonTemporal(dst + offset, vec0);
                    Sse2.StoreAlignedNonTemporal(dst + offset + 16, vec1);
                    Sse2.StoreAlignedNonTemporal(dst + offset + 32, vec2);
                    Sse2.StoreAlignedNonTemporal(dst + offset + 48, vec3);
                }

                // Handle remaining 16-byte chunks
                var sse2Length = length & -16;
                for (; offset < sse2Length; offset += 16)
                {
                    var vec = Sse2.LoadVector128(src + offset);
                    Sse2.StoreAlignedNonTemporal(dst + offset, vec);
                }
            }
            else if (isAligned)
            {
                // 16-byte non-temporal (smaller buffers)
                var sse2Length = length & -16;
                for (; offset < sse2Length; offset += 16)
                {
                    if (offset + PrefetchDistance < length)
                    {
                        Sse.Prefetch0(src + offset + PrefetchDistance);
                    }

                    var vec = Sse2.LoadVector128(src + offset);
                    Sse2.StoreAlignedNonTemporal(dst + offset, vec);
                }
            }
            else
            {
                // Unaligned - use regular stores with 64-byte pipelining
                offset = CopySse2Regular(src, dst, length, offset);
            }
        }
        else
        {
            // Regular stores with 64-byte pipelining
            offset = CopySse2Regular(src, dst, length, offset);
        }

        return offset;
    }

    /// <summary>
    /// SSE2 regular (temporal) store path. Used when NT stores are not requested
    /// or when the destination is not properly aligned.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static unsafe int CopySse2Regular(byte* src, byte* dst, int length, int offset)
    {
        if (length >= 64)
        {
            var sse2x4Length = length & -64;
            for (; offset < sse2x4Length; offset += 64)
            {
                if (offset + PrefetchDistance < length)
                {
                    Sse.Prefetch0(src + offset + PrefetchDistance);
                }

                var vec0 = Sse2.LoadVector128(src + offset);
                var vec1 = Sse2.LoadVector128(src + offset + 16);
                var vec2 = Sse2.LoadVector128(src + offset + 32);
                var vec3 = Sse2.LoadVector128(src + offset + 48);
                Sse2.Store(dst + offset, vec0);
                Sse2.Store(dst + offset + 16, vec1);
                Sse2.Store(dst + offset + 32, vec2);
                Sse2.Store(dst + offset + 48, vec3);
            }
        }

        // Handle remaining 16-byte chunks
        var sse2Length = length & -16;
        for (; offset < sse2Length; offset += 16)
        {
            if (offset + PrefetchDistance < length)
            {
                Sse.Prefetch0(src + offset + PrefetchDistance);
            }

            var vec = Sse2.LoadVector128(src + offset);
            Sse2.Store(dst + offset, vec);
        }

        return offset;
    }

    /// <summary>
    /// ARM NEON path: 128-bit vectors.
    /// ARM relies on hardware prefetching; PRFM is not exposed via AdvSimd intrinsics.
    /// Modern ARM cores (Apple Silicon, Cortex-A78+) have aggressive hardware prefetchers.
    /// No software prefetch needed unlike x86 SSE/AVX paths.
    /// ARM NEON uses regular stores (not non-temporal), so no explicit memory barrier
    /// is required for cache coherency.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static unsafe int CopyAdvSimd(byte* src, byte* dst, int length, int offset)
    {
        if (_advSimdArm64Supported && length >= 64)
        {
            // Process 64 bytes at a time using four vector registers.
            // This maximizes memory bandwidth utilization on ARM64 cores with wide
            // execution units (Apple M-series, Cortex-X series).
            // JIT should emit LDP/STP pairs for optimal throughput.
            var advSimd64Length = length & -64;
            for (; offset < advSimd64Length; offset += 64)
            {
                var vec0 = AdvSimd.LoadVector128(src + offset);
                var vec1 = AdvSimd.LoadVector128(src + offset + 16);
                var vec2 = AdvSimd.LoadVector128(src + offset + 32);
                var vec3 = AdvSimd.LoadVector128(src + offset + 48);
                AdvSimd.Store(dst + offset, vec0);
                AdvSimd.Store(dst + offset + 16, vec1);
                AdvSimd.Store(dst + offset + 32, vec2);
                AdvSimd.Store(dst + offset + 48, vec3);
            }
        }
        else if (_advSimdArm64Supported && length >= 32)
        {
            // Process 32 bytes at a time using two vector registers for pipelining
            var advSimd32Length = length & -32;
            for (; offset < advSimd32Length; offset += 32)
            {
                var vec0 = AdvSimd.LoadVector128(src + offset);
                var vec1 = AdvSimd.LoadVector128(src + offset + 16);
                AdvSimd.Store(dst + offset, vec0);
                AdvSimd.Store(dst + offset + 16, vec1);
            }
        }

        // Process remaining 16-byte chunks (handles both ARM32 NEON and ARM64 remainder)
        var advSimdLength = length & -16;
        for (; offset < advSimdLength; offset += 16)
        {
            var vec = AdvSimd.LoadVector128(src + offset);
            AdvSimd.Store(dst + offset, vec);
        }

        return offset;
    }

    /// <summary>
    /// Portable Vector path: uses <see cref="Vector{T}"/> which the JIT maps
    /// to the best available SIMD width on the current platform.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static unsafe int CopyVector(byte* src, byte* dst, int length, int offset)
    {
        var vectorLength = length & ~(Vector<byte>.Count - 1);
        for (; offset < vectorLength; offset += Vector<byte>.Count)
        {
            var vec = Unsafe.ReadUnaligned<Vector<byte>>(src + offset);
            Unsafe.WriteUnaligned(dst + offset, vec);
        }

        return offset;
    }

    /// <summary>
    /// Copies the remaining bytes (fewer than the smallest SIMD register width)
    /// using progressively smaller scalar widths: 8, 4, 2, then 1 byte.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void CopyRemainder(byte* src, byte* dst, int length, int offset)
    {
        var remaining = length - offset;

        // Use while for 8-byte chunks since remainder can exceed 15 bytes
        // (e.g., AVX2 processes 32-byte aligned chunks, leaving up to 31 bytes)
        while (remaining >= 8)
        {
            Unsafe.WriteUnaligned(dst + offset, Unsafe.ReadUnaligned<long>(src + offset));
            offset += 8;
            remaining -= 8;
        }

        if (remaining >= 4)
        {
            Unsafe.WriteUnaligned(dst + offset, Unsafe.ReadUnaligned<int>(src + offset));
            offset += 4;
            remaining -= 4;
        }

        if (remaining >= 2)
        {
            Unsafe.WriteUnaligned(dst + offset, Unsafe.ReadUnaligned<short>(src + offset));
            offset += 2;
            remaining -= 2;
        }

        if (remaining > 0)
        {
            dst[offset] = src[offset];
        }
    }
}
