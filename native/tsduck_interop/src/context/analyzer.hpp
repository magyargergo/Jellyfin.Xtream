// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_CONTEXT_ANALYZER_HPP
#define TSDUCK_INTEROP_CONTEXT_ANALYZER_HPP

#include <atomic>
#include <array>
#include <chrono>
#include <cstring>
#include <memory>
#include <vector>

#include "../core/constants.hpp"
#include "../concurrency/seqlock.hpp"
#include "../analysis/pcr_analyzer.hpp"
#include "../analysis/iat_analyzer.hpp"
#include "../analysis/pid_tracker.hpp"
#include "../analysis/av_sync_tracker.hpp"
#include "../analysis/psi_monitor.hpp"
#include "../analysis/tr101290.hpp"
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

class TsDuckAnalyzer {
public:
    TsDuckContext* context;
    TsDuckConfigNative config;

    // Lock-Free Metrics State
    alignas(CACHE_LINE_SIZE) MetricsBuffer metrics;
    alignas(CACHE_LINE_SIZE) mutable std::atomic<bool> has_new_metrics{false};  // mutable for const get_metrics()
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> last_metrics_time_ns{0};

    // Custom Analysis Components (unique to our implementation)
    alignas(CACHE_LINE_SIZE) analysis::AvSyncTracker av_sync;  // A/V drift - TSDuck doesn't have this
    alignas(CACHE_LINE_SIZE) analysis::PcrAnalyzer pcr;        // Our PCR jitter tracking for real-time callbacks
    alignas(CACHE_LINE_SIZE) analysis::IatAnalyzer iat;        // Network jitter - TSDuck doesn't have this
    alignas(CACHE_LINE_SIZE) BitrateBuffer bitrate;
    alignas(CACHE_LINE_SIZE) analysis::PidTracker pids;           // Per-PID stats (simplified, CC from TSDuck)
    alignas(CACHE_LINE_SIZE) analysis::PsiMonitor psi;            // PAT/PMT table parsing
    alignas(CACHE_LINE_SIZE) analysis::Tr101290Monitor tr101290;  // TR 101 290 quality monitor

    // Callbacks
    std::atomic<TsDuckMetricsCallback> metrics_callback{nullptr};
    std::atomic<void*> metrics_user_data{nullptr};
    std::atomic<TsDuckViolationCallback> violation_callback{nullptr};
    std::atomic<void*> violation_user_data{nullptr};

    // Previous error counts for violation detection
    int64_t prev_cc_errors{0};
    int64_t prev_transport_errors{0};
    int64_t prev_crc_errors{0};
    int64_t prev_pcr_errors{0};

    // Packet counter
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> packets_processed{0};

    // Atomic counters for bitrate calculation
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> null_packet_count{0};
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> total_packet_count{0};

    // Thresholds
    std::atomic<double> pcr_jitter_threshold_us{DEFAULT_PCR_JITTER_THRESHOLD_US};
    std::atomic<double> iat_jitter_threshold_us{DEFAULT_IAT_JITTER_THRESHOLD_US};

    // Start time for bitrate calculation (reset on first data arrival)
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> start_time_ns{0};
    alignas(CACHE_LINE_SIZE) std::atomic<bool> first_feed_received{false};

    // Integrated restamping (optional)
    alignas(CACHE_LINE_SIZE) std::unique_ptr<restamping::Restamper> restamper;
    alignas(CACHE_LINE_SIZE) std::atomic<bool> auto_restamp_enabled{false};

    TsDuckAnalyzer(TsDuckContext* ctx, const TsDuckConfigNative* cfg) : context(ctx), psi(ctx->duck) {
        if (cfg) {
            config = *cfg;
        } else {
            config.metrics_interval_ms = 1000;
            config.enable_tr101290 = 1;
            config.sample_size_bytes = static_cast<int32_t>(ts::PKT_SIZE) * 1000;
            config.enable_auto_restamp = 1;
            config.restamp_mode = RESTAMP_MODE_CORRECT;
            config.smooth_pcr = 1;
            config.fix_discontinuities = 1;
            config.reserved = 0;
            config.correction_threshold_ms = DEFAULT_CORRECTION_THRESHOLD_MS;
            config.max_correction_rate_ms = DEFAULT_MAX_CORRECTION_RATE_MS;
            config.hysteresis_threshold_ms = DEFAULT_HYSTERESIS_THRESHOLD_MS;
            config.stream_bitrate_hint = 0;
        }

        // Initialize integrated restamper if enabled
        if (config.enable_auto_restamp && config.restamp_mode != RESTAMP_MODE_DISABLED) {
            RestampingConfigNative restamp_cfg{};
            restamp_cfg.mode = config.restamp_mode;
            restamp_cfg.smooth_pcr = config.smooth_pcr;
            restamp_cfg.fix_discontinuities = config.fix_discontinuities;
            restamp_cfg.correction_threshold_ms = config.correction_threshold_ms;
            restamp_cfg.max_correction_rate_ms = config.max_correction_rate_ms;
            restamp_cfg.hysteresis_threshold_ms = config.hysteresis_threshold_ms;
            restamp_cfg.stream_bitrate_hint = config.stream_bitrate_hint;

            restamper = std::make_unique<restamping::Restamper>(&av_sync, &restamp_cfg);
            auto_restamp_enabled.store(true, std::memory_order_release);
        }

        auto now = std::chrono::steady_clock::now().time_since_epoch();
        auto now_ns_val = std::chrono::duration_cast<std::chrono::nanoseconds>(now).count();

        start_time_ns.store(now_ns_val, std::memory_order_release);
        last_metrics_time_ns.store(now_ns_val, std::memory_order_release);
    }

