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

#include <tsduck.h>
#include <new>

// Include all refactored components
#include "context/context.hpp"
#include "context/analyzer.hpp"
#include "restamping/restamper.hpp"
#include "platform/simd_search.hpp"

using namespace tsduck_interop;

// ============================================================================
// Type Conversion Helpers
// ============================================================================
// The C API uses opaque forward-declared struct pointers (TsDuckContextHandle, etc.)
// We need to cast between these and the actual C++ class pointers.

namespace {

inline context::TsDuckContext* to_impl(TsDuckContextHandle h) {
    return reinterpret_cast<context::TsDuckContext*>(h);
}

inline TsDuckContextHandle to_handle(context::TsDuckContext* p) {
    return reinterpret_cast<TsDuckContextHandle>(p);
}

inline context::TsDuckAnalyzer* to_impl(TsDuckAnalyzerHandle h) {
    return reinterpret_cast<context::TsDuckAnalyzer*>(h);
}

inline TsDuckAnalyzerHandle to_handle(context::TsDuckAnalyzer* p) {
    return reinterpret_cast<TsDuckAnalyzerHandle>(p);
}

}  // namespace

struct TsDuckRestamper {
    restamping::Restamper impl;
    context::TsDuckAnalyzer* analyzer;

    TsDuckRestamper(context::TsDuckAnalyzer* a, const RestampingConfigNative* cfg)
        : impl(&a->av_sync, cfg), analyzer(a) {}
};

// ============================================================================
// Library Initialization
// ============================================================================

TSDUCK_API const char* tsduck_get_version(void) {
    static std::string version = TS_STRINGIFY(TS_VERSION_MAJOR) "."
                                 TS_STRINGIFY(TS_VERSION_MINOR) "-"
                                 TS_STRINGIFY(TS_COMMIT);
    return version.c_str();
}

TSDUCK_API bool tsduck_is_available(void) {
    return true;
}

// ============================================================================
// Context Management
// ============================================================================

TSDUCK_API TsDuckContextHandle tsduck_context_create(void) {
    try {
        return to_handle(new context::TsDuckContext());
    } catch (...) {
        return nullptr;
    }
}

TSDUCK_API void tsduck_context_destroy(TsDuckContextHandle ctx) {
    delete to_impl(ctx);
}

TSDUCK_API bool tsduck_context_is_available(TsDuckContextHandle ctx) {
    auto* impl = to_impl(ctx);
    return impl && impl->isInitialized();
}

// ============================================================================
// Analyzer Lifecycle
// ============================================================================

TSDUCK_API TsDuckAnalyzerHandle tsduck_analyzer_create(
    TsDuckContextHandle ctx,
    const TsDuckConfigNative* config)
{
    auto* ctx_impl = to_impl(ctx);
    if (!ctx_impl) {
        return nullptr;
    }

    try {
        return to_handle(new context::TsDuckAnalyzer(ctx_impl, config));
    } catch (...) {
        return nullptr;
    }
}

TSDUCK_API void tsduck_analyzer_destroy(TsDuckAnalyzerHandle analyzer) {
    delete to_impl(analyzer);
}

TSDUCK_API bool tsduck_analyzer_is_initialized(TsDuckAnalyzerHandle analyzer) {
    return to_impl(analyzer) != nullptr;
}

// ============================================================================
// Data Processing
// ============================================================================

TSDUCK_API int32_t tsduck_analyzer_feed(
    TsDuckAnalyzerHandle analyzer,
    const uint8_t* data,
    int32_t length)
{
    auto* impl = to_impl(analyzer);
    if (!impl || !data || length <= 0) {
        return TSDUCK_ERROR_INVALID_DATA;
    }

    try {
        return impl->feed(data, length);
    } catch (...) {
        return TSDUCK_ERROR_INTERNAL;
    }
}

TSDUCK_API void tsduck_analyzer_reset(TsDuckAnalyzerHandle analyzer) {
    auto* impl = to_impl(analyzer);
    if (impl) {
        impl->reset();
    }
}

// ============================================================================
// Metrics Retrieval
// ============================================================================

TSDUCK_API bool tsduck_analyzer_get_metrics(
    TsDuckAnalyzerHandle analyzer,
    TsDuckMetricsNative* out_metrics)
{
    auto* impl = to_impl(analyzer);
    if (!impl || !out_metrics) {
        return false;
    }

    return impl->getMetrics(out_metrics);
}

TSDUCK_API bool tsduck_analyzer_has_new_metrics(TsDuckAnalyzerHandle analyzer) {
    auto* impl = to_impl(analyzer);
    return impl && impl->has_new_metrics.load(std::memory_order_acquire);
}

// ============================================================================
// PCR Analysis
// ============================================================================

TSDUCK_API bool tsduck_analyzer_get_pcr_analysis(
    TsDuckAnalyzerHandle analyzer,
    PcrAnalysisNative* out_analysis)
{
    auto* impl = to_impl(analyzer);
    if (!impl || !out_analysis) {
        return false;
    }

    return impl->pcr.get(out_analysis);
}

