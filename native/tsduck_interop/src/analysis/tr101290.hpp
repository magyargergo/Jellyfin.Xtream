// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_ANALYSIS_TR101290_HPP
#define TSDUCK_INTEROP_ANALYSIS_TR101290_HPP

#include <algorithm>
#include <array>
#include <atomic>
#include <cstdint>
#include <limits>

#include "../concurrency/seqlock.hpp"
#include "../core/constants.hpp"
#include "tsduck_interop.h"

namespace tsduck_interop::analysis {

/// TR 101 290 Quality Monitor
///
/// Implements ETSI TR 101 290 V1.3.1 Priority 1 and Priority 2 indicators
/// using ts::TSPacket methods (getTEI, getDiscontinuityIndicator).
///
/// Priority 1 (Critical - affects decodability):
/// - sync_byte_error: Invalid 0x47 sync byte
/// - sync_loss: 2+ consecutive sync errors
/// - pat_error: PAT not received within 500ms
/// - continuity_count_error: Packet loss indicator (tracked by PidTracker)
/// - pmt_error: PMT not received within 500ms
/// - pid_error: Referenced PID not seen for 5s
///
/// Priority 2 (Recommended monitoring):
/// - transport_error: TEI bit set
/// - crc_error: PSI section CRC failure
/// - pcr_repetition_error: >40ms between PCRs
/// - pcr_discontinuity_error: Unexpected PCR jump
/// - pcr_accuracy_error: >500ns deviation
/// - pts_error: >700ms between PTS
/// - cat_error: CAT not received within 500ms
class Tr101290Monitor {
public:
    // ========================================================================
    // Public Members
    // ========================================================================

    /// Seqlock-protected counters for lock-free reads from C# thread
    concurrency::Seqlock seqlock;
    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};

    /// Estimated bitrate for PCR accuracy/repetition checks (bits per second)
    std::atomic<std::int64_t> estimated_bitrate_bps{0};

    // ========================================================================
    // Constructor
    // ========================================================================

    Tr101290Monitor() noexcept {
        last_pts_time_ns_.fill(0);
    }

    // ========================================================================
    // Priority 1 Checks
    // ========================================================================

    /// Check sync byte validity.
    /// Call for EVERY packet in the stream (even invalid ones).
    /// Tracks sync_byte_error and sync_loss.
    /// @param valid_sync true if packet has valid 0x47 sync byte
    void check_sync(bool valid_sync) noexcept {
        if (!valid_sync) {
            auto guard = seqlock.write_guard();
            ++p1.sync_byte_error;
            ++consecutive_sync_errors_;
            if (consecutive_sync_errors_ >= 2) {
                ++p1.sync_loss;
            }
        } else {
            consecutive_sync_errors_ = 0;
        }
    }

    /// Call when PAT is successfully received (from PsiMonitor).
    /// Records the packet index for stream-time timeout calculation.
    /// @param packet_idx Current packet index
    void on_pat_received(std::int64_t packet_idx) noexcept {
        last_pat_packet_idx_.store(packet_idx, std::memory_order_release);
        if (!pat_baseline_set_.load(std::memory_order_relaxed)) {
            pat_baseline_set_.store(true, std::memory_order_release);
        }
    }

    /// Check for PAT timeout (500ms stream-time between PAT packets).
    /// Uses packet-distance-based timing to avoid wall-clock jitter from HTTP delivery.
    /// @param current_packet_idx Current packet index
    void check_pat_timeout(std::int64_t current_packet_idx) noexcept {
        if (!pat_baseline_set_.load(std::memory_order_acquire)) {
            return;  // Still in startup grace period
        }

        std::int64_t bitrate = estimated_bitrate_bps.load(std::memory_order_relaxed);
        if (bitrate <= 0) {
            return;  // Can't compute stream-time without bitrate
        }

        std::int64_t last_idx = last_pat_packet_idx_.load(std::memory_order_relaxed);
        if (last_idx < 0) {
            return;
        }

        std::int64_t packet_delta = current_packet_idx - last_idx;
        if (packet_delta <= 0) {
            return;
        }

        std::int64_t interval_ns = compute_stream_time_interval(packet_delta, bitrate);
        if (interval_ns > TR101290_PAT_INTERVAL_NS) {
            auto guard = seqlock.write_guard();
            ++p1.pat_error;
            // Reset so we don't keep counting for the same gap
            last_pat_packet_idx_.store(current_packet_idx, std::memory_order_release);
        }
    }

