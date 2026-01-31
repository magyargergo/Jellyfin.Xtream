// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_REGISTRY_CHANNEL_REGISTRY_HPP
#define TSDUCK_INTEROP_REGISTRY_CHANNEL_REGISTRY_HPP

#include <atomic>
#include <chrono>
#include <cstdint>
#include <memory>
#include <mutex>
#include <shared_mutex>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

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

/// Per-provider health tracking (lock-free atomics)
struct ProviderHealth {
    std::atomic<double> score{50.0};
    std::atomic<int64_t> quarantine_until_ticks{0};
    std::atomic<int32_t> consecutive_failures{0};
    std::atomic<int64_t> total_bytes{0};
    std::atomic<int64_t> total_successes{0};
    std::atomic<int64_t> total_failures{0};
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
// Channel Registry Class
// ============================================================================

/// Native channel-to-provider registry with O(1) lookups.
///
/// Thread safety:
///   - Build phase: single-threaded (main thread during init)
///   - Lookup phase: lock-free reads via const methods
///   - Health updates: atomic operations (safe from streaming thread)
class ChannelRegistry {
public:
    ChannelRegistry() = default;
    ~ChannelRegistry() = default;

    // Non-copyable
    ChannelRegistry(const ChannelRegistry&) = delete;
    ChannelRegistry& operator=(const ChannelRegistry&) = delete;

    // ========================================================================
    // Configuration (for health tracking tuning)
    // ========================================================================

    struct Config {
        double score_boost_on_success = 0.5;
        double score_penalty_on_failure = 5.0;
        double score_penalty_on_timeout = 10.0;
        int32_t quarantine_base_ms = 30000;
        int32_t quarantine_max_ms = 300000;
        double quarantine_multiplier = 2.0;
    };

    void set_config(const Config& config) noexcept { config_ = config; }

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
    /// @return true on success
    bool build();

    /// Check if registry has been built.
    [[nodiscard]] bool is_built() const noexcept { return built_; }

    /// Get build statistics.
    [[nodiscard]] bool get_stats(RegistryStatsNative* out) const noexcept;

    // ========================================================================
    // Phase 3: Runtime Queries (thread-safe)
    // ========================================================================

    /// Get the best stream URL for a channel GUID.
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

    // ========================================================================
    // Phase 4: Health Tracking (called from streaming callbacks)
    // ========================================================================

    /// Record successful streaming.
    void record_success(int32_t provider_index, int64_t bytes_received) noexcept;

    /// Record streaming failure.
    void record_failure(int32_t provider_index, DisconnectReason reason) noexcept;

    /// Get provider health score.
    [[nodiscard]] double get_provider_health(int32_t provider_index) const noexcept;

    /// Check if provider is quarantined.
    [[nodiscard]] bool is_provider_quarantined(int32_t provider_index) const noexcept;

    /// Reset all quarantines.
    void reset_all_quarantines() noexcept;

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
    Config config_;
    bool built_ = false;

    // Registration phase data
    std::vector<Provider> providers_;
    std::vector<std::unique_ptr<ProviderHealth>> provider_health_;

    // Build phase temporary data
    struct PendingStream {
        int32_t provider_index;
        int32_t stream_id;
        std::string name;
        std::string icon_url;
    };
    std::vector<PendingStream> pending_streams_;

    // Lookup phase data (immutable after build)
    std::unordered_map<std::string, Channel> channels_;
    std::unordered_map<GuidKey, std::string, GuidKeyHash> guid_to_channel_;

    // Statistics
    int32_t skipped_count_ = 0;

    // Internal helpers
    void process_pending_streams();
    void sort_channel_entries();
    void generate_guids();
    std::string build_url(const Provider& provider, int32_t stream_id) const;
    static int64_t get_current_ticks() noexcept;

    // Find best entry in a channel considering health
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
