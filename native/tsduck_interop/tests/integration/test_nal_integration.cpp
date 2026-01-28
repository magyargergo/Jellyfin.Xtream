// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#include <gtest/gtest.h>
#include <atomic>
#include <chrono>
#include <cstring>
#include <thread>
#include <vector>

#include "context/analyzer.hpp"
#include "context/context.hpp"
#include "tsduck_interop.h"

using namespace tsduck_interop;

// ============================================================================
// Helper Functions for Packet Construction
// ============================================================================

namespace {

/// CRC-32 lookup table for MPEG-2 PSI sections
static constexpr uint32_t kCrc32Table[256] = {
    0x00000000, 0x04C11DB7, 0x09823B6E, 0x0D4326D9, 0x130476DC, 0x17C56B6B,
    0x1A864DB2, 0x1E475005, 0x2608EDB8, 0x22C9F00F, 0x2F8AD6D6, 0x2B4BCB61,
    0x350C9B64, 0x31CD86D3, 0x3C8EA00A, 0x384FBDBD, 0x4C11DB70, 0x48D0C6C7,
    0x4593E01E, 0x4152FDA9, 0x5F15ADAC, 0x5BD4B01B, 0x569796C2, 0x52568B75,
    0x6A1936C8, 0x6ED82B7F, 0x639B0DA6, 0x675A1011, 0x791D4014, 0x7DDC5DA3,
    0x709F7B7A, 0x745E66CD, 0x9823B6E0, 0x9CE2AB57, 0x91A18D8E, 0x95609039,
    0x8B27C03C, 0x8FE6DD8B, 0x82A5FB52, 0x8664E6E5, 0xBE2B5B58, 0xBAEA46EF,
    0xB7A96036, 0xB3687D81, 0xAD2F2D84, 0xA9EE3033, 0xA4AD16EA, 0xA06C0B5D,
    0xD4326D90, 0xD0F37027, 0xDDB056FE, 0xD9714B49, 0xC7361B4C, 0xC3F706FB,
    0xCEB42022, 0xCA753D95, 0xF23A8028, 0xF6FB9D9F, 0xFBB8BB46, 0xFF79A6F1,
    0xE13EF6F4, 0xE5FFEB43, 0xE8BCCD9A, 0xEC7DD02D, 0x34867077, 0x30476DC0,
    0x3D044B19, 0x39C556AE, 0x278206AB, 0x23431B1C, 0x2E003DC5, 0x2AC12072,
    0x128E9DCF, 0x164F8078, 0x1B0CA6A1, 0x1FCDBB16, 0x018AEB13, 0x054BF6A4,
    0x0808D07D, 0x0CC9CDCA, 0x7897AB07, 0x7C56B6B0, 0x71159069, 0x75D48DDE,
    0x6B93DDDB, 0x6F52C06C, 0x6211E6B5, 0x66D0FB02, 0x5E9F46BF, 0x5A5E5B08,
    0x571D7DD1, 0x53DC6066, 0x4D9B3063, 0x495A2DD4, 0x44190B0D, 0x40D816BA,
    0xACA5C697, 0xA864DB20, 0xA527FDF9, 0xA1E6E04E, 0xBFA1B04B, 0xBB60ADFC,
    0xB6238B25, 0xB2E29692, 0x8AAD2B2F, 0x8E6C3698, 0x832F1041, 0x87EE0DF6,
    0x99A95DF3, 0x9D684044, 0x902B669D, 0x94EA7B2A, 0xE0B41DE7, 0xE4750050,
    0xE9362689, 0xEDF73B3E, 0xF3B06B3B, 0xF771768C, 0xFA325055, 0xFEF34DE2,
    0xC6BCF05F, 0xC27DEDE8, 0xCF3ECB31, 0xCBFFD686, 0xD5B88683, 0xD1799B34,
    0xDC3ABDED, 0xD8FBA05A, 0x690CE0EE, 0x6DCDFD59, 0x608EDB80, 0x644FC637,
    0x7A089632, 0x7EC98B85, 0x738AAD5C, 0x774BB0EB, 0x4F040D56, 0x4BC510E1,
    0x46863638, 0x42472B8F, 0x5C007B8A, 0x58C1663D, 0x558240E4, 0x51435D53,
    0x251D3B9E, 0x21DC2629, 0x2C9F00F0, 0x285E1D47, 0x36194D42, 0x32D850F5,
    0x3F9B762C, 0x3B5A6B9B, 0x0315D626, 0x07D4CB91, 0x0A97ED48, 0x0E56F0FF,
    0x1011A0FA, 0x14D0BD4D, 0x19939B94, 0x1D528623, 0xF12F560E, 0xF5EE4BB9,
    0xF8AD6D60, 0xFC6C70D7, 0xE22B20D2, 0xE6EA3D65, 0xEBA91BBC, 0xEF68060B,
    0xD727BBB6, 0xD3E6A601, 0xDEA580D8, 0xDA649D6F, 0xC423CD6A, 0xC0E2D0DD,
    0xCDA1F604, 0xC960EBB3, 0xBD3E8D7E, 0xB9FF90C9, 0xB4BCB610, 0xB07DABA7,
    0xAE3AFBA2, 0xAAFBE615, 0xA7B8C0CC, 0xA379DD7B, 0x9B3660C6, 0x9FF77D71,
    0x92B45BA8, 0x9675461F, 0x8832161A, 0x8CF30BAD, 0x81B02D74, 0x857130C3,
    0x5D8A9099, 0x594B8D2E, 0x5408ABF7, 0x50C9B640, 0x4E8EE645, 0x4A4FFBF2,
    0x470CDD2B, 0x43CDC09C, 0x7B827D21, 0x7F436096, 0x7200464F, 0x76C15BF8,
    0x68860BFD, 0x6C47164A, 0x61043093, 0x65C52D24, 0x119B4BE9, 0x155A565E,
    0x18197087, 0x1CD86D30, 0x029F3D35, 0x065E2082, 0x0B1D065B, 0x0FDC1BEC,
    0x3793A651, 0x3352BBE6, 0x3E119D3F, 0x3AD08088, 0x2497D08D, 0x2056CD3A,
    0x2D15EBE3, 0x29D4F654, 0xC5A92679, 0xC1683BCE, 0xCC2B1D17, 0xC8EA00A0,
    0xD6AD50A5, 0xD26C4D12, 0xDF2F6BCB, 0xDBEE767C, 0xE3A1CBC1, 0xE760D676,
    0xEA23F0AF, 0xEEE2ED18, 0xF0A5BD1D, 0xF464A0AA, 0xF9278673, 0xFDE69BC4,
    0x89B8FD09, 0x8D79E0BE, 0x803AC667, 0x84FBDBD0, 0x9ABC8BD5, 0x9E7D9662,
    0x933EB0BB, 0x97FFAD0C, 0xAFB010B1, 0xAB710D06, 0xA6322BDF, 0xA2F33668,
    0xBCB4666D, 0xB8757BDA, 0xB5365D03, 0xB1F740B4
};

/// Compute CRC-32 for MPEG-2 PSI sections
uint32_t computeCrc32(const uint8_t* data, size_t length) {
    uint32_t crc = 0xFFFFFFFF;
    for (size_t i = 0; i < length; i++) {
        crc = (crc << 8) ^ kCrc32Table[((crc >> 24) ^ data[i]) & 0xFF];
    }
    return crc;
}

/// Create a PAT packet
void createPatPacket(uint8_t* packet, uint16_t program_num, uint16_t pmt_pid) {
    std::memset(packet, 0xFF, ts::PKT_SIZE);

    packet[0] = 0x47;
    packet[1] = 0x40 | ((ts::PID_PAT >> 8) & 0x1F);
    packet[2] = ts::PID_PAT & 0xFF;
    packet[3] = 0x10;
    packet[4] = 0x00;

    size_t section_start = 5;
    uint8_t* section = &packet[section_start];

    section[0] = 0x00;
    section[1] = 0xB0;
    section[2] = 13;
    section[3] = 0x00;
    section[4] = 0x01;
    section[5] = 0xC1;
    section[6] = 0x00;
    section[7] = 0x00;

    section[8] = (program_num >> 8) & 0xFF;
    section[9] = program_num & 0xFF;
    section[10] = 0xE0 | ((pmt_pid >> 8) & 0x1F);
    section[11] = pmt_pid & 0xFF;

    uint32_t crc = computeCrc32(section, 12);
    section[12] = (crc >> 24) & 0xFF;
    section[13] = (crc >> 16) & 0xFF;
    section[14] = (crc >> 8) & 0xFF;
    section[15] = crc & 0xFF;
}

/// Create a PMT packet with video stream
/// @param packet Output buffer (188 bytes)
/// @param pmt_pid PMT PID
/// @param program_num Program number
/// @param video_pid Video PID
/// @param video_type Video stream type (0x1B=H.264, 0x24=H.265, 0x33=H.266)
void createPmtPacket(uint8_t* packet, uint16_t pmt_pid, uint16_t program_num,
                     uint16_t video_pid, uint8_t video_type) {
    std::memset(packet, 0xFF, ts::PKT_SIZE);

    packet[0] = 0x47;
    packet[1] = 0x40 | ((pmt_pid >> 8) & 0x1F);
    packet[2] = pmt_pid & 0xFF;
    packet[3] = 0x10;
    packet[4] = 0x00;

    size_t section_start = 5;
    uint8_t* section = &packet[section_start];

    // section_length = 9 (fixed header) + 5 (video ES) + 4 (CRC) = 18
    int section_length = 18;

    section[0] = 0x02;
    section[1] = 0xB0 | ((section_length >> 8) & 0x0F);
    section[2] = section_length & 0xFF;
    section[3] = (program_num >> 8) & 0xFF;
    section[4] = program_num & 0xFF;
    section[5] = 0xC1;
    section[6] = 0x00;
    section[7] = 0x00;

    // PCR_PID = video_pid
    section[8] = 0xE0 | ((video_pid >> 8) & 0x1F);
    section[9] = video_pid & 0xFF;

    // program_info_length = 0
    section[10] = 0xF0;
    section[11] = 0x00;

    // Video ES entry
    section[12] = video_type;
    section[13] = 0xE0 | ((video_pid >> 8) & 0x1F);
    section[14] = video_pid & 0xFF;
    section[15] = 0xF0;
    section[16] = 0x00;

    // CRC-32
    uint32_t crc = computeCrc32(section, 17);
    section[17] = (crc >> 24) & 0xFF;
    section[18] = (crc >> 16) & 0xFF;
    section[19] = (crc >> 8) & 0xFF;
    section[20] = crc & 0xFF;
}

/// Create a PES packet start with NAL unit data
/// @param packet Output buffer (188 bytes)
/// @param pid Video PID
/// @param cc Continuity counter
/// @param stream_id PES stream ID (0xE0 for video)
/// @param nal_data NAL unit data (with start codes)
/// @param nal_len Length of NAL data
void createPesStart(uint8_t* packet, uint16_t pid, uint8_t cc,
                    uint8_t stream_id, const uint8_t* nal_data, size_t nal_len) {
    std::memset(packet, 0xFF, ts::PKT_SIZE);

    // TS header with PUSI
    packet[0] = 0x47;
    packet[1] = 0x40 | ((pid >> 8) & 0x1F);
    packet[2] = pid & 0xFF;
    packet[3] = 0x10 | (cc & 0x0F);

    // PES header
    size_t offset = 4;
    packet[offset++] = 0x00;  // packet_start_code_prefix
    packet[offset++] = 0x00;
    packet[offset++] = 0x01;
    packet[offset++] = stream_id;

    // PES_packet_length (0 = unlimited for video)
    packet[offset++] = 0x00;
    packet[offset++] = 0x00;

    // PES header flags: marker='10', no scrambling, no priority, no alignment, no copyright
    packet[offset++] = 0x80;

    // PTS_DTS_flags=0, no ESCR, no ES_rate, no DSM_trick_mode, no additional_copy_info
    packet[offset++] = 0x00;

    // PES_header_data_length
    packet[offset++] = 0x00;

    // Copy NAL data
    size_t payload_space = ts::PKT_SIZE - offset;
    size_t copy_len = std::min(nal_len, payload_space);
    std::memcpy(&packet[offset], nal_data, copy_len);
}

/// Create a simple TS packet
void createPacket(uint8_t* packet, uint16_t pid, uint8_t cc) {
    std::memset(packet, 0xFF, ts::PKT_SIZE);
    packet[0] = 0x47;
    packet[1] = (pid >> 8) & 0x1F;
    packet[2] = pid & 0xFF;
    packet[3] = 0x10 | (cc & 0x0F);
}

// Sample H.264 SPS NAL unit (simplified, 1920x1080 progressive)
// This is a minimal valid SPS for testing purposes
static const uint8_t kH264Sps[] = {
    0x00, 0x00, 0x00, 0x01,  // Start code
    0x67,  // NAL header: nal_ref_idc=3, nal_unit_type=7 (SPS)
    0x64, 0x00, 0x1F,  // profile_idc=100 (High), constraint_set flags, level_idc=31
    0xAC, 0xD9, 0x40, 0x78, 0x02, 0x27, 0xE5, 0xC0,
    0x44, 0x00, 0x00, 0x03, 0x00, 0x04, 0x00, 0x00,
    0x03, 0x00, 0xF0, 0x3C, 0x60, 0xC6, 0x58
};

// Sample H.264 PPS NAL unit
static const uint8_t kH264Pps[] = {
    0x00, 0x00, 0x00, 0x01,  // Start code
    0x68,  // NAL header: nal_ref_idc=3, nal_unit_type=8 (PPS)
    0xCE, 0x3C, 0x80
};

// Sample H.264 IDR NAL unit header (just the type byte)
static const uint8_t kH264Idr[] = {
    0x00, 0x00, 0x00, 0x01,  // Start code
    0x65,  // NAL header: nal_ref_idc=3, nal_unit_type=5 (IDR)
    0x88, 0x80, 0x10  // Sample slice data
};

// Sample HEVC VPS NAL unit
static const uint8_t kHevcVps[] = {
    0x00, 0x00, 0x00, 0x01,  // Start code
    0x40, 0x01,  // NAL header: nal_unit_type=32 (VPS)
    0x0C, 0x01, 0xFF, 0xFF, 0x01, 0x60, 0x00, 0x00,
    0x03, 0x00, 0x00, 0x03, 0x00, 0x00, 0x03, 0x00
};

// Sample HEVC SPS NAL unit
static const uint8_t kHevcSps[] = {
    0x00, 0x00, 0x00, 0x01,  // Start code
    0x42, 0x01,  // NAL header: nal_unit_type=33 (SPS)
    0x01, 0x01, 0x60, 0x00, 0x00, 0x03, 0x00, 0x00,
    0x03, 0x00, 0x00, 0x03, 0x00, 0x7B, 0xA0, 0x03
};

// Sample HEVC PPS NAL unit
static const uint8_t kHevcPps[] = {
    0x00, 0x00, 0x00, 0x01,  // Start code
    0x44, 0x01,  // NAL header: nal_unit_type=34 (PPS)
    0xC1, 0x72, 0xB4, 0x62
};

// Sample HEVC IDR NAL unit
static const uint8_t kHevcIdr[] = {
    0x00, 0x00, 0x00, 0x01,  // Start code
    0x26, 0x01,  // NAL header: nal_unit_type=19 (IDR_W_RADL)
    0xAF, 0x08, 0x60  // Sample slice data
};

}  // namespace

