// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_STREAMING_TYPES_HPP
#define TSDUCK_INTEROP_STREAMING_TYPES_HPP

#include <cstdint>

namespace tsduck_interop::streaming {

// ============================================================================
// Streamer State Machine States
// ============================================================================

// NOLINTBEGIN(performance-enum-size) - int32_t required for C# P/Invoke interop
enum class StreamerState : int32_t {
    Idle = 0,           // Not started
    Connecting = 1,     // curl establishing connection
    Streaming = 2,      // Actively receiving data
    Reconnecting = 3,   // Backoff before retry on same URL
    Switching = 4,      // Mid-stream URL switch in progress
    Stalled = 5,        // No data received within threshold
    Stopped = 6,        // Explicitly stopped
    Failed = 7          // Unrecoverable error (max retries exhausted)
};

// ============================================================================
// Stream Events (for callbacks)
// ============================================================================

enum class StreamEvent : int32_t {
    Connected = 0,      // Successfully connected to URL
    Disconnected = 1,   // Connection lost
    Reconnecting = 2,   // Starting reconnection attempt
    Switched = 3,       // Successfully switched to new URL
    Stalled = 4,        // Data stall detected
    DataReceived = 5,   // First data received after connect/reconnect
    Error = 6,          // HTTP or network error (detail = HTTP status or curl code)
    Stopped = 7,        // Streaming stopped (explicit or exhausted)
    QualityDegraded = 8 // TR 101 290 error rate exceeded threshold, switching URL
};
// NOLINTEND(performance-enum-size)

// ============================================================================
// Streamer Configuration (blittable for C# marshalling)
// ============================================================================

struct StreamerConfig {
    // Connection settings
    int32_t connect_timeout_ms = 5000;      // TCP+TLS handshake timeout
    int32_t response_timeout_ms = 10000;    // Time to first byte
    int32_t stall_timeout_ms = 20000;       // No data for this long = stalled
    int32_t max_retries = 10;               // Total retry attempts before Failed

    // Backoff settings
    int32_t initial_backoff_ms = 500;       // First retry delay
    int32_t max_backoff_ms = 30000;         // Cap on backoff delay
    double backoff_multiplier = 2.0;        // Exponential growth factor
    int32_t backoff_jitter_ms = 200;        // Random jitter range

    // Output settings
    int32_t output_fd = -1;                 // Pipe fd for FFmpeg (-1 = callback mode)
    int32_t alignment_buffer_packets = 32;  // TS packets to buffer for alignment

    // Analyzer integration
    int32_t enable_restamp = 1;             // Feed through restamper
    int32_t restamp_mode = 2;               // RESTAMP_MODE_CORRECT

    // Low-speed stall detection (libcurl)
    int32_t low_speed_limit_bytes = 1000;   // Minimum bytes/sec
    int32_t low_speed_time_sec = 10;        // Duration below limit = stall

    // Consecutive stalls before URL rotation
    int32_t stalls_before_switch = 2;

    // Quality-based switching (TR 101 290 error rate thresholds)
    // When error rates exceed these thresholds, trigger URL switch.
    // Set to 0 to disable quality-based switching entirely.
    int32_t enable_quality_switch = 1;          // 1 = enabled, 0 = disabled
    int32_t quality_check_interval_ms = 1000;   // How often to evaluate quality
    int32_t quality_window_seconds = 10;        // Sliding window for rate calculation

    // Error rate thresholds (per second, averaged over window)
    // Priority 1 (critical - affects decodability):
    int32_t max_sync_errors_per_window = 1;     // Any sync_loss = immediate switch
    int32_t max_continuity_errors_per_sec = 20; // CC errors/sec (~1% packet loss at 3Mbps)

    // Priority 2 (recommended monitoring):
    int32_t max_transport_errors_per_sec = 10;  // TEI bit errors/sec
    int32_t max_pcr_errors_per_sec = 5;         // PCR discontinuity+repetition/sec
};

// ============================================================================
// Streamer Status Snapshot (blittable, seqlock-protected)
// ============================================================================

struct StreamerStatus {
    int32_t state;                  // StreamerState enum value
    int32_t current_url_index;      // Active URL (0-based)
    int32_t url_count;              // Total URLs configured
    int32_t retry_count;            // Current retry attempt number
    int64_t bytes_received;         // Total bytes from network
    int64_t packets_output;         // Total TS packets written to output
    int64_t switches_completed;     // Number of URL switches
    int64_t reconnections;          // Number of reconnection attempts
    int64_t last_data_time_ticks;   // Last data received (.NET ticks)
    int64_t session_start_ticks;    // Session start (.NET ticks)
    int32_t last_http_status;       // Last HTTP response code
    int32_t last_curl_error;        // Last CURLcode error
    int64_t quality_switches;       // Switches triggered by quality degradation
};

// ============================================================================
// Callback Signatures (C-compatible)
// ============================================================================

// Event callback: fired on state changes
using StreamerEventCallback = void (*)(
    int32_t event,          // StreamEvent enum value
    int32_t detail,         // Event-specific detail (HTTP status, curl code, URL index)
    void* user_data
);

// Data output callback: fired with restamped TS data
using StreamerOutputCallback = void (*)(
    const uint8_t* data,
    int32_t length,         // Always multiple of ts::PKT_SIZE (188)
    void* user_data
);

}  // namespace tsduck_interop::streaming

#endif  // TSDUCK_INTEROP_STREAMING_TYPES_HPP
