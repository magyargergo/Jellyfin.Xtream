// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_STREAMING_FAILOVER_MANAGER_HPP
#define TSDUCK_INTEROP_STREAMING_FAILOVER_MANAGER_HPP

#include <atomic>
#include <chrono>
#include <cmath>
#include <random>
#include "streaming_types.hpp"

namespace tsduck_interop::streaming {

// ============================================================================
// FailoverManager: State machine for stream failover logic
// ============================================================================
//
// State transitions:
//   IDLE → CONNECTING → STREAMING → (stall/error) → RECONNECTING → CONNECTING
//                                                          ↓ (max retries)
//                                                       FAILED
//   STREAMING → (requestSwitch) → SWITCHING → CONNECTING (next URL)
//
// The failover manager tracks retry counts, consecutive failures, and
// calculates exponential backoff with jitter. It does NOT own any
// network resources - it only manages state and timing decisions.

class FailoverManager {
public:
    explicit FailoverManager(const StreamerConfig& config) noexcept
        : config_(config), state_(StreamerState::Idle), retry_count_(0), consecutive_failures_(0), total_switches_(0),
          total_reconnections_(0), last_data_time_(std::chrono::steady_clock::now()), rng_(std::random_device{}()) {}

    // ========================================================================
    // State Queries
    // ========================================================================

    StreamerState state() const noexcept { return state_.load(std::memory_order_acquire); }

    bool is_active() const noexcept {
        auto s = state();
        return s == StreamerState::Connecting || s == StreamerState::Streaming || s == StreamerState::Reconnecting ||
               s == StreamerState::Switching;
    }

    bool is_terminal() const noexcept {
        auto s = state();
        return s == StreamerState::Stopped || s == StreamerState::Failed;
    }

    // ========================================================================
    // State Transitions
    // ========================================================================

    /// Transition to Connecting state
    void on_connecting() noexcept { state_.store(StreamerState::Connecting, std::memory_order_release); }

    /// Called when connection succeeds and streaming begins.
    /// Resets retry count and failure counter.
    void on_connected() noexcept {
        state_.store(StreamerState::Streaming, std::memory_order_release);
        retry_count_ = 0;
        consecutive_failures_ = 0;
        last_data_time_ = std::chrono::steady_clock::now();
    }

    /// Called when data is received (heartbeat for stall detection).
    void on_data_received() noexcept { last_data_time_ = std::chrono::steady_clock::now(); }

    /// Called when connection drops or HTTP error occurs.
    /// @param reason Classification of why the disconnect occurred (for failover decisions)
    /// @return ShouldRetry = continue retrying same URL,
    ///         ShouldSwitch = switch to next URL,
    ///         Failed = max retries exhausted.
    enum class DisconnectAction {
        ShouldRetry,
        ShouldSwitch,
        Failed
    };
    DisconnectAction on_disconnected(DisconnectReason reason = DisconnectReason::Unknown) noexcept {
        retry_count_++;
        total_reconnections_++;
        consecutive_failures_++;

        if (retry_count_ >= config_.max_retries) {
            state_.store(StreamerState::Failed, std::memory_order_release);
            return DisconnectAction::Failed;
        }

        // TIMEOUT FAST PATH: Immediately switch URL on timeout if configured.
        // This prevents wasting time retrying the same URL when it's timing out.
        if (config_.timeout_immediate_switch != 0 && reason == DisconnectReason::Timeout) {
            return DisconnectAction::ShouldSwitch;
        }

        // DNS FAILURE FAST PATH: Immediately try another URL on DNS failure.
        // DNS failures indicate the provider's host is unreachable, so retrying
        // the same URL is unlikely to help. Switch to next healthy URL immediately.
        // The StreamPipeline tracks cumulative DNS failures for eventual ejection.
        if (reason == DisconnectReason::DnsResolutionFailed) {
            return DisconnectAction::ShouldSwitch;
        }

        // Check if we should switch URL after consecutive failures
        // (errors count the same as stalls for this purpose)
        if (consecutive_failures_ >= config_.stalls_before_switch) {
            return DisconnectAction::ShouldSwitch;
        }

        state_.store(StreamerState::Reconnecting, std::memory_order_release);
        return DisconnectAction::ShouldRetry;
    }