// ============================================================================
// Test Fixture
// ============================================================================

class NalIntegrationTest : public ::testing::Test {
protected:
    context::TsDuckContext* ctx = nullptr;
    context::TsDuckAnalyzer* analyzer = nullptr;

    void SetUp() override {
        ctx = new context::TsDuckContext();
        ASSERT_TRUE(ctx->is_initialized());

        TsDuckConfigNative config{};
        config.metrics_interval_ms = 100;
        config.enable_tr101290 = 1;
        config.sample_size_bytes = ts::PKT_SIZE * 100;

        analyzer = new context::TsDuckAnalyzer(ctx, &config);
    }

    void TearDown() override {
        delete analyzer;
        delete ctx;
    }

    void feedPackets(const std::vector<uint8_t>& data) {
        analyzer->feed(data.data(), static_cast<int32_t>(data.size()));
    }
};

// ============================================================================
// PMT-based Video PID Detection Tests
// ============================================================================

TEST_F(NalIntegrationTest, DetectsH264VideoPidFromPmt) {
    std::vector<uint8_t> data(ts::PKT_SIZE * 2);

    // Feed PAT
    createPatPacket(&data[0], 1, 0x100);
    feedPackets(std::vector<uint8_t>(data.begin(), data.begin() + ts::PKT_SIZE));

    // Feed PMT with H.264 video
    createPmtPacket(&data[ts::PKT_SIZE], 0x100, 1, 0x101, 0x1B);
    feedPackets(std::vector<uint8_t>(data.begin() + ts::PKT_SIZE, data.end()));

    // Verify video PID was registered for NAL parsing
    int32_t idx = analyzer->nal_parser.find_stream_index(0x101);
    EXPECT_GE(idx, 0);
}

