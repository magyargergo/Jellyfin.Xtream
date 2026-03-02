// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_CONTEXT_ANALYZER_HPP
#define TSDUCK_INTEROP_CONTEXT_ANALYZER_HPP

#include <array>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <memory>
#include <span>

#include "../analysis/av_sync_tracker.hpp"
#include "../analysis/iat_analyzer.hpp"
#include "../analysis/nal_parser.hpp"
#include "../analysis/pcr_analyzer.hpp"
#include "../analysis/pid_tracker.hpp"
#include "../analysis/psi_monitor.hpp"
#include "../analysis/scte35_monitor.hpp"
#include "../analysis/tr101290.hpp"
#include "../concurrency/seqlock.hpp"
#include "../core/constants.hpp"
#include "../restamping/restamper.hpp"
#include "context.hpp"
#include "tsduck_interop.h"

namespace tsduck_interop::context {

// Double-buffered metrics for lock-free snapshot reads
struct alignas(CACHE_LINE_SIZE) MetricsBuffer {
    TsDuckMetricsNative data{};
    concurrency::Seqlock seqlock;
};

// Double-buffered bitrate analysis
struct alignas(CACHE_LINE_SIZE) BitrateBuffer {
    BitrateAnalysisNative data{};
    concurrency::Seqlock seqlock;
};

/// TsDuck Analyzer - High-performance MPEG-TS stream analysis.
///
/// Provides comprehensive transport stream analysis including:
/// - PCR timing and jitter analysis
/// - Inter-arrival time (network jitter) tracking
/// - Per-PID statistics with continuity counter validation
/// - PAT/PMT parsing via TsDuck's SectionDemux
/// - TR 101 290 quality monitoring
/// - A/V sync drift detection
/// - Integrated timestamp restamping (optional)
///
/// Thread safety:
/// - Single writer thread (packet processing)
/// - Multiple reader threads (metrics queries via seqlock)
class TsDuckAnalyzer {
public:
    // ========================================================================
    // Public Members
    // ========================================================================

    TsDuckContext* context;
    TsDuckConfigNative config;

    // Lock-Free Metrics State
    alignas(CACHE_LINE_SIZE) MetricsBuffer metrics;
    alignas(CACHE_LINE_SIZE) mutable std::atomic<bool> has_new_metrics{false};
    alignas(CACHE_LINE_SIZE) std::atomic<std::int64_t> last_metrics_time_ns{0};

    // Custom Analysis Components (unique to our implementation)
    alignas(CACHE_LINE_SIZE) analysis::AvSyncTracker av_sync;     // A/V drift tracking
    alignas(CACHE_LINE_SIZE) analysis::PcrAnalyzer pcr;           // PCR jitter tracking
    alignas(CACHE_LINE_SIZE) analysis::IatAnalyzer iat;           // Network jitter
    alignas(CACHE_LINE_SIZE) BitrateBuffer bitrate;
    alignas(CACHE_LINE_SIZE) analysis::PidTracker pids;           // Per-PID statistics
    alignas(CACHE_LINE_SIZE) analysis::PsiMonitor psi;            // PAT/PMT parsing
    alignas(CACHE_LINE_SIZE) analysis::Tr101290Monitor tr101290;  // TR 101 290 quality
    alignas(CACHE_LINE_SIZE) analysis::Scte35Monitor scte35;      // SCTE-35 ad markers
    alignas(CACHE_LINE_SIZE) analysis::NalParser nal_parser;      // NAL unit parsing

    // Callbacks
    std::atomic<TsDuckMetricsCallback> metrics_callback{nullptr};
    std::atomic<void*> metrics_user_data{nullptr};
    std::atomic<TsDuckViolationCallback> violation_callback{nullptr};
    std::atomic<void*> violation_user_data{nullptr};
    std::atomic<TsDuckScte35Callback> scte35_callback{nullptr};
    std::atomic<void*> scte35_user_data{nullptr};

    // Previous error counts for violation detection
    std::int64_t prev_cc_errors{0};
    std::int64_t prev_transport_errors{0};
    std::int64_t prev_crc_errors{0};
    std::int64_t prev_pcr_errors{0};

    // Packet counter
    alignas(CACHE_LINE_SIZE) std::atomic<std::int64_t> packets_processed{0};

    // Atomic counters for bitrate calculation
    alignas(CACHE_LINE_SIZE) std::atomic<std::int64_t> null_packet_count{0};
    alignas(CACHE_LINE_SIZE) std::atomic<std::int64_t> total_packet_count{0};

    // Thresholds
    std::atomic<double> pcr_jitter_threshold_us{DEFAULT_PCR_JITTER_THRESHOLD_US};
    std::atomic<double> iat_jitter_threshold_us{DEFAULT_IAT_JITTER_THRESHOLD_US};