    static int64_t now_ns() noexcept {
        auto now = std::chrono::steady_clock::now().time_since_epoch();
        return std::chrono::duration_cast<std::chrono::nanoseconds>(now).count();
    }

    static int64_t get_dotnet_ticks() noexcept {
        auto sys_now = std::chrono::system_clock::now();
        auto duration = sys_now.time_since_epoch();
        auto ticks = std::chrono::duration_cast<std::chrono::nanoseconds>(duration).count() / 100;
        return ticks + 621355968000000000LL;
    }

    void process_pcr(ts::TSPacket& pkt, int64_t packet_index) noexcept {
        if (!pkt.hasPCR())
            return;
        uint64_t pcr_value = pkt.getPCR();
        if (pcr_value == ts::INVALID_PCR)
            return;
        int64_t current_time = now_ns();
        pcr.process(pcr_value, packet_index, current_time);
        int64_t pcr_base_90khz = static_cast<int64_t>(pcr_value / ts::SYSTEM_CLOCK_SUBFACTOR);
        av_sync.update_pcr_reference(pcr_base_90khz);
    }

    void process_iat() noexcept { iat.process(now_ns()); }

    void process_bitrate(uint16_t pid) noexcept {
        total_packet_count.fetch_add(1, std::memory_order_relaxed);

        // Check for null packet
        if (pid == ts::PID_NULL) {
            null_packet_count.fetch_add(1, std::memory_order_relaxed);
        }
    }

    void process_pid_info(ts::TSPacket& pkt, uint16_t pid) noexcept {
        bool scrambled = pkt.isScrambled();
        int64_t time = now_ns();
        pids.process_packet(pid, pkt.getCC(), pkt.hasPayload(), scrambled, time);

        if (pkt.hasPCR()) {
            pids.set_pcr_pid(pid, true);
            PcrAnalysisNative pcr_data;
            if (pcr.get(&pcr_data)) {
                pids.set_pcr_jitter(pid, pcr_data.pcr_jitter_us);
            }
        }
    }

