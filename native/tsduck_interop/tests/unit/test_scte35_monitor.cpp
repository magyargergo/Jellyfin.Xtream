// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#include <gtest/gtest.h>
#include <array>
#include <atomic>
#include <chrono>
#include <cstring>
#include <thread>
#include <vector>
#include "analysis/scte35_monitor.hpp"

using namespace tsduck_interop;
using namespace tsduck_interop::analysis;

// ============================================================================
// SCTE-35 Command Types
// ============================================================================

static constexpr uint8_t SCTE35_TABLE_ID = 0xFC;
static constexpr uint8_t SPLICE_NULL = 0x00;
static constexpr uint8_t SPLICE_SCHEDULE = 0x04;
static constexpr uint8_t SPLICE_INSERT = 0x05;
static constexpr uint8_t TIME_SIGNAL = 0x06;

// ============================================================================
// Helper: Compute CRC-32/MPEG-2 using TsDuck's utility
// ============================================================================

static uint32_t computeCrc32(const uint8_t* data, size_t length) {
    ts::CRC32 crc;
    crc.add(data, length);
    return crc.value();
}

// ============================================================================
// Helper: Create a splice_null section (command type 0x00)
// ============================================================================

static std::vector<uint8_t> createSpliceNullSection() {
    std::vector<uint8_t> section;

    // table_id (8 bits)
    section.push_back(SCTE35_TABLE_ID);

    // section_syntax_indicator (1) = 0
    // private_indicator (1) = 0
    // reserved (2) = 11
    // section_length (12) - will be calculated
    uint16_t section_length = 11 + 4;  // Fixed header (11) + CRC (4), no command data
    section.push_back(static_cast<uint8_t>(0x30 | ((section_length >> 8) & 0x0F)));
    section.push_back(static_cast<uint8_t>(section_length & 0xFF));

    // protocol_version (8 bits) = 0
    section.push_back(0x00);

    // encrypted_packet (1) = 0
    // encryption_algorithm (6) = 0
    // pts_adjustment (33 bits) = 0
    section.push_back(0x00);  // encrypted_packet=0, encryption_algorithm=0, pts_adjustment high 1 bit
    section.push_back(0x00);  // pts_adjustment bits 32-25
    section.push_back(0x00);  // pts_adjustment bits 24-17
    section.push_back(0x00);  // pts_adjustment bits 16-9
    section.push_back(0x00);  // pts_adjustment bits 8-1

    // cw_index (8 bits) = 0
    section.push_back(0x00);

    // tier (12 bits) = 0xFFF (all bits set)
    // splice_command_length (12 bits) = 0 for splice_null
    section.push_back(0xFF);  // tier high 8 bits
    section.push_back(0xF0);  // tier low 4 bits + splice_command_length high 4 bits
    section.push_back(0x00);  // splice_command_length low 8 bits

    // splice_command_type (8 bits) = 0x00 (splice_null)
    section.push_back(SPLICE_NULL);

    // No splice_command data for splice_null

    // descriptor_loop_length (16 bits) = 0
    section.push_back(0x00);
    section.push_back(0x00);

    // Compute and append CRC-32
    uint32_t crc = computeCrc32(section.data(), section.size());
    section.push_back(static_cast<uint8_t>((crc >> 24) & 0xFF));
    section.push_back(static_cast<uint8_t>((crc >> 16) & 0xFF));
    section.push_back(static_cast<uint8_t>((crc >> 8) & 0xFF));
    section.push_back(static_cast<uint8_t>(crc & 0xFF));

    return section;
}

// ============================================================================
// Helper: Create a splice_insert section (command type 0x05)
// ============================================================================

