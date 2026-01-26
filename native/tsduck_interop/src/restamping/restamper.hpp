// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_RESTAMPING_RESTAMPER_HPP
#define TSDUCK_INTEROP_RESTAMPING_RESTAMPER_HPP

#include <atomic>
#include <cstdint>
#include <cmath>
#include <chrono>
#include "../core/constants.hpp"
#include "../core/logging.hpp"
#include "../concurrency/seqlock.hpp"
#include "../analysis/av_sync_tracker.hpp"
#include "tsduck_interop.h"

namespace tsduck_interop::restamping {

class alignas(CACHE_LINE_SIZE) Restamper {
public:
    RestampingConfigNative config;
    analysis::AvSyncTracker* av_sync;

    // Statistics
    alignas(CACHE_LINE_SIZE) RestampingStatisticsNative stats{};
    concurrency::Seqlock stats_seqlock;

    // Correction state
    alignas(CACHE_LINE_SIZE) std::atomic<double> current_offset_ms{0.0};
    alignas(CACHE_LINE_SIZE) std::atomic<bool> correction_active{false};
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> last_correction_time_ns{0};
    alignas(CACHE_LINE_SIZE) std::atomic<double> accumulated_correction_ms{0.0};

    // Provider switch handling (stored in 90kHz for PTS/DTS compatibility)
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> switch_offset_90khz{0};
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> switch_gap_90khz{DEFAULT_SWITCH_GAP_90KHZ};

    // PCR smoothing state (full 42-bit PCR at 27MHz via TsDuck)
    alignas(CACHE_LINE_SIZE) std::atomic<uint64_t> last_smoothed_pcr{UINT64_MAX};
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> last_pcr_packet_idx{0};
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> estimated_bitrate{0};
    alignas(CACHE_LINE_SIZE) std::atomic<uint64_t> last_original_pcr{UINT64_MAX};

    // Callback
    std::atomic<TsDuckCorrectionCallback> correction_callback{nullptr};
    std::atomic<void*> correction_user_data{nullptr};

    Restamper(analysis::AvSyncTracker* tracker, const RestampingConfigNative* cfg) : av_sync(tracker) {
        if (cfg) {
            config = *cfg;
        } else {
            config.mode = RESTAMP_MODE_MONITOR;
            config.smooth_pcr = 1;
            config.fix_discontinuities = 1;
            config.reserved = 0;
            config.correction_threshold_ms = DEFAULT_CORRECTION_THRESHOLD_MS;
            config.max_correction_rate_ms = DEFAULT_MAX_CORRECTION_RATE_MS;
            config.hysteresis_threshold_ms = DEFAULT_HYSTERESIS_THRESHOLD_MS;
            config.stream_bitrate_hint = 0;
        }

        last_correction_time_ns.store(now_ns(), std::memory_order_release);
    }