    // Start time for bitrate calculation (reset on first data arrival)
    alignas(CACHE_LINE_SIZE) std::atomic<std::int64_t> start_time_ns{0};
    alignas(CACHE_LINE_SIZE) std::atomic<bool> first_feed_received{false};

    // Integrated restamping (optional)
    alignas(CACHE_LINE_SIZE) std::unique_ptr<restamping::Restamper> restamper;
    alignas(CACHE_LINE_SIZE) std::atomic<bool> auto_restamp_enabled{false};

    // ========================================================================
    // Constructor
    // ========================================================================

    /// Construct analyzer with context and optional configuration.
    /// @param ctx TsDuck context (must not be null)
    /// @param cfg Configuration (uses defaults if nullptr)
    TsDuckAnalyzer(TsDuckContext* ctx, const TsDuckConfigNative* cfg)
        : context(ctx), psi(ctx->duck), scte35(ctx->duck), nal_parser(ctx->duck) {
        if (cfg != nullptr) {
            config = *cfg;
        } else {
            config = make_default_config();
        }

        initialize_restamper();

        std::int64_t now_val = now_ns();
        start_time_ns.store(now_val, std::memory_order_release);
        last_metrics_time_ns.store(now_val, std::memory_order_release);
    }

    // ========================================================================
    // Static Utility Functions
    // ========================================================================

    /// Get current time in nanoseconds.
    [[nodiscard]] static std::int64_t now_ns() noexcept {
        return std::chrono::duration_cast<std::chrono::nanoseconds>(
            std::chrono::steady_clock::now().time_since_epoch()).count();
    }

    /// Get .NET DateTime.Ticks equivalent.
    [[nodiscard]] static std::int64_t get_dotnet_ticks() noexcept {
        auto sys_now = std::chrono::system_clock::now();
        auto ticks = std::chrono::duration_cast<std::chrono::nanoseconds>(
            sys_now.time_since_epoch()).count() / 100;
        constexpr std::int64_t DOTNET_EPOCH_OFFSET = 621355968000000000LL;
        return ticks + DOTNET_EPOCH_OFFSET;
    }

    // ========================================================================
    // Packet Processing
    // ========================================================================

    /// Process PCR from a packet.
    void process_pcr(ts::TSPacket& pkt, std::int64_t packet_index) noexcept {
        if (!pkt.hasPCR()) {
            return;
        }

        std::uint64_t pcr_value = pkt.getPCR();
        if (pcr_value == ts::INVALID_PCR) {
            return;
        }

        std::int64_t current_time = now_ns();
        pcr.process(pcr_value, packet_index, current_time);

        std::int64_t pcr_base_90khz =
            static_cast<std::int64_t>(pcr_value / ts::SYSTEM_CLOCK_SUBFACTOR);
        av_sync.update_pcr_reference(pcr_base_90khz);
    }

    /// Process inter-arrival time.
    void process_iat() noexcept {
        iat.process(now_ns());
    }

    /// Process bitrate tracking for a packet.
    void process_bitrate(std::uint16_t pid) noexcept {
        total_packet_count.fetch_add(1, std::memory_order_relaxed);

        if (pid == ts::PID_NULL) {
            null_packet_count.fetch_add(1, std::memory_order_relaxed);
        }
    }

    /// Process PID information from a packet.
    void process_pid_info(ts::TSPacket& pkt, std::uint16_t pid) noexcept {
        bool scrambled = pkt.isScrambled();
        std::int64_t time = now_ns();
        pids.process_packet(pid, pkt.getCC(), pkt.hasPayload(), scrambled, time);

        if (pkt.hasPCR()) {
            pids.set_pcr_pid(pid, true);
            PcrAnalysisNative pcr_data;
            if (pcr.get(&pcr_data)) {
                pids.set_pcr_jitter(pid, pcr_data.pcr_jitter_us);
            }
        }
    }

    // ========================================================================
    // Metrics Updates
    // ========================================================================

