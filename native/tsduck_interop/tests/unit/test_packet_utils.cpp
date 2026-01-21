// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#include <gtest/gtest.h>
#include <cstring>
#include "mpegts/packet_utils.hpp"

using namespace tsduck_interop::mpegts;

class PacketUtilsTest : public ::testing::Test {
protected:
    uint8_t pts_data[5];
    uint8_t pcr_data[6];
    uint8_t packet[188];

    void SetUp() override {
        std::memset(pts_data, 0, sizeof(pts_data));
        std::memset(pcr_data, 0, sizeof(pcr_data));
        std::memset(packet, 0, sizeof(packet));
    }
};

// ============================================================================
// PTS Extraction Tests
// ============================================================================

TEST_F(PacketUtilsTest, ExtractPtsZero) {
    // PTS = 0: all marker bits set, value bits zero
    // Format: '00xx' (4 bits) + PTS[32..30] (3 bits) + marker (1 bit)
    pts_data[0] = 0x21;  // 0010 000 1 - prefix '0010', PTS[32:30]=0, marker=1
    pts_data[1] = 0x00;  // PTS[29:22] = 0
    pts_data[2] = 0x01;  // PTS[21:15] = 0, marker=1
    pts_data[3] = 0x00;  // PTS[14:7] = 0
    pts_data[4] = 0x01;  // PTS[6:0] = 0, marker=1

    int64_t pts = extractPts(pts_data);
    EXPECT_EQ(pts, 0);
}

TEST_F(PacketUtilsTest, ExtractPtsMaxValue) {
    // PTS max = 2^33 - 1 = 8589934591
    pts_data[0] = 0x2F;  // 0010 111 1 - PTS[32:30]=7
    pts_data[1] = 0xFF;  // PTS[29:22] = 255
    pts_data[2] = 0xFF;  // PTS[21:15] = 127, marker=1
    pts_data[3] = 0xFF;  // PTS[14:7] = 255
    pts_data[4] = 0xFF;  // PTS[6:0] = 127, marker=1

    int64_t pts = extractPts(pts_data);
    EXPECT_EQ(pts, tsduck_interop::PTS_33BIT_MAX);
}

TEST_F(PacketUtilsTest, ExtractPtsKnownValue) {
    // PTS = 90000 (1 second at 90kHz)
    // 90000 = 0x15F90
    // Binary (33 bits): 000 00000000 00000010 10111111 0010000
    // PTS[32:30]=0, PTS[29:22]=0, PTS[21:15]=2, PTS[14:7]=191(0xBF), PTS[6:0]=16
    pts_data[0] = 0x21;  // 0010 000 1 - prefix, PTS[32:30]=0, marker=1
    pts_data[1] = 0x00;  // PTS[29:22] = 0
    pts_data[2] = 0x05;  // 0000010 1 - PTS[21:15]=2, marker=1
    pts_data[3] = 0xBF;  // PTS[14:7] = 191
    pts_data[4] = 0x21;  // 0010000 1 - PTS[6:0]=16, marker=1

    int64_t pts = extractPts(pts_data);
    EXPECT_EQ(pts, 90000);
}

// ============================================================================
// PCR Extraction Tests
// ============================================================================

TEST_F(PacketUtilsTest, ExtractPcrBaseZero) {
    std::memset(pcr_data, 0, 6);
    pcr_data[4] = 0x7E;  // Reserved bits set, extension bit clear

    int64_t pcr_base = extractPcrBase(pcr_data);
    EXPECT_EQ(pcr_base, 0);
}

TEST_F(PacketUtilsTest, ExtractPcrBaseMaxValue) {
    pcr_data[0] = 0xFF;
    pcr_data[1] = 0xFF;
    pcr_data[2] = 0xFF;
    pcr_data[3] = 0xFF;
    pcr_data[4] = 0xFE;  // PCR base[0] = 1, reserved = 0x3F

    int64_t pcr_base = extractPcrBase(pcr_data);
    EXPECT_EQ(pcr_base, tsduck_interop::PTS_33BIT_MAX);
}

TEST_F(PacketUtilsTest, ExtractPcrExtension) {
    pcr_data[4] = 0x01;  // Extension bit 8
    pcr_data[5] = 0x2C;  // Extension bits 7-0 = 44

    int32_t ext = extractPcrExtension(pcr_data);
    EXPECT_EQ(ext, 300);  // (1 << 8) + 44 = 300
}

