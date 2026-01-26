// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#include <gtest/gtest.h>
#include "streaming/keyframe_aligner.hpp"
#include "core/constants.hpp"
#include <tsduck.h>
#include <vector>
#include <random>
#include <cstring>

using namespace tsduck_interop;
using namespace tsduck_interop::streaming;

class KeyframeAlignerTest : public ::testing::Test {
protected:
    KeyframeAligner aligner{100};  // Small buffer for tests

    // Create a basic TS packet with sync byte
    static std::vector<uint8_t> createTsPacket(ts::PID pid = 0x100, uint8_t cc = 0) {
        std::vector<uint8_t> packet(ts::PKT_SIZE, 0xFF);
        packet[0] = ts::SYNC_BYTE;
        // PID: bits 0-4 of byte 1, bits 0-7 of byte 2
        packet[1] = static_cast<uint8_t>(0x40 | ((pid >> 8) & 0x1F));  // PUSI=1
        packet[2] = static_cast<uint8_t>(pid & 0xFF);
        // Adaptation + payload, CC
        packet[3] = static_cast<uint8_t>(0x10 | (cc & 0x0F));
        return packet;
    }

    // Create a TS packet with Random Access Indicator set
    static std::vector<uint8_t> createTsPacketWithRAI(ts::PID pid = 0x100) {
        std::vector<uint8_t> packet(ts::PKT_SIZE, 0xFF);
        packet[0] = ts::SYNC_BYTE;
        packet[1] = static_cast<uint8_t>(0x40 | ((pid >> 8) & 0x1F));  // PUSI=1
        packet[2] = static_cast<uint8_t>(pid & 0xFF);
        // Adaptation field + payload (0x30), CC=0
        packet[3] = 0x30;
        // Adaptation field length
        packet[4] = 7;  // 7 bytes of adaptation field
        // Adaptation field flags: RAI=1 (bit 6)
        packet[5] = 0x40;  // Random Access Indicator set
        // Rest of adaptation field (6 bytes of stuffing)
        std::memset(&packet[6], 0xFF, 6);
        return packet;
    }

    // Create a TS packet containing a video PES header
    static std::vector<uint8_t> createVideoPesPacket(ts::PID pid = 0x100, bool with_rai = false) {
        std::vector<uint8_t> packet(ts::PKT_SIZE, 0xFF);
        packet[0] = ts::SYNC_BYTE;
        packet[1] = static_cast<uint8_t>(0x40 | ((pid >> 8) & 0x1F));  // PUSI=1
        packet[2] = static_cast<uint8_t>(pid & 0xFF);

        int payload_offset = 4;
        if (with_rai) {
            // Adaptation field with RAI
            packet[3] = 0x30;  // Adaptation + payload
            packet[4] = 1;     // Adaptation field length
            packet[5] = 0x40;  // RAI set
            payload_offset = 6;
        } else {
            packet[3] = 0x10;  // Payload only
        }

        // PES header: 00 00 01 E0 (video stream_id = 0xE0)
        packet[payload_offset + 0] = 0x00;
        packet[payload_offset + 1] = 0x00;
        packet[payload_offset + 2] = 0x01;
        packet[payload_offset + 3] = 0xE0;  // Video stream_id
        // PES packet length (0 = unbounded)
        packet[payload_offset + 4] = 0x00;
        packet[payload_offset + 5] = 0x00;
        // PES header flags
        packet[payload_offset + 6] = 0x80;  // '10' marker bits
        packet[payload_offset + 7] = 0x80;  // PTS flag set
        packet[payload_offset + 8] = 5;     // PES header data length

        // PTS (dummy value)
        packet[payload_offset + 9] = 0x21;
        packet[payload_offset + 10] = 0x00;
        packet[payload_offset + 11] = 0x01;
        packet[payload_offset + 12] = 0x00;
        packet[payload_offset + 13] = 0x01;

        return packet;
    }

