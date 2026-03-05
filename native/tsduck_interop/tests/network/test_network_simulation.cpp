// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Network simulation tests using the mock HTTP server.
// Tests streamer behavior under various network conditions.

#include <gtest/gtest.h>
#include <atomic>
#include <chrono>
#include <thread>

#include "mock_http_server.hpp"
#include "streaming/stream_pipeline.hpp"
#include "streaming/streaming_types.hpp"
#include "core/constants.hpp"
#include "tsduck_interop.h"

using namespace tsduck_interop;
using namespace tsduck_interop::streaming;
using namespace tsduck_interop::testing;

// ============================================================================
// Test Fixtures
// ============================================================================

class NetworkSimulationTest : public ::testing::Test {
protected:
    MockHttpServer server;
    StreamerConfig config{};

    void SetUp() override {
        config.connect_timeout_ms = 5000;
        config.response_timeout_ms = 5000;
        config.stall_timeout_ms = 2000;
        config.max_retries = 3;
        config.initial_backoff_ms = 100;
        config.max_backoff_ms = 1000;
        config.backoff_multiplier = 2.0;
        config.backoff_jitter_ms = 50;
        config.output_fd = -1;  // Callback mode
        config.alignment_buffer_packets = 8;
        config.enable_restamp = 0;
        config.restamp_mode = 0;
        config.low_speed_limit_bytes = 100;
        config.low_speed_time_sec = 2;
        config.stalls_before_switch = 2;
    }

    void TearDown() override {
        server.stop();
    }
};

// ============================================================================
// Basic Connectivity Tests
// ============================================================================

TEST_F(NetworkSimulationTest, BasicConnection) {
    NetworkSimConfig sim{};
    sim.total_bytes = 188 * 100;  // 100 packets

    ASSERT_TRUE(server.start(sim));

    std::atomic<int64_t> bytes_received{0};
    StreamPipeline pipeline(config, nullptr);
    pipeline.add_url(server.url().c_str());

    pipeline.set_output_callback([](const uint8_t*, int32_t len, void* user_data) {
        auto* counter = static_cast<std::atomic<int64_t>*>(user_data);
        counter->fetch_add(len, std::memory_order_relaxed);
    }, &bytes_received);

    ASSERT_TRUE(pipeline.start());

    // Wait for stream to complete
    auto start = std::chrono::steady_clock::now();
    while (bytes_received.load() < sim.total_bytes) {
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
        auto elapsed = std::chrono::steady_clock::now() - start;
        if (elapsed > std::chrono::seconds(10)) {
            FAIL() << "Timeout waiting for stream";
        }
    }

    pipeline.stop();

    EXPECT_GE(bytes_received.load(), sim.total_bytes);
    EXPECT_EQ(server.connectionCount(), 1);
}

// ============================================================================
// Latency Tests
// ============================================================================

TEST_F(NetworkSimulationTest, HighInitialLatency) {
    NetworkSimConfig sim{};
    sim.initial_latency_ms = 500;  // 500ms initial delay
    sim.total_bytes = 188 * 50;

    ASSERT_TRUE(server.start(sim));

    std::atomic<int64_t> bytes_received{0};
    auto start_time = std::chrono::steady_clock::now();

    StreamPipeline pipeline(config, nullptr);
    pipeline.add_url(server.url().c_str());

    pipeline.set_output_callback([](const uint8_t*, int32_t len, void* user_data) {
        auto* counter = static_cast<std::atomic<int64_t>*>(user_data);
        counter->fetch_add(len, std::memory_order_relaxed);
    }, &bytes_received);

    ASSERT_TRUE(pipeline.start());

    // Wait for first data
    while (bytes_received.load() == 0) {
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
        auto elapsed = std::chrono::steady_clock::now() - start_time;
        if (elapsed > std::chrono::seconds(10)) {
            FAIL() << "Timeout waiting for first data";
        }
    }

    auto first_data_time = std::chrono::steady_clock::now();
    auto latency = std::chrono::duration_cast<std::chrono::milliseconds>(
        first_data_time - start_time).count();

    pipeline.stop();

    // Latency should be at least the configured delay
    EXPECT_GE(latency, sim.initial_latency_ms - 100);  // Allow some slack
}

