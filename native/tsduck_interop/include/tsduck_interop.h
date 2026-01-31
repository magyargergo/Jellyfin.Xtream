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

// Library version
#define TSDUCK_INTEROP_VERSION "1.0.0"

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
    int32_t enable_auto_restamp;    // 1 = enable integrated restamping in feed()

    // Integrated restamping settings (used when enable_auto_restamp=1)
    int32_t restamp_mode;           // RestampingMode enum (0=disabled, 1=monitor, 2=correct)
    int32_t smooth_pcr;             // 1 = enable PCR jitter smoothing
    int32_t fix_discontinuities;    // 1 = repair PTS discontinuities
    int32_t reserved;               // Padding for alignment

    double correction_threshold_ms; // Start correcting at this drift (default: 45ms)
    double max_correction_rate_ms;  // Max correction per second (default: 10ms)
    double hysteresis_threshold_ms; // Stop correcting below this (default: 20ms)
    int64_t stream_bitrate_hint;    // Hint for CBR PCR smoothing (0=auto-detect)
} TsDuckConfigNative;

// PCR Analysis structure (Phase 2a - blittable)
// Tracks PCR timing per ISO/IEC 13818-1 requirements:
// - Accuracy: ±500ns phase tolerance
// - Frequency offset: ±30 ppm (±810 Hz at 27MHz)
// - Drift rate: 75 mHz/sec (10 ppm/hr)
typedef struct {
    double pcr_jitter_us;           // Current PCR jitter in microseconds
    double pcr_jitter_max_us;       // Maximum PCR jitter observed
    double pcr_jitter_avg_us;       // Average PCR jitter
    int64_t pcr_interval_packets;   // Packets between PCRs
    double pcr_interval_ms;         // Time between PCRs in milliseconds
    double pcr_drift_ppm;           // PCR frequency offset in parts-per-million
    int64_t pcr_count;              // Total PCRs received
    int64_t pcr_valid_count;        // Valid PCRs (within tolerance)

    // ISO/IEC 13818-1 compliance tracking (new fields)
    double pcr_frequency_offset_ppm; // Current frequency offset (limit: ±30 ppm)
    double pcr_drift_rate_ppm_hr;   // Drift rate in ppm/hour (limit: 10 ppm/hr)
    int32_t frequency_offset_valid; // 1=within ±30 ppm limit
    int32_t drift_rate_valid;       // 1=within 10 ppm/hr limit
    double pcr_accuracy_ns;         // Current PCR accuracy in nanoseconds
    int32_t accuracy_valid;         // 1=within ±500ns limit
    int32_t reserved;               // Padding for alignment
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

// =============================================================================
// A/V Sync Analysis Structures (Phase 3 - Restamping)
// =============================================================================

// Sync status enumeration
typedef enum {
    AVSYNC_STATUS_UNKNOWN = 0,      // Not enough data
    AVSYNC_STATUS_SYNCHRONIZED = 1, // Within tolerance (±20ms)
    AVSYNC_STATUS_DRIFTING = 2,     // Drift detected but correctable
    AVSYNC_STATUS_DESYNC = 3,       // Severe desync (>100ms)
    AVSYNC_STATUS_NO_AUDIO = 4,     // No audio PTS detected
    AVSYNC_STATUS_NO_VIDEO = 5,     // No video PTS detected
} AvSyncStatus;

// PTS/DTS sample for detailed analysis (blittable)
typedef struct {
    int32_t pid;                    // PID carrying this timestamp
    int32_t stream_type;            // 0x02=video, 0x03/0x04=audio, etc.
    int64_t pts_90khz;              // Presentation timestamp (90kHz)
    int64_t dts_90khz;              // Decoding timestamp (-1 if not present)
    int64_t pcr_90khz;              // Reference PCR at sample time (-1 if N/A)
    int64_t packet_index;           // Packet position in stream
    int64_t byte_offset;            // Byte offset in stream
    int32_t is_video;               // 1=video, 0=other
    int32_t is_audio;               // 1=audio, 0=other
    int32_t is_keyframe;            // 1=keyframe/RAP, 0=other (video only)
    int32_t reserved;               // Padding for alignment
} PtsDtsSampleNative;

// A/V synchronization analysis result (blittable)
typedef struct {
    // Current drift state
    double video_audio_drift_ms;    // Current A/V drift (+ = audio ahead)
    double drift_rate_ms_per_sec;   // Drift trend (+ = increasing drift)
    double peak_drift_ms;           // Maximum drift observed
    double avg_drift_ms;            // Average drift over window

    // PCR-PTS relationship
    double pcr_video_offset_ms;     // PCR to video PTS offset
    double pcr_audio_offset_ms;     // PCR to audio PTS offset

    // Sample counts
    int64_t video_pts_count;        // Video PTS samples collected
    int64_t audio_pts_count;        // Audio PTS samples collected
    int64_t pcr_count;              // PCR samples for reference

    // Discontinuity tracking
    int64_t video_discontinuities;  // Video PTS jumps detected
    int64_t audio_discontinuities;  // Audio PTS jumps detected
    int64_t pcr_discontinuities;    // PCR discontinuities detected

    // Timing
    int64_t last_video_pts;         // Most recent video PTS (90kHz)
    int64_t last_audio_pts;         // Most recent audio PTS (90kHz)
    int64_t last_pcr;               // Most recent PCR (90kHz base)

    // Status
    int32_t sync_status;            // AvSyncStatus enum value
    int32_t reserved;               // Padding for alignment
} AvSyncAnalysisNative;

// Restamping mode enumeration
typedef enum {
    RESTAMP_MODE_DISABLED = 0,      // No restamping
    RESTAMP_MODE_MONITOR = 1,       // Detect but don't correct
    RESTAMP_MODE_CORRECT = 2,       // Detect and apply corrections
} RestampingMode;

// Restamping configuration (blittable)
typedef struct {
    int32_t mode;                   // RestampingMode enum value
    int32_t smooth_pcr;             // 1=enable PCR jitter smoothing
    int32_t fix_discontinuities;    // 1=repair PTS discontinuities
    int32_t reserved;               // Padding

    double correction_threshold_ms; // Start correcting at this drift (default: 45ms)
    double max_correction_rate_ms;  // Max correction per second (default: 10ms)
    double hysteresis_threshold_ms; // Stop correcting below this (default: 20ms)

    int64_t stream_bitrate_hint;    // Hint for CBR PCR smoothing (0=auto-detect)
} RestampingConfigNative;

// Restamping statistics (blittable)
typedef struct {
    int64_t packets_processed;      // Total packets analyzed
    int64_t pcr_smoothed;           // PCRs that were smoothed
    int64_t pts_corrected;          // PTS values corrected
    int64_t dts_corrected;          // DTS values corrected
    int64_t discontinuities_fixed;  // Discontinuities repaired

    double total_correction_ms;     // Cumulative correction applied
    double current_offset_ms;       // Current correction offset

    int64_t last_correction_time;   // Timestamp of last correction (.NET ticks)
    int32_t correction_active;      // 1=currently applying corrections
    int32_t reserved;               // Padding
} RestampingStatisticsNative;

// Callback for correction events
typedef void (*TsDuckCorrectionCallback)(
    double correction_ms,           // Amount corrected
    const char* correction_type,    // "pcr", "video_pts", "audio_pts", "dts"
    void* user_data
);

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
// Logging Configuration
// =============================================================================

/// Log level enumeration
typedef enum {
    TSDUCK_LOG_NONE = 0,     // No logging
    TSDUCK_LOG_ERROR = 1,    // Errors only
    TSDUCK_LOG_WARNING = 2,  // Warnings and errors
    TSDUCK_LOG_INFO = 3,     // Info, warnings, and errors
    TSDUCK_LOG_DEBUG = 4,    // All messages including debug
    TSDUCK_LOG_TRACE = 5     // Most verbose - includes detailed tracing
} TsDuckLogLevel;

/// Callback signature for custom log handlers.
/// @param level Log level (TsDuckLogLevel enum value)
/// @param component Component name (e.g., "StreamPipeline", "Analyzer")
/// @param message Formatted log message
/// @param user_data User-provided context pointer
typedef void (*TsDuckLogCallback)(
    int32_t level,
    const char* component,
    const char* message,
    void* user_data
);

/// Set the global log level.
/// Messages at this level or below will be logged.
/// Default: TSDUCK_LOG_WARNING
/// @param level The log level to set.
TSDUCK_API void tsduck_set_log_level(int32_t level);

/// Get the current log level.
/// @return The current log level (TsDuckLogLevel enum value).
TSDUCK_API int32_t tsduck_get_log_level(void);

/// Set a custom log callback to receive log messages.
/// Set to NULL to use default stderr output.
/// @param callback The callback function, or NULL for default.
/// @param user_data User-provided context passed to callback.
TSDUCK_API void tsduck_set_log_callback(TsDuckLogCallback callback, void* user_data);

/// Check if a specific log level is enabled.
/// Useful for avoiding expensive string formatting when logging is disabled.
/// @param level The log level to check.
/// @return true if the level is enabled.
TSDUCK_API bool tsduck_is_log_enabled(int32_t level);

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

/// Feed MPEG-TS data with integrated restamping.
/// This function modifies data IN-PLACE to apply timestamp corrections,
/// then analyzes the corrected data. Use this when auto-restamp is enabled.
/// @param analyzer The analyzer handle.
/// @param data Pointer to MPEG-TS data (will be MODIFIED if restamping enabled).
/// @param length Number of bytes to process.
/// @return Number of packets processed, or negative error code.
TSDUCK_API int32_t tsduck_analyzer_feed_restamp(
    TsDuckAnalyzerHandle analyzer,
    uint8_t* data,
    int32_t length
);

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

// =============================================================================
// Integrated Restamping (through Analyzer)
// =============================================================================

/// Configure integrated restamping on an analyzer.
/// This allows changing restamping settings without recreating the analyzer.
/// @param analyzer The analyzer handle.
/// @param mode Restamping mode (RESTAMP_MODE_*).
/// @param smooth_pcr Enable PCR smoothing (1=yes, 0=no).
/// @param fix_discontinuities Fix timestamp discontinuities (1=yes, 0=no).
/// @return true if configuration was applied, false on error.
TSDUCK_API bool tsduck_analyzer_configure_restamp(
    TsDuckAnalyzerHandle analyzer,
    int32_t mode,
    int32_t smooth_pcr,
    int32_t fix_discontinuities
);

/// Notify analyzer's integrated restamper of a provider switch.
/// Calculates offset needed for timestamp continuity.
/// @param analyzer The analyzer handle.
/// @param last_output_pts Last PTS written to output (90kHz).
/// @param new_input_first_pts First PTS from new provider (90kHz).
TSDUCK_API void tsduck_analyzer_handle_switch(
    TsDuckAnalyzerHandle analyzer,
    int64_t last_output_pts,
    int64_t new_input_first_pts
);

/// Get integrated restamping statistics from analyzer.
/// @param analyzer The analyzer handle.
/// @param out_stats Pointer to structure to receive statistics.
/// @return true if statistics were retrieved (restamping enabled), false otherwise.
TSDUCK_API bool tsduck_analyzer_get_restamp_statistics(
    TsDuckAnalyzerHandle analyzer,
    RestampingStatisticsNative* out_stats
);

/// Check if integrated restamping is enabled on the analyzer.
/// @param analyzer The analyzer handle.
/// @return true if restamping is enabled.
TSDUCK_API bool tsduck_analyzer_is_restamping_enabled(
    TsDuckAnalyzerHandle analyzer
);

// =============================================================================
// A/V Sync Analysis (Phase 3 - Restamping)
// =============================================================================

/// Get A/V synchronization analysis.
/// Provides drift measurement, trend analysis, and sync status.
/// @param analyzer The analyzer handle.
/// @param out_analysis Pointer to structure to receive analysis.
/// @return true if analysis data is available, false otherwise.
TSDUCK_API bool tsduck_analyzer_get_av_sync_analysis(
    TsDuckAnalyzerHandle analyzer,
    AvSyncAnalysisNative* out_analysis
);

/// Get recent PTS/DTS samples for detailed analysis.
/// Returns samples in chronological order (oldest first).
/// @param analyzer The analyzer handle.
/// @param out_samples Array to receive samples.
/// @param max_samples Maximum number of samples to return.
/// @return Number of samples returned, or negative error code.
TSDUCK_API int32_t tsduck_analyzer_get_pts_samples(
    TsDuckAnalyzerHandle analyzer,
    PtsDtsSampleNative* out_samples,
    int32_t max_samples
);

/// Get the number of PTS/DTS samples currently buffered.
/// @param analyzer The analyzer handle.
/// @return Number of samples available.
TSDUCK_API int32_t tsduck_analyzer_get_pts_sample_count(
    TsDuckAnalyzerHandle analyzer
);

// =============================================================================
// PSI Program Information (Phase 3b - Deep TsDuck Integration)
// =============================================================================

// Program information from PAT/PMT parsing (blittable)
typedef struct {
    int32_t program_number;         // Program number from PAT
    int32_t pmt_pid;                // PMT PID from PAT
    int32_t pcr_pid;                // PCR PID from PMT
    int32_t stream_count;           // Elementary streams in this program
    int32_t has_video;              // 1=has video ES, 0=no
    int32_t has_audio;              // 1=has audio ES, 0=no
    int32_t reserved;               // Padding
    int32_t reserved2;              // Padding
} TsDuckProgramInfoNative;

/// Get program information discovered from PAT/PMT parsing.
/// @param analyzer The analyzer handle.
/// @param out_programs Array to receive program information.
/// @param max_programs Maximum number of programs to return (array size).
/// @return Number of programs returned, or negative error code.
TSDUCK_API int32_t tsduck_analyzer_get_programs(
    TsDuckAnalyzerHandle analyzer,
    TsDuckProgramInfoNative* out_programs,
    int32_t max_programs
);

// =============================================================================
// SCTE-35 Splice Information Parsing
// =============================================================================

// SCTE-35 splice event structure (blittable)
typedef struct {
    uint32_t splice_event_id;       // Unique identifier for the splice event
    uint64_t pts_time;              // 33-bit PTS time when splice should occur
    uint64_t duration_pts;          // Duration in PTS ticks (0 if not specified)
    int32_t out_of_network;         // 1 = ad break start, 0 = return to content
    int32_t splice_immediate;       // 1 = immediate splice, 0 = scheduled
    uint8_t splice_command_type;    // 0x00=null, 0x04=schedule, 0x05=insert, 0x06=time_signal
    uint8_t reserved1;              // Padding
    uint16_t scte35_pid;            // PID carrying this SCTE-35 data
    int64_t packet_index;           // Packet index where event was detected
} Scte35EventNative;

// SCTE-35 splice state enumeration
typedef enum {
    SCTE35_STATE_IN_CONTENT = 0,    // Normal content playback
    SCTE35_STATE_IN_BREAK = 1,      // Inside ad break (out_of_network=true)
    SCTE35_STATE_TRANSITIONING = 2  // Transitioning between states
} Scte35SpliceState;

// Callback for SCTE-35 events (called from analyzer thread)
typedef void (*TsDuckScte35Callback)(
    const Scte35EventNative* event,
    void* user_data
);

/// Get pending SCTE-35 splice events from the analyzer.
/// Events are returned in chronological order (oldest first).
/// @param analyzer The analyzer handle.
/// @param out_events Array to receive events.
/// @param max_events Maximum number of events to return.
/// @return Number of events returned, or negative error code.
TSDUCK_API int32_t tsduck_analyzer_get_scte35_events(
    TsDuckAnalyzerHandle analyzer,
    Scte35EventNative* out_events,
    int32_t max_events
);

/// Get the current/most recent SCTE-35 splice event.
/// @param analyzer The analyzer handle.
/// @param out_event Pointer to receive the event.
/// @return true if an event is available, false otherwise.
TSDUCK_API bool tsduck_analyzer_get_current_scte35_event(
    TsDuckAnalyzerHandle analyzer,
    Scte35EventNative* out_event
);

/// Get the current splice state (in content, in break, etc.).
/// @param analyzer The analyzer handle.
/// @return Scte35SpliceState enum value.
TSDUCK_API int32_t tsduck_analyzer_get_scte35_state(
    TsDuckAnalyzerHandle analyzer
);

/// Check if the stream is currently in an ad break.
/// @param analyzer The analyzer handle.
/// @return true if currently in ad break (out_of_network=true).
TSDUCK_API bool tsduck_analyzer_is_in_ad_break(
    TsDuckAnalyzerHandle analyzer
);

/// Get the total number of SCTE-35 events received.
/// @param analyzer The analyzer handle.
/// @return Total event count.
TSDUCK_API int64_t tsduck_analyzer_get_scte35_event_count(
    TsDuckAnalyzerHandle analyzer
);

/// Set a callback for SCTE-35 splice events.
/// The callback is invoked from the analyzer thread when events are detected.
/// @param analyzer The analyzer handle.
/// @param callback The callback function, or NULL to disable.
/// @param user_data User-provided context passed to callback.
TSDUCK_API void tsduck_analyzer_set_scte35_callback(
    TsDuckAnalyzerHandle analyzer,
    TsDuckScte35Callback callback,
    void* user_data
);

// =============================================================================
// NAL Unit Parsing (H.264/H.265 SPS/PPS/VPS Extraction)
// =============================================================================

// Video codec type enumeration
typedef enum {
    VIDEO_CODEC_UNKNOWN = 0,
    VIDEO_CODEC_H264_AVC = 1,
    VIDEO_CODEC_H265_HEVC = 2,
    VIDEO_CODEC_H266_VVC = 3
} VideoCodecTypeEnum;

// Video codec information structure (blittable)
typedef struct {
    uint8_t codec_type;             // VideoCodecTypeEnum value
    uint8_t profile;                // Profile (profile_idc for H.264)
    uint8_t level;                  // Level (level_idc for H.264)
    uint8_t reserved;               // Padding
    uint16_t width;                 // Picture width in pixels
    uint16_t height;                // Picture height in pixels
    uint16_t frame_rate_num;        // Frame rate numerator (0 if unknown)
    uint16_t frame_rate_den;        // Frame rate denominator (0 if unknown)
    int32_t interlaced;             // 1 = interlaced, 0 = progressive
} VideoCodecInfoNative;

// NAL parameter sets structure (blittable)
// Contains cached SPS/PPS/VPS for stream initialization
typedef struct {
    uint16_t video_pid;             // Video PID these parameters belong to
    uint16_t reserved;              // Padding

    VideoCodecInfoNative codec_info; // Parsed codec information

    uint8_t sps_data[256];          // Cached SPS NAL unit (without start code)
    uint16_t sps_length;            // SPS data length in bytes

    uint8_t pps_data[128];          // Cached PPS NAL unit (without start code)
    uint16_t pps_length;            // PPS data length in bytes

    uint8_t vps_data[128];          // HEVC VPS (0 length for H.264)
    uint16_t vps_length;            // VPS data length in bytes

    int32_t parameters_complete;    // 1 = all required params available
} NalParameterSetsNative;

/// Get video codec information for a specific video PID.
/// @param analyzer The analyzer handle.
/// @param video_pid The video PID to query.
/// @param out_info Pointer to receive codec information.
/// @return true if codec info is available, false otherwise.
TSDUCK_API bool tsduck_analyzer_get_video_codec_info(
    TsDuckAnalyzerHandle analyzer,
    uint16_t video_pid,
    VideoCodecInfoNative* out_info
);

/// Get cached NAL parameter sets (SPS/PPS/VPS) for a video PID.
/// These can be used for stream initialization when starting mid-stream.
/// @param analyzer The analyzer handle.
/// @param video_pid The video PID to query.
/// @param out_params Pointer to receive parameter sets.
/// @return true if parameter sets are available, false otherwise.
TSDUCK_API bool tsduck_analyzer_get_parameter_sets(
    TsDuckAnalyzerHandle analyzer,
    uint16_t video_pid,
    NalParameterSetsNative* out_params
);

/// Check if the most recent packet on a PID contained an IDR frame.
/// This uses NAL unit type detection (more accurate than transport RAI flag).
/// @param analyzer The analyzer handle.
/// @param video_pid The video PID to check.
/// @return true if an IDR frame was detected in the last processed packet.
TSDUCK_API bool tsduck_analyzer_has_idr_frame(
    TsDuckAnalyzerHandle analyzer,
    uint16_t video_pid
);

/// Get the number of IDR frames detected on all video PIDs.
/// @param analyzer The analyzer handle.
/// @return Total IDR frame count.
TSDUCK_API int64_t tsduck_analyzer_get_idr_frame_count(
    TsDuckAnalyzerHandle analyzer
);

/// Register a video PID for NAL parsing.
/// This is typically called automatically when PMT is parsed, but can be
/// called manually for streams without proper PMT signaling.
/// @param analyzer The analyzer handle.
/// @param video_pid The video PID to register.
/// @param stream_type MPEG stream type (0x1B=H.264, 0x24=H.265, 0x33=H.266).
/// @return true if registered successfully.
TSDUCK_API bool tsduck_analyzer_register_video_pid(
    TsDuckAnalyzerHandle analyzer,
    uint16_t video_pid,
    uint8_t stream_type
);

// =============================================================================
// HTTP Streamer (native curl-based streaming with failover)
// =============================================================================

// Opaque handle
typedef struct TsDuckStreamer* TsDuckStreamerHandle;

// Streamer state enumeration
typedef enum {
    STREAMER_STATE_IDLE = 0,
    STREAMER_STATE_CONNECTING = 1,
    STREAMER_STATE_STREAMING = 2,
    STREAMER_STATE_RECONNECTING = 3,
    STREAMER_STATE_SWITCHING = 4,
    STREAMER_STATE_STALLED = 5,
    STREAMER_STATE_STOPPED = 6,
    STREAMER_STATE_FAILED = 7
} TsDuckStreamerState;

// Streamer event enumeration
typedef enum {
    STREAMER_EVENT_CONNECTED = 0,
    STREAMER_EVENT_DISCONNECTED = 1,
    STREAMER_EVENT_RECONNECTING = 2,
    STREAMER_EVENT_SWITCHED = 3,
    STREAMER_EVENT_STALLED = 4,
    STREAMER_EVENT_DATA_RECEIVED = 5,
    STREAMER_EVENT_ERROR = 6,
    STREAMER_EVENT_STOPPED = 7,
    STREAMER_EVENT_QUALITY_DEGRADED = 8  // TR 101 290 error rate exceeded threshold
} TsDuckStreamerEvent;

// Streamer configuration (blittable)
typedef struct {
    int32_t connect_timeout_ms;         // TCP+TLS handshake timeout (default: 5000)
    int32_t response_timeout_ms;        // Time to first byte (default: 10000)
    int32_t stall_timeout_ms;           // No-data threshold (default: 20000)
    int32_t max_retries;                // Total attempts before Failed (default: 10)

    int32_t initial_backoff_ms;         // First retry delay (default: 500)
    int32_t max_backoff_ms;             // Backoff cap (default: 30000)
    double backoff_multiplier;          // Exponential factor (default: 2.0)
    int32_t backoff_jitter_ms;          // Random jitter range (default: 200)

    int32_t output_fd;                  // Pipe fd for FFmpeg (-1 = callback mode)
    int32_t alignment_buffer_packets;   // TS packets to buffer (default: 32)

    int32_t enable_restamp;             // Feed through restamper (default: 1)
    int32_t restamp_mode;               // RESTAMP_MODE_* (default: CORRECT)

    int32_t low_speed_limit_bytes;      // Min bytes/sec for stall (default: 1000)
    int32_t low_speed_time_sec;         // Duration below limit (default: 10)

    int32_t stalls_before_switch;       // Consecutive stalls before URL rotation (default: 2)
    int32_t timeout_immediate_switch;   // 1=switch URL on first timeout, 0=count as regular failure (default: 1)

    // Quality-based switching (TR 101 290 error rate thresholds)
    int32_t enable_quality_switch;      // 1 = enabled, 0 = disabled (default: 1)
    int32_t quality_check_interval_ms;  // How often to evaluate quality (default: 1000)
    int32_t quality_window_seconds;     // Sliding window for rate calc (default: 10)
    int32_t max_sync_errors_per_window; // sync_loss threshold (default: 1)
    int32_t max_continuity_errors_per_sec; // CC errors/sec (default: 20)
    int32_t max_transport_errors_per_sec;  // TEI errors/sec (default: 10)
    int32_t max_pcr_errors_per_sec;     // PCR errors/sec (default: 5)

    // Health-based URL selection settings
    int32_t quarantine_duration_ms;     // Initial quarantine after failure (default: 30000)
    int32_t max_quarantine_duration_ms; // Maximum quarantine cap (default: 300000)
    double quarantine_backoff_multiplier; // Exponential backoff (default: 2.0)
    double score_boost_on_success;      // Score increase on data received (default: 0.5)
    double score_penalty_on_failure;    // Score decrease on failure (default: 5.0)
    double default_health_score;        // Default score for new URLs (default: 50.0)
} TsDuckStreamerConfigNative;

// Streamer status snapshot (blittable)
typedef struct {
    int32_t state;                      // TsDuckStreamerState enum value
    int32_t current_url_index;          // Active URL (0-based)
    int32_t url_count;                  // Total URLs configured
    int32_t retry_count;                // Current retry attempt
    int64_t bytes_received;             // Total bytes from network
    int64_t packets_output;             // Total TS packets written to output
    int64_t switches_completed;         // Number of URL switches
    int64_t reconnections;              // Number of reconnection attempts
    int64_t last_data_time_ticks;       // Last data received (.NET ticks)
    int64_t session_start_ticks;        // Session start (.NET ticks)
    int32_t last_http_status;           // Last HTTP response code
    int32_t last_curl_error;            // Last CURLcode error
    int64_t quality_switches;           // Switches triggered by quality degradation
} TsDuckStreamerStatusNative;

// Event callback (called from streaming thread)
typedef void (*TsDuckStreamerEventCallback)(
    int32_t event,          // TsDuckStreamerEvent enum value
    int32_t detail,         // Event-specific detail (HTTP status, curl code, URL index)
    void* user_data
);

// Data output callback (called from streaming thread with restamped TS data)
typedef void (*TsDuckStreamerOutputCallback)(
    const uint8_t* data,
    int32_t length,         // Always multiple of 188
    void* user_data
);

/// Create a new streamer instance.
/// @param config Streamer configuration. If NULL, defaults are used.
/// @param analyzer_config Analyzer configuration. If NULL, defaults are used.
/// @return Streamer handle, or NULL on failure.
TSDUCK_API TsDuckStreamerHandle tsduck_streamer_create(
    const TsDuckStreamerConfigNative* config,
    const TsDuckConfigNative* analyzer_config
);

/// Destroy a streamer and free all resources.
/// Stops streaming if active. Safe to call with NULL.
TSDUCK_API void tsduck_streamer_destroy(TsDuckStreamerHandle streamer);

/// Add a URL to the streamer's URL list with default health score.
/// @param streamer The streamer handle.
/// @param url Null-terminated URL string.
/// @return TSDUCK_OK or error code.
TSDUCK_API int32_t tsduck_streamer_add_url(
    TsDuckStreamerHandle streamer,
    const char* url
);

/// Add a URL with specified health score for intelligent selection.
/// @param streamer The streamer handle.
/// @param url Null-terminated URL string.
/// @param health_score Health score (0.0-100.0), higher = better. Clamped to range.
/// @return TSDUCK_OK or error code.
TSDUCK_API int32_t tsduck_streamer_add_url_with_score(
    TsDuckStreamerHandle streamer,
    const char* url,
    double health_score
);

/// Update health score for existing URL by index.
/// Use this to update scores based on external provider reliability data.
/// @param streamer The streamer handle.
/// @param url_index Index of URL to update (0-based).
/// @param new_score New health score (0.0-100.0). Clamped to range.
/// @return TSDUCK_OK or TSDUCK_ERROR_INVALID_DATA if index invalid.
TSDUCK_API int32_t tsduck_streamer_update_url_score(
    TsDuckStreamerHandle streamer,
    int32_t url_index,
    double new_score
);

/// Get health score for URL by index.
/// @param streamer The streamer handle.
/// @param url_index Index of URL to query (0-based).
/// @return Health score (0.0-100.0) or -1.0 if index invalid.
TSDUCK_API double tsduck_streamer_get_url_score(
    TsDuckStreamerHandle streamer,
    int32_t url_index
);

/// Clear all URLs from the streamer.
TSDUCK_API void tsduck_streamer_clear_urls(TsDuckStreamerHandle streamer);

/// Set the output file descriptor (pipe for FFmpeg).
/// Must be called before start(). Set to -1 for callback mode.
TSDUCK_API void tsduck_streamer_set_output_fd(
    TsDuckStreamerHandle streamer,
    int32_t fd
);

/// Set the output data callback.
/// Called from the streaming thread with restamped TS data.
TSDUCK_API void tsduck_streamer_set_output_callback(
    TsDuckStreamerHandle streamer,
    TsDuckStreamerOutputCallback callback,
    void* user_data
);

/// Set the event callback.
/// Called from the streaming thread on state changes.
TSDUCK_API void tsduck_streamer_set_event_callback(
    TsDuckStreamerHandle streamer,
    TsDuckStreamerEventCallback callback,
    void* user_data
);

/// Start streaming. Spawns a worker thread.
/// At least one URL must be added before calling this.
/// @return true if started successfully.
TSDUCK_API bool tsduck_streamer_start(TsDuckStreamerHandle streamer);

/// Stop streaming. Blocks until worker thread exits.
TSDUCK_API void tsduck_streamer_stop(TsDuckStreamerHandle streamer);

/// Request a switch to the next URL in the rotation.
/// The switch happens asynchronously on the next loop iteration.
TSDUCK_API void tsduck_streamer_request_switch(TsDuckStreamerHandle streamer);

/// Get the current streamer status snapshot (lock-free).
/// @param streamer The streamer handle.
/// @param out_status Pointer to receive status.
/// @return true if status retrieved, false on error.
TSDUCK_API bool tsduck_streamer_get_status(
    TsDuckStreamerHandle streamer,
    TsDuckStreamerStatusNative* out_status
);

/// Get the internal analyzer handle (for querying metrics).
/// The returned handle is owned by the streamer - do NOT destroy it.
/// @return Analyzer handle, or NULL if streamer is invalid.
TSDUCK_API TsDuckAnalyzerHandle tsduck_streamer_get_analyzer(
    TsDuckStreamerHandle streamer
);

// =============================================================================
// Streamer Shared Memory Output Mode
// =============================================================================

/// Configure the streamer to use shared memory for output instead of callbacks.
/// Must be called before tsduck_streamer_start().
/// @param streamer The streamer handle.
/// @param name Unique name for the shared memory region (null-terminated).
/// @param slot_count Number of slots in ring buffer (power of 2, 0 for default 1024).
/// @param slot_size Size of each slot in bytes (multiple of 188, 0 for default 1316).
/// @return TSDUCK_OK on success, error code on failure.
TSDUCK_API int32_t tsduck_streamer_set_shared_memory_output(
    TsDuckStreamerHandle streamer,
    const char* name,
    uint32_t slot_count,
    uint32_t slot_size
);

/// Get the shared memory name configured for the streamer.
/// @param streamer The streamer handle.
/// @return The shared memory name, or NULL if not in shared memory mode.
TSDUCK_API const char* tsduck_streamer_get_shared_memory_name(
    TsDuckStreamerHandle streamer
);

/// Check if the streamer is in shared memory output mode.
/// @param streamer The streamer handle.
/// @return 1 if in shared memory mode, 0 otherwise.
TSDUCK_API int32_t tsduck_streamer_is_shared_memory_mode(
    TsDuckStreamerHandle streamer
);

// =============================================================================
// Shared Memory Producer (for E2E testing)
// =============================================================================

/// Create a shared memory producer for streaming TS data.
/// @param name Unique name for the shared memory region (null-terminated).
/// @param slot_count Number of slots in ring buffer (must be power of 2).
/// @param slot_size Size of each slot in bytes (must be multiple of 188).
/// @return Opaque handle to the producer, or NULL on failure.
TSDUCK_API void* tsduck_shm_producer_create(
    const char* name,
    uint32_t slot_count,
    uint32_t slot_size
);

/// Destroy a shared memory producer and free all resources.
/// Safe to call with NULL handle.
/// @param producer The producer handle.
TSDUCK_API void tsduck_shm_producer_destroy(void* producer);

/// Write TS packet data to the shared memory ring buffer.
/// @param producer The producer handle.
/// @param data Pointer to TS data (must be multiple of 188 bytes).
/// @param length Number of bytes to write.
/// @param bytes_written Output: number of bytes actually written.
/// @return 1 if overflow occurred (data may be lost), 0 otherwise, -1 on error.
TSDUCK_API int32_t tsduck_shm_producer_write(
    void* producer,
    const uint8_t* data,
    uint32_t length,
    uint32_t* bytes_written
);

/// Signal the consumer that data is available.
/// Call after writing a batch of data for efficient wakeup.
/// @param producer The producer handle.
TSDUCK_API void tsduck_shm_producer_signal(void* producer);

/// Set the end-of-stream flag.
/// Consumer will complete after reading remaining data.
/// @param producer The producer handle.
TSDUCK_API void tsduck_shm_producer_set_eos(void* producer);

/// Set an error condition.
/// @param producer The producer handle.
/// @param code Error code.
/// @param message Human-readable error message (null-terminated).
TSDUCK_API void tsduck_shm_producer_set_error(
    void* producer,
    uint32_t code,
    const char* message
);

/// Set the discontinuity flag.
/// Consumer should handle stream discontinuity (e.g., after URL switch).
/// @param producer The producer handle.
TSDUCK_API void tsduck_shm_producer_set_discontinuity(void* producer);

/// Clear the discontinuity flag.
/// @param producer The producer handle.
TSDUCK_API void tsduck_shm_producer_clear_discontinuity(void* producer);

/// Check if consumer is attached to the shared memory region.
/// @param producer The producer handle.
/// @return 1 if consumer is attached, 0 otherwise.
TSDUCK_API int32_t tsduck_shm_producer_is_consumer_attached(void* producer);

// =============================================================================
// Channel Registry (Native Channel-to-Provider Mapping)
// =============================================================================
//
// Minimal C API for streaming setup. C++ handles all health tracking,
// quality monitoring, and failover decisions internally.

// Opaque handle
typedef struct ChannelRegistry* ChannelRegistryHandle;

// Provider information (blittable, for registration)
typedef struct {
    char id[16];              // Provider ID (null-terminated)
    char name[64];            // Provider display name
    char base_url[512];       // Base URL for API
    char username[128];       // Username for auth
    char password[128];       // Password for auth
    int32_t priority;         // 0=highest priority
    int32_t id_hash;          // Hash for GUID generation
    double initial_health;    // Starting health score (0-100)
} RegistryProviderInfoNative;

// Registry statistics (blittable, for diagnostics)
typedef struct {
    int32_t provider_count;   // Number of providers registered
    int32_t channel_count;    // Unique channels after deduplication
    int32_t stream_count;     // Total streams before deduplication
    int32_t guid_count;       // Total GUIDs in lookup
    int32_t skipped_count;    // Streams with empty normalized names
} RegistryStatsNative;

/// Create a new channel registry.
/// @return Registry handle, or NULL on failure.
TSDUCK_API ChannelRegistryHandle tsduck_registry_create(void);

/// Destroy a channel registry and free all resources.
/// Safe to call with NULL handle.
TSDUCK_API void tsduck_registry_destroy(ChannelRegistryHandle registry);

/// Add a provider to the registry.
/// @param registry The registry handle.
/// @param info Provider information.
/// @return Provider index (>=0) on success, -1 if already built.
TSDUCK_API int32_t tsduck_registry_add_provider(
    ChannelRegistryHandle registry,
    const RegistryProviderInfoNative* info
);

/// Add a stream to the registry.
/// @param registry The registry handle.
/// @param provider_index Index returned by add_provider().
/// @param stream_id Provider's stream ID.
/// @param name Raw channel name (will be normalized).
/// @param icon_url Optional icon URL (can be NULL).
/// @return 0 on success, <0 on error (-1=built, -2=invalid provider, -3=null name).
TSDUCK_API int32_t tsduck_registry_add_stream(
    ChannelRegistryHandle registry,
    int32_t provider_index,
    int32_t stream_id,
    const char* name,
    const char* icon_url
);

/// Build the registry. After this, no more add_* calls allowed.
/// Performs channel name normalization, quality scoring, and GUID generation.
/// @param registry The registry handle.
/// @return true on success, false if already built.
TSDUCK_API bool tsduck_registry_build(ChannelRegistryHandle registry);

/// Get registry statistics (for diagnostics/logging).
/// @param registry The registry handle.
/// @param out_stats Pointer to receive statistics.
/// @return true on success.
TSDUCK_API bool tsduck_registry_get_stats(
    ChannelRegistryHandle registry,
    RegistryStatsNative* out_stats
);

// =============================================================================
// Streamer Registry Integration
// =============================================================================
//
// Connect a channel registry to a streamer for GUID-based streaming.
// The streamer will use the registry to look up URLs and track health.

/// Set the channel registry for GUID-based URL lookups.
/// The registry must outlive the streamer and must be built before start().
/// @param streamer The streamer handle.
/// @param registry The registry handle (can be NULL to clear).
/// @return TSDUCK_OK on success.
TSDUCK_API int32_t tsduck_streamer_set_registry(
    TsDuckStreamerHandle streamer,
    ChannelRegistryHandle registry
);

/// Set the channel GUID for registry-based streaming.
/// When registry is set and GUID is valid, start() will populate URLs from registry.
/// The GUID is 128-bit, passed as two 64-bit values for C interop.
/// @param streamer The streamer handle.
/// @param guid_high High 64 bits of the channel GUID.
/// @param guid_low Low 64 bits of the channel GUID.
/// @return TSDUCK_OK on success.
TSDUCK_API int32_t tsduck_streamer_set_channel_guid(
    TsDuckStreamerHandle streamer,
    int64_t guid_high,
    int64_t guid_low
);

#ifdef __cplusplus
}
#endif

#endif // TSDUCK_INTEROP_H
