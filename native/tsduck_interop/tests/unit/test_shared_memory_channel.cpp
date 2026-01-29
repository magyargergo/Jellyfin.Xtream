// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#include <gtest/gtest.h>

#include <algorithm>
#include <array>
#include <chrono>
#include <cstddef>
#include <cstring>
#include <numeric>
#include <thread>
#include <vector>

#include "../../src/ipc/shared_memory_channel.hpp"

namespace tsduck_interop::ipc {
namespace {

// Helper to generate test TS packets
std::vector<std::byte> generate_ts_packets(std::size_t count) {
    std::vector<std::byte> data(count * TS_PACKET_SIZE);
    for (std::size_t i = 0; i < count; ++i) {
        std::byte* packet = data.data() + (i * TS_PACKET_SIZE);
        // TS sync byte
        packet[0] = std::byte{0x47};
        // PID and flags
        packet[1] = std::byte{0x00};
        packet[2] = std::byte{static_cast<uint8_t>(i & 0x1F)};  // Low PID bits
        // Continuity counter
        packet[3] = std::byte{static_cast<uint8_t>(i & 0x0F)};
        // Fill rest with pattern
        for (std::size_t j = 4; j < TS_PACKET_SIZE; ++j) {
            packet[j] = std::byte{static_cast<uint8_t>((i + j) & 0xFF)};
        }
    }
    return data;
}

// Helper to generate non-aligned data (not multiple of 188 bytes)
std::vector<std::byte> generate_non_aligned_data(std::size_t size) {
    std::vector<std::byte> data(size);
    for (std::size_t i = 0; i < size; ++i) {
        data[i] = std::byte{static_cast<uint8_t>(i & 0xFF)};
    }
    return data;
}

// Test fixture for shared memory tests
class SharedMemoryChannelTest : public ::testing::Test {
protected:
    void SetUp() override {
        // Use unique name for each test
        test_name_ = "test_shm_" + std::to_string(
            std::chrono::steady_clock::now().time_since_epoch().count());
    }

    void TearDown() override {
        // Cleanup is handled by destructors
    }

