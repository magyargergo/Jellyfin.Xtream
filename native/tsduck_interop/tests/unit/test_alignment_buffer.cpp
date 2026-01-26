// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#include <gtest/gtest.h>
#include "streaming/alignment_buffer.hpp"
#include "core/constants.hpp"
#include <vector>
#include <cstring>

using namespace tsduck_interop;
using namespace tsduck_interop::streaming;

class AlignmentBufferTest : public ::testing::Test {
protected:
    AlignmentBuffer buffer{32};

    // Create a valid TS packet with sync byte
    std::vector<uint8_t> createTsPacket(uint8_t fill = 0xFF) {
        std::vector<uint8_t> packet(ts::PKT_SIZE, fill);
        packet[0] = ts::SYNC_BYTE;
        return packet;
    }

    // Create multiple TS packets
    std::vector<uint8_t> createTsPackets(int count, uint8_t fill = 0xFF) {
        std::vector<uint8_t> data;
        for (int i = 0; i < count; i++) {
            auto pkt = createTsPacket(static_cast<uint8_t>(fill + i));
            pkt[0] = ts::SYNC_BYTE;  // Ensure sync byte
            data.insert(data.end(), pkt.begin(), pkt.end());
        }
        return data;
    }
};

TEST_F(AlignmentBufferTest, InitialState) {
    EXPECT_EQ(buffer.pending_bytes(), 0);
    EXPECT_FALSE(buffer.is_synced());
}

TEST_F(AlignmentBufferTest, EmptyAppend) {
    auto chunk = buffer.append(nullptr, 0);
    EXPECT_EQ(chunk.length, 0);
    EXPECT_EQ(chunk.data, nullptr);
}

TEST_F(AlignmentBufferTest, ExactSinglePacket) {
    auto packet = createTsPacket();
    auto chunk = buffer.append(packet.data(), packet.size());

    EXPECT_EQ(chunk.length, ts::PKT_SIZE);
    EXPECT_NE(chunk.data, nullptr);
    EXPECT_EQ(chunk.data[0], ts::SYNC_BYTE);
    EXPECT_TRUE(buffer.is_synced());
    EXPECT_EQ(buffer.pending_bytes(), 0);
}

TEST_F(AlignmentBufferTest, ExactMultiplePackets) {
    auto packets = createTsPackets(5);
    auto chunk = buffer.append(packets.data(), packets.size());

    EXPECT_EQ(chunk.length, 5 * ts::PKT_SIZE);
    EXPECT_NE(chunk.data, nullptr);

    // Verify each packet starts with sync byte
    for (int i = 0; i < 5; i++) {
        EXPECT_EQ(chunk.data[i * ts::PKT_SIZE], ts::SYNC_BYTE);
    }
}

TEST_F(AlignmentBufferTest, PartialPacketAccumulation) {
    auto packet = createTsPacket();

    // Feed first half
    auto chunk1 = buffer.append(packet.data(), 94);
    EXPECT_EQ(chunk1.length, 0);
    EXPECT_EQ(buffer.pending_bytes(), 94);

    // Feed second half
    auto chunk2 = buffer.append(packet.data() + 94, 94);
    EXPECT_EQ(chunk2.length, ts::PKT_SIZE);
    EXPECT_EQ(chunk2.data[0], ts::SYNC_BYTE);
    EXPECT_EQ(buffer.pending_bytes(), 0);
}

TEST_F(AlignmentBufferTest, MultipleSplitAppends) {
    auto packets = createTsPackets(3);
    int total = static_cast<int>(packets.size());  // 564 bytes

    // Feed in 100-byte chunks
    int offset = 0;
    int aligned_total = 0;
    while (offset < total) {
        int chunk_size = std::min(100, total - offset);
        auto chunk = buffer.append(packets.data() + offset, chunk_size);
        aligned_total += chunk.length;
        offset += chunk_size;
    }

    // We should have received all 3 packets worth of aligned data
    EXPECT_EQ(aligned_total, 3 * ts::PKT_SIZE);
}