static std::vector<uint8_t> createSpliceInsertSection(
    uint32_t event_id,
    bool out_of_network,
    bool immediate,
    uint64_t pts_time = 0,
    uint64_t duration = 0)
{
    std::vector<uint8_t> section;

    // Build splice_insert command first to know its length
    std::vector<uint8_t> command;

    // splice_event_id (32 bits)
    command.push_back(static_cast<uint8_t>((event_id >> 24) & 0xFF));
    command.push_back(static_cast<uint8_t>((event_id >> 16) & 0xFF));
    command.push_back(static_cast<uint8_t>((event_id >> 8) & 0xFF));
    command.push_back(static_cast<uint8_t>(event_id & 0xFF));

    // splice_event_cancel_indicator (1) = 0
    // reserved (7) = 0x7F
    command.push_back(0x7F);

    // out_of_network_indicator (1)
    // program_splice_flag (1) = 1 (program splice, not component)
    // duration_flag (1)
    // splice_immediate_flag (1)
    // reserved (4) = 0xF
    uint8_t flags = 0x0F;  // reserved bits
    if (out_of_network) flags |= 0x80;
    flags |= 0x40;  // program_splice_flag = 1
    if (duration > 0) flags |= 0x20;  // duration_flag
    if (immediate) flags |= 0x10;  // splice_immediate_flag
    command.push_back(flags);

    // If not immediate, include splice_time
    if (!immediate) {
        // time_specified_flag (1) = 1
        // reserved (6) = 0x3F
        // pts_time (33 bits)
        if (pts_time > 0) {
            command.push_back(static_cast<uint8_t>(0xFE | ((pts_time >> 32) & 0x01)));
            command.push_back(static_cast<uint8_t>((pts_time >> 24) & 0xFF));
            command.push_back(static_cast<uint8_t>((pts_time >> 16) & 0xFF));
            command.push_back(static_cast<uint8_t>((pts_time >> 8) & 0xFF));
            command.push_back(static_cast<uint8_t>(pts_time & 0xFF));
        } else {
            // time_specified_flag = 0
            command.push_back(0x7F);  // reserved bits only
        }
    }

    // If duration_flag is set, include break_duration
    if (duration > 0) {
        // auto_return (1) = 1 (return to network after break)
        // reserved (6) = 0x3F
        // duration (33 bits)
        command.push_back(static_cast<uint8_t>(0xFE | ((duration >> 32) & 0x01)));
        command.push_back(static_cast<uint8_t>((duration >> 24) & 0xFF));
        command.push_back(static_cast<uint8_t>((duration >> 16) & 0xFF));
        command.push_back(static_cast<uint8_t>((duration >> 8) & 0xFF));
        command.push_back(static_cast<uint8_t>(duration & 0xFF));
    }

    // unique_program_id (16 bits) = 0
    command.push_back(0x00);
    command.push_back(0x00);

    // avail_num (8 bits) = 0
    command.push_back(0x00);

    // avails_expected (8 bits) = 0
    command.push_back(0x00);

    // Now build the full section
    uint16_t command_length = static_cast<uint16_t>(command.size());
    uint16_t section_length = 11 + command_length + 2 + 4;  // header + command + desc_loop_len + CRC

    // table_id
    section.push_back(SCTE35_TABLE_ID);

    // section_syntax_indicator=0, private_indicator=0, reserved=11, section_length
    section.push_back(static_cast<uint8_t>(0x30 | ((section_length >> 8) & 0x0F)));
    section.push_back(static_cast<uint8_t>(section_length & 0xFF));

    // protocol_version = 0
    section.push_back(0x00);

    // encrypted_packet=0, encryption_algorithm=0, pts_adjustment=0
    section.push_back(0x00);
    section.push_back(0x00);
    section.push_back(0x00);
    section.push_back(0x00);
    section.push_back(0x00);

    // cw_index = 0
    section.push_back(0x00);

    // tier (12 bits) = 0xFFF, splice_command_length (12 bits)
    section.push_back(0xFF);
    section.push_back(static_cast<uint8_t>(0xF0 | ((command_length >> 8) & 0x0F)));
    section.push_back(static_cast<uint8_t>(command_length & 0xFF));

    // splice_command_type
    section.push_back(SPLICE_INSERT);

    // splice_command (the actual command data)
    section.insert(section.end(), command.begin(), command.end());

    // descriptor_loop_length = 0
    section.push_back(0x00);
    section.push_back(0x00);

    // CRC-32
    uint32_t crc = computeCrc32(section.data(), section.size());
    section.push_back(static_cast<uint8_t>((crc >> 24) & 0xFF));
    section.push_back(static_cast<uint8_t>((crc >> 16) & 0xFF));
    section.push_back(static_cast<uint8_t>((crc >> 8) & 0xFF));
    section.push_back(static_cast<uint8_t>(crc & 0xFF));

    return section;
}

// ============================================================================
// Helper: Create a time_signal section (command type 0x06)
// ============================================================================

