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
using System.Runtime.InteropServices;
using System.Text;

namespace Jellyfin.Xtream.Service.Streaming.Native;

// =============================================================================
// Network Configuration Enums
// =============================================================================

/// <summary>
/// IP address resolution mode for network connections.
/// Must match IpResolveMode enum in network_config.hpp exactly.
/// </summary>
public enum IpResolveMode
{
    /// <summary>Use whatever the system decides (default).</summary>
    Whatever = 0,

    /// <summary>Force IPv4 only resolution.</summary>
    IPv4Only = 1,

    /// <summary>Force IPv6 only resolution.</summary>
    IPv6Only = 2,

    /// <summary>Prefer IPv4 but fall back to IPv6 if unavailable.</summary>
    PreferIPv4 = 3,

    /// <summary>Prefer IPv6 but fall back to IPv4 if unavailable.</summary>
    PreferIPv6 = 4,
}

/// <summary>
/// DNS resolution mode for network connections.
/// Must match DnsResolveMode enum in network_config.hpp exactly.
/// </summary>
public enum DnsResolveMode
{
    /// <summary>Use system DNS resolver (default).</summary>
    System = 0,

    /// <summary>Use custom DNS servers via libcurl's DOH-style resolution.</summary>
    CustomDns = 1,

    /// <summary>Use DNS-over-HTTPS for encrypted DNS queries.</summary>
    DnsOverHttps = 2,
}

/// <summary>
/// DNS error types returned by the native streamer.
/// Must match DnsErrorType enum in network_config.hpp exactly.
/// </summary>
public enum DnsErrorType
{
    /// <summary>No DNS error.</summary>
    None = 0,

    /// <summary>DNS resolution failed (NXDOMAIN or no records).</summary>
    ResolutionFailed = 1,

    /// <summary>DNS query timed out.</summary>
    Timeout = 2,

    /// <summary>DNS server unreachable.</summary>
    ServerUnreachable = 3,

    /// <summary>Invalid DNS server address.</summary>
    InvalidServer = 4,

    /// <summary>DNS-over-HTTPS specific error.</summary>
    DoHError = 5,
}

// =============================================================================
// Native Network Configuration Structure
// =============================================================================

