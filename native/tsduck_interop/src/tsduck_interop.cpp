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

#include <algorithm>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <exception>
#include <string>

// Conditional SQLite support
#ifndef TSDUCK_HAS_SQLITE
#define TSDUCK_HAS_SQLITE 0
#endif

// Include all refactored components
#include "context/analyzer.hpp"
#include "context/context.hpp"
#include "core/logging.hpp"
#include "ipc/shared_memory_channel.hpp"
#include "registry/channel_registry.hpp"
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
// SCTE-35 Splice Information
// ============================================================================

TSDUCK_API int32_t tsduck_analyzer_get_scte35_events(
    TsDuckAnalyzerHandle analyzer,
    Scte35EventNative* out_events,
    int32_t max_events)
{
    auto* impl = toImpl(analyzer);
    if (impl == nullptr) {
        return TSDUCK_ERROR_NULL_HANDLE;
    }
    if (out_events == nullptr || max_events <= 0) {
        return TSDUCK_ERROR_INVALID_DATA;
    }

    return impl->scte35.get_events(out_events, max_events);
}

TSDUCK_API bool tsduck_analyzer_get_current_scte35_event(
    TsDuckAnalyzerHandle analyzer,
    Scte35EventNative* out_event)
{
    auto* impl = toImpl(analyzer);
    if (impl == nullptr || out_event == nullptr) {
        return false;
    }

    return impl->scte35.get_current_event(out_event);
}

TSDUCK_API int32_t tsduck_analyzer_get_scte35_state(TsDuckAnalyzerHandle analyzer)
{
    auto* impl = toImpl(analyzer);
    if (impl == nullptr) {
        return SCTE35_STATE_IN_CONTENT;
    }

    return static_cast<int32_t>(impl->scte35.get_splice_state());
}

TSDUCK_API bool tsduck_analyzer_is_in_ad_break(TsDuckAnalyzerHandle analyzer)
{
    auto* impl = toImpl(analyzer);
    if (impl == nullptr) {
        return false;
    }

    return impl->scte35.is_in_ad_break();
}

TSDUCK_API int64_t tsduck_analyzer_get_scte35_event_count(TsDuckAnalyzerHandle analyzer)
{
    auto* impl = toImpl(analyzer);
    if (impl == nullptr) {
        return 0;
    }

    return impl->scte35.get_event_count();
}

TSDUCK_API void tsduck_analyzer_set_scte35_callback(
    TsDuckAnalyzerHandle analyzer,
    TsDuckScte35Callback callback,
    void* user_data)
{
    auto* impl = toImpl(analyzer);
    if (impl == nullptr) {
        return;
    }
    impl->scte35_user_data.store(user_data, std::memory_order_release);
    impl->scte35_callback.store(callback, std::memory_order_release);
}

// ============================================================================
// NAL Unit Parsing (Video Codec Info / Parameter Sets)
// ============================================================================

TSDUCK_API bool tsduck_analyzer_get_video_codec_info(
    TsDuckAnalyzerHandle analyzer,
    uint16_t video_pid,
    VideoCodecInfoNative* out_info)
{
    auto* impl = toImpl(analyzer);
    if (impl == nullptr || out_info == nullptr) {
        return false;
    }

    return impl->nal_parser.get_video_codec_info(video_pid, out_info);
}

TSDUCK_API bool tsduck_analyzer_get_parameter_sets(
    TsDuckAnalyzerHandle analyzer,
    uint16_t video_pid,
    NalParameterSetsNative* out_params)
{
    auto* impl = toImpl(analyzer);
    if (impl == nullptr || out_params == nullptr) {
        return false;
    }

    return impl->nal_parser.get_parameter_sets(video_pid, out_params);
}

TSDUCK_API bool tsduck_analyzer_has_idr_frame(
    TsDuckAnalyzerHandle analyzer,
    uint16_t video_pid)
{
    auto* impl = toImpl(analyzer);
    if (impl == nullptr) {
        return false;
    }

    return impl->nal_parser.check_idr_frame(video_pid);
}

TSDUCK_API int64_t tsduck_analyzer_get_idr_frame_count(TsDuckAnalyzerHandle analyzer)
{
    auto* impl = toImpl(analyzer);
    if (impl == nullptr) {
        return 0;
    }

    return impl->nal_parser.total_idr_frames.load(std::memory_order_acquire);
}

