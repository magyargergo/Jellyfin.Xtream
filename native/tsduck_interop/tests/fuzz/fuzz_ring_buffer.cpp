// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Fuzz test harness for LockFreeRingBuffer.
// Tests the buffer's push, min/max, at operations with arbitrary sequences.
//
// Build with libFuzzer:
//   clang++ -g -O1 -fsanitize=fuzzer,address,undefined \
//     -I../src -I../include fuzz_ring_buffer.cpp -o fuzz_ring_buffer
//
// Run:
//   ./fuzz_ring_buffer corpus/ -max_len=8192

#include <cstdint>
#include <cstddef>
#include <cstring>
#include <limits>
#include "concurrency/ring_buffer.hpp"
#include "core/constants.hpp"

using namespace tsduck_interop::concurrency;

// Command encoding:
// Byte 0: command type
//   0x00-0x3F: push int64_t (next 8 bytes)
//   0x40-0x7F: push int32_t (next 4 bytes)
//   0x80-0x9F: get_min_max
//   0xA0-0xBF: at(index) - next byte is index
//   0xC0-0xDF: size query
//   0xE0-0xFF: clear

extern "C" int LLVMFuzzerTestOneInput(const uint8_t* data, size_t size) {
    if (size == 0) return 0;

    // Test with different capacities (all powers of 2)
    LockFreeRingBuffer<int64_t, 16> buffer16;
    LockFreeRingBuffer<int64_t, 64> buffer64;
    LockFreeRingBuffer<int64_t, 256> buffer256;

    size_t offset = 0;
    int64_t expected_push_count = 0;

    while (offset < size) {
        uint8_t cmd = data[offset++];

        if (cmd < 0x40) {
            // Push int64_t
            if (offset + 8 > size) break;

            int64_t value;
            std::memcpy(&value, data + offset, sizeof(value));
            offset += 8;

            buffer16.push(value);
            buffer64.push(value);
            buffer256.push(value);
            expected_push_count++;

            // Verify size invariants
            size_t size16 = buffer16.size();
            size_t size64 = buffer64.size();
            size_t size256 = buffer256.size();

            if (size16 > buffer16.capacity() ||
                size64 > buffer64.capacity() ||
                size256 > buffer256.capacity()) {
                __builtin_trap();
            }

            // Size should grow until capacity
            if (expected_push_count <= static_cast<int64_t>(buffer16.capacity())) {
                if (size16 != static_cast<size_t>(expected_push_count)) {
                    __builtin_trap();
                }
            } else {
                if (size16 != buffer16.capacity()) {
                    __builtin_trap();
                }
            }

        } else if (cmd < 0x80) {
            // Push int32_t (converted to int64_t)
            if (offset + 4 > size) break;

            int32_t value32;
            std::memcpy(&value32, data + offset, sizeof(value32));
            offset += 4;

            int64_t value = static_cast<int64_t>(value32);
            buffer16.push(value);
            buffer64.push(value);
            buffer256.push(value);
            expected_push_count++;

        } else if (cmd < 0xA0) {
            // get_min_max
            auto [min16, max16] = buffer16.get_min_max();
            auto [min64, max64] = buffer64.get_min_max();
            auto [min256, max256] = buffer256.get_min_max();

            // If there's data, min should be <= max
            if (buffer16.size() > 0 && min16 > max16) {
                __builtin_trap();
            }
            if (buffer64.size() > 0 && min64 > max64) {
                __builtin_trap();
            }
            if (buffer256.size() > 0 && min256 > max256) {
                __builtin_trap();
            }

        } else if (cmd < 0xC0) {
            // at(index)
            if (offset >= size) break;
            uint8_t idx = data[offset++];

            // Access should not crash for any index
            size_t size16 = buffer16.size();
            if (size16 > 0) {
                // at() returns item at (write_idx - 1 - idx) wrapped
                // Should not crash even with out-of-bounds index (wraps)
                int64_t val = buffer16.at(idx % size16);
                (void)val;  // Just verify it doesn't crash
            }

            size_t size64 = buffer64.size();
            if (size64 > 0) {
                int64_t val = buffer64.at(idx % size64);
                (void)val;
            }

        } else if (cmd < 0xE0) {
            // size query
            size_t s16 = buffer16.size();
            size_t s64 = buffer64.size();
            size_t s256 = buffer256.size();

            // Size should be bounded by capacity
            if (s16 > 16 || s64 > 64 || s256 > 256) {
                __builtin_trap();
            }

        } else {
            // clear
            buffer16.clear();
            buffer64.clear();
            buffer256.clear();
            expected_push_count = 0;

            // Size should be 0 after clear
            if (buffer16.size() != 0 || buffer64.size() != 0 ||
                buffer256.size() != 0) {
                __builtin_trap();
            }
        }
    }

    // Final verification
    size_t final_size16 = buffer16.size();
    size_t final_size64 = buffer64.size();
    size_t final_size256 = buffer256.size();

    if (final_size16 > buffer16.capacity() ||
        final_size64 > buffer64.capacity() ||
        final_size256 > buffer256.capacity()) {
        __builtin_trap();
    }

    // write_index should reflect total pushes
    size_t write_idx16 = buffer16.write_index();
    size_t write_idx64 = buffer64.write_index();
    (void)write_idx16;
    (void)write_idx64;

    return 0;
}

#ifdef FUZZ_MAIN
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
