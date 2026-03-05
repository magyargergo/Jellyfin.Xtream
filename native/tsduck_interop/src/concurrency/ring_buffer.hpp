// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_CONCURRENCY_RING_BUFFER_HPP
#define TSDUCK_INTEROP_CONCURRENCY_RING_BUFFER_HPP

#include <algorithm>
#include <array>
#include <atomic>
#include <cstddef>
#include <utility>

#include "../core/constants.hpp"

namespace tsduck_interop::concurrency {

// ============================================================================
// Lock-Free Ring Buffer (SPSC - Single Producer Single Consumer)
// ============================================================================
//
// A fixed-capacity circular buffer optimized for single-producer,
// single-consumer scenarios. Uses atomic operations with appropriate
// memory ordering for lock-free access.
//
// Properties:
// - Capacity must be power of 2 (enables fast modulo via bitmask)
// - Cache-line aligned to prevent false sharing
// - Push always succeeds (overwrites oldest data when full)
// - Approximate min/max available for statistical analysis
//
// Thread safety:
// - Single writer calling push()
// - Single reader calling get_min_max(), at(), size()
// - Lock-free via atomic operations with acquire/release semantics

/// Lock-free ring buffer for statistical sample storage.
/// @tparam T Element type (must be trivially copyable)
/// @tparam Capacity Buffer capacity (must be power of 2)
template <typename T, std::size_t Capacity>
class alignas(CACHE_LINE_SIZE) LockFreeRingBuffer {
    static_assert(is_power_of_two(Capacity), "Capacity must be power of 2");
    static_assert(std::is_trivially_copyable_v<T>, "Element type must be trivially copyable");

    /// Bitmask for fast modulo (Capacity - 1 when Capacity is power of 2)
    static constexpr std::size_t INDEX_MASK = Capacity - 1;

    alignas(CACHE_LINE_SIZE) std::array<T, Capacity> buffer_{};
    alignas(CACHE_LINE_SIZE) std::atomic<std::size_t> write_idx_{0};
    alignas(CACHE_LINE_SIZE) std::atomic<std::size_t> size_{0};

public:
    /// Default constructor - initializes empty buffer.
    LockFreeRingBuffer() noexcept = default;

    // Non-copyable, non-movable (due to atomics)
    LockFreeRingBuffer(const LockFreeRingBuffer&) = delete;
    LockFreeRingBuffer& operator=(const LockFreeRingBuffer&) = delete;
    LockFreeRingBuffer(LockFreeRingBuffer&&) = delete;
    LockFreeRingBuffer& operator=(LockFreeRingBuffer&&) = delete;

    /// Push a value to the buffer.
    /// If the buffer is full, the oldest value is overwritten.
    /// @param value The value to push
    void push(T value) noexcept {
        std::size_t idx = write_idx_.load(std::memory_order_relaxed);
        buffer_[idx & INDEX_MASK] = value;
        write_idx_.store(idx + 1, std::memory_order_release);

        std::size_t current_size = size_.load(std::memory_order_relaxed);
        if (current_size < Capacity) {
            size_.store(current_size + 1, std::memory_order_release);
        }
    }

    /// Get the current number of elements in the buffer.
    /// @return Number of elements (0 to Capacity)
    [[nodiscard]] std::size_t size() const noexcept {
        return size_.load(std::memory_order_acquire);
    }

    /// Check if the buffer is empty.
    [[nodiscard]] bool empty() const noexcept {
        return size() == 0;
    }

    /// Check if the buffer is full.
    [[nodiscard]] bool full() const noexcept {
        return size() == Capacity;
    }

    /// Get min/max values from buffer (approximate - may be slightly stale).
    /// Useful for statistical analysis where exact consistency is not required.
    /// @return Pair of (min, max) values, or default-constructed pair if empty
    [[nodiscard]] std::pair<T, T> get_min_max() const noexcept {
        std::size_t sz = size_.load(std::memory_order_acquire);
        if (sz == 0) {
            return {T{}, T{}};
        }

        std::size_t w = write_idx_.load(std::memory_order_acquire);

        // Start with most recent value
        T min_val = buffer_[(w - 1) & INDEX_MASK];
        T max_val = min_val;

        std::size_t count = std::min(sz, Capacity);
        for (std::size_t i = 0; i < count; ++i) {
            T val = buffer_[(w - 1 - i) & INDEX_MASK];
            min_val = std::min(min_val, val);
            max_val = std::max(max_val, val);
        }

        return {min_val, max_val};
    }

    /// Clear all elements from the buffer.
    void clear() noexcept {
        write_idx_.store(0, std::memory_order_release);
        size_.store(0, std::memory_order_release);
    }

    /// Access element at index (0 = most recent).
    /// @param idx Index from most recent (0) to oldest (size()-1)
    /// @return Element at the specified index
    /// @note No bounds checking - caller must ensure idx < size()
    [[nodiscard]] T at(std::size_t idx) const noexcept {
        std::size_t w = write_idx_.load(std::memory_order_acquire);
        return buffer_[(w - 1 - idx) & INDEX_MASK];
    }

    /// Get the current write index.
    /// Useful for detecting updates between reads.
    [[nodiscard]] std::size_t write_index() const noexcept {
        return write_idx_.load(std::memory_order_acquire);
    }

    /// Get the buffer capacity.
    [[nodiscard]] static constexpr std::size_t capacity() noexcept {
        return Capacity;
    }

    /// Calculate average of all elements in the buffer.
    /// @return Average value, or T{} if buffer is empty
    [[nodiscard]] T average() const noexcept {
        std::size_t sz = size_.load(std::memory_order_acquire);
        if (sz == 0) {
            return T{};
        }

        std::size_t w = write_idx_.load(std::memory_order_acquire);
        T sum{};

        std::size_t count = std::min(sz, Capacity);
        for (std::size_t i = 0; i < count; ++i) {
            sum += buffer_[(w - 1 - i) & INDEX_MASK];
        }

        return sum / static_cast<T>(count);
    }
};

}  // namespace tsduck_interop::concurrency

#endif  // TSDUCK_INTEROP_CONCURRENCY_RING_BUFFER_HPP