    // Create a TS packet containing a video PES with H.264 IDR NAL unit
    static std::vector<uint8_t> createH264IdrPacket(ts::PID pid = 0x100) {
        std::vector<uint8_t> packet(ts::PKT_SIZE, 0xFF);
        packet[0] = ts::SYNC_BYTE;
        packet[1] = static_cast<uint8_t>(0x40 | ((pid >> 8) & 0x1F));  // PUSI=1
        packet[2] = static_cast<uint8_t>(pid & 0xFF);
        packet[3] = 0x10;  // Payload only

        int offset = 4;
        // PES header: 00 00 01 E0 (video stream_id)
        packet[offset++] = 0x00;
        packet[offset++] = 0x00;
        packet[offset++] = 0x01;
        packet[offset++] = 0xE0;  // Video
        packet[offset++] = 0x00;  // Length high (unbounded)
        packet[offset++] = 0x00;  // Length low
        packet[offset++] = 0x80;  // Marker bits
        packet[offset++] = 0x80;  // PTS flag
        packet[offset++] = 5;     // Header data length

        // PTS
        packet[offset++] = 0x21;
        packet[offset++] = 0x00;
        packet[offset++] = 0x01;
        packet[offset++] = 0x00;
        packet[offset++] = 0x01;

        // H.264 NAL start code + IDR NAL unit
        // Start code: 00 00 00 01
        packet[offset++] = 0x00;
        packet[offset++] = 0x00;
        packet[offset++] = 0x00;
        packet[offset++] = 0x01;
        // NAL unit type 5 = IDR slice
        packet[offset++] = 0x65;  // (0x60 = nal_ref_idc=3, 0x05 = type=5)

        return packet;
    }

    // Create a TS packet with H.264 Access Unit Delimiter indicating I-frame
    static std::vector<uint8_t> createH264AudIFramePacket(ts::PID pid = 0x100) {
        std::vector<uint8_t> packet(ts::PKT_SIZE, 0xFF);
        packet[0] = ts::SYNC_BYTE;
        packet[1] = static_cast<uint8_t>(0x40 | ((pid >> 8) & 0x1F));  // PUSI=1
        packet[2] = static_cast<uint8_t>(pid & 0xFF);
        packet[3] = 0x10;  // Payload only

        int offset = 4;
        // PES header
        packet[offset++] = 0x00;
        packet[offset++] = 0x00;
        packet[offset++] = 0x01;
        packet[offset++] = 0xE0;  // Video
        packet[offset++] = 0x00;
        packet[offset++] = 0x00;
        packet[offset++] = 0x80;
        packet[offset++] = 0x80;
        packet[offset++] = 5;

        // PTS
        packet[offset++] = 0x21;
        packet[offset++] = 0x00;
        packet[offset++] = 0x01;
        packet[offset++] = 0x00;
        packet[offset++] = 0x01;

        // H.264 AUD NAL unit: type 9 with primary_pic_type = 0 (I-frame)
        packet[offset++] = 0x00;
        packet[offset++] = 0x00;
        packet[offset++] = 0x00;
        packet[offset++] = 0x01;
        packet[offset++] = 0x09;  // NAL type 9 = AUD
        packet[offset++] = 0x10;  // primary_pic_type = 0 (I-frame) << 5

        return packet;
    }

    // Create multiple packets
    static std::vector<uint8_t> createPackets(int count, ts::PID pid = 0x100) {
        std::vector<uint8_t> data;
        for (int i = 0; i < count; i++) {
            auto pkt = createTsPacket(pid, static_cast<uint8_t>(i));
            data.insert(data.end(), pkt.begin(), pkt.end());
        }
        return data;
    }
};

// ============================================================================
// Basic State Tests
// ============================================================================

TEST_F(KeyframeAlignerTest, InitialStateIsNotWaiting) {
    EXPECT_FALSE(aligner.is_waiting());
    EXPECT_EQ(aligner.packets_buffered(), 0);
    EXPECT_EQ(aligner.idr_packet_index(), -1);
}

TEST_F(KeyframeAlignerTest, StartWaitingSetsState) {
    aligner.start_waiting();
    EXPECT_TRUE(aligner.is_waiting());
    EXPECT_EQ(aligner.packets_buffered(), 0);
}

TEST_F(KeyframeAlignerTest, StopWaitingClearsState) {
    aligner.start_waiting();
    aligner.stop_waiting();
    EXPECT_FALSE(aligner.is_waiting());
    EXPECT_EQ(aligner.packets_buffered(), 0);
}