static std::vector<uint8_t> createTimeSignalSection(uint64_t pts_time) {
    std::vector<uint8_t> section;

    // Build time_signal command: just a splice_time
    std::vector<uint8_t> command;

    // time_specified_flag (1) = 1
    // reserved (6) = 0x3F
    // pts_time (33 bits)
    command.push_back(static_cast<uint8_t>(0xFE | ((pts_time >> 32) & 0x01)));
    command.push_back(static_cast<uint8_t>((pts_time >> 24) & 0xFF));
    command.push_back(static_cast<uint8_t>((pts_time >> 16) & 0xFF));
    command.push_back(static_cast<uint8_t>((pts_time >> 8) & 0xFF));
    command.push_back(static_cast<uint8_t>(pts_time & 0xFF));

    uint16_t command_length = static_cast<uint16_t>(command.size());
    uint16_t section_length = 11 + command_length + 2 + 4;

    // table_id
    section.push_back(SCTE35_TABLE_ID);

    // section_syntax_indicator=0, private_indicator=0, reserved=11, section_length
    section.push_back(static_cast<uint8_t>(0x30 | ((section_length >> 8) & 0x0F)));
    section.push_back(static_cast<uint8_t>(section_length & 0xFF));

    // protocol_version = 0
    section.push_back(0x00);

    // encrypted_packet=0, encryption_algorithm=0, pts_adjustment=0
    section.push_back(0x00);
    section.push_back(0x00);
    section.push_back(0x00);
    section.push_back(0x00);
    section.push_back(0x00);

    // cw_index = 0
    section.push_back(0x00);

    // tier (12 bits) = 0xFFF, splice_command_length (12 bits)
    section.push_back(0xFF);
    section.push_back(static_cast<uint8_t>(0xF0 | ((command_length >> 8) & 0x0F)));
    section.push_back(static_cast<uint8_t>(command_length & 0xFF));

    // splice_command_type
    section.push_back(TIME_SIGNAL);

    // splice_command
    section.insert(section.end(), command.begin(), command.end());

    // descriptor_loop_length = 0
    section.push_back(0x00);
    section.push_back(0x00);

    // CRC-32
    uint32_t crc = computeCrc32(section.data(), section.size());
    section.push_back(static_cast<uint8_t>((crc >> 24) & 0xFF));
    section.push_back(static_cast<uint8_t>((crc >> 16) & 0xFF));
    section.push_back(static_cast<uint8_t>((crc >> 8) & 0xFF));
    section.push_back(static_cast<uint8_t>(crc & 0xFF));

    return section;
}

// ============================================================================
// Helper: Create a TS packet with SCTE-35 section
// ============================================================================

static void createScte35Packet(uint8_t* packet, uint16_t pid, uint8_t cc,
                                const uint8_t* section_data, size_t section_len) {
    std::memset(packet, 0xFF, ts::PKT_SIZE);

    // Sync byte
    packet[0] = ts::SYNC_BYTE;

    // PID and PUSI=1
    packet[1] = static_cast<uint8_t>(0x40 | ((pid >> 8) & 0x1F));
    packet[2] = static_cast<uint8_t>(pid & 0xFF);

    // Adaptation field control = 01 (payload only), CC
    packet[3] = static_cast<uint8_t>(0x10 | (cc & 0x0F));

    // Pointer field = 0 (section starts immediately after)
    packet[4] = 0x00;

    // Copy section data
    size_t copy_len = std::min(section_len, size_t(183));
    if (section_data && copy_len > 0) {
        std::memcpy(&packet[5], section_data, copy_len);
    }
}

// ============================================================================
// Helper: Wrap a PSI section into a TS packet
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

class Scte35MonitorTest : public ::testing::Test {
protected:
    ts::DuckContext duck;
    std::unique_ptr<Scte35Monitor> monitor;

    void SetUp() override {
        monitor = std::make_unique<Scte35Monitor>(duck);
    }

    void TearDown() override {
        monitor.reset();
    }
};

// ============================================================================
// Basic Functionality Tests
// ============================================================================

TEST_F(Scte35MonitorTest, InitialStateIsInContent) {
    EXPECT_EQ(monitor->get_splice_state(), SpliceState::InContent);
    EXPECT_FALSE(monitor->is_in_ad_break());
    EXPECT_EQ(monitor->get_event_count(), 0);
}

TEST_F(Scte35MonitorTest, InitialStateHasNoEvents) {
    Scte35EventNative events[10];
    int32_t count = monitor->get_events(events, 10);
    EXPECT_EQ(count, 0);
}

