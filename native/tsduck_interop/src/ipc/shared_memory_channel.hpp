// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_IPC_SHARED_MEMORY_CHANNEL_HPP
#define TSDUCK_INTEROP_IPC_SHARED_MEMORY_CHANNEL_HPP

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <memory>
#include <span>
#include <string>
#include <string_view>
#include <system_error>

#include "../core/constants.hpp"

namespace tsduck_interop::ipc {

// ============================================================================
// Constants
// ============================================================================

/// Size of the shared memory header region
inline constexpr std::size_t SHM_HEADER_SIZE = 256;

/// Default number of slots in the ring buffer (must be power of 2)
inline constexpr std::size_t DEFAULT_SLOT_COUNT = 1024;

/// Default packets per slot (7 packets fits well in cache)
inline constexpr std::size_t DEFAULT_PACKETS_PER_SLOT = 7;

/// Default slot size in bytes
inline constexpr std::size_t DEFAULT_SLOT_SIZE = DEFAULT_PACKETS_PER_SLOT * TS_PACKET_SIZE;

/// Magic number for validation: "TSTREAM\0" in big-endian
inline constexpr std::uint64_t SHM_MAGIC = 0x5453545245414D00ULL;

/// Protocol version for compatibility checking
inline constexpr std::uint32_t SHM_PROTOCOL_VERSION = 1;

// ============================================================================
// Enumerations
// ============================================================================

/// Status flags for shared memory communication.
/// Multiple flags can be set simultaneously using bitwise OR.
enum class SharedMemoryFlags : std::uint32_t {
    None           = 0x00000000,
    ProducerReady  = 0x00000001,  ///< Producer has initialized the region
    ConsumerReady  = 0x00000002,  ///< Consumer has attached to the region
    EndOfStream    = 0x00000004,  ///< No more data will be written
    Error          = 0x00000008,  ///< An error occurred (check error_code)
    Discontinuity  = 0x00000010,  ///< Stream discontinuity (e.g., URL switch)
    Overflow       = 0x00000020,  ///< Buffer overflow occurred (data lost)
    Underflow      = 0x00000040,  ///< Consumer requested unavailable data
    SwitchPending  = 0x00000080,  ///< URL switch is in progress
};

/// Bitwise OR for flags
inline SharedMemoryFlags operator|(SharedMemoryFlags a, SharedMemoryFlags b) noexcept {
    return static_cast<SharedMemoryFlags>(
        static_cast<std::uint32_t>(a) | static_cast<std::uint32_t>(b));
}

/// Bitwise AND for flags
inline SharedMemoryFlags operator&(SharedMemoryFlags a, SharedMemoryFlags b) noexcept {
    return static_cast<SharedMemoryFlags>(
        static_cast<std::uint32_t>(a) & static_cast<std::uint32_t>(b));
}

/// Bitwise NOT for flags
inline SharedMemoryFlags operator~(SharedMemoryFlags a) noexcept {
    return static_cast<SharedMemoryFlags>(~static_cast<std::uint32_t>(a));
}

/// Producer state enumeration.
enum class ProducerState : std::uint64_t {
    Initializing = 0,  ///< Setting up shared memory
    Connecting   = 1,  ///< Connecting to data source
    Streaming    = 2,  ///< Actively writing data
    Paused       = 3,  ///< Temporarily paused
    Switching    = 4,  ///< Switching data source (URL)
    Stopped      = 5,  ///< Gracefully stopped
    Failed       = 6,  ///< Unrecoverable error
};

/// Consumer state enumeration.
enum class ConsumerState : std::uint64_t {
    Unattached = 0,  ///< Not yet connected
    Attached   = 1,  ///< Connected but not reading
    Reading    = 2,  ///< Actively reading data
    Paused     = 3,  ///< Temporarily paused
    Detached   = 4,  ///< Disconnected from region
};

/// Error codes for shared memory operations.
enum class SharedMemoryError : std::uint32_t {
    None                  = 0,
    InvalidMagic          = 1,
    VersionMismatch       = 2,
    MapFailed             = 3,
    SemaphoreCreateFailed = 4,
    ProducerDisconnected  = 5,
    ConsumerDisconnected  = 6,
    BufferOverflow        = 7,
    NetworkError          = 8,
    InternalError         = 9,
};

// ============================================================================
// Shared Memory Header Structure
// ============================================================================

/// Shared memory header layout.
/// This structure is mapped to the beginning of the shared memory region.
/// All atomic fields must be accessed with appropriate memory ordering.
///
/// Memory Layout (256 bytes total):
///   0x00-0x3F: Metadata (immutable after creation)
///   0x40-0x7F: Producer cache line (written by producer)
///   0x80-0xBF: Consumer cache line (written by consumer)
///   0xC0-0xFF: Flags and error information
struct alignas(CACHE_LINE_SIZE) SharedMemoryHeader {
    // === Metadata Section (0x00-0x3F) - Immutable after creation ===
    std::uint64_t magic;              ///< 0x00: Magic number for validation
    std::uint32_t version;            ///< 0x08: Protocol version
    std::uint32_t header_size;        ///< 0x0C: Size of header region
    std::uint64_t buffer_capacity;    ///< 0x10: Total data region capacity
    std::uint64_t slot_count;         ///< 0x18: Number of slots in ring buffer
    std::uint32_t slot_size;          ///< 0x20: Size of each slot in bytes
    std::uint32_t ts_packet_size;     ///< 0x24: TS packet size (188)
    std::uint64_t reserved1;          ///< 0x28: Reserved for future use

