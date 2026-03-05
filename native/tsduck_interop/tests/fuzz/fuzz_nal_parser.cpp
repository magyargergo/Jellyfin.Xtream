// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Fuzz test harness for NalParser.
// Tests H.264/H.265 NAL unit parsing with arbitrary PES payloads.
//
// Build with libFuzzer:
//   clang++ -g -O1 -fsanitize=fuzzer,address,undefined \
//     -I../src -I../include $(pkg-config --cflags --libs tsduck) \
//     fuzz_nal_parser.cpp -o fuzz_nal_parser
//
// Run:
//   ./fuzz_nal_parser corpus/ -max_len=16384
//
// Corpus seeds should include valid H.264/H.265 NAL units with start codes.

#include <cstdint>
#include <cstddef>
#include <cstring>
#include <tsduck.h>
#include "analysis/nal_parser.hpp"
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
// 0x00-0x3F: Feed NAL data as PES payload
// 0x40-0x5F: Add video PID with stream type (next 3 bytes = PID + type)
// 0x60-0x7F: Check IDR frame
// 0x80-0x9F: Get codec info
// 0xA0-0xBF: Get parameter sets
// 0xC0-0xDF: Reset
// 0xE0-0xFF: Reset full

extern "C" int LLVMFuzzerTestOneInput(const uint8_t* data, size_t size) {
    if (size == 0 || g_duck == nullptr) return 0;

    NalParser parser(*g_duck);
    size_t offset = 0;
    int64_t packet_idx = 0;

    while (offset < size) {
        uint8_t cmd = data[offset++];

        if (cmd < 0x40) {
            // Feed NAL data as PES payload wrapped in TS packet
            size_t nal_len = std::min(size - offset, static_cast<size_t>((cmd & 0x3F) + 1) * 3);
            if (nal_len == 0) continue;

            // Need a registered video PID to process
            size_t stream_count = parser.video_stream_count.load(std::memory_order_relaxed);
            if (stream_count == 0) {
                // Register a default H.264 PID if none registered
                parser.add_video_pid(0x100, 0x1B);  // H.264 stream type
            }

            // Get first registered PID
            uint16_t video_pid = parser.video_streams[0].video_pid;

            // Build a TS packet with PES start
            ts::TSPacket pkt;
            std::memset(pkt.b, 0xFF, ts::PKT_SIZE);
            pkt.b[0] = ts::SYNC_BYTE;

            // Set PID and PUSI
            pkt.b[1] = static_cast<uint8_t>(0x40 | ((video_pid >> 8) & 0x1F));  // PUSI=1
            pkt.b[2] = static_cast<uint8_t>(video_pid & 0xFF);
            pkt.b[3] = 0x10;  // Payload only

            // Build minimal PES header
            // PES start code: 00 00 01 stream_id
            pkt.b[4] = 0x00;
            pkt.b[5] = 0x00;
            pkt.b[6] = 0x01;
            pkt.b[7] = 0xE0;  // Video stream ID

            // PES packet length (0 = unbounded for video)
            pkt.b[8] = 0x00;
            pkt.b[9] = 0x00;

            // PES flags: no PTS/DTS
            pkt.b[10] = 0x80;  // '10' marker bits
            pkt.b[11] = 0x00;  // No PTS/DTS
            pkt.b[12] = 0x00;  // PES header data length = 0

            // Copy NAL data starting at offset 13
            size_t pes_header_size = 13;
            size_t max_nal = ts::PKT_SIZE - pes_header_size;
            size_t copy_len = std::min(nal_len, max_nal);

            std::memcpy(&pkt.b[pes_header_size], &data[offset], copy_len);

            // Optionally inject NAL start code at beginning
            if (copy_len >= 4) {
                pkt.b[pes_header_size] = 0x00;
                pkt.b[pes_header_size + 1] = 0x00;
                pkt.b[pes_header_size + 2] = 0x01;
                // Keep the fuzzer-provided NAL type byte at [pes_header_size + 3]
            }

            // Process the packet
            parser.process_pes_start(pkt, video_pid, packet_idx++);

            offset += nal_len;
        } else if (cmd < 0x60) {
            // Add video PID with stream type
            if (offset + 3 > size) break;

            uint16_t pid = static_cast<uint16_t>((data[offset] << 8) | data[offset + 1]);
            pid &= 0x1FFF;  // Valid PID range
            uint8_t stream_type = data[offset + 2];
            offset += 3;

            // Map fuzzer byte to valid stream types
            // 0x1B = H.264, 0x24 = H.265, 0x33 = H.266
            uint8_t valid_types[] = {0x1B, 0x24, 0x33};
            stream_type = valid_types[stream_type % 3];

            int32_t idx = parser.add_video_pid(pid, stream_type);

            // Verify return value
            if (idx < -1 || idx >= static_cast<int32_t>(MAX_VIDEO_PIDS)) {
                __builtin_trap();
            }

            // Verify stream count
            size_t count = parser.video_stream_count.load(std::memory_order_relaxed);
            if (count > MAX_VIDEO_PIDS) {
                __builtin_trap();
            }
        } else if (cmd < 0x80) {
            // Check IDR frame for all registered PIDs
            size_t count = parser.video_stream_count.load(std::memory_order_relaxed);
            for (size_t i = 0; i < count; ++i) {
                uint16_t pid = parser.video_streams[i].video_pid;

                // Verify PID is valid
                if (pid > 0x1FFF && pid != 0) {
                    __builtin_trap();
                }

                bool has_idr = parser.check_idr_frame(pid);
                (void)has_idr;  // Result is valid either way
            }

            // Verify total IDR count is non-negative
            int64_t total_idr = parser.total_idr_frames.load(std::memory_order_relaxed);
            if (total_idr < 0) {
                __builtin_trap();
            }

            // Verify total NAL count is non-negative
            int64_t total_nal = parser.total_nal_units.load(std::memory_order_relaxed);
            if (total_nal < 0) {
                __builtin_trap();
            }
        } else if (cmd < 0xA0) {
            // Get codec info for registered PIDs
            size_t count = parser.video_stream_count.load(std::memory_order_relaxed);
            for (size_t i = 0; i < count; ++i) {
                uint16_t pid = parser.video_streams[i].video_pid;

                VideoCodecInfoNative info;
                std::memset(&info, 0, sizeof(info));

                bool has_info = parser.get_video_codec_info(pid, &info);

                if (has_info) {
                    // Verify codec type is valid enum value (0-3)
                    if (info.codec_type > 3) {
                        __builtin_trap();
                    }

                    // Resolution values should be reasonable
                    // Width/height of 0 is valid (unknown), but negative would be bad
                    // Since they're uint16_t, they can't be negative, but check sanity
                    // Max reasonable resolution: 8K = 7680x4320
                    if (info.width > 8192 || info.height > 8192) {
                        // Allow larger values as fuzzer may produce anything
                        // Just don't crash
                    }

                    // Frame rate denominator should not cause div-by-zero
                    // (But we don't use it here, just verify it's reasonable)
                    if (info.frame_rate_den == 0 && info.frame_rate_num != 0) {
                        // Invalid combination, but don't trap - parser may not validate this
                    }
                }
            }
        } else if (cmd < 0xC0) {
            // Get parameter sets
            size_t count = parser.video_stream_count.load(std::memory_order_relaxed);
            for (size_t i = 0; i < count; ++i) {
                uint16_t pid = parser.video_streams[i].video_pid;

                NalParameterSetsNative params;
                std::memset(&params, 0, sizeof(params));

                bool has_params = parser.get_parameter_sets(pid, &params);

                if (has_params) {
                    // Verify parameter set lengths are within bounds
                    if (params.sps_length > MAX_SPS_SIZE) {
                        __builtin_trap();
                    }
                    if (params.pps_length > MAX_PPS_SIZE) {
                        __builtin_trap();
                    }
                    if (params.vps_length > MAX_VPS_SIZE) {
                        __builtin_trap();
                    }

                    // Verify codec type
                    if (params.codec_info.codec_type > 3) {
                        __builtin_trap();
                    }

                    // Verify PID matches
                    if (params.video_pid != pid && params.video_pid != 0) {
                        __builtin_trap();
                    }
                }
            }
        } else if (cmd < 0xE0) {
            // Reset
            parser.reset();

            // Verify reset state
            // IDR flags should all be false
            size_t count = parser.video_stream_count.load(std::memory_order_relaxed);
            for (size_t i = 0; i < count; ++i) {
                uint16_t pid = parser.video_streams[i].video_pid;
                if (parser.check_idr_frame(pid)) {
                    __builtin_trap();
                }
            }

            // Counters should be reset
            if (parser.total_nal_units.load(std::memory_order_relaxed) != 0) {
                __builtin_trap();
            }
            if (parser.total_idr_frames.load(std::memory_order_relaxed) != 0) {
                __builtin_trap();
            }
        } else {
            // Reset full
            parser.reset_full();

            // Verify full reset
            if (parser.video_stream_count.load(std::memory_order_relaxed) != 0) {
                __builtin_trap();
            }
            if (parser.total_nal_units.load(std::memory_order_relaxed) != 0) {
                __builtin_trap();
            }
            if (parser.total_idr_frames.load(std::memory_order_relaxed) != 0) {
                __builtin_trap();
            }
        }
    }

    // Final invariant checks
    size_t final_stream_count = parser.video_stream_count.load(std::memory_order_relaxed);
    if (final_stream_count > MAX_VIDEO_PIDS) {
        __builtin_trap();
    }

    int64_t final_nal_count = parser.total_nal_units.load(std::memory_order_relaxed);
    if (final_nal_count < 0) {
        __builtin_trap();
    }

    int64_t final_idr_count = parser.total_idr_frames.load(std::memory_order_relaxed);
    if (final_idr_count < 0) {
        __builtin_trap();
    }

    // Verify all registered streams have valid codec types
    for (size_t i = 0; i < final_stream_count; ++i) {
        uint8_t codec_type = parser.video_streams[i].codec_info.codec_type;
        if (codec_type > 3) {
            __builtin_trap();
        }
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
