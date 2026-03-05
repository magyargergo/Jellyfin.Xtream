// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_STREAMING_KEYFRAME_ALIGNER_HPP
#define TSDUCK_INTEROP_STREAMING_KEYFRAME_ALIGNER_HPP

#include <cstring>
#include <cstdint>
#include <vector>
#include <array>
#include <chrono>
#include <tsduck.h>
#include "../core/constants.hpp"
#include "../core/logging.hpp"
#include "../platform/simd_memcpy.hpp"

namespace tsduck_interop::streaming {

// ============================================================================
// KeyframeAligner: Buffers data after URL switch until IDR frame is detected
// ============================================================================
//
// Problem: When switching between MPEG-TS streams mid-GOP, the decoder receives
// P/B frames without a reference I-frame, causing severe visual corruption.
//
// Solution: After a URL switch, buffer incoming packets and scan for an IDR
// (Instantaneous Decoder Refresh) frame. Only release data starting from the
// IDR frame, ensuring the decoder has a clean sync point.
//
// Detection methods (in order of priority):
// 1. TsDuck PESPacket::FindIntraImage() - handles H.264/H.265/H.266/MPEG-2
// 2. Random Access Indicator in TS adaptation field (fast path)
//
// Limitations:
// - Maximum buffer size prevents unbounded memory growth
// - If no IDR found within limit, emits data anyway (with warning)
// - Only scans video PIDs (audio doesn't need keyframe alignment)

class KeyframeAligner {
public:
    static constexpr int32_t DEFAULT_MAX_PACKETS = 2000;       // ~376KB, enough for ~1-2 GOPs
    static constexpr int32_t MIN_PACKETS_BEFORE_SCAN = 7;      // Need enough for PES header
    static constexpr size_t MAX_PES_BUFFER_SIZE = 256 * 1024;  // 256KB max per PID
    static constexpr auto DEFAULT_TIMEOUT = std::chrono::seconds(5);  // Max time to wait for IDR

    explicit KeyframeAligner(int32_t max_packets = DEFAULT_MAX_PACKETS,
                             std::chrono::milliseconds timeout = DEFAULT_TIMEOUT) noexcept
        : max_packets_(max_packets), timeout_(timeout), waiting_for_keyframe_(false),
          packets_buffered_(0), idr_packet_index_(-1) {
        buffer_.reserve(static_cast<size_t>(max_packets) * ts::PKT_SIZE);
    }

    // ========================================================================
    // Control
    // ========================================================================

    /// Start waiting for keyframe (call after URL switch).
    /// Subsequent data will be buffered until IDR is found.
    void start_waiting() noexcept {
        waiting_for_keyframe_ = true;
        packets_buffered_ = 0;
        idr_packet_index_ = -1;
        buffer_.clear();
        clear_pid_states();
        waiting_started_ = std::chrono::steady_clock::now();
        LOG_DEBUG("KeyframeAligner", "started waiting for keyframe (timeout=%lldms)",
                  static_cast<long long>(timeout_.count()));
    }

    /// Stop waiting (reset to pass-through mode).
    void stop_waiting() noexcept {
        waiting_for_keyframe_ = false;
        packets_buffered_ = 0;
        idr_packet_index_ = -1;
        buffer_.clear();
        clear_pid_states();
    }

    /// Check if currently buffering (waiting for keyframe).
    bool is_waiting() const noexcept { return waiting_for_keyframe_; }

    // ========================================================================
    // Data Processing
    // ========================================================================

    /// Result of processing a chunk of data.
    struct ProcessResult {
        const uint8_t* data;  // Data to output (may be from buffer or input)
        int32_t length;       // Bytes to output (0 if still buffering)
        bool found_keyframe;  // True if IDR was found this call
        const uint8_t* pre_idr_data;  // Pre-IDR data for SPS/PPS extraction (null if not found)
        int32_t pre_idr_length;       // Bytes before IDR frame
    };

