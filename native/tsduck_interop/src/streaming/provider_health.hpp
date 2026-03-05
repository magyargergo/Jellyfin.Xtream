// Copyright (C) 2025 Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Circuit breaker logic adapted from Apache brpc (Apache License 2.0)
// https://github.com/apache/brpc/blob/master/src/brpc/circuit_breaker.cpp
// Original authors: The Apache Software Foundation

#ifndef TSDUCK_INTEROP_STREAMING_PROVIDER_HEALTH_HPP
#define TSDUCK_INTEROP_STREAMING_PROVIDER_HEALTH_HPP

#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <memory>
#include <mutex>
#include <random>
#include <shared_mutex>
#include <string>
#include <thread>
#include <vector>

#include "../core/constants.hpp"

namespace tsduck_interop::streaming {

// ============================================================================
// Configuration
// ============================================================================

/// Configuration for the circuit breaker (adapted from brpc gflags).
struct CircuitBreakerConfig {
    // Window sizes (number of samples)
    int32_t short_window_size = 1500;
    int32_t long_window_size = 3000;

    // Maximum error percentages (0-99)
    int32_t short_window_error_percent = 10;
    int32_t long_window_error_percent = 5;

    // Error cost thresholds
    int32_t min_error_cost_us = 500;
    int32_t max_failed_latency_multiple = 2;

    // Isolation timing (milliseconds)
    int32_t min_isolation_duration_ms = 100;
    int32_t max_isolation_duration_ms = 30000;

    // EMA smoothing coefficient base
    // smooth = pow(epsilon, 1.0 / window_size)
    double epsilon = 0.02;

    // Half-open window (0 = disabled)
    // Number of successful requests required before closing circuit
    int32_t half_open_window_size = 3;
};

/// Configuration for latency-based load balancing.
struct LoadBalancerConfig {
    // EWMA decay time in nanoseconds (default 10 seconds)
    double ewma_decay_ns = 10'000'000'000.0;

    // Enable P2C (Power of Two Choices) selection
    bool enable_p2c = true;

    // Outlier detection settings
    bool enable_outlier_detection = true;
    int32_t min_samples_for_outlier = 10;
    double outlier_stddev_factor = 1.9;  // Envoy default

    // Probation settings
    int32_t probation_success_threshold = 3;
};

/// Combined configuration for the unified health system.
struct ProviderHealthConfig {
    CircuitBreakerConfig circuit_breaker;
    LoadBalancerConfig load_balancer;

    // DNS failure handling
    int32_t dns_failure_threshold = 3;         // Failures before force ejection
    int64_t dns_ejection_duration_ms = 300000; // 5 minutes
};

// ============================================================================
// Provider State
// ============================================================================

/// Provider state for the three-state model.
enum class ProviderState : int32_t {
    Active = 0,     ///< Fully healthy, eligible for selection
    Probation = 1,  ///< Recently recovered, under observation
    Ejected = 2     ///< Circuit open, temporarily unavailable
};

// ============================================================================
// Latency EWMA Tracker (from Finagle)
// ============================================================================

/// Exponentially Weighted Moving Average for latency tracking.
/// Based on Finagle's Peak EWMA with configurable decay time.
class LatencyEwma {
public:
    explicit LatencyEwma(double decay_ns = 10'000'000'000.0) noexcept
        : decay_ns_(decay_ns) {}

    /// Update EWMA with a new latency sample.
    /// @param latency_ms Latency in milliseconds.
    /// @param now_ns Current time in nanoseconds.
    void update(double latency_ms, int64_t now_ns) noexcept {
        double old_val, new_val;

        // CAS loop to ensure atomic read-modify-write on value_
        do {
            int64_t last = last_update_ns_.load(std::memory_order_acquire);
            old_val = value_.load(std::memory_order_acquire);

            double weight;
            if (last == 0) {
                weight = 1.0;  // First sample
            } else {
                double elapsed = static_cast<double>(now_ns - last);
                weight = 1.0 - std::exp(-elapsed / decay_ns_);
            }

            new_val = (old_val * (1.0 - weight)) + (latency_ms * weight);
        } while (!value_.compare_exchange_weak(old_val, new_val,
                                                std::memory_order_release,
                                                std::memory_order_relaxed));

        // Update timestamp - concurrent stores are fine (latest caller wins)
        last_update_ns_.store(now_ns, std::memory_order_release);
    }

    /// Get current EWMA value with time decay applied.
    /// @param now_ns Current time in nanoseconds.
    /// @return Decayed latency estimate in milliseconds.
    [[nodiscard]] double get(int64_t now_ns) const noexcept {
        int64_t last = last_update_ns_.load(std::memory_order_acquire);
        if (last == 0) return 0.0;

        double elapsed = static_cast<double>(now_ns - last);
        double decay = std::exp(-elapsed / decay_ns_);
        return value_.load(std::memory_order_acquire) * decay;
    }

    /// Reset the EWMA tracker.
    void reset() noexcept {
        value_.store(0.0, std::memory_order_relaxed);
        last_update_ns_.store(0, std::memory_order_relaxed);
    }

private:
    double decay_ns_;
    alignas(CACHE_LINE_SIZE) std::atomic<double> value_{0.0};
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> last_update_ns_{0};
};

// ============================================================================
// Circuit Breaker (adapted from brpc)
// ============================================================================

/// EMA-based error recorder for circuit breaker.
/// Tracks error patterns using exponential moving average across a window.
class EmaErrorRecorder {
public:
    EmaErrorRecorder(int32_t window_size, int32_t max_error_percent, double epsilon) noexcept
        : window_size_(window_size)
        , max_error_percent_(max_error_percent)
        , smooth_(std::pow(epsilon, 1.0 / window_size)) {}

