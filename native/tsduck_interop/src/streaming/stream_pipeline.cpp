// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#include "stream_pipeline.hpp"

#include "../core/logging.hpp"

#ifdef _WIN32
#include <io.h>
#define WRITE_FD(fd, data, len) _write(fd, data, static_cast<unsigned int>(len))
#else
#include <unistd.h>
#define WRITE_FD(fd, data, len) write(fd, data, static_cast<size_t>(len))
#endif

#include <algorithm>
#include <cstring>
#include <mutex>
#include <vector>

namespace tsduck_interop {
namespace streaming {

namespace {
constexpr const char* kPipeline = "StreamPipeline";

/// Classify a CURLcode into a DisconnectReason for failover decisions.
constexpr DisconnectReason classify_curl_error(CURLcode code) noexcept {
    switch (code) {
        case CURLE_OPERATION_TIMEDOUT:
            return DisconnectReason::Timeout;
        case CURLE_COULDNT_RESOLVE_HOST:
        case CURLE_COULDNT_RESOLVE_PROXY:
            return DisconnectReason::DnsResolutionFailed;  // DNS-specific for retry logic
        case CURLE_COULDNT_CONNECT:
            return DisconnectReason::ConnectionFailed;
        case CURLE_ABORTED_BY_CALLBACK:
            return DisconnectReason::Aborted;
        case CURLE_OK:
            return DisconnectReason::Normal;
        default:
            return DisconnectReason::Unknown;
    }
}

/// Check if a curl error warrants immediate forced ejection with longer duration.
/// These are infrastructure-level failures that indicate the host itself is problematic.
constexpr bool is_severe_infrastructure_error(CURLcode code) noexcept {
    switch (code) {
        case CURLE_COULDNT_RESOLVE_HOST:     // DNS failure
        case CURLE_COULDNT_RESOLVE_PROXY:    // Proxy DNS failure
        case CURLE_SSL_CONNECT_ERROR:        // SSL handshake failed
        case CURLE_PEER_FAILED_VERIFICATION: // SSL cert invalid
            return true;
        default:
            return false;
    }
}

/// Duration for forced ejection of providers with severe errors (5 minutes).
constexpr int64_t SEVERE_ERROR_EJECTION_MS = 300000;
}  // namespace

// curl_global_init() is NOT thread-safe and must only be called once.
// Use call_once to ensure it's initialized exactly once across all StreamPipelines.
// We never call curl_global_cleanup() since this is a library (cleanup at process exit).
static std::once_flag s_curl_init_flag;
static void ensureCurlInitialized() noexcept {
    std::call_once(s_curl_init_flag, []() {
        curl_global_init(CURL_GLOBAL_DEFAULT);
    });
}

// ============================================================================
// Construction / Destruction
// ============================================================================

StreamPipeline::StreamPipeline(const StreamerConfig& config, const TsDuckConfigNative* analyzer_config)
    : config_(config)
    , source_(config)
    , failover_(config)
    , quality_trigger_(config)
    , alignment_(config.alignment_buffer_packets)
{
    LOG_DEBUG(kPipeline, "ctor this=%p", static_cast<const void*>(this));
    init_common(analyzer_config);
    LOG_DEBUG(kPipeline, "ctor done this=%p", static_cast<const void*>(this));
}

void StreamPipeline::init_common(const TsDuckConfigNative* analyzer_config) {
    // Create owned TsDuck context and analyzer
    context_ = std::make_unique<context::TsDuckContext>();

    if (analyzer_config) {
        analyzer_ = std::make_unique<context::TsDuckAnalyzer>(context_.get(), analyzer_config);
    } else {
        TsDuckConfigNative default_cfg{};
        default_cfg.metrics_interval_ms = 1000;
        default_cfg.enable_tr101290 = 1;
        default_cfg.sample_size_bytes = static_cast<int32_t>(ts::PKT_SIZE) * 1000;
        default_cfg.enable_auto_restamp = config_.enable_restamp;
        default_cfg.restamp_mode = config_.restamp_mode;
        default_cfg.smooth_pcr = 1;
        default_cfg.fix_discontinuities = 1;
        default_cfg.reserved = 0;
        default_cfg.correction_threshold_ms = DEFAULT_CORRECTION_THRESHOLD_MS;
        default_cfg.max_correction_rate_ms = DEFAULT_MAX_CORRECTION_RATE_MS;
        default_cfg.hysteresis_threshold_ms = DEFAULT_HYSTERESIS_THRESHOLD_MS;
        default_cfg.stream_bitrate_hint = 0;
        analyzer_ = std::make_unique<context::TsDuckAnalyzer>(context_.get(), &default_cfg);
    }

    // Initialize unified provider health manager (brpc circuit breaker + Finagle EWMA + P2C)
    ProviderHealthConfig health_cfg;
    // Map existing config to health manager config
    health_cfg.circuit_breaker.min_isolation_duration_ms = config_.quarantine_duration_ms;
    health_cfg.circuit_breaker.max_isolation_duration_ms = config_.max_quarantine_duration_ms;
    health_cfg.circuit_breaker.half_open_window_size = 3;  // Test 3 requests before full recovery

    // Apply circuit breaker window sizes from config (allows E2E tests to use smaller windows)
    if (config_.circuit_breaker_short_window_size > 0) {
        health_cfg.circuit_breaker.short_window_size = config_.circuit_breaker_short_window_size;
    }
    if (config_.circuit_breaker_long_window_size > 0) {
        health_cfg.circuit_breaker.long_window_size = config_.circuit_breaker_long_window_size;
    }
    if (config_.circuit_breaker_short_window_error_percent > 0) {
        health_cfg.circuit_breaker.short_window_error_percent = config_.circuit_breaker_short_window_error_percent;
    }
    if (config_.circuit_breaker_long_window_error_percent > 0) {
        health_cfg.circuit_breaker.long_window_error_percent = config_.circuit_breaker_long_window_error_percent;
    }

    // Apply DNS failure settings from config
    if (config_.dns_retry_count > 0) {
        health_cfg.dns_failure_threshold = config_.dns_retry_count;
    }
    if (config_.dns_ejection_duration_ms > 0) {
        health_cfg.dns_ejection_duration_ms = config_.dns_ejection_duration_ms;
    }

    health_manager_ = std::make_unique<UnifiedProviderHealthManager>(health_cfg);
    source_.set_health_manager(health_manager_.get());

    // Wire up the data path: curl -> this -> alignment -> restamp -> output
    source_.set_data_callback([this](const uint8_t* data, size_t size) {
        on_data_received(data, size);
    });
}

bool StreamPipeline::set_shared_memory_output(const std::string& name,
                                              std::size_t slot_count,
                                              std::size_t slot_size) noexcept {
    if (running_.load(std::memory_order_acquire)) {
        LOG_WARNING(kPipeline, "cannot set shared memory while running");
        return false;
    }

    std::error_code ec;
    shm_producer_ = ipc::SharedMemoryProducer::create(name, slot_count, slot_size, &ec);

    if (!shm_producer_) {
        LOG_ERROR(kPipeline, "failed to create shared memory '%s': %s",
                  name.c_str(), ec.message().c_str());
        return false;
    }

    shm_name_ = name;

    // Disable callback mode when using shared memory
    output_callback_ = nullptr;
    output_user_data_ = nullptr;
    config_.output_fd = -1;

    LOG_INFO(kPipeline, "shared memory output configured: %s (slots=%zu, size=%zu)",
             name.c_str(), slot_count, slot_size);
    return true;
}

StreamPipeline::~StreamPipeline() {
    LOG_DEBUG(kPipeline, "dtor this=%p running=%d worker_finished=%d",
              static_cast<const void*>(this), running_.load(), worker_finished_.load());

    // Signal end of stream to shared memory consumer
    if (shm_producer_) {
        shm_producer_->set_end_of_stream();
        shm_producer_->signal_data_available();
    }

    // Ensure worker is stopped. If called from the worker thread itself
    // (deferred destruction), the thread is already at its exit point.
    running_.store(false, std::memory_order_release);
    source_.request_stop();

    if (worker_thread_.joinable()) {
        if (std::this_thread::get_id() == worker_thread_.get_id()) {
            // We're on the worker thread (deferred self-delete) - can't join self
            worker_thread_.detach();
        } else {
            worker_thread_.join();
        }
    }

    LOG_DEBUG(kPipeline, "dtor done this=%p", static_cast<const void*>(this));
}

// ============================================================================
// Control
// ============================================================================

bool StreamPipeline::start() noexcept {
    LOG_DEBUG(kPipeline, "start this=%p running=%d worker_finished=%d",
              static_cast<const void*>(this), running_.load(), worker_finished_.load());

    if (running_.load(std::memory_order_acquire)) {
        LOG_WARNING(kPipeline, "start called while already running");
        return false;  // Already running
    }

    // Wait for any previous worker thread to fully exit (e.g., if stop() detached it)
    if (!worker_finished_.load(std::memory_order_acquire)) {
        LOG_DEBUG(kPipeline, "start waiting for previous worker this=%p", static_cast<const void*>(this));
        while (!worker_finished_.load(std::memory_order_acquire)) {
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }
    }

    // If registry is configured, populate URLs from it
    if (registry_ != nullptr && (channel_guid_high_ != 0 || channel_guid_low_ != 0)) {
        if (!populate_urls_from_registry()) {
            LOG_WARNING(kPipeline, "start: failed to populate URLs from registry");
        }
    }

    if (source_.url_count() == 0) {
        LOG_WARNING(kPipeline, "start called with no URLs configured");
        return false;  // No URLs configured
    }

    LOG_INFO(kPipeline, "starting stream with %d URL(s)", source_.url_count());

    // Reset state
    failover_.reset();
    alignment_.reset();
    keyframe_aligner_.stop_waiting();
    last_output_pts_ = -1;
    packets_output_ = 0;
    bytes_received_total_ = 0;
    first_data_after_switch_ = false;
    session_start_ticks_ = get_dotnet_ticks();
    switch_requested_.store(false, std::memory_order_release);

    // Clear status
    std::memset(&status_, 0, sizeof(status_));
    status_.url_count = source_.url_count();
    status_.session_start_ticks = session_start_ticks_;

    worker_finished_.store(false, std::memory_order_release);
    running_.store(true, std::memory_order_release);

    worker_thread_ = std::thread([this]() { worker_loop(); });
    worker_thread_id_ = worker_thread_.get_id();
    return true;
}

void StreamPipeline::stop() noexcept {
    LOG_DEBUG(kPipeline, "stop this=%p running=%d worker_finished=%d",
              static_cast<const void*>(this), running_.load(), worker_finished_.load());

    if (!running_.load(std::memory_order_acquire)) {
        return;
    }

    // Signal end of stream to shared memory consumer
    if (shm_producer_) {
        shm_producer_->set_end_of_stream();
        shm_producer_->signal_data_available();
    }

    running_.store(false, std::memory_order_release);
    source_.request_stop();

    if (worker_thread_.joinable()) {
        // Guard against self-join: if stop() is called from the worker thread
        // (e.g., via event callback → C# disposal → tsduck_streamer_stop()),
        // joining ourselves would throw system_error(EDEADLK).
        // In that case, detach so the thread can finish naturally.
        if (std::this_thread::get_id() == worker_thread_.get_id()) {
            LOG_DEBUG(kPipeline, "stop self-join detected, detaching this=%p", static_cast<const void*>(this));
            worker_thread_.detach();
            // Don't do post-stop cleanup here - the worker is still running.
            // Cleanup will happen when the worker exits and self-deletes.
            return;
        } else {
            LOG_DEBUG(kPipeline, "stop joining worker this=%p", static_cast<const void*>(this));
            worker_thread_.join();
            LOG_DEBUG(kPipeline, "stop join complete this=%p", static_cast<const void*>(this));
        }
    }

    source_.clear_stop_request();
    failover_.on_stopped();
    update_status();

    LOG_INFO(kPipeline, "stream stopped, received %lld bytes, output %lld packets",
             static_cast<long long>(bytes_received_total_), static_cast<long long>(packets_output_));
}

bool StreamPipeline::get_status(StreamerStatus* out) const noexcept {
    if (!out) return false;

    uint64_t seq;
    do {
        seq = status_seqlock_.begin_read();
        *out = status_;
    } while (!status_seqlock_.read_consistent(seq));

    return true;
}

// ============================================================================
// Worker Thread
// ============================================================================

void StreamPipeline::worker_loop() noexcept {
    LOG_DEBUG(kPipeline, "worker_loop start this=%p", static_cast<const void*>(this));

    ensureCurlInitialized();

    while (running_.load(std::memory_order_acquire)) {
        // Check for switch request
        if (switch_requested_.load(std::memory_order_acquire)) {
            switch_requested_.store(false, std::memory_order_release);
            perform_switch();
        }

        // Attempt a streaming session
        stream_session();

        // If we're still supposed to be running, handle reconnection
        if (!running_.load(std::memory_order_acquire)) {
            break;
        }

        // Check if failover manager says we should continue
        if (failover_.is_terminal()) {
            emit_event(StreamEvent::Stopped, 0);
            break;
        }

        // Check if we should switch URL (after stall)
        if (failover_.state() == StreamerState::Stalled) {
            bool should_switch = failover_.on_stall();
            if (should_switch && source_.url_count() > 1) {
                perform_switch();
                continue;
            }
        }

        // Handle disconnection: retry same URL, switch to next, or fail
        // Pass the disconnect reason for immediate-switch-on-timeout logic
        auto action = failover_.on_disconnected(last_disconnect_reason_);
        last_disconnect_reason_ = DisconnectReason::Unknown;  // Reset for next session
        if (action == FailoverManager::DisconnectAction::Failed) {
            // Max retries exhausted
            LOG_ERROR(kPipeline, "max retries exhausted (%d attempts), giving up", failover_.retry_count());
            emit_event(StreamEvent::Stopped, static_cast<int32_t>(failover_.retry_count()));
            break;
        }

        if (action == FailoverManager::DisconnectAction::ShouldSwitch && source_.url_count() > 1) {
            // Consecutive failures exceeded threshold - switch to next URL
            LOG_WARNING(kPipeline, "consecutive failures on URL index %d, switching", source_.current_url_index());
            perform_switch();
            continue;
        }

        // ShouldRetry: reconnect to same URL with backoff
        int32_t backoff_ms = failover_.calculate_backoff_ms();
        LOG_INFO(kPipeline, "reconnecting in %d ms (attempt %d)", backoff_ms, failover_.retry_count());
        emit_event(StreamEvent::Reconnecting, backoff_ms);
        update_status();

        if (!interruptible_sleep(backoff_ms)) {
            break;  // Stop requested during backoff
        }
    }

    LOG_DEBUG(kPipeline, "worker_loop exit this=%p destroy_requested=%d",
              static_cast<const void*>(this), destroy_requested_.load());

    // Signal that the worker has fully exited.
    worker_finished_.store(true, std::memory_order_release);

    // If destruction was requested while we were inside a callback,
    // the caller couldn't delete us (we were still on the stack).
    // Now that we're about to return, it's safe to self-delete.
    if (destroy_requested_.load(std::memory_order_acquire)) {
        LOG_DEBUG(kPipeline, "worker_loop self-delete this=%p", static_cast<const void*>(this));
        delete this;
    }
}

void StreamPipeline::stream_session() noexcept {
    failover_.on_connecting();
    update_status();

    // Register request start with health manager BEFORE connecting
    // This ensures failed connection attempts are tracked for success rate calculation
    int32_t provider_idx = get_effective_provider_index();
    if (health_manager_ && provider_idx >= 0) {
        health_manager_->on_request_start(provider_idx);
    }

    if (!source_.connect()) {
        LOG_WARNING(kPipeline, "connection failed to URL index %d", source_.current_url_index());
        emit_event(StreamEvent::Error, -1);

        // Record failure for failed connection attempts
        if (health_manager_ && provider_idx >= 0) {
            health_manager_->on_failure(provider_idx);
            health_manager_->on_request_end(provider_idx);
        }
        return;
    }

    // Poll loop: drive curl multi and check for completion
    while (running_.load(std::memory_order_acquire)) {
        // Check for switch request mid-stream
        if (switch_requested_.load(std::memory_order_acquire)) {
            switch_requested_.store(false, std::memory_order_release);
            source_.disconnect();
            perform_switch();
            return;
        }

        int still_running = source_.perform_multi();

        if (still_running == 0) {
            // Transfer finished - check why
            CURLcode curl_code;
            long http_status;
            if (source_.check_transfer_done(&curl_code, &http_status)) {
                // Capture disconnect reason for failover decision
                last_disconnect_reason_ = classify_curl_error(curl_code);

                // Override for HTTP errors (curl succeeded but server returned error)
                if (curl_code == CURLE_OK && (http_status < 200 || http_status >= 400)) {
                    last_disconnect_reason_ = DisconnectReason::HttpError;
                }

                if (curl_code == CURLE_OK && http_status >= 200 && http_status < 300) {
                    // Stream ended normally (server closed connection)
                    LOG_INFO(kPipeline, "stream ended normally, HTTP %ld", http_status);
                    last_disconnect_reason_ = DisconnectReason::Normal;
                    emit_event(StreamEvent::Disconnected, static_cast<int32_t>(http_status));
                } else if (curl_code == CURLE_ABORTED_BY_CALLBACK) {
                    // We aborted (stop requested)
                    last_disconnect_reason_ = DisconnectReason::Aborted;
                    break;
                } else {
                    // Error - log the disconnect reason for debugging
                    LOG_WARNING(kPipeline, "stream error: curl_code=%d, http_status=%ld, reason=%d",
                                static_cast<int>(curl_code), http_status,
                                static_cast<int>(last_disconnect_reason_));
                    emit_event(StreamEvent::Error,
                              curl_code != CURLE_OK ? static_cast<int32_t>(curl_code)
                                                   : static_cast<int32_t>(http_status));

                    // Record failure in health manager (replaces old blacklist system)
                    int32_t provider_idx = get_effective_provider_index();
                    if (health_manager_ && provider_idx >= 0) {
                        health_manager_->on_failure(provider_idx);
                        health_manager_->on_request_end(provider_idx);

                        // Handle severe infrastructure errors
                        if (is_severe_infrastructure_error(curl_code)) {
                            // DNS failures: use health manager's three-tier policy
                            // - Transient (1-2 failures): Switch URLs, don't eject
                            // - Persistent (>=3 failures): Force eject for 5 minutes
                            if (last_disconnect_reason_ == DisconnectReason::DnsResolutionFailed) {
                                auto policy = health_manager_->on_dns_failure(provider_idx);
                                int32_t dns_failures = health_manager_->dns_failure_count(provider_idx);

                                if (policy == DnsFailurePolicy::EjectAndSwitch) {
                                    LOG_WARNING(kPipeline, "provider %d force ejected after DNS failures (curl=%d)",
                                                provider_idx, static_cast<int>(curl_code));
                                } else {
                                    // Under threshold - log and let failover try next URL
                                    LOG_INFO(kPipeline, "DNS failure %d/%d for provider %d, switching to next URL",
                                             dns_failures, config_.dns_retry_count, provider_idx);
                                }
                            } else {
                                // Non-DNS infrastructure errors (SSL, etc.) - force eject immediately
                                health_manager_->force_eject(provider_idx, SEVERE_ERROR_EJECTION_MS);
                                LOG_WARNING(kPipeline, "provider %d force ejected due to infrastructure error (curl=%d)",
                                            provider_idx, static_cast<int>(curl_code));
                            }
                        }
                    }
                }
            }
            source_.disconnect();
            return;
        }

        // Check for stall while streaming
        if (failover_.state() == StreamerState::Streaming && failover_.is_stalled()) {
            LOG_WARNING(kPipeline, "stream stalled, no data for %d ms", failover_.ms_since_last_data());
            emit_event(StreamEvent::Stalled, static_cast<int32_t>(failover_.ms_since_last_data()));
            source_.disconnect();
            return;
        }

        // Check for quality degradation (TR 101 290 error rate thresholds)
        if (failover_.state() == StreamerState::Streaming &&
            quality_trigger_.is_enabled() &&
            source_.url_count() > 1) {
            Tr101290Priority1Native p1{};
            Tr101290Priority2Native p2{};
            analyzer_->tr101290.get_counters(&p1, &p2);

            if (quality_trigger_.should_switch(p1, p2)) {
                LOG_WARNING(kPipeline, "quality degraded on URL index %d, switching (cc_errors=%lld, sync_loss=%lld)",
                            source_.current_url_index(),
                            static_cast<long long>(p1.continuity_count_error),
                            static_cast<long long>(p1.sync_loss));
                emit_event(StreamEvent::QualityDegraded, source_.current_url_index());
                source_.disconnect();
                perform_switch();
                return;
            }
        }

        update_status();
    }

    // End request tracking in health manager
    {
        int32_t provider_idx = get_effective_provider_index();
        if (health_manager_ && provider_idx >= 0) {
            health_manager_->on_request_end(provider_idx);
        }
    }

    source_.disconnect();
}

// ============================================================================
// Data Path
// ============================================================================

void StreamPipeline::on_data_received(const uint8_t* data, size_t size) noexcept {
    // Update failover heartbeat
    failover_.on_data_received();
    bytes_received_total_ += static_cast<int64_t>(size);

    // Record success on current URL (boosts health score, clears quarantine)
    source_.record_success(static_cast<int64_t>(size));

    // Also record to registry if available (centralized health tracking)
    record_provider_success(static_cast<int64_t>(size));

    // If this is first data after connect, transition to Streaming
    // and record success in health manager (once per connection, not per chunk)
    if (failover_.state() == StreamerState::Connecting) {
        // Check HTTP status before recording success - don't count HTTP errors as successful
        // This prevents HTTP 503 error responses from being counted as successful connections
        int32_t http_status = source_.get_current_http_status();
        bool is_http_success = (http_status >= 200 && http_status < 300) || http_status == 0;

        // Reset DNS failure count on successful connection (via health manager)
        int32_t provider_idx = get_effective_provider_index();
        if (health_manager_ && provider_idx >= 0) {
            health_manager_->on_dns_success(provider_idx);
        }

        failover_.on_connected();
        quality_trigger_.reset();  // Fresh quality baseline on new connection
        LOG_INFO(kPipeline, "connected to URL index %d (score: %.1f), streaming started",
                 source_.current_url_index(), source_.get_url_score(source_.current_url_index()));
        emit_event(StreamEvent::Connected, source_.current_url_index());

        // Record success in unified health manager (once per successful connection)
        // Note: on_request_start() was already called at session start
        // Only record success for HTTP 2xx responses (not for error response bodies)
        if (health_manager_ && provider_idx >= 0 && is_http_success) {
            // Record success with initial latency (time to first byte)
            double latency_ms = static_cast<double>(failover_.ms_since_last_data());
            if (latency_ms < 1.0) latency_ms = 1.0;  // Minimum 1ms
            health_manager_->on_success(provider_idx, latency_ms);
        }

        if (first_data_after_switch_) {
            emit_event(StreamEvent::DataReceived, source_.current_url_index());
        }
    }

    // Align to TS packet boundaries
    auto chunk = alignment_.append(data, size);
    if (chunk.length > 0) {
        process_aligned(chunk.data, chunk.length);
    }
}

void StreamPipeline::process_aligned(uint8_t* data, int32_t length) noexcept {
    // If waiting for keyframe after switch, buffer until IDR found
    if (keyframe_aligner_.is_waiting()) {
        auto result = keyframe_aligner_.process(data, length);
        if (result.length == 0) {
            // Still buffering - don't output anything yet
            return;
        }

        // Got data to output (either found IDR or exceeded buffer limit)
        if (result.found_keyframe) {
            LOG_INFO(kPipeline, "keyframe alignment complete, outputting %d bytes",
                     result.length);
        }

        // Process the aligned data (may be from buffer, not original input)
        // Note: result.data points to keyframe aligner's internal buffer
        // We need to copy to a mutable buffer for feed_and_restamp
        std::vector<uint8_t> aligned_data(result.data, result.data + result.length);
        keyframe_aligner_.clear_buffer();  // Safe to clear now

        // Recursively process the keyframe-aligned data
        process_aligned(aligned_data.data(), static_cast<int32_t>(aligned_data.size()));
        return;
    }

    int32_t packets = length / static_cast<int32_t>(ts::PKT_SIZE);

    // Handle first data after a switch: extract first PTS and notify restamper
    if (first_data_after_switch_ && last_output_pts_ >= 0) {
        int64_t first_pts = -1;

        // Scan for first video PTS in this chunk
        const ts::TSPacket* pkt_array = reinterpret_cast<const ts::TSPacket*>(data);
        for (int32_t i = 0; i < packets && first_pts < 0; i++) {
            ts::TSPacket& pkt = const_cast<ts::TSPacket&>(pkt_array[i]);
            if (!pkt.hasValidSync()) continue;
            if (!pkt.startPES()) continue;

            const uint8_t* payload = pkt.getPayload();
            size_t payload_size = pkt.getPayloadSize();
            if (payload_size < 4) continue;

            uint8_t stream_id = payload[3];
            if (ts::IsVideoSID(stream_id) && pkt.hasPTS()) {
                uint64_t pts_val = pkt.getPTS();
                if (pts_val != ts::INVALID_PTS) {
                    first_pts = static_cast<int64_t>(pts_val);
                }
            }
        }

        if (first_pts >= 0 && analyzer_) {
            analyzer_->handle_switch(last_output_pts_, first_pts);
            emit_event(StreamEvent::Switched, source_.current_url_index());
        }

        first_data_after_switch_ = false;
    }

    // Feed through analyzer (restamps in-place if enabled)
    if (analyzer_) {
        (void)analyzer_->feed_and_restamp(data, length);
    }

    // Track last output PTS for switch continuity
    int64_t pts = extract_last_video_pts(data, length);
    if (pts >= 0) {
        last_output_pts_ = pts;
    }

    packets_output_ += packets;

    // Write to output
    write_output(data, length);
}

void StreamPipeline::write_output(const uint8_t* data, int32_t length) noexcept {
    // Priority: shared memory > file descriptor > callback
    if (shm_producer_) {
        // Shared memory mode: write to ring buffer
        auto result = shm_producer_->write(
            std::span<const std::byte>(
                reinterpret_cast<const std::byte*>(data),
                static_cast<std::size_t>(length)
            )
        );

        if (result.overflow) {
            LOG_WARNING(kPipeline, "shared memory overflow: %zu bytes written of %d",
                        result.bytes_written, length);
        }

        // Batch signaling: signal every ~64KB or on overflow to balance latency vs syscall overhead.
        // At 10 Mbps, 64KB = ~50ms of data, which is acceptable latency for streaming.
        // Signal immediately on overflow to wake consumer for recovery.
        constexpr std::size_t kSignalThreshold = 65536;
        shm_bytes_since_signal_ += result.bytes_written;
        if (result.overflow || shm_bytes_since_signal_ >= kSignalThreshold) {
            shm_producer_->signal_data_available();
            shm_bytes_since_signal_ = 0;
        }
    } else if (config_.output_fd >= 0) {
        // Pipe mode: write to file descriptor
        int32_t written = 0;
        while (written < length) {
            auto result = WRITE_FD(config_.output_fd, data + written, length - written);
            if (result <= 0) {
                // Pipe broken or error - stop streaming
                LOG_ERROR(kPipeline, "output pipe broken or write error, stopping stream");
                emit_event(StreamEvent::Error, -2);
                running_.store(false, std::memory_order_release);
                return;
            }
            written += static_cast<int32_t>(result);
        }
    } else if (output_callback_) {
        // Callback mode: deliver to C# layer
        output_callback_(data, length, output_user_data_);
    }
}

// ============================================================================
// Mid-Stream Switch
// ============================================================================

void StreamPipeline::perform_switch() noexcept {
    // Disconnect current source
    source_.disconnect();

    int32_t old_index = source_.current_url_index();

    // Record failure to registry (centralized health tracking)
    record_provider_failure(last_disconnect_reason_);

    // Record failure in unified health manager
    int32_t provider_idx = get_effective_provider_index();
    if (health_manager_ && provider_idx >= 0) {
        health_manager_->on_request_end(provider_idx);
        health_manager_->on_failure(provider_idx);
    }

    // Mark the old URL as failed (applies quarantine and score penalty)
    source_.mark_url_failed(old_index);

    // Select the best available URL based on health scores
    int32_t new_index = source_.select_best_url();
    if (new_index < 0) {
        LOG_WARNING(kPipeline, "no URLs available to switch to");
        return;  // No URLs to rotate to
    }

    LOG_INFO(kPipeline, "switching URL: %d -> %d (score: %.1f, total URLs: %d)",
             old_index, new_index, source_.get_url_score(new_index), source_.url_count());

    failover_.on_switching();

    // Reset quality trigger (fresh baseline on new source)
    quality_trigger_.reset();

    // Reset alignment buffer (discard partial packets from old stream)
    alignment_.reset();

    // Signal discontinuity to shared memory consumer
    if (shm_producer_) {
        shm_producer_->set_discontinuity();
        shm_producer_->signal_data_available();
        shm_bytes_since_signal_ = 0;  // Reset batching counter after explicit signal
    }

    // Start keyframe aligner - buffer data until IDR frame is found
    // This prevents decoder corruption from starting mid-GOP
    keyframe_aligner_.start_waiting();

    // Mark that next data chunk is from a new source
    first_data_after_switch_ = true;

    update_status();
}

// ============================================================================
// Status
// ============================================================================

void StreamPipeline::update_status() noexcept {
    auto seq = status_seqlock_.begin_write();

    status_.state = static_cast<int32_t>(failover_.state());
    status_.current_url_index = source_.current_url_index();
    status_.url_count = source_.url_count();
    status_.retry_count = failover_.retry_count();
    status_.bytes_received = bytes_received_total_;
    status_.packets_output = packets_output_;
    status_.switches_completed = failover_.total_switches();
    status_.reconnections = failover_.total_reconnections();
    status_.last_data_time_ticks = get_dotnet_ticks();
    status_.session_start_ticks = session_start_ticks_;
    status_.last_http_status = source_.last_http_status();
    status_.last_curl_error = static_cast<int32_t>(source_.last_curl_error());
    status_.quality_switches = quality_trigger_.total_quality_switches();

    status_seqlock_.end_write(seq);
}

void StreamPipeline::emit_event(StreamEvent event, int32_t detail) noexcept {
    if (event_callback_) {
        event_callback_(static_cast<int32_t>(event), detail, event_user_data_);
    }
}

// ============================================================================
// Helpers
// ============================================================================

int64_t StreamPipeline::extract_last_video_pts(const uint8_t* data, int32_t length) noexcept {
    int32_t packets = length / static_cast<int32_t>(ts::PKT_SIZE);
    int64_t last_pts = -1;

    // Scan backwards for efficiency (we want the LAST PTS)
    const ts::TSPacket* pkt_array = reinterpret_cast<const ts::TSPacket*>(data);
    for (int32_t i = packets - 1; i >= 0; i--) {
        ts::TSPacket& pkt = const_cast<ts::TSPacket&>(pkt_array[i]);
        if (!pkt.hasValidSync()) continue;
        if (!pkt.startPES()) continue;

        const uint8_t* payload = pkt.getPayload();
        size_t payload_size = pkt.getPayloadSize();
        if (payload_size < 4) continue;

        uint8_t stream_id = payload[3];
        if (ts::IsVideoSID(stream_id) && pkt.hasPTS()) {
            uint64_t pts_val = pkt.getPTS();
            if (pts_val != ts::INVALID_PTS) {
                last_pts = static_cast<int64_t>(pts_val);
                break;
            }
        }
    }

    return last_pts;
}

int64_t StreamPipeline::get_dotnet_ticks() noexcept {
    auto sys_now = std::chrono::system_clock::now();
    auto duration = sys_now.time_since_epoch();
    auto ticks = std::chrono::duration_cast<std::chrono::nanoseconds>(duration).count() / 100;
    return ticks + 621355968000000000LL;  // .NET epoch offset
}

bool StreamPipeline::interruptible_sleep(int32_t ms) noexcept {
    constexpr int32_t check_interval_ms = 50;
    int32_t remaining = ms;

    while (remaining > 0 && running_.load(std::memory_order_acquire)) {
        int32_t sleep_ms = std::min(remaining, check_interval_ms);
        std::this_thread::sleep_for(std::chrono::milliseconds(sleep_ms));
        remaining -= sleep_ms;
    }

    return running_.load(std::memory_order_acquire);
}

// ============================================================================
// Registry Integration
// ============================================================================

bool StreamPipeline::populate_urls_from_registry() noexcept {
    if (registry_ == nullptr || !registry_->is_built()) {
        return false;
    }

    // Get all URLs for this channel GUID
    constexpr int32_t kMaxUrls = 16;
    char url_buffer[kMaxUrls * 1024];
    int32_t providers[kMaxUrls];
    int32_t stream_ids[kMaxUrls];

    int32_t url_count = registry_->get_all_urls(
        channel_guid_high_, channel_guid_low_,
        url_buffer, providers, stream_ids, kMaxUrls);

    if (url_count <= 0) {
        LOG_WARNING(kPipeline, "no URLs found for GUID %llx:%llx",
                    static_cast<long long>(channel_guid_high_),
                    static_cast<long long>(channel_guid_low_));
        return false;
    }

    // Clear existing URLs
    source_.clear_urls();

    // Add URLs from registry with their health scores
    for (int32_t i = 0; i < url_count; i++) {
        const char* url = url_buffer + (i * 1024);
        double health = registry_->get_provider_health(providers[i]);
        source_.add_url_with_score(std::string(url), health);
    }

    // Store the first provider index for health tracking
    if (url_count > 0) {
        current_provider_index_ = providers[0];
    }

    LOG_INFO(kPipeline, "populated %d URLs from registry for GUID %llx:%llx",
             url_count,
             static_cast<long long>(channel_guid_high_),
             static_cast<long long>(channel_guid_low_));

    return true;
}

void StreamPipeline::record_provider_success(int64_t bytes) noexcept {
    if (registry_ != nullptr && current_provider_index_ >= 0) {
        registry_->record_success(current_provider_index_, bytes);
    }
}

void StreamPipeline::record_provider_failure(DisconnectReason reason) noexcept {
    if (registry_ != nullptr && current_provider_index_ >= 0) {
        // Cast streaming::DisconnectReason to registry::DisconnectReason (same values)
        registry_->record_failure(current_provider_index_,
            static_cast<registry::DisconnectReason>(reason));
    }
}

}  // namespace streaming
}  // namespace tsduck_interop
