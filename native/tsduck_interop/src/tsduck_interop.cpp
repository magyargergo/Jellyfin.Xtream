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

#define TSDUCK_INTEROP_EXPORTS
#include "tsduck_interop.h"

#include <atomic>
#include <cstdint>
#include <exception>
#include <string>

// Include all refactored components
#include "context/analyzer.hpp"
#include "context/context.hpp"
#include "core/logging.hpp"
#include "streaming/stream_pipeline.hpp"

using namespace tsduck_interop;

// ============================================================================
// Logging Component Names
// ============================================================================

namespace {
constexpr const char* kContext = "Context";
constexpr const char* kAnalyzer = "Analyzer";
constexpr const char* kStreamer = "Streamer";
}  // namespace

// ============================================================================
// Type Conversion Helpers
// ============================================================================
// The C API uses opaque forward-declared struct pointers (TsDuckContextHandle, etc.)
// We need to cast between these and the actual C++ class pointers.

namespace {

inline context::TsDuckContext* toImpl(TsDuckContextHandle h) {
    return reinterpret_cast<context::TsDuckContext*>(h);
}

inline TsDuckContextHandle toHandle(context::TsDuckContext* p) {
    return reinterpret_cast<TsDuckContextHandle>(p);
}

inline context::TsDuckAnalyzer* toImpl(TsDuckAnalyzerHandle h) {
    return reinterpret_cast<context::TsDuckAnalyzer*>(h);
}

inline TsDuckAnalyzerHandle toHandle(context::TsDuckAnalyzer* p) {
    return reinterpret_cast<TsDuckAnalyzerHandle>(p);
}

inline streaming::StreamPipeline* toImpl(TsDuckStreamerHandle h) {
    return reinterpret_cast<streaming::StreamPipeline*>(h);
}

inline TsDuckStreamerHandle toHandle(streaming::StreamPipeline* p) {
    return reinterpret_cast<TsDuckStreamerHandle>(p);
}

}  // namespace

// ============================================================================
// Library Initialization
// ============================================================================

TSDUCK_API const char* tsduck_get_version(void) {
    return TSDUCK_INTEROP_VERSION;
}

TSDUCK_API bool tsduck_is_available(void) {
    return true;
}

// ============================================================================
// Logging Configuration
// ============================================================================

TSDUCK_API void tsduck_set_log_level(int32_t level) {
    logging::set_level(static_cast<logging::LogLevel>(level));
    // Log at INFO level so it shows when debugging is enabled
    LOG_INFO("Logging", "log level set to %d", level);
}

TSDUCK_API int32_t tsduck_get_log_level(void) {
    return static_cast<int32_t>(logging::get_level());
}

TSDUCK_API void tsduck_set_log_callback(TsDuckLogCallback callback, void* user_data) {
    logging::set_callback(callback, user_data);
}

TSDUCK_API bool tsduck_is_log_enabled(int32_t level) {
    return logging::is_enabled(static_cast<logging::LogLevel>(level));
}

// ============================================================================
// Context Management
// ============================================================================

// NOLINTBEGIN(cppcoreguidelines-owning-memory) - C API uses raw pointers for P/Invoke
TSDUCK_API TsDuckContextHandle tsduck_context_create(void) {
    try {
        return toHandle(new context::TsDuckContext());
    } catch (const std::exception& e) {
        LOG_ERROR(kContext, "tsduck_context_create exception: %s", e.what());
        return nullptr;
    } catch (...) {
        LOG_ERROR(kContext, "tsduck_context_create unknown exception");
        return nullptr;
    }
}

TSDUCK_API void tsduck_context_destroy(TsDuckContextHandle ctx) {
    delete toImpl(ctx);
}
// NOLINTEND(cppcoreguidelines-owning-memory)

TSDUCK_API bool tsduck_context_is_available(TsDuckContextHandle ctx) {
    auto* impl = toImpl(ctx);
    return impl != nullptr && impl->is_initialized();
}

// ============================================================================
// Analyzer Lifecycle
// ============================================================================