    /// Record a call result.
    /// @param error_code Error code (0 = success).
    /// @param latency_us Latency in microseconds.
    /// @param max_latency_multiple Maximum latency multiple for error cost capping.
    /// @return true if healthy, false if should isolate.
    [[nodiscard]] bool on_call_end(int32_t error_code, int64_t latency_us,
                                   int32_t max_latency_multiple) noexcept {
        int64_t ema_latency = 0;
        bool healthy = false;

        if (error_code == 0) {
            ema_latency = update_latency(latency_us);
            healthy = update_error_cost(0, ema_latency, max_latency_multiple);
        } else {
            ema_latency = ema_latency_.load(std::memory_order_relaxed);
            healthy = update_error_cost(latency_us, ema_latency, max_latency_multiple);
        }

        // During initialization, use simple error rate
        int32_t sample_count = sample_count_init_.load(std::memory_order_relaxed);
        if (sample_count < window_size_) {
            sample_count = sample_count_init_.fetch_add(1, std::memory_order_relaxed);
            if (sample_count < window_size_) {
                if (error_code != 0) {
                    int32_t error_count = error_count_init_.fetch_add(1, std::memory_order_relaxed);
                    return error_count < window_size_ * max_error_percent_ / 100;
                }
                return true;
            }
        }

        return healthy;
    }

    /// Reset the recorder.
    void reset() noexcept {
        if (sample_count_init_.load(std::memory_order_relaxed) < window_size_) {
            sample_count_init_.store(0, std::memory_order_relaxed);
            error_count_init_.store(0, std::memory_order_relaxed);
            ema_latency_.store(0, std::memory_order_relaxed);
        }
        ema_error_cost_.store(0, std::memory_order_relaxed);
    }

private:
    // Maximum CAS retry attempts before giving up (prevents livelock)
    static constexpr int32_t kMaxCasRetries = 64;

    [[nodiscard]] int64_t update_latency(int64_t latency_us) noexcept {
        int64_t ema_latency = ema_latency_.load(std::memory_order_relaxed);
        for (int32_t retry = 0; retry < kMaxCasRetries; ++retry) {
            int64_t next_ema_latency;
            if (ema_latency == 0) {
                next_ema_latency = latency_us;
            } else {
                next_ema_latency = static_cast<int64_t>(
                    ema_latency * smooth_ + latency_us * (1.0 - smooth_));
            }
            if (ema_latency_.compare_exchange_weak(ema_latency, next_ema_latency,
                    std::memory_order_relaxed)) {
                return next_ema_latency;
            }
            // Yield on every 8th retry to prevent CPU spinning
            if ((retry & 7) == 7) {
                std::this_thread::yield();
            }
        }
        // Give up after max retries - return current raw value
        return latency_us;
    }