TEST_F(Scte35MonitorTest, InitialStateHasNoCurrentEvent) {
    Scte35EventNative evt;
    EXPECT_FALSE(monitor->get_current_event(&evt));
}

TEST_F(Scte35MonitorTest, ResetClearsEvents) {
    // Register a PID and process some data
    monitor->add_scte35_pid(500);

    auto section = createSpliceInsertSection(1, true, true);
    auto pkt = wrapSectionInPacket(500, section);
    monitor->feed_packet(pkt, 1000);

    EXPECT_GT(monitor->get_event_count(), 0);

    monitor->reset();

    EXPECT_EQ(monitor->get_event_count(), 0);
    EXPECT_EQ(monitor->get_splice_state(), SpliceState::InContent);
    EXPECT_FALSE(monitor->is_in_ad_break());

    // PID should still be registered after reset()
    EXPECT_TRUE(monitor->is_scte35_pid(500));
}

TEST_F(Scte35MonitorTest, ResetFullClearsPids) {
    monitor->add_scte35_pid(500);
    monitor->add_scte35_pid(501);
    EXPECT_TRUE(monitor->is_scte35_pid(500));
    EXPECT_TRUE(monitor->is_scte35_pid(501));

    monitor->reset_full();

    EXPECT_FALSE(monitor->is_scte35_pid(500));
    EXPECT_FALSE(monitor->is_scte35_pid(501));
    EXPECT_EQ(monitor->scte35_pid_count.load(), 0);
}

// ============================================================================
// PID Management Tests
// ============================================================================

TEST_F(Scte35MonitorTest, AddScte35PidRegistration) {
    EXPECT_FALSE(monitor->is_scte35_pid(500));

    monitor->add_scte35_pid(500);

    EXPECT_TRUE(monitor->is_scte35_pid(500));
    EXPECT_EQ(monitor->scte35_pid_count.load(), 1);
}

TEST_F(Scte35MonitorTest, IsScte35PidDetection) {
    monitor->add_scte35_pid(500);
    monitor->add_scte35_pid(600);

    EXPECT_TRUE(monitor->is_scte35_pid(500));
    EXPECT_TRUE(monitor->is_scte35_pid(600));
    EXPECT_FALSE(monitor->is_scte35_pid(700));
    EXPECT_FALSE(monitor->is_scte35_pid(0));
}

TEST_F(Scte35MonitorTest, MultipleSCTE35Pids) {
    monitor->add_scte35_pid(500);
    monitor->add_scte35_pid(501);
    monitor->add_scte35_pid(502);

    EXPECT_EQ(monitor->scte35_pid_count.load(), 3);
    EXPECT_TRUE(monitor->is_scte35_pid(500));
    EXPECT_TRUE(monitor->is_scte35_pid(501));
    EXPECT_TRUE(monitor->is_scte35_pid(502));
}

TEST_F(Scte35MonitorTest, DuplicatePidNotAdded) {
    monitor->add_scte35_pid(500);
    monitor->add_scte35_pid(500);  // duplicate
    monitor->add_scte35_pid(500);  // duplicate again

    EXPECT_EQ(monitor->scte35_pid_count.load(), 1);
}

TEST_F(Scte35MonitorTest, NullPidRejected) {
    monitor->add_scte35_pid(ts::PID_NULL);  // 0x1FFF
    EXPECT_EQ(monitor->scte35_pid_count.load(), 0);
    EXPECT_FALSE(monitor->is_scte35_pid(ts::PID_NULL));
}

TEST_F(Scte35MonitorTest, InvalidHighPidRejected) {
    monitor->add_scte35_pid(0x2000);  // Above valid range
    EXPECT_EQ(monitor->scte35_pid_count.load(), 0);
}

// ============================================================================
// SCTE-35 Section Parsing Tests - splice_insert
// ============================================================================

TEST_F(Scte35MonitorTest, ParsesSpliceInsertImmediate) {
    monitor->add_scte35_pid(500);

    auto section = createSpliceInsertSection(12345, true, true);
    auto pkt = wrapSectionInPacket(500, section);
    monitor->feed_packet(pkt, 1000);

    EXPECT_EQ(monitor->get_event_count(), 1);

    Scte35EventNative evt;
    EXPECT_TRUE(monitor->get_current_event(&evt));
    EXPECT_EQ(evt.splice_event_id, 12345u);
    EXPECT_EQ(evt.out_of_network, 1);
    EXPECT_EQ(evt.splice_immediate, 1);
    EXPECT_EQ(evt.splice_command_type, SPLICE_INSERT);
    EXPECT_EQ(evt.scte35_pid, 500);
}

