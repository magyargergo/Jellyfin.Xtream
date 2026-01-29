// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_STREAMING_STREAM_SOURCE_HPP
#define TSDUCK_INTEROP_STREAMING_STREAM_SOURCE_HPP

#include <curl/curl.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <functional>
#include <mutex>
#include <string>
#include <utility>
#include <vector>

#include "streaming_types.hpp"

namespace tsduck_interop::streaming {

// ============================================================================
// StreamSource: libcurl-based HTTP streaming source with health-based selection
// ============================================================================
//
// Manages a single HTTP connection to an IPTV provider using libcurl's
// multi interface for non-blocking I/O. Delivers data via callback.
//
// Health-based URL selection:
//   - Each URL has a health score (0.0 - 100.0, higher = better)
//   - Failed URLs are quarantined temporarily with exponential backoff
//   - select_best_url() picks the highest-scoring non-quarantined URL
//   - Scores can be set from C# (from provider reliability data)
//
// Thread safety:
//   - URL management methods are mutex-protected (rarely called)
//   - connect/disconnect/perform_multi must be called from the same thread
//   - stop() is thread-safe (sets atomic flag checked by perform_multi)
//   - Data callback is invoked from the perform_multi() caller's thread

using DataCallback = std::function<void(const uint8_t* data, size_t size)>;
using SteadyClock = std::chrono::steady_clock;
using TimePoint = SteadyClock::time_point;

// ============================================================================
// UrlInfo: Health metadata for a streaming URL
// ============================================================================

struct UrlInfo {
    std::string url;
    double health_score = 50.0;         // 0.0 - 100.0, higher = better

    // Quarantine state
    TimePoint quarantined_until{};      // Time when quarantine expires
    int32_t consecutive_failures = 0;   // Failures since last success
    TimePoint last_failure_time{};      // When last failure occurred

    // Success tracking
    int64_t total_bytes_received = 0;
    int64_t total_successes = 0;
    int64_t total_failures = 0;

    /// Check if URL is currently quarantined.
    [[nodiscard]] bool is_quarantined() const noexcept {
        return SteadyClock::now() < quarantined_until;
    }

    /// Get remaining quarantine time in milliseconds (0 if not quarantined).
    [[nodiscard]] int32_t quarantine_remaining_ms() const noexcept {
        auto now = SteadyClock::now();
        if (now >= quarantined_until) {
            return 0;
        }
        return static_cast<int32_t>(
            std::chrono::duration_cast<std::chrono::milliseconds>(quarantined_until - now).count());
    }
};

class StreamSource {
public:
    explicit StreamSource(const StreamerConfig& config) noexcept
        : config_(config) {}

    ~StreamSource() {
        cleanup();
    }

    // Non-copyable, non-movable (owns curl handles)
    StreamSource(const StreamSource&) = delete;
    StreamSource& operator=(const StreamSource&) = delete;
    StreamSource(StreamSource&&) = delete;
    StreamSource& operator=(StreamSource&&) = delete;

    // ========================================================================
    // URL Management (thread-safe via mutex)
    // ========================================================================

    /// Add a URL with default health score.
    void add_url(const std::string& url) {
        const std::lock_guard<std::mutex> lock(url_mutex_);
        UrlInfo info;
        info.url = url;
        info.health_score = config_.default_health_score;
        urls_.push_back(std::move(info));
    }

    /// Add a URL with specified health score.
    void add_url_with_score(const std::string& url, double health_score) {
        const std::lock_guard<std::mutex> lock(url_mutex_);
        UrlInfo info;
        info.url = url;
        info.health_score = std::clamp(health_score, 0.0, 100.0);
        urls_.push_back(std::move(info));
    }

    /// Update health score for existing URL by index.
    /// @return true if index was valid.
    bool update_url_score(int32_t url_index, double new_score) {
        const std::lock_guard<std::mutex> lock(url_mutex_);
        if (url_index < 0 || url_index >= static_cast<int32_t>(urls_.size())) {
            return false;
        }
        urls_[url_index].health_score = std::clamp(new_score, 0.0, 100.0);
        return true;
    }

    /// Get health score for URL by index.
    /// @return Score or -1.0 if index invalid.
    double get_url_score(int32_t url_index) const {
        const std::lock_guard<std::mutex> lock(url_mutex_);
        if (url_index < 0 || url_index >= static_cast<int32_t>(urls_.size())) {
            return -1.0;
        }
        return urls_[url_index].health_score;
    }

    void clear_urls() {
        const std::lock_guard<std::mutex> lock(url_mutex_);
        urls_.clear();
        current_url_index_ = 0;
    }

    int32_t url_count() const {
        const std::lock_guard<std::mutex> lock(url_mutex_);
        return static_cast<int32_t>(urls_.size());
    }

