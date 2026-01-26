// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Fuzz test harness for PsiMonitor.
// Tests PSI parsing (PAT/PMT) with arbitrary TS packets.
//
// Build with libFuzzer:
//   clang++ -g -O1 -fsanitize=fuzzer,address,undefined \
//     -I../src -I../include $(pkg-config --cflags --libs tsduck) \
//     fuzz_psi_monitor.cpp -o fuzz_psi_monitor
//
// Run:
//   ./fuzz_psi_monitor corpus/ -max_len=8192
//
// Corpus seeds should be valid PAT/PMT sections wrapped in TS packets.

#include <cstdint>
#include <cstddef>
#include <cstring>
#include <tsduck.h>
#include "analysis/psi_monitor.hpp"
#include "core/constants.hpp"

using namespace tsduck_interop;
using namespace tsduck_interop::analysis;

// Global DuckContext to avoid repeated initialization overhead
static ts::DuckContext* g_duck = nullptr;

extern "C" int LLVMFuzzerInitialize(int* /*argc*/, char*** /*argv*/) {
    // Suppress TsDuck error output during fuzzing
    // DuckContext takes a pointer to Report, not the object itself
    g_duck = new ts::DuckContext(&ts::NullReport::Instance());
    return 0;
}

extern "C" int LLVMFuzzerTestOneInput(const uint8_t* data, size_t size) {
    if (size == 0 || g_duck == nullptr) return 0;

    PsiMonitor monitor(*g_duck);

    // Process input as TS packets
    size_t offset = 0;
    int64_t packet_idx = 0;

    while (offset + ts::PKT_SIZE <= size) {
        // Create a TSPacket from the input data
        ts::TSPacket pkt;
        std::memcpy(pkt.b, data + offset, ts::PKT_SIZE);

        // Force sync byte to avoid trivial rejection
        pkt.b[0] = ts::SYNC_BYTE;

        // Feed to PSI monitor
        monitor.feedPacket(pkt, packet_idx++);

        // Verify invariants
        int32_t prog_count = monitor.getProgramCount();
        if (prog_count < 0 || prog_count > static_cast<int32_t>(MAX_PROGRAMS)) {
            __builtin_trap();
        }

        offset += ts::PKT_SIZE;
    }

    // Test getPrograms API
    TsDuckProgramInfoNative progs[MAX_PROGRAMS];
    int32_t count = monitor.getPrograms(progs, MAX_PROGRAMS);
    if (count < 0 || count > static_cast<int32_t>(MAX_PROGRAMS)) {
        __builtin_trap();
    }

    // Test reset
    monitor.reset();
    if (monitor.getProgramCount() != 0) {
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