// NOLINTBEGIN(cppcoreguidelines-owning-memory) - C API uses raw pointers for P/Invoke
TSDUCK_API TsDuckAnalyzerHandle tsduck_analyzer_create(
    TsDuckContextHandle ctx,
    const TsDuckConfigNative* config)
{
    auto* ctx_impl = toImpl(ctx);
    if (ctx_impl == nullptr) {
        return nullptr;
    }

    try {
        return toHandle(new context::TsDuckAnalyzer(ctx_impl, config));
    } catch (const std::exception& e) {
        LOG_ERROR(kAnalyzer, "tsduck_analyzer_create exception: %s", e.what());
        return nullptr;
    } catch (...) {
        LOG_ERROR(kAnalyzer, "tsduck_analyzer_create unknown exception");
        return nullptr;
    }
}

TSDUCK_API void tsduck_analyzer_destroy(TsDuckAnalyzerHandle analyzer) {
    delete toImpl(analyzer);
}
// NOLINTEND(cppcoreguidelines-owning-memory)

TSDUCK_API bool tsduck_analyzer_is_initialized(TsDuckAnalyzerHandle analyzer) {
    return toImpl(analyzer) != nullptr;
}

// ============================================================================
// Data Processing
// ============================================================================

TSDUCK_API int32_t tsduck_analyzer_feed(
    TsDuckAnalyzerHandle analyzer,
    const uint8_t* data,
    int32_t length)
{
    auto* impl = toImpl(analyzer);
    if (impl == nullptr || data == nullptr || length <= 0) {
        return TSDUCK_ERROR_INVALID_DATA;
    }

    try {
        return impl->feed(data, length);
    } catch (const std::exception& e) {
        LOG_ERROR(kAnalyzer, "tsduck_analyzer_feed exception: %s", e.what());
        return TSDUCK_ERROR_INTERNAL;
    } catch (...) {
        LOG_ERROR(kAnalyzer, "tsduck_analyzer_feed unknown exception");
        return TSDUCK_ERROR_INTERNAL;
    }
}

TSDUCK_API void tsduck_analyzer_reset(TsDuckAnalyzerHandle analyzer) {
    auto* impl = toImpl(analyzer);
    if (impl != nullptr) {
        impl->reset();
    }
}

TSDUCK_API int32_t tsduck_analyzer_feed_restamp(
    TsDuckAnalyzerHandle analyzer,
    uint8_t* data,
    int32_t length)
{
    auto* impl = toImpl(analyzer);
    if (impl == nullptr || data == nullptr || length <= 0) {
        return TSDUCK_ERROR_INVALID_DATA;
    }

    try {
        return impl->feed_and_restamp(data, length);
    } catch (const std::exception& e) {
        LOG_ERROR(kAnalyzer, "tsduck_analyzer_feed_restamp exception: %s", e.what());
        return TSDUCK_ERROR_INTERNAL;
    } catch (...) {
        LOG_ERROR(kAnalyzer, "tsduck_analyzer_feed_restamp unknown exception");
        return TSDUCK_ERROR_INTERNAL;
    }
}

// ============================================================================
// Metrics Retrieval
// ============================================================================

TSDUCK_API bool tsduck_analyzer_get_metrics(
    TsDuckAnalyzerHandle analyzer,
    TsDuckMetricsNative* out_metrics)
{
    auto* impl = toImpl(analyzer);
    if (impl == nullptr || out_metrics == nullptr) {
        return false;
    }

    return impl->get_metrics(out_metrics);
}

TSDUCK_API bool tsduck_analyzer_has_new_metrics(TsDuckAnalyzerHandle analyzer) {
    auto* impl = toImpl(analyzer);
    return impl != nullptr && impl->has_new_metrics.load(std::memory_order_acquire);
}

// ============================================================================
// PCR Analysis
// ============================================================================

TSDUCK_API bool tsduck_analyzer_get_pcr_analysis(
    TsDuckAnalyzerHandle analyzer,
    PcrAnalysisNative* out_analysis)
{
    auto* impl = toImpl(analyzer);
    if (impl == nullptr || out_analysis == nullptr) {
        return false;
    }

    return impl->pcr.get(out_analysis);
}

