// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_STREAMING_ALIGNMENT_BUFFER_HPP
#define TSDUCK_INTEROP_STREAMING_ALIGNMENT_BUFFER_HPP

#include <cstring>
#include <cstdint>
#include <vector>
#include "../core/constants.hpp"

namespace tsduck_interop::streaming {

// ============================================================================
// AlignmentBuffer: Accumulates arbitrary-sized curl chunks into TS-aligned data
// ============================================================================
//
// libcurl delivers data in arbitrary chunk sizes (typically 16KB-64KB).
// MPEG-TS requires 188-byte packet alignment for processing.
// This buffer accumulates partial data and emits aligned chunks.
//
// The buffer also handles initial sync byte detection: if the stream
// starts with garbage (partial packet), it scans for the first valid
// sync byte pair before emitting data.

class AlignmentBuffer {
public:
    // Represents an aligned chunk of TS packets ready for processing
    struct AlignedChunk {
        uint8_t* data;   // Pointer into output buffer (valid until next append())
        int32_t length;  // Byte count, always multiple of ts::PKT_SIZE (or 0)
    };

    explicit AlignmentBuffer(int32_t max_packets = 32) noexcept
        : buffer_(static_cast<size_t>(max_packets + 1) * ts::PKT_SIZE),
          output_buffer_(static_cast<size_t>(max_packets) * ts::PKT_SIZE), pending_bytes_(0), synced_(false) {}

    /// Append raw data from curl write callback.
    /// Returns an AlignedChunk pointing to complete TS packets.
    /// The returned pointer is valid until the next call to append() or reset().
    /// Remaining partial data is kept in the buffer for the next append().
    AlignedChunk append(const uint8_t* data, size_t size) noexcept {
        AlignedChunk result{nullptr, 0};

        if (!data || size == 0) {
            return result;
        }

        // Ensure we have space (grow if needed)
        size_t required = static_cast<size_t>(pending_bytes_) + size;
        if (required > buffer_.size()) {
            buffer_.resize(required + ts::PKT_SIZE);
        }

        // Append new data after pending bytes
        // flawfinder: ignore - bounds checked by resize() above ensuring buffer_ can hold required bytes
        std::memcpy(buffer_.data() + pending_bytes_, data, size);
        pending_bytes_ += static_cast<int32_t>(size);

        // Find aligned start
        int32_t start = find_sync_start();
        if (start < 0) {
            // No sync found yet, keep accumulating
            return result;
        }

        // Calculate how many complete packets we have from sync point
        int32_t available = pending_bytes_ - start;
        int32_t aligned_bytes = (available / static_cast<int32_t>(ts::PKT_SIZE)) * static_cast<int32_t>(ts::PKT_SIZE);

        if (aligned_bytes <= 0) {
            return result;
        }

        // Copy aligned data to output buffer BEFORE moving remainder
        // This prevents corruption when memmove overwrites the source data
        if (static_cast<size_t>(aligned_bytes) > output_buffer_.size()) {
            output_buffer_.resize(static_cast<size_t>(aligned_bytes));
        }
        // flawfinder: ignore - bounds checked by resize() above ensuring output_buffer_ can hold aligned_bytes
        std::memcpy(output_buffer_.data(), buffer_.data() + start, aligned_bytes);

        result.data = output_buffer_.data();
        result.length = aligned_bytes;

        // Move remainder to front of buffer (safe now - output is in separate buffer)
        int32_t remainder = available - aligned_bytes;
        if (remainder > 0) {
            std::memmove(buffer_.data(), buffer_.data() + start + aligned_bytes, remainder);
        }
        pending_bytes_ = remainder;

        return result;
    }

    /// Reset buffer state (discard pending data, lose sync)
    void reset() noexcept {
        pending_bytes_ = 0;
        synced_ = false;
    }

    /// Get number of pending (unaligned) bytes
    int32_t pending_bytes() const noexcept { return pending_bytes_; }

    /// Check if sync has been established
    bool is_synced() const noexcept { return synced_; }

private:
    std::vector<uint8_t> buffer_;         // Accumulates incoming data
    std::vector<uint8_t> output_buffer_;  // Holds aligned output (survives memmove)
    int32_t pending_bytes_;
    bool synced_;

    /// Find the first valid sync byte position.
    /// A valid sync is confirmed by checking that the byte at offset + 188
    /// is also a sync byte (if available). Returns -1 if no sync found.
    int32_t find_sync_start() noexcept {
        if (synced_) {
            // Already synced: data starts at 0 (remainder from last time)
            if (pending_bytes_ >= static_cast<int32_t>(ts::PKT_SIZE) && buffer_[0] == ts::SYNC_BYTE) {
                return 0;
            }
            // Lost sync - need to re-acquire
            synced_ = false;
        }

        // Scan for sync byte pair
        int32_t scan_limit = pending_bytes_ - static_cast<int32_t>(ts::PKT_SIZE);
        for (int32_t i = 0; i <= scan_limit; i++) {
            if (buffer_[i] == ts::SYNC_BYTE) {
                // Verify: next packet boundary also has sync byte (if we have enough data)
                int32_t next = i + static_cast<int32_t>(ts::PKT_SIZE);
                if (next < pending_bytes_) {
                    if (buffer_[next] == ts::SYNC_BYTE) {
                        synced_ = true;
                        return i;
                    }
                } else {
                    // Can't verify, but we have at least one packet.
                    // Accept if this is the only candidate.
                    synced_ = true;
                    return i;
                }
            }
        }

        // No sync found. If we have more than 2 packets of unsynced data,
        // discard the oldest part to avoid unbounded growth.
        if (pending_bytes_ > static_cast<int32_t>(ts::PKT_SIZE) * 3) {
            int32_t discard = pending_bytes_ - static_cast<int32_t>(ts::PKT_SIZE);
            std::memmove(buffer_.data(), buffer_.data() + discard, ts::PKT_SIZE);
            pending_bytes_ = static_cast<int32_t>(ts::PKT_SIZE);
        }

        return -1;
    }
};

}  // namespace tsduck_interop::streaming

#endif  // TSDUCK_INTEROP_STREAMING_ALIGNMENT_BUFFER_HPP
