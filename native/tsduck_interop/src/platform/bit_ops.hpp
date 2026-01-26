// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_PLATFORM_BIT_OPS_HPP
#define TSDUCK_INTEROP_PLATFORM_BIT_OPS_HPP

#include <cstdint>

#if defined(_MSC_VER)
#include <intrin.h>
#endif

namespace tsduck_interop::platform {

// Count trailing zeros in a 32-bit integer
inline int ctz32(unsigned int x) noexcept {
#if defined(_MSC_VER)
    unsigned long idx;
    _BitScanForward(&idx, x);
    return static_cast<int>(idx);
#else
    return __builtin_ctz(x);
#endif
}

// Population count (count set bits) in a 32-bit integer
inline int popcount32(unsigned int x) noexcept {
#if defined(_MSC_VER)
    return static_cast<int>(__popcnt(x));
#else
    return __builtin_popcount(x);
#endif
}

}  // namespace tsduck_interop::platform

#endif  // TSDUCK_INTEROP_PLATFORM_BIT_OPS_HPP
