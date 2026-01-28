// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_ANALYSIS_PID_TRACKER_HPP
#define TSDUCK_INTEROP_ANALYSIS_PID_TRACKER_HPP

#include <algorithm>
#include <array>
#include <atomic>
#include <cstdint>
#include <span>

#include "../concurrency/seqlock.hpp"
#include "../core/constants.hpp"
#include "tsduck_interop.h"

namespace tsduck_interop::analysis {

/// Per-PID tracking slot with seqlock for lock-free access.
///
/// Each slot tracks statistics for a single PID including packet count,
/// continuity counter errors, and stream type information.
struct alignas(CACHE_LINE_SIZE) PidSlot {
    concurrency::Seqlock seqlock;

    // Atomic counters for thread-safe access
    std::atomic<std::int64_t> packets{0};
    std::atomic<std::int64_t> continuity_errors{0};
    std::atomic<std::int64_t> duplicate_packets{0};
    std::atomic<std::int64_t> scrambled_packets{0};
    std::atomic<std::int32_t> last_cc{-1};
    std::atomic<bool> is_active{false};
    std::atomic<bool> is_scrambled{false};
    std::atomic<bool> is_pcr_pid{false};
    std::atomic<bool> is_video{false};
    std::atomic<bool> is_audio{false};
    std::atomic<std::int32_t> stream_type{0};
    std::atomic<std::int64_t> first_seen_ns{0};
    std::atomic<std::int64_t> last_seen_ns{0};
    double pcr_jitter_us{0.0};  // Updated atomically via seqlock

    /// Reset all tracking state for this PID slot.
    void reset() noexcept {
        auto guard = seqlock.write_guard();
        packets.store(0, std::memory_order_relaxed);
        continuity_errors.store(0, std::memory_order_relaxed);
        duplicate_packets.store(0, std::memory_order_relaxed);
        scrambled_packets.store(0, std::memory_order_relaxed);
        last_cc.store(-1, std::memory_order_relaxed);
        is_active.store(false, std::memory_order_relaxed);
        is_scrambled.store(false, std::memory_order_relaxed);
        is_pcr_pid.store(false, std::memory_order_relaxed);
        is_video.store(false, std::memory_order_relaxed);
        is_audio.store(false, std::memory_order_relaxed);
        stream_type.store(0, std::memory_order_relaxed);
        first_seen_ns.store(0, std::memory_order_relaxed);
        last_seen_ns.store(0, std::memory_order_relaxed);
        pcr_jitter_us = 0.0;
    }
};

/// PID Tracker - Maintains per-PID statistics for all PIDs in a transport stream.
///
/// Tracks:
/// - Packet counts per PID
/// - Continuity counter errors (ISO/IEC 13818-1)
/// - Duplicate packets
/// - Scrambled packet detection
/// - Stream type classification
/// - PID timeout detection for TR 101 290
///
/// Thread safety:
/// - Single writer (packet processing thread)
/// - Multiple readers (metrics queries)
/// - Lock-free reads via seqlock
class PidTracker {
public:
    // ========================================================================
    // Public Members
    // ========================================================================

    /// Per-PID tracking slots (8192 PIDs: 0x0000-0x1FFF)
    alignas(CACHE_LINE_SIZE) std::array<PidSlot, MAX_PIDS> slots;

    /// Active PID count
    alignas(CACHE_LINE_SIZE) std::atomic<std::int32_t> active_count{0};

    /// Total continuity counter errors across all PIDs
    alignas(CACHE_LINE_SIZE) std::atomic<std::int64_t> total_cc_errors{0};

    /// PIDs marked as "expected" (referenced in PAT/PMT)
    alignas(CACHE_LINE_SIZE) std::array<std::atomic<bool>, MAX_PIDS> expected_pids{};

    /// PID timeout count (for TR 101 290)
    alignas(CACHE_LINE_SIZE) std::atomic<std::int64_t> pid_timeout_count{0};

    // ========================================================================
    // Packet Processing
    // ========================================================================

    /// Process a packet for PID tracking and continuity counter validation.
    /// @param pid The packet PID (0-8191)
    /// @param cc The 4-bit continuity counter value
    /// @param has_payload Whether the packet carries payload (AFC bit 0)
    /// @param scrambled Whether the packet is scrambled (TSC != 0)
    /// @param time_ns Current timestamp in nanoseconds
    void process_packet(std::uint16_t pid, std::uint8_t cc, bool has_payload,
                       bool scrambled, std::int64_t time_ns) noexcept {
        auto& slot = slots[pid];

        // Check if first time seeing this PID
        if (!slot.is_active.load(std::memory_order_relaxed)) {
            slot.is_active.store(true, std::memory_order_release);
            slot.first_seen_ns.store(time_ns, std::memory_order_release);
            active_count.fetch_add(1, std::memory_order_relaxed);
        }

        slot.packets.fetch_add(1, std::memory_order_relaxed);
        slot.last_seen_ns.store(time_ns, std::memory_order_release);

        // Continuity counter validation (per ISO/IEC 13818-1)
        validate_continuity_counter(slot, cc, has_payload);

        // Track scrambled packets
        update_scrambled_state(slot, scrambled);
    }

