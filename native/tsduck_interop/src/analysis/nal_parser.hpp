// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_ANALYSIS_NAL_PARSER_HPP
#define TSDUCK_INTEROP_ANALYSIS_NAL_PARSER_HPP

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstring>
#include <optional>
#include <vector>
#include <tsduck.h>

#include "../concurrency/seqlock.hpp"
#include "../core/constants.hpp"
#include "../core/types.hpp"
#include "tsduck_interop.h"

namespace tsduck_interop::analysis {

// ============================================================================
// NAL Unit Type Constants
// ============================================================================

// H.264/AVC NAL unit types (nal_unit_type in 5 LSBs)
namespace h264 {
    inline constexpr std::uint8_t NAL_TYPE_MASK = 0x1F;
    inline constexpr std::uint8_t NAL_SLICE_NON_IDR = 1;
    inline constexpr std::uint8_t NAL_SLICE_A = 2;
    inline constexpr std::uint8_t NAL_SLICE_B = 3;
    inline constexpr std::uint8_t NAL_SLICE_C = 4;
    inline constexpr std::uint8_t NAL_IDR = 5;
    inline constexpr std::uint8_t NAL_SEI = 6;
    inline constexpr std::uint8_t NAL_SPS = 7;
    inline constexpr std::uint8_t NAL_PPS = 8;
    inline constexpr std::uint8_t NAL_AUD = 9;
    inline constexpr std::uint8_t NAL_END_SEQUENCE = 10;
    inline constexpr std::uint8_t NAL_END_STREAM = 11;
    inline constexpr std::uint8_t NAL_FILLER = 12;
    inline constexpr std::uint8_t NAL_SPS_EXT = 13;
}  // namespace h264

// H.265/HEVC NAL unit types (nal_unit_type in bits 1-6 of first byte)
namespace hevc {
    inline constexpr std::uint8_t NAL_TYPE_MASK = 0x7E;
    inline constexpr std::uint8_t NAL_TYPE_SHIFT = 1;
    inline constexpr std::uint8_t NAL_TRAIL_N = 0;
    inline constexpr std::uint8_t NAL_TRAIL_R = 1;
    inline constexpr std::uint8_t NAL_IDR_W_RADL = 19;
    inline constexpr std::uint8_t NAL_IDR_N_LP = 20;
    inline constexpr std::uint8_t NAL_CRA_NUT = 21;
    inline constexpr std::uint8_t NAL_VPS = 32;
    inline constexpr std::uint8_t NAL_SPS = 33;
    inline constexpr std::uint8_t NAL_PPS = 34;
    inline constexpr std::uint8_t NAL_AUD = 35;
    inline constexpr std::uint8_t NAL_EOS = 36;
    inline constexpr std::uint8_t NAL_EOB = 37;
    inline constexpr std::uint8_t NAL_FD = 38;
    inline constexpr std::uint8_t NAL_PREFIX_SEI = 39;
    inline constexpr std::uint8_t NAL_SUFFIX_SEI = 40;
}  // namespace hevc

// MAX_VIDEO_PIDS is defined in constants.hpp

// ============================================================================
// NAL Parser - H.264/H.265 Parameter Set Extraction and IDR Detection
//
// Extracts NAL units from PES packets on video PIDs and caches SPS/PPS/VPS
// parameter sets for stream initialization. Also detects IDR frames based
// on NAL unit type (more accurate than transport-level RAI flag).
//
// Thread safety:
// - Single writer (packet processing thread)
// - Multiple readers (queries via seqlock)
// ============================================================================

class alignas(CACHE_LINE_SIZE) NalParser {
public:
    // ========================================================================
    // Public State
    // ========================================================================

    /// Tracked video PIDs and their parameter sets
    std::array<NalParameterSets, MAX_VIDEO_PIDS> video_streams{};
    std::atomic<std::size_t> video_stream_count{0};

    /// Seqlock for parameter set access
    std::array<concurrency::Seqlock, MAX_VIDEO_PIDS> stream_seqlocks{};

