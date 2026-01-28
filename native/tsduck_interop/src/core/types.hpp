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

}  // namespace tsduck_interop

#endif  // TSDUCK_INTEROP_CORE_TYPES_HPP
