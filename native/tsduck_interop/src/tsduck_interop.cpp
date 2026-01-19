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
#include <atomic>
#include <chrono>
#include <cstring>
#include <cmath>
#include <algorithm>
#include <array>
#include <new>

// ============================================================================
// Lock-Free Primitives
// ============================================================================

// Cache line size for avoiding false sharing
#ifdef __cpp_lib_hardware_interference_size
    constexpr size_t CACHE_LINE_SIZE = std::hardware_destructive_interference_size;
#else
    constexpr size_t CACHE_LINE_SIZE = 64;
#endif

// Seqlock for lock-free read of compound data structures
// Writer: increment seq (odd), write data, increment seq (even)
// Reader: read seq, read data, check seq unchanged and even
struct alignas(CACHE_LINE_SIZE) Seqlock {
    std::atomic<uint64_t> sequence{0};

    // Begin write - returns sequence to use for end_write
    uint64_t begin_write() noexcept {
        uint64_t seq = sequence.load(std::memory_order_relaxed);
        sequence.store(seq + 1, std::memory_order_release);  // Make odd
        std::atomic_thread_fence(std::memory_order_release);
        return seq + 2;  // Expected final value
    }

    // End write
    void end_write(uint64_t expected_seq) noexcept {
        std::atomic_thread_fence(std::memory_order_release);
        sequence.store(expected_seq, std::memory_order_release);  // Make even
    }

    // Begin read - returns sequence for consistency check
    uint64_t begin_read() const noexcept {
        uint64_t seq;
        do {
            seq = sequence.load(std::memory_order_acquire);
        } while (seq & 1);  // Wait if writer is active (odd)
        std::atomic_thread_fence(std::memory_order_acquire);
        return seq;
    }

    // Check if read is consistent
    bool read_consistent(uint64_t start_seq) const noexcept {
        std::atomic_thread_fence(std::memory_order_acquire);
        return sequence.load(std::memory_order_acquire) == start_seq;
    }
};

// Lock-free ring buffer for IAT samples (SPSC - single producer single consumer)
template<typename T, size_t Capacity>
class alignas(CACHE_LINE_SIZE) LockFreeRingBuffer {
    static_assert((Capacity & (Capacity - 1)) == 0, "Capacity must be power of 2");

    alignas(CACHE_LINE_SIZE) std::array<T, Capacity> buffer_{};
    alignas(CACHE_LINE_SIZE) std::atomic<size_t> write_idx_{0};
    alignas(CACHE_LINE_SIZE) std::atomic<size_t> size_{0};

public:
    void push(T value) noexcept {
        size_t idx = write_idx_.load(std::memory_order_relaxed);
        buffer_[idx & (Capacity - 1)] = value;
        write_idx_.store(idx + 1, std::memory_order_release);

        size_t current_size = size_.load(std::memory_order_relaxed);
        if (current_size < Capacity) {
            size_.store(current_size + 1, std::memory_order_release);
        }
    }

    size_t size() const noexcept {
        return size_.load(std::memory_order_acquire);
    }

    // Get min/max from buffer (approximate - may be slightly stale)
    std::pair<T, T> get_min_max() const noexcept {
        size_t sz = size_.load(std::memory_order_acquire);
        if (sz == 0) return {T{}, T{}};

        size_t w = write_idx_.load(std::memory_order_acquire);
        T min_val = buffer_[(w - 1) & (Capacity - 1)];
        T max_val = min_val;

        size_t count = std::min(sz, Capacity);
        for (size_t i = 0; i < count; ++i) {
            T val = buffer_[(w - 1 - i) & (Capacity - 1)];
            min_val = std::min(min_val, val);
            max_val = std::max(max_val, val);
        }
        return {min_val, max_val};
    }

    void clear() noexcept {
        write_idx_.store(0, std::memory_order_release);
        size_.store(0, std::memory_order_release);
    }
};

// ============================================================================
// Constants
// ============================================================================

constexpr size_t IAT_SAMPLE_WINDOW = 1024;    // Power of 2 for ring buffer
constexpr double PCR_CLOCK_FREQ = 27000000.0; // 27 MHz PCR clock
constexpr double DEFAULT_PCR_JITTER_THRESHOLD_US = 0.5;  // TR 101 290 limit: 500ns
constexpr double DEFAULT_IAT_JITTER_THRESHOLD_US = 1000.0;  // 1ms default
constexpr size_t MAX_PIDS = 8192;             // Maximum PID value + 1

