// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#include <gtest/gtest.h>
#include <cstring>
#include "analysis/tr101290.hpp"

using namespace tsduck_interop;
using namespace tsduck_interop::analysis;

// ============================================================================
// Helper: Build a minimal TS packet with given PID and flags
// ============================================================================

static ts::TSPacket makeValidPacket(uint16_t pid = 0x100) {
    ts::TSPacket pkt;
    std::memset(&pkt, 0xFF, ts::PKT_SIZE);
    pkt.b[0] = ts::SYNC_BYTE;
    pkt.b[1] = static_cast<uint8_t>((pid >> 8) & 0x1F);
    pkt.b[2] = static_cast<uint8_t>(pid & 0xFF);
    pkt.b[3] = 0x10;  // Payload only, CC=0
    // Fill payload with zeros
    std::memset(&pkt.b[4], 0, ts::PKT_SIZE - 4);
    return pkt;
}

static ts::TSPacket makePacketWithTEI(uint16_t pid = 0x100) {
    ts::TSPacket pkt = makeValidPacket(pid);
    pkt.b[1] |= 0x80;  // Set TEI bit
    return pkt;
}

static ts::TSPacket makePacketWithPCR(uint16_t pid, uint64_t pcr_value) {
    ts::TSPacket pkt;
    std::memset(&pkt, 0, ts::PKT_SIZE);
    pkt.b[0] = ts::SYNC_BYTE;
    pkt.b[1] = static_cast<uint8_t>((pid >> 8) & 0x1F);
    pkt.b[2] = static_cast<uint8_t>(pid & 0xFF);
    // Adaptation field + payload
    pkt.b[3] = 0x30;
    // Adaptation field length (covers PCR: 6 bytes + flags byte = 7)
    pkt.b[4] = 7;
    // Adaptation field flags: PCR flag set
    pkt.b[5] = 0x10;
    // PCR (6 bytes): base(33) + reserved(6) + extension(9)
    uint64_t pcr_base = pcr_value / 300;
    uint16_t pcr_ext = static_cast<uint16_t>(pcr_value % 300);
    pkt.b[6] = static_cast<uint8_t>((pcr_base >> 25) & 0xFF);
    pkt.b[7] = static_cast<uint8_t>((pcr_base >> 17) & 0xFF);
    pkt.b[8] = static_cast<uint8_t>((pcr_base >> 9) & 0xFF);
    pkt.b[9] = static_cast<uint8_t>((pcr_base >> 1) & 0xFF);
    pkt.b[10] = static_cast<uint8_t>(
        ((pcr_base & 0x01) << 7) | 0x7E | ((pcr_ext >> 8) & 0x01));
    pkt.b[11] = static_cast<uint8_t>(pcr_ext & 0xFF);
    return pkt;
}

static ts::TSPacket makePacketWithDiscontinuity(uint16_t pid) {
    ts::TSPacket pkt;
    std::memset(&pkt, 0, ts::PKT_SIZE);
    pkt.b[0] = ts::SYNC_BYTE;
    pkt.b[1] = static_cast<uint8_t>((pid >> 8) & 0x1F);
    pkt.b[2] = static_cast<uint8_t>(pid & 0xFF);
    pkt.b[3] = 0x30;  // AF + payload
    pkt.b[4] = 1;     // AF length
    pkt.b[5] = 0x80;  // Discontinuity indicator set
    return pkt;
}

// ============================================================================
// Test Fixture
// ============================================================================

class Tr101290Test : public ::testing::Test {
protected:
    Tr101290Monitor monitor;

    void SetUp() override {
        monitor.reset();
    }
};

// ============================================================================
// Priority 1: Sync Byte Error / Sync Loss
// ============================================================================

TEST_F(Tr101290Test, NoSyncErrorOnValidPacket) {
    monitor.check_sync(true);

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};
    monitor.get_counters(&p1, &p2);

    EXPECT_EQ(p1.sync_byte_error, 0);
    EXPECT_EQ(p1.sync_loss, 0);
}

TEST_F(Tr101290Test, DetectsSyncByteError) {
    monitor.check_sync(false);

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};
    monitor.get_counters(&p1, &p2);

    EXPECT_EQ(p1.sync_byte_error, 1);
    EXPECT_EQ(p1.sync_loss, 0);  // Need 2+ consecutive
}

