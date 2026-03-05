// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_ANALYSIS_PCR_ANALYZER_HPP
#define TSDUCK_INTEROP_ANALYSIS_PCR_ANALYZER_HPP

#include <array>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <limits>
#include <optional>

#include "../concurrency/seqlock.hpp"
#include "../core/constants.hpp"
#include "tsduck_interop.h"

namespace tsduck_interop::analysis {

/// PCR (Program Clock Reference) Analyzer
///
/// Tracks PCR timing per ISO/IEC 13818-1 requirements:
/// - Accuracy: +/-500ns phase tolerance
/// - Frequency offset: +/-30 ppm (+/-810 Hz at 27MHz)
/// - Drift rate: 75 mHz/sec (10 ppm/hr)
///
/// This analyzer uses lock-free techniques (seqlock) to allow concurrent
/// reads from the metrics thread while the packet processing thread writes.
class alignas(CACHE_LINE_SIZE) PcrAnalyzer {
public:
    // ========================================================================
    // PCR Constants
    // ========================================================================

    /// PCR wraparound value (2^33 * 300 at 27MHz)
    static constexpr std::int64_t PCR_WRAPAROUND_VALUE = PCR_WRAPAROUND;

    /// PCR system clock frequency (27 MHz)
    static constexpr double PCR_CLOCK_HZ = PCR_FREQUENCY_HZ;

    /// PCR to 90kHz conversion factor
    static constexpr std::int64_t PCR_90KHZ_FACTOR = PCR_TO_90KHZ;

    // ISO/IEC 13818-1 compliance limits
    static constexpr double FREQUENCY_OFFSET_LIMIT = PCR_FREQUENCY_OFFSET_LIMIT_PPM;
    static constexpr double DRIFT_RATE_LIMIT = PCR_DRIFT_RATE_LIMIT_PPM_PER_HOUR;
    static constexpr double ACCURACY_LIMIT_NS = 500.0;

    // ========================================================================
    // Public Interface
    // ========================================================================

    /// Process a PCR value from a transport stream packet.
    /// @param pcr_value The 42-bit PCR value (27MHz base)
    /// @param packet_index The packet index in the stream
    /// @param current_time_ns Wall clock time in nanoseconds (steady_clock)
    void process(std::uint64_t pcr_value, std::int64_t packet_index,
                 std::int64_t current_time_ns) noexcept {
        auto guard = seqlock_.write_guard();

        data_.pcr_count++;

        if (last_pcr_value_ < 0) {
            // First PCR - initialize tracking state
            last_pcr_value_ = static_cast<std::int64_t>(pcr_value);
            last_pcr_packet_index_ = packet_index;
            last_pcr_time_ns_ = current_time_ns;
            first_pcr_value_ = static_cast<std::int64_t>(pcr_value);
            first_pcr_time_ns_ = current_time_ns;
            data_.pcr_valid_count++;
            return;
        }

        // Calculate PCR interval with wraparound handling
        std::int64_t pcr_diff = compute_pcr_diff(
            static_cast<std::int64_t>(pcr_value), last_pcr_value_);

        double interval_ms = static_cast<double>(pcr_diff) / PCR_CLOCK_HZ * 1000.0;
        std::int64_t packet_diff = packet_index - last_pcr_packet_index_;

        data_.pcr_interval_packets = packet_diff;
        data_.pcr_interval_ms = interval_ms;

        // Calculate jitter (deviation from expected interval)
        double time_diff_us =
            static_cast<double>(current_time_ns - last_pcr_time_ns_) / 1000.0;
        double expected_us = interval_ms * 1000.0;
        double jitter_us = std::abs(time_diff_us - expected_us);

        update_jitter_stats(jitter_us);

        // Validate PCR (reasonable interval check)
        if (interval_ms > 0.0 && interval_ms < 1000.0) {
            data_.pcr_valid_count++;
        }

        // Calculate frequency offset (ISO/IEC 13818-1 limit: +/-30 ppm)
        if (first_pcr_value_ >= 0) {
            update_frequency_offset(pcr_value, current_time_ns);
        }

        // Calculate PCR accuracy
        update_accuracy(jitter_us);

        // Update tracking state
        last_pcr_value_ = static_cast<std::int64_t>(pcr_value);
        last_pcr_packet_index_ = packet_index;
        last_pcr_time_ns_ = current_time_ns;
    }

