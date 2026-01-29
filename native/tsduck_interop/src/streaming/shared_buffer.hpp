// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_STREAMING_SHARED_BUFFER_HPP
#define TSDUCK_INTEROP_STREAMING_SHARED_BUFFER_HPP

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <string>

#ifdef _WIN32
#include <windows.h>
#else
#include <fcntl.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>
#endif

namespace tsduck_interop::streaming {

// ============================================================================
// Shared Buffer Flags
// ============================================================================

/// Flags stored in the shared memory header for state signaling.
/// These are accessed atomically by both producer (C++) and consumer (C#).
enum class SharedBufferFlags : uint32_t {
    None = 0,
    DataAvailable = 1 << 0,   // Data has been written
    EndOfStream = 1 << 1,     // Producer signaled stream end
    Error = 1 << 2,           // Error occurred
    Overflow = 1 << 3,        // Ring buffer overflowed (reader too slow)
    Underflow = 1 << 4,       // Reader caught up with writer
};

inline SharedBufferFlags operator|(SharedBufferFlags a, SharedBufferFlags b) {
    return static_cast<SharedBufferFlags>(static_cast<uint32_t>(a) | static_cast<uint32_t>(b));
}

inline SharedBufferFlags operator&(SharedBufferFlags a, SharedBufferFlags b) {
    return static_cast<SharedBufferFlags>(static_cast<uint32_t>(a) & static_cast<uint32_t>(b));
}

inline SharedBufferFlags& operator|=(SharedBufferFlags& a, SharedBufferFlags b) {
    a = a | b;
    return a;
}

// ============================================================================
// Shared Buffer Header (in shared memory)
// ============================================================================

/// Header structure at the beginning of shared memory.
/// This is a POD type that must have the same layout in C++ and C#.
/// All fields are accessed atomically for lock-free operation.
struct SharedBufferHeader {
    // Magic number for validation
    uint32_t magic;                          // Should be 0x54534255 ("TSBU")

    // Version for compatibility checking
    uint32_t version;                        // Currently 1

    // Buffer configuration (set once at creation)
    uint32_t buffer_size;                    // Total ring buffer capacity in bytes
    uint32_t reserved1;                      // Alignment padding

    // Atomic state (accessed by both producer and consumer)
    std::atomic<uint64_t> write_pos;         // Next write position (bytes)
    std::atomic<uint64_t> read_pos;          // Next read position (bytes)
    std::atomic<uint32_t> flags;             // SharedBufferFlags
    std::atomic<uint32_t> sequence;          // Incremented on each write batch

    // Statistics (written by producer, read by consumer)
    std::atomic<uint64_t> total_bytes_written;
    std::atomic<uint64_t> total_bytes_read;
    std::atomic<uint64_t> overflow_count;    // Times writer overwrote unread data
    std::atomic<uint64_t> last_write_time;   // Timestamp of last write (monotonic ns)

    // Padding to ensure data starts at cache-line boundary
    uint8_t padding[24];                     // Pad to 128 bytes total
};

static_assert(sizeof(SharedBufferHeader) == 128, "SharedBufferHeader must be 128 bytes");

constexpr uint32_t SHARED_BUFFER_MAGIC = 0x54534255;  // "TSBU"
constexpr uint32_t SHARED_BUFFER_VERSION = 1;

// ============================================================================
// Shared Buffer Class (Producer Side - C++)
// ============================================================================

/// Cross-process shared memory ring buffer for streaming data.
///
/// This class creates and owns a shared memory region that can be mapped
/// by C# for zero-copy data transfer. The buffer uses a lock-free ring
/// buffer design with atomic operations for synchronization.
///
/// Memory layout:
///   [SharedBufferHeader (128 bytes)] [Ring buffer data (buffer_size bytes)]
///
/// Producer (C++) protocol:
///   1. Create SharedBuffer with name and size
///   2. Write data using write() method
///   3. Call signal_data_available() after writes
///   4. Call signal_end_of_stream() when done
///
/// Consumer (C#) protocol:
///   1. Open memory-mapped file using get_name()
///   2. Map the region and cast header
///   3. Poll read_pos/write_pos to detect available data
///   4. Read data and update read_pos
///   5. Check flags for EOF/error
///
/// Thread safety:
///   - Single producer (C++ streaming thread)
///   - Single consumer (C# reading thread)
///   - Lock-free via atomic operations
class SharedBuffer {
public:
    /// Create a new shared buffer.
    /// @param name Unique name for the shared memory (used as file mapping name).
    /// @param buffer_size Size of the ring buffer in bytes (rounded up to page size).
    /// @throws std::runtime_error if shared memory creation fails.
    SharedBuffer(const std::string& name, size_t buffer_size);