TEST_F(Tr101290Test, DetectsSyncLossOnTwoConsecutiveErrors) {
    monitor.check_sync(false);
    monitor.check_sync(false);

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};
    monitor.get_counters(&p1, &p2);

    EXPECT_EQ(p1.sync_byte_error, 2);
    EXPECT_EQ(p1.sync_loss, 1);  // Triggered on second consecutive error
}

TEST_F(Tr101290Test, SyncLossResetsAfterValidPacket) {
    monitor.check_sync(false);
    monitor.check_sync(true);   // Resets consecutive counter
    monitor.check_sync(false);  // New single error

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};
    monitor.get_counters(&p1, &p2);

    EXPECT_EQ(p1.sync_byte_error, 2);
    EXPECT_EQ(p1.sync_loss, 0);  // No consecutive pair
}

// ============================================================================
// Priority 1: PAT Error
// ============================================================================

TEST_F(Tr101290Test, DetectsPatTimeout) {
    // At 5 Mbps: 500ms = 5000000*0.5/8 = 312500 bytes = ~1662 packets
    monitor.estimated_bitrate_bps.store(5000000, std::memory_order_relaxed);

    int64_t start_idx = 1000;
    monitor.on_pat_received(start_idx);

    // Check at ~510ms worth of packets later (exceeds 500ms limit)
    int64_t check_idx = start_idx + 1700;  // ~509ms at 5Mbps
    monitor.check_pat_timeout(check_idx);

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};
    monitor.get_counters(&p1, &p2);

    EXPECT_EQ(p1.pat_error, 1);
}

TEST_F(Tr101290Test, NoPatErrorWithinInterval) {
    // At 5 Mbps: 500ms = ~1662 packets
    monitor.estimated_bitrate_bps.store(5000000, std::memory_order_relaxed);

    int64_t start_idx = 1000;
    monitor.on_pat_received(start_idx);

    // Check at ~400ms worth of packets (within 500ms limit)
    int64_t check_idx = start_idx + 1330;  // ~398ms at 5Mbps
    monitor.check_pat_timeout(check_idx);

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};
    monitor.get_counters(&p1, &p2);

    EXPECT_EQ(p1.pat_error, 0);
}

TEST_F(Tr101290Test, NoPatErrorBeforeBaselineEstablished) {
    // Without calling onPatReceived, the baseline is not set
    monitor.estimated_bitrate_bps.store(5000000, std::memory_order_relaxed);
    monitor.check_pat_timeout(100000);

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};
    monitor.get_counters(&p1, &p2);

    EXPECT_EQ(p1.pat_error, 0);  // Startup grace period
}

TEST_F(Tr101290Test, NoPatErrorWithoutBitrate) {
    // Without bitrate, PAT timeout cannot be computed
    int64_t start_idx = 1000;
    monitor.on_pat_received(start_idx);
    monitor.check_pat_timeout(start_idx + 100000);

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};
    monitor.get_counters(&p1, &p2);

    EXPECT_EQ(p1.pat_error, 0);  // Can't compute without bitrate
}

// ============================================================================
// Priority 1: PMT Error and PID Error
// ============================================================================

TEST_F(Tr101290Test, DetectsPmtTimeout) {
    monitor.on_pmt_timeout();

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};
    monitor.get_counters(&p1, &p2);

    EXPECT_EQ(p1.pmt_error, 1);
}

TEST_F(Tr101290Test, DetectsPidTimeout) {
    monitor.on_pid_timeout();

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};
    monitor.get_counters(&p1, &p2);

    EXPECT_EQ(p1.pid_error, 1);
}

// ============================================================================
// Priority 2: Transport Error Indicator
// ============================================================================

TEST_F(Tr101290Test, DetectsTransportError) {
    ts::TSPacket pkt = makePacketWithTEI(0x100);

    monitor.check_transport_error(pkt);

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};
    monitor.get_counters(&p1, &p2);

    EXPECT_EQ(p2.transport_error, 1);
}

TEST_F(Tr101290Test, NoTransportErrorOnNormalPacket) {
    ts::TSPacket pkt = makeValidPacket(0x100);

    monitor.check_transport_error(pkt);

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};
    monitor.get_counters(&p1, &p2);

    EXPECT_EQ(p2.transport_error, 0);
}

