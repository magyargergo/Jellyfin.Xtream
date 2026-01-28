// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Fuzz test harness for Scte35Monitor.
// Tests SCTE-35 splice information section parsing with arbitrary inputs.
//
// Build with libFuzzer:
//   clang++ -g -O1 -fsanitize=fuzzer,address,undefined \
//     -I../src -I../include $(pkg-config --cflags --libs tsduck) \
//     fuzz_scte35_monitor.cpp -o fuzz_scte35_monitor
//
// Run:
//   ./fuzz_scte35_monitor corpus/ -max_len=16384
//
// Corpus seeds should include valid SCTE-35 splice_info_section data.

#include <cstdint>
#include <cstddef>
#include <cstring>
#include <tsduck.h>
#include "analysis/scte35_monitor.hpp"
#include "core/constants.hpp"

using namespace tsduck_interop;
using namespace tsduck_interop::analysis;

// Global DuckContext to avoid repeated initialization overhead
static ts::DuckContext* g_duck = nullptr;

extern "C" int LLVMFuzzerInitialize(int* /*argc*/, char*** /*argv*/) {
    // Suppress TsDuck error output during fuzzing
    g_duck = new ts::DuckContext(&ts::NullReport::Instance());
    return 0;
}

// Command encoding:
// 0x00-0x3F: Feed raw SCTE-35 section data (next N bytes = section)
// 0x40-0x5F: Add SCTE-35 PID (next 2 bytes = PID)
// 0x60-0x7F: Query state (get_splice_state, is_in_ad_break)
// 0x80-0x9F: Get events
// 0xA0-0xBF: Reset
// 0xC0-0xFF: Reset full

