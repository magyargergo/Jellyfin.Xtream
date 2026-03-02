// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_RESTAMPING_RESTAMPER_HPP
#define TSDUCK_INTEROP_RESTAMPING_RESTAMPER_HPP

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <limits>
#include <span>

#include "../analysis/av_sync_tracker.hpp"
#include "../concurrency/seqlock.hpp"
#include "../core/constants.hpp"
#include "../core/logging.hpp"
#include "tsduck_interop.h"

namespace tsduck_interop::restamping {

/// Restamper - MPEG-TS timestamp correction for seamless streaming.
///
/// Provides:
/// - PCR smoothing: Eliminates jitter from HTTP chunked delivery
/// - PTS/DTS correction: Maintains A/V sync across provider switches
/// - Discontinuity indicator management: Per ISO 13818-1 Section 2.4.3.5
/// - Seamless URL switching: Handles mid-stream failover with proper offset calculation
///
/// Thread safety:
/// - Single writer (packet processing thread)
/// - Multiple readers (statistics queries via seqlock)
class alignas(CACHE_LINE_SIZE) Restamper {
public:
    // ========================================================================
    // Public Members
    // ========================================================================

    /// Configuration
    RestampingConfigNative config;

    /// Pointer to A/V sync tracker for drift information
    analysis::AvSyncTracker* av_sync;

    // ========================================================================
    // Constructor
    // ========================================================================

    /// Construct a restamper.
    /// @param tracker Pointer to A/V sync tracker for drift information
    /// @param cfg Configuration (uses defaults if nullptr)
    explicit Restamper(analysis::AvSyncTracker* tracker,
                      const RestampingConfigNative* cfg = nullptr)
        : av_sync(tracker) {
        if (cfg != nullptr) {
            config = *cfg;
        } else {
            config = make_default_config();
        }

        last_correction_time_ns_.store(now_ns(), std::memory_order_release);
    }

    // ========================================================================
    // Processing
    // ========================================================================

    /// Process MPEG-TS data with restamping.
    /// Modifies data in-place to apply timestamp corrections.
    /// @param data Pointer to MPEG-TS packet data
    /// @param length Length in bytes (must be multiple of 188)
    /// @param base_packet_idx Base packet index for this chunk
    /// @return Number of timestamp modifications made
    [[nodiscard]] std::int32_t process(std::uint8_t* data, std::int32_t length,
                                       std::int64_t base_packet_idx) noexcept {
        if (data == nullptr || length <= 0 ||
            config.mode == RESTAMP_MODE_DISABLED) {
            return 0;
        }

        std::int64_t current_time = now_ns();

        // Record stream start time on first call for warmup period.
        std::int64_t expected_zero = 0;
        stream_start_time_ns_.compare_exchange_strong(
            expected_zero, current_time, std::memory_order_acq_rel, std::memory_order_relaxed);

        std::int64_t last_time = last_correction_time_ns_.load(std::memory_order_relaxed);
        double elapsed_sec = static_cast<double>(current_time - last_time) / 1e9;

        double correction_ms = calculate_correction(elapsed_sec);
        double accumulated_ms = accumulated_correction_ms_.load(std::memory_order_acquire);
        std::int64_t accumulated_offset_90khz =
            static_cast<std::int64_t>(std::llround(accumulated_ms * 90.0));
        std::int64_t switch_off_90khz = switch_offset_90khz_.load(std::memory_order_relaxed);

        // Drift correction is stream-selective:
        // - Positive drift (audio timestamp later than video) => delay video only.
        // - Negative drift (audio timestamp earlier than video) => delay audio only.
        // The analyzer runs on ORIGINAL timestamps (before restamping), so
        // avg_drift_ms reflects the source's real A/V drift. The controller
        // computes the residual (source_drift - accumulated_correction) to
        // determine the remaining error that downstream consumers see.
        std::int64_t video_total_offset_90khz = switch_off_90khz;
        std::int64_t audio_total_offset_90khz = switch_off_90khz;
        if (accumulated_offset_90khz > 0) {
            video_total_offset_90khz += accumulated_offset_90khz;
        } else if (accumulated_offset_90khz < 0) {
            audio_total_offset_90khz += -accumulated_offset_90khz;
        }

        std::int64_t video_pid = target_video_pid_.load(std::memory_order_acquire);
        std::int64_t audio_pid = target_audio_pid_.load(std::memory_order_acquire);

        std::int32_t modifications = 0;

        // Local counters — batched into single seqlock write after loop
        std::int32_t local_pcr_smoothed = 0;
        std::int32_t local_pts_corrected = 0;
        std::int32_t local_dts_corrected = 0;
        std::int32_t local_disc_fixed = 0;

        std::int32_t packets = length / static_cast<std::int32_t>(TS_PACKET_SIZE);
        auto packet_span = std::span{reinterpret_cast<ts::TSPacket*>(data),
                                    static_cast<std::size_t>(packets)};

        for (std::int32_t i = 0; const auto& pkt_ref : packet_span) {
            // We need non-const access for modification
            auto& pkt = const_cast<ts::TSPacket&>(pkt_ref);
            std::int64_t packet_idx = base_packet_idx + i;
            ++i;

            if (!pkt.hasValidSync()) {
                continue;
            }

            // Process PCR with switch offset only. Drift correction is for A/V stream timestamps.
            modifications += process_pcr(pkt, packet_idx, switch_off_90khz, current_time,
                                         local_pcr_smoothed, local_disc_fixed);

            // Process PTS/DTS — apply whenever in CORRECT mode.
            // process_pts_dts() short-circuits via selected_offset_90khz == 0.
            if (config.mode == RESTAMP_MODE_CORRECT) {
                modifications += process_pts_dts(
                    pkt,
                    video_pid,
                    audio_pid,
                    switch_off_90khz,
                    video_total_offset_90khz,
                    audio_total_offset_90khz,
                    local_pts_corrected,
                    local_dts_corrected
                );
            }
        }

        // Update timing
        if (elapsed_sec >= MIN_CORRECTION_INTERVAL_SEC) {
            last_correction_time_ns_.store(current_time, std::memory_order_release);
        }

        // Single seqlock write for all statistics
        {
            auto guard = stats_seqlock_.write_guard();
            stats_.packets_processed += packets;
            stats_.pcr_smoothed += local_pcr_smoothed;
            stats_.pts_corrected += local_pts_corrected;
            stats_.dts_corrected += local_dts_corrected;
            stats_.discontinuities_fixed += local_disc_fixed;

            if (std::abs(correction_ms) > 0.001) {
                stats_.total_correction_ms += correction_ms;
                stats_.current_offset_ms = accumulated_correction_ms_.load(std::memory_order_relaxed);
                stats_.correction_active = correction_active_.load(std::memory_order_relaxed) ? 1 : 0;
                stats_.last_correction_time = get_dotnet_ticks();
            }
        }

        return modifications;
    }