    [[nodiscard]] bool update_error_cost(int64_t error_cost, int64_t ema_latency,
                                         int32_t max_multiple) noexcept {
        if (ema_latency != 0) {
            error_cost = std::min(ema_latency * max_multiple, error_cost);
        }

        // Erroneous response
        if (error_cost != 0) {
            int64_t ema_error_cost = ema_error_cost_.fetch_add(error_cost, std::memory_order_relaxed);
            ema_error_cost += error_cost;
            const double epsilon = std::pow(smooth_, window_size_);  // Recover epsilon
            const int64_t max_error_cost = static_cast<int64_t>(
                ema_latency * window_size_ * (max_error_percent_ / 100.0) * (1.0 + epsilon));
            return ema_error_cost <= max_error_cost;
        }

        // Successful response - decay error cost (with retry limit)
        int64_t ema_error_cost = ema_error_cost_.load(std::memory_order_relaxed);
        for (int32_t retry = 0; retry < kMaxCasRetries; ++retry) {
            if (ema_error_cost == 0) {
                break;
            } else if (ema_error_cost < 500) {  // min_error_cost_us
                if (ema_error_cost_.compare_exchange_weak(ema_error_cost, 0,
                        std::memory_order_relaxed)) {
                    break;
                }
            } else {
                int64_t next_ema_error_cost = static_cast<int64_t>(ema_error_cost * smooth_);
                if (ema_error_cost_.compare_exchange_weak(ema_error_cost, next_ema_error_cost,
                        std::memory_order_relaxed)) {
                    break;
                }
            }
            // Yield on every 8th retry to prevent CPU spinning
            if ((retry & 7) == 7) {
                std::this_thread::yield();
            }
        }
        return true;
    }

    const int32_t window_size_;
    const int32_t max_error_percent_;
    const double smooth_;

    alignas(CACHE_LINE_SIZE) std::atomic<int32_t> sample_count_init_{0};
    alignas(CACHE_LINE_SIZE) std::atomic<int32_t> error_count_init_{0};
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> ema_error_cost_{0};
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> ema_latency_{0};
};

/// Circuit breaker with dual-window EMA tracking.
/// Adapted from Apache brpc's circuit breaker implementation.
class CircuitBreaker {
public:
    explicit CircuitBreaker(const CircuitBreakerConfig& config = {}) noexcept
        : config_(config)
        , long_window_(config.long_window_size, config.long_window_error_percent, config.epsilon)
        , short_window_(config.short_window_size, config.short_window_error_percent, config.epsilon)
        , isolation_duration_ms_(config.min_isolation_duration_ms) {}

    /// Record a call result.
    /// @param error_code Error code (0 = success).
    /// @param latency_us Latency in microseconds.
    /// @return true if healthy, false if circuit is open.
    [[nodiscard]] bool on_call_end(int32_t error_code, int64_t latency_us) noexcept {
        if (broken_.load(std::memory_order_relaxed)) {
            return false;
        }

        // Half-open state handling
        if (config_.half_open_window_size > 0 &&
            half_open_.load(std::memory_order_relaxed)) {
            if (error_code != 0) {
                mark_as_broken();
                return false;
            }
            if (half_open_success_count_.fetch_add(1, std::memory_order_relaxed) + 1 >=
                config_.half_open_window_size) {
                half_open_.store(false, std::memory_order_relaxed);
                half_open_success_count_.store(0, std::memory_order_relaxed);
            }
        }

        // Check both windows
        bool long_ok = long_window_.on_call_end(error_code, latency_us,
                                                 config_.max_failed_latency_multiple);
        bool short_ok = short_window_.on_call_end(error_code, latency_us,
                                                   config_.max_failed_latency_multiple);

        if (long_ok && short_ok) {
            return true;
        }

        mark_as_broken();
        return false;
    }

    /// Reset the circuit breaker (typically called by health check).
    void reset() noexcept {
        long_window_.reset();
        short_window_.reset();
        last_reset_time_ms_ = now_ms();
        broken_.store(false, std::memory_order_release);

        if (config_.half_open_window_size > 0) {
            half_open_.store(true, std::memory_order_relaxed);
            half_open_success_count_.store(0, std::memory_order_relaxed);
        }
    }

    /// Mark the circuit as broken (open).
    void mark_as_broken() noexcept {
        if (!broken_.exchange(true, std::memory_order_acquire)) {
            isolated_times_.fetch_add(1, std::memory_order_relaxed);
            update_isolation_duration();
        }
    }

    /// Check if circuit is open (broken).
    [[nodiscard]] bool is_broken() const noexcept {
        return broken_.load(std::memory_order_relaxed);
    }

