// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_ANALYSIS_TR101290_HPP
#define TSDUCK_INTEROP_ANALYSIS_TR101290_HPP

#include <algorithm>
#include <array>
#include <atomic>
#include <cstdint>
#include "../core/constants.hpp"
#include "../concurrency/seqlock.hpp"
#include "tsduck_interop.h"

namespace tsduck_interop::analysis {

// ============================================================================
// TR 101 290 Quality Monitor
// Implements ETSI TR 101 290 V1.3.1 Priority 1 and Priority 2 indicators
// using ts::TSPacket methods (getTEI, getDiscontinuityIndicator)
// ============================================================================

class Tr101290Monitor {
public:
    // Seqlock-protected counters for lock-free reads from C# thread
    concurrency::Seqlock seqlock;
    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};

    // Per-PID PCR tracking (only for PIDs carrying PCR)
    struct PcrTrack {
        uint64_t last_value{UINT64_MAX};  // Last PCR value (27MHz)
        int64_t last_packet_idx{0};       // Packet index of last PCR
        bool initialized{false};
    };
    std::array<PcrTrack, MAX_PIDS> pcr_tracks{};

    // Per-PID PTS tracking (for PTS repetition check)
    std::array<int64_t, MAX_PIDS> last_pts_time_ns{};

    // Sync loss tracking
    int32_t consecutive_sync_errors{0};

    // PAT/PMT timing — uses packet index for stream-time calculation
    std::atomic<int64_t> last_pat_packet_idx{-1};

    // Startup grace: don't fire PAT/PMT timeout until first PAT is received
    std::atomic<bool> pat_baseline_set{false};

    // Estimated bitrate for PCR accuracy/repetition checks
    std::atomic<int64_t> estimated_bitrate_bps{0};

    Tr101290Monitor() noexcept { last_pts_time_ns.fill(0); }

    // ========================================================================
    // Priority 1 Checks
    // ========================================================================

    /// Call for EVERY packet in the stream (even invalid ones)
    /// Tracks sync_byte_error and sync_loss
    void check_sync(bool valid_sync) noexcept {
        if (!valid_sync) {
            auto seq = seqlock.begin_write();
            p1.sync_byte_error++;
            consecutive_sync_errors++;
            if (consecutive_sync_errors >= 2) {
                p1.sync_loss++;
            }
            seqlock.end_write(seq);
        } else {
            consecutive_sync_errors = 0;
        }
    }

    /// Call when PAT is successfully received (from PsiMonitor).
    /// Records the packet index for stream-time timeout calculation.
    void on_pat_received(int64_t packet_idx) noexcept {
        last_pat_packet_idx.store(packet_idx, std::memory_order_release);
        if (!pat_baseline_set.load(std::memory_order_relaxed)) {
            pat_baseline_set.store(true, std::memory_order_release);
        }
    }

    /// Check for PAT timeout (500ms stream-time between PAT packets).
    /// Uses packet-distance-based timing to avoid wall-clock jitter from HTTP delivery.
    void check_pat_timeout(int64_t current_packet_idx) noexcept {
        if (!pat_baseline_set.load(std::memory_order_acquire)) {
            return;  // Still in startup grace period
        }

        int64_t bitrate = estimated_bitrate_bps.load(std::memory_order_relaxed);
        if (bitrate <= 0) {
            return;  // Can't compute stream-time without bitrate
        }

        int64_t last_idx = last_pat_packet_idx.load(std::memory_order_relaxed);
        if (last_idx < 0)
            return;

        int64_t packet_delta = current_packet_idx - last_idx;
        if (packet_delta <= 0)
            return;

        // Compute stream-time interval from byte distance and bitrate
        double bits_transmitted = static_cast<double>(packet_delta) * static_cast<double>(ts::PKT_SIZE_BITS);
        double interval_sec = bits_transmitted / static_cast<double>(bitrate);
        int64_t interval_ns = static_cast<int64_t>(interval_sec * 1e9);

        if (interval_ns > TR101290_PAT_INTERVAL_NS) {
            auto seq = seqlock.begin_write();
            p1.pat_error++;
            seqlock.end_write(seq);
            // Reset so we don't keep counting for the same gap
            last_pat_packet_idx.store(current_packet_idx, std::memory_order_release);
        }
    }