    // === Producer Section (0x40-0x7F) - Written by producer only ===
    alignas(CACHE_LINE_SIZE)
    std::atomic<std::uint64_t> write_sequence;        ///< 0x40: Monotonic write sequence
    std::atomic<std::uint64_t> write_position;        ///< 0x48: Current write slot index
    std::atomic<std::uint64_t> producer_state;        ///< 0x50: ProducerState enum
    std::atomic<std::uint64_t> last_write_timestamp;  ///< 0x58: Last write time (ns)
    std::atomic<std::uint64_t> total_bytes_written;   ///< 0x60: Total bytes written
    std::atomic<std::uint64_t> total_packets_written; ///< 0x68: Total TS packets written
    std::atomic<std::uint64_t> write_wrap_count;      ///< 0x70: Buffer wrap count
    std::uint64_t reserved2;                          ///< 0x78: Reserved

    // === Consumer Section (0x80-0xBF) - Written by consumer only ===
    alignas(CACHE_LINE_SIZE)
    std::atomic<std::uint64_t> read_sequence;         ///< 0x80: Read sequence for validation
    std::atomic<std::uint64_t> read_position;         ///< 0x88: Current read slot index
    std::atomic<std::uint64_t> consumer_state;        ///< 0x90: ConsumerState enum
    std::atomic<std::uint64_t> last_read_timestamp;   ///< 0x98: Last read time (ns)
    std::atomic<std::uint64_t> total_bytes_read;      ///< 0xA0: Total bytes read
    std::atomic<std::uint64_t> total_packets_read;    ///< 0xA8: Total TS packets read
    std::atomic<std::uint64_t> read_wrap_count;       ///< 0xB0: Consumer wrap count
    std::uint64_t reserved3;                          ///< 0xB8: Reserved