    /// Check if circuit is in half-open state.
    [[nodiscard]] bool is_half_open() const noexcept {
        return half_open_.load(std::memory_order_relaxed);
    }

    /// Get number of times the circuit has been opened.
    [[nodiscard]] int32_t isolated_times() const noexcept {
        return isolated_times_.load(std::memory_order_relaxed);
    }

    /// Get current isolation duration in milliseconds.
    [[nodiscard]] int32_t isolation_duration_ms() const noexcept {
        return isolation_duration_ms_.load(std::memory_order_relaxed);
    }

private:
    void update_isolation_duration() noexcept {
        int64_t now_time_ms = now_ms();
        int32_t duration = isolation_duration_ms_.load(std::memory_order_relaxed);

        if (now_time_ms - last_reset_time_ms_ < config_.max_isolation_duration_ms) {
            duration = std::min(duration * 2, config_.max_isolation_duration_ms);
        } else {
            duration = config_.min_isolation_duration_ms;
        }
        isolation_duration_ms_.store(duration, std::memory_order_relaxed);
    }

    [[nodiscard]] static int64_t now_ms() noexcept {
        using namespace std::chrono;
        return duration_cast<milliseconds>(steady_clock::now().time_since_epoch()).count();
    }

    CircuitBreakerConfig config_;
    EmaErrorRecorder long_window_;
    EmaErrorRecorder short_window_;
    int64_t last_reset_time_ms_{0};

    alignas(CACHE_LINE_SIZE) std::atomic<int32_t> isolation_duration_ms_;
    alignas(CACHE_LINE_SIZE) std::atomic<int32_t> isolated_times_{0};
    alignas(CACHE_LINE_SIZE) std::atomic<bool> broken_{false};
    alignas(CACHE_LINE_SIZE) std::atomic<bool> half_open_{false};
    alignas(CACHE_LINE_SIZE) std::atomic<int32_t> half_open_success_count_{0};
};

// ============================================================================
// Provider Health (combines circuit breaker + latency EWMA + load tracking)
// ============================================================================

/// Complete health state for a single provider.
struct ProviderHealth {
    explicit ProviderHealth(int32_t idx, std::string url_str,
                            double quality, const ProviderHealthConfig& config)
        : index(idx)
        , url(std::move(url_str))
        , static_quality(quality)
        , breaker(config.circuit_breaker)
        , latency_ewma(config.load_balancer.ewma_decay_ns) {}

    // Identity
    int32_t index;
    std::string url;
    double static_quality;  // 0-100, from channel registry

    // Circuit breaker (from brpc)
    CircuitBreaker breaker;

    // Latency tracking (from Finagle)
    LatencyEwma latency_ewma;

    // Load tracking (for P2C)
    alignas(CACHE_LINE_SIZE) std::atomic<int32_t> active_requests{0};

    // State machine
    alignas(CACHE_LINE_SIZE) std::atomic<ProviderState> state{ProviderState::Active};
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> ejected_until_ns{0};
    alignas(CACHE_LINE_SIZE) std::atomic<int32_t> probation_successes{0};

    // Statistics (for outlier detection)
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> total_requests{0};
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> total_successes{0};
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> last_success_ns{0};
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> last_failure_ns{0};

    // DNS-specific tracking (cache-line aligned to prevent false sharing)
    alignas(CACHE_LINE_SIZE) std::atomic<int32_t> dns_failure_count{0};
    alignas(CACHE_LINE_SIZE) std::atomic<int64_t> last_dns_failure_ns{0};
};

// ============================================================================
// DNS Failure Policy
// ============================================================================

/// Policy returned by on_dns_failure() to guide failover behavior.
enum class DnsFailurePolicy : int32_t {
    Switch = 0,         ///< Switch to next URL (transient failure)
    EjectAndSwitch = 1  ///< Force eject provider, then switch (persistent failure)
};

// ============================================================================
// Unified Provider Health Manager
// ============================================================================

/// Unified health manager combining circuit breaker, EWMA, P2C, and outlier detection.
class UnifiedProviderHealthManager {
public:
    explicit UnifiedProviderHealthManager(const ProviderHealthConfig& config = {}) noexcept
        : config_(config) {}

    // ========================================================================
    // Provider Registration
    // ========================================================================