    int32_t process(uint8_t* data, int32_t length, int64_t base_packet_idx) noexcept {
        if (!data || length <= 0 || config.mode == RESTAMP_MODE_DISABLED) {
            return 0;
        }

        int64_t current_time = now_ns();
        int64_t last_time = last_correction_time_ns.load(std::memory_order_relaxed);
        double elapsed_sec = static_cast<double>(current_time - last_time) / 1e9;

        double correction_ms = calculate_correction(elapsed_sec);
        int64_t correction_90khz = static_cast<int64_t>(correction_ms * 90.0);

        int64_t switch_off = switch_offset_90khz.load(std::memory_order_relaxed);
        int64_t total_offset_90khz = correction_90khz + switch_off;

        int32_t modifications = 0;

        int32_t packets = length / static_cast<int32_t>(ts::PKT_SIZE);
        ts::TSPacket* pkt_array = reinterpret_cast<ts::TSPacket*>(data);

        for (int32_t i = 0; i < packets; i++) {
            ts::TSPacket& pkt = pkt_array[i];

            if (!pkt.hasValidSync()) {
                continue;
            }

            // PCR: full 42-bit value at 27MHz via TsDuck
            if (pkt.hasPCR()) {
                uint64_t original_pcr = pkt.getPCR();
                if (original_pcr != ts::INVALID_PCR) {
                    uint64_t smoothed = calculate_smoothed_pcr(original_pcr, base_packet_idx + i);

                    // Convert offsets from 90kHz to 27MHz (multiply by subfactor 300)
                    int64_t total_pcr_offset = total_offset_90khz * static_cast<int64_t>(ts::SYSTEM_CLOCK_SUBFACTOR);

                    if (smoothed != original_pcr || total_pcr_offset != 0) {
                        int64_t final_pcr = static_cast<int64_t>(smoothed) + total_pcr_offset;
                        // Wrap within valid PCR range [0, PCR_SCALE)
                        while (final_pcr < 0) {
                            final_pcr += static_cast<int64_t>(ts::PCR_SCALE);
                        }
                        final_pcr %= static_cast<int64_t>(ts::PCR_SCALE);
                        pkt.setPCR(static_cast<uint64_t>(final_pcr));
                        update_stats(true, false, false);
                        modifications++;
                    }
                }
            }

            // PTS/DTS: 33-bit values at 90kHz via TsDuck
            if ((config.mode == RESTAMP_MODE_CORRECT) && total_offset_90khz != 0) {
                if (pkt.hasPTS()) {
                    uint64_t pts = pkt.getPTS();
                    if (pts != ts::INVALID_PTS) {
                        int64_t new_pts = (static_cast<int64_t>(pts) + total_offset_90khz) & PTS_33BIT_MAX;
                        pkt.setPTS(static_cast<uint64_t>(new_pts));
                        update_stats(false, true, false);
                        modifications++;
                    }
                }
                if (pkt.hasDTS()) {
                    uint64_t dts = pkt.getDTS();
                    if (dts != ts::INVALID_DTS) {
                        int64_t new_dts = (static_cast<int64_t>(dts) + total_offset_90khz) & PTS_33BIT_MAX;
                        pkt.setDTS(static_cast<uint64_t>(new_dts));
                        update_stats(false, false, true);
                        modifications++;
                    }
                }
            }
        }

        // Update timing
        if (elapsed_sec >= MIN_CORRECTION_INTERVAL_SEC) {
            last_correction_time_ns.store(current_time, std::memory_order_release);
        }

        // Update packet count
        auto seq = stats_seqlock.begin_write();
        stats.packets_processed += packets;
        if (std::abs(correction_ms) > 0.001) {
            stats.total_correction_ms += correction_ms;
            stats.current_offset_ms = accumulated_correction_ms.load(std::memory_order_relaxed);
            stats.correction_active = correction_active.load(std::memory_order_relaxed) ? 1 : 0;
            stats.last_correction_time = get_dotnet_ticks();
        }
        stats_seqlock.end_write(seq);

        return modifications;
    }

    void handle_switch(int64_t last_output_pts, int64_t new_input_first_pts) noexcept {
        int64_t gap = switch_gap_90khz.load(std::memory_order_relaxed);
        int64_t new_offset = last_output_pts + gap - new_input_first_pts;
        switch_offset_90khz.store(new_offset, std::memory_order_release);
    }

    bool get_statistics(RestampingStatisticsNative* out) const noexcept {
        if (!out)
            return false;

        uint64_t seq;
        do {
            seq = stats_seqlock.begin_read();
            *out = stats;
        } while (!stats_seqlock.read_consistent(seq));

        return true;
    }

    void reset() noexcept {
        auto seq = stats_seqlock.begin_write();
        stats = RestampingStatisticsNative{};
        stats_seqlock.end_write(seq);

        current_offset_ms.store(0.0, std::memory_order_release);
        correction_active.store(false, std::memory_order_release);
        accumulated_correction_ms.store(0.0, std::memory_order_release);
        switch_offset_90khz.store(0, std::memory_order_release);
        last_smoothed_pcr.store(UINT64_MAX, std::memory_order_release);
        last_pcr_packet_idx.store(0, std::memory_order_release);
        last_original_pcr.store(UINT64_MAX, std::memory_order_release);

        last_correction_time_ns.store(now_ns(), std::memory_order_release);
    }

