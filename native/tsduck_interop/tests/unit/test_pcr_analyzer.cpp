// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#include <gtest/gtest.h>
#include <thread>
#include <chrono>
#include "analysis/pcr_analyzer.hpp"

using namespace tsduck_interop::analysis;

class PcrAnalyzerTest : public ::testing::Test {
protected:
    PcrAnalyzer analyzer;

    void SetUp() override {
        analyzer.reset();
    }

    int64_t now_ns() {
        return std::chrono::duration_cast<std::chrono::nanoseconds>(
            std::chrono::steady_clock::now().time_since_epoch()
        ).count();
    }
};

// ============================================================================
// Basic Functionality Tests
// ============================================================================

TEST_F(PcrAnalyzerTest, InitialStateIsEmpty) {
    PcrAnalysisNative data;
    EXPECT_FALSE(analyzer.get(&data));  // Returns false when no PCRs
    EXPECT_EQ(data.pcr_count, 0);
}

TEST_F(PcrAnalyzerTest, FirstPcrInitializesState) {
    int64_t time = now_ns();
    uint64_t pcr = 27000000;  // 1 second in 27MHz ticks

    analyzer.process(pcr, 0, time);

    PcrAnalysisNative data;
    EXPECT_TRUE(analyzer.get(&data));
    EXPECT_EQ(data.pcr_count, 1);
    EXPECT_EQ(data.pcr_valid_count, 1);
}

TEST_F(PcrAnalyzerTest, PcrCountIncrements) {
    int64_t time = now_ns();
    uint64_t pcr = 0;

    for (int i = 0; i < 10; i++) {
        pcr += 2700000;  // 100ms intervals
        time += 100000000;  // 100ms in ns
        analyzer.process(pcr, i * 100, time);
    }

    PcrAnalysisNative data;
    analyzer.get(&data);
    EXPECT_EQ(data.pcr_count, 10);
}

// ============================================================================
// PCR Interval Tests
// ============================================================================

TEST_F(PcrAnalyzerTest, CalculatesPcrInterval) {
    int64_t time1 = now_ns();
    uint64_t pcr1 = 27000000;
    analyzer.process(pcr1, 0, time1);

    // 100ms later (2.7M ticks)
    int64_t time2 = time1 + 100000000;  // 100ms in ns
    uint64_t pcr2 = pcr1 + 2700000;
    analyzer.process(pcr2, 100, time2);

    PcrAnalysisNative data;
    analyzer.get(&data);

    EXPECT_NEAR(data.pcr_interval_ms, 100.0, 0.1);
    EXPECT_EQ(data.pcr_interval_packets, 100);
}

TEST_F(PcrAnalyzerTest, HandlesPcrWraparound) {
    int64_t time1 = now_ns();
    uint64_t pcr1 = (1ULL << 42) - 1000000;  // Near max
    analyzer.process(pcr1, 0, time1);

    // Wrap around
    int64_t time2 = time1 + 37037037;  // ~37ms
    uint64_t pcr2 = 1000000;  // Wrapped value
    analyzer.process(pcr2, 100, time2);

    PcrAnalysisNative data;
    analyzer.get(&data);

    // Should handle wraparound correctly
    EXPECT_GT(data.pcr_interval_ms, 0);
    EXPECT_LT(data.pcr_interval_ms, 1000);  // Should be reasonable
}

// ============================================================================
// Jitter Tests
// ============================================================================

TEST_F(PcrAnalyzerTest, CalculatesJitter) {
    int64_t base_time = now_ns();
    uint64_t pcr = 0;

    // First PCR
    analyzer.process(pcr, 0, base_time);

    // Second PCR - perfect timing (no jitter expected)
    pcr += 2700000;  // 100ms in PCR ticks
    int64_t time2 = base_time + 100000000;  // 100ms in ns (perfect)
    analyzer.process(pcr, 100, time2);

    PcrAnalysisNative data;
    analyzer.get(&data);

    EXPECT_NEAR(data.pcr_jitter_us, 0, 100);  // Allow small tolerance
}

TEST_F(PcrAnalyzerTest, DetectsHighJitter) {
    int64_t base_time = now_ns();
    uint64_t pcr = 0;

    // First PCR
    analyzer.process(pcr, 0, base_time);

    // Second PCR - late arrival (10ms jitter)
    pcr += 2700000;  // 100ms in PCR ticks
    int64_t time2 = base_time + 110000000;  // 110ms - 10ms late
    analyzer.process(pcr, 100, time2);

    PcrAnalysisNative data;
    analyzer.get(&data);

    EXPECT_NEAR(data.pcr_jitter_us, 10000, 1000);  // ~10ms jitter
}