TSDUCK_API bool tsduck_analyzer_register_video_pid(
    TsDuckAnalyzerHandle analyzer,
    uint16_t video_pid,
    uint8_t stream_type)
{
    auto* impl = toImpl(analyzer);
    if (impl == nullptr) {
        return false;
    }

    return impl->nal_parser.add_video_pid(video_pid, stream_type) >= 0;
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

            // Health-based URL selection settings
            if (config->quarantine_duration_ms > 0) {
                cfg.quarantine_duration_ms = config->quarantine_duration_ms;
            }
            if (config->max_quarantine_duration_ms > 0) {
                cfg.max_quarantine_duration_ms = config->max_quarantine_duration_ms;
            }
            if (config->quarantine_backoff_multiplier > 0.0) {
                cfg.quarantine_backoff_multiplier = config->quarantine_backoff_multiplier;
            }
            if (config->score_boost_on_success > 0.0) {
                cfg.score_boost_on_success = config->score_boost_on_success;
            }
            if (config->score_penalty_on_failure > 0.0) {
                cfg.score_penalty_on_failure = config->score_penalty_on_failure;
            }
            if (config->default_health_score > 0.0) {
                cfg.default_health_score = config->default_health_score;
            }
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

TSDUCK_API int32_t tsduck_streamer_add_url_with_score(
    TsDuckStreamerHandle streamer,
    const char* url,
    double health_score)
{
    auto* impl = toImpl(streamer);
    if (impl == nullptr) {
        LOG_WARNING(kStreamer, "tsduck_streamer_add_url_with_score called with null handle");
        return TSDUCK_ERROR_NULL_HANDLE;
    }
    if (url == nullptr) {
        LOG_WARNING(kStreamer, "tsduck_streamer_add_url_with_score called with null URL");
        return TSDUCK_ERROR_INVALID_DATA;
    }

    try {
        impl->add_url_with_score(std::string(url), health_score);
        LOG_INFO(kStreamer, "added URL with score %.1f: %s", health_score, url);
        return TSDUCK_OK;
    } catch (const std::exception& e) {
        LOG_ERROR(kStreamer, "tsduck_streamer_add_url_with_score exception: %s", e.what());
        return TSDUCK_ERROR_INTERNAL;
    } catch (...) {
        LOG_ERROR(kStreamer, "tsduck_streamer_add_url_with_score unknown exception");
        return TSDUCK_ERROR_INTERNAL;
    }
}

TSDUCK_API int32_t tsduck_streamer_update_url_score(
    TsDuckStreamerHandle streamer,
    int32_t url_index,
    double new_score)
{
    auto* impl = toImpl(streamer);
    if (impl == nullptr) {
        LOG_WARNING(kStreamer, "tsduck_streamer_update_url_score called with null handle");
        return TSDUCK_ERROR_NULL_HANDLE;
    }

    if (!impl->update_url_score(url_index, new_score)) {
        LOG_WARNING(kStreamer, "tsduck_streamer_update_url_score: invalid index %d", url_index);
        return TSDUCK_ERROR_INVALID_DATA;
    }

    LOG_DEBUG(kStreamer, "updated URL %d score to %.1f", url_index, new_score);
    return TSDUCK_OK;
}

TSDUCK_API double tsduck_streamer_get_url_score(
    TsDuckStreamerHandle streamer,
    int32_t url_index)
{
    auto* impl = toImpl(streamer);
    if (impl == nullptr) {
        return -1.0;
    }

    return impl->get_url_score(url_index);
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
    out_status->quality_switches = status.quality_switches;
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

// ============================================================================
// Streamer Shared Memory Output Mode
// ============================================================================

TSDUCK_API int32_t tsduck_streamer_set_shared_memory_output(
    TsDuckStreamerHandle streamer,
    const char* name,
    uint32_t slot_count,
    uint32_t slot_size)
{
    auto* impl = toImpl(streamer);
    if (impl == nullptr) {
        LOG_WARNING(kStreamer, "tsduck_streamer_set_shared_memory_output null handle");
        return TSDUCK_ERROR_NULL_HANDLE;
    }
    if (name == nullptr || name[0] == '\0') {
        LOG_WARNING(kStreamer, "tsduck_streamer_set_shared_memory_output null/empty name");
        return TSDUCK_ERROR_INVALID_DATA;
    }

    // Use defaults if zero
    if (slot_count == 0) slot_count = static_cast<uint32_t>(ipc::DEFAULT_SLOT_COUNT);
    if (slot_size == 0) slot_size = static_cast<uint32_t>(ipc::DEFAULT_SLOT_SIZE);

    bool ok = impl->set_shared_memory_output(
        std::string(name),
        static_cast<std::size_t>(slot_count),
        static_cast<std::size_t>(slot_size)
    );

    if (ok) {
        LOG_INFO(kStreamer, "tsduck_streamer_set_shared_memory_output: %s (slots=%u, size=%u)",
                 name, slot_count, slot_size);
        return TSDUCK_OK;
    }
    return TSDUCK_ERROR_INTERNAL;
}

TSDUCK_API const char* tsduck_streamer_get_shared_memory_name(
    TsDuckStreamerHandle streamer)
{
    auto* impl = toImpl(streamer);
    if (impl == nullptr) {
        return nullptr;
    }
    const auto& name = impl->shared_memory_name();
    return name.empty() ? nullptr : name.c_str();
}

TSDUCK_API int32_t tsduck_streamer_is_shared_memory_mode(
    TsDuckStreamerHandle streamer)
{
    auto* impl = toImpl(streamer);
    return (impl != nullptr && impl->is_shared_memory_mode()) ? 1 : 0;
}

// ============================================================================
// Network Configuration API
// ============================================================================

namespace {
constexpr const char* kNetworkConfig = "NetworkConfig";

/// Convert C API NetworkConfigNative to C++ NetworkConfig
streaming::NetworkConfig convertNetworkConfig(const NetworkConfigNative* config) noexcept {
    streaming::NetworkConfig cpp_config{};

    if (config == nullptr) {
        return cpp_config;
    }

    cpp_config.dns_mode = config->dns_mode;
    cpp_config.dns_server_count = config->dns_server_count;
    cpp_config.dns_cache_timeout_sec = config->dns_cache_timeout_sec;
    cpp_config.ip_resolve_mode = config->ip_resolve_mode;
    cpp_config.dns_timeout_ms = config->dns_timeout_ms;
    cpp_config.tcp_connect_timeout_ms = config->tcp_connect_timeout_ms;
    cpp_config.tls_handshake_timeout_ms = config->tls_handshake_timeout_ms;
    cpp_config.first_byte_timeout_ms = config->first_byte_timeout_ms;
    cpp_config.happy_eyeballs_timeout_ms = config->happy_eyeballs_timeout_ms;
    cpp_config.tcp_keepalive_enabled = config->tcp_keepalive_enabled;
    cpp_config.tcp_keepalive_idle_sec = config->tcp_keepalive_idle_sec;
    cpp_config.tcp_keepalive_interval_sec = config->tcp_keepalive_interval_sec;
    cpp_config.recv_buffer_size = config->recv_buffer_size;

    // Copy DNS servers
    for (int32_t i = 0; i < config->dns_server_count && i < streaming::MAX_DNS_SERVERS; ++i) {
        std::strncpy(cpp_config.dns_servers[i], config->dns_servers[i],
                     streaming::DNS_SERVER_MAX_LEN - 1);
        cpp_config.dns_servers[i][streaming::DNS_SERVER_MAX_LEN - 1] = '\0';
    }

    // Copy DoH URL
    std::strncpy(cpp_config.doh_url, config->doh_url, streaming::DOH_URL_MAX_LEN - 1);
    cpp_config.doh_url[streaming::DOH_URL_MAX_LEN - 1] = '\0';

    return cpp_config;
}
}  // namespace

TSDUCK_API NetworkConfigNative tsduck_network_config_default(void) {
    NetworkConfigNative config{};

    // Use sensible defaults matching the C++ NetworkConfig defaults
    config.dns_mode = DNS_RESOLVE_SYSTEM;
    config.dns_server_count = 0;
    config.dns_cache_timeout_sec = 60;
    config.ip_resolve_mode = IP_RESOLVE_IPV4_ONLY;  // Safe default for IPTV
    config.dns_timeout_ms = 5000;
    config.tcp_connect_timeout_ms = 5000;
    config.tls_handshake_timeout_ms = 5000;
    config.first_byte_timeout_ms = 10000;
    config.happy_eyeballs_timeout_ms = 200;
    config.tcp_keepalive_enabled = 1;
    config.tcp_keepalive_idle_sec = 60;
    config.tcp_keepalive_interval_sec = 60;
    config.recv_buffer_size = 65536;  // 64KB

    // Zero out arrays
    std::memset(config.dns_servers, 0, sizeof(config.dns_servers));
    std::memset(config.doh_url, 0, sizeof(config.doh_url));
    std::memset(config.reserved, 0, sizeof(config.reserved));

    LOG_DEBUG(kNetworkConfig, "Created default network config (IPv4-only, system DNS)");
    return config;
}

TSDUCK_API int32_t tsduck_network_config_add_dns_server(
    NetworkConfigNative* config,
    const char* server)
{
    if (config == nullptr || server == nullptr) {
        return 0;
    }

    if (config->dns_server_count >= NETWORK_CONFIG_MAX_DNS_SERVERS) {
        LOG_WARNING(kNetworkConfig, "Cannot add DNS server '%s': max servers (%d) reached",
                    server, NETWORK_CONFIG_MAX_DNS_SERVERS);
        return 0;
    }

    std::strncpy(config->dns_servers[config->dns_server_count], server,
                 NETWORK_CONFIG_DNS_SERVER_MAX_LEN - 1);
    config->dns_servers[config->dns_server_count][NETWORK_CONFIG_DNS_SERVER_MAX_LEN - 1] = '\0';
    config->dns_server_count++;

    LOG_DEBUG(kNetworkConfig, "Added DNS server: %s (total: %d)", server, config->dns_server_count);
    return 1;
}

TSDUCK_API void tsduck_network_config_set_doh_url(
    NetworkConfigNative* config,
    const char* url)
{
    if (config == nullptr) {
        return;
    }

    if (url == nullptr) {
        config->doh_url[0] = '\0';
        return;
    }

    std::strncpy(config->doh_url, url, NETWORK_CONFIG_DOH_URL_MAX_LEN - 1);
    config->doh_url[NETWORK_CONFIG_DOH_URL_MAX_LEN - 1] = '\0';

    LOG_DEBUG(kNetworkConfig, "Set DoH URL: %s", url);
}

TSDUCK_API void tsduck_network_config_clear_dns_servers(NetworkConfigNative* config) {
    if (config == nullptr) {
        return;
    }

    config->dns_server_count = 0;
    std::memset(config->dns_servers, 0, sizeof(config->dns_servers));

    LOG_DEBUG(kNetworkConfig, "Cleared DNS servers");
}

TSDUCK_API int32_t tsduck_streamer_set_network_config(
    TsDuckStreamerHandle streamer,
    const NetworkConfigNative* config)
{
    auto* impl = toImpl(streamer);
    if (impl == nullptr) {
        LOG_WARNING(kStreamer, "tsduck_streamer_set_network_config: null streamer handle");
        return TSDUCK_ERROR_NULL_HANDLE;
    }

    if (config == nullptr) {
        // Use default config
        LOG_DEBUG(kStreamer, "Setting default network config on streamer");
        streaming::NetworkConfig default_config{};
        impl->set_network_config(default_config);
    } else {
        LOG_DEBUG(kStreamer, "Setting custom network config on streamer "
                  "(dns_mode=%d, ip_mode=%d, dns_servers=%d)",
                  config->dns_mode, config->ip_resolve_mode, config->dns_server_count);
        auto cpp_config = convertNetworkConfig(config);
        impl->set_network_config(cpp_config);
    }

    return TSDUCK_OK;
}

TSDUCK_API int32_t tsduck_streamer_get_last_dns_error(TsDuckStreamerHandle streamer) {
    auto* impl = toImpl(streamer);
    if (impl == nullptr) {
        return DNS_ERROR_NONE;
    }

    // TODO: Track DNS errors in StreamPipeline/StreamSource and expose via this API
    // For now, return NONE - DNS errors are logged but not tracked
    return DNS_ERROR_NONE;
}

// ============================================================================
// Shared Memory Producer API
// ============================================================================

namespace {
constexpr const char* kSharedMemory = "SharedMemory";
}  // namespace

TSDUCK_API void* tsduck_shm_producer_create(
    const char* name,
    uint32_t slot_count,
    uint32_t slot_size)
{
    if (name == nullptr) {
        LOG_ERROR(kSharedMemory, "tsduck_shm_producer_create called with null name");
        return nullptr;
    }

    LOG_DEBUG(kSharedMemory, "Creating shared memory producer '%s' (%u slots x %u bytes)",
              name, slot_count, slot_size);

    try {
        std::error_code ec;
        auto producer = ipc::SharedMemoryProducer::create(
            name,
            static_cast<std::size_t>(slot_count),
            static_cast<std::size_t>(slot_size),
            &ec);

        if (!producer) {
            LOG_ERROR(kSharedMemory, "Failed to create shared memory producer: %s",
                      ec.message().c_str());
            return nullptr;
        }

        // Release ownership from unique_ptr and return opaque pointer
        LOG_INFO(kSharedMemory, "Shared memory producer '%s' created successfully", name);
        return producer.release();
    } catch (const std::exception& e) {
        LOG_ERROR(kSharedMemory, "tsduck_shm_producer_create exception: %s", e.what());
        return nullptr;
    } catch (...) {
        LOG_ERROR(kSharedMemory, "tsduck_shm_producer_create unknown exception");
        return nullptr;
    }
}

TSDUCK_API void tsduck_shm_producer_destroy(void* producer) {
    if (producer == nullptr) {
        return;
    }

    LOG_DEBUG(kSharedMemory, "Destroying shared memory producer");

    // NOLINTBEGIN(cppcoreguidelines-owning-memory) - C API requires raw pointers
    auto* impl = static_cast<ipc::SharedMemoryProducer*>(producer);
    delete impl;
    // NOLINTEND(cppcoreguidelines-owning-memory)

    LOG_DEBUG(kSharedMemory, "Shared memory producer destroyed");
}

TSDUCK_API int32_t tsduck_shm_producer_write(
    void* producer,
    const uint8_t* data,
    uint32_t length,
    uint32_t* bytes_written)
{
    if (producer == nullptr || data == nullptr) {
        return -1;
    }

    auto* impl = static_cast<ipc::SharedMemoryProducer*>(producer);

    auto result = impl->write(
        std::span<const std::byte>(reinterpret_cast<const std::byte*>(data), length));

    if (bytes_written != nullptr) {
        *bytes_written = static_cast<uint32_t>(result.bytes_written);
    }

    return result.overflow ? 1 : 0;
}

TSDUCK_API void tsduck_shm_producer_signal(void* producer) {
    if (producer == nullptr) {
        return;
    }

    auto* impl = static_cast<ipc::SharedMemoryProducer*>(producer);
    impl->signal_data_available();
}

TSDUCK_API void tsduck_shm_producer_set_eos(void* producer) {
    if (producer == nullptr) {
        return;
    }

    auto* impl = static_cast<ipc::SharedMemoryProducer*>(producer);
    impl->set_end_of_stream();
}

TSDUCK_API void tsduck_shm_producer_set_error(
    void* producer,
    uint32_t code,
    const char* message)
{
    if (producer == nullptr) {
        return;
    }

    auto* impl = static_cast<ipc::SharedMemoryProducer*>(producer);
    impl->set_error(
        static_cast<ipc::SharedMemoryError>(code),
        message != nullptr ? message : "");
}

TSDUCK_API void tsduck_shm_producer_set_discontinuity(void* producer) {
    if (producer == nullptr) {
        return;
    }

    auto* impl = static_cast<ipc::SharedMemoryProducer*>(producer);
    impl->set_discontinuity();
}

TSDUCK_API void tsduck_shm_producer_clear_discontinuity(void* producer) {
    if (producer == nullptr) {
        return;
    }

    auto* impl = static_cast<ipc::SharedMemoryProducer*>(producer);
    impl->clear_discontinuity();
}

TSDUCK_API int32_t tsduck_shm_producer_is_consumer_attached(void* producer) {
    if (producer == nullptr) {
        return 0;
    }

    auto* impl = static_cast<ipc::SharedMemoryProducer*>(producer);
    return impl->is_consumer_attached() ? 1 : 0;
}

// ============================================================================
// Channel Registry API (Minimal - C++ handles health/quality internally)
// ============================================================================

namespace {
constexpr const char* kRegistry = "Registry";

inline registry::ChannelRegistry* toImpl(ChannelRegistryHandle h) {
    return reinterpret_cast<registry::ChannelRegistry*>(h);
}

inline ChannelRegistryHandle toHandle(registry::ChannelRegistry* p) {
    return reinterpret_cast<ChannelRegistryHandle>(p);
}
}  // namespace

// NOLINTBEGIN(cppcoreguidelines-owning-memory) - C API uses raw pointers for P/Invoke
TSDUCK_API ChannelRegistryHandle tsduck_registry_create(void) {
    LOG_DEBUG(kRegistry, "tsduck_registry_create called");

    try {
        auto* reg = new registry::ChannelRegistry();
        LOG_DEBUG(kRegistry, "tsduck_registry_create done, handle=%p", static_cast<void*>(reg));
        return toHandle(reg);
    } catch (const std::exception& e) {
        LOG_ERROR(kRegistry, "tsduck_registry_create exception: %s", e.what());
        return nullptr;
    } catch (...) {
        LOG_ERROR(kRegistry, "tsduck_registry_create unknown exception");
        return nullptr;
    }
}

TSDUCK_API void tsduck_registry_destroy(ChannelRegistryHandle registry) {
    auto* impl = toImpl(registry);
    LOG_DEBUG(kRegistry, "tsduck_registry_destroy handle=%p", static_cast<void*>(impl));
    delete impl;
}
// NOLINTEND(cppcoreguidelines-owning-memory)

TSDUCK_API int32_t tsduck_registry_add_provider(
    ChannelRegistryHandle registry,
    const RegistryProviderInfoNative* info)
{
    auto* impl = toImpl(registry);
    if (impl == nullptr || info == nullptr) {
        LOG_WARNING(kRegistry, "tsduck_registry_add_provider: null handle or info");
        return -1;
    }

    try {
        registry::ProviderInfoNative native_info;
        std::strncpy(native_info.id, info->id, sizeof(native_info.id) - 1);
        native_info.id[sizeof(native_info.id) - 1] = '\0';
        std::strncpy(native_info.name, info->name, sizeof(native_info.name) - 1);
        native_info.name[sizeof(native_info.name) - 1] = '\0';
        std::strncpy(native_info.base_url, info->base_url, sizeof(native_info.base_url) - 1);
        native_info.base_url[sizeof(native_info.base_url) - 1] = '\0';
        std::strncpy(native_info.username, info->username, sizeof(native_info.username) - 1);
        native_info.username[sizeof(native_info.username) - 1] = '\0';
        std::strncpy(native_info.password, info->password, sizeof(native_info.password) - 1);
        native_info.password[sizeof(native_info.password) - 1] = '\0';
        native_info.priority = info->priority;
        native_info.id_hash = info->id_hash;
        native_info.initial_health = info->initial_health;

        int32_t index = impl->add_provider(native_info);
        LOG_INFO(kRegistry, "tsduck_registry_add_provider: '%s' -> index %d",
                 native_info.name, index);
        return index;
    } catch (const std::exception& e) {
        LOG_ERROR(kRegistry, "tsduck_registry_add_provider exception: %s", e.what());
        return -1;
    } catch (...) {
        LOG_ERROR(kRegistry, "tsduck_registry_add_provider unknown exception");
        return -1;
    }
}

TSDUCK_API int32_t tsduck_registry_add_stream(
    ChannelRegistryHandle registry,
    int32_t provider_index,
    int32_t stream_id,
    const char* name,
    const char* icon_url)
{
    auto* impl = toImpl(registry);
    if (impl == nullptr) {
        return -1;
    }

    try {
        return impl->add_stream(provider_index, stream_id, name, icon_url);
    } catch (const std::exception& e) {
        LOG_ERROR(kRegistry, "tsduck_registry_add_stream exception: %s", e.what());
        return -1;
    } catch (...) {
        LOG_ERROR(kRegistry, "tsduck_registry_add_stream unknown exception");
        return -1;
    }
}

TSDUCK_API bool tsduck_registry_build(ChannelRegistryHandle registry) {
    auto* impl = toImpl(registry);
    if (impl == nullptr) {
        return false;
    }

    try {
        bool result = impl->build();
        if (result) {
            registry::RegistryStatsNative stats;
            if (impl->get_stats(&stats)) {
                LOG_INFO(kRegistry, "tsduck_registry_build: %d providers, %d channels, %d streams, %d GUIDs",
                         stats.provider_count, stats.channel_count, stats.stream_count, stats.guid_count);
            }
        }
        return result;
    } catch (const std::exception& e) {
        LOG_ERROR(kRegistry, "tsduck_registry_build exception: %s", e.what());
        return false;
    } catch (...) {
        LOG_ERROR(kRegistry, "tsduck_registry_build unknown exception");
        return false;
    }
}

TSDUCK_API bool tsduck_registry_get_stats(
    ChannelRegistryHandle registry,
    RegistryStatsNative* out_stats)
{
    auto* impl = toImpl(registry);
    if (impl == nullptr || out_stats == nullptr) {
        return false;
    }

    registry::RegistryStatsNative stats;
    if (!impl->get_stats(&stats)) {
        return false;
    }

    out_stats->provider_count = stats.provider_count;
    out_stats->channel_count = stats.channel_count;
    out_stats->stream_count = stats.stream_count;
    out_stats->guid_count = stats.guid_count;
    out_stats->skipped_count = stats.skipped_count;
    return true;
}

// ============================================================================
// Streamer Registry Integration
// ============================================================================

TSDUCK_API int32_t tsduck_streamer_set_registry(
    TsDuckStreamerHandle streamer,
    ChannelRegistryHandle registry)
{
    auto* impl = toImpl(streamer);
    if (impl == nullptr) {
        LOG_WARNING(kStreamer, "tsduck_streamer_set_registry: null streamer handle");
        return TSDUCK_ERROR_NULL_HANDLE;
    }

    auto* reg_impl = toImpl(registry);
    impl->set_registry(reg_impl);
    LOG_DEBUG(kStreamer, "tsduck_streamer_set_registry: registry=%p", static_cast<void*>(reg_impl));
    return TSDUCK_OK;
}

TSDUCK_API int32_t tsduck_streamer_set_channel_guid(
    TsDuckStreamerHandle streamer,
    int64_t guid_high,
    int64_t guid_low)
{
    auto* impl = toImpl(streamer);
    if (impl == nullptr) {
        LOG_WARNING(kStreamer, "tsduck_streamer_set_channel_guid: null streamer handle");
        return TSDUCK_ERROR_NULL_HANDLE;
    }

    impl->set_channel_guid(guid_high, guid_low);
    LOG_DEBUG(kStreamer, "tsduck_streamer_set_channel_guid: high=0x%016llx low=0x%016llx",
              static_cast<unsigned long long>(guid_high),
              static_cast<unsigned long long>(guid_low));
    return TSDUCK_OK;
}

// ============================================================================
// Provider Health System
// ============================================================================

TSDUCK_API int32_t tsduck_streamer_get_provider_state(
    TsDuckStreamerHandle streamer,
    int32_t provider_index)
{
    auto* impl = toImpl(streamer);
    if (impl == nullptr) {
        LOG_WARNING(kStreamer, "tsduck_streamer_get_provider_state: null streamer handle");
        return -1;
    }

    auto* health_mgr = impl->health_manager();
    if (health_mgr == nullptr) {
        LOG_WARNING(kStreamer, "tsduck_streamer_get_provider_state: null health manager");
        return -1;
    }

    auto state = health_mgr->get_state(provider_index);
    return static_cast<int32_t>(state);
}

TSDUCK_API bool tsduck_streamer_get_provider_health(
    TsDuckStreamerHandle streamer,
    int32_t provider_index,
    ProviderHealthSnapshotNative* out_snapshot)
{
    auto* impl = toImpl(streamer);
    if (impl == nullptr || out_snapshot == nullptr) {
        LOG_WARNING(kStreamer, "tsduck_streamer_get_provider_health: null handle or output");
        return false;
    }

    auto* health_mgr = impl->health_manager();
    if (health_mgr == nullptr) {
        LOG_WARNING(kStreamer, "tsduck_streamer_get_provider_health: null health manager");
        return false;
    }

    auto snapshot = health_mgr->get_snapshot(provider_index);

    out_snapshot->provider_index = snapshot.provider_index;
    out_snapshot->state = static_cast<int32_t>(snapshot.state);
    out_snapshot->success_rate = snapshot.success_rate;
    out_snapshot->latency_ewma_ms = snapshot.latency_ewma_ms;
    out_snapshot->active_requests = snapshot.active_requests;
    out_snapshot->isolated_times = snapshot.isolated_times;
    out_snapshot->isolation_duration_ms = snapshot.isolation_duration_ms;
    out_snapshot->reserved = 0;

    return true;
}

TSDUCK_API int32_t tsduck_streamer_get_isolated_times(
    TsDuckStreamerHandle streamer,
    int32_t provider_index)
{
    auto* impl = toImpl(streamer);
    if (impl == nullptr) {
        LOG_WARNING(kStreamer, "tsduck_streamer_get_isolated_times: null streamer handle");
        return -1;
    }

    auto* health_mgr = impl->health_manager();
    if (health_mgr == nullptr) {
        LOG_WARNING(kStreamer, "tsduck_streamer_get_isolated_times: null health manager");
        return -1;
    }

    auto snapshot = health_mgr->get_snapshot(provider_index);
    return snapshot.isolated_times;
}

TSDUCK_API void tsduck_streamer_run_outlier_detection(
    TsDuckStreamerHandle streamer)
{
    auto* impl = toImpl(streamer);
    if (impl == nullptr) {
        LOG_WARNING(kStreamer, "tsduck_streamer_run_outlier_detection: null streamer handle");
        return;
    }

    auto* health_mgr = impl->health_manager();
    if (health_mgr == nullptr) {
        LOG_WARNING(kStreamer, "tsduck_streamer_run_outlier_detection: null health manager");
        return;
    }

    health_mgr->run_outlier_detection();
    LOG_DEBUG(kStreamer, "tsduck_streamer_run_outlier_detection: executed");
}

TSDUCK_API int32_t tsduck_streamer_get_provider_count(
    TsDuckStreamerHandle streamer)
{
    auto* impl = toImpl(streamer);
    if (impl == nullptr) {
        LOG_WARNING(kStreamer, "tsduck_streamer_get_provider_count: null streamer handle");
        return 0;
    }

    auto* health_mgr = impl->health_manager();
    if (health_mgr == nullptr) {
        LOG_WARNING(kStreamer, "tsduck_streamer_get_provider_count: null health manager");
        return 0;
    }

    return health_mgr->provider_count();
}

TSDUCK_API void tsduck_streamer_reset_all_providers(
    TsDuckStreamerHandle streamer)
{
    auto* impl = toImpl(streamer);
    if (impl == nullptr) {
        LOG_WARNING(kStreamer, "tsduck_streamer_reset_all_providers: null streamer handle");
        return;
    }

    auto* health_mgr = impl->health_manager();
    if (health_mgr == nullptr) {
        LOG_WARNING(kStreamer, "tsduck_streamer_reset_all_providers: null health manager");
        return;
    }

    health_mgr->reset_all();
    LOG_DEBUG(kStreamer, "tsduck_streamer_reset_all_providers: all providers reset to Active");
}

TSDUCK_API void tsduck_streamer_force_eject_provider(
    TsDuckStreamerHandle streamer,
    int32_t provider_index,
    int32_t duration_ms)
{
    auto* impl = toImpl(streamer);
    if (impl == nullptr) {
        LOG_WARNING(kStreamer, "tsduck_streamer_force_eject_provider: null streamer handle");
        return;
    }

    auto* health_mgr = impl->health_manager();
    if (health_mgr == nullptr) {
        LOG_WARNING(kStreamer, "tsduck_streamer_force_eject_provider: null health manager");
        return;
    }

    health_mgr->force_eject(provider_index, duration_ms);
    LOG_DEBUG(kStreamer, "tsduck_streamer_force_eject_provider: provider %d ejected for %d ms",
              provider_index, duration_ms);
}
