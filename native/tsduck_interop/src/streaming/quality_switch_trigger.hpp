// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_STREAMING_QUALITY_SWITCH_TRIGGER_HPP
#define TSDUCK_INTEROP_STREAMING_QUALITY_SWITCH_TRIGGER_HPP

#include <chrono>
#include <cstdint>
#include "streaming_types.hpp"
#include "tsduck_interop.h"

namespace tsduck_interop::streaming {

// ============================================================================
// QualitySwitchTrigger: Monitors TR 101 290 error rates and triggers switches
// ============================================================================
//
// This class implements quality-based URL switching based on ETSI TR 101 290
// error rates. It uses a sliding window approach to calculate error rates
// and triggers a switch when thresholds are exceeded.
//
// Industry context:
// - TR 101 290 defines WHAT to measure (error indicators)
// - Thresholds are operator-configurable (no universal standard)
// - We use "errors per second" averaged over a sliding window
//
// Priority 1 errors (critical - affects decodability):
// - sync_loss: Any occurrence = immediate switch (stream undecodable)
// - continuity_count_error: Packet loss indicator
//
// Priority 2 errors (recommended monitoring):
// - transport_error: TEI bit set by upstream equipment
// - pcr_discontinuity_error + pcr_repetition_error: Timing issues
//
// The trigger does NOT own any network resources. It only reads TR 101 290
// counters and returns a decision.

class QualitySwitchTrigger {
public:
    explicit QualitySwitchTrigger(const StreamerConfig& config) noexcept
        : config_(config), enabled_(config.enable_quality_switch != 0),
          last_check_time_(std::chrono::steady_clock::now()), window_start_time_(std::chrono::steady_clock::now()),
          baseline_set_(false), total_quality_switches_(0) {}

    // ========================================================================
    // Main Check Function
    // ========================================================================

    /// Check if quality has degraded enough to warrant a URL switch.
    /// @param p1 Current Priority 1 counters from TR 101 290 monitor
    /// @param p2 Current Priority 2 counters from TR 101 290 monitor
    /// @return true if should switch to next URL, false otherwise
    bool should_switch(const Tr101290Priority1Native& p1, const Tr101290Priority2Native& p2) noexcept {
        if (!enabled_) {
            return false;
        }

        auto now = std::chrono::steady_clock::now();

        // First call: establish baseline (always allowed, ignores interval)
        if (!baseline_set_) {
            set_baseline(p1, p2);
            last_check_time_ = now;
            return false;
        }

        // Check interval - don't spam quality checks
        auto since_last = std::chrono::duration_cast<std::chrono::milliseconds>(now - last_check_time_).count();
        if (since_last < config_.quality_check_interval_ms) {
            return false;
        }
        last_check_time_ = now;

        // Calculate window duration
        auto window_ms = std::chrono::duration_cast<std::chrono::milliseconds>(now - window_start_time_).count();
        if (window_ms <= 0) {
            return false;
        }

        double window_seconds = static_cast<double>(window_ms) / 1000.0;

        // ====================================================================
        // Priority 1 Checks (Critical)
        // ====================================================================

        // Sync loss: ANY occurrence is catastrophic - immediate switch
        int64_t sync_loss_delta = p1.sync_loss - baseline_p1_.sync_loss;
        // Negative delta indicates counter reset/wraparound - re-establish baseline
        if (sync_loss_delta < 0) {
            reset_window(p1, p2);
            return false;
        }
        if (sync_loss_delta >= config_.max_sync_errors_per_window) {
            reset_window(p1, p2);
            total_quality_switches_++;
            return true;
        }

        // Continuity count errors: indicates packet loss
        int64_t cc_delta = p1.continuity_count_error - baseline_p1_.continuity_count_error;
        if (cc_delta < 0) {
            reset_window(p1, p2);
            return false;
        }
        double cc_per_sec = static_cast<double>(cc_delta) / window_seconds;
        if (cc_per_sec > static_cast<double>(config_.max_continuity_errors_per_sec)) {
            reset_window(p1, p2);
            total_quality_switches_++;
            return true;
        }

        // ====================================================================
        // Priority 2 Checks (Recommended Monitoring)
        // ====================================================================

        // Transport errors (TEI bit)
        int64_t tei_delta = p2.transport_error - baseline_p2_.transport_error;
        if (tei_delta < 0) {
            reset_window(p1, p2);
            return false;
        }
        double tei_per_sec = static_cast<double>(tei_delta) / window_seconds;
        if (tei_per_sec > static_cast<double>(config_.max_transport_errors_per_sec)) {
            reset_window(p1, p2);
            total_quality_switches_++;
            return true;
        }

        // PCR errors (discontinuity + repetition combined)
        int64_t pcr_disc_delta = p2.pcr_discontinuity_error - baseline_p2_.pcr_discontinuity_error;
        int64_t pcr_rep_delta = p2.pcr_repetition_error - baseline_p2_.pcr_repetition_error;
        if (pcr_disc_delta < 0 || pcr_rep_delta < 0) {
            reset_window(p1, p2);
            return false;
        }
        int64_t pcr_delta = pcr_disc_delta + pcr_rep_delta;
        double pcr_per_sec = static_cast<double>(pcr_delta) / window_seconds;
        if (pcr_per_sec > static_cast<double>(config_.max_pcr_errors_per_sec)) {
            reset_window(p1, p2);
            total_quality_switches_++;
            return true;
        }

        // ====================================================================
        // Sliding Window Maintenance
        // ====================================================================

        // If we've exceeded the window duration, slide it forward
        if (window_seconds >= static_cast<double>(config_.quality_window_seconds)) {
            reset_window(p1, p2);
        }

        return false;
    }

