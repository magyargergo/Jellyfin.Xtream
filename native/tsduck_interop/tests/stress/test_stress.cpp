// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Stress tests for memory pressure, thread contention, and long-running stability.
// These tests are designed to expose concurrency bugs, memory leaks, and race conditions.
//
// Usage:
//   ./test_stress                          # Run all stress tests
//   ./test_stress --gtest_filter=*Memory*  # Run only memory tests
//   ./test_stress --gtest_filter=*Thread*  # Run only thread contention tests
//   ./test_stress --gtest_filter=*Scte35*  # Run only SCTE-35 tests
//   ./test_stress --gtest_filter=*Nal*     # Run only NAL parser tests

#include <gtest/gtest.h>
#include <atomic>
#include <chrono>
#include <random>
#include <thread>
#include <vector>

#include "analysis/nal_parser.hpp"
#include "analysis/scte35_monitor.hpp"
#include "concurrency/seqlock.hpp"
#include "context/analyzer.hpp"
#include "core/constants.hpp"
#include "core/types.hpp"
#include "streaming/alignment_buffer.hpp"

using namespace tsduck_interop;
using namespace tsduck_interop::analysis;
using namespace tsduck_interop::concurrency;
using namespace tsduck_interop::context;
using namespace tsduck_interop::streaming;

// ============================================================================
// Test Configuration
// ============================================================================

namespace {

// Default stress test duration (can be overridden via environment)
constexpr int kDefaultDurationMs = 1000;
constexpr int kHighContentionThreads = 16;
constexpr int kMediumContentionThreads = 8;
constexpr int kLowContentionThreads = 4;

int getDurationMs() {
    const char* env = std::getenv("STRESS_DURATION_MS");
    return env ? std::atoi(env) : kDefaultDurationMs;
}

// Create a valid TS packet with sync byte
std::vector<uint8_t> createTsPacket(uint16_t pid = 0x100, uint8_t cc = 0) {
    std::vector<uint8_t> packet(ts::PKT_SIZE, 0xFF);
    packet[0] = ts::SYNC_BYTE;
    packet[1] = static_cast<uint8_t>((pid >> 8) & 0x1F);
    packet[2] = static_cast<uint8_t>(pid & 0xFF);
    packet[3] = static_cast<uint8_t>(0x10 | (cc & 0x0F));
    return packet;
}

// Create multiple TS packets with varying PIDs
std::vector<uint8_t> createTsStream(int count, int pid_variance = 10) {
    std::vector<uint8_t> stream;
    stream.reserve(count * ts::PKT_SIZE);
    std::mt19937 rng(42);  // Fixed seed for reproducibility
    std::uniform_int_distribution<int> pid_dist(0x100, 0x100 + pid_variance);
    std::uniform_int_distribution<int> cc_dist(0, 15);

    for (int i = 0; i < count; i++) {
        auto pkt = createTsPacket(static_cast<uint16_t>(pid_dist(rng)),
                                   static_cast<uint8_t>(cc_dist(rng)));
        stream.insert(stream.end(), pkt.begin(), pkt.end());
    }
    return stream;
}

}  // namespace

// ============================================================================
// Seqlock Stress Tests
// ============================================================================

class SeqlockStressTest : public ::testing::Test {
protected:
    Seqlock seqlock;
    std::atomic<bool> stop_flag{false};
    std::atomic<int64_t> reads_performed{0};
    std::atomic<int64_t> writes_performed{0};
    std::atomic<int64_t> inconsistent_reads{0};
};

TEST_F(SeqlockStressTest, HighContentionReadWrite) {
    // Shared data protected by seqlock
    struct SharedData {
        int64_t a = 0;
        int64_t b = 0;
        int64_t c = 0;
        int64_t d = 0;
    };
    SharedData data;

    int duration_ms = getDurationMs();

    // Single writer thread (seqlock is single-writer by design)
    std::thread writer([&]() {
        int64_t counter = 0;
        while (!stop_flag.load(std::memory_order_relaxed)) {
            auto seq = seqlock.begin_write();
            data.a = counter;
            data.b = counter;
            data.c = counter;
            data.d = counter;
            seqlock.end_write(seq);
            counter++;
            writes_performed.fetch_add(1, std::memory_order_relaxed);
        }
    });

    // Multiple reader threads
    std::vector<std::thread> readers;
    for (int i = 0; i < kHighContentionThreads; i++) {
        readers.emplace_back([&]() {
            while (!stop_flag.load(std::memory_order_relaxed)) {
                uint64_t seq;
                int64_t a, b, c, d;
                do {
                    seq = seqlock.begin_read();
                    a = data.a;
                    b = data.b;
                    c = data.c;
                    d = data.d;
                } while (!seqlock.read_consistent(seq));

                // All values should be identical
                if (a != b || b != c || c != d) {
                    inconsistent_reads.fetch_add(1, std::memory_order_relaxed);
                }
                reads_performed.fetch_add(1, std::memory_order_relaxed);
            }
        });
    }

    // Run for specified duration
    std::this_thread::sleep_for(std::chrono::milliseconds(duration_ms));
    stop_flag.store(true, std::memory_order_release);

    // Join all threads
    writer.join();
    for (auto& t : readers) {
        t.join();
    }

    // Verify results
    EXPECT_EQ(inconsistent_reads.load(), 0)
        << "Detected " << inconsistent_reads.load() << " inconsistent reads";
    EXPECT_GT(reads_performed.load(), 0);
    EXPECT_GT(writes_performed.load(), 0);

    std::cout << "Seqlock stress: " << writes_performed.load() << " writes, "
              << reads_performed.load() << " reads, "
              << inconsistent_reads.load() << " inconsistent" << std::endl;
}