// ============================================================================
// Priority 2: CRC Error
// ============================================================================

TEST_F(Tr101290Test, DetectsCrcError) {
    monitor.on_crc_error();
    monitor.on_crc_error();

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};
    monitor.get_counters(&p1, &p2);

    EXPECT_EQ(p2.crc_error, 2);
}

// ============================================================================
// Priority 2: PCR Repetition Error
// ============================================================================

TEST_F(Tr101290Test, DetectsPcrRepetitionError) {
    uint16_t pid = 0x100;
    // At 5 Mbps: 40ms = 5000000*0.04/8 = 25000 bytes = ~133 packets
    monitor.estimated_bitrate_bps.store(5000000, std::memory_order_relaxed);

    int64_t idx = 1000;
    // First PCR initializes
    monitor.check_pcr_repetition(pid, idx);

    // Second PCR at 150 packets later (~45ms, exceeds 40ms limit)
    idx += 150;
    monitor.check_pcr_repetition(pid, idx);

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};
    monitor.get_counters(&p1, &p2);

    EXPECT_EQ(p2.pcr_repetition_error, 1);
}

TEST_F(Tr101290Test, NoPcrRepetitionErrorWithinLimit) {
    uint16_t pid = 0x100;
    // At 5 Mbps: 40ms = ~133 packets
    monitor.estimated_bitrate_bps.store(5000000, std::memory_order_relaxed);

    int64_t idx = 1000;
    monitor.check_pcr_repetition(pid, idx);

    // Second PCR at 100 packets later (~30ms, within 40ms limit)
    idx += 100;
    monitor.check_pcr_repetition(pid, idx);

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};
    monitor.get_counters(&p1, &p2);

    EXPECT_EQ(p2.pcr_repetition_error, 0);
}

TEST_F(Tr101290Test, NoPcrRepetitionErrorWithoutBitrate) {
    uint16_t pid = 0x100;
    // No bitrate set — should skip check

    monitor.check_pcr_repetition(pid, 1000);
    monitor.check_pcr_repetition(pid, 100000);  // Large gap but no bitrate

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};
    monitor.get_counters(&p1, &p2);

    EXPECT_EQ(p2.pcr_repetition_error, 0);
}

// ============================================================================
// Priority 2: PCR Discontinuity Error
// ============================================================================

TEST_F(Tr101290Test, DetectsPcrDiscontinuityJump) {
    uint16_t pid = 0x100;
    uint64_t pcr = 27000000;  // 1 second at 27MHz

    // First PCR initializes
    ts::TSPacket pkt1 = makePacketWithPCR(pid, pcr);
    monitor.check_pcr_discontinuity(pkt1, pid, pcr);

    // Large jump (200ms = 5,400,000 ticks, exceeds 100ms threshold)
    uint64_t pcr2 = pcr + 5400000;
    ts::TSPacket pkt2 = makePacketWithPCR(pid, pcr2);
    monitor.check_pcr_discontinuity(pkt2, pid, pcr2);

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};
    monitor.get_counters(&p1, &p2);

    EXPECT_EQ(p2.pcr_discontinuity_error, 1);
}

TEST_F(Tr101290Test, AllowsPcrDiscontinuityWithIndicator) {
    uint16_t pid = 0x100;
    uint64_t pcr = 27000000;

    // First PCR
    ts::TSPacket pkt1 = makePacketWithPCR(pid, pcr);
    monitor.check_pcr_discontinuity(pkt1, pid, pcr);

    // Large jump WITH discontinuity indicator — should NOT cause error
    uint64_t pcr2 = pcr + 5400000;
    ts::TSPacket pkt2 = makePacketWithDiscontinuity(pid);
    // Set PCR in packet with discontinuity indicator already set
    monitor.check_pcr_discontinuity(pkt2, pid, pcr2);

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};
    monitor.get_counters(&p1, &p2);

    EXPECT_EQ(p2.pcr_discontinuity_error, 0);
}

