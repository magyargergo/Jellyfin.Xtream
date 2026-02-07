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

// ============================================================================
// Disconnect Reasons (for failover decision-making)
// ============================================================================

enum class DisconnectReason : int32_t {
    Unknown = 0,           // Unknown/unclassified reason
    Normal = 1,            // Clean disconnect
    Timeout = 2,           // CURLE_OPERATION_TIMEDOUT (28)
    ConnectionFailed = 3,  // CURLE_COULDNT_CONNECT (7), CURLE_COULDNT_RESOLVE_HOST (6)
    HttpError = 4,         // HTTP 4xx/5xx response
    Aborted = 5,           // User abort / explicit stop
    DnsResolutionFailed = 6, // CURLE_COULDNT_RESOLVE_HOST (6) - DNS specific
    DnsTimeout = 7,        // DNS resolution timed out
    SslHandshakeFailed = 8 // CURLE_SSL_CONNECT_ERROR (35), CURLE_PEER_FAILED_VERIFICATION (60)
};
// NOLINTEND(performance-enum-size)

// ============================================================================
// Streamer Configuration (blittable for C# marshalling)
// ============================================================================

struct StreamerConfig {
    // Connection settings (based on industry standards research)
    // Connect: 10-15s recommended for slow IPTV providers
    // Response: 15s recommended for Xtream API variability
    // Stall: 30s industry standard (TVHeadend, etc.)
    int32_t connect_timeout_ms = 10000;     // TCP+TLS handshake timeout
    int32_t response_timeout_ms = 15000;    // Time to first byte
    int32_t stall_timeout_ms = 30000;       // No data for this long = stalled
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

    // Timeout handling: 1 = switch URL immediately on timeout, 0 = count as regular failure
    int32_t timeout_immediate_switch = 1;

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

    // Health-based URL selection and quarantine settings
    int32_t quarantine_duration_ms = 30000;     // Initial quarantine after failure (30s)
    int32_t max_quarantine_duration_ms = 300000; // Maximum quarantine cap (5 minutes)
    double quarantine_backoff_multiplier = 2.0; // Exponential backoff for consecutive failures
    double score_boost_on_success = 0.5;        // Score increase on successful data
    double score_penalty_on_failure = 5.0;      // Score decrease on failure
    double default_health_score = 50.0;         // Default score for new URLs

    // Circuit breaker settings (use small values for E2E testing)
    int32_t circuit_breaker_short_window_size = 0;  // 0 = use default (1500)
    int32_t circuit_breaker_long_window_size = 0;   // 0 = use default (3000)
    int32_t circuit_breaker_short_window_error_percent = 0; // 0 = use default (10%)
    int32_t circuit_breaker_long_window_error_percent = 0;  // 0 = use default (5%)

    // DNS retry settings (for transient DNS failures)
    // DNS failures can be temporary (e.g., DNS server overload, network hiccup)
    // so we retry a few times before force-ejecting the provider for 5 minutes.
    // Note: The actual DNS failure threshold is managed by UnifiedProviderHealthManager
    // which uses provider-indexed tracking. This field is kept for backward compatibility.
    int32_t dns_retry_count = 3;          // Retries before force ejection (deprecated, use health manager config)
    int64_t dns_ejection_duration_ms = 300000;  // Duration of DNS-based ejection (default: 5 minutes)

    // Load balancer settings (P2C, EWMA, outlier detection)
    int32_t enable_p2c = 1;                     // 1 = P2C selection, 0 = round-robin
    int32_t enable_outlier_detection = 1;       // 1 = statistical outlier ejection
    int32_t ewma_decay_seconds = 10;            // EWMA latency decay time (seconds)
    int32_t probation_success_threshold = 3;    // Successes before leaving probation
    int32_t min_samples_for_outlier = 10;       // Minimum samples for outlier detection
    int32_t reserved_lb = 0;                    // Padding for alignment
    double outlier_stddev_factor = 1.9;         // Std deviation factor for outlier ejection (Envoy default)
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