// ============================================================================
// Internal Structures
// ============================================================================

struct TsDuckContext {
    ts::DuckContext duck;
    std::atomic<bool> initialized{false};

    TsDuckContext() : duck(nullptr) {
        initialized.store(true, std::memory_order_release);
    }
};

// Per-PID tracking with seqlock for lock-free access
struct alignas(CACHE_LINE_SIZE) PidTrackingSlot {
    Seqlock seqlock;

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
    std::atomic<int64_t> first_seen_ns{0};  // Nanoseconds since epoch
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

// Double-buffered metrics for lock-free snapshot reads
struct alignas(CACHE_LINE_SIZE) MetricsBuffer {
    TsDuckMetricsNative data{};
    Seqlock seqlock;
};

// Double-buffered PCR analysis
struct alignas(CACHE_LINE_SIZE) PcrBuffer {
    PcrAnalysisNative data{};
    Seqlock seqlock;

    // Writer state (only accessed by writer thread)
    int64_t last_pcr_value{-1};
    int64_t last_pcr_packet_index{0};
    int64_t last_pcr_time_ns{0};
    int64_t first_pcr_value{-1};
    int64_t first_pcr_time_ns{0};
    double jitter_sum{0.0};
    int64_t jitter_count{0};
};

// Double-buffered IAT analysis
struct alignas(CACHE_LINE_SIZE) IatBuffer {
    IatAnalysisNative data{};
    Seqlock seqlock;

    // Writer state
    LockFreeRingBuffer<double, IAT_SAMPLE_WINDOW> samples;
    int64_t last_packet_time_ns{0};
    double sum{0.0};
    double sum_sq{0.0};
    int64_t count{0};
    bool first_packet{true};
};

// Double-buffered bitrate analysis
struct alignas(CACHE_LINE_SIZE) BitrateBuffer {
    BitrateAnalysisNative data{};
    Seqlock seqlock;
};

struct TsDuckAnalyzer {
    TsDuckContext* context;
    ts::TSAnalyzer analyzer;
    TsDuckConfigNative config;

    // =========================================================================
    // Lock-Free Metrics State
    // =========================================================================
    alignas(CACHE_LINE_SIZE) MetricsBuffer metrics;
    alignas(CACHE_LINE_SIZE) std::atomic<bool> has_new_metrics{false};
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> last_metrics_time_ns{0};

    // Callbacks (set once, read many - safe with relaxed ordering)
    std::atomic<TsDuckMetricsCallback> metrics_callback{nullptr};
    std::atomic<void*> metrics_user_data{nullptr};
    std::atomic<TsDuckViolationCallback> violation_callback{nullptr};
    std::atomic<void*> violation_user_data{nullptr};

    // Previous error counts for violation detection (writer-only)
    int64_t prev_cc_errors{0};
    int64_t prev_transport_errors{0};
    int64_t prev_crc_errors{0};
    int64_t prev_pcr_errors{0};

    // Packet counter - atomic for thread-safe increment
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> packets_processed{0};

    // =========================================================================
    // Lock-Free Phase 2a State
    // =========================================================================
    alignas(CACHE_LINE_SIZE) PcrBuffer pcr;
    alignas(CACHE_LINE_SIZE) IatBuffer iat;
    alignas(CACHE_LINE_SIZE) BitrateBuffer bitrate;

    // Atomic counters for bitrate calculation
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> null_packet_count{0};
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> total_packet_count{0};

    // Thresholds (written rarely, read often)
    std::atomic<double> pcr_jitter_threshold_us{DEFAULT_PCR_JITTER_THRESHOLD_US};
    std::atomic<double> iat_jitter_threshold_us{DEFAULT_IAT_JITTER_THRESHOLD_US};

    // =========================================================================
    // Lock-Free Phase 2b: Fixed-Size PID Array
    // =========================================================================
    alignas(CACHE_LINE_SIZE) std::array<PidTrackingSlot, MAX_PIDS> pid_slots;
    alignas(CACHE_LINE_SIZE) std::atomic<int32_t> active_pid_count{0};

    // Start time for bitrate calculation
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> start_time_ns{0};

