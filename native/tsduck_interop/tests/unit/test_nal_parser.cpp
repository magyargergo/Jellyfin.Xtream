// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#include <gtest/gtest.h>
#include <thread>
#include <chrono>
#include <cstring>
#include <vector>
#include <tsduck.h>
#include "analysis/nal_parser.hpp"

using namespace tsduck_interop;
using namespace tsduck_interop::analysis;

// ============================================================================
// Test Fixture
// ============================================================================

class NalParserTest : public ::testing::Test {
protected:
    ts::DuckContext duck;
    std::unique_ptr<NalParser> parser;

    void SetUp() override {
        parser = std::make_unique<NalParser>(duck);
    }

    void TearDown() override {
        parser.reset();
    }

    // ========================================================================
    // Helper Methods
    // ========================================================================

    /// Create a TS packet with PES start indicator containing NAL data.
    /// @param packet Output 188-byte packet buffer
    /// @param pid The PID value (0-8191)
    /// @param cc Continuity counter (0-15)
    /// @param nal_data NAL unit data to embed (without start code)
    /// @param nal_len Length of NAL data
    /// @param use_4byte_start Use 4-byte start code (0x00 0x00 0x00 0x01) instead of 3-byte
    /// @param add_delimiter Add an AUD NAL unit after the main NAL to properly delimit it
    static void createPesPacketWithNal(uint8_t* packet, uint16_t pid, uint8_t cc,
                                       const uint8_t* nal_data, size_t nal_len,
                                       bool use_4byte_start = false,
                                       bool add_delimiter = false) {
        std::memset(packet, 0xFF, ts::PKT_SIZE);

        // TS header (4 bytes)
        packet[0] = 0x47;  // Sync byte
        packet[1] = 0x40 | ((pid >> 8) & 0x1F);  // PUSI=1, upper PID bits
        packet[2] = pid & 0xFF;  // Lower PID bits
        packet[3] = 0x10 | (cc & 0x0F);  // Adaptation=00, Payload=1, CC

        // PES header (9 bytes minimum)
        size_t offset = 4;
        packet[offset++] = 0x00;  // Start code prefix
        packet[offset++] = 0x00;
        packet[offset++] = 0x01;
        packet[offset++] = 0xE0;  // Video stream ID
        packet[offset++] = 0x00;  // PES packet length (0 = unbounded for video)
        packet[offset++] = 0x00;
        packet[offset++] = 0x80;  // Flags: no PTS/DTS
        packet[offset++] = 0x00;  // Flags2
        packet[offset++] = 0x00;  // PES header data length (0 bytes)

        // NAL start code
        if (use_4byte_start) {
            packet[offset++] = 0x00;
            packet[offset++] = 0x00;
            packet[offset++] = 0x00;
            packet[offset++] = 0x01;
        } else {
            packet[offset++] = 0x00;
            packet[offset++] = 0x00;
            packet[offset++] = 0x01;
        }

        // NAL data
        size_t copy_len = std::min(nal_len, ts::PKT_SIZE - offset - 10);  // Reserve space for delimiter
        if (nal_data != nullptr && copy_len > 0) {
            std::memcpy(packet + offset, nal_data, copy_len);
            offset += copy_len;
        }

        // Add delimiter NAL (AUD) to properly terminate the previous NAL
        // This ensures find_next_start_code() returns the correct NAL size
        if (add_delimiter && offset + 5 < ts::PKT_SIZE) {
            packet[offset++] = 0x00;
            packet[offset++] = 0x00;
            packet[offset++] = 0x01;
            packet[offset++] = 0x09;  // H.264 AUD NAL type
            packet[offset++] = 0xF0;  // AUD data: primary_pic_type=7
        }
    }

    /// Create a minimal H.264 SPS NAL unit.
    /// @param profile Profile IDC (e.g., 66=Baseline, 77=Main, 100=High)
    /// @param level Level IDC (e.g., 31=3.1, 40=4.0, 51=5.1)
    /// @param width Picture width in pixels (multiple of 16)
    /// @param height Picture height in pixels (multiple of 16)
    /// @return NAL unit bytes (including NAL header, without start code)
    static std::vector<uint8_t> createH264Sps(uint8_t profile, uint8_t level,
                                              uint16_t width, uint16_t height) {
        // This creates a simplified but parseable H.264 SPS
        // Real SPS is more complex with exp-golomb encoded fields
        std::vector<uint8_t> sps;

        // NAL header: forbidden_zero_bit(1) + nal_ref_idc(2) + nal_unit_type(5)
        // For SPS: nal_ref_idc=3, nal_unit_type=7 -> 0x67
        sps.push_back(0x67);

        // profile_idc
        sps.push_back(profile);

        // constraint_set flags + reserved_zero_2bits + level_idc
        sps.push_back(0x00);  // constraint_set0-5_flag + reserved
        sps.push_back(level);

        // seq_parameter_set_id (ue(v) = 1 for id=0)
        // log2_max_frame_num_minus4 (ue(v) = 1 for value=0)
        // pic_order_cnt_type (ue(v) = 1 for value=0)
        // log2_max_pic_order_cnt_lsb_minus4 (ue(v) = 1 for value=0)
        // max_num_ref_frames (ue(v))
        // gaps_in_frame_num_value_allowed_flag (1 bit)
        // pic_width_in_mbs_minus1 (ue(v))
        // pic_height_in_map_units_minus1 (ue(v))
        // frame_mbs_only_flag (1 bit)
        // direct_8x8_inference_flag (1 bit)
        // frame_cropping_flag (1 bit)
        // vui_parameters_present_flag (1 bit)

        // Simplified encoding: width/16-1 and height/16-1 as ue(v)
        // For 1920x1080: width_mbs=120-1=119, height_mbs=68-1=67 (if frame_mbs_only)
        // But for simplicity, use a minimal SPS that TsDuck can parse

        uint8_t width_mbs = static_cast<uint8_t>((width / 16) - 1);
        uint8_t height_mbs = static_cast<uint8_t>((height / 16) - 1);

        // Exp-golomb encoded minimal SPS body
        // This is a simplified version - real encoding is more complex
        // For testing, we use values that TsDuck's parser can handle

        // seq_parameter_set_id=0 (1 bit: 1)
        // log2_max_frame_num_minus4=0 (1 bit: 1)
        // pic_order_cnt_type=0 (1 bit: 1)
        // log2_max_pic_order_cnt_lsb_minus4=0 (1 bit: 1)
        // max_num_ref_frames=1 (3 bits: 010)
        // gaps_in_frame_num_allowed=0 (1 bit)
        // pic_width_in_mbs_minus1=width_mbs (ue)
        // pic_height_in_map_units_minus1=height_mbs (ue)
        // frame_mbs_only_flag=1 (1 bit)
        // direct_8x8_inference_flag=1 (1 bit)
        // frame_cropping_flag=0 (1 bit)
        // vui_parameters_present_flag=0 (1 bit)

        // Binary: 1 1 1 1 010 0 [width_mbs_ue] [height_mbs_ue] 1 1 0 0
        // Simplified: just enough bytes to be parseable
        sps.push_back(0xE8);  // 11101000
        sps.push_back(static_cast<uint8_t>(width_mbs + 1));  // Simplified
        sps.push_back(static_cast<uint8_t>(height_mbs + 1));
        sps.push_back(0x80);  // RBSP stop bit

        return sps;
    }

