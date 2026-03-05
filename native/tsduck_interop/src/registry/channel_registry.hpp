// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_REGISTRY_CHANNEL_REGISTRY_HPP
#define TSDUCK_INTEROP_REGISTRY_CHANNEL_REGISTRY_HPP

#include <atomic>
#include <chrono>
#include <cstdint>
#include <functional>
#include <memory>
#include <mutex>
#include <shared_mutex>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#include "../streaming/provider_health.hpp"

namespace tsduck_interop::registry {

// ============================================================================
// Quality Score Constants (matching C# QualityScorer)
// ============================================================================

constexpr int32_t QUALITY_SCORE_4K = 100;
constexpr int32_t QUALITY_SCORE_FHD = 80;
constexpr int32_t QUALITY_SCORE_HD = 60;
constexpr int32_t QUALITY_SCORE_SD = 40;
constexpr int32_t QUALITY_SCORE_UNKNOWN = 50;
constexpr int32_t QUALITY_SCORE_HAS_ICON = 10;

// ============================================================================
// Disconnect Reason (for health tracking)
// ============================================================================

enum class DisconnectReason : int32_t {
    Unknown = 0,
    Normal = 1,
    Timeout = 2,
    ConnectionFailed = 3,
    HttpError = 4,
    Aborted = 5
};

// ============================================================================
// API Error Type (for reporting C# API call failures)
// ============================================================================

enum class ApiErrorType : int32_t {
    DnsFailure = 0,
    Timeout = 1,
    HttpError = 2,
    AuthFailure = 3
};

// ============================================================================
// Provider Info (blittable for C# interop)
// ============================================================================

struct ProviderInfoNative {
    char id[16];              // Provider ID (null-terminated)
    char name[64];            // Provider display name
    char base_url[512];       // Base URL for API
    char username[128];       // Username for auth
    char password[128];       // Password for auth
    int32_t priority;         // 0=highest priority
    int32_t id_hash;          // Hash for GUID generation
    double initial_health;    // Starting health score (0-100)
};

// ============================================================================
// Registry Stats (blittable for C# interop)
// ============================================================================

struct RegistryStatsNative {
    int32_t provider_count;   // Number of providers registered
    int32_t channel_count;    // Unique channels after deduplication
    int32_t stream_count;     // Total streams before deduplication
    int32_t guid_count;       // Total GUIDs in lookup
    int32_t skipped_count;    // Streams with empty normalized names
};

// ============================================================================
// Channel List Entry (blittable for C# enumeration)
// ============================================================================

struct ChannelListEntryNative {
    int64_t guid_high;
    int64_t guid_low;
    char display_name[128];
    char icon_url[512];
    int32_t provider_count;
    int32_t best_quality_score;
};

// ============================================================================
// Provider Status (blittable for C# diagnostics)
// ============================================================================

struct ProviderStatusNative {
    char id[16];
    char name[64];
    double health_score;          // Derived: success_rate * 100
    int32_t state;                // streaming::ProviderState enum
    int32_t circuit_breaker_state; // 0=closed, 1=open, 2=half-open
    int64_t quarantine_until;     // .NET ticks (0 if not quarantined)
    int32_t channel_count;        // Channels this provider serves
    int32_t consecutive_failures; // From circuit breaker isolated_times
    double latency_ewma_ms;
    double success_rate;
    int32_t reserved;
    int32_t reserved2;
};

// ============================================================================
// Internal Data Structures
// ============================================================================

/// Provider information stored internally
struct Provider {
    std::string id;
    std::string name;
    std::string base_url;
    std::string username;
    std::string password;
    int32_t priority;
    int32_t id_hash;
};

/// A single stream entry from one provider
struct StreamEntry {
    int32_t stream_id;
    int32_t provider_index;
    int32_t quality_score;
    std::string display_name;
    std::string icon_url;

    // Pre-computed streaming URL
    std::string url;

    // Pre-computed GUID (128-bit as two int64s)
    int64_t guid_high;
    int64_t guid_low;
};

/// A channel with all its provider entries
struct Channel {
    std::string normalized_name;
    std::string display_name;
    std::string icon_url;
    std::vector<StreamEntry> entries;  // Sorted by quality (best first)
};

/// 128-bit GUID key for hash map
struct GuidKey {
    int64_t high;
    int64_t low;

    bool operator==(const GuidKey& o) const noexcept {
        return high == o.high && low == o.low;
    }
};

/// Hash function for GuidKey
struct GuidKeyHash {
    std::size_t operator()(const GuidKey& k) const noexcept {
        // FNV-1a-like combining
        return static_cast<std::size_t>(k.high) ^
               (static_cast<std::size_t>(k.low) * 0x9e3779b97f4a7c15ULL);
    }
};

// ============================================================================
// Health Event Callback
// ============================================================================

/// Callback type for health state change events.
/// @param provider_index Index of the affected provider
/// @param event_type Event type string (e.g. "ejected", "recovered", "probation")
/// @param details Additional details string
using HealthEventCallback = std::function<void(int32_t provider_index,
                                                const char* event_type,
                                                const char* details)>;

// ============================================================================
// Channel Registry Class
// ============================================================================

/// Native channel-to-provider registry with O(1) lookups.
///
/// Thread safety:
///   - Build phase: single-threaded (main thread during init)
///   - Lookup phase: reads under shared_mutex
///   - Health updates: delegated to UnifiedProviderHealthManager (lock-free atomics)
///   - Rebuild: takes unique lock, preserves health data
class ChannelRegistry {
public:
    ChannelRegistry() = default;
    ~ChannelRegistry() = default;