    /// Update metrics if enough time has elapsed.
    void update_metrics() noexcept {
        std::int64_t current_time = now_ns();
        std::int64_t last_time = last_metrics_time_ns.load(std::memory_order_relaxed);
        std::int64_t elapsed_ms = (current_time - last_time) / 1'000'000;

        if (elapsed_ms < config.metrics_interval_ms) {
            return;
        }

        last_metrics_time_ns.store(current_time, std::memory_order_release);

        auto seq = metrics.seqlock.begin_write();

        metrics.data.timestamp_ticks = get_dotnet_ticks();

        // Get PID count from our PidTracker
        metrics.data.pid_count = pids.active_count.load(std::memory_order_relaxed);
        metrics.data.service_count = 0;  // Overwritten below from PSI monitor

        // Calculate bitrate from packet count and elapsed time
        {
            int64_t start = start_time_ns.load(std::memory_order_relaxed);
            int64_t total_time_ms = (current_time - start) / 1000000;
            if (total_time_ms > 0) {
                int64_t bytes = packets_processed.load(std::memory_order_relaxed) * static_cast<int32_t>(ts::PKT_SIZE);
                metrics.data.ts_bitrate = (bytes * 8 * 1000) / total_time_ms;
            }
        }

        // Service count from PSI monitor
        metrics.data.service_count = psi.get_program_count();

        // TR 101 290 metrics
        if (config.enable_tr101290) {
            int64_t current_pkt_idx = packets_processed.load(std::memory_order_relaxed);
            int64_t bitrate = tr101290.estimated_bitrate_bps.load(std::memory_order_relaxed);

            // Check PAT timeout (uses packet-distance timing)
            tr101290.check_pat_timeout(current_pkt_idx);

            // Check CAT timeout (only if CAT is present in stream)
            tr101290.check_cat_timeout(current_pkt_idx);

            // Check PID timeouts (PIDs referenced in PAT/PMT not seen for 5s)
            int64_t current_time = now_ns();
            int32_t pid_timeouts = pids.check_pid_timeouts(current_time, bitrate, current_pkt_idx);
            for (int32_t t = 0; t < pid_timeouts; t++) {
                tr101290.on_pid_timeout();
            }

            // Check PMT timeouts for all programs (packet-distance timing)
            if (bitrate > 0) {
                int32_t prog_count = psi.get_program_count();
                for (int32_t p = 0; p < prog_count; p++) {
                    if (psi.programs[p].active && psi.programs[p].pmt_received) {
                        int64_t last_pmt_idx = psi.programs[p].last_pmt_packet_idx;
                        if (last_pmt_idx >= 0) {
                            int64_t pkt_delta = current_pkt_idx - last_pmt_idx;
                            if (pkt_delta > 0) {
                                double bits = static_cast<double>(pkt_delta) * static_cast<double>(ts::PKT_SIZE_BITS);
                                double interval_sec = bits / static_cast<double>(bitrate);
                                int64_t interval_ns = static_cast<int64_t>(interval_sec * 1e9);
                                if (interval_ns > TR101290_PMT_INTERVAL_NS) {
                                    tr101290.on_pmt_timeout();
                                    psi.programs[p].last_pmt_packet_idx = current_pkt_idx;
                                }
                            }
                        }
                    }
                }
            }

            // Get counters from TR 101 290 monitor
            Tr101290Priority1Native p1{};
            Tr101290Priority2Native p2{};
            tr101290.get_counters(&p1, &p2);

            // CC errors from PidTracker (already tracked there)
            int64_t total_cc_errors = pids.total_cc_errors.load(std::memory_order_relaxed);

            metrics.data.priority1.sync_byte_error = p1.sync_byte_error;
            metrics.data.priority1.sync_loss = p1.sync_loss;
            metrics.data.priority1.pat_error = p1.pat_error;
            metrics.data.priority1.pat_error_2 = 0;
            metrics.data.priority1.continuity_count_error = total_cc_errors;
            metrics.data.priority1.pmt_error = p1.pmt_error;
            metrics.data.priority1.pmt_error_2 = 0;
            metrics.data.priority1.pid_error = p1.pid_error;

            metrics.data.priority2.transport_error = p2.transport_error;
            metrics.data.priority2.crc_error = p2.crc_error + psi.crc_errors.load(std::memory_order_relaxed);
            metrics.data.priority2.pcr_repetition_error = p2.pcr_repetition_error;
            metrics.data.priority2.pcr_discontinuity_error = p2.pcr_discontinuity_error;
            metrics.data.priority2.pcr_accuracy_error = p2.pcr_accuracy_error;
            metrics.data.priority2.pts_error = p2.pts_error;
            metrics.data.priority2.cat_error = 0;
        }

        metrics.seqlock.end_write(seq);

        // Update bitrate analysis
        int64_t total_packets = total_packet_count.load(std::memory_order_relaxed);
        int64_t null_packets = null_packet_count.load(std::memory_order_relaxed);

        auto bitrate_seq = bitrate.seqlock.begin_write();
        bitrate.data.ts_bitrate_nominal = metrics.data.ts_bitrate;

        if (total_packets > 0) {
            bitrate.data.null_packet_ratio = static_cast<double>(null_packets) / static_cast<double>(total_packets);
            bitrate.data.null_packet_bitrate =
                static_cast<int64_t>(metrics.data.ts_bitrate * bitrate.data.null_packet_ratio);
            bitrate.data.useful_bitrate = metrics.data.ts_bitrate - bitrate.data.null_packet_bitrate;
        }
        bitrate.seqlock.end_write(bitrate_seq);

        has_new_metrics.store(true, std::memory_order_release);

        // Invoke callback if registered
        auto callback = metrics_callback.load(std::memory_order_acquire);
        if (callback) {
            auto user_data = metrics_user_data.load(std::memory_order_acquire);
            callback(&metrics.data, user_data);
        }
    }

