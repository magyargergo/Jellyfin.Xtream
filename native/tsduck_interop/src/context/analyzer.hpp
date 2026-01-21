// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_CONTEXT_ANALYZER_HPP
#define TSDUCK_INTEROP_CONTEXT_ANALYZER_HPP

#include <atomic>
#include <array>
#include <chrono>
#include <cstring>
#include <vector>
#include <tsduck.h>

#include "../core/constants.hpp"
#include "../concurrency/seqlock.hpp"
#include "../analysis/pcr_analyzer.hpp"
#include "../analysis/iat_analyzer.hpp"
#include "../analysis/pid_tracker.hpp"
#include "../analysis/av_sync_tracker.hpp"
#include "../mpegts/packet_utils.hpp"
#include "context.hpp"
#include "tsduck_interop.h"

namespace tsduck_interop {
namespace context {

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
    ts::TSAnalyzer ts_analyzer;
    TsDuckConfigNative config;

    // TSDuck Native Analyzers (delegating to TSDuck for what it does best)
    ts::ContinuityAnalyzer cc_analyzer;   // Continuity counter errors
    ts::PCRAnalyzer pcr_bitrate_analyzer; // PCR-based bitrate calculation

    // Lock-Free Metrics State
    alignas(CACHE_LINE_SIZE) MetricsBuffer metrics;
    alignas(CACHE_LINE_SIZE) mutable std::atomic<bool> has_new_metrics{false};  // mutable for const getMetrics()
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> last_metrics_time_ns{0};

    // Custom Analysis Components (unique to our implementation)
    alignas(CACHE_LINE_SIZE) analysis::AvSyncTracker av_sync;  // A/V drift - TSDuck doesn't have this
    alignas(CACHE_LINE_SIZE) analysis::PcrAnalyzer pcr;        // Our PCR jitter tracking for real-time callbacks
    alignas(CACHE_LINE_SIZE) analysis::IatAnalyzer iat;        // Network jitter - TSDuck doesn't have this
    alignas(CACHE_LINE_SIZE) BitrateBuffer bitrate;
    alignas(CACHE_LINE_SIZE) analysis::PidTracker pids;        // Per-PID stats (simplified, CC from TSDuck)

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

    // Start time for bitrate calculation
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> start_time_ns{0};

