// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_ANALYSIS_IAT_ANALYZER_HPP
#define TSDUCK_INTEROP_ANALYSIS_IAT_ANALYZER_HPP

#include <atomic>
#include <cstdint>
#include <cmath>
#include "../core/constants.hpp"
#include "../concurrency/seqlock.hpp"
#include "../concurrency/ring_buffer.hpp"
#include "tsduck_interop.h"

namespace tsduck_interop {
namespace analysis {

class alignas(CACHE_LINE_SIZE) IatAnalyzer {
public:
    IatAnalysisNative data{};
    concurrency::Seqlock seqlock;

    // Writer state
    concurrency::LockFreeRingBuffer<double, IAT_SAMPLE_WINDOW> samples;
    int64_t last_packet_time_ns{0};
    double sum{0.0};
    double sum_sq{0.0};
    int64_t count{0};
    bool first_packet{true};

    void process(int64_t current_time_ns) noexcept {
        auto seq = seqlock.begin_write();

        if (first_packet) {
            first_packet = false;
            last_packet_time_ns = current_time_ns;
            seqlock.end_write(seq);
            return;
        }

        // Calculate IAT in microseconds
        double iat_us = static_cast<double>(current_time_ns - last_packet_time_ns) / 1000.0;
        last_packet_time_ns = current_time_ns;

        // Update statistics
        count++;
        sum += iat_us;
        sum_sq += iat_us * iat_us;

        // Add to ring buffer
        samples.push(iat_us);

        // Calculate statistics
        size_t window_size = samples.size();
        if (window_size > 0) {
            data.iat_avg_us = sum / static_cast<double>(count);

            auto [min_val, max_val] = samples.get_min_max();
            data.iat_min_us = min_val;
            data.iat_max_us = max_val;

            data.iat_jitter_us = std::max(
                max_val - data.iat_avg_us,
                data.iat_avg_us - min_val
            );

            // Standard deviation (approximate from recent window)
            double variance = (sum_sq / static_cast<double>(count)) -
                              (data.iat_avg_us * data.iat_avg_us);
            data.iat_stddev_us = variance > 0 ? std::sqrt(variance) : 0.0;

            // Detect late/early packets
            double threshold = 2.0 * data.iat_stddev_us;
            if (iat_us > data.iat_avg_us + threshold) {
                data.late_packets++;
            } else if (iat_us < data.iat_avg_us - threshold && iat_us > 0) {
                data.early_packets++;
            }

            // Detect bursts
            if (iat_us < 10.0 && window_size > 1) {
                data.burst_count++;
            }
        }

        seqlock.end_write(seq);
    }

    bool get(IatAnalysisNative* out) const noexcept {
        if (!out) return false;

        uint64_t seq;
        do {
            seq = seqlock.begin_read();
            *out = data;
        } while (!seqlock.read_consistent(seq));

        return count > 0;
    }

    void reset() noexcept {
        auto seq = seqlock.begin_write();
        data = IatAnalysisNative{};
        samples.clear();
        sum = 0.0;
        sum_sq = 0.0;
        count = 0;
        first_packet = true;
        seqlock.end_write(seq);
    }
};

}  // namespace analysis
}  // namespace tsduck_interop

#endif  // TSDUCK_INTEROP_ANALYSIS_IAT_ANALYZER_HPP