TEST_F(SeqlockStressTest, RapidWriteBursts) {
    int duration_ms = getDurationMs();
    std::atomic<int64_t> burst_count{0};

    // Writer performs rapid bursts of writes
    std::thread writer([&]() {
        while (!stop_flag.load(std::memory_order_relaxed)) {
            // Burst of 1000 writes
            for (int i = 0; i < 1000; i++) {
                auto seq = seqlock.begin_write();
                seqlock.end_write(seq);
                writes_performed.fetch_add(1, std::memory_order_relaxed);
            }
            burst_count.fetch_add(1, std::memory_order_relaxed);
            std::this_thread::yield();
        }
    });

    // Reader tries to keep up
    std::thread reader([&]() {
        while (!stop_flag.load(std::memory_order_relaxed)) {
            uint64_t seq = seqlock.begin_read();
            (void)seqlock.read_consistent(seq);
            reads_performed.fetch_add(1, std::memory_order_relaxed);
        }
    });

    std::this_thread::sleep_for(std::chrono::milliseconds(duration_ms));
    stop_flag.store(true, std::memory_order_release);

    writer.join();
    reader.join();

    EXPECT_GT(burst_count.load(), 0);
    std::cout << "Burst writes: " << burst_count.load() << " bursts, "
              << writes_performed.load() << " total writes" << std::endl;
}

// ============================================================================
// Alignment Buffer Stress Tests
// ============================================================================

class AlignmentBufferStressTest : public ::testing::Test {
protected:
    AlignmentBuffer buffer{64};  // 64-packet buffer
    std::atomic<bool> stop_flag{false};
    std::atomic<int64_t> bytes_processed{0};
    std::atomic<int64_t> packets_aligned{0};
};

TEST_F(AlignmentBufferStressTest, RapidSmallChunks) {
    // Stress test with many small chunks (1-10 bytes)
    int duration_ms = getDurationMs();
    auto stream = createTsStream(10000);

    std::thread feeder([&]() {
        std::mt19937 rng(42);
        std::uniform_int_distribution<size_t> chunk_dist(1, 10);
        size_t offset = 0;

        while (!stop_flag.load(std::memory_order_relaxed)) {
            size_t chunk_size = std::min(chunk_dist(rng), stream.size() - offset);
            auto chunk = buffer.append(stream.data() + offset, static_cast<int>(chunk_size));
            bytes_processed.fetch_add(chunk_size, std::memory_order_relaxed);
            packets_aligned.fetch_add(chunk.length / ts::PKT_SIZE, std::memory_order_relaxed);
            offset += chunk_size;

            if (offset >= stream.size()) {
                offset = 0;
                buffer.reset();
            }
        }
    });

    std::this_thread::sleep_for(std::chrono::milliseconds(duration_ms));
    stop_flag.store(true, std::memory_order_release);
    feeder.join();

    EXPECT_GT(bytes_processed.load(), 0);
    EXPECT_GT(packets_aligned.load(), 0);
    std::cout << "Small chunks: " << bytes_processed.load() << " bytes, "
              << packets_aligned.load() << " packets aligned" << std::endl;
}

TEST_F(AlignmentBufferStressTest, LargeChunkBursts) {
    // Stress test with large chunks (like real HTTP responses)
    int duration_ms = getDurationMs();
    auto stream = createTsStream(1000);

    std::thread feeder([&]() {
        std::mt19937 rng(42);
        std::uniform_int_distribution<size_t> chunk_dist(8192, 65536);  // 8KB-64KB
        size_t offset = 0;

        while (!stop_flag.load(std::memory_order_relaxed)) {
            size_t chunk_size = std::min(chunk_dist(rng), stream.size() - offset);
            auto chunk = buffer.append(stream.data() + offset, static_cast<int>(chunk_size));
            bytes_processed.fetch_add(chunk_size, std::memory_order_relaxed);
            packets_aligned.fetch_add(chunk.length / ts::PKT_SIZE, std::memory_order_relaxed);
            offset += chunk_size;

            if (offset >= stream.size()) {
                offset = 0;
                buffer.reset();
            }
        }
    });

    std::this_thread::sleep_for(std::chrono::milliseconds(duration_ms));
    stop_flag.store(true, std::memory_order_release);
    feeder.join();

    EXPECT_GT(bytes_processed.load(), 0);
    std::cout << "Large chunks: " << bytes_processed.load() << " bytes, "
              << packets_aligned.load() << " packets aligned" << std::endl;
}

TEST_F(AlignmentBufferStressTest, ResetUnderLoad) {
    // Stress test reset() being called while append() is happening
    // NOTE: AlignmentBuffer is single-threaded by design, but this tests
    // rapid create/reset cycles
    int duration_ms = getDurationMs();
    auto stream = createTsStream(100);
    std::atomic<int64_t> reset_count{0};

    std::thread worker([&]() {
        std::mt19937 rng(42);
        std::uniform_int_distribution<int> should_reset(0, 99);

        while (!stop_flag.load(std::memory_order_relaxed)) {
            for (size_t offset = 0; offset < stream.size(); offset += ts::PKT_SIZE) {
                auto chunk = buffer.append(stream.data() + offset, ts::PKT_SIZE);
                packets_aligned.fetch_add(chunk.length / ts::PKT_SIZE, std::memory_order_relaxed);

                // Randomly reset (1% chance)
                if (should_reset(rng) == 0) {
                    buffer.reset();
                    reset_count.fetch_add(1, std::memory_order_relaxed);
                    break;
                }
            }
        }
    });

    std::this_thread::sleep_for(std::chrono::milliseconds(duration_ms));
    stop_flag.store(true, std::memory_order_release);
    worker.join();

    EXPECT_GT(reset_count.load(), 0);
    std::cout << "Reset under load: " << reset_count.load() << " resets, "
              << packets_aligned.load() << " packets" << std::endl;
}

// ============================================================================
// Memory Pressure Tests
// ============================================================================

class MemoryPressureTest : public ::testing::Test {
protected:
    std::atomic<bool> stop_flag{false};
};