// ============================================================================
// PTS Patching Tests
// ============================================================================

TEST_F(PacketUtilsTest, PatchPtsPositiveOffset) {
    // Start with PTS = 1000
    // 1000 = 7*128 + 104 -> PTS[14:7]=7, PTS[6:0]=104
    int64_t original = 1000;
    pts_data[0] = 0x21;  // PTS[32:30]=0, marker=1
    pts_data[1] = 0x00;  // PTS[29:22]=0
    pts_data[2] = 0x01;  // PTS[21:15]=0, marker=1
    pts_data[3] = 0x07;  // PTS[14:7]=7
    pts_data[4] = 0xD1;  // PTS[6:0]=104, marker=1

    // Verify extraction
    EXPECT_EQ(extractPts(pts_data), original);

    // Apply offset of +500
    patchPts(pts_data, 500);
    EXPECT_EQ(extractPts(pts_data), 1500);
}

TEST_F(PacketUtilsTest, PatchPtsNegativeOffset) {
    // Start with PTS = 1000
    pts_data[0] = 0x21;
    pts_data[1] = 0x00;
    pts_data[2] = 0x01;
    pts_data[3] = 0x07;
    pts_data[4] = 0xD1;

    // Apply offset of -500
    patchPts(pts_data, -500);
    EXPECT_EQ(extractPts(pts_data), 500);
}

TEST_F(PacketUtilsTest, PatchPtsWraparound) {
    // Start with PTS near max (PTS_33BIT_MAX - 100)
    int64_t near_max = tsduck_interop::PTS_33BIT_MAX - 100;

    // Construct the PTS bytes for near_max
    uint8_t temp[5];
    temp[0] = 0x20 | ((near_max >> 29) & 0x0E) | 0x01;
    temp[1] = (near_max >> 22) & 0xFF;
    temp[2] = ((near_max >> 14) & 0xFE) | 0x01;
    temp[3] = (near_max >> 7) & 0xFF;
    temp[4] = ((near_max << 1) & 0xFE) | 0x01;

    std::memcpy(pts_data, temp, 5);

    // Apply offset of +200 (should wrap around)
    patchPts(pts_data, 200);
    int64_t result = extractPts(pts_data);

    // Expected: (PTS_33BIT_MAX - 100 + 200) & PTS_33BIT_MAX = 99
    EXPECT_EQ(result, 99);
}

// ============================================================================
// PCR Patching Tests
// ============================================================================

TEST_F(PacketUtilsTest, PatchPcrPositiveOffset) {
    // PCR base = 1000
    // 1000 >> 9 = 1, (1000 >> 1) & 0xFF = 244, 1000 & 1 = 0
    pcr_data[0] = 0x00;  // bits[32:25] = 0
    pcr_data[1] = 0x00;  // bits[24:17] = 0
    pcr_data[2] = 0x01;  // bits[16:9] = 1
    pcr_data[3] = 0xF4;  // bits[8:1] = 244
    pcr_data[4] = 0x7E;  // bit[0]=0, reserved bits
    pcr_data[5] = 0x00;  // extension

    int64_t original = extractPcrBase(pcr_data);
    EXPECT_EQ(original, 1000);

    patchPcr(pcr_data, 500);
    EXPECT_EQ(extractPcrBase(pcr_data), 1500);
}

// ============================================================================
// PTS Difference Tests
// ============================================================================

TEST_F(PacketUtilsTest, PtsDiffSimple) {
    EXPECT_EQ(ptsDiff(1000, 500), 500);
    EXPECT_EQ(ptsDiff(500, 1000), -500);
}

TEST_F(PacketUtilsTest, PtsDiffWraparound) {
    // When pts_a is near 0 and pts_b is near max, the difference should be small positive
    int64_t pts_a = 100;
    int64_t pts_b = tsduck_interop::PTS_33BIT_MAX - 100;

    int64_t diff = ptsDiff(pts_a, pts_b);
    EXPECT_EQ(diff, 201);  // Forward wrap
}