    TsDuckAnalyzer(TsDuckContext* ctx, const TsDuckConfigNative* cfg)
        : context(ctx)
        , analyzer(ctx->duck)
    {
        if (cfg) {
            config = *cfg;
        } else {
            config.metrics_interval_ms = 1000;
            config.enable_tr101290 = 1;
            config.sample_size_bytes = 188 * 1000;
            config.reserved = 0;
        }

        auto now = std::chrono::steady_clock::now().time_since_epoch();
        auto now_ns = std::chrono::duration_cast<std::chrono::nanoseconds>(now).count();

        start_time_ns.store(now_ns, std::memory_order_release);
        last_metrics_time_ns.store(now_ns, std::memory_order_release);
        iat.last_packet_time_ns = now_ns;
        pcr.last_pcr_time_ns = now_ns;
        pcr.first_pcr_time_ns = now_ns;
    }

    // Get current time in nanoseconds
    static int64_t now_ns() noexcept {
        auto now = std::chrono::steady_clock::now().time_since_epoch();
        return std::chrono::duration_cast<std::chrono::nanoseconds>(now).count();
    }

    // Get .NET ticks from system clock
    static int64_t get_dotnet_ticks() noexcept {
        auto sys_now = std::chrono::system_clock::now();
        auto duration = sys_now.time_since_epoch();
        auto ticks = std::chrono::duration_cast<std::chrono::nanoseconds>(duration).count() / 100;
        return ticks + 621355968000000000LL;  // .NET epoch offset
    }

    // =========================================================================
    // Lock-Free PCR Processing
    // =========================================================================
    void processPcr(const ts::TSPacket& packet, int64_t packet_index) noexcept {
        if (!packet.hasPCR()) {
            return;
        }

        uint64_t pcr_value = packet.getPCR();
        int64_t current_time = now_ns();

        // Begin seqlock write
        auto seq = pcr.seqlock.begin_write();

        pcr.data.pcr_count++;

        if (pcr.last_pcr_value < 0) {
            // First PCR
            pcr.last_pcr_value = static_cast<int64_t>(pcr_value);
            pcr.last_pcr_packet_index = packet_index;
            pcr.last_pcr_time_ns = current_time;
            pcr.first_pcr_value = static_cast<int64_t>(pcr_value);
            pcr.first_pcr_time_ns = current_time;
            pcr.data.pcr_valid_count++;
            pcr.seqlock.end_write(seq);
            return;
        }

        // Calculate PCR interval
        int64_t pcr_diff = static_cast<int64_t>(pcr_value) - pcr.last_pcr_value;
        if (pcr_diff < 0) {
            pcr_diff += (1LL << 42);  // Handle wraparound
        }

        double interval_ms = static_cast<double>(pcr_diff) / PCR_CLOCK_FREQ * 1000.0;
        int64_t packet_diff = packet_index - pcr.last_pcr_packet_index;

        pcr.data.pcr_interval_packets = packet_diff;
        pcr.data.pcr_interval_ms = interval_ms;

        // Calculate jitter
        double time_diff_us = static_cast<double>(current_time - pcr.last_pcr_time_ns) / 1000.0;
        double expected_us = interval_ms * 1000.0;
        double jitter_us = std::abs(time_diff_us - expected_us);

        pcr.data.pcr_jitter_us = jitter_us;
        if (jitter_us > pcr.data.pcr_jitter_max_us) {
            pcr.data.pcr_jitter_max_us = jitter_us;
        }

        // Update rolling average
        pcr.jitter_sum += jitter_us;
        pcr.jitter_count++;
        pcr.data.pcr_jitter_avg_us = pcr.jitter_sum / static_cast<double>(pcr.jitter_count);

        // Validate PCR
        if (interval_ms > 0 && interval_ms < 1000) {
            pcr.data.pcr_valid_count++;
        }

        // Calculate drift
        if (pcr.first_pcr_value >= 0) {
            double total_system_us = static_cast<double>(current_time - pcr.first_pcr_time_ns) / 1000.0;
            if (total_system_us > 1000000.0) {  // After 1 second
                int64_t total_pcr_diff = static_cast<int64_t>(pcr_value) - pcr.first_pcr_value;
                if (total_pcr_diff < 0) {
                    total_pcr_diff += (1LL << 42);
                }
                double expected_pcr_ticks = total_system_us * PCR_CLOCK_FREQ / 1000000.0;
                double drift_ratio = (static_cast<double>(total_pcr_diff) - expected_pcr_ticks) / expected_pcr_ticks;
                pcr.data.pcr_drift_ppm = drift_ratio * 1000000.0;
            }
        }

        // Update tracking
        pcr.last_pcr_value = static_cast<int64_t>(pcr_value);
        pcr.last_pcr_packet_index = packet_index;
        pcr.last_pcr_time_ns = current_time;

        pcr.seqlock.end_write(seq);
    }