    /// IDR detection state (per-PID)
    std::array<std::atomic<bool>, MAX_PIDS> has_idr_frame{};

    /// Total NAL units parsed
    std::atomic<std::int64_t> total_nal_units{0};

    /// Total IDR frames detected
    std::atomic<std::int64_t> total_idr_frames{0};

    // ========================================================================
    // Constructor
    // ========================================================================

    explicit NalParser(ts::DuckContext& duck) : duck_(duck) {
        // Initialize IDR flags
        for (auto& flag : has_idr_frame) {
            flag.store(false, std::memory_order_relaxed);
        }
    }

    // ========================================================================
    // Video PID Registration
    // ========================================================================

    /// Register a video PID for NAL parsing.
    /// @param pid The video PID
    /// @param stream_type The MPEG stream type (0x1B=H.264, 0x24=H.265)
    /// @return Index of the stream slot, or -1 if full
    std::int32_t add_video_pid(std::uint16_t pid, std::uint8_t stream_type) noexcept {
        if (pid >= ts::PID_NULL) {
            return -1;
        }

        // Check if already registered
        std::size_t count = video_stream_count.load(std::memory_order_relaxed);
        for (std::size_t i = 0; i < count; ++i) {
            if (video_streams[i].video_pid == pid) {
                return static_cast<std::int32_t>(i);
            }
        }

        // Add new PID
        if (count < MAX_VIDEO_PIDS) {
            auto seq = stream_seqlocks[count].begin_write();

            video_streams[count].video_pid = pid;
            video_streams[count].codec_info.codec_type = stream_type_to_codec(stream_type);

            stream_seqlocks[count].end_write(seq);

            video_stream_count.store(count + 1, std::memory_order_release);
            return static_cast<std::int32_t>(count);
        }

        return -1;
    }

    /// Find stream index for a PID.
    /// @return Stream index or -1 if not found
    [[nodiscard]] std::int32_t find_stream_index(std::uint16_t pid) const noexcept {
        std::size_t count = video_stream_count.load(std::memory_order_relaxed);
        for (std::size_t i = 0; i < count; ++i) {
            if (video_streams[i].video_pid == pid) {
                return static_cast<std::int32_t>(i);
            }
        }
        return -1;
    }

    // ========================================================================
    // Packet Processing
    // ========================================================================

    /// Process a PES packet for NAL unit extraction.
    /// @param pkt The TS packet (must have PES start indicator)
    /// @param pid The packet PID
    /// @param packet_idx Current packet index
    void process_pes_start(ts::TSPacket& pkt, std::uint16_t pid,
                           std::int64_t packet_idx) noexcept {
        if (!pkt.startPES()) {
            return;
        }

        std::int32_t stream_idx = find_stream_index(pid);
        if (stream_idx < 0) {
            return;
        }

        // Clear IDR flag for this packet (will be set if IDR found)
        has_idr_frame[pid].store(false, std::memory_order_relaxed);

        // Get PES payload
        const std::uint8_t* payload = pkt.getPayload();
        std::size_t payload_size = pkt.getPayloadSize();

        if (payload_size < 9) {  // Minimum PES header
            return;
        }

        // Skip PES header to get to NAL data
        // PES header: start_code(3) + stream_id(1) + packet_length(2) + flags(2) + header_length(1) + [header_data]
        std::uint8_t pes_header_data_length = payload[8];
        std::size_t pes_header_size = 9 + pes_header_data_length;

        if (payload_size <= pes_header_size) {
            return;
        }

        const std::uint8_t* nal_data = payload + pes_header_size;
        std::size_t nal_data_size = payload_size - pes_header_size;

        process_nal_data(static_cast<std::size_t>(stream_idx), pid, nal_data,
                        nal_data_size, packet_idx);
    }

    /// Check if the most recent packet on a PID contained an IDR frame.
    /// @param pid The PID to check
    /// @return true if IDR was detected
    [[nodiscard]] bool check_idr_frame(std::uint16_t pid) const noexcept {
        if (pid >= ts::PID_NULL) {
            return false;
        }
        return has_idr_frame[pid].load(std::memory_order_acquire);
    }

