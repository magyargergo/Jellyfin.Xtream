// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_CONCURRENCY_SEQLOCK_HPP
#define TSDUCK_INTEROP_CONCURRENCY_SEQLOCK_HPP

#include <atomic>
#include <cstdint>
#include <concepts>
#include <type_traits>
#include "../core/constants.hpp"

namespace tsduck_interop::concurrency {

// ============================================================================
// Seqlock: Lock-Free Read of Compound Data Structures
// ============================================================================
//
// A seqlock provides optimistic, lock-free reads of compound data structures
// protected by a single writer. It is ideal for scenarios where reads are
// frequent and writes are infrequent.
//
// Writer protocol:
//   1. Call begin_write() - makes sequence odd (signals write in progress)
//   2. Write data
//   3. Call end_write(seq) - makes sequence even (signals write complete)
//
// Reader protocol:
//   1. Call begin_read() - waits for even sequence, returns it
//   2. Read data
//   3. Call read_consistent(seq) - returns true if data is valid
//   4. If false, retry from step 1
//
// Memory ordering guarantees:
//   - Writers use release semantics to ensure writes are visible
//   - Readers use acquire semantics to see writer's modifications
//   - Fences ensure proper ordering of compound reads/writes
//
// Thread safety:
//   - Single writer, multiple readers (SWMR)
//   - Readers never block; they retry on inconsistent reads
//   - Writer may cause readers to retry but never blocks them

/// RAII guard for seqlock write operations.
/// Ensures end_write is called even if an exception occurs during the write.
class SeqlockWriteGuard;

struct alignas(CACHE_LINE_SIZE) Seqlock {
    std::atomic<std::uint64_t> sequence{0};

    /// Begin a write operation.
    /// @return The expected final sequence value to pass to end_write()
    /// @note Must be paired with end_write() call
    [[nodiscard]] std::uint64_t begin_write() noexcept {
        std::uint64_t seq = sequence.load(std::memory_order_relaxed);
        sequence.store(seq + 1, std::memory_order_release);  // Make odd
        std::atomic_thread_fence(std::memory_order_release);
        return seq + 2;  // Expected final value
    }

    /// End a write operation.
    /// @param expected_seq The value returned by begin_write()
    void end_write(std::uint64_t expected_seq) noexcept {
        std::atomic_thread_fence(std::memory_order_release);
        sequence.store(expected_seq, std::memory_order_release);  // Make even
    }

    /// Begin a read operation.
    /// Spins until the sequence is even (no writer active).
    /// @return The sequence number for consistency check
    [[nodiscard]] std::uint64_t begin_read() const noexcept {
        std::uint64_t seq;
        do {
            seq = sequence.load(std::memory_order_acquire);
        } while (seq & 1);  // Wait if writer is active (odd)
        std::atomic_thread_fence(std::memory_order_acquire);
        return seq;
    }

    /// Check if a read operation saw consistent data.
    /// @param start_seq The sequence returned by begin_read()
    /// @return true if the read was consistent, false if it should be retried
    [[nodiscard]] bool read_consistent(std::uint64_t start_seq) const noexcept {
        std::atomic_thread_fence(std::memory_order_acquire);
        return sequence.load(std::memory_order_acquire) == start_seq;
    }

    /// Get the current sequence number.
    /// @return Current sequence (even = no write in progress, odd = write active)
    [[nodiscard]] std::uint64_t current_sequence() const noexcept {
        return sequence.load(std::memory_order_acquire);
    }

    /// Check if a write is currently in progress.
    [[nodiscard]] bool is_write_in_progress() const noexcept {
        return (sequence.load(std::memory_order_acquire) & 1) != 0;
    }

    /// Create an RAII write guard for exception-safe writes.
    [[nodiscard]] SeqlockWriteGuard write_guard() noexcept;
};

/// RAII guard for seqlock write operations.
/// Automatically calls end_write when destroyed, ensuring proper cleanup
/// even if an exception occurs during the write operation.
class [[nodiscard]] SeqlockWriteGuard {
public:
    explicit SeqlockWriteGuard(Seqlock& lock) noexcept
        : lock_(lock), expected_seq_(lock.begin_write()) {}

    ~SeqlockWriteGuard() noexcept {
        lock_.end_write(expected_seq_);
    }

    // Non-copyable, non-movable
    SeqlockWriteGuard(const SeqlockWriteGuard&) = delete;
    SeqlockWriteGuard& operator=(const SeqlockWriteGuard&) = delete;
    SeqlockWriteGuard(SeqlockWriteGuard&&) = delete;
    SeqlockWriteGuard& operator=(SeqlockWriteGuard&&) = delete;

private:
    Seqlock& lock_;
    std::uint64_t expected_seq_;
};

inline SeqlockWriteGuard Seqlock::write_guard() noexcept {
    return SeqlockWriteGuard{*this};
}

/// Helper concept for types that can be safely read with a seqlock.
/// Types must be trivially copyable to ensure atomic copy semantics.
template <typename T>
concept SeqlockReadable = std::is_trivially_copyable_v<T>;

/// Read a value protected by a seqlock with automatic retry on inconsistency.
/// @tparam T The type to read (must be trivially copyable)
/// @param lock The seqlock protecting the data
/// @param source Reference to the protected data
/// @return A consistent copy of the data
template <SeqlockReadable T>
[[nodiscard]] T seqlock_read(const Seqlock& lock, const T& source) noexcept {
    T result;
    std::uint64_t seq;
    do {
        seq = lock.begin_read();
        result = source;
    } while (!lock.read_consistent(seq));
    return result;
}

/// Write a value protected by a seqlock.
/// @tparam T The type to write (must be trivially copyable)
/// @param lock The seqlock protecting the data
/// @param dest Reference to the protected data
/// @param value The value to write
template <SeqlockReadable T>
void seqlock_write(Seqlock& lock, T& dest, const T& value) noexcept {
    auto guard = lock.write_guard();
    dest = value;
}

}  // namespace tsduck_interop::concurrency

#endif  // TSDUCK_INTEROP_CONCURRENCY_SEQLOCK_HPP