    // =========================================================================
    // Lock-Free IAT Processing
    // =========================================================================
    void processIat() noexcept {
        int64_t current_time = now_ns();

        auto seq = iat.seqlock.begin_write();

        if (iat.first_packet) {
            iat.first_packet = false;
            iat.last_packet_time_ns = current_time;
            iat.seqlock.end_write(seq);
            return;
        }

        // Calculate IAT in microseconds
        double iat_us = static_cast<double>(current_time - iat.last_packet_time_ns) / 1000.0;
        iat.last_packet_time_ns = current_time;

        // Update statistics
        iat.count++;
        iat.sum += iat_us;
        iat.sum_sq += iat_us * iat_us;

        // Add to ring buffer
        iat.samples.push(iat_us);

        // Calculate statistics
        size_t window_size = iat.samples.size();
        if (window_size > 0) {
            iat.data.iat_avg_us = iat.sum / static_cast<double>(iat.count);

            auto [min_val, max_val] = iat.samples.get_min_max();
            iat.data.iat_min_us = min_val;
            iat.data.iat_max_us = max_val;

            iat.data.iat_jitter_us = std::max(
                max_val - iat.data.iat_avg_us,
                iat.data.iat_avg_us - min_val
            );

            // Standard deviation (approximate from recent window)
            double variance = (iat.sum_sq / static_cast<double>(iat.count)) -
                              (iat.data.iat_avg_us * iat.data.iat_avg_us);
            iat.data.iat_stddev_us = variance > 0 ? std::sqrt(variance) : 0.0;

            // Detect late/early packets
            double threshold = 2.0 * iat.data.iat_stddev_us;
            if (iat_us > iat.data.iat_avg_us + threshold) {
                iat.data.late_packets++;
            } else if (iat_us < iat.data.iat_avg_us - threshold && iat_us > 0) {
                iat.data.early_packets++;
            }

            // Detect bursts
            if (iat_us < 10.0 && window_size > 1) {
                iat.data.burst_count++;
            }
        }

        iat.seqlock.end_write(seq);
    }

    // =========================================================================
    // Lock-Free Bitrate Processing
    // =========================================================================
    void processBitrate(const ts::TSPacket& packet) noexcept {
        total_packet_count.fetch_add(1, std::memory_order_relaxed);

        if (packet.getPID() == 0x1FFF) {
            null_packet_count.fetch_add(1, std::memory_order_relaxed);
        }
    }

    void updateBitrateAnalysis(int64_t ts_bitrate) noexcept {
        auto seq = bitrate.seqlock.begin_write();

        bitrate.data.ts_bitrate_nominal = ts_bitrate;

        int64_t total = total_packet_count.load(std::memory_order_relaxed);
        int64_t nulls = null_packet_count.load(std::memory_order_relaxed);

        if (total > 0) {
            bitrate.data.null_packet_ratio = static_cast<double>(nulls) / static_cast<double>(total);
        }

        bitrate.data.null_packet_bitrate = static_cast<int64_t>(
            static_cast<double>(ts_bitrate) * bitrate.data.null_packet_ratio
        );
        bitrate.data.useful_bitrate = ts_bitrate - bitrate.data.null_packet_bitrate;

        // PCR-based bitrate (read PCR data with seqlock)
        uint64_t pcr_seq;
        do {
            pcr_seq = pcr.seqlock.begin_read();
            if (pcr.data.pcr_interval_ms > 0 && pcr.data.pcr_interval_packets > 0) {
                double pcr_bitrate = (static_cast<double>(pcr.data.pcr_interval_packets) * 188.0 * 8.0) /
                                     (pcr.data.pcr_interval_ms / 1000.0);
                bitrate.data.ts_bitrate_pcr = static_cast<int64_t>(pcr_bitrate);

                if (ts_bitrate > 0) {
                    double ratio = static_cast<double>(bitrate.data.ts_bitrate_pcr) /
                                   static_cast<double>(ts_bitrate);
                    bitrate.data.bitrate_accuracy = std::max(0.0, 1.0 - std::abs(1.0 - ratio));
                }
            }
        } while (!pcr.seqlock.read_consistent(pcr_seq));

        bitrate.data.ts_bitrate_dts = ts_bitrate;

        bitrate.seqlock.end_write(seq);
    }

