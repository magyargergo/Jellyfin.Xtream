// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Fuzz test harness for AlignmentBuffer.
// Tests the buffer's ability to handle arbitrary input data without crashes.
//
// Build with libFuzzer:
//   clang++ -g -O1 -fsanitize=fuzzer,address,undefined \
//     -I../src -I../include fuzz_alignment_buffer.cpp \
//     -o fuzz_alignment_buffer
//
// Run:
//   ./fuzz_alignment_buffer corpus/ -max_len=65536
//
// With AFL++:
//   afl-clang++ -g -O1 -I../src -I../include fuzz_alignment_buffer.cpp \
//     -o fuzz_alignment_buffer_afl
//   afl-fuzz -i corpus/ -o findings/ ./fuzz_alignment_buffer_afl

#include <cstdint>
#include <cstddef>
#include <cstring>
#include "streaming/alignment_buffer.hpp"
#include "core/constants.hpp"

using namespace tsduck_interop::streaming;

extern "C" int LLVMFuzzerTestOneInput(const uint8_t* data, size_t size) {
    if (size == 0) return 0;

    // Use first byte to determine buffer size (4-128 packets)
    int buffer_packets = 4 + (data[0] % 125);
    AlignmentBuffer buffer(buffer_packets);

    // Feed remaining data in various chunk sizes based on input
    const uint8_t* remaining = data + 1;
    size_t remaining_size = size - 1;
    size_t offset = 0;

    while (offset < remaining_size) {
        // Use next byte to determine chunk size (1-256 bytes)
        size_t chunk_size;
        if (offset + 1 < remaining_size) {
            chunk_size = 1 + (remaining[offset] % 256);
            offset++;
        } else {
            chunk_size = remaining_size - offset;
        }

        // Clamp to remaining data
        chunk_size = std::min(chunk_size, remaining_size - offset);

        // Feed the chunk
        auto result = buffer.append(remaining + offset, static_cast<int>(chunk_size));

        // Verify invariants
        if (result.length > 0) {
            // Output length must be multiple of packet size
            if (result.length % ts::PKT_SIZE != 0) {
                __builtin_trap();
            }
            // Output must be aligned (start with sync byte if valid TS)
            // Note: fuzzer may produce invalid TS, so we don't require sync byte
        }

        // Verify pending bytes invariant:
        // - If synced and emitted data: pendingBytes() < PKT_SIZE (partial remainder only)
        // - If not synced: pendingBytes() <= PKT_SIZE * 3 (bounded by discard policy)
        if (buffer.is_synced() && result.length > 0) {
            // After emitting aligned data, only partial packet should remain
            if (buffer.pending_bytes() >= static_cast<int32_t>(ts::PKT_SIZE)) {
                __builtin_trap();
            }
        } else {
            // While searching for sync, bounded by discard policy (max 3 packets)
            if (buffer.pending_bytes() > static_cast<int32_t>(ts::PKT_SIZE) * 3) {
                __builtin_trap();
            }
        }

        offset += chunk_size;
    }

    // Test reset
    buffer.reset();
    if (buffer.pending_bytes() != 0) {
        __builtin_trap();
    }

    return 0;
}

#ifdef FUZZ_MAIN
// Standalone main for AFL and debugging
#include <fstream>
#include <vector>

int main(int argc, char** argv) {
    if (argc < 2) {
        fprintf(stderr, "Usage: %s <input_file>\n", argv[0]);
        return 1;
    }

    std::ifstream file(argv[1], std::ios::binary);
    if (!file) {
        fprintf(stderr, "Failed to open: %s\n", argv[1]);
        return 1;
    }

    std::vector<uint8_t> data((std::istreambuf_iterator<char>(file)),
                               std::istreambuf_iterator<char>());

    return LLVMFuzzerTestOneInput(data.data(), data.size());
}
#endif