TEST_F(Tr101290Test, NoPcrDiscontinuityForSmallDelta) {
    uint16_t pid = 0x100;
    uint64_t pcr = 27000000;

    ts::TSPacket pkt1 = makePacketWithPCR(pid, pcr);
    monitor.check_pcr_discontinuity(pkt1, pid, pcr);

    // Small jump (50ms = 1,350,000 ticks, within 100ms threshold)
    uint64_t pcr2 = pcr + 1350000;
    ts::TSPacket pkt2 = makePacketWithPCR(pid, pcr2);
    monitor.check_pcr_discontinuity(pkt2, pid, pcr2);

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};
    monitor.get_counters(&p1, &p2);

    EXPECT_EQ(p2.pcr_discontinuity_error, 0);
}

// ============================================================================
// Priority 2: PCR Accuracy Error
// ============================================================================

TEST_F(Tr101290Test, DetectsPcrAccuracyError) {
    uint16_t pid = 0x100;

    // Set a known bitrate (10 Mbps)
    monitor.estimated_bitrate_bps.store(10000000, std::memory_order_relaxed);

    // First PCR: accuracy check first (initializes), then discontinuity (sets last_value)
    uint64_t pcr1 = 27000000;
    int64_t idx1 = 1000;
    ts::TSPacket pkt1 = makePacketWithPCR(pid, pcr1);
    monitor.check_pcr_accuracy(pid, pcr1, idx1);
    monitor.check_pcr_discontinuity(pkt1, pid, pcr1);

    // Second PCR: 100 packets later at 10Mbps means
    // 100 * 188 * 8 = 150400 bits / 10000000 bps = 0.015040s
    // Expected PCR delta = 0.015040 * 27000000 = 406080 ticks
    // Give actual PCR delta much larger to trigger error
    int64_t idx2 = idx1 + 100;
    uint64_t pcr2 = pcr1 + 500000;  // ~18.5ms worth, but only 15ms elapsed
    ts::TSPacket pkt2 = makePacketWithPCR(pid, pcr2);
    monitor.check_pcr_accuracy(pid, pcr2, idx2);
    monitor.check_pcr_discontinuity(pkt2, pid, pcr2);

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};
    monitor.get_counters(&p1, &p2);

    EXPECT_EQ(p2.pcr_accuracy_error, 1);
}

TEST_F(Tr101290Test, NoPcrAccuracyErrorWhenAccurate) {
    uint16_t pid = 0x100;
    monitor.estimated_bitrate_bps.store(10000000, std::memory_order_relaxed);

    uint64_t pcr1 = 27000000;
    int64_t idx1 = 1000;
    ts::TSPacket pkt1 = makePacketWithPCR(pid, pcr1);
    monitor.check_pcr_accuracy(pid, pcr1, idx1);
    monitor.check_pcr_discontinuity(pkt1, pid, pcr1);

    // 100 packets at 10Mbps: expected delta = 406080 ticks
    // Give actual PCR delta matching expected
    int64_t idx2 = idx1 + 100;
    uint64_t pcr2 = pcr1 + 406080;
    ts::TSPacket pkt2 = makePacketWithPCR(pid, pcr2);
    monitor.check_pcr_accuracy(pid, pcr2, idx2);
    monitor.check_pcr_discontinuity(pkt2, pid, pcr2);

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};
    monitor.get_counters(&p1, &p2);

    EXPECT_EQ(p2.pcr_accuracy_error, 0);
}

TEST_F(Tr101290Test, NoPcrAccuracyErrorWithoutBitrate) {
    uint16_t pid = 0x100;
    // No bitrate set (0) — should skip check

    uint64_t pcr1 = 27000000;
    monitor.check_pcr_accuracy(pid, pcr1, 1000);

    uint64_t pcr2 = pcr1 + 999999;  // Large error, but no bitrate to compare
    monitor.check_pcr_accuracy(pid, pcr2, 1100);

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};
    monitor.get_counters(&p1, &p2);

    EXPECT_EQ(p2.pcr_accuracy_error, 0);
}

// ============================================================================
// Priority 2: PTS Repetition Error
// ============================================================================

TEST_F(Tr101290Test, DetectsPtsRepetitionError) {
    uint16_t pid = 0x101;
    int64_t time = 1000000000LL;

    // First PTS
    monitor.check_pts_repetition(pid, time);

    // Second PTS after 800ms (exceeds 700ms limit)
    time += 800000000LL;
    monitor.check_pts_repetition(pid, time);

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};
    monitor.get_counters(&p1, &p2);

    EXPECT_EQ(p2.pts_error, 1);
}