    /// Get the current PCR analysis results.
    /// @param out Pointer to structure to receive results
    /// @return true if valid data is available
    [[nodiscard]] bool get(PcrAnalysisNative* out) const noexcept {
        if (out == nullptr) {
            return false;
        }

        *out = concurrency::seqlock_read(seqlock_, data_);
        return out->pcr_count > 0;
    }

    /// Reset all analysis state.
    void reset() noexcept {
        auto guard = seqlock_.write_guard();

        data_ = PcrAnalysisNative{};
        last_pcr_value_ = -1;
        last_pcr_packet_index_ = 0;
        last_pcr_time_ns_ = 0;
        first_pcr_value_ = -1;
        first_pcr_time_ns_ = 0;
        jitter_sum_ = 0.0;
        jitter_count_ = 0;

        drift_history_.fill(DriftSample{});
        drift_history_idx_ = 0;
        drift_history_count_ = 0;
        last_accuracy_ns_ = 0.0;
    }

    /// Get the last PCR value converted to 90kHz base.
    /// @return PCR in 90kHz units, or -1 if no PCR received
    [[nodiscard]] std::int64_t last_pcr_base_90khz() const noexcept {
        std::int64_t pcr = last_pcr_value_;
        return pcr >= 0 ? pcr / PCR_90KHZ_FACTOR : -1;
    }

    /// Get the raw PCR data with seqlock protection.
    [[nodiscard]] PcrAnalysisNative get_data() const noexcept {
        return concurrency::seqlock_read(seqlock_, data_);
    }

private:
    // ========================================================================
    // Drift Sample for Rate Tracking
    // ========================================================================

    struct DriftSample {
        double frequency_offset_ppm{0.0};
        std::int64_t timestamp_ns{0};
    };

    static constexpr std::size_t DRIFT_HISTORY_SIZE = 16;

    // ========================================================================
    // Helper Functions
    // ========================================================================

    /// Compute PCR difference with wraparound handling.
    [[nodiscard]] static constexpr std::int64_t compute_pcr_diff(
        std::int64_t current, std::int64_t previous) noexcept {
        std::int64_t diff = current - previous;
        if (diff < 0) {
            diff += PCR_WRAPAROUND_VALUE;
        }
        return diff;
    }

    /// Update jitter statistics.
    void update_jitter_stats(double jitter_us) noexcept {
        data_.pcr_jitter_us = jitter_us;

        if (jitter_us > data_.pcr_jitter_max_us) {
            data_.pcr_jitter_max_us = jitter_us;
        }

        jitter_sum_ += jitter_us;
        ++jitter_count_;
        data_.pcr_jitter_avg_us = jitter_sum_ / static_cast<double>(jitter_count_);
    }

    /// Update frequency offset calculation.
    void update_frequency_offset(std::uint64_t pcr_value,
                                 std::int64_t current_time_ns) noexcept {
        double total_system_us =
            static_cast<double>(current_time_ns - first_pcr_time_ns_) / 1000.0;

        // Need at least 1 second of data for meaningful frequency offset
        if (total_system_us <= 1'000'000.0) {
            return;
        }

        std::int64_t total_pcr_diff =
            compute_pcr_diff(static_cast<std::int64_t>(pcr_value), first_pcr_value_);

        double expected_pcr_ticks = total_system_us * PCR_CLOCK_HZ / 1'000'000.0;
        double drift_ratio =
            (static_cast<double>(total_pcr_diff) - expected_pcr_ticks) / expected_pcr_ticks;
        double frequency_offset_ppm = drift_ratio * 1'000'000.0;

        data_.pcr_drift_ppm = frequency_offset_ppm;
        data_.pcr_frequency_offset_ppm = frequency_offset_ppm;
        data_.frequency_offset_valid =
            (std::abs(frequency_offset_ppm) <= FREQUENCY_OFFSET_LIMIT) ? 1 : 0;

        // Record drift sample for drift rate calculation
        record_drift_sample(frequency_offset_ppm, current_time_ns);

        // Calculate drift rate using linear regression
        calculate_drift_rate();
    }