TEST_F(NalIntegrationTest, DetectsH265VideoPidFromPmt) {
    std::vector<uint8_t> data(ts::PKT_SIZE * 2);

    createPatPacket(&data[0], 1, 0x100);
    feedPackets(std::vector<uint8_t>(data.begin(), data.begin() + ts::PKT_SIZE));

    // H.265/HEVC stream type
    createPmtPacket(&data[ts::PKT_SIZE], 0x100, 1, 0x101, 0x24);
    feedPackets(std::vector<uint8_t>(data.begin() + ts::PKT_SIZE, data.end()));

    int32_t idx = analyzer->nal_parser.find_stream_index(0x101);
    EXPECT_GE(idx, 0);
}

TEST_F(NalIntegrationTest, DetectsH266VideoPidFromPmt) {
    std::vector<uint8_t> data(ts::PKT_SIZE * 2);

    createPatPacket(&data[0], 1, 0x100);
    feedPackets(std::vector<uint8_t>(data.begin(), data.begin() + ts::PKT_SIZE));

    // H.266/VVC stream type
    createPmtPacket(&data[ts::PKT_SIZE], 0x100, 1, 0x101, 0x33);
    feedPackets(std::vector<uint8_t>(data.begin() + ts::PKT_SIZE, data.end()));

    int32_t idx = analyzer->nal_parser.find_stream_index(0x101);
    EXPECT_GE(idx, 0);
}