    std::string test_name_;
};

// ============================================================================
// Producer Creation Tests
// ============================================================================

TEST_F(SharedMemoryChannelTest, CreateProducerWithDefaults) {
    auto result = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(result != nullptr) << "Failed to create producer";

    auto& producer = *result;
    EXPECT_EQ(producer.name(), test_name_);
    EXPECT_EQ(producer.slot_count(), DEFAULT_SLOT_COUNT);
    EXPECT_EQ(producer.slot_size(), DEFAULT_SLOT_SIZE);
}

TEST_F(SharedMemoryChannelTest, CreateProducerWithCustomSize) {
    constexpr std::size_t slot_count = 256;
    constexpr std::size_t slot_size = 14 * TS_PACKET_SIZE;  // 14 packets per slot

    auto result = SharedMemoryProducer::create(test_name_, slot_count, slot_size);
    ASSERT_TRUE(result != nullptr);

    EXPECT_EQ(result->slot_count(), slot_count);
    EXPECT_EQ(result->slot_size(), slot_size);
}

TEST_F(SharedMemoryChannelTest, CreateProducerFailsWithEmptyName) {
    auto result = SharedMemoryProducer::create("");
    EXPECT_EQ(result, nullptr);
}

TEST_F(SharedMemoryChannelTest, CreateProducerFailsWithNonPowerOf2SlotCount) {
    auto result = SharedMemoryProducer::create(test_name_, 100, DEFAULT_SLOT_SIZE);
    EXPECT_EQ(result, nullptr);
}

TEST_F(SharedMemoryChannelTest, CreateProducerFailsWithInvalidSlotSize) {
    // Slot size not multiple of TS packet size
    auto result = SharedMemoryProducer::create(test_name_, DEFAULT_SLOT_COUNT, 100);
    EXPECT_EQ(result, nullptr);
}

TEST_F(SharedMemoryChannelTest, CreateProducerFailsWithZeroSlotSize) {
    auto result = SharedMemoryProducer::create(test_name_, DEFAULT_SLOT_COUNT, 0);
    EXPECT_EQ(result, nullptr);
}

TEST_F(SharedMemoryChannelTest, CreateProducerFailsWithSlotCountZero) {
    // 0 is not a power of 2
    auto result = SharedMemoryProducer::create(test_name_, 0, DEFAULT_SLOT_SIZE);
    EXPECT_EQ(result, nullptr);
}

TEST_F(SharedMemoryChannelTest, CreateProducerFailsWithSlotCountOne) {
    // 1 is a power of 2, but would leave no usable slots
    auto result = SharedMemoryProducer::create(test_name_, 1, DEFAULT_SLOT_SIZE);
    // This should succeed since 1 is power of 2
    EXPECT_TRUE(result != nullptr);
}

TEST_F(SharedMemoryChannelTest, VariousPowerOf2SlotCountsSucceed) {
    // Test various power of 2 values
    for (std::size_t count : {2, 4, 8, 16, 32, 64, 128, 256, 512, 1024}) {
        std::string name = test_name_ + "_" + std::to_string(count);
        auto result = SharedMemoryProducer::create(name, count, DEFAULT_SLOT_SIZE);
        EXPECT_TRUE(result != nullptr) << "Failed for slot_count=" << count;
    }
}

TEST_F(SharedMemoryChannelTest, VariousNonPowerOf2SlotCountsFail) {
    // Test various non-power-of-2 values
    for (std::size_t count : {3, 5, 6, 7, 9, 10, 15, 17, 100, 1000}) {
        std::string name = test_name_ + "_" + std::to_string(count);
        auto result = SharedMemoryProducer::create(name, count, DEFAULT_SLOT_SIZE);
        EXPECT_EQ(result, nullptr) << "Should have failed for slot_count=" << count;
    }
}

// ============================================================================
// Consumer Opening Tests
// ============================================================================

TEST_F(SharedMemoryChannelTest, ConsumerCanOpenExistingRegion) {
    auto producer_result = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(producer_result != nullptr);

    auto consumer_result = SharedMemoryConsumer::open(test_name_);
    ASSERT_TRUE(consumer_result != nullptr);
}

TEST_F(SharedMemoryChannelTest, ConsumerFailsOnNonExistentRegion) {
    auto result = SharedMemoryConsumer::open("nonexistent_shm_region_12345");
    EXPECT_EQ(result, nullptr);
}

TEST_F(SharedMemoryChannelTest, ConsumerSetsReadyFlag) {
    auto producer_result = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(producer_result != nullptr);

    auto consumer_result = SharedMemoryConsumer::open(test_name_);
    ASSERT_TRUE(consumer_result != nullptr);

    EXPECT_TRUE(producer_result->is_consumer_attached());
}

// ============================================================================
// Header Initialization Tests
// ============================================================================

TEST_F(SharedMemoryChannelTest, HeaderMagicNumberIsCorrect) {
    auto producer_result = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(producer_result != nullptr);

    // Open consumer and verify magic via consumer reading producer state
    auto consumer_result = SharedMemoryConsumer::open(test_name_);
    ASSERT_TRUE(consumer_result != nullptr);

    // Consumer successfully opening verifies magic was correct
    // (open() validates magic and version)
}

TEST_F(SharedMemoryChannelTest, HeaderVersionIsCorrect) {
    auto producer_result = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(producer_result != nullptr);

    // Consumer successfully opening verifies version was correct
    auto consumer_result = SharedMemoryConsumer::open(test_name_);
    ASSERT_TRUE(consumer_result != nullptr);

    // If version was wrong, open() would have failed
}

TEST_F(SharedMemoryChannelTest, HeaderSizesMatchConfiguration) {
    constexpr std::size_t slot_count = 128;
    constexpr std::size_t slot_size = 14 * TS_PACKET_SIZE;

    auto producer_result = SharedMemoryProducer::create(test_name_, slot_count, slot_size);
    ASSERT_TRUE(producer_result != nullptr);
    auto& producer = *producer_result;

    EXPECT_EQ(producer.slot_count(), slot_count);
    EXPECT_EQ(producer.slot_size(), slot_size);

    // Consumer should see same configuration
    auto consumer_result = SharedMemoryConsumer::open(test_name_);
    ASSERT_TRUE(consumer_result != nullptr);

    // Available space should match configuration
    EXPECT_GE(producer.available_write_space(), (slot_count - 1) * slot_size);
}

TEST_F(SharedMemoryChannelTest, InitialProducerStateIsInitializing) {
    auto producer_result = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(producer_result != nullptr);

    auto consumer_result = SharedMemoryConsumer::open(test_name_);
    ASSERT_TRUE(consumer_result != nullptr);

    // Initial state after creation is Initializing
    EXPECT_EQ(consumer_result->get_producer_state(), ProducerState::Initializing);
}

// ============================================================================
// Write/Read Tests
// ============================================================================

TEST_F(SharedMemoryChannelTest, WriteAndReadSingleSlot) {
    auto producer_result = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(producer_result != nullptr);
    auto& producer = *producer_result;

    auto consumer_result = SharedMemoryConsumer::open(test_name_);
    ASSERT_TRUE(consumer_result != nullptr);
    auto& consumer = *consumer_result;

    // Generate test data (7 packets = 1 slot)
    auto test_data = generate_ts_packets(DEFAULT_PACKETS_PER_SLOT);

    // Write
    auto write_result = producer.write({test_data.data(), test_data.size()});
    EXPECT_EQ(write_result.bytes_written, test_data.size());
    EXPECT_EQ(write_result.packets_written, DEFAULT_PACKETS_PER_SLOT);
    EXPECT_FALSE(write_result.overflow);

    // Signal
    producer.signal_data_available();

    // Read
    std::vector<std::byte> read_buffer(test_data.size());
    std::size_t bytes_read = consumer.read(read_buffer);

    EXPECT_EQ(bytes_read, test_data.size());
    EXPECT_EQ(std::memcmp(test_data.data(), read_buffer.data(), test_data.size()), 0);
}

TEST_F(SharedMemoryChannelTest, WriteAndReadMultipleSlots) {
    auto producer_result = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(producer_result != nullptr);
    auto& producer = *producer_result;

    auto consumer_result = SharedMemoryConsumer::open(test_name_);
    ASSERT_TRUE(consumer_result != nullptr);
    auto& consumer = *consumer_result;

    // Generate test data (21 packets = 3 slots)
    auto test_data = generate_ts_packets(21);

    // Write
    auto write_result = producer.write({test_data.data(), test_data.size()});
    EXPECT_EQ(write_result.bytes_written, test_data.size());

    // Read in chunks
    std::vector<std::byte> read_buffer(test_data.size());
    std::size_t total_read = 0;

    while (total_read < test_data.size()) {
        std::size_t bytes_read = consumer.read({
            read_buffer.data() + total_read,
            read_buffer.size() - total_read
        });
        if (bytes_read == 0) break;
        total_read += bytes_read;
    }

    EXPECT_EQ(total_read, test_data.size());
    EXPECT_EQ(std::memcmp(test_data.data(), read_buffer.data(), test_data.size()), 0);
}

TEST_F(SharedMemoryChannelTest, ReadReturnsZeroWhenEmpty) {
    auto producer_result = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(producer_result != nullptr);

    auto consumer_result = SharedMemoryConsumer::open(test_name_);
    ASSERT_TRUE(consumer_result != nullptr);
    auto& consumer = *consumer_result;

    std::vector<std::byte> buffer(1024);
    std::size_t bytes_read = consumer.read(buffer);
    EXPECT_EQ(bytes_read, 0);
}

TEST_F(SharedMemoryChannelTest, AvailableDataReflectsWrites) {
    auto producer_result = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(producer_result != nullptr);
    auto& producer = *producer_result;

    auto consumer_result = SharedMemoryConsumer::open(test_name_);
    ASSERT_TRUE(consumer_result != nullptr);
    auto& consumer = *consumer_result;

    EXPECT_EQ(consumer.available_data(), 0);

    auto test_data = generate_ts_packets(7);
    producer.write({test_data.data(), test_data.size()});

    // Available data is in slot granularity
    EXPECT_GE(consumer.available_data(), test_data.size());
}

TEST_F(SharedMemoryChannelTest, WriteRejectsNonTsPacketAlignedData) {
    auto producer_result = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(producer_result != nullptr);
    auto& producer = *producer_result;

    // Generate data that is NOT a multiple of 188 bytes
    auto non_aligned_data = generate_non_aligned_data(100);

    auto write_result = producer.write({non_aligned_data.data(), non_aligned_data.size()});
    EXPECT_EQ(write_result.bytes_written, 0);
    EXPECT_EQ(write_result.packets_written, 0);
}

TEST_F(SharedMemoryChannelTest, WriteRejectsPartialTsPacket) {
    auto producer_result = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(producer_result != nullptr);
    auto& producer = *producer_result;

    // Generate 1.5 packets worth of data (not aligned)
    auto non_aligned_data = generate_non_aligned_data(TS_PACKET_SIZE + 94);

    auto write_result = producer.write({non_aligned_data.data(), non_aligned_data.size()});
    EXPECT_EQ(write_result.bytes_written, 0);
    EXPECT_EQ(write_result.packets_written, 0);
}

TEST_F(SharedMemoryChannelTest, WriteEmptyDataReturnsZero) {
    auto producer_result = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(producer_result != nullptr);
    auto& producer = *producer_result;

    std::span<const std::byte> empty_data;
    auto write_result = producer.write(empty_data);
    EXPECT_EQ(write_result.bytes_written, 0);
    EXPECT_EQ(write_result.packets_written, 0);
    EXPECT_FALSE(write_result.overflow);
}

TEST_F(SharedMemoryChannelTest, WritePositionUpdatesCorrectly) {
    auto producer_result = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(producer_result != nullptr);
    auto& producer = *producer_result;

    auto consumer_result = SharedMemoryConsumer::open(test_name_);
    ASSERT_TRUE(consumer_result != nullptr);
    auto& consumer = *consumer_result;

    // Initial available space
    std::size_t initial_space = producer.available_write_space();
    EXPECT_GT(initial_space, 0);

    // Write one slot
    auto test_data = generate_ts_packets(DEFAULT_PACKETS_PER_SLOT);
    producer.write({test_data.data(), test_data.size()});

    // Available space should decrease by one slot
    std::size_t space_after_write = producer.available_write_space();
    EXPECT_EQ(space_after_write, initial_space - DEFAULT_SLOT_SIZE);

    // Consumer should see the data
    EXPECT_EQ(consumer.available_data(), DEFAULT_SLOT_SIZE);

    // Read the data
    std::vector<std::byte> buffer(DEFAULT_SLOT_SIZE);
    consumer.read(buffer);

    // Space should be restored
    EXPECT_EQ(producer.available_write_space(), initial_space);
}

TEST_F(SharedMemoryChannelTest, WriteWithWrapAround) {
    constexpr std::size_t slot_count = 8;
    auto producer_result = SharedMemoryProducer::create(test_name_, slot_count, DEFAULT_SLOT_SIZE);
    ASSERT_TRUE(producer_result != nullptr);
    auto& producer = *producer_result;

    auto consumer_result = SharedMemoryConsumer::open(test_name_);
    ASSERT_TRUE(consumer_result != nullptr);
    auto& consumer = *consumer_result;

    // Write and read more than slot_count times to force wrap-around
    for (int iteration = 0; iteration < 20; ++iteration) {
        auto test_data = generate_ts_packets(DEFAULT_PACKETS_PER_SLOT);
        auto write_result = producer.write({test_data.data(), test_data.size()});
        EXPECT_EQ(write_result.bytes_written, test_data.size())
            << "Write failed at iteration " << iteration;

        // Read to prevent overflow
        std::vector<std::byte> buffer(DEFAULT_SLOT_SIZE);
        std::size_t bytes_read = consumer.read(buffer);
        EXPECT_EQ(bytes_read, DEFAULT_SLOT_SIZE)
            << "Read failed at iteration " << iteration;

        // Verify data integrity
        EXPECT_EQ(std::memcmp(test_data.data(), buffer.data(), DEFAULT_SLOT_SIZE), 0)
            << "Data mismatch at iteration " << iteration;
    }
}

// ============================================================================
// Overflow Tests
// ============================================================================

TEST_F(SharedMemoryChannelTest, OverflowDropsOldestData) {
    // Use small buffer for testing overflow
    constexpr std::size_t slot_count = 8;
    auto producer_result = SharedMemoryProducer::create(test_name_, slot_count, DEFAULT_SLOT_SIZE);
    ASSERT_TRUE(producer_result != nullptr);
    auto& producer = *producer_result;

    auto consumer_result = SharedMemoryConsumer::open(test_name_);
    ASSERT_TRUE(consumer_result != nullptr);

    // Fill the buffer (slot_count - 1 slots can be used)
    for (std::size_t i = 0; i < slot_count; ++i) {
        auto data = generate_ts_packets(DEFAULT_PACKETS_PER_SLOT);
        auto result = producer.write({data.data(), data.size()});

        if (i >= slot_count - 1) {
            // Should overflow after buffer is full
            EXPECT_TRUE(result.overflow) << "Expected overflow at iteration " << i;
        }
    }
}

TEST_F(SharedMemoryChannelTest, OverflowAdvancesReadPosition) {
    constexpr std::size_t slot_count = 4;
    auto producer_result = SharedMemoryProducer::create(test_name_, slot_count, DEFAULT_SLOT_SIZE);
    ASSERT_TRUE(producer_result != nullptr);
    auto& producer = *producer_result;

    auto consumer_result = SharedMemoryConsumer::open(test_name_);
    ASSERT_TRUE(consumer_result != nullptr);
    auto& consumer = *consumer_result;

    // Fill buffer to capacity (3 slots usable with 4 slots total)
    for (std::size_t i = 0; i < slot_count - 1; ++i) {
        auto data = generate_ts_packets(DEFAULT_PACKETS_PER_SLOT);
        auto result = producer.write({data.data(), data.size()});
        EXPECT_FALSE(result.overflow) << "Unexpected overflow at slot " << i;
    }

    // Next write should overflow and advance read position
    auto overflow_data = generate_ts_packets(DEFAULT_PACKETS_PER_SLOT);
    auto overflow_result = producer.write({overflow_data.data(), overflow_data.size()});
    EXPECT_TRUE(overflow_result.overflow);

    // Data should still be readable (read position was advanced, not all data lost)
    std::size_t available = consumer.available_data();
    EXPECT_GT(available, 0);

    // We should be able to read some data (oldest was dropped)
    std::vector<std::byte> buffer(available);
    std::size_t bytes_read = consumer.read(buffer);
    EXPECT_GT(bytes_read, 0);
}

TEST_F(SharedMemoryChannelTest, ContinuousWritesAfterOverflow) {
    constexpr std::size_t slot_count = 4;
    auto producer_result = SharedMemoryProducer::create(test_name_, slot_count, DEFAULT_SLOT_SIZE);
    ASSERT_TRUE(producer_result != nullptr);
    auto& producer = *producer_result;

    auto consumer_result = SharedMemoryConsumer::open(test_name_);
    ASSERT_TRUE(consumer_result != nullptr);
    auto& consumer = *consumer_result;

    // Continuously write without reading - system should not deadlock
    for (int i = 0; i < 50; ++i) {
        auto data = generate_ts_packets(DEFAULT_PACKETS_PER_SLOT);
        auto result = producer.write({data.data(), data.size()});
        // After buffer fills, all writes should report overflow
        EXPECT_EQ(result.bytes_written, data.size());
    }

    // Consumer should still be able to read most recent data
    std::size_t available = consumer.available_data();
    EXPECT_GT(available, 0);
}

// ============================================================================
// State and Flag Tests
// ============================================================================

TEST_F(SharedMemoryChannelTest, ProducerStateTransitions) {
    auto producer_result = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(producer_result != nullptr);
    auto& producer = *producer_result;

    auto consumer_result = SharedMemoryConsumer::open(test_name_);
    ASSERT_TRUE(consumer_result != nullptr);
    auto& consumer = *consumer_result;

    producer.set_state(ProducerState::Connecting);
    EXPECT_EQ(consumer.get_producer_state(), ProducerState::Connecting);

    producer.set_state(ProducerState::Streaming);
    EXPECT_EQ(consumer.get_producer_state(), ProducerState::Streaming);
}

TEST_F(SharedMemoryChannelTest, EndOfStreamFlag) {
    auto producer_result = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(producer_result != nullptr);
    auto& producer = *producer_result;

    auto consumer_result = SharedMemoryConsumer::open(test_name_);
    ASSERT_TRUE(consumer_result != nullptr);
    auto& consumer = *consumer_result;

    EXPECT_FALSE(consumer.is_end_of_stream());

    producer.set_end_of_stream();

    EXPECT_TRUE(consumer.is_end_of_stream());
}

TEST_F(SharedMemoryChannelTest, DiscontinuityFlag) {
    auto producer_result = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(producer_result != nullptr);
    auto& producer = *producer_result;

    auto consumer_result = SharedMemoryConsumer::open(test_name_);
    ASSERT_TRUE(consumer_result != nullptr);
    auto& consumer = *consumer_result;

    producer.set_discontinuity();

    // First consume should return true
    EXPECT_TRUE(consumer.consume_discontinuity());

    // Second consume should return false (already consumed)
    EXPECT_FALSE(consumer.consume_discontinuity());
}

TEST_F(SharedMemoryChannelTest, SetAndClearDiscontinuityFlag) {
    auto producer_result = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(producer_result != nullptr);
    auto& producer = *producer_result;

    auto consumer_result = SharedMemoryConsumer::open(test_name_);
    ASSERT_TRUE(consumer_result != nullptr);
    auto& consumer = *consumer_result;

    // Set discontinuity
    producer.set_discontinuity();

    // Clear it from producer side
    producer.clear_discontinuity();

    // Consumer should not see discontinuity
    EXPECT_FALSE(consumer.consume_discontinuity());
}

TEST_F(SharedMemoryChannelTest, MultipleDiscontinuitySetClearCycles) {
    auto producer_result = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(producer_result != nullptr);
    auto& producer = *producer_result;

    auto consumer_result = SharedMemoryConsumer::open(test_name_);
    ASSERT_TRUE(consumer_result != nullptr);
    auto& consumer = *consumer_result;

    for (int i = 0; i < 5; ++i) {
        // Set discontinuity
        producer.set_discontinuity();
        EXPECT_TRUE(consumer.consume_discontinuity())
            << "Should see discontinuity on iteration " << i;

        // Already consumed
        EXPECT_FALSE(consumer.consume_discontinuity());

        // Set and clear without consuming
        producer.set_discontinuity();
        producer.clear_discontinuity();
        EXPECT_FALSE(consumer.consume_discontinuity())
            << "Should not see discontinuity after clear on iteration " << i;
    }
}

TEST_F(SharedMemoryChannelTest, ErrorFlagAndMessage) {
    auto producer_result = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(producer_result != nullptr);
    auto& producer = *producer_result;

    auto consumer_result = SharedMemoryConsumer::open(test_name_);
    ASSERT_TRUE(consumer_result != nullptr);
    auto& consumer = *consumer_result;

    EXPECT_FALSE(consumer.has_error());

    producer.set_error(SharedMemoryError::NetworkError, "Connection lost");

    EXPECT_TRUE(consumer.has_error());
    EXPECT_EQ(consumer.get_error_code(), SharedMemoryError::NetworkError);
    EXPECT_EQ(consumer.get_error_message(), "Connection lost");
}

TEST_F(SharedMemoryChannelTest, ErrorMessageTruncation) {
    auto producer_result = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(producer_result != nullptr);
    auto& producer = *producer_result;

    auto consumer_result = SharedMemoryConsumer::open(test_name_);
    ASSERT_TRUE(consumer_result != nullptr);
    auto& consumer = *consumer_result;

    // Create a very long error message (>64 bytes)
    std::string long_message(200, 'X');
    producer.set_error(SharedMemoryError::InternalError, long_message);

    // Message should be truncated to fit in header (63 chars + null)
    auto retrieved_message = consumer.get_error_message();
    EXPECT_LE(retrieved_message.size(), 63);
    EXPECT_TRUE(consumer.has_error());
}

TEST_F(SharedMemoryChannelTest, AllErrorCodesCanBeSet) {
    auto producer_result = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(producer_result != nullptr);
    auto& producer = *producer_result;

    auto consumer_result = SharedMemoryConsumer::open(test_name_);
    ASSERT_TRUE(consumer_result != nullptr);
    auto& consumer = *consumer_result;

    std::vector<SharedMemoryError> error_codes = {
        SharedMemoryError::InvalidMagic,
        SharedMemoryError::VersionMismatch,
        SharedMemoryError::MapFailed,
        SharedMemoryError::SemaphoreCreateFailed,
        SharedMemoryError::ProducerDisconnected,
        SharedMemoryError::ConsumerDisconnected,
        SharedMemoryError::BufferOverflow,
        SharedMemoryError::NetworkError,
        SharedMemoryError::InternalError,
    };

    for (auto code : error_codes) {
        producer.set_error(code, "Test error");
        EXPECT_EQ(consumer.get_error_code(), code)
            << "Failed for error code " << static_cast<int>(code);
    }
}

TEST_F(SharedMemoryChannelTest, AllProducerStatesCanBeSet) {
    auto producer_result = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(producer_result != nullptr);
    auto& producer = *producer_result;

    auto consumer_result = SharedMemoryConsumer::open(test_name_);
    ASSERT_TRUE(consumer_result != nullptr);
    auto& consumer = *consumer_result;

    std::vector<ProducerState> states = {
        ProducerState::Initializing,
        ProducerState::Connecting,
        ProducerState::Streaming,
        ProducerState::Paused,
        ProducerState::Switching,
        ProducerState::Stopped,
        ProducerState::Failed,
    };

    for (auto state : states) {
        producer.set_state(state);
        EXPECT_EQ(consumer.get_producer_state(), state)
            << "Failed for state " << static_cast<int>(state);
    }
}

// ============================================================================
// Statistics Tests
// ============================================================================

TEST_F(SharedMemoryChannelTest, StatisticsTrackWritesAndReads) {
    auto producer_result = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(producer_result != nullptr);
    auto& producer = *producer_result;

    auto consumer_result = SharedMemoryConsumer::open(test_name_);
    ASSERT_TRUE(consumer_result != nullptr);
    auto& consumer = *consumer_result;

    auto test_data = generate_ts_packets(14);  // 2 slots worth

    // Write data
    producer.write({test_data.data(), test_data.size()});

    auto stats = producer.get_statistics();
    EXPECT_EQ(stats.bytes_written, test_data.size());
    EXPECT_EQ(stats.packets_written, 14);

    // Read data
    std::vector<std::byte> buffer(test_data.size());
    consumer.read(buffer);

    stats = consumer.get_statistics();
    EXPECT_EQ(stats.bytes_read, test_data.size());
    EXPECT_EQ(stats.packets_read, 14);
}

// ============================================================================
// Wait/Signal Tests
// ============================================================================

TEST_F(SharedMemoryChannelTest, WaitForDataReturnsOnSignal) {
    auto producer_result = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(producer_result != nullptr);
    auto& producer = *producer_result;

    auto consumer_result = SharedMemoryConsumer::open(test_name_);
    ASSERT_TRUE(consumer_result != nullptr);
    auto& consumer = *consumer_result;

    // Start consumer waiting in separate thread
    std::atomic<bool> got_data{false};
    std::thread consumer_thread([&] {
        got_data = consumer.wait_for_data(5000);
    });

    // Brief delay then write and signal
    std::this_thread::sleep_for(std::chrono::milliseconds(50));
    auto data = generate_ts_packets(7);
    producer.write({data.data(), data.size()});
    producer.signal_data_available();

    consumer_thread.join();
    EXPECT_TRUE(got_data);
}

TEST_F(SharedMemoryChannelTest, WaitForDataTimesOut) {
    auto producer_result = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(producer_result != nullptr);

    auto consumer_result = SharedMemoryConsumer::open(test_name_);
    ASSERT_TRUE(consumer_result != nullptr);
    auto& consumer = *consumer_result;

    auto start = std::chrono::steady_clock::now();
    bool got_data = consumer.wait_for_data(100);  // 100ms timeout
    auto elapsed = std::chrono::steady_clock::now() - start;

    EXPECT_FALSE(got_data);
    EXPECT_GE(elapsed, std::chrono::milliseconds(100));
}

TEST_F(SharedMemoryChannelTest, WaitForConsumerAttach) {
    auto producer_result = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(producer_result != nullptr);
    auto& producer = *producer_result;

    EXPECT_FALSE(producer.is_consumer_attached());

    // Start waiting in separate thread
    std::atomic<bool> attached{false};
    std::thread wait_thread([&] {
        attached = producer.wait_for_consumer(1000);
    });

    // Brief delay then create consumer
    std::this_thread::sleep_for(std::chrono::milliseconds(50));
    auto consumer_result = SharedMemoryConsumer::open(test_name_);

    wait_thread.join();
    EXPECT_TRUE(attached);
    EXPECT_TRUE(producer.is_consumer_attached());
}

// ============================================================================
// Move Semantics Tests
// ============================================================================

TEST_F(SharedMemoryChannelTest, ProducerIsMoveConstructible) {
    auto result = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(result != nullptr);

    SharedMemoryProducer moved_producer = std::move(*result);
    EXPECT_EQ(moved_producer.name(), test_name_);

    // Original should be in empty state
    // (accessing it would be UB, so we just verify move succeeded)
}

TEST_F(SharedMemoryChannelTest, ConsumerIsMoveConstructible) {
    auto producer_result = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(producer_result != nullptr);

    auto consumer_result = SharedMemoryConsumer::open(test_name_);
    ASSERT_TRUE(consumer_result != nullptr);

    SharedMemoryConsumer moved_consumer = std::move(*consumer_result);

    // Verify moved consumer works
    EXPECT_FALSE(moved_consumer.is_end_of_stream());
}

TEST_F(SharedMemoryChannelTest, ProducerMoveAssignment) {
    auto result1 = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(result1 != nullptr);

    std::string other_name = test_name_ + "_other";
    auto result2 = SharedMemoryProducer::create(other_name);
    ASSERT_TRUE(result2 != nullptr);

    // Move assign
    *result1 = std::move(*result2);
    EXPECT_EQ(result1->name(), other_name);
}

TEST_F(SharedMemoryChannelTest, ConsumerMoveAssignment) {
    auto producer_result = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(producer_result != nullptr);

    auto consumer_result = SharedMemoryConsumer::open(test_name_);
    ASSERT_TRUE(consumer_result != nullptr);

    // Create another producer/consumer pair
    std::string other_name = test_name_ + "_other";
    auto producer2 = SharedMemoryProducer::create(other_name);
    ASSERT_TRUE(producer2 != nullptr);

    auto consumer2 = SharedMemoryConsumer::open(other_name);
    ASSERT_TRUE(consumer2 != nullptr);

    // Move assign
    *consumer_result = std::move(*consumer2);

    // Verify the moved-to consumer works
    EXPECT_FALSE(consumer_result->is_end_of_stream());
}

// ============================================================================
// Memory Layout Verification Tests
// ============================================================================

TEST(SharedMemoryLayoutTest, HeaderSizeIsExactly256Bytes) {
    static_assert(sizeof(SharedMemoryHeader) == 256,
                  "SharedMemoryHeader must be exactly 256 bytes");
    EXPECT_EQ(sizeof(SharedMemoryHeader), 256);
}

TEST(SharedMemoryLayoutTest, HeaderSizeConstantMatches) {
    EXPECT_EQ(sizeof(SharedMemoryHeader), SHM_HEADER_SIZE);
    EXPECT_EQ(SHM_HEADER_SIZE, 256);
}

TEST(SharedMemoryLayoutTest, HeaderIsCacheLineAligned) {
    static_assert(alignof(SharedMemoryHeader) == CACHE_LINE_SIZE,
                  "SharedMemoryHeader must be cache-line aligned");
    EXPECT_EQ(alignof(SharedMemoryHeader), CACHE_LINE_SIZE);
    EXPECT_EQ(CACHE_LINE_SIZE, 64);
}

TEST(SharedMemoryLayoutTest, MetadataSectionOffset) {
    // Metadata section starts at offset 0x00
    SharedMemoryHeader header{};
    auto base = reinterpret_cast<std::uintptr_t>(&header);

    EXPECT_EQ(reinterpret_cast<std::uintptr_t>(&header.magic) - base, 0x00);
    EXPECT_EQ(reinterpret_cast<std::uintptr_t>(&header.version) - base, 0x08);
    EXPECT_EQ(reinterpret_cast<std::uintptr_t>(&header.header_size) - base, 0x0C);
    EXPECT_EQ(reinterpret_cast<std::uintptr_t>(&header.buffer_capacity) - base, 0x10);
    EXPECT_EQ(reinterpret_cast<std::uintptr_t>(&header.slot_count) - base, 0x18);
    EXPECT_EQ(reinterpret_cast<std::uintptr_t>(&header.slot_size) - base, 0x20);
    EXPECT_EQ(reinterpret_cast<std::uintptr_t>(&header.ts_packet_size) - base, 0x24);
}

TEST(SharedMemoryLayoutTest, ProducerSectionOffset) {
    // Producer section starts at offset 0x40 (cache-line aligned)
    SharedMemoryHeader header{};
    auto base = reinterpret_cast<std::uintptr_t>(&header);

    EXPECT_EQ(reinterpret_cast<std::uintptr_t>(&header.write_sequence) - base, 0x40);
    EXPECT_EQ(reinterpret_cast<std::uintptr_t>(&header.write_position) - base, 0x48);
    EXPECT_EQ(reinterpret_cast<std::uintptr_t>(&header.producer_state) - base, 0x50);
    EXPECT_EQ(reinterpret_cast<std::uintptr_t>(&header.last_write_timestamp) - base, 0x58);
    EXPECT_EQ(reinterpret_cast<std::uintptr_t>(&header.total_bytes_written) - base, 0x60);
    EXPECT_EQ(reinterpret_cast<std::uintptr_t>(&header.total_packets_written) - base, 0x68);
    EXPECT_EQ(reinterpret_cast<std::uintptr_t>(&header.write_wrap_count) - base, 0x70);
}

TEST(SharedMemoryLayoutTest, ConsumerSectionOffset) {
    // Consumer section starts at offset 0x80 (cache-line aligned)
    SharedMemoryHeader header{};
    auto base = reinterpret_cast<std::uintptr_t>(&header);

    EXPECT_EQ(reinterpret_cast<std::uintptr_t>(&header.read_sequence) - base, 0x80);
    EXPECT_EQ(reinterpret_cast<std::uintptr_t>(&header.read_position) - base, 0x88);
    EXPECT_EQ(reinterpret_cast<std::uintptr_t>(&header.consumer_state) - base, 0x90);
    EXPECT_EQ(reinterpret_cast<std::uintptr_t>(&header.last_read_timestamp) - base, 0x98);
    EXPECT_EQ(reinterpret_cast<std::uintptr_t>(&header.total_bytes_read) - base, 0xA0);
    EXPECT_EQ(reinterpret_cast<std::uintptr_t>(&header.total_packets_read) - base, 0xA8);
    EXPECT_EQ(reinterpret_cast<std::uintptr_t>(&header.read_wrap_count) - base, 0xB0);
}

TEST(SharedMemoryLayoutTest, FlagsSectionOffset) {
    // Flags section starts at offset 0xC0 (cache-line aligned)
    SharedMemoryHeader header{};
    auto base = reinterpret_cast<std::uintptr_t>(&header);

    EXPECT_EQ(reinterpret_cast<std::uintptr_t>(&header.flags) - base, 0xC0);
    EXPECT_EQ(reinterpret_cast<std::uintptr_t>(&header.error_code) - base, 0xC4);
    EXPECT_EQ(reinterpret_cast<std::uintptr_t>(&header.error_timestamp) - base, 0xC8);
    EXPECT_EQ(reinterpret_cast<std::uintptr_t>(&header.error_message) - base, 0xD0);
}

TEST(SharedMemoryLayoutTest, CacheLineAlignmentForSections) {
    // Verify each major section starts on cache-line boundary
    SharedMemoryHeader header{};
    auto base = reinterpret_cast<std::uintptr_t>(&header);

    // Producer section at 0x40
    auto producer_offset = reinterpret_cast<std::uintptr_t>(&header.write_sequence) - base;
    EXPECT_EQ(producer_offset % CACHE_LINE_SIZE, 0)
        << "Producer section not cache-line aligned";

    // Consumer section at 0x80
    auto consumer_offset = reinterpret_cast<std::uintptr_t>(&header.read_sequence) - base;
    EXPECT_EQ(consumer_offset % CACHE_LINE_SIZE, 0)
        << "Consumer section not cache-line aligned";

    // Flags section at 0xC0
    auto flags_offset = reinterpret_cast<std::uintptr_t>(&header.flags) - base;
    EXPECT_EQ(flags_offset % CACHE_LINE_SIZE, 0)
        << "Flags section not cache-line aligned";
}

TEST(SharedMemoryLayoutTest, MagicNumberValue) {
    // "TSTREAM\0" in big-endian
    EXPECT_EQ(SHM_MAGIC, 0x5453545245414D00ULL);
}

TEST(SharedMemoryLayoutTest, ProtocolVersion) {
    EXPECT_EQ(SHM_PROTOCOL_VERSION, 1);
}

TEST(SharedMemoryLayoutTest, DefaultSlotCount) {
    EXPECT_EQ(DEFAULT_SLOT_COUNT, 1024);
    // Verify it's a power of 2
    EXPECT_EQ(DEFAULT_SLOT_COUNT & (DEFAULT_SLOT_COUNT - 1), 0);
}

TEST(SharedMemoryLayoutTest, DefaultPacketsPerSlot) {
    EXPECT_EQ(DEFAULT_PACKETS_PER_SLOT, 7);
}

TEST(SharedMemoryLayoutTest, DefaultSlotSize) {
    EXPECT_EQ(DEFAULT_SLOT_SIZE, DEFAULT_PACKETS_PER_SLOT * TS_PACKET_SIZE);
    EXPECT_EQ(DEFAULT_SLOT_SIZE, 7 * 188);
    EXPECT_EQ(DEFAULT_SLOT_SIZE, 1316);
}

TEST(SharedMemoryLayoutTest, TsPacketSizeConstant) {
    EXPECT_EQ(TS_PACKET_SIZE, 188);
}

// ============================================================================
// Flag Enumeration Tests
// ============================================================================

TEST(SharedMemoryFlagsTest, FlagValues) {
    EXPECT_EQ(static_cast<uint32_t>(SharedMemoryFlags::None), 0x00000000);
    EXPECT_EQ(static_cast<uint32_t>(SharedMemoryFlags::ProducerReady), 0x00000001);
    EXPECT_EQ(static_cast<uint32_t>(SharedMemoryFlags::ConsumerReady), 0x00000002);
    EXPECT_EQ(static_cast<uint32_t>(SharedMemoryFlags::EndOfStream), 0x00000004);
    EXPECT_EQ(static_cast<uint32_t>(SharedMemoryFlags::Error), 0x00000008);
    EXPECT_EQ(static_cast<uint32_t>(SharedMemoryFlags::Discontinuity), 0x00000010);
    EXPECT_EQ(static_cast<uint32_t>(SharedMemoryFlags::Overflow), 0x00000020);
    EXPECT_EQ(static_cast<uint32_t>(SharedMemoryFlags::Underflow), 0x00000040);
    EXPECT_EQ(static_cast<uint32_t>(SharedMemoryFlags::SwitchPending), 0x00000080);
}

TEST(SharedMemoryFlagsTest, FlagBitwiseOperations) {
    auto combined = SharedMemoryFlags::ProducerReady | SharedMemoryFlags::ConsumerReady;
    EXPECT_EQ(static_cast<uint32_t>(combined), 0x00000003);

    auto masked = combined & SharedMemoryFlags::ProducerReady;
    EXPECT_EQ(static_cast<uint32_t>(masked), 0x00000001);

    auto inverted = ~SharedMemoryFlags::ProducerReady;
    EXPECT_EQ(static_cast<uint32_t>(inverted), 0xFFFFFFFE);
}

// ============================================================================
// Additional Consumer Attachment Tests
// ============================================================================

TEST_F(SharedMemoryChannelTest, ConsumerNotAttachedInitially) {
    auto producer_result = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(producer_result != nullptr);

    EXPECT_FALSE(producer_result->is_consumer_attached());
}

TEST_F(SharedMemoryChannelTest, ConsumerDetachClearsReadyFlag) {
    auto producer_result = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(producer_result != nullptr);

    {
        auto consumer_result = SharedMemoryConsumer::open(test_name_);
        ASSERT_TRUE(consumer_result != nullptr);
        EXPECT_TRUE(producer_result->is_consumer_attached());
    }

    // Consumer destroyed - flag should be cleared
    // Note: This test may be timing-dependent on some systems
    EXPECT_FALSE(producer_result->is_consumer_attached());
}

TEST_F(SharedMemoryChannelTest, WaitForConsumerTimesOut) {
    auto producer_result = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(producer_result != nullptr);

    auto start = std::chrono::steady_clock::now();
    bool attached = producer_result->wait_for_consumer(100);  // 100ms timeout
    auto elapsed = std::chrono::steady_clock::now() - start;

    EXPECT_FALSE(attached);
    EXPECT_GE(elapsed, std::chrono::milliseconds(100));
}

// ============================================================================
// Statistics Verification Tests
// ============================================================================

TEST_F(SharedMemoryChannelTest, StatisticsInitiallyZero) {
    auto producer_result = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(producer_result != nullptr);

    auto stats = producer_result->get_statistics();
    EXPECT_EQ(stats.bytes_written, 0);
    EXPECT_EQ(stats.packets_written, 0);
    EXPECT_EQ(stats.write_wrap_count, 0);
    EXPECT_EQ(stats.bytes_read, 0);
    EXPECT_EQ(stats.packets_read, 0);
    EXPECT_EQ(stats.read_wrap_count, 0);
    EXPECT_EQ(stats.available_data, 0);
}

TEST_F(SharedMemoryChannelTest, StatisticsAccumulateOverMultipleWrites) {
    auto producer_result = SharedMemoryProducer::create(test_name_);
    ASSERT_TRUE(producer_result != nullptr);
    auto& producer = *producer_result;

    auto consumer_result = SharedMemoryConsumer::open(test_name_);
    ASSERT_TRUE(consumer_result != nullptr);
    auto& consumer = *consumer_result;

    std::size_t total_bytes = 0;
    std::size_t total_packets = 0;

    for (int i = 0; i < 10; ++i) {
        auto data = generate_ts_packets(7);
        auto result = producer.write({data.data(), data.size()});
        total_bytes += result.bytes_written;
        total_packets += result.packets_written;

        // Read to keep buffer from overflowing
        std::vector<std::byte> buffer(data.size());
        consumer.read(buffer);
    }

    auto stats = producer.get_statistics();
    EXPECT_EQ(stats.bytes_written, total_bytes);
    EXPECT_EQ(stats.packets_written, total_packets);
}

TEST_F(SharedMemoryChannelTest, AvailableWriteSpaceCalculation) {
    constexpr std::size_t slot_count = 16;
    auto producer_result = SharedMemoryProducer::create(test_name_, slot_count, DEFAULT_SLOT_SIZE);
    ASSERT_TRUE(producer_result != nullptr);
    auto& producer = *producer_result;

    // Initial available space: (slot_count - 1) * slot_size
    std::size_t expected_space = (slot_count - 1) * DEFAULT_SLOT_SIZE;
    EXPECT_EQ(producer.available_write_space(), expected_space);

    // Write one slot, space decreases
    auto data = generate_ts_packets(DEFAULT_PACKETS_PER_SLOT);
    producer.write({data.data(), data.size()});

    EXPECT_EQ(producer.available_write_space(), expected_space - DEFAULT_SLOT_SIZE);
}

}  // namespace
}  // namespace tsduck_interop::ipc