    std::string current_url() const {
        const std::lock_guard<std::mutex> lock(url_mutex_);
        if (urls_.empty()) {
            return "";
        }
        return urls_[current_url_index_].url;
    }

    int32_t current_url_index() const noexcept { return current_url_index_; }

    /// Legacy method: Advance to next URL in rotation (simple round-robin).
    /// @deprecated Use select_best_url() for health-aware selection.
    bool rotate_url() noexcept {
        const std::lock_guard<std::mutex> lock(url_mutex_);
        if (urls_.empty()) {
            return false;
        }
        current_url_index_ = (current_url_index_ + 1) % static_cast<int32_t>(urls_.size());
        return true;
    }

    // ========================================================================
    // Health-Based URL Selection (thread-safe via mutex)
    // ========================================================================

    /// Select the best URL based on health score and quarantine status.
    /// Returns the index of the selected URL, or -1 if no URLs available.
    /// This method updates current_url_index_.
    int32_t select_best_url() noexcept;

    /// Mark the current URL as failed, applying quarantine and score penalty.
    void mark_url_failed() noexcept;

    /// Mark a specific URL as failed by index.
    void mark_url_failed(int32_t url_index) noexcept;

    /// Record successful data reception on current URL.
    /// @param bytes Number of bytes received.
    void record_success(int64_t bytes) noexcept;

    /// Reset quarantine status for all URLs.
    void reset_all_quarantines() noexcept;

    /// Reset failure counters and quarantine for a specific URL.
    void reset_url_status(int32_t url_index) noexcept;

    // ========================================================================
    // Callback
    // ========================================================================

    void set_data_callback(DataCallback callback) { data_callback_ = std::move(callback); }

    // ========================================================================
    // Connection Lifecycle (call from worker thread only)
    // ========================================================================

    /// Initialize and connect to the current URL.
    /// @return true if connection was initiated successfully.
    bool connect() noexcept;

    /// Disconnect and clean up the current transfer.
    void disconnect() noexcept;

    /// Perform non-blocking I/O work.
    /// Must be called in a loop from the worker thread.
    /// @return Number of still-active transfers (0 = transfer done/failed).
    int perform_multi() noexcept;

    /// Check if the last transfer completed successfully or with error.
    /// Call after perform_multi() returns 0.
    /// @param out_curl_code Receives the CURLcode result.
    /// @param out_http_status Receives the HTTP status code.
    /// @return true if transfer finished (check codes for success/failure).
    bool check_transfer_done(CURLcode* out_curl_code, long* out_http_status) noexcept;

    // ========================================================================
    // Control
    // ========================================================================

    /// Request stop (thread-safe, checked by perform_multi loop).
    void request_stop() noexcept {
        stop_requested_.store(true, std::memory_order_release);
    }

    bool is_stop_requested() const noexcept {
        return stop_requested_.load(std::memory_order_acquire);
    }

    void clear_stop_request() noexcept {
        stop_requested_.store(false, std::memory_order_release);
    }

    // ========================================================================
    // Status
    // ========================================================================

    int64_t bytes_received() const noexcept { return bytes_received_; }
    int32_t last_http_status() const noexcept { return last_http_status_; }
    CURLcode last_curl_error() const noexcept { return last_curl_error_; }
    bool is_connected() const noexcept { return connected_; }

private:
    StreamerConfig config_;
    CURL* curl_handle_{nullptr};
    CURLM* curl_multi_{nullptr};

    mutable std::mutex url_mutex_;
    std::vector<UrlInfo> urls_;
    int32_t current_url_index_{0};

    int64_t bytes_received_{0};
    int32_t last_http_status_{0};
    CURLcode last_curl_error_{CURLE_OK};
    std::atomic<bool> stop_requested_{false};
    bool connected_{false};

    DataCallback data_callback_;

    /// Clean up all curl resources.
    void cleanup() noexcept;

    /// Static curl write callback.
    static size_t curl_write_callback(char* ptr, size_t size, size_t nmemb, void* userdata) noexcept;

    /// Static curl progress callback (for stop detection).
    static int curl_progress_callback(void* userdata, curl_off_t dltotal, curl_off_t dlnow,
                                    curl_off_t ultotal, curl_off_t ulnow) noexcept;

    /// Select URL with least recent failure (fallback when all quarantined).
    /// Must be called with url_mutex_ held.
    int32_t select_least_recently_failed() const noexcept;

    /// Calculate quarantine duration with exponential backoff.
    /// @param consecutive_failures Number of consecutive failures.
    int32_t calculate_quarantine_ms(int32_t consecutive_failures) const noexcept;
};

}  // namespace tsduck_interop::streaming

#endif  // TSDUCK_INTEROP_STREAMING_STREAM_SOURCE_HPP
