// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#include <gtest/gtest.h>
#include <tsduck.h>
#include <cstring>

// ============================================================================
// Verify that ts::DuckContext can be safely instantiated in our environment.
// Previously this caused EDEADLK when TsDuck data files were missing.
// ============================================================================

TEST(DuckContextTest, InitializesWithoutDeadlock) {
    // Default constructor — requires TsDuck data files to be deployed
    ts::DuckContext duck;
    EXPECT_TRUE(true);  // If we reach here, no deadlock occurred
}

TEST(DuckContextTest, SectionDemuxCreates) {
    ts::DuckContext duck;
    ts::SectionDemux demux(duck);
    EXPECT_TRUE(true);
}

TEST(DuckContextTest, SectionDemuxAddPid) {
    ts::DuckContext duck;
    ts::SectionDemux demux(duck);
    demux.addPID(ts::PID_PAT);
    EXPECT_TRUE(true);
}

// ============================================================================
// Verify PAT/PMT deserialization via BinaryTable
// ============================================================================

// Helper: compute CRC-32/MPEG-2 for a section using TsDuck
static uint32_t computeCrc32(const uint8_t* data, size_t length) {
    ts::CRC32 crc;
    crc.add(data, length);
    return crc.value();
}

// Helper: build a minimal PAT section with one program
static std::vector<uint8_t> buildPatSection(uint16_t tsid, uint16_t prog_num, uint16_t pmt_pid) {
    std::vector<uint8_t> section;

    // table_id = 0x00 (PAT)
    section.push_back(0x00);
    // section_syntax_indicator(1)=1 | '0'(1)=0 | reserved(2)=11 | section_length(12)
    uint16_t section_length = 5 + 4 + 4;  // 5=fixed, 4=one program, 4=CRC
    section.push_back(static_cast<uint8_t>(0xB0 | ((section_length >> 8) & 0x0F)));
    section.push_back(static_cast<uint8_t>(section_length & 0xFF));
    // transport_stream_id
    section.push_back(static_cast<uint8_t>(tsid >> 8));
    section.push_back(static_cast<uint8_t>(tsid & 0xFF));
    // reserved(2)=11 | version(5)=0 | current_next(1)=1
    section.push_back(0xC1);
    // section_number
    section.push_back(0x00);
    // last_section_number
    section.push_back(0x00);
    // program entry: program_number(16) + reserved(3) + PID(13)
    section.push_back(static_cast<uint8_t>(prog_num >> 8));
    section.push_back(static_cast<uint8_t>(prog_num & 0xFF));
    section.push_back(static_cast<uint8_t>(0xE0 | ((pmt_pid >> 8) & 0x1F)));
    section.push_back(static_cast<uint8_t>(pmt_pid & 0xFF));

    // Compute and append CRC-32
    uint32_t crc = computeCrc32(section.data(), section.size());
    section.push_back(static_cast<uint8_t>((crc >> 24) & 0xFF));
    section.push_back(static_cast<uint8_t>((crc >> 16) & 0xFF));
    section.push_back(static_cast<uint8_t>((crc >> 8) & 0xFF));
    section.push_back(static_cast<uint8_t>(crc & 0xFF));

    return section;
}

TEST(DuckContextTest, PatDeserialization) {
    ts::DuckContext duck;

    // Build a PAT with one program (prog 1 -> PMT PID 0x100)
    auto section_data = buildPatSection(1, 1, 0x100);

    // Create a Section via shared_ptr (Section is not copyable)
    auto section_ptr = std::make_shared<ts::Section>(
        section_data.data(), section_data.size(), ts::PID_PAT, ts::CRC32::CHECK);
    ASSERT_TRUE(section_ptr->isValid()) << "Section should be valid with correct CRC";

    // Create a BinaryTable from the section
    ts::BinaryTable table;
    table.addSection(section_ptr);
    ASSERT_TRUE(table.isValid());

    // Deserialize into a PAT
    ts::PAT pat(duck, table);
    ASSERT_TRUE(pat.isValid()) << "PAT should deserialize successfully";

    // Verify content
    EXPECT_EQ(pat.ts_id, 1);
    ASSERT_EQ(pat.pmts.size(), 1u);
    EXPECT_EQ(pat.pmts.begin()->first, 1);      // program_number
    EXPECT_EQ(pat.pmts.begin()->second, 0x100u); // PMT PID
}

