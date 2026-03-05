// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#include <gtest/gtest.h>
#include <vector>
#include <chrono>
#include <cstring>
#include <tsduck.h>
#include "analysis/nal_parser.hpp"
#include "streaming/keyframe_aligner.hpp"
#include "context/context.hpp"
#include "context/analyzer.hpp"
#include "tsduck_interop.h"

using namespace tsduck_interop;
using namespace tsduck_interop::analysis;
using namespace tsduck_interop::streaming;

// ============================================================================
// Test Fixture for SPS/PPS Injection
// ============================================================================

class SpsPpsInjectionTest : public ::testing::Test {
protected:
    ts::DuckContext duck;
    std::unique_ptr<NalParser> parser;
    KeyframeAligner aligner{500};  // Buffer for tests

    void SetUp() override {
        parser = std::make_unique<NalParser>(duck);
    }

    void TearDown() override {
        parser.reset();
    }

    // ========================================================================
    // H.264 NAL Unit Helpers
    // ========================================================================

    /// Create a minimal H.264 SPS NAL unit.
    static std::vector<uint8_t> createH264Sps() {
        // NAL header: nal_ref_idc=3, nal_unit_type=7 (SPS) -> 0x67
        // Profile: High (100), Level: 4.0, simplified encoding
        return {
            0x67,       // NAL header (SPS)
            0x64,       // profile_idc = 100 (High)
            0x00,       // constraint_set flags
            0x28,       // level_idc = 40 (4.0)
            0xAC,       // seq_parameter_set_id + other params
            0xD9, 0x40, 0x77, 0x20, 0x10, 0xB8,
            0x00, 0x00, 0x04, 0x80,  // trailing bits
        };
    }

    /// Create a minimal H.264 PPS NAL unit.
    static std::vector<uint8_t> createH264Pps() {
        // NAL header: nal_ref_idc=3, nal_unit_type=8 (PPS) -> 0x68
        return {
            0x68,       // NAL header (PPS)
            0xE8,       // pic_parameter_set_id + seq_parameter_set_id
            0x43, 0xC8, 0x80,  // flags + trailing bits
        };
    }

    /// Create an H.264 IDR slice NAL unit header.
    static std::vector<uint8_t> createH264Idr() {
        // NAL header: nal_ref_idc=3, nal_unit_type=5 (IDR) -> 0x65
        return {
            0x65,       // NAL header (IDR slice)
            0x88, 0x84, 0x00,  // slice header
        };
    }

    /// Create an H.264 P-slice NAL unit header.
    static std::vector<uint8_t> createH264PSlice() {
        // NAL header: nal_ref_idc=2, nal_unit_type=1 (non-IDR) -> 0x41
        return {
            0x41,       // NAL header (non-IDR slice)
            0x9A, 0x80, 0x00,  // slice header
        };
    }

    /// Create a TS packet with video PES containing NAL data.
    static void createVideoPesPacket(
        uint8_t* packet, uint16_t pid, uint8_t cc,
        const uint8_t* nal_data, size_t nal_len,
        bool with_rai = false
    ) {
        std::memset(packet, 0xFF, ts::PKT_SIZE);

        // TS header
        packet[0] = 0x47;
        packet[1] = static_cast<uint8_t>(0x40 | ((pid >> 8) & 0x1F));  // PUSI=1
        packet[2] = static_cast<uint8_t>(pid & 0xFF);

        size_t payload_offset = 4;
        if (with_rai) {
            // Adaptation field with RAI
            packet[3] = static_cast<uint8_t>(0x30 | (cc & 0x0F));  // AF + payload
            packet[4] = 1;     // Adaptation field length
            packet[5] = 0x40;  // RAI set
            payload_offset = 6;
        } else {
            packet[3] = static_cast<uint8_t>(0x10 | (cc & 0x0F));  // Payload only
        }

        // PES header
        packet[payload_offset + 0] = 0x00;
        packet[payload_offset + 1] = 0x00;
        packet[payload_offset + 2] = 0x01;
        packet[payload_offset + 3] = 0xE0;  // Video stream_id
        packet[payload_offset + 4] = 0x00;  // Length (unbounded)
        packet[payload_offset + 5] = 0x00;
        packet[payload_offset + 6] = 0x80;  // Marker
        packet[payload_offset + 7] = 0x80;  // PTS flag
        packet[payload_offset + 8] = 5;     // Header data length

        // PTS (dummy)
        packet[payload_offset + 9] = 0x21;
        packet[payload_offset + 10] = 0x00;
        packet[payload_offset + 11] = 0x01;
        packet[payload_offset + 12] = 0x00;
        packet[payload_offset + 13] = 0x01;

        // NAL start code + data
        size_t nal_offset = payload_offset + 14;
        packet[nal_offset++] = 0x00;
        packet[nal_offset++] = 0x00;
        packet[nal_offset++] = 0x01;

        size_t copy_len = std::min(nal_len, ts::PKT_SIZE - nal_offset);
        if (nal_data && copy_len > 0) {
            std::memcpy(packet + nal_offset, nal_data, copy_len);
        }
    }