    // ========================================================================
    // Provider Switch Handling
    // ========================================================================

    /// Handle provider switch for timestamp continuity.
    /// Per ISO/IEC 13818-1 Section 2.4.3.5, when creating a timestamp discontinuity,
    /// we must set the discontinuity_indicator flag on the first PCR packet after
    /// the switch to signal downstream decoders that timeline has changed.
    /// @param last_output_pts Last PTS value output before the switch (90kHz)
    /// @param new_input_first_pts First PTS from the new provider (90kHz)
    /// @param pcr_pid Optional: PCR PID to set discontinuity on (-1 for any)
    void handle_switch(std::int64_t last_output_pts,
                      std::int64_t new_input_first_pts,
                      std::int64_t pcr_pid = -1) noexcept {
        std::int64_t gap = switch_gap_90khz_.load(std::memory_order_relaxed);
        std::int64_t new_offset = last_output_pts + gap - new_input_first_pts;
        switch_offset_90khz_.store(new_offset, std::memory_order_release);

        // Signal discontinuity indicator needed on next PCR
        pending_discontinuity_.store(true, std::memory_order_release);
        discontinuity_pcr_pid_.store(pcr_pid, std::memory_order_release);

        // Reset smoothing state for fresh start from new source.
        // Note: stream_start_time_ns_ is intentionally NOT reset here.
        // After a switch the warmup has already elapsed; correction resumes
        // immediately using the windowed avg_drift_ms from the analyzer.
        last_smoothed_pcr_.store(INVALID_PCR, std::memory_order_release);
        last_original_pcr_.store(INVALID_PCR, std::memory_order_release);
        pcr_smoothing_delta_90khz_.store(0, std::memory_order_release);
        // Reset PI controller state to prevent stale accumulated error from previous
        // provider from influencing correction on the new source.
        drift_integral_ms_.store(0.0, std::memory_order_release);
        correction_activation_time_ns_.store(0, std::memory_order_release);
        // Signal EPTLA reset (consumed by writer thread to avoid data race)
        eptla_reset_pending_.store(true, std::memory_order_release);
        // Invalidate DTS-derived PCR offset for fresh calibration
        dts_pcr_state_.valid.store(false, std::memory_order_release);

        LOG_INFO("Restamper", "Provider switch: offset=%lld (90kHz), discontinuity pending",
                static_cast<long long>(new_offset));
    }

    // ========================================================================
    // Statistics
    // ========================================================================

    /// Get restamping statistics.
    /// @param out Pointer to receive statistics
    /// @return true if statistics available
    [[nodiscard]] bool get_statistics(RestampingStatisticsNative* out) const noexcept {
        if (out == nullptr) {
            return false;
        }

        *out = concurrency::seqlock_read(stats_seqlock_, stats_);
        return true;
    }

    // ========================================================================
    // Configuration
    // ========================================================================

    /// Update configuration at runtime.
    /// @param cfg New configuration
    void configure(const RestampingConfigNative* cfg) noexcept {
        if (cfg != nullptr) {
            config = *cfg;
        }
    }

    /// Set preferred video/audio PIDs for stream-selective drift correction.
    /// PIDs < 0 are ignored to allow partial updates.
    void set_target_pids(std::int64_t video_pid, std::int64_t audio_pid) noexcept {
        if (video_pid >= 0) {
            target_video_pid_.store(video_pid, std::memory_order_release);
        }
        if (audio_pid >= 0) {
            target_audio_pid_.store(audio_pid, std::memory_order_release);
        }
    }

