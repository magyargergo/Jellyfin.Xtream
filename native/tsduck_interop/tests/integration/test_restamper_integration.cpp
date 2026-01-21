// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#include <gtest/gtest.h>
#include <tsduck.h>
#include <cstring>
#include <vector>
#include "context/context.hpp"
#include "context/analyzer.hpp"
#include "restamping/restamper.hpp"
#include "mpegts/packet_utils.hpp"
#include "tsduck_interop.h"

using namespace tsduck_interop;

class RestamperIntegrationTest : public ::testing::Test {
protected:
    context::TsDuckContext* ctx = nullptr;
    context::TsDuckAnalyzer* analyzer = nullptr;
    restamping::Restamper* restamper = nullptr;

    void SetUp() override {
        ctx = new context::TsDuckContext();
        ASSERT_TRUE(ctx->isInitialized());

        TsDuckConfigNative config{};
        config.metrics_interval_ms = 100;
        config.enable_tr101290 = 1;

        analyzer = new context::TsDuckAnalyzer(ctx, &config);

        RestampingConfigNative restamp_config{};
        restamp_config.mode = RESTAMP_MODE_CORRECT;
        restamp_config.smooth_pcr = 1;
        restamp_config.fix_discontinuities = 1;
        restamp_config.correction_threshold_ms = 45.0;
        restamp_config.max_correction_rate_ms = 10.0;
        restamp_config.hysteresis_threshold_ms = 20.0;

        restamper = new restamping::Restamper(&analyzer->av_sync, &restamp_config);
    }

    void TearDown() override {
        delete restamper;
        delete analyzer;
        delete ctx;
    }

    // Create a packet with PCR
    static void createPcrPacket(uint8_t* packet, uint16_t pid, uint8_t cc, int64_t pcr_base) {
        std::memset(packet, 0xFF, TS_PACKET_SIZE);
        packet[0] = TS_SYNC_BYTE;
        packet[1] = (pid >> 8) & 0x1F;
        packet[2] = pid & 0xFF;
        packet[3] = 0x30 | (cc & 0x0F);  // Adaptation + payload
        packet[4] = 7;  // Adaptation field length
        packet[5] = 0x10;  // PCR flag

        // Write PCR (base only, extension = 0)
        packet[6] = (pcr_base >> 25) & 0xFF;
        packet[7] = (pcr_base >> 17) & 0xFF;
        packet[8] = (pcr_base >> 9) & 0xFF;
        packet[9] = (pcr_base >> 1) & 0xFF;
        packet[10] = ((pcr_base & 0x01) << 7) | 0x7E;
        packet[11] = 0x00;
    }

    // Create a PES packet with PTS
    static void createPesPacket(uint8_t* packet, uint16_t pid, uint8_t cc, int64_t pts, bool video) {
        std::memset(packet, 0xFF, TS_PACKET_SIZE);
        packet[0] = TS_SYNC_BYTE;
        packet[1] = 0x40 | ((pid >> 8) & 0x1F);  // PUSI set
        packet[2] = pid & 0xFF;
        packet[3] = 0x10 | (cc & 0x0F);  // Payload only

        // PES header
        packet[4] = 0x00;  // Start code
        packet[5] = 0x00;
        packet[6] = 0x01;
        packet[7] = video ? 0xE0 : 0xC0;  // Stream ID
        packet[8] = 0x00;  // PES length high
        packet[9] = 0x00;  // PES length low (0 = unbounded)
        packet[10] = 0x80;  // Flags
        packet[11] = 0x80;  // PTS only
        packet[12] = 0x05;  // PES header length

        // Write PTS
        packet[13] = 0x20 | ((pts >> 29) & 0x0E) | 0x01;
        packet[14] = (pts >> 22) & 0xFF;
        packet[15] = ((pts >> 14) & 0xFE) | 0x01;
        packet[16] = (pts >> 7) & 0xFF;
        packet[17] = ((pts << 1) & 0xFE) | 0x01;
    }

    // Extract PTS from packet
    static int64_t extractPts(const uint8_t* packet) {
        if (packet[4] != 0x00 || packet[5] != 0x00 || packet[6] != 0x01) {
            return -1;
        }
        return mpegts::extractPts(&packet[13]);
    }
};

// ============================================================================
// Basic Functionality Tests
// ============================================================================

TEST_F(RestamperIntegrationTest, InitializesCorrectly) {
    EXPECT_NE(restamper, nullptr);
}

TEST_F(RestamperIntegrationTest, ProcessesEmptyBuffer) {
    EXPECT_EQ(restamper->process(nullptr, 0, 0), 0);
}

