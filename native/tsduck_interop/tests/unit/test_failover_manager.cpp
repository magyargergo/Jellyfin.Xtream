// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#include <gtest/gtest.h>
#include "streaming/failover_manager.hpp"
#include <thread>
#include <chrono>

using namespace tsduck_interop::streaming;

class FailoverManagerTest : public ::testing::Test {
protected:
    StreamerConfig config{};

    void SetUp() override {
        config.connect_timeout_ms = 5000;
        config.stall_timeout_ms = 200;  // Short for testing
        config.max_retries = 5;
        config.initial_backoff_ms = 100;
        config.max_backoff_ms = 5000;
        config.backoff_multiplier = 2.0;
        config.backoff_jitter_ms = 50;
        config.stalls_before_switch = 2;
    }
};

TEST_F(FailoverManagerTest, InitialState) {
    FailoverManager fm(config);
    EXPECT_EQ(fm.state(), StreamerState::Idle);
    EXPECT_FALSE(fm.is_active());
    EXPECT_FALSE(fm.is_terminal());
    EXPECT_EQ(fm.retry_count(), 0);
    EXPECT_EQ(fm.total_switches(), 0);
    EXPECT_EQ(fm.total_reconnections(), 0);
}

TEST_F(FailoverManagerTest, ConnectingTransition) {
    FailoverManager fm(config);
    fm.on_connecting();
    EXPECT_EQ(fm.state(), StreamerState::Connecting);
    EXPECT_TRUE(fm.is_active());
}

TEST_F(FailoverManagerTest, ConnectedTransition) {
    FailoverManager fm(config);
    fm.on_connecting();
    fm.on_connected();
    EXPECT_EQ(fm.state(), StreamerState::Streaming);
    EXPECT_TRUE(fm.is_active());
    EXPECT_EQ(fm.retry_count(), 0);
}

TEST_F(FailoverManagerTest, DisconnectedRetry) {
    FailoverManager fm(config);
    fm.on_connecting();
    fm.on_connected();

    auto action = fm.on_disconnected();
    EXPECT_EQ(action, FailoverManager::DisconnectAction::ShouldRetry);
    EXPECT_EQ(fm.state(), StreamerState::Reconnecting);
    EXPECT_EQ(fm.retry_count(), 1);
    EXPECT_EQ(fm.total_reconnections(), 1);
}

TEST_F(FailoverManagerTest, DisconnectedExhausted) {
    FailoverManager fm(config);
    fm.on_connecting();
    fm.on_connected();

    // Exhaust all retries
    FailoverManager::DisconnectAction action;
    for (int i = 0; i < config.max_retries; i++) {
        action = fm.on_disconnected();
    }

    EXPECT_EQ(action, FailoverManager::DisconnectAction::Failed);
    EXPECT_EQ(fm.state(), StreamerState::Failed);
    EXPECT_TRUE(fm.is_terminal());
    EXPECT_EQ(fm.retry_count(), config.max_retries);
}

TEST_F(FailoverManagerTest, ConnectedResetsRetries) {
    FailoverManager fm(config);
    fm.on_connecting();
    fm.on_connected();

    // Some disconnections
    fm.on_disconnected();
    fm.on_disconnected();
    EXPECT_EQ(fm.retry_count(), 2);

    // Reconnect resets
    fm.on_connected();
    EXPECT_EQ(fm.retry_count(), 0);
    EXPECT_EQ(fm.state(), StreamerState::Streaming);
}

TEST_F(FailoverManagerTest, StallDetection) {
    FailoverManager fm(config);
    fm.on_connecting();
    fm.on_connected();

    // Right after connect, should not be stalled
    EXPECT_FALSE(fm.is_stalled());

    // Wait longer than stall timeout
    std::this_thread::sleep_for(std::chrono::milliseconds(config.stall_timeout_ms + 50));

    EXPECT_TRUE(fm.is_stalled());
}

TEST_F(FailoverManagerTest, DataReceivedResetsStall) {
    FailoverManager fm(config);
    fm.on_connecting();
    fm.on_connected();

    // Wait almost to stall timeout
    std::this_thread::sleep_for(std::chrono::milliseconds(config.stall_timeout_ms - 50));
    fm.on_data_received();

    // Should not be stalled after data received
    EXPECT_FALSE(fm.is_stalled());
}

TEST_F(FailoverManagerTest, StallTriggersSwitch) {
    FailoverManager fm(config);
    fm.on_connecting();
    fm.on_connected();

    // First stall: just reconnect
    bool should_switch = fm.on_stall();
    EXPECT_FALSE(should_switch);
    EXPECT_EQ(fm.consecutive_failures(), 1);

    // Second stall: should switch
    should_switch = fm.on_stall();
    EXPECT_TRUE(should_switch);
    EXPECT_EQ(fm.consecutive_failures(), 2);
}

TEST_F(FailoverManagerTest, SwitchingTransition) {
    FailoverManager fm(config);
    fm.on_connecting();
    fm.on_connected();

    // Accumulate some retries
    fm.on_disconnected();
    fm.on_disconnected();

    fm.on_switching();
    EXPECT_EQ(fm.state(), StreamerState::Switching);
    EXPECT_EQ(fm.retry_count(), 0);  // Reset on switch
    EXPECT_EQ(fm.consecutive_failures(), 0);  // Reset on switch
    EXPECT_EQ(fm.total_switches(), 1);
}