    /// Set fallback video/audio PIDs only when current targets are unset.
    /// Used by PES stream-id detection before PMT-derived stream mapping is available.
    void set_target_pids_if_unset(std::int64_t video_pid, std::int64_t audio_pid) noexcept {
        if (video_pid >= 0) {
            std::int64_t expected = -1;
            (void)target_video_pid_.compare_exchange_strong(
                expected, video_pid, std::memory_order_acq_rel);
        }
        if (audio_pid >= 0) {
            std::int64_t expected = -1;
            (void)target_audio_pid_.compare_exchange_strong(
                expected, audio_pid, std::memory_order_acq_rel);
        }
    }

    /// Get current switch offset in 90kHz ticks.
    /// @return Current switch offset
    [[nodiscard]] std::int64_t get_switch_offset_90khz() const noexcept {
        return switch_offset_90khz_.load(std::memory_order_acquire);
    }

    /// Reset all state.
    void reset() noexcept {
        {
            auto guard = stats_seqlock_.write_guard();
            stats_ = RestampingStatisticsNative{};
        }


        correction_active_.store(false, std::memory_order_release);
        accumulated_correction_ms_.store(0.0, std::memory_order_release);
        switch_offset_90khz_.store(0, std::memory_order_release);
        last_smoothed_pcr_.store(INVALID_PCR, std::memory_order_release);
        last_pcr_packet_idx_.store(0, std::memory_order_release);
        last_original_pcr_.store(INVALID_PCR, std::memory_order_release);
        pcr_smoothing_delta_90khz_.store(0, std::memory_order_release);
        pending_discontinuity_.store(false, std::memory_order_release);
        discontinuity_pcr_pid_.store(-1, std::memory_order_release);
        target_video_pid_.store(-1, std::memory_order_release);
        target_audio_pid_.store(-1, std::memory_order_release);

        last_correction_time_ns_.store(now_ns(), std::memory_order_release);
        stream_start_time_ns_.store(0, std::memory_order_release);
        drift_integral_ms_.store(0.0, std::memory_order_release);
        correction_activation_time_ns_.store(0, std::memory_order_release);

        // Signal EPTLA reset (consumed by writer thread to avoid data race on non-atomic fields)
        eptla_reset_pending_.store(true, std::memory_order_release);
        // Invalidate DTS-derived PCR offset for fresh calibration
        dts_pcr_state_.valid.store(false, std::memory_order_release);
    }

private:
    // ========================================================================
    // Constants
    // ========================================================================

    static constexpr std::uint64_t INVALID_PCR = std::numeric_limits<std::uint64_t>::max();

    /// PCR discontinuity detection threshold: 100ms at 27MHz
    static constexpr std::int64_t PCR_DISCONTINUITY_THRESHOLD = 27'000'000 / 10;
    /// Clamp very large PCR jumps to avoid timeline shocks during short source stalls.
    static constexpr std::int64_t PCR_JUMP_CLAMP_TICKS = 27'000'000 * 3 / 10;  // 300ms

    /// Safe modulo for PCR wraparound (handles negative values without loop).
    [[nodiscard]] static constexpr std::int64_t safe_pcr_mod(std::int64_t val) noexcept {
        std::int64_t r = val % PCR_WRAPAROUND;
        return r < 0 ? r + PCR_WRAPAROUND : r;
    }

    // ========================================================================
    // Helper Functions
    // ========================================================================

    /// Create default configuration.
    [[nodiscard]] static constexpr RestampingConfigNative make_default_config() noexcept {
        return RestampingConfigNative{
            .mode = RESTAMP_MODE_MONITOR,
            .smooth_pcr = 1,
            .fix_discontinuities = 1,
            .reserved = 0,
            .correction_threshold_ms = DEFAULT_CORRECTION_THRESHOLD_MS,
            .max_correction_rate_ms = DEFAULT_MAX_CORRECTION_RATE_MS,
            .hysteresis_threshold_ms = DEFAULT_HYSTERESIS_THRESHOLD_MS,
            .reserved1 = 0,
            .use_dts_derived_pcr = 1,
            .reserved2 = 0
        };
    }

    /// Get current time in nanoseconds.
    [[nodiscard]] static std::int64_t now_ns() noexcept {
        return std::chrono::duration_cast<std::chrono::nanoseconds>(
            std::chrono::steady_clock::now().time_since_epoch()).count();
    }

    /// Get .NET DateTime.Ticks equivalent.
    [[nodiscard]] static std::int64_t get_dotnet_ticks() noexcept {
        auto sys_now = std::chrono::system_clock::now();
        auto duration = sys_now.time_since_epoch();
        auto ticks = std::chrono::duration_cast<std::chrono::nanoseconds>(duration).count() / 100;
        // .NET epoch: 0001-01-01 00:00:00.0000000 UTC
        constexpr std::int64_t DOTNET_EPOCH_OFFSET = 621355968000000000LL;
        return ticks + DOTNET_EPOCH_OFFSET;
    }

