// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#include "stream_source.hpp"

#include <atomic>
#include <cstdint>
#include <cstring>

namespace tsduck_interop::streaming {

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
        url = urls_[current_url_index_];
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