TEST_F(AlignmentBufferTest, GarbageBeforeSync) {
    // 10 bytes of garbage followed by a valid packet and another
    std::vector<uint8_t> data(10, 0xAA);  // garbage
    auto packets = createTsPackets(2);
    data.insert(data.end(), packets.begin(), packets.end());

    auto chunk = buffer.append(data.data(), data.size());

    // Should skip garbage and align to first sync byte
    EXPECT_GE(chunk.length, ts::PKT_SIZE);
    EXPECT_EQ(chunk.data[0], ts::SYNC_BYTE);
    EXPECT_TRUE(buffer.is_synced());
}

TEST_F(AlignmentBufferTest, SyncByteVerification) {
    // A false sync byte (0x47) that doesn't have another at +188
    std::vector<uint8_t> data(ts::PKT_SIZE * 3, 0x00);
    data[5] = ts::SYNC_BYTE;  // False sync (no matching byte at 5+188)
    data[ts::PKT_SIZE] = ts::SYNC_BYTE;  // Real sync
    data[ts::PKT_SIZE * 2] = ts::SYNC_BYTE;  // Confirms it

    auto chunk = buffer.append(data.data(), data.size());

    // Should find the real sync pair at offset ts::PKT_SIZE
    EXPECT_GE(chunk.length, ts::PKT_SIZE);
    if (chunk.length > 0) {
        EXPECT_EQ(chunk.data[0], ts::SYNC_BYTE);
    }
}

TEST_F(AlignmentBufferTest, Reset) {
    auto packet = createTsPacket();

    // Feed partial data
    buffer.append(packet.data(), 50);
    EXPECT_EQ(buffer.pending_bytes(), 50);

    buffer.reset();
    EXPECT_EQ(buffer.pending_bytes(), 0);
    EXPECT_FALSE(buffer.is_synced());
}

TEST_F(AlignmentBufferTest, LargeChunk) {
    // Simulate a typical curl chunk (16KB = ~85 packets)
    int num_packets = 85;
    auto packets = createTsPackets(num_packets);
    auto chunk = buffer.append(packets.data(), packets.size());

    EXPECT_EQ(chunk.length, num_packets * ts::PKT_SIZE);
}

TEST_F(AlignmentBufferTest, TrailingPartialPreserved) {
    // 2.5 packets
    auto packets = createTsPackets(3);
    int partial_size = 2 * ts::PKT_SIZE + 50;

    auto chunk = buffer.append(packets.data(), partial_size);

    // Should output 2 packets, keep 50 bytes pending
    EXPECT_EQ(chunk.length, 2 * ts::PKT_SIZE);
    EXPECT_EQ(buffer.pending_bytes(), 50);
}

TEST_F(AlignmentBufferTest, ConsecutiveAppendsAfterPartial) {
    auto packets = createTsPackets(4);

    // First: 1.5 packets
    int first_size = ts::PKT_SIZE + 94;
    auto chunk1 = buffer.append(packets.data(), first_size);
    EXPECT_EQ(chunk1.length, ts::PKT_SIZE);

    // Second: rest (completes partial + more)
    int second_offset = first_size;
    int second_size = static_cast<int>(packets.size()) - first_size;
    auto chunk2 = buffer.append(packets.data() + second_offset, second_size);

    // Should output remaining 3 packets (94 pending + rest = 3 * 188)
    EXPECT_EQ(chunk2.length, 3 * ts::PKT_SIZE);
    EXPECT_EQ(buffer.pending_bytes(), 0);
}

TEST_F(AlignmentBufferTest, ZeroSizeAppend) {
    auto chunk = buffer.append(reinterpret_cast<const uint8_t*>(""), 0);
    EXPECT_EQ(chunk.length, 0);
}