    /// Called when data stall is detected (no data for stall_timeout_ms).
    /// @return true if should switch URL, false if should just reconnect.
    bool on_stall() noexcept {
        state_.store(StreamerState::Stalled, std::memory_order_release);
        consecutive_failures_++;
        return consecutive_failures_ >= config_.stalls_before_switch;
    }

    /// Called when initiating a switch to a different URL.
    /// Resets retry count and failure counter (fresh start on new URL).
    void on_switching() noexcept {
        state_.store(StreamerState::Switching, std::memory_order_release);
        total_switches_++;
        retry_count_ = 0;
        consecutive_failures_ = 0;
    }

    /// Called when streaming is explicitly stopped.
    void on_stopped() noexcept { state_.store(StreamerState::Stopped, std::memory_order_release); }

    // ========================================================================
    // Stall Detection
    // ========================================================================

    /// Check if currently stalled (no data for stall_timeout_ms).
    /// Only meaningful when in Streaming state.
    bool is_stalled() const noexcept {
        auto elapsed = std::chrono::steady_clock::now() - last_data_time_;
        auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(elapsed).count();
        return ms > config_.stall_timeout_ms;
    }

    /// Get milliseconds since last data received.
    int64_t ms_since_last_data() const noexcept {
        auto elapsed = std::chrono::steady_clock::now() - last_data_time_;
        return std::chrono::duration_cast<std::chrono::milliseconds>(elapsed).count();
    }

    // ========================================================================
    // Backoff Calculation
    // ========================================================================

    /// Calculate the next backoff delay in milliseconds.
    /// Uses exponential backoff with jitter, capped at max_backoff_ms.
    int32_t calculate_backoff_ms() noexcept {
        // Exponential: initial * multiplier^(retry-1) so first retry uses initial value
        int32_t effective_retry = std::min(std::max(retry_count_ - 1, 0), 10);
        double base = config_.initial_backoff_ms * std::pow(config_.backoff_multiplier, effective_retry);
        int32_t delay = static_cast<int32_t>(std::min(base, static_cast<double>(config_.max_backoff_ms)));

        // Add uniform jitter [0, backoff_jitter_ms]
        if (config_.backoff_jitter_ms > 0) {
            std::uniform_int_distribution<int32_t> dist(0, config_.backoff_jitter_ms);
            delay += dist(rng_);
        }

        return delay;
    }

    // ========================================================================
    // Reset
    // ========================================================================

    /// Reset all state for a new session.
    void reset() noexcept {
        state_.store(StreamerState::Idle, std::memory_order_release);
        retry_count_ = 0;
        consecutive_failures_ = 0;
        total_switches_ = 0;
        total_reconnections_ = 0;
        last_data_time_ = std::chrono::steady_clock::now();
    }

    // ========================================================================
    // Statistics
    // ========================================================================

    int32_t retry_count() const noexcept { return retry_count_; }
    int32_t consecutive_failures() const noexcept { return consecutive_failures_; }
    int64_t total_switches() const noexcept { return total_switches_; }
    int64_t total_reconnections() const noexcept { return total_reconnections_; }

private:
    StreamerConfig config_;
    std::atomic<StreamerState> state_;

    int32_t retry_count_;
    int32_t consecutive_failures_;  // Counts both stalls and connection errors
    int64_t total_switches_;
    int64_t total_reconnections_;

    std::chrono::steady_clock::time_point last_data_time_;
    std::mt19937 rng_;
};

}  // namespace tsduck_interop::streaming

#endif  // TSDUCK_INTEROP_STREAMING_FAILOVER_MANAGER_HPP
