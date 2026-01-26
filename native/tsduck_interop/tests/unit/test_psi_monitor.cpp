// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#include <gtest/gtest.h>
#include <cstring>
#include <array>
#include <vector>
#include "analysis/psi_monitor.hpp"

using namespace tsduck_interop;
using namespace tsduck_interop::analysis;

// ============================================================================
// Helper: Compute CRC-32/MPEG-2 using TsDuck's utility
// ============================================================================

static uint32_t computeCrc32(const uint8_t* data, size_t length) {
    ts::CRC32 crc;
    crc.add(data, length);
    return crc.value();
}

// ============================================================================
// Helper: Build a TS packet with given PID, PUSI, and payload
// ============================================================================

static ts::TSPacket makePacket(uint16_t pid, bool pusi, const uint8_t* payload, size_t payload_len) {
    ts::TSPacket pkt;
    std::memset(&pkt, 0xFF, ts::PKT_SIZE);

    pkt.b[0] = ts::SYNC_BYTE;
    pkt.b[1] = static_cast<uint8_t>(((pusi ? 0x40 : 0x00) | ((pid >> 8) & 0x1F)));
    pkt.b[2] = static_cast<uint8_t>(pid & 0xFF);
    pkt.b[3] = 0x10;  // payload only, CC=0

    if (payload && payload_len > 0) {
        size_t max_payload = ts::PKT_SIZE - 4;
        size_t copy_len = std::min(payload_len, max_payload);
        std::memcpy(&pkt.b[4], payload, copy_len);
    }

    return pkt;
}

// ============================================================================
// Helper: Build a PAT section with CRC
// ============================================================================

static std::vector<uint8_t> buildPatSection(
    uint16_t tsid,
    const std::vector<std::pair<uint16_t, uint16_t>>& programs,
    int8_t version = 0)
{
    size_t payload_len = programs.size() * 4;
    uint16_t section_length = static_cast<uint16_t>(5 + payload_len + 4);

    std::vector<uint8_t> section;
    section.reserve(3 + section_length);

    section.push_back(0x00);  // table_id = PAT
    section.push_back(static_cast<uint8_t>(0xB0 | ((section_length >> 8) & 0x0F)));
    section.push_back(static_cast<uint8_t>(section_length & 0xFF));
    section.push_back(static_cast<uint8_t>(tsid >> 8));
    section.push_back(static_cast<uint8_t>(tsid & 0xFF));
    section.push_back(static_cast<uint8_t>(0xC0 | ((version & 0x1F) << 1) | 0x01));
    section.push_back(0x00);  // section_number
    section.push_back(0x00);  // last_section_number

    for (const auto& [prog_num, pmt_pid] : programs) {
        section.push_back(static_cast<uint8_t>(prog_num >> 8));
        section.push_back(static_cast<uint8_t>(prog_num & 0xFF));
        section.push_back(static_cast<uint8_t>(0xE0 | ((pmt_pid >> 8) & 0x1F)));
        section.push_back(static_cast<uint8_t>(pmt_pid & 0xFF));
    }

    // Compute and append CRC-32
    uint32_t crc = computeCrc32(section.data(), section.size());
    section.push_back(static_cast<uint8_t>((crc >> 24) & 0xFF));
    section.push_back(static_cast<uint8_t>((crc >> 16) & 0xFF));
    section.push_back(static_cast<uint8_t>((crc >> 8) & 0xFF));
    section.push_back(static_cast<uint8_t>(crc & 0xFF));

    return section;
}

// ============================================================================
// Helper: Build a PMT section with CRC
// ============================================================================

struct EsInfo {
    uint8_t stream_type;
    uint16_t pid;
};

