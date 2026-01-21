// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_RESTAMPING_RESTAMPER_HPP
#define TSDUCK_INTEROP_RESTAMPING_RESTAMPER_HPP

#include <atomic>
#include <cstdint>
#include <cmath>
#include <chrono>
#include "../core/constants.hpp"
#include "../concurrency/seqlock.hpp"
#include "../mpegts/packet_utils.hpp"
#include "../analysis/av_sync_tracker.hpp"
#include "tsduck_interop.h"

namespace tsduck_interop {
namespace restamping {

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

    // Provider switch handling
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> switch_offset_90khz{0};
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> switch_gap_90khz{DEFAULT_SWITCH_GAP_90KHZ};

    // PCR smoothing state
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> last_smoothed_pcr{-1};
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> last_pcr_packet_idx{0};
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> estimated_bitrate{0};
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> last_original_pcr{-1};

    // Callback
    std::atomic<TsDuckCorrectionCallback> correction_callback{nullptr};
    std::atomic<void*> correction_user_data{nullptr};

    Restamper(analysis::AvSyncTracker* tracker, const RestampingConfigNative* cfg)
        : av_sync(tracker) {
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

        double correction_ms = calculateCorrection(elapsed_sec);
        int64_t correction_90khz = static_cast<int64_t>(correction_ms * 90.0);

        int64_t switch_off = switch_offset_90khz.load(std::memory_order_relaxed);
        int64_t total_offset = correction_90khz + switch_off;

        int32_t modifications = 0;
        int32_t packets = length / TS_PACKET_SIZE;

        for (int32_t i = 0; i < packets; i++) {
            uint8_t* packet = data + (i * TS_PACKET_SIZE);

            if (packet[0] != TS_SYNC_BYTE) {
                continue;
            }

            uint8_t adaptation_control = (packet[3] >> 4) & 0x03;
            bool has_adaptation = (adaptation_control & 0x02) != 0;
            bool has_payload = (adaptation_control & 0x01) != 0;

            // Process PCR in adaptation field
            if (has_adaptation && packet[4] > 0) {
                uint8_t adaptation_length = packet[4];
                if (adaptation_length >= 7) {
                    uint8_t flags = packet[5];
                    bool has_pcr = (flags & 0x10) != 0;

                    if (has_pcr) {
                        int64_t original_pcr = mpegts::extractPcrBase(&packet[6]);
                        int64_t smoothed_pcr = calculateSmoothedPcr(
                            original_pcr, base_packet_idx + i);

                        if (smoothed_pcr != original_pcr || total_offset != 0) {
                            int64_t final_pcr = (smoothed_pcr + total_offset) & PTS_33BIT_MAX;
                            mpegts::patchPcr(&packet[6], final_pcr - original_pcr);
                            updateStats(true, false, false);
                            modifications++;
                        }
                    }
                }
            }

            // Process PTS/DTS in PES header
            if (has_payload && (config.mode == RESTAMP_MODE_CORRECT) && total_offset != 0) {
                uint8_t* pes_start = packet + 4;
                if (has_adaptation) {
                    pes_start += 1 + packet[4];
                }

                int bytes_remaining = TS_PACKET_SIZE - static_cast<int>(pes_start - packet);
                if (bytes_remaining >= 14) {
                    // Check for PES start code
                    if (pes_start[0] == 0x00 && pes_start[1] == 0x00 && pes_start[2] == 0x01) {
                        uint8_t stream_id = pes_start[3];
                        bool is_av_stream = (stream_id >= 0xC0 && stream_id <= 0xEF);

                        if (is_av_stream && bytes_remaining >= 9) {
                            uint8_t pts_dts_flags = (pes_start[7] >> 6) & 0x03;
                            uint8_t header_length = pes_start[8];
                            uint8_t* optional_start = &pes_start[9];

                            if (bytes_remaining >= 9 + header_length) {
                                // PTS present
                                if ((pts_dts_flags & 0x02) && header_length >= 5) {
                                    mpegts::patchPts(optional_start, total_offset);
                                    updateStats(false, true, false);
                                    modifications++;

                                    // DTS present
                                    if ((pts_dts_flags & 0x01) && header_length >= 10) {
                                        mpegts::patchPts(optional_start + 5, total_offset);
                                        updateStats(false, false, true);
                                        modifications++;
                                    }
                                }
                            }
                        }
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
            stats.last_correction_time = getDotnetTicks();
        }
        stats_seqlock.end_write(seq);

        return modifications;
    }

    void handleSwitch(int64_t last_output_pts, int64_t new_input_first_pts) noexcept {
        int64_t gap = switch_gap_90khz.load(std::memory_order_relaxed);
        int64_t new_offset = last_output_pts + gap - new_input_first_pts;
        switch_offset_90khz.store(new_offset, std::memory_order_release);
    }

    bool getStatistics(RestampingStatisticsNative* out) const noexcept {
        if (!out) return false;

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
        last_smoothed_pcr.store(-1, std::memory_order_release);
        last_pcr_packet_idx.store(0, std::memory_order_release);
        last_original_pcr.store(-1, std::memory_order_release);

        last_correction_time_ns.store(now_ns(), std::memory_order_release);
    }

    void configure(const RestampingConfigNative* cfg) noexcept {
        if (cfg) {
            config = *cfg;
        }
    }

private:
    static int64_t now_ns() noexcept {
        return std::chrono::duration_cast<std::chrono::nanoseconds>(
            std::chrono::steady_clock::now().time_since_epoch()
        ).count();
    }

    static int64_t getDotnetTicks() noexcept {
        auto sys_now = std::chrono::system_clock::now();
        auto duration = sys_now.time_since_epoch();
        auto ticks = std::chrono::duration_cast<std::chrono::nanoseconds>(duration).count() / 100;
        return ticks + 621355968000000000LL;
    }

    double calculateCorrection(double elapsed_sec) noexcept {
        if (config.mode != RESTAMP_MODE_CORRECT) {
            return 0.0;
        }

        if (elapsed_sec < MIN_CORRECTION_INTERVAL_SEC) {
            return 0.0;
        }

        AvSyncAnalysisNative sync_analysis;
        if (!av_sync->getAnalysis(&sync_analysis)) {
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

    int64_t calculateSmoothedPcr(int64_t original_pcr, int64_t packet_idx) noexcept {
        if (!config.smooth_pcr) {
            return original_pcr;
        }

        int64_t last_pcr = last_original_pcr.load(std::memory_order_relaxed);
        int64_t last_idx = last_pcr_packet_idx.load(std::memory_order_relaxed);
        int64_t bitrate = estimated_bitrate.load(std::memory_order_relaxed);

        last_original_pcr.store(original_pcr, std::memory_order_release);
        last_pcr_packet_idx.store(packet_idx, std::memory_order_release);

        if (last_pcr < 0 || bitrate <= 0) {
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

        double bits_transmitted = static_cast<double>(packet_delta) * TS_PACKET_SIZE * 8.0;
        double expected_delta_90khz = (bits_transmitted / static_cast<double>(bitrate)) * 90000.0;

        int64_t last_smoothed = last_smoothed_pcr.load(std::memory_order_relaxed);
        int64_t expected_pcr = last_smoothed + static_cast<int64_t>(expected_delta_90khz);
        expected_pcr &= PTS_33BIT_MAX;

        int64_t delta = original_pcr - expected_pcr;
        if (delta > PTS_33BIT_MAX / 2) {
            delta -= PTS_33BIT_MAX + 1;
        } else if (delta < -static_cast<int64_t>(PTS_33BIT_MAX) / 2) {
            delta += PTS_33BIT_MAX + 1;
        }

        double smoothed = static_cast<double>(expected_pcr) +
                          (1.0 - PCR_SMOOTHING_FACTOR) * static_cast<double>(delta);

        int64_t smoothed_pcr = static_cast<int64_t>(smoothed) & PTS_33BIT_MAX;
        last_smoothed_pcr.store(smoothed_pcr, std::memory_order_release);

        // Update bitrate estimate
        if (packet_delta > 100) {
            int64_t pcr_delta = original_pcr - last_pcr;
            if (pcr_delta < 0) {
                pcr_delta += PTS_33BIT_MAX + 1;
            }
            if (pcr_delta > 0 && pcr_delta < 90000 * 10) {
                double time_sec = static_cast<double>(pcr_delta) / 90000.0;
                int64_t new_bitrate = static_cast<int64_t>(bits_transmitted / time_sec);

                int64_t old_bitrate = estimated_bitrate.load(std::memory_order_relaxed);
                if (old_bitrate > 0) {
                    new_bitrate = static_cast<int64_t>(
                        0.9 * static_cast<double>(old_bitrate) +
                        0.1 * static_cast<double>(new_bitrate)
                    );
                }
                estimated_bitrate.store(new_bitrate, std::memory_order_release);
            }
        }

        return smoothed_pcr;
    }

    void updateStats(bool pcr, bool pts, bool dts) noexcept {
        auto seq = stats_seqlock.begin_write();
        if (pcr) stats.pcr_smoothed++;
        if (pts) stats.pts_corrected++;
        if (dts) stats.dts_corrected++;
        stats_seqlock.end_write(seq);
    }
};

}  // namespace restamping
}  // namespace tsduck_interop

#endif  // TSDUCK_INTEROP_RESTAMPING_RESTAMPER_HPP
