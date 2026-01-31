// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#include "channel_registry.hpp"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstring>
#include <iomanip>
#include <sstream>

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
    if (built_) {
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

    // Initialize health tracking
    auto health = std::make_unique<ProviderHealth>();
    health->score.store(info.initial_health, std::memory_order_relaxed);
    provider_health_.push_back(std::move(health));

    return index;
}

int32_t ChannelRegistry::add_stream(int32_t provider_index, int32_t stream_id,
                                     const char* name, const char* icon_url) {
    if (built_) {
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
    if (built_) {
        return false;  // Already built
    }

    // Process all pending streams
    process_pending_streams();

    // Sort entries within each channel by quality
    sort_channel_entries();

    // Generate GUIDs for all entries
    generate_guids();

    // Clear pending data
    pending_streams_.clear();
    pending_streams_.shrink_to_fit();

    built_ = true;
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
    for (auto& [name, channel] : channels_) {
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

std::string ChannelRegistry::build_url(const Provider& provider, int32_t stream_id) const {
    std::ostringstream oss;
    oss << provider.base_url;

    // Remove trailing slash if present
    std::string base = provider.base_url;
    if (!base.empty() && base.back() == '/') {
        base.pop_back();
    }

    // Build Xtream API URL: {base}/live/{username}/{password}/{stream_id}.ts
    oss.str("");
    oss << base << "/live/" << provider.username << "/" << provider.password
        << "/" << stream_id << ".ts";

    return oss.str();
}

bool ChannelRegistry::get_stats(RegistryStatsNative* out) const noexcept {
    if (!out) {
        return false;
    }

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

    if (!built_ || !out_url) {
        return -1;
    }

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

    // Find best entry considering health
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

    const StreamEntry* best = nullptr;
    double best_score = -1.0;

    for (const auto& entry : channel.entries) {
        // Skip excluded providers
        if (excluded.count(entry.provider_index) > 0) {
            continue;
        }

        // Skip quarantined providers
        if (is_provider_quarantined(entry.provider_index)) {
            continue;
        }

        // Combine quality score with health score
        // Quality: 0-110, Health: 0-100
        double health = get_provider_health(entry.provider_index);
        double combined = (entry.quality_score * 0.6) + (health * 0.4);

        if (combined > best_score) {
            best_score = combined;
            best = &entry;
        }
    }

    // If all providers are quarantined/excluded, try any non-excluded
    if (!best) {
        for (const auto& entry : channel.entries) {
            if (excluded.count(entry.provider_index) == 0) {
                best = &entry;
                break;
            }
        }
    }

    return best;
}

bool ChannelRegistry::get_channel_info(int64_t guid_high, int64_t guid_low,
                                        ChannelInfo* out) const noexcept {
    if (!built_ || !out) {
        return false;
    }

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

    return true;
}

int32_t ChannelRegistry::get_all_urls(
    int64_t guid_high, int64_t guid_low,
    char* out_urls,
    int32_t* out_providers,
    int32_t* out_stream_ids,
    int32_t max_urls) const noexcept {

    if (!built_ || !out_urls || max_urls <= 0) {
        return -1;
    }

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

// ============================================================================
// Health Tracking
// ============================================================================

void ChannelRegistry::record_success(int32_t provider_index, int64_t bytes_received) noexcept {
    if (provider_index < 0 || provider_index >= static_cast<int32_t>(provider_health_.size())) {
        return;
    }

    auto& health = *provider_health_[provider_index];

    // Clear quarantine on success
    health.quarantine_until_ticks.store(0, std::memory_order_relaxed);
    health.consecutive_failures.store(0, std::memory_order_relaxed);

    // Boost health score
    double current = health.score.load(std::memory_order_relaxed);
    double new_score = std::min(100.0, current + config_.score_boost_on_success);
    health.score.store(new_score, std::memory_order_relaxed);

    // Update stats
    health.total_bytes.fetch_add(bytes_received, std::memory_order_relaxed);
    health.total_successes.fetch_add(1, std::memory_order_relaxed);
}

void ChannelRegistry::record_failure(int32_t provider_index, DisconnectReason reason) noexcept {
    if (provider_index < 0 || provider_index >= static_cast<int32_t>(provider_health_.size())) {
        return;
    }

    auto& health = *provider_health_[provider_index];

    // Increment consecutive failures
    int32_t failures = health.consecutive_failures.fetch_add(1, std::memory_order_relaxed) + 1;

    // Calculate penalty (higher for timeout)
    double penalty = config_.score_penalty_on_failure;
    if (reason == DisconnectReason::Timeout) {
        penalty = config_.score_penalty_on_timeout;
    }

    // Apply penalty
    double current = health.score.load(std::memory_order_relaxed);
    double new_score = std::max(0.0, current - penalty);
    health.score.store(new_score, std::memory_order_relaxed);

    // Calculate quarantine duration (exponential backoff)
    double multiplier = std::pow(config_.quarantine_multiplier, failures - 1);
    int32_t quarantine_ms = static_cast<int32_t>(
        std::min(static_cast<double>(config_.quarantine_max_ms),
                 config_.quarantine_base_ms * multiplier));

    // Set quarantine
    int64_t now = get_current_ticks();
    int64_t quarantine_ticks = now + (static_cast<int64_t>(quarantine_ms) * 10000);
    health.quarantine_until_ticks.store(quarantine_ticks, std::memory_order_relaxed);

    // Update stats
    health.total_failures.fetch_add(1, std::memory_order_relaxed);
}

double ChannelRegistry::get_provider_health(int32_t provider_index) const noexcept {
    if (provider_index < 0 || provider_index >= static_cast<int32_t>(provider_health_.size())) {
        return 0.0;
    }

    return provider_health_[provider_index]->score.load(std::memory_order_relaxed);
}

bool ChannelRegistry::is_provider_quarantined(int32_t provider_index) const noexcept {
    if (provider_index < 0 || provider_index >= static_cast<int32_t>(provider_health_.size())) {
        return true;  // Treat invalid index as quarantined
    }

    int64_t quarantine_until = provider_health_[provider_index]
        ->quarantine_until_ticks.load(std::memory_order_relaxed);

    if (quarantine_until == 0) {
        return false;
    }

    return get_current_ticks() < quarantine_until;
}

void ChannelRegistry::reset_all_quarantines() noexcept {
    for (auto& health : provider_health_) {
        health->quarantine_until_ticks.store(0, std::memory_order_relaxed);
        health->consecutive_failures.store(0, std::memory_order_relaxed);
    }
}

}  // namespace tsduck_interop::registry