    /// Create a minimal H.264 PPS NAL unit.
    /// @return NAL unit bytes (including NAL header, without start code)
    static std::vector<uint8_t> createH264Pps() {
        std::vector<uint8_t> pps;

        // NAL header for PPS: nal_ref_idc=3, nal_unit_type=8 -> 0x68
        pps.push_back(0x68);

        // pic_parameter_set_id=0 (ue(v)=1)
        // seq_parameter_set_id=0 (ue(v)=1)
        // entropy_coding_mode_flag=0 (1 bit)
        // bottom_field_pic_order_in_frame_present_flag=0 (1 bit)
        // num_slice_groups_minus1=0 (ue(v)=1)
        // num_ref_idx_l0_default_active_minus1=0 (ue(v)=1)
        // num_ref_idx_l1_default_active_minus1=0 (ue(v)=1)
        // weighted_pred_flag=0 (1 bit)
        // weighted_bipred_idc=0 (2 bits)
        // pic_init_qp_minus26=0 (se(v)=1)
        // pic_init_qs_minus26=0 (se(v)=1)
        // chroma_qp_index_offset=0 (se(v)=1)
        // deblocking_filter_control_present_flag=0 (1 bit)
        // constrained_intra_pred_flag=0 (1 bit)
        // redundant_pic_cnt_present_flag=0 (1 bit)
        // rbsp_trailing_bits

        pps.push_back(0xE8);  // Minimal PPS body
        pps.push_back(0x43);
        pps.push_back(0xC8);

        return pps;
    }

    /// Create an H.264 IDR slice NAL unit header.
    /// @return NAL unit bytes (just header, without start code)
    static std::vector<uint8_t> createH264Idr() {
        std::vector<uint8_t> idr;
        // NAL header for IDR slice: nal_ref_idc=3, nal_unit_type=5 -> 0x65
        idr.push_back(0x65);
        // Minimal slice header
        idr.push_back(0x88);
        idr.push_back(0x84);
        return idr;
    }

    /// Create an H.264 non-IDR slice NAL unit header.
    /// @return NAL unit bytes (just header, without start code)
    static std::vector<uint8_t> createH264NonIdr() {
        std::vector<uint8_t> slice;
        // NAL header for non-IDR slice: nal_ref_idc=2, nal_unit_type=1 -> 0x41
        slice.push_back(0x41);
        slice.push_back(0x9A);
        return slice;
    }

    /// Create an H.264 AUD (Access Unit Delimiter) NAL unit.
    /// @return NAL unit bytes (including NAL header, without start code)
    static std::vector<uint8_t> createH264Aud() {
        std::vector<uint8_t> aud;
        // NAL header for AUD: nal_ref_idc=0, nal_unit_type=9 -> 0x09
        aud.push_back(0x09);
        // primary_pic_type (3 bits) + rbsp_trailing_bits
        aud.push_back(0xF0);  // primary_pic_type=7 (all types)
        return aud;
    }

    /// Create a minimal HEVC VPS NAL unit.
    /// @return NAL unit bytes (including 2-byte NAL header, without start code)
    static std::vector<uint8_t> createHevcVps() {
        std::vector<uint8_t> vps;

        // HEVC NAL header: forbidden_zero(1) + nal_type(6) + nuh_layer_id(6) + nuh_temporal_id_plus1(3)
        // VPS: nal_type=32 -> (32 << 1) = 0x40, second byte: 0x01 (layer_id=0, temporal_id=1)
        vps.push_back(0x40);
        vps.push_back(0x01);

        // Minimal VPS body
        vps.push_back(0x0C);  // vps_video_parameter_set_id(4) + base_layer_internal(1) + base_layer_available(1) + ...
        vps.push_back(0x01);
        vps.push_back(0xFF);
        vps.push_back(0xFF);
        vps.push_back(0x01);
        vps.push_back(0x60);
        vps.push_back(0x00);
        vps.push_back(0x00);
        vps.push_back(0x03);

        return vps;
    }

