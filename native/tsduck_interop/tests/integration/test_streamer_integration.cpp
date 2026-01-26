// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#include <gtest/gtest.h>
#include "streaming/stream_pipeline.hpp"
#include "streaming/streaming_types.hpp"
#include "core/constants.hpp"
#include "tsduck_interop.h"

#include <vector>
#include <atomic>
#include <chrono>
#include <thread>
#include <mutex>

using namespace tsduck_interop;
using namespace tsduck_interop::streaming;

// ============================================================================
// Test Fixtures
// ============================================================================

class StreamerIntegrationTest : public ::testing::Test {
protected:
    StreamerConfig config{};

    void SetUp() override {
        config.connect_timeout_ms = 2000;
        config.response_timeout_ms = 2000;
        config.stall_timeout_ms = 1000;
        config.max_retries = 3;
        config.initial_backoff_ms = 100;
        config.max_backoff_ms = 1000;
        config.backoff_multiplier = 2.0;
        config.backoff_jitter_ms = 50;
        config.output_fd = -1;  // Callback mode
        config.alignment_buffer_packets = 8;
        config.enable_restamp = 0;  // Disable for basic tests
        config.restamp_mode = 0;
        config.low_speed_limit_bytes = 100;
        config.low_speed_time_sec = 2;
        config.stalls_before_switch = 2;
    }

    // Create synthetic TS packets with PES/PTS for testing
    std::vector<uint8_t> createTsPacket(uint16_t pid = 0x100, bool with_pts = false, int64_t pts = 0) {
        std::vector<uint8_t> packet(ts::PKT_SIZE);
        ts::TSPacket& pkt = *reinterpret_cast<ts::TSPacket*>(packet.data());
        pkt = ts::NullPacket;
        pkt.setPID(ts::PID(pid));
        pkt.b[1] |= 0x40;  // Set PUSI
        pkt.b[3] = 0x10;  // Payload only, CC=0

        if (with_pts) {
            // PES header structure
            pkt.b[4] = 0x00;   // PES start code
            pkt.b[5] = 0x00;
            pkt.b[6] = 0x01;
            pkt.b[7] = ts::SID_VIDEO;
            pkt.b[8] = 0x00;   // PES length MSB
            pkt.b[9] = 0x00;   // PES length LSB (unbounded)
            pkt.b[10] = 0x80;  // Marker bits
            pkt.b[11] = 0x80;  // PTS present
            pkt.b[12] = 0x05;  // PES header data length
            // Encode PTS using TsDuck
            pkt.setPTS(static_cast<uint64_t>(pts & ((1LL << 33) - 1)));
        }

        return packet;
    }

    std::vector<uint8_t> createTsStream(int packet_count, uint16_t pid = 0x100) {
        std::vector<uint8_t> stream;
        for (int i = 0; i < packet_count; i++) {
            auto pkt = createTsPacket(pid);
            pkt[3] = static_cast<uint8_t>(0x10 | (i & 0x0F));  // Vary CC
            stream.insert(stream.end(), pkt.begin(), pkt.end());
        }
        return stream;
    }
};

// ============================================================================
// Lifecycle Tests
// ============================================================================

TEST_F(StreamerIntegrationTest, CreateDestroy) {
    StreamPipeline pipeline(config, nullptr);
    // Should not crash; always creates internal analyzer
    EXPECT_NE(pipeline.analyzer(), nullptr);
}

TEST_F(StreamerIntegrationTest, CreateWithAnalyzerConfig) {
    TsDuckConfigNative analyzer_cfg{};
    analyzer_cfg.metrics_interval_ms = 1000;
    analyzer_cfg.enable_tr101290 = 1;
    analyzer_cfg.sample_size_bytes = ts::PKT_SIZE * 100;
    analyzer_cfg.enable_auto_restamp = 0;

    StreamPipeline pipeline(config, &analyzer_cfg);
    EXPECT_NE(pipeline.analyzer(), nullptr);
}

TEST_F(StreamerIntegrationTest, StartWithoutUrls) {
    StreamPipeline pipeline(config, nullptr);
    bool started = pipeline.start();
    EXPECT_FALSE(started);  // Should fail without URLs
}