/// <summary>
/// Native network configuration structure for P/Invoke.
/// Layout must match NetworkConfigNative in network_config.hpp exactly.
/// </summary>
/// <remarks>
/// This struct uses fixed-size buffers for DNS servers and DoH URL to ensure
/// blittable layout for efficient P/Invoke marshalling. The struct is designed
/// to be passed by reference to native code.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NetworkConfigNative
{
    /// <summary>Maximum number of DNS servers that can be configured.</summary>
    public const int MaxDnsServers = 4;

    /// <summary>Maximum length of each DNS server address string.</summary>
    public const int DnsServerMaxLen = 256;

    /// <summary>Maximum length of the DNS-over-HTTPS URL.</summary>
    public const int DohUrlMaxLen = 512;

    /// <summary>DNS resolution mode (DnsResolveMode enum value).</summary>
    public int DnsMode;

    /// <summary>Number of configured DNS servers (0-4).</summary>
    public int DnsServerCount;

    /// <summary>Fixed buffer for DNS server addresses (4 x 256 = 1024 bytes).</summary>
    private fixed byte _dnsServers[MaxDnsServers * DnsServerMaxLen];

    /// <summary>Fixed buffer for DNS-over-HTTPS URL (512 bytes).</summary>
    private fixed byte _dohUrl[DohUrlMaxLen];

    /// <summary>DNS cache timeout in seconds (0 = no caching).</summary>
    public int DnsCacheTimeoutSec;

    /// <summary>IP resolution mode (IpResolveMode enum value).</summary>
    public int IpResolveMode;

    /// <summary>DNS query timeout in milliseconds.</summary>
    public int DnsTimeoutMs;

    /// <summary>TCP connection timeout in milliseconds.</summary>
    public int TcpConnectTimeoutMs;

    /// <summary>TLS/SSL handshake timeout in milliseconds.</summary>
    public int TlsHandshakeTimeoutMs;

    /// <summary>Timeout for first byte after connection in milliseconds.</summary>
    public int FirstByteTimeoutMs;

    /// <summary>Happy Eyeballs algorithm timeout in milliseconds.</summary>
    public int HappyEyeballsTimeoutMs;

    /// <summary>Enable TCP keepalive (1 = enabled, 0 = disabled).</summary>
    public int TcpKeepaliveEnabled;

    /// <summary>TCP keepalive idle time in seconds.</summary>
    public int TcpKeepaliveIdleSec;

    /// <summary>TCP keepalive probe interval in seconds.</summary>
    public int TcpKeepaliveIntervalSec;

    /// <summary>Socket receive buffer size in bytes (0 = system default).</summary>
    public int RecvBufferSize;

    /// <summary>Reserved fields for future expansion.</summary>
    private fixed int _reserved[4];

    /// <summary>
    /// Gets the default network configuration with sensible defaults.
    /// </summary>
    public static NetworkConfigNative Default
    {
        get
        {
            var config = new NetworkConfigNative
            {
                DnsMode = (int)Native.DnsResolveMode.System,
                DnsServerCount = 0,
                DnsCacheTimeoutSec = 60,
                IpResolveMode = (int)Native.IpResolveMode.Whatever,
                DnsTimeoutMs = 5000,
                TcpConnectTimeoutMs = 10000,
                TlsHandshakeTimeoutMs = 10000,
                FirstByteTimeoutMs = 15000,
                HappyEyeballsTimeoutMs = 300,
                TcpKeepaliveEnabled = 1,
                TcpKeepaliveIdleSec = 60,
                TcpKeepaliveIntervalSec = 30,
                RecvBufferSize = 0, // System default
            };

            return config;
        }
    }

    /// <summary>
    /// Sets a DNS server address at the specified index.
    /// </summary>
    /// <param name="index">Index of the DNS server (0-3).</param>
    /// <param name="server">DNS server address (IP or hostname).</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if index is out of range.</exception>
    /// <exception cref="ArgumentNullException">Thrown if server is null.</exception>
    /// <exception cref="ArgumentException">Thrown if server is too long.</exception>
    public void SetDnsServer(int index, string server)
    {
        ArgumentNullException.ThrowIfNull(server);

        if (index < 0 || index >= MaxDnsServers)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index),
                index,
                $"Index must be between 0 and {MaxDnsServers - 1}"
            );
        }

        var bytes = Encoding.UTF8.GetBytes(server);
        if (bytes.Length >= DnsServerMaxLen)
        {
            throw new ArgumentException(
                $"DNS server address too long (max {DnsServerMaxLen - 1} bytes)",
                nameof(server)
            );
        }

        fixed (byte* ptr = _dnsServers)
        {
            var offset = index * DnsServerMaxLen;

            // Clear the slot first
            for (var i = 0; i < DnsServerMaxLen; i++)
            {
                ptr[offset + i] = 0;
            }

            // Copy the server address
            for (var i = 0; i < bytes.Length; i++)
            {
                ptr[offset + i] = bytes[i];
            }
        }
    }

    /// <summary>
    /// Gets a DNS server address at the specified index.
    /// </summary>
    /// <param name="index">Index of the DNS server (0-3).</param>
    /// <returns>The DNS server address, or null if not set.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if index is out of range.</exception>
    public readonly string? GetDnsServer(int index)
    {
        if (index < 0 || index >= MaxDnsServers)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index),
                index,
                $"Index must be between 0 and {MaxDnsServers - 1}"
            );
        }

        fixed (byte* ptr = _dnsServers)
        {
            var offset = index * DnsServerMaxLen;

            // Find null terminator
            var length = 0;
            while (length < DnsServerMaxLen && ptr[offset + length] != 0)
            {
                length++;
            }

            if (length == 0)
            {
                return null;
            }

            return Encoding.UTF8.GetString(ptr + offset, length);
        }
    }

    /// <summary>
    /// Sets the DNS-over-HTTPS URL.
    /// </summary>
    /// <param name="url">The DoH URL (e.g., "https://1.1.1.1/dns-query").</param>
    /// <exception cref="ArgumentNullException">Thrown if url is null.</exception>
    /// <exception cref="ArgumentException">Thrown if url is too long.</exception>
    public void SetDohUrl(string url)
    {
        ArgumentNullException.ThrowIfNull(url);

        var bytes = Encoding.UTF8.GetBytes(url);
        if (bytes.Length >= DohUrlMaxLen)
        {
            throw new ArgumentException($"DoH URL too long (max {DohUrlMaxLen - 1} bytes)", nameof(url));
        }

        fixed (byte* ptr = _dohUrl)
        {
            // Clear the buffer first
            for (var i = 0; i < DohUrlMaxLen; i++)
            {
                ptr[i] = 0;
            }

            // Copy the URL
            for (var i = 0; i < bytes.Length; i++)
            {
                ptr[i] = bytes[i];
            }
        }
    }

    /// <summary>
    /// Gets the DNS-over-HTTPS URL.
    /// </summary>
    /// <returns>The DoH URL, or null if not set.</returns>
    public readonly string? GetDohUrl()
    {
        fixed (byte* ptr = _dohUrl)
        {
            // Find null terminator
            var length = 0;
            while (length < DohUrlMaxLen && ptr[length] != 0)
            {
                length++;
            }

            if (length == 0)
            {
                return null;
            }

            return Encoding.UTF8.GetString(ptr, length);
        }
    }
}

