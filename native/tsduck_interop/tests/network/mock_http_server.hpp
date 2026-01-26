// Copyright (C) 2025  Gergo Magyar
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Mock HTTP server for network simulation testing.
// Provides configurable latency, packet loss, bandwidth limits, and error injection.

#ifndef TSDUCK_INTEROP_TESTS_MOCK_HTTP_SERVER_HPP
#define TSDUCK_INTEROP_TESTS_MOCK_HTTP_SERVER_HPP

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <functional>
#include <mutex>
#include <queue>
#include <random>
#include <string>
#include <thread>
#include <vector>

#ifdef _WIN32
#include <winsock2.h>
#include <ws2tcpip.h>
#pragma comment(lib, "ws2_32.lib")
using socket_t = SOCKET;
#define INVALID_SOCK INVALID_SOCKET
#define SOCK_ERROR SOCKET_ERROR
#define CLOSE_SOCKET closesocket
inline ssize_t send_bytes(socket_t s, const void* buf, size_t len, int flags) {
    return send(s, static_cast<const char*>(buf), static_cast<int>(len), flags);
}
inline ssize_t recv_bytes(socket_t s, void* buf, size_t len, int flags) {
    return recv(s, static_cast<char*>(buf), static_cast<int>(len), flags);
}
#else
#include <arpa/inet.h>
#include <netinet/in.h>
#include <sys/socket.h>
#include <unistd.h>
#include <signal.h>
#include <cstring>
using socket_t = int;
#define INVALID_SOCK (-1)
#define SOCK_ERROR (-1)
#define CLOSE_SOCKET close
inline ssize_t send_bytes(socket_t s, const void* buf, size_t len, int flags) {
    return send(s, buf, len, flags);
}
inline ssize_t recv_bytes(socket_t s, void* buf, size_t len, int flags) {
    return recv(s, buf, len, flags);
}
#endif

namespace tsduck_interop {
namespace testing {

// ============================================================================
// Network Simulation Configuration
// ============================================================================

struct NetworkSimConfig {
    // Latency simulation
    int initial_latency_ms{0};      // Latency before first byte
    int chunk_latency_ms{0};        // Latency between chunks
    int latency_jitter_ms{0};       // Random jitter (+/- ms)

    // Bandwidth limiting
    int bandwidth_kbps{0};          // 0 = unlimited

    // Packet loss simulation
    double packet_loss_rate{0.0};   // 0.0-1.0 probability

    // Error injection
    bool inject_connection_reset{false};
    int reset_after_bytes{0};       // Reset after N bytes sent

    bool inject_stall{false};
    int stall_after_bytes{0};       // Stall after N bytes
    int stall_duration_ms{0};       // How long to stall

    // HTTP response
    int http_status{200};           // HTTP status code
    std::string content_type{"video/MP2T"};

    // Content generation
    bool generate_ts_packets{true}; // Generate valid TS packets
    int total_bytes{0};             // 0 = infinite stream
};

// ============================================================================
// Mock HTTP Server
// ============================================================================

class MockHttpServer {
public:
    MockHttpServer() : running_(false), port_(0), server_socket_(INVALID_SOCK) {
#ifdef _WIN32
        WSADATA wsa_data;
        WSAStartup(MAKEWORD(2, 2), &wsa_data);
#else
        // Ignore SIGPIPE to prevent process termination when writing to closed socket
        signal(SIGPIPE, SIG_IGN);
#endif
    }

    ~MockHttpServer() {
        stop();
#ifdef _WIN32
        WSACleanup();
#endif
    }