    /// Process aligned TS data.
    /// If waiting for keyframe: buffers data, scans for IDR, returns aligned output.
    /// If not waiting: passes through unchanged.
    ProcessResult process(const uint8_t* data, int32_t length) noexcept {
        ProcessResult result{nullptr, 0, false, nullptr, 0};

        if (!waiting_for_keyframe_) {
            // Pass-through mode
            result.data = data;
            result.length = length;
            return result;
        }

        // Buffering mode: accumulate data
        int32_t new_packets = length / static_cast<int32_t>(ts::PKT_SIZE);
        size_t old_size = buffer_.size();
        buffer_.resize(old_size + static_cast<size_t>(length));
        // SIMD-optimized copy for keyframe accumulation (can be up to 376KB)
        // flawfinder: ignore - bounds checked by resize() above ensuring buffer_ can hold old_size + length
        platform::simd_memcpy(buffer_.data() + old_size, data, static_cast<size_t>(length));
        packets_buffered_ += new_packets;

        // Scan for IDR if not yet found
        if (idr_packet_index_ < 0) {
            idr_packet_index_ = scan_for_idr();
        }

        // Check if we found IDR
        if (idr_packet_index_ >= 0) {
            // Found keyframe! Output from IDR position onwards
            int32_t idr_byte_offset = idr_packet_index_ * static_cast<int32_t>(ts::PKT_SIZE);
            result.data = buffer_.data() + idr_byte_offset;
            result.length = static_cast<int32_t>(buffer_.size()) - idr_byte_offset;
            result.found_keyframe = true;

            // Provide pre-IDR data for SPS/PPS extraction
            // The caller should feed this through NAL parser before discarding
            if (idr_packet_index_ > 0) {
                result.pre_idr_data = buffer_.data();
                result.pre_idr_length = idr_byte_offset;
            }

            LOG_INFO("KeyframeAligner", "found IDR at packet %d, discarding %d packets, outputting %d packets",
                     idr_packet_index_, idr_packet_index_, result.length / static_cast<int32_t>(ts::PKT_SIZE));

            waiting_for_keyframe_ = false;
            // Don't clear buffer yet - caller needs to use the data pointer
            return result;
        }

        // Check timeout - prevents infinite buffering if no IDR is ever found
        auto elapsed = std::chrono::steady_clock::now() - waiting_started_;
        if (elapsed > timeout_) {
            auto elapsed_ms = std::chrono::duration_cast<std::chrono::milliseconds>(elapsed).count();
            LOG_WARNING("KeyframeAligner", "timeout (%lldms) waiting for IDR after %lldms, emitting %d buffered packets",
                        static_cast<long long>(timeout_.count()), static_cast<long long>(elapsed_ms), packets_buffered_);
            result.data = buffer_.data();
            result.length = static_cast<int32_t>(buffer_.size());
            result.found_keyframe = false;

            waiting_for_keyframe_ = false;
            return result;
        }

        // Check buffer limit
        if (packets_buffered_ >= max_packets_) {
            // Exceeded limit without finding IDR - emit anyway with warning
            LOG_WARNING("KeyframeAligner", "max buffer (%d packets) exceeded without finding IDR, emitting anyway",
                        max_packets_);
            result.data = buffer_.data();
            result.length = static_cast<int32_t>(buffer_.size());
            result.found_keyframe = false;

            waiting_for_keyframe_ = false;
            return result;
        }

        // Still waiting for IDR
        return result;
    }

    /// Clear the internal buffer (call after consuming ProcessResult data).
    void clear_buffer() noexcept {
        buffer_.clear();
        packets_buffered_ = 0;
        idr_packet_index_ = -1;
        clear_pid_states();
    }

    // ========================================================================
    // Diagnostics
    // ========================================================================

    int32_t packets_buffered() const noexcept { return packets_buffered_; }
    int32_t idr_packet_index() const noexcept { return idr_packet_index_; }

private:
    int32_t max_packets_;
    std::chrono::milliseconds timeout_;
    bool waiting_for_keyframe_;
    int32_t packets_buffered_;
    int32_t idr_packet_index_;
    std::chrono::steady_clock::time_point waiting_started_;
    std::vector<uint8_t> buffer_;

    // Per-PID state for PES accumulation
    struct PidState {
        bool is_video = false;
        bool found_pes_start = false;
        int32_t pes_start_packet_index = -1;  // Which packet started this PES
        uint8_t stream_type = ts::ST_NULL;
        std::vector<uint8_t> pes_buffer;