    // ========================================================================
    // Parameter Set Retrieval
    // ========================================================================

    /// Get video codec information for a PID.
    /// @param pid The video PID
    /// @param out Pointer to receive codec info
    /// @return true if codec info is available
    [[nodiscard]] bool get_video_codec_info(std::uint16_t pid,
                                            VideoCodecInfoNative* out) const noexcept {
        if (out == nullptr) {
            return false;
        }

        std::int32_t idx = find_stream_index(pid);
        if (idx < 0) {
            return false;
        }

        VideoCodecInfo info = concurrency::seqlock_read(
            stream_seqlocks[idx], video_streams[idx].codec_info);

        out->codec_type = info.codec_type;
        out->profile = info.profile;
        out->level = info.level;
        out->width = info.width;
        out->height = info.height;
        out->frame_rate_num = info.frame_rate_num;
        out->frame_rate_den = info.frame_rate_den;
        out->interlaced = info.interlaced ? 1 : 0;

        return info.codec_type != 0;
    }

    /// Get cached parameter sets for a video PID.
    /// @param pid The video PID
    /// @param out Pointer to receive parameter sets
    /// @return true if parameter sets are available
    [[nodiscard]] bool get_parameter_sets(std::uint16_t pid,
                                          NalParameterSetsNative* out) const noexcept {
        if (out == nullptr) {
            return false;
        }

        std::int32_t idx = find_stream_index(pid);
        if (idx < 0) {
            return false;
        }

        NalParameterSets params = concurrency::seqlock_read(
            stream_seqlocks[idx], video_streams[idx]);

        out->video_pid = params.video_pid;

        out->codec_info.codec_type = params.codec_info.codec_type;
        out->codec_info.profile = params.codec_info.profile;
        out->codec_info.level = params.codec_info.level;
        out->codec_info.width = params.codec_info.width;
        out->codec_info.height = params.codec_info.height;
        out->codec_info.frame_rate_num = params.codec_info.frame_rate_num;
        out->codec_info.frame_rate_den = params.codec_info.frame_rate_den;
        out->codec_info.interlaced = params.codec_info.interlaced ? 1 : 0;

        out->sps_length = params.sps_length;
        if (params.sps_length > 0) {
            std::memcpy(out->sps_data, params.sps_data,
                       std::min(static_cast<std::size_t>(params.sps_length), MAX_SPS_SIZE));
        }

        out->pps_length = params.pps_length;
        if (params.pps_length > 0) {
            std::memcpy(out->pps_data, params.pps_data,
                       std::min(static_cast<std::size_t>(params.pps_length), MAX_PPS_SIZE));
        }

        out->vps_length = params.vps_length;
        if (params.vps_length > 0) {
            std::memcpy(out->vps_data, params.vps_data,
                       std::min(static_cast<std::size_t>(params.vps_length), MAX_VPS_SIZE));
        }

        out->parameters_complete = params.parameters_complete ? 1 : 0;

        return params.has_sps();
    }

    // ========================================================================
    // Reset
    // ========================================================================

    void reset() noexcept {
        for (auto& flag : has_idr_frame) {
            flag.store(false, std::memory_order_relaxed);
        }

        std::size_t count = video_stream_count.load(std::memory_order_relaxed);
        for (std::size_t i = 0; i < count; ++i) {
            auto seq = stream_seqlocks[i].begin_write();

            video_streams[i].sps_length = 0;
            video_streams[i].pps_length = 0;
            video_streams[i].vps_length = 0;
            video_streams[i].parameters_complete = false;
            video_streams[i].has_idr_since_params = false;

            stream_seqlocks[i].end_write(seq);
        }

        total_nal_units.store(0, std::memory_order_release);
        total_idr_frames.store(0, std::memory_order_release);
    }

