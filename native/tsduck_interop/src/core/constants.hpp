// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_CORE_CONSTANTS_HPP
#define TSDUCK_INTEROP_CORE_CONSTANTS_HPP

#include <cstddef>
#include <cstdint>

#include "tsduck.h"

namespace tsduck_interop {

// ============================================================================
// MPEG-TS Constants (derived from TsDuck)
// ============================================================================

/// Number of distinct PID values in MPEG-TS (0x0000..0x1FFF inclusive)
constexpr size_t MAX_PIDS = static_cast<size_t>(ts::PID_NULL) + 1;

// ============================================================================
// PTS/DTS Constants (derived from TsDuck)
// ============================================================================

/// Maximum 33-bit PTS/DTS value (2^33 - 1), derived from ts::PTS_DTS_SCALE
constexpr int64_t PTS_33BIT_MAX = static_cast<int64_t>(ts::PTS_DTS_SCALE) - 1;
constexpr int64_t PTS_DISCONTINUITY_THRESHOLD = 90000;   // 1 second in 90kHz
constexpr int64_t PTS_BACKWARD_THRESHOLD = 45000;        // 500ms backward jump

// ============================================================================
// Quality Thresholds (TR 101 290)
// ============================================================================

constexpr double DEFAULT_PCR_JITTER_THRESHOLD_US = 0.5;   // TR 101 290: 500ns
constexpr double DEFAULT_IAT_JITTER_THRESHOLD_US = 1000.0; // 1ms default

// ============================================================================
// A/V Sync Thresholds (EBU R37)
// ============================================================================

constexpr double SYNC_THRESHOLD_MS = 20.0;     // ±20ms for production
constexpr double DRIFT_THRESHOLD_MS = 45.0;    // Human perception threshold
constexpr double DESYNC_THRESHOLD_MS = 100.0;  // Severe desync threshold
constexpr double DRIFT_OUTLIER_THRESHOLD_MS = 200.0;

// ============================================================================
// Buffer Sizes (must be power of 2 for ring buffers)
// ============================================================================

constexpr size_t IAT_SAMPLE_WINDOW = 1024;
constexpr size_t PTS_SAMPLE_WINDOW = 128;
constexpr size_t DRIFT_SAMPLE_WINDOW = 64;
constexpr size_t MATCHED_SAMPLE_WINDOW = 32;
constexpr size_t PCR_HISTORY_SIZE = 8;

// ============================================================================
// Matching and Correction Constants
// ============================================================================

constexpr int64_t PTS_MATCH_TOLERANCE_90KHZ = 4500;  // 50ms tolerance

// ============================================================================
// Restamping Constants
// ============================================================================

constexpr double DEFAULT_CORRECTION_THRESHOLD_MS = 45.0;
constexpr double DEFAULT_MAX_CORRECTION_RATE_MS = 10.0;
constexpr double DEFAULT_HYSTERESIS_THRESHOLD_MS = 20.0;
constexpr int64_t DEFAULT_SWITCH_GAP_90KHZ = 9000;  // 100ms

constexpr double CORRECTION_RAMP_FACTOR = 0.15;      // 15% of drift per second
constexpr double MIN_CORRECTION_INTERVAL_SEC = 0.1;  // 100ms minimum interval
constexpr double PCR_SMOOTHING_FACTOR = 0.85;

// ============================================================================
// TR 101 290 Timing Limits (ETSI TR 101 290 V1.3.1)
// ============================================================================

constexpr int64_t TR101290_PAT_INTERVAL_NS = 500000000LL;   // 500ms
constexpr int64_t TR101290_PMT_INTERVAL_NS = 500000000LL;   // 500ms
constexpr int64_t TR101290_PCR_INTERVAL_NS = 40000000LL;    // 40ms
constexpr int64_t TR101290_PTS_INTERVAL_NS = 700000000LL;   // 700ms
constexpr int64_t TR101290_PID_TIMEOUT_NS  = 5000000000LL;  // 5s

// PCR accuracy limit: ±500ns = ±13.5 ticks at 27MHz
constexpr int64_t TR101290_PCR_ACCURACY_TICKS = 14;

// PCR discontinuity threshold: 100ms at 27MHz
constexpr int64_t TR101290_PCR_DISCONTINUITY_TICKS = 2700000LL;

// ============================================================================
// PSI Table Limits
// ============================================================================

constexpr size_t MAX_PROGRAMS = 32;
constexpr size_t MAX_ELEMENTARY_STREAMS = 64;
constexpr size_t PSI_SECTION_MAX_SIZE = 1024;

// ============================================================================
// Cache Line Size
// ============================================================================

// Use hardcoded 64 bytes (typical for x86-64 and ARM64) to avoid
// -Winterference-size warnings from std::hardware_destructive_interference_size
constexpr size_t CACHE_LINE_SIZE = 64;

}  // namespace tsduck_interop

#endif  // TSDUCK_INTEROP_CORE_CONSTANTS_HPP