TEST_F(MemoryPressureTest, AlignmentBufferMemoryStability) {
    // Create and destroy many alignment buffers while processing data
    int duration_ms = getDurationMs();
    auto stream = createTsStream(1000);
    std::atomic<int64_t> buffers_created{0};
    std::atomic<int64_t> bytes_total{0};

    std::thread worker([&]() {
        std::mt19937 rng(42);
        std::uniform_int_distribution<int> buffer_size(4, 128);

        while (!stop_flag.load(std::memory_order_relaxed)) {
            // Create a new buffer with random size
            AlignmentBuffer buf(buffer_size(rng));
            buffers_created.fetch_add(1, std::memory_order_relaxed);

            // Process some data
            for (size_t i = 0; i < stream.size(); i += ts::PKT_SIZE * 10) {
                size_t chunk_size = std::min(static_cast<size_t>(ts::PKT_SIZE * 10),
                                             stream.size() - i);
                auto chunk = buf.append(stream.data() + i, static_cast<int>(chunk_size));
                bytes_total.fetch_add(chunk.length, std::memory_order_relaxed);
            }
            // Buffer goes out of scope and is destroyed
        }
    });

    std::this_thread::sleep_for(std::chrono::milliseconds(duration_ms));
    stop_flag.store(true, std::memory_order_release);
    worker.join();

    EXPECT_GT(buffers_created.load(), 0);
    std::cout << "Buffer memory: " << buffers_created.load() << " buffers created/destroyed, "
              << bytes_total.load() << " bytes processed" << std::endl;
}


// ============================================================================
// Long-Running Stability Tests
// ============================================================================

class LongRunningStabilityTest : public ::testing::Test {
protected:
    std::atomic<bool> stop_flag{false};
};

TEST_F(LongRunningStabilityTest, ContinuousProcessing) {
    // Simulates continuous stream processing with alignment buffer
    int duration_ms = getDurationMs() * 5;  // 5x longer for stability test
    auto stream = createTsStream(10000, 20);  // 20 different PIDs

    AlignmentBuffer buffer(32);
    std::atomic<int64_t> iterations{0};
    std::atomic<int64_t> packets_total{0};

    std::thread worker([&]() {
        size_t offset = 0;
        std::mt19937 rng(42);
        std::uniform_int_distribution<size_t> chunk_dist(1, 4096);

        while (!stop_flag.load(std::memory_order_relaxed)) {
            // Feed chunk to alignment buffer
            size_t chunk_size = std::min(chunk_dist(rng), stream.size() - offset);
            auto aligned = buffer.append(stream.data() + offset, static_cast<int>(chunk_size));

            // Count aligned packets
            packets_total.fetch_add(aligned.length / ts::PKT_SIZE, std::memory_order_relaxed);

            offset += chunk_size;
            if (offset >= stream.size()) {
                offset = 0;
                iterations.fetch_add(1, std::memory_order_relaxed);
            }
        }
    });

    std::this_thread::sleep_for(std::chrono::milliseconds(duration_ms));
    stop_flag.store(true, std::memory_order_release);
    worker.join();

    EXPECT_GT(iterations.load(), 0);
    EXPECT_GT(packets_total.load(), 0);
    std::cout << "Long-running: " << iterations.load() << " iterations, "
              << packets_total.load() << " packets processed" << std::endl;
}

// ============================================================================
// SCTE-35 Monitor Stress Tests
// ============================================================================

class Scte35StressTest : public ::testing::Test {
protected:
    static constexpr int kDefaultDurationMs = 1000;
    std::atomic<bool> stop_flag{false};
    std::atomic<int64_t> events_generated{0};
    std::atomic<int64_t> events_read{0};
    std::atomic<int64_t> state_queries{0};

    int getDurationMs() {
        const char* env = std::getenv("STRESS_DURATION_MS");
        return env ? std::atoi(env) : kDefaultDurationMs;
    }

    // Create a valid SCTE-35 splice_info_section (simplified)
    // Table ID 0xFC, minimal structure for testing
    static std::vector<uint8_t> createScte35Section(uint32_t event_id, bool out_of_network) {
        // Minimal SCTE-35 splice_info_section with splice_insert command
        std::vector<uint8_t> section;

        // Table ID (0xFC for splice_info_section)
        section.push_back(0xFC);

        // Section syntax indicator (0) + private indicator (0) + reserved (3 bits) + section_length (12 bits)
        // Section length placeholder (we'll fill this in)
        section.push_back(0x30);  // 0011 0000 = syntax=0, private=0, reserved=11, length MSB=0
        section.push_back(0x00);  // Length LSB (placeholder)

        // Protocol version (8 bits)
        section.push_back(0x00);

        // encrypted_packet (1) + encryption_algorithm (6) + pts_adjustment (33 bits over 5 bytes)
        section.push_back(0x00);  // No encryption
        section.push_back(0x00);  // PTS adjustment
        section.push_back(0x00);
        section.push_back(0x00);
        section.push_back(0x00);

        // cw_index (8 bits)
        section.push_back(0x00);

        // tier (12 bits) + splice_command_length (12 bits) over 3 bytes
        section.push_back(0xFF);  // tier high
        section.push_back(0xF0);  // tier low + command length high (0x00F)
        section.push_back(0x0F);  // command length low

        // splice_command_type (8 bits) - 0x05 for splice_insert
        section.push_back(0x05);

        // splice_insert command:
        // splice_event_id (32 bits)
        section.push_back(static_cast<uint8_t>((event_id >> 24) & 0xFF));
        section.push_back(static_cast<uint8_t>((event_id >> 16) & 0xFF));
        section.push_back(static_cast<uint8_t>((event_id >> 8) & 0xFF));
        section.push_back(static_cast<uint8_t>(event_id & 0xFF));

        // splice_event_cancel_indicator (1) + reserved (7)
        section.push_back(0x00);  // Not cancelled

        // out_of_network_indicator (1) + program_splice_flag (1) + duration_flag (0) +
        // splice_immediate_flag (1) + reserved (4)
        uint8_t flags = 0x00;
        if (out_of_network) {
            flags |= 0x80;  // out_of_network = 1
        }
        flags |= 0x40;  // program_splice_flag = 1
        flags |= 0x10;  // splice_immediate_flag = 1
        section.push_back(flags);

        // descriptor_loop_length (16 bits)
        section.push_back(0x00);
        section.push_back(0x00);

        // CRC_32 (32 bits) - placeholder
        section.push_back(0x00);
        section.push_back(0x00);
        section.push_back(0x00);
        section.push_back(0x00);

        // Update section length (bytes after section_length field, excluding CRC)
        size_t section_length = section.size() - 3;  // Exclude table_id, section_syntax, section_length
        section[1] = static_cast<uint8_t>(0x30 | ((section_length >> 8) & 0x0F));
        section[2] = static_cast<uint8_t>(section_length & 0xFF);

        return section;
    }