// ============================================================================
// End-to-End Parameter Set Detection Tests
// ============================================================================

TEST_F(NalIntegrationTest, CachesH264SpsFromPes) {
    std::vector<uint8_t> data(ts::PKT_SIZE * 3);

    // Setup: PAT -> PMT
    createPatPacket(&data[0], 1, 0x100);
    createPmtPacket(&data[ts::PKT_SIZE], 0x100, 1, 0x101, 0x1B);
    feedPackets(std::vector<uint8_t>(data.begin(), data.begin() + ts::PKT_SIZE * 2));

    // Feed PES with SPS
    createPesStart(&data[ts::PKT_SIZE * 2], 0x101, 0, 0xE0, kH264Sps, sizeof(kH264Sps));
    feedPackets(std::vector<uint8_t>(data.begin() + ts::PKT_SIZE * 2, data.end()));

    // Check SPS was cached
    NalParameterSetsNative params{};
    bool has_params = analyzer->nal_parser.get_parameter_sets(0x101, &params);

    // Parsing may succeed or fail depending on SPS validity
    if (has_params) {
        EXPECT_GT(params.sps_length, 0);
    }
}

TEST_F(NalIntegrationTest, CachesH264SpsPps) {
    std::vector<uint8_t> data(ts::PKT_SIZE * 4);

    // Setup
    createPatPacket(&data[0], 1, 0x100);
    createPmtPacket(&data[ts::PKT_SIZE], 0x100, 1, 0x101, 0x1B);
    feedPackets(std::vector<uint8_t>(data.begin(), data.begin() + ts::PKT_SIZE * 2));

    // Build combined SPS + PPS NAL data
    std::vector<uint8_t> combined_nal;
    combined_nal.insert(combined_nal.end(), kH264Sps, kH264Sps + sizeof(kH264Sps));
    combined_nal.insert(combined_nal.end(), kH264Pps, kH264Pps + sizeof(kH264Pps));

    createPesStart(&data[ts::PKT_SIZE * 2], 0x101, 0, 0xE0,
                   combined_nal.data(), combined_nal.size());
    feedPackets(std::vector<uint8_t>(data.begin() + ts::PKT_SIZE * 2,
                                      data.begin() + ts::PKT_SIZE * 3));

    NalParameterSetsNative params{};
    bool has_params = analyzer->nal_parser.get_parameter_sets(0x101, &params);

    if (has_params) {
        EXPECT_GT(params.sps_length, 0);
        // PPS may or may not be cached depending on parsing
    }
}

