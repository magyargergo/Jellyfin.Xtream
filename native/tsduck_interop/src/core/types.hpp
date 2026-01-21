// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_CORE_TYPES_HPP
#define TSDUCK_INTEROP_CORE_TYPES_HPP

#include <cstdint>
#include "constants.hpp"

namespace tsduck_interop {

// ============================================================================
// PTS/DTS Sample (internal representation)
// ============================================================================

struct PtsSample {
    int32_t pid;
    int32_t stream_type;
    int64_t pts_90khz;
    int64_t dts_90khz;
    int64_t pcr_ref_90khz;
    int64_t packet_index;
    int64_t byte_offset;
    int64_t timestamp_ns;
    bool is_video;
    bool is_audio;
    bool is_keyframe;
};

// ============================================================================
// Drift Sample for Trend Analysis
// ============================================================================

struct DriftSample {
    double drift_ms;
    double elapsed_sec;
};

// ============================================================================
// Matched A/V Pair for Accurate Drift Calculation
// ============================================================================

struct MatchedAvPair {
    int64_t video_pts_90khz;
    int64_t video_dts_90khz;
    int64_t audio_pts_90khz;
    int64_t reference_pts_90khz;
    double drift_ms;
    int64_t timestamp_ns;
    bool video_is_keyframe;
};

// ============================================================================
// Recent Timestamp for Interpolation
// ============================================================================

struct RecentTimestamp {
    int64_t pts_90khz;
    int64_t dts_90khz;
    int64_t wall_time_ns;
    int64_t packet_index;
};

// ============================================================================
// PCR History Entry for Bitrate Estimation
// ============================================================================

struct PcrHistoryEntry {
    int64_t pcr_90khz;
    int64_t packet_index;
    int64_t wall_time_ns;
};

}  // namespace tsduck_interop

#endif  // TSDUCK_INTEROP_CORE_TYPES_HPP