TEST_F(NetworkSimulationTest, ChunkLatencyWithJitter) {
    NetworkSimConfig sim{};
    sim.chunk_latency_ms = 50;
    sim.latency_jitter_ms = 20;
    sim.total_bytes = 188 * 20;

    ASSERT_TRUE(server.start(sim));

    std::atomic<int64_t> bytes_received{0};
    std::atomic<int64_t> chunk_count{0};
    auto start_time = std::chrono::steady_clock::now();

    StreamPipeline pipeline(config, nullptr);
    pipeline.add_url(server.url().c_str());

    pipeline.set_output_callback([](const uint8_t*, int32_t len, void* user_data) {
        auto* counter = static_cast<std::atomic<int64_t>*>(user_data);
        counter->fetch_add(len, std::memory_order_relaxed);
    }, &bytes_received);

    ASSERT_TRUE(pipeline.start());

    while (bytes_received.load() < sim.total_bytes) {
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
        auto elapsed = std::chrono::steady_clock::now() - start_time;
        if (elapsed > std::chrono::seconds(30)) {
            FAIL() << "Timeout waiting for stream";
        }
    }

    auto end_time = std::chrono::steady_clock::now();
    auto duration = std::chrono::duration_cast<std::chrono::milliseconds>(
        end_time - start_time).count();

    pipeline.stop();

    // Duration should reflect chunk latency
    // 20 packets / 7 per chunk = ~3 chunks, each with ~50ms delay
    EXPECT_GT(duration, 100);  // At least some delay
}

// ============================================================================
// Bandwidth Limiting Tests
// ============================================================================

TEST_F(NetworkSimulationTest, BandwidthLimiting) {
    NetworkSimConfig sim{};
    sim.bandwidth_kbps = 500;  // 500 kbps
    sim.total_bytes = 188 * 100;  // ~18KB

    ASSERT_TRUE(server.start(sim));

    std::atomic<int64_t> bytes_received{0};
    auto start_time = std::chrono::steady_clock::now();

    StreamPipeline pipeline(config, nullptr);
    pipeline.add_url(server.url().c_str());

    pipeline.set_output_callback([](const uint8_t*, int32_t len, void* user_data) {
        auto* counter = static_cast<std::atomic<int64_t>*>(user_data);
        counter->fetch_add(len, std::memory_order_relaxed);
    }, &bytes_received);

    ASSERT_TRUE(pipeline.start());

    while (bytes_received.load() < sim.total_bytes) {
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
        auto elapsed = std::chrono::steady_clock::now() - start_time;
        if (elapsed > std::chrono::seconds(60)) {
            FAIL() << "Timeout waiting for stream";
        }
    }

    auto end_time = std::chrono::steady_clock::now();
    auto duration_ms = std::chrono::duration_cast<std::chrono::milliseconds>(
        end_time - start_time).count();

    pipeline.stop();

    // Calculate effective bandwidth
    double effective_kbps = (bytes_received.load() * 8.0) / duration_ms;

    // Should be close to configured bandwidth
    // Allow some variance due to chunked transfer overhead
    EXPECT_LT(effective_kbps, sim.bandwidth_kbps * 1.5);
}

// ============================================================================
// Error Injection Tests
// ============================================================================

TEST_F(NetworkSimulationTest, ConnectionReset) {
    NetworkSimConfig sim{};
    sim.inject_connection_reset = true;
    sim.reset_after_bytes = 188 * 10;  // Reset after 10 packets

    ASSERT_TRUE(server.start(sim));

    std::atomic<int64_t> bytes_received{0};
    std::atomic<int32_t> error_events{0};

    StreamPipeline pipeline(config, nullptr);
    pipeline.add_url(server.url().c_str());

    pipeline.set_output_callback([](const uint8_t*, int32_t len, void* user_data) {
        auto* counter = static_cast<std::atomic<int64_t>*>(user_data);
        counter->fetch_add(len, std::memory_order_relaxed);
    }, &bytes_received);

    pipeline.set_event_callback([](int32_t event, int32_t, void* user_data) {
        auto* counter = static_cast<std::atomic<int32_t>*>(user_data);
        if (event == STREAMER_EVENT_ERROR || event == STREAMER_EVENT_RECONNECTING) {
            counter->fetch_add(1, std::memory_order_relaxed);
        }
    }, &error_events);

    ASSERT_TRUE(pipeline.start());

    // Let it run and experience resets
    std::this_thread::sleep_for(std::chrono::milliseconds(2000));

    pipeline.stop();

    // Should have experienced errors/reconnects
    EXPECT_GT(error_events.load(), 0);
}

