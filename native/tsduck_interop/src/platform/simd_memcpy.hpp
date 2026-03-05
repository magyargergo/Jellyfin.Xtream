// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_PLATFORM_SIMD_MEMCPY_HPP
#define TSDUCK_INTEROP_PLATFORM_SIMD_MEMCPY_HPP

#include <cassert>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include "cpu_features.hpp"

namespace tsduck_interop::platform {

// ============================================================================
// Configuration Constants
// ============================================================================

/// Threshold below which std::memcpy is more efficient than SIMD
inline constexpr std::size_t kSimdThreshold = 512;

/// Threshold above which non-temporal stores are beneficial to avoid cache pollution
inline constexpr std::size_t kNonTemporalThreshold = 256 * 1024;

/// Threshold above which prefetching helps even for NT stores (hides memory latency)
/// Using _MM_HINT_NTA minimizes cache pollution while still benefiting from prefetch
inline constexpr std::size_t kNtPrefetchThreshold = 1024 * 1024;  // 1MB

/// Prefetch distance in bytes for temporal copies (tuned for typical memory latency)
inline constexpr std::size_t kPrefetchDistance = 512;

/// Prefetch distance for NT stores - 2 cache lines ahead for pipelined access
/// Matches glibc approach: prefetch N+2, load N (cached from N-2), NT store N
inline constexpr std::size_t kNtPrefetchDistance = 128;

// ============================================================================
// SIMD Memory Copy - Core Implementation
// ============================================================================

/// @brief High-performance memory copy using SIMD instructions.
///
/// Automatically selects optimal implementation based on:
/// - CPU capabilities (AVX2, SSE2, NEON)
/// - Buffer size (small uses memcpy, large uses non-temporal stores)
/// - Alignment (aligned destinations enable streaming stores)
///
/// @param dest Destination buffer (should be cache-line aligned for best perf)
/// @param src Source buffer
/// @param size Number of bytes to copy
/// @note For sizes < 512 bytes, falls back to std::memcpy
/// @note For sizes >= 256KB, uses non-temporal stores to avoid cache pollution
[[gnu::hot]] [[gnu::flatten]]
inline void simd_memcpy(void* __restrict dest, const void* __restrict src, std::size_t size) noexcept {
    // Zero-size check: nothing to do
    if (size == 0) {
        return;
    }

#ifndef NDEBUG
    assert(dest != nullptr && "simd_memcpy: null destination");
    assert(src != nullptr && "simd_memcpy: null source");
#endif

    // Small copies: std::memcpy is already highly optimized
    if (size < kSimdThreshold) {
        std::memcpy(dest, src, size);
        return;
    }

    auto* d = static_cast<std::uint8_t*>(dest);
    const auto* s = static_cast<const std::uint8_t*>(src);
    std::size_t offset = 0;

    const auto& cpu = CpuFeatures::instance();
    const bool use_nontemporal = size >= kNonTemporalThreshold;

#if defined(TSDUCK_HAS_AVX2)
    if (cpu.avx2) {
        // Check 32-byte alignment for optimal AVX2 performance
        const bool is_aligned = (reinterpret_cast<std::uintptr_t>(d) & 31) == 0;

        if (use_nontemporal && is_aligned && size >= 128) {
            // Non-temporal stores: 128 bytes (4x YMM) per iteration
            // Bypasses cache for large transfers, reducing cache pollution
            const std::size_t avx2_nt_len = size & ~static_cast<std::size_t>(127);

            // For very large transfers (>1MB), use NTA prefetch to hide memory latency
            // while minimizing cache pollution. This creates a pipeline:
            // prefetch N+2 (NTA), load N (from L1), NT store N
            const bool use_prefetch = size >= kNtPrefetchThreshold;

            for (; offset < avx2_nt_len; offset += 128) {
                // Prefetch with NTA hint for very large transfers
                // NTA = Non-Temporal Access: data loaded into L1 with minimal pollution
                if (use_prefetch && offset + kNtPrefetchDistance < size) {
                    _mm_prefetch(reinterpret_cast<const char*>(s + offset + kNtPrefetchDistance), _MM_HINT_NTA);
                }

                // Load 4x 32-byte vectors (pipelined)
                __m256i v0 = _mm256_loadu_si256(reinterpret_cast<const __m256i*>(s + offset));
                __m256i v1 = _mm256_loadu_si256(reinterpret_cast<const __m256i*>(s + offset + 32));
                __m256i v2 = _mm256_loadu_si256(reinterpret_cast<const __m256i*>(s + offset + 64));
                __m256i v3 = _mm256_loadu_si256(reinterpret_cast<const __m256i*>(s + offset + 96));

                // Non-temporal stores (streaming, bypasses cache)
                _mm256_stream_si256(reinterpret_cast<__m256i*>(d + offset), v0);
                _mm256_stream_si256(reinterpret_cast<__m256i*>(d + offset + 32), v1);
                _mm256_stream_si256(reinterpret_cast<__m256i*>(d + offset + 64), v2);
                _mm256_stream_si256(reinterpret_cast<__m256i*>(d + offset + 96), v3);
            }

            // Memory fence required after non-temporal stores
            _mm_sfence();
        } else if (size >= 128) {
            // Temporal stores: 128 bytes (4x YMM) per iteration
            // Cache-friendly for medium-sized copies
            const std::size_t avx2_len = size & ~static_cast<std::size_t>(127);

            for (; offset < avx2_len; offset += 128) {
                // Prefetch into L1 cache
                if (offset + kPrefetchDistance < size) {
                    _mm_prefetch(reinterpret_cast<const char*>(s + offset + kPrefetchDistance), _MM_HINT_T0);
                }

                // Load 4x 32-byte vectors (pipelined for ILP)
                __m256i v0 = _mm256_loadu_si256(reinterpret_cast<const __m256i*>(s + offset));
                __m256i v1 = _mm256_loadu_si256(reinterpret_cast<const __m256i*>(s + offset + 32));
                __m256i v2 = _mm256_loadu_si256(reinterpret_cast<const __m256i*>(s + offset + 64));
                __m256i v3 = _mm256_loadu_si256(reinterpret_cast<const __m256i*>(s + offset + 96));

                // Temporal stores (normal writeback)
                _mm256_storeu_si256(reinterpret_cast<__m256i*>(d + offset), v0);
                _mm256_storeu_si256(reinterpret_cast<__m256i*>(d + offset + 32), v1);
                _mm256_storeu_si256(reinterpret_cast<__m256i*>(d + offset + 64), v2);
                _mm256_storeu_si256(reinterpret_cast<__m256i*>(d + offset + 96), v3);
            }
        }

        // Handle 32-byte remainder chunks
        const std::size_t avx2_remainder = size & ~static_cast<std::size_t>(31);
        for (; offset < avx2_remainder; offset += 32) {
            __m256i v = _mm256_loadu_si256(reinterpret_cast<const __m256i*>(s + offset));
            _mm256_storeu_si256(reinterpret_cast<__m256i*>(d + offset), v);
        }
    }
#endif

#if defined(TSDUCK_HAS_SSE2)
    if (cpu.sse2 && offset < size) {
        // SSE2 fallback: 64 bytes (4x XMM) per iteration
        const std::size_t sse2_len = size & ~static_cast<std::size_t>(63);

        // Check if NT stores should be used (large remaining size, aligned destination)
        const bool is_aligned = (reinterpret_cast<std::uintptr_t>(d + offset) & 15) == 0;
        const bool use_sse2_nt = (size - offset) >= kNonTemporalThreshold && is_aligned;

        if (use_sse2_nt) {
            // Non-temporal SSE2 path for large transfers
            const bool use_prefetch = (size - offset) >= kNtPrefetchThreshold;

            for (; offset < sse2_len; offset += 64) {
                if (use_prefetch && offset + kNtPrefetchDistance < size) {
                    _mm_prefetch(reinterpret_cast<const char*>(s + offset + kNtPrefetchDistance), _MM_HINT_NTA);
                }

                __m128i v0 = _mm_loadu_si128(reinterpret_cast<const __m128i*>(s + offset));
                __m128i v1 = _mm_loadu_si128(reinterpret_cast<const __m128i*>(s + offset + 16));
                __m128i v2 = _mm_loadu_si128(reinterpret_cast<const __m128i*>(s + offset + 32));
                __m128i v3 = _mm_loadu_si128(reinterpret_cast<const __m128i*>(s + offset + 48));

                _mm_stream_si128(reinterpret_cast<__m128i*>(d + offset), v0);
                _mm_stream_si128(reinterpret_cast<__m128i*>(d + offset + 16), v1);
                _mm_stream_si128(reinterpret_cast<__m128i*>(d + offset + 32), v2);
                _mm_stream_si128(reinterpret_cast<__m128i*>(d + offset + 48), v3);
            }
            _mm_sfence();
        } else {
            // Temporal SSE2 path for smaller transfers
            for (; offset < sse2_len; offset += 64) {
                if (offset + kPrefetchDistance < size) {
                    _mm_prefetch(reinterpret_cast<const char*>(s + offset + kPrefetchDistance), _MM_HINT_T0);
                }

                __m128i v0 = _mm_loadu_si128(reinterpret_cast<const __m128i*>(s + offset));
                __m128i v1 = _mm_loadu_si128(reinterpret_cast<const __m128i*>(s + offset + 16));
                __m128i v2 = _mm_loadu_si128(reinterpret_cast<const __m128i*>(s + offset + 32));
                __m128i v3 = _mm_loadu_si128(reinterpret_cast<const __m128i*>(s + offset + 48));

                _mm_storeu_si128(reinterpret_cast<__m128i*>(d + offset), v0);
                _mm_storeu_si128(reinterpret_cast<__m128i*>(d + offset + 16), v1);
                _mm_storeu_si128(reinterpret_cast<__m128i*>(d + offset + 32), v2);
                _mm_storeu_si128(reinterpret_cast<__m128i*>(d + offset + 48), v3);
            }
        }

        // Handle 16-byte remainder chunks
        const std::size_t sse2_remainder = size & ~static_cast<std::size_t>(15);
        for (; offset < sse2_remainder; offset += 16) {
            __m128i v = _mm_loadu_si128(reinterpret_cast<const __m128i*>(s + offset));
            _mm_storeu_si128(reinterpret_cast<__m128i*>(d + offset), v);
        }
    }
#endif

#if defined(TSDUCK_HAS_NEON)
    if (cpu.neon && offset < size) {
        // ARM NEON: 64 bytes (4x 128-bit) per iteration to match cache line
        // Note: ARM relies on hardware prefetching; PRFM not exposed in intrinsics
        const std::size_t neon_len = size & ~static_cast<std::size_t>(63);

        for (; offset < neon_len; offset += 64) {
            // Load 4x 16-byte vectors (LDP pairs on ARM64)
            uint8x16_t v0 = vld1q_u8(s + offset);
            uint8x16_t v1 = vld1q_u8(s + offset + 16);
            uint8x16_t v2 = vld1q_u8(s + offset + 32);
            uint8x16_t v3 = vld1q_u8(s + offset + 48);

            // Store 4x 16-byte vectors (STP pairs on ARM64)
            vst1q_u8(d + offset, v0);
            vst1q_u8(d + offset + 16, v1);
            vst1q_u8(d + offset + 32, v2);
            vst1q_u8(d + offset + 48, v3);
        }

        // Handle 16-byte remainder chunks
        const std::size_t neon_remainder = size & ~static_cast<std::size_t>(15);
        for (; offset < neon_remainder; offset += 16) {
            uint8x16_t v = vld1q_u8(s + offset);
            vst1q_u8(d + offset, v);
        }
    }
#endif

    // Scalar fallback for remaining bytes
    if (offset < size) {
        std::memcpy(d + offset, s + offset, size - offset);
    }
}

// ============================================================================
// SIMD Memory Set - Fill with Pattern
// ============================================================================

/// @brief High-performance memory fill using SIMD instructions.
///
/// @param dest Destination buffer
/// @param value Byte value to fill with
/// @param size Number of bytes to fill
[[gnu::hot]]
inline void simd_memset(void* dest, int value, std::size_t size) noexcept {
    // Small fills: std::memset is optimized
    if (size < kSimdThreshold) {
        std::memset(dest, value, size);
        return;
    }

    auto* d = static_cast<std::uint8_t*>(dest);
    std::size_t offset = 0;
    const std::uint8_t byte_val = static_cast<std::uint8_t>(value);

    const auto& cpu = CpuFeatures::instance();

#if defined(TSDUCK_HAS_AVX2)
    if (cpu.avx2 && size >= 128) {
        // Broadcast byte to 256-bit vector
        __m256i fill_vec = _mm256_set1_epi8(static_cast<char>(byte_val));
        const std::size_t avx2_len = size & ~static_cast<std::size_t>(127);

        // Check alignment and size for non-temporal stores
        // memset is write-only - NT stores avoid cache pollution for large fills
        const bool is_aligned = (reinterpret_cast<std::uintptr_t>(d) & 31) == 0;
        const bool use_nontemporal = size >= kNonTemporalThreshold && is_aligned;

        if (use_nontemporal) {
            // Non-temporal stores for large fills - bypasses cache
            for (; offset < avx2_len; offset += 128) {
                _mm256_stream_si256(reinterpret_cast<__m256i*>(d + offset), fill_vec);
                _mm256_stream_si256(reinterpret_cast<__m256i*>(d + offset + 32), fill_vec);
                _mm256_stream_si256(reinterpret_cast<__m256i*>(d + offset + 64), fill_vec);
                _mm256_stream_si256(reinterpret_cast<__m256i*>(d + offset + 96), fill_vec);
            }
            _mm_sfence();  // Required after NT stores
        } else {
            // Temporal stores for smaller fills - cache-friendly
            for (; offset < avx2_len; offset += 128) {
                _mm256_storeu_si256(reinterpret_cast<__m256i*>(d + offset), fill_vec);
                _mm256_storeu_si256(reinterpret_cast<__m256i*>(d + offset + 32), fill_vec);
                _mm256_storeu_si256(reinterpret_cast<__m256i*>(d + offset + 64), fill_vec);
                _mm256_storeu_si256(reinterpret_cast<__m256i*>(d + offset + 96), fill_vec);
            }
        }

        // Handle 32-byte remainder
        const std::size_t avx2_remainder = size & ~static_cast<std::size_t>(31);
        for (; offset < avx2_remainder; offset += 32) {
            _mm256_storeu_si256(reinterpret_cast<__m256i*>(d + offset), fill_vec);
        }
    }
#endif

#if defined(TSDUCK_HAS_SSE2)
    if (cpu.sse2 && offset < size) {
        __m128i fill_vec = _mm_set1_epi8(static_cast<char>(byte_val));
        const std::size_t sse2_len = size & ~static_cast<std::size_t>(63);

        // Check alignment for NT stores (SSE2 fallback)
        const bool is_aligned = (reinterpret_cast<std::uintptr_t>(d + offset) & 15) == 0;
        const bool use_nontemporal = (size - offset) >= kNonTemporalThreshold && is_aligned;

        if (use_nontemporal) {
            for (; offset < sse2_len; offset += 64) {
                _mm_stream_si128(reinterpret_cast<__m128i*>(d + offset), fill_vec);
                _mm_stream_si128(reinterpret_cast<__m128i*>(d + offset + 16), fill_vec);
                _mm_stream_si128(reinterpret_cast<__m128i*>(d + offset + 32), fill_vec);
                _mm_stream_si128(reinterpret_cast<__m128i*>(d + offset + 48), fill_vec);
            }
            _mm_sfence();
        } else {
            for (; offset < sse2_len; offset += 64) {
                _mm_storeu_si128(reinterpret_cast<__m128i*>(d + offset), fill_vec);
                _mm_storeu_si128(reinterpret_cast<__m128i*>(d + offset + 16), fill_vec);
                _mm_storeu_si128(reinterpret_cast<__m128i*>(d + offset + 32), fill_vec);
                _mm_storeu_si128(reinterpret_cast<__m128i*>(d + offset + 48), fill_vec);
            }
        }

        const std::size_t sse2_remainder = size & ~static_cast<std::size_t>(15);
        for (; offset < sse2_remainder; offset += 16) {
            _mm_storeu_si128(reinterpret_cast<__m128i*>(d + offset), fill_vec);
        }
    }
#endif

#if defined(TSDUCK_HAS_NEON)
    if (cpu.neon && offset < size) {
        uint8x16_t fill_vec = vdupq_n_u8(byte_val);
        const std::size_t neon_len = size & ~static_cast<std::size_t>(63);

        for (; offset < neon_len; offset += 64) {
            vst1q_u8(d + offset, fill_vec);
            vst1q_u8(d + offset + 16, fill_vec);
            vst1q_u8(d + offset + 32, fill_vec);
            vst1q_u8(d + offset + 48, fill_vec);
        }

        const std::size_t neon_remainder = size & ~static_cast<std::size_t>(15);
        for (; offset < neon_remainder; offset += 16) {
            vst1q_u8(d + offset, fill_vec);
        }
    }
#endif

    // Scalar fallback for remaining bytes
    if (offset < size) {
        std::memset(d + offset, byte_val, size - offset);
    }
}

}  // namespace tsduck_interop::platform

#endif  // TSDUCK_INTEROP_PLATFORM_SIMD_MEMCPY_HPP