TSDUCK_API bool tsduck_analyzer_set_pcr_jitter_threshold(
    TsDuckAnalyzerHandle analyzer,
    double max_jitter_us)
{
    auto* impl = to_impl(analyzer);
    if (!impl || max_jitter_us < 0) {
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
    auto* impl = to_impl(analyzer);
    if (!impl || !out_analysis) {
        return false;
    }

    return impl->iat.get(out_analysis);
}

TSDUCK_API bool tsduck_analyzer_set_iat_jitter_threshold(
    TsDuckAnalyzerHandle analyzer,
    double max_jitter_us)
{
    auto* impl = to_impl(analyzer);
    if (!impl || max_jitter_us < 0) {
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
    auto* impl = to_impl(analyzer);
    if (!impl || !out_analysis) {
        return false;
    }

    return impl->getBitrateAnalysis(out_analysis);
}

// ============================================================================
// Extended PID Information
// ============================================================================

TSDUCK_API int32_t tsduck_analyzer_get_pid_count(TsDuckAnalyzerHandle analyzer) {
    auto* impl = to_impl(analyzer);
    if (!impl) {
        return TSDUCK_ERROR_NULL_HANDLE;
    }
    return impl->pids.getActiveCount();
}

TSDUCK_API int32_t tsduck_analyzer_get_pid_info_extended(
    TsDuckAnalyzerHandle analyzer,
    TsDuckPidInfoExtended* out_pids,
    int32_t max_pids)
{
    auto* impl = to_impl(analyzer);
    if (!impl) {
        return TSDUCK_ERROR_NULL_HANDLE;
    }
    if (!out_pids || max_pids <= 0) {
        return TSDUCK_ERROR_INVALID_DATA;
    }

    return impl->pids.getCount(out_pids, max_pids);
}

// ============================================================================
// Callbacks
// ============================================================================

TSDUCK_API void tsduck_analyzer_set_metrics_callback(
    TsDuckAnalyzerHandle analyzer,
    TsDuckMetricsCallback callback,
    void* user_data)
{
    auto* impl = to_impl(analyzer);
    if (!impl) {
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
    auto* impl = to_impl(analyzer);
    if (!impl) {
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
    auto* impl = to_impl(analyzer);
    if (!impl || !out_analysis) {
        return false;
    }

    return impl->av_sync.getAnalysis(out_analysis);
}

TSDUCK_API int32_t tsduck_analyzer_get_pts_samples(
    TsDuckAnalyzerHandle analyzer,
    PtsDtsSampleNative* out_samples,
    int32_t max_samples)
{
    auto* impl = to_impl(analyzer);
    if (!impl) {
        return TSDUCK_ERROR_NULL_HANDLE;
    }
    if (!out_samples || max_samples <= 0) {
        return TSDUCK_ERROR_INVALID_DATA;
    }

    return impl->av_sync.getSamples(out_samples, max_samples);
}

TSDUCK_API int32_t tsduck_analyzer_get_pts_sample_count(TsDuckAnalyzerHandle analyzer) {
    auto* impl = to_impl(analyzer);
    if (!impl) {
        return TSDUCK_ERROR_NULL_HANDLE;
    }

    return impl->av_sync.getSampleCount();
}

// ============================================================================
// Restamper Lifecycle
// ============================================================================

TSDUCK_API TsDuckRestamperHandle tsduck_restamper_create(
    TsDuckAnalyzerHandle analyzer,
    const RestampingConfigNative* config)
{
    auto* impl = to_impl(analyzer);
    if (!impl) {
        return nullptr;
    }

    try {
        return new TsDuckRestamper(impl, config);
    } catch (...) {
        return nullptr;
    }
}

TSDUCK_API void tsduck_restamper_destroy(TsDuckRestamperHandle restamper) {
    delete restamper;
}

TSDUCK_API bool tsduck_restamper_is_initialized(TsDuckRestamperHandle restamper) {
    return restamper != nullptr;
}

TSDUCK_API bool tsduck_restamper_configure(
    TsDuckRestamperHandle restamper,
    const RestampingConfigNative* config)
{
    if (!restamper || !config) {
        return false;
    }

    restamper->impl.configure(config);
    return true;
}

// ============================================================================
// Restamper Data Processing
// ============================================================================

TSDUCK_API int32_t tsduck_restamper_process(
    TsDuckRestamperHandle restamper,
    uint8_t* data,
    int32_t length)
{
    if (!restamper || !data || length <= 0) {
        return TSDUCK_ERROR_INVALID_DATA;
    }

    int64_t base_packet_idx = restamper->analyzer->packets_processed.load(
        std::memory_order_relaxed);

    return restamper->impl.process(data, length, base_packet_idx);
}

TSDUCK_API void tsduck_restamper_handle_switch(
    TsDuckRestamperHandle restamper,
    int64_t last_output_pts,
    int64_t new_input_first_pts)
{
    if (!restamper) {
        return;
    }

    restamper->impl.handleSwitch(last_output_pts, new_input_first_pts);
}

TSDUCK_API bool tsduck_restamper_get_statistics(
    TsDuckRestamperHandle restamper,
    RestampingStatisticsNative* out_stats)
{
    if (!restamper || !out_stats) {
        return false;
    }

    return restamper->impl.getStatistics(out_stats);
}

TSDUCK_API void tsduck_restamper_reset(TsDuckRestamperHandle restamper) {
    if (restamper) {
        restamper->impl.reset();
    }
}

TSDUCK_API void tsduck_restamper_set_correction_callback(
    TsDuckRestamperHandle restamper,
    TsDuckCorrectionCallback callback,
    void* user_data)
{
    if (!restamper) {
        return;
    }

    restamper->impl.correction_user_data.store(user_data, std::memory_order_release);
    restamper->impl.correction_callback.store(callback, std::memory_order_release);
}