    /// Call when PMT timeout is detected (from PsiMonitor timing)
    void on_pmt_timeout() noexcept {
        auto seq = seqlock.begin_write();
        p1.pmt_error++;
        seqlock.end_write(seq);
    }

    /// Call when a PID referenced in PAT/PMT hasn't been seen for 5 seconds
    void on_pid_timeout() noexcept {
        auto seq = seqlock.begin_write();
        p1.pid_error++;
        seqlock.end_write(seq);
    }

    // ========================================================================
    // Priority 2 Checks
    // ========================================================================

    /// Check Transport Error Indicator (TEI) bit
    /// Uses ts::TSPacket::getTEI() — NEW TsDuck API usage
    void check_transport_error(ts::TSPacket& pkt) noexcept {
        if (pkt.getTEI()) {
            auto seq = seqlock.begin_write();
            p2.transport_error++;
            seqlock.end_write(seq);
        }
    }

    /// Called by PsiMonitor when CRC validation fails
    void on_crc_error() noexcept {
        auto seq = seqlock.begin_write();
        p2.crc_error++;
        seqlock.end_write(seq);
    }

    /// Check PCR repetition rate (max 40ms between PCRs on same PID).
    /// Uses packet-distance-based timing to avoid wall-clock jitter from HTTP delivery.
    /// TR 101 290 defines this as the stream-time interval between consecutive PCR packets.
    void check_pcr_repetition(uint16_t pid, int64_t packet_idx) noexcept {
        auto& track = pcr_tracks[pid];
        if (!track.initialized) {
            track.last_packet_idx = packet_idx;
            track.initialized = true;
            return;
        }

        int64_t bitrate = estimated_bitrate_bps.load(std::memory_order_relaxed);
        if (bitrate <= 0) {
            // Can't compute stream-time interval without bitrate — skip check
            track.last_packet_idx = packet_idx;
            return;
        }

        // Compute stream-time interval from byte distance and bitrate
        int64_t packet_delta = packet_idx - track.last_packet_idx;
        if (packet_delta <= 0) {
            track.last_packet_idx = packet_idx;
            return;
        }

        // interval_ns = (packet_delta * PKT_SIZE_BITS / bitrate) * 1e9
        double bits_transmitted = static_cast<double>(packet_delta) * static_cast<double>(ts::PKT_SIZE_BITS);
        double interval_sec = bits_transmitted / static_cast<double>(bitrate);
        int64_t interval_ns = static_cast<int64_t>(interval_sec * 1e9);

        if (interval_ns > TR101290_PCR_INTERVAL_NS) {
            auto seq = seqlock.begin_write();
            p2.pcr_repetition_error++;
            seqlock.end_write(seq);
        }
        track.last_packet_idx = packet_idx;
    }

    /// Check PCR discontinuity (unexpected jump without discontinuity indicator)
    /// Uses ts::TSPacket::getDiscontinuityIndicator() — NEW TsDuck API usage
    void check_pcr_discontinuity(ts::TSPacket& pkt, uint16_t pid, uint64_t pcr_value) noexcept {
        auto& track = pcr_tracks[pid];

        if (track.last_value == UINT64_MAX) {
            track.last_value = pcr_value;
            return;
        }

        // If discontinuity indicator is set, expect PCR jump — no error
        if (pkt.getDiscontinuityIndicator()) {
            track.last_value = pcr_value;
            return;
        }

        // Calculate PCR difference with wraparound
        int64_t diff = static_cast<int64_t>(pcr_value) - static_cast<int64_t>(track.last_value);
        if (diff < 0) {
            diff += static_cast<int64_t>(ts::PCR_SCALE);
        }

        // Check for unexpected large jump (>100ms at 27MHz)
        // or backward jump (negative after wraparound correction)
        if (diff > TR101290_PCR_DISCONTINUITY_TICKS) {
            auto seq = seqlock.begin_write();
            p2.pcr_discontinuity_error++;
            seqlock.end_write(seq);
        }

        track.last_value = pcr_value;
    }