    /// Create a minimal HEVC SPS NAL unit.
    /// @param profile Main profile (1) or Main10 (2)
    /// @param level Level times 30 (e.g., 120=4.0, 153=5.1)
    /// @param width Picture width in pixels
    /// @param height Picture height in pixels
    /// @return NAL unit bytes (including 2-byte NAL header, without start code)
    static std::vector<uint8_t> createHevcSps(uint8_t profile, uint8_t level,
                                               uint16_t width, uint16_t height) {
        std::vector<uint8_t> sps;

        // HEVC NAL header: SPS nal_type=33 -> (33 << 1) = 0x42
        sps.push_back(0x42);
        sps.push_back(0x01);

        // Simplified SPS body - enough for TsDuck to parse dimensions
        sps.push_back(0x01);  // sps_video_parameter_set_id(4) + sps_max_sub_layers_minus1(3) + temporal_id_nesting(1)

        // Profile tier level (PTL) - simplified
        sps.push_back(0x60);  // general_profile_space(2) + general_tier_flag(1) + general_profile_idc(5)
        sps.push_back(profile);
        sps.push_back(0x00);  // Compatibility flags
        sps.push_back(0x00);
        sps.push_back(0x00);
        sps.push_back(0x00);
        sps.push_back(0x00);  // Constraint indicator flags
        sps.push_back(0x00);
        sps.push_back(0x00);
        sps.push_back(0x00);
        sps.push_back(0x00);
        sps.push_back(0x00);
        sps.push_back(level);  // general_level_idc

        // SPS body - width/height as ue(v)
        // For simplicity, encode dimensions directly (not proper exp-golomb)
        sps.push_back(static_cast<uint8_t>(width >> 8));
        sps.push_back(static_cast<uint8_t>(width & 0xFF));
        sps.push_back(static_cast<uint8_t>(height >> 8));
        sps.push_back(static_cast<uint8_t>(height & 0xFF));

        // Padding
        sps.push_back(0x00);
        sps.push_back(0x00);
        sps.push_back(0x00);
        sps.push_back(0x80);

        return sps;
    }

    /// Create a minimal HEVC PPS NAL unit.
    /// @return NAL unit bytes (including 2-byte NAL header, without start code)
    static std::vector<uint8_t> createHevcPps() {
        std::vector<uint8_t> pps;

        // HEVC NAL header: PPS nal_type=34 -> (34 << 1) = 0x44
        pps.push_back(0x44);
        pps.push_back(0x01);

        // Minimal PPS body
        pps.push_back(0xC1);  // pps_pic_parameter_set_id(ue) + pps_seq_parameter_set_id(ue)
        pps.push_back(0x72);
        pps.push_back(0xB4);
        pps.push_back(0x62);
        pps.push_back(0x40);

        return pps;
    }

    /// Create an HEVC IDR_W_RADL NAL unit header.
    /// @return NAL unit bytes (2-byte header, without start code)
    static std::vector<uint8_t> createHevcIdrWRadl() {
        std::vector<uint8_t> idr;
        // NAL header: IDR_W_RADL nal_type=19 -> (19 << 1) = 0x26
        idr.push_back(0x26);
        idr.push_back(0x01);
        // Minimal slice segment header
        idr.push_back(0x80);
        return idr;
    }

    /// Create an HEVC IDR_N_LP NAL unit header.
    /// @return NAL unit bytes (2-byte header, without start code)
    static std::vector<uint8_t> createHevcIdrNLp() {
        std::vector<uint8_t> idr;
        // NAL header: IDR_N_LP nal_type=20 -> (20 << 1) = 0x28
        idr.push_back(0x28);
        idr.push_back(0x01);
        idr.push_back(0x80);
        return idr;
    }

    /// Create an HEVC CRA (Clean Random Access) NAL unit header.
    /// @return NAL unit bytes (2-byte header, without start code)
    static std::vector<uint8_t> createHevcCra() {
        std::vector<uint8_t> cra;
        // NAL header: CRA_NUT nal_type=21 -> (21 << 1) = 0x2A
        cra.push_back(0x2A);
        cra.push_back(0x01);
        cra.push_back(0x80);
        return cra;
    }

    /// Create PES packet with multiple NAL units.
    static void createPesPacketWithMultipleNals(uint8_t* packet, uint16_t pid, uint8_t cc,
                                                const std::vector<std::vector<uint8_t>>& nals) {
        std::memset(packet, 0xFF, ts::PKT_SIZE);

        // TS header
        packet[0] = 0x47;
        packet[1] = 0x40 | ((pid >> 8) & 0x1F);
        packet[2] = pid & 0xFF;
        packet[3] = 0x10 | (cc & 0x0F);

        // PES header
        size_t offset = 4;
        packet[offset++] = 0x00;
        packet[offset++] = 0x00;
        packet[offset++] = 0x01;
        packet[offset++] = 0xE0;
        packet[offset++] = 0x00;
        packet[offset++] = 0x00;
        packet[offset++] = 0x80;
        packet[offset++] = 0x00;
        packet[offset++] = 0x00;

        // Add each NAL with start code
        for (const auto& nal : nals) {
            if (offset + 3 + nal.size() > ts::PKT_SIZE) break;

            // 3-byte start code
            packet[offset++] = 0x00;
            packet[offset++] = 0x00;
            packet[offset++] = 0x01;

            // NAL data
            std::memcpy(packet + offset, nal.data(), nal.size());
            offset += nal.size();
        }
    }
};

// ============================================================================
// Basic Functionality Tests
// ============================================================================

TEST_F(NalParserTest, InitialStateHasNoVideoPids) {
    EXPECT_EQ(parser->video_stream_count.load(), 0);
}

TEST_F(NalParserTest, InitialStateHasNoParameterSets) {
    NalParameterSetsNative params;
    EXPECT_FALSE(parser->get_parameter_sets(100, &params));
}

TEST_F(NalParserTest, InitialStateHasNoIdrFrames) {
    EXPECT_EQ(parser->total_idr_frames.load(), 0);
}

TEST_F(NalParserTest, InitialStateHasNoNalUnits) {
    EXPECT_EQ(parser->total_nal_units.load(), 0);
}

TEST_F(NalParserTest, ResetClearsParameterSets) {
    // Add a video PID and cache some data
    parser->add_video_pid(100, 0x1B);

    // Create and process SPS
    auto sps = createH264Sps(100, 40, 1920, 1080);
    uint8_t packet[ts::PKT_SIZE];
    createPesPacketWithNal(packet, 100, 0, sps.data(), sps.size());

    ts::TSPacket ts_pkt;
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 0);

    // Verify SPS was cached
    NalParameterSetsNative params;
    parser->get_parameter_sets(100, &params);
    EXPECT_GT(params.sps_length, 0);

    // Reset and verify cleared
    parser->reset();

    parser->get_parameter_sets(100, &params);
    EXPECT_EQ(params.sps_length, 0);
    EXPECT_EQ(params.pps_length, 0);
    EXPECT_EQ(params.vps_length, 0);
}

