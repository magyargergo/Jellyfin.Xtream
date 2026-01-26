// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_ANALYSIS_AV_SYNC_TRACKER_HPP
#define TSDUCK_INTEROP_ANALYSIS_AV_SYNC_TRACKER_HPP

#include <atomic>
#include <array>
#include <cstdint>
#include <cmath>
#include <algorithm>
#include <chrono>
#include <cstring>
#include "../core/constants.hpp"
#include "../core/types.hpp"
#include "../concurrency/seqlock.hpp"
#include "tsduck_interop.h"

namespace tsduck_interop::analysis {

class alignas(CACHE_LINE_SIZE) AvSyncTracker {
public:
    // Lock-free atomic state
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> last_video_pts{-1};
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> last_audio_pts{-1};
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> last_pcr_base{-1};
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> video_pts_count{0};
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> audio_pts_count{0};
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> video_discontinuities{0};
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> audio_discontinuities{0};

    // Previous PTS for discontinuity detection
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> prev_video_pts{-1};
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> prev_audio_pts{-1};

    // Seqlock-protected compound data
    concurrency::Seqlock analysis_seqlock;
    AvSyncAnalysisNative analysis{};

    // PTS sample ring buffer
    concurrency::Seqlock samples_seqlock;
    std::array<PtsSample, PTS_SAMPLE_WINDOW> pts_samples{};
    std::atomic<size_t> sample_write_idx{0};
    std::atomic<size_t> sample_count{0};

    // Drift history for trend analysis
    concurrency::Seqlock drift_seqlock;
    std::array<DriftSample, DRIFT_SAMPLE_WINDOW> drift_history{};
    std::atomic<size_t> drift_write_idx{0};
    std::atomic<size_t> drift_count{0};
    double drift_sum{0.0};

    // Matched A/V pair tracking
    concurrency::Seqlock matched_seqlock;
    std::array<MatchedAvPair, MATCHED_SAMPLE_WINDOW> matched_pairs{};
    std::atomic<size_t> matched_write_idx{0};
    std::atomic<size_t> matched_count{0};
    double matched_drift_sum{0.0};

    // Recent timestamps for interpolation
    concurrency::Seqlock recent_video_seqlock;
    std::array<RecentTimestamp, 8> recent_video{};
    std::atomic<size_t> recent_video_idx{0};
    std::atomic<size_t> recent_video_count{0};

    concurrency::Seqlock recent_audio_seqlock;
    std::array<RecentTimestamp, 8> recent_audio{};
    std::atomic<size_t> recent_audio_idx{0};
    std::atomic<size_t> recent_audio_count{0};

    // PCR history
    concurrency::Seqlock pcr_history_seqlock;
    std::array<PcrHistoryEntry, PCR_HISTORY_SIZE> pcr_history{};
    std::atomic<size_t> pcr_history_idx{0};
    std::atomic<size_t> pcr_history_count{0};
    std::atomic<int64_t> estimated_bitrate_bps{0};

    int64_t start_time_ns{0};

    AvSyncTracker() {
        start_time_ns =
            std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch())
                .count();
    }

    void record_pts_sample(int32_t pid, int32_t stream_type, int64_t pts, int64_t dts, int64_t packet_idx,
                         int64_t byte_off, bool is_video, bool is_audio, bool is_keyframe) noexcept {
        auto now_ns =
            std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch())
                .count();

        int64_t pcr_ref = last_pcr_base.load(std::memory_order_acquire);

        // Check for discontinuity and update counters
        if (is_video) {
            int64_t prev = prev_video_pts.load(std::memory_order_relaxed);
            if (prev > 0) {
                int64_t delta = pts_diff(pts, prev);
                if (delta > PTS_DISCONTINUITY_THRESHOLD || delta < -PTS_BACKWARD_THRESHOLD) {
                    video_discontinuities.fetch_add(1, std::memory_order_relaxed);
                }
            }
            prev_video_pts.store(pts, std::memory_order_release);
            last_video_pts.store(pts, std::memory_order_release);
            video_pts_count.fetch_add(1, std::memory_order_relaxed);

            store_recent_video(pts, dts, now_ns, packet_idx);
            try_match_av_pair(pts, dts, is_keyframe, now_ns, true);
        }

