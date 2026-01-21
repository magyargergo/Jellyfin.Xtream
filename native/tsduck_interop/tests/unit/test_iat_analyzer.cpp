// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#include <gtest/gtest.h>
#include <thread>
#include <chrono>
#include "analysis/iat_analyzer.hpp"

using namespace tsduck_interop::analysis;

class IatAnalyzerTest : public ::testing::Test {
protected:
    IatAnalyzer analyzer;

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

TEST_F(IatAnalyzerTest, InitialStateIsEmpty) {
    IatAnalysisNative data;
    EXPECT_FALSE(analyzer.get(&data));  // Returns false when no data
}

TEST_F(IatAnalyzerTest, FirstPacketInitializesState) {
    int64_t time = now_ns();
    analyzer.process(time);

    IatAnalysisNative data;
    EXPECT_FALSE(analyzer.get(&data));  // Still false after first packet
}

TEST_F(IatAnalyzerTest, SecondPacketStartsAnalysis) {
    int64_t time1 = now_ns();
    analyzer.process(time1);

    int64_t time2 = time1 + 1000000;  // 1ms later
    analyzer.process(time2);

    IatAnalysisNative data;
    EXPECT_TRUE(analyzer.get(&data));  // Now we have data
}

// ============================================================================
// IAT Calculation Tests
// ============================================================================

TEST_F(IatAnalyzerTest, CalculatesCorrectIat) {
    int64_t time = now_ns();
    analyzer.process(time);

    // 1000us (1ms) interval
    analyzer.process(time + 1000000);

    IatAnalysisNative data;
    analyzer.get(&data);

    EXPECT_NEAR(data.iat_avg_us, 1000.0, 1.0);
}

TEST_F(IatAnalyzerTest, CalculatesAverageIat) {
    int64_t time = now_ns();
    analyzer.process(time);

    // Send 10 packets at 1ms intervals
    for (int i = 1; i <= 10; i++) {
        analyzer.process(time + (i * 1000000));
    }

    IatAnalysisNative data;
    analyzer.get(&data);

    EXPECT_NEAR(data.iat_avg_us, 1000.0, 10.0);
}

// ============================================================================
// Min/Max Tests
// ============================================================================

TEST_F(IatAnalyzerTest, TracksMinMaxIat) {
    int64_t time = now_ns();
    analyzer.process(time);

    // Varying intervals: 500us, 1000us, 2000us
    analyzer.process(time + 500000);      // 500us
    analyzer.process(time + 1500000);     // 1000us
    analyzer.process(time + 3500000);     // 2000us

    IatAnalysisNative data;
    analyzer.get(&data);

    EXPECT_NEAR(data.iat_min_us, 500.0, 10.0);
    EXPECT_NEAR(data.iat_max_us, 2000.0, 10.0);
}

// ============================================================================
// Jitter Tests
// ============================================================================

TEST_F(IatAnalyzerTest, CalculatesJitter) {
    int64_t time = now_ns();
    analyzer.process(time);

    // Send packets with consistent timing, then one late
    for (int i = 1; i <= 10; i++) {
        analyzer.process(time + (i * 1000000));  // 1ms intervals
    }

    // Add a late packet (5ms instead of 1ms)
    analyzer.process(time + 15000000);

    IatAnalysisNative data;
    analyzer.get(&data);

    // Jitter should reflect the deviation
    EXPECT_GT(data.iat_jitter_us, 1000.0);
}

// ============================================================================
// Standard Deviation Tests
// ============================================================================

TEST_F(IatAnalyzerTest, CalculatesStdDev) {
    int64_t time = now_ns();
    analyzer.process(time);

    // Consistent intervals should have low stddev
    for (int i = 1; i <= 100; i++) {
        analyzer.process(time + (i * 1000000));  // 1ms intervals
    }

    IatAnalysisNative data;
    analyzer.get(&data);

    // StdDev should be very low for consistent timing
    EXPECT_LT(data.iat_stddev_us, 100.0);
}

TEST_F(IatAnalyzerTest, HighVarianceIncreasesStdDev) {
    int64_t time = now_ns();
    analyzer.process(time);

    // Alternating intervals: 500us, 1500us, 500us, 1500us...
    for (int i = 1; i <= 50; i++) {
        int64_t interval = (i % 2 == 0) ? 500000 : 1500000;
        time += interval;
        analyzer.process(time);
    }

    IatAnalysisNative data;
    analyzer.get(&data);

    // StdDev should be higher for varying intervals
    EXPECT_GT(data.iat_stddev_us, 100.0);
}

// ============================================================================
// Late/Early Packet Detection Tests
// ============================================================================

TEST_F(IatAnalyzerTest, DetectsLatePackets) {
    int64_t time = now_ns();
    analyzer.process(time);

    // First, establish a baseline with consistent timing
    for (int i = 1; i <= 50; i++) {
        analyzer.process(time + (i * 1000000));  // 1ms
    }

    // Now send a very late packet
    time += 51000000;
    analyzer.process(time + 10000000);  // 10ms interval (very late)

    IatAnalysisNative data;
    analyzer.get(&data);

    EXPECT_GT(data.late_packets, 0);
}

// ============================================================================
// Burst Detection Tests
// ============================================================================

TEST_F(IatAnalyzerTest, DetectsBursts) {
    int64_t time = now_ns();
    analyzer.process(time);

    // Normal interval
    time += 1000000;
    analyzer.process(time);

    // Burst: several packets very close together
    for (int i = 0; i < 5; i++) {
        time += 5000;  // 5us apart (burst)
        analyzer.process(time);
    }

    IatAnalysisNative data;
    analyzer.get(&data);

    EXPECT_GT(data.burst_count, 0);
}

// ============================================================================
// Reset Tests
// ============================================================================

TEST_F(IatAnalyzerTest, ResetClearsAllState) {
    int64_t time = now_ns();
    analyzer.process(time);
    analyzer.process(time + 1000000);
    analyzer.process(time + 2000000);

    analyzer.reset();

    IatAnalysisNative data;
    EXPECT_FALSE(analyzer.get(&data));
    EXPECT_EQ(data.iat_avg_us, 0);
    EXPECT_EQ(data.iat_min_us, 0);
    EXPECT_EQ(data.iat_max_us, 0);
}

// ============================================================================
// Concurrent Access Tests
// ============================================================================

TEST_F(IatAnalyzerTest, ConcurrentReadWrite) {
    std::atomic<bool> stop{false};
    std::atomic<int> successful_reads{0};

    // Writer thread
    std::thread writer([&]() {
        int64_t time = now_ns();
        for (int i = 0; i < 10000 && !stop.load(); i++) {
            time += 1000000;  // 1ms intervals
            analyzer.process(time);
        }
        stop.store(true);
    });

    // Reader thread
    std::thread reader([&]() {
        while (!stop.load()) {
            IatAnalysisNative data;
            if (analyzer.get(&data)) {
                // Verify data consistency
                if (data.iat_avg_us >= 0) {
                    successful_reads.fetch_add(1);
                }
            }
        }
    });

    writer.join();
    reader.join();

    EXPECT_GT(successful_reads.load(), 0);
}

TEST_F(IatAnalyzerTest, GetHandlesNullPointer) {
    EXPECT_FALSE(analyzer.get(nullptr));
}

// ============================================================================
// Edge Cases
// ============================================================================

TEST_F(IatAnalyzerTest, HandlesZeroInterval) {
    int64_t time = now_ns();
    analyzer.process(time);
    analyzer.process(time);  // Same time

    IatAnalysisNative data;
    analyzer.get(&data);

    // Should handle gracefully
    EXPECT_GE(data.iat_avg_us, 0);
}

TEST_F(IatAnalyzerTest, HandlesLargeInterval) {
    int64_t time = now_ns();
    analyzer.process(time);

    // 10 second interval
    analyzer.process(time + 10000000000LL);

    IatAnalysisNative data;
    analyzer.get(&data);

    EXPECT_NEAR(data.iat_avg_us, 10000000.0, 1000.0);
}

int main(int argc, char **argv) {
    ::testing::InitGoogleTest(&argc, argv);
    return RUN_ALL_TESTS();
}
