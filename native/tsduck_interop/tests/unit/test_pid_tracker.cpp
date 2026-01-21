// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#include <gtest/gtest.h>
#include <thread>
#include <chrono>
#include "analysis/pid_tracker.hpp"

using namespace tsduck_interop::analysis;

class PidTrackerTest : public ::testing::Test {
protected:
    PidTracker tracker;

    void SetUp() override {
        tracker.reset();
    }

    int64_t now_ns() {
        return std::chrono::duration_cast<std::chrono::nanoseconds>(
            std::chrono::steady_clock::now().time_since_epoch()
        ).count();
    }
};

// ============================================================================
// Basic Functionality Tests
// ============================================================================

TEST_F(PidTrackerTest, InitialStateIsEmpty) {
    EXPECT_EQ(tracker.getActiveCount(), 0);
}

TEST_F(PidTrackerTest, ProcessPacketActivatesPid) {
    tracker.processPacket(100, false, now_ns());
    EXPECT_EQ(tracker.getActiveCount(), 1);
    EXPECT_TRUE(tracker.slots[100].is_active.load());
}

TEST_F(PidTrackerTest, ProcessPacketIncrementsPacketCount) {
    int64_t time = now_ns();
    tracker.processPacket(100, false, time);
    tracker.processPacket(100, false, time + 1000);
    tracker.processPacket(100, false, time + 2000);

    EXPECT_EQ(tracker.slots[100].packets.load(), 3);
}

TEST_F(PidTrackerTest, ProcessMultiplePids) {
    int64_t time = now_ns();
    tracker.processPacket(100, false, time);
    tracker.processPacket(200, false, time);
    tracker.processPacket(300, false, time);

    EXPECT_EQ(tracker.getActiveCount(), 3);
}

// ============================================================================
// Scrambled Packet Tests
// ============================================================================

TEST_F(PidTrackerTest, TracksScrambledPackets) {
    int64_t time = now_ns();
    tracker.processPacket(100, false, time);
    tracker.processPacket(100, true, time + 1000);
    tracker.processPacket(100, true, time + 2000);

    EXPECT_EQ(tracker.slots[100].scrambled_packets.load(), 2);
    EXPECT_TRUE(tracker.slots[100].is_scrambled.load());
}

TEST_F(PidTrackerTest, ScrambledFlagUpdates) {
    int64_t time = now_ns();
    tracker.processPacket(100, true, time);
    EXPECT_TRUE(tracker.slots[100].is_scrambled.load());

    tracker.processPacket(100, false, time + 1000);
    EXPECT_FALSE(tracker.slots[100].is_scrambled.load());
}

// ============================================================================
// Timestamp Tests
// ============================================================================

TEST_F(PidTrackerTest, TracksFirstAndLastSeen) {
    int64_t time1 = now_ns();
    tracker.processPacket(100, false, time1);

    int64_t time2 = time1 + 1000000;  // 1ms later
    tracker.processPacket(100, false, time2);

    EXPECT_EQ(tracker.slots[100].first_seen_ns.load(), time1);
    EXPECT_EQ(tracker.slots[100].last_seen_ns.load(), time2);
}

// ============================================================================
// Stream Type Tests
// ============================================================================

TEST_F(PidTrackerTest, SetStreamType) {
    tracker.processPacket(100, false, now_ns());
    tracker.setStreamType(100, 0x1B, true, false);  // H.264 Video

    EXPECT_EQ(tracker.slots[100].stream_type.load(), 0x1B);
    EXPECT_TRUE(tracker.slots[100].is_video.load());
    EXPECT_FALSE(tracker.slots[100].is_audio.load());
}

TEST_F(PidTrackerTest, SetAudioStreamType) {
    tracker.processPacket(200, false, now_ns());
    tracker.setStreamType(200, 0x0F, false, true);  // AAC Audio

    EXPECT_EQ(tracker.slots[200].stream_type.load(), 0x0F);
    EXPECT_FALSE(tracker.slots[200].is_video.load());
    EXPECT_TRUE(tracker.slots[200].is_audio.load());
}

// ============================================================================
// PCR PID Tests
// ============================================================================

TEST_F(PidTrackerTest, SetPcrPid) {
    tracker.processPacket(256, false, now_ns());
    tracker.setPcrPid(256, true);

    EXPECT_TRUE(tracker.slots[256].is_pcr_pid.load());
}

TEST_F(PidTrackerTest, SetPcrJitter) {
    tracker.processPacket(256, false, now_ns());
    tracker.setPcrJitter(256, 0.5);

    auto& slot = tracker.slots[256];
    uint64_t seq;
    double jitter;
    do {
        seq = slot.seqlock.begin_read();
        jitter = slot.pcr_jitter_us;
    } while (!slot.seqlock.read_consistent(seq));

    EXPECT_DOUBLE_EQ(jitter, 0.5);
}