    // =========================================================================
    // Lock-Free Per-PID Processing
    // =========================================================================
    void processPidInfo(const ts::TSPacket& packet) noexcept {
        int64_t current_time = now_ns();
        int32_t pid = static_cast<int32_t>(packet.getPID());

        if (pid < 0 || pid >= static_cast<int32_t>(MAX_PIDS)) {
            return;
        }

        auto& slot = pid_slots[pid];

        // Check if this is a new PID
        if (!slot.is_active.load(std::memory_order_relaxed)) {
            slot.is_active.store(true, std::memory_order_release);
            slot.first_seen_ns.store(current_time, std::memory_order_release);
            active_pid_count.fetch_add(1, std::memory_order_relaxed);
        }

        // Update atomic counters (no seqlock needed for atomic increments)
        slot.packets.fetch_add(1, std::memory_order_relaxed);
        slot.last_seen_ns.store(current_time, std::memory_order_release);

        // Check continuity counter (for non-null packets)
        if (pid != 0x1FFF) {
            int32_t cc = packet.getCC();
            int32_t last = slot.last_cc.load(std::memory_order_relaxed);

            if (last >= 0) {
                int32_t expected_cc = (last + 1) & 0x0F;
                if (cc != expected_cc && cc != last) {
                    slot.continuity_errors.fetch_add(1, std::memory_order_relaxed);
                } else if (cc == last && packet.hasPayload()) {
                    slot.duplicate_packets.fetch_add(1, std::memory_order_relaxed);
                }
            }
            slot.last_cc.store(cc, std::memory_order_release);
        }

        // Check scrambling
        uint8_t tsc = packet.getScrambling();
        if (tsc != 0) {
            slot.scrambled_packets.fetch_add(1, std::memory_order_relaxed);
            slot.is_scrambled.store(true, std::memory_order_release);
        } else {
            slot.is_scrambled.store(false, std::memory_order_release);
        }

        // Check for PCR
        if (packet.hasPCR()) {
            slot.is_pcr_pid.store(true, std::memory_order_release);
            // Copy PCR jitter using seqlock
            auto seq = slot.seqlock.begin_write();
            uint64_t pcr_seq;
            do {
                pcr_seq = pcr.seqlock.begin_read();
                slot.pcr_jitter_us = pcr.data.pcr_jitter_us;
            } while (!pcr.seqlock.read_consistent(pcr_seq));
            slot.seqlock.end_write(seq);
        }

        // Detect stream type from PES header
        if (pid >= 0x100 && pid < 0x1FFF && packet.hasPayload() && packet.getPUSI()) {
            const uint8_t* payload = packet.getPayload();
            size_t payloadSize = packet.getPayloadSize();

            if (payloadSize >= 4) {
                if (payload[0] == 0x00 && payload[1] == 0x00 && payload[2] == 0x01) {
                    uint8_t stream_id = payload[3];
                    if (stream_id >= 0xE0 && stream_id <= 0xEF) {
                        slot.is_video.store(true, std::memory_order_release);
                        slot.stream_type.store(0x02, std::memory_order_release);
                    } else if (stream_id >= 0xC0 && stream_id <= 0xDF) {
                        slot.is_audio.store(true, std::memory_order_release);
                        slot.stream_type.store(0x03, std::memory_order_release);
                    }
                }
            }
        }
    }