    // Create TS packet with SCTE-35 section payload
    static std::vector<uint8_t> createScte35Packet(uint16_t pid, uint8_t cc,
                                                   const std::vector<uint8_t>& section) {
        std::vector<uint8_t> packet(ts::PKT_SIZE, 0xFF);
        packet[0] = ts::SYNC_BYTE;
        packet[1] = static_cast<uint8_t>(0x40 | ((pid >> 8) & 0x1F));  // PUSI = 1
        packet[2] = static_cast<uint8_t>(pid & 0xFF);
        packet[3] = static_cast<uint8_t>(0x10 | (cc & 0x0F));  // Has payload

        // Pointer field (for section start)
        packet[4] = 0x00;

        // Copy section data (truncate if needed)
        size_t copy_len = std::min(section.size(), static_cast<size_t>(ts::PKT_SIZE - 5));
        std::memcpy(&packet[5], section.data(), copy_len);

        return packet;
    }
};

TEST_F(Scte35StressTest, HighEventRate) {
    ts::DuckContext duck;
    Scte35Monitor monitor(duck);
    int duration_ms = getDurationMs();

    // Register a SCTE-35 PID
    const uint16_t scte35_pid = 0x1234;
    monitor.add_scte35_pid(scte35_pid);

    // Writer thread generating rapid SCTE-35 events
    std::thread writer([&]() {
        uint32_t event_id = 1;
        uint8_t cc = 0;
        while (!stop_flag.load(std::memory_order_relaxed)) {
            // Generate and feed SCTE-35 section
            auto section = createScte35Section(event_id, (event_id % 2) == 0);
            auto packet_data = createScte35Packet(scte35_pid, cc, section);

            ts::TSPacket pkt;
            std::memcpy(&pkt, packet_data.data(), ts::PKT_SIZE);
            monitor.feed_packet(pkt, static_cast<int64_t>(event_id));

            events_generated.fetch_add(1, std::memory_order_relaxed);
            event_id++;
            cc = (cc + 1) & 0x0F;
        }
    });

    // Multiple reader threads
    std::vector<std::thread> readers;
    for (int i = 0; i < 4; i++) {
        readers.emplace_back([&]() {
            while (!stop_flag.load(std::memory_order_relaxed)) {
                // Read events from ring buffer
                std::array<Scte35EventNative, 16> events;
                int32_t count = monitor.get_events(events.data(),
                                                   static_cast<int32_t>(events.size()));
                events_read.fetch_add(count, std::memory_order_relaxed);

                // Query splice state
                auto state = monitor.get_splice_state();
                state_queries.fetch_add(1, std::memory_order_relaxed);

                // Verify state is valid (InContent=0, InBreak=1, Transitioning=2)
                EXPECT_LE(static_cast<int>(state), 2);
            }
        });
    }

    std::this_thread::sleep_for(std::chrono::milliseconds(duration_ms));
    stop_flag.store(true, std::memory_order_release);

    writer.join();
    for (auto& t : readers) {
        t.join();
    }

    EXPECT_GT(events_generated.load(), 0);
    EXPECT_GT(state_queries.load(), 0);
    std::cout << "SCTE-35 stress: " << events_generated.load() << " events generated, "
              << events_read.load() << " events read, "
              << state_queries.load() << " state queries" << std::endl;
}

TEST_F(Scte35StressTest, RapidStateChanges) {
    ts::DuckContext duck;
    Scte35Monitor monitor(duck);
    int duration_ms = getDurationMs();

    const uint16_t scte35_pid = 0x1234;
    monitor.add_scte35_pid(scte35_pid);

    std::atomic<int64_t> state_changes{0};
    std::atomic<int64_t> invalid_states{0};

    // Writer thread alternating between InContent and InBreak
    std::thread writer([&]() {
        uint32_t event_id = 1;
        uint8_t cc = 0;
        while (!stop_flag.load(std::memory_order_relaxed)) {
            // Alternate: even = out_of_network (InBreak), odd = return (InContent)
            bool out_of_network = (event_id % 2) == 0;
            auto section = createScte35Section(event_id, out_of_network);
            auto packet_data = createScte35Packet(scte35_pid, cc, section);

            ts::TSPacket pkt;
            std::memcpy(&pkt, packet_data.data(), ts::PKT_SIZE);
            monitor.feed_packet(pkt, static_cast<int64_t>(event_id));

            state_changes.fetch_add(1, std::memory_order_relaxed);
            event_id++;
            cc = (cc + 1) & 0x0F;
        }
    });

    // Reader threads verifying state consistency
    std::vector<std::thread> readers;
    for (int i = 0; i < 4; i++) {
        readers.emplace_back([&]() {
            while (!stop_flag.load(std::memory_order_relaxed)) {
                auto state = monitor.get_splice_state();

                // State must be one of the valid enum values
                int state_val = static_cast<int>(state);
                if (state_val < 0 || state_val > 2) {
                    invalid_states.fetch_add(1, std::memory_order_relaxed);
                }

                // Check if in ad break flag is consistent with state
                bool in_break = monitor.is_in_ad_break();
                if (in_break && state != SpliceState::InBreak) {
                    // This is okay during transition - state may change between calls
                }
            }
        });
    }

    std::this_thread::sleep_for(std::chrono::milliseconds(duration_ms));
    stop_flag.store(true, std::memory_order_release);

    writer.join();
    for (auto& t : readers) {
        t.join();
    }

    EXPECT_EQ(invalid_states.load(), 0)
        << "Detected " << invalid_states.load() << " invalid states";
    EXPECT_GT(state_changes.load(), 0);
    std::cout << "SCTE-35 state stress: " << state_changes.load() << " state changes, "
              << invalid_states.load() << " invalid states" << std::endl;
}

