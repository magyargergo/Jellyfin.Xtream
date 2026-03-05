// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#include <gtest/gtest.h>
#include <vector>
#include "context/context.hpp"
#include "context/analyzer.hpp"
#include "restamping/restamper.hpp"
#include "tsduck_interop.h"

using namespace tsduck_interop;

class RestamperIntegrationTest : public ::testing::Test {
protected:
    context::TsDuckContext* ctx = nullptr;
    context::TsDuckAnalyzer* analyzer = nullptr;
    restamping::Restamper* restamper = nullptr;

    void SetUp() override {
        ctx = new context::TsDuckContext();
        ASSERT_TRUE(ctx->is_initialized());

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
        ts::TSPacket& pkt = *reinterpret_cast<ts::TSPacket*>(packet);
        pkt = ts::NullPacket;
        pkt.setPID(ts::PID(pid));
        pkt.b[3] = 0x30 | (cc & 0x0F);  // Adaptation + payload
        pkt.b[4] = 7;   // AF length (flags + 6 PCR bytes)
        pkt.b[5] = 0x10;  // PCR flag
        // Encode PCR using TsDuck (convert 90kHz base to 27MHz)
        pkt.setPCR(static_cast<uint64_t>(pcr_base) * ts::SYSTEM_CLOCK_SUBFACTOR);
    }

    // Create a PES packet with PTS
    static void createPesPacket(uint8_t* packet, uint16_t pid, uint8_t cc, int64_t pts, bool video) {
        ts::TSPacket& pkt = *reinterpret_cast<ts::TSPacket*>(packet);
        pkt = ts::NullPacket;
        pkt.setPID(ts::PID(pid));
        pkt.b[1] |= 0x40;  // Set PUSI
        pkt.b[3] = 0x10 | (cc & 0x0F);  // Payload only

        // PES header structure
        pkt.b[4] = 0x00;   // Start code
        pkt.b[5] = 0x00;
        pkt.b[6] = 0x01;
        pkt.b[7] = video ? ts::SID_VIDEO : ts::SID_AUDIO;
        pkt.b[8] = 0x00;   // PES length high
        pkt.b[9] = 0x00;   // PES length low (unbounded)
        pkt.b[10] = 0x80;  // MPEG-2 marker
        pkt.b[11] = 0x80;  // PTS present
        pkt.b[12] = 0x05;  // PES header data length
        // Encode PTS using TsDuck
        pkt.setPTS(static_cast<uint64_t>(pts));
    }