        if (is_audio) {
            int64_t prev = prev_audio_pts.load(std::memory_order_relaxed);
            if (prev > 0) {
                int64_t delta = pts_diff(pts, prev);
                if (delta > PTS_DISCONTINUITY_THRESHOLD || delta < -PTS_BACKWARD_THRESHOLD) {
                    audio_discontinuities.fetch_add(1, std::memory_order_relaxed);
                }
            }
            prev_audio_pts.store(pts, std::memory_order_release);
            last_audio_pts.store(pts, std::memory_order_release);
            audio_pts_count.fetch_add(1, std::memory_order_relaxed);

            store_recent_audio(pts, dts, now_ns, packet_idx);
            try_match_av_pair(pts, dts, false, now_ns, false);
        }

        // Store sample in ring buffer
        auto seq = samples_seqlock.begin_write();
        size_t idx = sample_write_idx.load(std::memory_order_relaxed);
        auto& sample = pts_samples[idx & (PTS_SAMPLE_WINDOW - 1)];
        sample.pid = pid;
        sample.stream_type = stream_type;
        sample.pts_90khz = pts;
        sample.dts_90khz = dts;
        sample.pcr_ref_90khz = pcr_ref;
        sample.packet_index = packet_idx;
        sample.byte_offset = byte_off;
        sample.timestamp_ns = now_ns;
        sample.is_video = is_video;
        sample.is_audio = is_audio;
        sample.is_keyframe = is_keyframe;
        sample_write_idx.store(idx + 1, std::memory_order_release);

        size_t count = sample_count.load(std::memory_order_relaxed);
        if (count < PTS_SAMPLE_WINDOW) {
            sample_count.store(count + 1, std::memory_order_release);
        }
        samples_seqlock.end_write(seq);