    /// Create TS stream with SPS, PPS, P-frames, then IDR.
    std::vector<uint8_t> createStreamWithParameterSets(
        uint16_t pid, int p_frames_before_idr
    ) {
        std::vector<uint8_t> stream;
        stream.reserve((p_frames_before_idr + 4) * ts::PKT_SIZE);

        uint8_t cc = 0;
        uint8_t packet[ts::PKT_SIZE];

        // SPS packet
        auto sps = createH264Sps();
        createVideoPesPacket(packet, pid, cc++, sps.data(), sps.size());
        stream.insert(stream.end(), packet, packet + ts::PKT_SIZE);

        // PPS packet
        auto pps = createH264Pps();
        createVideoPesPacket(packet, pid, cc++, pps.data(), pps.size());
        stream.insert(stream.end(), packet, packet + ts::PKT_SIZE);

        // P-frames (non-IDR)
        auto pslice = createH264PSlice();
        for (int i = 0; i < p_frames_before_idr; ++i) {
            createVideoPesPacket(packet, pid, cc++, pslice.data(), pslice.size());
            stream.insert(stream.end(), packet, packet + ts::PKT_SIZE);
        }

        // IDR packet with RAI
        auto idr = createH264Idr();
        createVideoPesPacket(packet, pid, cc++, idr.data(), idr.size(), true);
        stream.insert(stream.end(), packet, packet + ts::PKT_SIZE);

        // More P-frames after IDR
        for (int i = 0; i < 5; ++i) {
            createVideoPesPacket(packet, pid, cc++, pslice.data(), pslice.size());
            stream.insert(stream.end(), packet, packet + ts::PKT_SIZE);
        }

        return stream;
    }
};

// ============================================================================
// Keyframe Aligner Pre-IDR Data Tests
// ============================================================================

TEST_F(SpsPpsInjectionTest, ProcessResult_IncludesPreIdrData) {
    constexpr uint16_t VIDEO_PID = 0x101;

    // Create stream with SPS, PPS, P-frames, then IDR
    auto stream = createStreamWithParameterSets(VIDEO_PID, 5);

    aligner.start_waiting();

    // Feed stream to aligner
    auto result = aligner.process(stream.data(), static_cast<int32_t>(stream.size()));

    // Should find the keyframe
    ASSERT_TRUE(result.found_keyframe);
    ASSERT_GT(result.length, 0);

    // Should have pre-IDR data (SPS, PPS, P-frames before the IDR)
    ASSERT_NE(result.pre_idr_data, nullptr);
    ASSERT_GT(result.pre_idr_length, 0);

    // Pre-IDR data should contain SPS and PPS packets
    int pre_idr_packets = result.pre_idr_length / static_cast<int32_t>(ts::PKT_SIZE);
    EXPECT_GE(pre_idr_packets, 2);  // At least SPS + PPS
}