TEST_F(StreamerIntegrationTest, StartStop) {
    StreamPipeline pipeline(config, nullptr);
    pipeline.add_url("http://invalid.example.com/stream.ts");

    bool started = pipeline.start();
    EXPECT_TRUE(started);

    // Give it time to fail connecting
    std::this_thread::sleep_for(std::chrono::milliseconds(100));

    pipeline.stop();

    StreamerStatus status{};
    pipeline.get_status(&status);
    EXPECT_TRUE(status.state == static_cast<int32_t>(StreamerState::Stopped) ||
                status.state == static_cast<int32_t>(StreamerState::Failed));
}

TEST_F(StreamerIntegrationTest, DoubleStart) {
    StreamPipeline pipeline(config, nullptr);
    pipeline.add_url("http://invalid.example.com/stream.ts");

    bool started1 = pipeline.start();
    EXPECT_TRUE(started1);

    bool started2 = pipeline.start();
    EXPECT_FALSE(started2);  // Already running

    pipeline.stop();
}

TEST_F(StreamerIntegrationTest, DoubleStop) {
    StreamPipeline pipeline(config, nullptr);
    pipeline.add_url("http://invalid.example.com/stream.ts");
    pipeline.start();

    pipeline.stop();
    pipeline.stop();  // Should not crash
}

// ============================================================================
// URL Management Tests
// ============================================================================

TEST_F(StreamerIntegrationTest, AddAndClearUrls) {
    StreamPipeline pipeline(config, nullptr);

    pipeline.add_url("http://example.com/stream1.ts");
    pipeline.add_url("http://example.com/stream2.ts");
    pipeline.add_url("http://example.com/stream3.ts");

    StreamerStatus status{};
    // Status reports URL count after start
    pipeline.start();
    std::this_thread::sleep_for(std::chrono::milliseconds(50));
    pipeline.get_status(&status);
    EXPECT_EQ(status.url_count, 3);

    pipeline.stop();
}

// ============================================================================
// Status Tests
// ============================================================================

TEST_F(StreamerIntegrationTest, InitialStatus) {
    StreamPipeline pipeline(config, nullptr);
    pipeline.add_url("http://example.com/stream.ts");

    StreamerStatus status{};
    bool ok = pipeline.get_status(&status);
    EXPECT_TRUE(ok);
    EXPECT_EQ(status.state, static_cast<int32_t>(StreamerState::Idle));
    EXPECT_EQ(status.bytes_received, 0);
    EXPECT_EQ(status.packets_output, 0);
    EXPECT_EQ(status.switches_completed, 0);
}

TEST_F(StreamerIntegrationTest, NullStatusPointer) {
    StreamPipeline pipeline(config, nullptr);
    bool ok = pipeline.get_status(nullptr);
    EXPECT_FALSE(ok);
}

// ============================================================================
// Event Callback Tests
// ============================================================================

TEST_F(StreamerIntegrationTest, EventCallback) {
    std::atomic<int> event_count{0};
    std::vector<int32_t> events;
    std::mutex events_mutex;

    StreamPipeline pipeline(config, nullptr);
    pipeline.add_url("http://invalid.example.com/stream.ts");

    pipeline.set_event_callback([](int32_t event, int32_t /*detail*/, void* user_data) {
        auto* count = static_cast<std::atomic<int>*>(user_data);
        count->fetch_add(1, std::memory_order_relaxed);
    }, &event_count);

    pipeline.start();
    std::this_thread::sleep_for(std::chrono::milliseconds(500));
    pipeline.stop();

    // Should have received at least one event (error or reconnecting)
    EXPECT_GT(event_count.load(), 0);
}

// ============================================================================
// Output Callback Tests
// ============================================================================

TEST_F(StreamerIntegrationTest, OutputCallbackSet) {
    std::atomic<int64_t> bytes_received{0};

    StreamPipeline pipeline(config, nullptr);
    pipeline.set_output_callback([](const uint8_t* /*data*/, int32_t length, void* user_data) {
        auto* counter = static_cast<std::atomic<int64_t>*>(user_data);
        counter->fetch_add(length, std::memory_order_relaxed);
    }, &bytes_received);

    // Output callback is set but we can't easily test it without a real HTTP server.
    // This test just verifies the callback setup doesn't crash.
    EXPECT_EQ(bytes_received.load(), 0);
}

