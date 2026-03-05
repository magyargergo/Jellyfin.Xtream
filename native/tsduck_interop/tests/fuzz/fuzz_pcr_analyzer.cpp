// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Fuzz test harness for PcrAnalyzer.
// Tests PCR processing with arbitrary values, indices, and timestamps.
//
// Build with libFuzzer:
//   clang++ -g -O1 -fsanitize=fuzzer,address,undefined \
//     -I../src -I../include $(pkg-config --cflags --libs tsduck) \
//     fuzz_pcr_analyzer.cpp -o fuzz_pcr_analyzer
//
// Run:
//   ./fuzz_pcr_analyzer corpus/ -max_len=4096

#include <cstdint>
#include <cstddef>
#include <cstring>
#include <cmath>
#include "analysis/pcr_analyzer.hpp"
#include "core/constants.hpp"

using namespace tsduck_interop;
using namespace tsduck_interop::analysis;

// Command encoding:
// Each command is 17 bytes:
//   - 8 bytes: PCR value (uint64_t, masked to valid range)
//   - 4 bytes: packet index delta (int32_t, converted to positive)
//   - 4 bytes: time delta nanoseconds (int32_t, converted to positive)
//   - 1 byte: command type (0x00-0x7F = process, 0x80-0xBF = get, 0xC0-0xFF = reset)

extern "C" int LLVMFuzzerTestOneInput(const uint8_t* data, size_t size) {
    if (size == 0) return 0;

    PcrAnalyzer analyzer;
    int64_t packet_idx = 0;
    int64_t time_ns = 0;

    size_t offset = 0;
    while (offset < size) {
        if (offset + 17 > size) break;

        // Parse PCR value (modulo PCR_SCALE for valid range)
        uint64_t pcr_value;
        std::memcpy(&pcr_value, data + offset, sizeof(pcr_value));
        pcr_value %= ts::PCR_SCALE;
        offset += 8;

        // Parse packet index delta
        int32_t pkt_delta;
        std::memcpy(&pkt_delta, data + offset, sizeof(pkt_delta));
        pkt_delta = std::abs(pkt_delta) % 10000 + 1;  // 1-10000 packets
        offset += 4;

        // Parse time delta
        int32_t time_delta;
        std::memcpy(&time_delta, data + offset, sizeof(time_delta));
        time_delta = std::abs(time_delta) % 100000000 + 1000;  // 1us - 100ms
        offset += 4;

        // Command type
        uint8_t cmd = data[offset++];

        if (cmd < 0x80) {
            // Process PCR
            packet_idx += pkt_delta;
            time_ns += time_delta;
            analyzer.process(pcr_value, packet_idx, time_ns);

            // Verify invariants
            PcrAnalysisNative result;
            if (analyzer.get(&result)) {
                // PCR count must be positive after processing
                if (result.pcr_count <= 0) {
                    __builtin_trap();
                }
                // Valid count must not exceed total count
                if (result.pcr_valid_count > result.pcr_count) {
                    __builtin_trap();
                }
                // Jitter values must be non-negative
                if (result.pcr_jitter_us < 0 || result.pcr_jitter_max_us < 0 ||
                    result.pcr_jitter_avg_us < 0) {
                    __builtin_trap();
                }
                // Max jitter must be >= current jitter
                if (result.pcr_jitter_max_us < result.pcr_jitter_us - 0.001) {
                    __builtin_trap();
                }
            }
        } else if (cmd < 0xC0) {
            // Get analysis
            PcrAnalysisNative result;
            (void)analyzer.get(&result);

            // Verify last_pcr_base_90khz consistency
            int64_t last_pcr_90khz = analyzer.last_pcr_base_90khz();
            // Should be -1 if no PCR processed, or >= 0 otherwise
            if (result.pcr_count == 0 && last_pcr_90khz != -1) {
                __builtin_trap();
            }
        } else {
            // Reset
            analyzer.reset();

            // Verify reset state
            PcrAnalysisNative result;
            if (analyzer.get(&result)) {
                // Should return false after reset (no data)
                __builtin_trap();
            }
            if (analyzer.last_pcr_base_90khz() != -1) {
                __builtin_trap();
            }
        }
    }

    // Final verification
    PcrAnalysisNative final_result;
    (void)analyzer.get(&final_result);

    // If we have data, verify consistency
    if (final_result.pcr_count > 0) {
        if (final_result.pcr_valid_count > final_result.pcr_count) {
            __builtin_trap();
        }
        // Drift should be bounded (not NaN or Inf)
        if (std::isnan(final_result.pcr_drift_ppm) ||
            std::isinf(final_result.pcr_drift_ppm)) {
            __builtin_trap();
        }
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
