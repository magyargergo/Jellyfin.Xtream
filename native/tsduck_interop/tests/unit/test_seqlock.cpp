// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#include <gtest/gtest.h>
#include <thread>
#include <atomic>
#include <chrono>
#include <mutex>
#include <vector>
#include "concurrency/seqlock.hpp"

using namespace tsduck_interop::concurrency;

class SeqlockTest : public ::testing::Test {
protected:
    Seqlock seqlock;
};

TEST_F(SeqlockTest, InitialSequenceIsZero) {
    EXPECT_EQ(seqlock.sequence.load(), 0);
}

TEST_F(SeqlockTest, BeginWriteMakesSequenceOdd) {
    uint64_t expected_final = seqlock.begin_write();
    EXPECT_EQ(seqlock.sequence.load() & 1, 1);  // Odd during write
    seqlock.end_write(expected_final);
    EXPECT_EQ(seqlock.sequence.load() & 1, 0);  // Even after write
}

TEST_F(SeqlockTest, EndWriteMakesSequenceEven) {
    uint64_t expected = seqlock.begin_write();
    seqlock.end_write(expected);
    EXPECT_EQ(seqlock.sequence.load(), expected);
    EXPECT_EQ(expected & 1, 0);  // Even
}

TEST_F(SeqlockTest, SequenceIncrementsCorrectly) {
    for (int i = 0; i < 10; i++) {
        uint64_t expected = seqlock.begin_write();
        seqlock.end_write(expected);
        EXPECT_EQ(seqlock.sequence.load(), (i + 1) * 2);
    }
}

TEST_F(SeqlockTest, BeginReadWaitsForEvenSequence) {
    // Begin read should return current sequence when even
    uint64_t seq = seqlock.begin_read();
    EXPECT_EQ(seq & 1, 0);
}

TEST_F(SeqlockTest, ReadConsistentReturnsTrueWhenUnchanged) {
    uint64_t seq = seqlock.begin_read();
    EXPECT_TRUE(seqlock.read_consistent(seq));
}

TEST_F(SeqlockTest, ReadConsistentReturnsFalseWhenChanged) {
    uint64_t seq = seqlock.begin_read();

    // Perform a write
    uint64_t write_seq = seqlock.begin_write();
    seqlock.end_write(write_seq);

    EXPECT_FALSE(seqlock.read_consistent(seq));
}

// Test concurrent read/write scenario
TEST_F(SeqlockTest, ConcurrentReadWrite) {
    struct SharedData {
        int64_t value1 = 0;
        int64_t value2 = 0;
    };

    SharedData data;
    std::atomic<bool> writer_done{false};
    std::atomic<int> consistent_reads{0};
    std::atomic<int> inconsistent_reads{0};

    // Reader thread - start first to ensure it's ready
    std::thread reader([&]() {
        while (!writer_done.load(std::memory_order_acquire) || consistent_reads.load() < 10) {
            uint64_t seq;
            int64_t v1, v2;
            do {
                seq = seqlock.begin_read();
                v1 = data.value1;
                v2 = data.value2;
            } while (!seqlock.read_consistent(seq));

            // With seqlock, we should always see consistent data
            if (v1 == v2) {
                consistent_reads.fetch_add(1);
            } else {
                inconsistent_reads.fetch_add(1);
            }
        }
    });

    // Give reader time to start
    std::this_thread::sleep_for(std::chrono::milliseconds(1));

    // Writer thread
    std::thread writer([&]() {
        for (int64_t i = 1; i <= 100000; i++) {
            auto seq = seqlock.begin_write();
            data.value1 = i;
            data.value2 = i;  // Should always match value1
            seqlock.end_write(seq);
            // Small yield to give reader opportunities
            if (i % 1000 == 0) {
                std::this_thread::yield();
            }
        }
        writer_done.store(true, std::memory_order_release);
    });

    writer.join();
    reader.join();

    EXPECT_EQ(inconsistent_reads.load(), 0);
    EXPECT_GT(consistent_reads.load(), 0);
}

// Test multiple writers with external synchronization
// NOTE: Seqlock is designed for single-writer scenarios. Multiple writers
// MUST use external synchronization (mutex) to coordinate writes.
TEST_F(SeqlockTest, MultipleWriters) {
    std::atomic<int> write_count{0};
    std::mutex write_mutex;  // External sync for multiple writers
    const int writes_per_thread = 100;
    const int num_threads = 4;

    auto writer_func = [&]() {
        for (int i = 0; i < writes_per_thread; i++) {
            std::lock_guard<std::mutex> lock(write_mutex);
            auto seq = seqlock.begin_write();
            // Simulate some work
            std::this_thread::yield();
            seqlock.end_write(seq);
            write_count.fetch_add(1);
        }
    };

    std::vector<std::thread> threads;
    for (int i = 0; i < num_threads; i++) {
        threads.emplace_back(writer_func);
    }

    for (auto& t : threads) {
        t.join();
    }

    EXPECT_EQ(write_count.load(), num_threads * writes_per_thread);
    // Final sequence should be even
    EXPECT_EQ(seqlock.sequence.load() & 1, 0);
}

int main(int argc, char **argv) {
    ::testing::InitGoogleTest(&argc, argv);
    return RUN_ALL_TESTS();
}
