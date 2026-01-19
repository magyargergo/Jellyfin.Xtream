// Copyright (C) 2025  Gergo Magyar
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

#ifndef TSDUCK_INTEROP_H
#define TSDUCK_INTEROP_H

#include <stdint.h>
#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

// Platform-specific export macros
#ifdef _WIN32
    #ifdef TSDUCK_INTEROP_EXPORTS
        #define TSDUCK_API __declspec(dllexport)
    #else
        #define TSDUCK_API __declspec(dllimport)
    #endif
#else
    #define TSDUCK_API __attribute__((visibility("default")))
#endif

// Opaque handle types
typedef struct TsDuckContext* TsDuckContextHandle;
typedef struct TsDuckAnalyzer* TsDuckAnalyzerHandle;

// Error codes
typedef enum {
    TSDUCK_OK = 0,
    TSDUCK_ERROR_NULL_HANDLE = -1,
    TSDUCK_ERROR_INVALID_CONFIG = -2,
    TSDUCK_ERROR_NOT_INITIALIZED = -3,
    TSDUCK_ERROR_INVALID_DATA = -4,
    TSDUCK_ERROR_INTERNAL = -5,
} TsDuckError;

// TR 101 290 Priority 1 structure (blittable, matches C# struct exactly)
typedef struct {
    int64_t sync_byte_error;
    int64_t sync_loss;
    int64_t pat_error;
    int64_t pat_error_2;
    int64_t continuity_count_error;
    int64_t pmt_error;
    int64_t pmt_error_2;
    int64_t pid_error;
} Tr101290Priority1Native;

// TR 101 290 Priority 2 structure (blittable)
typedef struct {
    int64_t transport_error;
    int64_t crc_error;
    int64_t pcr_repetition_error;
    int64_t pcr_discontinuity_error;
    int64_t pcr_accuracy_error;
    int64_t pts_error;
    int64_t cat_error;
} Tr101290Priority2Native;

// Metrics snapshot structure (blittable)
typedef struct {
    int64_t timestamp_ticks;        // DateTime.Ticks in C#
    int64_t ts_bitrate;             // bits per second
    int32_t service_count;
    int32_t pid_count;
    Tr101290Priority1Native priority1;
    Tr101290Priority2Native priority2;
} TsDuckMetricsNative;

// Configuration structure
typedef struct {
    int32_t metrics_interval_ms;    // Metrics update interval in milliseconds
    int32_t enable_tr101290;        // 1 = enable, 0 = disable (using int32 for ABI stability)
    int32_t sample_size_bytes;      // Max bytes to process per feed call
    int32_t reserved;               // Padding for alignment
} TsDuckConfigNative;

// PCR Analysis structure (Phase 2a - blittable)
typedef struct {
    double pcr_jitter_us;           // Current PCR jitter in microseconds
    double pcr_jitter_max_us;       // Maximum PCR jitter observed
    double pcr_jitter_avg_us;       // Average PCR jitter
    int64_t pcr_interval_packets;   // Packets between PCRs
    double pcr_interval_ms;         // Time between PCRs in milliseconds
    double pcr_drift_ppm;           // PCR drift in parts-per-million
    int64_t pcr_count;              // Total PCRs received
    int64_t pcr_valid_count;        // Valid PCRs (within tolerance)
} PcrAnalysisNative;

// IAT (Inter-packet Arrival Time) Analysis structure (Phase 2a - blittable)
typedef struct {
    double iat_avg_us;              // Average inter-packet arrival time in microseconds
    double iat_min_us;              // Minimum IAT
    double iat_max_us;              // Maximum IAT
    double iat_jitter_us;           // IAT jitter (variation from average)
    double iat_stddev_us;           // Standard deviation of IAT
    int64_t late_packets;           // Packets arriving late (> avg + threshold)
    int64_t early_packets;          // Packets arriving early (< avg - threshold)
    int64_t burst_count;            // Packet burst events (multiple packets at once)
} IatAnalysisNative;

// Bitrate Analysis structure (Phase 2a - blittable)
typedef struct {
    int64_t ts_bitrate_nominal;     // Nominal/configured bitrate
    int64_t ts_bitrate_pcr;         // Bitrate calculated from PCR
    int64_t ts_bitrate_dts;         // Bitrate calculated from DTS (fallback)
    double bitrate_accuracy;        // PCR vs actual accuracy (0.0-1.0)
    int64_t null_packet_bitrate;    // Null packet bitrate (stuffing)
    double null_packet_ratio;       // Null packets / total (0.0-1.0)
    int64_t useful_bitrate;         // Non-null bitrate
} BitrateAnalysisNative;