    /// Full reset including PID list.
    void reset_full() noexcept {
        reset();

        std::size_t count = video_stream_count.load(std::memory_order_relaxed);
        for (std::size_t i = 0; i < count; ++i) {
            auto seq = stream_seqlocks[i].begin_write();
            video_streams[i] = NalParameterSets{};
            stream_seqlocks[i].end_write(seq);
        }

        video_stream_count.store(0, std::memory_order_release);
    }

private:
    ts::DuckContext& duck_;

    /// PES packet accumulator for multi-packet PES
    std::array<std::vector<std::uint8_t>, MAX_VIDEO_PIDS> pes_buffers_{};

    // ========================================================================
    // NAL Processing Implementation
    // ========================================================================

    /// Convert MPEG stream type to VideoCodecType
    [[nodiscard]] static constexpr std::uint8_t stream_type_to_codec(
        std::uint8_t stream_type) noexcept {
        switch (stream_type) {
            case 0x1B:  // H.264/AVC
                return static_cast<std::uint8_t>(VideoCodecType::H264_AVC);
            case 0x24:  // H.265/HEVC
                return static_cast<std::uint8_t>(VideoCodecType::H265_HEVC);
            case 0x33:  // H.266/VVC
                return static_cast<std::uint8_t>(VideoCodecType::H266_VVC);
            default:
                return static_cast<std::uint8_t>(VideoCodecType::Unknown);
        }
    }

    /// Process NAL unit data from PES payload.
    void process_nal_data(std::size_t stream_idx, std::uint16_t pid,
                         const std::uint8_t* data, std::size_t size,
                         std::int64_t packet_idx) noexcept {
        if (size < 4) {
            return;
        }

        std::uint8_t codec_type = video_streams[stream_idx].codec_info.codec_type;

        // Find NAL units using start code detection
        std::size_t pos = 0;
        while (pos < size - 3) {
            // Look for start code: 0x00 0x00 0x01 or 0x00 0x00 0x00 0x01
            if (data[pos] == 0x00 && data[pos + 1] == 0x00) {
                std::size_t start_code_len = 0;
                if (data[pos + 2] == 0x01) {
                    start_code_len = 3;
                } else if (pos < size - 4 && data[pos + 2] == 0x00 && data[pos + 3] == 0x01) {
                    start_code_len = 4;
                }

                if (start_code_len > 0) {
                    std::size_t nal_start = pos + start_code_len;
                    if (nal_start >= size) {
                        break;
                    }

                    // Find end of NAL (next start code or end of data)
                    std::size_t nal_end = find_next_start_code(data, size, nal_start);
                    std::size_t nal_size = nal_end - nal_start;

                    if (nal_size > 0) {
                        process_single_nal(stream_idx, pid, codec_type,
                                          data + nal_start, nal_size, packet_idx);
                        total_nal_units.fetch_add(1, std::memory_order_relaxed);
                    }

                    pos = nal_end;
                    continue;
                }
            }
            ++pos;
        }
    }

    /// Find the next start code position.
    [[nodiscard]] static std::size_t find_next_start_code(const std::uint8_t* data,
                                                          std::size_t size,
                                                          std::size_t start) noexcept {
        for (std::size_t i = start; i < size - 2; ++i) {
            if (data[i] == 0x00 && data[i + 1] == 0x00 &&
                (data[i + 2] == 0x01 || (i < size - 3 && data[i + 2] == 0x00 && data[i + 3] == 0x01))) {
                return i;
            }
        }
        return size;
    }

    /// Process a single NAL unit.
    void process_single_nal(std::size_t stream_idx, std::uint16_t pid,
                           std::uint8_t codec_type, const std::uint8_t* nal_data,
                           std::size_t nal_size, std::int64_t packet_idx) noexcept {
        if (nal_size == 0) {
            return;
        }

        if (codec_type == static_cast<std::uint8_t>(VideoCodecType::H264_AVC)) {
            process_h264_nal(stream_idx, pid, nal_data, nal_size, packet_idx);
        } else if (codec_type == static_cast<std::uint8_t>(VideoCodecType::H265_HEVC)) {
            process_hevc_nal(stream_idx, pid, nal_data, nal_size, packet_idx);
        }
    }

