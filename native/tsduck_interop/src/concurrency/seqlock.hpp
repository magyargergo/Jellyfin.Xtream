// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_CONCURRENCY_SEQLOCK_HPP
#define TSDUCK_INTEROP_CONCURRENCY_SEQLOCK_HPP

#include <atomic>
#include <cstdint>
#include "../core/constants.hpp"

namespace tsduck_interop::concurrency {

// ============================================================================
// Seqlock: Lock-Free Read of Compound Data Structures
// ============================================================================
//
// Writer protocol:
//   1. Call begin_write() - makes sequence odd
//   2. Write data
//   3. Call end_write(seq) - makes sequence even
//
// Reader protocol:
//   1. Call begin_read() - waits for even sequence
//   2. Read data
//   3. Call read_consistent(seq) - returns true if data is valid

struct alignas(CACHE_LINE_SIZE) Seqlock {
    std::atomic<uint64_t> sequence{0};

    // Begin write - returns sequence to use for end_write
    uint64_t begin_write() noexcept {
        uint64_t seq = sequence.load(std::memory_order_relaxed);
        sequence.store(seq + 1, std::memory_order_release);  // Make odd
        std::atomic_thread_fence(std::memory_order_release);
        return seq + 2;  // Expected final value
    }

    // End write
    void end_write(uint64_t expected_seq) noexcept {
        std::atomic_thread_fence(std::memory_order_release);
        sequence.store(expected_seq, std::memory_order_release);  // Make even
    }

    // Begin read - returns sequence for consistency check
    uint64_t begin_read() const noexcept {
        uint64_t seq;
        do {
            seq = sequence.load(std::memory_order_acquire);
        } while (seq & 1);  // Wait if writer is active (odd)
        std::atomic_thread_fence(std::memory_order_acquire);
        return seq;
    }

    // Check if read is consistent
    bool read_consistent(uint64_t start_seq) const noexcept {
        std::atomic_thread_fence(std::memory_order_acquire);
        return sequence.load(std::memory_order_acquire) == start_seq;
    }
};

}  // namespace tsduck_interop::concurrency

#endif  // TSDUCK_INTEROP_CONCURRENCY_SEQLOCK_HPP
