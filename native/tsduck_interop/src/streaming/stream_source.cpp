// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#include "stream_source.hpp"

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstring>

namespace tsduck_interop::streaming {

// ============================================================================
// Health-Based URL Selection
// ============================================================================

int32_t StreamSource::select_best_url() noexcept {
    const std::lock_guard<std::mutex> lock(url_mutex_);

    if (urls_.empty()) {
        return -1;
    }

    int32_t best_idx = -1;
    double best_score = -1.0;
    auto now = SteadyClock::now();

    // Find highest-scoring non-quarantined URL
    for (int32_t i = 0; i < static_cast<int32_t>(urls_.size()); ++i) {
        const auto& info = urls_[i];

        // Skip quarantined URLs
        if (now < info.quarantined_until) {
            continue;
        }

        if (info.health_score > best_score) {
            best_score = info.health_score;
            best_idx = i;
        }
    }

    if (best_idx >= 0) {
        current_url_index_ = best_idx;
        return best_idx;
    }

    // All URLs are quarantined - pick the one with least recent failure
    best_idx = select_least_recently_failed();
    if (best_idx >= 0) {
        current_url_index_ = best_idx;
    }
    return best_idx;
}

int32_t StreamSource::select_least_recently_failed() const noexcept {
    // Must be called with url_mutex_ held
    if (urls_.empty()) {
        return -1;
    }

    int32_t best_idx = 0;
    auto earliest_failure = urls_[0].last_failure_time;

    for (int32_t i = 1; i < static_cast<int32_t>(urls_.size()); ++i) {
        const auto& info = urls_[i];
        // Prefer URLs that failed earlier (more time to recover)
        // or URLs that never failed (time_point{} is earliest)
        if (info.last_failure_time < earliest_failure) {
            earliest_failure = info.last_failure_time;
            best_idx = i;
        }
    }

    return best_idx;
}

void StreamSource::mark_url_failed() noexcept {
    mark_url_failed(current_url_index_);
}

void StreamSource::mark_url_failed(int32_t url_index) noexcept {
    const std::lock_guard<std::mutex> lock(url_mutex_);

    if (url_index < 0 || url_index >= static_cast<int32_t>(urls_.size())) {
        return;
    }

    auto& info = urls_[url_index];
    auto now = SteadyClock::now();

    // Increment failure count
    info.consecutive_failures++;
    info.total_failures++;
    info.last_failure_time = now;

    // Apply score penalty
    info.health_score = std::max(0.0, info.health_score - config_.score_penalty_on_failure);

    // Apply quarantine with exponential backoff
    int32_t quarantine_ms = calculate_quarantine_ms(info.consecutive_failures);
    info.quarantined_until = now + std::chrono::milliseconds(quarantine_ms);
}

void StreamSource::record_success(int64_t bytes) noexcept {
    const std::lock_guard<std::mutex> lock(url_mutex_);

    if (current_url_index_ < 0 || current_url_index_ >= static_cast<int32_t>(urls_.size())) {
        return;
    }

    auto& info = urls_[current_url_index_];

    // Clear quarantine and failure count on success
    info.quarantined_until = TimePoint{};
    info.consecutive_failures = 0;
    info.total_successes++;
    info.total_bytes_received += bytes;

    // Boost score slightly (capped at 100)
    info.health_score = std::min(100.0, info.health_score + config_.score_boost_on_success);
}

void StreamSource::reset_all_quarantines() noexcept {
    const std::lock_guard<std::mutex> lock(url_mutex_);

    for (auto& info : urls_) {
        info.quarantined_until = TimePoint{};
        info.consecutive_failures = 0;
    }
}

void StreamSource::reset_url_status(int32_t url_index) noexcept {
    const std::lock_guard<std::mutex> lock(url_mutex_);

    if (url_index < 0 || url_index >= static_cast<int32_t>(urls_.size())) {
        return;
    }

    auto& info = urls_[url_index];
    info.quarantined_until = TimePoint{};
    info.consecutive_failures = 0;
    info.last_failure_time = TimePoint{};
}

int32_t StreamSource::calculate_quarantine_ms(int32_t consecutive_failures) const noexcept {
    // Base quarantine with exponential backoff: base * multiplier^(failures-1)
    double multiplier = std::pow(config_.quarantine_backoff_multiplier,
                                  static_cast<double>(consecutive_failures - 1));
    double quarantine = static_cast<double>(config_.quarantine_duration_ms) * multiplier;

    // Cap at maximum
    return static_cast<int32_t>(std::min(quarantine,
                                          static_cast<double>(config_.max_quarantine_duration_ms)));
}

// ============================================================================
// Connection Lifecycle
// ============================================================================

bool StreamSource::connect() noexcept {
    // Clean up any previous transfer
    disconnect();

    std::string url;
    {
        const std::lock_guard<std::mutex> lock(url_mutex_);
        if (urls_.empty()) {
            return false;
        }
        url = urls_[current_url_index_].url;
    }

    // Initialize curl multi handle (once per source lifetime, reused across connects)
    if (curl_multi_ == nullptr) {
        curl_multi_ = curl_multi_init();
        if (curl_multi_ == nullptr) {
            return false;
        }
    }

    // Create easy handle for this transfer
    curl_handle_ = curl_easy_init();
    if (curl_handle_ == nullptr) {
        return false;
    }

    // URL
    curl_easy_setopt(curl_handle_, CURLOPT_URL, url.c_str());

    // Write callback: delivers data to our pipeline
    curl_easy_setopt(curl_handle_, CURLOPT_WRITEFUNCTION, curl_write_callback);
    curl_easy_setopt(curl_handle_, CURLOPT_WRITEDATA, this);

    // Progress callback: allows us to abort from stop request
    curl_easy_setopt(curl_handle_, CURLOPT_XFERINFOFUNCTION, curl_progress_callback);
    curl_easy_setopt(curl_handle_, CURLOPT_XFERINFODATA, this);
    curl_easy_setopt(curl_handle_, CURLOPT_NOPROGRESS, 0L);

    // Timeouts
    curl_easy_setopt(curl_handle_, CURLOPT_CONNECTTIMEOUT_MS,
                     static_cast<long>(config_.connect_timeout_ms));

    // Low-speed stall detection (curl will abort if below limit for time period)
    curl_easy_setopt(curl_handle_, CURLOPT_LOW_SPEED_LIMIT,
                     static_cast<long>(config_.low_speed_limit_bytes));
    curl_easy_setopt(curl_handle_, CURLOPT_LOW_SPEED_TIME,
                     static_cast<long>(config_.low_speed_time_sec));

    // Follow HTTP redirects
    curl_easy_setopt(curl_handle_, CURLOPT_FOLLOWLOCATION, 1L);
    curl_easy_setopt(curl_handle_, CURLOPT_MAXREDIRS, 10L);

    // Thread safety: don't use signals for timeouts
    curl_easy_setopt(curl_handle_, CURLOPT_NOSIGNAL, 1L);

    // Buffer size hint (64KB is good for streaming)
    curl_easy_setopt(curl_handle_, CURLOPT_BUFFERSIZE, 65536L);

    // Accept any content type (IPTV providers may not set correct MIME)
    curl_easy_setopt(curl_handle_, CURLOPT_ACCEPT_ENCODING, "");

    // User-agent
    curl_easy_setopt(curl_handle_, CURLOPT_USERAGENT, "Jellyfin-Xtream/1.0");

    // Add easy handle to multi
    CURLMcode mc = curl_multi_add_handle(curl_multi_, curl_handle_);
    if (mc != CURLM_OK) {
        curl_easy_cleanup(curl_handle_);
        curl_handle_ = nullptr;
        return false;
    }

    connected_ = true;
    stop_requested_.store(false, std::memory_order_release);
    return true;
}

void StreamSource::disconnect() noexcept {
    if (curl_handle_ != nullptr && curl_multi_ != nullptr) {
        curl_multi_remove_handle(curl_multi_, curl_handle_);
        curl_easy_cleanup(curl_handle_);
        curl_handle_ = nullptr;
    }
    connected_ = false;
}

int StreamSource::perform_multi() noexcept {
    if (curl_multi_ == nullptr || curl_handle_ == nullptr) {
        return 0;
    }

    int still_running = 0;
    CURLMcode mc = curl_multi_perform(curl_multi_, &still_running);

    if (mc != CURLM_OK) {
        return 0;
    }

    // If there are still running transfers, wait for activity
    if (still_running > 0) {
        int numfds = 0;
        // Wait up to 100ms for socket activity
        curl_multi_poll(curl_multi_, nullptr, 0, 100, &numfds);
    }

    return still_running;
}

bool StreamSource::check_transfer_done(CURLcode* out_curl_code, long* out_http_status) noexcept {
    if (curl_multi_ == nullptr) {
        return false;
    }

    int msgs_left = 0;
    CURLMsg* msg = curl_multi_info_read(curl_multi_, &msgs_left);

    if (msg != nullptr && msg->msg == CURLMSG_DONE) {
        last_curl_error_ = msg->data.result;
        if (out_curl_code != nullptr) {
            *out_curl_code = msg->data.result;
        }

        long http_code = 0;
        if (curl_handle_ != nullptr) {
            curl_easy_getinfo(curl_handle_, CURLINFO_RESPONSE_CODE, &http_code);
        }
        last_http_status_ = static_cast<int32_t>(http_code);
        if (out_http_status != nullptr) {
            *out_http_status = http_code;
        }

        connected_ = false;
        return true;
    }

    return false;
}

void StreamSource::cleanup() noexcept {
    disconnect();
    if (curl_multi_ != nullptr) {
        curl_multi_cleanup(curl_multi_);
        curl_multi_ = nullptr;
    }
}

// ============================================================================
// Curl Callbacks
// ============================================================================

size_t StreamSource::curl_write_callback(char* ptr, size_t size, size_t nmemb, void* userdata) noexcept {
    auto* self = static_cast<StreamSource*>(userdata);
    const size_t total = size * nmemb;

    if (self->stop_requested_.load(std::memory_order_acquire)) {
        // Return 0 to abort the transfer
        return 0;
    }

    self->bytes_received_ += static_cast<int64_t>(total);

    // Deliver data to pipeline
    if (self->data_callback_) {
        self->data_callback_(reinterpret_cast<const uint8_t*>(ptr), total);
    }

    return total;
}

int StreamSource::curl_progress_callback(void* userdata, curl_off_t /*dltotal*/, curl_off_t /*dlnow*/,
                                        curl_off_t /*ultotal*/, curl_off_t /*ulnow*/) noexcept {
    auto* self = static_cast<StreamSource*>(userdata);

    // Return non-zero to abort the transfer
    if (self->stop_requested_.load(std::memory_order_acquire)) {
        return 1;
    }

    return 0;
}

}  // namespace tsduck_interop::streaming