// =============================================================================
// High-Level Network Configuration Builder
// =============================================================================

/// <summary>
/// High-level network configuration builder with fluent API.
/// Use this class to configure network settings for the native streamer.
/// </summary>
/// <remarks>
/// <para>
/// This class provides a convenient builder pattern for configuring network
/// settings including DNS resolution, timeouts, and TCP keepalive.
/// </para>
/// <para>
/// Example usage:
/// <code>
/// var config = new NetworkConfig()
///     .UseCloudflareDns()
///     .WithTcpConnectTimeout(TimeSpan.FromSeconds(5))
///     .WithTcpKeepalive(idle: TimeSpan.FromMinutes(1), interval: TimeSpan.FromSeconds(30));
/// </code>
/// </para>
/// </remarks>
public sealed class NetworkConfig
{
    // DNS configuration
    private DnsResolveMode _dnsMode = DnsResolveMode.System;
    private readonly string?[] _dnsServers = new string?[NetworkConfigNative.MaxDnsServers];
    private int _dnsServerCount;
    private string? _dohUrl;
    private int _dnsCacheTimeoutSeconds = 60;
    private int _dnsTimeoutMs = 5000;

    // IP resolution
    private IpResolveMode _ipResolve = IpResolveMode.Whatever;

    // Connection timeouts
    private int _tcpConnectTimeoutMs = 10000;
    private int _tlsHandshakeTimeoutMs = 10000;
    private int _firstByteTimeoutMs = 15000;
    private int _happyEyeballsTimeoutMs = 300;

    // TCP keepalive
    private bool _tcpKeepaliveEnabled = true;
    private int _tcpKeepaliveIdleSec = 60;
    private int _tcpKeepaliveIntervalSec = 30;

    // Buffer
    private int _recvBufferSize;

    /// <summary>
    /// Gets or sets the DNS resolution mode.
    /// </summary>
    public DnsResolveMode DnsMode
    {
        get => _dnsMode;
        set => _dnsMode = value;
    }

    /// <summary>
    /// Gets or sets the IP address resolution mode.
    /// </summary>
    public IpResolveMode IpResolve
    {
        get => _ipResolve;
        set => _ipResolve = value;
    }

    /// <summary>
    /// Gets or sets the DNS cache timeout in seconds.
    /// </summary>
    public int DnsCacheTimeoutSeconds
    {
        get => _dnsCacheTimeoutSeconds;
        set => _dnsCacheTimeoutSeconds = value;
    }