// ============================================================================
// Switch Request Tests
// ============================================================================

TEST_F(StreamerIntegrationTest, RequestSwitch) {
    StreamPipeline pipeline(config, nullptr);
    pipeline.add_url("http://invalid1.example.com/stream.ts");
    pipeline.add_url("http://invalid2.example.com/stream.ts");

    pipeline.start();
    std::this_thread::sleep_for(std::chrono::milliseconds(100));

    // Request switch (async, should not block)
    pipeline.request_switch();

    std::this_thread::sleep_for(std::chrono::milliseconds(200));
    pipeline.stop();

    StreamerStatus status{};
    pipeline.get_status(&status);
    // May or may not have completed switch depending on timing
    EXPECT_GE(status.switches_completed, 0);
}

// ============================================================================
// C API Tests
// ============================================================================

TEST_F(StreamerIntegrationTest, CApiCreateDestroy) {
    TsDuckStreamerConfigNative native_config{};
    native_config.connect_timeout_ms = 2000;
    native_config.response_timeout_ms = 2000;
    native_config.stall_timeout_ms = 1000;
    native_config.max_retries = 3;
    native_config.initial_backoff_ms = 100;
    native_config.max_backoff_ms = 1000;
    native_config.backoff_multiplier = 2.0;
    native_config.backoff_jitter_ms = 50;
    native_config.output_fd = -1;
    native_config.alignment_buffer_packets = 8;
    native_config.enable_restamp = 0;
    native_config.restamp_mode = 0;
    native_config.low_speed_limit_bytes = 100;
    native_config.low_speed_time_sec = 2;
    native_config.stalls_before_switch = 2;

    auto handle = tsduck_streamer_create(&native_config, nullptr);
    ASSERT_NE(handle, nullptr);

    tsduck_streamer_destroy(handle);
}

TEST_F(StreamerIntegrationTest, CApiNullConfig) {
    auto handle = tsduck_streamer_create(nullptr, nullptr);
    ASSERT_NE(handle, nullptr);
    tsduck_streamer_destroy(handle);
}

TEST_F(StreamerIntegrationTest, CApiAddUrl) {
    auto handle = tsduck_streamer_create(nullptr, nullptr);
    ASSERT_NE(handle, nullptr);

    int32_t result = tsduck_streamer_add_url(handle, "http://example.com/stream.ts");
    EXPECT_EQ(result, TSDUCK_OK);

    result = tsduck_streamer_add_url(handle, nullptr);
    EXPECT_EQ(result, TSDUCK_ERROR_INVALID_DATA);

    tsduck_streamer_destroy(handle);
}

TEST_F(StreamerIntegrationTest, CApiGetStatus) {
    auto handle = tsduck_streamer_create(nullptr, nullptr);
    ASSERT_NE(handle, nullptr);

    TsDuckStreamerStatusNative status{};
    bool ok = tsduck_streamer_get_status(handle, &status);
    EXPECT_TRUE(ok);
    EXPECT_EQ(status.state, STREAMER_STATE_IDLE);

    tsduck_streamer_destroy(handle);
}

TEST_F(StreamerIntegrationTest, CApiGetAnalyzer) {
    auto handle = tsduck_streamer_create(nullptr, nullptr);
    ASSERT_NE(handle, nullptr);

    auto analyzer = tsduck_streamer_get_analyzer(handle);
    EXPECT_NE(analyzer, nullptr);

    tsduck_streamer_destroy(handle);
}

TEST_F(StreamerIntegrationTest, CApiNullHandle) {
    // All functions should handle null gracefully
    tsduck_streamer_destroy(nullptr);
    EXPECT_EQ(tsduck_streamer_add_url(nullptr, "test"), TSDUCK_ERROR_NULL_HANDLE);
    tsduck_streamer_clear_urls(nullptr);
    tsduck_streamer_set_output_fd(nullptr, 1);
    EXPECT_FALSE(tsduck_streamer_start(nullptr));
    tsduck_streamer_stop(nullptr);
    tsduck_streamer_request_switch(nullptr);

    TsDuckStreamerStatusNative status{};
    EXPECT_FALSE(tsduck_streamer_get_status(nullptr, &status));
    EXPECT_EQ(tsduck_streamer_get_analyzer(nullptr), nullptr);
}