    void configure(const RestampingConfigNative* cfg) noexcept {
        if (cfg) {
            config = *cfg;
        }
    }

private:
    static int64_t now_ns() noexcept {
        return std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch())
            .count();
    }

    static int64_t get_dotnet_ticks() noexcept {
        auto sys_now = std::chrono::system_clock::now();
        auto duration = sys_now.time_since_epoch();
        auto ticks = std::chrono::duration_cast<std::chrono::nanoseconds>(duration).count() / 100;
        return ticks + 621355968000000000LL;
    }

    double calculate_correction(double elapsed_sec) noexcept {
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

        if (correction_active.load(std::memory_order_relaxed)) {
            if (abs_drift < config.hysteresis_threshold_ms) {
                correction_active.store(false, std::memory_order_release);
                accumulated_correction_ms.store(0.0, std::memory_order_release);
                return 0.0;
            }
        } else {
            if (abs_drift < config.correction_threshold_ms) {
                return 0.0;
            }
            correction_active.store(true, std::memory_order_release);
        }

        double correction_per_sec = drift_ms * CORRECTION_RAMP_FACTOR;
        double target_correction = correction_per_sec * elapsed_sec;

        double max_correction = config.max_correction_rate_ms * elapsed_sec;
        if (std::abs(target_correction) > max_correction) {
            target_correction = (target_correction > 0) ? max_correction : -max_correction;
        }

        double acc = accumulated_correction_ms.load(std::memory_order_relaxed);
        accumulated_correction_ms.store(acc + target_correction, std::memory_order_release);

        return target_correction;
    }