    // =========================================================================
    // Lock-Free Metrics Update
    // =========================================================================
    void updateMetrics() noexcept {
        int64_t current_time = now_ns();
        int64_t last_time = last_metrics_time_ns.load(std::memory_order_relaxed);
        int64_t elapsed_ms = (current_time - last_time) / 1000000;

        if (elapsed_ms < config.metrics_interval_ms) {
            return;
        }

        last_metrics_time_ns.store(current_time, std::memory_order_release);

        auto seq = metrics.seqlock.begin_write();

        metrics.data.timestamp_ticks = get_dotnet_ticks();

        // Get PID and service counts from TSDuck analyzer
        std::vector<ts::PID> pids;
        std::vector<uint16_t> services;
        analyzer.getPIDs(pids);
        analyzer.getServiceIds(services);

        metrics.data.pid_count = static_cast<int32_t>(pids.size());
        metrics.data.service_count = static_cast<int32_t>(services.size());

        // Get unreferenced PIDs
        std::vector<ts::PID> unreferenced_pids;
        analyzer.getUnreferencedPIDs(unreferenced_pids);

        // Calculate bitrate
        int64_t start = start_time_ns.load(std::memory_order_relaxed);
        int64_t total_time_ms = (current_time - start) / 1000000;
        if (total_time_ms > 0) {
            int64_t bytes = packets_processed.load(std::memory_order_relaxed) * 188;
            metrics.data.ts_bitrate = (bytes * 8 * 1000) / total_time_ms;
        }

        // TR 101 290 metrics
        if (config.enable_tr101290) {
            // Sum continuity errors from all PIDs
            int64_t total_cc_errors = 0;
            for (size_t i = 0; i < MAX_PIDS; ++i) {
                if (pid_slots[i].is_active.load(std::memory_order_relaxed)) {
                    total_cc_errors += pid_slots[i].continuity_errors.load(std::memory_order_relaxed);
                }
            }

            metrics.data.priority1.sync_byte_error = 0;
            metrics.data.priority1.sync_loss = 0;
            metrics.data.priority1.pat_error = 0;
            metrics.data.priority1.pat_error_2 = 0;
            metrics.data.priority1.continuity_count_error = total_cc_errors;
            metrics.data.priority1.pmt_error = 0;
            metrics.data.priority1.pmt_error_2 = 0;
            metrics.data.priority1.pid_error = static_cast<int64_t>(unreferenced_pids.size());

            metrics.data.priority2.transport_error = 0;
            metrics.data.priority2.crc_error = 0;
            metrics.data.priority2.pcr_repetition_error = 0;
            metrics.data.priority2.pcr_discontinuity_error = 0;
            metrics.data.priority2.pcr_accuracy_error = 0;
            metrics.data.priority2.pts_error = 0;
            metrics.data.priority2.cat_error = 0;
        }

        metrics.seqlock.end_write(seq);

        // Update bitrate analysis
        updateBitrateAnalysis(metrics.data.ts_bitrate);

        has_new_metrics.store(true, std::memory_order_release);

        // Invoke callback if registered
        auto callback = metrics_callback.load(std::memory_order_acquire);
        if (callback) {
            auto user_data = metrics_user_data.load(std::memory_order_acquire);
            callback(&metrics.data, user_data);
        }
    }
};

// ============================================================================
// Library Initialization
// ============================================================================

extern "C" {

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
        return new TsDuckContext();
    } catch (...) {
        return nullptr;
    }
}

TSDUCK_API void tsduck_context_destroy(TsDuckContextHandle ctx) {
    delete ctx;
}

TSDUCK_API bool tsduck_context_is_available(TsDuckContextHandle ctx) {
    return ctx && ctx->initialized.load(std::memory_order_acquire);
}

// ============================================================================
// Analyzer Lifecycle
// ============================================================================

TSDUCK_API TsDuckAnalyzerHandle tsduck_analyzer_create(
    TsDuckContextHandle ctx,
    const TsDuckConfigNative* config)
{
    if (!ctx) {
        return nullptr;
    }

    try {
        return new TsDuckAnalyzer(ctx, config);
    } catch (...) {
        return nullptr;
    }
}

TSDUCK_API void tsduck_analyzer_destroy(TsDuckAnalyzerHandle analyzer) {
    delete analyzer;
}

TSDUCK_API bool tsduck_analyzer_is_initialized(TsDuckAnalyzerHandle analyzer) {
    return analyzer != nullptr;
}

// ============================================================================
// Data Processing (Lock-Free Hot Path)
// ============================================================================

TSDUCK_API int32_t tsduck_analyzer_feed(
    TsDuckAnalyzerHandle analyzer,
    const uint8_t* data,
    int32_t length)
{
    if (!analyzer || !data || length <= 0) {
        return TSDUCK_ERROR_INVALID_DATA;
    }

    constexpr int PACKET_SIZE = 188;
    int32_t packets = length / PACKET_SIZE;

    if (packets == 0) {
        return 0;
    }

    try {
        int64_t base_packet_index = analyzer->packets_processed.load(std::memory_order_relaxed);
        ts::TSPacketMetadata mdata;

        for (int32_t i = 0; i < packets; i++) {
            const uint8_t* packet_data = data + (i * PACKET_SIZE);

            if (packet_data[0] != 0x47) {
                continue;
            }

            ts::TSPacket packet;
            std::memcpy(packet.b, packet_data, PACKET_SIZE);
            analyzer->analyzer.feedPacket(packet, mdata);

            // Lock-free processing
            analyzer->processPcr(packet, base_packet_index + i);
            analyzer->processIat();
            analyzer->processBitrate(packet);
            analyzer->processPidInfo(packet);
        }

        analyzer->packets_processed.fetch_add(packets, std::memory_order_release);
        analyzer->updateMetrics();

        return packets;
    } catch (...) {
        return TSDUCK_ERROR_INTERNAL;
    }
}