        update_drift_from_matched_pairs(now_ns);
    }

    void update_pcr_reference(int64_t pcr_base) noexcept {
        last_pcr_base.store(pcr_base, std::memory_order_release);

        auto seq = analysis_seqlock.begin_write();
        analysis.pcr_count++;
        analysis_seqlock.end_write(seq);
    }

    bool get_analysis(AvSyncAnalysisNative* out) const noexcept {
        if (!out)
            return false;

        uint64_t seq;
        do {
            seq = analysis_seqlock.begin_read();
            *out = analysis;
        } while (!analysis_seqlock.read_consistent(seq));

        return out->video_pts_count > 0 || out->audio_pts_count > 0;
    }

    int32_t get_samples(PtsDtsSampleNative* out, int32_t max_samples) const noexcept {
        if (!out || max_samples <= 0)
            return 0;

        uint64_t seq;
        int32_t count;
        do {
            seq = samples_seqlock.begin_read();
            size_t available = sample_count.load(std::memory_order_acquire);
            count = static_cast<int32_t>(std::min(static_cast<size_t>(max_samples), available));

            size_t write_idx = sample_write_idx.load(std::memory_order_acquire);

            for (int32_t i = 0; i < count; i++) {
                size_t src_idx = (write_idx - count + i) & (PTS_SAMPLE_WINDOW - 1);
                const auto& src = pts_samples[src_idx];
                auto& dst = out[i];

                dst.pid = src.pid;
                dst.stream_type = src.stream_type;
                dst.pts_90khz = src.pts_90khz;
                dst.dts_90khz = src.dts_90khz;
                dst.pcr_90khz = src.pcr_ref_90khz;
                dst.packet_index = src.packet_index;
                dst.byte_offset = src.byte_offset;
                dst.is_video = src.is_video ? 1 : 0;
                dst.is_audio = src.is_audio ? 1 : 0;
                dst.is_keyframe = src.is_keyframe ? 1 : 0;
                dst.reserved = 0;
            }
        } while (!samples_seqlock.read_consistent(seq));

        return count;
    }

    int32_t get_sample_count() const noexcept {
        return static_cast<int32_t>(sample_count.load(std::memory_order_acquire));
    }

    void reset() noexcept {
        last_video_pts.store(-1, std::memory_order_release);
        last_audio_pts.store(-1, std::memory_order_release);
        last_pcr_base.store(-1, std::memory_order_release);
        prev_video_pts.store(-1, std::memory_order_release);
        prev_audio_pts.store(-1, std::memory_order_release);
        video_pts_count.store(0, std::memory_order_release);
        audio_pts_count.store(0, std::memory_order_release);
        video_discontinuities.store(0, std::memory_order_release);
        audio_discontinuities.store(0, std::memory_order_release);
        sample_write_idx.store(0, std::memory_order_release);
        sample_count.store(0, std::memory_order_release);
        drift_write_idx.store(0, std::memory_order_release);
        drift_count.store(0, std::memory_order_release);
        drift_sum = 0.0;
        matched_write_idx.store(0, std::memory_order_release);
        matched_count.store(0, std::memory_order_release);
        matched_drift_sum = 0.0;
        recent_video_idx.store(0, std::memory_order_release);
        recent_video_count.store(0, std::memory_order_release);
        recent_audio_idx.store(0, std::memory_order_release);
        recent_audio_count.store(0, std::memory_order_release);
        pcr_history_idx.store(0, std::memory_order_release);
        pcr_history_count.store(0, std::memory_order_release);
        estimated_bitrate_bps.store(0, std::memory_order_release);

        auto seq = analysis_seqlock.begin_write();
        // Use aggregate initialization instead of memset (portability: memset on floats)
        analysis = AvSyncAnalysisNative{};
        analysis_seqlock.end_write(seq);

        start_time_ns =
            std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch())
                .count();
    }

