// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_PLATFORM_BIT_OPS_HPP
#define TSDUCK_INTEROP_PLATFORM_BIT_OPS_HPP

#include <cstdint>
#include <cassert>

#if defined(_MSC_VER)
#include <intrin.h>
#endif

namespace tsduck_interop::platform {

// ============================================================================
// Bit Operations - Count Leading/Trailing Zeros, Population Count
// ============================================================================
//
// PRECONDITION: All ctz/clz functions require non-zero input.
// Calling with x == 0 is undefined behavior. Callers must check for zero.
// ============================================================================

/// Count trailing zeros in a 32-bit integer.
/// @pre x != 0 (undefined behavior if x == 0)
[[nodiscard]] inline int ctz32(unsigned int x) noexcept {
    assert(x != 0 && "ctz32: undefined behavior for zero input");
#if defined(_MSC_VER)
    unsigned long idx;
    _BitScanForward(&idx, x);
    return static_cast<int>(idx);
#else
    return __builtin_ctz(x);
#endif
}

/// Count leading zeros in a 32-bit integer.
/// @pre x != 0 (undefined behavior if x == 0)
[[nodiscard]] inline int clz32(unsigned int x) noexcept {
    assert(x != 0 && "clz32: undefined behavior for zero input");
#if defined(_MSC_VER)
    unsigned long idx;
    _BitScanReverse(&idx, x);
    return static_cast<int>(31 - idx);
#else
    return __builtin_clz(x);
#endif
}

/// Count trailing zeros in a 64-bit integer.
/// @pre x != 0 (undefined behavior if x == 0)
[[nodiscard]] inline int ctz64(uint64_t x) noexcept {
    assert(x != 0 && "ctz64: undefined behavior for zero input");
#if defined(_MSC_VER)
    unsigned long idx;
    _BitScanForward64(&idx, x);
    return static_cast<int>(idx);
#else
    return __builtin_ctzll(x);
#endif
}

/// Count leading zeros in a 64-bit integer.
/// @pre x != 0 (undefined behavior if x == 0)
[[nodiscard]] inline int clz64(uint64_t x) noexcept {
    assert(x != 0 && "clz64: undefined behavior for zero input");
#if defined(_MSC_VER)
    unsigned long idx;
    _BitScanReverse64(&idx, x);
    return static_cast<int>(63 - idx);
#else
    return __builtin_clzll(x);
#endif
}

/// Population count (count set bits) in a 32-bit integer.
/// Note: Safe to call with any value including zero.
[[nodiscard]] inline int popcount32(unsigned int x) noexcept {
#if defined(_MSC_VER)
    return static_cast<int>(__popcnt(x));
#else
    return __builtin_popcount(x);
#endif
}

}  // namespace tsduck_interop::platform

#endif  // TSDUCK_INTEROP_PLATFORM_BIT_OPS_HPP