    /// Calculate correction amount based on drift.
    [[nodiscard]] double calculate_correction(double elapsed_sec) noexcept {
        if (config.mode != RESTAMP_MODE_CORRECT) {
            return 0.0;
        }

        if (elapsed_sec < MIN_CORRECTION_INTERVAL_SEC) {
            return 0.0;
        }

        // During warmup, the analyzer uses update_drift_simple() which produces
        // transient peaks from PTS emission timing artifacts. Skip correction
        // until the matched-pair algorithm has enough data to be accurate.
        std::int64_t start = stream_start_time_ns_.load(std::memory_order_relaxed);
        if (start > 0) {
            double stream_age_sec = static_cast<double>(now_ns() - start) / 1e9;
            if (stream_age_sec < CORRECTION_WARMUP_SEC) {
                return 0.0;
            }
        }

        AvSyncAnalysisNative sync_analysis;
        if (!av_sync->get_analysis(&sync_analysis)) {
            return 0.0;
        }

        // The analyzer measures original (uncorrected) timestamps because analysis
        // runs before restamping in the pipeline. avg_drift_ms reflects the SOURCE's
        // real A/V drift, not the corrected output. Compute the residual: the drift
        // that downstream (FFmpeg) still sees after our accumulated correction.
        double source_drift_ms = sync_analysis.avg_drift_ms;
        double acc_correction = accumulated_correction_ms_.load(std::memory_order_relaxed);
        double residual_ms = source_drift_ms - acc_correction;
        double abs_residual = std::abs(residual_ms);

        // Hysteresis on RESIDUAL drift (what downstream actually sees).
        if (correction_active_.load(std::memory_order_relaxed)) {
            if (abs_residual < config.hysteresis_threshold_ms) {
                correction_active_.store(false, std::memory_order_release);
                return 0.0;
            }
        } else {
            if (abs_residual < config.correction_threshold_ms) {
                return 0.0;
            }
            correction_active_.store(true, std::memory_order_release);
            correction_activation_time_ns_.store(now_ns(), std::memory_order_release);
            drift_integral_ms_.store(0.0, std::memory_order_release);
        }

        // PI controller on the residual error.
        // P term drives residual toward zero; I term eliminates steady-state offset.
        double p_term = residual_ms * CORRECTION_RAMP_FACTOR * elapsed_sec;

        double i_term = 0.0;
        std::int64_t activation_time = correction_activation_time_ns_.load(std::memory_order_relaxed);
        double correction_age_sec = static_cast<double>(now_ns() - activation_time) / 1e9;
        if (correction_age_sec >= INTEGRAL_WARMUP_SEC) {
            double integral = drift_integral_ms_.load(std::memory_order_relaxed);
            integral += residual_ms * elapsed_sec;
            constexpr double MAX_INTEGRAL_MS = 500.0;
            integral = std::clamp(integral, -MAX_INTEGRAL_MS, MAX_INTEGRAL_MS);
            drift_integral_ms_.store(integral, std::memory_order_release);
            i_term = CORRECTION_KI * integral;
        }

        double target_correction = p_term + i_term;

        // Clamp to max rate
        double max_correction = config.max_correction_rate_ms * elapsed_sec;
        if (std::abs(target_correction) > max_correction) {
            target_correction = std::copysign(max_correction, target_correction);
        }

        // Keep correction bounded to prevent runaway offsets during unstable sources.
        // Reuse acc_correction from line above to avoid double-load TOCTOU window.
        constexpr double MAX_ACCUMULATED_CORRECTION_MS = 500.0;
        double new_acc = std::clamp(acc_correction + target_correction,
                                     -MAX_ACCUMULATED_CORRECTION_MS,
                                      MAX_ACCUMULATED_CORRECTION_MS);
        accumulated_correction_ms_.store(new_acc, std::memory_order_release);

        return target_correction;
    }