    // === Flags and Error Section (0xC0-0xFF) ===
    alignas(CACHE_LINE_SIZE)
    std::atomic<std::uint32_t> flags;                 ///< 0xC0: Status flags
    std::atomic<std::uint32_t> error_code;            ///< 0xC4: Error code
    std::atomic<std::uint64_t> error_timestamp;       ///< 0xC8: Error time (ns)
    char error_message[48];                           ///< 0xD0-0xFF: Error message (null-terminated)
};

static_assert(sizeof(SharedMemoryHeader) == SHM_HEADER_SIZE,
              "SharedMemoryHeader must be exactly 256 bytes");
static_assert(alignof(SharedMemoryHeader) == CACHE_LINE_SIZE,
              "SharedMemoryHeader must be cache-line aligned");

// ============================================================================
// Write Result
// ============================================================================

/// Result of a write operation.
struct WriteResult {
    std::size_t bytes_written;    ///< Number of bytes successfully written
    std::size_t packets_written;  ///< Number of TS packets written
    bool overflow;                ///< True if overflow occurred (data may be lost)
};

// ============================================================================
// Statistics
// ============================================================================

/// Statistics snapshot from shared memory channel.
struct SharedMemoryStatistics {
    std::uint64_t bytes_written;     ///< Total bytes written by producer
    std::uint64_t packets_written;   ///< Total TS packets written
    std::uint64_t write_wrap_count;  ///< Producer wrap count
    std::uint64_t bytes_read;        ///< Total bytes read by consumer
    std::uint64_t packets_read;      ///< Total TS packets read
    std::uint64_t read_wrap_count;   ///< Consumer wrap count
    std::uint64_t available_data;    ///< Bytes available for reading
};

// ============================================================================
// SharedMemoryProducer
// ============================================================================

/// Producer side of shared memory streaming channel.
///
/// Creates and manages a shared memory region for streaming TS data.
/// Uses a lock-free SPSC ring buffer for efficient data transfer.
///
/// Thread safety:
/// - Single thread writes data (write, set_* methods)
/// - Statistics can be read from any thread
/// - Signal methods are thread-safe
class SharedMemoryProducer {
public:
    /// Create a new shared memory region for streaming.
    /// @param name Unique name for the shared memory region
    /// @param slot_count Number of slots in ring buffer (must be power of 2)
    /// @param slot_size Size of each slot in bytes (should be multiple of TS_PACKET_SIZE)
    /// @param out_error Optional output parameter for error code on failure
    /// @return Producer instance or nullptr on failure
    [[nodiscard]] static std::unique_ptr<SharedMemoryProducer> create(
        std::string_view name,
        std::size_t slot_count = DEFAULT_SLOT_COUNT,
        std::size_t slot_size = DEFAULT_SLOT_SIZE,
        std::error_code* out_error = nullptr);

    ~SharedMemoryProducer();

    // Non-copyable
    SharedMemoryProducer(const SharedMemoryProducer&) = delete;
    SharedMemoryProducer& operator=(const SharedMemoryProducer&) = delete;

    // Movable
    SharedMemoryProducer(SharedMemoryProducer&& other) noexcept;
    SharedMemoryProducer& operator=(SharedMemoryProducer&& other) noexcept;

    /// Write TS packet data to the ring buffer.
    /// @param data TS packet data (must be multiple of 188 bytes)
    /// @return Write result with statistics
    [[nodiscard]] WriteResult write(std::span<const std::byte> data) noexcept;

    /// Signal consumer that data is available.
    /// Call after writing a batch of data for efficient wakeup.
    void signal_data_available() noexcept;

    /// Set the end-of-stream flag.
    /// Consumer will complete after reading remaining data.
    void set_end_of_stream() noexcept;

    /// Set an error condition.
    /// @param code Error code
    /// @param message Human-readable error message
    void set_error(SharedMemoryError code, std::string_view message) noexcept;

    /// Set the discontinuity flag.
    /// Consumer should handle stream discontinuity (e.g., after URL switch).
    void set_discontinuity() noexcept;

    /// Clear the discontinuity flag.
    void clear_discontinuity() noexcept;

    /// Set the producer state.
    void set_state(ProducerState state) noexcept;

    /// Check if consumer is attached to the shared memory region.
    [[nodiscard]] bool is_consumer_attached() const noexcept;

    /// Wait for consumer to attach.
    /// @param timeout_ms Timeout in milliseconds (-1 for infinite)
    /// @return true if consumer attached, false on timeout
    [[nodiscard]] bool wait_for_consumer(int timeout_ms = -1) noexcept;

    /// Get available write space in bytes.
    [[nodiscard]] std::size_t available_write_space() const noexcept;

