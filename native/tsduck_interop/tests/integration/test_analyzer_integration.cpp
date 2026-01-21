// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#include <gtest/gtest.h>
#include <tsduck.h>
#include <cstring>
#include <vector>
#include <chrono>
#include "context/context.hpp"
#include "context/analyzer.hpp"
#include "tsduck_interop.h"

using namespace tsduck_interop;

class AnalyzerIntegrationTest : public ::testing::Test {
protected:
    context::TsDuckContext* ctx = nullptr;
    context::TsDuckAnalyzer* analyzer = nullptr;

    void SetUp() override {
        ctx = new context::TsDuckContext();
        ASSERT_TRUE(ctx->isInitialized());

        TsDuckConfigNative config{};
        config.metrics_interval_ms = 100;
        config.enable_tr101290 = 1;
        config.sample_size_bytes = TS_PACKET_SIZE * 100;

        analyzer = new context::TsDuckAnalyzer(ctx, &config);
    }

    void TearDown() override {
        delete analyzer;
        delete ctx;
    }

    // Create a valid TS packet
    static void createPacket(uint8_t* packet, uint16_t pid, uint8_t cc,
                             bool has_payload = true, bool has_adaptation = false) {
        std::memset(packet, 0xFF, TS_PACKET_SIZE);
        packet[0] = TS_SYNC_BYTE;
        packet[1] = (pid >> 8) & 0x1F;
        packet[2] = pid & 0xFF;

        uint8_t adaptation_control = 0;
        if (has_adaptation && has_payload) adaptation_control = 0x30;
        else if (has_adaptation) adaptation_control = 0x20;
        else if (has_payload) adaptation_control = 0x10;

        packet[3] = adaptation_control | (cc & 0x0F);

        if (has_adaptation) {
            packet[4] = 7;  // Adaptation field length
            packet[5] = 0;  // Flags
        }
    }

    // Create a null packet
    static void createNullPacket(uint8_t* packet) {
        createPacket(packet, 0x1FFF, 0, true, false);
    }

    // Create a packet with PCR
    static void createPcrPacket(uint8_t* packet, uint16_t pid, uint8_t cc, uint64_t pcr) {
        createPacket(packet, pid, cc, true, true);
        packet[4] = 7;  // Adaptation field length
        packet[5] = 0x10;  // PCR flag

        // Write PCR base (33 bits)
        uint64_t pcr_base = pcr / 300;
        uint16_t pcr_ext = pcr % 300;

        packet[6] = (pcr_base >> 25) & 0xFF;
        packet[7] = (pcr_base >> 17) & 0xFF;
        packet[8] = (pcr_base >> 9) & 0xFF;
        packet[9] = (pcr_base >> 1) & 0xFF;
        packet[10] = ((pcr_base & 0x01) << 7) | 0x7E | ((pcr_ext >> 8) & 0x01);
        packet[11] = pcr_ext & 0xFF;
    }
};

// ============================================================================
// Context Tests
// ============================================================================

TEST_F(AnalyzerIntegrationTest, ContextInitializesCorrectly) {
    EXPECT_TRUE(ctx->isInitialized());
}

// ============================================================================
// Basic Feed Tests
// ============================================================================

TEST_F(AnalyzerIntegrationTest, FeedSinglePacket) {
    uint8_t packet[TS_PACKET_SIZE];
    createPacket(packet, 100, 0);

    int32_t result = analyzer->feed(packet, TS_PACKET_SIZE);
    EXPECT_EQ(result, 1);
}

TEST_F(AnalyzerIntegrationTest, FeedMultiplePackets) {
    std::vector<uint8_t> data(TS_PACKET_SIZE * 100);

    for (int i = 0; i < 100; i++) {
        createPacket(&data[i * TS_PACKET_SIZE], 100, i % 16);
    }

    int32_t result = analyzer->feed(data.data(), static_cast<int32_t>(data.size()));
    EXPECT_EQ(result, 100);
}