    /// Call when PMT timeout is detected (from PsiMonitor timing)
    void on_pmt_timeout() noexcept {
        auto guard = seqlock.write_guard();
        ++p1.pmt_error;
    }

    /// Call when CAT is successfully received (from PsiMonitor).
    /// Records the packet index for stream-time timeout calculation.
    /// @param packet_idx Current packet index
    void on_cat_received(std::int64_t packet_idx) noexcept {
        last_cat_packet_idx_.store(packet_idx, std::memory_order_release);
        if (!cat_baseline_set_.load(std::memory_order_relaxed)) {
            cat_baseline_set_.store(true, std::memory_order_release);
            cat_present_in_stream_.store(true, std::memory_order_release);
        }
    }

    /// Check for CAT timeout (500ms stream-time between CAT packets).
    /// Only checked if CAT has been observed in the stream (conditional access present).
    /// @param current_packet_idx Current packet index
    void check_cat_timeout(std::int64_t current_packet_idx) noexcept {
        if (!cat_present_in_stream_.load(std::memory_order_acquire)) {
            return;  // CAT not used in this stream
        }

        if (!cat_baseline_set_.load(std::memory_order_acquire)) {
            return;  // Still in startup grace period
        }

        std::int64_t bitrate = estimated_bitrate_bps.load(std::memory_order_relaxed);
        if (bitrate <= 0) {
            return;
        }

        std::int64_t last_idx = last_cat_packet_idx_.load(std::memory_order_relaxed);
        if (last_idx < 0) {
            return;
        }

        std::int64_t packet_delta = current_packet_idx - last_idx;
        if (packet_delta <= 0) {
            return;
        }

        std::int64_t interval_ns = compute_stream_time_interval(packet_delta, bitrate);
        if (interval_ns > TR101290_CAT_INTERVAL_NS) {
            auto guard = seqlock.write_guard();
            ++p2.cat_error;
            last_cat_packet_idx_.store(current_packet_idx, std::memory_order_release);
        }
    }

    /// Call when a PID referenced in PAT/PMT hasn't been seen for 5 seconds
    void on_pid_timeout() noexcept {
        auto guard = seqlock.write_guard();
        ++p1.pid_error;
    }

    // ========================================================================
    // Priority 2 Checks
    // ========================================================================

    /// Check Transport Error Indicator (TEI) bit.
    /// Uses ts::TSPacket::getTEI()
    /// @param pkt The TS packet to check
    void check_transport_error(ts::TSPacket& pkt) noexcept {
        if (pkt.getTEI()) {
            auto guard = seqlock.write_guard();
            ++p2.transport_error;
        }
    }

    /// Called by PsiMonitor when CRC validation fails
    void on_crc_error() noexcept {
        auto guard = seqlock.write_guard();
        ++p2.crc_error;
    }

    /// Check PCR repetition rate (max 40ms between PCRs on same PID).
    /// Uses packet-distance-based timing to avoid wall-clock jitter from HTTP delivery.
    /// @param pid The PID carrying the PCR
    /// @param packet_idx Current packet index
    void check_pcr_repetition(std::uint16_t pid, std::int64_t packet_idx) noexcept {
        auto& track = pcr_tracks_[pid];
        if (!track.initialized) {
            track.last_packet_idx = packet_idx;
            track.initialized = true;
            return;
        }

        std::int64_t bitrate = estimated_bitrate_bps.load(std::memory_order_relaxed);
        if (bitrate <= 0) {
            track.last_packet_idx = packet_idx;
            return;
        }

        std::int64_t packet_delta = packet_idx - track.last_packet_idx;
        if (packet_delta <= 0) {
            track.last_packet_idx = packet_idx;
            return;
        }

        std::int64_t interval_ns = compute_stream_time_interval(packet_delta, bitrate);
        if (interval_ns > TR101290_PCR_INTERVAL_NS) {
            auto guard = seqlock.write_guard();
            ++p2.pcr_repetition_error;
        }
        track.last_packet_idx = packet_idx;
    }