    // Non-copyable
    ChannelRegistry(const ChannelRegistry&) = delete;
    ChannelRegistry& operator=(const ChannelRegistry&) = delete;

    // ========================================================================
    // Configuration
    // ========================================================================

    /// Set health manager configuration. Must be called before build().
    void set_health_config(const streaming::ProviderHealthConfig& config) noexcept {
        health_config_ = config;
    }

    // ========================================================================
    // Phase 1: Registration (call from main thread during init)
    // ========================================================================

    /// Add a provider. Returns provider index.
    /// @return Provider index (>=0) or -1 on error
    int32_t add_provider(const ProviderInfoNative& info);

    /// Add a stream for a provider.
    /// @param provider_index Index from add_provider()
    /// @param stream_id Provider's stream ID
    /// @param name Raw channel name (will be normalized)
    /// @param icon_url Optional icon URL (can be nullptr)
    /// @return 0 on success, <0 on error
    int32_t add_stream(int32_t provider_index, int32_t stream_id,
                       const char* name, const char* icon_url);

    // ========================================================================
    // Phase 2: Build (finalizes registration)
    // ========================================================================

    /// Build the registry. After this, no more add_* calls allowed.
    /// Creates the health manager and registers all providers.
    /// @return true on success
    bool build();

    /// Check if registry has been built.
    [[nodiscard]] bool is_built() const noexcept { return built_.load(std::memory_order_acquire); }

    /// Get build statistics.
    [[nodiscard]] bool get_stats(RegistryStatsNative* out) const noexcept;

    // ========================================================================
    // Rebuild (preserves health data)
    // ========================================================================

    /// Begin rebuild: clears channels and GUIDs but preserves health manager
    /// and provider list. After calling, add new streams and call rebuild().
    void begin_rebuild() noexcept;

    /// Finalize rebuild: re-processes streams, regenerates GUIDs.
    /// Health scores are preserved from the existing health manager.
    /// @return true on success
    bool rebuild();

    // ========================================================================
    // Phase 3: Runtime Queries (thread-safe)
    // ========================================================================

    /// Get the best stream URL for a channel GUID.
    /// Uses UnifiedProviderHealthManager P2C selection.
    /// @param guid_high High 64 bits of the GUID
    /// @param guid_low Low 64 bits of the GUID
    /// @param out_url Buffer for URL (at least 1024 chars)
    /// @param out_provider_index Selected provider index
    /// @param out_stream_id Selected stream ID
    /// @param excluded_providers Provider indices to skip (for failover)
    /// @param excluded_count Number of excluded providers
    /// @return 1 if found, 0 if not found, <0 on error
    [[nodiscard]] int32_t get_stream_url(
        int64_t guid_high, int64_t guid_low,
        char* out_url,
        int32_t* out_provider_index,
        int32_t* out_stream_id,
        const int32_t* excluded_providers = nullptr,
        int32_t excluded_count = 0) const noexcept;

    /// Get channel info by GUID.
    struct ChannelInfo {
        char normalized_name[64];
        char display_name[128];
        char icon_url[512];
        int32_t provider_count;
        int32_t best_quality_score;
        int64_t guid_high;
        int64_t guid_low;
    };
    [[nodiscard]] bool get_channel_info(int64_t guid_high, int64_t guid_low,
                                        ChannelInfo* out) const noexcept;

    /// Get all URLs for a channel (for failover enumeration).
    /// @return Number of URLs written, or <0 on error
    [[nodiscard]] int32_t get_all_urls(
        int64_t guid_high, int64_t guid_low,
        char* out_urls,           // Array of 1024-char buffers
        int32_t* out_providers,   // Provider index for each
        int32_t* out_stream_ids,  // Stream ID for each
        int32_t max_urls) const noexcept;

    /// Register an external GUID alias for a stream entry.
    /// The alias GUID (e.g., from C#'s ToProviderGuid) maps to the same channel
    /// as the provider_index + stream_id combination.
    /// Must be called after build().
    /// @return true if the alias was registered
    bool add_guid_alias(int64_t alias_high, int64_t alias_low,
                        int32_t provider_index, int32_t stream_id) noexcept;

    // ========================================================================
    // Channel Enumeration
    // ========================================================================

    /// Get the number of deduplicated channels.
    [[nodiscard]] int32_t get_channel_count() const noexcept;

    /// Enumerate channels with pagination.
    /// @param out Output buffer for channel entries
    /// @param max_count Maximum entries to write
    /// @param offset Skip this many channels
    /// @return Number of entries written
    [[nodiscard]] int32_t enumerate_channels(
        ChannelListEntryNative* out,
        int32_t max_count,
        int32_t offset) const noexcept;