TEST_F(AnalyzerIntegrationTest, FeedRejectsInvalidData) {
    EXPECT_EQ(analyzer->feed(nullptr, 100), TSDUCK_ERROR_INVALID_DATA);
    EXPECT_EQ(analyzer->feed(new uint8_t[10], 0), TSDUCK_ERROR_INVALID_DATA);
    EXPECT_EQ(analyzer->feed(new uint8_t[10], -1), TSDUCK_ERROR_INVALID_DATA);
}

// ============================================================================
// PID Tracking Tests
// ============================================================================

TEST_F(AnalyzerIntegrationTest, TracksMultiplePids) {
    std::vector<uint8_t> data(TS_PACKET_SIZE * 30);

    // Create packets for 3 different PIDs
    for (int i = 0; i < 10; i++) {
        createPacket(&data[i * TS_PACKET_SIZE], 100, i % 16);
        createPacket(&data[(10 + i) * TS_PACKET_SIZE], 200, i % 16);
        createPacket(&data[(20 + i) * TS_PACKET_SIZE], 300, i % 16);
    }

    analyzer->feed(data.data(), static_cast<int32_t>(data.size()));

    EXPECT_EQ(analyzer->pids.getActiveCount(), 3);
}

TEST_F(AnalyzerIntegrationTest, TracksNullPackets) {
    std::vector<uint8_t> data(TS_PACKET_SIZE * 10);

    for (int i = 0; i < 5; i++) {
        createPacket(&data[i * TS_PACKET_SIZE], 100, i % 16);
    }
    for (int i = 5; i < 10; i++) {
        createNullPacket(&data[i * TS_PACKET_SIZE]);
    }

    analyzer->feed(data.data(), static_cast<int32_t>(data.size()));

    int64_t total = analyzer->total_packet_count.load();
    int64_t null_count = analyzer->null_packet_count.load();

    EXPECT_EQ(total, 10);
    EXPECT_EQ(null_count, 5);
}

// ============================================================================
// PCR Tests
// ============================================================================

TEST_F(AnalyzerIntegrationTest, ProcessesPcrPackets) {
    std::vector<uint8_t> data(TS_PACKET_SIZE * 10);
    uint64_t pcr = 27000000;  // 1 second

    for (int i = 0; i < 10; i++) {
        createPcrPacket(&data[i * TS_PACKET_SIZE], 256, i % 16, pcr);
        pcr += 2700000;  // 100ms intervals
    }

    analyzer->feed(data.data(), static_cast<int32_t>(data.size()));

    PcrAnalysisNative pcr_analysis;
    bool has_pcr = analyzer->pcr.get(&pcr_analysis);

    EXPECT_TRUE(has_pcr);
    EXPECT_EQ(pcr_analysis.pcr_count, 10);
}

// ============================================================================
// Metrics Tests
// ============================================================================

TEST_F(AnalyzerIntegrationTest, GeneratesMetrics) {
    std::vector<uint8_t> data(TS_PACKET_SIZE * 1000);

    for (int i = 0; i < 1000; i++) {
        createPacket(&data[i * TS_PACKET_SIZE], 100 + (i % 3), i % 16);
    }

    analyzer->feed(data.data(), static_cast<int32_t>(data.size()));

    // Wait for metrics interval to elapse
    std::this_thread::sleep_for(std::chrono::milliseconds(150));

    // Feed a small amount of data to trigger updateMetrics()
    std::vector<uint8_t> trigger_data(TS_PACKET_SIZE);
    createPacket(trigger_data.data(), 100, 0);
    analyzer->feed(trigger_data.data(), static_cast<int32_t>(trigger_data.size()));

    TsDuckMetricsNative metrics;
    bool has_metrics = analyzer->getMetrics(&metrics);

    EXPECT_TRUE(has_metrics);
    EXPECT_GT(metrics.pid_count, 0);
}

// ============================================================================
// Continuity Error Detection Tests
// ============================================================================