    /// Register a provider URL with initial quality score.
    /// @return Provider index for future reference.
    int32_t register_provider(const std::string& url, double quality_score = 50.0) {
        std::unique_lock lock(mutex_);
        int32_t index = static_cast<int32_t>(providers_.size());
        providers_.push_back(std::make_unique<ProviderHealth>(index, url, quality_score, config_));
        return index;
    }

    /// Clear all registered providers.
    void clear_providers() {
        std::unique_lock lock(mutex_);
        providers_.clear();
    }

    /// Get number of registered providers.
    [[nodiscard]] int32_t provider_count() const noexcept {
        std::shared_lock lock(mutex_);
        return static_cast<int32_t>(providers_.size());
    }

    /// Get provider URL by index.
    [[nodiscard]] const std::string& get_provider_url(int32_t index) const {
        std::shared_lock lock(mutex_);
        return providers_.at(index)->url;
    }

    // ========================================================================
    // Selection (P2C + EWMA cost function)
    // ========================================================================

    /// Select the best provider using P2C algorithm.
    /// @param excluded_indices Providers to skip.
    /// @return Provider index, or -1 if none available.
    [[nodiscard]] int32_t select_provider(
        const std::vector<int32_t>& excluded_indices = {}) noexcept {

        std::shared_lock lock(mutex_);
        if (providers_.empty()) return -1;

        int64_t now = now_ns();

        // Build list of eligible providers
        std::vector<int32_t> eligible;
        eligible.reserve(providers_.size());

        for (auto& p : providers_) {
            // Check ejection expiry
            check_ejection_expiry(*p, now);

            // Skip if ejected
            if (p->state.load(std::memory_order_acquire) == ProviderState::Ejected) {
                continue;
            }

            // Skip if excluded
            bool is_excluded = false;
            for (int32_t ex : excluded_indices) {
                if (ex == p->index) {
                    is_excluded = true;
                    break;
                }
            }
            if (is_excluded) continue;

            eligible.push_back(p->index);
        }

        if (eligible.empty()) {
            // All providers ejected - return least recently failed
            return select_least_recently_failed(excluded_indices);
        }

        if (eligible.size() == 1) {
            return eligible[0];
        }

        // P2C: Pick 2 random, select better
        if (!config_.load_balancer.enable_p2c || eligible.size() == 2) {
            double cost_a = compute_cost(*providers_[eligible[0]], now);
            double cost_b = compute_cost(*providers_[eligible[1]], now);
            return (cost_a <= cost_b) ? eligible[0] : eligible[1];
        }

        // Use thread_local RNG to avoid concurrent corruption under shared_lock
        static thread_local std::mt19937 tl_rng{std::random_device{}()};
        std::uniform_int_distribution<size_t> dist(0, eligible.size() - 1);
        size_t pick_a = dist(tl_rng);
        size_t pick_b = dist(tl_rng);
        while (pick_b == pick_a && eligible.size() > 2) {
            pick_b = dist(tl_rng);
        }

        double cost_a = compute_cost(*providers_[eligible[pick_a]], now);
        double cost_b = compute_cost(*providers_[eligible[pick_b]], now);

        return (cost_a <= cost_b) ? eligible[pick_a] : eligible[pick_b];
    }

    // ========================================================================
    // Event Recording
    // ========================================================================

    /// Record start of a request.
    void on_request_start(int32_t provider_index) noexcept {
        std::shared_lock lock(mutex_);
        if (provider_index < 0 || provider_index >= static_cast<int32_t>(providers_.size())) return;

        auto& p = *providers_[provider_index];
        p.active_requests.fetch_add(1, std::memory_order_relaxed);
        p.total_requests.fetch_add(1, std::memory_order_relaxed);
    }

    /// Record successful data reception.
    /// @param latency_ms Connection/response latency in milliseconds.
    void on_success(int32_t provider_index, double latency_ms) noexcept {
        std::shared_lock lock(mutex_);
        if (provider_index < 0 || provider_index >= static_cast<int32_t>(providers_.size())) return;

        auto& p = *providers_[provider_index];
        int64_t now = now_ns();

        // Update circuit breaker (success always returns true, ignore return)
        int64_t latency_us = static_cast<int64_t>(latency_ms * 1000.0);
        (void)p.breaker.on_call_end(0, latency_us);

        // Update latency EWMA
        p.latency_ewma.update(latency_ms, now);

        // Update statistics
        p.total_successes.fetch_add(1, std::memory_order_relaxed);
        p.last_success_ns.store(now, std::memory_order_relaxed);

        // Handle probation state
        if (p.state.load(std::memory_order_relaxed) == ProviderState::Probation) {
            int32_t successes = p.probation_successes.fetch_add(1, std::memory_order_relaxed) + 1;
            if (successes >= config_.load_balancer.probation_success_threshold) {
                p.state.store(ProviderState::Active, std::memory_order_release);
                p.probation_successes.store(0, std::memory_order_relaxed);
            }
        }
    }