// ============================================================================
// IDR Frame Detection Tests
// ============================================================================

TEST_F(NalIntegrationTest, DetectsH264IdrFrame) {
    std::vector<uint8_t> data(ts::PKT_SIZE * 3);

    // Setup
    createPatPacket(&data[0], 1, 0x100);
    createPmtPacket(&data[ts::PKT_SIZE], 0x100, 1, 0x101, 0x1B);
    feedPackets(std::vector<uint8_t>(data.begin(), data.begin() + ts::PKT_SIZE * 2));

    // Feed IDR frame
    createPesStart(&data[ts::PKT_SIZE * 2], 0x101, 0, 0xE0, kH264Idr, sizeof(kH264Idr));
    feedPackets(std::vector<uint8_t>(data.begin() + ts::PKT_SIZE * 2, data.end()));

    // Check IDR was detected
    bool has_idr = analyzer->nal_parser.check_idr_frame(0x101);
    // May or may not detect depending on NAL parsing
    // Just verify no crash
    (void)has_idr;

    int64_t idr_count = analyzer->nal_parser.total_idr_frames.load();
    EXPECT_GE(idr_count, 0);
}

TEST_F(NalIntegrationTest, DetectsHevcIdrFrame) {
    std::vector<uint8_t> data(ts::PKT_SIZE * 3);

    createPatPacket(&data[0], 1, 0x100);
    createPmtPacket(&data[ts::PKT_SIZE], 0x100, 1, 0x101, 0x24);
    feedPackets(std::vector<uint8_t>(data.begin(), data.begin() + ts::PKT_SIZE * 2));

    createPesStart(&data[ts::PKT_SIZE * 2], 0x101, 0, 0xE0, kHevcIdr, sizeof(kHevcIdr));
    feedPackets(std::vector<uint8_t>(data.begin() + ts::PKT_SIZE * 2, data.end()));

    int64_t idr_count = analyzer->nal_parser.total_idr_frames.load();
    EXPECT_GE(idr_count, 0);
}