TEST_F(Scte35StressTest, EventBufferOverflow) {
    ts::DuckContext duck;
    Scte35Monitor monitor(duck);
    int duration_ms = getDurationMs();

    const uint16_t scte35_pid = 0x1234;
    monitor.add_scte35_pid(scte35_pid);

    std::atomic<int64_t> overflow_events{0};

    // Writer thread generating more events than buffer size
    std::thread writer([&]() {
        uint32_t event_id = 1;
        uint8_t cc = 0;

        // Generate 2x the buffer size to ensure overflow
        while (!stop_flag.load(std::memory_order_relaxed)) {
            auto section = createScte35Section(event_id, (event_id % 2) == 0);
            auto packet_data = createScte35Packet(scte35_pid, cc, section);

            ts::TSPacket pkt;
            std::memcpy(&pkt, packet_data.data(), ts::PKT_SIZE);
            monitor.feed_packet(pkt, static_cast<int64_t>(event_id));

            if (event_id > SCTE35_EVENT_BUFFER_SIZE) {
                overflow_events.fetch_add(1, std::memory_order_relaxed);
            }

            events_generated.fetch_add(1, std::memory_order_relaxed);
            event_id++;
            cc = (cc + 1) & 0x0F;
        }
    });

    // Reader thread checking for memory corruption
    std::thread reader([&]() {
        while (!stop_flag.load(std::memory_order_relaxed)) {
            std::array<Scte35EventNative, 16> events;
            int32_t count = monitor.get_events(events.data(),
                                               static_cast<int32_t>(events.size()));

            // Verify all returned events have valid structure
            for (int32_t i = 0; i < count; i++) {
                // Event ID should be positive
                EXPECT_GT(events[i].splice_event_id, 0u);
                // Command type should be 0x05 (splice_insert)
                EXPECT_EQ(events[i].splice_command_type, 0x05);
                // PID should match
                EXPECT_EQ(events[i].scte35_pid, scte35_pid);
            }
            events_read.fetch_add(count, std::memory_order_relaxed);
        }
    });

    std::this_thread::sleep_for(std::chrono::milliseconds(duration_ms));
    stop_flag.store(true, std::memory_order_release);

    writer.join();
    reader.join();

    EXPECT_GT(overflow_events.load(), 0)
        << "Test should generate overflow conditions";
    EXPECT_GT(events_generated.load(), static_cast<int64_t>(SCTE35_EVENT_BUFFER_SIZE));
    std::cout << "SCTE-35 overflow stress: " << events_generated.load() << " events, "
              << overflow_events.load() << " overflow events, "
              << events_read.load() << " events read" << std::endl;
}

// ============================================================================
// NAL Parser Stress Tests
// ============================================================================

class NalParserStressTest : public ::testing::Test {
protected:
    static constexpr int kDefaultDurationMs = 1000;
    std::atomic<bool> stop_flag{false};
    std::atomic<int64_t> sps_updates{0};
    std::atomic<int64_t> pps_updates{0};
    std::atomic<int64_t> idr_count{0};
    std::atomic<int64_t> param_reads{0};

    int getDurationMs() {
        const char* env = std::getenv("STRESS_DURATION_MS");
        return env ? std::atoi(env) : kDefaultDurationMs;
    }

    // Create minimal H.264 SPS NAL unit (simplified for testing)
    static std::vector<uint8_t> createH264Sps(uint8_t profile_idc = 100,
                                               uint8_t level_idc = 40) {
        std::vector<uint8_t> nal;
        // NAL header: forbidden_zero_bit(0) + nal_ref_idc(3) + nal_unit_type(7=SPS)
        nal.push_back(0x67);  // 0110 0111

        // profile_idc
        nal.push_back(profile_idc);

        // constraint_set flags + reserved_zero_2bits + level_idc
        nal.push_back(0x00);  // constraint flags
        nal.push_back(level_idc);

        // seq_parameter_set_id (ue(v)) = 0, encoded as single bit 1
        // log2_max_frame_num_minus4 = 0
        // pic_order_cnt_type = 0
        // ... minimal RBSP follows
        nal.push_back(0xAC);  // SPS RBSP data
        nal.push_back(0x56);
        nal.push_back(0xBD);
        nal.push_back(0x00);
        nal.push_back(0x00);

        return nal;
    }

    // Create minimal H.264 PPS NAL unit
    static std::vector<uint8_t> createH264Pps() {
        std::vector<uint8_t> nal;
        // NAL header: forbidden_zero_bit(0) + nal_ref_idc(3) + nal_unit_type(8=PPS)
        nal.push_back(0x68);  // 0110 1000

        // pic_parameter_set_id = 0, seq_parameter_set_id = 0
        nal.push_back(0xCE);  // PPS RBSP data
        nal.push_back(0x3C);
        nal.push_back(0x80);

        return nal;
    }

    // Create H.264 IDR NAL unit header
    static std::vector<uint8_t> createH264Idr() {
        std::vector<uint8_t> nal;
        // NAL header: forbidden_zero_bit(0) + nal_ref_idc(3) + nal_unit_type(5=IDR)
        nal.push_back(0x65);  // 0110 0101

        // Minimal IDR slice data
        nal.push_back(0x88);
        nal.push_back(0x84);
        nal.push_back(0x21);

        return nal;
    }