    /// Process PCR in a packet.
    /// @param wall_ns Wall-clock timestamp from process() (reused, no extra syscall)
    /// @param local_pcr_smoothed [out] Batched counter for PCR modifications
    /// @param local_disc_fixed [out] Batched counter for discontinuity flags set
    /// @return 1 if PCR was modified, 0 otherwise
    [[nodiscard]] std::int32_t process_pcr(ts::TSPacket& pkt,
                                           std::int64_t packet_idx,
                                           std::int64_t total_offset_90khz,
                                           std::int64_t wall_ns,
                                           std::int32_t& local_pcr_smoothed,
                                           std::int32_t& local_disc_fixed) noexcept {
        if (!pkt.hasPCR()) {
            return 0;
        }

        std::uint64_t original_pcr = pkt.getPCR();
        if (original_pcr == ts::INVALID_PCR) {
            return 0;
        }

        std::uint16_t pid = pkt.getPID();

        // Handle discontinuity indicator after provider switch
        if (pending_discontinuity_.load(std::memory_order_acquire)) {
            std::int64_t target_pid = discontinuity_pcr_pid_.load(std::memory_order_relaxed);
            if (target_pid < 0 || target_pid == static_cast<std::int64_t>(pid)) {
                pkt.setDiscontinuityIndicator(true);
                pending_discontinuity_.store(false, std::memory_order_release);
                ++local_disc_fixed;
                LOG_DEBUG("Restamper", "Set discontinuity_indicator on PID %u", pid);
            }
        }

        // Capture DTS-PCR offset for DTS-derived PCR (Tvheadend approach)
        if (pkt.hasDTS() && config.use_dts_derived_pcr) {
            std::int64_t dts_27mhz = static_cast<std::int64_t>(pkt.getDTS()) * PCR_TO_90KHZ;
            std::int64_t raw_delta = static_cast<std::int64_t>(original_pcr) - dts_27mhz;

            // Symmetric wraparound handling
            constexpr std::int64_t half_pcr = PCR_WRAPAROUND / 2;
            if (raw_delta > half_pcr) raw_delta -= PCR_WRAPAROUND;
            else if (raw_delta < -half_pcr) raw_delta += PCR_WRAPAROUND;

            if (!dts_pcr_state_.valid.load(std::memory_order_relaxed)) {
                // Store offset and pid before publishing valid=true.
                // The release on valid synchronizes-with acquire on reader,
                // guaranteeing visibility of both preceding relaxed stores.
                dts_pcr_state_.offset_27mhz.store(raw_delta, std::memory_order_relaxed);
                dts_pcr_state_.pid.store(pid, std::memory_order_relaxed);
                dts_pcr_state_.valid.store(true, std::memory_order_release);
            } else {
                // EMA update: tracks VBV buffer fullness changes in VBR streams
                std::int64_t old_offset = dts_pcr_state_.offset_27mhz.load(std::memory_order_relaxed);
                auto new_offset = static_cast<std::int64_t>(
                    0.99 * static_cast<double>(old_offset) +
                    0.01 * static_cast<double>(raw_delta));
                dts_pcr_state_.offset_27mhz.store(new_offset, std::memory_order_release);
            }
        }

        bool derived_from_dts = false;
        std::uint64_t smoothed = calculate_smoothed_pcr(
            pkt, original_pcr, packet_idx, wall_ns, derived_from_dts);

        // Both DTS-derived and EPTLA paths need the switch offset applied.
        // process_pcr() runs BEFORE process_pts_dts(), so DTS is still uncorrected
        // at this point — the switch offset is NOT baked in via DTS.
        std::int64_t total_pcr_offset = total_offset_90khz * PCR_TO_90KHZ;

        if (smoothed != original_pcr || total_pcr_offset != 0) {
            std::int64_t final_pcr = safe_pcr_mod(
                static_cast<std::int64_t>(smoothed) + total_pcr_offset);

            pkt.setPCR(static_cast<std::uint64_t>(final_pcr));
            ++local_pcr_smoothed;
            return 1;
        }

        return 0;
    }

    /// Process PTS/DTS in a packet.
    /// @param local_pts_corrected [out] Batched counter for PTS modifications
    /// @param local_dts_corrected [out] Batched counter for DTS modifications
    /// @return Number of modifications (0, 1, or 2)
    [[nodiscard]] std::int32_t process_pts_dts(ts::TSPacket& pkt,
                                               std::int64_t video_pid,
                                               std::int64_t audio_pid,
                                               std::int64_t switch_offset_90khz,
                                               std::int64_t video_offset_90khz,
                                               std::int64_t audio_offset_90khz,
                                               std::int32_t& local_pts_corrected,
                                               std::int32_t& local_dts_corrected) noexcept {
        std::int32_t mods = 0;
        std::int64_t pid = static_cast<std::int64_t>(pkt.getPID());
        std::int64_t selected_offset_90khz = switch_offset_90khz;

        if (pid == video_pid) {
            selected_offset_90khz = video_offset_90khz;
        } else if (pid == audio_pid) {
            selected_offset_90khz = audio_offset_90khz;
        }

        if (selected_offset_90khz == 0) {
            return 0;
        }

        if (pkt.hasPTS()) {
            std::uint64_t pts = pkt.getPTS();
            if (pts != ts::INVALID_PTS) {
                std::int64_t new_pts = (static_cast<std::int64_t>(pts) + selected_offset_90khz) &
                                      PTS_33BIT_MAX;
                pkt.setPTS(static_cast<std::uint64_t>(new_pts));
                ++local_pts_corrected;
                ++mods;
            }
        }

        if (pkt.hasDTS()) {
            std::uint64_t dts = pkt.getDTS();
            if (dts != ts::INVALID_DTS) {
                std::int64_t new_dts = (static_cast<std::int64_t>(dts) + selected_offset_90khz) &
                                      PTS_33BIT_MAX;
                pkt.setDTS(static_cast<std::uint64_t>(new_dts));
                ++local_dts_corrected;
                ++mods;
            }
        }

        return mods;
    }