TEST_F(Scte35MonitorTest, ParsesSpliceInsertWithPtsTime) {
    monitor->add_scte35_pid(500);

    uint64_t pts_time = 900000;  // 10 seconds at 90kHz
    auto section = createSpliceInsertSection(100, true, false, pts_time);
    auto pkt = wrapSectionInPacket(500, section);
    monitor->feed_packet(pkt, 2000);

    Scte35EventNative evt;
    EXPECT_TRUE(monitor->get_current_event(&evt));
    EXPECT_EQ(evt.splice_event_id, 100u);
    EXPECT_EQ(evt.splice_immediate, 0);
    EXPECT_EQ(evt.pts_time, pts_time);
}

TEST_F(Scte35MonitorTest, ParsesSpliceInsertWithDuration) {
    monitor->add_scte35_pid(500);

    uint64_t pts_time = 900000;
    uint64_t duration = 2700000;  // 30 seconds at 90kHz
    auto section = createSpliceInsertSection(200, true, false, pts_time, duration);
    auto pkt = wrapSectionInPacket(500, section);
    monitor->feed_packet(pkt, 3000);

    Scte35EventNative evt;
    EXPECT_TRUE(monitor->get_current_event(&evt));
    EXPECT_EQ(evt.splice_event_id, 200u);
    EXPECT_EQ(evt.duration_pts, duration);
}

TEST_F(Scte35MonitorTest, ParsesSpliceInsertOutOfNetwork) {
    monitor->add_scte35_pid(500);

    // out_of_network = true
    auto section = createSpliceInsertSection(300, true, true);
    auto pkt = wrapSectionInPacket(500, section);
    monitor->feed_packet(pkt, 4000);

    Scte35EventNative evt;
    EXPECT_TRUE(monitor->get_current_event(&evt));
    EXPECT_EQ(evt.out_of_network, 1);
}

TEST_F(Scte35MonitorTest, ParsesSpliceInsertReturnToNetwork) {
    monitor->add_scte35_pid(500);

    // out_of_network = false (return to content)
    auto section = createSpliceInsertSection(400, false, true);
    auto pkt = wrapSectionInPacket(500, section);
    monitor->feed_packet(pkt, 5000);

    Scte35EventNative evt;
    EXPECT_TRUE(monitor->get_current_event(&evt));
    EXPECT_EQ(evt.out_of_network, 0);
}

// ============================================================================
// SCTE-35 Section Parsing Tests - time_signal
// ============================================================================

TEST_F(Scte35MonitorTest, ParsesTimeSignal) {
    monitor->add_scte35_pid(500);

    uint64_t pts_time = 1800000;  // 20 seconds at 90kHz
    auto section = createTimeSignalSection(pts_time);
    auto pkt = wrapSectionInPacket(500, section);
    monitor->feed_packet(pkt, 6000);

    EXPECT_EQ(monitor->get_event_count(), 1);

    Scte35EventNative evt;
    EXPECT_TRUE(monitor->get_current_event(&evt));
    EXPECT_EQ(evt.splice_command_type, TIME_SIGNAL);
    EXPECT_EQ(evt.pts_time, pts_time);
}

// ============================================================================
// SCTE-35 Section Parsing Tests - splice_null
// ============================================================================

TEST_F(Scte35MonitorTest, SpliceNullIsHeartbeat) {
    monitor->add_scte35_pid(500);

    auto section = createSpliceNullSection();
    auto pkt = wrapSectionInPacket(500, section);
    monitor->feed_packet(pkt, 7000);

    // splice_null is a heartbeat, should not generate an event
    EXPECT_EQ(monitor->get_event_count(), 0);
}

// ============================================================================
// State Transition Tests
// ============================================================================

TEST_F(Scte35MonitorTest, TransitionToInBreak) {
    monitor->add_scte35_pid(500);

    EXPECT_EQ(monitor->get_splice_state(), SpliceState::InContent);
    EXPECT_FALSE(monitor->is_in_ad_break());

    // out_of_network = true -> InBreak
    auto section = createSpliceInsertSection(1, true, true);
    auto pkt = wrapSectionInPacket(500, section);
    monitor->feed_packet(pkt, 1000);

    EXPECT_EQ(monitor->get_splice_state(), SpliceState::InBreak);
    EXPECT_TRUE(monitor->is_in_ad_break());
}