    /// Check PCR discontinuity (unexpected jump without discontinuity indicator).
    /// Uses ts::TSPacket::getDiscontinuityIndicator()
    /// @param pkt The TS packet
    /// @param pid The PID carrying the PCR
    /// @param pcr_value The 42-bit PCR value
    void check_pcr_discontinuity(ts::TSPacket& pkt, std::uint16_t pid,
                                 std::uint64_t pcr_value) noexcept {
        auto& track = pcr_tracks_[pid];

        if (track.last_value == INVALID_PCR) {
            track.last_value = pcr_value;
            return;
        }

        // If discontinuity indicator is set, expect PCR jump - no error
        if (pkt.getDiscontinuityIndicator()) {
            track.last_value = pcr_value;
            return;
        }

        // Calculate PCR difference with wraparound handling
        std::int64_t diff = static_cast<std::int64_t>(pcr_value) -
                           static_cast<std::int64_t>(track.last_value);
        if (diff < 0) {
            diff += PCR_WRAPAROUND;
        }

        // Check for unexpected large jump (>100ms at 27MHz)
        if (diff > TR101290_PCR_DISCONTINUITY_TICKS) {
            auto guard = seqlock.write_guard();
            ++p2.pcr_discontinuity_error;
        }

        track.last_value = pcr_value;
    }

    /// Check PCR accuracy (deviation from expected based on bitrate).
    /// @param pid The PID carrying the PCR
    /// @param pcr_value The 42-bit PCR value
    /// @param packet_idx Current packet index
    void check_pcr_accuracy(std::uint16_t pid, std::uint64_t pcr_value,
                           std::int64_t packet_idx) noexcept {
        auto& track = pcr_tracks_[pid];
        std::int64_t bitrate = estimated_bitrate_bps.load(std::memory_order_relaxed);

        if (track.last_value == INVALID_PCR || bitrate <= 0 ||
            track.last_packet_idx == 0) {
            track.last_packet_idx = packet_idx;
            return;
        }

        std::int64_t packet_delta = packet_idx - track.last_packet_idx;
        if (packet_delta <= 0) {
            track.last_packet_idx = packet_idx;
            return;
        }

        // Expected PCR delta based on bitrate
        double bits_transmitted =
            static_cast<double>(packet_delta) * static_cast<double>(TS_PACKET_SIZE_BITS);
        double expected_delta_27mhz =
            (bits_transmitted / static_cast<double>(bitrate)) *
            static_cast<double>(ts::SYSTEM_CLOCK_FREQ);

        // Actual PCR delta
        std::int64_t actual_diff = static_cast<std::int64_t>(pcr_value) -
                                  static_cast<std::int64_t>(track.last_value);
        if (actual_diff < 0) {
            actual_diff += PCR_WRAPAROUND;
        }

        // Check accuracy: |actual - expected| > 500ns (+/-13.5 ticks at 27MHz)
        double error = std::abs(static_cast<double>(actual_diff) - expected_delta_27mhz);

        if (error > static_cast<double>(TR101290_PCR_ACCURACY_TICKS)) {
            auto guard = seqlock.write_guard();
            ++p2.pcr_accuracy_error;
        }

        track.last_packet_idx = packet_idx;
    }

    /// Check PTS repetition period (max 700ms between PTS on same PID).
    /// @param pid The PID carrying the PTS
    /// @param current_time_ns Current wall-clock time in nanoseconds
    void check_pts_repetition(std::uint16_t pid, std::int64_t current_time_ns) noexcept {
        std::int64_t last = last_pts_time_ns_[pid];
        if (last > 0 && (current_time_ns - last) > TR101290_PTS_INTERVAL_NS) {
            auto guard = seqlock.write_guard();
            ++p2.pts_error;
        }
        last_pts_time_ns_[pid] = current_time_ns;
    }

    // ========================================================================
    // Retrieval
    // ========================================================================