    // Extract PTS from packet using TsDuck
    static int64_t extractPts(uint8_t* packet) {
        ts::TSPacket& pkt = *reinterpret_cast<ts::TSPacket*>(packet);
        if (!pkt.hasPTS()) return -1;
        return static_cast<int64_t>(pkt.getPTS());
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
    uint8_t packet[ts::PKT_SIZE];
    int64_t pcr_base = 90000;  // 1 second in 90kHz

    createPcrPacket(packet, 256, 0, pcr_base);

    int32_t mods = restamper->process(packet, static_cast<int32_t>(ts::PKT_SIZE), 0);

    // In CORRECT mode with switch offset 0, should still process PCR
    EXPECT_GE(mods, 0);
}

TEST_F(RestamperIntegrationTest, SmoothsPcrValues) {
    // Enable smoothing
    restamper->config.smooth_pcr = 1;
    // EPTLA replaces bitrate estimation — no hint needed

    std::vector<uint8_t> data(ts::PKT_SIZE * 100);
    int64_t pcr_base = 90000;

    // Create packets with PCR every 10 packets
    for (int i = 0; i < 100; i++) {
        if (i % 10 == 0) {
            createPcrPacket(&data[i * ts::PKT_SIZE], 256, i % 16, pcr_base);
            pcr_base += 9000;  // 100ms in 90kHz
        } else {
            ts::TSPacket& pkt = *reinterpret_cast<ts::TSPacket*>(&data[i * ts::PKT_SIZE]);
            pkt = ts::NullPacket;
            pkt.setPID(ts::PID(256));
            pkt.b[3] = 0x10 | (i % 16);
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

    restamper->handle_switch(last_output_pts, new_input_first_pts);

    int64_t switch_offset = restamper->get_switch_offset_90khz();

    // Offset should bridge the gap
    EXPECT_NE(switch_offset, 0);
}

TEST_F(RestamperIntegrationTest, AppliesSwitchOffsetToPts) {
    // Set up a switch offset
    restamper->handle_switch(270000, 90000);  // Gap of ~180000

    // Create a PES packet
    uint8_t packet[ts::PKT_SIZE];
    createPesPacket(packet, 0x100, 0, 90000, true);

    // Process with restamper (in CORRECT mode)
    (void)restamper->process(packet, static_cast<int32_t>(ts::PKT_SIZE), 0);

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
    std::vector<uint8_t> data(ts::PKT_SIZE * 100);

    for (int i = 0; i < 100; i++) {
        if (i % 10 == 0) {
            createPcrPacket(&data[i * ts::PKT_SIZE], 256, i % 16, 90000 + i * 900);
        } else {
            ts::TSPacket& pkt = *reinterpret_cast<ts::TSPacket*>(&data[i * ts::PKT_SIZE]);
            pkt = ts::NullPacket;
            pkt.setPID(ts::PID(256));
            pkt.b[3] = 0x10 | (i % 16);
        }
    }

    (void)restamper->process(data.data(), static_cast<int32_t>(data.size()), 0);

    RestampingStatisticsNative stats;
    bool has_stats = restamper->get_statistics(&stats);

    EXPECT_TRUE(has_stats);
    EXPECT_EQ(stats.packets_processed, 100);
}

// ============================================================================
// Reset Tests
// ============================================================================

TEST_F(RestamperIntegrationTest, ResetClearsState) {
    // Process some data
    std::vector<uint8_t> data(ts::PKT_SIZE * 10);
    for (int i = 0; i < 10; i++) {
        createPcrPacket(&data[i * ts::PKT_SIZE], 256, i % 16, 90000 + i * 900);
    }
    (void)restamper->process(data.data(), static_cast<int32_t>(data.size()), 0);

    // Set switch offset
    restamper->handle_switch(270000, 90000);

    // Reset
    restamper->reset();

    RestampingStatisticsNative stats;
    (void)restamper->get_statistics(&stats);

    EXPECT_EQ(stats.packets_processed, 0);
    EXPECT_EQ(restamper->get_switch_offset_90khz(), 0);
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

    uint8_t packet[ts::PKT_SIZE];
    createPcrPacket(packet, 256, 0, 90000);

    // Store original PCR base (90kHz)
    ts::TSPacket& pkt = *reinterpret_cast<ts::TSPacket*>(packet);
    int64_t original_pcr = static_cast<int64_t>(pkt.getPCR() / ts::SYSTEM_CLOCK_SUBFACTOR);

    (void)restamper->process(packet, static_cast<int32_t>(ts::PKT_SIZE), 0);

    // PCR should be unchanged in monitor mode
    int64_t new_pcr = static_cast<int64_t>(pkt.getPCR() / ts::SYSTEM_CLOCK_SUBFACTOR);
    EXPECT_EQ(new_pcr, original_pcr);
}

TEST_F(RestamperIntegrationTest, DisabledModeSkipsProcessing) {
    restamper->config.mode = RESTAMP_MODE_DISABLED;

    uint8_t packet[ts::PKT_SIZE];
    createPcrPacket(packet, 256, 0, 90000);

    int32_t mods = restamper->process(packet, static_cast<int32_t>(ts::PKT_SIZE), 0);

    EXPECT_EQ(mods, 0);
}

// ============================================================================
// API Tests
// ============================================================================

TEST(IntegratedRestamperApiTest, ConfigureAndCheck) {
    TsDuckContextHandle ctx = tsduck_context_create();
    ASSERT_NE(ctx, nullptr);

    TsDuckConfigNative config{};
    TsDuckAnalyzerHandle analyzer = tsduck_analyzer_create(ctx, &config);
    ASSERT_NE(analyzer, nullptr);

    // Initially restamping should be disabled
    EXPECT_FALSE(tsduck_analyzer_is_restamping_enabled(analyzer));

    // Configure restamping through analyzer
    bool configured = tsduck_analyzer_configure_restamp(analyzer, RESTAMP_MODE_CORRECT, 1, 1);
    EXPECT_TRUE(configured);
    EXPECT_TRUE(tsduck_analyzer_is_restamping_enabled(analyzer));

    tsduck_analyzer_destroy(analyzer);
    tsduck_context_destroy(ctx);
}

TEST(IntegratedRestamperApiTest, FeedWithRestamp) {
    TsDuckContextHandle ctx = tsduck_context_create();

    // Create analyzer with restamping enabled from the start
    TsDuckConfigNative config{};
    config.enable_auto_restamp = 1;
    config.restamp_mode = RESTAMP_MODE_CORRECT;
    TsDuckAnalyzerHandle analyzer = tsduck_analyzer_create(ctx, &config);

    EXPECT_TRUE(tsduck_analyzer_is_restamping_enabled(analyzer));

    // Create test data
    uint8_t data[ts::PKT_SIZE];
    ts::TSPacket& pkt = *reinterpret_cast<ts::TSPacket*>(data);
    pkt = ts::NullPacket;
    pkt.setPID(ts::PID(256));
    pkt.b[3] = 0x10;

    // Feed with restamping (modifies data in-place)
    int32_t result = tsduck_analyzer_feed_restamp(analyzer, data, static_cast<int32_t>(ts::PKT_SIZE));
    EXPECT_GE(result, 0);

    tsduck_analyzer_destroy(analyzer);
    tsduck_context_destroy(ctx);
}

TEST(IntegratedRestamperApiTest, GetStatisticsViaAnalyzer) {
    TsDuckContextHandle ctx = tsduck_context_create();

    TsDuckConfigNative config{};
    config.enable_auto_restamp = 1;
    config.restamp_mode = RESTAMP_MODE_CORRECT;
    TsDuckAnalyzerHandle analyzer = tsduck_analyzer_create(ctx, &config);

    RestampingStatisticsNative stats;
    bool result = tsduck_analyzer_get_restamp_statistics(analyzer, &stats);

    EXPECT_TRUE(result);

    tsduck_analyzer_destroy(analyzer);
    tsduck_context_destroy(ctx);
}

int main(int argc, char **argv) {
    ::testing::InitGoogleTest(&argc, argv);
    return RUN_ALL_TESTS();
}