    /// Record a failure.
    /// @param latency_ms Time until failure in milliseconds.
    void on_failure(int32_t provider_index, double latency_ms = 0.0) noexcept {
        std::shared_lock lock(mutex_);
        if (provider_index < 0 || provider_index >= static_cast<int32_t>(providers_.size())) return;

        auto& p = *providers_[provider_index];
        int64_t now = now_ns();

        // Update circuit breaker
        int64_t latency_us = static_cast<int64_t>(latency_ms * 1000.0);
        bool healthy = p.breaker.on_call_end(1, latency_us);

        // Update statistics
        p.last_failure_ns.store(now, std::memory_order_relaxed);

        // Handle state transitions
        ProviderState current = p.state.load(std::memory_order_relaxed);
        if (current == ProviderState::Probation) {
            // Failure during probation - re-eject with longer duration
            eject_provider(p, now);
        } else if (!healthy) {
            // Circuit breaker tripped
            eject_provider(p, now);
        }
    }

    /// Record end of a request.
    void on_request_end(int32_t provider_index) noexcept {
        std::shared_lock lock(mutex_);
        if (provider_index < 0 || provider_index >= static_cast<int32_t>(providers_.size())) return;

        auto& p = *providers_[provider_index];
        p.active_requests.fetch_sub(1, std::memory_order_relaxed);
    }

    // ========================================================================
    // Outlier Detection (from Envoy)
    // ========================================================================

    /// Run statistical outlier detection.
    /// Call periodically (e.g., every 10 seconds) to detect underperforming providers.
    void run_outlier_detection() noexcept {
        if (!config_.load_balancer.enable_outlier_detection) return;

        std::shared_lock lock(mutex_);
        if (providers_.size() < 2) return;

        int64_t now = now_ns();

        // Store provider index with rate to avoid index misalignment from atomic changes
        struct ProviderRate {
            size_t provider_idx;
            double rate;
        };

        // Calculate success rates with provider index
        std::vector<ProviderRate> rates;
        rates.reserve(providers_.size());

        for (size_t i = 0; i < providers_.size(); ++i) {
            auto& p = providers_[i];
            int64_t total = p->total_requests.load(std::memory_order_relaxed);
            if (total < config_.load_balancer.min_samples_for_outlier) {
                continue;  // Not enough data
            }
            int64_t successes = p->total_successes.load(std::memory_order_relaxed);
            rates.push_back({i, static_cast<double>(successes) / static_cast<double>(total)});
        }

        if (rates.size() < 2) return;

        // Calculate mean
        double sum = 0.0;
        for (const auto& r : rates) sum += r.rate;
        double mean = sum / static_cast<double>(rates.size());

        // Calculate stddev
        double variance_sum = 0.0;
        for (const auto& r : rates) {
            double diff = r.rate - mean;
            variance_sum += diff * diff;
        }
        double variance = variance_sum / static_cast<double>(rates.size());
        double stddev = std::sqrt(variance);

        // Ejection threshold (Envoy formula)
        double threshold = mean - (config_.load_balancer.outlier_stddev_factor * stddev);

        // Check each provider using stored indices
        for (const auto& pr : rates) {
            auto& p = providers_[pr.provider_idx];

            // Only eject Active providers
            if (p->state.load(std::memory_order_relaxed) != ProviderState::Active) {
                continue;
            }

            if (pr.rate < threshold && pr.rate < mean) {
                // Statistical outlier - eject
                eject_provider(*p, now);
            }
        }
    }

    // ========================================================================
    // State Queries
    // ========================================================================

    /// Get current state of a provider.
    [[nodiscard]] ProviderState get_state(int32_t provider_index) const noexcept {
        std::shared_lock lock(mutex_);
        if (provider_index < 0 || provider_index >= static_cast<int32_t>(providers_.size())) {
            return ProviderState::Ejected;
        }
        return providers_[provider_index]->state.load(std::memory_order_acquire);
    }