TSDUCK_API void tsduck_analyzer_reset(TsDuckAnalyzerHandle analyzer) {
    if (!analyzer) {
        return;
    }

    try {
        analyzer->analyzer.reset();
        analyzer->packets_processed.store(0, std::memory_order_release);
        analyzer->has_new_metrics.store(false, std::memory_order_release);
        analyzer->prev_cc_errors = 0;
        analyzer->prev_transport_errors = 0;
        analyzer->prev_crc_errors = 0;
        analyzer->prev_pcr_errors = 0;

        int64_t current_time = TsDuckAnalyzer::now_ns();
        analyzer->start_time_ns.store(current_time, std::memory_order_release);
        analyzer->last_metrics_time_ns.store(current_time, std::memory_order_release);

        // Reset metrics
        auto seq = analyzer->metrics.seqlock.begin_write();
        std::memset(&analyzer->metrics.data, 0, sizeof(analyzer->metrics.data));
        analyzer->metrics.seqlock.end_write(seq);

        // Reset PCR
        auto pcr_seq = analyzer->pcr.seqlock.begin_write();
        std::memset(&analyzer->pcr.data, 0, sizeof(analyzer->pcr.data));
        analyzer->pcr.last_pcr_value = -1;
        analyzer->pcr.last_pcr_packet_index = 0;
        analyzer->pcr.last_pcr_time_ns = current_time;
        analyzer->pcr.first_pcr_value = -1;
        analyzer->pcr.first_pcr_time_ns = current_time;
        analyzer->pcr.jitter_sum = 0.0;
        analyzer->pcr.jitter_count = 0;
        analyzer->pcr.seqlock.end_write(pcr_seq);

        // Reset IAT
        auto iat_seq = analyzer->iat.seqlock.begin_write();
        std::memset(&analyzer->iat.data, 0, sizeof(analyzer->iat.data));
        analyzer->iat.samples.clear();
        analyzer->iat.last_packet_time_ns = current_time;
        analyzer->iat.sum = 0.0;
        analyzer->iat.sum_sq = 0.0;
        analyzer->iat.count = 0;
        analyzer->iat.first_packet = true;
        analyzer->iat.seqlock.end_write(iat_seq);

        // Reset bitrate
        auto br_seq = analyzer->bitrate.seqlock.begin_write();
        std::memset(&analyzer->bitrate.data, 0, sizeof(analyzer->bitrate.data));
        analyzer->bitrate.seqlock.end_write(br_seq);

        analyzer->null_packet_count.store(0, std::memory_order_release);
        analyzer->total_packet_count.store(0, std::memory_order_release);

        // Reset PIDs
        analyzer->active_pid_count.store(0, std::memory_order_release);
        for (auto& slot : analyzer->pid_slots) {
            slot.reset();
        }
    } catch (...) {
        // Ignore reset errors
    }
}

// ============================================================================
// Lock-Free Metrics Retrieval
// ============================================================================

TSDUCK_API bool tsduck_analyzer_get_metrics(
    TsDuckAnalyzerHandle analyzer,
    TsDuckMetricsNative* out_metrics)
{
    if (!analyzer || !out_metrics) {
        return false;
    }

    // Lock-free read with seqlock
    uint64_t seq;
    do {
        seq = analyzer->metrics.seqlock.begin_read();
        *out_metrics = analyzer->metrics.data;
    } while (!analyzer->metrics.seqlock.read_consistent(seq));

    analyzer->has_new_metrics.store(false, std::memory_order_release);
    return out_metrics->timestamp_ticks > 0;
}

TSDUCK_API bool tsduck_analyzer_has_new_metrics(TsDuckAnalyzerHandle analyzer) {
    return analyzer && analyzer->has_new_metrics.load(std::memory_order_acquire);
}

// ============================================================================
// Callbacks
// ============================================================================

