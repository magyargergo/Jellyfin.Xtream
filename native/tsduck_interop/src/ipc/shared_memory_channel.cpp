// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#include "shared_memory_channel.hpp"

#include <algorithm>
#include <chrono>
#include <cstring>
#include <thread>

#include "../core/logging.hpp"
#include "../platform/simd_memcpy.hpp"

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#else
#include <cerrno>
#include <fcntl.h>
#include <semaphore.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>
#endif

namespace tsduck_interop::ipc {

namespace {

constexpr const char* kLogComponent = "SharedMemory";

/// Get current timestamp in nanoseconds since epoch.
[[nodiscard]] std::uint64_t get_timestamp_ns() noexcept {
    using namespace std::chrono;
    return static_cast<std::uint64_t>(
        duration_cast<nanoseconds>(steady_clock::now().time_since_epoch()).count());
}

/// Check if a value is a power of 2.
[[nodiscard]] constexpr bool is_power_of_2(std::size_t value) noexcept {
    return value != 0 && (value & (value - 1)) == 0;
}

}  // namespace

// ============================================================================
// SharedMemoryProducer Implementation
// ============================================================================

/// Helper to store error code in output parameter if provided.
static void store_error(std::error_code* out_error, std::error_code ec) noexcept {
    if (out_error != nullptr) {
        *out_error = ec;
    }
}

std::unique_ptr<SharedMemoryProducer> SharedMemoryProducer::create(
    std::string_view name,
    std::size_t slot_count,
    std::size_t slot_size,
    std::error_code* out_error)
{
    // Clear any previous error
    store_error(out_error, std::error_code{});

    // Validate parameters
    if (name.empty()) {
        LOG_ERROR(kLogComponent, "Empty shared memory name");
        store_error(out_error, std::make_error_code(std::errc::invalid_argument));
        return nullptr;
    }

    if (!is_power_of_2(slot_count)) {
        LOG_ERROR(kLogComponent, "slot_count must be power of 2: %zu", slot_count);
        store_error(out_error, std::make_error_code(std::errc::invalid_argument));
        return nullptr;
    }

    if (slot_size < TS_PACKET_SIZE || (slot_size % TS_PACKET_SIZE) != 0) {
        LOG_ERROR(kLogComponent, "slot_size must be positive multiple of TS_PACKET_SIZE: %zu",
                  slot_size);
        store_error(out_error, std::make_error_code(std::errc::invalid_argument));
        return nullptr;
    }

    auto producer = std::unique_ptr<SharedMemoryProducer>(new SharedMemoryProducer());
    producer->name_ = std::string(name);
    producer->slot_count_ = slot_count;
    producer->slot_size_ = slot_size;
    producer->slot_mask_ = slot_count - 1;
    producer->total_size_ = SHM_HEADER_SIZE + (slot_count * slot_size);

    LOG_INFO(kLogComponent, "Creating shared memory '%s': %zu slots x %zu bytes = %zu total",
             producer->name_.c_str(), slot_count, slot_size, producer->total_size_);

#ifdef _WIN32
    // Windows implementation using CreateFileMapping
    std::wstring wname(name.begin(), name.end());

    HANDLE h_map = CreateFileMappingW(
        INVALID_HANDLE_VALUE,  // Use pagefile-backed memory
        nullptr,               // Default security
        PAGE_READWRITE,        // Read/write access
        static_cast<DWORD>(producer->total_size_ >> 32),
        static_cast<DWORD>(producer->total_size_),
        wname.c_str());

    if (h_map == nullptr) {
        auto err = GetLastError();
        LOG_ERROR(kLogComponent, "CreateFileMapping failed: %lu", err);
        store_error(out_error, std::error_code(static_cast<int>(err), std::system_category()));
        return nullptr;
    }
    producer->mapping_ = h_map;

    void* ptr = MapViewOfFile(h_map, FILE_MAP_ALL_ACCESS, 0, 0, producer->total_size_);
    if (ptr == nullptr) {
        auto err = GetLastError();
        CloseHandle(h_map);
        LOG_ERROR(kLogComponent, "MapViewOfFile failed: %lu", err);
        store_error(out_error, std::error_code(static_cast<int>(err), std::system_category()));
        return nullptr;
    }

    // Create semaphore for signaling
    std::wstring sem_name = wname + L"_sem";
    HANDLE h_sem = CreateSemaphoreW(nullptr, 0, LONG_MAX, sem_name.c_str());
    if (h_sem == nullptr) {
        auto err = GetLastError();
        UnmapViewOfFile(ptr);
        CloseHandle(h_map);
        LOG_ERROR(kLogComponent, "CreateSemaphore failed: %lu", err);
        store_error(out_error, std::error_code(static_cast<int>(err), std::system_category()));
        return nullptr;
    }
    producer->signal_semaphore_ = h_sem;

    producer->header_ = static_cast<SharedMemoryHeader*>(ptr);

#else
    // POSIX implementation using shm_open
    std::string shm_name = "/" + producer->name_;

    int fd = shm_open(shm_name.c_str(), O_CREAT | O_RDWR, 0666);
    if (fd < 0) {
        auto err = errno;
        LOG_ERROR(kLogComponent, "shm_open failed: %s", strerror(err));
        store_error(out_error, std::error_code(err, std::generic_category()));
        return nullptr;
    }

    if (ftruncate(fd, static_cast<off_t>(producer->total_size_)) < 0) {
        auto err = errno;
        close(fd);
        shm_unlink(shm_name.c_str());
        LOG_ERROR(kLogComponent, "ftruncate failed: %s", strerror(err));
        store_error(out_error, std::error_code(err, std::generic_category()));
        return nullptr;
    }

    void* ptr = mmap(nullptr, producer->total_size_, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    close(fd);  // fd no longer needed after mmap

    if (ptr == MAP_FAILED) {
        auto err = errno;
        shm_unlink(shm_name.c_str());
        LOG_ERROR(kLogComponent, "mmap failed: %s", strerror(err));
        store_error(out_error, std::error_code(err, std::generic_category()));
        return nullptr;
    }
    producer->mapping_ = ptr;
    producer->header_ = static_cast<SharedMemoryHeader*>(ptr);

    // Create named semaphore
    std::string sem_name = shm_name + "_sem";
    sem_t* sem = sem_open(sem_name.c_str(), O_CREAT, 0666, 0);
    if (sem == SEM_FAILED) {
        auto err = errno;
        munmap(ptr, producer->total_size_);
        shm_unlink(shm_name.c_str());
        LOG_ERROR(kLogComponent, "sem_open failed: %s", strerror(err));
        store_error(out_error, std::error_code(err, std::generic_category()));
        return nullptr;
    }
    producer->signal_semaphore_ = sem;
#endif

    // Byte-level offset from header to data region
    auto* base = static_cast<std::byte*>(static_cast<void*>(producer->header_));
    producer->data_region_ = base + SHM_HEADER_SIZE;

    // Zero the entire region (SIMD-optimized for large shared memory regions ~1MB+)
    platform::simd_memset(producer->header_, 0, producer->total_size_);

    // Initialize header metadata
    producer->header_->magic = SHM_MAGIC;
    producer->header_->version = SHM_PROTOCOL_VERSION;
    producer->header_->header_size = SHM_HEADER_SIZE;
    producer->header_->buffer_capacity = slot_count * slot_size;
    producer->header_->slot_count = slot_count;
    producer->header_->slot_size = static_cast<std::uint32_t>(slot_size);
    producer->header_->ts_packet_size = TS_PACKET_SIZE;

    // Initialize atomic fields
    producer->header_->write_sequence.store(0, std::memory_order_release);
    producer->header_->write_position.store(0, std::memory_order_release);
    producer->header_->producer_state.store(
        static_cast<std::uint64_t>(ProducerState::Initializing), std::memory_order_release);
    producer->header_->last_write_timestamp.store(get_timestamp_ns(), std::memory_order_release);
    producer->header_->total_bytes_written.store(0, std::memory_order_release);
    producer->header_->total_packets_written.store(0, std::memory_order_release);
    producer->header_->write_wrap_count.store(0, std::memory_order_release);

    producer->header_->read_sequence.store(0, std::memory_order_release);
    producer->header_->read_position.store(0, std::memory_order_release);
    producer->header_->consumer_state.store(
        static_cast<std::uint64_t>(ConsumerState::Unattached), std::memory_order_release);

    producer->header_->flags.store(
        static_cast<std::uint32_t>(SharedMemoryFlags::ProducerReady), std::memory_order_release);
    producer->header_->error_code.store(0, std::memory_order_release);

    LOG_INFO(kLogComponent, "Shared memory '%s' created successfully", producer->name_.c_str());
    return producer;
}

SharedMemoryProducer::~SharedMemoryProducer() {
    cleanup();
}

SharedMemoryProducer::SharedMemoryProducer(SharedMemoryProducer&& other) noexcept
    : name_(std::move(other.name_))
    , mapping_(other.mapping_)
    , header_(other.header_)
    , data_region_(other.data_region_)
    , total_size_(other.total_size_)
    , slot_count_(other.slot_count_)
    , slot_size_(other.slot_size_)
    , slot_mask_(other.slot_mask_)
    , signal_semaphore_(other.signal_semaphore_)
{
    other.mapping_ = nullptr;
    other.header_ = nullptr;
    other.data_region_ = nullptr;
    other.signal_semaphore_ = nullptr;
}

SharedMemoryProducer& SharedMemoryProducer::operator=(SharedMemoryProducer&& other) noexcept {
    if (this != &other) {
        cleanup();

        name_ = std::move(other.name_);
        mapping_ = other.mapping_;
        header_ = other.header_;
        data_region_ = other.data_region_;
        total_size_ = other.total_size_;
        slot_count_ = other.slot_count_;
        slot_size_ = other.slot_size_;
        slot_mask_ = other.slot_mask_;
        signal_semaphore_ = other.signal_semaphore_;

        other.mapping_ = nullptr;
        other.header_ = nullptr;
        other.data_region_ = nullptr;
        other.signal_semaphore_ = nullptr;
    }
    return *this;
}

void SharedMemoryProducer::cleanup() noexcept {
    if (header_ != nullptr) {
        header_->producer_state.store(
            static_cast<std::uint64_t>(ProducerState::Stopped), std::memory_order_release);
        header_->flags.fetch_and(
            ~static_cast<std::uint32_t>(SharedMemoryFlags::ProducerReady),
            std::memory_order_release);
    }

#ifdef _WIN32
    if (signal_semaphore_ != nullptr) {
        CloseHandle(static_cast<HANDLE>(signal_semaphore_));
    }
    if (header_ != nullptr) {
        UnmapViewOfFile(header_);
    }
    if (mapping_ != nullptr) {
        CloseHandle(static_cast<HANDLE>(mapping_));
    }
#else
    if (signal_semaphore_ != nullptr) {
        sem_close(static_cast<sem_t*>(signal_semaphore_));
        std::string sem_name = "/" + name_ + "_sem";
        sem_unlink(sem_name.c_str());
    }
    if (mapping_ != nullptr) {
        munmap(mapping_, total_size_);
        std::string shm_name = "/" + name_;
        shm_unlink(shm_name.c_str());
    }
#endif

    header_ = nullptr;
    data_region_ = nullptr;
    mapping_ = nullptr;
    signal_semaphore_ = nullptr;

    LOG_DEBUG(kLogComponent, "Shared memory '%s' cleaned up", name_.c_str());
}

WriteResult SharedMemoryProducer::write(std::span<const std::byte> data) noexcept {
    WriteResult result{0, 0, false};

    if (data.empty() || header_ == nullptr) {
        return result;
    }

    // Ensure data is TS packet aligned
    if ((data.size() % TS_PACKET_SIZE) != 0) {
        LOG_WARNING(kLogComponent, "Write data not TS packet aligned: %zu bytes", data.size());
        return result;
    }

    const std::size_t packets_to_write = data.size() / TS_PACKET_SIZE;
    const std::size_t packets_per_slot = slot_size_ / TS_PACKET_SIZE;

    // Load write_pos first (we own this), then acquire read_pos
    std::uint64_t write_pos = header_->write_position.load(std::memory_order_relaxed);
    std::uint64_t read_pos = header_->read_position.load(std::memory_order_acquire);

    // Calculate available slots (leave one slot empty to distinguish full from empty)
    std::uint64_t used_slots = write_pos - read_pos;
    std::uint64_t available_slots = slot_count_ - 1 - used_slots;

    if (available_slots == 0) {
        // Buffer full - advance read position to make room (drop oldest data).
        // This is an acceptable SPSC pattern because:
        // 1. Producer only advances read_pos forward, never backward
        // 2. Consumer uses acquire load and will see the new position
        // 3. Overflow flag signals to consumer that data was lost
        result.overflow = true;
        header_->flags.fetch_or(
            static_cast<std::uint32_t>(SharedMemoryFlags::Overflow),
            std::memory_order_release);

        // Calculate how many slots we need and advance read_position to make room
        std::uint64_t slots_needed = (packets_to_write + packets_per_slot - 1) / packets_per_slot;
        header_->read_position.store(read_pos + slots_needed, std::memory_order_release);
        available_slots = slots_needed;

        LOG_WARNING(kLogComponent, "Buffer overflow, dropped %zu slots of data", slots_needed);
    }

    // Track previous write_pos for wrap detection
    const std::uint64_t prev_wrap_epoch = write_pos / slot_count_;

    // Write data in slot-sized chunks
    const std::byte* src = data.data();
    std::size_t remaining = data.size();
    std::size_t slot_index = write_pos & slot_mask_;

    while (remaining > 0 && available_slots > 0) {
        std::size_t to_copy = std::min(remaining, slot_size_);

        // Overflow check: slot_index * slot_size_ could overflow for very large values
        if (slot_index > SIZE_MAX / slot_size_) {
            LOG_ERROR(kLogComponent, "slot offset calculation would overflow: slot_index=%zu slot_size=%zu",
                      slot_index, slot_size_);
            break;
        }
        std::byte* dest = data_region_ + (slot_index * slot_size_);

        // SIMD-optimized copy for streaming hot path
        platform::simd_memcpy(dest, src, to_copy);

        // Pad remainder of slot with 0xFF (invalid sync byte pattern)
        if (to_copy < slot_size_) {
            platform::simd_memset(dest + to_copy, 0xFF, slot_size_ - to_copy);
        }

        src += to_copy;
        remaining -= to_copy;
        result.bytes_written += to_copy;
        result.packets_written += to_copy / TS_PACKET_SIZE;

        slot_index = (slot_index + 1) & slot_mask_;
        write_pos++;
        available_slots--;
    }

    // Single release fence before updating write_position ensures all data writes are visible
    // before the consumer sees the new position. This is the only barrier needed for correctness.
    header_->write_position.store(write_pos, std::memory_order_release);

    // Statistics updates: use relaxed ordering as these are diagnostic only.
    // Batch updates to minimize cache line traffic.
    // Note: write_sequence is only needed if consumer uses seqlock validation.
    header_->write_sequence.fetch_add(1, std::memory_order_relaxed);
    header_->total_bytes_written.fetch_add(result.bytes_written, std::memory_order_relaxed);
    header_->total_packets_written.fetch_add(result.packets_written, std::memory_order_relaxed);

    // Track wrap count correctly: only increment when we cross a wrap boundary
    const std::uint64_t new_wrap_epoch = write_pos / slot_count_;
    if (new_wrap_epoch > prev_wrap_epoch) {
        header_->write_wrap_count.fetch_add(
            static_cast<std::uint64_t>(new_wrap_epoch - prev_wrap_epoch),
            std::memory_order_relaxed);
    }

    // Defer timestamp update: only update periodically to avoid syscall overhead.
    // Consumer can use write_sequence change as a staleness indicator.
    // Update timestamp every 16 writes (amortize syscall cost).
    if ((result.packets_written > 0) && ((header_->write_sequence.load(std::memory_order_relaxed) & 0xF) == 0)) {
        header_->last_write_timestamp.store(get_timestamp_ns(), std::memory_order_relaxed);
    }

    return result;
}

void SharedMemoryProducer::signal_data_available() noexcept {
    if (signal_semaphore_ == nullptr) {
        return;
    }

#ifdef _WIN32
    ReleaseSemaphore(static_cast<HANDLE>(signal_semaphore_), 1, nullptr);
#else
    sem_post(static_cast<sem_t*>(signal_semaphore_));
#endif
}

void SharedMemoryProducer::set_end_of_stream() noexcept {
    if (header_ == nullptr) {
        return;
    }

    header_->flags.fetch_or(
        static_cast<std::uint32_t>(SharedMemoryFlags::EndOfStream),
        std::memory_order_release);
    signal_data_available();

    LOG_INFO(kLogComponent, "End of stream signaled for '%s'", name_.c_str());
}

void SharedMemoryProducer::set_error(SharedMemoryError code, std::string_view message) noexcept {
    if (header_ == nullptr) {
        return;
    }

    header_->error_code.store(static_cast<std::uint32_t>(code), std::memory_order_release);
    header_->error_timestamp.store(get_timestamp_ns(), std::memory_order_release);

    std::size_t len = std::min(message.size(), sizeof(header_->error_message) - 1);
    std::memcpy(header_->error_message, message.data(), len);
    header_->error_message[len] = '\0';

    header_->flags.fetch_or(
        static_cast<std::uint32_t>(SharedMemoryFlags::Error),
        std::memory_order_release);
    signal_data_available();

    LOG_ERROR(kLogComponent, "Error set for '%s': code=%u, message='%.*s'",
              name_.c_str(), static_cast<unsigned>(code),
              static_cast<int>(message.size()), message.data());
}

void SharedMemoryProducer::set_discontinuity() noexcept {
    if (header_ == nullptr) {
        return;
    }

    header_->flags.fetch_or(
        static_cast<std::uint32_t>(SharedMemoryFlags::Discontinuity),
        std::memory_order_release);

    LOG_DEBUG(kLogComponent, "Discontinuity signaled for '%s'", name_.c_str());
}

void SharedMemoryProducer::clear_discontinuity() noexcept {
    if (header_ == nullptr) {
        return;
    }

    header_->flags.fetch_and(
        ~static_cast<std::uint32_t>(SharedMemoryFlags::Discontinuity),
        std::memory_order_release);
}

void SharedMemoryProducer::set_state(ProducerState state) noexcept {
    if (header_ == nullptr) {
        return;
    }

    header_->producer_state.store(static_cast<std::uint64_t>(state), std::memory_order_release);
    LOG_DEBUG(kLogComponent, "Producer state changed to %u for '%s'",
              static_cast<unsigned>(state), name_.c_str());
}

bool SharedMemoryProducer::is_consumer_attached() const noexcept {
    if (header_ == nullptr) {
        return false;
    }

    auto flags = header_->flags.load(std::memory_order_acquire);
    return (flags & static_cast<std::uint32_t>(SharedMemoryFlags::ConsumerReady)) != 0;
}

bool SharedMemoryProducer::wait_for_consumer(int timeout_ms) noexcept {
    if (header_ == nullptr) {
        return false;
    }

    using namespace std::chrono;
    auto deadline = timeout_ms < 0
        ? steady_clock::time_point::max()
        : steady_clock::now() + milliseconds(timeout_ms);

    while (steady_clock::now() < deadline) {
        if (is_consumer_attached()) {
            return true;
        }
        std::this_thread::sleep_for(milliseconds(10));
    }

    return false;
}

std::size_t SharedMemoryProducer::available_write_space() const noexcept {
    if (header_ == nullptr) {
        return 0;
    }

    std::uint64_t write_pos = header_->write_position.load(std::memory_order_relaxed);
    std::uint64_t read_pos = header_->read_position.load(std::memory_order_acquire);
    std::uint64_t used_slots = write_pos - read_pos;
    std::uint64_t available_slots = slot_count_ - 1 - used_slots;
    return available_slots * slot_size_;
}

SharedMemoryStatistics SharedMemoryProducer::get_statistics() const noexcept {
    if (header_ == nullptr) {
        return {};
    }

    std::uint64_t bytes_written = header_->total_bytes_written.load(std::memory_order_acquire);
    std::uint64_t bytes_read = header_->total_bytes_read.load(std::memory_order_acquire);

    return SharedMemoryStatistics{
        .bytes_written = bytes_written,
        .packets_written = header_->total_packets_written.load(std::memory_order_acquire),
        .write_wrap_count = header_->write_wrap_count.load(std::memory_order_acquire),
        .bytes_read = bytes_read,
        .packets_read = header_->total_packets_read.load(std::memory_order_acquire),
        .read_wrap_count = header_->read_wrap_count.load(std::memory_order_acquire),
        .available_data = bytes_written - bytes_read,
    };
}

// ============================================================================
// SharedMemoryConsumer Implementation
// ============================================================================

std::unique_ptr<SharedMemoryConsumer> SharedMemoryConsumer::open(
    std::string_view name,
    std::error_code* out_error)
{
    // Clear any previous error
    store_error(out_error, std::error_code{});

    if (name.empty()) {
        store_error(out_error, std::make_error_code(std::errc::invalid_argument));
        return nullptr;
    }

    auto consumer = std::unique_ptr<SharedMemoryConsumer>(new SharedMemoryConsumer());
    consumer->name_ = std::string(name);

#ifdef _WIN32
    std::wstring wname(name.begin(), name.end());

    HANDLE h_map = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, wname.c_str());
    if (h_map == nullptr) {
        auto err = GetLastError();
        LOG_ERROR(kLogComponent, "OpenFileMapping failed: %lu", err);
        store_error(out_error, std::error_code(static_cast<int>(err), std::system_category()));
        return nullptr;
    }
    consumer->mapping_ = h_map;

    // Map header first to read size
    void* ptr = MapViewOfFile(h_map, FILE_MAP_ALL_ACCESS, 0, 0, SHM_HEADER_SIZE);
    if (ptr == nullptr) {
        auto err = GetLastError();
        CloseHandle(h_map);
        store_error(out_error, std::error_code(static_cast<int>(err), std::system_category()));
        return nullptr;
    }

    auto* temp_header = static_cast<SharedMemoryHeader*>(ptr);

    // Validate magic and version
    if (temp_header->magic != SHM_MAGIC) {
        UnmapViewOfFile(ptr);
        CloseHandle(h_map);
        LOG_ERROR(kLogComponent, "Invalid magic: 0x%llx", temp_header->magic);
        store_error(out_error, std::make_error_code(std::errc::invalid_argument));
        return nullptr;
    }

    if (temp_header->version != SHM_PROTOCOL_VERSION) {
        UnmapViewOfFile(ptr);
        CloseHandle(h_map);
        LOG_ERROR(kLogComponent, "Version mismatch: %u vs %u",
                  temp_header->version, SHM_PROTOCOL_VERSION);
        store_error(out_error, std::make_error_code(std::errc::protocol_error));
        return nullptr;
    }

    consumer->slot_count_ = temp_header->slot_count;
    consumer->slot_size_ = temp_header->slot_size;
    consumer->slot_mask_ = consumer->slot_count_ - 1;
    consumer->total_size_ = SHM_HEADER_SIZE + temp_header->buffer_capacity;

    // Unmap and remap with full size
    UnmapViewOfFile(ptr);
    ptr = MapViewOfFile(h_map, FILE_MAP_ALL_ACCESS, 0, 0, consumer->total_size_);
    if (ptr == nullptr) {
        auto err = GetLastError();
        CloseHandle(h_map);
        store_error(out_error, std::error_code(static_cast<int>(err), std::system_category()));
        return nullptr;
    }

    consumer->header_ = static_cast<SharedMemoryHeader*>(ptr);
    consumer->data_region_ = static_cast<std::byte*>(ptr) + SHM_HEADER_SIZE;

    // Open signaling semaphore
    std::wstring sem_name = wname + L"_sem";
    HANDLE h_sem = OpenSemaphoreW(SEMAPHORE_ALL_ACCESS, FALSE, sem_name.c_str());
    consumer->signal_semaphore_ = h_sem;  // May be nullptr if semaphore doesn't exist

#else
    std::string shm_name = "/" + consumer->name_;

    int fd = shm_open(shm_name.c_str(), O_RDWR, 0666);
    if (fd < 0) {
        auto err = errno;
        store_error(out_error, std::error_code(err, std::generic_category()));
        return nullptr;
    }

    // Get size from header
    void* temp_ptr = mmap(nullptr, SHM_HEADER_SIZE, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    if (temp_ptr == MAP_FAILED) {
        auto err = errno;
        close(fd);
        store_error(out_error, std::error_code(err, std::generic_category()));
        return nullptr;
    }

    auto* temp_header = static_cast<SharedMemoryHeader*>(temp_ptr);

    if (temp_header->magic != SHM_MAGIC) {
        munmap(temp_ptr, SHM_HEADER_SIZE);
        close(fd);
        store_error(out_error, std::make_error_code(std::errc::invalid_argument));
        return nullptr;
    }

    if (temp_header->version != SHM_PROTOCOL_VERSION) {
        munmap(temp_ptr, SHM_HEADER_SIZE);
        close(fd);
        store_error(out_error, std::make_error_code(std::errc::protocol_error));
        return nullptr;
    }

    consumer->slot_count_ = temp_header->slot_count;
    consumer->slot_size_ = temp_header->slot_size;
    consumer->slot_mask_ = consumer->slot_count_ - 1;
    consumer->total_size_ = SHM_HEADER_SIZE + temp_header->buffer_capacity;

    munmap(temp_ptr, SHM_HEADER_SIZE);

    // Remap with full size
    void* ptr = mmap(nullptr, consumer->total_size_, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    close(fd);

    if (ptr == MAP_FAILED) {
        auto err = errno;
        store_error(out_error, std::error_code(err, std::generic_category()));
        return nullptr;
    }

    consumer->mapping_ = ptr;
    consumer->header_ = static_cast<SharedMemoryHeader*>(ptr);
    consumer->data_region_ = static_cast<std::byte*>(ptr) + SHM_HEADER_SIZE;

    // Open signaling semaphore
    std::string sem_name = shm_name + "_sem";
    sem_t* sem = sem_open(sem_name.c_str(), 0);
    consumer->signal_semaphore_ = (sem != SEM_FAILED) ? sem : nullptr;
#endif

    // Set consumer ready flag
    consumer->header_->flags.fetch_or(
        static_cast<std::uint32_t>(SharedMemoryFlags::ConsumerReady),
        std::memory_order_release);
    consumer->header_->consumer_state.store(
        static_cast<std::uint64_t>(ConsumerState::Attached),
        std::memory_order_release);

    LOG_INFO(kLogComponent, "Consumer attached to shared memory '%s'", consumer->name_.c_str());
    return consumer;
}

SharedMemoryConsumer::~SharedMemoryConsumer() {
    cleanup();
}

SharedMemoryConsumer::SharedMemoryConsumer(SharedMemoryConsumer&& other) noexcept
    : name_(std::move(other.name_))
    , mapping_(other.mapping_)
    , header_(other.header_)
    , data_region_(other.data_region_)
    , total_size_(other.total_size_)
    , slot_count_(other.slot_count_)
    , slot_size_(other.slot_size_)
    , slot_mask_(other.slot_mask_)
    , signal_semaphore_(other.signal_semaphore_)
{
    other.mapping_ = nullptr;
    other.header_ = nullptr;
    other.data_region_ = nullptr;
    other.signal_semaphore_ = nullptr;
}

SharedMemoryConsumer& SharedMemoryConsumer::operator=(SharedMemoryConsumer&& other) noexcept {
    if (this != &other) {
        cleanup();

        name_ = std::move(other.name_);
        mapping_ = other.mapping_;
        header_ = other.header_;
        data_region_ = other.data_region_;
        total_size_ = other.total_size_;
        slot_count_ = other.slot_count_;
        slot_size_ = other.slot_size_;
        slot_mask_ = other.slot_mask_;
        signal_semaphore_ = other.signal_semaphore_;

        other.mapping_ = nullptr;
        other.header_ = nullptr;
        other.data_region_ = nullptr;
        other.signal_semaphore_ = nullptr;
    }
    return *this;
}

void SharedMemoryConsumer::cleanup() noexcept {
    if (header_ != nullptr) {
        header_->consumer_state.store(
            static_cast<std::uint64_t>(ConsumerState::Detached),
            std::memory_order_release);
        header_->flags.fetch_and(
            ~static_cast<std::uint32_t>(SharedMemoryFlags::ConsumerReady),
            std::memory_order_release);
    }

#ifdef _WIN32
    if (signal_semaphore_ != nullptr) {
        CloseHandle(static_cast<HANDLE>(signal_semaphore_));
    }
    if (header_ != nullptr) {
        UnmapViewOfFile(header_);
    }
    if (mapping_ != nullptr) {
        CloseHandle(static_cast<HANDLE>(mapping_));
    }
#else
    if (signal_semaphore_ != nullptr) {
        sem_close(static_cast<sem_t*>(signal_semaphore_));
    }
    if (mapping_ != nullptr) {
        munmap(mapping_, total_size_);
    }
#endif

    header_ = nullptr;
    data_region_ = nullptr;
    mapping_ = nullptr;
    signal_semaphore_ = nullptr;
}

std::size_t SharedMemoryConsumer::read(std::span<std::byte> buffer) noexcept {
    if (buffer.empty() || header_ == nullptr) {
        return 0;
    }

    header_->consumer_state.store(
        static_cast<std::uint64_t>(ConsumerState::Reading),
        std::memory_order_release);

    std::uint64_t write_pos = header_->write_position.load(std::memory_order_acquire);
    std::uint64_t read_pos = header_->read_position.load(std::memory_order_relaxed);

    std::uint64_t available_slots = write_pos - read_pos;
    if (available_slots == 0) {
        return 0;
    }

    // Calculate bytes to read (aligned to TS packets)
    std::size_t bytes_to_read = std::min(
        static_cast<std::size_t>(available_slots * slot_size_),
        buffer.size());
    bytes_to_read = (bytes_to_read / TS_PACKET_SIZE) * TS_PACKET_SIZE;

    if (bytes_to_read == 0) {
        return 0;
    }

    std::size_t total_read = 0;
    std::size_t slot_index = read_pos & slot_mask_;

    while (total_read < bytes_to_read) {
        std::size_t chunk_size = std::min(bytes_to_read - total_read, slot_size_);
        const std::byte* src = data_region_ + (slot_index * slot_size_);

        // SIMD-optimized copy for streaming hot path
        platform::simd_memcpy(buffer.data() + total_read, src, chunk_size);
        total_read += chunk_size;
        slot_index = (slot_index + 1) & slot_mask_;
        read_pos++;
    }

    // Update read position with release semantics
    header_->read_position.store(read_pos, std::memory_order_release);
    header_->last_read_timestamp.store(get_timestamp_ns(), std::memory_order_release);
    header_->total_bytes_read.fetch_add(total_read, std::memory_order_relaxed);
    header_->total_packets_read.fetch_add(total_read / TS_PACKET_SIZE, std::memory_order_relaxed);

    return total_read;
}

bool SharedMemoryConsumer::wait_for_data(int timeout_ms) noexcept {
    if (header_ == nullptr) {
        return false;
    }

    // Quick check first
    if (available_data() > 0 || is_end_of_stream() || has_error()) {
        return true;
    }

    if (signal_semaphore_ != nullptr) {
#ifdef _WIN32
        DWORD wait_ms = timeout_ms < 0 ? INFINITE : static_cast<DWORD>(timeout_ms);
        return WaitForSingleObject(static_cast<HANDLE>(signal_semaphore_), wait_ms) == WAIT_OBJECT_0;
#else
        if (timeout_ms < 0) {
            return sem_wait(static_cast<sem_t*>(signal_semaphore_)) == 0;
        } else {
            struct timespec ts;
            clock_gettime(CLOCK_REALTIME, &ts);
            ts.tv_sec += timeout_ms / 1000;
            ts.tv_nsec += (timeout_ms % 1000) * 1000000;
            if (ts.tv_nsec >= 1000000000) {
                ts.tv_sec++;
                ts.tv_nsec -= 1000000000;
            }
            return sem_timedwait(static_cast<sem_t*>(signal_semaphore_), &ts) == 0;
        }
#endif
    }

    // Fallback to spin-wait
    using namespace std::chrono;
    auto deadline = timeout_ms < 0
        ? steady_clock::time_point::max()
        : steady_clock::now() + milliseconds(timeout_ms);

    while (steady_clock::now() < deadline) {
        if (available_data() > 0 || is_end_of_stream() || has_error()) {
            return true;
        }
        std::this_thread::sleep_for(milliseconds(1));
    }

    return false;
}

std::size_t SharedMemoryConsumer::available_data() const noexcept {
    if (header_ == nullptr) {
        return 0;
    }

    std::uint64_t write_pos = header_->write_position.load(std::memory_order_acquire);
    std::uint64_t read_pos = header_->read_position.load(std::memory_order_relaxed);
    return static_cast<std::size_t>((write_pos - read_pos) * slot_size_);
}

bool SharedMemoryConsumer::is_end_of_stream() const noexcept {
    if (header_ == nullptr) {
        return false;
    }

    auto flags = header_->flags.load(std::memory_order_acquire);
    return (flags & static_cast<std::uint32_t>(SharedMemoryFlags::EndOfStream)) != 0;
}

bool SharedMemoryConsumer::has_error() const noexcept {
    if (header_ == nullptr) {
        return false;
    }

    auto flags = header_->flags.load(std::memory_order_acquire);
    return (flags & static_cast<std::uint32_t>(SharedMemoryFlags::Error)) != 0;
}

bool SharedMemoryConsumer::consume_discontinuity() noexcept {
    if (header_ == nullptr) {
        return false;
    }

    auto flags = header_->flags.load(std::memory_order_acquire);
    if ((flags & static_cast<std::uint32_t>(SharedMemoryFlags::Discontinuity)) != 0) {
        header_->flags.fetch_and(
            ~static_cast<std::uint32_t>(SharedMemoryFlags::Discontinuity),
            std::memory_order_release);
        return true;
    }
    return false;
}

SharedMemoryError SharedMemoryConsumer::get_error_code() const noexcept {
    if (header_ == nullptr) {
        return SharedMemoryError::None;
    }
    return static_cast<SharedMemoryError>(header_->error_code.load(std::memory_order_acquire));
}

std::string_view SharedMemoryConsumer::get_error_message() const noexcept {
    if (header_ == nullptr) {
        return {};
    }
    return header_->error_message;
}

ProducerState SharedMemoryConsumer::get_producer_state() const noexcept {
    if (header_ == nullptr) {
        return ProducerState::Failed;
    }
    return static_cast<ProducerState>(header_->producer_state.load(std::memory_order_acquire));
}

SharedMemoryStatistics SharedMemoryConsumer::get_statistics() const noexcept {
    if (header_ == nullptr) {
        return {};
    }

    std::uint64_t bytes_written = header_->total_bytes_written.load(std::memory_order_acquire);
    std::uint64_t bytes_read = header_->total_bytes_read.load(std::memory_order_acquire);

    return SharedMemoryStatistics{
        .bytes_written = bytes_written,
        .packets_written = header_->total_packets_written.load(std::memory_order_acquire),
        .write_wrap_count = header_->write_wrap_count.load(std::memory_order_acquire),
        .bytes_read = bytes_read,
        .packets_read = header_->total_packets_read.load(std::memory_order_acquire),
        .read_wrap_count = header_->read_wrap_count.load(std::memory_order_acquire),
        .available_data = bytes_written - bytes_read,
    };
}

}  // namespace tsduck_interop::ipc