// ============================================================================
// PCR Processing Tests
// ============================================================================

TEST_F(RestamperIntegrationTest, ProcessesPcrPacket) {
    uint8_t packet[TS_PACKET_SIZE];
    int64_t pcr_base = 90000;  // 1 second in 90kHz

    createPcrPacket(packet, 256, 0, pcr_base);

    int32_t mods = restamper->process(packet, TS_PACKET_SIZE, 0);

    // In CORRECT mode with switch offset 0, should still process PCR
    EXPECT_GE(mods, 0);
}

TEST_F(RestamperIntegrationTest, SmoothsPcrValues) {
    // Enable smoothing
    restamper->config.smooth_pcr = 1;
    restamper->config.stream_bitrate_hint = 10000000;  // 10 Mbps

    std::vector<uint8_t> data(TS_PACKET_SIZE * 100);
    int64_t pcr_base = 90000;

    // Create packets with PCR every 10 packets
    for (int i = 0; i < 100; i++) {
        if (i % 10 == 0) {
            createPcrPacket(&data[i * TS_PACKET_SIZE], 256, i % 16, pcr_base);
            pcr_base += 9000;  // 100ms in 90kHz
        } else {
            std::memset(&data[i * TS_PACKET_SIZE], 0xFF, TS_PACKET_SIZE);
            data[i * TS_PACKET_SIZE] = TS_SYNC_BYTE;
            data[i * TS_PACKET_SIZE + 1] = 0x01;
            data[i * TS_PACKET_SIZE + 2] = 0x00;  // PID 256
            data[i * TS_PACKET_SIZE + 3] = 0x10 | ((i) % 16);
        }
    }

    int32_t total_mods = restamper->process(data.data(), static_cast<int32_t>(data.size()), 0);

    // Some PCR modifications expected
    EXPECT_GE(total_mods, 0);
}

// ============================================================================
// Provider Switch Tests
// ============================================================================

TEST_F(RestamperIntegrationTest, HandlesProviderSwitch) {
    int64_t last_output_pts = 270000;  // 3 seconds
    int64_t new_input_first_pts = 90000;  // 1 second

    restamper->handleSwitch(last_output_pts, new_input_first_pts);

    int64_t switch_offset = restamper->switch_offset_90khz.load();

    // Offset should bridge the gap
    EXPECT_NE(switch_offset, 0);
}

TEST_F(RestamperIntegrationTest, AppliesSwitchOffsetToPts) {
    // Set up a switch offset
    restamper->handleSwitch(270000, 90000);  // Gap of ~180000

    // Create a PES packet
    uint8_t packet[TS_PACKET_SIZE];
    createPesPacket(packet, 0x100, 0, 90000, true);

    // Process with restamper (in CORRECT mode)
    restamper->process(packet, TS_PACKET_SIZE, 0);

    // The PTS should have been modified
    int64_t new_pts = extractPts(packet);

    // The offset should have been applied
    // Note: exact value depends on switch gap configuration
    EXPECT_NE(new_pts, 90000);
}

// ============================================================================
// Statistics Tests
// ============================================================================

TEST_F(RestamperIntegrationTest, TracksStatistics) {
    std::vector<uint8_t> data(TS_PACKET_SIZE * 100);

    for (int i = 0; i < 100; i++) {
        if (i % 10 == 0) {
            createPcrPacket(&data[i * TS_PACKET_SIZE], 256, i % 16, 90000 + i * 900);
        } else {
            std::memset(&data[i * TS_PACKET_SIZE], 0xFF, TS_PACKET_SIZE);
            data[i * TS_PACKET_SIZE] = TS_SYNC_BYTE;
            data[i * TS_PACKET_SIZE + 1] = 0x01;
            data[i * TS_PACKET_SIZE + 2] = 0x00;
            data[i * TS_PACKET_SIZE + 3] = 0x10 | (i % 16);
        }
    }

    restamper->process(data.data(), static_cast<int32_t>(data.size()), 0);

    RestampingStatisticsNative stats;
    bool has_stats = restamper->getStatistics(&stats);

    EXPECT_TRUE(has_stats);
    EXPECT_EQ(stats.packets_processed, 100);
}

// ============================================================================
// Reset Tests
// ============================================================================

TEST_F(RestamperIntegrationTest, ResetClearsState) {
    // Process some data
    std::vector<uint8_t> data(TS_PACKET_SIZE * 10);
    for (int i = 0; i < 10; i++) {
        createPcrPacket(&data[i * TS_PACKET_SIZE], 256, i % 16, 90000 + i * 900);
    }
    restamper->process(data.data(), static_cast<int32_t>(data.size()), 0);

    // Set switch offset
    restamper->handleSwitch(270000, 90000);

    // Reset
    restamper->reset();

    RestampingStatisticsNative stats;
    restamper->getStatistics(&stats);

    EXPECT_EQ(stats.packets_processed, 0);
    EXPECT_EQ(restamper->switch_offset_90khz.load(), 0);
}