TEST_F(NalParserTest, ResetFullClearsPids) {
    parser->add_video_pid(100, 0x1B);
    parser->add_video_pid(200, 0x24);
    EXPECT_EQ(parser->video_stream_count.load(), 2);

    parser->reset_full();

    EXPECT_EQ(parser->video_stream_count.load(), 0);
}

TEST_F(NalParserTest, ResetClearsIdrFlags) {
    parser->add_video_pid(100, 0x1B);

    // Process IDR
    auto idr = createH264Idr();
    uint8_t packet[ts::PKT_SIZE];
    createPesPacketWithNal(packet, 100, 0, idr.data(), idr.size());

    ts::TSPacket ts_pkt;
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 0);

    EXPECT_TRUE(parser->check_idr_frame(100));

    parser->reset();

    EXPECT_FALSE(parser->check_idr_frame(100));
}

TEST_F(NalParserTest, ResetClearsCounters) {
    parser->add_video_pid(100, 0x1B);

    auto idr = createH264Idr();
    uint8_t packet[ts::PKT_SIZE];
    createPesPacketWithNal(packet, 100, 0, idr.data(), idr.size());

    ts::TSPacket ts_pkt;
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 0);

    EXPECT_GT(parser->total_nal_units.load(), 0);
    EXPECT_GT(parser->total_idr_frames.load(), 0);

    parser->reset();

    EXPECT_EQ(parser->total_nal_units.load(), 0);
    EXPECT_EQ(parser->total_idr_frames.load(), 0);
}

// ============================================================================
// Video PID Registration Tests
// ============================================================================

TEST_F(NalParserTest, AddVideoPidH264) {
    int32_t idx = parser->add_video_pid(100, 0x1B);

    EXPECT_EQ(idx, 0);
    EXPECT_EQ(parser->video_stream_count.load(), 1);
    EXPECT_EQ(parser->video_streams[0].video_pid, 100);
    EXPECT_EQ(parser->video_streams[0].codec_info.codec_type,
              static_cast<uint8_t>(VideoCodecType::H264_AVC));
}

TEST_F(NalParserTest, AddVideoPidH265) {
    int32_t idx = parser->add_video_pid(200, 0x24);

    EXPECT_EQ(idx, 0);
    EXPECT_EQ(parser->video_stream_count.load(), 1);
    EXPECT_EQ(parser->video_streams[0].video_pid, 200);
    EXPECT_EQ(parser->video_streams[0].codec_info.codec_type,
              static_cast<uint8_t>(VideoCodecType::H265_HEVC));
}

TEST_F(NalParserTest, AddVideoPidH266) {
    int32_t idx = parser->add_video_pid(300, 0x33);

    EXPECT_EQ(idx, 0);
    EXPECT_EQ(parser->video_stream_count.load(), 1);
    EXPECT_EQ(parser->video_streams[0].codec_info.codec_type,
              static_cast<uint8_t>(VideoCodecType::H266_VVC));
}

TEST_F(NalParserTest, AddVideoPidUnknownStreamType) {
    int32_t idx = parser->add_video_pid(100, 0xFF);

    EXPECT_EQ(idx, 0);
    EXPECT_EQ(parser->video_streams[0].codec_info.codec_type,
              static_cast<uint8_t>(VideoCodecType::Unknown));
}

TEST_F(NalParserTest, AddMultipleVideoPids) {
    parser->add_video_pid(100, 0x1B);
    parser->add_video_pid(200, 0x24);
    parser->add_video_pid(300, 0x1B);

    EXPECT_EQ(parser->video_stream_count.load(), 3);
    EXPECT_EQ(parser->video_streams[0].video_pid, 100);
    EXPECT_EQ(parser->video_streams[1].video_pid, 200);
    EXPECT_EQ(parser->video_streams[2].video_pid, 300);
}

TEST_F(NalParserTest, AddDuplicatePidReturnsSameIndex) {
    int32_t idx1 = parser->add_video_pid(100, 0x1B);
    int32_t idx2 = parser->add_video_pid(100, 0x1B);

    EXPECT_EQ(idx1, idx2);
    EXPECT_EQ(parser->video_stream_count.load(), 1);
}

TEST_F(NalParserTest, AddVideoPidRejectsNullPid) {
    int32_t idx = parser->add_video_pid(ts::PID_NULL, 0x1B);
    EXPECT_EQ(idx, -1);
}

TEST_F(NalParserTest, AddVideoPidRejectsInvalidPid) {
    int32_t idx = parser->add_video_pid(0x2000, 0x1B);  // > 8191
    EXPECT_EQ(idx, -1);
}

TEST_F(NalParserTest, AddVideoPidRespectsMaxLimit) {
    // Add maximum number of PIDs
    for (size_t i = 0; i < MAX_VIDEO_PIDS; ++i) {
        int32_t idx = parser->add_video_pid(static_cast<uint16_t>(100 + i), 0x1B);
        EXPECT_GE(idx, 0);
    }

    // Next add should fail
    int32_t idx = parser->add_video_pid(999, 0x1B);
    EXPECT_EQ(idx, -1);
}

TEST_F(NalParserTest, FindStreamIndexReturnsCorrectIndex) {
    parser->add_video_pid(100, 0x1B);
    parser->add_video_pid(200, 0x24);
    parser->add_video_pid(300, 0x1B);

    EXPECT_EQ(parser->find_stream_index(100), 0);
    EXPECT_EQ(parser->find_stream_index(200), 1);
    EXPECT_EQ(parser->find_stream_index(300), 2);
}

TEST_F(NalParserTest, FindStreamIndexReturnsNegativeForUnknown) {
    parser->add_video_pid(100, 0x1B);

    EXPECT_EQ(parser->find_stream_index(999), -1);
}

// ============================================================================
// H.264 NAL Unit Processing Tests
// ============================================================================

TEST_F(NalParserTest, H264DetectsSps) {
    parser->add_video_pid(100, 0x1B);

    auto sps = createH264Sps(100, 40, 1920, 1080);
    uint8_t packet[ts::PKT_SIZE];
    createPesPacketWithNal(packet, 100, 0, sps.data(), sps.size());

    ts::TSPacket ts_pkt;
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 0);

    NalParameterSetsNative params;
    EXPECT_TRUE(parser->get_parameter_sets(100, &params));
    EXPECT_GT(params.sps_length, 0);
    EXPECT_EQ(params.sps_data[0] & h264::NAL_TYPE_MASK, h264::NAL_SPS);
}