    // Start server on random available port
    bool start(const NetworkSimConfig& config = {}) {
        config_ = config;

        server_socket_ = socket(AF_INET, SOCK_STREAM, 0);
        if (server_socket_ == INVALID_SOCK) {
            return false;
        }

        // Allow address reuse
        int opt = 1;
#ifdef _WIN32
        setsockopt(server_socket_, SOL_SOCKET, SO_REUSEADDR, (const char*)&opt, sizeof(opt));
#else
        setsockopt(server_socket_, SOL_SOCKET, SO_REUSEADDR, &opt, sizeof(opt));
#endif

        struct sockaddr_in addr{};
        addr.sin_family = AF_INET;
        addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        addr.sin_port = 0;  // Random port

        if (bind(server_socket_, (struct sockaddr*)&addr, sizeof(addr)) == SOCK_ERROR) {
            CLOSE_SOCKET(server_socket_);
            server_socket_ = INVALID_SOCK;
            return false;
        }

        // Get assigned port
        socklen_t addr_len = sizeof(addr);
        if (getsockname(server_socket_, (struct sockaddr*)&addr, &addr_len) == SOCK_ERROR) {
            CLOSE_SOCKET(server_socket_);
            server_socket_ = INVALID_SOCK;
            return false;
        }
        port_ = ntohs(addr.sin_port);

        if (listen(server_socket_, 5) == SOCK_ERROR) {
            CLOSE_SOCKET(server_socket_);
            server_socket_ = INVALID_SOCK;
            return false;
        }

        running_.store(true, std::memory_order_release);
        accept_thread_ = std::thread(&MockHttpServer::acceptLoop, this);

        return true;
    }

    void stop() {
        running_.store(false, std::memory_order_release);

        if (server_socket_ != INVALID_SOCK) {
            CLOSE_SOCKET(server_socket_);
            server_socket_ = INVALID_SOCK;
        }

        if (accept_thread_.joinable()) {
            accept_thread_.join();
        }
    }

    int port() const { return port_; }

    std::string url() const {
        return "http://127.0.0.1:" + std::to_string(port_) + "/stream.ts";
    }

    // Statistics
    int64_t totalBytesServed() const {
        return bytes_served_.load(std::memory_order_relaxed);
    }

    int64_t connectionCount() const {
        return connection_count_.load(std::memory_order_relaxed);
    }

    void resetStats() {
        bytes_served_.store(0, std::memory_order_relaxed);
        connection_count_.store(0, std::memory_order_relaxed);
    }

    // Update config while running (for dynamic simulation)
    void updateConfig(const NetworkSimConfig& config) {
        std::lock_guard<std::mutex> lock(config_mutex_);
        config_ = config;
    }

private:
    void acceptLoop() {
        while (running_.load(std::memory_order_relaxed)) {
            struct sockaddr_in client_addr{};
            socklen_t client_len = sizeof(client_addr);

            // Set timeout on accept
#ifdef _WIN32
            DWORD timeout = 100;
            setsockopt(server_socket_, SOL_SOCKET, SO_RCVTIMEO, (const char*)&timeout, sizeof(timeout));
#else
            struct timeval tv{0, 100000};  // 100ms
            setsockopt(server_socket_, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv));
#endif

            socket_t client = accept(server_socket_, (struct sockaddr*)&client_addr, &client_len);
            if (client == INVALID_SOCK) {
                continue;  // Timeout or shutdown
            }

            connection_count_.fetch_add(1, std::memory_order_relaxed);

            // Handle client in new thread
            std::thread([this, client]() {
                handleClient(client);
            }).detach();
        }
    }