    /// Calculate smoothed PCR using EPTLA windowed-minimum clock ratio.
    /// Replaces the old bitrate-estimation + TsDuck pcradjust algorithm with
    /// GStreamer's EPTLA approach: network jitter is one-sided (packets only
    /// arrive late), so the minimum inter-arrival ratio = true clock rate.
    ///
    /// When DTS-derived PCR is available and enabled, it is preferred over EPTLA
    /// because it guarantees PCR-PTS coherence by construction (Tvheadend approach).
    ///
    /// @param pkt          The packet (needed for DTS-derived PCR path)
    /// @param original_pcr The original PCR value from the packet
    /// @param packet_idx   Packet index in the stream
    /// @param wall_ns      Wall-clock timestamp from process()
    /// @param derived_from_dts [out] Set to true if PCR was derived from DTS
    /// @return Smoothed PCR value
    [[nodiscard]] std::uint64_t calculate_smoothed_pcr(
            const ts::TSPacket& pkt,
            std::uint64_t original_pcr,
            std::int64_t packet_idx,
            std::int64_t wall_ns,
            bool& derived_from_dts) noexcept {

        derived_from_dts = false;

        if (config.smooth_pcr == 0) {
            return original_pcr;
        }

        std::uint64_t last_pcr = last_original_pcr_.load(std::memory_order_relaxed);
        std::int64_t last_idx = last_pcr_packet_idx_.load(std::memory_order_relaxed);

        last_original_pcr_.store(original_pcr, std::memory_order_release);
        last_pcr_packet_idx_.store(packet_idx, std::memory_order_release);

        // DTS-derived PCR path: prefer when available (Tvheadend approach).
        // Guarantees PCR-PTS coherence by construction: PCR = corrected_DTS + offset.
        if (config.use_dts_derived_pcr &&
            dts_pcr_state_.valid.load(std::memory_order_acquire) &&
            pkt.hasDTS()) {

            std::int64_t dts_27mhz = static_cast<std::int64_t>(pkt.getDTS()) * PCR_TO_90KHZ;
            std::int64_t offset = dts_pcr_state_.offset_27mhz.load(std::memory_order_relaxed);

            auto derived_pcr = static_cast<std::uint64_t>(
                safe_pcr_mod(dts_27mhz + offset));
            last_smoothed_pcr_.store(derived_pcr, std::memory_order_release);
            pcr_smoothing_delta_90khz_.store(0, std::memory_order_release);
            derived_from_dts = true;

            // Still update EPTLA for potential fallback
            if (last_pcr != INVALID_PCR) {
                // Capture wall delta BEFORE eptla_update updates last_pcr_wall_ns_
                eptla_update(last_pcr, original_pcr, wall_ns);
            } else {
                last_pcr_wall_ns_ = wall_ns;
            }

            return derived_pcr;
        }

        // EPTLA path: wall-clock based smoothing
        if (last_pcr == INVALID_PCR) {
            last_smoothed_pcr_.store(original_pcr, std::memory_order_release);
            last_pcr_wall_ns_ = wall_ns;
            return original_pcr;
        }

        std::int64_t packet_delta = packet_idx - last_idx;
        if (packet_delta <= 0) {
            return original_pcr;
        }

        // Capture wall delta BEFORE eptla_update updates last_pcr_wall_ns_
        std::int64_t wall_delta_ns = (last_pcr_wall_ns_ == 0)
            ? 0
            : wall_ns - last_pcr_wall_ns_;

        eptla_update(last_pcr, original_pcr, wall_ns);

        if (wall_delta_ns <= 0) {
            last_smoothed_pcr_.store(original_pcr, std::memory_order_release);
            return original_pcr;
        }

        // Detect PCR discontinuity - clamp large jumps
        std::int64_t pcr_jump = static_cast<std::int64_t>(original_pcr) -
                               static_cast<std::int64_t>(last_pcr);

        constexpr std::int64_t half_scale = PCR_WRAPAROUND / 2;
        if (pcr_jump > half_scale) {
            pcr_jump -= PCR_WRAPAROUND;
        } else if (pcr_jump < -half_scale) {
            pcr_jump += PCR_WRAPAROUND;
        }

        if (std::abs(pcr_jump) > PCR_JUMP_CLAMP_TICKS) {
            LOG_DEBUG("Restamper",
                     "PCR jump clamp: jump=%lld ticks (%.3fms) -> %.3fms",
                     static_cast<long long>(pcr_jump),
                     static_cast<double>(pcr_jump) / 27000.0,
                     static_cast<double>(PCR_JUMP_CLAMP_TICKS) / 27000.0);
            pcr_jump = (pcr_jump > 0) ? PCR_JUMP_CLAMP_TICKS : -PCR_JUMP_CLAMP_TICKS;
        } else if (std::abs(pcr_jump) > PCR_DISCONTINUITY_THRESHOLD) {
            LOG_DEBUG("Restamper",
                     "PCR discontinuity detected: jump=%lld ticks (%.3fms), continuing",
                     static_cast<long long>(pcr_jump),
                     static_cast<double>(pcr_jump) / 27000.0);
        }

        std::uint64_t adjusted_pcr = static_cast<std::uint64_t>(
            safe_pcr_mod(static_cast<std::int64_t>(last_pcr) + pcr_jump));

        // EPTLA: expected PCR delta from wall-clock and smoothed clock ratio
        double expected_delta_27mhz = static_cast<double>(wall_delta_ns)
            * (static_cast<double>(ts::SYSTEM_CLOCK_FREQ) / 1e9)
            * eptla_skew_ratio_;

        std::uint64_t last_smoothed = last_smoothed_pcr_.load(std::memory_order_relaxed);
        std::uint64_t expected_pcr = (last_smoothed + static_cast<std::uint64_t>(expected_delta_27mhz)) %
                                    static_cast<std::uint64_t>(PCR_WRAPAROUND);

        // Signed difference with wraparound handling
        std::int64_t delta = static_cast<std::int64_t>(adjusted_pcr) -
                            static_cast<std::int64_t>(expected_pcr);
        if (delta > half_scale) {
            delta -= PCR_WRAPAROUND;
        } else if (delta < -half_scale) {
            delta += PCR_WRAPAROUND;
        }

        // EMA smoothing
        double smoothed_d = static_cast<double>(expected_pcr) +
                           (1.0 - PCR_SMOOTHING_FACTOR) * static_cast<double>(delta);

        std::uint64_t smoothed_pcr = static_cast<std::uint64_t>(
            safe_pcr_mod(static_cast<std::int64_t>(smoothed_d)));
        last_smoothed_pcr_.store(smoothed_pcr, std::memory_order_release);

        // Track smoothing delta for metrics visibility
        std::int64_t smoothing_delta = (static_cast<std::int64_t>(smoothed_pcr) -
                                        static_cast<std::int64_t>(adjusted_pcr));
        if (smoothing_delta > half_scale) {
            smoothing_delta -= PCR_WRAPAROUND;
        } else if (smoothing_delta < -half_scale) {
            smoothing_delta += PCR_WRAPAROUND;
        }
        pcr_smoothing_delta_90khz_.store(smoothing_delta / PCR_TO_90KHZ,
                                         std::memory_order_release);

        return smoothed_pcr;
    }