TEST_F(StreamerIntegrationTest, CApiStartStopCycle) {
    TsDuckStreamerConfigNative native_config{};
    native_config.connect_timeout_ms = 1000;
    native_config.stall_timeout_ms = 500;
    native_config.max_retries = 2;
    native_config.initial_backoff_ms = 50;
    native_config.max_backoff_ms = 200;
    native_config.backoff_multiplier = 2.0;
    native_config.backoff_jitter_ms = 10;
    native_config.output_fd = -1;
    native_config.alignment_buffer_packets = 4;
    native_config.enable_restamp = 0;
    native_config.low_speed_limit_bytes = 100;
    native_config.low_speed_time_sec = 1;
    native_config.stalls_before_switch = 2;

    auto handle = tsduck_streamer_create(&native_config, nullptr);
    ASSERT_NE(handle, nullptr);

    tsduck_streamer_add_url(handle, "http://invalid.example.com/stream.ts");

    EXPECT_TRUE(tsduck_streamer_start(handle));
    std::this_thread::sleep_for(std::chrono::milliseconds(200));
    tsduck_streamer_stop(handle);

    TsDuckStreamerStatusNative status{};
    tsduck_streamer_get_status(handle, &status);
    EXPECT_TRUE(status.state == STREAMER_STATE_STOPPED ||
                status.state == STREAMER_STATE_FAILED);

    tsduck_streamer_destroy(handle);
}

// ============================================================================
// Keyframe Aligner Integration Tests
// ============================================================================

#include "streaming/keyframe_aligner.hpp"

class KeyframeAlignerIntegrationTest : public ::testing::Test {
protected:
    KeyframeAligner aligner{100};  // 100 packet buffer for tests

    // Create a video TS packet with specific characteristics
    std::vector<uint8_t> createVideoPacket(uint16_t pid = 0x100, uint8_t cc = 0,
                                            bool pusi = false, bool rai = false) {
        std::vector<uint8_t> packet(ts::PKT_SIZE);
        ts::TSPacket& pkt = *reinterpret_cast<ts::TSPacket*>(packet.data());
        pkt = ts::NullPacket;
        pkt.setPID(ts::PID(pid));
        pkt.b[3] = static_cast<uint8_t>(0x10 | (cc & 0x0F));  // Payload only

        if (pusi) {
            pkt.b[1] |= 0x40;  // Set PUSI
        }

        if (rai) {
            // Add adaptation field with RAI
            pkt.b[3] = static_cast<uint8_t>(0x30 | (cc & 0x0F));  // AF + payload
            pkt.b[4] = 7;  // AF length
            pkt.b[5] = 0x40;  // RAI flag set
            // Fill rest of AF with stuffing
            for (int i = 6; i < 12; i++) {
                pkt.b[i] = 0xFF;
            }
        }

        return packet;
    }

    // Create a video TS packet with PES header (video stream)
    std::vector<uint8_t> createVideoPesPacket(uint16_t pid = 0x100, uint8_t cc = 0,
                                               bool rai = false) {
        std::vector<uint8_t> packet(ts::PKT_SIZE);
        ts::TSPacket& pkt = *reinterpret_cast<ts::TSPacket*>(packet.data());
        pkt = ts::NullPacket;
        pkt.setPID(ts::PID(pid));
        pkt.b[1] |= 0x40;  // Set PUSI

        if (rai) {
            // Adaptation field with RAI
            pkt.b[3] = static_cast<uint8_t>(0x30 | (cc & 0x0F));
            pkt.b[4] = 7;
            pkt.b[5] = 0x40;  // RAI flag
            for (int i = 6; i < 12; i++) {
                pkt.b[i] = 0xFF;
            }
            // PES header starts at byte 12
            pkt.b[12] = 0x00;  // PES start code
            pkt.b[13] = 0x00;
            pkt.b[14] = 0x01;
            pkt.b[15] = ts::SID_VIDEO;  // Video stream ID (0xE0)
        } else {
            pkt.b[3] = static_cast<uint8_t>(0x10 | (cc & 0x0F));
            // PES header starts at byte 4
            pkt.b[4] = 0x00;  // PES start code
            pkt.b[5] = 0x00;
            pkt.b[6] = 0x01;
            pkt.b[7] = ts::SID_VIDEO;  // Video stream ID
        }

        return packet;
    }