static std::vector<uint8_t> buildPmtSection(
    uint16_t program_number,
    uint16_t pcr_pid,
    const std::vector<EsInfo>& streams,
    int8_t version = 0)
{
    size_t es_len = streams.size() * 5;  // 5 bytes per ES entry (no descriptors)
    uint16_t section_length = static_cast<uint16_t>(5 + 4 + es_len + 4);  // 5=fixed, 4=pcr+prog_info, 4=CRC

    std::vector<uint8_t> section;
    section.reserve(3 + section_length);

    section.push_back(0x02);  // table_id = PMT
    section.push_back(static_cast<uint8_t>(0xB0 | ((section_length >> 8) & 0x0F)));
    section.push_back(static_cast<uint8_t>(section_length & 0xFF));
    section.push_back(static_cast<uint8_t>(program_number >> 8));
    section.push_back(static_cast<uint8_t>(program_number & 0xFF));
    section.push_back(static_cast<uint8_t>(0xC0 | ((version & 0x1F) << 1) | 0x01));
    section.push_back(0x00);  // section_number
    section.push_back(0x00);  // last_section_number
    // PCR PID
    section.push_back(static_cast<uint8_t>(0xE0 | ((pcr_pid >> 8) & 0x1F)));
    section.push_back(static_cast<uint8_t>(pcr_pid & 0xFF));
    // program_info_length = 0
    section.push_back(0xF0);
    section.push_back(0x00);

    for (const auto& es : streams) {
        section.push_back(es.stream_type);
        section.push_back(static_cast<uint8_t>(0xE0 | ((es.pid >> 8) & 0x1F)));
        section.push_back(static_cast<uint8_t>(es.pid & 0xFF));
        section.push_back(0xF0);  // ES_info_length = 0
        section.push_back(0x00);
    }

    uint32_t crc = computeCrc32(section.data(), section.size());
    section.push_back(static_cast<uint8_t>((crc >> 24) & 0xFF));
    section.push_back(static_cast<uint8_t>((crc >> 16) & 0xFF));
    section.push_back(static_cast<uint8_t>((crc >> 8) & 0xFF));
    section.push_back(static_cast<uint8_t>(crc & 0xFF));

    return section;
}

// ============================================================================
// Helper: Wrap a PSI section into a TS packet (PUSI + pointer_field=0)
// ============================================================================

static ts::TSPacket wrapSectionInPacket(uint16_t pid, const std::vector<uint8_t>& section, uint8_t cc = 0) {
    ts::TSPacket pkt;
    std::memset(&pkt, 0xFF, ts::PKT_SIZE);

    pkt.b[0] = ts::SYNC_BYTE;
    pkt.b[1] = static_cast<uint8_t>(0x40 | ((pid >> 8) & 0x1F));  // PUSI=1
    pkt.b[2] = static_cast<uint8_t>(pid & 0xFF);
    pkt.b[3] = static_cast<uint8_t>(0x10 | (cc & 0x0F));  // payload only

    // pointer_field = 0 (section starts immediately)
    pkt.b[4] = 0x00;

    size_t copy_len = std::min(section.size(), size_t(183));
    std::memcpy(&pkt.b[5], section.data(), copy_len);

    return pkt;
}

// ============================================================================
// Test Fixture
// ============================================================================

class PsiMonitorTest : public ::testing::Test {
protected:
    ts::DuckContext duck;
    std::unique_ptr<PsiMonitor> monitor;

    void SetUp() override {
        monitor = std::make_unique<PsiMonitor>(duck);
    }
};

// ============================================================================
// Stream Type Classification Tests
// ============================================================================

TEST_F(PsiMonitorTest, ClassifiesH264AsVideo) {
    bool is_video = false, is_audio = false;
    EXPECT_TRUE(classify_stream_type(0x1B, is_video, is_audio));
    EXPECT_TRUE(is_video);
    EXPECT_FALSE(is_audio);
}

TEST_F(PsiMonitorTest, ClassifiesHevcAsVideo) {
    bool is_video = false, is_audio = false;
    EXPECT_TRUE(classify_stream_type(0x24, is_video, is_audio));
    EXPECT_TRUE(is_video);
    EXPECT_FALSE(is_audio);
}

TEST_F(PsiMonitorTest, ClassifiesVvcAsVideo) {
    bool is_video = false, is_audio = false;
    EXPECT_TRUE(classify_stream_type(0x33, is_video, is_audio));
    EXPECT_TRUE(is_video);
    EXPECT_FALSE(is_audio);
}

TEST_F(PsiMonitorTest, ClassifiesAacAsAudio) {
    bool is_video = false, is_audio = false;
    EXPECT_TRUE(classify_stream_type(0x0F, is_video, is_audio));
    EXPECT_FALSE(is_video);
    EXPECT_TRUE(is_audio);
}

TEST_F(PsiMonitorTest, ClassifiesAc3AsAudio) {
    bool is_video = false, is_audio = false;
    EXPECT_TRUE(classify_stream_type(0x81, is_video, is_audio));
    EXPECT_FALSE(is_video);
    EXPECT_TRUE(is_audio);
}

TEST_F(PsiMonitorTest, ClassifiesEac3AsAudio) {
    bool is_video = false, is_audio = false;
    EXPECT_TRUE(classify_stream_type(0x87, is_video, is_audio));
    EXPECT_FALSE(is_video);
    EXPECT_TRUE(is_audio);
}