    // Create PES packet with NAL units
    static std::vector<uint8_t> createVideoPesPacket(uint16_t pid, uint8_t cc,
                                                      const std::vector<uint8_t>& nal_data) {
        std::vector<uint8_t> packet(ts::PKT_SIZE, 0xFF);
        packet[0] = ts::SYNC_BYTE;
        packet[1] = static_cast<uint8_t>(0x40 | ((pid >> 8) & 0x1F));  // PUSI = 1
        packet[2] = static_cast<uint8_t>(pid & 0xFF);
        packet[3] = static_cast<uint8_t>(0x10 | (cc & 0x0F));  // Has payload

        // PES header (simplified)
        packet[4] = 0x00;  // packet_start_code_prefix
        packet[5] = 0x00;
        packet[6] = 0x01;
        packet[7] = 0xE0;  // stream_id for video
        packet[8] = 0x00;  // PES_packet_length (0 = unbounded)
        packet[9] = 0x00;

        // PES header data
        packet[10] = 0x80;  // marker bits + PTS_DTS_flags(10) + other flags
        packet[11] = 0x80;  // PTS only
        packet[12] = 0x05;  // PES_header_data_length

        // PTS (5 bytes)
        packet[13] = 0x21;  // 0010 | PTS[32..30] | marker
        packet[14] = 0x00;  // PTS[29..22]
        packet[15] = 0x01;  // PTS[21..15] | marker
        packet[16] = 0x00;  // PTS[14..7]
        packet[17] = 0x01;  // PTS[6..0] | marker

        // NAL start code + NAL data
        size_t offset = 18;
        if (offset + 4 + nal_data.size() <= ts::PKT_SIZE) {
            packet[offset++] = 0x00;
            packet[offset++] = 0x00;
            packet[offset++] = 0x00;
            packet[offset++] = 0x01;
            std::memcpy(&packet[offset], nal_data.data(),
                       std::min(nal_data.size(), ts::PKT_SIZE - offset));
        }

        return packet;
    }
};

TEST_F(NalParserStressTest, RapidParameterSetUpdates) {
    ts::DuckContext duck;
    NalParser parser(duck);
    int duration_ms = getDurationMs();

    const uint16_t video_pid = 0x100;
    parser.add_video_pid(video_pid, 0x1B);  // H.264

    std::atomic<int64_t> incomplete_reads{0};

    // Writer thread feeding continuous SPS/PPS updates
    std::thread writer([&]() {
        uint8_t cc = 0;
        int64_t packet_idx = 0;
        uint8_t profile = 100;

        while (!stop_flag.load(std::memory_order_relaxed)) {
            // Alternate between SPS and PPS
            if ((packet_idx % 2) == 0) {
                auto sps = createH264Sps(profile, 40);
                auto packet = createVideoPesPacket(video_pid, cc, sps);
                ts::TSPacket pkt;
                std::memcpy(&pkt, packet.data(), ts::PKT_SIZE);
                parser.process_pes_start(pkt, video_pid, packet_idx);
                sps_updates.fetch_add(1, std::memory_order_relaxed);
                profile = static_cast<uint8_t>((profile + 1) % 256);
            } else {
                auto pps = createH264Pps();
                auto packet = createVideoPesPacket(video_pid, cc, pps);
                ts::TSPacket pkt;
                std::memcpy(&pkt, packet.data(), ts::PKT_SIZE);
                parser.process_pes_start(pkt, video_pid, packet_idx);
                pps_updates.fetch_add(1, std::memory_order_relaxed);
            }

            cc = (cc + 1) & 0x0F;
            packet_idx++;
        }
    });

    // Reader threads calling get_parameter_sets()
    std::vector<std::thread> readers;
    for (int i = 0; i < 4; i++) {
        readers.emplace_back([&]() {
            while (!stop_flag.load(std::memory_order_relaxed)) {
                NalParameterSetsNative params{};
                bool has_params = parser.get_parameter_sets(video_pid, &params);
                param_reads.fetch_add(1, std::memory_order_relaxed);

                if (has_params) {
                    // Verify parameter sets are either complete or empty (no partial reads)
                    bool sps_valid = (params.sps_length == 0) ||
                                    (params.sps_length > 0 && params.sps_data[0] == 0x67);
                    bool pps_valid = (params.pps_length == 0) ||
                                    (params.pps_length > 0 && params.pps_data[0] == 0x68);

                    if (!sps_valid || !pps_valid) {
                        incomplete_reads.fetch_add(1, std::memory_order_relaxed);
                    }
                }
            }
        });
    }

    std::this_thread::sleep_for(std::chrono::milliseconds(duration_ms));
    stop_flag.store(true, std::memory_order_release);

    writer.join();
    for (auto& t : readers) {
        t.join();
    }

    EXPECT_EQ(incomplete_reads.load(), 0)
        << "Detected " << incomplete_reads.load() << " incomplete/corrupt parameter set reads";
    EXPECT_GT(sps_updates.load(), 0);
    EXPECT_GT(pps_updates.load(), 0);
    std::cout << "NAL param stress: " << sps_updates.load() << " SPS updates, "
              << pps_updates.load() << " PPS updates, "
              << param_reads.load() << " reads, "
              << incomplete_reads.load() << " incomplete" << std::endl;
}

TEST_F(NalParserStressTest, HighIdrRate) {
    ts::DuckContext duck;
    NalParser parser(duck);
    int duration_ms = getDurationMs();

    const uint16_t video_pid = 0x100;
    parser.add_video_pid(video_pid, 0x1B);  // H.264

    std::atomic<int64_t> idr_checks{0};
    std::atomic<int64_t> monotonic_violations{0};

    // Writer thread injecting rapid IDR frames
    std::thread writer([&]() {
        uint8_t cc = 0;
        int64_t packet_idx = 0;

        while (!stop_flag.load(std::memory_order_relaxed)) {
            auto idr = createH264Idr();
            auto packet = createVideoPesPacket(video_pid, cc, idr);
            ts::TSPacket pkt;
            std::memcpy(&pkt, packet.data(), ts::PKT_SIZE);
            parser.process_pes_start(pkt, video_pid, packet_idx);

            idr_count.fetch_add(1, std::memory_order_relaxed);
            cc = (cc + 1) & 0x0F;
            packet_idx++;
        }
    });

    // Reader threads checking IDR frame detection
    std::vector<std::thread> readers;
    for (int i = 0; i < 4; i++) {
        readers.emplace_back([&]() {
            int64_t last_total = 0;

            while (!stop_flag.load(std::memory_order_relaxed)) {
                bool has_idr = parser.check_idr_frame(video_pid);
                int64_t total = parser.total_idr_frames.load(std::memory_order_relaxed);
                idr_checks.fetch_add(1, std::memory_order_relaxed);

                // Total IDR count should be monotonically increasing
                if (total < last_total) {
                    monotonic_violations.fetch_add(1, std::memory_order_relaxed);
                }
                last_total = total;

                (void)has_idr;  // Used for check
            }
        });
    }

    std::this_thread::sleep_for(std::chrono::milliseconds(duration_ms));
    stop_flag.store(true, std::memory_order_release);

    writer.join();
    for (auto& t : readers) {
        t.join();
    }

    EXPECT_EQ(monotonic_violations.load(), 0)
        << "IDR count decreased during test";
    EXPECT_GT(idr_count.load(), 0);
    std::cout << "NAL IDR stress: " << idr_count.load() << " IDRs injected, "
              << idr_checks.load() << " checks, "
              << monotonic_violations.load() << " monotonic violations" << std::endl;
}