TEST_F(NalParserTest, H264DetectsPps) {
    parser->add_video_pid(100, 0x1B);

    auto pps = createH264Pps();
    uint8_t packet[ts::PKT_SIZE];
    // Add delimiter to properly terminate NAL (prevents size > MAX_PPS_SIZE)
    createPesPacketWithNal(packet, 100, 0, pps.data(), pps.size(), false, true);

    ts::TSPacket ts_pkt;
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 0);

    NalParameterSetsNative params;
    parser->get_parameter_sets(100, &params);
    EXPECT_GT(params.pps_length, 0);
    EXPECT_EQ(params.pps_data[0] & h264::NAL_TYPE_MASK, h264::NAL_PPS);
}

TEST_F(NalParserTest, H264DetectsIdrFrame) {
    parser->add_video_pid(100, 0x1B);

    auto idr = createH264Idr();
    uint8_t packet[ts::PKT_SIZE];
    createPesPacketWithNal(packet, 100, 0, idr.data(), idr.size());

    ts::TSPacket ts_pkt;
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 0);

    EXPECT_TRUE(parser->check_idr_frame(100));
    EXPECT_EQ(parser->total_idr_frames.load(), 1);
}

TEST_F(NalParserTest, H264NonIdrSliceDoesNotSetIdrFlag) {
    parser->add_video_pid(100, 0x1B);

    auto slice = createH264NonIdr();
    uint8_t packet[ts::PKT_SIZE];
    createPesPacketWithNal(packet, 100, 0, slice.data(), slice.size());

    ts::TSPacket ts_pkt;
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 0);

    EXPECT_FALSE(parser->check_idr_frame(100));
    EXPECT_EQ(parser->total_idr_frames.load(), 0);
}

TEST_F(NalParserTest, H264AudDoesNotAffectIdrFlag) {
    parser->add_video_pid(100, 0x1B);

    auto aud = createH264Aud();
    uint8_t packet[ts::PKT_SIZE];
    // Add delimiter to properly terminate NAL unit
    createPesPacketWithNal(packet, 100, 0, aud.data(), aud.size(), false, true);

    ts::TSPacket ts_pkt;
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 0);

    EXPECT_FALSE(parser->check_idr_frame(100));
    // AUD + delimiter AUD = 2 NAL units, neither sets IDR flag
    EXPECT_EQ(parser->total_nal_units.load(), 2);
}

// ============================================================================
// H.265 NAL Unit Processing Tests
// ============================================================================

TEST_F(NalParserTest, HevcDetectsVps) {
    parser->add_video_pid(100, 0x24);

    auto vps = createHevcVps();
    uint8_t packet[ts::PKT_SIZE];
    // Add delimiter to properly terminate NAL (prevents size > MAX_VPS_SIZE)
    createPesPacketWithNal(packet, 100, 0, vps.data(), vps.size(), false, true);

    ts::TSPacket ts_pkt;
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 0);

    NalParameterSetsNative params;
    parser->get_parameter_sets(100, &params);
    EXPECT_GT(params.vps_length, 0);

    // Verify HEVC VPS NAL type
    uint8_t nal_type = (params.vps_data[0] & hevc::NAL_TYPE_MASK) >> hevc::NAL_TYPE_SHIFT;
    EXPECT_EQ(nal_type, hevc::NAL_VPS);
}

TEST_F(NalParserTest, HevcDetectsSps) {
    parser->add_video_pid(100, 0x24);

    auto sps = createHevcSps(1, 120, 1920, 1080);
    uint8_t packet[ts::PKT_SIZE];
    createPesPacketWithNal(packet, 100, 0, sps.data(), sps.size());

    ts::TSPacket ts_pkt;
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 0);

    NalParameterSetsNative params;
    parser->get_parameter_sets(100, &params);
    EXPECT_GT(params.sps_length, 0);

    uint8_t nal_type = (params.sps_data[0] & hevc::NAL_TYPE_MASK) >> hevc::NAL_TYPE_SHIFT;
    EXPECT_EQ(nal_type, hevc::NAL_SPS);
}

TEST_F(NalParserTest, HevcDetectsPps) {
    parser->add_video_pid(100, 0x24);

    auto pps = createHevcPps();
    uint8_t packet[ts::PKT_SIZE];
    // Add delimiter to properly terminate NAL (prevents size > MAX_PPS_SIZE)
    createPesPacketWithNal(packet, 100, 0, pps.data(), pps.size(), false, true);

    ts::TSPacket ts_pkt;
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 0);

    NalParameterSetsNative params;
    parser->get_parameter_sets(100, &params);
    EXPECT_GT(params.pps_length, 0);

    uint8_t nal_type = (params.pps_data[0] & hevc::NAL_TYPE_MASK) >> hevc::NAL_TYPE_SHIFT;
    EXPECT_EQ(nal_type, hevc::NAL_PPS);
}

TEST_F(NalParserTest, HevcDetectsIdrWRadl) {
    parser->add_video_pid(100, 0x24);

    auto idr = createHevcIdrWRadl();
    uint8_t packet[ts::PKT_SIZE];
    createPesPacketWithNal(packet, 100, 0, idr.data(), idr.size());

    ts::TSPacket ts_pkt;
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 0);

    EXPECT_TRUE(parser->check_idr_frame(100));
    EXPECT_EQ(parser->total_idr_frames.load(), 1);
}

TEST_F(NalParserTest, HevcDetectsIdrNLp) {
    parser->add_video_pid(100, 0x24);

    auto idr = createHevcIdrNLp();
    uint8_t packet[ts::PKT_SIZE];
    createPesPacketWithNal(packet, 100, 0, idr.data(), idr.size());

    ts::TSPacket ts_pkt;
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 0);

    EXPECT_TRUE(parser->check_idr_frame(100));
    EXPECT_EQ(parser->total_idr_frames.load(), 1);
}