// ============================================================================
// GetCount Tests
// ============================================================================

TEST_F(PidTrackerTest, GetCountReturnsActivePids) {
    int64_t time = now_ns();
    tracker.processPacket(100, false, time);
    tracker.processPacket(200, false, time);
    tracker.processPacket(300, false, time);

    TsDuckPidInfoExtended pids[10];
    int32_t count = tracker.getCount(pids, 10);

    EXPECT_EQ(count, 3);
}

TEST_F(PidTrackerTest, GetCountRespectsMaxLimit) {
    int64_t time = now_ns();
    for (int i = 0; i < 10; i++) {
        tracker.processPacket(i * 100, false, time);
    }

    TsDuckPidInfoExtended pids[5];
    int32_t count = tracker.getCount(pids, 5);

    EXPECT_EQ(count, 5);  // Limited to max
}

TEST_F(PidTrackerTest, GetCountPopulatesCorrectData) {
    int64_t time = now_ns();
    tracker.processPacket(100, false, time);
    tracker.processPacket(100, false, time + 1000000);  // 1ms later
    tracker.processPacket(100, true, time + 2000000);   // scrambled
    tracker.setStreamType(100, 0x1B, true, false);
    tracker.setPcrPid(100, true);

    TsDuckPidInfoExtended pids[1];
    int32_t count = tracker.getCount(pids, 1);

    EXPECT_EQ(count, 1);
    EXPECT_EQ(pids[0].pid, 100);
    EXPECT_EQ(pids[0].packets, 3);
    EXPECT_EQ(pids[0].stream_type, 0x1B);
    EXPECT_EQ(pids[0].scrambled_packets, 1);
    EXPECT_EQ(pids[0].is_pcr_pid, 1);
    EXPECT_EQ(pids[0].is_video, 1);
    EXPECT_EQ(pids[0].is_audio, 0);
}

TEST_F(PidTrackerTest, GetCountHandlesNullPointer) {
    tracker.processPacket(100, false, now_ns());
    EXPECT_EQ(tracker.getCount(nullptr, 10), 0);
}

TEST_F(PidTrackerTest, GetCountHandlesZeroMax) {
    tracker.processPacket(100, false, now_ns());
    TsDuckPidInfoExtended pids[1];
    EXPECT_EQ(tracker.getCount(pids, 0), 0);
}

// ============================================================================
// Reset Tests
// ============================================================================

TEST_F(PidTrackerTest, ResetClearsAllState) {
    int64_t time = now_ns();
    tracker.processPacket(100, true, time);
    tracker.setStreamType(100, 0x1B, true, false);
    tracker.setPcrPid(100, true);

    EXPECT_EQ(tracker.getActiveCount(), 1);

    tracker.reset();

    EXPECT_EQ(tracker.getActiveCount(), 0);
    EXPECT_FALSE(tracker.slots[100].is_active.load());
    EXPECT_EQ(tracker.slots[100].packets.load(), 0);
    EXPECT_EQ(tracker.slots[100].scrambled_packets.load(), 0);
}

// ============================================================================
// Concurrent Access Tests
// ============================================================================

TEST_F(PidTrackerTest, ConcurrentWrites) {
    const int num_threads = 4;
    const int packets_per_thread = 1000;

    std::vector<std::thread> threads;
    for (int t = 0; t < num_threads; t++) {
        threads.emplace_back([&, t]() {
            uint16_t pid = t * 100;  // Different PID per thread
            int64_t time = now_ns();
            for (int i = 0; i < packets_per_thread; i++) {
                tracker.processPacket(pid, false, time + i);
            }
        });
    }

    for (auto& t : threads) {
        t.join();
    }

    EXPECT_EQ(tracker.getActiveCount(), num_threads);

    for (int t = 0; t < num_threads; t++) {
        uint16_t pid = t * 100;
        EXPECT_EQ(tracker.slots[pid].packets.load(), packets_per_thread);
    }
}

TEST_F(PidTrackerTest, ConcurrentReadWrite) {
    std::atomic<bool> stop{false};
    std::atomic<int> read_count{0};

    // Writer thread
    std::thread writer([&]() {
        int64_t time = now_ns();
        for (int i = 0; i < 10000 && !stop.load(); i++) {
            tracker.processPacket(100, (i % 2) == 0, time + i);
        }
        stop.store(true);
    });

    // Reader thread
    std::thread reader([&]() {
        TsDuckPidInfoExtended pids[10];
        while (!stop.load()) {
            tracker.getCount(pids, 10);
            read_count.fetch_add(1);
        }
    });

    writer.join();
    reader.join();

    EXPECT_GT(read_count.load(), 0);
}

int main(int argc, char **argv) {
    ::testing::InitGoogleTest(&argc, argv);
    return RUN_ALL_TESTS();
}