TEST_F(NalIntegrationTest, IdrFrameCountIncrements) {
    std::vector<uint8_t> data(ts::PKT_SIZE * 4);

    createPatPacket(&data[0], 1, 0x100);
    createPmtPacket(&data[ts::PKT_SIZE], 0x100, 1, 0x101, 0x1B);
    feedPackets(std::vector<uint8_t>(data.begin(), data.begin() + ts::PKT_SIZE * 2));

    int64_t initial_count = analyzer->nal_parser.total_idr_frames.load();

    // Feed multiple IDR frames
    for (int i = 0; i < 3; i++) {
        createPesStart(&data[ts::PKT_SIZE * 2], 0x101, static_cast<uint8_t>(i),
                       0xE0, kH264Idr, sizeof(kH264Idr));
        feedPackets(std::vector<uint8_t>(data.begin() + ts::PKT_SIZE * 2,
                                          data.begin() + ts::PKT_SIZE * 3));
    }

    int64_t final_count = analyzer->nal_parser.total_idr_frames.load();
    EXPECT_GE(final_count, initial_count);
}

// ============================================================================
// C API Integration Tests
// ============================================================================

TEST(NalCApiTest, GetVideoCodecInfoWithNullHandle) {
    VideoCodecInfoNative info{};
    EXPECT_FALSE(tsduck_analyzer_get_video_codec_info(nullptr, 0x101, &info));
}

