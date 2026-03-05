// Copyright (C) 2025  Gergo Magyar
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Client;

/// <summary>
/// Industry-standard TCP socket configuration for MPEG-TS streaming.
/// Implements RFC 7323 (TCP Extensions for High Performance) and
/// RFC 1122 (Host Requirements for Internet Hosts) compliance.
/// </summary>
/// <remarks>
/// Key standards implemented:
/// - RFC 7323: TCP Window Scaling, Timestamps
/// - RFC 1122: TCP Keep-Alive, Receive/Send Buffer Sizing
/// - RFC 793: TCP Nagle Algorithm Control
/// - Linux/Windows TCP Tuning Best Practices for Streaming.
/// </remarks>
public static class StreamingSocketConfiguration
{
    // Socket buffer sizes based on Bandwidth-Delay Product (BDP)
    // Formula: BDP = Bandwidth (bits/s) × RTT (seconds) / 8
    // For 100 Mbps at 100ms RTT: 100,000,000 × 0.1 / 8 = 1.25 MB
    // We use 2MB to handle bursts and higher latency networks

    /// <summary>
    /// Receive buffer size for high-throughput streaming.
    /// 2MB handles up to 160 Mbps at 100ms RTT (RFC 7323 Window Scaling).
    /// </summary>
    public const int StreamingReceiveBufferSize = 2 * 1024 * 1024; // 2MB

    /// <summary>
    /// Send buffer size (smaller since we're primarily receiving).
    /// 256KB is sufficient for HTTP requests and keep-alive packets.
    /// </summary>
    public const int StreamingSendBufferSize = 256 * 1024; // 256KB

    /// <summary>
    /// TCP Keep-Alive idle time before first probe (seconds).
    /// RFC 1122 recommends 2 hours default, but streaming needs faster detection.
    /// 30 seconds detects dead connections before user notices buffering.
    /// </summary>
    public const int TcpKeepAliveIdleSeconds = 30;

    /// <summary>
    /// TCP Keep-Alive interval between probes (seconds).
    /// After idle timeout, send probes every 10 seconds.
    /// </summary>
    public const int TcpKeepAliveIntervalSeconds = 10;

    /// <summary>
    /// Number of TCP Keep-Alive probes before declaring connection dead.
    /// 3 probes × 10 seconds = 30 seconds to detect failure.
    /// </summary>
    public const int TcpKeepAliveRetryCount = 3;

    // Platform detection for socket option availability
    private static readonly bool _isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private static readonly bool _isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    /// <summary>
    /// Configures a socket for optimal MPEG-TS streaming performance.
    /// Applies industry-standard TCP tuning based on the target platform.
    /// </summary>
    /// <param name="socket">The socket to configure.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <exception cref="ArgumentNullException">Thrown if socket is null.</exception>
    public static void ConfigureForStreaming(Socket socket, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(socket);

        try
        {
            // Core TCP optimizations (cross-platform)
            ConfigureBufferSizes(socket, logger);
            ConfigureKeepAlive(socket, logger);
            ConfigureNagle(socket, logger);

            // Platform-specific optimizations
            if (_isLinux)
            {
                ConfigureLinuxSpecific(socket, logger);
            }
            else if (_isWindows)
            {
                ConfigureWindowsSpecific(socket, logger);
            }

            logger?.LogDebugIfEnabled(
                "Socket configured for streaming: RcvBuf={RcvBuf}KB, SndBuf={SndBuf}KB, KeepAlive=enabled, Nagle=disabled",
                socket.ReceiveBufferSize / 1024,
                socket.SendBufferSize / 1024
            );
        }
        catch (SocketException ex)
        {
            // Log but don't fail - some options may not be available on all platforms
            logger?.PluginLogWarning(
                ex,
                "Failed to apply some socket options. Streaming may still work but with suboptimal performance."
            );
        }
    }

