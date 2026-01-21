// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_PLATFORM_SIMD_SEARCH_HPP
#define TSDUCK_INTEROP_PLATFORM_SIMD_SEARCH_HPP

#include <cstdint>
#include <algorithm>
#include "cpu_features.hpp"
#include "bit_ops.hpp"
#include "../core/constants.hpp"

namespace tsduck_interop {
namespace platform {

// ============================================================================
// SIMD-Accelerated Sync Byte Search
// ============================================================================

// Find first sync byte (0x47) with packet boundary verification
// Returns index of first valid sync byte, or -1 if not found
inline int findSyncByte(const uint8_t* data, int length) noexcept {
    int i = 0;

#if defined(TSDUCK_HAS_AVX2)
    if (CpuFeatures::instance().avx2 && length >= 32) {
        __m256i syncVec = _mm256_set1_epi8(static_cast<char>(TS_SYNC_BYTE));

        while (i <= length - 32) {
            __m256i chunk = _mm256_loadu_si256(
                reinterpret_cast<const __m256i*>(data + i));
            __m256i cmp = _mm256_cmpeq_epi8(chunk, syncVec);
            int mask = _mm256_movemask_epi8(cmp);

            if (mask != 0) {
                int bitPos = ctz32(static_cast<unsigned int>(mask));
                int candidate = i + bitPos;

                // Verify sync byte at next packet boundary
                if (candidate + TS_PACKET_SIZE >= length ||
                    data[candidate + TS_PACKET_SIZE] == TS_SYNC_BYTE) {
                    return candidate;
                }

                // False positive - continue after this position
                i = candidate + 1;
                continue;
            }

            i += 32;
        }
    }
#endif

#if defined(TSDUCK_HAS_SSE2)
    if (CpuFeatures::instance().sse2 && length >= 16) {
        __m128i syncVec = _mm_set1_epi8(static_cast<char>(TS_SYNC_BYTE));

        while (i <= length - 16) {
            __m128i chunk = _mm_loadu_si128(
                reinterpret_cast<const __m128i*>(data + i));
            __m128i cmp = _mm_cmpeq_epi8(chunk, syncVec);
            int mask = _mm_movemask_epi8(cmp);

            if (mask != 0) {
                int bitPos = ctz32(static_cast<unsigned int>(mask));
                int candidate = i + bitPos;

                if (candidate + TS_PACKET_SIZE >= length ||
                    data[candidate + TS_PACKET_SIZE] == TS_SYNC_BYTE) {
                    return candidate;
                }

                i = candidate + 1;
                continue;
            }

            i += 16;
        }
    }
#endif

#if defined(TSDUCK_HAS_NEON)
    if (CpuFeatures::instance().neon && length >= 16) {
        uint8x16_t syncVec = vdupq_n_u8(TS_SYNC_BYTE);

        while (i <= length - 16) {
            uint8x16_t chunk = vld1q_u8(data + i);
            uint8x16_t cmp = vceqq_u8(chunk, syncVec);

            // Check if any matches
            uint64x2_t cmp64 = vreinterpretq_u64_u8(cmp);
            if (vgetq_lane_u64(cmp64, 0) != 0 || vgetq_lane_u64(cmp64, 1) != 0) {
                // Find first match using scalar for simplicity
                for (int j = 0; j < 16 && i + j < length; j++) {
                    if (data[i + j] == TS_SYNC_BYTE) {
                        int candidate = i + j;
                        if (candidate + TS_PACKET_SIZE >= length ||
                            data[candidate + TS_PACKET_SIZE] == TS_SYNC_BYTE) {
                            return candidate;
                        }
                    }
                }
            }

            i += 16;
        }
    }
#endif

    // Scalar fallback
    for (; i < length; i++) {
        if (data[i] == TS_SYNC_BYTE) {
            if (i + TS_PACKET_SIZE >= length ||
                data[i + TS_PACKET_SIZE] == TS_SYNC_BYTE) {
                return i;
            }
        }
    }

    return -1;
}

// ============================================================================
// SIMD-Accelerated Sync Byte Validation
// ============================================================================

// Validate all sync bytes in a packet-aligned buffer
// Returns count of valid sync bytes (packets with 0x47 at expected positions)
inline int validateSyncBytes(const uint8_t* data, int length) noexcept {
    int packets = length / TS_PACKET_SIZE;
    int validCount = 0;

#if defined(TSDUCK_HAS_AVX2)
    if (CpuFeatures::instance().avx2 && packets >= 32) {
        // Gather sync bytes from 32 packets at a time
        alignas(32) uint8_t syncBytes[32];

        for (int batch = 0; batch <= packets - 32; batch += 32) {
            // Gather sync bytes
            for (int j = 0; j < 32; j++) {
                syncBytes[j] = data[(batch + j) * TS_PACKET_SIZE];
            }

            // Compare all 32 at once
            __m256i gathered = _mm256_load_si256(
                reinterpret_cast<const __m256i*>(syncBytes));
            __m256i syncVec = _mm256_set1_epi8(static_cast<char>(TS_SYNC_BYTE));
            __m256i cmp = _mm256_cmpeq_epi8(gathered, syncVec);
            int mask = _mm256_movemask_epi8(cmp);

            validCount += popcount32(static_cast<unsigned int>(mask));
        }

        // Handle remaining packets
        for (int i = (packets / 32) * 32; i < packets; i++) {
            if (data[i * TS_PACKET_SIZE] == TS_SYNC_BYTE) {
                validCount++;
            }
        }

        return validCount;
    }
#endif

    // Scalar fallback
    for (int i = 0; i < packets; i++) {
        if (data[i * TS_PACKET_SIZE] == TS_SYNC_BYTE) {
            validCount++;
        }
    }

    return validCount;
}

}  // namespace platform
}  // namespace tsduck_interop

#endif  // TSDUCK_INTEROP_PLATFORM_SIMD_SEARCH_HPP
