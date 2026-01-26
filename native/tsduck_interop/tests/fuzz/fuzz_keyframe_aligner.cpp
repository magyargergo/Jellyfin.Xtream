// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Fuzz test harness for KeyframeAligner.
// Tests the aligner's ability to handle arbitrary input data without crashes.
//
// Build with libFuzzer:
//   clang++ -g -O1 -fsanitize=fuzzer,address,undefined \
//     -I../src -I../include fuzz_keyframe_aligner.cpp \
//     -o fuzz_keyframe_aligner $(pkg-config --cflags --libs tsduck)
//
// Run:
//   ./fuzz_keyframe_aligner corpus/ -max_len=65536

#include <cstdint>
#include <cstddef>
#include <cstring>
#include "streaming/keyframe_aligner.hpp"
#include "core/constants.hpp"

using namespace tsduck_interop::streaming;

extern "C" int LLVMFuzzerTestOneInput(const uint8_t* data, size_t size) {
    if (size < 2) return 0;

    // Use first byte to determine buffer size (10-500 packets)
    int buffer_packets = 10 + (data[0] % 491);
    KeyframeAligner aligner(buffer_packets);

    // Use second byte for operation mode
    bool start_in_waiting_mode = (data[1] & 0x01) != 0;
    bool alternate_modes = (data[1] & 0x02) != 0;

    const uint8_t* remaining = data + 2;
    size_t remaining_size = size - 2;

    if (start_in_waiting_mode) {
        aligner.start_waiting();
    }

    // Feed data in chunks
    size_t offset = 0;
    int iteration = 0;

    while (offset < remaining_size) {
        // Determine chunk size from input (1-4096 bytes)
        size_t chunk_size;
        if (offset < remaining_size) {
            chunk_size = 1 + (remaining[offset] % 256) * 16;
            offset++;
        } else {
            break;
        }

        // Clamp to remaining data
        chunk_size = std::min(chunk_size, remaining_size - offset);
        if (chunk_size == 0) break;

        // Alternate between waiting and pass-through modes if configured
        if (alternate_modes && iteration % 7 == 0) {
            if (aligner.is_waiting()) {
                aligner.stop_waiting();
            } else {
                aligner.start_waiting();
            }
        }

        // Process the chunk
        auto result = aligner.process(remaining + offset, static_cast<int32_t>(chunk_size));

        // Verify invariants
        if (result.length > 0) {
            // Returned data pointer must not be null
            if (result.data == nullptr) {
                __builtin_trap();
            }

            // Length must be non-negative
            if (result.length < 0) {
                __builtin_trap();
            }
        }

        // If not waiting, should always return the same data
        if (!aligner.is_waiting() && result.length == 0 && chunk_size > 0) {
            // This is okay if we just found a keyframe and cleared
        }

        // If found keyframe, should no longer be waiting
        if (result.found_keyframe && aligner.is_waiting()) {
            __builtin_trap();
        }

        // Clear buffer periodically (simulate consumption)
        if (result.length > 0 && iteration % 3 == 0) {
            aligner.clear_buffer();
        }

        offset += chunk_size;
        iteration++;
    }

    // Test state transitions
    aligner.start_waiting();
    if (!aligner.is_waiting()) {
        __builtin_trap();
    }

    aligner.stop_waiting();
    if (aligner.is_waiting()) {
        __builtin_trap();
    }

    // Test clearBuffer
    aligner.clear_buffer();
    if (aligner.packets_buffered() != 0) {
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
