// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_ANALYSIS_PCR_ANALYZER_HPP
#define TSDUCK_INTEROP_ANALYSIS_PCR_ANALYZER_HPP

#include <atomic>
#include <cstdint>
#include <cmath>
#include "../core/constants.hpp"
#include "../concurrency/seqlock.hpp"
#include "tsduck_interop.h"

namespace tsduck_interop {
namespace analysis {

class alignas(CACHE_LINE_SIZE) PcrAnalyzer {
public:
    PcrAnalysisNative data{};
    concurrency::Seqlock seqlock;

    // Writer state (only accessed by writer thread)
    int64_t last_pcr_value{-1};
    int64_t last_pcr_packet_index{0};
    int64_t last_pcr_time_ns{0};
    int64_t first_pcr_value{-1};
    int64_t first_pcr_time_ns{0};
    double jitter_sum{0.0};
    int64_t jitter_count{0};

    void process(uint64_t pcr_value, int64_t packet_index, int64_t current_time_ns) noexcept {
        auto seq = seqlock.begin_write();

        data.pcr_count++;

        if (last_pcr_value < 0) {
            // First PCR
            last_pcr_value = static_cast<int64_t>(pcr_value);
            last_pcr_packet_index = packet_index;
            last_pcr_time_ns = current_time_ns;
            first_pcr_value = static_cast<int64_t>(pcr_value);
            first_pcr_time_ns = current_time_ns;
            data.pcr_valid_count++;
            seqlock.end_write(seq);
            return;
        }

        // Calculate PCR interval
        int64_t pcr_diff = static_cast<int64_t>(pcr_value) - last_pcr_value;
        if (pcr_diff < 0) {
            pcr_diff += (1LL << 42);  // Handle wraparound
        }

        double interval_ms = static_cast<double>(pcr_diff) / PCR_CLOCK_FREQ * 1000.0;
        int64_t packet_diff = packet_index - last_pcr_packet_index;

        data.pcr_interval_packets = packet_diff;
        data.pcr_interval_ms = interval_ms;

        // Calculate jitter
        double time_diff_us = static_cast<double>(current_time_ns - last_pcr_time_ns) / 1000.0;
        double expected_us = interval_ms * 1000.0;
        double jitter_us = std::abs(time_diff_us - expected_us);

        data.pcr_jitter_us = jitter_us;
        if (jitter_us > data.pcr_jitter_max_us) {
            data.pcr_jitter_max_us = jitter_us;
        }

        // Update rolling average
        jitter_sum += jitter_us;
        jitter_count++;
        data.pcr_jitter_avg_us = jitter_sum / static_cast<double>(jitter_count);

        // Validate PCR
        if (interval_ms > 0 && interval_ms < 1000) {
            data.pcr_valid_count++;
        }

        // Calculate drift
        if (first_pcr_value >= 0) {
            double total_system_us = static_cast<double>(current_time_ns - first_pcr_time_ns) / 1000.0;
            if (total_system_us > 1000000.0) {  // After 1 second
                int64_t total_pcr_diff = static_cast<int64_t>(pcr_value) - first_pcr_value;
                if (total_pcr_diff < 0) {
                    total_pcr_diff += (1LL << 42);
                }
                double expected_pcr_ticks = total_system_us * PCR_CLOCK_FREQ / 1000000.0;
                double drift_ratio = (static_cast<double>(total_pcr_diff) - expected_pcr_ticks) / expected_pcr_ticks;
                data.pcr_drift_ppm = drift_ratio * 1000000.0;
            }
        }

        // Update tracking
        last_pcr_value = static_cast<int64_t>(pcr_value);
        last_pcr_packet_index = packet_index;
        last_pcr_time_ns = current_time_ns;

        seqlock.end_write(seq);
    }

    bool get(PcrAnalysisNative* out) const noexcept {
        if (!out) return false;

        uint64_t seq;
        do {
            seq = seqlock.begin_read();
            *out = data;
        } while (!seqlock.read_consistent(seq));

        return out->pcr_count > 0;
    }

    void reset() noexcept {
        auto seq = seqlock.begin_write();
        data = PcrAnalysisNative{};
        last_pcr_value = -1;
        last_pcr_packet_index = 0;
        first_pcr_value = -1;
        jitter_sum = 0.0;
        jitter_count = 0;
        seqlock.end_write(seq);
    }

    int64_t lastPcrBase90khz() const noexcept {
        // Convert from 27MHz to 90kHz (divide by 300)
        int64_t pcr = last_pcr_value;
        return pcr >= 0 ? pcr / 300 : -1;
    }
};

}  // namespace analysis
}  // namespace tsduck_interop

#endif  // TSDUCK_INTEROP_ANALYSIS_PCR_ANALYZER_HPP
