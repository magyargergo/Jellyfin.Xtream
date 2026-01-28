// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_CORE_TYPES_HPP
#define TSDUCK_INTEROP_CORE_TYPES_HPP

#include <cstdint>
#include <type_traits>

#include "constants.hpp"

namespace tsduck_interop {

// ============================================================================
// PTS/DTS Sample (internal representation)
// ============================================================================

/// PTS/DTS sample for A/V sync analysis.
/// Contains timestamp information from a single PES packet.
struct PtsSample {
    std::int32_t pid{0};
    std::int32_t stream_type{0};
    std::int64_t pts_90khz{-1};
    std::int64_t dts_90khz{-1};
    std::int64_t pcr_ref_90khz{-1};
    std::int64_t packet_index{0};
    std::int64_t byte_offset{0};
    std::int64_t timestamp_ns{0};
    bool is_video{false};
    bool is_audio{false};
    bool is_keyframe{false};

    /// Check if this sample has a valid PTS value.
    [[nodiscard]] constexpr bool has_pts() const noexcept {
        return pts_90khz >= 0;
    }

    /// Check if this sample has a valid DTS value.
    [[nodiscard]] constexpr bool has_dts() const noexcept {
        return dts_90khz >= 0;
    }

    /// Check if this sample has a valid PCR reference.
    [[nodiscard]] constexpr bool has_pcr_ref() const noexcept {
        return pcr_ref_90khz >= 0;
    }

    /// Get the decode timestamp, falling back to PTS if DTS is not present.
    /// Per MPEG spec, when DTS is not present, PTS equals DTS.
    [[nodiscard]] constexpr std::int64_t effective_dts() const noexcept {
        return has_dts() ? dts_90khz : pts_90khz;
    }

    /// Convert PTS to milliseconds.
    [[nodiscard]] constexpr double pts_ms() const noexcept {
        return has_pts() ? ticks_90khz_to_ms(pts_90khz) : 0.0;
    }

    /// Convert DTS to milliseconds.
    [[nodiscard]] constexpr double dts_ms() const noexcept {
        return has_dts() ? ticks_90khz_to_ms(dts_90khz) : 0.0;
    }
};

// Ensure PtsSample is trivially copyable for seqlock usage
static_assert(std::is_trivially_copyable_v<PtsSample>,
              "PtsSample must be trivially copyable");

// ============================================================================
// Drift Sample for Trend Analysis
// ============================================================================

/// Drift sample for linear regression analysis.
struct DriftSample {
    double drift_ms{0.0};
    double elapsed_sec{0.0};

    /// Check if this sample is valid (has elapsed time).
    [[nodiscard]] constexpr bool is_valid() const noexcept {
        return elapsed_sec > 0.0;
    }
};

static_assert(std::is_trivially_copyable_v<DriftSample>,
              "DriftSample must be trivially copyable");

// ============================================================================
// Matched A/V Pair for Accurate Drift Calculation
// ============================================================================

/// Matched audio/video timestamp pair for precise drift measurement.
struct MatchedAvPair {
    std::int64_t video_pts_90khz{-1};
    std::int64_t video_dts_90khz{-1};
    std::int64_t audio_pts_90khz{-1};
    std::int64_t reference_pts_90khz{-1};
    double drift_ms{0.0};
    std::int64_t timestamp_ns{0};
    bool video_is_keyframe{false};

    /// Check if this pair has valid video and audio timestamps.
    [[nodiscard]] constexpr bool is_complete() const noexcept {
        return video_pts_90khz >= 0 && audio_pts_90khz >= 0;
    }

    /// Get video PTS in milliseconds.
    [[nodiscard]] constexpr double video_pts_ms() const noexcept {
        return video_pts_90khz >= 0 ? ticks_90khz_to_ms(video_pts_90khz) : 0.0;
    }

    /// Get audio PTS in milliseconds.
    [[nodiscard]] constexpr double audio_pts_ms() const noexcept {
        return audio_pts_90khz >= 0 ? ticks_90khz_to_ms(audio_pts_90khz) : 0.0;
    }
};

static_assert(std::is_trivially_copyable_v<MatchedAvPair>,
              "MatchedAvPair must be trivially copyable");

// ============================================================================
// Recent Timestamp for Interpolation
// ============================================================================

/// Recent timestamp entry for interpolation and continuity checking.
struct RecentTimestamp {
    std::int64_t pts_90khz{-1};
    std::int64_t dts_90khz{-1};
    std::int64_t wall_time_ns{0};
    std::int64_t packet_index{0};