TEST_F(SpsPpsInjectionTest, PreIdrData_ContainsSpsAndPps) {
    constexpr uint16_t VIDEO_PID = 0x101;

    // Create stream with SPS, PPS, then immediately IDR
    std::vector<uint8_t> stream;
    uint8_t cc = 0;
    uint8_t packet[ts::PKT_SIZE];

    // SPS packet
    auto sps = createH264Sps();
    createVideoPesPacket(packet, VIDEO_PID, cc++, sps.data(), sps.size());
    stream.insert(stream.end(), packet, packet + ts::PKT_SIZE);

    // PPS packet
    auto pps = createH264Pps();
    createVideoPesPacket(packet, VIDEO_PID, cc++, pps.data(), pps.size());
    stream.insert(stream.end(), packet, packet + ts::PKT_SIZE);

    // IDR packet with RAI
    auto idr = createH264Idr();
    createVideoPesPacket(packet, VIDEO_PID, cc++, idr.data(), idr.size(), true);
    stream.insert(stream.end(), packet, packet + ts::PKT_SIZE);

    aligner.start_waiting();
    auto result = aligner.process(stream.data(), static_cast<int32_t>(stream.size()));

    ASSERT_TRUE(result.found_keyframe);
    ASSERT_NE(result.pre_idr_data, nullptr);

    // Pre-IDR data should be exactly SPS + PPS (2 packets)
    int pre_idr_packets = result.pre_idr_length / static_cast<int32_t>(ts::PKT_SIZE);
    EXPECT_EQ(pre_idr_packets, 2);

    // Verify we can parse NAL units from pre-IDR data
    parser->add_video_pid(VIDEO_PID, 0x1B);  // H.264

    // Process each pre-IDR packet
    for (int i = 0; i < pre_idr_packets; ++i) {
        const uint8_t* pkt_data = result.pre_idr_data + (i * ts::PKT_SIZE);
        ts::TSPacket ts_pkt;
        std::memcpy(ts_pkt.b, pkt_data, ts::PKT_SIZE);

        if (ts_pkt.startPES()) {
            parser->process_pes_start(ts_pkt, VIDEO_PID, i);
        }
    }

    // Verify SPS and PPS were extracted
    NalParameterSetsNative params;
    ASSERT_TRUE(parser->get_parameter_sets(VIDEO_PID, &params));
    EXPECT_GT(params.sps_length, 0);
    EXPECT_GT(params.pps_length, 0);
}

TEST_F(SpsPpsInjectionTest, NalParameterSets_CanInitializeDecoder) {
    constexpr uint16_t VIDEO_PID = 0x101;
    parser->add_video_pid(VIDEO_PID, 0x1B);  // H.264

    // Process SPS
    auto sps = createH264Sps();
    uint8_t packet[ts::PKT_SIZE];
    createVideoPesPacket(packet, VIDEO_PID, 0, sps.data(), sps.size());

    ts::TSPacket ts_pkt;
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, VIDEO_PID, 0);

    // Not yet complete (missing PPS)
    NalParameterSetsNative params;
    parser->get_parameter_sets(VIDEO_PID, &params);
    EXPECT_EQ(params.parameters_complete, 0);

    // Process PPS
    auto pps = createH264Pps();
    createVideoPesPacket(packet, VIDEO_PID, 1, pps.data(), pps.size());
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, VIDEO_PID, 1);

    // Now complete
    parser->get_parameter_sets(VIDEO_PID, &params);
    EXPECT_EQ(params.parameters_complete, 1);
}

TEST_F(SpsPpsInjectionTest, IdrPacketIndex_AfterParameterSets) {
    constexpr uint16_t VIDEO_PID = 0x101;

    // Create stream: SPS, PPS, P, P, IDR
    auto stream = createStreamWithParameterSets(VIDEO_PID, 2);

    aligner.start_waiting();
    auto result = aligner.process(stream.data(), static_cast<int32_t>(stream.size()));

    ASSERT_TRUE(result.found_keyframe);

    // IDR should be at packet index 4 (SPS=0, PPS=1, P=2, P=3, IDR=4)
    EXPECT_EQ(aligner.idr_packet_index(), 4);

    // Pre-IDR should be 4 packets * 188 bytes
    EXPECT_EQ(result.pre_idr_length, 4 * static_cast<int32_t>(ts::PKT_SIZE));
}

TEST_F(SpsPpsInjectionTest, OutputStartsFromIdr) {
    constexpr uint16_t VIDEO_PID = 0x101;

    auto stream = createStreamWithParameterSets(VIDEO_PID, 3);

    aligner.start_waiting();
    auto result = aligner.process(stream.data(), static_cast<int32_t>(stream.size()));

    ASSERT_TRUE(result.found_keyframe);
    ASSERT_GT(result.length, 0);

    // Output data should start from the IDR packet
    // First byte should be sync byte
    EXPECT_EQ(result.data[0], 0x47);

    // Check that output packet has RAI set (indicating IDR)
    const ts::TSPacket* output_pkt = reinterpret_cast<const ts::TSPacket*>(result.data);
    EXPECT_TRUE(output_pkt->getRandomAccessIndicator());
}

// ============================================================================
// NalParameterSets Structure Tests
// ============================================================================