    // ========================================================================
    // Stream Type Configuration
    // ========================================================================

    /// Set the stream type for a PID.
    /// @param pid The PID to configure
    /// @param type MPEG stream type code
    /// @param video Whether this is a video stream
    /// @param audio Whether this is an audio stream
    void set_stream_type(std::uint16_t pid, std::int32_t type,
                        bool video, bool audio) noexcept {
        auto& slot = slots[pid];
        slot.stream_type.store(type, std::memory_order_release);
        slot.is_video.store(video, std::memory_order_release);
        slot.is_audio.store(audio, std::memory_order_release);
    }

    /// Mark a PID as carrying PCR.
    /// @param pid The PID to mark
    /// @param is_pcr true if this PID carries PCR
    void set_pcr_pid(std::uint16_t pid, bool is_pcr) noexcept {
        slots[pid].is_pcr_pid.store(is_pcr, std::memory_order_release);
    }

    /// Update PCR jitter for a PID.
    /// @param pid The PID to update
    /// @param jitter_us PCR jitter in microseconds
    void set_pcr_jitter(std::uint16_t pid, double jitter_us) noexcept {
        auto& slot = slots[pid];
        auto guard = slot.seqlock.write_guard();
        slot.pcr_jitter_us = jitter_us;
    }

    // ========================================================================
    // PID Information Retrieval
    // ========================================================================

    /// Get extended information for all active PIDs.
    /// @param out_pids Span to receive PID information
    /// @return Number of PIDs returned
    [[nodiscard]] std::int32_t get_pid_info(
        std::span<TsDuckPidInfoExtended> out_pids) const noexcept {
        if (out_pids.empty()) {
            return 0;
        }

        std::int32_t count = 0;
        const auto max_pids = static_cast<std::int32_t>(out_pids.size());

        for (std::size_t pid = 0; pid < MAX_PIDS && count < max_pids; ++pid) {
            const auto& slot = slots[pid];

            if (!slot.is_active.load(std::memory_order_acquire)) {
                continue;
            }

            auto& out = out_pids[count];
            populate_pid_info(out, slot, static_cast<std::int32_t>(pid));
            ++count;
        }

        return count;
    }

    /// Get extended information for all active PIDs (raw pointer overload).
    /// @param out_pids Array to receive PID information
    /// @param max_pids Maximum number of PIDs to return
    /// @return Number of PIDs returned
    [[nodiscard]] std::int32_t get_count(TsDuckPidInfoExtended* out_pids,
                                         std::int32_t max_pids) const noexcept {
        if (out_pids == nullptr || max_pids <= 0) {
            return 0;
        }
        return get_pid_info(std::span{out_pids, static_cast<std::size_t>(max_pids)});
    }

    /// Get the number of active PIDs.
    [[nodiscard]] std::int32_t get_active_count() const noexcept {
        return active_count.load(std::memory_order_acquire);
    }

    // ========================================================================
    // TR 101 290 PID Timeout Detection
    // ========================================================================

    /// Mark a PID as "expected" (referenced in PAT/PMT).
    /// Used for TR 101 290 PID timeout detection.
    /// @param pid The PID to mark as expected
    void mark_expected(std::uint16_t pid) noexcept {
        if (pid < MAX_PIDS) {
            expected_pids[pid].store(true, std::memory_order_release);
        }
    }

    /// Check for PID timeouts.
    /// PIDs marked as expected that haven't been seen for 5 seconds
    /// (TR101290_PID_TIMEOUT_NS) should trigger pid_error.
    /// @param current_time_ns Current timestamp in nanoseconds
    /// @param bitrate Stream bitrate (reserved for stream-time calculation)
    /// @param current_packet_idx Current packet index (reserved)
    /// @return Number of PIDs that have timed out since last check
    [[nodiscard]] std::int32_t check_pid_timeouts(
        std::int64_t current_time_ns,
        [[maybe_unused]] std::int64_t bitrate,
        [[maybe_unused]] std::int64_t current_packet_idx) noexcept {

        std::int32_t timeouts = 0;

        for (std::size_t pid = 0; pid < MAX_PIDS; ++pid) {
            if (!expected_pids[pid].load(std::memory_order_relaxed)) {
                continue;  // Not an expected PID
            }

            const auto& slot = slots[pid];
            if (!slot.is_active.load(std::memory_order_relaxed)) {
                // Expected PID that has never been seen
                continue;
            }

            std::int64_t last_seen = slot.last_seen_ns.load(std::memory_order_relaxed);
            if (last_seen <= 0) {
                continue;
            }

            // Check wall-clock timeout (5 seconds)
            std::int64_t elapsed_ns = current_time_ns - last_seen;
            if (elapsed_ns > TR101290_PID_TIMEOUT_NS) {
                ++timeouts;
                pid_timeout_count.fetch_add(1, std::memory_order_relaxed);

                // Update last_seen to prevent repeated counting
                slots[pid].last_seen_ns.store(current_time_ns, std::memory_order_release);
            }
        }

        return timeouts;
    }

