// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_STREAMING_STREAM_PIPELINE_HPP
#define TSDUCK_INTEROP_STREAMING_STREAM_PIPELINE_HPP

#include <thread>
#include <atomic>
#include <memory>
#include <functional>
#include <chrono>
#include <string>

#include "stream_source.hpp"
#include "failover_manager.hpp"
#include "alignment_buffer.hpp"
#include "keyframe_aligner.hpp"
#include "streaming_types.hpp"
#include "quality_switch_trigger.hpp"
#include "shared_buffer.hpp"
#include "../context/analyzer.hpp"
#include "../context/context.hpp"
#include "../concurrency/seqlock.hpp"

// Conditional metrics database support
#ifndef TSDUCK_HAS_SQLITE
#define TSDUCK_HAS_SQLITE 0
#endif

#if TSDUCK_HAS_SQLITE
#include "../metrics/metrics_database.hpp"
#endif

namespace tsduck_interop::streaming {

// ============================================================================
// StreamPipeline: Orchestrates source → analyzer → output
// ============================================================================
//
// Owns and coordinates:
//   - StreamSource (libcurl HTTP fetching)
//   - FailoverManager (state machine for reconnection/switching)
//   - AlignmentBuffer (TS packet alignment)
//   - TsDuckAnalyzer (restamping + analysis)
//
// Thread model:
//   - Worker thread: runs curl multi loop, all data processing is synchronous
//   - Caller thread: start/stop/request_switch/get_status (lock-free)
//
// Data flow (all in worker thread):
//   curl write_callback → AlignmentBuffer → feedAndRestamp() → write_output()

class StreamPipeline {
public:
    /// Standard constructor (callback mode, no database).
    StreamPipeline(const StreamerConfig& config, const TsDuckConfigNative* analyzer_config);

    /// V2 constructor with metrics database and optional shared buffer.
    /// @param config Streamer configuration.
    /// @param analyzer_config Analyzer configuration (nullable).
    /// @param metrics_db_path Path to SQLite database (empty = disabled).
    /// @param shared_buffer_name Shared memory name (empty = callback mode).
    /// @param shared_buffer_size Ring buffer size (ignored if name empty).
    StreamPipeline(const StreamerConfig& config,
                   const TsDuckConfigNative* analyzer_config,
                   const std::string& metrics_db_path,
                   const std::string& shared_buffer_name,
                   size_t shared_buffer_size);

    ~StreamPipeline();

    // Non-copyable, non-movable
    StreamPipeline(const StreamPipeline&) = delete;
    StreamPipeline& operator=(const StreamPipeline&) = delete;

    // ========================================================================
    // Configuration (call before start)
    // ========================================================================

    /// Add a streaming URL to the source list with default health score.
    void add_url(const std::string& url) { source_.add_url(url); }

    /// Add a streaming URL with specified health score.
    /// @param url The URL to add.
    /// @param health_score Health score (0.0-100.0), higher = better.
    void add_url_with_score(const std::string& url, double health_score) {
        source_.add_url_with_score(url, health_score);
    }

    /// Update health score for existing URL by index.
    /// @param url_index Index of URL to update.
    /// @param new_score New health score (0.0-100.0).
    /// @return true if index was valid.
    bool update_url_score(int32_t url_index, double new_score) {
        return source_.update_url_score(url_index, new_score);
    }

    /// Get health score for URL by index.
    double get_url_score(int32_t url_index) const {
        return source_.get_url_score(url_index);
    }

    /// Clear all URLs.
    void clear_urls() { source_.clear_urls(); }

    /// Set output file descriptor (pipe for FFmpeg). -1 = callback mode.
    void set_output_fd(int32_t fd) noexcept { config_.output_fd = fd; }

    /// Set data output callback (alternative to fd mode).
    void set_output_callback(StreamerOutputCallback cb, void* user_data) noexcept {
        output_callback_ = cb;
        output_user_data_ = user_data;
    }

    /// Set event callback for state change notifications.
    void set_event_callback(StreamerEventCallback cb, void* user_data) noexcept {
        event_callback_ = cb;
        event_user_data_ = user_data;
    }

    // ========================================================================
    // Control (thread-safe)
    // ========================================================================

    /// Start streaming. Spawns worker thread.
    /// At least one URL must be configured before calling.
    /// @return true if started, false if already running or no URLs.
    bool start() noexcept;

    /// Stop streaming. Blocks until worker thread exits.
    void stop() noexcept;

    /// Request a switch to the next URL in rotation (async).
    void request_switch() noexcept { switch_requested_.store(true, std::memory_order_release); }

    // ========================================================================
    // Status (lock-free via seqlock)
    // ========================================================================

    /// Get a consistent status snapshot.
    bool get_status(StreamerStatus* out) const noexcept;

    /// Check if the current thread is the worker thread.
    bool is_on_worker_thread() const noexcept {
        return worker_thread_id_ != std::thread::id{} && std::this_thread::get_id() == worker_thread_id_;
    }

    /// Request deferred destruction. The worker thread will delete this
    /// object when it exits (used when destroy is called from a callback
    /// executing on the worker thread).
    void request_deferred_destruction() noexcept { destroy_requested_.store(true, std::memory_order_release); }

    // ========================================================================
    // Analyzer Access
    // ========================================================================

    /// Get the internal analyzer (for metric queries from C# side).
    /// The pipeline owns the analyzer - do NOT delete it.
    context::TsDuckAnalyzer* analyzer() noexcept { return analyzer_.get(); }
    const context::TsDuckAnalyzer* analyzer() const noexcept { return analyzer_.get(); }