TEST_F(PacketUtilsTest, PtsDiffWraparoundBackward) {
    // When pts_a is near max and pts_b is near 0, the difference should be small negative
    int64_t pts_a = tsduck_interop::PTS_33BIT_MAX - 100;
    int64_t pts_b = 100;

    int64_t diff = ptsDiff(pts_a, pts_b);
    EXPECT_EQ(diff, -201);  // Backward wrap
}

// ============================================================================
// Packet Header Parsing Tests
// ============================================================================

TEST_F(PacketUtilsTest, ParseHeaderSyncByte) {
    packet[0] = 0x47;  // Sync byte
    packet[1] = 0x00;
    packet[2] = 0x10;  // PID = 16
    packet[3] = 0x10;  // Adaptation = 01 (payload only), CC = 0

    PacketHeader h = parseHeader(packet);
    EXPECT_EQ(h.pid, 16);
    EXPECT_FALSE(h.transport_error);
    EXPECT_FALSE(h.payload_unit_start);
    EXPECT_FALSE(h.transport_priority);
    EXPECT_FALSE(h.has_adaptation_field);
    EXPECT_TRUE(h.has_payload);
    EXPECT_FALSE(h.is_scrambled);
    EXPECT_EQ(h.continuity_counter, 0);
}

TEST_F(PacketUtilsTest, ParseHeaderWithFlags) {
    packet[0] = 0x47;
    packet[1] = 0xE0;  // TEI=1, PUSI=1, Priority=1, PID high=0
    packet[2] = 0x64;  // PID low = 100
    packet[3] = 0xB5;  // Scrambled=10, Adaptation=11, CC=5

    PacketHeader h = parseHeader(packet);
    EXPECT_EQ(h.pid, 100);
    EXPECT_TRUE(h.transport_error);
    EXPECT_TRUE(h.payload_unit_start);
    EXPECT_TRUE(h.transport_priority);
    EXPECT_TRUE(h.has_adaptation_field);
    EXPECT_TRUE(h.has_payload);
    EXPECT_TRUE(h.is_scrambled);
    EXPECT_EQ(h.continuity_counter, 5);
}

TEST_F(PacketUtilsTest, ParseHeaderNullPacket) {
    packet[0] = 0x47;
    packet[1] = 0x1F;  // PID high = 0x1F
    packet[2] = 0xFF;  // PID low = 0xFF (null packet PID = 0x1FFF)
    packet[3] = 0x10;

    PacketHeader h = parseHeader(packet);
    EXPECT_EQ(h.pid, 0x1FFF);
}

// ============================================================================
// Stream Type Detection Tests
// ============================================================================

TEST_F(PacketUtilsTest, IsVideoPid) {
    EXPECT_TRUE(isVideoPid(0x01));   // MPEG-1 Video
    EXPECT_TRUE(isVideoPid(0x02));   // MPEG-2 Video
    EXPECT_TRUE(isVideoPid(0x10));   // MPEG-4 Video
    EXPECT_TRUE(isVideoPid(0x1B));   // H.264/AVC
    EXPECT_TRUE(isVideoPid(0x24));   // H.265/HEVC

    EXPECT_FALSE(isVideoPid(0x03));  // MPEG-1 Audio
    EXPECT_FALSE(isVideoPid(0x0F));  // AAC
    EXPECT_FALSE(isVideoPid(0x00));  // Reserved
}

TEST_F(PacketUtilsTest, IsAudioPid) {
    EXPECT_TRUE(isAudioPid(0x03));   // MPEG-1 Audio
    EXPECT_TRUE(isAudioPid(0x04));   // MPEG-2 Audio
    EXPECT_TRUE(isAudioPid(0x0F));   // AAC
    EXPECT_TRUE(isAudioPid(0x11));   // AAC LATM
    EXPECT_TRUE(isAudioPid(0x81));   // AC-3
    EXPECT_TRUE(isAudioPid(0x87));   // E-AC-3

    EXPECT_FALSE(isAudioPid(0x02));  // MPEG-2 Video
    EXPECT_FALSE(isAudioPid(0x1B));  // H.264
    EXPECT_FALSE(isAudioPid(0x00));  // Reserved
}

int main(int argc, char **argv) {
    ::testing::InitGoogleTest(&argc, argv);
    return RUN_ALL_TESTS();
}