    // ========================================================================
    // State Management
    // ========================================================================

    /// Reset the quality trigger for a new streaming session or after switch.
    /// Called when:
    /// - Starting a new stream
    /// - After a URL switch (fresh baseline on new source)
    /// - After reconnection to same URL
    void reset() noexcept {
        baseline_set_ = false;
        last_check_time_ = std::chrono::steady_clock::now();
        window_start_time_ = std::chrono::steady_clock::now();
    }

    /// Get the number of switches triggered by quality degradation
    int64_t total_quality_switches() const noexcept { return total_quality_switches_; }

    /// Check if quality-based switching is enabled
    bool is_enabled() const noexcept { return enabled_; }

    // ========================================================================
    // Diagnostics (for debugging/logging)
    // ========================================================================

    struct QualitySnapshot {
        double cc_errors_per_sec;
        double tei_errors_per_sec;
        double pcr_errors_per_sec;
        int64_t sync_losses_in_window;
        double window_seconds;
        bool thresholds_exceeded;
    };

    /// Get current quality metrics for diagnostics
    QualitySnapshot get_snapshot(const Tr101290Priority1Native& p1, const Tr101290Priority2Native& p2) const noexcept {
        QualitySnapshot snap{};

        if (!baseline_set_) {
            return snap;
        }

        auto now = std::chrono::steady_clock::now();
        auto window_ms = std::chrono::duration_cast<std::chrono::milliseconds>(now - window_start_time_).count();
        if (window_ms <= 0) {
            return snap;
        }

        double window_seconds = static_cast<double>(window_ms) / 1000.0;
        snap.window_seconds = window_seconds;

        // Use max(0, delta) to handle counter reset/wraparound gracefully
        int64_t cc_delta = p1.continuity_count_error - baseline_p1_.continuity_count_error;
        snap.cc_errors_per_sec = cc_delta > 0 ? static_cast<double>(cc_delta) / window_seconds : 0.0;

        int64_t tei_delta = p2.transport_error - baseline_p2_.transport_error;
        snap.tei_errors_per_sec = tei_delta > 0 ? static_cast<double>(tei_delta) / window_seconds : 0.0;

        int64_t pcr_disc_delta = p2.pcr_discontinuity_error - baseline_p2_.pcr_discontinuity_error;
        int64_t pcr_rep_delta = p2.pcr_repetition_error - baseline_p2_.pcr_repetition_error;
        int64_t pcr_delta = (pcr_disc_delta > 0 ? pcr_disc_delta : 0) + (pcr_rep_delta > 0 ? pcr_rep_delta : 0);
        snap.pcr_errors_per_sec = static_cast<double>(pcr_delta) / window_seconds;

        int64_t sync_delta = p1.sync_loss - baseline_p1_.sync_loss;
        snap.sync_losses_in_window = sync_delta > 0 ? sync_delta : 0;

        snap.thresholds_exceeded =
            (snap.sync_losses_in_window >= config_.max_sync_errors_per_window) ||
            (snap.cc_errors_per_sec > static_cast<double>(config_.max_continuity_errors_per_sec)) ||
            (snap.tei_errors_per_sec > static_cast<double>(config_.max_transport_errors_per_sec)) ||
            (snap.pcr_errors_per_sec > static_cast<double>(config_.max_pcr_errors_per_sec));

        return snap;
    }

private:
    void set_baseline(const Tr101290Priority1Native& p1, const Tr101290Priority2Native& p2) noexcept {
        baseline_p1_ = p1;
        baseline_p2_ = p2;
        window_start_time_ = std::chrono::steady_clock::now();
        baseline_set_ = true;
    }

    void reset_window(const Tr101290Priority1Native& p1, const Tr101290Priority2Native& p2) noexcept {
        // Slide window: current counters become new baseline
        set_baseline(p1, p2);
    }

    StreamerConfig config_;
    bool enabled_;

    std::chrono::steady_clock::time_point last_check_time_;
    std::chrono::steady_clock::time_point window_start_time_;

    Tr101290Priority1Native baseline_p1_{};
    Tr101290Priority2Native baseline_p2_{};
    bool baseline_set_;

    int64_t total_quality_switches_;
};

}  // namespace tsduck_interop::streaming

#endif  // TSDUCK_INTEROP_STREAMING_QUALITY_SWITCH_TRIGGER_HPP