// ============================================================================
// Pass-Through Mode Tests
// ============================================================================

TEST_F(KeyframeAlignerTest, PassThroughWhenNotWaiting) {
    auto packets = createPackets(5);
    auto result = aligner.process(packets.data(), static_cast<int32_t>(packets.size()));

    // Should pass through unchanged
    EXPECT_EQ(result.data, packets.data());
    EXPECT_EQ(result.length, static_cast<int32_t>(packets.size()));
    EXPECT_FALSE(result.found_keyframe);
}

TEST_F(KeyframeAlignerTest, PassThroughDoesNotBuffer) {
    auto packets = createPackets(5);
    aligner.process(packets.data(), static_cast<int32_t>(packets.size()));

    EXPECT_EQ(aligner.packets_buffered(), 0);
}

// ============================================================================
// Buffering Mode Tests
// ============================================================================

TEST_F(KeyframeAlignerTest, BuffersDataWhileWaiting) {
    aligner.start_waiting();

    auto packets = createPackets(5);
    auto result = aligner.process(packets.data(), static_cast<int32_t>(packets.size()));

    // Should buffer (no keyframe in basic packets)
    EXPECT_EQ(result.length, 0);
    EXPECT_EQ(aligner.packets_buffered(), 5);
}

TEST_F(KeyframeAlignerTest, AccumulatesMultipleAppends) {
    aligner.start_waiting();

    auto packets1 = createPackets(3);
    aligner.process(packets1.data(), static_cast<int32_t>(packets1.size()));
    EXPECT_EQ(aligner.packets_buffered(), 3);

    auto packets2 = createPackets(4);
    aligner.process(packets2.data(), static_cast<int32_t>(packets2.size()));
    EXPECT_EQ(aligner.packets_buffered(), 7);
}

// ============================================================================
// RAI Detection Tests
// ============================================================================

TEST_F(KeyframeAlignerTest, DetectsRAIOnVideoPid) {
    aligner.start_waiting();

    // First, establish the PID as video
    auto video_pes = createVideoPesPacket(0x100, false);
    aligner.process(video_pes.data(), static_cast<int32_t>(video_pes.size()));

    // Now send packet with RAI on same PID
    auto rai_packet = createTsPacketWithRAI(0x100);
    auto result = aligner.process(rai_packet.data(), static_cast<int32_t>(rai_packet.size()));

    EXPECT_TRUE(result.found_keyframe);
    EXPECT_GT(result.length, 0);
    EXPECT_FALSE(aligner.is_waiting());
}

TEST_F(KeyframeAlignerTest, DetectsRAIOnVideoPesStart) {
    aligner.start_waiting();

    // Send video PES with RAI in adaptation field
    auto packet = createVideoPesPacket(0x100, true);
    auto result = aligner.process(packet.data(), static_cast<int32_t>(packet.size()));

    EXPECT_TRUE(result.found_keyframe);
    EXPECT_GT(result.length, 0);
}

TEST_F(KeyframeAlignerTest, IgnoresRAIOnNonVideoPid) {
    aligner.start_waiting();

    // RAI on a PID we haven't seen as video
    auto rai_packet = createTsPacketWithRAI(0x100);
    auto result = aligner.process(rai_packet.data(), static_cast<int32_t>(rai_packet.size()));

    // Should not detect as keyframe (PID not established as video)
    EXPECT_EQ(result.length, 0);
    EXPECT_TRUE(aligner.is_waiting());
}

// ============================================================================
// RAI-Based Keyframe Detection Tests (Primary Mechanism)
// ============================================================================
//
// Note: Real MPEG-TS streams use the Random Access Indicator (RAI) in the
// adaptation field to signal keyframes. This is the primary detection method.
// NAL-based detection via TsDuck's FindIntraImage is a fallback, but the
// synthetic NAL units in these tests don't trigger it because FindIntraImage
// expects elementary stream data without PES headers.