TSDUCK_API bool tsduck_analyzer_set_pcr_jitter_threshold(
    TsDuckAnalyzerHandle analyzer,
    double max_jitter_us)
{
    auto* impl = toImpl(analyzer);
    if (impl == nullptr || max_jitter_us < 0) {
        return false;
    }
    impl->pcr_jitter_threshold_us.store(max_jitter_us, std::memory_order_release);
    return true;
}

// ============================================================================
// IAT Analysis
// ============================================================================

TSDUCK_API bool tsduck_analyzer_get_iat_analysis(
    TsDuckAnalyzerHandle analyzer,
    IatAnalysisNative* out_analysis)
{
    auto* impl = toImpl(analyzer);
    if (impl == nullptr || out_analysis == nullptr) {
        return false;
    }

    return impl->iat.get(out_analysis);
}

TSDUCK_API bool tsduck_analyzer_set_iat_jitter_threshold(
    TsDuckAnalyzerHandle analyzer,
    double max_jitter_us)
{
    auto* impl = toImpl(analyzer);
    if (impl == nullptr || max_jitter_us < 0) {
        return false;
    }
    impl->iat_jitter_threshold_us.store(max_jitter_us, std::memory_order_release);
    return true;
}

// ============================================================================
// Bitrate Analysis
// ============================================================================

TSDUCK_API bool tsduck_analyzer_get_bitrate_analysis(
    TsDuckAnalyzerHandle analyzer,
    BitrateAnalysisNative* out_analysis)
{
    auto* impl = toImpl(analyzer);
    if (impl == nullptr || out_analysis == nullptr) {
        return false;
    }

    return impl->get_bitrate_analysis(out_analysis);
}

// ============================================================================
// Extended PID Information
// ============================================================================

TSDUCK_API int32_t tsduck_analyzer_get_pid_count(TsDuckAnalyzerHandle analyzer) {
    auto* impl = toImpl(analyzer);
    if (impl == nullptr) {
        return TSDUCK_ERROR_NULL_HANDLE;
    }
    return impl->pids.get_active_count();
}

TSDUCK_API int32_t tsduck_analyzer_get_pid_info_extended(
    TsDuckAnalyzerHandle analyzer,
    TsDuckPidInfoExtended* out_pids,
    int32_t max_pids)
{
    auto* impl = toImpl(analyzer);
    if (impl == nullptr) {
        return TSDUCK_ERROR_NULL_HANDLE;
    }
    if (out_pids == nullptr || max_pids <= 0) {
        return TSDUCK_ERROR_INVALID_DATA;
    }

    return impl->pids.get_count(out_pids, max_pids);
}

// ============================================================================
// Integrated Restamping (through Analyzer)
// ============================================================================

TSDUCK_API bool tsduck_analyzer_configure_restamp(
    TsDuckAnalyzerHandle analyzer,
    int32_t mode,
    int32_t smooth_pcr,
    int32_t fix_discontinuities)
{
    auto* impl = toImpl(analyzer);
    if (impl == nullptr) {
        return false;
    }

    impl->configure_restamp(mode, smooth_pcr, fix_discontinuities);
    return true;
}

TSDUCK_API void tsduck_analyzer_handle_switch(
    TsDuckAnalyzerHandle analyzer,
    int64_t last_output_pts,
    int64_t new_input_first_pts)
{
    auto* impl = toImpl(analyzer);
    if (impl != nullptr) {
        impl->handle_switch(last_output_pts, new_input_first_pts);
    }
}

TSDUCK_API bool tsduck_analyzer_get_restamp_statistics(
    TsDuckAnalyzerHandle analyzer,
    RestampingStatisticsNative* out_stats)
{
    auto* impl = toImpl(analyzer);
    if (impl == nullptr || out_stats == nullptr) {
        return false;
    }

    return impl->get_restamp_statistics(out_stats);
}