    /// Check PCR accuracy (deviation from expected based on bitrate)
    void check_pcr_accuracy(uint16_t pid, uint64_t pcr_value, int64_t packet_idx) noexcept {
        auto& track = pcr_tracks[pid];
        int64_t bitrate = estimated_bitrate_bps.load(std::memory_order_relaxed);

        if (track.last_value == UINT64_MAX || bitrate <= 0 || track.last_packet_idx == 0) {
            track.last_packet_idx = packet_idx;
            return;
        }

        int64_t packet_delta = packet_idx - track.last_packet_idx;
        if (packet_delta <= 0) {
            track.last_packet_idx = packet_idx;
            return;
        }

        // Expected PCR delta based on bitrate
        double bits_transmitted = static_cast<double>(packet_delta) * static_cast<double>(ts::PKT_SIZE_BITS);
        double expected_delta_27mhz =
            (bits_transmitted / static_cast<double>(bitrate)) * static_cast<double>(ts::SYSTEM_CLOCK_FREQ);

        // Actual PCR delta
        int64_t actual_diff = static_cast<int64_t>(pcr_value) - static_cast<int64_t>(track.last_value);
        if (actual_diff < 0) {
            actual_diff += static_cast<int64_t>(ts::PCR_SCALE);
        }

        // Check accuracy: |actual - expected| > 500ns (±13.5 ticks at 27MHz)
        double error = static_cast<double>(actual_diff) - expected_delta_27mhz;
        if (error < 0)
            error = -error;

        if (error > static_cast<double>(TR101290_PCR_ACCURACY_TICKS)) {
            auto seq = seqlock.begin_write();
            p2.pcr_accuracy_error++;
            seqlock.end_write(seq);
        }

        track.last_packet_idx = packet_idx;
    }

    /// Check PTS repetition period (max 700ms between PTS on same PID)
    void check_pts_repetition(uint16_t pid, int64_t current_time_ns) noexcept {
        int64_t last = last_pts_time_ns[pid];
        if (last > 0 && (current_time_ns - last) > TR101290_PTS_INTERVAL_NS) {
            auto seq = seqlock.begin_write();
            p2.pts_error++;
            seqlock.end_write(seq);
        }
        last_pts_time_ns[pid] = current_time_ns;
    }

    // ========================================================================
    // Retrieval
    // ========================================================================

    /// Get current TR 101 290 counters (lock-free read)
    void get_counters(Tr101290Priority1Native* out_p1, Tr101290Priority2Native* out_p2) const noexcept {
        if (!out_p1 || !out_p2)
            return;

        uint64_t seq;
        do {
            seq = seqlock.begin_read();
            *out_p1 = p1;
            *out_p2 = p2;
        } while (!seqlock.read_consistent(seq));
    }

    void reset() noexcept {
        auto seq = seqlock.begin_write();
        p1 = Tr101290Priority1Native{};
        p2 = Tr101290Priority2Native{};
        seqlock.end_write(seq);

        std::fill(pcr_tracks.begin(), pcr_tracks.end(), PcrTrack{});
        last_pts_time_ns.fill(0);
        consecutive_sync_errors = 0;
        last_pat_packet_idx.store(-1, std::memory_order_release);
        pat_baseline_set.store(false, std::memory_order_release);
        estimated_bitrate_bps.store(0, std::memory_order_release);
    }
};

}  // namespace tsduck_interop::analysis

#endif  // TSDUCK_INTEROP_ANALYSIS_TR101290_HPP