TEST_F(SpsPpsInjectionTest, NalParameterSets_HasCorrectSizes) {
    constexpr uint16_t VIDEO_PID = 0x101;
    parser->add_video_pid(VIDEO_PID, 0x1B);

    // Create combined SPS+PPS packet (both in one PES)
    std::vector<uint8_t> combined;
    combined.push_back(0x00); combined.push_back(0x00); combined.push_back(0x01);  // Start code
    auto sps = createH264Sps();
    combined.insert(combined.end(), sps.begin(), sps.end());
    combined.push_back(0x00); combined.push_back(0x00); combined.push_back(0x01);  // Start code
    auto pps = createH264Pps();
    combined.insert(combined.end(), pps.begin(), pps.end());

    uint8_t packet[ts::PKT_SIZE];
    std::memset(packet, 0xFF, ts::PKT_SIZE);

    // Build TS packet manually with combined NAL data
    packet[0] = 0x47;
    packet[1] = 0x41;  // PUSI + PID high
    packet[2] = 0x01;  // PID low
    packet[3] = 0x10;  // Payload only

    // PES header
    packet[4] = 0x00;
    packet[5] = 0x00;
    packet[6] = 0x01;
    packet[7] = 0xE0;
    packet[8] = 0x00;
    packet[9] = 0x00;
    packet[10] = 0x80;
    packet[11] = 0x00;
    packet[12] = 0x00;

    // Copy combined NAL data
    size_t copy_len = std::min(combined.size(), ts::PKT_SIZE - 13);
    std::memcpy(packet + 13, combined.data(), copy_len);

    ts::TSPacket ts_pkt;
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, VIDEO_PID, 0);

    NalParameterSetsNative params;
    parser->get_parameter_sets(VIDEO_PID, &params);

    // SPS should match our created data
    EXPECT_EQ(params.sps_length, sps.size());
    EXPECT_EQ(std::memcmp(params.sps_data, sps.data(), sps.size()), 0);

    // PPS should match our created data
    EXPECT_EQ(params.pps_length, pps.size());
    EXPECT_EQ(std::memcmp(params.pps_data, pps.data(), pps.size()), 0);
}

TEST_F(SpsPpsInjectionTest, H264_RequiresSpsAndPps) {
    constexpr uint16_t VIDEO_PID = 0x101;
    parser->add_video_pid(VIDEO_PID, 0x1B);  // H.264

    // Only SPS - not complete
    auto sps = createH264Sps();
    uint8_t packet[ts::PKT_SIZE];
    createVideoPesPacket(packet, VIDEO_PID, 0, sps.data(), sps.size());

    ts::TSPacket ts_pkt;
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, VIDEO_PID, 0);

    NalParameterSetsNative params;
    parser->get_parameter_sets(VIDEO_PID, &params);
    EXPECT_EQ(params.parameters_complete, 0);
    EXPECT_GT(params.sps_length, 0);
    EXPECT_EQ(params.pps_length, 0);
}

// ============================================================================
// Integration with Analyzer
// ============================================================================

TEST_F(SpsPpsInjectionTest, AnalyzerTr101290_ResetAfterKeyframeAlignment) {
    // Create context and analyzer
    context::TsDuckContext ctx;
    ASSERT_TRUE(ctx.is_initialized());

    TsDuckConfigNative config{};
    config.metrics_interval_ms = 100;
    config.enable_tr101290 = 1;
    config.sample_size_bytes = ts::PKT_SIZE * 100;

    context::TsDuckAnalyzer analyzer(&ctx, &config);

    constexpr uint16_t VIDEO_PID = 0x101;

    // Create stream with intentional discontinuity
    std::vector<uint8_t> stream1;
    uint8_t cc = 0;
    uint8_t packet[ts::PKT_SIZE];

    // First stream segment (CC 0-9)
    auto pslice = createH264PSlice();
    for (int i = 0; i < 10; ++i) {
        createVideoPesPacket(packet, VIDEO_PID, cc++, pslice.data(), pslice.size());
        stream1.insert(stream1.end(), packet, packet + ts::PKT_SIZE);
    }

    // Feed first stream
    analyzer.feed(stream1.data(), static_cast<int32_t>(stream1.size()));

    // Get counters before reset
    Tr101290Priority1Native p1_before{};
    Tr101290Priority2Native p2_before{};
    analyzer.tr101290.get_counters(&p1_before, &p2_before);

    // Reset TR 101 290 (simulating keyframe alignment complete)
    analyzer.tr101290.reset();

    // Get counters after reset
    Tr101290Priority1Native p1_after{};
    Tr101290Priority2Native p2_after{};
    analyzer.tr101290.get_counters(&p1_after, &p2_after);

    // Counters should be zero after reset
    EXPECT_EQ(p1_after.sync_byte_error, 0);
    EXPECT_EQ(p1_after.sync_loss, 0);
    EXPECT_EQ(p1_after.continuity_count_error, 0);
}

// ============================================================================
// Main
// ============================================================================

int main(int argc, char **argv) {
    ::testing::InitGoogleTest(&argc, argv);
    return RUN_ALL_TESTS();
}