TEST_F(Scte35MonitorTest, TransitionBackToContent) {
    monitor->add_scte35_pid(500);

    // First go to break
    auto section1 = createSpliceInsertSection(1, true, true);
    auto pkt1 = wrapSectionInPacket(500, section1, 0);
    monitor->feed_packet(pkt1, 1000);
    EXPECT_TRUE(monitor->is_in_ad_break());

    // Then return to content (out_of_network = false)
    auto section2 = createSpliceInsertSection(2, false, true);
    auto pkt2 = wrapSectionInPacket(500, section2, 1);
    monitor->feed_packet(pkt2, 2000);

    EXPECT_EQ(monitor->get_splice_state(), SpliceState::InContent);
    EXPECT_FALSE(monitor->is_in_ad_break());
}

TEST_F(Scte35MonitorTest, IsInAdBreakAccuracy) {
    monitor->add_scte35_pid(500);

    // Initial state
    EXPECT_FALSE(monitor->is_in_ad_break());

    // Enter break
    auto section = createSpliceInsertSection(1, true, true);
    auto pkt = wrapSectionInPacket(500, section);
    monitor->feed_packet(pkt, 1000);
    EXPECT_TRUE(monitor->is_in_ad_break());

    // Exit break
    auto section2 = createSpliceInsertSection(2, false, true);
    auto pkt2 = wrapSectionInPacket(500, section2, 1);
    monitor->feed_packet(pkt2, 2000);
    EXPECT_FALSE(monitor->is_in_ad_break());
}

// ============================================================================
// Event Ring Buffer Tests
// ============================================================================

TEST_F(Scte35MonitorTest, GetEventsReturnsPendingEvents) {
    monitor->add_scte35_pid(500);

    // Add 3 events
    for (uint32_t i = 1; i <= 3; ++i) {
        auto section = createSpliceInsertSection(i * 100, true, true);
        auto pkt = wrapSectionInPacket(500, section, static_cast<uint8_t>(i));
        monitor->feed_packet(pkt, i * 1000);
    }

    EXPECT_EQ(monitor->get_event_count(), 3);

    Scte35EventNative events[10];
    int32_t count = monitor->get_events(events, 10);
    EXPECT_EQ(count, 3);

    // Events should be in chronological order (oldest first)
    EXPECT_EQ(events[0].splice_event_id, 100u);
    EXPECT_EQ(events[1].splice_event_id, 200u);
    EXPECT_EQ(events[2].splice_event_id, 300u);
}

TEST_F(Scte35MonitorTest, GetCurrentEventReturnsMostRecent) {
    monitor->add_scte35_pid(500);

    // Add multiple events
    for (uint32_t i = 1; i <= 5; ++i) {
        auto section = createSpliceInsertSection(i * 10, true, true);
        auto pkt = wrapSectionInPacket(500, section, static_cast<uint8_t>(i));
        monitor->feed_packet(pkt, i * 1000);
    }

    Scte35EventNative evt;
    EXPECT_TRUE(monitor->get_current_event(&evt));
    EXPECT_EQ(evt.splice_event_id, 50u);  // Most recent (5 * 10)
}

TEST_F(Scte35MonitorTest, GetEventCountIncrementsCorrectly) {
    monitor->add_scte35_pid(500);

    EXPECT_EQ(monitor->get_event_count(), 0);

    for (int i = 1; i <= 10; ++i) {
        auto section = createSpliceInsertSection(static_cast<uint32_t>(i), true, true);
        auto pkt = wrapSectionInPacket(500, section, static_cast<uint8_t>(i));
        monitor->feed_packet(pkt, i * 1000);
        EXPECT_EQ(monitor->get_event_count(), i);
    }
}