TEST_F(PsiMonitorTest, UnknownStreamTypeReturnsFalse) {
    bool is_video = false, is_audio = false;
    EXPECT_FALSE(classify_stream_type(0x05, is_video, is_audio));  // Private sections
    EXPECT_FALSE(is_video);
    EXPECT_FALSE(is_audio);
}

// ============================================================================
// PAT Parsing Tests
// ============================================================================

TEST_F(PsiMonitorTest, ParsesSingleProgramPat) {
    auto section = buildPatSection(1, {{1, 0x100}});
    auto pkt = wrapSectionInPacket(0x0000, section);

    monitor->feed_packet(pkt, 1000000);

    EXPECT_EQ(monitor->get_program_count(), 1);
    EXPECT_EQ(monitor->programs[0].program_number, 1);
    EXPECT_EQ(monitor->programs[0].pmt_pid, 0x100);
    EXPECT_TRUE(monitor->programs[0].active);
}

TEST_F(PsiMonitorTest, ParsesMultipleProgramsPat) {
    auto section = buildPatSection(1, {{1, 0x100}, {2, 0x200}, {3, 0x300}});
    auto pkt = wrapSectionInPacket(0x0000, section);

    monitor->feed_packet(pkt, 2000000);

    EXPECT_EQ(monitor->get_program_count(), 3);
    EXPECT_EQ(monitor->programs[0].program_number, 1);
    EXPECT_EQ(monitor->programs[0].pmt_pid, 0x100);
    EXPECT_EQ(monitor->programs[1].program_number, 2);
    EXPECT_EQ(monitor->programs[1].pmt_pid, 0x200);
    EXPECT_EQ(monitor->programs[2].program_number, 3);
    EXPECT_EQ(monitor->programs[2].pmt_pid, 0x300);
}

TEST_F(PsiMonitorTest, PatSkipsNetworkPid) {
    // Program 0 is the network PID entry — PAT class should skip it
    auto section = buildPatSection(1, {{0, 0x010}, {1, 0x100}});
    auto pkt = wrapSectionInPacket(0x0000, section);

    monitor->feed_packet(pkt, 3000000);

    // ts::PAT.pmts only contains non-zero program numbers
    EXPECT_EQ(monitor->get_program_count(), 1);
    EXPECT_EQ(monitor->programs[0].program_number, 1);
}

TEST_F(PsiMonitorTest, PatVersionChangeUpdatesPrograms) {
    // First version
    auto section_v0 = buildPatSection(1, {{1, 0x100}}, 0);
    auto pkt_v0 = wrapSectionInPacket(0x0000, section_v0);
    monitor->feed_packet(pkt_v0, 1000000);
    EXPECT_EQ(monitor->get_program_count(), 1);

    // Same version — SectionDemux should skip (no handleTable called)
    auto section_v0_dup = buildPatSection(1, {{1, 0x100}, {2, 0x200}}, 0);
    auto pkt_v0_dup = wrapSectionInPacket(0x0000, section_v0_dup, 1);
    monitor->feed_packet(pkt_v0_dup, 2000000);
    EXPECT_EQ(monitor->get_program_count(), 1);  // Still 1 — duplicate version ignored

    // New version
    auto section_v1 = buildPatSection(1, {{1, 0x100}, {2, 0x200}}, 1);
    auto pkt_v1 = wrapSectionInPacket(0x0000, section_v1, 2);
    monitor->feed_packet(pkt_v1, 3000000);
    EXPECT_EQ(monitor->get_program_count(), 2);  // Updated
}

TEST_F(PsiMonitorTest, PatUpdatesPacketIndex) {
    auto section = buildPatSection(1, {{1, 0x100}});
    auto pkt = wrapSectionInPacket(0x0000, section);

    monitor->feed_packet(pkt, 5000);

    EXPECT_EQ(monitor->last_pat_packet_idx.load(), 5000);
}

TEST_F(PsiMonitorTest, InvalidCrcSectionIgnored) {
    auto section = buildPatSection(1, {{1, 0x100}});
    // Corrupt the CRC
    section[section.size() - 1] ^= 0xFF;
    auto pkt = wrapSectionInPacket(0x0000, section);

    monitor->feed_packet(pkt, 1000000);

    // SectionDemux silently discards invalid CRC sections
    EXPECT_EQ(monitor->get_program_count(), 0);
}

// ============================================================================
// PMT Parsing Tests
// ============================================================================