    /// Check if a provider is currently ejected.
    /// Used by URL selection to skip ejected providers.
    [[nodiscard]] bool is_ejected(int32_t provider_index) const noexcept {
        return get_state(provider_index) == ProviderState::Ejected;
    }

    /// Health snapshot for diagnostics.
    struct HealthSnapshot {
        int32_t provider_index;
        ProviderState state;
        double success_rate;
        double latency_ewma_ms;
        int32_t active_requests;
        int32_t isolated_times;
        int32_t isolation_duration_ms;
    };

    /// Get health snapshot for diagnostics.
    [[nodiscard]] HealthSnapshot get_snapshot(int32_t provider_index) const noexcept {
        std::shared_lock lock(mutex_);
        HealthSnapshot snap{};
        snap.provider_index = provider_index;

        if (provider_index < 0 || provider_index >= static_cast<int32_t>(providers_.size())) {
            snap.state = ProviderState::Ejected;
            return snap;
        }

        auto& p = *providers_[provider_index];
        int64_t now = now_ns();

        snap.state = p.state.load(std::memory_order_relaxed);
        snap.latency_ewma_ms = p.latency_ewma.get(now);
        snap.active_requests = p.active_requests.load(std::memory_order_relaxed);
        snap.isolated_times = p.breaker.isolated_times();
        snap.isolation_duration_ms = p.breaker.isolation_duration_ms();

        int64_t total = p.total_requests.load(std::memory_order_relaxed);
        int64_t successes = p.total_successes.load(std::memory_order_relaxed);
        snap.success_rate = (total > 0) ? static_cast<double>(successes) / total : 1.0;

        return snap;
    }

    // ========================================================================
    // Administrative
    // ========================================================================

    /// Reset all providers to Active state.
    void reset_all() noexcept {
        std::shared_lock lock(mutex_);
        for (auto& p : providers_) {
            p->breaker.reset();
            p->state.store(ProviderState::Active, std::memory_order_release);
            p->ejected_until_ns.store(0, std::memory_order_relaxed);
            p->probation_successes.store(0, std::memory_order_relaxed);
            p->latency_ewma.reset();
        }
    }

    /// Force eject a provider (e.g., for DNS/SSL errors).
    void force_eject(int32_t provider_index, int64_t duration_ms) noexcept {
        std::shared_lock lock(mutex_);
        if (provider_index < 0 || provider_index >= static_cast<int32_t>(providers_.size())) return;

        auto& p = *providers_[provider_index];
        int64_t now = now_ns();
        int64_t duration_ns = duration_ms * 1'000'000;

        p.breaker.mark_as_broken();
        p.state.store(ProviderState::Ejected, std::memory_order_release);
        p.ejected_until_ns.store(now + duration_ns, std::memory_order_release);
    }

    /// Check and recover any ejected providers whose quarantine has expired.
    /// Call this periodically or before checking provider state to ensure
    /// ejected providers transition to Probation when their time is up.
    void check_recovery() noexcept {
        std::shared_lock lock(mutex_);
        int64_t now = now_ns();
        for (auto& p : providers_) {
            check_ejection_expiry(*p, now);
        }
    }

    // ========================================================================
    // DNS Failure Handling
    // ========================================================================

    /// Handle a DNS failure for a provider.
    /// Implements three-tier policy:
    /// - Transient (1-2 failures): Switch URLs, don't eject
    /// - Persistent (>=3 failures): Force eject for 5 minutes, then switch
    /// @param provider_index The provider that experienced DNS failure.
    /// @return Policy indicating whether to just switch or eject and switch.
    [[nodiscard]] [[gnu::hot]] DnsFailurePolicy on_dns_failure(int32_t provider_index) noexcept {
        std::shared_lock lock(mutex_);
        if (provider_index < 0 || provider_index >= static_cast<int32_t>(providers_.size())) [[unlikely]] {
            return DnsFailurePolicy::Switch;
        }

        auto& p = *providers_[provider_index];
        int64_t now = now_ns();

        // Increment DNS failure counter (relaxed - no ordering requirements)
        int32_t failures = p.dns_failure_count.fetch_add(1, std::memory_order_relaxed) + 1;
        p.last_dns_failure_ns.store(now, std::memory_order_relaxed);

        // Check threshold
        if (failures >= config_.dns_failure_threshold) [[unlikely]] {
            // Persistent failure - force eject
            int64_t duration_ns = config_.dns_ejection_duration_ms * 1'000'000;
            p.breaker.mark_as_broken();
            p.state.store(ProviderState::Ejected, std::memory_order_release);
            p.ejected_until_ns.store(now + duration_ns, std::memory_order_release);
            // Reset counter for when provider comes back from ejection
            p.dns_failure_count.store(0, std::memory_order_relaxed);
            return DnsFailurePolicy::EjectAndSwitch;
        }

        // Transient failure - just switch to next URL
        return DnsFailurePolicy::Switch;
    }