TSDUCK_API bool tsduck_analyzer_is_restamping_enabled(
    TsDuckAnalyzerHandle analyzer)
{
    auto* impl = toImpl(analyzer);
    return impl != nullptr && impl->is_restamping_enabled();
}

// ============================================================================
// Callbacks
// ============================================================================

TSDUCK_API void tsduck_analyzer_set_metrics_callback(
    TsDuckAnalyzerHandle analyzer,
    TsDuckMetricsCallback callback,
    void* user_data)
{
    auto* impl = toImpl(analyzer);
    if (impl == nullptr) {
        return;
    }
    impl->metrics_user_data.store(user_data, std::memory_order_release);
    impl->metrics_callback.store(callback, std::memory_order_release);
}

TSDUCK_API void tsduck_analyzer_set_violation_callback(
    TsDuckAnalyzerHandle analyzer,
    TsDuckViolationCallback callback,
    void* user_data)
{
    auto* impl = toImpl(analyzer);
    if (impl == nullptr) {
        return;
    }
    impl->violation_user_data.store(user_data, std::memory_order_release);
    impl->violation_callback.store(callback, std::memory_order_release);
}

// ============================================================================
// A/V Sync Analysis
// ============================================================================

TSDUCK_API bool tsduck_analyzer_get_av_sync_analysis(
    TsDuckAnalyzerHandle analyzer,
    AvSyncAnalysisNative* out_analysis)
{
    auto* impl = toImpl(analyzer);
    if (impl == nullptr || out_analysis == nullptr) {
        return false;
    }

    return impl->av_sync.get_analysis(out_analysis);
}

TSDUCK_API int32_t tsduck_analyzer_get_pts_samples(
    TsDuckAnalyzerHandle analyzer,
    PtsDtsSampleNative* out_samples,
    int32_t max_samples)
{
    auto* impl = toImpl(analyzer);
    if (impl == nullptr) {
        return TSDUCK_ERROR_NULL_HANDLE;
    }
    if (out_samples == nullptr || max_samples <= 0) {
        return TSDUCK_ERROR_INVALID_DATA;
    }

    return impl->av_sync.get_samples(out_samples, max_samples);
}

TSDUCK_API int32_t tsduck_analyzer_get_pts_sample_count(TsDuckAnalyzerHandle analyzer) {
    auto* impl = toImpl(analyzer);
    if (impl == nullptr) {
        return TSDUCK_ERROR_NULL_HANDLE;
    }

    return impl->av_sync.get_sample_count();
}

// ============================================================================
// PSI Program Information
// ============================================================================

TSDUCK_API int32_t tsduck_analyzer_get_programs(
    TsDuckAnalyzerHandle analyzer,
    TsDuckProgramInfoNative* out_programs,
    int32_t max_programs)
{
    auto* impl = toImpl(analyzer);
    if (impl == nullptr) {
        return TSDUCK_ERROR_NULL_HANDLE;
    }
    if (out_programs == nullptr || max_programs <= 0) {
        return TSDUCK_ERROR_INVALID_DATA;
    }

    return impl->psi.get_programs(out_programs, max_programs);
}

// ============================================================================
// HTTP Streamer
// ============================================================================