    void handleClient(socket_t client) {
        // Read HTTP request (simplified - just drain the request)
        char buffer[4096];
        recv_bytes(client, buffer, sizeof(buffer), 0);

        NetworkSimConfig cfg;
        {
            std::lock_guard<std::mutex> lock(config_mutex_);
            cfg = config_;
        }

        // Apply initial latency
        if (cfg.initial_latency_ms > 0) {
            std::this_thread::sleep_for(std::chrono::milliseconds(cfg.initial_latency_ms));
        }

        // Send HTTP response header
        std::string response = "HTTP/1.1 " + std::to_string(cfg.http_status) + " OK\r\n"
                               "Content-Type: " + cfg.content_type + "\r\n"
                               "Transfer-Encoding: chunked\r\n"
                               "Connection: close\r\n"
                               "\r\n";
        send_bytes(client, response.c_str(), response.size(), 0);

        // Generate and send content
        std::mt19937 rng(std::random_device{}());
        std::uniform_int_distribution<int> jitter_dist(-cfg.latency_jitter_ms, cfg.latency_jitter_ms);
        std::uniform_real_distribution<double> loss_dist(0.0, 1.0);

        int64_t bytes_sent = 0;
        constexpr int CHUNK_SIZE = 188 * 7;  // 7 TS packets per chunk
        std::vector<uint8_t> chunk_data(CHUNK_SIZE);

        while (running_.load(std::memory_order_relaxed)) {
            // Check if we've sent enough
            if (cfg.total_bytes > 0 && bytes_sent >= cfg.total_bytes) {
                break;
            }

            // Check for connection reset injection
            if (cfg.inject_connection_reset && bytes_sent >= cfg.reset_after_bytes) {
                break;  // Abrupt close
            }

            // Check for stall injection
            if (cfg.inject_stall && bytes_sent >= cfg.stall_after_bytes && cfg.stall_duration_ms > 0) {
                std::this_thread::sleep_for(std::chrono::milliseconds(cfg.stall_duration_ms));
                cfg.inject_stall = false;  // Only stall once
            }

            // Generate chunk
            int chunk_len = CHUNK_SIZE;
            if (cfg.total_bytes > 0) {
                chunk_len = std::min(chunk_len, static_cast<int>(cfg.total_bytes - bytes_sent));
            }

            if (cfg.generate_ts_packets) {
                generateTsPackets(chunk_data.data(), chunk_len, rng);
            } else {
                std::fill(chunk_data.begin(), chunk_data.begin() + chunk_len, 0xFF);
            }

            // Simulate packet loss
            if (cfg.packet_loss_rate > 0.0 && loss_dist(rng) < cfg.packet_loss_rate) {
                continue;  // Drop this chunk
            }

            // Apply bandwidth limiting
            if (cfg.bandwidth_kbps > 0) {
                int delay_ms = (chunk_len * 8) / cfg.bandwidth_kbps;
                if (delay_ms > 0) {
                    std::this_thread::sleep_for(std::chrono::milliseconds(delay_ms));
                }
            }

            // Apply chunk latency with jitter
            if (cfg.chunk_latency_ms > 0 || cfg.latency_jitter_ms > 0) {
                int delay = cfg.chunk_latency_ms;
                if (cfg.latency_jitter_ms > 0) {
                    delay += jitter_dist(rng);
                }
                if (delay > 0) {
                    std::this_thread::sleep_for(std::chrono::milliseconds(delay));
                }
            }

            // Send HTTP chunked encoding
            char chunk_header[32];
            snprintf(chunk_header, sizeof(chunk_header), "%X\r\n", chunk_len);
            if (send_bytes(client, chunk_header, strlen(chunk_header), 0) <= 0) {
                break;
            }
            if (send_bytes(client, chunk_data.data(), static_cast<size_t>(chunk_len), 0) <= 0) {
                break;
            }
            if (send_bytes(client, "\r\n", 2, 0) <= 0) {
                break;
            }

            bytes_sent += chunk_len;
            bytes_served_.fetch_add(chunk_len, std::memory_order_relaxed);
        }

        // Send final chunk
        send_bytes(client, "0\r\n\r\n", 5, 0);

        CLOSE_SOCKET(client);
    }

    void generateTsPackets(uint8_t* data, int length, std::mt19937& rng) {
        std::uniform_int_distribution<int> pid_dist(0x100, 0x1FF);
        int offset = 0;
        uint8_t cc = 0;

        while (offset + 188 <= length) {
            // Sync byte
            data[offset] = 0x47;

            // PID
            int pid = pid_dist(rng);
            data[offset + 1] = static_cast<uint8_t>((pid >> 8) & 0x1F);
            data[offset + 2] = static_cast<uint8_t>(pid & 0xFF);

            // Flags + CC
            data[offset + 3] = static_cast<uint8_t>(0x10 | (cc++ & 0x0F));

            // Payload (fill with pattern)
            for (int i = 4; i < 188; i++) {
                data[offset + i] = static_cast<uint8_t>(offset + i);
            }

            offset += 188;
        }

        // Fill remaining bytes with null packets
        while (offset < length) {
            data[offset++] = 0xFF;
        }
    }

    std::atomic<bool> running_;
    int port_;
    socket_t server_socket_;
    std::thread accept_thread_;
    NetworkSimConfig config_;
    std::mutex config_mutex_;

    std::atomic<int64_t> bytes_served_{0};
    std::atomic<int64_t> connection_count_{0};
};

}  // namespace testing
}  // namespace tsduck_interop

#endif  // TSDUCK_INTEROP_TESTS_MOCK_HTTP_SERVER_HPP