TEST_F(AnalyzerIntegrationTest, DetectsContinuityErrors) {
    std::vector<uint8_t> data(TS_PACKET_SIZE * 10);

    // Create packets with CC: 0, 1, 2, 5, 6, 7 (missing 3, 4)
    createPacket(&data[0 * TS_PACKET_SIZE], 100, 0);
    createPacket(&data[1 * TS_PACKET_SIZE], 100, 1);
    createPacket(&data[2 * TS_PACKET_SIZE], 100, 2);
    createPacket(&data[3 * TS_PACKET_SIZE], 100, 5);  // CC error here
    createPacket(&data[4 * TS_PACKET_SIZE], 100, 6);
    createPacket(&data[5 * TS_PACKET_SIZE], 100, 7);
    createPacket(&data[6 * TS_PACKET_SIZE], 100, 8);
    createPacket(&data[7 * TS_PACKET_SIZE], 100, 9);
    createPacket(&data[8 * TS_PACKET_SIZE], 100, 10);
    createPacket(&data[9 * TS_PACKET_SIZE], 100, 11);

    analyzer->feed(data.data(), static_cast<int32_t>(data.size()));

    // Check TSDuck's CC analyzer detected errors
    size_t cc_errors = analyzer->cc_analyzer.errorCount();
    EXPECT_GT(cc_errors, 0);
}

// ============================================================================
// Bitrate Tests
// ============================================================================

TEST_F(AnalyzerIntegrationTest, CalculatesBitrateFromPackets) {
    std::vector<uint8_t> data(TS_PACKET_SIZE * 1000);

    for (int i = 0; i < 1000; i++) {
        createPacket(&data[i * TS_PACKET_SIZE], 100, i % 16);
    }

    analyzer->feed(data.data(), static_cast<int32_t>(data.size()));

    // Wait for metrics interval to elapse
    std::this_thread::sleep_for(std::chrono::milliseconds(150));

    // Feed a small amount of data to trigger updateMetrics()
    std::vector<uint8_t> trigger_data(TS_PACKET_SIZE);
    createPacket(trigger_data.data(), 100, 0);
    analyzer->feed(trigger_data.data(), static_cast<int32_t>(trigger_data.size()));

    BitrateAnalysisNative bitrate;
    bool has_bitrate = analyzer->getBitrateAnalysis(&bitrate);

    EXPECT_TRUE(has_bitrate);
    EXPECT_GT(bitrate.ts_bitrate_nominal, 0);
}

// ============================================================================
// Reset Tests
// ============================================================================

TEST_F(AnalyzerIntegrationTest, ResetClearsAllState) {
    std::vector<uint8_t> data(TS_PACKET_SIZE * 100);

    for (int i = 0; i < 100; i++) {
        createPacket(&data[i * TS_PACKET_SIZE], 100, i % 16);
    }

    analyzer->feed(data.data(), static_cast<int32_t>(data.size()));
    EXPECT_GT(analyzer->packets_processed.load(), 0);

    analyzer->reset();

    EXPECT_EQ(analyzer->packets_processed.load(), 0);
    EXPECT_EQ(analyzer->pids.getActiveCount(), 0);
}

// ============================================================================
// API Tests
// ============================================================================

TEST(TsDuckApiTest, ContextCreateDestroy) {
    TsDuckContextHandle ctx = tsduck_context_create();
    ASSERT_NE(ctx, nullptr);
    EXPECT_TRUE(tsduck_context_is_available(ctx));
    tsduck_context_destroy(ctx);
}

TEST(TsDuckApiTest, AnalyzerCreateDestroy) {
    TsDuckContextHandle ctx = tsduck_context_create();
    ASSERT_NE(ctx, nullptr);

    TsDuckConfigNative config{};
    config.metrics_interval_ms = 1000;

    TsDuckAnalyzerHandle analyzer = tsduck_analyzer_create(ctx, &config);
    ASSERT_NE(analyzer, nullptr);
    EXPECT_TRUE(tsduck_analyzer_is_initialized(analyzer));

    tsduck_analyzer_destroy(analyzer);
    tsduck_context_destroy(ctx);
}

TEST(TsDuckApiTest, GetVersionReturnsString) {
    const char* version = tsduck_get_version();
    ASSERT_NE(version, nullptr);
    EXPECT_GT(strlen(version), 0);
}

TEST(TsDuckApiTest, IsAvailableReturnsTrue) {
    EXPECT_TRUE(tsduck_is_available());
}

int main(int argc, char **argv) {
    ::testing::InitGoogleTest(&argc, argv);
    return RUN_ALL_TESTS();
}