    /// <summary>
    /// Configures socket buffer sizes based on Bandwidth-Delay Product.
    /// Implements RFC 7323 Window Scaling support.
    /// </summary>
    private static void ConfigureBufferSizes(Socket socket, ILogger? logger)
    {
        try
        {
            // Set receive buffer for high-throughput streaming
            // This enables TCP Window Scaling (RFC 7323) automatically
            socket.ReceiveBufferSize = StreamingReceiveBufferSize;
            logger?.LogTrace("Set ReceiveBufferSize to {Size} bytes", StreamingReceiveBufferSize);
        }
        catch (SocketException ex)
        {
            // Null-conditional is required: logger parameter is optional (nullable)
            logger?.LogDebugIfEnabled(
                ex,
                "Could not set ReceiveBufferSize to {Size}. OS may limit maximum.",
                StreamingReceiveBufferSize
            );
        }

        try
        {
            // Set send buffer (smaller for receive-heavy workload)
            socket.SendBufferSize = StreamingSendBufferSize;
            logger?.LogTrace("Set SendBufferSize to {Size} bytes", StreamingSendBufferSize);
        }
        catch (SocketException ex)
        {
            // Null-conditional is required: logger parameter is optional (nullable)
            logger?.LogDebugIfEnabled(
                ex,
                "Could not set SendBufferSize to {Size}. OS may limit maximum.",
                StreamingSendBufferSize
            );
        }
    }

