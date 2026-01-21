// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_PLATFORM_CPU_FEATURES_HPP
#define TSDUCK_INTEROP_PLATFORM_CPU_FEATURES_HPP

// ============================================================================
// Platform Detection Macros
// ============================================================================

#if defined(__x86_64__) || defined(_M_X64) || defined(__i386__) || defined(_M_IX86)
    #define TSDUCK_ARCH_X86 1
#elif defined(__aarch64__) || defined(_M_ARM64)
    #define TSDUCK_ARCH_ARM64 1
#elif defined(__arm__) && defined(__ARM_NEON)
    #define TSDUCK_ARCH_ARM32 1
#endif

#if defined(TSDUCK_ARCH_ARM64) || defined(TSDUCK_ARCH_ARM32)
    #define TSDUCK_ARCH_ARM 1
#endif

// ============================================================================
// SIMD Capability Macros (compile-time)
// ============================================================================

#if defined(TSDUCK_ARCH_X86)
    #if defined(__AVX2__)
        #define TSDUCK_HAS_AVX2 1
    #endif
    #if defined(__SSE4_2__) || defined(__SSE4_1__) || defined(__SSE2__) || defined(_M_X64)
        #define TSDUCK_HAS_SSE2 1
    #endif
#endif

#if defined(TSDUCK_ARCH_ARM)
    #define TSDUCK_HAS_NEON 1
#endif

// ============================================================================
// Include SIMD Headers
// ============================================================================

#if defined(TSDUCK_ARCH_X86)
    #include <immintrin.h>
    #if defined(__GNUC__) || defined(__clang__)
        #include <cpuid.h>
    #elif defined(_MSC_VER)
        #include <intrin.h>
    #endif
#endif

#if defined(TSDUCK_ARCH_ARM)
    #include <arm_neon.h>
#endif

namespace tsduck_interop {
namespace platform {

// ============================================================================
// Runtime CPU Feature Detection
// ============================================================================

struct CpuFeatures {
    bool avx2 = false;
    bool sse2 = false;
    bool neon = false;

    static CpuFeatures detect() noexcept {
        CpuFeatures f;

#if defined(TSDUCK_ARCH_X86)
    #if defined(_MSC_VER)
        int cpuInfo[4];
        __cpuid(cpuInfo, 0);
        int nIds = cpuInfo[0];
        if (nIds >= 1) {
            __cpuid(cpuInfo, 1);
            f.sse2 = (cpuInfo[3] & (1 << 26)) != 0;
        }
        if (nIds >= 7) {
            __cpuidex(cpuInfo, 7, 0);
            f.avx2 = (cpuInfo[1] & (1 << 5)) != 0;
        }
    #elif defined(__GNUC__) || defined(__clang__)
        unsigned int eax, ebx, ecx, edx;
        if (__get_cpuid(1, &eax, &ebx, &ecx, &edx)) {
            f.sse2 = (edx & (1 << 26)) != 0;
        }
        if (__get_cpuid_count(7, 0, &eax, &ebx, &ecx, &edx)) {
            f.avx2 = (ebx & (1 << 5)) != 0;
        }
    #endif
#elif defined(TSDUCK_ARCH_ARM)
        f.neon = true;
#endif

        return f;
    }

    static const CpuFeatures& instance() noexcept {
        static CpuFeatures features = detect();
        return features;
    }
};

}  // namespace platform
}  // namespace tsduck_interop

#endif  // TSDUCK_INTEROP_PLATFORM_CPU_FEATURES_HPP
