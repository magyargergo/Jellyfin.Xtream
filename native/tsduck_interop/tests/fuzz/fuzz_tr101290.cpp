// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Fuzz test harness for Tr101290Monitor.
// Tests TR 101 290 priority 1 and 2 checks with arbitrary TS packets.
//
// Build with libFuzzer:
//   clang++ -g -O1 -fsanitize=fuzzer,address,undefined \
//     -I../src -I../include $(pkg-config --cflags --libs tsduck) \
//     fuzz_tr101290.cpp -o fuzz_tr101290
//
// Run:
//   ./fuzz_tr101290 corpus/ -max_len=16384

#include <cstdint>
#include <cstddef>
#include <cstring>
#include <tsduck.h>
#include "analysis/tr101290.hpp"
#include "core/constants.hpp"

using namespace tsduck_interop;
using namespace tsduck_interop::analysis;

extern "C" int LLVMFuzzerTestOneInput(const uint8_t* data, size_t size) {
    if (size == 0) return 0;

    Tr101290Monitor monitor;

    // Set a reasonable bitrate for timing calculations
    monitor.estimated_bitrate_bps.store(10000000, std::memory_order_release);  // 10 Mbps

    size_t offset = 0;
    int64_t packet_idx = 0;
    int64_t time_ns = 0;

    while (offset + ts::PKT_SIZE <= size) {
        // Create a TSPacket from the input data
        ts::TSPacket pkt;
        std::memcpy(pkt.b, data + offset, ts::PKT_SIZE);

        // Check sync byte (Priority 1: sync_byte_error, sync_loss)
        bool valid_sync = (pkt.b[0] == ts::SYNC_BYTE);
        monitor.check_sync(valid_sync);

        if (valid_sync) {
            // Force valid sync for further processing
            uint16_t pid = pkt.getPID();

            // Check transport error indicator (Priority 2)
            monitor.check_transport_error(pkt);

            // Check PCR-related metrics if packet has PCR
            if (pkt.hasPCR()) {
                uint64_t pcr = pkt.getPCR();
                if (pcr != ts::INVALID_PCR) {
                    monitor.check_pcr_repetition(pid, packet_idx);
                    monitor.check_pcr_discontinuity(pkt, pid, pcr);
                    monitor.check_pcr_accuracy(pid, pcr, packet_idx);
                }
            }

            // Simulate PAT reception periodically
            if (pid == ts::PID_PAT) {
                monitor.on_pat_received(packet_idx);
            }

            // Check PAT timeout
            monitor.check_pat_timeout(packet_idx);

            // Check PTS repetition for video/audio PIDs (0x100-0x1FF)
            if (pid >= 0x100 && pid < 0x200) {
                monitor.check_pts_repetition(pid, time_ns);
            }
        }

        offset += ts::PKT_SIZE;
        packet_idx++;
        time_ns += 1000000;  // 1ms per packet (simulated)
    }

    // Verify invariants
    Tr101290Priority1Native p1;
    Tr101290Priority2Native p2;
    monitor.get_counters(&p1, &p2);

    // All counters must be non-negative
    if (p1.sync_byte_error < 0 || p1.sync_loss < 0 ||
        p1.pat_error < 0 || p1.pmt_error < 0 || p1.pid_error < 0) {
        __builtin_trap();
    }
    if (p2.transport_error < 0 || p2.crc_error < 0 ||
        p2.pcr_repetition_error < 0 || p2.pcr_discontinuity_error < 0 ||
        p2.pcr_accuracy_error < 0 || p2.pts_error < 0) {
        __builtin_trap();
    }

    // sync_loss can only occur after sync_byte_errors
    // (sync_loss requires 2+ consecutive sync errors)
    // Note: sync_loss increments each time we're in sync_loss state,
    // so it could be higher than sync_byte_error in some edge cases.

    // Test reset
    monitor.reset();
    monitor.get_counters(&p1, &p2);

    // After reset, all counters should be zero
    if (p1.sync_byte_error != 0 || p1.sync_loss != 0 ||
        p1.pat_error != 0 || p1.pmt_error != 0 || p1.pid_error != 0) {
        __builtin_trap();
    }
    if (p2.transport_error != 0 || p2.crc_error != 0 ||
        p2.pcr_repetition_error != 0 || p2.pcr_discontinuity_error != 0 ||
        p2.pcr_accuracy_error != 0 || p2.pts_error != 0) {
        __builtin_trap();
    }

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