    /// Force immediate metrics update (bypasses interval check).
    /// Used by get_metrics() to provide on-demand metrics.
    void force_update_metrics() noexcept {
        // Skip if no data has been received yet
        if (!first_feed_received.load(std::memory_order_acquire)) {
            return;
        }

        std::int64_t current_time = now_ns();

        auto seq = metrics.seqlock.begin_write();

        metrics.data.timestamp_ticks = get_dotnet_ticks();

        // Get PID count from our PidTracker
        metrics.data.pid_count = pids.active_count.load(std::memory_order_relaxed);
        metrics.data.service_count = psi.get_program_count();

        // Calculate bitrate from packet count and elapsed time
        {
            int64_t start = start_time_ns.load(std::memory_order_relaxed);
            int64_t total_time_ms = (current_time - start) / 1000000;
            if (total_time_ms > 0) {
                int64_t bytes = packets_processed.load(std::memory_order_relaxed) * static_cast<int32_t>(ts::PKT_SIZE);
                metrics.data.ts_bitrate = (bytes * 8 * 1000) / total_time_ms;
            }
        }

        // TR 101 290 metrics (simplified - just get current counters)
        if (config.enable_tr101290) {
            Tr101290Priority1Native p1{};
            Tr101290Priority2Native p2{};
            tr101290.get_counters(&p1, &p2);

            int64_t total_cc_errors = pids.total_cc_errors.load(std::memory_order_relaxed);

            metrics.data.priority1.sync_byte_error = p1.sync_byte_error;
            metrics.data.priority1.sync_loss = p1.sync_loss;
            metrics.data.priority1.pat_error = p1.pat_error;
            metrics.data.priority1.pat_error_2 = 0;
            metrics.data.priority1.continuity_count_error = total_cc_errors;
            metrics.data.priority1.pmt_error = p1.pmt_error;
            metrics.data.priority1.pmt_error_2 = 0;
            metrics.data.priority1.pid_error = p1.pid_error;

            metrics.data.priority2.transport_error = p2.transport_error;
            metrics.data.priority2.crc_error = p2.crc_error + psi.crc_errors.load(std::memory_order_relaxed);
            metrics.data.priority2.pcr_repetition_error = p2.pcr_repetition_error;
            metrics.data.priority2.pcr_discontinuity_error = p2.pcr_discontinuity_error;
            metrics.data.priority2.pcr_accuracy_error = p2.pcr_accuracy_error;
            metrics.data.priority2.pts_error = p2.pts_error;
            metrics.data.priority2.cat_error = 0;
        }

        metrics.seqlock.end_write(seq);

        // Update bitrate analysis
        int64_t total_packets = total_packet_count.load(std::memory_order_relaxed);
        int64_t null_packets = null_packet_count.load(std::memory_order_relaxed);

        auto bitrate_seq = bitrate.seqlock.begin_write();
        bitrate.data.ts_bitrate_nominal = metrics.data.ts_bitrate;

        if (total_packets > 0) {
            bitrate.data.null_packet_ratio = static_cast<double>(null_packets) / static_cast<double>(total_packets);
            bitrate.data.null_packet_bitrate =
                static_cast<int64_t>(metrics.data.ts_bitrate * bitrate.data.null_packet_ratio);
            bitrate.data.useful_bitrate = metrics.data.ts_bitrate - bitrate.data.null_packet_bitrate;
        }
        bitrate.seqlock.end_write(bitrate_seq);
    }

    // ========================================================================
    // Feed Methods
    // ========================================================================

    /// Feed MPEG-TS data for analysis (read-only).
    /// @param data Pointer to MPEG-TS packet data
    /// @param length Length in bytes (must be multiple of 188)
    /// @return Number of packets processed, or negative error code
    [[nodiscard]] std::int32_t feed(const std::uint8_t* data, std::int32_t length) noexcept {
        if (data == nullptr || length <= 0) {
            return TSDUCK_ERROR_INVALID_DATA;
        }

        std::int32_t packets = length / static_cast<std::int32_t>(TS_PACKET_SIZE);
        if (packets == 0) {
            return 0;
        }

        initialize_timing_on_first_feed();

        std::int64_t base_packet_index = packets_processed.load(std::memory_order_relaxed);

        auto packet_span = std::span{reinterpret_cast<const ts::TSPacket*>(data),
                                    static_cast<std::size_t>(packets)};

        for (std::int32_t i = 0; const auto& pkt_ref : packet_span) {
            // const_cast safe: feed() is read-only analysis
            auto& pkt = const_cast<ts::TSPacket&>(pkt_ref);
            std::int64_t packet_idx = base_packet_index + i;
            ++i;

            process_single_packet(pkt, packet_idx);
        }

        packets_processed.fetch_add(packets, std::memory_order_release);
        update_metrics();

        return packets;
    }

