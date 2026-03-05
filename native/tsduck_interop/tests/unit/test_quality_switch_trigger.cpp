// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#include <gtest/gtest.h>
#include "streaming/quality_switch_trigger.hpp"
#include <thread>
#include <chrono>

using namespace tsduck_interop::streaming;

class QualitySwitchTriggerTest : public ::testing::Test {
protected:
    StreamerConfig config{};

    void SetUp() override {
        config.enable_quality_switch = 1;
        config.quality_check_interval_ms = 100;  // Short for testing
        config.quality_window_seconds = 5;
        config.max_sync_errors_per_window = 1;
        config.max_continuity_errors_per_sec = 10;
        config.max_transport_errors_per_sec = 5;
        config.max_pcr_errors_per_sec = 5;
    }
};

TEST_F(QualitySwitchTriggerTest, InitialState) {
    QualitySwitchTrigger trigger(config);
    EXPECT_TRUE(trigger.is_enabled());
    EXPECT_EQ(trigger.total_quality_switches(), 0);
}

TEST_F(QualitySwitchTriggerTest, DisabledTriggerNeverSwitches) {
    config.enable_quality_switch = 0;
    QualitySwitchTrigger trigger(config);

    EXPECT_FALSE(trigger.is_enabled());

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};
    p1.sync_loss = 100;  // Would normally trigger immediate switch

    // Wait for check interval
    std::this_thread::sleep_for(std::chrono::milliseconds(150));

    EXPECT_FALSE(trigger.should_switch(p1, p2));
    EXPECT_EQ(trigger.total_quality_switches(), 0);
}

TEST_F(QualitySwitchTriggerTest, FirstCheckEstablishesBaseline) {
    QualitySwitchTrigger trigger(config);

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};
    p1.continuity_count_error = 1000;  // Start with existing errors

    // First call establishes baseline, never triggers switch
    EXPECT_FALSE(trigger.should_switch(p1, p2));
    EXPECT_EQ(trigger.total_quality_switches(), 0);
}

TEST_F(QualitySwitchTriggerTest, RespectCheckInterval) {
    QualitySwitchTrigger trigger(config);

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};

    // First call: baseline
    trigger.should_switch(p1, p2);

    // Immediate second call should return false (too soon)
    p1.sync_loss = 10;  // Would normally trigger
    EXPECT_FALSE(trigger.should_switch(p1, p2));
}

TEST_F(QualitySwitchTriggerTest, SyncLossTriggersSwitchImmediately) {
    QualitySwitchTrigger trigger(config);

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};

    // Establish baseline
    trigger.should_switch(p1, p2);

    // Wait for check interval
    std::this_thread::sleep_for(std::chrono::milliseconds(150));

    // Any sync_loss should trigger switch
    p1.sync_loss = 1;
    EXPECT_TRUE(trigger.should_switch(p1, p2));
    EXPECT_EQ(trigger.total_quality_switches(), 1);
}

TEST_F(QualitySwitchTriggerTest, ContinuityErrorRateExceedsThreshold) {
    QualitySwitchTrigger trigger(config);

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};

    // Establish baseline
    trigger.should_switch(p1, p2);

    // Wait 500ms (0.5 seconds) then add errors
    std::this_thread::sleep_for(std::chrono::milliseconds(500));

    // 20 errors in 0.5s = 40 errors/sec > threshold of 10
    p1.continuity_count_error = 20;
    EXPECT_TRUE(trigger.should_switch(p1, p2));
    EXPECT_EQ(trigger.total_quality_switches(), 1);
}

TEST_F(QualitySwitchTriggerTest, ContinuityErrorRateBelowThreshold) {
    QualitySwitchTrigger trigger(config);

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};

    // Establish baseline
    trigger.should_switch(p1, p2);

    // Wait 1 second then add moderate errors
    std::this_thread::sleep_for(std::chrono::milliseconds(1000));

    // 5 errors in 1s = 5 errors/sec < threshold of 10
    p1.continuity_count_error = 5;
    EXPECT_FALSE(trigger.should_switch(p1, p2));
    EXPECT_EQ(trigger.total_quality_switches(), 0);
}

TEST_F(QualitySwitchTriggerTest, TransportErrorRateExceedsThreshold) {
    QualitySwitchTrigger trigger(config);

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};

    // Establish baseline
    trigger.should_switch(p1, p2);

    // Wait 500ms then add errors
    std::this_thread::sleep_for(std::chrono::milliseconds(500));

    // 10 errors in 0.5s = 20 errors/sec > threshold of 5
    p2.transport_error = 10;
    EXPECT_TRUE(trigger.should_switch(p1, p2));
    EXPECT_EQ(trigger.total_quality_switches(), 1);
}