TEST_F(NalParserStressTest, MultiStreamProcessing) {
    ts::DuckContext duck;
    NalParser parser(duck);
    int duration_ms = getDurationMs();

    // Register multiple video PIDs
    const uint16_t video_pids[] = {0x100, 0x101, 0x102, 0x103};
    const size_t num_pids = sizeof(video_pids) / sizeof(video_pids[0]);

    for (auto pid : video_pids) {
        parser.add_video_pid(pid, 0x1B);  // All H.264
    }

    std::atomic<int64_t> cross_contamination{0};
    std::array<std::atomic<int64_t>, 4> per_pid_updates{};
    for (auto& a : per_pid_updates) a.store(0);

    // Writer threads - one per PID
    std::vector<std::thread> writers;
    for (size_t p = 0; p < num_pids; p++) {
        writers.emplace_back([&, p]() {
            uint8_t cc = 0;
            int64_t packet_idx = static_cast<int64_t>(p) * 1000000;  // Different base per stream
            uint8_t unique_marker = static_cast<uint8_t>(0xA0 + p);  // Unique per stream

            while (!stop_flag.load(std::memory_order_relaxed)) {
                // Create SPS with stream-unique profile for identification
                auto sps = createH264Sps(unique_marker, 40);
                auto packet = createVideoPesPacket(video_pids[p], cc, sps);
                ts::TSPacket pkt;
                std::memcpy(&pkt, packet.data(), ts::PKT_SIZE);
                parser.process_pes_start(pkt, video_pids[p], packet_idx);

                per_pid_updates[p].fetch_add(1, std::memory_order_relaxed);
                cc = (cc + 1) & 0x0F;
                packet_idx++;
            }
        });
    }

    // Reader threads - verify stream isolation
    std::vector<std::thread> readers;
    for (size_t p = 0; p < num_pids; p++) {
        readers.emplace_back([&, p]() {
            uint8_t expected_marker = static_cast<uint8_t>(0xA0 + p);

            while (!stop_flag.load(std::memory_order_relaxed)) {
                NalParameterSetsNative params{};
                bool has_params = parser.get_parameter_sets(video_pids[p], &params);

                if (has_params && params.sps_length > 1) {
                    // Check that the profile byte matches our expected marker
                    // (profile_idc is at byte 1 of SPS NAL)
                    uint8_t profile = params.sps_data[1];
                    if (profile != expected_marker && profile >= 0xA0 && profile < 0xA4) {
                        // Got another stream's data - cross contamination!
                        cross_contamination.fetch_add(1, std::memory_order_relaxed);
                    }
                }
                param_reads.fetch_add(1, std::memory_order_relaxed);
            }
        });
    }

    std::this_thread::sleep_for(std::chrono::milliseconds(duration_ms));
    stop_flag.store(true, std::memory_order_release);

    for (auto& t : writers) {
        t.join();
    }
    for (auto& t : readers) {
        t.join();
    }

    EXPECT_EQ(cross_contamination.load(), 0)
        << "Detected cross-stream contamination: PID A updates corrupting PID B";

    int64_t total_updates = 0;
    for (size_t p = 0; p < num_pids; p++) {
        total_updates += per_pid_updates[p].load();
    }

    EXPECT_GT(total_updates, 0);
    std::cout << "NAL multi-stream stress: " << total_updates << " total updates, "
              << param_reads.load() << " reads, "
              << cross_contamination.load() << " cross-contamination" << std::endl;
}

// ============================================================================
// Combined Stress Tests
// ============================================================================

class CombinedStressTest : public ::testing::Test {
protected:
    static constexpr int kDefaultDurationMs = 1000;
    std::atomic<bool> stop_flag{false};

    int getDurationMs() {
        const char* env = std::getenv("STRESS_DURATION_MS");
        return env ? std::atoi(env) : kDefaultDurationMs;
    }
};

