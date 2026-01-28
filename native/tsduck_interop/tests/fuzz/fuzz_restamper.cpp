// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Fuzz test harness for Restamper.
// Tests PCR/PTS/DTS timestamp manipulation with arbitrary TS packets.
//
// Build with libFuzzer:
//   clang++ -g -O1 -fsanitize=fuzzer,address,undefined \
//     -I../src -I../include $(pkg-config --cflags --libs tsduck) \
//     fuzz_restamper.cpp -o fuzz_restamper
//
// Run:
//   ./fuzz_restamper corpus/ -max_len=65536

#include <cstdint>
#include <cstddef>
#include <cstring>
#include <cmath>
#include <vector>
#include <tsduck.h>
#include "restamping/restamper.hpp"
#include "analysis/av_sync_tracker.hpp"
#include "core/constants.hpp"

using namespace tsduck_interop;
using namespace tsduck_interop::restamping;
using namespace tsduck_interop::analysis;

extern "C" int LLVMFuzzerTestOneInput(const uint8_t* data, size_t size) {
    if (size < 4) return 0;

    // First 4 bytes control restamper configuration
    RestampingConfigNative config{};
    config.mode = data[0] % 3;  // DISABLED, MONITOR, or CORRECT
    config.smooth_pcr = data[1] & 0x01;
    config.fix_discontinuities = data[1] & 0x02;
    config.correction_threshold_ms = static_cast<double>(data[2]) + 1.0;
    config.max_correction_rate_ms = static_cast<double>(data[3]) / 10.0 + 0.1;
    config.hysteresis_threshold_ms = config.correction_threshold_ms / 2.0;
    config.stream_bitrate_hint = 10000000;  // 10 Mbps

    AvSyncTracker av_sync;
    Restamper restamper(&av_sync, &config);

    // Copy remaining data as TS packets
    const uint8_t* ts_data = data + 4;
    size_t ts_size = size - 4;

    // Round down to packet boundary
    size_t num_packets = ts_size / ts::PKT_SIZE;
    if (num_packets == 0) return 0;

    size_t aligned_size = num_packets * ts::PKT_SIZE;
    std::vector<uint8_t> buffer(ts_data, ts_data + aligned_size);

    // Force sync bytes at packet boundaries for valid processing
    for (size_t i = 0; i < num_packets; i++) {
        buffer[i * ts::PKT_SIZE] = ts::SYNC_BYTE;
    }

    // Process packets
    int64_t packet_idx = 0;
    int32_t modifications = restamper.process(buffer.data(),
                                               static_cast<int32_t>(aligned_size),
                                               packet_idx);

    // Verify invariants
    if (modifications < 0) {
        __builtin_trap();  // Should never be negative
    }

    // Get statistics
    RestampingStatisticsNative stats;
    if (restamper.get_statistics(&stats)) {
        // Packets processed should match (only when mode is not DISABLED)
        // When mode is DISABLED, process() returns early without counting packets
        if (config.mode != RESTAMP_MODE_DISABLED) {
            if (stats.packets_processed != static_cast<int64_t>(num_packets)) {
                __builtin_trap();
            }
        } else {
            // When disabled, no packets should be processed
            if (stats.packets_processed != 0) {
                __builtin_trap();
            }
        }

        // Counters should be non-negative
        if (stats.pcr_smoothed < 0 || stats.pts_corrected < 0 ||
            stats.dts_corrected < 0) {
            __builtin_trap();
        }

        // Total correction should be bounded (not NaN or Inf)
        if (std::isnan(stats.total_correction_ms) ||
            std::isinf(stats.total_correction_ms)) {
            __builtin_trap();
        }
    }

    // Verify output packets still have valid sync bytes
    for (size_t i = 0; i < num_packets; i++) {
        if (buffer[i * ts::PKT_SIZE] != ts::SYNC_BYTE) {
            __builtin_trap();  // Restamper should never corrupt sync bytes
        }
    }

    // Test switch handling
    restamper.handle_switch(1000000, 500000);  // last_output=1M, new_input=500K

    // Process more packets after switch
    packet_idx += static_cast<int64_t>(num_packets);
    (void)restamper.process(buffer.data(), static_cast<int32_t>(aligned_size), packet_idx);

    // Test reset
    restamper.reset();
    if (restamper.get_statistics(&stats)) {
        if (stats.packets_processed != 0 || stats.pcr_smoothed != 0 ||
            stats.pts_corrected != 0 || stats.dts_corrected != 0) {
            __builtin_trap();
        }
    }

    // Test reconfigure
    config.mode = RESTAMP_MODE_MONITOR;
    restamper.configure(&config);

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