TEST_F(QualitySwitchTriggerTest, PcrErrorsCombineDiscontinuityAndRepetition) {
    QualitySwitchTrigger trigger(config);

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};

    // Establish baseline
    trigger.should_switch(p1, p2);

    // Wait 500ms
    std::this_thread::sleep_for(std::chrono::milliseconds(500));

    // 3 + 3 = 6 errors in 0.5s = 12 errors/sec > threshold of 5
    p2.pcr_discontinuity_error = 3;
    p2.pcr_repetition_error = 3;
    EXPECT_TRUE(trigger.should_switch(p1, p2));
    EXPECT_EQ(trigger.total_quality_switches(), 1);
}

TEST_F(QualitySwitchTriggerTest, ResetClearsBaseline) {
    QualitySwitchTrigger trigger(config);

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};

    // Establish baseline
    trigger.should_switch(p1, p2);

    std::this_thread::sleep_for(std::chrono::milliseconds(150));

    // Add errors that would trigger
    p1.sync_loss = 5;

    // Reset before check
    trigger.reset();

    // Now the next call establishes a new baseline (won't trigger)
    EXPECT_FALSE(trigger.should_switch(p1, p2));
    EXPECT_EQ(trigger.total_quality_switches(), 0);
}

TEST_F(QualitySwitchTriggerTest, WindowSlidesForward) {
    config.quality_window_seconds = 1;  // 1 second window
    config.quality_check_interval_ms = 50;
    QualitySwitchTrigger trigger(config);

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};

    // Establish baseline
    trigger.should_switch(p1, p2);

    // Wait 1.2 seconds (window should slide)
    std::this_thread::sleep_for(std::chrono::milliseconds(1200));

    // Small number of errors accumulated should reset with window slide
    p1.continuity_count_error = 5;
    bool result = trigger.should_switch(p1, p2);

    // Window slides, new baseline established
    // 5 errors in ~1s = 5/sec, which is below threshold of 10
    EXPECT_FALSE(result);
}

TEST_F(QualitySwitchTriggerTest, MultipleSwitchesAccumulate) {
    QualitySwitchTrigger trigger(config);

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};

    // First switch: sync loss
    trigger.should_switch(p1, p2);  // baseline
    std::this_thread::sleep_for(std::chrono::milliseconds(150));
    p1.sync_loss = 1;
    EXPECT_TRUE(trigger.should_switch(p1, p2));
    EXPECT_EQ(trigger.total_quality_switches(), 1);

    // After switch, window resets (counter resets to new baseline)
    std::this_thread::sleep_for(std::chrono::milliseconds(150));

    // Second switch: more sync loss
    p1.sync_loss = 2;  // delta of 1 from last baseline
    EXPECT_TRUE(trigger.should_switch(p1, p2));
    EXPECT_EQ(trigger.total_quality_switches(), 2);
}

TEST_F(QualitySwitchTriggerTest, SnapshotReturnsCurrentRates) {
    QualitySwitchTrigger trigger(config);

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};

    // Establish baseline
    trigger.should_switch(p1, p2);

    // Wait and add errors
    std::this_thread::sleep_for(std::chrono::milliseconds(500));
    p1.continuity_count_error = 5;
    p2.transport_error = 2;
    p2.pcr_discontinuity_error = 1;

    auto snap = trigger.get_snapshot(p1, p2);

    EXPECT_GT(snap.window_seconds, 0.4);  // At least 400ms elapsed
    EXPECT_GT(snap.cc_errors_per_sec, 0);
    EXPECT_GT(snap.tei_errors_per_sec, 0);
    EXPECT_GT(snap.pcr_errors_per_sec, 0);
    EXPECT_EQ(snap.sync_losses_in_window, 0);
    EXPECT_FALSE(snap.thresholds_exceeded);  // Below thresholds
}

TEST_F(QualitySwitchTriggerTest, SnapshotShowsExceededThresholds) {
    QualitySwitchTrigger trigger(config);

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};

    // Establish baseline
    trigger.should_switch(p1, p2);

    std::this_thread::sleep_for(std::chrono::milliseconds(200));

    // Add enough errors to exceed threshold
    p1.continuity_count_error = 100;  // Way above threshold

    auto snap = trigger.get_snapshot(p1, p2);
    EXPECT_TRUE(snap.thresholds_exceeded);
}

TEST_F(QualitySwitchTriggerTest, NoBaselineNoSnapshot) {
    QualitySwitchTrigger trigger(config);

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};
    p1.continuity_count_error = 100;

    // No baseline established yet
    auto snap = trigger.get_snapshot(p1, p2);
    EXPECT_EQ(snap.window_seconds, 0);
    EXPECT_EQ(snap.cc_errors_per_sec, 0);
    EXPECT_FALSE(snap.thresholds_exceeded);
}