    /// Get current TR 101 290 counters (lock-free read).
    /// @param out_p1 Pointer to receive Priority 1 counters
    /// @param out_p2 Pointer to receive Priority 2 counters
    void get_counters(Tr101290Priority1Native* out_p1,
                     Tr101290Priority2Native* out_p2) const noexcept {
        if (out_p1 == nullptr || out_p2 == nullptr) {
            return;
        }

        std::uint64_t seq;
        do {
            seq = seqlock.begin_read();
            *out_p1 = p1;
            *out_p2 = p2;
        } while (!seqlock.read_consistent(seq));
    }

    /// Get Priority 1 counters.
    [[nodiscard]] Tr101290Priority1Native get_priority1() const noexcept {
        return concurrency::seqlock_read(seqlock, p1);
    }

    /// Get Priority 2 counters.
    [[nodiscard]] Tr101290Priority2Native get_priority2() const noexcept {
        return concurrency::seqlock_read(seqlock, p2);
    }

    // ========================================================================
    // Reset
    // ========================================================================

    /// Reset all counters and state.
    void reset() noexcept {
        {
            auto guard = seqlock.write_guard();
            p1 = Tr101290Priority1Native{};
            p2 = Tr101290Priority2Native{};
        }

        std::fill(pcr_tracks_.begin(), pcr_tracks_.end(), PcrTrack{});
        last_pts_time_ns_.fill(0);
        consecutive_sync_errors_ = 0;
        last_pat_packet_idx_.store(-1, std::memory_order_release);
        last_cat_packet_idx_.store(-1, std::memory_order_release);
        pat_baseline_set_.store(false, std::memory_order_release);
        cat_baseline_set_.store(false, std::memory_order_release);
        cat_present_in_stream_.store(false, std::memory_order_release);
        estimated_bitrate_bps.store(0, std::memory_order_release);
    }

private:
    // ========================================================================
    // Constants
    // ========================================================================

    static constexpr std::uint64_t INVALID_PCR = std::numeric_limits<std::uint64_t>::max();

    // ========================================================================
    // Helper Functions
    // ========================================================================

    /// Compute stream-time interval from packet distance and bitrate.
    /// @param packet_delta Number of packets
    /// @param bitrate_bps Stream bitrate in bits per second
    /// @return Interval in nanoseconds
    [[nodiscard]] static std::int64_t compute_stream_time_interval(
        std::int64_t packet_delta, std::int64_t bitrate_bps) noexcept {
        double bits_transmitted =
            static_cast<double>(packet_delta) * static_cast<double>(TS_PACKET_SIZE_BITS);
        double interval_sec = bits_transmitted / static_cast<double>(bitrate_bps);
        return static_cast<std::int64_t>(interval_sec * 1e9);
    }

    // ========================================================================
    // Per-PID PCR Tracking
    // ========================================================================

    struct PcrTrack {
        std::uint64_t last_value{INVALID_PCR};  // Last PCR value (27MHz)
        std::int64_t last_packet_idx{0};        // Packet index of last PCR
        bool initialized{false};
    };

    std::array<PcrTrack, MAX_PIDS> pcr_tracks_{};

    // ========================================================================
    // Per-PID PTS Tracking
    // ========================================================================

    std::array<std::int64_t, MAX_PIDS> last_pts_time_ns_{};

    // ========================================================================
    // Sync Loss Tracking
    // ========================================================================

    std::int32_t consecutive_sync_errors_{0};

    // ========================================================================
    // PAT/PMT/CAT Timing
    // ========================================================================

    std::atomic<std::int64_t> last_pat_packet_idx_{-1};
    std::atomic<std::int64_t> last_cat_packet_idx_{-1};

    // Startup grace: don't fire timeouts until first table is received
    std::atomic<bool> pat_baseline_set_{false};
    std::atomic<bool> cat_baseline_set_{false};

    // CAT presence tracking: only monitor CAT if the stream uses it
    std::atomic<bool> cat_present_in_stream_{false};
};

}  // namespace tsduck_interop::analysis

#endif  // TSDUCK_INTEROP_ANALYSIS_TR101290_HPP