    // Create multiple packets as a stream buffer
    std::vector<uint8_t> createPacketStream(int count, uint16_t pid = 0x100) {
        std::vector<uint8_t> stream;
        for (int i = 0; i < count; i++) {
            auto pkt = createVideoPacket(pid, static_cast<uint8_t>(i & 0x0F));
            stream.insert(stream.end(), pkt.begin(), pkt.end());
        }
        return stream;
    }
};

// Basic state transitions
TEST_F(KeyframeAlignerIntegrationTest, InitialStateIsPassthrough) {
    EXPECT_FALSE(aligner.is_waiting());
}

TEST_F(KeyframeAlignerIntegrationTest, StartWaitingChangesState) {
    aligner.start_waiting();
    EXPECT_TRUE(aligner.is_waiting());
}

TEST_F(KeyframeAlignerIntegrationTest, StopWaitingResetsState) {
    aligner.start_waiting();
    aligner.stop_waiting();
    EXPECT_FALSE(aligner.is_waiting());
}

// Pass-through mode tests
TEST_F(KeyframeAlignerIntegrationTest, PassthroughReturnsInputUnchanged) {
    auto stream = createPacketStream(5);

    auto result = aligner.process(stream.data(), static_cast<int32_t>(stream.size()));

    EXPECT_EQ(result.data, stream.data());
    EXPECT_EQ(result.length, static_cast<int32_t>(stream.size()));
    EXPECT_FALSE(result.found_keyframe);
}

// Buffering mode tests
TEST_F(KeyframeAlignerIntegrationTest, WaitingModeBuffersData) {
    aligner.start_waiting();
    auto stream = createPacketStream(5);

    auto result = aligner.process(stream.data(), static_cast<int32_t>(stream.size()));

    // Should be buffering, not outputting yet
    EXPECT_EQ(result.length, 0);
    EXPECT_FALSE(result.found_keyframe);
    EXPECT_EQ(aligner.packets_buffered(), 5);
}

// RAI detection tests
TEST_F(KeyframeAlignerIntegrationTest, DetectsRaiOnVideoPid) {
    aligner.start_waiting();

    // First: send a video PES packet to identify the PID as video
    auto pes_pkt = createVideoPesPacket(0x100, 0, false);
    auto result1 = aligner.process(pes_pkt.data(), static_cast<int32_t>(pes_pkt.size()));
    EXPECT_EQ(result1.length, 0);  // Still buffering

    // Then: send packet with RAI on the same video PID
    auto rai_pkt = createVideoPacket(0x100, 1, false, true);
    auto result2 = aligner.process(rai_pkt.data(), static_cast<int32_t>(rai_pkt.size()));

    // Should detect keyframe at the RAI packet
    EXPECT_GT(result2.length, 0);
    EXPECT_TRUE(result2.found_keyframe);
    EXPECT_FALSE(aligner.is_waiting());
}

TEST_F(KeyframeAlignerIntegrationTest, DetectsRaiOnVideoPusiPacket) {
    aligner.start_waiting();

    // Send a video PES packet WITH RAI
    auto pkt = createVideoPesPacket(0x100, 0, true);
    auto result = aligner.process(pkt.data(), static_cast<int32_t>(pkt.size()));

    // Should immediately detect keyframe
    EXPECT_GT(result.length, 0);
    EXPECT_TRUE(result.found_keyframe);
    EXPECT_FALSE(aligner.is_waiting());
}

// Buffer limit tests
TEST_F(KeyframeAlignerIntegrationTest, EmitsAfterBufferLimitReached) {
    KeyframeAligner small_aligner{10};  // Very small buffer
    small_aligner.start_waiting();

    // Create 15 packets (exceeds 10 packet limit)
    auto stream = createPacketStream(15);

    auto result = small_aligner.process(stream.data(), static_cast<int32_t>(stream.size()));

    // Should emit all data even without finding keyframe
    EXPECT_GT(result.length, 0);
    EXPECT_FALSE(result.found_keyframe);  // No keyframe, just limit exceeded
    EXPECT_FALSE(small_aligner.is_waiting());
}