TSDUCK_API TsDuckStreamerHandle tsduck_streamer_create(
    const TsDuckStreamerConfigNative* config,
    const TsDuckConfigNative* analyzer_config)
{
    LOG_DEBUG(kStreamer, "tsduck_streamer_create called");

    try {
        streaming::StreamerConfig cfg{};
        if (config != nullptr) {
            cfg.connect_timeout_ms = config->connect_timeout_ms;
            cfg.response_timeout_ms = config->response_timeout_ms;
            cfg.stall_timeout_ms = config->stall_timeout_ms;
            cfg.max_retries = config->max_retries;
            cfg.initial_backoff_ms = config->initial_backoff_ms;
            cfg.max_backoff_ms = config->max_backoff_ms;
            cfg.backoff_multiplier = config->backoff_multiplier;
            cfg.backoff_jitter_ms = config->backoff_jitter_ms;
            cfg.output_fd = config->output_fd;
            cfg.alignment_buffer_packets = config->alignment_buffer_packets;
            cfg.enable_restamp = config->enable_restamp;
            cfg.restamp_mode = config->restamp_mode;
            cfg.low_speed_limit_bytes = config->low_speed_limit_bytes;
            cfg.low_speed_time_sec = config->low_speed_time_sec;
            cfg.stalls_before_switch = config->stalls_before_switch;

            // Quality-based switching (TR 101 290 error rate thresholds)
            cfg.enable_quality_switch = config->enable_quality_switch;
            cfg.quality_check_interval_ms = config->quality_check_interval_ms;
            cfg.quality_window_seconds = config->quality_window_seconds;
            cfg.max_sync_errors_per_window = config->max_sync_errors_per_window;
            cfg.max_continuity_errors_per_sec = config->max_continuity_errors_per_sec;
            cfg.max_transport_errors_per_sec = config->max_transport_errors_per_sec;
            cfg.max_pcr_errors_per_sec = config->max_pcr_errors_per_sec;
        }

        LOG_DEBUG(kStreamer, "config: enable_quality_switch=%d, stalls_before_switch=%d",
                  cfg.enable_quality_switch, cfg.stalls_before_switch);

        // NOLINTBEGIN(cppcoreguidelines-owning-memory) - C API requires raw pointers
        auto* pipeline = new streaming::StreamPipeline(cfg, analyzer_config);
        LOG_DEBUG(kStreamer, "tsduck_streamer_create done, handle=%p", static_cast<void*>(pipeline));
        return toHandle(pipeline);
        // NOLINTEND(cppcoreguidelines-owning-memory)
    } catch (const std::exception& e) {
        LOG_ERROR(kStreamer, "tsduck_streamer_create exception: %s", e.what());
        return nullptr;
    } catch (...) {
        LOG_ERROR(kStreamer, "tsduck_streamer_create unknown exception");
        return nullptr;
    }
}

TSDUCK_API void tsduck_streamer_destroy(TsDuckStreamerHandle streamer) {
    auto* impl = toImpl(streamer);
    const bool on_worker = impl != nullptr && impl->is_on_worker_thread();
    LOG_DEBUG(kStreamer, "tsduck_streamer_destroy called handle=%p on_worker=%d",
              static_cast<void*>(impl), static_cast<int>(on_worker));

    if (impl == nullptr) {
        return;
    }

    impl->stop();

    if (impl->is_on_worker_thread()) {
        // We're being called from within a callback on the worker thread.
        // We can't delete the object now because the worker is still using it
        // (we're in the middle of emitEvent → callback → destroy call chain).
        // Request deferred destruction: the worker will delete itself when it
        // exits the workerLoop naturally (running_ is already false).
        LOG_DEBUG(kStreamer, "tsduck_streamer_destroy deferred handle=%p", static_cast<void*>(impl));
        impl->request_deferred_destruction();
    } else {
        // Normal case: worker has been joined, safe to delete immediately.
        // NOLINTBEGIN(cppcoreguidelines-owning-memory) - C API requires raw pointers
        LOG_DEBUG(kStreamer, "tsduck_streamer_destroy deleting handle=%p", static_cast<void*>(impl));
        delete impl;
        LOG_DEBUG(kStreamer, "tsduck_streamer_destroy done");
        // NOLINTEND(cppcoreguidelines-owning-memory)
    }
}

TSDUCK_API int32_t tsduck_streamer_add_url(
    TsDuckStreamerHandle streamer,
    const char* url)
{
    auto* impl = toImpl(streamer);
    if (impl == nullptr) {
        LOG_WARNING(kStreamer, "tsduck_streamer_add_url called with null handle");
        return TSDUCK_ERROR_NULL_HANDLE;
    }
    if (url == nullptr) {
        LOG_WARNING(kStreamer, "tsduck_streamer_add_url called with null URL");
        return TSDUCK_ERROR_INVALID_DATA;
    }

    try {
        impl->add_url(std::string(url));
        LOG_INFO(kStreamer, "added URL: %s", url);
        return TSDUCK_OK;
    } catch (const std::exception& e) {
        LOG_ERROR(kStreamer, "tsduck_streamer_add_url exception: %s", e.what());
        return TSDUCK_ERROR_INTERNAL;
    } catch (...) {
        LOG_ERROR(kStreamer, "tsduck_streamer_add_url unknown exception");
        return TSDUCK_ERROR_INTERNAL;
    }
}