    /// Update EPTLA clock ratio estimate from PCR arrival timing.
    /// Network jitter is one-sided (packets only arrive late), so the
    /// minimum inter-arrival ratio = true clock rate (GStreamer approach).
    /// @param prev_pcr  Previous original PCR value
    /// @param curr_pcr  Current original PCR value
    /// @param wall_ns   Wall-clock timestamp from process()
    void eptla_update(std::uint64_t prev_pcr, std::uint64_t curr_pcr,
                      std::int64_t wall_ns) noexcept {
        // Check for pending reset from handle_switch()/reset()
        if (eptla_reset_pending_.exchange(false, std::memory_order_acquire)) {
            eptla_count_ = 0;
            eptla_head_ = 0;
            eptla_min_idx_ = 0;
            eptla_skew_ratio_ = 1.0;
            eptla_raw_min_ = std::numeric_limits<double>::max();
            eptla_filling_ = true;
            last_pcr_wall_ns_ = wall_ns;
            return;
        }

        if (last_pcr_wall_ns_ == 0 || prev_pcr == INVALID_PCR) {
            last_pcr_wall_ns_ = wall_ns;
            return;
        }

        // Symmetric wraparound handling
        std::int64_t pcr_delta = static_cast<std::int64_t>(curr_pcr) -
                                 static_cast<std::int64_t>(prev_pcr);
        constexpr std::int64_t half_scale = PCR_WRAPAROUND / 2;
        if (pcr_delta > half_scale) {
            pcr_delta -= PCR_WRAPAROUND;
        } else if (pcr_delta < -half_scale) {
            pcr_delta += PCR_WRAPAROUND;
        }
        if (std::abs(pcr_delta) > half_scale) return; // true discontinuity

        std::int64_t wall_delta_ns = wall_ns - last_pcr_wall_ns_;
        if (wall_delta_ns < EPTLA_MIN_WALL_DELTA_NS ||
            wall_delta_ns > EPTLA_MAX_WALL_DELTA_NS) {
            last_pcr_wall_ns_ = wall_ns;
            return;
        }

        // Clock ratio: stream_ticks / wall_ticks (both normalized to 27MHz)
        constexpr double PCR_TICKS_PER_NS =
            static_cast<double>(ts::SYSTEM_CLOCK_FREQ) / 1e9;
        double ratio = static_cast<double>(pcr_delta) /
                       (static_cast<double>(wall_delta_ns) * PCR_TICKS_PER_NS);

        // Reject extreme outliers
        if (ratio < EPTLA_MIN_VALID_RATIO || ratio > EPTLA_MAX_VALID_RATIO) {
            last_pcr_wall_ns_ = wall_ns;
            return;
        }

        // Push into circular window (bitmask for power-of-2 size)
        constexpr std::size_t MASK = EPTLA_WINDOW_SIZE - 1;
        std::size_t evicted_idx = SIZE_MAX;
        std::size_t idx = (eptla_head_ + eptla_count_) & MASK;
        if (eptla_count_ < EPTLA_WINDOW_SIZE) {
            ++eptla_count_;
        } else {
            evicted_idx = eptla_head_;
            eptla_head_ = (eptla_head_ + 1) & MASK;
        }
        eptla_ratios_[idx] = ratio;
        eptla_wall_times_[idx] = wall_ns;

        // Lazy minimum tracking against raw window minimum (not smoothed EMA)
        double min_ratio;
        if (ratio <= eptla_raw_min_) {
            eptla_raw_min_ = ratio;
            min_ratio = ratio;
            eptla_min_idx_ = idx;
        } else if (evicted_idx == eptla_min_idx_) {
            // Evicted the minimum — full rescan needed
            eptla_raw_min_ = std::numeric_limits<double>::max();
            for (std::size_t i = 0; i < eptla_count_; ++i) {
                std::size_t j = (eptla_head_ + i) & MASK;
                if (eptla_ratios_[j] < eptla_raw_min_) {
                    eptla_raw_min_ = eptla_ratios_[j];
                    eptla_min_idx_ = j;
                }
            }
            min_ratio = eptla_raw_min_;
        } else {
            min_ratio = eptla_raw_min_; // unchanged
        }

        // GStreamer-style EMA smoothing
        if (eptla_filling_) {
            // Parabolic startup: more weight to min as window fills
            std::size_t perc = (eptla_count_ * 100) / EPTLA_WINDOW_SIZE;
            perc = perc * perc; // quadratic: 0..10000
            eptla_skew_ratio_ = (static_cast<double>(perc) * min_ratio +
                                 static_cast<double>(10000 - perc) * eptla_skew_ratio_) / 10000.0;
            if (eptla_count_ >= EPTLA_WINDOW_SIZE) {
                eptla_skew_ratio_ = min_ratio;
                eptla_filling_ = false;
            }
        } else {
            // Steady-state: 1/125 EMA weight (from GStreamer rtpjitterbuffer)
            eptla_skew_ratio_ = (min_ratio + 124.0 * eptla_skew_ratio_) / 125.0;
        }

        last_pcr_wall_ns_ = wall_ns;
    }