// Helper: build a minimal PMT section
static std::vector<uint8_t> buildPmtSection(uint16_t prog_num, uint16_t pcr_pid,
                                             uint8_t stream_type, uint16_t es_pid) {
    std::vector<uint8_t> section;

    // table_id = 0x02 (PMT)
    section.push_back(0x02);
    // section_syntax_indicator + section_length
    uint16_t section_length = 5 + 4 + 5 + 4;  // 5=fixed, 4=pcr+prog_info, 5=one ES, 4=CRC
    section.push_back(static_cast<uint8_t>(0xB0 | ((section_length >> 8) & 0x0F)));
    section.push_back(static_cast<uint8_t>(section_length & 0xFF));
    // program_number
    section.push_back(static_cast<uint8_t>(prog_num >> 8));
    section.push_back(static_cast<uint8_t>(prog_num & 0xFF));
    // reserved(2)=11 | version(5)=0 | current_next(1)=1
    section.push_back(0xC1);
    // section_number
    section.push_back(0x00);
    // last_section_number
    section.push_back(0x00);
    // reserved(3)=111 | PCR_PID(13)
    section.push_back(static_cast<uint8_t>(0xE0 | ((pcr_pid >> 8) & 0x1F)));
    section.push_back(static_cast<uint8_t>(pcr_pid & 0xFF));
    // reserved(4)=1111 | program_info_length(12)=0
    section.push_back(0xF0);
    section.push_back(0x00);
    // Elementary stream entry: stream_type + reserved(3) + ES_PID(13) + ES_info_length(12)=0
    section.push_back(stream_type);
    section.push_back(static_cast<uint8_t>(0xE0 | ((es_pid >> 8) & 0x1F)));
    section.push_back(static_cast<uint8_t>(es_pid & 0xFF));
    section.push_back(0xF0);
    section.push_back(0x00);

    // CRC-32
    uint32_t crc = computeCrc32(section.data(), section.size());
    section.push_back(static_cast<uint8_t>((crc >> 24) & 0xFF));
    section.push_back(static_cast<uint8_t>((crc >> 16) & 0xFF));
    section.push_back(static_cast<uint8_t>((crc >> 8) & 0xFF));
    section.push_back(static_cast<uint8_t>(crc & 0xFF));

    return section;
}

TEST(DuckContextTest, PmtDeserialization) {
    ts::DuckContext duck;

    // Build PMT: program 1, PCR PID 0x100, H.264 video on PID 0x101
    auto section_data = buildPmtSection(1, 0x100, 0x1B, 0x101);

    auto section_ptr = std::make_shared<ts::Section>(
        section_data.data(), section_data.size(), ts::PID(0x100), ts::CRC32::CHECK);
    ASSERT_TRUE(section_ptr->isValid());

    ts::BinaryTable table;
    table.addSection(section_ptr);
    ASSERT_TRUE(table.isValid());

    ts::PMT pmt(duck, table);
    ASSERT_TRUE(pmt.isValid()) << "PMT should deserialize successfully";

    EXPECT_EQ(pmt.service_id, 1);
    EXPECT_EQ(pmt.pcr_pid, 0x100u);
    ASSERT_EQ(pmt.streams.size(), 1u);

    auto it = pmt.streams.begin();
    EXPECT_EQ(it->first, 0x101u);       // ES PID
    EXPECT_EQ(it->second.stream_type, 0x1Bu);  // H.264
}

TEST(DuckContextTest, SectionDemuxWithHandler) {
    // Test the full pipeline: create handler, feed packets, receive table
    struct TestHandler : public ts::TableHandlerInterface {
        int tables_received = 0;
        ts::TID last_table_id = 0xFF;

        void handleTable(ts::SectionDemux& /*demux*/, const ts::BinaryTable& table) override {
            tables_received++;
            last_table_id = table.tableId();
        }
    };

    ts::DuckContext duck;
    TestHandler handler;
    ts::SectionDemux demux(duck, &handler);
    demux.addPID(ts::PID_PAT);

    // Build a PAT section
    auto section_data = buildPatSection(1, 1, 0x100);

    // Wrap in a TS packet with PUSI + pointer_field=0
    ts::TSPacket pkt;
    std::memset(&pkt, 0xFF, ts::PKT_SIZE);
    pkt.b[0] = ts::SYNC_BYTE;
    pkt.b[1] = 0x40;  // PUSI=1, PID=0x0000 (high bits)
    pkt.b[2] = 0x00;  // PID=0x0000 (low bits)
    pkt.b[3] = 0x10;  // payload only, CC=0

    // Payload: pointer_field (0x00) + section data
    pkt.b[4] = 0x00;  // pointer_field
    std::memcpy(&pkt.b[5], section_data.data(), std::min(section_data.size(), size_t(183)));

    demux.feedPacket(pkt);

    EXPECT_EQ(handler.tables_received, 1);
    EXPECT_EQ(handler.last_table_id, ts::TID_PAT);
}