TEST_F(PsiMonitorTest, ParsesPmtAfterPat) {
    // First send PAT to discover PMT PID
    auto pat_section = buildPatSection(1, {{1, 0x100}});
    auto pat_pkt = wrapSectionInPacket(0x0000, pat_section);
    monitor->feed_packet(pat_pkt, 1000000);
    ASSERT_EQ(monitor->get_program_count(), 1);

    // Now send PMT on the discovered PID
    auto pmt_section = buildPmtSection(1, 0x100,
        {{0x1B, 0x101}, {0x0F, 0x102}});  // H.264 + AAC
    auto pmt_pkt = wrapSectionInPacket(0x100, pmt_section);
    monitor->feed_packet(pmt_pkt, 2000000);

    auto& prog = monitor->programs[0];
    EXPECT_TRUE(prog.pmt_received);
    EXPECT_EQ(prog.pcr_pid, 0x100);
    EXPECT_EQ(prog.stream_count, 2);
    EXPECT_EQ(prog.last_pmt_packet_idx, 2000000);

    // Video stream
    EXPECT_EQ(prog.streams[0].pid, 0x101);
    EXPECT_EQ(prog.streams[0].stream_type, 0x1B);
    EXPECT_TRUE(prog.streams[0].is_video);
    EXPECT_FALSE(prog.streams[0].is_audio);

    // Audio stream
    EXPECT_EQ(prog.streams[1].pid, 0x102);
    EXPECT_EQ(prog.streams[1].stream_type, 0x0F);
    EXPECT_FALSE(prog.streams[1].is_video);
    EXPECT_TRUE(prog.streams[1].is_audio);
}

TEST_F(PsiMonitorTest, PmtForUnknownProgramIgnored) {
    // Send PAT with program 1
    auto pat_section = buildPatSection(1, {{1, 0x100}});
    auto pat_pkt = wrapSectionInPacket(0x0000, pat_section);
    monitor->feed_packet(pat_pkt, 1000000);

    // Send PMT for program 99 (not in PAT) on a different PID
    auto pmt_section = buildPmtSection(99, 0x200, {{0x1B, 0x201}});
    auto pmt_pkt = wrapSectionInPacket(0x200, pmt_section);
    monitor->feed_packet(pmt_pkt, 2000000);

    // Program 1 should still not have PMT
    EXPECT_FALSE(monitor->programs[0].pmt_received);
}

TEST_F(PsiMonitorTest, IsPmtPidDetectsKnownPids) {
    auto pat_section = buildPatSection(1, {{1, 0x100}, {2, 0x200}});
    auto pat_pkt = wrapSectionInPacket(0x0000, pat_section);
    monitor->feed_packet(pat_pkt, 1000000);

    EXPECT_TRUE(monitor->is_pmt_pid(0x100));
    EXPECT_TRUE(monitor->is_pmt_pid(0x200));
    EXPECT_FALSE(monitor->is_pmt_pid(0x300));
    EXPECT_FALSE(monitor->is_pmt_pid(0x0000));
}

// ============================================================================
// C API Tests
// ============================================================================

TEST_F(PsiMonitorTest, GetProgramsReturnsInfo) {
    // Setup PAT + PMT
    auto pat_section = buildPatSection(1, {{1, 0x100}});
    auto pat_pkt = wrapSectionInPacket(0x0000, pat_section);
    monitor->feed_packet(pat_pkt, 1000000);

    auto pmt_section = buildPmtSection(1, 0x100,
        {{0x1B, 0x101}, {0x0F, 0x102}});
    auto pmt_pkt = wrapSectionInPacket(0x100, pmt_section);
    monitor->feed_packet(pmt_pkt, 2000000);

    TsDuckProgramInfoNative info[4]{};
    int32_t count = monitor->get_programs(info, 4);

    EXPECT_EQ(count, 1);
    EXPECT_EQ(info[0].program_number, 1);
    EXPECT_EQ(info[0].pmt_pid, 0x100);
    EXPECT_EQ(info[0].pcr_pid, 0x100);
    EXPECT_EQ(info[0].stream_count, 2);
    EXPECT_EQ(info[0].has_video, 1);
    EXPECT_EQ(info[0].has_audio, 1);
}