TEST_F(Scte35MonitorTest, BufferOverflowBehavior) {
    monitor->add_scte35_pid(500);

    // Fill beyond buffer size (SCTE35_EVENT_BUFFER_SIZE = 32)
    const size_t overflow_count = SCTE35_EVENT_BUFFER_SIZE + 10;

    for (size_t i = 1; i <= overflow_count; ++i) {
        auto section = createSpliceInsertSection(static_cast<uint32_t>(i), true, true);
        auto pkt = wrapSectionInPacket(500, section, static_cast<uint8_t>(i & 0x0F));
        monitor->feed_packet(pkt, static_cast<int64_t>(i * 1000));
    }

    // Total events counted should be all events
    EXPECT_EQ(monitor->get_event_count(), static_cast<int64_t>(overflow_count));

    // But retrievable events are limited to buffer size
    Scte35EventNative events[SCTE35_EVENT_BUFFER_SIZE];
    int32_t count = monitor->get_events(events, SCTE35_EVENT_BUFFER_SIZE);
    EXPECT_EQ(count, static_cast<int32_t>(SCTE35_EVENT_BUFFER_SIZE));

    // Oldest events should be overwritten - newest events preserved
    // The most recent events should be (overflow_count - SCTE35_EVENT_BUFFER_SIZE + 1) to overflow_count
    uint32_t expected_oldest = static_cast<uint32_t>(overflow_count - SCTE35_EVENT_BUFFER_SIZE + 1);
    EXPECT_EQ(events[0].splice_event_id, expected_oldest);
    EXPECT_EQ(events[SCTE35_EVENT_BUFFER_SIZE - 1].splice_event_id, static_cast<uint32_t>(overflow_count));
}

TEST_F(Scte35MonitorTest, GetEventsNullPointerReturnsZero) {
    monitor->add_scte35_pid(500);

    auto section = createSpliceInsertSection(1, true, true);
    auto pkt = wrapSectionInPacket(500, section);
    monitor->feed_packet(pkt, 1000);

    EXPECT_EQ(monitor->get_events(nullptr, 10), 0);
}

TEST_F(Scte35MonitorTest, GetEventsZeroMaxReturnsZero) {
    monitor->add_scte35_pid(500);

    auto section = createSpliceInsertSection(1, true, true);
    auto pkt = wrapSectionInPacket(500, section);
    monitor->feed_packet(pkt, 1000);

    Scte35EventNative events[10];
    EXPECT_EQ(monitor->get_events(events, 0), 0);
}

TEST_F(Scte35MonitorTest, GetCurrentEventNullPointerReturnsFalse) {
    monitor->add_scte35_pid(500);

    auto section = createSpliceInsertSection(1, true, true);
    auto pkt = wrapSectionInPacket(500, section);
    monitor->feed_packet(pkt, 1000);

    EXPECT_FALSE(monitor->get_current_event(nullptr));
}

// ============================================================================
// Edge Case Tests
// ============================================================================

TEST_F(Scte35MonitorTest, InvalidSectionDataDoesNotCrash) {
    monitor->add_scte35_pid(500);

    // Create a packet with garbage data
    ts::TSPacket pkt;
    std::memset(&pkt, 0x00, ts::PKT_SIZE);
    pkt.b[0] = ts::SYNC_BYTE;
    pkt.b[1] = static_cast<uint8_t>(0x40 | ((500 >> 8) & 0x1F));
    pkt.b[2] = static_cast<uint8_t>(500 & 0xFF);
    pkt.b[3] = 0x10;
    pkt.b[4] = 0x00;  // pointer field

    // Fill with random-ish data but table_id = 0xFC
    pkt.b[5] = SCTE35_TABLE_ID;
    for (int i = 6; i < ts::PKT_SIZE; ++i) {
        pkt.b[i] = static_cast<uint8_t>(i * 17);  // pseudo-random
    }

    // Should not crash
    EXPECT_NO_THROW(monitor->feed_packet(pkt, 1000));

    // Invalid section should be rejected (bad CRC, malformed)
    // Event count may be 0 if section is rejected
}

TEST_F(Scte35MonitorTest, EmptyPacketHandled) {
    monitor->add_scte35_pid(500);

    ts::TSPacket pkt;
    std::memset(&pkt, 0xFF, ts::PKT_SIZE);
    pkt.b[0] = ts::SYNC_BYTE;
    pkt.b[1] = static_cast<uint8_t>(0x40 | ((500 >> 8) & 0x1F));
    pkt.b[2] = static_cast<uint8_t>(500 & 0xFF);
    pkt.b[3] = 0x10;

    // Should not crash
    EXPECT_NO_THROW(monitor->feed_packet(pkt, 1000));
}