    /// Update PCR accuracy calculation.
    void update_accuracy(double jitter_us) noexcept {
        // Use jitter as a proxy for accuracy (deviation from expected timing)
        double accuracy_ns = jitter_us * 1000.0;
        last_accuracy_ns_ = accuracy_ns;
        data_.pcr_accuracy_ns = accuracy_ns;
        data_.accuracy_valid = (std::abs(accuracy_ns) <= ACCURACY_LIMIT_NS) ? 1 : 0;
    }

    /// Record a frequency offset sample for drift rate tracking.
    void record_drift_sample(double frequency_offset_ppm,
                            std::int64_t timestamp_ns) noexcept {
        drift_history_[drift_history_idx_] =
            DriftSample{frequency_offset_ppm, timestamp_ns};
        drift_history_idx_ = (drift_history_idx_ + 1) % DRIFT_HISTORY_SIZE;

        if (drift_history_count_ < DRIFT_HISTORY_SIZE) {
            ++drift_history_count_;
        }
    }

    /// Calculate drift rate using linear regression over the sample history.
    /// Drift rate is the rate of change of frequency offset over time.
    /// ISO/IEC 13818-1 limit: 75 mHz/sec = 10 ppm/hour
    void calculate_drift_rate() noexcept {
        if (drift_history_count_ < 4) {
            // Not enough samples for meaningful drift rate calculation
            data_.pcr_drift_rate_ppm_hr = 0.0;
            data_.drift_rate_valid = 1;  // Assume valid until proven otherwise
            return;
        }

        // Find the oldest timestamp as reference
        std::int64_t oldest_ts = std::numeric_limits<std::int64_t>::max();
        for (std::size_t i = 0; i < drift_history_count_; ++i) {
            std::size_t idx =
                (drift_history_idx_ + DRIFT_HISTORY_SIZE - drift_history_count_ + i) %
                DRIFT_HISTORY_SIZE;
            if (drift_history_[idx].timestamp_ns < oldest_ts) {
                oldest_ts = drift_history_[idx].timestamp_ns;
            }
        }

        // Linear regression: find slope of frequency_offset_ppm over time
        double sum_x = 0.0;   // time in hours
        double sum_y = 0.0;   // frequency offset in ppm
        double sum_xy = 0.0;
        double sum_xx = 0.0;
        const std::size_t n = drift_history_count_;

        for (std::size_t i = 0; i < n; ++i) {
            std::size_t idx =
                (drift_history_idx_ + DRIFT_HISTORY_SIZE - n + i) % DRIFT_HISTORY_SIZE;
            const auto& sample = drift_history_[idx];

            // Convert time to hours from oldest sample
            double x = static_cast<double>(sample.timestamp_ns - oldest_ts) /
                       (1e9 * 3600.0);
            double y = sample.frequency_offset_ppm;

            sum_x += x;
            sum_y += y;
            sum_xy += x * y;
            sum_xx += x * x;
        }

        // Calculate slope (drift rate in ppm/hour)
        double n_d = static_cast<double>(n);
        double denom = n_d * sum_xx - sum_x * sum_x;

        if (std::abs(denom) > 1e-15) {
            double slope = (n_d * sum_xy - sum_x * sum_y) / denom;
            data_.pcr_drift_rate_ppm_hr = slope;
            data_.drift_rate_valid = (std::abs(slope) <= DRIFT_RATE_LIMIT) ? 1 : 0;
        } else {
            data_.pcr_drift_rate_ppm_hr = 0.0;
            data_.drift_rate_valid = 1;
        }
    }

    // ========================================================================
    // Member Variables
    // ========================================================================

    // Seqlock-protected analysis data
    mutable concurrency::Seqlock seqlock_;
    PcrAnalysisNative data_{};

    // Writer state (only accessed by writer thread)
    std::int64_t last_pcr_value_{-1};
    std::int64_t last_pcr_packet_index_{0};
    std::int64_t last_pcr_time_ns_{0};
    std::int64_t first_pcr_value_{-1};
    std::int64_t first_pcr_time_ns_{0};
    double jitter_sum_{0.0};
    std::int64_t jitter_count_{0};

    // Drift rate tracking
    std::array<DriftSample, DRIFT_HISTORY_SIZE> drift_history_{};
    std::size_t drift_history_idx_{0};
    std::size_t drift_history_count_{0};

    // PCR accuracy tracking
    double last_accuracy_ns_{0.0};
};

}  // namespace tsduck_interop::analysis

#endif  // TSDUCK_INTEROP_ANALYSIS_PCR_ANALYZER_HPP
