// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Fuzz test harness for PidTracker.
// Tests the tracker's ability to handle arbitrary PID sequences.
//
// Build with libFuzzer:
//   clang++ -g -O1 -fsanitize=fuzzer,address,undefined \
//     -I../src -I../include $(pkg-config --cflags --libs tsduck) \
//     fuzz_pid_tracker.cpp -o fuzz_pid_tracker
//
// Run:
//   ./fuzz_pid_tracker corpus/ -max_len=16384

#include <cstdint>
#include <cstddef>
#include <cstring>
#include "analysis/pid_tracker.hpp"
#include "core/constants.hpp"

using namespace tsduck_interop::analysis;

// Command encoding:
// Byte 0: Command type
//   0x00-0x7F: processPacket with PID from next 2 bytes
//   0x80-0xBF: reset
//   0xC0-0xFF: query getCount

extern "C" int LLVMFuzzerTestOneInput(const uint8_t* data, size_t size) {
    if (size == 0) return 0;

    PidTracker tracker;
    size_t offset = 0;
    int64_t time_ns = 0;

    while (offset < size) {
        uint8_t cmd = data[offset++];

        if (cmd < 0x80) {
            // processPacket: need 4 more bytes (PID + cc + flags)
            if (offset + 4 > size) break;

            uint16_t pid = static_cast<uint16_t>((data[offset] << 8) | data[offset + 1]);
            pid &= 0x1FFF;  // Valid PID range
            uint8_t cc = data[offset + 2] & 0x0F;
            uint8_t flags = data[offset + 3];
            offset += 4;

            bool has_payload = (flags & 0x01) != 0;
            bool scrambled = (flags & 0x02) != 0;

            tracker.processPacket(pid, cc, has_payload, scrambled, time_ns++);
        } else if (cmd < 0xC0) {
            // reset
            tracker.reset();

            // Verify invariants after reset
            TsDuckPidInfoExtended pids[8192];
            int32_t count = tracker.getCount(pids, 8192);
            if (count != 0) {
                __builtin_trap();
            }
        } else {
            // query getCount
            TsDuckPidInfoExtended pids[8192];
            int32_t count = tracker.getCount(pids, 8192);

            // Verify count is valid
            if (count < 0 || count > 8192) {
                __builtin_trap();
            }

            // Verify all returned entries are valid
            for (int32_t i = 0; i < count; i++) {
                if (pids[i].pid > 0x1FFF) {
                    __builtin_trap();
                }
                if (pids[i].packets < 0) {
                    __builtin_trap();
                }
            }
        }
    }

    // Final verification
    TsDuckPidInfoExtended final_pids[8192];
    int32_t final_count = tracker.getCount(final_pids, 8192);

    if (final_count < 0 || final_count > 8192) {
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