    void update_metrics() noexcept {
        int64_t current_time = now_ns();
        int64_t last_time = last_metrics_time_ns.load(std::memory_order_relaxed);
        int64_t elapsed_ms = (current_time - last_time) / 1000000;

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

    int32_t feed(const uint8_t* data, int32_t length) noexcept {
        if (!data || length <= 0) {
            return TSDUCK_ERROR_INVALID_DATA;
        }

        int32_t packets = length / static_cast<int32_t>(ts::PKT_SIZE);
        if (packets == 0) {
            return 0;
        }

        // Reset start time on first data arrival so bitrate isn't diluted
        // by any pre-data idle period (e.g., delayed HTTP response body).
        if (!first_feed_received.load(std::memory_order_relaxed)) {
            first_feed_received.store(true, std::memory_order_release);
            auto now_val = now_ns();
            start_time_ns.store(now_val, std::memory_order_release);
            last_metrics_time_ns.store(now_val, std::memory_order_release);
        }

        int64_t base_packet_index = packets_processed.load(std::memory_order_relaxed);

        const ts::TSPacket* pkt_array = reinterpret_cast<const ts::TSPacket*>(data);
        for (int32_t i = 0; i < packets; i++) {
            // const_cast safe: feed() is read-only analysis
            ts::TSPacket& pkt = const_cast<ts::TSPacket&>(pkt_array[i]);
            int64_t packet_idx = base_packet_index + i;

            // TR 101 290: sync check (even for invalid packets)
            bool valid_sync = pkt.hasValidSync();
            tr101290.check_sync(valid_sync);
            if (!valid_sync)
                continue;

            uint16_t pid = pkt.getPID();

            // TR 101 290 Priority 2: Transport Error Indicator
            tr101290.check_transport_error(pkt);

            // TR 101 290: Track PAT/PMT PID reception for timeout checks
            track_psi_reception(pid, pkt, packet_idx);

            // PSI accumulation — SectionDemux handles PID filtering internally
            psi.feed_packet(pkt, packet_idx);
            apply_psi_updates();

            process_pcr(pkt, packet_idx);

            // TR 101 290: PCR checks
            if (pkt.hasPCR()) {
                uint64_t pcr_val = pkt.getPCR();
                if (pcr_val != ts::INVALID_PCR) {
                    tr101290.check_pcr_repetition(pid, packet_idx);
                    tr101290.check_pcr_accuracy(pid, pcr_val, packet_idx);
                    tr101290.check_pcr_discontinuity(pkt, pid, pcr_val);
                }
            }

            process_iat();
            process_bitrate(pid);
            process_pid_info(pkt, pid);
            process_pes_pts(pkt, pid, packet_idx, i * static_cast<int32_t>(ts::PKT_SIZE));
        }

        packets_processed.fetch_add(packets, std::memory_order_release);
        update_metrics();

        return packets;
    }

    /// Feed MPEG-TS data with integrated restamping.
    /// This function modifies data IN-PLACE to apply timestamp corrections,
    /// then analyzes the corrected data.
    /// @param data MPEG-TS data to process (will be modified if restamping enabled).
    /// @param length Number of bytes.
    /// @return Number of packets processed, or negative error code.
    int32_t feed_and_restamp(uint8_t* data, int32_t length) noexcept {
        if (!data || length <= 0) {
            return TSDUCK_ERROR_INVALID_DATA;
        }

        int32_t packets = length / static_cast<int32_t>(ts::PKT_SIZE);
        if (packets == 0) {
            return 0;
        }

        // Reset start time on first data arrival so bitrate isn't diluted
        // by any pre-data idle period (e.g., delayed HTTP response body).
        if (!first_feed_received.load(std::memory_order_relaxed)) {
            first_feed_received.store(true, std::memory_order_release);
            auto now_val = now_ns();
            start_time_ns.store(now_val, std::memory_order_release);
            last_metrics_time_ns.store(now_val, std::memory_order_release);
        }

        int64_t base_packet_index = packets_processed.load(std::memory_order_relaxed);

        // Apply restamping BEFORE analysis (modifies data in-place)
        if (auto_restamp_enabled.load(std::memory_order_acquire) && restamper) {
            restamper->process(data, length, base_packet_index);
        }

        ts::TSPacket* pkt_array = reinterpret_cast<ts::TSPacket*>(data);
        for (int32_t i = 0; i < packets; i++) {
            ts::TSPacket& pkt = pkt_array[i];
            int64_t packet_idx = base_packet_index + i;

            // TR 101 290: sync check (even for invalid packets)
            bool valid_sync = pkt.hasValidSync();
            tr101290.check_sync(valid_sync);
            if (!valid_sync)
                continue;

            uint16_t pid = pkt.getPID();

            // TR 101 290 Priority 2: Transport Error Indicator
            tr101290.check_transport_error(pkt);

            // TR 101 290: Track PAT/PMT PID reception for timeout checks
            track_psi_reception(pid, pkt, packet_idx);

            // PSI accumulation — SectionDemux handles PID filtering internally
            psi.feed_packet(pkt, packet_idx);
            apply_psi_updates();

            process_pcr(pkt, packet_idx);

            // TR 101 290: PCR checks
            if (pkt.hasPCR()) {
                uint64_t pcr_val = pkt.getPCR();
                if (pcr_val != ts::INVALID_PCR) {
                    tr101290.check_pcr_repetition(pid, packet_idx);
                    tr101290.check_pcr_accuracy(pid, pcr_val, packet_idx);
                    tr101290.check_pcr_discontinuity(pkt, pid, pcr_val);
                }
            }

            process_iat();
            process_bitrate(pid);
            process_pid_info(pkt, pid);
            process_pes_pts(pkt, pid, packet_idx, i * static_cast<int32_t>(ts::PKT_SIZE));
        }

        packets_processed.fetch_add(packets, std::memory_order_release);
        update_metrics();

        return packets;
    }

    /// Configure integrated restamping at runtime.
    void configure_restamp(int32_t mode, int32_t smooth_pcr, int32_t fix_discontinuities) noexcept {
        if (mode == RESTAMP_MODE_DISABLED) {
            auto_restamp_enabled.store(false, std::memory_order_release);
            return;
        }

        // Create restamper if it doesn't exist
        if (!restamper) {
            RestampingConfigNative cfg{};
            cfg.mode = mode;
            cfg.smooth_pcr = smooth_pcr;
            cfg.fix_discontinuities = fix_discontinuities;
            cfg.correction_threshold_ms = config.correction_threshold_ms;
            cfg.max_correction_rate_ms = config.max_correction_rate_ms;
            cfg.hysteresis_threshold_ms = config.hysteresis_threshold_ms;
            cfg.stream_bitrate_hint = config.stream_bitrate_hint;

            restamper = std::make_unique<restamping::Restamper>(&av_sync, &cfg);
        } else {
            // Update existing restamper configuration
            restamper->config.mode = mode;
            restamper->config.smooth_pcr = smooth_pcr;
            restamper->config.fix_discontinuities = fix_discontinuities;
        }

        auto_restamp_enabled.store(true, std::memory_order_release);
    }

    /// Handle provider switch for timestamp continuity.
    void handle_switch(int64_t last_output_pts, int64_t new_input_first_pts) noexcept {
        if (restamper) {
            restamper->handle_switch(last_output_pts, new_input_first_pts);
        }
    }

    /// Get restamping statistics.
    bool get_restamp_statistics(RestampingStatisticsNative* out) const noexcept {
        if (!restamper || !out) {
            return false;
        }
        return restamper->get_statistics(out);
    }

    /// Check if restamping is enabled.
    bool is_restamping_enabled() const noexcept { return auto_restamp_enabled.load(std::memory_order_acquire); }

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

    bool get_metrics(TsDuckMetricsNative* out) const noexcept {
        if (!out)
            return false;

        uint64_t seq;
        do {
            seq = metrics.seqlock.begin_read();
            *out = metrics.data;
        } while (!metrics.seqlock.read_consistent(seq));

        has_new_metrics.store(false, std::memory_order_release);
        return true;
    }

    bool get_bitrate_analysis(BitrateAnalysisNative* out) const noexcept {
        if (!out)
            return false;

        uint64_t seq;
        do {
            seq = bitrate.seqlock.begin_read();
            *out = bitrate.data;
        } while (!bitrate.seqlock.read_consistent(seq));

        return total_packet_count.load(std::memory_order_acquire) > 0;
    }

private:
    /// Apply PSI discoveries to PID tracker and TR 101 290 monitor.
    /// Called after PSI feedPacket when PAT/PMT may have been parsed.
    void apply_psi_updates() noexcept {
        // Note: PAT/PMT reception timing for TR 101 290 is tracked at the
        // packet level (track_psi_reception), NOT here. SectionDemux uses version
        // filtering, so handlePat/handlePmt only fire on version changes.
        // TR 101 290 requires tracking every PAT/PMT packet reception.

        // Apply PMT stream types to PID tracker (overrides PES stream_id guesses)
        int32_t prog_count = psi.get_program_count();
        for (int32_t p = 0; p < prog_count; p++) {
            const auto& prog = psi.programs[p];
            if (!prog.active || !prog.pmt_received)
                continue;

            // Set PCR PID
            if (prog.pcr_pid != 0x1FFF) {
                pids.set_pcr_pid(prog.pcr_pid, true);
            }

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
                } else if (es.is_audio) {
                    slot.is_audio.store(true, std::memory_order_release);
                    slot.is_video.store(false, std::memory_order_release);
                }
            }
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
        } else if (is_audio || is_private) {
            slot.is_audio.store(true, std::memory_order_release);
            slot.stream_type.store(ts::ST_MPEG2_AUDIO, std::memory_order_release);
        }

        // Extract PTS/DTS via TsDuck
        if (is_video || is_audio || is_private) {
            int64_t pts = -1, dts = -1;
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

            if (pts >= 0) {
                // TR 101 290: PTS repetition check (max 700ms between PTS)
                tr101290.check_pts_repetition(pid, now_ns());

                bool is_keyframe = is_video && pkt.getRandomAccessIndicator();
                av_sync.record_pts_sample(pid, slot.stream_type.load(std::memory_order_relaxed), pts, dts, packet_idx,
                                        byte_off, is_video, is_audio || is_private, is_keyframe);
            }
        }
    }

    /// Track PAT/PMT PID reception for TR 101 290 timeout checks.
    /// This operates at the packet level: every PAT/PMT packet with payload
    /// updates the timing baseline. This is independent of SectionDemux's
    /// version-filtered table delivery (which only fires on version changes).
    void track_psi_reception(uint16_t pid, ts::TSPacket& pkt, int64_t packet_idx) noexcept {
        if (!pkt.hasPayload())
            return;

        // PAT reception (PID 0)
        if (pid == ts::PID_PAT) {
            tr101290.on_pat_received(packet_idx);
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