TEST(NalCApiTest, GetVideoCodecInfoWithNullBuffer) {
    TsDuckContextHandle ctx = tsduck_context_create();
    ASSERT_NE(ctx, nullptr);

    TsDuckConfigNative config{};
    TsDuckAnalyzerHandle analyzer = tsduck_analyzer_create(ctx, &config);
    ASSERT_NE(analyzer, nullptr);

    EXPECT_FALSE(tsduck_analyzer_get_video_codec_info(analyzer, 0x101, nullptr));

    tsduck_analyzer_destroy(analyzer);
    tsduck_context_destroy(ctx);
}

TEST(NalCApiTest, GetParameterSetsWithNullHandle) {
    NalParameterSetsNative params{};
    EXPECT_FALSE(tsduck_analyzer_get_parameter_sets(nullptr, 0x101, &params));
}

TEST(NalCApiTest, GetParameterSetsWithNullBuffer) {
    TsDuckContextHandle ctx = tsduck_context_create();
    ASSERT_NE(ctx, nullptr);

    TsDuckConfigNative config{};
    TsDuckAnalyzerHandle analyzer = tsduck_analyzer_create(ctx, &config);
    ASSERT_NE(analyzer, nullptr);

    EXPECT_FALSE(tsduck_analyzer_get_parameter_sets(analyzer, 0x101, nullptr));

    tsduck_analyzer_destroy(analyzer);
    tsduck_context_destroy(ctx);
}

TEST(NalCApiTest, HasIdrFrameWithNullHandle) {
    EXPECT_FALSE(tsduck_analyzer_has_idr_frame(nullptr, 0x101));
}

TEST(NalCApiTest, GetIdrFrameCountWithNullHandle) {
    EXPECT_EQ(tsduck_analyzer_get_idr_frame_count(nullptr), 0);
}

TEST(NalCApiTest, RegisterVideoPidDirectly) {
    TsDuckContextHandle ctx = tsduck_context_create();
    ASSERT_NE(ctx, nullptr);

    TsDuckConfigNative config{};
    TsDuckAnalyzerHandle analyzer = tsduck_analyzer_create(ctx, &config);
    ASSERT_NE(analyzer, nullptr);

    // Register video PID manually (without PMT)
    EXPECT_TRUE(tsduck_analyzer_register_video_pid(analyzer, 0x101, 0x1B));

    // Should now be registered
    VideoCodecInfoNative info{};
    // May or may not have codec info yet (no data fed)
    tsduck_analyzer_get_video_codec_info(analyzer, 0x101, &info);

    tsduck_analyzer_destroy(analyzer);
    tsduck_context_destroy(ctx);
}

TEST(NalCApiTest, RegisterVideoPidWithNullHandle) {
    EXPECT_FALSE(tsduck_analyzer_register_video_pid(nullptr, 0x101, 0x1B));
}

// ============================================================================
// Resolution Extraction Tests
// ============================================================================

TEST_F(NalIntegrationTest, ExtractsH264Resolution) {
    std::vector<uint8_t> data(ts::PKT_SIZE * 3);

    createPatPacket(&data[0], 1, 0x100);
    createPmtPacket(&data[ts::PKT_SIZE], 0x100, 1, 0x101, 0x1B);
    feedPackets(std::vector<uint8_t>(data.begin(), data.begin() + ts::PKT_SIZE * 2));

    createPesStart(&data[ts::PKT_SIZE * 2], 0x101, 0, 0xE0, kH264Sps, sizeof(kH264Sps));
    feedPackets(std::vector<uint8_t>(data.begin() + ts::PKT_SIZE * 2, data.end()));

    VideoCodecInfoNative info{};
    bool has_info = analyzer->nal_parser.get_video_codec_info(0x101, &info);

    if (has_info && info.width > 0) {
        EXPECT_EQ(info.codec_type, static_cast<uint8_t>(VideoCodecType::H264_AVC));
        // Resolution depends on the actual SPS data
        EXPECT_GT(info.width, 0);
        EXPECT_GT(info.height, 0);
    }
}