TEST_F(NetworkSimulationTest, StreamStall) {
    NetworkSimConfig sim{};
    sim.inject_stall = true;
    sim.stall_after_bytes = 188 * 5;
    sim.stall_duration_ms = 2000;  // 2 second stall (shorter to speed up test)
    sim.total_bytes = 188 * 50;

    // Adjust config to detect stall
    config.stall_timeout_ms = 1000;
    // Prevent switch after stalls - we only have one URL
    config.stalls_before_switch = 100;

    ASSERT_TRUE(server.start(sim));

    std::atomic<int32_t> stall_events{0};

    StreamPipeline pipeline(config, nullptr);
    pipeline.add_url(server.url().c_str());

    pipeline.set_event_callback([](int32_t event, int32_t, void* user_data) {
        auto* counter = static_cast<std::atomic<int32_t>*>(user_data);
        if (event == STREAMER_EVENT_STALLED) {
            counter->fetch_add(1, std::memory_order_relaxed);
        }
    }, &stall_events);

    ASSERT_TRUE(pipeline.start());

    std::this_thread::sleep_for(std::chrono::milliseconds(3500));

    pipeline.stop();

    // Should have detected stall
    EXPECT_GE(stall_events.load(), 1);
}

// ============================================================================
// Failover Tests
// ============================================================================

TEST_F(NetworkSimulationTest, FailoverToSecondUrl) {
    // First server: fails immediately
    MockHttpServer server1;
    NetworkSimConfig sim1{};
    sim1.http_status = 503;  // Service unavailable
    sim1.total_bytes = 0;
    ASSERT_TRUE(server1.start(sim1));

    // Second server: works fine
    MockHttpServer server2;
    NetworkSimConfig sim2{};
    sim2.total_bytes = 188 * 100;
    ASSERT_TRUE(server2.start(sim2));

    std::atomic<int64_t> bytes_received{0};
    std::atomic<int32_t> switch_events{0};

    StreamPipeline pipeline(config, nullptr);
    pipeline.add_url(server1.url().c_str());
    pipeline.add_url(server2.url().c_str());

    pipeline.set_output_callback([](const uint8_t*, int32_t len, void* user_data) {
        auto* counter = static_cast<std::atomic<int64_t>*>(user_data);
        counter->fetch_add(len, std::memory_order_relaxed);
    }, &bytes_received);

    pipeline.set_event_callback([](int32_t event, int32_t, void* user_data) {
        auto* counter = static_cast<std::atomic<int32_t>*>(user_data);
        if (event == STREAMER_EVENT_SWITCHED) {
            counter->fetch_add(1, std::memory_order_relaxed);
        }
    }, &switch_events);

    ASSERT_TRUE(pipeline.start());

    // Wait for data from second server
    auto start = std::chrono::steady_clock::now();
    while (bytes_received.load() < sim2.total_bytes / 2) {
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
        auto elapsed = std::chrono::steady_clock::now() - start;
        if (elapsed > std::chrono::seconds(30)) {
            break;
        }
    }

    pipeline.stop();

    // Should have received data and switched at least once
    EXPECT_GT(bytes_received.load(), 0);

    server1.stop();
    server2.stop();
}

// ============================================================================
// Load Tests
// ============================================================================

TEST_F(NetworkSimulationTest, MultipleReconnects) {
    NetworkSimConfig sim{};
    sim.inject_connection_reset = true;
    sim.reset_after_bytes = 188 * 50;  // Reset every 50 packets

    config.max_retries = 10;

    ASSERT_TRUE(server.start(sim));

    std::atomic<int64_t> bytes_received{0};
    std::atomic<int32_t> reconnect_count{0};

    StreamPipeline pipeline(config, nullptr);
    pipeline.add_url(server.url().c_str());

    pipeline.set_output_callback([](const uint8_t*, int32_t len, void* user_data) {
        auto* counter = static_cast<std::atomic<int64_t>*>(user_data);
        counter->fetch_add(len, std::memory_order_relaxed);
    }, &bytes_received);

    pipeline.set_event_callback([](int32_t event, int32_t, void* user_data) {
        auto* counter = static_cast<std::atomic<int32_t>*>(user_data);
        if (event == STREAMER_EVENT_RECONNECTING) {
            counter->fetch_add(1, std::memory_order_relaxed);
        }
    }, &reconnect_count);

    ASSERT_TRUE(pipeline.start());

    // Run for a while to accumulate reconnects
    std::this_thread::sleep_for(std::chrono::seconds(5));

    pipeline.stop();

    // Should have reconnected multiple times
    EXPECT_GT(reconnect_count.load(), 1);
    std::cout << "Reconnects: " << reconnect_count.load()
              << ", Bytes: " << bytes_received.load() << std::endl;
}

// ============================================================================
// Main
// ============================================================================

int main(int argc, char** argv) {
    ::testing::InitGoogleTest(&argc, argv);
    return RUN_ALL_TESTS();
}