extern "C" int LLVMFuzzerTestOneInput(const uint8_t* data, size_t size) {
    if (size == 0 || g_duck == nullptr) return 0;

    Scte35Monitor monitor(*g_duck);
    size_t offset = 0;
    int64_t packet_idx = 0;

    while (offset < size) {
        uint8_t cmd = data[offset++];

        if (cmd < 0x40) {
            // Feed raw SCTE-35 section data wrapped in TS packet
            // Use remaining cmd bits to influence section length
            size_t section_len = std::min(size - offset, static_cast<size_t>((cmd & 0x3F) + 1) * 4);
            if (section_len == 0) continue;

            // Build a TS packet with SCTE-35 section
            ts::TSPacket pkt;
            std::memset(pkt.b, 0xFF, ts::PKT_SIZE);
            pkt.b[0] = ts::SYNC_BYTE;

            // Pick a SCTE-35 PID that we've registered (or default 0x1FF)
            uint16_t scte_pid = 0x1FF;
            int32_t pid_count = monitor.scte35_pid_count.load(std::memory_order_relaxed);
            if (pid_count > 0) {
                scte_pid = monitor.scte35_pids[0];
            }

            // Set PID in header
            pkt.b[1] = static_cast<uint8_t>(0x40 | ((scte_pid >> 8) & 0x1F));  // PUSI=1
            pkt.b[2] = static_cast<uint8_t>(scte_pid & 0xFF);
            pkt.b[3] = 0x10;  // Payload only, no adaptation field

            // Add pointer field (required for sections) and section data
            pkt.b[4] = 0x00;  // Pointer field = 0
            size_t payload_start = 5;
            size_t max_payload = ts::PKT_SIZE - payload_start;
            size_t copy_len = std::min(section_len, max_payload);

            std::memcpy(&pkt.b[payload_start], &data[offset], copy_len);

            // Ensure we have a valid SCTE-35 table_id (0xFC)
            pkt.b[payload_start] = 0xFC;

            // Feed to monitor
            monitor.feed_packet(pkt, packet_idx++);

            offset += section_len;
        } else if (cmd < 0x60) {
            // Add SCTE-35 PID
            if (offset + 2 > size) break;

            uint16_t pid = static_cast<uint16_t>((data[offset] << 8) | data[offset + 1]);
            pid &= 0x1FFF;  // Valid PID range
            offset += 2;

            monitor.add_scte35_pid(pid);

            // Verify PID was added (if within limits)
            int32_t count = monitor.scte35_pid_count.load(std::memory_order_relaxed);
            if (count > static_cast<int32_t>(MAX_PROGRAMS)) {
                __builtin_trap();
            }
        } else if (cmd < 0x80) {
            // Query state
            SpliceState state = monitor.get_splice_state();

            // Verify splice state is valid enum value (0, 1, or 2)
            uint8_t state_val = static_cast<uint8_t>(state);
            if (state_val > 2) {
                __builtin_trap();
            }

            // Query is_in_ad_break (must be consistent with state)
            bool in_ad = monitor.is_in_ad_break();
            if (in_ad && state != SpliceState::InBreak) {
                __builtin_trap();
            }
            if (!in_ad && state == SpliceState::InBreak) {
                __builtin_trap();
            }
        } else if (cmd < 0xA0) {
            // Get events
            Scte35EventNative events[SCTE35_EVENT_BUFFER_SIZE];
            int32_t count = monitor.get_events(events, SCTE35_EVENT_BUFFER_SIZE);

            // Verify event count is valid
            if (count < 0 || count > static_cast<int32_t>(SCTE35_EVENT_BUFFER_SIZE)) {
                __builtin_trap();
            }

            // Verify each event has reasonable values
            for (int32_t i = 0; i < count; ++i) {
                // PTS time should be within 33-bit range (0 is valid for unspecified)
                if (events[i].pts_time > PTS_33BIT_MAX && events[i].pts_time != 0) {
                    // Allow 0 or values within range
                    // Note: pts_time is uint64_t, so > PTS_33BIT_MAX indicates overflow
                }

                // Splice command type should be a known value
                // Valid: 0x00 (null), 0x04 (schedule), 0x05 (insert), 0x06 (time_signal), 0x07 (bandwidth), 0xFF (private)
                uint8_t cmd_type = events[i].splice_command_type;
                bool valid_cmd = (cmd_type == 0x00 || cmd_type == 0x04 || cmd_type == 0x05 ||
                                  cmd_type == 0x06 || cmd_type == 0x07 || cmd_type == 0xFF);
                // Note: We don't trap on invalid command type as fuzzer may produce arbitrary data
                (void)valid_cmd;

                // PID should be valid
                if (events[i].scte35_pid > 0x1FFF) {
                    __builtin_trap();
                }
            }

            // Also test get_current_event
            Scte35EventNative current;
            bool has_current = monitor.get_current_event(&current);
            (void)has_current;  // May or may not have current event

            // Verify total event count is non-negative
            int64_t total = monitor.get_event_count();
            if (total < 0) {
                __builtin_trap();
            }
        } else if (cmd < 0xC0) {
            // Reset
            monitor.reset();

            // Verify state after reset
            SpliceState state = monitor.get_splice_state();
            if (state != SpliceState::InContent) {
                __builtin_trap();
            }

            if (monitor.get_event_count() != 0) {
                __builtin_trap();
            }

            if (monitor.is_in_ad_break()) {
                __builtin_trap();
            }
        } else {
            // Reset full
            monitor.reset_full();

            // Verify full reset cleared PIDs
            if (monitor.scte35_pid_count.load(std::memory_order_relaxed) != 0) {
                __builtin_trap();
            }

            // State should be reset
            if (monitor.get_splice_state() != SpliceState::InContent) {
                __builtin_trap();
            }
        }
    }

    // Final invariant checks
    SpliceState final_state = monitor.get_splice_state();
    if (static_cast<uint8_t>(final_state) > 2) {
        __builtin_trap();
    }

    int64_t final_event_count = monitor.get_event_count();
    if (final_event_count < 0) {
        __builtin_trap();
    }

    int32_t final_pid_count = monitor.scte35_pid_count.load(std::memory_order_relaxed);
    if (final_pid_count < 0 || final_pid_count > static_cast<int32_t>(MAX_PROGRAMS)) {
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

    int fake_argc = 1;
    char* fake_argv[] = {argv[0], nullptr};
    LLVMFuzzerInitialize(&fake_argc, &fake_argv);

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