    /// Feed MPEG-TS data with integrated restamping.
    /// Modifies data IN-PLACE to apply timestamp corrections, then analyzes.
    /// @param data MPEG-TS data to process (will be modified if restamping enabled)
    /// @param length Number of bytes
    /// @return Number of packets processed, or negative error code
    [[nodiscard]] std::int32_t feed_and_restamp(std::uint8_t* data, std::int32_t length) noexcept {
        if (data == nullptr || length <= 0) {
            return TSDUCK_ERROR_INVALID_DATA;
        }

        std::int32_t packets = length / static_cast<std::int32_t>(TS_PACKET_SIZE);
        if (packets == 0) {
            return 0;
        }

        initialize_timing_on_first_feed();

        std::int64_t base_packet_index = packets_processed.load(std::memory_order_relaxed);

        // Apply restamping BEFORE analysis (modifies data in-place)
        if (auto_restamp_enabled.load(std::memory_order_acquire) && restamper) {
            (void)restamper->process(data, length, base_packet_index);
        }

        auto packet_span = std::span{reinterpret_cast<ts::TSPacket*>(data),
                                    static_cast<std::size_t>(packets)};

        for (std::int32_t i = 0; auto& pkt : packet_span) {
            std::int64_t packet_idx = base_packet_index + i;
            ++i;

            process_single_packet(pkt, packet_idx);
        }

        packets_processed.fetch_add(packets, std::memory_order_release);
        update_metrics();

        return packets;
    }

    // ========================================================================
    // Restamping Configuration
    // ========================================================================

    /// Configure integrated restamping at runtime.
    /// @param mode Restamp mode (DISABLED, MONITOR, or CORRECT)
    /// @param smooth_pcr Enable PCR smoothing (1 = enable)
    /// @param fix_discontinuities Fix discontinuities (1 = enable)
    void configure_restamp(std::int32_t mode, std::int32_t smooth_pcr,
                          std::int32_t fix_discontinuities) noexcept {
        if (mode == RESTAMP_MODE_DISABLED) {
            auto_restamp_enabled.store(false, std::memory_order_release);
            return;
        }

        if (!restamper) {
            RestampingConfigNative cfg{
                .mode = mode,
                .smooth_pcr = smooth_pcr,
                .fix_discontinuities = fix_discontinuities,
                .reserved = 0,
                .correction_threshold_ms = config.correction_threshold_ms,
                .max_correction_rate_ms = config.max_correction_rate_ms,
                .hysteresis_threshold_ms = config.hysteresis_threshold_ms,
                .stream_bitrate_hint = config.stream_bitrate_hint
            };
            restamper = std::make_unique<restamping::Restamper>(&av_sync, &cfg);
        } else {
            restamper->config.mode = mode;
            restamper->config.smooth_pcr = smooth_pcr;
            restamper->config.fix_discontinuities = fix_discontinuities;
        }

        auto_restamp_enabled.store(true, std::memory_order_release);
    }

    /// Handle provider switch for timestamp continuity.
    /// Resets PCR analyzer state so post-reconnection interval measurements
    /// start fresh instead of spanning the wall-clock gap during disconnect.
    /// @param last_output_pts Last PTS value output before switch (90kHz)
    /// @param new_input_first_pts First PTS from new provider (90kHz)
    void handle_switch(std::int64_t last_output_pts,
                      std::int64_t new_input_first_pts) noexcept {
        if (restamper) {
            restamper->handle_switch(last_output_pts, new_input_first_pts);
        }

        // Reset PCR analyzer state: prevents interval measurements from
        // spanning the reconnection gap (which would produce >100ms intervals
        // that trigger TR 101 290 violations).
        pcr.reset();
    }

    /// Get restamping statistics.
    /// @param out Pointer to receive statistics
    /// @return true if statistics available
    [[nodiscard]] bool get_restamp_statistics(RestampingStatisticsNative* out) const noexcept {
        if (restamper == nullptr || out == nullptr) {
            return false;
        }
        return restamper->get_statistics(out);
    }

    /// Check if restamping is enabled.
    [[nodiscard]] bool is_restamping_enabled() const noexcept {
        return auto_restamp_enabled.load(std::memory_order_acquire);
    }