// ============================================================================
// Configuration Tests
// ============================================================================

TEST_F(RestamperIntegrationTest, ConfigureChangesMode) {
    RestampingConfigNative new_config{};
    new_config.mode = RESTAMP_MODE_MONITOR;

    restamper->configure(&new_config);

    EXPECT_EQ(restamper->config.mode, RESTAMP_MODE_MONITOR);
}

TEST_F(RestamperIntegrationTest, MonitorModeDoesNotModify) {
    restamper->config.mode = RESTAMP_MODE_MONITOR;

    uint8_t packet[TS_PACKET_SIZE];
    createPcrPacket(packet, 256, 0, 90000);

    // Store original PCR
    int64_t original_pcr = mpegts::extractPcrBase(&packet[6]);

    restamper->process(packet, TS_PACKET_SIZE, 0);

    // PCR should be unchanged in monitor mode
    int64_t new_pcr = mpegts::extractPcrBase(&packet[6]);
    EXPECT_EQ(new_pcr, original_pcr);
}

TEST_F(RestamperIntegrationTest, DisabledModeSkipsProcessing) {
    restamper->config.mode = RESTAMP_MODE_DISABLED;

    uint8_t packet[TS_PACKET_SIZE];
    createPcrPacket(packet, 256, 0, 90000);

    int32_t mods = restamper->process(packet, TS_PACKET_SIZE, 0);

    EXPECT_EQ(mods, 0);
}

// ============================================================================
// API Tests
// ============================================================================

TEST(RestamperApiTest, CreateDestroy) {
    TsDuckContextHandle ctx = tsduck_context_create();
    ASSERT_NE(ctx, nullptr);

    TsDuckConfigNative config{};
    TsDuckAnalyzerHandle analyzer = tsduck_analyzer_create(ctx, &config);
    ASSERT_NE(analyzer, nullptr);

    RestampingConfigNative restamp_config{};
    restamp_config.mode = RESTAMP_MODE_CORRECT;

    TsDuckRestamperHandle restamper = tsduck_restamper_create(analyzer, &restamp_config);
    ASSERT_NE(restamper, nullptr);
    EXPECT_TRUE(tsduck_restamper_is_initialized(restamper));

    tsduck_restamper_destroy(restamper);
    tsduck_analyzer_destroy(analyzer);
    tsduck_context_destroy(ctx);
}

TEST(RestamperApiTest, ProcessViaApi) {
    TsDuckContextHandle ctx = tsduck_context_create();
    TsDuckConfigNative config{};
    TsDuckAnalyzerHandle analyzer = tsduck_analyzer_create(ctx, &config);

    RestampingConfigNative restamp_config{};
    restamp_config.mode = RESTAMP_MODE_CORRECT;
    TsDuckRestamperHandle restamper = tsduck_restamper_create(analyzer, &restamp_config);

    // Create test data
    uint8_t data[TS_PACKET_SIZE];
    std::memset(data, 0xFF, TS_PACKET_SIZE);
    data[0] = TS_SYNC_BYTE;
    data[1] = 0x01;
    data[2] = 0x00;
    data[3] = 0x10;

    int32_t result = tsduck_restamper_process(restamper, data, TS_PACKET_SIZE);
    EXPECT_GE(result, 0);

    tsduck_restamper_destroy(restamper);
    tsduck_analyzer_destroy(analyzer);
    tsduck_context_destroy(ctx);
}

TEST(RestamperApiTest, GetStatisticsViaApi) {
    TsDuckContextHandle ctx = tsduck_context_create();
    TsDuckConfigNative config{};
    TsDuckAnalyzerHandle analyzer = tsduck_analyzer_create(ctx, &config);

    RestampingConfigNative restamp_config{};
    restamp_config.mode = RESTAMP_MODE_CORRECT;
    TsDuckRestamperHandle restamper = tsduck_restamper_create(analyzer, &restamp_config);

    RestampingStatisticsNative stats;
    bool result = tsduck_restamper_get_statistics(restamper, &stats);

    EXPECT_TRUE(result);

    tsduck_restamper_destroy(restamper);
    tsduck_analyzer_destroy(analyzer);
    tsduck_context_destroy(ctx);
}

int main(int argc, char **argv) {
    ::testing::InitGoogleTest(&argc, argv);
    return RUN_ALL_TESTS();
}