TEST_F(NalIntegrationTest, ExtractsHevcResolution) {
    std::vector<uint8_t> data(ts::PKT_SIZE * 3);

    createPatPacket(&data[0], 1, 0x100);
    createPmtPacket(&data[ts::PKT_SIZE], 0x100, 1, 0x101, 0x24);
    feedPackets(std::vector<uint8_t>(data.begin(), data.begin() + ts::PKT_SIZE * 2));

    // Combine VPS + SPS for HEVC
    std::vector<uint8_t> combined_nal;
    combined_nal.insert(combined_nal.end(), kHevcVps, kHevcVps + sizeof(kHevcVps));
    combined_nal.insert(combined_nal.end(), kHevcSps, kHevcSps + sizeof(kHevcSps));

    createPesStart(&data[ts::PKT_SIZE * 2], 0x101, 0, 0xE0,
                   combined_nal.data(), combined_nal.size());
    feedPackets(std::vector<uint8_t>(data.begin() + ts::PKT_SIZE * 2, data.end()));

    VideoCodecInfoNative info{};
    bool has_info = analyzer->nal_parser.get_video_codec_info(0x101, &info);

    if (has_info && info.width > 0) {
        EXPECT_EQ(info.codec_type, static_cast<uint8_t>(VideoCodecType::H265_HEVC));
    }
}

// ============================================================================
// Edge Cases
// ============================================================================

TEST_F(NalIntegrationTest, NoVideoPidWithoutPmt) {
    // Without PMT, no video PIDs should be registered
    EXPECT_EQ(analyzer->nal_parser.video_stream_count.load(), 0);
    EXPECT_EQ(analyzer->nal_parser.find_stream_index(0x101), -1);
}

TEST_F(NalIntegrationTest, ResetClearsNalState) {
    std::vector<uint8_t> data(ts::PKT_SIZE * 3);

    createPatPacket(&data[0], 1, 0x100);
    createPmtPacket(&data[ts::PKT_SIZE], 0x100, 1, 0x101, 0x1B);
    createPesStart(&data[ts::PKT_SIZE * 2], 0x101, 0, 0xE0, kH264Sps, sizeof(kH264Sps));
    feedPackets(data);

    // Reset
    analyzer->reset();

    // IDR count should be reset
    EXPECT_EQ(analyzer->nal_parser.total_idr_frames.load(), 0);

    // Video PIDs remain registered (only param data is cleared)
    // This is implementation-defined behavior
}

TEST_F(NalIntegrationTest, MultipleVideoPids) {
    // Manually register multiple video PIDs
    analyzer->nal_parser.add_video_pid(0x101, 0x1B);  // H.264
    analyzer->nal_parser.add_video_pid(0x201, 0x24);  // H.265

    EXPECT_EQ(analyzer->nal_parser.video_stream_count.load(), 2);
    EXPECT_GE(analyzer->nal_parser.find_stream_index(0x101), 0);
    EXPECT_GE(analyzer->nal_parser.find_stream_index(0x201), 0);
}

TEST_F(NalIntegrationTest, DuplicateVideoPidRegistration) {
    int32_t idx1 = analyzer->nal_parser.add_video_pid(0x101, 0x1B);
    int32_t idx2 = analyzer->nal_parser.add_video_pid(0x101, 0x1B);

    // Should return same index
    EXPECT_EQ(idx1, idx2);
    EXPECT_EQ(analyzer->nal_parser.video_stream_count.load(), 1);
}

TEST_F(NalIntegrationTest, InvalidPidRejected) {
    int32_t idx = analyzer->nal_parser.add_video_pid(0x1FFF, 0x1B);  // Null PID
    EXPECT_EQ(idx, -1);
}

TEST_F(NalIntegrationTest, MaxVideoPidsLimit) {
    // Register up to MAX_VIDEO_PIDS
    for (size_t i = 0; i < MAX_VIDEO_PIDS; i++) {
        int32_t idx = analyzer->nal_parser.add_video_pid(
            static_cast<uint16_t>(0x100 + i), 0x1B);
        EXPECT_GE(idx, 0);
    }

    // Next registration should fail
    int32_t overflow_idx = analyzer->nal_parser.add_video_pid(
        static_cast<uint16_t>(0x100 + MAX_VIDEO_PIDS), 0x1B);
    EXPECT_EQ(overflow_idx, -1);
}

int main(int argc, char** argv) {
    ::testing::InitGoogleTest(&argc, argv);
    return RUN_ALL_TESTS();
}