TEST_F(PsiMonitorTest, GetProgramsRespectsMaxLimit) {
    auto pat_section = buildPatSection(1, {{1, 0x100}, {2, 0x200}, {3, 0x300}});
    auto pat_pkt = wrapSectionInPacket(0x0000, pat_section);
    monitor->feed_packet(pat_pkt, 1000000);

    TsDuckProgramInfoNative info[2]{};
    int32_t count = monitor->get_programs(info, 2);

    EXPECT_EQ(count, 2);  // Limited to max
}

// ============================================================================
// Reset Tests
// ============================================================================

TEST_F(PsiMonitorTest, ResetClearsState) {
    auto pat_section = buildPatSection(1, {{1, 0x100}});
    auto pat_pkt = wrapSectionInPacket(0x0000, pat_section);
    monitor->feed_packet(pat_pkt, 1000000);
    EXPECT_EQ(monitor->get_program_count(), 1);

    monitor->reset();

    EXPECT_EQ(monitor->get_program_count(), 0);
    EXPECT_EQ(monitor->last_pat_packet_idx.load(), -1);
    EXPECT_FALSE(monitor->programs[0].active);
}

TEST_F(PsiMonitorTest, ResetAllowsNewPatAfterReset) {
    // Send PAT v0
    auto pat_v0 = buildPatSection(1, {{1, 0x100}}, 0);
    auto pkt_v0 = wrapSectionInPacket(0x0000, pat_v0);
    monitor->feed_packet(pkt_v0, 1000000);
    EXPECT_EQ(monitor->get_program_count(), 1);

    monitor->reset();

    // Same version should work after reset (demux version tracking cleared)
    auto pat_v0_again = buildPatSection(1, {{1, 0x100}, {2, 0x200}}, 0);
    auto pkt_v0_again = wrapSectionInPacket(0x0000, pat_v0_again);
    monitor->feed_packet(pkt_v0_again, 2000000);
    EXPECT_EQ(monitor->get_program_count(), 2);
}

// ============================================================================
// Integration: Non-PSI packets are ignored
// ============================================================================

TEST_F(PsiMonitorTest, NonPsiPacketsIgnored) {
    // Feed a video packet (PID 0x101) — should be ignored by SectionDemux
    ts::TSPacket video_pkt;
    std::memset(&video_pkt, 0x00, ts::PKT_SIZE);
    video_pkt.b[0] = ts::SYNC_BYTE;
    video_pkt.b[1] = 0x41;  // PUSI=1, PID=0x101
    video_pkt.b[2] = 0x01;
    video_pkt.b[3] = 0x10;  // payload only

    monitor->feed_packet(video_pkt, 1000000);

    EXPECT_EQ(monitor->get_program_count(), 0);
}

// ============================================================================
// Multiple programs with PMTs
// ============================================================================

TEST_F(PsiMonitorTest, MultipleProgamsWithPmts) {
    // PAT with 2 programs
    auto pat = buildPatSection(1, {{1, 0x100}, {2, 0x200}});
    auto pat_pkt = wrapSectionInPacket(0x0000, pat);
    monitor->feed_packet(pat_pkt, 1000000);
    ASSERT_EQ(monitor->get_program_count(), 2);

    // PMT for program 1
    auto pmt1 = buildPmtSection(1, 0x101, {{0x1B, 0x101}});
    auto pmt1_pkt = wrapSectionInPacket(0x100, pmt1);
    monitor->feed_packet(pmt1_pkt, 2000000);

    // PMT for program 2
    auto pmt2 = buildPmtSection(2, 0x201, {{0x24, 0x201}, {0x87, 0x202}});
    auto pmt2_pkt = wrapSectionInPacket(0x200, pmt2);
    monitor->feed_packet(pmt2_pkt, 3000000);

    // Verify program 1
    EXPECT_TRUE(monitor->programs[0].pmt_received);
    EXPECT_EQ(monitor->programs[0].pcr_pid, 0x101);
    EXPECT_EQ(monitor->programs[0].stream_count, 1);
    EXPECT_EQ(monitor->programs[0].streams[0].stream_type, 0x1B);

    // Verify program 2
    EXPECT_TRUE(monitor->programs[1].pmt_received);
    EXPECT_EQ(monitor->programs[1].pcr_pid, 0x201);
    EXPECT_EQ(monitor->programs[1].stream_count, 2);
    EXPECT_EQ(monitor->programs[1].streams[0].stream_type, 0x24);  // HEVC
    EXPECT_TRUE(monitor->programs[1].streams[0].is_video);
    EXPECT_EQ(monitor->programs[1].streams[1].stream_type, 0x87);  // E-AC-3
    EXPECT_TRUE(monitor->programs[1].streams[1].is_audio);
}