TEST_F(PcrAnalyzerTest, TracksMaxJitter) {
    int64_t base_time = now_ns();
    uint64_t pcr = 0;

    // First PCR
    analyzer.process(pcr, 0, base_time);

    // Several PCRs with varying jitter
    for (int i = 1; i <= 5; i++) {
        pcr += 2700000;  // 100ms
        int64_t jitter_ns = (i == 3) ? 50000000 : 1000000;  // 50ms on iteration 3
        int64_t time = base_time + (i * 100000000) + jitter_ns;
        analyzer.process(pcr, i * 100, time);
    }

    PcrAnalysisNative data;
    analyzer.get(&data);

    EXPECT_GT(data.pcr_jitter_max_us, 40000);  // Max should be ~50ms
}

TEST_F(PcrAnalyzerTest, CalculatesAverageJitter) {
    int64_t base_time = now_ns();
    uint64_t pcr = 0;

    // First PCR
    analyzer.process(pcr, 0, base_time);

    // Multiple PCRs with consistent 1ms jitter
    // PCR says 100ms intervals, but wall clock shows 101ms intervals
    // This creates 1ms jitter on each measurement
    for (int i = 1; i <= 10; i++) {
        pcr += 2700000;  // 100ms in PCR ticks
        // Each interval is 101ms (100ms expected + 1ms late = 1ms jitter)
        int64_t time = base_time + (i * 101000000LL);
        analyzer.process(pcr, i * 100, time);
    }

    PcrAnalysisNative data;
    analyzer.get(&data);

    EXPECT_NEAR(data.pcr_jitter_avg_us, 1000, 200);  // ~1ms average
}

// ============================================================================
// Drift Tests
// ============================================================================

TEST_F(PcrAnalyzerTest, CalculatesDrift) {
    int64_t base_time = now_ns();
    uint64_t pcr = 0;

    // First PCR
    analyzer.process(pcr, 0, base_time);

    // Simulate 2 seconds of data with slight drift
    // PCR runs 100ppm fast
    for (int i = 1; i <= 20; i++) {
        int64_t ideal_pcr_increment = 2700000;  // 100ms
        pcr += ideal_pcr_increment + 270;  // +100ppm
        int64_t time = base_time + (i * 100000000LL);
        analyzer.process(pcr, i * 100, time);
    }

    PcrAnalysisNative data;
    analyzer.get(&data);

    // Drift should be approximately 100ppm
    EXPECT_NEAR(data.pcr_drift_ppm, 100, 50);
}

// ============================================================================
// Reset Tests
// ============================================================================

TEST_F(PcrAnalyzerTest, ResetClearsAllState) {
    int64_t time = now_ns();
    analyzer.process(27000000, 0, time);
    analyzer.process(29700000, 100, time + 100000000);

    analyzer.reset();

    PcrAnalysisNative data;
    EXPECT_FALSE(analyzer.get(&data));
    EXPECT_EQ(data.pcr_count, 0);
    EXPECT_EQ(data.pcr_valid_count, 0);
    EXPECT_EQ(data.pcr_jitter_max_us, 0);
}

// ============================================================================
// LastPcrBase90khz Tests
// ============================================================================

TEST_F(PcrAnalyzerTest, LastPcrBase90khzConvertsCorrectly) {
    // Before any PCR
    EXPECT_EQ(analyzer.lastPcrBase90khz(), -1);

    // Process a PCR (27MHz value)
    int64_t time = now_ns();
    uint64_t pcr_27mhz = 27000000;  // 1 second in 27MHz
    analyzer.process(pcr_27mhz, 0, time);

    // Should convert to 90kHz (divide by 300)
    int64_t pcr_90khz = analyzer.lastPcrBase90khz();
    EXPECT_EQ(pcr_90khz, 90000);  // 1 second in 90kHz
}

// ============================================================================
// Concurrent Access Tests
// ============================================================================

TEST_F(PcrAnalyzerTest, ConcurrentReadWrite) {
    std::atomic<bool> stop{false};
    std::atomic<int> successful_reads{0};

    // Writer thread
    std::thread writer([&]() {
        int64_t time = now_ns();
        uint64_t pcr = 0;
        for (int i = 0; i < 10000 && !stop.load(); i++) {
            pcr += 270000;  // 10ms intervals
            time += 10000000;
            analyzer.process(pcr, i * 10, time);
        }
        stop.store(true);
    });

    // Reader thread
    std::thread reader([&]() {
        while (!stop.load()) {
            PcrAnalysisNative data;
            if (analyzer.get(&data)) {
                // Verify data consistency
                if (data.pcr_count >= data.pcr_valid_count) {
                    successful_reads.fetch_add(1);
                }
            }
        }
    });

    writer.join();
    reader.join();

    EXPECT_GT(successful_reads.load(), 0);
}

TEST_F(PcrAnalyzerTest, GetHandlesNullPointer) {
    EXPECT_FALSE(analyzer.get(nullptr));
}

int main(int argc, char **argv) {
    ::testing::InitGoogleTest(&argc, argv);
    return RUN_ALL_TESTS();
}
