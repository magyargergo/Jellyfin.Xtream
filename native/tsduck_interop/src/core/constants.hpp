// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_CORE_CONSTANTS_HPP
#define TSDUCK_INTEROP_CORE_CONSTANTS_HPP

#include <cstddef>
#include <cstdint>
#include <concepts>
#include <type_traits>

#include "tsduck.h"

namespace tsduck_interop {

// ============================================================================
// Type Traits and Concepts for MPEG-TS Processing
// ============================================================================

/// Concept for types that can represent PID values (0-8191)
template <typename T>
concept PidType = std::integral<T> && (sizeof(T) >= 2);

/// Concept for types that can represent timestamp values (PTS/DTS/PCR)
template <typename T>
concept TimestampType = std::integral<T> && std::is_signed_v<T> && (sizeof(T) >= 8);

/// Concept for types that can represent bitrate values
template <typename T>
concept BitrateType = std::integral<T> && (sizeof(T) >= 4);

// ============================================================================
// Compile-Time Utility Functions
// ============================================================================

/// Compile-time power of 2 check
template <std::integral T>
[[nodiscard]] consteval bool is_power_of_two(T value) noexcept {
    return value > 0 && (value & (value - 1)) == 0;
}

/// Compile-time log2 for power-of-2 values
template <std::integral T>
[[nodiscard]] consteval T log2_floor(T value) noexcept {
    T result = 0;
    while (value > 1) {
        value >>= 1;
        ++result;
    }
    return result;
}

// ============================================================================
// MPEG-TS Constants (derived from TsDuck)
// ============================================================================

/// Number of distinct PID values in MPEG-TS (0x0000..0x1FFF inclusive)
inline constexpr std::size_t MAX_PIDS = static_cast<std::size_t>(ts::PID_NULL) + 1;

/// TS packet size in bytes
inline constexpr std::size_t TS_PACKET_SIZE = static_cast<std::size_t>(ts::PKT_SIZE);
static_assert(sizeof(ts::TSPacket) == 188, "TSPacket must be exactly 188 bytes");

/// TS packet size in bits
inline constexpr std::size_t TS_PACKET_SIZE_BITS = static_cast<std::size_t>(ts::PKT_SIZE_BITS);

// ============================================================================
// PTS/DTS Constants (derived from TsDuck)
// ============================================================================

/// Maximum 33-bit PTS/DTS value (2^33 - 1), derived from ts::PTS_DTS_SCALE
inline constexpr std::int64_t PTS_33BIT_MAX = static_cast<std::int64_t>(ts::PTS_DTS_SCALE) - 1;

/// PTS discontinuity threshold: 1 second in 90kHz ticks
inline constexpr std::int64_t PTS_DISCONTINUITY_THRESHOLD = 90000;

/// PTS backward jump threshold: 500ms in 90kHz ticks
inline constexpr std::int64_t PTS_BACKWARD_THRESHOLD = 45000;

/// PTS/DTS wraparound value (2^33)
inline constexpr std::int64_t PTS_WRAPAROUND = static_cast<std::int64_t>(ts::PTS_DTS_SCALE);

// ============================================================================
// PCR Constants (derived from TsDuck)
// ============================================================================

/// PCR system clock frequency (27 MHz)
inline constexpr double PCR_FREQUENCY_HZ = static_cast<double>(ts::SYSTEM_CLOCK_FREQ);

/// PCR to 90kHz conversion factor (300)
inline constexpr std::int64_t PCR_TO_90KHZ = static_cast<std::int64_t>(ts::SYSTEM_CLOCK_SUBFACTOR);

/// PCR wraparound value
inline constexpr std::int64_t PCR_WRAPAROUND = static_cast<std::int64_t>(ts::PCR_SCALE);

// ============================================================================
// Quality Thresholds (TR 101 290)
// ============================================================================

/// Default PCR jitter threshold in microseconds (TR 101 290: 500ns)
inline constexpr double DEFAULT_PCR_JITTER_THRESHOLD_US = 0.5;

/// Default IAT jitter threshold in microseconds (1ms)
inline constexpr double DEFAULT_IAT_JITTER_THRESHOLD_US = 1000.0;

// ============================================================================
// A/V Sync Thresholds (EBU R37)
// ============================================================================

/// Sync threshold for production quality (EBU R37)
inline constexpr double SYNC_THRESHOLD_MS = 20.0;

/// Human perception threshold for A/V drift
inline constexpr double DRIFT_THRESHOLD_MS = 45.0;

/// Severe desync threshold
inline constexpr double DESYNC_THRESHOLD_MS = 100.0;

/// Drift outlier rejection threshold
inline constexpr double DRIFT_OUTLIER_THRESHOLD_MS = 200.0;

// ============================================================================
// Buffer Sizes (must be power of 2 for ring buffers)
// ============================================================================

inline constexpr std::size_t IAT_SAMPLE_WINDOW = 1024;
inline constexpr std::size_t PTS_SAMPLE_WINDOW = 128;
inline constexpr std::size_t DRIFT_SAMPLE_WINDOW = 64;
inline constexpr std::size_t MATCHED_SAMPLE_WINDOW = 32;
inline constexpr std::size_t PCR_HISTORY_SIZE = 8;

// Static assertions for power-of-2 requirements
static_assert(is_power_of_two(IAT_SAMPLE_WINDOW), "IAT_SAMPLE_WINDOW must be power of 2");
static_assert(is_power_of_two(PTS_SAMPLE_WINDOW), "PTS_SAMPLE_WINDOW must be power of 2");
static_assert(is_power_of_two(DRIFT_SAMPLE_WINDOW), "DRIFT_SAMPLE_WINDOW must be power of 2");
static_assert(is_power_of_two(MATCHED_SAMPLE_WINDOW), "MATCHED_SAMPLE_WINDOW must be power of 2");
static_assert(is_power_of_two(PCR_HISTORY_SIZE), "PCR_HISTORY_SIZE must be power of 2");

// ============================================================================
// Matching and Correction Constants
// ============================================================================

/// PTS matching tolerance: 50ms in 90kHz ticks
inline constexpr std::int64_t PTS_MATCH_TOLERANCE_90KHZ = 4500;

// ============================================================================
// Restamping Constants
// ============================================================================

inline constexpr double DEFAULT_CORRECTION_THRESHOLD_MS = 15.0;   // Was 25.0; lowered to catch drifts before EBU R37 20ms
inline constexpr double DEFAULT_MAX_CORRECTION_RATE_MS = 20.0;
inline constexpr double DEFAULT_HYSTERESIS_THRESHOLD_MS = 5.0;    // Was 10.0; prevents oscillation at lower threshold

/// Default gap between streams on switch: 100ms in 90kHz ticks
inline constexpr std::int64_t DEFAULT_SWITCH_GAP_90KHZ = 9000;

/// Correction ramp factor: 8% of drift per second (lowered from 15% to prevent
/// oscillation in the feedback loop — analyzer measures corrected timestamps)
inline constexpr double CORRECTION_RAMP_FACTOR = 0.08;

/// Minimum correction interval in seconds
inline constexpr double MIN_CORRECTION_INTERVAL_SEC = 0.1;

/// Warmup period before drift correction activates (seconds).
/// During startup, the A/V sync analyzer uses the inaccurate update_drift_simple()
/// fallback until enough matched pairs accumulate. This produces transient drift
/// peaks that would trigger false corrections. Skip correction during warmup to
/// allow the analyzer to stabilize and the PCR bitrate estimate to converge.
inline constexpr double CORRECTION_WARMUP_SEC = 3.0;

/// PCR smoothing EMA factor
inline constexpr double PCR_SMOOTHING_FACTOR = 0.95;  // Was 0.85; gentler smoothing reduces PCR-PTS divergence

// ============================================================================
// TR 101 290 Timing Limits (ETSI TR 101 290 V1.3.1)
// ============================================================================

/// PAT repetition interval limit: 500ms
inline constexpr std::int64_t TR101290_PAT_INTERVAL_NS = 500'000'000LL;

/// PMT repetition interval limit: 500ms
inline constexpr std::int64_t TR101290_PMT_INTERVAL_NS = 500'000'000LL;

/// CAT repetition interval limit: 500ms
inline constexpr std::int64_t TR101290_CAT_INTERVAL_NS = 500'000'000LL;

/// PCR repetition interval limit: 40ms
inline constexpr std::int64_t TR101290_PCR_INTERVAL_NS = 40'000'000LL;

/// PTS repetition interval limit: 700ms
inline constexpr std::int64_t TR101290_PTS_INTERVAL_NS = 700'000'000LL;

/// PID timeout limit: 5 seconds
inline constexpr std::int64_t TR101290_PID_TIMEOUT_NS = 5'000'000'000LL;

/// PCR accuracy limit: +/-500ns = 13.5 ticks at 27MHz (ISO/IEC 13818-1)
/// Using 13 ticks for strict compliance (481ns), as 14 would be 519ns
inline constexpr std::int64_t TR101290_PCR_ACCURACY_TICKS = 13;

/// PCR discontinuity threshold: 100ms at 27MHz
inline constexpr std::int64_t TR101290_PCR_DISCONTINUITY_TICKS = 2'700'000LL;

/// PCR frequency offset limit: +/-30 ppm per ISO/IEC 13818-1
inline constexpr double PCR_FREQUENCY_OFFSET_LIMIT_PPM = 30.0;

/// PCR drift rate limit: 10 ppm/hour (75 mHz/sec) per ISO/IEC 13818-1
inline constexpr double PCR_DRIFT_RATE_LIMIT_PPM_PER_HOUR = 10.0;

/// PCR drift rate limit in mHz/sec
inline constexpr double PCR_DRIFT_RATE_LIMIT_MHZ_PER_SEC = 0.075;

// ============================================================================
// PSI Table Limits
// ============================================================================

inline constexpr std::size_t MAX_PROGRAMS = 32;
inline constexpr std::size_t MAX_ELEMENTARY_STREAMS = 64;
inline constexpr std::size_t PSI_SECTION_MAX_SIZE = 1024;

// ============================================================================
// SCTE-35 Buffer Sizes
// ============================================================================

inline constexpr std::size_t SCTE35_EVENT_BUFFER_SIZE = 32;
static_assert(is_power_of_two(SCTE35_EVENT_BUFFER_SIZE),
              "SCTE35_EVENT_BUFFER_SIZE must be power of 2");

// ============================================================================
// NAL Parser Limits
// ============================================================================

inline constexpr std::size_t MAX_VIDEO_PIDS = 8;

// ============================================================================
// Cache Line Size
// ============================================================================

/// Cache line size for alignment (64 bytes typical for x86-64 and ARM64)
/// Using hardcoded value to avoid -Winterference-size warnings from
/// std::hardware_destructive_interference_size
inline constexpr std::size_t CACHE_LINE_SIZE = 64;

// ============================================================================
// Timestamp Conversion Utilities
// ============================================================================

/// Convert microseconds to 90kHz ticks
[[nodiscard]] constexpr std::int64_t us_to_90khz(double us) noexcept {
    return static_cast<std::int64_t>(us * 0.09);
}

/// Convert 90kHz ticks to microseconds
[[nodiscard]] constexpr double ticks_90khz_to_us(std::int64_t ticks) noexcept {
    return static_cast<double>(ticks) / 0.09;
}

/// Convert milliseconds to 90kHz ticks
[[nodiscard]] constexpr std::int64_t ms_to_90khz(double ms) noexcept {
    return static_cast<std::int64_t>(ms * 90.0);
}

/// Convert 90kHz ticks to milliseconds
[[nodiscard]] constexpr double ticks_90khz_to_ms(std::int64_t ticks) noexcept {
    return static_cast<double>(ticks) / 90.0;
}

/// Convert 27MHz PCR ticks to 90kHz base
[[nodiscard]] constexpr std::int64_t pcr_to_90khz(std::int64_t pcr_27mhz) noexcept {
    return pcr_27mhz / PCR_TO_90KHZ;
}

/// Convert 90kHz base to 27MHz PCR ticks
[[nodiscard]] constexpr std::int64_t base_90khz_to_pcr(std::int64_t base_90khz) noexcept {
    return base_90khz * PCR_TO_90KHZ;
}

}  // namespace tsduck_interop

#endif  // TSDUCK_INTEROP_CORE_CONSTANTS_HPP