        void reset() noexcept {
            is_video = false;
            found_pes_start = false;
            pes_start_packet_index = -1;
            stream_type = ts::ST_NULL;
            pes_buffer.clear();
        }
    };
    std::array<PidState, 8192> pid_states_{};

    void clear_pid_states() noexcept {
        for (auto& state : pid_states_) {
            state.reset();
        }
    }

    /// Scan buffered packets for IDR frame using TsDuck's FindIntraImage.
    /// Returns packet index of first IDR, or -1 if not found.
    int32_t scan_for_idr() noexcept {
        const ts::TSPacket* packets = reinterpret_cast<const ts::TSPacket*>(buffer_.data());

        for (int32_t i = 0; i < packets_buffered_; i++) {
            const ts::TSPacket& pkt = packets[i];
            if (!pkt.hasValidSync())
                continue;

            ts::PID pid = pkt.getPID();
            if (pid >= 8192)
                continue;

            auto& state = pid_states_[pid];

            // Fast path: Check Random Access Indicator
            // If RAI is set on a video PID, this is likely a keyframe
            if (pkt.getRandomAccessIndicator() && state.is_video) {
                LOG_DEBUG("KeyframeAligner", "found RAI on video PID %d at packet %d", pid, i);
                return i;
            }

            // PES start - identify video PIDs and start accumulating
            if (pkt.startPES()) {
                const uint8_t* payload = pkt.getPayload();
                size_t payload_size = pkt.getPayloadSize();

                if (payload_size >= 4) {
                    uint8_t stream_id = payload[3];
                    bool is_video = ts::IsVideoSID(stream_id);

                    state.is_video = is_video;
                    state.found_pes_start = true;
                    state.pes_start_packet_index = i;
                    state.pes_buffer.clear();

                    // Check RAI on video PUSI packet
                    if (is_video && pkt.getRandomAccessIndicator()) {
                        LOG_DEBUG("KeyframeAligner", "found RAI on video PUSI PID %d at packet %d", pid, i);
                        return i;
                    }

                    if (is_video) {
                        // Accumulate the entire PES payload starting from byte 0
                        // TsDuck's FindIntraImage expects PES data including header
                        // Note: payload_size is guaranteed >= 4 from outer condition
                        state.pes_buffer.insert(state.pes_buffer.end(), payload, payload + payload_size);

                        // Try to detect intra-image using TsDuck
                        size_t intra_offset = ts::PESPacket::FindIntraImage(state.pes_buffer.data(),
                                                                            state.pes_buffer.size(), state.stream_type);

                        if (intra_offset != ts::NPOS) {
                            LOG_DEBUG("KeyframeAligner",
                                      "found intra-image via TsDuck on PID %d at packet %d (offset %zu)", pid, i,
                                      intra_offset);
                            return i;
                        }
                    }
                }
            } else if (state.is_video && state.found_pes_start && pkt.hasPayload()) {
                // Continuation packet for video PID - accumulate PES data
                const uint8_t* payload = pkt.getPayload();
                size_t payload_size = pkt.getPayloadSize();

                // Limit PES buffer size to avoid unbounded growth
                if (state.pes_buffer.size() + payload_size <= MAX_PES_BUFFER_SIZE) {
                    state.pes_buffer.insert(state.pes_buffer.end(), payload, payload + payload_size);

                    // Try to detect intra-image
                    size_t intra_offset = ts::PESPacket::FindIntraImage(state.pes_buffer.data(),
                                                                        state.pes_buffer.size(), state.stream_type);

                    if (intra_offset != ts::NPOS) {
                        // Found intra-image - return the packet where this PES started
                        LOG_DEBUG("KeyframeAligner",
                                  "found intra-image via TsDuck in PES continuation, PID %d, PES start at packet %d",
                                  pid, state.pes_start_packet_index);
                        return state.pes_start_packet_index;
                    }
                }
            }
        }

        return -1;  // No IDR found
    }
};

}  // namespace tsduck_interop::streaming

#endif  // TSDUCK_INTEROP_STREAMING_KEYFRAME_ALIGNER_HPP