private:
    /// 33-bit PTS/DTS wraparound-aware subtraction: (a - b) with wrap handling.
    static int64_t pts_diff(int64_t pts_a, int64_t pts_b) noexcept {
        int64_t diff = pts_a - pts_b;
        if (diff > PTS_33BIT_MAX / 2) {
            diff -= PTS_33BIT_MAX + 1;
        } else if (diff < -static_cast<int64_t>(PTS_33BIT_MAX) / 2) {
            diff += PTS_33BIT_MAX + 1;
        }
        return diff;
    }

    void store_recent_video(int64_t pts, int64_t dts, int64_t now_ns, int64_t packet_idx) noexcept {
        auto v_seq = recent_video_seqlock.begin_write();
        size_t v_idx = recent_video_idx.load(std::memory_order_relaxed);
        auto& v_sample = recent_video[v_idx & 7];
        v_sample.pts_90khz = pts;
        v_sample.dts_90khz = dts;
        v_sample.wall_time_ns = now_ns;
        v_sample.packet_index = packet_idx;
        recent_video_idx.store(v_idx + 1, std::memory_order_release);
        size_t v_count = recent_video_count.load(std::memory_order_relaxed);
        if (v_count < 8) {
            recent_video_count.store(v_count + 1, std::memory_order_release);
        }
        recent_video_seqlock.end_write(v_seq);
    }

    void store_recent_audio(int64_t pts, int64_t dts, int64_t now_ns, int64_t packet_idx) noexcept {
        auto a_seq = recent_audio_seqlock.begin_write();
        size_t a_idx = recent_audio_idx.load(std::memory_order_relaxed);
        auto& a_sample = recent_audio[a_idx & 7];
        a_sample.pts_90khz = pts;
        a_sample.dts_90khz = dts;
        a_sample.wall_time_ns = now_ns;
        a_sample.packet_index = packet_idx;
        recent_audio_idx.store(a_idx + 1, std::memory_order_release);
        size_t a_count = recent_audio_count.load(std::memory_order_relaxed);
        if (a_count < 8) {
            recent_audio_count.store(a_count + 1, std::memory_order_release);
        }
        recent_audio_seqlock.end_write(a_seq);
    }

    void try_match_av_pair(int64_t current_pts, int64_t current_dts, bool is_keyframe, int64_t now_ns,
                        bool is_video_sample) noexcept {
        if (is_video_sample) {
            match_with_audio(current_pts, current_dts, is_keyframe, now_ns);
        } else {
            match_with_video(current_pts, current_dts, now_ns);
        }
    }

    void match_with_audio(int64_t video_pts, int64_t video_dts, bool is_keyframe, int64_t now_ns) noexcept {
        uint64_t a_seq;
        do {
            a_seq = recent_audio_seqlock.begin_read();
            size_t a_count = recent_audio_count.load(std::memory_order_acquire);
            if (a_count == 0)
                break;

            size_t a_write = recent_audio_idx.load(std::memory_order_acquire);
            int64_t best_audio_pts = -1;
            int64_t best_diff = INT64_MAX;

            for (size_t i = 0; i < std::min(a_count, size_t{8}); i++) {
                const auto& a = recent_audio[(a_write - 1 - i) & 7];
                int64_t diff = std::abs(pts_diff(video_pts, a.pts_90khz));
                if (diff < best_diff) {
                    best_diff = diff;
                    best_audio_pts = a.pts_90khz;
                }
            }

            if (best_audio_pts >= 0 && best_diff <= PTS_MATCH_TOLERANCE_90KHZ) {
                int64_t drift_90khz = pts_diff(best_audio_pts, video_pts);
                double drift_ms = static_cast<double>(drift_90khz) / 90.0;

                if (std::abs(drift_ms) <= DRIFT_OUTLIER_THRESHOLD_MS) {
                    record_matched_pair(video_pts, video_dts, best_audio_pts, drift_ms, now_ns, is_keyframe);
                }
            }
        } while (!recent_audio_seqlock.read_consistent(a_seq));
    }

    void match_with_video(int64_t audio_pts, int64_t audio_dts, int64_t now_ns) noexcept {
        uint64_t v_seq;
        do {
            v_seq = recent_video_seqlock.begin_read();
            size_t v_count = recent_video_count.load(std::memory_order_acquire);
            if (v_count == 0)
                break;

            size_t v_write = recent_video_idx.load(std::memory_order_acquire);
            int64_t best_video_pts = -1;
            int64_t best_video_dts = -1;
            int64_t best_diff = INT64_MAX;

            for (size_t i = 0; i < std::min(v_count, size_t{8}); i++) {
                const auto& v = recent_video[(v_write - 1 - i) & 7];
                int64_t diff = std::abs(pts_diff(audio_pts, v.pts_90khz));
                if (diff < best_diff) {
                    best_diff = diff;
                    best_video_pts = v.pts_90khz;
                    best_video_dts = v.dts_90khz;
                }
            }

            if (best_video_pts >= 0 && best_diff <= PTS_MATCH_TOLERANCE_90KHZ) {
                int64_t drift_90khz = pts_diff(audio_pts, best_video_pts);
                double drift_ms = static_cast<double>(drift_90khz) / 90.0;

                if (std::abs(drift_ms) <= DRIFT_OUTLIER_THRESHOLD_MS) {
                    record_matched_pair(best_video_pts, best_video_dts, audio_pts, drift_ms, now_ns, false);
                }
            }
        } while (!recent_video_seqlock.read_consistent(v_seq));

        (void)audio_dts;  // Unused for audio matching
    }

    void record_matched_pair(int64_t video_pts, int64_t video_dts, int64_t audio_pts, double drift_ms, int64_t now_ns,
                           bool is_keyframe) noexcept {
        auto seq = matched_seqlock.begin_write();

        size_t idx = matched_write_idx.load(std::memory_order_relaxed);
        auto& pair = matched_pairs[idx & (MATCHED_SAMPLE_WINDOW - 1)];

        size_t count = matched_count.load(std::memory_order_relaxed);
        if (count >= MATCHED_SAMPLE_WINDOW) {
            size_t old_idx = idx - MATCHED_SAMPLE_WINDOW;
            matched_drift_sum -= matched_pairs[old_idx & (MATCHED_SAMPLE_WINDOW - 1)].drift_ms;
        }

        pair.video_pts_90khz = video_pts;
        pair.video_dts_90khz = video_dts;
        pair.audio_pts_90khz = audio_pts;
        pair.reference_pts_90khz = video_pts;
        pair.drift_ms = drift_ms;
        pair.timestamp_ns = now_ns;
        pair.video_is_keyframe = is_keyframe;

        matched_drift_sum += drift_ms;

        matched_write_idx.store(idx + 1, std::memory_order_release);
        if (count < MATCHED_SAMPLE_WINDOW) {
            matched_count.store(count + 1, std::memory_order_release);
        }

        matched_seqlock.end_write(seq);
    }

    void update_drift_from_matched_pairs(int64_t now_ns) noexcept {
        size_t m_count = matched_count.load(std::memory_order_acquire);

        if (m_count < 3) {
            update_drift_simple(now_ns);
            return;
        }

        uint64_t m_seq;
        double current_drift_ms = 0.0;
        double avg_drift_ms = 0.0;
        double drift_rate = 0.0;

        do {
            m_seq = matched_seqlock.begin_read();
            size_t count = matched_count.load(std::memory_order_acquire);
            if (count == 0)
                break;

            avg_drift_ms = matched_drift_sum / static_cast<double>(count);

            size_t write_idx = matched_write_idx.load(std::memory_order_acquire);
            current_drift_ms = matched_pairs[(write_idx - 1) & (MATCHED_SAMPLE_WINDOW - 1)].drift_ms;

            // Linear regression for drift rate
            if (count >= 5) {
                double sum_x = 0, sum_y = 0, sum_xy = 0, sum_xx = 0;
                size_t n = std::min(count, MATCHED_SAMPLE_WINDOW);

                for (size_t i = 0; i < n; i++) {
                    const auto& p = matched_pairs[(write_idx - 1 - i) & (MATCHED_SAMPLE_WINDOW - 1)];
                    double x = static_cast<double>(p.timestamp_ns - start_time_ns) / 1e9;
                    double y = p.drift_ms;
                    sum_x += x;
                    sum_y += y;
                    sum_xy += x * y;
                    sum_xx += x * x;
                }

                const auto n_d = static_cast<double>(n);
                double denom = n_d * sum_xx - sum_x * sum_x;
                if (std::abs(denom) > 1e-9) {
                    drift_rate = (n_d * sum_xy - sum_x * sum_y) / denom;
                }
            }
        } while (!matched_seqlock.read_consistent(m_seq));

        double elapsed_sec = static_cast<double>(now_ns - start_time_ns) / 1e9;
        record_drift_sample(current_drift_ms, elapsed_sec);
        update_analysis_from_drift(current_drift_ms, avg_drift_ms, drift_rate, elapsed_sec);
    }

    void update_drift_simple(int64_t now_ns) noexcept {
        int64_t video_pts = last_video_pts.load(std::memory_order_acquire);
        int64_t audio_pts = last_audio_pts.load(std::memory_order_acquire);

        if (video_pts < 0 || audio_pts < 0) {
            return;
        }

        int64_t pts_delta = pts_diff(audio_pts, video_pts);
        double drift_ms = static_cast<double>(pts_delta) / 90.0;

        if (std::abs(drift_ms) > DRIFT_OUTLIER_THRESHOLD_MS) {
            return;
        }

        double elapsed_sec = static_cast<double>(now_ns - start_time_ns) / 1e9;
        record_drift_sample(drift_ms, elapsed_sec);
        update_analysis_from_drift(drift_ms, drift_ms, 0.0, elapsed_sec);
    }

    void record_drift_sample(double drift_ms, double elapsed_sec) noexcept {
        auto seq = drift_seqlock.begin_write();

        size_t idx = drift_write_idx.load(std::memory_order_relaxed);
        auto& sample = drift_history[idx & (DRIFT_SAMPLE_WINDOW - 1)];

        size_t count = drift_count.load(std::memory_order_relaxed);
        if (count >= DRIFT_SAMPLE_WINDOW) {
            size_t old_idx = idx - DRIFT_SAMPLE_WINDOW;
            drift_sum -= drift_history[old_idx & (DRIFT_SAMPLE_WINDOW - 1)].drift_ms;
        }

        sample.drift_ms = drift_ms;
        sample.elapsed_sec = elapsed_sec;
        drift_sum += drift_ms;

        drift_write_idx.store(idx + 1, std::memory_order_release);
        if (count < DRIFT_SAMPLE_WINDOW) {
            drift_count.store(count + 1, std::memory_order_release);
        }

        drift_seqlock.end_write(seq);
    }

    void update_analysis_from_drift(double current_drift_ms, double avg_drift_ms, double drift_rate,
                                 double elapsed_sec) noexcept {
        (void)elapsed_sec;  // Not currently used

        auto seq = analysis_seqlock.begin_write();

        analysis.video_audio_drift_ms = current_drift_ms;
        analysis.avg_drift_ms = avg_drift_ms;
        analysis.drift_rate_ms_per_sec = drift_rate;

        double abs_drift = std::abs(current_drift_ms);
        if (abs_drift > std::abs(analysis.peak_drift_ms)) {
            analysis.peak_drift_ms = current_drift_ms;
        }

        analysis.video_pts_count = video_pts_count.load(std::memory_order_relaxed);
        analysis.audio_pts_count = audio_pts_count.load(std::memory_order_relaxed);
        analysis.video_discontinuities = video_discontinuities.load(std::memory_order_relaxed);
        analysis.audio_discontinuities = audio_discontinuities.load(std::memory_order_relaxed);
        analysis.last_video_pts = last_video_pts.load(std::memory_order_relaxed);
        analysis.last_audio_pts = last_audio_pts.load(std::memory_order_relaxed);
        analysis.last_pcr = last_pcr_base.load(std::memory_order_relaxed);

        int64_t pcr = analysis.last_pcr;
        if (pcr >= 0) {
            int64_t v_pts = analysis.last_video_pts;
            int64_t a_pts = analysis.last_audio_pts;

            if (v_pts >= 0) {
                analysis.pcr_video_offset_ms = static_cast<double>(pts_diff(v_pts, pcr)) / 90.0;
            }
            if (a_pts >= 0) {
                analysis.pcr_audio_offset_ms = static_cast<double>(pts_diff(a_pts, pcr)) / 90.0;
            }
        }

        // Determine sync status
        if (analysis.video_pts_count == 0) {
            analysis.sync_status = AVSYNC_STATUS_NO_VIDEO;
        } else if (analysis.audio_pts_count == 0) {
            analysis.sync_status = AVSYNC_STATUS_NO_AUDIO;
        } else if (abs_drift <= SYNC_THRESHOLD_MS) {
            analysis.sync_status = AVSYNC_STATUS_SYNCHRONIZED;
        } else if (abs_drift <= DESYNC_THRESHOLD_MS) {
            analysis.sync_status = AVSYNC_STATUS_DRIFTING;
        } else {
            analysis.sync_status = AVSYNC_STATUS_DESYNC;
        }

        analysis_seqlock.end_write(seq);
    }
};

}  // namespace tsduck_interop::analysis

#endif  // TSDUCK_INTEROP_ANALYSIS_AV_SYNC_TRACKER_HPP