    /// <summary>
    /// Gets or sets the DNS query timeout in milliseconds.
    /// </summary>
    public int DnsTimeoutMs
    {
        get => _dnsTimeoutMs;
        set => _dnsTimeoutMs = value;
    }

    /// <summary>
    /// Gets or sets the TCP connection timeout in milliseconds.
    /// </summary>
    public int TcpConnectTimeoutMs
    {
        get => _tcpConnectTimeoutMs;
        set => _tcpConnectTimeoutMs = value;
    }

    /// <summary>
    /// Gets or sets the TLS handshake timeout in milliseconds.
    /// </summary>
    public int TlsHandshakeTimeoutMs
    {
        get => _tlsHandshakeTimeoutMs;
        set => _tlsHandshakeTimeoutMs = value;
    }

    /// <summary>
    /// Gets or sets the timeout for receiving first byte after connection in milliseconds.
    /// </summary>
    public int FirstByteTimeoutMs
    {
        get => _firstByteTimeoutMs;
        set => _firstByteTimeoutMs = value;
    }

    /// <summary>
    /// Gets or sets the Happy Eyeballs algorithm timeout in milliseconds.
    /// </summary>
    public int HappyEyeballsTimeoutMs
    {
        get => _happyEyeballsTimeoutMs;
        set => _happyEyeballsTimeoutMs = value;
    }

    /// <summary>
    /// Gets or sets whether TCP keepalive is enabled.
    /// </summary>
    public bool TcpKeepaliveEnabled
    {
        get => _tcpKeepaliveEnabled;
        set => _tcpKeepaliveEnabled = value;
    }

    /// <summary>
    /// Gets or sets the TCP keepalive idle time in seconds.
    /// </summary>
    public int TcpKeepaliveIdleSeconds
    {
        get => _tcpKeepaliveIdleSec;
        set => _tcpKeepaliveIdleSec = value;
    }

    /// <summary>
    /// Gets or sets the TCP keepalive probe interval in seconds.
    /// </summary>
    public int TcpKeepaliveIntervalSeconds
    {
        get => _tcpKeepaliveIntervalSec;
        set => _tcpKeepaliveIntervalSec = value;
    }

    /// <summary>
    /// Gets or sets the socket receive buffer size in bytes (0 = system default).
    /// </summary>
    public int ReceiveBufferSize
    {
        get => _recvBufferSize;
        set => _recvBufferSize = value;
    }

    /// <summary>
    /// Adds a custom DNS server to the configuration.
    /// </summary>
    /// <param name="server">DNS server address (IP or hostname).</param>
    /// <returns>This instance for fluent chaining.</returns>
    /// <exception cref="InvalidOperationException">Thrown if maximum DNS servers exceeded.</exception>
    public NetworkConfig AddDnsServer(string server)
    {
        ArgumentNullException.ThrowIfNull(server);

        if (_dnsServerCount >= NetworkConfigNative.MaxDnsServers)
        {
            throw new InvalidOperationException(
                $"Cannot add more than {NetworkConfigNative.MaxDnsServers} DNS servers"
            );
        }

        _dnsServers[_dnsServerCount++] = server;
        _dnsMode = DnsResolveMode.CustomDns;
        return this;
    }

    /// <summary>
    /// Clears all configured DNS servers.
    /// </summary>
    /// <returns>This instance for fluent chaining.</returns>
    public NetworkConfig ClearDnsServers()
    {
        for (var i = 0; i < _dnsServerCount; i++)
        {
            _dnsServers[i] = null;
        }

        _dnsServerCount = 0;
        return this;
    }

    /// <summary>
    /// Configures Google Public DNS (8.8.8.8, 8.8.4.4).
    /// </summary>
    /// <returns>This instance for fluent chaining.</returns>
    public NetworkConfig UseGoogleDns()
    {
        ClearDnsServers();
        AddDnsServer("8.8.8.8");
        AddDnsServer("8.8.4.4");
        return this;
    }

    /// <summary>
    /// Configures Cloudflare DNS (1.1.1.1, 1.0.0.1).
    /// </summary>
    /// <returns>This instance for fluent chaining.</returns>
    public NetworkConfig UseCloudflareDns()
    {
        ClearDnsServers();
        AddDnsServer("1.1.1.1");
        AddDnsServer("1.0.0.1");
        return this;
    }