    /// Record successful DNS resolution / connection for a provider.
    /// Resets the DNS failure counter.
    /// @param provider_index The provider that successfully connected.
    [[gnu::hot]] void on_dns_success(int32_t provider_index) noexcept {
        std::shared_lock lock(mutex_);
        if (provider_index < 0 || provider_index >= static_cast<int32_t>(providers_.size())) [[unlikely]] {
            return;
        }

        auto& p = *providers_[provider_index];
        // Reset DNS failure counter on success (relaxed - no ordering requirements)
        p.dns_failure_count.store(0, std::memory_order_relaxed);
    }

    /// Get current DNS failure count for a provider.
    /// @param provider_index The provider to query.
    /// @return Current DNS failure count, or 0 if invalid index.
    [[nodiscard]] int32_t dns_failure_count(int32_t provider_index) const noexcept {
        std::shared_lock lock(mutex_);
        if (provider_index < 0 || provider_index >= static_cast<int32_t>(providers_.size())) {
            return 0;
        }
        return providers_[provider_index]->dns_failure_count.load(std::memory_order_relaxed);
    }

private:
    [[nodiscard]] static int64_t now_ns() noexcept {
        using namespace std::chrono;
        return duration_cast<nanoseconds>(steady_clock::now().time_since_epoch()).count();
    }

    [[nodiscard]] double compute_cost(const ProviderHealth& p, int64_t now) const noexcept {
        // Finagle P2C formula: cost = latency * (active_requests + 1)
        double latency = p.latency_ewma.get(now);
        if (latency < 1.0) latency = 1.0;  // Minimum 1ms

        int32_t load = p.active_requests.load(std::memory_order_relaxed);
        double load_factor = static_cast<double>(load + 1);

        // Incorporate static quality (lower quality = higher cost)
        double quality_penalty = (100.0 - p.static_quality) / 100.0;

        return (latency * load_factor) + (quality_penalty * 100.0);
    }

    void check_ejection_expiry(ProviderHealth& p, int64_t now) const noexcept {
        if (p.state.load(std::memory_order_relaxed) != ProviderState::Ejected) {
            return;
        }

        int64_t until = p.ejected_until_ns.load(std::memory_order_relaxed);
        if (now >= until) {
            // Ejection expired - move to probation
            p.breaker.reset();
            p.state.store(ProviderState::Probation, std::memory_order_release);
            p.probation_successes.store(0, std::memory_order_relaxed);
        }
    }

    void eject_provider(ProviderHealth& p, int64_t now) noexcept {
        p.breaker.mark_as_broken();
        int64_t duration_ms = p.breaker.isolation_duration_ms();
        int64_t duration_ns = duration_ms * 1'000'000;

        p.state.store(ProviderState::Ejected, std::memory_order_release);
        p.ejected_until_ns.store(now + duration_ns, std::memory_order_release);
        p.probation_successes.store(0, std::memory_order_relaxed);
    }

    [[nodiscard]] int32_t select_least_recently_failed(
        const std::vector<int32_t>& excluded) const noexcept {

        int32_t best = -1;
        int64_t oldest_failure = INT64_MAX;

        for (auto& p : providers_) {
            bool is_excluded = false;
            for (int32_t ex : excluded) {
                if (ex == p->index) {
                    is_excluded = true;
                    break;
                }
            }
            if (is_excluded) continue;

            int64_t last_fail = p->last_failure_ns.load(std::memory_order_relaxed);
            if (last_fail < oldest_failure) {
                oldest_failure = last_fail;
                best = p->index;
            }
        }

        return best;
    }

    ProviderHealthConfig config_;
    std::vector<std::unique_ptr<ProviderHealth>> providers_;
    mutable std::shared_mutex mutex_;
};

}  // namespace tsduck_interop::streaming

#endif  // TSDUCK_INTEROP_STREAMING_PROVIDER_HEALTH_HPP