    /// Get statistics snapshot.
    [[nodiscard]] SharedMemoryStatistics get_statistics() const noexcept;

    /// Get the shared memory name.
    [[nodiscard]] std::string_view name() const noexcept { return name_; }

    /// Get the slot count.
    [[nodiscard]] std::size_t slot_count() const noexcept { return slot_count_; }

    /// Get the slot size.
    [[nodiscard]] std::size_t slot_size() const noexcept { return slot_size_; }

private:
    SharedMemoryProducer() = default;

    void cleanup() noexcept;

    std::string name_;
    void* mapping_{nullptr};              // Platform-specific mapping handle
    SharedMemoryHeader* header_{nullptr}; // Pointer to header in mapped region
    std::byte* data_region_{nullptr};     // Pointer to data region
    std::size_t total_size_{0};           // Total mapped size
    std::size_t slot_count_{0};           // Number of slots
    std::size_t slot_size_{0};            // Size of each slot
    std::size_t slot_mask_{0};            // slot_count - 1 for fast modulo
    void* signal_semaphore_{nullptr};     // Platform-specific semaphore
};

// ============================================================================
// SharedMemoryConsumer (C++ side - primarily for testing)
// ============================================================================

/// Consumer side of shared memory streaming channel.
///
/// Opens an existing shared memory region created by a producer.
/// Primary use is for C++ testing; production consumer is in C#.
class SharedMemoryConsumer {
public:
    /// Open an existing shared memory region.
    /// @param name Name of the shared memory region to open
    /// @param out_error Optional output parameter for error code on failure
    /// @return Consumer instance or nullptr on failure
    [[nodiscard]] static std::unique_ptr<SharedMemoryConsumer> open(
        std::string_view name,
        std::error_code* out_error = nullptr);

    ~SharedMemoryConsumer();

    SharedMemoryConsumer(const SharedMemoryConsumer&) = delete;
    SharedMemoryConsumer& operator=(const SharedMemoryConsumer&) = delete;
    SharedMemoryConsumer(SharedMemoryConsumer&& other) noexcept;
    SharedMemoryConsumer& operator=(SharedMemoryConsumer&& other) noexcept;

    /// Read available data from the ring buffer.
    /// @param buffer Destination buffer
    /// @return Number of bytes read (0 if no data available)
    [[nodiscard]] std::size_t read(std::span<std::byte> buffer) noexcept;

    /// Wait for data to become available.
    /// @param timeout_ms Timeout in milliseconds (-1 for infinite)
    /// @return true if data available, false on timeout
    [[nodiscard]] bool wait_for_data(int timeout_ms = -1) noexcept;

    /// Get available data for reading in bytes.
    [[nodiscard]] std::size_t available_data() const noexcept;

    /// Check if end-of-stream has been signaled.
    [[nodiscard]] bool is_end_of_stream() const noexcept;

    /// Check if an error has occurred.
    [[nodiscard]] bool has_error() const noexcept;

    /// Check and clear discontinuity flag.
    [[nodiscard]] bool consume_discontinuity() noexcept;

    /// Get the error code.
    [[nodiscard]] SharedMemoryError get_error_code() const noexcept;

    /// Get the error message.
    [[nodiscard]] std::string_view get_error_message() const noexcept;

    /// Get the producer state.
    [[nodiscard]] ProducerState get_producer_state() const noexcept;

    /// Get statistics snapshot.
    [[nodiscard]] SharedMemoryStatistics get_statistics() const noexcept;

private:
    SharedMemoryConsumer() = default;

    void cleanup() noexcept;

    std::string name_;
    void* mapping_{nullptr};
    SharedMemoryHeader* header_{nullptr};
    std::byte* data_region_{nullptr};
    std::size_t total_size_{0};
    std::size_t slot_count_{0};
    std::size_t slot_size_{0};
    std::size_t slot_mask_{0};
    void* signal_semaphore_{nullptr};
};

}  // namespace tsduck_interop::ipc

#endif  // TSDUCK_INTEROP_IPC_SHARED_MEMORY_CHANNEL_HPP