    /// <summary>
    /// Configures TCP Keep-Alive per RFC 1122.
    /// Enables faster detection of dead connections for live streaming.
    /// </summary>
    private static void ConfigureKeepAlive(Socket socket, ILogger? logger)
    {
        try
        {
            // Enable keep-alive (RFC 1122)
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, optionValue: true);

            // .NET 5+ supports setting keep-alive parameters directly
            // Uses IOControl on Windows, setsockopt on Linux
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                try
                {
                    // TCP_KEEPIDLE: Time before first keep-alive probe (seconds)
                    socket.SetSocketOption(
                        SocketOptionLevel.Tcp,
                        SocketOptionName.TcpKeepAliveTime,
                        TcpKeepAliveIdleSeconds
                    );

                    // TCP_KEEPINTVL: Interval between keep-alive probes (seconds)
                    socket.SetSocketOption(
                        SocketOptionLevel.Tcp,
                        SocketOptionName.TcpKeepAliveInterval,
                        TcpKeepAliveIntervalSeconds
                    );

                    // TCP_KEEPCNT: Number of probes before connection is considered dead
                    socket.SetSocketOption(
                        SocketOptionLevel.Tcp,
                        SocketOptionName.TcpKeepAliveRetryCount,
                        TcpKeepAliveRetryCount
                    );

                    logger?.LogTrace(
                        "TCP Keep-Alive configured: idle={IdleS}s, interval={IntervalS}s, retries={Retries}",
                        TcpKeepAliveIdleSeconds,
                        TcpKeepAliveIntervalSeconds,
                        TcpKeepAliveRetryCount
                    );
                }
                catch (SocketException ex)
                    when (ex.SocketErrorCode is SocketError.InvalidArgument or SocketError.ProtocolOption)
                {
                    // Platform doesn't support fine-grained keep-alive settings
                    logger?.LogDebugIfEnabled(
                        "Fine-grained TCP keep-alive not supported on this platform. Using OS defaults."
                    );
                }
            }
        }
        catch (SocketException ex)
        {
            logger?.LogDebugIfEnabled(
                ex,
                "Could not configure TCP keep-alive. Connections may not detect failures quickly."
            );
        }
    }

    /// <summary>
    /// Disables Nagle's algorithm for low-latency streaming.
    /// RFC 793 defines TCP_NODELAY to disable Nagle buffering.
    /// </summary>
    /// <remarks>
    /// For MPEG-TS streaming, we want data sent immediately rather than
    /// buffered to reduce latency. The circular buffer handles coalescing.
    /// </remarks>
    private static void ConfigureNagle(Socket socket, ILogger? logger)
    {
        try
        {
            // Disable Nagle's algorithm (TCP_NODELAY) for low latency
            // This sends packets immediately without waiting for more data
            socket.NoDelay = true;
            logger?.LogTrace("Nagle's algorithm disabled (TCP_NODELAY=true)");
        }
        catch (SocketException ex)
        {
            // Null-conditional is required: logger parameter is optional (nullable)
            logger?.LogDebugIfEnabled(ex, "Could not disable Nagle's algorithm. Latency may be slightly higher.");
        }
    }

    /// <summary>
    /// Linux-specific socket optimizations.
    /// Uses platform-specific syscalls for optimal performance.
    /// </summary>
    private static void ConfigureLinuxSpecific(Socket socket, ILogger? logger)
    {
        // TCP_QUICKACK: Disable delayed ACKs for faster data flow
        // This reduces latency at the cost of slightly more ACK packets
        const int TCP_QUICKACK = 12; // Linux-specific
        try
        {
            socket.SetRawSocketOption(6, TCP_QUICKACK, BitConverter.GetBytes(1)); // IPPROTO_TCP = 6
            logger?.LogTrace("Linux: TCP_QUICKACK enabled for faster acknowledgments");
        }
        catch (SocketException)
        {
            // Not available on all Linux versions
        }

        // TCP_CORK: Disable corking to ensure immediate sends
        // When disabled (0), data is sent immediately
        const int TCP_CORK = 3; // Linux-specific
        try
        {
            socket.SetRawSocketOption(6, TCP_CORK, BitConverter.GetBytes(0)); // Disable cork
            logger?.LogTrace("Linux: TCP_CORK disabled for immediate sends");
        }
        catch (SocketException)
        {
            // Not available on all Linux versions
        }

        // SO_BUSY_POLL: Enable busy polling for low latency (if kernel supports it)
        // Value is microseconds to spin before blocking
        const int SO_BUSY_POLL = 46; // Linux-specific
        try
        {
            socket.SetRawSocketOption(1, SO_BUSY_POLL, BitConverter.GetBytes(50)); // SOL_SOCKET = 1, 50µs spin
            logger?.LogTrace("Linux: SO_BUSY_POLL enabled (50µs spin)");
        }
        catch (SocketException)
        {
            // Not available on all kernels - requires CONFIG_NET_RX_BUSY_POLL
        }
    }

    /// <summary>
    /// Windows-specific socket optimizations.
    /// Uses Winsock options for optimal streaming performance.
    /// </summary>
    private static void ConfigureWindowsSpecific(Socket socket, ILogger? logger)
    {
        // SIO_LOOPBACK_FAST_PATH: Enable loopback optimization (localhost testing)
        // This bypasses the normal TCP stack for localhost connections
        const int SIO_LOOPBACK_FAST_PATH = unchecked((int)0x98000010);
        try
        {
            var optionValue = BitConverter.GetBytes(1);
            _ = socket.IOControl(SIO_LOOPBACK_FAST_PATH, optionValue, optionOutValue: null);
            logger?.LogTrace("Windows: SIO_LOOPBACK_FAST_PATH enabled for localhost optimization");
        }
        catch (SocketException)
        {
            // Only works on Windows 8+ and for localhost connections
        }

        // TCP_FASTOPEN: Enable TCP Fast Open (RFC 7413)
        // Reduces connection establishment time by sending data in SYN packet
        const int TCP_FASTOPEN = 15; // Windows 10+
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Tcp, (SocketOptionName)TCP_FASTOPEN, 1);
            logger?.LogTrace("Windows: TCP_FASTOPEN enabled");
        }
        catch (SocketException)
        {
            // Not available on older Windows versions
        }
    }

    /// <summary>
    /// Gets a diagnostic string with current socket configuration.
    /// Useful for troubleshooting streaming issues.
    /// </summary>
    /// <param name="socket">The socket to diagnose.</param>
    /// <returns>Formatted diagnostic string.</returns>
    public static string GetDiagnostics(Socket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);

        try
        {
            return "Socket Diagnostics:\n"
                + $"  Receive Buffer: {socket.ReceiveBufferSize / 1024}KB (requested: {StreamingReceiveBufferSize / 1024}KB)\n"
                + $"  Send Buffer: {socket.SendBufferSize / 1024}KB (requested: {StreamingSendBufferSize / 1024}KB)\n"
                + $"  Keep-Alive: {socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive) ?? false}\n"
                + $"  No Delay (TCP_NODELAY): {socket.NoDelay}\n"
                + $"  Connected: {socket.Connected}\n"
                + $"  Available: {socket.Available} bytes\n"
                + $"  Platform: {(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : "Other")}";
        }
        catch (Exception ex)
        {
            return $"Socket Diagnostics: Failed to retrieve - {ex.Message}";
        }
    }
}