TEST_F(Scte35MonitorTest, PacketsOnNonScte35PidIgnored) {
    monitor->add_scte35_pid(500);

    // Create a valid SCTE-35 section but on a different PID
    auto section = createSpliceInsertSection(999, true, true);
    auto pkt = wrapSectionInPacket(600, section);  // PID 600 not registered

    monitor->feed_packet(pkt, 1000);

    // Should be ignored - no events
    EXPECT_EQ(monitor->get_event_count(), 0);
}

TEST_F(Scte35MonitorTest, NonScte35TableIdIgnored) {
    monitor->add_scte35_pid(500);

    // Create a section with wrong table_id (e.g., PAT = 0x00)
    std::vector<uint8_t> section;
    section.push_back(0x00);  // table_id = PAT, not SCTE-35

    auto pkt = wrapSectionInPacket(500, section);
    monitor->feed_packet(pkt, 1000);

    // Should not generate an event
    EXPECT_EQ(monitor->get_event_count(), 0);
}

TEST_F(Scte35MonitorTest, InvalidCrcSectionIgnored) {
    monitor->add_scte35_pid(500);

    auto section = createSpliceInsertSection(123, true, true);
    // Corrupt the CRC
    if (!section.empty()) {
        section[section.size() - 1] ^= 0xFF;
    }

    auto pkt = wrapSectionInPacket(500, section);
    monitor->feed_packet(pkt, 1000);

    // CRC error should cause section to be rejected
    EXPECT_EQ(monitor->get_event_count(), 0);
}

TEST_F(Scte35MonitorTest, MultipleScte35PidsProcessedIndependently) {
    monitor->add_scte35_pid(500);
    monitor->add_scte35_pid(600);

    // Send event on PID 500
    auto section1 = createSpliceInsertSection(100, true, true);
    auto pkt1 = wrapSectionInPacket(500, section1, 0);
    monitor->feed_packet(pkt1, 1000);

    // Send event on PID 600
    auto section2 = createSpliceInsertSection(200, true, true);
    auto pkt2 = wrapSectionInPacket(600, section2, 0);
    monitor->feed_packet(pkt2, 2000);

    EXPECT_EQ(monitor->get_event_count(), 2);

    Scte35EventNative events[10];
    int32_t count = monitor->get_events(events, 10);
    EXPECT_EQ(count, 2);

    // Check PIDs are recorded correctly
    EXPECT_EQ(events[0].scte35_pid, 500);
    EXPECT_EQ(events[0].splice_event_id, 100u);
    EXPECT_EQ(events[1].scte35_pid, 600);
    EXPECT_EQ(events[1].splice_event_id, 200u);
}

TEST_F(Scte35MonitorTest, PacketIndexRecordedCorrectly) {
    monitor->add_scte35_pid(500);

    auto section = createSpliceInsertSection(1, true, true);
    auto pkt = wrapSectionInPacket(500, section);
    monitor->feed_packet(pkt, 12345678);

    Scte35EventNative evt;
    EXPECT_TRUE(monitor->get_current_event(&evt));
    EXPECT_EQ(evt.packet_index, 12345678);
}

// ============================================================================
// Concurrent Access Tests (basic thread safety verification)
// ============================================================================

TEST_F(Scte35MonitorTest, ConcurrentReadWhileWriting) {
    monitor->add_scte35_pid(500);

    std::atomic<bool> stop{false};
    std::atomic<int> read_count{0};

    // Reader thread
    std::thread reader([&]() {
        while (!stop.load(std::memory_order_acquire)) {
            Scte35EventNative events[10];
            (void)monitor->get_events(events, 10);
            (void)monitor->is_in_ad_break();
            (void)monitor->get_event_count();
            read_count.fetch_add(1, std::memory_order_relaxed);
        }
    });

    // Writer thread (feed packets)
    std::thread writer([&]() {
        for (int i = 0; i < 1000; ++i) {
            auto section = createSpliceInsertSection(static_cast<uint32_t>(i), (i % 2) == 0, true);
            auto pkt = wrapSectionInPacket(500, section, static_cast<uint8_t>(i & 0x0F));
            monitor->feed_packet(pkt, i);
        }
    });

    writer.join();
    std::this_thread::sleep_for(std::chrono::milliseconds(10));
    stop.store(true, std::memory_order_release);
    reader.join();

    EXPECT_GT(read_count.load(), 0);
    EXPECT_EQ(monitor->get_event_count(), 1000);
}

int main(int argc, char **argv) {
    ::testing::InitGoogleTest(&argc, argv);
    return RUN_ALL_TESTS();
}
