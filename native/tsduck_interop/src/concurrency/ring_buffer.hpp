// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_CONCURRENCY_RING_BUFFER_HPP
#define TSDUCK_INTEROP_CONCURRENCY_RING_BUFFER_HPP

#include <atomic>
#include <array>
#include <algorithm>
#include <utility>
#include <cstddef>
#include "../core/constants.hpp"

namespace tsduck_interop::concurrency {

// ============================================================================
// Lock-Free Ring Buffer (SPSC - Single Producer Single Consumer)
// ============================================================================

template <typename T, size_t Capacity>
class alignas(CACHE_LINE_SIZE) LockFreeRingBuffer {
    static_assert((Capacity & (Capacity - 1)) == 0, "Capacity must be power of 2");

    alignas(CACHE_LINE_SIZE) std::array<T, Capacity> buffer_{};
    alignas(CACHE_LINE_SIZE) std::atomic<size_t> write_idx_{0};
    alignas(CACHE_LINE_SIZE) std::atomic<size_t> size_{0};

public:
    void push(T value) noexcept {
        size_t idx = write_idx_.load(std::memory_order_relaxed);
        buffer_[idx & (Capacity - 1)] = value;
        write_idx_.store(idx + 1, std::memory_order_release);

        size_t current_size = size_.load(std::memory_order_relaxed);
        if (current_size < Capacity) {
            size_.store(current_size + 1, std::memory_order_release);
        }
    }

    size_t size() const noexcept { return size_.load(std::memory_order_acquire); }

    // Get min/max from buffer (approximate - may be slightly stale)
    std::pair<T, T> get_min_max() const noexcept {
        size_t sz = size_.load(std::memory_order_acquire);
        if (sz == 0)
            return {T{}, T{}};

        size_t w = write_idx_.load(std::memory_order_acquire);
        T min_val = buffer_[(w - 1) & (Capacity - 1)];
        T max_val = min_val;

        size_t count = std::min(sz, Capacity);
        for (size_t i = 0; i < count; ++i) {
            T val = buffer_[(w - 1 - i) & (Capacity - 1)];
            min_val = std::min(min_val, val);
            max_val = std::max(max_val, val);
        }
        return {min_val, max_val};
    }

    void clear() noexcept {
        write_idx_.store(0, std::memory_order_release);
        size_.store(0, std::memory_order_release);
    }

    // Access element at index (for iteration)
    T at(size_t idx) const noexcept {
        size_t w = write_idx_.load(std::memory_order_acquire);
        return buffer_[(w - 1 - idx) & (Capacity - 1)];
    }

    size_t write_index() const noexcept { return write_idx_.load(std::memory_order_acquire); }

    static constexpr size_t capacity() noexcept { return Capacity; }
};

}  // namespace tsduck_interop::concurrency

#endif  // TSDUCK_INTEROP_CONCURRENCY_RING_BUFFER_HPP