    void reset() noexcept {
        packets_processed.store(0, std::memory_order_release);
        total_packet_count.store(0, std::memory_order_release);
        null_packet_count.store(0, std::memory_order_release);
        first_feed_received.store(false, std::memory_order_release);

        // Reset previous error counts
        prev_cc_errors = 0;
        prev_transport_errors = 0;
        prev_crc_errors = 0;
        prev_pcr_errors = 0;

        // Reset custom analysis components
        pcr.reset();       // Our PCR jitter tracker (for callbacks)
        iat.reset();       // Network IAT - unique to us
        pids.reset();      // Per-PID stats
        av_sync.reset();   // A/V sync - unique to us
        psi.reset();       // PAT/PMT table parser
        tr101290.reset();  // TR 101 290 quality monitor
        scte35.reset();    // SCTE-35 monitor
        nal_parser.reset(); // NAL unit parser

        // Reset integrated restamper if present
        if (restamper) {
            restamper->reset();
        }

        // Reset metrics
        auto seq = metrics.seqlock.begin_write();
        metrics.data = TsDuckMetricsNative{};
        metrics.seqlock.end_write(seq);

        // Reset bitrate
        auto bitrate_seq = bitrate.seqlock.begin_write();
        bitrate.data = BitrateAnalysisNative{};
        bitrate.seqlock.end_write(bitrate_seq);

        has_new_metrics.store(false, std::memory_order_release);

        auto now = std::chrono::steady_clock::now().time_since_epoch();
        auto now_ns_val = std::chrono::duration_cast<std::chrono::nanoseconds>(now).count();
        start_time_ns.store(now_ns_val, std::memory_order_release);
        last_metrics_time_ns.store(now_ns_val, std::memory_order_release);
    }

    // ========================================================================
    // Metrics Retrieval
    // ========================================================================

    /// Get current metrics with on-demand calculation.
    /// Forces metrics update to ensure returned data reflects current state.
    /// @param out Pointer to receive metrics
    /// @return true if metrics available
    [[nodiscard]] bool get_metrics(TsDuckMetricsNative* out) noexcept {
        if (out == nullptr) {
            return false;
        }

        // Force metrics update to ensure on-demand availability
        // This bypasses the interval check to provide real-time metrics
        force_update_metrics();

        *out = concurrency::seqlock_read(metrics.seqlock, metrics.data);
        has_new_metrics.store(false, std::memory_order_release);
        return true;
    }

    /// Get bitrate analysis.
    /// @param out Pointer to receive analysis
    /// @return true if data available
    [[nodiscard]] bool get_bitrate_analysis(BitrateAnalysisNative* out) const noexcept {
        if (out == nullptr) {
            return false;
        }

        *out = concurrency::seqlock_read(bitrate.seqlock, bitrate.data);
        return total_packet_count.load(std::memory_order_acquire) > 0;
    }

private:
    // ========================================================================
    // Private Helper Functions
    // ========================================================================

    /// Create default configuration.
    [[nodiscard]] static constexpr TsDuckConfigNative make_default_config() noexcept {
        return TsDuckConfigNative{
            .metrics_interval_ms = 1000,
            .enable_tr101290 = 1,
            .sample_size_bytes = static_cast<std::int32_t>(TS_PACKET_SIZE) * 1000,
            .enable_auto_restamp = 1,
            .restamp_mode = RESTAMP_MODE_CORRECT,
            .smooth_pcr = 1,
            .fix_discontinuities = 1,
            .reserved = 0,
            .correction_threshold_ms = DEFAULT_CORRECTION_THRESHOLD_MS,
            .max_correction_rate_ms = DEFAULT_MAX_CORRECTION_RATE_MS,
            .hysteresis_threshold_ms = DEFAULT_HYSTERESIS_THRESHOLD_MS,
            .stream_bitrate_hint = 0
        };
    }

    /// Initialize restamper if enabled in configuration.
    void initialize_restamper() noexcept {
        if (config.enable_auto_restamp != 0 &&
            config.restamp_mode != RESTAMP_MODE_DISABLED) {
            RestampingConfigNative restamp_cfg{
                .mode = config.restamp_mode,
                .smooth_pcr = config.smooth_pcr,
                .fix_discontinuities = config.fix_discontinuities,
                .reserved = 0,
                .correction_threshold_ms = config.correction_threshold_ms,
                .max_correction_rate_ms = config.max_correction_rate_ms,
                .hysteresis_threshold_ms = config.hysteresis_threshold_ms,
                .stream_bitrate_hint = config.stream_bitrate_hint
            };
            restamper = std::make_unique<restamping::Restamper>(&av_sync, &restamp_cfg);
            auto_restamp_enabled.store(true, std::memory_order_release);
        }
    }