    // ========================================================================
    // V2 API: Metrics Database Access
    // ========================================================================

#if TSDUCK_HAS_SQLITE
    /// Get the metrics database (may be nullptr if not configured).
    metrics::MetricsDatabase* metrics_db() noexcept { return metrics_db_.get(); }
    const metrics::MetricsDatabase* metrics_db() const noexcept { return metrics_db_.get(); }

    /// Check if metrics database is enabled.
    bool has_metrics_db() const noexcept { return metrics_db_ != nullptr; }
#else
    /// Stub when SQLite not available.
    void* metrics_db() noexcept { return nullptr; }
    const void* metrics_db() const noexcept { return nullptr; }
    bool has_metrics_db() const noexcept { return false; }
#endif

    /// Get metrics database path (empty if not configured).
    const std::string& metrics_db_path() const noexcept { return metrics_db_path_; }

    // ========================================================================
    // V2 API: Shared Buffer Access
    // ========================================================================

    /// Get the shared buffer (may be nullptr if not configured).
    SharedBuffer* shared_buffer() noexcept { return shared_buffer_.get(); }
    const SharedBuffer* shared_buffer() const noexcept { return shared_buffer_.get(); }

    /// Check if shared buffer is enabled.
    bool has_shared_buffer() const noexcept { return shared_buffer_ != nullptr; }

    /// Get shared buffer name (empty if not configured).
    const std::string& shared_buffer_name() const noexcept { return shared_buffer_name_; }

    /// Reset the shared buffer (call between sessions).
    void reset_shared_buffer() noexcept {
        if (shared_buffer_) {
            shared_buffer_->reset();
        }
    }

private:
    StreamerConfig config_;
    StreamSource source_;
    FailoverManager failover_;
    QualitySwitchTrigger quality_trigger_;
    AlignmentBuffer alignment_;
    KeyframeAligner keyframe_aligner_;

    std::unique_ptr<context::TsDuckContext> context_;
    std::unique_ptr<context::TsDuckAnalyzer> analyzer_;

    // V2: Metrics database and shared buffer
#if TSDUCK_HAS_SQLITE
    std::unique_ptr<metrics::MetricsDatabase> metrics_db_;
#endif
    std::unique_ptr<SharedBuffer> shared_buffer_;
    std::string metrics_db_path_;
    std::string shared_buffer_name_;

    std::thread worker_thread_;
    std::atomic<bool> running_{false};
    std::atomic<bool> switch_requested_{false};
    std::atomic<bool> worker_finished_{true};     // true = no worker running
    std::atomic<bool> destroy_requested_{false};  // deferred destruction from callback
    std::thread::id worker_thread_id_{};

    // Status snapshot (seqlock-protected for lock-free reads)
    alignas(CACHE_LINE_SIZE) mutable StreamerStatus status_{};
    alignas(CACHE_LINE_SIZE) mutable concurrency::Seqlock status_seqlock_;

    // Output
    StreamerOutputCallback output_callback_{nullptr};
    void* output_user_data_{nullptr};
    StreamerEventCallback event_callback_{nullptr};
    void* event_user_data_{nullptr};

    // Timing state for mid-stream switch
    int64_t last_output_pts_{-1};
    int64_t packets_output_{0};
    int64_t bytes_received_total_{0};
    bool first_data_after_switch_{false};

    // Session timing
    int64_t session_start_ticks_{0};

    // ========================================================================
    // Worker Thread
    // ========================================================================

    /// Main worker loop (runs in worker_thread_).
    void worker_loop() noexcept;

    /// Handle a single streaming session (connect → stream → disconnect).
    /// Returns when connection drops, stalls, or stop is requested.
    void stream_session() noexcept;

    /// Process data received from curl (called from data callback context).
    void on_data_received(const uint8_t* data, size_t size) noexcept;

    /// Process aligned TS packets through analyzer and output.
    void process_aligned(uint8_t* data, int32_t length) noexcept;

    /// Write processed data to output (fd or callback).
    void write_output(const uint8_t* data, int32_t length) noexcept;

    /// Perform mid-stream switch to next URL.
    void perform_switch() noexcept;

    /// Update the status snapshot (called periodically from worker).
    void update_status() noexcept;

    /// Emit an event via callback.
    void emit_event(StreamEvent event, int32_t detail = 0) noexcept;

    /// Extract the last video PTS from a chunk of TS data.
    /// Used to track output timeline for switch continuity.
    int64_t extract_last_video_pts(const uint8_t* data, int32_t length) noexcept;

    /// Get current time as .NET ticks.
    static int64_t get_dotnet_ticks() noexcept;

    /// Sleep for specified milliseconds, checking stop flag periodically.
    /// Returns true if sleep completed, false if stop was requested.
    bool interruptible_sleep(int32_t ms) noexcept;

    // ========================================================================
    // V2: Metrics Recording Helpers
    // ========================================================================

#if TSDUCK_HAS_SQLITE
    /// Record success in metrics database (if enabled).
    void record_metrics_success(int64_t bytes, int64_t latency_ms) noexcept;

    /// Record failure in metrics database (if enabled).
    void record_metrics_failure(metrics::FailureType type, const std::string& error_msg) noexcept;
#else
    /// Stub when SQLite not available.
    void record_metrics_success(int64_t, int64_t) noexcept {}
    void record_metrics_failure(int, const std::string&) noexcept {}
#endif

    /// Common initialization shared by both constructors.
    void init_common(const TsDuckConfigNative* analyzer_config);
};

}  // namespace tsduck_interop::streaming

#endif  // TSDUCK_INTEROP_STREAMING_STREAM_PIPELINE_HPP