    /// Process H.264/AVC NAL unit.
    void process_h264_nal(std::size_t stream_idx, std::uint16_t pid,
                         const std::uint8_t* nal_data, std::size_t nal_size,
                         std::int64_t /*packet_idx*/) noexcept {
        std::uint8_t nal_type = nal_data[0] & h264::NAL_TYPE_MASK;

        switch (nal_type) {
            case h264::NAL_SPS:
                cache_h264_sps(stream_idx, nal_data, nal_size);
                break;

            case h264::NAL_PPS:
                cache_h264_pps(stream_idx, nal_data, nal_size);
                break;

            case h264::NAL_IDR:
                has_idr_frame[pid].store(true, std::memory_order_release);
                total_idr_frames.fetch_add(1, std::memory_order_relaxed);

                // Mark that we've seen IDR since params
                {
                    auto seq = stream_seqlocks[stream_idx].begin_write();
                    video_streams[stream_idx].has_idr_since_params = true;
                    stream_seqlocks[stream_idx].end_write(seq);
                }
                break;

            default:
                break;
        }
    }

    /// Process H.265/HEVC NAL unit.
    void process_hevc_nal(std::size_t stream_idx, std::uint16_t pid,
                         const std::uint8_t* nal_data, std::size_t nal_size,
                         std::int64_t /*packet_idx*/) noexcept {
        if (nal_size < 2) {
            return;
        }

        // HEVC NAL header is 2 bytes: forbidden_zero_bit(1) + nal_unit_type(6) + nuh_layer_id(6) + nuh_temporal_id_plus1(3)
        std::uint8_t nal_type = (nal_data[0] & hevc::NAL_TYPE_MASK) >> hevc::NAL_TYPE_SHIFT;

        switch (nal_type) {
            case hevc::NAL_VPS:
                cache_hevc_vps(stream_idx, nal_data, nal_size);
                break;

            case hevc::NAL_SPS:
                cache_hevc_sps(stream_idx, nal_data, nal_size);
                break;

            case hevc::NAL_PPS:
                cache_hevc_pps(stream_idx, nal_data, nal_size);
                break;

            case hevc::NAL_IDR_W_RADL:
            case hevc::NAL_IDR_N_LP:
                has_idr_frame[pid].store(true, std::memory_order_release);
                total_idr_frames.fetch_add(1, std::memory_order_relaxed);

                {
                    auto seq = stream_seqlocks[stream_idx].begin_write();
                    video_streams[stream_idx].has_idr_since_params = true;
                    stream_seqlocks[stream_idx].end_write(seq);
                }
                break;

            case hevc::NAL_CRA_NUT:
                // CRA (Clean Random Access) is also a random access point
                has_idr_frame[pid].store(true, std::memory_order_release);
                total_idr_frames.fetch_add(1, std::memory_order_relaxed);
                break;

            default:
                break;
        }
    }

    // ========================================================================
    // H.264 Parameter Set Caching
    // ========================================================================

    void cache_h264_sps(std::size_t stream_idx, const std::uint8_t* nal_data,
                       std::size_t nal_size) noexcept {
        if (nal_size > MAX_SPS_SIZE) {
            return;
        }

        auto seq = stream_seqlocks[stream_idx].begin_write();

        auto& stream = video_streams[stream_idx];
        std::memcpy(stream.sps_data, nal_data, nal_size);
        stream.sps_length = static_cast<std::uint16_t>(nal_size);
        stream.has_idr_since_params = false;

        // Parse SPS using TsDuck
        parse_h264_sps(stream_idx, nal_data, nal_size);

        // Update parameters_complete flag
        stream.parameters_complete = stream.has_sps() && stream.has_pps();

        stream_seqlocks[stream_idx].end_write(seq);
    }