// Extended PID information structure (Phase 2b - blittable)
typedef struct {
    int32_t pid;                    // PID value (0-8191)
    int32_t stream_type;            // MPEG stream type (0x00-0xFF)
    int64_t packets;                // Total packets for this PID
    int64_t bitrate;                // Bitrate for this PID (bps)
    int64_t continuity_errors;      // Continuity counter errors for this PID
    int64_t duplicate_packets;      // Duplicate packets detected
    int64_t scrambled_packets;      // Scrambled packet count
    int32_t is_scrambled;           // Currently scrambled (1=yes, 0=no)
    int32_t is_pcr_pid;             // Carries PCR (1=yes, 0=no)
    double pcr_jitter_us;           // PCR jitter if PCR PID (0 otherwise)
    int32_t is_video;               // Video PID (1=yes, 0=no)
    int32_t is_audio;               // Audio PID (1=yes, 0=no)
} TsDuckPidInfoExtended;

// Callback for metrics updates (called from analyzer thread)
typedef void (*TsDuckMetricsCallback)(const TsDuckMetricsNative* metrics, void* user_data);

// Callback for quality violations
typedef void (*TsDuckViolationCallback)(
    const char* violation_type,
    const char* details,
    void* user_data
);

// =============================================================================
// Library Initialization
// =============================================================================

/// Get the TSDuck library version string.
/// Returns a static string that must not be freed.
TSDUCK_API const char* tsduck_get_version(void);

/// Check if TSDuck is available on this system.
/// Returns true if TSDuck libraries are properly installed.
TSDUCK_API bool tsduck_is_available(void);

// =============================================================================
// Context Management
// =============================================================================

/// Create a new TSDuck context.
/// Returns NULL on failure (e.g., TSDuck not installed).
TSDUCK_API TsDuckContextHandle tsduck_context_create(void);

/// Destroy a TSDuck context and free all resources.
/// Safe to call with NULL handle.
TSDUCK_API void tsduck_context_destroy(TsDuckContextHandle ctx);

/// Check if the context is valid and TSDuck is operational.
TSDUCK_API bool tsduck_context_is_available(TsDuckContextHandle ctx);

// =============================================================================
// Analyzer Lifecycle
// =============================================================================

/// Create a new stream analyzer attached to a context.
/// @param ctx The context handle (required).
/// @param config Configuration options. If NULL, defaults are used.
/// @return Analyzer handle, or NULL on failure.
TSDUCK_API TsDuckAnalyzerHandle tsduck_analyzer_create(
    TsDuckContextHandle ctx,
    const TsDuckConfigNative* config
);

/// Destroy an analyzer and free its resources.
/// Safe to call with NULL handle.
TSDUCK_API void tsduck_analyzer_destroy(TsDuckAnalyzerHandle analyzer);

/// Check if the analyzer is initialized and ready to process data.
TSDUCK_API bool tsduck_analyzer_is_initialized(TsDuckAnalyzerHandle analyzer);

// =============================================================================
// Data Processing
// =============================================================================

/// Feed MPEG-TS data to the analyzer.
/// @param analyzer The analyzer handle.
/// @param data Pointer to MPEG-TS data (must be packet-aligned, 188-byte packets).
/// @param length Number of bytes to process.
/// @return Number of packets processed, or negative error code.
TSDUCK_API int32_t tsduck_analyzer_feed(
    TsDuckAnalyzerHandle analyzer,
    const uint8_t* data,
    int32_t length
);

/// Reset the analyzer state for a new stream.
/// Clears all accumulated metrics and error counters.
TSDUCK_API void tsduck_analyzer_reset(TsDuckAnalyzerHandle analyzer);

// =============================================================================
// Metrics Retrieval
// =============================================================================

/// Get the current metrics snapshot.
/// @param analyzer The analyzer handle.
/// @param out_metrics Pointer to structure to receive metrics.
/// @return true if metrics were retrieved, false if no metrics available yet.
TSDUCK_API bool tsduck_analyzer_get_metrics(
    TsDuckAnalyzerHandle analyzer,
    TsDuckMetricsNative* out_metrics
);