TEST_F(AlignmentBufferTest, SingleByteAppends) {
    auto packet = createTsPacket(0xAB);

    // Feed one byte at a time
    for (int i = 0; i < ts::PKT_SIZE - 1; i++) {
        auto chunk = buffer.append(&packet[i], 1);
        EXPECT_EQ(chunk.length, 0);
    }

    // Last byte should complete the packet
    auto chunk = buffer.append(&packet[ts::PKT_SIZE - 1], 1);
    EXPECT_EQ(chunk.length, ts::PKT_SIZE);
    EXPECT_EQ(chunk.data[0], ts::SYNC_BYTE);
}

// ============================================================================
// Regression test: Output data integrity with remainder
// ============================================================================
// This test verifies that the output data is NOT corrupted when there's a
// remainder that gets moved to the front of the buffer. Previously, the
// memmove would overwrite the beginning of the output data.

TEST_F(AlignmentBufferTest, OutputDataIntegrityWithRemainder) {
    // Create 3 packets with distinct content in each
    std::vector<uint8_t> packets;
    for (int pkt = 0; pkt < 3; pkt++) {
        packets.push_back(ts::SYNC_BYTE);  // offset 0: sync
        for (int i = 1; i < ts::PKT_SIZE; i++) {
            // Fill with packet number + byte position
            // This creates unique, verifiable content
            packets.push_back(static_cast<uint8_t>((pkt << 4) | (i & 0x0F)));
        }
    }

    // Feed 2.5 packets (470 bytes) - creates 94 byte remainder
    int partial_size = 2 * static_cast<int>(ts::PKT_SIZE) + 94;
    auto chunk = buffer.append(packets.data(), partial_size);

    // Should output 2 complete packets
    EXPECT_EQ(chunk.length, 2 * ts::PKT_SIZE);
    EXPECT_EQ(buffer.pending_bytes(), 94);

    // CRITICAL: Verify ALL bytes of output are correct, especially the first
    // 94 bytes which would be corrupted by the old memmove bug
    for (int pkt = 0; pkt < 2; pkt++) {
        int pkt_offset = pkt * static_cast<int>(ts::PKT_SIZE);

        // Verify sync byte
        EXPECT_EQ(chunk.data[pkt_offset], ts::SYNC_BYTE)
            << "Packet " << pkt << " sync byte corrupted";

        // Verify content bytes
        for (int i = 1; i < ts::PKT_SIZE; i++) {
            uint8_t expected = static_cast<uint8_t>((pkt << 4) | (i & 0x0F));
            EXPECT_EQ(chunk.data[pkt_offset + i], expected)
                << "Packet " << pkt << " byte " << i << " corrupted "
                << "(expected " << static_cast<int>(expected)
                << ", got " << static_cast<int>(chunk.data[pkt_offset + i]) << ")";
        }
    }
}

TEST_F(AlignmentBufferTest, MultipleAppendsWithRemainder) {
    // Test that consecutive appends don't cause accumulating corruption
    for (int iteration = 0; iteration < 10; iteration++) {
        // Create 2 packets with iteration-specific content
        std::vector<uint8_t> packets;
        for (int pkt = 0; pkt < 2; pkt++) {
            packets.push_back(ts::SYNC_BYTE);
            for (int i = 1; i < ts::PKT_SIZE; i++) {
                packets.push_back(static_cast<uint8_t>(iteration * 10 + pkt));
            }
        }

        // Feed 1.5 packets (creates remainder each time)
        int partial_size = static_cast<int>(ts::PKT_SIZE) + 94;
        auto chunk = buffer.append(packets.data(), partial_size);

        // Should get 1 packet out
        EXPECT_EQ(chunk.length, ts::PKT_SIZE);

        // Verify first packet content is not corrupted
        EXPECT_EQ(chunk.data[0], ts::SYNC_BYTE);
        for (int i = 1; i < ts::PKT_SIZE; i++) {
            uint8_t expected = static_cast<uint8_t>(iteration * 10);
            EXPECT_EQ(chunk.data[i], expected)
                << "Iteration " << iteration << " byte " << i << " corrupted";
        }

        // Feed remaining portion to complete the second packet
        buffer.append(packets.data() + partial_size, packets.size() - partial_size);
    }
}
