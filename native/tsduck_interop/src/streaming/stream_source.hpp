// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_STREAMING_STREAM_SOURCE_HPP
#define TSDUCK_INTEROP_STREAMING_STREAM_SOURCE_HPP

#include <curl/curl.h>

#include <atomic>
#include <cstdint>
#include <functional>
#include <mutex>
#include <string>
#include <utility>
#include <vector>

#include "streaming_types.hpp"

namespace tsduck_interop::streaming {

// ============================================================================
// StreamSource: libcurl-based HTTP streaming source
// ============================================================================
//
// Manages a single HTTP connection to an IPTV provider using libcurl's
// multi interface for non-blocking I/O. Delivers data via callback.
//
// Thread safety:
//   - URL management methods are mutex-protected (rarely called)
//   - connect/disconnect/perform_multi must be called from the same thread
//   - stop() is thread-safe (sets atomic flag checked by perform_multi)
//   - Data callback is invoked from the perform_multi() caller's thread

using DataCallback = std::function<void(const uint8_t* data, size_t size)>;

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

    void add_url(const std::string& url) {
        const std::lock_guard<std::mutex> lock(url_mutex_);
        urls_.push_back(url);
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
        return urls_[current_url_index_];
    }

    int32_t current_url_index() const noexcept { return current_url_index_; }

    /// Advance to next URL in rotation. Returns false if no URLs.
    bool rotate_url() noexcept {
        const std::lock_guard<std::mutex> lock(url_mutex_);
        if (urls_.empty()) {
            return false;
        }
        current_url_index_ = (current_url_index_ + 1) % static_cast<int32_t>(urls_.size());
        return true;
    }

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
    std::vector<std::string> urls_;
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
};

}  // namespace tsduck_interop::streaming

#endif  // TSDUCK_INTEROP_STREAMING_STREAM_SOURCE_HPP