/// Check if new metrics are available since last retrieval.
TSDUCK_API bool tsduck_analyzer_has_new_metrics(TsDuckAnalyzerHandle analyzer);

// =============================================================================
// PCR Analysis (Phase 2a)
// =============================================================================

/// Get PCR (Program Clock Reference) analysis metrics.
/// Provides detailed timing analysis including jitter, interval, and drift.
/// @param analyzer The analyzer handle.
/// @param out_analysis Pointer to structure to receive PCR analysis.
/// @return true if PCR analysis data is available, false otherwise.
TSDUCK_API bool tsduck_analyzer_get_pcr_analysis(
    TsDuckAnalyzerHandle analyzer,
    PcrAnalysisNative* out_analysis
);

/// Set the PCR jitter threshold for violation alerts.
/// @param analyzer The analyzer handle.
/// @param max_jitter_us Maximum acceptable jitter in microseconds.
///                      TR 101 290 limit is 0.5us (500ns).
/// @return true if threshold was set, false on error.
TSDUCK_API bool tsduck_analyzer_set_pcr_jitter_threshold(
    TsDuckAnalyzerHandle analyzer,
    double max_jitter_us
);

// =============================================================================
// IAT (Inter-packet Arrival Time) Analysis (Phase 2a)
// =============================================================================

/// Get IAT (Inter-packet Arrival Time) analysis metrics.
/// Provides network jitter analysis for UDP/IP streams.
/// @param analyzer The analyzer handle.
/// @param out_analysis Pointer to structure to receive IAT analysis.
/// @return true if IAT analysis data is available, false otherwise.
TSDUCK_API bool tsduck_analyzer_get_iat_analysis(
    TsDuckAnalyzerHandle analyzer,
    IatAnalysisNative* out_analysis
);

/// Set the IAT jitter threshold for violation alerts.
/// @param analyzer The analyzer handle.
/// @param max_jitter_us Maximum acceptable jitter in microseconds.
///                      Default is 1000us (1ms).
/// @return true if threshold was set, false on error.
TSDUCK_API bool tsduck_analyzer_set_iat_jitter_threshold(
    TsDuckAnalyzerHandle analyzer,
    double max_jitter_us
);

// =============================================================================
// Bitrate Analysis (Phase 2a)
// =============================================================================

/// Get detailed bitrate analysis metrics.
/// Provides PCR-based bitrate, null packet ratio, and bandwidth utilization.
/// @param analyzer The analyzer handle.
/// @param out_analysis Pointer to structure to receive bitrate analysis.
/// @return true if bitrate analysis data is available, false otherwise.
TSDUCK_API bool tsduck_analyzer_get_bitrate_analysis(
    TsDuckAnalyzerHandle analyzer,
    BitrateAnalysisNative* out_analysis
);

// =============================================================================
// Extended PID Information (Phase 2b)
// =============================================================================

/// Get extended information for all PIDs currently being tracked.
/// @param analyzer The analyzer handle.
/// @param out_pids Array to receive PID information.
/// @param max_pids Maximum number of PIDs to return (array size).
/// @return Number of PIDs returned, or negative error code.
TSDUCK_API int32_t tsduck_analyzer_get_pid_info_extended(
    TsDuckAnalyzerHandle analyzer,
    TsDuckPidInfoExtended* out_pids,
    int32_t max_pids
);

/// Get the number of PIDs currently being tracked.
/// Useful for allocating the right-sized array before calling get_pid_info_extended.
/// @param analyzer The analyzer handle.
/// @return Number of PIDs, or negative error code.
TSDUCK_API int32_t tsduck_analyzer_get_pid_count(TsDuckAnalyzerHandle analyzer);

// =============================================================================
// Callbacks
// =============================================================================

/// Set callback for metrics updates.
/// The callback will be invoked from the analyzer's processing thread.
/// Set to NULL to disable callbacks.
TSDUCK_API void tsduck_analyzer_set_metrics_callback(
    TsDuckAnalyzerHandle analyzer,
    TsDuckMetricsCallback callback,
    void* user_data
);

/// Set callback for quality violations.
/// The callback will be invoked when TR 101 290 violations are detected.
/// Set to NULL to disable callbacks.
TSDUCK_API void tsduck_analyzer_set_violation_callback(
    TsDuckAnalyzerHandle analyzer,
    TsDuckViolationCallback callback,
    void* user_data
);

#ifdef __cplusplus
}
#endif

#endif // TSDUCK_INTEROP_H