TEST_F(CombinedStressTest, AnalyzerFullLoad) {
    // Create TsDuck context and analyzer
    // Use fully qualified names to avoid ambiguity with C API forward declarations
    auto context = std::make_unique<context::TsDuckContext>();
    TsDuckConfigNative config{
        .metrics_interval_ms = 100,
        .enable_tr101290 = 1,
        .sample_size_bytes = static_cast<int32_t>(ts::PKT_SIZE) * 1000,
        .enable_auto_restamp = 0,
        .restamp_mode = RESTAMP_MODE_DISABLED,
        .smooth_pcr = 0,
        .fix_discontinuities = 0,
        .reserved = 0,
        .correction_threshold_ms = 45.0,
        .max_correction_rate_ms = 10.0,
        .hysteresis_threshold_ms = 20.0,
        .stream_bitrate_hint = 0
    };

    context::TsDuckAnalyzer analyzer(context.get(), &config);

    int duration_ms = getDurationMs();

    std::atomic<int64_t> packets_fed{0};
    std::atomic<int64_t> metrics_reads{0};
    std::atomic<int64_t> pcr_reads{0};
    std::atomic<int64_t> scte35_reads{0};
    std::atomic<int64_t> nal_reads{0};
    std::atomic<int64_t> pid_reads{0};

    // Register video and SCTE-35 PIDs
    const uint16_t video_pid = 0x100;
    const uint16_t scte35_pid = 0x200;
    const uint16_t pcr_pid = 0x1FFF;  // Use null PID as PCR for simplicity

    analyzer.nal_parser.add_video_pid(video_pid, 0x1B);
    analyzer.scte35.add_scte35_pid(scte35_pid);

    // Writer thread feeding mixed packets
    std::thread writer([&]() {
        std::mt19937 rng(42);
        std::uniform_int_distribution<int> packet_type(0, 3);

        uint8_t video_cc = 0;
        uint8_t scte35_cc = 0;
        uint8_t null_cc = 0;
        int64_t packet_idx = 0;

        while (!stop_flag.load(std::memory_order_relaxed)) {
            std::vector<uint8_t> stream;
            stream.reserve(ts::PKT_SIZE * 10);

            // Generate batch of mixed packets
            for (int i = 0; i < 10; i++) {
                std::vector<uint8_t> pkt(ts::PKT_SIZE, 0xFF);
                int type = packet_type(rng);

                switch (type) {
                    case 0:  // Video packet
                        pkt[0] = ts::SYNC_BYTE;
                        pkt[1] = static_cast<uint8_t>((video_pid >> 8) & 0x1F);
                        pkt[2] = static_cast<uint8_t>(video_pid & 0xFF);
                        pkt[3] = static_cast<uint8_t>(0x10 | (video_cc & 0x0F));
                        video_cc = (video_cc + 1) & 0x0F;
                        break;

                    case 1:  // SCTE-35 packet
                        pkt[0] = ts::SYNC_BYTE;
                        pkt[1] = static_cast<uint8_t>((scte35_pid >> 8) & 0x1F);
                        pkt[2] = static_cast<uint8_t>(scte35_pid & 0xFF);
                        pkt[3] = static_cast<uint8_t>(0x10 | (scte35_cc & 0x0F));
                        scte35_cc = (scte35_cc + 1) & 0x0F;
                        break;

                    case 2:  // Null packet
                        pkt[0] = ts::SYNC_BYTE;
                        pkt[1] = 0x1F;
                        pkt[2] = 0xFF;
                        pkt[3] = static_cast<uint8_t>(0x10 | (null_cc & 0x0F));
                        null_cc = (null_cc + 1) & 0x0F;
                        break;

                    default:  // PCR packet
                        pkt[0] = ts::SYNC_BYTE;
                        pkt[1] = static_cast<uint8_t>(0x40 | ((video_pid >> 8) & 0x1F));  // PUSI
                        pkt[2] = static_cast<uint8_t>(video_pid & 0xFF);
                        pkt[3] = static_cast<uint8_t>(0x30 | (video_cc & 0x0F));  // Adaptation + payload
                        pkt[4] = 7;  // Adaptation field length
                        pkt[5] = 0x10;  // PCR flag
                        // PCR value (6 bytes)
                        pkt[6] = 0x00;
                        pkt[7] = 0x00;
                        pkt[8] = 0x00;
                        pkt[9] = 0x01;
                        pkt[10] = 0x00;
                        pkt[11] = 0x00;
                        video_cc = (video_cc + 1) & 0x0F;
                        break;
                }

                stream.insert(stream.end(), pkt.begin(), pkt.end());
            }

            // Feed to analyzer
            int32_t processed = analyzer.feed(stream.data(),
                                              static_cast<int32_t>(stream.size()));
            if (processed > 0) {
                packets_fed.fetch_add(processed, std::memory_order_relaxed);
            }
            packet_idx += 10;
        }
    });

    // Multiple reader threads querying all metrics
    std::vector<std::thread> readers;
    for (int i = 0; i < 4; i++) {
        readers.emplace_back([&, i]() {
            while (!stop_flag.load(std::memory_order_relaxed)) {
                // Rotate through different query types based on thread ID and iteration
                switch (i % 4) {
                    case 0: {
                        TsDuckMetricsNative m{};
                        analyzer.get_metrics(&m);
                        metrics_reads.fetch_add(1, std::memory_order_relaxed);
                        break;
                    }
                    case 1: {
                        PcrAnalysisNative pcr{};
                        analyzer.pcr.get(&pcr);
                        pcr_reads.fetch_add(1, std::memory_order_relaxed);
                        break;
                    }
                    case 2: {
                        std::array<Scte35EventNative, 8> events;
                        analyzer.scte35.get_events(events.data(),
                                                   static_cast<int32_t>(events.size()));
                        scte35_reads.fetch_add(1, std::memory_order_relaxed);
                        break;
                    }
                    case 3: {
                        NalParameterSetsNative params{};
                        analyzer.nal_parser.get_parameter_sets(video_pid, &params);
                        nal_reads.fetch_add(1, std::memory_order_relaxed);
                        break;
                    }
                }

                // Also query PID stats
                TsDuckPidInfoExtended pids[16];
                analyzer.pids.get_count(pids, 16);
                pid_reads.fetch_add(1, std::memory_order_relaxed);
            }
        });
    }

    std::this_thread::sleep_for(std::chrono::milliseconds(duration_ms));
    stop_flag.store(true, std::memory_order_release);

    writer.join();
    for (auto& t : readers) {
        t.join();
    }

    EXPECT_GT(packets_fed.load(), 0);
    EXPECT_GT(metrics_reads.load(), 0);

    std::cout << "Combined stress: " << packets_fed.load() << " packets fed" << std::endl;
    std::cout << "  Metrics reads: " << metrics_reads.load() << std::endl;
    std::cout << "  PCR reads: " << pcr_reads.load() << std::endl;
    std::cout << "  SCTE-35 reads: " << scte35_reads.load() << std::endl;
    std::cout << "  NAL reads: " << nal_reads.load() << std::endl;
    std::cout << "  PID reads: " << pid_reads.load() << std::endl;
}

// ============================================================================
// Main
// ============================================================================

int main(int argc, char** argv) {
    ::testing::InitGoogleTest(&argc, argv);
    std::cout << "Stress test duration: " << getDurationMs() << " ms" << std::endl;
    std::cout << "(Set STRESS_DURATION_MS environment variable to override)" << std::endl;
    return RUN_ALL_TESTS();
}