    /// Initialize timing on first data arrival.
    void initialize_timing_on_first_feed() noexcept {
        bool expected = false;
        if (first_feed_received.compare_exchange_strong(expected, true, std::memory_order_acq_rel)) {
            // Only one thread will enter here due to atomic CAS
            std::int64_t now_val = now_ns();
            start_time_ns.store(now_val, std::memory_order_release);
            last_metrics_time_ns.store(now_val, std::memory_order_release);
        }
    }

    /// Process a single packet (shared logic for feed and feed_and_restamp).
    void process_single_packet(ts::TSPacket& pkt, std::int64_t packet_idx) noexcept {
        // TR 101 290: sync check (even for invalid packets)
        bool valid_sync = pkt.hasValidSync();
        tr101290.check_sync(valid_sync);
        if (!valid_sync) {
            return;
        }

        std::uint16_t pid = pkt.getPID();

        // TR 101 290 Priority 2: Transport Error Indicator
        tr101290.check_transport_error(pkt);

        // TR 101 290: Track PAT/PMT PID reception for timeout checks
        track_psi_reception(pid, pkt, packet_idx);

        // PSI accumulation
        psi.feed_packet(pkt, packet_idx);
        apply_psi_updates();

        process_pcr(pkt, packet_idx);

        // TR 101 290: PCR checks
        if (pkt.hasPCR()) {
            std::uint64_t pcr_val = pkt.getPCR();
            if (pcr_val != ts::INVALID_PCR) {
                tr101290.check_pcr_repetition(pid, packet_idx);
                tr101290.check_pcr_accuracy(pid, pcr_val, packet_idx);
                tr101290.check_pcr_discontinuity(pkt, pid, pcr_val);
            }
        }

        process_iat();
        process_bitrate(pid);
        process_pid_info(pkt, pid);
        process_pes_pts(pkt, pid, packet_idx,
                       static_cast<std::int64_t>(packet_idx % 1000) *
                       static_cast<std::int32_t>(TS_PACKET_SIZE));

        // SCTE-35 splice information processing
        scte35.feed_packet(pkt, packet_idx);

        // NAL unit parsing for video PIDs
        if (pkt.startPES()) {
            nal_parser.process_pes_start(pkt, pid, packet_idx);
        }
    }
    /// Apply PSI discoveries to PID tracker and TR 101 290 monitor.
    /// Called after PSI feedPacket when PAT/PMT may have been parsed.
    void apply_psi_updates() noexcept {
        // Note: PAT/PMT reception timing for TR 101 290 is tracked at the
        // packet level (track_psi_reception), NOT here. SectionDemux uses version
        // filtering, so handlePat/handlePmt only fire on version changes.
        // TR 101 290 requires tracking every PAT/PMT packet reception.

        // PAT PID (0x0000) is always expected in a valid transport stream
        pids.mark_expected(ts::PID_PAT);

        // Apply PMT stream types to PID tracker (overrides PES stream_id guesses)
        int32_t prog_count = psi.get_program_count();
        constexpr std::int64_t NO_PID = -1;
        std::int64_t first_video_pid = NO_PID;
        std::int64_t first_audio_pid = NO_PID;
        for (int32_t p = 0; p < prog_count; p++) {
            const auto& prog = psi.programs[p];
            if (!prog.active || !prog.pmt_received)
                continue;

            // Set PCR PID and mark as expected
            if (prog.pcr_pid != 0x1FFF) {
                pids.set_pcr_pid(prog.pcr_pid, true);
                pids.mark_expected(prog.pcr_pid);
            }

            // Mark PMT PID as expected
            pids.mark_expected(prog.pmt_pid);

            // Apply authoritative stream types from PMT
            for (int32_t s = 0; s < prog.stream_count; s++) {
                const auto& es = prog.streams[s];
                if (!es.active)
                    continue;

                auto& slot = pids.slots[es.pid];
                slot.stream_type.store(es.stream_type, std::memory_order_release);
                if (es.is_video) {
                    slot.is_video.store(true, std::memory_order_release);
                    slot.is_audio.store(false, std::memory_order_release);
                    if (first_video_pid == NO_PID) {
                        first_video_pid = static_cast<std::int64_t>(es.pid);
                    }

                    // Register video PID for NAL parsing (H.264/H.265/H.266)
                    if (es.stream_type == 0x1B || es.stream_type == 0x24 || es.stream_type == 0x33) {
                        nal_parser.add_video_pid(es.pid, es.stream_type);
                    }
                } else if (es.is_audio) {
                    slot.is_audio.store(true, std::memory_order_release);
                    slot.is_video.store(false, std::memory_order_release);
                    if (first_audio_pid == NO_PID) {
                        first_audio_pid = static_cast<std::int64_t>(es.pid);
                    }
                }

                // Check for SCTE-35 stream type (0x86)
                if (es.stream_type == 0x86) {
                    scte35.add_scte35_pid(es.pid);
                }

                // Mark elementary stream PIDs as expected for timeout tracking
                pids.mark_expected(es.pid);
            }
        }

        // PMT stream mapping is authoritative, use first discovered A/V PIDs as primary restamp targets.
        if (restamper && (first_video_pid != NO_PID || first_audio_pid != NO_PID)) {
            restamper->set_target_pids(first_video_pid, first_audio_pid);
        }

        // Feed estimated bitrate to TR 101 290 for PCR accuracy checks
        int64_t current_bitrate = metrics.data.ts_bitrate;
        if (current_bitrate > 0) {
            tr101290.estimated_bitrate_bps.store(current_bitrate, std::memory_order_relaxed);
        }
    }