// Incremental processing tests
TEST_F(KeyframeAlignerIntegrationTest, AccumulatesMultipleChunks) {
    aligner.start_waiting();

    auto chunk1 = createPacketStream(3);
    auto result1 = aligner.process(chunk1.data(), static_cast<int32_t>(chunk1.size()));
    EXPECT_EQ(result1.length, 0);
    EXPECT_EQ(aligner.packets_buffered(), 3);

    auto chunk2 = createPacketStream(4);
    auto result2 = aligner.process(chunk2.data(), static_cast<int32_t>(chunk2.size()));
    EXPECT_EQ(result2.length, 0);
    EXPECT_EQ(aligner.packets_buffered(), 7);
}

TEST_F(KeyframeAlignerIntegrationTest, FindsKeyframeInLaterChunk) {
    aligner.start_waiting();

    // First chunk: regular packets
    auto chunk1 = createPacketStream(5);
    aligner.process(chunk1.data(), static_cast<int32_t>(chunk1.size()));

    // Second chunk: video PES to identify PID
    auto pes_pkt = createVideoPesPacket(0x100, 5, false);
    aligner.process(pes_pkt.data(), static_cast<int32_t>(pes_pkt.size()));

    // Third chunk: packet with RAI
    auto rai_pkt = createVideoPacket(0x100, 6, false, true);
    auto result = aligner.process(rai_pkt.data(), static_cast<int32_t>(rai_pkt.size()));

    EXPECT_GT(result.length, 0);
    EXPECT_TRUE(result.found_keyframe);
}

// Clear buffer tests
TEST_F(KeyframeAlignerIntegrationTest, ClearBufferResetsState) {
    aligner.start_waiting();
    auto stream = createPacketStream(5);
    aligner.process(stream.data(), static_cast<int32_t>(stream.size()));

    EXPECT_EQ(aligner.packets_buffered(), 5);

    aligner.clear_buffer();

    EXPECT_EQ(aligner.packets_buffered(), 0);
}

// Output data correctness tests
TEST_F(KeyframeAlignerIntegrationTest, OutputStartsFromKeyframe) {
    aligner.start_waiting();

    // Send 3 non-keyframe packets
    auto pre_packets = createPacketStream(3);
    aligner.process(pre_packets.data(), static_cast<int32_t>(pre_packets.size()));

    // Send video PES to identify PID
    auto pes_pkt = createVideoPesPacket(0x100, 3, false);
    aligner.process(pes_pkt.data(), static_cast<int32_t>(pes_pkt.size()));

    // Send keyframe packet with RAI
    auto rai_pkt = createVideoPacket(0x100, 4, false, true);
    auto result = aligner.process(rai_pkt.data(), static_cast<int32_t>(rai_pkt.size()));

    // Output should start from the PES packet (packet index 3)
    // Since RAI was found on packet 4, and PID was identified as video at packet 3
    EXPECT_TRUE(result.found_keyframe);
    EXPECT_GT(result.length, 0);

    // Verify we're outputting at least from the keyframe position
    // (may include some earlier packets depending on implementation)
    int32_t output_packets = result.length / static_cast<int32_t>(ts::PKT_SIZE);
    EXPECT_GE(output_packets, 1);  // At least the keyframe packet
}