TEST_F(NalParserTest, HevcDetectsCra) {
    parser->add_video_pid(100, 0x24);

    auto cra = createHevcCra();
    uint8_t packet[ts::PKT_SIZE];
    createPesPacketWithNal(packet, 100, 0, cra.data(), cra.size());

    ts::TSPacket ts_pkt;
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 0);

    EXPECT_TRUE(parser->check_idr_frame(100));
    EXPECT_EQ(parser->total_idr_frames.load(), 1);
}

// ============================================================================
// Parameter Set Caching Tests
// ============================================================================

TEST_F(NalParserTest, GetParameterSetsReturnsCachedData) {
    parser->add_video_pid(100, 0x1B);

    auto sps = createH264Sps(100, 40, 1920, 1080);
    auto pps = createH264Pps();

    // Process SPS (with delimiter to properly terminate NAL)
    uint8_t packet[ts::PKT_SIZE];
    createPesPacketWithNal(packet, 100, 0, sps.data(), sps.size(), false, true);
    ts::TSPacket ts_pkt;
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 0);

    // Process PPS (with delimiter to properly terminate NAL)
    createPesPacketWithNal(packet, 100, 1, pps.data(), pps.size(), false, true);
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 1);

    NalParameterSetsNative params;
    EXPECT_TRUE(parser->get_parameter_sets(100, &params));

    // Verify cached data matches
    EXPECT_EQ(params.sps_length, sps.size());
    EXPECT_EQ(params.pps_length, pps.size());
    EXPECT_EQ(std::memcmp(params.sps_data, sps.data(), sps.size()), 0);
    EXPECT_EQ(std::memcmp(params.pps_data, pps.data(), pps.size()), 0);
}

TEST_F(NalParserTest, H264ParametersCompleteWithSpsPps) {
    parser->add_video_pid(100, 0x1B);

    // Initially incomplete
    NalParameterSetsNative params;
    parser->get_parameter_sets(100, &params);
    EXPECT_EQ(params.parameters_complete, 0);

    // Add SPS - still incomplete
    auto sps = createH264Sps(100, 40, 1920, 1080);
    uint8_t packet[ts::PKT_SIZE];
    createPesPacketWithNal(packet, 100, 0, sps.data(), sps.size());
    ts::TSPacket ts_pkt;
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 0);

    parser->get_parameter_sets(100, &params);
    EXPECT_EQ(params.parameters_complete, 0);

    // Add PPS - now complete (with delimiter)
    auto pps = createH264Pps();
    createPesPacketWithNal(packet, 100, 1, pps.data(), pps.size(), false, true);
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 1);

    parser->get_parameter_sets(100, &params);
    EXPECT_EQ(params.parameters_complete, 1);
}

TEST_F(NalParserTest, HevcParametersCompleteWithVpsSpsPps) {
    parser->add_video_pid(100, 0x24);

    NalParameterSetsNative params;

    // Add VPS (with delimiter)
    auto vps = createHevcVps();
    uint8_t packet[ts::PKT_SIZE];
    createPesPacketWithNal(packet, 100, 0, vps.data(), vps.size(), false, true);
    ts::TSPacket ts_pkt;
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 0);

    parser->get_parameter_sets(100, &params);
    EXPECT_EQ(params.parameters_complete, 0);

    // Add SPS
    auto sps = createHevcSps(1, 120, 1920, 1080);
    createPesPacketWithNal(packet, 100, 1, sps.data(), sps.size());
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 1);

    parser->get_parameter_sets(100, &params);
    EXPECT_EQ(params.parameters_complete, 0);

    // Add PPS - now complete (with delimiter)
    auto pps = createHevcPps();
    createPesPacketWithNal(packet, 100, 2, pps.data(), pps.size(), false, true);
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 2);

    parser->get_parameter_sets(100, &params);
    EXPECT_EQ(params.parameters_complete, 1);
}

TEST_F(NalParserTest, CanInitializeDecoderH264) {
    parser->add_video_pid(100, 0x1B);

    // Not ready without SPS+PPS
    EXPECT_FALSE(parser->video_streams[0].can_initialize_decoder());

    // Add SPS
    auto sps = createH264Sps(100, 40, 1920, 1080);
    uint8_t packet[ts::PKT_SIZE];
    createPesPacketWithNal(packet, 100, 0, sps.data(), sps.size());
    ts::TSPacket ts_pkt;
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 0);

    EXPECT_FALSE(parser->video_streams[0].can_initialize_decoder());

    // Add PPS (with delimiter)
    auto pps = createH264Pps();
    createPesPacketWithNal(packet, 100, 1, pps.data(), pps.size(), false, true);
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 1);

    EXPECT_TRUE(parser->video_streams[0].can_initialize_decoder());
}

// ============================================================================
// IDR Frame Tracking Tests
// ============================================================================

TEST_F(NalParserTest, HasIdrFrameReturnsTrueAfterIdr) {
    parser->add_video_pid(100, 0x1B);

    EXPECT_FALSE(parser->check_idr_frame(100));

    auto idr = createH264Idr();
    uint8_t packet[ts::PKT_SIZE];
    createPesPacketWithNal(packet, 100, 0, idr.data(), idr.size());
    ts::TSPacket ts_pkt;
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 0);

    EXPECT_TRUE(parser->check_idr_frame(100));
}

TEST_F(NalParserTest, HasIdrFrameClearedOnNextPes) {
    parser->add_video_pid(100, 0x1B);

    // Process IDR
    auto idr = createH264Idr();
    uint8_t packet[ts::PKT_SIZE];
    createPesPacketWithNal(packet, 100, 0, idr.data(), idr.size());
    ts::TSPacket ts_pkt;
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 0);

    EXPECT_TRUE(parser->check_idr_frame(100));

    // Process non-IDR
    auto slice = createH264NonIdr();
    createPesPacketWithNal(packet, 100, 1, slice.data(), slice.size());
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 1);

    EXPECT_FALSE(parser->check_idr_frame(100));
}