    // ========================================================================
    // Provider Status Query
    // ========================================================================

    /// Get status for all providers.
    /// @param out Output buffer for provider status entries
    /// @param max_count Maximum entries to write
    /// @return Number of entries written
    [[nodiscard]] int32_t get_provider_status(
        ProviderStatusNative* out,
        int32_t max_count) const noexcept;

    // ========================================================================
    // Health Tracking (delegated to UnifiedProviderHealthManager)
    // ========================================================================

    /// Record successful streaming data reception.
    void record_success(int32_t provider_index, int64_t bytes_received) noexcept;

    /// Record streaming failure.
    void record_failure(int32_t provider_index, DisconnectReason reason) noexcept;

    /// Report API call success (from C# XtreamClient).
    void report_api_success(int32_t provider_index, int32_t latency_ms) noexcept;

    /// Report API call failure (from C# XtreamClient).
    void report_api_failure(int32_t provider_index, ApiErrorType error_type) noexcept;

    /// Get provider health score (0-100, derived from success rate).
    [[nodiscard]] double get_provider_health(int32_t provider_index) const noexcept;

    /// Check if provider is quarantined/ejected.
    [[nodiscard]] bool is_provider_quarantined(int32_t provider_index) const noexcept;

    /// Reset all quarantines/ejections.
    void reset_all_quarantines() noexcept;

    // ========================================================================
    // Health Event Callback
    // ========================================================================

    /// Set callback for health state change events.
    void set_health_callback(HealthEventCallback cb) noexcept;

    // ========================================================================
    // Direct access to health manager (for streamer integration)
    // ========================================================================

    /// Get the health manager (for passing to StreamPipeline).
    /// Returns nullptr if not built.
    [[nodiscard]] streaming::UnifiedProviderHealthManager* get_health_manager() noexcept {
        return health_manager_.get();
    }

    [[nodiscard]] const streaming::UnifiedProviderHealthManager* get_health_manager() const noexcept {
        return health_manager_.get();
    }

    // ========================================================================
    // Normalization (static, for testing/external use)
    // ========================================================================

    /// Normalize a channel name.
    /// @param input Raw channel name
    /// @param output Buffer for normalized name (at least 64 chars)
    /// @param output_size Size of output buffer
    /// @return true if non-empty result
    static bool normalize_name(const char* input, char* output, size_t output_size);

    /// Calculate quality score from a name.
    static int32_t calculate_quality_score(const char* name, bool has_icon);

private:
    streaming::ProviderHealthConfig health_config_;
    std::atomic<bool> built_{false};
    mutable std::shared_mutex mutex_;

    // Registration phase data
    std::vector<Provider> providers_;

    // Unified health manager (replaces per-provider ProviderHealth)
    std::unique_ptr<streaming::UnifiedProviderHealthManager> health_manager_;

    // Health event callback
    HealthEventCallback health_callback_;
    std::mutex callback_mutex_;

    // Build phase temporary data
    struct PendingStream {
        int32_t provider_index;
        int32_t stream_id;
        std::string name;
        std::string icon_url;
    };
    std::vector<PendingStream> pending_streams_;

    // Lookup phase data (immutable after build, guarded by shared_mutex for rebuild)
    std::unordered_map<std::string, Channel> channels_;
    std::unordered_map<GuidKey, std::string, GuidKeyHash> guid_to_channel_;

    // Reverse index: (provider_index, stream_id) -> channel_name for O(1) alias lookup
    std::unordered_map<int64_t, std::string> stream_to_channel_;

    // Ordered channel keys for stable enumeration
    std::vector<std::string> channel_keys_;

    // Per-provider channel count cache
    std::vector<int32_t> provider_channel_counts_;

    // Statistics
    int32_t skipped_count_ = 0;

    // Internal helpers
    void process_pending_streams();
    void sort_channel_entries();
    void generate_guids();
    void compute_provider_channel_counts();
    void build_reverse_index();
    std::string build_url(const Provider& provider, int32_t stream_id) const;
    static int64_t get_current_ticks() noexcept;
    static int64_t make_stream_key(int32_t provider_index, int32_t stream_id) noexcept;

    // Fire health event callback
    void fire_health_event(int32_t provider_index, const char* event_type,
                           const char* details) noexcept;

    // Find best entry in a channel using health manager P2C selection
    const StreamEntry* select_best_entry(
        const Channel& channel,
        const std::unordered_set<int32_t>& excluded) const noexcept;
};

// ============================================================================
// Normalization Functions (implemented in channel_normalizer.cpp)
// ============================================================================

/// Normalize a channel name using the full pipeline.
std::string normalize_channel_name(const std::string& input);

/// Calculate quality score from name and icon presence.
int32_t calculate_quality_score(const std::string& name, bool has_icon);

}  // namespace tsduck_interop::registry

#endif  // TSDUCK_INTEROP_REGISTRY_CHANNEL_REGISTRY_HPP