    void cache_h264_pps(std::size_t stream_idx, const std::uint8_t* nal_data,
                       std::size_t nal_size) noexcept {
        if (nal_size > MAX_PPS_SIZE) {
            return;
        }

        auto seq = stream_seqlocks[stream_idx].begin_write();

        auto& stream = video_streams[stream_idx];
        std::memcpy(stream.pps_data, nal_data, nal_size);
        stream.pps_length = static_cast<std::uint16_t>(nal_size);
        stream.has_idr_since_params = false;

        // Update parameters_complete flag
        stream.parameters_complete = stream.has_sps() && stream.has_pps();

        stream_seqlocks[stream_idx].end_write(seq);
    }

    void parse_h264_sps(std::size_t stream_idx, const std::uint8_t* nal_data,
                       std::size_t nal_size) noexcept {
        // Use TsDuck's AVCSequenceParameterSet for parsing
        // The SPS NAL unit starts with nal_unit_header (1 byte), followed by SPS RBSP

        ts::AVCSequenceParameterSet sps;
        if (!sps.parse(nal_data, nal_size)) {
            return;
        }

        auto& info = video_streams[stream_idx].codec_info;
        info.codec_type = static_cast<std::uint8_t>(VideoCodecType::H264_AVC);
        info.profile = static_cast<std::uint8_t>(sps.profile_idc);
        info.level = static_cast<std::uint8_t>(sps.level_idc);

        // Calculate frame dimensions
        // Width = (pic_width_in_mbs_minus1 + 1) * 16 - crop_left - crop_right
        // Height = (pic_height_in_map_units_minus1 + 1) * 16 * (2 - frame_mbs_only_flag) - crop_top - crop_bottom

        std::uint32_t width = (sps.pic_width_in_mbs_minus1 + 1) * 16;
        std::uint32_t height = (sps.pic_height_in_map_units_minus1 + 1) * 16;

        if (!sps.frame_mbs_only_flag) {
            height *= 2;  // Field/frame encoding
            info.interlaced = true;
        } else {
            info.interlaced = false;
        }

        // Apply cropping
        if (sps.frame_cropping_flag) {
            std::uint32_t crop_unit_x = 1;
            std::uint32_t crop_unit_y = sps.frame_mbs_only_flag ? 1 : 2;

            // Chroma format affects crop unit
            if (sps.chroma_format_idc == 1) {  // 4:2:0
                crop_unit_x = 2;
                crop_unit_y *= 2;
            } else if (sps.chroma_format_idc == 2) {  // 4:2:2
                crop_unit_x = 2;
            }

            width -= (sps.frame_crop_left_offset + sps.frame_crop_right_offset) * crop_unit_x;
            height -= (sps.frame_crop_top_offset + sps.frame_crop_bottom_offset) * crop_unit_y;
        }

        info.width = static_cast<std::uint16_t>(width);
        info.height = static_cast<std::uint16_t>(height);

        // Extract frame rate from VUI if present
        if (sps.vui_parameters_present_flag && sps.vui.timing_info_present_flag) {
            if (sps.vui.num_units_in_tick > 0 && sps.vui.time_scale > 0) {
                // Frame rate = time_scale / (2 * num_units_in_tick) for progressive
                info.frame_rate_num = static_cast<std::uint16_t>(
                    std::min(sps.vui.time_scale, 65535u));
                info.frame_rate_den = static_cast<std::uint16_t>(
                    std::min(sps.vui.num_units_in_tick * 2, 65535u));
            }
        }
    }

    // ========================================================================
    // HEVC Parameter Set Caching
    // ========================================================================

    void cache_hevc_vps(std::size_t stream_idx, const std::uint8_t* nal_data,
                       std::size_t nal_size) noexcept {
        if (nal_size > MAX_VPS_SIZE) {
            return;
        }

        auto seq = stream_seqlocks[stream_idx].begin_write();

        auto& stream = video_streams[stream_idx];
        std::memcpy(stream.vps_data, nal_data, nal_size);
        stream.vps_length = static_cast<std::uint16_t>(nal_size);
        stream.has_idr_since_params = false;

        // Update parameters_complete flag
        stream.parameters_complete = stream.has_vps() && stream.has_sps() && stream.has_pps();

        stream_seqlocks[stream_idx].end_write(seq);
    }