    /// <summary>
    /// Configures Cloudflare DNS-over-HTTPS.
    /// </summary>
    /// <returns>This instance for fluent chaining.</returns>
    public NetworkConfig UseCloudflareDoH()
    {
        return UseDnsOverHttps("https://cloudflare-dns.com/dns-query");
    }

    /// <summary>
    /// Configures Google DNS-over-HTTPS.
    /// </summary>
    /// <returns>This instance for fluent chaining.</returns>
    public NetworkConfig UseGoogleDoH()
    {
        return UseDnsOverHttps("https://dns.google/dns-query");
    }

    /// <summary>
    /// Configures a custom DNS-over-HTTPS URL.
    /// </summary>
    /// <param name="url">The DoH URL (e.g., "https://1.1.1.1/dns-query").</param>
    /// <returns>This instance for fluent chaining.</returns>
    public NetworkConfig UseDnsOverHttps(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        _dohUrl = url;
        _dnsMode = DnsResolveMode.DnsOverHttps;
        return this;
    }

    /// <summary>
    /// Configures the IP resolution mode.
    /// </summary>
    /// <param name="mode">The IP resolution mode.</param>
    /// <returns>This instance for fluent chaining.</returns>
    public NetworkConfig WithIpResolveMode(IpResolveMode mode)
    {
        _ipResolve = mode;
        return this;
    }

    /// <summary>
    /// Configures connection timeouts.
    /// </summary>
    /// <param name="tcpConnect">TCP connection timeout.</param>
    /// <param name="tlsHandshake">TLS handshake timeout (optional, defaults to tcpConnect).</param>
    /// <param name="firstByte">First byte timeout (optional, defaults to 1.5x tcpConnect).</param>
    /// <returns>This instance for fluent chaining.</returns>
    public NetworkConfig WithConnectionTimeouts(
        TimeSpan tcpConnect,
        TimeSpan? tlsHandshake = null,
        TimeSpan? firstByte = null
    )
    {
        _tcpConnectTimeoutMs = (int)tcpConnect.TotalMilliseconds;
        _tlsHandshakeTimeoutMs = (int)(tlsHandshake ?? tcpConnect).TotalMilliseconds;
        _firstByteTimeoutMs = (int)
            (firstByte ?? TimeSpan.FromMilliseconds(tcpConnect.TotalMilliseconds * 1.5)).TotalMilliseconds;
        return this;
    }

    /// <summary>
    /// Configures TCP connection timeout.
    /// </summary>
    /// <param name="timeout">The timeout value.</param>
    /// <returns>This instance for fluent chaining.</returns>
    public NetworkConfig WithTcpConnectTimeout(TimeSpan timeout)
    {
        _tcpConnectTimeoutMs = (int)timeout.TotalMilliseconds;
        return this;
    }

    /// <summary>
    /// Configures DNS timeout.
    /// </summary>
    /// <param name="timeout">The timeout value.</param>
    /// <returns>This instance for fluent chaining.</returns>
    public NetworkConfig WithDnsTimeout(TimeSpan timeout)
    {
        _dnsTimeoutMs = (int)timeout.TotalMilliseconds;
        return this;
    }

    /// <summary>
    /// Configures DNS cache timeout.
    /// </summary>
    /// <param name="timeout">The cache timeout (TimeSpan.Zero disables caching).</param>
    /// <returns>This instance for fluent chaining.</returns>
    public NetworkConfig WithDnsCacheTimeout(TimeSpan timeout)
    {
        _dnsCacheTimeoutSeconds = (int)timeout.TotalSeconds;
        return this;
    }

