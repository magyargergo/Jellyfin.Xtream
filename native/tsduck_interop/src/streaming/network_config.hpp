// Copyright (C) 2025 Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later

#ifndef TSDUCK_INTEROP_STREAMING_NETWORK_CONFIG_HPP
#define TSDUCK_INTEROP_STREAMING_NETWORK_CONFIG_HPP

#include <cstdint>
#include <cstring>
#include <type_traits>

namespace tsduck_interop::streaming {

constexpr int32_t MAX_DNS_SERVERS = 4;
constexpr int32_t DNS_SERVER_MAX_LEN = 256;
constexpr int32_t DOH_URL_MAX_LEN = 512;

/// IP version resolve preference
enum class IpResolveMode : int32_t {
    Whatever = 0,
    IPv4Only = 1,
    IPv6Only = 2,
    PreferIPv4 = 3,
    PreferIPv6 = 4
};

/// DNS resolution method
enum class DnsResolveMode : int32_t {
    System = 0,
    CustomDns = 1,
    DnsOverHttps = 2
};

/// Network configuration for streaming (blittable for C# interop)
struct NetworkConfig {
    // DNS Configuration
    int32_t dns_mode = static_cast<int32_t>(DnsResolveMode::System);
    int32_t dns_server_count = 0;
    char dns_servers[MAX_DNS_SERVERS][DNS_SERVER_MAX_LEN] = {};
    char doh_url[DOH_URL_MAX_LEN] = {};
    int32_t dns_cache_timeout_sec = 60;

    // IP Version
    int32_t ip_resolve_mode = static_cast<int32_t>(IpResolveMode::IPv4Only);

    // Timeouts
    int32_t dns_timeout_ms = 5000;
    int32_t tcp_connect_timeout_ms = 5000;
    int32_t tls_handshake_timeout_ms = 5000;
    int32_t first_byte_timeout_ms = 10000;
    int32_t happy_eyeballs_timeout_ms = 200;

    // TCP Keep-Alive
    int32_t tcp_keepalive_enabled = 1;
    int32_t tcp_keepalive_idle_sec = 60;
    int32_t tcp_keepalive_interval_sec = 60;

    // Buffer
    int32_t recv_buffer_size = 65536;

    // Reserved for future
    int32_t reserved[4] = {};

    // Helper methods
    bool add_dns_server(const char* server) noexcept {
        if (server == nullptr || dns_server_count >= MAX_DNS_SERVERS) return false;
        std::strncpy(dns_servers[dns_server_count], server, DNS_SERVER_MAX_LEN - 1);
        dns_servers[dns_server_count][DNS_SERVER_MAX_LEN - 1] = '\0';
        dns_server_count++;
        return true;
    }

    void set_doh_url(const char* url) noexcept {
        if (url == nullptr) { doh_url[0] = '\0'; return; }
        std::strncpy(doh_url, url, DOH_URL_MAX_LEN - 1);
        doh_url[DOH_URL_MAX_LEN - 1] = '\0';
    }

    void clear_dns_servers() noexcept {
        dns_server_count = 0;
        for (int i = 0; i < MAX_DNS_SERVERS; i++) dns_servers[i][0] = '\0';
    }
};

static_assert(std::is_trivially_copyable_v<NetworkConfig>, "NetworkConfig must be trivially copyable for FFI");

} // namespace tsduck_interop::streaming

#endif