// Multiple PIDs test
TEST_F(KeyframeAlignerIntegrationTest, TracksMultiplePidsIndependently) {
    aligner.start_waiting();

    // Video PID 0x100
    auto video_pes = createVideoPesPacket(0x100, 0, false);
    aligner.process(video_pes.data(), static_cast<int32_t>(video_pes.size()));

    // Audio PID 0x101 (not video)
    auto audio_pkt = createVideoPacket(0x101, 0, true, true);  // Even with RAI, shouldn't trigger
    // Note: This packet has PUSI but no video stream ID in PES header
    audio_pkt[4] = 0x00;
    audio_pkt[5] = 0x00;
    audio_pkt[6] = 0x01;
    audio_pkt[7] = 0xC0;  // MPEG Audio stream ID (0xC0-0xDF range)
    auto result1 = aligner.process(audio_pkt.data(), static_cast<int32_t>(audio_pkt.size()));

    // Should still be buffering (audio RAI doesn't count)
    EXPECT_EQ(result1.length, 0);

    // Now send video with RAI
    auto video_rai = createVideoPacket(0x100, 1, false, true);
    auto result2 = aligner.process(video_rai.data(), static_cast<int32_t>(video_rai.size()));

    // Now should detect keyframe
    EXPECT_TRUE(result2.found_keyframe);
}

// State machine integration test
TEST_F(KeyframeAlignerIntegrationTest, FullSwitchCycle) {
    // Initial state: pass-through
    EXPECT_FALSE(aligner.is_waiting());

    auto initial_stream = createPacketStream(5);
    auto result1 = aligner.process(initial_stream.data(), static_cast<int32_t>(initial_stream.size()));
    EXPECT_EQ(result1.data, initial_stream.data());  // Pass-through

    // Simulate URL switch
    aligner.start_waiting();
    EXPECT_TRUE(aligner.is_waiting());

    // Buffering phase
    auto pre_keyframe = createPacketStream(3);
    auto result2 = aligner.process(pre_keyframe.data(), static_cast<int32_t>(pre_keyframe.size()));
    EXPECT_EQ(result2.length, 0);  // Buffering

    // Identify video PID
    auto pes_pkt = createVideoPesPacket(0x100, 3, false);
    aligner.process(pes_pkt.data(), static_cast<int32_t>(pes_pkt.size()));

    // Keyframe arrives
    auto keyframe_pkt = createVideoPacket(0x100, 4, false, true);
    auto result3 = aligner.process(keyframe_pkt.data(), static_cast<int32_t>(keyframe_pkt.size()));
    EXPECT_TRUE(result3.found_keyframe);
    EXPECT_GT(result3.length, 0);

    // Back to pass-through
    EXPECT_FALSE(aligner.is_waiting());
    aligner.clear_buffer();

    auto post_keyframe = createPacketStream(5);
    auto result4 = aligner.process(post_keyframe.data(), static_cast<int32_t>(post_keyframe.size()));
    EXPECT_EQ(result4.data, post_keyframe.data());  // Pass-through again
}

// Edge case: Empty input
TEST_F(KeyframeAlignerIntegrationTest, HandlesEmptyInput) {
    aligner.start_waiting();

    auto result = aligner.process(nullptr, 0);

    EXPECT_EQ(result.length, 0);
    EXPECT_EQ(aligner.packets_buffered(), 0);
}

// Edge case: Single packet
TEST_F(KeyframeAlignerIntegrationTest, HandlesSinglePacket) {
    aligner.start_waiting();

    auto pkt = createVideoPacket(0x100, 0);
    auto result = aligner.process(pkt.data(), static_cast<int32_t>(pkt.size()));

    EXPECT_EQ(result.length, 0);  // Still buffering
    EXPECT_EQ(aligner.packets_buffered(), 1);
}

// Stress test: Large buffer accumulation
TEST_F(KeyframeAlignerIntegrationTest, HandlesLargeBufferAccumulation) {
    KeyframeAligner large_aligner{500};
    large_aligner.start_waiting();

    // Send 400 packets in chunks
    for (int i = 0; i < 8; i++) {
        auto chunk = createPacketStream(50);
        large_aligner.process(chunk.data(), static_cast<int32_t>(chunk.size()));
    }

    EXPECT_EQ(large_aligner.packets_buffered(), 400);

    // Send keyframe to release
    auto pes_pkt = createVideoPesPacket(0x100, 0, false);
    large_aligner.process(pes_pkt.data(), static_cast<int32_t>(pes_pkt.size()));

    auto rai_pkt = createVideoPacket(0x100, 1, false, true);
    auto result = large_aligner.process(rai_pkt.data(), static_cast<int32_t>(rai_pkt.size()));

    EXPECT_TRUE(result.found_keyframe);
    EXPECT_GT(result.length, 0);
}