    // Note: Statistics are batched into a single seqlock write in process().
    // No per-packet increment helpers needed — local counters are flushed
    // after the packet loop to minimize seqlock contention.

    // ========================================================================
    // Member Variables
    // ========================================================================

    // Statistics (seqlock-protected)
    alignas(CACHE_LINE_SIZE) mutable concurrency::Seqlock stats_seqlock_;
    RestampingStatisticsNative stats_{};

    // Correction state
    alignas(CACHE_LINE_SIZE) std::atomic<bool> correction_active_{false};
    alignas(CACHE_LINE_SIZE) std::atomic<std::int64_t> last_correction_time_ns_{0};
    alignas(CACHE_LINE_SIZE) std::atomic<double> accumulated_correction_ms_{0.0};

    // Provider switch handling
    alignas(CACHE_LINE_SIZE) std::atomic<std::int64_t> switch_offset_90khz_{0};
    alignas(CACHE_LINE_SIZE) std::atomic<std::int64_t> switch_gap_90khz_{DEFAULT_SWITCH_GAP_90KHZ};

    // PCR smoothing state
    alignas(CACHE_LINE_SIZE) std::atomic<std::uint64_t> last_smoothed_pcr_{INVALID_PCR};
    alignas(CACHE_LINE_SIZE) std::atomic<std::int64_t> last_pcr_packet_idx_{0};
    alignas(CACHE_LINE_SIZE) std::atomic<std::uint64_t> last_original_pcr_{INVALID_PCR};
    alignas(CACHE_LINE_SIZE) std::atomic<std::int64_t> pcr_smoothing_delta_90khz_{0};

    // EPTLA windowed-minimum state (non-atomic: single-writer in process())
    // Split into parallel arrays for better scan cache utilization
    // (ratio scan only touches 256 bytes = 4 cache lines)
    std::array<double, EPTLA_WINDOW_SIZE> eptla_ratios_{};
    std::array<std::int64_t, EPTLA_WINDOW_SIZE> eptla_wall_times_{}; // diagnostics
    std::size_t eptla_count_{0};
    std::size_t eptla_head_{0};
    std::size_t eptla_min_idx_{0};  // index of current minimum for lazy tracking
    std::int64_t last_pcr_wall_ns_{0};
    double eptla_skew_ratio_{1.0};  // EMA-smoothed minimum ratio
    double eptla_raw_min_{std::numeric_limits<double>::max()};  // raw window minimum
    bool eptla_filling_{true};       // startup phase flag

    // Thread-safe reset flag (set by handle_switch/reset, consumed by process)
    alignas(CACHE_LINE_SIZE) std::atomic<bool> eptla_reset_pending_{false};

    // DTS-derived PCR state (Tvheadend delta-preservation approach)
    // Packed into one cache line — always accessed together
    struct alignas(CACHE_LINE_SIZE) DtsDerivedPcrState {
        std::atomic<std::int64_t> offset_27mhz{0};  // PCR-DTS offset (signed, EMA-updated)
        std::atomic<bool> valid{false};
        std::atomic<std::int64_t> pid{-1};           // PID carrying PCR
    };
    DtsDerivedPcrState dts_pcr_state_;

    // Discontinuity indicator management
    alignas(CACHE_LINE_SIZE) std::atomic<bool> pending_discontinuity_{false};
    alignas(CACHE_LINE_SIZE) std::atomic<std::int64_t> discontinuity_pcr_pid_{-1};

    // Warmup period tracking — skip correction during initial analyzer convergence
    alignas(CACHE_LINE_SIZE) std::atomic<std::int64_t> stream_start_time_ns_{0};

    // PI controller integral term — accumulates drift×time to eliminate steady-state offset
    alignas(CACHE_LINE_SIZE) std::atomic<double> drift_integral_ms_{0.0};
    // Time when correction first activated — gates integral warmup
    alignas(CACHE_LINE_SIZE) std::atomic<std::int64_t> correction_activation_time_ns_{0};

    // Target stream selection for A/V drift correction
    alignas(CACHE_LINE_SIZE) std::atomic<std::int64_t> target_video_pid_{-1};
    alignas(CACHE_LINE_SIZE) std::atomic<std::int64_t> target_audio_pid_{-1};

    // Callback (not currently used, reserved for future)
    std::atomic<TsDuckCorrectionCallback> correction_callback_{nullptr};
    std::atomic<void*> correction_user_data_{nullptr};
};

}  // namespace tsduck_interop::restamping

#endif  // TSDUCK_INTEROP_RESTAMPING_RESTAMPER_HPP
