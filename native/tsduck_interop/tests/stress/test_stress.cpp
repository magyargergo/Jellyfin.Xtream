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

#include <gtest/gtest.h>
#include <atomic>
#include <chrono>
#include <random>
#include <thread>
#include <vector>

#include "concurrency/seqlock.hpp"
#include "streaming/alignment_buffer.hpp"
#include "core/constants.hpp"

using namespace tsduck_interop;
using namespace tsduck_interop::concurrency;
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
// Main
// ============================================================================

int main(int argc, char** argv) {
    ::testing::InitGoogleTest(&argc, argv);
    std::cout << "Stress test duration: " << getDurationMs() << " ms" << std::endl;
    std::cout << "(Set STRESS_DURATION_MS environment variable to override)" << std::endl;
    return RUN_ALL_TESTS();
}