TEST_F(NalParserTest, GetIdrFrameCountIncrements) {
    parser->add_video_pid(100, 0x1B);

    auto idr = createH264Idr();
    uint8_t packet[ts::PKT_SIZE];
    ts::TSPacket ts_pkt;

    // First IDR
    createPesPacketWithNal(packet, 100, 0, idr.data(), idr.size());
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 0);
    EXPECT_EQ(parser->total_idr_frames.load(), 1);

    // Second IDR
    createPesPacketWithNal(packet, 100, 1, idr.data(), idr.size());
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 1);
    EXPECT_EQ(parser->total_idr_frames.load(), 2);

    // Third IDR
    createPesPacketWithNal(packet, 100, 2, idr.data(), idr.size());
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 2);
    EXPECT_EQ(parser->total_idr_frames.load(), 3);
}

TEST_F(NalParserTest, IdrTrackingPerPid) {
    parser->add_video_pid(100, 0x1B);
    parser->add_video_pid(200, 0x1B);

    auto idr = createH264Idr();
    uint8_t packet[ts::PKT_SIZE];
    ts::TSPacket ts_pkt;

    // IDR on PID 100
    createPesPacketWithNal(packet, 100, 0, idr.data(), idr.size());
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 0);

    EXPECT_TRUE(parser->check_idr_frame(100));
    EXPECT_FALSE(parser->check_idr_frame(200));

    // IDR on PID 200
    createPesPacketWithNal(packet, 200, 0, idr.data(), idr.size());
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 200, 0);

    EXPECT_TRUE(parser->check_idr_frame(200));
}

// ============================================================================
// NAL Start Code Detection Tests
// ============================================================================

TEST_F(NalParserTest, Detects3ByteStartCode) {
    parser->add_video_pid(100, 0x1B);

    auto idr = createH264Idr();
    uint8_t packet[ts::PKT_SIZE];
    // Add delimiter to properly terminate NAL unit
    createPesPacketWithNal(packet, 100, 0, idr.data(), idr.size(), false, true);  // 3-byte + delimiter

    ts::TSPacket ts_pkt;
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 0);

    EXPECT_TRUE(parser->check_idr_frame(100));
    // IDR + delimiter AUD = 2 NAL units
    EXPECT_EQ(parser->total_nal_units.load(), 2);
}

TEST_F(NalParserTest, Detects4ByteStartCode) {
    parser->add_video_pid(100, 0x1B);

    auto idr = createH264Idr();
    uint8_t packet[ts::PKT_SIZE];
    // Add delimiter to properly terminate NAL unit
    createPesPacketWithNal(packet, 100, 0, idr.data(), idr.size(), true, true);  // 4-byte + delimiter

    ts::TSPacket ts_pkt;
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 0);

    EXPECT_TRUE(parser->check_idr_frame(100));
    // IDR + delimiter AUD = 2 NAL units
    EXPECT_EQ(parser->total_nal_units.load(), 2);
}

TEST_F(NalParserTest, MultipleNalUnitsInSinglePes) {
    parser->add_video_pid(100, 0x1B);

    auto sps = createH264Sps(100, 40, 1920, 1080);
    auto pps = createH264Pps();
    auto idr = createH264Idr();

    std::vector<std::vector<uint8_t>> nals = {sps, pps, idr};

    uint8_t packet[ts::PKT_SIZE];
    createPesPacketWithMultipleNals(packet, 100, 0, nals);

    ts::TSPacket ts_pkt;
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 0);

    // Should have detected all 3 NAL units
    EXPECT_EQ(parser->total_nal_units.load(), 3);

    // Should have cached SPS and PPS
    NalParameterSetsNative params;
    parser->get_parameter_sets(100, &params);
    EXPECT_GT(params.sps_length, 0);
    EXPECT_GT(params.pps_length, 0);

    // Should have detected IDR
    EXPECT_TRUE(parser->check_idr_frame(100));
}

// ============================================================================
// Edge Cases
// ============================================================================

TEST_F(NalParserTest, MalformedNalDataIgnored) {
    parser->add_video_pid(100, 0x1B);

    // Create packet with garbage data (no valid NAL structure)
    uint8_t garbage[] = {0xAB, 0xCD, 0xEF, 0x12};
    uint8_t packet[ts::PKT_SIZE];
    createPesPacketWithNal(packet, 100, 0, garbage, sizeof(garbage));

    ts::TSPacket ts_pkt;
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 0);

    // Should not crash, may detect garbage as NAL but should not affect state
    EXPECT_FALSE(parser->check_idr_frame(100));
}

TEST_F(NalParserTest, EmptyPesPayloadIgnored) {
    parser->add_video_pid(100, 0x1B);

    // Create packet with minimal PES header but no NAL data
    uint8_t packet[ts::PKT_SIZE];
    std::memset(packet, 0xFF, ts::PKT_SIZE);

    packet[0] = 0x47;
    packet[1] = 0x40 | ((100 >> 8) & 0x1F);
    packet[2] = 100 & 0xFF;
    packet[3] = 0x10;

    // Minimal PES header
    packet[4] = 0x00;
    packet[5] = 0x00;
    packet[6] = 0x01;
    packet[7] = 0xE0;
    packet[8] = 0x00;
    packet[9] = 0x00;
    packet[10] = 0x80;
    packet[11] = 0x00;
    packet[12] = 0x00;
    // No NAL data after header

    ts::TSPacket ts_pkt;
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 0);

    // Should not crash
    EXPECT_EQ(parser->total_nal_units.load(), 0);
}

TEST_F(NalParserTest, TruncatedSpsHandledGracefully) {
    parser->add_video_pid(100, 0x1B);

    // Create truncated SPS (just NAL header, no body)
    uint8_t truncated_sps[] = {0x67};  // SPS header only
    uint8_t packet[ts::PKT_SIZE];
    createPesPacketWithNal(packet, 100, 0, truncated_sps, sizeof(truncated_sps));

    ts::TSPacket ts_pkt;
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 0);

    // Should cache what we got, but TsDuck parser may fail to extract resolution
    NalParameterSetsNative params;
    parser->get_parameter_sets(100, &params);
    EXPECT_GE(params.sps_length, 1);  // At least the header
}

TEST_F(NalParserTest, NonPesStartPacketIgnored) {
    parser->add_video_pid(100, 0x1B);

    // Create packet WITHOUT PES start indicator
    uint8_t packet[ts::PKT_SIZE];
    std::memset(packet, 0xFF, ts::PKT_SIZE);

    packet[0] = 0x47;
    packet[1] = 0x00 | ((100 >> 8) & 0x1F);  // PUSI=0
    packet[2] = 100 & 0xFF;
    packet[3] = 0x10;

    ts::TSPacket ts_pkt;
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 0);

    EXPECT_EQ(parser->total_nal_units.load(), 0);
}