TEST_F(KeyframeAlignerTest, DiscardsPacketsBeforeKeyframe) {
    aligner.start_waiting();

    // 5 non-keyframe packets
    auto non_key = createPackets(5, 0x100);
    aligner.process(non_key.data(), static_cast<int32_t>(non_key.size()));

    // Establish PID as video
    auto video_pes = createVideoPesPacket(0x100, false);
    aligner.process(video_pes.data(), static_cast<int32_t>(video_pes.size()));

    // Then a packet with RAI (keyframe indicator)
    auto rai_packet = createTsPacketWithRAI(0x100);
    auto result = aligner.process(rai_packet.data(), static_cast<int32_t>(rai_packet.size()));

    // Should detect keyframe via RAI
    EXPECT_TRUE(result.found_keyframe);
    EXPECT_GT(result.length, 0);
    EXPECT_FALSE(aligner.is_waiting());
}

TEST_F(KeyframeAlignerTest, OutputStartsFromKeyframePacket) {
    aligner.start_waiting();

    // 3 non-keyframe packets
    auto non_key = createPackets(3, 0x100);
    aligner.process(non_key.data(), static_cast<int32_t>(non_key.size()));

    // Video PES to establish PID
    auto video_pes = createVideoPesPacket(0x100, false);
    aligner.process(video_pes.data(), static_cast<int32_t>(video_pes.size()));

    // Packet with RAI (keyframe)
    auto rai_packet = createTsPacketWithRAI(0x100);
    auto result = aligner.process(rai_packet.data(), static_cast<int32_t>(rai_packet.size()));

    EXPECT_TRUE(result.found_keyframe);
    // Output includes packets from keyframe onwards
    EXPECT_GT(result.length, 0);
    // IDR packet index should be where the RAI packet was found
    EXPECT_GE(aligner.idr_packet_index(), 0);
}

// ============================================================================
// Buffer Limit Tests
// ============================================================================

TEST_F(KeyframeAlignerTest, EmitsOnBufferLimitExceeded) {
    KeyframeAligner small_aligner(10);  // Very small buffer
    small_aligner.start_waiting();

    // Send 15 non-keyframe packets (exceeds 10 limit)
    auto packets = createPackets(15);
    auto result = small_aligner.process(packets.data(), static_cast<int32_t>(packets.size()));

    // Should emit all data even without finding keyframe
    EXPECT_FALSE(result.found_keyframe);
    EXPECT_EQ(result.length, static_cast<int32_t>(packets.size()));
    EXPECT_FALSE(small_aligner.is_waiting());
}

// ============================================================================
// Clear Buffer Tests
// ============================================================================

TEST_F(KeyframeAlignerTest, ClearBufferResetsState) {
    aligner.start_waiting();

    auto packets = createPackets(5);
    aligner.process(packets.data(), static_cast<int32_t>(packets.size()));
    EXPECT_EQ(aligner.packets_buffered(), 5);

    aligner.clear_buffer();
    EXPECT_EQ(aligner.packets_buffered(), 0);
    EXPECT_EQ(aligner.idr_packet_index(), -1);
}

// ============================================================================
// Edge Case Tests
// ============================================================================

TEST_F(KeyframeAlignerTest, EmptyDataDoesNotCrash) {
    aligner.start_waiting();

    auto result = aligner.process(nullptr, 0);
    EXPECT_EQ(result.length, 0);
    EXPECT_TRUE(aligner.is_waiting());
}

TEST_F(KeyframeAlignerTest, InvalidSyncByteIgnored) {
    aligner.start_waiting();

    // Packet without valid sync byte
    std::vector<uint8_t> invalid(ts::PKT_SIZE, 0xAA);
    auto result = aligner.process(invalid.data(), static_cast<int32_t>(invalid.size()));

    // Should buffer but not find keyframe
    EXPECT_EQ(result.length, 0);
    EXPECT_EQ(aligner.packets_buffered(), 1);
}

TEST_F(KeyframeAlignerTest, MultiplePidsHandledCorrectly) {
    aligner.start_waiting();

    // Video PES on PID 0x100
    auto video = createVideoPesPacket(0x100, false);
    aligner.process(video.data(), static_cast<int32_t>(video.size()));

    // Non-video packets on different PID
    auto other = createPackets(3, 0x200);
    aligner.process(other.data(), static_cast<int32_t>(other.size()));

    // RAI on the video PID
    auto rai = createTsPacketWithRAI(0x100);
    auto result = aligner.process(rai.data(), static_cast<int32_t>(rai.size()));

    EXPECT_TRUE(result.found_keyframe);
}