    /// PCR smoothing using TsDuck's pcradjust algorithm.
    /// Works with full 42-bit PCR values at 27MHz using TsDuck constants.
    uint64_t calculate_smoothed_pcr(uint64_t original_pcr, int64_t packet_idx) noexcept {
        if (!config.smooth_pcr) {
            return original_pcr;
        }

        uint64_t last_pcr = last_original_pcr.load(std::memory_order_relaxed);
        int64_t last_idx = last_pcr_packet_idx.load(std::memory_order_relaxed);
        int64_t bitrate = estimated_bitrate.load(std::memory_order_relaxed);

        last_original_pcr.store(original_pcr, std::memory_order_release);
        last_pcr_packet_idx.store(packet_idx, std::memory_order_release);

        if (last_pcr == UINT64_MAX || bitrate <= 0) {
            if (config.stream_bitrate_hint > 0) {
                estimated_bitrate.store(config.stream_bitrate_hint, std::memory_order_release);
            }
            last_smoothed_pcr.store(original_pcr, std::memory_order_release);
            return original_pcr;
        }

        int64_t packet_delta = packet_idx - last_idx;
        if (packet_delta <= 0) {
            return original_pcr;
        }

        // Detect PCR discontinuity: if the original PCR jumped significantly,
        // reset smoothing state to prevent gradual drift that freezes video.
        // A discontinuity is typically > 100ms (2.7M ticks at 27MHz).
        constexpr int64_t PCR_DISCONTINUITY_THRESHOLD = 27000000 / 10;  // 100ms
        int64_t pcr_jump = static_cast<int64_t>(original_pcr) - static_cast<int64_t>(last_pcr);
        // Handle wraparound
        if (pcr_jump > static_cast<int64_t>(ts::PCR_SCALE / 2)) {
            pcr_jump -= static_cast<int64_t>(ts::PCR_SCALE);
        } else if (pcr_jump < -static_cast<int64_t>(ts::PCR_SCALE / 2)) {
            pcr_jump += static_cast<int64_t>(ts::PCR_SCALE);
        }
        if (std::abs(pcr_jump) > PCR_DISCONTINUITY_THRESHOLD) {
            // Large PCR jump detected - reset smoothing to follow source
            LOG_DEBUG("Restamper", "PCR discontinuity detected: jump=%lld ticks (%.3fms), resetting smoothing",
                      static_cast<long long>(pcr_jump), static_cast<double>(pcr_jump) / 27000.0);
            last_smoothed_pcr.store(original_pcr, std::memory_order_release);
            return original_pcr;
        }

        // TsDuck pcradjust formula:
        // next_pcr = last_pcr + (delta * PKT_SIZE_BITS * SYSTEM_CLOCK_FREQ) / bitrate
        double bits_transmitted = static_cast<double>(packet_delta) * static_cast<double>(ts::PKT_SIZE_BITS);
        double expected_delta_27mhz =
            (bits_transmitted / static_cast<double>(bitrate)) * static_cast<double>(ts::SYSTEM_CLOCK_FREQ);

        uint64_t last_smoothed = last_smoothed_pcr.load(std::memory_order_relaxed);
        uint64_t expected_pcr = last_smoothed + static_cast<uint64_t>(expected_delta_27mhz);
        expected_pcr %= ts::PCR_SCALE;

        // Compute signed difference with wraparound handling
        int64_t delta = static_cast<int64_t>(original_pcr) - static_cast<int64_t>(expected_pcr);
        if (delta > static_cast<int64_t>(ts::PCR_SCALE / 2)) {
            delta -= static_cast<int64_t>(ts::PCR_SCALE);
        } else if (delta < -static_cast<int64_t>(ts::PCR_SCALE / 2)) {
            delta += static_cast<int64_t>(ts::PCR_SCALE);
        }

        // EMA smoothing: blend expected with actual
        double smoothed_d =
            static_cast<double>(expected_pcr) + (1.0 - PCR_SMOOTHING_FACTOR) * static_cast<double>(delta);

        int64_t smoothed_signed = static_cast<int64_t>(smoothed_d);
        while (smoothed_signed < 0) {
            smoothed_signed += static_cast<int64_t>(ts::PCR_SCALE);
        }
        uint64_t smoothed_pcr = static_cast<uint64_t>(smoothed_signed) % ts::PCR_SCALE;
        last_smoothed_pcr.store(smoothed_pcr, std::memory_order_release);

        // Update bitrate estimate from PCR deltas (27MHz domain)
        if (packet_delta > 100) {
            int64_t pcr_delta = static_cast<int64_t>(original_pcr) - static_cast<int64_t>(last_pcr);
            if (pcr_delta < 0) {
                pcr_delta += static_cast<int64_t>(ts::PCR_SCALE);
            }
            // Sanity: PCR delta should correspond to < 10 seconds
            if (pcr_delta > 0 && pcr_delta < static_cast<int64_t>(ts::SYSTEM_CLOCK_FREQ) * 10) {
                double time_sec = static_cast<double>(pcr_delta) / static_cast<double>(ts::SYSTEM_CLOCK_FREQ);
                int64_t new_bitrate = static_cast<int64_t>(bits_transmitted / time_sec);

                int64_t old_bitrate = estimated_bitrate.load(std::memory_order_relaxed);
                if (old_bitrate > 0) {
                    new_bitrate = static_cast<int64_t>(0.9 * static_cast<double>(old_bitrate) +
                                                       0.1 * static_cast<double>(new_bitrate));
                }
                estimated_bitrate.store(new_bitrate, std::memory_order_release);
            }
        }

        return smoothed_pcr;
    }

    void update_stats(bool pcr, bool pts, bool dts) noexcept {
        auto seq = stats_seqlock.begin_write();
        if (pcr)
            stats.pcr_smoothed++;
        if (pts)
            stats.pts_corrected++;
        if (dts)
            stats.dts_corrected++;
        stats_seqlock.end_write(seq);
    }
};

}  // namespace tsduck_interop::restamping

#endif  // TSDUCK_INTEROP_RESTAMPING_RESTAMPER_HPP