TEST_F(FailoverManagerTest, BackoffCalculation) {
    FailoverManager fm(config);
    fm.on_connecting();
    fm.on_connected();

    // First retry: ~100ms + jitter
    fm.on_disconnected();
    int32_t delay1 = fm.calculate_backoff_ms();
    EXPECT_GE(delay1, config.initial_backoff_ms);
    EXPECT_LE(delay1, config.initial_backoff_ms + config.backoff_jitter_ms);

    // Second retry: ~200ms + jitter
    fm.on_disconnected();
    int32_t delay2 = fm.calculate_backoff_ms();
    EXPECT_GE(delay2, config.initial_backoff_ms * 2);
    EXPECT_LE(delay2, config.initial_backoff_ms * 2 + config.backoff_jitter_ms);

    // Third retry: ~400ms + jitter
    fm.on_disconnected();
    int32_t delay3 = fm.calculate_backoff_ms();
    EXPECT_GE(delay3, config.initial_backoff_ms * 4);
}

TEST_F(FailoverManagerTest, BackoffCapped) {
    config.max_retries = 20;
    FailoverManager fm(config);
    fm.on_connecting();
    fm.on_connected();

    // Many retries should cap at max_backoff_ms
    for (int i = 0; i < 15; i++) {
        fm.on_disconnected();
    }

    int32_t delay = fm.calculate_backoff_ms();
    EXPECT_LE(delay, config.max_backoff_ms + config.backoff_jitter_ms);
}

TEST_F(FailoverManagerTest, StoppedTransition) {
    FailoverManager fm(config);
    fm.on_connecting();
    fm.on_connected();

    fm.on_stopped();
    EXPECT_EQ(fm.state(), StreamerState::Stopped);
    EXPECT_TRUE(fm.is_terminal());
    EXPECT_FALSE(fm.is_active());
}

TEST_F(FailoverManagerTest, Reset) {
    FailoverManager fm(config);
    fm.on_connecting();
    fm.on_connected();
    fm.on_disconnected();
    fm.on_switching();

    EXPECT_GT(fm.total_switches(), 0);
    EXPECT_GT(fm.total_reconnections(), 0);

    fm.reset();
    EXPECT_EQ(fm.state(), StreamerState::Idle);
    EXPECT_EQ(fm.retry_count(), 0);
    EXPECT_EQ(fm.total_switches(), 0);
    EXPECT_EQ(fm.total_reconnections(), 0);
}

TEST_F(FailoverManagerTest, MsSinceLastData) {
    FailoverManager fm(config);
    fm.on_connecting();
    fm.on_connected();

    std::this_thread::sleep_for(std::chrono::milliseconds(100));

    int64_t ms = fm.ms_since_last_data();
    EXPECT_GE(ms, 90);  // Allow some timing variance
    EXPECT_LE(ms, 200);
}

TEST_F(FailoverManagerTest, MultipleStallsResetOnSwitch) {
    FailoverManager fm(config);
    fm.on_connecting();
    fm.on_connected();

    fm.on_stall();
    EXPECT_EQ(fm.consecutive_failures(), 1);

    fm.on_switching();
    EXPECT_EQ(fm.consecutive_failures(), 0);

    // After switch, stall counter restarts
    fm.on_stall();
    EXPECT_EQ(fm.consecutive_failures(), 1);
    bool should_switch = fm.on_stall();
    EXPECT_TRUE(should_switch);
}

TEST_F(FailoverManagerTest, ConsecutiveDisconnectsTriggersSwitch) {
    FailoverManager fm(config);
    fm.on_connecting();
    fm.on_connected();

    // First disconnect: just retry
    auto action1 = fm.on_disconnected();
    EXPECT_EQ(action1, FailoverManager::DisconnectAction::ShouldRetry);
    EXPECT_EQ(fm.consecutive_failures(), 1);

    // Second disconnect: should trigger switch (stalls_before_switch = 2)
    auto action2 = fm.on_disconnected();
    EXPECT_EQ(action2, FailoverManager::DisconnectAction::ShouldSwitch);
    EXPECT_EQ(fm.consecutive_failures(), 2);
}

TEST_F(FailoverManagerTest, MixedStallsAndDisconnectsCountTogether) {
    FailoverManager fm(config);
    fm.on_connecting();
    fm.on_connected();

    // First failure: stall
    bool should_switch = fm.on_stall();
    EXPECT_FALSE(should_switch);
    EXPECT_EQ(fm.consecutive_failures(), 1);

    // Second failure: disconnect - should trigger switch
    auto action = fm.on_disconnected();
    EXPECT_EQ(action, FailoverManager::DisconnectAction::ShouldSwitch);
    EXPECT_EQ(fm.consecutive_failures(), 2);
}

TEST_F(FailoverManagerTest, SwitchResetsFailureCounter) {
    FailoverManager fm(config);
    fm.on_connecting();
    fm.on_connected();

    // Accumulate failures
    fm.on_disconnected();
    EXPECT_EQ(fm.consecutive_failures(), 1);

    // Switch resets
    fm.on_switching();
    EXPECT_EQ(fm.consecutive_failures(), 0);

    // New failures start from zero
    auto action = fm.on_disconnected();
    EXPECT_EQ(action, FailoverManager::DisconnectAction::ShouldRetry);
    EXPECT_EQ(fm.consecutive_failures(), 1);
}
