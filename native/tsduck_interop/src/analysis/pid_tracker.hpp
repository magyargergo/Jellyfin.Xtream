// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_ANALYSIS_PID_TRACKER_HPP
#define TSDUCK_INTEROP_ANALYSIS_PID_TRACKER_HPP

#include <atomic>
#include <array>
#include <cstdint>
#include "../core/constants.hpp"
#include "../concurrency/seqlock.hpp"
#include "tsduck_interop.h"

namespace tsduck_interop {
namespace analysis {

// Per-PID tracking with seqlock for lock-free access
struct alignas(CACHE_LINE_SIZE) PidSlot {
    concurrency::Seqlock seqlock;

    // Data protected by seqlock
    std::atomic<int64_t> packets{0};
    std::atomic<int64_t> continuity_errors{0};
    std::atomic<int64_t> duplicate_packets{0};
    std::atomic<int64_t> scrambled_packets{0};
    std::atomic<int32_t> last_cc{-1};
    std::atomic<bool> is_active{false};
    std::atomic<bool> is_scrambled{false};
    std::atomic<bool> is_pcr_pid{false};
    std::atomic<bool> is_video{false};
    std::atomic<bool> is_audio{false};
    std::atomic<int32_t> stream_type{0};
    std::atomic<int64_t> first_seen_ns{0};
    std::atomic<int64_t> last_seen_ns{0};
    double pcr_jitter_us{0.0};  // Updated atomically via seqlock

    void reset() noexcept {
        auto seq = seqlock.begin_write();
        packets.store(0, std::memory_order_relaxed);
        continuity_errors.store(0, std::memory_order_relaxed);
        duplicate_packets.store(0, std::memory_order_relaxed);
        scrambled_packets.store(0, std::memory_order_relaxed);
        last_cc.store(-1, std::memory_order_relaxed);
        is_active.store(false, std::memory_order_relaxed);
        is_scrambled.store(false, std::memory_order_relaxed);
        is_pcr_pid.store(false, std::memory_order_relaxed);
        is_video.store(false, std::memory_order_relaxed);
        is_audio.store(false, std::memory_order_relaxed);
        stream_type.store(0, std::memory_order_relaxed);
        first_seen_ns.store(0, std::memory_order_relaxed);
        last_seen_ns.store(0, std::memory_order_relaxed);
        pcr_jitter_us = 0.0;
        seqlock.end_write(seq);
    }
};

class PidTracker {
public:
    alignas(CACHE_LINE_SIZE) std::array<PidSlot, MAX_PIDS> slots;
    alignas(CACHE_LINE_SIZE) std::atomic<int32_t> active_count{0};

    // Note: CC errors are now tracked by TSDuck's ContinuityAnalyzer
    // This tracker focuses on per-PID statistics that TSDuck doesn't provide
    void processPacket(uint16_t pid, bool scrambled, int64_t time_ns) noexcept {
        auto& slot = slots[pid];

        // Check if first time seeing this PID
        if (!slot.is_active.load(std::memory_order_relaxed)) {
            slot.is_active.store(true, std::memory_order_release);
            slot.first_seen_ns.store(time_ns, std::memory_order_release);
            active_count.fetch_add(1, std::memory_order_relaxed);
        }

        slot.packets.fetch_add(1, std::memory_order_relaxed);
        slot.last_seen_ns.store(time_ns, std::memory_order_release);

        // Track scrambled packets
        if (scrambled) {
            slot.scrambled_packets.fetch_add(1, std::memory_order_relaxed);
            slot.is_scrambled.store(true, std::memory_order_release);
        } else {
            slot.is_scrambled.store(false, std::memory_order_release);
        }
    }

    void setStreamType(uint16_t pid, int32_t type, bool video, bool audio) noexcept {
        auto& slot = slots[pid];
        slot.stream_type.store(type, std::memory_order_release);
        slot.is_video.store(video, std::memory_order_release);
        slot.is_audio.store(audio, std::memory_order_release);
    }

    void setPcrPid(uint16_t pid, bool is_pcr) noexcept {
        slots[pid].is_pcr_pid.store(is_pcr, std::memory_order_release);
    }

    void setPcrJitter(uint16_t pid, double jitter_us) noexcept {
        auto& slot = slots[pid];
        auto seq = slot.seqlock.begin_write();
        slot.pcr_jitter_us = jitter_us;
        slot.seqlock.end_write(seq);
    }

    int32_t getCount(TsDuckPidInfoExtended* out_pids, int32_t max_pids) const noexcept {
        if (!out_pids || max_pids <= 0) return 0;

        int32_t count = 0;
        for (size_t pid = 0; pid < MAX_PIDS && count < max_pids; ++pid) {
            const auto& slot = slots[pid];

            if (!slot.is_active.load(std::memory_order_acquire)) {
                continue;
            }

            auto& out = out_pids[count];
            out.pid = static_cast<int32_t>(pid);
            out.stream_type = slot.stream_type.load(std::memory_order_relaxed);
            out.packets = slot.packets.load(std::memory_order_relaxed);

            // Calculate bitrate
            int64_t first = slot.first_seen_ns.load(std::memory_order_relaxed);
            int64_t last = slot.last_seen_ns.load(std::memory_order_relaxed);
            int64_t duration_ms = (last - first) / 1000000;
            if (duration_ms > 0) {
                out.bitrate = (out.packets * TS_PACKET_SIZE * 8 * 1000) / duration_ms;
            } else {
                out.bitrate = 0;
            }

            out.continuity_errors = slot.continuity_errors.load(std::memory_order_relaxed);
            out.duplicate_packets = slot.duplicate_packets.load(std::memory_order_relaxed);
            out.scrambled_packets = slot.scrambled_packets.load(std::memory_order_relaxed);
            out.is_scrambled = slot.is_scrambled.load(std::memory_order_relaxed) ? 1 : 0;
            out.is_pcr_pid = slot.is_pcr_pid.load(std::memory_order_relaxed) ? 1 : 0;

            // Read PCR jitter with seqlock
            uint64_t seq;
            do {
                seq = slot.seqlock.begin_read();
                out.pcr_jitter_us = slot.pcr_jitter_us;
            } while (!slot.seqlock.read_consistent(seq));

            out.is_video = slot.is_video.load(std::memory_order_relaxed) ? 1 : 0;
            out.is_audio = slot.is_audio.load(std::memory_order_relaxed) ? 1 : 0;

            count++;
        }

        return count;
    }

    int32_t getActiveCount() const noexcept {
        return active_count.load(std::memory_order_acquire);
    }

    void reset() noexcept {
        for (auto& slot : slots) {
            slot.reset();
        }
        active_count.store(0, std::memory_order_release);
    }
};

}  // namespace analysis
}  // namespace tsduck_interop

#endif  // TSDUCK_INTEROP_ANALYSIS_PID_TRACKER_HPP