    void process_pes_pts(ts::TSPacket& pkt, uint16_t pid, int64_t packet_idx, int64_t byte_off) noexcept {
        if (!pkt.startPES())
            return;
        if (pid < 0x100 || pid >= ts::PID_NULL)
            return;

        // Classify stream from PES stream_id
        const uint8_t* payload = pkt.getPayload();
        size_t payload_size = pkt.getPayloadSize();
        if (payload_size < 4)
            return;

        uint8_t stream_id = payload[3];
        bool is_video = ts::IsVideoSID(stream_id);
        bool is_audio = ts::IsAudioSID(stream_id);
        bool is_private = (stream_id == ts::SID_PRIV1);

        auto& slot = pids.slots[pid];
        if (is_video) {
            slot.is_video.store(true, std::memory_order_release);
            slot.stream_type.store(ts::ST_MPEG2_VIDEO, std::memory_order_release);
            if (restamper) {
                restamper->set_target_pids_if_unset(static_cast<std::int64_t>(pid), -1);
            }
        } else if (is_audio || is_private) {
            slot.is_audio.store(true, std::memory_order_release);
            slot.stream_type.store(ts::ST_MPEG2_AUDIO, std::memory_order_release);
            if (restamper) {
                restamper->set_target_pids_if_unset(-1, static_cast<std::int64_t>(pid));
            }
        }

        // Extract PTS/DTS via TsDuck
        if (is_video || is_audio || is_private) {
            constexpr int64_t INVALID_TS = -1;
            int64_t pts = INVALID_TS, dts = INVALID_TS;
            if (pkt.hasPTS()) {
                uint64_t pts_val = pkt.getPTS();
                if (pts_val != ts::INVALID_PTS) {
                    pts = static_cast<int64_t>(pts_val);
                }
            }
            if (pkt.hasDTS()) {
                uint64_t dts_val = pkt.getDTS();
                if (dts_val != ts::INVALID_DTS) {
                    dts = static_cast<int64_t>(dts_val);
                }
            }

            if (pts != INVALID_TS) {
                // TR 101 290: PTS repetition check (max 700ms between PTS)
                tr101290.check_pts_repetition(pid, now_ns());

                bool is_keyframe = is_video && pkt.getRandomAccessIndicator();
                av_sync.record_pts_sample(pid, slot.stream_type.load(std::memory_order_relaxed), pts, dts, packet_idx,
                                        byte_off, is_video, is_audio || is_private, is_keyframe);
            }
        }
    }

    /// Track PAT/PMT/CAT PID reception for TR 101 290 timeout checks.
    /// This operates at the packet level: every PAT/PMT/CAT packet with payload
    /// updates the timing baseline. This is independent of SectionDemux's
    /// version-filtered table delivery (which only fires on version changes).
    void track_psi_reception(uint16_t pid, ts::TSPacket& pkt, int64_t packet_idx) noexcept {
        if (!pkt.hasPayload())
            return;

        // PAT reception (PID 0x0000)
        if (pid == ts::PID_PAT) {
            tr101290.on_pat_received(packet_idx);
            return;
        }

        // CAT reception (PID 0x0001) - Conditional Access Table
        if (pid == ts::PID_CAT) {
            tr101290.on_cat_received(packet_idx);
            return;
        }

        // PMT reception (any active PMT PID)
        int32_t prog_count = psi.get_program_count();
        for (int32_t p = 0; p < prog_count; p++) {
            if (psi.programs[p].active && psi.programs[p].pmt_pid == pid) {
                psi.programs[p].last_pmt_packet_idx = packet_idx;
                break;
            }
        }
    }
};

}  // namespace tsduck_interop::context

#endif  // TSDUCK_INTEROP_CONTEXT_ANALYZER_HPP