TSDUCK_API void tsduck_analyzer_set_metrics_callback(
    TsDuckAnalyzerHandle analyzer,
    TsDuckMetricsCallback callback,
    void* user_data)
{
    if (!analyzer) {
        return;
    }
    analyzer->metrics_user_data.store(user_data, std::memory_order_release);
    analyzer->metrics_callback.store(callback, std::memory_order_release);
}

TSDUCK_API void tsduck_analyzer_set_violation_callback(
    TsDuckAnalyzerHandle analyzer,
    TsDuckViolationCallback callback,
    void* user_data)
{
    if (!analyzer) {
        return;
    }
    analyzer->violation_user_data.store(user_data, std::memory_order_release);
    analyzer->violation_callback.store(callback, std::memory_order_release);
}

// ============================================================================
// Lock-Free Phase 2a: PCR Analysis
// ============================================================================

TSDUCK_API bool tsduck_analyzer_get_pcr_analysis(
    TsDuckAnalyzerHandle analyzer,
    PcrAnalysisNative* out_analysis)
{
    if (!analyzer || !out_analysis) {
        return false;
    }

    uint64_t seq;
    do {
        seq = analyzer->pcr.seqlock.begin_read();
        *out_analysis = analyzer->pcr.data;
    } while (!analyzer->pcr.seqlock.read_consistent(seq));

    return out_analysis->pcr_count > 0;
}

TSDUCK_API bool tsduck_analyzer_set_pcr_jitter_threshold(
    TsDuckAnalyzerHandle analyzer,
    double max_jitter_us)
{
    if (!analyzer || max_jitter_us < 0) {
        return false;
    }
    analyzer->pcr_jitter_threshold_us.store(max_jitter_us, std::memory_order_release);
    return true;
}

// ============================================================================
// Lock-Free Phase 2a: IAT Analysis
// ============================================================================

TSDUCK_API bool tsduck_analyzer_get_iat_analysis(
    TsDuckAnalyzerHandle analyzer,
    IatAnalysisNative* out_analysis)
{
    if (!analyzer || !out_analysis) {
        return false;
    }

    uint64_t seq;
    do {
        seq = analyzer->iat.seqlock.begin_read();
        *out_analysis = analyzer->iat.data;
    } while (!analyzer->iat.seqlock.read_consistent(seq));

    return analyzer->iat.count > 0;
}

TSDUCK_API bool tsduck_analyzer_set_iat_jitter_threshold(
    TsDuckAnalyzerHandle analyzer,
    double max_jitter_us)
{
    if (!analyzer || max_jitter_us < 0) {
        return false;
    }
    analyzer->iat_jitter_threshold_us.store(max_jitter_us, std::memory_order_release);
    return true;
}

// ============================================================================
// Lock-Free Phase 2a: Bitrate Analysis
// ============================================================================

TSDUCK_API bool tsduck_analyzer_get_bitrate_analysis(
    TsDuckAnalyzerHandle analyzer,
    BitrateAnalysisNative* out_analysis)
{
    if (!analyzer || !out_analysis) {
        return false;
    }

    uint64_t seq;
    do {
        seq = analyzer->bitrate.seqlock.begin_read();
        *out_analysis = analyzer->bitrate.data;
    } while (!analyzer->bitrate.seqlock.read_consistent(seq));

    return analyzer->total_packet_count.load(std::memory_order_acquire) > 0;
}

// ============================================================================
// Lock-Free Phase 2b: Extended PID Information
// ============================================================================

TSDUCK_API int32_t tsduck_analyzer_get_pid_count(TsDuckAnalyzerHandle analyzer)
{
    if (!analyzer) {
        return TSDUCK_ERROR_NULL_HANDLE;
    }
    return analyzer->active_pid_count.load(std::memory_order_acquire);
}

TSDUCK_API int32_t tsduck_analyzer_get_pid_info_extended(
    TsDuckAnalyzerHandle analyzer,
    TsDuckPidInfoExtended* out_pids,
    int32_t max_pids)
{
    if (!analyzer) {
        return TSDUCK_ERROR_NULL_HANDLE;
    }
    if (!out_pids || max_pids <= 0) {
        return TSDUCK_ERROR_INVALID_DATA;
    }

    int32_t count = 0;

    for (size_t pid = 0; pid < MAX_PIDS && count < max_pids; ++pid) {
        auto& slot = analyzer->pid_slots[pid];

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
            out.bitrate = (out.packets * 188 * 8 * 1000) / duration_ms;
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

} // extern "C"