    /// Check if this timestamp has valid PTS.
    [[nodiscard]] constexpr bool has_pts() const noexcept {
        return pts_90khz >= 0;
    }

    /// Check if this timestamp has valid DTS.
    [[nodiscard]] constexpr bool has_dts() const noexcept {
        return dts_90khz >= 0;
    }

    /// Get effective timestamp (PTS, or DTS if PTS not available).
    [[nodiscard]] constexpr std::int64_t effective_pts() const noexcept {
        return has_pts() ? pts_90khz : dts_90khz;
    }
};

static_assert(std::is_trivially_copyable_v<RecentTimestamp>,
              "RecentTimestamp must be trivially copyable");

// ============================================================================
// PCR History Entry for Bitrate Estimation
// ============================================================================

/// PCR history entry for bitrate calculation.
struct PcrHistoryEntry {
    std::int64_t pcr_90khz{-1};
    std::int64_t packet_index{0};
    std::int64_t wall_time_ns{0};

    /// Check if this entry has a valid PCR value.
    [[nodiscard]] constexpr bool is_valid() const noexcept {
        return pcr_90khz >= 0;
    }

    /// Get PCR in milliseconds.
    [[nodiscard]] constexpr double pcr_ms() const noexcept {
        return is_valid() ? ticks_90khz_to_ms(pcr_90khz) : 0.0;
    }
};

static_assert(std::is_trivially_copyable_v<PcrHistoryEntry>,
              "PcrHistoryEntry must be trivially copyable");

// ============================================================================
// SCTE-35 Splice Event Structure
// ============================================================================

/// SCTE-35 splice command types
enum class SpliceCommandType : std::uint8_t {
    Null = 0x00,
    Reserved = 0x01,
    SpliceSchedule = 0x04,
    SpliceInsert = 0x05,
    TimeSignal = 0x06,
    BandwidthReservation = 0x07,
    PrivateCommand = 0xFF
};

/// SCTE-35 splice event information.
/// Contains parsed data from splice_info_section.
struct Scte35Event {
    std::uint32_t splice_event_id{0};       ///< Unique identifier for the splice event
    std::uint64_t pts_time{0};               ///< 33-bit PTS time when splice should occur
    std::uint64_t duration_pts{0};           ///< Duration in PTS ticks (0 if not specified)
    bool out_of_network{false};              ///< true = ad break start, false = return to content
    bool splice_immediate{false};            ///< Immediate splice vs scheduled
    std::uint8_t splice_command_type{0};     ///< 0x00=null, 0x04=schedule, 0x05=insert, 0x06=time_signal
    std::uint16_t scte35_pid{0};             ///< PID carrying this SCTE-35 data
    std::int64_t packet_index{0};            ///< Packet index where this event was detected
    std::int64_t timestamp_ns{0};            ///< Wall clock time when event was parsed

    /// Check if this event has a valid PTS time.
    [[nodiscard]] constexpr bool has_pts_time() const noexcept {
        return pts_time > 0;
    }

    /// Check if this event has a specified duration.
    [[nodiscard]] constexpr bool has_duration() const noexcept {
        return duration_pts > 0;
    }

    /// Get PTS time in milliseconds.
    [[nodiscard]] constexpr double pts_time_ms() const noexcept {
        return has_pts_time() ? ticks_90khz_to_ms(static_cast<std::int64_t>(pts_time)) : 0.0;
    }

