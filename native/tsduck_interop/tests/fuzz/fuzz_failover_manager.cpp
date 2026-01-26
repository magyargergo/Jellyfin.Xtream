// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Fuzz test harness for FailoverManager.
// Tests state machine transitions with arbitrary event sequences.
//
// Build with libFuzzer:
//   clang++ -g -O1 -fsanitize=fuzzer,address,undefined \
//     -I../src -I../include fuzz_failover_manager.cpp -o fuzz_failover_manager
//
// Run:
//   ./fuzz_failover_manager corpus/ -max_len=4096

#include <cstdint>
#include <cstddef>
#include <cstring>
#include <cmath>
#include "streaming/failover_manager.hpp"
#include "streaming/streaming_types.hpp"
#include "core/constants.hpp"

using namespace tsduck_interop::streaming;

// Command encoding:
// Each command is 1 byte:
//   0x00-0x1F: onConnecting
//   0x20-0x3F: onConnected
//   0x40-0x5F: onDataReceived
//   0x60-0x7F: onDisconnected
//   0x80-0x9F: onStall
//   0xA0-0xBF: onSwitching
//   0xC0-0xDF: onStopped
//   0xE0-0xEF: calculateBackoffMs
//   0xF0-0xFF: reset

extern "C" int LLVMFuzzerTestOneInput(const uint8_t* data, size_t size) {
    if (size < 8) return 0;

    // First 8 bytes configure the manager
    StreamerConfig config{};
    config.connect_timeout_ms = static_cast<int32_t>(data[0]) * 100 + 100;  // 100-25600ms
    config.response_timeout_ms = static_cast<int32_t>(data[1]) * 100 + 100;
    config.stall_timeout_ms = static_cast<int32_t>(data[2]) * 100 + 100;
    config.max_retries = static_cast<int32_t>(data[3] % 20) + 1;  // 1-20
    config.initial_backoff_ms = static_cast<int32_t>(data[4]) * 10 + 10;  // 10-2560ms
    config.max_backoff_ms = static_cast<int32_t>(data[5]) * 100 + 1000;  // 1000-26500ms
    config.backoff_multiplier = 1.0 + static_cast<double>(data[6] % 30) / 10.0;  // 1.0-4.0
    config.backoff_jitter_ms = static_cast<int32_t>(data[7] % 100);  // 0-99ms
    config.stalls_before_switch = 2;

    FailoverManager manager(config);

    size_t offset = 8;
    while (offset < size) {
        uint8_t cmd = data[offset++];

        StreamerState state_before = manager.state();

        if (cmd < 0x20) {
            manager.on_connecting();

            // State should be Connecting after this call
            if (manager.state() != StreamerState::Connecting) {
                __builtin_trap();
            }

        } else if (cmd < 0x40) {
            manager.on_connected();

            // State should be Streaming after this call
            if (manager.state() != StreamerState::Streaming) {
                __builtin_trap();
            }

            // Retry count should be reset
            if (manager.retry_count() != 0) {
                __builtin_trap();
            }

        } else if (cmd < 0x60) {
            manager.on_data_received();
            // State shouldn't change from data received
            // (unless stall detection was in progress)

        } else if (cmd < 0x80) {
            auto action = manager.on_disconnected();

            // Verify action is valid enum value
            if (action != FailoverManager::DisconnectAction::ShouldRetry &&
                action != FailoverManager::DisconnectAction::ShouldSwitch &&
                action != FailoverManager::DisconnectAction::Failed) {
                __builtin_trap();
            }

            // If Failed, state should be Failed
            if (action == FailoverManager::DisconnectAction::Failed) {
                if (manager.state() != StreamerState::Failed) {
                    __builtin_trap();
                }
            }

            // Retry count should have increased
            // (unless we reset or the counter wrapped somehow)

        } else if (cmd < 0xA0) {
            bool should_switch = manager.on_stall();

            // State should be Stalled
            if (manager.state() != StreamerState::Stalled) {
                __builtin_trap();
            }

            // should_switch should be consistent with consecutive failures
            // >= stalls_before_switch means should switch
            (void)should_switch;

        } else if (cmd < 0xC0) {
            manager.on_switching();

            // State should be Switching
            if (manager.state() != StreamerState::Switching) {
                __builtin_trap();
            }

            // Counters should be reset
            if (manager.retry_count() != 0 || manager.consecutive_failures() != 0) {
                __builtin_trap();
            }

        } else if (cmd < 0xE0) {
            manager.on_stopped();

            // State should be Stopped
            if (manager.state() != StreamerState::Stopped) {
                __builtin_trap();
            }

            // isTerminal should be true
            if (!manager.is_terminal()) {
                __builtin_trap();
            }

        } else if (cmd < 0xF0) {
            int32_t backoff = manager.calculate_backoff_ms();

            // Backoff should be non-negative
            if (backoff < 0) {
                __builtin_trap();
            }

            // Backoff should be bounded by max + jitter
            int32_t max_possible = config.max_backoff_ms + config.backoff_jitter_ms;
            if (backoff > max_possible) {
                __builtin_trap();
            }

        } else {
            manager.reset();

            // State should be Idle
            if (manager.state() != StreamerState::Idle) {
                __builtin_trap();
            }

            // All counters should be reset
            if (manager.retry_count() != 0 ||
                manager.consecutive_failures() != 0 ||
                manager.total_switches() != 0 ||
                manager.total_reconnections() != 0) {
                __builtin_trap();
            }
        }

        // Verify state invariants
        StreamerState current = manager.state();

        // isActive and isTerminal should be mutually consistent
        bool is_active = manager.is_active();
        bool is_terminal = manager.is_terminal();

        if (is_active && is_terminal) {
            __builtin_trap();  // Can't be both active and terminal
        }

        // Verify state-specific invariants
        if (current == StreamerState::Idle) {
            if (is_active || is_terminal) {
                __builtin_trap();
            }
        }
        if (current == StreamerState::Stopped || current == StreamerState::Failed) {
            if (!is_terminal) {
                __builtin_trap();
            }
        }
        if (current == StreamerState::Streaming ||
            current == StreamerState::Connecting ||
            current == StreamerState::Reconnecting ||
            current == StreamerState::Switching) {
            if (!is_active) {
                __builtin_trap();
            }
        }

        (void)state_before;  // Suppress unused warning
    }

    // Final verification
    int64_t total_switches = manager.total_switches();
    int64_t total_reconnections = manager.total_reconnections();

    if (total_switches < 0 || total_reconnections < 0) {
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