TEST_F(Tr101290Test, NoPtsErrorWithinLimit) {
    uint16_t pid = 0x101;
    int64_t time = 1000000000LL;

    monitor.check_pts_repetition(pid, time);

    // Second PTS after 500ms (within 700ms limit)
    time += 500000000LL;
    monitor.check_pts_repetition(pid, time);

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};
    monitor.get_counters(&p1, &p2);

    EXPECT_EQ(p2.pts_error, 0);
}

TEST_F(Tr101290Test, PtsErrorTrackedPerPid) {
    int64_t time = 1000000000LL;

    // PID 0x101: within limits
    monitor.check_pts_repetition(0x101, time);
    monitor.check_pts_repetition(0x101, time + 500000000LL);

    // PID 0x102: exceeds limits
    monitor.check_pts_repetition(0x102, time);
    monitor.check_pts_repetition(0x102, time + 800000000LL);

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};
    monitor.get_counters(&p1, &p2);

    EXPECT_EQ(p2.pts_error, 1);  // Only one PID exceeded
}

// ============================================================================
// Reset Tests
// ============================================================================

TEST_F(Tr101290Test, ResetClearsAllCounters) {
    monitor.check_sync(false);
    monitor.check_sync(false);
    monitor.on_pmt_timeout();
    monitor.on_crc_error();

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};
    monitor.get_counters(&p1, &p2);
    EXPECT_GT(p1.sync_byte_error, 0);
    EXPECT_GT(p1.pmt_error, 0);
    EXPECT_GT(p2.crc_error, 0);

    monitor.reset();

    monitor.get_counters(&p1, &p2);
    EXPECT_EQ(p1.sync_byte_error, 0);
    EXPECT_EQ(p1.sync_loss, 0);
    EXPECT_EQ(p1.pat_error, 0);
    EXPECT_EQ(p1.pmt_error, 0);
    EXPECT_EQ(p1.pid_error, 0);
    EXPECT_EQ(p2.transport_error, 0);
    EXPECT_EQ(p2.crc_error, 0);
    EXPECT_EQ(p2.pcr_repetition_error, 0);
    EXPECT_EQ(p2.pcr_discontinuity_error, 0);
    EXPECT_EQ(p2.pcr_accuracy_error, 0);
    EXPECT_EQ(p2.pts_error, 0);
}

TEST_F(Tr101290Test, ResetClearsPcrTracking) {
    uint16_t pid = 0x100;
    monitor.estimated_bitrate_bps.store(5000000, std::memory_order_relaxed);

    // Initialize PCR tracking
    monitor.check_pcr_repetition(pid, 1000);

    monitor.reset();

    // Need to re-set bitrate after reset
    monitor.estimated_bitrate_bps.store(5000000, std::memory_order_relaxed);

    // After reset, first PCR should initialize again (no error)
    // Even with large gap, the first call after reset initializes
    monitor.check_pcr_repetition(pid, 100000);

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};
    monitor.get_counters(&p1, &p2);

    EXPECT_EQ(p2.pcr_repetition_error, 0);
}

// ============================================================================
// Lock-free getCounters Test
// ============================================================================

TEST_F(Tr101290Test, GetCountersReturnsConsistentSnapshot) {
    // Simulate multiple increments
    for (int i = 0; i < 100; i++) {
        monitor.check_sync(false);
    }

    Tr101290Priority1Native p1{};
    Tr101290Priority2Native p2{};
    monitor.get_counters(&p1, &p2);

    EXPECT_EQ(p1.sync_byte_error, 100);
    // sync_loss triggered when consecutive_sync_errors >= 2
    // First error: consecutive=1, no sync_loss
    // Errors 2-100: 99 sync_loss events
    EXPECT_EQ(p1.sync_loss, 99);
}

TEST_F(Tr101290Test, GetCountersHandlesNullPointers) {
    // Should not crash
    monitor.get_counters(nullptr, nullptr);

    Tr101290Priority1Native p1{};
    monitor.get_counters(&p1, nullptr);

    Tr101290Priority2Native p2{};
    monitor.get_counters(nullptr, &p2);
}