    /// Destructor unmaps and closes the shared memory.
    ~SharedBuffer();

    // Non-copyable, non-movable
    SharedBuffer(const SharedBuffer&) = delete;
    SharedBuffer& operator=(const SharedBuffer&) = delete;
    SharedBuffer(SharedBuffer&&) = delete;
    SharedBuffer& operator=(SharedBuffer&&) = delete;

    // ========================================================================
    // Producer Methods (called from C++ streaming thread)
    // ========================================================================

    /// Write data to the ring buffer.
    /// If the buffer is full, this will overwrite the oldest unread data
    /// and set the Overflow flag.
    /// @param data Pointer to data to write.
    /// @param length Number of bytes to write.
    /// @return Number of bytes actually written (may be less if buffer wraps).
    size_t write(const uint8_t* data, size_t length);

    /// Signal that data is available for the consumer.
    /// Should be called after one or more writes to wake up polling consumer.
    void signal_data_available();

    /// Signal that the stream has ended normally.
    /// Consumer should drain remaining data then close.
    void signal_end_of_stream();

    /// Signal an error condition.
    /// Consumer should read the error and close.
    void signal_error();

    /// Reset the buffer to initial state.
    /// Call between streaming sessions.
    void reset();

    // ========================================================================
    // Consumer Info (for C# to query via P/Invoke)
    // ========================================================================

    /// Get the shared memory name for mapping.
    [[nodiscard]] const std::string& get_name() const noexcept { return name_; }

    /// Get the total size of the shared memory region (header + buffer).
    [[nodiscard]] size_t get_total_size() const noexcept { return total_size_; }

    /// Get the ring buffer capacity in bytes.
    [[nodiscard]] size_t get_buffer_size() const noexcept { return buffer_size_; }

    /// Get pointer to the mapped memory (for internal use).
    [[nodiscard]] void* get_base_address() const noexcept { return mapped_memory_; }

    /// Check if the buffer is successfully created.
    [[nodiscard]] bool is_valid() const noexcept { return mapped_memory_ != nullptr; }

    // ========================================================================
    // Statistics (read from header)
    // ========================================================================

    /// Get the current write position.
    [[nodiscard]] uint64_t get_write_pos() const noexcept;

    /// Get the current read position.
    [[nodiscard]] uint64_t get_read_pos() const noexcept;

    /// Get the number of bytes available for reading.
    [[nodiscard]] size_t get_available_bytes() const noexcept;

    /// Get the current flags.
    [[nodiscard]] SharedBufferFlags get_flags() const noexcept;

    /// Get total bytes written to the buffer.
    [[nodiscard]] uint64_t get_total_bytes_written() const noexcept;

    /// Get number of overflow events.
    [[nodiscard]] uint64_t get_overflow_count() const noexcept;

private:
    std::string name_;
    size_t buffer_size_;       // Ring buffer capacity
    size_t total_size_;        // Header + buffer
    void* mapped_memory_{nullptr};
    SharedBufferHeader* header_{nullptr};
    uint8_t* buffer_data_{nullptr};  // Points to data area after header

#ifdef _WIN32
    HANDLE mapping_handle_{nullptr};
#else
    int shm_fd_{-1};
#endif

    /// Get current monotonic timestamp in nanoseconds.
    static uint64_t get_monotonic_time_ns();

    /// Platform-specific shared memory creation.
    void create_shared_memory();

    /// Platform-specific cleanup.
    void cleanup();
};

// ============================================================================
// Shared Buffer Info (for P/Invoke)
// ============================================================================

/// Information about a shared buffer for C# to open.
/// This is a blittable structure for P/Invoke marshalling.
struct SharedBufferInfo {
    int32_t name_length;         // Length of name string
    int64_t total_size;          // Total mapped size in bytes
    int64_t buffer_size;         // Ring buffer capacity
    int64_t header_offset;       // Offset to header (always 0)
    int64_t data_offset;         // Offset to ring buffer data
};

}  // namespace tsduck_interop::streaming

#endif  // TSDUCK_INTEROP_STREAMING_SHARED_BUFFER_HPP