    void cache_hevc_sps(std::size_t stream_idx, const std::uint8_t* nal_data,
                       std::size_t nal_size) noexcept {
        if (nal_size > MAX_SPS_SIZE) {
            return;
        }

        auto seq = stream_seqlocks[stream_idx].begin_write();

        auto& stream = video_streams[stream_idx];
        std::memcpy(stream.sps_data, nal_data, nal_size);
        stream.sps_length = static_cast<std::uint16_t>(nal_size);
        stream.has_idr_since_params = false;

        // Parse HEVC SPS
        parse_hevc_sps(stream_idx, nal_data, nal_size);

        // Update parameters_complete flag
        stream.parameters_complete = stream.has_vps() && stream.has_sps() && stream.has_pps();

        stream_seqlocks[stream_idx].end_write(seq);
    }

    void cache_hevc_pps(std::size_t stream_idx, const std::uint8_t* nal_data,
                       std::size_t nal_size) noexcept {
        if (nal_size > MAX_PPS_SIZE) {
            return;
        }

        auto seq = stream_seqlocks[stream_idx].begin_write();

        auto& stream = video_streams[stream_idx];
        std::memcpy(stream.pps_data, nal_data, nal_size);
        stream.pps_length = static_cast<std::uint16_t>(nal_size);
        stream.has_idr_since_params = false;

        // Update parameters_complete flag
        stream.parameters_complete = stream.has_vps() && stream.has_sps() && stream.has_pps();

        stream_seqlocks[stream_idx].end_write(seq);
    }

    void parse_hevc_sps(std::size_t stream_idx, const std::uint8_t* nal_data,
                       std::size_t nal_size) noexcept {
        // Use TsDuck's HEVCSequenceParameterSet for parsing
        ts::HEVCSequenceParameterSet sps;
        if (!sps.parse(nal_data, nal_size)) {
            return;
        }

        auto& info = video_streams[stream_idx].codec_info;
        info.codec_type = static_cast<std::uint8_t>(VideoCodecType::H265_HEVC);

        // HEVC profile is in general_profile_idc
        info.profile = static_cast<std::uint8_t>(sps.profile_tier_level.general_profile_idc);
        info.level = static_cast<std::uint8_t>(sps.profile_tier_level.general_level_idc);

        // Dimensions directly available
        info.width = static_cast<std::uint16_t>(sps.pic_width_in_luma_samples);
        info.height = static_cast<std::uint16_t>(sps.pic_height_in_luma_samples);

        // Apply conformance window cropping
        if (sps.conformance_window_flag) {
            std::uint32_t sub_width_c = 1;
            std::uint32_t sub_height_c = 1;

            if (sps.chroma_format_idc == 1) {  // 4:2:0
                sub_width_c = 2;
                sub_height_c = 2;
            } else if (sps.chroma_format_idc == 2) {  // 4:2:2
                sub_width_c = 2;
            }

            info.width -= static_cast<std::uint16_t>(
                (sps.conf_win_left_offset + sps.conf_win_right_offset) * sub_width_c);
            info.height -= static_cast<std::uint16_t>(
                (sps.conf_win_top_offset + sps.conf_win_bottom_offset) * sub_height_c);
        }

        // Check for interlaced (rare in HEVC)
        info.interlaced = false;  // HEVC typically progressive

        // Frame rate from VUI
        if (sps.vui_parameters_present_flag && sps.vui.vui_timing_info_present_flag) {
            if (sps.vui.vui_num_units_in_tick > 0 && sps.vui.vui_time_scale > 0) {
                info.frame_rate_num = static_cast<std::uint16_t>(
                    std::min(sps.vui.vui_time_scale, 65535u));
                info.frame_rate_den = static_cast<std::uint16_t>(
                    std::min(sps.vui.vui_num_units_in_tick, 65535u));
            }
        }
    }
};

}  // namespace tsduck_interop::analysis

#endif  // TSDUCK_INTEROP_ANALYSIS_NAL_PARSER_HPP
