// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#include "channel_registry.hpp"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstring>
#include <iomanip>
#include <sstream>
#include <string_view>

namespace tsduck_interop::registry {

// ============================================================================
// Time Utilities
// ============================================================================

int64_t ChannelRegistry::get_current_ticks() noexcept {
    auto now = std::chrono::system_clock::now();
    auto duration = now.time_since_epoch();
    auto ticks = std::chrono::duration_cast<std::chrono::nanoseconds>(duration).count() / 100;
    return ticks + 621355968000000000LL;  // .NET epoch offset
}

// ============================================================================
// Registration Phase
// ============================================================================

int32_t ChannelRegistry::add_provider(const ProviderInfoNative& info) {
    if (built_.load(std::memory_order_relaxed)) {
        return -1;  // Already built
    }

    Provider provider;
    provider.id = info.id;
    provider.name = info.name;
    provider.base_url = info.base_url;
    provider.username = info.username;
    provider.password = info.password;
    provider.priority = info.priority;
    provider.id_hash = info.id_hash;

    auto index = static_cast<int32_t>(providers_.size());
    providers_.push_back(std::move(provider));

    return index;
}

int32_t ChannelRegistry::add_stream(int32_t provider_index, int32_t stream_id,
                                     const char* name, const char* icon_url) {
    if (built_.load(std::memory_order_relaxed)) {
        return -1;  // Already built
    }

    if (provider_index < 0 || provider_index >= static_cast<int32_t>(providers_.size())) {
        return -2;  // Invalid provider index
    }

    if (!name) {
        return -3;  // Null name
    }

    PendingStream pending;
    pending.provider_index = provider_index;
    pending.stream_id = stream_id;
    pending.name = name;
    pending.icon_url = icon_url ? icon_url : "";

    pending_streams_.push_back(std::move(pending));
    return 0;
}

// ============================================================================
// Build Phase
// ============================================================================

bool ChannelRegistry::build() {
    if (built_.load(std::memory_order_relaxed)) {
        return false;  // Already built
    }

    // Create health manager and register all providers
    health_manager_ = std::make_unique<streaming::UnifiedProviderHealthManager>(health_config_);
    for (const auto& provider : providers_) {
        // Use base_url as the provider URL for health tracking
        health_manager_->register_provider(provider.base_url);
    }

    // Process all pending streams
    process_pending_streams();

    // Sort entries within each channel by quality
    sort_channel_entries();

    // Generate GUIDs for all entries
    generate_guids();

    // Compute per-provider channel counts
    compute_provider_channel_counts();

    // Build reverse index for O(1) alias lookups
    build_reverse_index();

    // Clear pending data
    pending_streams_.clear();
    pending_streams_.shrink_to_fit();

    built_.store(true, std::memory_order_release);
    return true;
}

// ============================================================================
// Rebuild Phase (preserves health data)
// ============================================================================

void ChannelRegistry::begin_rebuild() noexcept {
    std::unique_lock lock(mutex_);

    // Clear channel data but keep providers and health manager
    channels_.clear();
    guid_to_channel_.clear();
    stream_to_channel_.clear();
    channel_keys_.clear();
    provider_channel_counts_.clear();
    skipped_count_ = 0;

    // Allow new streams to be added
    built_.store(false, std::memory_order_release);
}

bool ChannelRegistry::rebuild() {
    std::unique_lock lock(mutex_);

    if (built_.load(std::memory_order_acquire)) {
        return false;  // Already built
    }

    // Process pending streams
    process_pending_streams();

    // Sort entries
    sort_channel_entries();

    // Generate GUIDs
    generate_guids();

    // Recompute channel counts
    compute_provider_channel_counts();

    // Build reverse index for O(1) alias lookups
    build_reverse_index();

    // Clear pending data
    pending_streams_.clear();
    pending_streams_.shrink_to_fit();

    built_.store(true, std::memory_order_release);
    return true;
}

void ChannelRegistry::process_pending_streams() {
    for (const auto& pending : pending_streams_) {
        // Normalize the channel name
        std::string normalized = normalize_channel_name(pending.name);

        if (normalized.empty()) {
            ++skipped_count_;
            continue;
        }

        // Find or create the channel
        auto it = channels_.find(normalized);
        if (it == channels_.end()) {
            Channel channel;
            channel.normalized_name = normalized;
            // Use first stream's display name as the channel display name
            channel.display_name = pending.name;
            channel.icon_url = pending.icon_url;
            it = channels_.emplace(normalized, std::move(channel)).first;
        }

        // Update channel display name/icon if this one is better
        Channel& channel = it->second;
        if (channel.icon_url.empty() && !pending.icon_url.empty()) {
            channel.icon_url = pending.icon_url;
        }

        // Create stream entry
        StreamEntry entry;
        entry.stream_id = pending.stream_id;
        entry.provider_index = pending.provider_index;
        entry.quality_score = calculate_quality_score(
            pending.name.c_str(), !pending.icon_url.empty());
        entry.display_name = pending.name;
        entry.icon_url = pending.icon_url;

        // Build the streaming URL
        const Provider& provider = providers_[pending.provider_index];
        entry.url = build_url(provider, pending.stream_id);

        // GUIDs will be generated in generate_guids()
        entry.guid_high = 0;
        entry.guid_low = 0;

        channel.entries.push_back(std::move(entry));
    }
}

void ChannelRegistry::sort_channel_entries() {
    // Build sorted channel keys for stable enumeration
    channel_keys_.clear();
    channel_keys_.reserve(channels_.size());

    for (auto& [name, channel] : channels_) {
        channel_keys_.push_back(name);

        // Sort by quality score (descending), then by provider priority (ascending)
        std::sort(channel.entries.begin(), channel.entries.end(),
            [this](const StreamEntry& a, const StreamEntry& b) {
                // Higher quality score first
                if (a.quality_score != b.quality_score) {
                    return a.quality_score > b.quality_score;
                }
                // Lower priority number first (0 = highest priority)
                int32_t prio_a = providers_[a.provider_index].priority;
                int32_t prio_b = providers_[b.provider_index].priority;
                return prio_a < prio_b;
            });
    }

    // Sort channel keys alphabetically for stable enumeration
    std::sort(channel_keys_.begin(), channel_keys_.end());
}

void ChannelRegistry::generate_guids() {
    // Generate deterministic GUIDs for each stream entry
    // Format: namespace(4) + provider_hash(4) + stream_id(4) + counter(4)
    //
    // Using a simple scheme: pack provider_hash and stream_id into 128 bits
    // This matches the C# StreamService.ToProviderGuid approach

    for (auto& [name, channel] : channels_) {
        for (auto& entry : channel.entries) {
            const Provider& provider = providers_[entry.provider_index];

            // Create a deterministic GUID
            // High: namespace marker + provider hash
            // Low: stream_id + entry index within channel
            int64_t high = 0x4C545600'00000000LL;  // "LTV" marker
            high |= static_cast<int64_t>(provider.id_hash & 0xFFFFFFFF);

            int64_t low = static_cast<int64_t>(entry.stream_id) << 32;
            low |= static_cast<int64_t>(&entry - channel.entries.data());

            entry.guid_high = high;
            entry.guid_low = low;

            // Register in GUID lookup
            GuidKey key{high, low};
            guid_to_channel_[key] = name;
        }
    }
}

void ChannelRegistry::compute_provider_channel_counts() {
    provider_channel_counts_.assign(providers_.size(), 0);

    for (const auto& [name, channel] : channels_) {
        // Track unique providers per channel
        std::unordered_set<int32_t> seen_providers;
        for (const auto& entry : channel.entries) {
            seen_providers.insert(entry.provider_index);
        }
        for (int32_t idx : seen_providers) {
            if (idx >= 0 && idx < static_cast<int32_t>(provider_channel_counts_.size())) {
                ++provider_channel_counts_[idx];
            }
        }
    }
}

std::string ChannelRegistry::build_url(const Provider& provider, int32_t stream_id) const {
    std::string_view base = provider.base_url;
    if (!base.empty() && base.back() == '/') {
        base.remove_suffix(1);
    }

    auto sid = std::to_string(stream_id);
    std::string url;
    url.reserve(base.size() + 6 + provider.username.size() + 1 +
                provider.password.size() + 1 + sid.size() + 3);
    url.append(base);
    url.append("/live/");
    url.append(provider.username);
    url.push_back('/');
    url.append(provider.password);
    url.push_back('/');
    url.append(sid);
    url.append(".ts");
    return url;
}

bool ChannelRegistry::get_stats(RegistryStatsNative* out) const noexcept {
    if (!out) {
        return false;
    }

    std::shared_lock lock(mutex_);

    out->provider_count = static_cast<int32_t>(providers_.size());
    out->channel_count = static_cast<int32_t>(channels_.size());
    out->guid_count = static_cast<int32_t>(guid_to_channel_.size());
    out->skipped_count = skipped_count_;

    // Count total streams
    int32_t stream_count = 0;
    for (const auto& [name, channel] : channels_) {
        stream_count += static_cast<int32_t>(channel.entries.size());
    }
    out->stream_count = stream_count;

    return true;
}

// ============================================================================
// Runtime Queries
// ============================================================================

int32_t ChannelRegistry::get_stream_url(
    int64_t guid_high, int64_t guid_low,
    char* out_url,
    int32_t* out_provider_index,
    int32_t* out_stream_id,
    const int32_t* excluded_providers,
    int32_t excluded_count) const noexcept {

    if (!built_.load(std::memory_order_acquire) || !out_url) {
        return -1;
    }

    std::shared_lock lock(mutex_);

    // Find channel by GUID
    GuidKey key{guid_high, guid_low};
    auto guid_it = guid_to_channel_.find(key);
    if (guid_it == guid_to_channel_.end()) {
        return 0;  // Not found
    }

    auto channel_it = channels_.find(guid_it->second);
    if (channel_it == channels_.end()) {
        return 0;  // Not found (shouldn't happen)
    }

    const Channel& channel = channel_it->second;

    // Build excluded set
    std::unordered_set<int32_t> excluded;
    if (excluded_providers && excluded_count > 0) {
        for (int32_t i = 0; i < excluded_count; ++i) {
            excluded.insert(excluded_providers[i]);
        }
    }

    // Find best entry using health manager P2C selection
    const StreamEntry* best = select_best_entry(channel, excluded);
    if (!best) {
        return 0;  // No available provider
    }

    // Copy URL to output
    std::strncpy(out_url, best->url.c_str(), 1023);
    out_url[1023] = '\0';

    if (out_provider_index) {
        *out_provider_index = best->provider_index;
    }
    if (out_stream_id) {
        *out_stream_id = best->stream_id;
    }

    return 1;
}

const StreamEntry* ChannelRegistry::select_best_entry(
    const Channel& channel,
    const std::unordered_set<int32_t>& excluded) const noexcept {

    if (!health_manager_) {
        // Fallback: return first non-excluded entry
        for (const auto& entry : channel.entries) {
            if (excluded.count(entry.provider_index) == 0) {
                return &entry;
            }
        }
        return nullptr;
    }

    // Build set of provider indices available for this channel (O(1) lookup)
    std::unordered_set<int32_t> channel_providers;
    for (const auto& entry : channel.entries) {
        if (excluded.count(entry.provider_index) == 0) {
            channel_providers.insert(entry.provider_index);
        }
    }

    if (channel_providers.empty()) {
        return nullptr;
    }

    // Build exclusion list: everything NOT in channel_providers
    std::vector<int32_t> full_excluded;
    int32_t provider_count = health_manager_->provider_count();
    for (int32_t i = 0; i < provider_count; ++i) {
        if (channel_providers.count(i) == 0) {
            full_excluded.push_back(i);
        }
    }

    int32_t selected = health_manager_->select_provider(full_excluded);
    if (selected < 0) {
        // Health manager couldn't select - fall back to first available
        for (const auto& entry : channel.entries) {
            if (excluded.count(entry.provider_index) == 0) {
                return &entry;
            }
        }
        return nullptr;
    }

    // Find the stream entry for the selected provider
    for (const auto& entry : channel.entries) {
        if (entry.provider_index == selected) {
            return &entry;
        }
    }

    // Selected provider not in this channel (shouldn't happen) - fallback
    for (const auto& entry : channel.entries) {
        if (excluded.count(entry.provider_index) == 0) {
            return &entry;
        }
    }
    return nullptr;
}

bool ChannelRegistry::get_channel_info(int64_t guid_high, int64_t guid_low,
                                        ChannelInfo* out) const noexcept {
    if (!built_.load(std::memory_order_acquire) || !out) {
        return false;
    }

    std::shared_lock lock(mutex_);

    GuidKey key{guid_high, guid_low};
    auto guid_it = guid_to_channel_.find(key);
    if (guid_it == guid_to_channel_.end()) {
        return false;
    }

    auto channel_it = channels_.find(guid_it->second);
    if (channel_it == channels_.end()) {
        return false;
    }

    const Channel& channel = channel_it->second;

    std::strncpy(out->normalized_name, channel.normalized_name.c_str(), 63);
    out->normalized_name[63] = '\0';

    std::strncpy(out->display_name, channel.display_name.c_str(), 127);
    out->display_name[127] = '\0';

    std::strncpy(out->icon_url, channel.icon_url.c_str(), 511);
    out->icon_url[511] = '\0';

    out->provider_count = static_cast<int32_t>(channel.entries.size());

    // Best quality score is the first entry (sorted descending)
    out->best_quality_score = channel.entries.empty() ? 0 : channel.entries[0].quality_score;

    // Return the first entry's GUID as the channel GUID
    if (!channel.entries.empty()) {
        out->guid_high = channel.entries[0].guid_high;
        out->guid_low = channel.entries[0].guid_low;
    } else {
        out->guid_high = 0;
        out->guid_low = 0;
    }

    return true;
}

int32_t ChannelRegistry::get_all_urls(
    int64_t guid_high, int64_t guid_low,
    char* out_urls,
    int32_t* out_providers,
    int32_t* out_stream_ids,
    int32_t max_urls) const noexcept {

    if (!built_.load(std::memory_order_acquire) || !out_urls || max_urls <= 0) {
        return -1;
    }

    std::shared_lock lock(mutex_);

    GuidKey key{guid_high, guid_low};
    auto guid_it = guid_to_channel_.find(key);
    if (guid_it == guid_to_channel_.end()) {
        return 0;
    }

    auto channel_it = channels_.find(guid_it->second);
    if (channel_it == channels_.end()) {
        return 0;
    }

    const Channel& channel = channel_it->second;
    int32_t count = std::min(static_cast<int32_t>(channel.entries.size()), max_urls);

    for (int32_t i = 0; i < count; ++i) {
        const auto& entry = channel.entries[i];

        // Copy URL (each buffer is 1024 chars)
        char* url_buf = out_urls + (i * 1024);
        std::strncpy(url_buf, entry.url.c_str(), 1023);
        url_buf[1023] = '\0';

        if (out_providers) {
            out_providers[i] = entry.provider_index;
        }
        if (out_stream_ids) {
            out_stream_ids[i] = entry.stream_id;
        }
    }

    return count;
}

bool ChannelRegistry::add_guid_alias(int64_t alias_high, int64_t alias_low,
                                      int32_t provider_index, int32_t stream_id) noexcept {
    std::unique_lock lock(mutex_);

    if (!built_.load(std::memory_order_relaxed)) {
        return false;
    }

    // O(1) lookup via reverse index built during build()/rebuild()
    auto it = stream_to_channel_.find(make_stream_key(provider_index, stream_id));
    if (it != stream_to_channel_.end()) {
        guid_to_channel_[GuidKey{alias_high, alias_low}] = it->second;
        return true;
    }

    return false;
}

int64_t ChannelRegistry::make_stream_key(int32_t provider_index, int32_t stream_id) noexcept {
    return (static_cast<int64_t>(provider_index) << 32) |
           static_cast<int64_t>(static_cast<uint32_t>(stream_id));
}

void ChannelRegistry::build_reverse_index() {
    stream_to_channel_.clear();
    for (const auto& [name, channel] : channels_) {
        for (const auto& entry : channel.entries) {
            stream_to_channel_[make_stream_key(entry.provider_index, entry.stream_id)] = name;
        }
    }
}

// ============================================================================
// Channel Enumeration
// ============================================================================

int32_t ChannelRegistry::get_channel_count() const noexcept {
    std::shared_lock lock(mutex_);
    return static_cast<int32_t>(channels_.size());
}

int32_t ChannelRegistry::enumerate_channels(
    ChannelListEntryNative* out,
    int32_t max_count,
    int32_t offset) const noexcept {

    if (!built_.load(std::memory_order_acquire) || !out || max_count <= 0) {
        return 0;
    }

    std::shared_lock lock(mutex_);

    int32_t total = static_cast<int32_t>(channel_keys_.size());
    if (offset >= total || offset < 0) {
        return 0;
    }

    int32_t available = total - offset;
    int32_t count = std::min(available, max_count);

    for (int32_t i = 0; i < count; ++i) {
        const auto& key = channel_keys_[offset + i];
        auto it = channels_.find(key);
        if (it == channels_.end()) {
            continue;  // Shouldn't happen
        }

        const Channel& channel = it->second;
        auto& entry = out[i];

        // Use the first stream entry's GUID as the channel GUID
        if (!channel.entries.empty()) {
            entry.guid_high = channel.entries[0].guid_high;
            entry.guid_low = channel.entries[0].guid_low;
        } else {
            entry.guid_high = 0;
            entry.guid_low = 0;
        }

        std::strncpy(entry.display_name, channel.display_name.c_str(), 127);
        entry.display_name[127] = '\0';

        std::strncpy(entry.icon_url, channel.icon_url.c_str(), 511);
        entry.icon_url[511] = '\0';

        entry.provider_count = static_cast<int32_t>(channel.entries.size());
        entry.best_quality_score = channel.entries.empty()
            ? 0
            : channel.entries[0].quality_score;
    }

    return count;
}

// ============================================================================
// Provider Status Query
// ============================================================================

int32_t ChannelRegistry::get_provider_status(
    ProviderStatusNative* out,
    int32_t max_count) const noexcept {

    if (!built_.load(std::memory_order_acquire) || !out || max_count <= 0) {
        return 0;
    }

    std::shared_lock lock(mutex_);

    int32_t count = std::min(static_cast<int32_t>(providers_.size()), max_count);

    for (int32_t i = 0; i < count; ++i) {
        auto& status = out[i];
        const Provider& provider = providers_[i];

        std::strncpy(status.id, provider.id.c_str(), 15);
        status.id[15] = '\0';

        std::strncpy(status.name, provider.name.c_str(), 63);
        status.name[63] = '\0';

        status.channel_count = (i < static_cast<int32_t>(provider_channel_counts_.size()))
            ? provider_channel_counts_[i]
            : 0;

        // Fill from health manager if available
        if (health_manager_) {
            auto snapshot = health_manager_->get_snapshot(i);
            status.state = static_cast<int32_t>(snapshot.state);
            status.success_rate = snapshot.success_rate;
            status.health_score = snapshot.success_rate * 100.0;
            status.latency_ewma_ms = snapshot.latency_ewma_ms;
            status.consecutive_failures = snapshot.isolated_times;

            // Circuit breaker state
            if (snapshot.state == streaming::ProviderState::Ejected) {
                status.circuit_breaker_state = 1;  // Open
            } else if (snapshot.state == streaming::ProviderState::Probation) {
                status.circuit_breaker_state = 2;  // Half-open
            } else {
                status.circuit_breaker_state = 0;  // Closed
            }

            // Quarantine time (convert ejection to .NET ticks)
            if (snapshot.state == streaming::ProviderState::Ejected) {
                status.quarantine_until = get_current_ticks() +
                    (static_cast<int64_t>(snapshot.isolation_duration_ms) * 10000);
            } else {
                status.quarantine_until = 0;
            }
        } else {
            status.state = 0;
            status.success_rate = 1.0;
            status.health_score = 100.0;
            status.latency_ewma_ms = 0.0;
            status.consecutive_failures = 0;
            status.circuit_breaker_state = 0;
            status.quarantine_until = 0;
        }

        status.reserved = 0;
        status.reserved2 = 0;
    }

    return count;
}

// ============================================================================
// Health Tracking (delegated to UnifiedProviderHealthManager)
// ============================================================================

void ChannelRegistry::record_success(int32_t provider_index, int64_t bytes_received) noexcept {
    if (!health_manager_) {
        return;
    }

    if (provider_index < 0 || provider_index >= health_manager_->provider_count()) {
        return;
    }

    // Estimate latency from bytes (rough approximation)
    double latency_ms = 1.0;  // Default 1ms for streaming data
    health_manager_->on_success(provider_index, latency_ms);
}

void ChannelRegistry::record_failure(int32_t provider_index, DisconnectReason reason) noexcept {
    if (!health_manager_) {
        return;
    }

    if (provider_index < 0 || provider_index >= health_manager_->provider_count()) {
        return;
    }

    // Track previous state for health events
    auto prev_state = health_manager_->get_state(provider_index);

    // Map disconnect reason to latency penalty
    double latency_ms = 0.0;
    if (reason == DisconnectReason::Timeout) {
        latency_ms = 30000.0;  // High penalty for timeout
    } else if (reason == DisconnectReason::ConnectionFailed) {
        latency_ms = 10000.0;
    }

    health_manager_->on_failure(provider_index, latency_ms);

    // Fire health event if state changed
    auto new_state = health_manager_->get_state(provider_index);
    if (new_state != prev_state && new_state == streaming::ProviderState::Ejected) {
        const char* reason_str = "unknown";
        switch (reason) {
            case DisconnectReason::Timeout: reason_str = "timeout"; break;
            case DisconnectReason::ConnectionFailed: reason_str = "connection_failed"; break;
            case DisconnectReason::HttpError: reason_str = "http_error"; break;
            default: break;
        }
        fire_health_event(provider_index, "ejected", reason_str);
    }
}

void ChannelRegistry::report_api_success(int32_t provider_index, int32_t latency_ms) noexcept {
    if (!health_manager_) {
        return;
    }

    if (provider_index < 0 || provider_index >= health_manager_->provider_count()) {
        return;
    }

    health_manager_->on_success(provider_index, static_cast<double>(latency_ms));
    health_manager_->on_dns_success(provider_index);
}

void ChannelRegistry::report_api_failure(int32_t provider_index, ApiErrorType error_type) noexcept {
    if (!health_manager_) {
        return;
    }

    if (provider_index < 0 || provider_index >= health_manager_->provider_count()) {
        return;
    }

    auto prev_state = health_manager_->get_state(provider_index);

    switch (error_type) {
        case ApiErrorType::DnsFailure: {
            auto policy = health_manager_->on_dns_failure(provider_index);
            if (policy == streaming::DnsFailurePolicy::EjectAndSwitch) {
                fire_health_event(provider_index, "ejected", "dns_failure_persistent");
            }
            break;
        }
        case ApiErrorType::Timeout:
            health_manager_->on_failure(provider_index, 30000.0);
            break;
        case ApiErrorType::HttpError:
            health_manager_->on_failure(provider_index, 5000.0);
            break;
        case ApiErrorType::AuthFailure:
            // Auth failures are severe - eject for 5 minutes
            health_manager_->force_eject(provider_index, 300000);
            fire_health_event(provider_index, "ejected", "auth_failure");
            break;
    }

    // Fire health event if state changed to ejected
    auto new_state = health_manager_->get_state(provider_index);
    if (new_state != prev_state && new_state == streaming::ProviderState::Ejected
        && error_type != ApiErrorType::DnsFailure  // Already handled above
        && error_type != ApiErrorType::AuthFailure) {
        const char* type_str = (error_type == ApiErrorType::Timeout) ? "timeout" : "http_error";
        fire_health_event(provider_index, "ejected", type_str);
    }
}

double ChannelRegistry::get_provider_health(int32_t provider_index) const noexcept {
    if (!health_manager_) {
        return 50.0;
    }

    if (provider_index < 0 || provider_index >= health_manager_->provider_count()) {
        return 0.0;
    }

    auto snapshot = health_manager_->get_snapshot(provider_index);
    return snapshot.success_rate * 100.0;
}

bool ChannelRegistry::is_provider_quarantined(int32_t provider_index) const noexcept {
    if (!health_manager_) {
        return false;
    }

    return health_manager_->is_ejected(provider_index);
}

void ChannelRegistry::reset_all_quarantines() noexcept {
    if (health_manager_) {
        health_manager_->reset_all();
    }
}

// ============================================================================
// Health Event Callback
// ============================================================================

void ChannelRegistry::set_health_callback(HealthEventCallback cb) noexcept {
    std::lock_guard lock(callback_mutex_);
    health_callback_ = std::move(cb);
}

void ChannelRegistry::fire_health_event(int32_t provider_index,
                                         const char* event_type,
                                         const char* details) noexcept {
    std::lock_guard lock(callback_mutex_);
    if (health_callback_) {
        try {
            health_callback_(provider_index, event_type, details);
        } catch (...) {
            // Never let callback exceptions escape
        }
    }
}

}  // namespace tsduck_interop::registry