    /// <summary>
    /// Configures TCP keepalive settings.
    /// </summary>
    /// <param name="idle">Time before first keepalive probe.</param>
    /// <param name="interval">Interval between keepalive probes.</param>
    /// <returns>This instance for fluent chaining.</returns>
    public NetworkConfig WithTcpKeepalive(TimeSpan idle, TimeSpan interval)
    {
        _tcpKeepaliveEnabled = true;
        _tcpKeepaliveIdleSec = (int)idle.TotalSeconds;
        _tcpKeepaliveIntervalSec = (int)interval.TotalSeconds;
        return this;
    }

    /// <summary>
    /// Disables TCP keepalive.
    /// </summary>
    /// <returns>This instance for fluent chaining.</returns>
    public NetworkConfig DisableTcpKeepalive()
    {
        _tcpKeepaliveEnabled = false;
        return this;
    }

    /// <summary>
    /// Configures the socket receive buffer size.
    /// </summary>
    /// <param name="size">Buffer size in bytes (0 = system default).</param>
    /// <returns>This instance for fluent chaining.</returns>
    public NetworkConfig WithReceiveBufferSize(int size)
    {
        _recvBufferSize = size;
        return this;
    }

    /// <summary>
    /// Configures Happy Eyeballs algorithm timeout.
    /// </summary>
    /// <param name="timeout">The timeout value.</param>
    /// <returns>This instance for fluent chaining.</returns>
    public NetworkConfig WithHappyEyeballsTimeout(TimeSpan timeout)
    {
        _happyEyeballsTimeoutMs = (int)timeout.TotalMilliseconds;
        return this;
    }

    /// <summary>
    /// Converts this configuration to a native structure for P/Invoke.
    /// </summary>
    /// <returns>A <see cref="NetworkConfigNative"/> structure.</returns>
    internal NetworkConfigNative ToNative()
    {
        var native = new NetworkConfigNative
        {
            DnsMode = (int)_dnsMode,
            DnsServerCount = _dnsServerCount,
            DnsCacheTimeoutSec = _dnsCacheTimeoutSeconds,
            IpResolveMode = (int)_ipResolve,
            DnsTimeoutMs = _dnsTimeoutMs,
            TcpConnectTimeoutMs = _tcpConnectTimeoutMs,
            TlsHandshakeTimeoutMs = _tlsHandshakeTimeoutMs,
            FirstByteTimeoutMs = _firstByteTimeoutMs,
            HappyEyeballsTimeoutMs = _happyEyeballsTimeoutMs,
            TcpKeepaliveEnabled = _tcpKeepaliveEnabled ? 1 : 0,
            TcpKeepaliveIdleSec = _tcpKeepaliveIdleSec,
            TcpKeepaliveIntervalSec = _tcpKeepaliveIntervalSec,
            RecvBufferSize = _recvBufferSize,
        };

        // Copy DNS servers
        for (var i = 0; i < _dnsServerCount; i++)
        {
            if (_dnsServers[i] != null)
            {
                native.SetDnsServer(i, _dnsServers[i]!);
            }
        }

        // Set DoH URL if configured
        if (!string.IsNullOrEmpty(_dohUrl))
        {
            native.SetDohUrl(_dohUrl);
        }

        return native;
    }

    /// <summary>
    /// Creates a default network configuration using system DNS.
    /// </summary>
    /// <returns>A new <see cref="NetworkConfig"/> instance with default settings.</returns>
    public static NetworkConfig CreateDefault() => new();

    /// <summary>
    /// Creates a configuration optimized for streaming with Cloudflare DNS.
    /// </summary>
    /// <returns>A new <see cref="NetworkConfig"/> instance optimized for streaming.</returns>
    public static NetworkConfig CreateForStreaming()
    {
        return new NetworkConfig()
            .UseCloudflareDns()
            .WithConnectionTimeouts(
                tcpConnect: TimeSpan.FromSeconds(5),
                tlsHandshake: TimeSpan.FromSeconds(5),
                firstByte: TimeSpan.FromSeconds(10)
            )
            .WithTcpKeepalive(idle: TimeSpan.FromSeconds(30), interval: TimeSpan.FromSeconds(15))
            .WithDnsTimeout(TimeSpan.FromSeconds(3))
            .WithDnsCacheTimeout(TimeSpan.FromMinutes(5));
    }
}