TEST_F(NalParserTest, UnregisteredPidIgnored) {
    // Don't register PID 100
    auto idr = createH264Idr();
    uint8_t packet[ts::PKT_SIZE];
    createPesPacketWithNal(packet, 100, 0, idr.data(), idr.size());

    ts::TSPacket ts_pkt;
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 0);

    EXPECT_EQ(parser->total_nal_units.load(), 0);
    EXPECT_FALSE(parser->check_idr_frame(100));
}

TEST_F(NalParserTest, OversizedSpsRejected) {
    parser->add_video_pid(100, 0x1B);

    // Create SPS larger than MAX_SPS_SIZE
    std::vector<uint8_t> large_sps(MAX_SPS_SIZE + 100, 0x00);
    large_sps[0] = 0x67;  // SPS NAL header

    uint8_t packet[ts::PKT_SIZE];
    // Can only fit part of it in one packet
    createPesPacketWithNal(packet, 100, 0, large_sps.data(),
                           std::min(large_sps.size(), static_cast<size_t>(150)));

    ts::TSPacket ts_pkt;
    std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
    parser->process_pes_start(ts_pkt, 100, 0);

    // Should not cache oversized SPS
    NalParameterSetsNative params;
    parser->get_parameter_sets(100, &params);
    EXPECT_LE(params.sps_length, MAX_SPS_SIZE);
}

TEST_F(NalParserTest, GetParameterSetsHandlesNullOutput) {
    parser->add_video_pid(100, 0x1B);
    EXPECT_FALSE(parser->get_parameter_sets(100, nullptr));
}

TEST_F(NalParserTest, GetVideoCodecInfoHandlesNullOutput) {
    parser->add_video_pid(100, 0x1B);
    EXPECT_FALSE(parser->get_video_codec_info(100, nullptr));
}

TEST_F(NalParserTest, CheckIdrFrameHandlesInvalidPid) {
    EXPECT_FALSE(parser->check_idr_frame(ts::PID_NULL));
    EXPECT_FALSE(parser->check_idr_frame(0x2000));
}

// ============================================================================
// Video Codec Info Tests
// ============================================================================

TEST_F(NalParserTest, GetVideoCodecInfoReturnsCodecType) {
    parser->add_video_pid(100, 0x1B);

    VideoCodecInfoNative info;
    EXPECT_TRUE(parser->get_video_codec_info(100, &info));
    EXPECT_EQ(info.codec_type, static_cast<uint8_t>(VideoCodecType::H264_AVC));
}

TEST_F(NalParserTest, GetVideoCodecInfoReturnsUnknownForUnregisteredPid) {
    VideoCodecInfoNative info;
    EXPECT_FALSE(parser->get_video_codec_info(100, &info));
}

// ============================================================================
// Concurrent Access Tests
// ============================================================================

TEST_F(NalParserTest, ConcurrentReadWrite) {
    parser->add_video_pid(100, 0x1B);

    std::atomic<bool> stop{false};
    std::atomic<int> successful_reads{0};

    auto sps = createH264Sps(100, 40, 1920, 1080);
    auto pps = createH264Pps();
    auto idr = createH264Idr();

    // Writer thread
    std::thread writer([&]() {
        uint8_t packet[ts::PKT_SIZE];
        ts::TSPacket ts_pkt;
        uint8_t cc = 0;

        for (int i = 0; i < 10000 && !stop.load(); ++i) {
            if (i % 3 == 0) {
                createPesPacketWithNal(packet, 100, cc++, sps.data(), sps.size());
            } else if (i % 3 == 1) {
                createPesPacketWithNal(packet, 100, cc++, pps.data(), pps.size());
            } else {
                createPesPacketWithNal(packet, 100, cc++, idr.data(), idr.size());
            }
            cc &= 0x0F;

            std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
            parser->process_pes_start(ts_pkt, 100, i);

            if (i % 1000 == 0) {
                std::this_thread::yield();
            }
        }
        stop.store(true);
    });

    // Reader thread
    std::thread reader([&]() {
        NalParameterSetsNative params;
        while (!stop.load()) {
            if (parser->get_parameter_sets(100, &params)) {
                // Verify consistency: if we have SPS, length should be valid
                if (params.sps_length > 0 && params.sps_length <= MAX_SPS_SIZE) {
                    successful_reads.fetch_add(1);
                }
            }
            (void)parser->check_idr_frame(100);
            std::this_thread::yield();
        }
    });

    writer.join();
    reader.join();

    EXPECT_GT(successful_reads.load(), 0);
}

TEST_F(NalParserTest, MultipleReadersSingleWriter) {
    parser->add_video_pid(100, 0x1B);

    std::atomic<bool> stop{false};
    std::atomic<int> total_reads{0};

    auto sps = createH264Sps(100, 40, 1920, 1080);

    // Writer thread
    std::thread writer([&]() {
        uint8_t packet[ts::PKT_SIZE];
        ts::TSPacket ts_pkt;

        for (int i = 0; i < 5000 && !stop.load(); ++i) {
            createPesPacketWithNal(packet, 100, i & 0x0F, sps.data(), sps.size());
            std::memcpy(ts_pkt.b, packet, ts::PKT_SIZE);
            parser->process_pes_start(ts_pkt, 100, i);
        }
        stop.store(true);
    });

    // Multiple reader threads
    std::vector<std::thread> readers;
    for (int r = 0; r < 4; ++r) {
        readers.emplace_back([&]() {
            NalParameterSetsNative params;
            while (!stop.load()) {
                if (parser->get_parameter_sets(100, &params)) {
                    total_reads.fetch_add(1);
                }
            }
        });
    }

    writer.join();
    for (auto& r : readers) {
        r.join();
    }

    EXPECT_GT(total_reads.load(), 0);
}

// ============================================================================
// Main
// ============================================================================

int main(int argc, char **argv) {
    ::testing::InitGoogleTest(&argc, argv);
    return RUN_ALL_TESTS();
}
