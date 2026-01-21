// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_CORE_CONSTANTS_HPP
#define TSDUCK_INTEROP_CORE_CONSTANTS_HPP

#include <cstddef>
#include <cstdint>

namespace tsduck_interop {

// ============================================================================
// MPEG-TS Constants
// ============================================================================

constexpr uint8_t TS_SYNC_BYTE = 0x47;
constexpr int TS_PACKET_SIZE = 188;
constexpr size_t MAX_PIDS = 8192;

// ============================================================================
// Clock Frequencies
// ============================================================================

constexpr double PCR_CLOCK_FREQ = 27000000.0;  // 27 MHz PCR clock
constexpr double PTS_CLOCK_FREQ = 90000.0;     // 90 kHz PTS/DTS clock

// ============================================================================
// PTS/DTS Constants
// ============================================================================

constexpr int64_t PTS_33BIT_MAX = (1LL << 33) - 1;
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
// Cache Line Size
// ============================================================================

#ifdef __cpp_lib_hardware_interference_size
constexpr size_t CACHE_LINE_SIZE = std::hardware_destructive_interference_size;
#else
constexpr size_t CACHE_LINE_SIZE = 64;
#endif

}  // namespace tsduck_interop

#endif  // TSDUCK_INTEROP_CORE_CONSTANTS_HPP
