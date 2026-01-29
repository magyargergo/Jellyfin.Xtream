// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#include "shared_buffer.hpp"

#include <algorithm>
#include <chrono>
#include <cstring>
#include <stdexcept>

#include "../core/logging.hpp"

namespace tsduck_interop::streaming {

namespace {
constexpr const char* kSharedBuffer = "SharedBuffer";

// Minimum buffer size (64KB)
constexpr size_t MIN_BUFFER_SIZE = 64 * 1024;

// Page size for alignment (4KB typical)
constexpr size_t PAGE_SIZE = 4096;

/// Round up to page boundary.
size_t round_up_to_page(size_t size) {
    return (size + PAGE_SIZE - 1) & ~(PAGE_SIZE - 1);
}

}  // namespace

// ============================================================================
// Construction / Destruction
// ============================================================================

SharedBuffer::SharedBuffer(const std::string& name, size_t buffer_size)
    : name_(name)
    , buffer_size_(std::max(buffer_size, MIN_BUFFER_SIZE))
{
    // Round buffer size to page boundary
    buffer_size_ = round_up_to_page(buffer_size_);

    // Total size = header + ring buffer
    total_size_ = sizeof(SharedBufferHeader) + buffer_size_;
    total_size_ = round_up_to_page(total_size_);

    LOG_INFO(kSharedBuffer, "Creating shared buffer: name=%s, buffer_size=%zu, total_size=%zu",
             name.c_str(), buffer_size_, total_size_);

    create_shared_memory();

    if (!mapped_memory_) {
        throw std::runtime_error("Failed to create shared memory: " + name);
    }

    // Initialize header
    header_ = static_cast<SharedBufferHeader*>(mapped_memory_);
    buffer_data_ = reinterpret_cast<uint8_t*>(mapped_memory_) + sizeof(SharedBufferHeader);

    // Zero out the entire region
    std::memset(mapped_memory_, 0, total_size_);

    // Initialize header fields
    header_->magic = SHARED_BUFFER_MAGIC;
    header_->version = SHARED_BUFFER_VERSION;
    header_->buffer_size = static_cast<uint32_t>(buffer_size_);
    header_->write_pos.store(0, std::memory_order_release);
    header_->read_pos.store(0, std::memory_order_release);
    header_->flags.store(0, std::memory_order_release);
    header_->sequence.store(0, std::memory_order_release);
    header_->total_bytes_written.store(0, std::memory_order_release);
    header_->total_bytes_read.store(0, std::memory_order_release);
    header_->overflow_count.store(0, std::memory_order_release);
    header_->last_write_time.store(0, std::memory_order_release);

    LOG_INFO(kSharedBuffer, "Shared buffer created successfully");
}

SharedBuffer::~SharedBuffer() {
    LOG_DEBUG(kSharedBuffer, "Destroying shared buffer: %s", name_.c_str());
    cleanup();
}

// ============================================================================
// Producer Methods
// ============================================================================

size_t SharedBuffer::write(const uint8_t* data, size_t length) {
    if (!header_ || !buffer_data_ || length == 0) {
        return 0;
    }

    uint64_t write_pos = header_->write_pos.load(std::memory_order_acquire);
    uint64_t read_pos = header_->read_pos.load(std::memory_order_acquire);

    // Calculate available space
    uint64_t used = write_pos - read_pos;
    uint64_t available = buffer_size_ - used;

    // Check for overflow
    if (length > available) {
        // Mark overflow - we're going to overwrite unread data
        header_->flags.fetch_or(static_cast<uint32_t>(SharedBufferFlags::Overflow),
                                std::memory_order_release);
        header_->overflow_count.fetch_add(1, std::memory_order_relaxed);

        // Advance read position to make room
        uint64_t overflow_amount = length - available;
        header_->read_pos.fetch_add(overflow_amount, std::memory_order_release);
    }

    // Write data to ring buffer (may wrap around)
    size_t write_offset = static_cast<size_t>(write_pos % buffer_size_);
    size_t bytes_to_end = buffer_size_ - write_offset;

    if (length <= bytes_to_end) {
        // Simple case: no wrap needed
        std::memcpy(buffer_data_ + write_offset, data, length);
    } else {
        // Wrap around: write to end, then from beginning
        std::memcpy(buffer_data_ + write_offset, data, bytes_to_end);
        std::memcpy(buffer_data_, data + bytes_to_end, length - bytes_to_end);
    }

    // Update write position
    header_->write_pos.store(write_pos + length, std::memory_order_release);

    // Update statistics
    header_->total_bytes_written.fetch_add(length, std::memory_order_relaxed);
    header_->last_write_time.store(get_monotonic_time_ns(), std::memory_order_release);
    header_->sequence.fetch_add(1, std::memory_order_release);

    return length;
}

void SharedBuffer::signal_data_available() {
    if (!header_) {
        return;
    }

    header_->flags.fetch_or(static_cast<uint32_t>(SharedBufferFlags::DataAvailable),
                            std::memory_order_release);
}

void SharedBuffer::signal_end_of_stream() {
    if (!header_) {
        return;
    }

    LOG_DEBUG(kSharedBuffer, "Signaling end of stream");
    header_->flags.fetch_or(static_cast<uint32_t>(SharedBufferFlags::EndOfStream),
                            std::memory_order_release);
}

void SharedBuffer::signal_error() {
    if (!header_) {
        return;
    }

    LOG_DEBUG(kSharedBuffer, "Signaling error");
    header_->flags.fetch_or(static_cast<uint32_t>(SharedBufferFlags::Error),
                            std::memory_order_release);
}

void SharedBuffer::reset() {
    if (!header_) {
        return;
    }

    LOG_DEBUG(kSharedBuffer, "Resetting buffer");

    header_->write_pos.store(0, std::memory_order_release);
    header_->read_pos.store(0, std::memory_order_release);
    header_->flags.store(0, std::memory_order_release);
    header_->sequence.store(0, std::memory_order_release);
    header_->total_bytes_written.store(0, std::memory_order_release);
    header_->total_bytes_read.store(0, std::memory_order_release);
    header_->overflow_count.store(0, std::memory_order_release);
    header_->last_write_time.store(0, std::memory_order_release);
}

// ============================================================================
// Statistics
// ============================================================================

uint64_t SharedBuffer::get_write_pos() const noexcept {
    return header_ ? header_->write_pos.load(std::memory_order_acquire) : 0;
}

uint64_t SharedBuffer::get_read_pos() const noexcept {
    return header_ ? header_->read_pos.load(std::memory_order_acquire) : 0;
}

size_t SharedBuffer::get_available_bytes() const noexcept {
    if (!header_) {
        return 0;
    }
    uint64_t write_pos = header_->write_pos.load(std::memory_order_acquire);
    uint64_t read_pos = header_->read_pos.load(std::memory_order_acquire);
    return static_cast<size_t>(write_pos - read_pos);
}

SharedBufferFlags SharedBuffer::get_flags() const noexcept {
    return header_ ? static_cast<SharedBufferFlags>(header_->flags.load(std::memory_order_acquire))
                   : SharedBufferFlags::None;
}

uint64_t SharedBuffer::get_total_bytes_written() const noexcept {
    return header_ ? header_->total_bytes_written.load(std::memory_order_acquire) : 0;
}

uint64_t SharedBuffer::get_overflow_count() const noexcept {
    return header_ ? header_->overflow_count.load(std::memory_order_acquire) : 0;
}

// ============================================================================
// Utilities
// ============================================================================

uint64_t SharedBuffer::get_monotonic_time_ns() {
    auto now = std::chrono::steady_clock::now();
    return static_cast<uint64_t>(
        std::chrono::duration_cast<std::chrono::nanoseconds>(now.time_since_epoch()).count()
    );
}

// ============================================================================
// Platform-Specific Implementation: Windows
// ============================================================================

#ifdef _WIN32

void SharedBuffer::create_shared_memory() {
    // Create file mapping object
    // Using "Local\" prefix for session-local mapping (works without admin rights)
    std::string mapping_name = "Local\\" + name_;

    mapping_handle_ = CreateFileMappingA(
        INVALID_HANDLE_VALUE,   // Use paging file
        nullptr,                // Default security
        PAGE_READWRITE,         // Read/write access
        static_cast<DWORD>(total_size_ >> 32),   // High-order size
        static_cast<DWORD>(total_size_ & 0xFFFFFFFF),  // Low-order size
        mapping_name.c_str()    // Object name
    );

    if (mapping_handle_ == nullptr) {
        DWORD error = GetLastError();
        LOG_ERROR(kSharedBuffer, "CreateFileMapping failed: error=%lu", error);
        return;
    }

    // Map the view
    mapped_memory_ = MapViewOfFile(
        mapping_handle_,
        FILE_MAP_ALL_ACCESS,
        0, 0,
        total_size_
    );

    if (mapped_memory_ == nullptr) {
        DWORD error = GetLastError();
        LOG_ERROR(kSharedBuffer, "MapViewOfFile failed: error=%lu", error);
        CloseHandle(mapping_handle_);
        mapping_handle_ = nullptr;
        return;
    }

    LOG_DEBUG(kSharedBuffer, "Windows shared memory created: %s", mapping_name.c_str());
}

void SharedBuffer::cleanup() {
    if (mapped_memory_) {
        UnmapViewOfFile(mapped_memory_);
        mapped_memory_ = nullptr;
    }

    if (mapping_handle_) {
        CloseHandle(mapping_handle_);
        mapping_handle_ = nullptr;
    }

    header_ = nullptr;
    buffer_data_ = nullptr;
}

#else

// ============================================================================
// Platform-Specific Implementation: POSIX (Linux/macOS)
// ============================================================================

void SharedBuffer::create_shared_memory() {
    // Create POSIX shared memory object
    std::string shm_name = "/" + name_;

    // Remove any existing object with this name
    shm_unlink(shm_name.c_str());

    // Create new shared memory object
    shm_fd_ = shm_open(shm_name.c_str(), O_CREAT | O_RDWR, 0666);
    if (shm_fd_ < 0) {
        LOG_ERROR(kSharedBuffer, "shm_open failed: %s", strerror(errno));
        return;
    }

    // Set size
    if (ftruncate(shm_fd_, static_cast<off_t>(total_size_)) < 0) {
        LOG_ERROR(kSharedBuffer, "ftruncate failed: %s", strerror(errno));
        close(shm_fd_);
        shm_unlink(shm_name.c_str());
        shm_fd_ = -1;
        return;
    }

    // Map the memory
    mapped_memory_ = mmap(nullptr, total_size_, PROT_READ | PROT_WRITE, MAP_SHARED, shm_fd_, 0);
    if (mapped_memory_ == MAP_FAILED) {
        LOG_ERROR(kSharedBuffer, "mmap failed: %s", strerror(errno));
        close(shm_fd_);
        shm_unlink(shm_name.c_str());
        shm_fd_ = -1;
        mapped_memory_ = nullptr;
        return;
    }

    LOG_DEBUG(kSharedBuffer, "POSIX shared memory created: %s", shm_name.c_str());
}

void SharedBuffer::cleanup() {
    if (mapped_memory_ && mapped_memory_ != MAP_FAILED) {
        munmap(mapped_memory_, total_size_);
        mapped_memory_ = nullptr;
    }

    if (shm_fd_ >= 0) {
        close(shm_fd_);
        // Unlink to remove the shared memory object
        std::string shm_name = "/" + name_;
        shm_unlink(shm_name.c_str());
        shm_fd_ = -1;
    }

    header_ = nullptr;
    buffer_data_ = nullptr;
}

#endif

}  // namespace tsduck_interop::streaming
