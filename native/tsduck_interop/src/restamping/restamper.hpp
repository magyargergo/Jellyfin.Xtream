// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_RESTAMPING_RESTAMPER_HPP
#define TSDUCK_INTEROP_RESTAMPING_RESTAMPER_HPP

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
        // Switch offset applies to all streams for continuity across provider changes.
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
            modifications += process_pcr(pkt, packet_idx, switch_off_90khz);

            // Process PTS/DTS
            if (config.mode == RESTAMP_MODE_CORRECT &&
                (switch_off_90khz != 0 || accumulated_offset_90khz != 0)) {
                modifications += process_pts_dts(
                    pkt,
                    video_pid,
                    audio_pid,
                    switch_off_90khz,
                    video_total_offset_90khz,
                    audio_total_offset_90khz
                );
            }
        }

        // Update timing
        if (elapsed_sec >= MIN_CORRECTION_INTERVAL_SEC) {
            last_correction_time_ns_.store(current_time, std::memory_order_release);
        }

        // Update statistics
        update_statistics(packets, correction_ms);

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

        // Reset smoothing state for fresh start from new source
        last_smoothed_pcr_.store(INVALID_PCR, std::memory_order_release);
        last_original_pcr_.store(INVALID_PCR, std::memory_order_release);

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

        current_offset_ms_.store(0.0, std::memory_order_release);
        correction_active_.store(false, std::memory_order_release);
        accumulated_correction_ms_.store(0.0, std::memory_order_release);
        switch_offset_90khz_.store(0, std::memory_order_release);
        last_smoothed_pcr_.store(INVALID_PCR, std::memory_order_release);
        last_pcr_packet_idx_.store(0, std::memory_order_release);
        last_original_pcr_.store(INVALID_PCR, std::memory_order_release);
        pending_discontinuity_.store(false, std::memory_order_release);
        discontinuity_pcr_pid_.store(-1, std::memory_order_release);
        target_video_pid_.store(-1, std::memory_order_release);
        target_audio_pid_.store(-1, std::memory_order_release);

        last_correction_time_ns_.store(now_ns(), std::memory_order_release);
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
            .stream_bitrate_hint = 0
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

        AvSyncAnalysisNative sync_analysis;
        if (!av_sync->get_analysis(&sync_analysis)) {
            return 0.0;
        }

        double drift_ms = sync_analysis.video_audio_drift_ms;
        double abs_drift = std::abs(drift_ms);

        // Hysteresis logic
        if (correction_active_.load(std::memory_order_relaxed)) {
            if (abs_drift < config.hysteresis_threshold_ms) {
                correction_active_.store(false, std::memory_order_release);
                accumulated_correction_ms_.store(0.0, std::memory_order_release);
                return 0.0;
            }
        } else {
            if (abs_drift < config.correction_threshold_ms) {
                return 0.0;
            }
            correction_active_.store(true, std::memory_order_release);
        }

        // Calculate ramped correction
        double correction_per_sec = drift_ms * CORRECTION_RAMP_FACTOR;
        double target_correction = correction_per_sec * elapsed_sec;

        // Clamp to max rate
        double max_correction = config.max_correction_rate_ms * elapsed_sec;
        if (std::abs(target_correction) > max_correction) {
            target_correction = std::copysign(max_correction, target_correction);
        }

        // Keep correction bounded to prevent runaway offsets during unstable sources.
        constexpr double MAX_ACCUMULATED_CORRECTION_MS = 500.0;
        double acc = accumulated_correction_ms_.load(std::memory_order_relaxed);
        acc += target_correction;
        if (acc > MAX_ACCUMULATED_CORRECTION_MS) {
            acc = MAX_ACCUMULATED_CORRECTION_MS;
        } else if (acc < -MAX_ACCUMULATED_CORRECTION_MS) {
            acc = -MAX_ACCUMULATED_CORRECTION_MS;
        }
        accumulated_correction_ms_.store(acc, std::memory_order_release);

        return target_correction;
    }

    /// Process PCR in a packet.
    /// @return 1 if PCR was modified, 0 otherwise
    [[nodiscard]] std::int32_t process_pcr(ts::TSPacket& pkt,
                                           std::int64_t packet_idx,
                                           std::int64_t total_offset_90khz) noexcept {
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
                increment_discontinuities_fixed();
                LOG_DEBUG("Restamper", "Set discontinuity_indicator on PID %u", pid);
            }
        }

        std::uint64_t smoothed = calculate_smoothed_pcr(original_pcr, packet_idx);

        // Convert offset from 90kHz to 27MHz
        std::int64_t total_pcr_offset = total_offset_90khz * PCR_TO_90KHZ;

        if (smoothed != original_pcr || total_pcr_offset != 0) {
            std::int64_t final_pcr = static_cast<std::int64_t>(smoothed) + total_pcr_offset;

            // Wrap within valid PCR range [0, PCR_SCALE)
            while (final_pcr < 0) {
                final_pcr += PCR_WRAPAROUND;
            }
            final_pcr %= PCR_WRAPAROUND;

            pkt.setPCR(static_cast<std::uint64_t>(final_pcr));
            increment_pcr_smoothed();
            return 1;
        }

        return 0;
    }

    /// Process PTS/DTS in a packet.
    /// @return Number of modifications (0, 1, or 2)
    [[nodiscard]] std::int32_t process_pts_dts(ts::TSPacket& pkt,
                                               std::int64_t video_pid,
                                               std::int64_t audio_pid,
                                               std::int64_t switch_offset_90khz,
                                               std::int64_t video_offset_90khz,
                                               std::int64_t audio_offset_90khz) noexcept {
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
                increment_pts_corrected();
                ++mods;
            }
        }

        if (pkt.hasDTS()) {
            std::uint64_t dts = pkt.getDTS();
            if (dts != ts::INVALID_DTS) {
                std::int64_t new_dts = (static_cast<std::int64_t>(dts) + selected_offset_90khz) &
                                      PTS_33BIT_MAX;
                pkt.setDTS(static_cast<std::uint64_t>(new_dts));
                increment_dts_corrected();
                ++mods;
            }
        }

        return mods;
    }

    /// Calculate smoothed PCR using TsDuck's pcradjust algorithm.
    [[nodiscard]] std::uint64_t calculate_smoothed_pcr(std::uint64_t original_pcr,
                                                       std::int64_t packet_idx) noexcept {
        if (config.smooth_pcr == 0) {
            return original_pcr;
        }

        std::uint64_t last_pcr = last_original_pcr_.load(std::memory_order_relaxed);
        std::int64_t last_idx = last_pcr_packet_idx_.load(std::memory_order_relaxed);
        std::int64_t bitrate = estimated_bitrate_.load(std::memory_order_relaxed);

        last_original_pcr_.store(original_pcr, std::memory_order_release);
        last_pcr_packet_idx_.store(packet_idx, std::memory_order_release);

        if (last_pcr == INVALID_PCR || bitrate <= 0) {
            if (config.stream_bitrate_hint > 0) {
                estimated_bitrate_.store(config.stream_bitrate_hint, std::memory_order_release);
            }
            last_smoothed_pcr_.store(original_pcr, std::memory_order_release);
            return original_pcr;
        }

        std::int64_t packet_delta = packet_idx - last_idx;
        if (packet_delta <= 0) {
            return original_pcr;
        }

        // Detect PCR discontinuity - reset smoothing if large jump detected
        std::int64_t pcr_jump = static_cast<std::int64_t>(original_pcr) -
                               static_cast<std::int64_t>(last_pcr);

        // Handle wraparound
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
                     "PCR discontinuity detected: jump=%lld ticks (%.3fms), continuing with smoothing",
                     static_cast<long long>(pcr_jump),
                     static_cast<double>(pcr_jump) / 27000.0);
        }

        std::int64_t adjusted_pcr_signed = static_cast<std::int64_t>(last_pcr) + pcr_jump;
        while (adjusted_pcr_signed < 0) {
            adjusted_pcr_signed += PCR_WRAPAROUND;
        }
        adjusted_pcr_signed %= PCR_WRAPAROUND;
        std::uint64_t adjusted_pcr = static_cast<std::uint64_t>(adjusted_pcr_signed);

        // TsDuck pcradjust formula
        double bits_transmitted =
            static_cast<double>(packet_delta) * static_cast<double>(TS_PACKET_SIZE_BITS);
        double expected_delta_27mhz =
            (bits_transmitted / static_cast<double>(bitrate)) *
            static_cast<double>(ts::SYSTEM_CLOCK_FREQ);

        std::uint64_t last_smoothed = last_smoothed_pcr_.load(std::memory_order_relaxed);
        std::uint64_t expected_pcr = (last_smoothed + static_cast<std::uint64_t>(expected_delta_27mhz)) %
                                    static_cast<std::uint64_t>(PCR_WRAPAROUND);

        // Compute signed difference with wraparound handling
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

        std::int64_t smoothed_signed = static_cast<std::int64_t>(smoothed_d);
        while (smoothed_signed < 0) {
            smoothed_signed += PCR_WRAPAROUND;
        }
        std::uint64_t smoothed_pcr = static_cast<std::uint64_t>(smoothed_signed) %
                                    static_cast<std::uint64_t>(PCR_WRAPAROUND);
        last_smoothed_pcr_.store(smoothed_pcr, std::memory_order_release);

        // Update bitrate estimate from PCR deltas
        if (packet_delta > 100) {
            update_bitrate_estimate(adjusted_pcr, last_pcr, bits_transmitted);
        }

        return smoothed_pcr;
    }

    /// Update bitrate estimate from PCR timing.
    void update_bitrate_estimate(std::uint64_t current_pcr, std::uint64_t last_pcr,
                                double bits_transmitted) noexcept {
        std::int64_t pcr_delta = static_cast<std::int64_t>(current_pcr) -
                                static_cast<std::int64_t>(last_pcr);
        if (pcr_delta < 0) {
            pcr_delta += PCR_WRAPAROUND;
        }

        // Sanity check: PCR delta should be less than 10 seconds
        constexpr std::int64_t MAX_PCR_DELTA = static_cast<std::int64_t>(ts::SYSTEM_CLOCK_FREQ) * 10;
        if (pcr_delta <= 0 || pcr_delta >= MAX_PCR_DELTA) {
            return;
        }

        double time_sec = static_cast<double>(pcr_delta) /
                         static_cast<double>(ts::SYSTEM_CLOCK_FREQ);
        std::int64_t new_bitrate = static_cast<std::int64_t>(bits_transmitted / time_sec);

        std::int64_t old_bitrate = estimated_bitrate_.load(std::memory_order_relaxed);
        if (old_bitrate > 0) {
            // Exponential moving average
            new_bitrate = static_cast<std::int64_t>(
                0.9 * static_cast<double>(old_bitrate) +
                0.1 * static_cast<double>(new_bitrate));
        }
        estimated_bitrate_.store(new_bitrate, std::memory_order_release);
    }

    /// Update statistics with packets processed and correction applied.
    void update_statistics(std::int32_t packets, double correction_ms) noexcept {
        auto guard = stats_seqlock_.write_guard();
        stats_.packets_processed += packets;

        if (std::abs(correction_ms) > 0.001) {
            stats_.total_correction_ms += correction_ms;
            stats_.current_offset_ms = accumulated_correction_ms_.load(std::memory_order_relaxed);
            stats_.correction_active = correction_active_.load(std::memory_order_relaxed) ? 1 : 0;
            stats_.last_correction_time = get_dotnet_ticks();
        }
    }

    // Statistics increment helpers
    void increment_pcr_smoothed() noexcept {
        auto guard = stats_seqlock_.write_guard();
        ++stats_.pcr_smoothed;
    }

    void increment_pts_corrected() noexcept {
        auto guard = stats_seqlock_.write_guard();
        ++stats_.pts_corrected;
    }

    void increment_dts_corrected() noexcept {
        auto guard = stats_seqlock_.write_guard();
        ++stats_.dts_corrected;
    }

    void increment_discontinuities_fixed() noexcept {
        auto guard = stats_seqlock_.write_guard();
        ++stats_.discontinuities_fixed;
    }

    // ========================================================================
    // Member Variables
    // ========================================================================

    // Statistics (seqlock-protected)
    alignas(CACHE_LINE_SIZE) mutable concurrency::Seqlock stats_seqlock_;
    RestampingStatisticsNative stats_{};

    // Correction state
    alignas(CACHE_LINE_SIZE) std::atomic<double> current_offset_ms_{0.0};
    alignas(CACHE_LINE_SIZE) std::atomic<bool> correction_active_{false};
    alignas(CACHE_LINE_SIZE) std::atomic<std::int64_t> last_correction_time_ns_{0};
    alignas(CACHE_LINE_SIZE) std::atomic<double> accumulated_correction_ms_{0.0};

    // Provider switch handling
    alignas(CACHE_LINE_SIZE) std::atomic<std::int64_t> switch_offset_90khz_{0};
    alignas(CACHE_LINE_SIZE) std::atomic<std::int64_t> switch_gap_90khz_{DEFAULT_SWITCH_GAP_90KHZ};

    // PCR smoothing state
    alignas(CACHE_LINE_SIZE) std::atomic<std::uint64_t> last_smoothed_pcr_{INVALID_PCR};
    alignas(CACHE_LINE_SIZE) std::atomic<std::int64_t> last_pcr_packet_idx_{0};
    alignas(CACHE_LINE_SIZE) std::atomic<std::int64_t> estimated_bitrate_{0};
    alignas(CACHE_LINE_SIZE) std::atomic<std::uint64_t> last_original_pcr_{INVALID_PCR};

    // Discontinuity indicator management
    alignas(CACHE_LINE_SIZE) std::atomic<bool> pending_discontinuity_{false};
    alignas(CACHE_LINE_SIZE) std::atomic<std::int64_t> discontinuity_pcr_pid_{-1};

    // Target stream selection for A/V drift correction
    alignas(CACHE_LINE_SIZE) std::atomic<std::int64_t> target_video_pid_{-1};
    alignas(CACHE_LINE_SIZE) std::atomic<std::int64_t> target_audio_pid_{-1};

    // Callback (not currently used, reserved for future)
    std::atomic<TsDuckCorrectionCallback> correction_callback_{nullptr};
    std::atomic<void*> correction_user_data_{nullptr};
};

}  // namespace tsduck_interop::restamping

#endif  // TSDUCK_INTEROP_RESTAMPING_RESTAMPER_HPP