TSDUCK_API void tsduck_streamer_clear_urls(TsDuckStreamerHandle streamer) {
    auto* impl = toImpl(streamer);
    if (impl != nullptr) {
        impl->clear_urls();
    }
}

TSDUCK_API void tsduck_streamer_set_output_fd(
    TsDuckStreamerHandle streamer,
    int32_t fd)
{
    auto* impl = toImpl(streamer);
    if (impl != nullptr) {
        impl->set_output_fd(fd);
    }
}

TSDUCK_API void tsduck_streamer_set_output_callback(
    TsDuckStreamerHandle streamer,
    TsDuckStreamerOutputCallback callback,
    void* user_data)
{
    auto* impl = toImpl(streamer);
    if (impl != nullptr) {
        impl->set_output_callback(callback, user_data);
    }
}

TSDUCK_API void tsduck_streamer_set_event_callback(
    TsDuckStreamerHandle streamer,
    TsDuckStreamerEventCallback callback,
    void* user_data)
{
    auto* impl = toImpl(streamer);
    if (impl != nullptr) {
        impl->set_event_callback(callback, user_data);
    }
}

TSDUCK_API bool tsduck_streamer_start(TsDuckStreamerHandle streamer) {
    auto* impl = toImpl(streamer);
    LOG_DEBUG(kStreamer, "tsduck_streamer_start handle=%p", static_cast<void*>(impl));
    if (impl == nullptr) {
        return false;
    }
    const bool result = impl->start();
    LOG_DEBUG(kStreamer, "tsduck_streamer_start result=%d handle=%p",
              static_cast<int>(result), static_cast<void*>(impl));
    return result;
}

TSDUCK_API void tsduck_streamer_stop(TsDuckStreamerHandle streamer) {
    auto* impl = toImpl(streamer);
    LOG_DEBUG(kStreamer, "tsduck_streamer_stop handle=%p", static_cast<void*>(impl));
    if (impl != nullptr) {
        impl->stop();
    }
    LOG_DEBUG(kStreamer, "tsduck_streamer_stop done");
}

TSDUCK_API void tsduck_streamer_request_switch(TsDuckStreamerHandle streamer) {
    auto* impl = toImpl(streamer);
    if (impl != nullptr) {
        impl->request_switch();
    }
}

TSDUCK_API bool tsduck_streamer_get_status(
    TsDuckStreamerHandle streamer,
    TsDuckStreamerStatusNative* out_status)
{
    auto* impl = toImpl(streamer);
    if (impl == nullptr || out_status == nullptr) {
        return false;
    }

    streaming::StreamerStatus status{};
    if (!impl->get_status(&status)) return false;

    out_status->state = status.state;
    out_status->current_url_index = status.current_url_index;
    out_status->url_count = status.url_count;
    out_status->retry_count = status.retry_count;
    out_status->bytes_received = status.bytes_received;
    out_status->packets_output = status.packets_output;
    out_status->switches_completed = status.switches_completed;
    out_status->reconnections = status.reconnections;
    out_status->last_data_time_ticks = status.last_data_time_ticks;
    out_status->session_start_ticks = status.session_start_ticks;
    out_status->last_http_status = status.last_http_status;
    out_status->last_curl_error = status.last_curl_error;
    return true;
}

TSDUCK_API TsDuckAnalyzerHandle tsduck_streamer_get_analyzer(
    TsDuckStreamerHandle streamer)
{
    auto* impl = toImpl(streamer);
    if (impl == nullptr) {
        return nullptr;
    }
    return toHandle(impl->analyzer());
}