    TsDuckAnalyzer(TsDuckContext* ctx, const TsDuckConfigNative* cfg)
        : context(ctx)
        , ts_analyzer(ctx->duck)
        , cc_analyzer(ts::AllPIDs())  // Monitor all PIDs for CC errors
        , pcr_bitrate_analyzer()
    {
        if (cfg) {
            config = *cfg;
        } else {
            config.metrics_interval_ms = 1000;
            config.enable_tr101290 = 1;
            config.sample_size_bytes = TS_PACKET_SIZE * 1000;
            config.reserved = 0;
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

    void processPcr(const ts::TSPacket& packet, int64_t packet_index) noexcept {
        if (!packet.hasPCR()) {
            return;
        }

        uint64_t pcr_value = packet.getPCR();
        int64_t current_time = now_ns();

        pcr.process(pcr_value, packet_index, current_time);

        // Update A/V sync tracker with PCR base (convert from 27MHz to 90kHz)
        int64_t pcr_base_90khz = static_cast<int64_t>(pcr_value / 300);
        av_sync.updatePcrReference(pcr_base_90khz);
    }

    void processIat() noexcept {
        iat.process(now_ns());
    }

    void processBitrate(const ts::TSPacket& packet) noexcept {
        total_packet_count.fetch_add(1, std::memory_order_relaxed);

        // Check for null packet (PID 0x1FFF)
        if (packet.getPID() == 0x1FFF) {
            null_packet_count.fetch_add(1, std::memory_order_relaxed);
        }
    }

    void processPidInfo(const ts::TSPacket& packet) noexcept {
        uint16_t pid = packet.getPID();
        uint8_t tsc = packet.getScrambling();
        bool scrambled = (tsc != 0);
        int64_t time = now_ns();

        // Process packet (CC errors handled by TSDuck's ContinuityAnalyzer)
        pids.processPacket(pid, scrambled, time);

        // Mark PCR PID and copy jitter
        if (packet.hasPCR()) {
            pids.setPcrPid(pid, true);

            // Copy PCR jitter from our PCR analysis to PID slot
            PcrAnalysisNative pcr_data;
            if (pcr.get(&pcr_data)) {
                pids.setPcrJitter(pid, pcr_data.pcr_jitter_us);
            }
        }
    }

    void updateMetrics() noexcept {
        int64_t current_time = now_ns();
        int64_t last_time = last_metrics_time_ns.load(std::memory_order_relaxed);
        int64_t elapsed_ms = (current_time - last_time) / 1000000;

        if (elapsed_ms < config.metrics_interval_ms) {
            return;
        }

        last_metrics_time_ns.store(current_time, std::memory_order_release);

        auto seq = metrics.seqlock.begin_write();

        metrics.data.timestamp_ticks = get_dotnet_ticks();

        // Get PID and service counts from TSDuck analyzer
        std::vector<ts::PID> pid_list;
        std::vector<uint16_t> services;
        ts_analyzer.getPIDs(pid_list);
        ts_analyzer.getServiceIds(services);

        metrics.data.pid_count = static_cast<int32_t>(pid_list.size());
        metrics.data.service_count = static_cast<int32_t>(services.size());

        // Get unreferenced PIDs from TSDuck
        std::vector<ts::PID> unreferenced_pids;
        ts_analyzer.getUnreferencedPIDs(unreferenced_pids);

        // Get bitrate - prefer TSDuck's PCR-based calculation, fallback to packet counting
        ts::BitRate pcr_bitrate = pcr_bitrate_analyzer.bitrate188();
        if (pcr_bitrate > 0) {
            // TSDuck PCR-based bitrate (more accurate)
            metrics.data.ts_bitrate = static_cast<int64_t>(pcr_bitrate.toInt());
        } else {
            // Fallback: calculate from packet count and elapsed time
            int64_t start = start_time_ns.load(std::memory_order_relaxed);
            int64_t total_time_ms = (current_time - start) / 1000000;
            if (total_time_ms > 0) {
                int64_t bytes = packets_processed.load(std::memory_order_relaxed) * TS_PACKET_SIZE;
                metrics.data.ts_bitrate = (bytes * 8 * 1000) / total_time_ms;
            }
        }

        // TR 101 290 metrics
        if (config.enable_tr101290) {
            // Get CC errors from TSDuck's ContinuityAnalyzer (authoritative source)
            int64_t total_cc_errors = static_cast<int64_t>(cc_analyzer.errorCount());

            metrics.data.priority1.sync_byte_error = 0;
            metrics.data.priority1.sync_loss = 0;
            metrics.data.priority1.pat_error = 0;
            metrics.data.priority1.pat_error_2 = 0;
            metrics.data.priority1.continuity_count_error = total_cc_errors;
            metrics.data.priority1.pmt_error = 0;
            metrics.data.priority1.pmt_error_2 = 0;
            metrics.data.priority1.pid_error = static_cast<int64_t>(unreferenced_pids.size());

            metrics.data.priority2.transport_error = 0;
            metrics.data.priority2.crc_error = 0;
            metrics.data.priority2.pcr_repetition_error = 0;
            metrics.data.priority2.pcr_discontinuity_error = 0;
            metrics.data.priority2.pcr_accuracy_error = 0;
            metrics.data.priority2.pts_error = 0;
            metrics.data.priority2.cat_error = 0;
        }

        metrics.seqlock.end_write(seq);

        // Update bitrate analysis
        int64_t total_packets = total_packet_count.load(std::memory_order_relaxed);
        int64_t null_packets = null_packet_count.load(std::memory_order_relaxed);

        auto bitrate_seq = bitrate.seqlock.begin_write();
        bitrate.data.ts_bitrate_nominal = metrics.data.ts_bitrate;

        if (total_packets > 0) {
            bitrate.data.null_packet_ratio =
                static_cast<double>(null_packets) / static_cast<double>(total_packets);
            bitrate.data.null_packet_bitrate =
                static_cast<int64_t>(metrics.data.ts_bitrate * bitrate.data.null_packet_ratio);
            bitrate.data.useful_bitrate =
                metrics.data.ts_bitrate - bitrate.data.null_packet_bitrate;
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

        int32_t packets = length / TS_PACKET_SIZE;
        if (packets == 0) {
            return 0;
        }

        int64_t base_packet_index = packets_processed.load(std::memory_order_relaxed);
        ts::TSPacketMetadata mdata;

        for (int32_t i = 0; i < packets; i++) {
            const uint8_t* packet_data = data + (i * TS_PACKET_SIZE);

            if (packet_data[0] != TS_SYNC_BYTE) {
                continue;
            }

            ts::TSPacket packet;
            std::memcpy(packet.b, packet_data, TS_PACKET_SIZE);

            // Feed to TSDuck native analyzers
            ts_analyzer.feedPacket(packet, mdata);
            cc_analyzer.feedPacket(packet);           // TSDuck handles CC errors
            pcr_bitrate_analyzer.feedPacket(packet);  // TSDuck handles PCR-based bitrate

            // Our custom analysis (unique functionality)
            processPcr(packet, base_packet_index + i);  // PCR jitter for real-time callbacks
            processIat();                               // Network IAT - not in TSDuck
            processBitrate(packet);                     // Null packet tracking
            processPidInfo(packet);                     // Per-PID stats

            // Extract PTS/DTS for A/V sync tracking (not in TSDuck)
            processPesPts(packet, base_packet_index + i, i * TS_PACKET_SIZE);
        }

        packets_processed.fetch_add(packets, std::memory_order_release);
        updateMetrics();

        return packets;
    }

    void reset() noexcept {
        // Reset TSDuck native analyzers
        ts_analyzer.reset();
        cc_analyzer.reset();
        pcr_bitrate_analyzer.reset();

        packets_processed.store(0, std::memory_order_release);
        total_packet_count.store(0, std::memory_order_release);
        null_packet_count.store(0, std::memory_order_release);

        // Reset previous error counts
        prev_cc_errors = 0;
        prev_transport_errors = 0;
        prev_crc_errors = 0;
        prev_pcr_errors = 0;

        // Reset custom analysis components
        pcr.reset();   // Our PCR jitter tracker (for callbacks)
        iat.reset();   // Network IAT - unique to us
        pids.reset();  // Per-PID stats
        av_sync.reset(); // A/V sync - unique to us

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

    bool getMetrics(TsDuckMetricsNative* out) const noexcept {
        if (!out) return false;

        uint64_t seq;
        do {
            seq = metrics.seqlock.begin_read();
            *out = metrics.data;
        } while (!metrics.seqlock.read_consistent(seq));

        has_new_metrics.store(false, std::memory_order_release);
        return true;
    }

    bool getBitrateAnalysis(BitrateAnalysisNative* out) const noexcept {
        if (!out) return false;

        uint64_t seq;
        do {
            seq = bitrate.seqlock.begin_read();
            *out = bitrate.data;
        } while (!bitrate.seqlock.read_consistent(seq));

        return total_packet_count.load(std::memory_order_acquire) > 0;
    }

private:
    void processPesPts(const ts::TSPacket& packet, int64_t packet_idx, int64_t byte_off) noexcept {
        // Check if packet has payload and starts a PES packet
        if (!packet.hasPayload() || !packet.getPUSI()) {
            return;
        }

        uint16_t pid = packet.getPID();

        // Skip non-media PIDs
        if (pid < 0x100 || pid >= 0x1FFF) {
            return;
        }

        // Get payload
        const uint8_t* payload = packet.getPayload();
        size_t payload_size = packet.getPayloadSize();

        // Check for PES start code (minimum 4 bytes needed)
        if (payload_size < 4 ||
            payload[0] != 0x00 || payload[1] != 0x00 || payload[2] != 0x01) {
            return;
        }

        // Detect stream type from PES header stream_id
        uint8_t stream_id = payload[3];
        bool is_video = (stream_id >= 0xE0 && stream_id <= 0xEF);
        bool is_audio = (stream_id >= 0xC0 && stream_id <= 0xDF);
        bool is_private = (stream_id == 0xBD);  // Private stream 1 (AC3, DTS, etc.)

        // Set stream type flags in PID slot
        auto& slot = pids.slots[pid];
        if (is_video) {
            slot.is_video.store(true, std::memory_order_release);
            slot.stream_type.store(0x02, std::memory_order_release);
        } else if (is_audio || is_private) {
            slot.is_audio.store(true, std::memory_order_release);
            slot.stream_type.store(0x03, std::memory_order_release);
        }

        // Extract PTS/DTS if this is a video/audio stream and header is long enough
        if ((is_video || is_audio || is_private) && payload_size >= 14) {
            uint8_t pts_dts_flags = (payload[7] >> 6) & 0x03;
            uint8_t pes_header_len = payload[8];

            // PTS present (flags = 2 or 3)
            if (pts_dts_flags >= 2 && pes_header_len >= 5 && payload_size >= 14) {
                int64_t pts = mpegts::extractPts(&payload[9]);
                int64_t dts = -1;

                // DTS also present (flags = 3)
                if (pts_dts_flags == 3 && pes_header_len >= 10 && payload_size >= 19) {
                    dts = mpegts::extractPts(&payload[14]);
                }

                // Check for keyframe (for video, look for RAI in adaptation field)
                bool is_keyframe = false;
                if (is_video && packet.getAFSize() > 0) {
                    const uint8_t* pkt_bytes = packet.b;
                    if (pkt_bytes[4] > 0 && (pkt_bytes[5] & 0x40) != 0) {
                        is_keyframe = true;
                    }
                }

                // Record the PTS/DTS sample
                av_sync.recordPtsSample(
                    pid,
                    slot.stream_type.load(std::memory_order_relaxed),
                    pts,
                    dts,
                    packet_idx,
                    byte_off,
                    is_video,
                    is_audio || is_private,
                    is_keyframe
                );
            }
        }
    }
};

}  // namespace context
}  // namespace tsduck_interop

#endif  // TSDUCK_INTEROP_CONTEXT_ANALYZER_HPP