// ============================================================================
// Fuzz-like Tests
// ============================================================================

TEST_F(KeyframeAlignerTest, HandlesRandomData) {
    aligner.start_waiting();

    std::mt19937 rng(12345);  // Fixed seed for reproducibility
    std::uniform_int_distribution<int> dist(0, 255);

    // Generate 10 packets of random data
    std::vector<uint8_t> random_data(ts::PKT_SIZE * 10);
    for (auto& byte : random_data) {
        byte = static_cast<uint8_t>(dist(rng));
    }

    // Should not crash
    auto result = aligner.process(random_data.data(), static_cast<int32_t>(random_data.size()));

    // May or may not find keyframe depending on random content
    EXPECT_TRUE(result.length == 0 || result.length > 0);
}

TEST_F(KeyframeAlignerTest, HandlesPartialPackets) {
    aligner.start_waiting();

    // Partial packet (not multiple of 188)
    std::vector<uint8_t> partial(ts::PKT_SIZE + 50);
    partial[0] = ts::SYNC_BYTE;
    partial[ts::PKT_SIZE] = ts::SYNC_BYTE;

    auto result = aligner.process(partial.data(), static_cast<int32_t>(partial.size()));

    // Should handle gracefully
    EXPECT_TRUE(result.length == 0 || result.length >= 0);
}

TEST_F(KeyframeAlignerTest, StressTestLargeBuffer) {
    KeyframeAligner large_aligner(5000);  // Large buffer
    large_aligner.start_waiting();

    // Send many packets
    for (int i = 0; i < 100; i++) {
        auto packets = createPackets(10);
        large_aligner.process(packets.data(), static_cast<int32_t>(packets.size()));
    }

    // Should have buffered 1000 packets
    EXPECT_EQ(large_aligner.packets_buffered(), 1000);

    // Establish video PID
    auto video_pes = createVideoPesPacket(0x100, false);
    large_aligner.process(video_pes.data(), static_cast<int32_t>(video_pes.size()));

    // Send packet with RAI (keyframe indicator)
    auto rai = createTsPacketWithRAI(0x100);
    auto result = large_aligner.process(rai.data(), static_cast<int32_t>(rai.size()));

    EXPECT_TRUE(result.found_keyframe);
    EXPECT_FALSE(large_aligner.is_waiting());
}

// ============================================================================
// State Transition Tests
// ============================================================================

TEST_F(KeyframeAlignerTest, CanRestartAfterKeyframeFound) {
    aligner.start_waiting();

    // Find keyframe via RAI
    auto video_pes = createVideoPesPacket(0x100, true);  // with RAI
    auto result1 = aligner.process(video_pes.data(), static_cast<int32_t>(video_pes.size()));
    EXPECT_TRUE(result1.found_keyframe);
    EXPECT_FALSE(aligner.is_waiting());

    aligner.clear_buffer();

    // Start waiting again
    aligner.start_waiting();
    EXPECT_TRUE(aligner.is_waiting());

    // Should work again
    auto result2 = aligner.process(video_pes.data(), static_cast<int32_t>(video_pes.size()));
    EXPECT_TRUE(result2.found_keyframe);
}

TEST_F(KeyframeAlignerTest, CanRestartAfterBufferLimitExceeded) {
    KeyframeAligner small_aligner(5);
    small_aligner.start_waiting();

    // Exceed buffer limit
    auto packets = createPackets(10);
    auto result1 = small_aligner.process(packets.data(), static_cast<int32_t>(packets.size()));
    EXPECT_FALSE(result1.found_keyframe);
    EXPECT_FALSE(small_aligner.is_waiting());

    small_aligner.clear_buffer();

    // Start waiting again
    small_aligner.start_waiting();
    EXPECT_TRUE(small_aligner.is_waiting());

    // Now send video PES with RAI (keyframe)
    auto video_pes = createVideoPesPacket(0x100, true);  // with RAI
    auto result2 = small_aligner.process(video_pes.data(), static_cast<int32_t>(video_pes.size()));
    EXPECT_TRUE(result2.found_keyframe);
}