    /// Get duration in milliseconds.
    [[nodiscard]] constexpr double duration_ms() const noexcept {
        return has_duration() ? ticks_90khz_to_ms(static_cast<std::int64_t>(duration_pts)) : 0.0;
    }
};

static_assert(std::is_trivially_copyable_v<Scte35Event>,
              "Scte35Event must be trivially copyable");

// ============================================================================
// Video Codec Information Structure
// ============================================================================

/// Video codec type enumeration
enum class VideoCodecType : std::uint8_t {
    Unknown = 0,
    H264_AVC = 1,
    H265_HEVC = 2,
    H266_VVC = 3
};

/// Video codec information parsed from SPS/VPS.
struct VideoCodecInfo {
    std::uint8_t codec_type{0};              ///< VideoCodecType enum value
    std::uint8_t profile{0};                 ///< Profile (profile_idc for H.264)
    std::uint8_t level{0};                   ///< Level (level_idc for H.264)
    std::uint8_t reserved{0};                ///< Padding for alignment
    std::uint16_t width{0};                  ///< Picture width in pixels
    std::uint16_t height{0};                 ///< Picture height in pixels
    std::uint16_t frame_rate_num{0};         ///< Frame rate numerator (0 if unknown)
    std::uint16_t frame_rate_den{0};         ///< Frame rate denominator (0 if unknown)
    bool interlaced{false};                  ///< True if interlaced content
    bool reserved2{false};                   ///< Padding
    bool reserved3{false};                   ///< Padding
    bool reserved4{false};                   ///< Padding

    /// Check if resolution is known.
    [[nodiscard]] constexpr bool has_resolution() const noexcept {
        return width > 0 && height > 0;
    }

    /// Check if frame rate is known.
    [[nodiscard]] constexpr bool has_frame_rate() const noexcept {
        return frame_rate_num > 0 && frame_rate_den > 0;
    }

    /// Get frame rate as floating point.
    [[nodiscard]] constexpr double frame_rate_fps() const noexcept {
        return has_frame_rate() ?
            static_cast<double>(frame_rate_num) / static_cast<double>(frame_rate_den) : 0.0;
    }
};

static_assert(std::is_trivially_copyable_v<VideoCodecInfo>,
              "VideoCodecInfo must be trivially copyable");

// ============================================================================
// NAL Parameter Sets Structure
// ============================================================================

/// Maximum sizes for NAL unit parameter set caching
inline constexpr std::size_t MAX_SPS_SIZE = 256;
inline constexpr std::size_t MAX_PPS_SIZE = 128;
inline constexpr std::size_t MAX_VPS_SIZE = 128;

/// Cached NAL unit parameter sets (SPS/PPS/VPS) for stream initialization.
struct NalParameterSets {
    std::uint16_t video_pid{0};              ///< Video PID these parameters belong to
    std::uint16_t reserved{0};               ///< Padding
    VideoCodecInfo codec_info{};             ///< Parsed codec information from SPS

    std::uint8_t sps_data[MAX_SPS_SIZE]{};   ///< Cached SPS NAL unit (without start code)
    std::uint16_t sps_length{0};             ///< SPS data length in bytes

    std::uint8_t pps_data[MAX_PPS_SIZE]{};   ///< Cached PPS NAL unit (without start code)
    std::uint16_t pps_length{0};             ///< PPS data length in bytes

    std::uint8_t vps_data[MAX_VPS_SIZE]{};   ///< HEVC VPS (0 length for H.264)
    std::uint16_t vps_length{0};             ///< VPS data length in bytes

    bool parameters_complete{false};         ///< true when we have all required param sets
    bool has_idr_since_params{false};        ///< true if IDR frame seen since params updated
    std::uint16_t reserved2{0};              ///< Padding

    /// Check if SPS is available.
    [[nodiscard]] constexpr bool has_sps() const noexcept {
        return sps_length > 0;
    }

    /// Check if PPS is available.
    [[nodiscard]] constexpr bool has_pps() const noexcept {
        return pps_length > 0;
    }

    /// Check if VPS is available (HEVC only).
    [[nodiscard]] constexpr bool has_vps() const noexcept {
        return vps_length > 0;
    }

    /// Check if stream can be initialized (has minimum required params).
    [[nodiscard]] constexpr bool can_initialize_decoder() const noexcept {
        // H.264 requires SPS + PPS
        // HEVC requires VPS + SPS + PPS
        if (codec_info.codec_type == static_cast<std::uint8_t>(VideoCodecType::H265_HEVC)) {
            return has_vps() && has_sps() && has_pps();
        }
        return has_sps() && has_pps();
    }
};

// NalParameterSets contains arrays so trivially_copyable should work
static_assert(std::is_trivially_copyable_v<NalParameterSets>,
              "NalParameterSets must be trivially copyable");

}  // namespace tsduck_interop

#endif  // TSDUCK_INTEROP_CORE_TYPES_HPP