    /// Get total PID timeout count.
    [[nodiscard]] std::int64_t get_pid_timeout_count() const noexcept {
        return pid_timeout_count.load(std::memory_order_acquire);
    }

    // ========================================================================
    // Reset
    // ========================================================================

    /// Reset all tracking state.
    void reset() noexcept {
        for (auto& slot : slots) {
            slot.reset();
        }
        for (auto& expected : expected_pids) {
            expected.store(false, std::memory_order_relaxed);
        }
        active_count.store(0, std::memory_order_release);
        total_cc_errors.store(0, std::memory_order_release);
        pid_timeout_count.store(0, std::memory_order_release);
    }

private:
    // ========================================================================
    // Helper Functions
    // ========================================================================

    /// Validate continuity counter per ISO/IEC 13818-1.
    /// - CC increments by 1 for each packet with payload on the same PID
    /// - Packets without payload should have the same CC as previous
    /// - Duplicate: same CC + has_payload (allowed once, max 2 consecutive same CC)
    void validate_continuity_counter(PidSlot& slot, std::uint8_t cc,
                                    bool has_payload) noexcept {
        std::int32_t prev_cc = slot.last_cc.load(std::memory_order_relaxed);

        if (prev_cc >= 0 && has_payload) {
            std::int32_t expected = (prev_cc + 1) & 0x0F;
            if (static_cast<std::int32_t>(cc) != expected) {
                // Could be a duplicate (same CC as previous)
                if (static_cast<std::int32_t>(cc) == prev_cc) {
                    slot.duplicate_packets.fetch_add(1, std::memory_order_relaxed);
                } else {
                    // Genuine CC error
                    slot.continuity_errors.fetch_add(1, std::memory_order_relaxed);
                    total_cc_errors.fetch_add(1, std::memory_order_relaxed);
                }
            }
        }

        if (has_payload) {
            slot.last_cc.store(static_cast<std::int32_t>(cc), std::memory_order_relaxed);
        }
    }

    /// Update scrambled state for a packet.
    void update_scrambled_state(PidSlot& slot, bool scrambled) noexcept {
        if (scrambled) {
            slot.scrambled_packets.fetch_add(1, std::memory_order_relaxed);
            slot.is_scrambled.store(true, std::memory_order_release);
        } else {
            slot.is_scrambled.store(false, std::memory_order_release);
        }
    }

    /// Populate PID info structure from slot.
    void populate_pid_info(TsDuckPidInfoExtended& out, const PidSlot& slot,
                          std::int32_t pid) const noexcept {
        out.pid = pid;
        out.stream_type = slot.stream_type.load(std::memory_order_relaxed);
        out.packets = slot.packets.load(std::memory_order_relaxed);

        // Calculate bitrate
        std::int64_t first = slot.first_seen_ns.load(std::memory_order_relaxed);
        std::int64_t last = slot.last_seen_ns.load(std::memory_order_relaxed);
        std::int64_t duration_ms = (last - first) / 1'000'000;

        if (duration_ms > 0) {
            out.bitrate = (out.packets * static_cast<std::int64_t>(TS_PACKET_SIZE) *
                          8 * 1000) / duration_ms;
        } else {
            out.bitrate = 0;
        }

        out.continuity_errors = slot.continuity_errors.load(std::memory_order_relaxed);
        out.duplicate_packets = slot.duplicate_packets.load(std::memory_order_relaxed);
        out.scrambled_packets = slot.scrambled_packets.load(std::memory_order_relaxed);
        out.is_scrambled = slot.is_scrambled.load(std::memory_order_relaxed) ? 1 : 0;
        out.is_pcr_pid = slot.is_pcr_pid.load(std::memory_order_relaxed) ? 1 : 0;

        // Read PCR jitter with seqlock
        out.pcr_jitter_us = concurrency::seqlock_read(slot.seqlock, slot.pcr_jitter_us);

        out.is_video = slot.is_video.load(std::memory_order_relaxed) ? 1 : 0;
        out.is_audio = slot.is_audio.load(std::memory_order_relaxed) ? 1 : 0;
    }
};

}  // namespace tsduck_interop::analysis

#endif  // TSDUCK_INTEROP_ANALYSIS_PID_TRACKER_HPP
