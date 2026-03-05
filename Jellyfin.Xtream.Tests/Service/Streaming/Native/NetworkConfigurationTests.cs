// Copyright (C) 2025  Gergo Magyar
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using Jellyfin.Xtream.Service.Streaming.Native;
using Xunit;

namespace Jellyfin.Xtream.Tests.Service.Streaming.Native;

/// <summary>
/// Unit tests for <see cref="NetworkConfig"/> builder and <see cref="NetworkConfigNative"/> struct.
/// </summary>
/// <remarks>
/// These tests verify the network configuration system used for production-grade
/// DNS resolution, connection timeouts, and TCP settings.
/// </remarks>
public class NetworkConfigurationTests
{
    // ========================================================================
    // NetworkConfigNative Tests
    // ========================================================================

    [Fact]
    public void NetworkConfigNative_Default_HasSensibleDefaults()
    {
        // Arrange & Act
        var config = NetworkConfigNative.Default;

        // Assert
        Assert.Equal((int)DnsResolveMode.System, config.DnsMode);
        Assert.Equal(0, config.DnsServerCount);
        Assert.Equal(60, config.DnsCacheTimeoutSec);
        Assert.Equal((int)IpResolveMode.Whatever, config.IpResolveMode);
        Assert.Equal(5000, config.DnsTimeoutMs);
        Assert.Equal(10000, config.TcpConnectTimeoutMs);
        Assert.Equal(10000, config.TlsHandshakeTimeoutMs);
        Assert.Equal(15000, config.FirstByteTimeoutMs);
        Assert.Equal(300, config.HappyEyeballsTimeoutMs);
        Assert.Equal(1, config.TcpKeepaliveEnabled);
        Assert.Equal(60, config.TcpKeepaliveIdleSec);
        Assert.Equal(30, config.TcpKeepaliveIntervalSec);
        Assert.Equal(0, config.RecvBufferSize); // System default
    }

    [Fact]
    public void NetworkConfigNative_SetDnsServer_StoresServer()
    {
        // Arrange
        var config = NetworkConfigNative.Default;

        // Act
        config.SetDnsServer(0, "8.8.8.8");

        // Assert
        Assert.Equal("8.8.8.8", config.GetDnsServer(0));
    }

    [Fact]
    public void NetworkConfigNative_SetDnsServer_MultipleServers()
    {
        // Arrange
        var config = NetworkConfigNative.Default;

        // Act
        config.SetDnsServer(0, "8.8.8.8");
        config.SetDnsServer(1, "8.8.4.4");
        config.SetDnsServer(2, "1.1.1.1");
        config.SetDnsServer(3, "1.0.0.1");

        // Assert
        Assert.Equal("8.8.8.8", config.GetDnsServer(0));
        Assert.Equal("8.8.4.4", config.GetDnsServer(1));
        Assert.Equal("1.1.1.1", config.GetDnsServer(2));
        Assert.Equal("1.0.0.1", config.GetDnsServer(3));
    }

    [Fact]
    public void NetworkConfigNative_SetDnsServer_OverwritesExisting()
    {
        // Arrange
        var config = NetworkConfigNative.Default;
        config.SetDnsServer(0, "old.dns.server");

        // Act
        config.SetDnsServer(0, "new.dns.server");

        // Assert
        Assert.Equal("new.dns.server", config.GetDnsServer(0));
    }

    [Fact]
    public void NetworkConfigNative_SetDnsServer_InvalidIndex_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var config = NetworkConfigNative.Default;

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => config.SetDnsServer(-1, "8.8.8.8"));
        Assert.Throws<ArgumentOutOfRangeException>(() => config.SetDnsServer(4, "8.8.8.8"));
        Assert.Throws<ArgumentOutOfRangeException>(() => config.SetDnsServer(100, "8.8.8.8"));
    }

    [Fact]
    public void NetworkConfigNative_SetDnsServer_NullServer_ThrowsArgumentNullException()
    {
        // Arrange
        var config = NetworkConfigNative.Default;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => config.SetDnsServer(0, null!));
    }

    [Fact]
    public void NetworkConfigNative_GetDnsServer_InvalidIndex_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var config = NetworkConfigNative.Default;

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => config.GetDnsServer(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => config.GetDnsServer(4));
    }

    [Fact]
    public void NetworkConfigNative_GetDnsServer_UnsetServer_ReturnsNull()
    {
        // Arrange
        var config = NetworkConfigNative.Default;

        // Act & Assert
        Assert.Null(config.GetDnsServer(0));
        Assert.Null(config.GetDnsServer(1));
        Assert.Null(config.GetDnsServer(2));
        Assert.Null(config.GetDnsServer(3));
    }

    [Fact]
    public void NetworkConfigNative_SetDohUrl_StoresUrl()
    {
        // Arrange
        var config = NetworkConfigNative.Default;

        // Act
        config.SetDohUrl("https://cloudflare-dns.com/dns-query");

        // Assert
        Assert.Equal("https://cloudflare-dns.com/dns-query", config.GetDohUrl());
    }

    [Fact]
    public void NetworkConfigNative_SetDohUrl_OverwritesExisting()
    {
        // Arrange
        var config = NetworkConfigNative.Default;
        config.SetDohUrl("https://old.doh.server/dns-query");

        // Act
        config.SetDohUrl("https://new.doh.server/dns-query");

        // Assert
        Assert.Equal("https://new.doh.server/dns-query", config.GetDohUrl());
    }

    [Fact]
    public void NetworkConfigNative_SetDohUrl_NullUrl_ThrowsArgumentNullException()
    {
        // Arrange
        var config = NetworkConfigNative.Default;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => config.SetDohUrl(null!));
    }

    [Fact]
    public void NetworkConfigNative_GetDohUrl_UnsetUrl_ReturnsNull()
    {
        // Arrange
        var config = NetworkConfigNative.Default;

        // Act & Assert
        Assert.Null(config.GetDohUrl());
    }

    [Fact]
    public void NetworkConfigNative_SetDnsServer_TooLongServer_ThrowsArgumentException()
    {
        // Arrange
        var config = NetworkConfigNative.Default;
        var longServer = new string('x', 300); // 300 chars, max is 255

        // Act & Assert
        Assert.Throws<ArgumentException>(() => config.SetDnsServer(0, longServer));
    }

    [Fact]
    public void NetworkConfigNative_SetDohUrl_TooLongUrl_ThrowsArgumentException()
    {
        // Arrange
        var config = NetworkConfigNative.Default;
        var longUrl = "https://" + new string('x', 600) + "/dns-query"; // Over 512 chars

        // Act & Assert
        Assert.Throws<ArgumentException>(() => config.SetDohUrl(longUrl));
    }

    // ========================================================================
    // NetworkConfig Builder Tests
    // ========================================================================

    [Fact]
    public void NetworkConfig_CreateDefault_ReturnsNewInstance()
    {
        // Act
        var config = NetworkConfig.CreateDefault();

        // Assert
        Assert.NotNull(config);
        Assert.Equal(DnsResolveMode.System, config.DnsMode);
        Assert.Equal(IpResolveMode.Whatever, config.IpResolve);
    }

    [Fact]
    public void NetworkConfig_CreateForStreaming_ReturnsOptimizedConfig()
    {
        // Act
        var config = NetworkConfig.CreateForStreaming();

        // Assert
        Assert.NotNull(config);
        Assert.Equal(DnsResolveMode.CustomDns, config.DnsMode);
        Assert.Equal(5000, config.TcpConnectTimeoutMs);
        Assert.Equal(3000, config.DnsTimeoutMs);
        Assert.True(config.TcpKeepaliveEnabled);
    }

    [Fact]
    public void NetworkConfig_UseGoogleDns_ConfiguresGoogleDnsServers()
    {
        // Act
        var config = new NetworkConfig().UseGoogleDns();
        var native = config.ToNative();

        // Assert
        Assert.Equal((int)DnsResolveMode.CustomDns, native.DnsMode);
        Assert.Equal(2, native.DnsServerCount);
        Assert.Equal("8.8.8.8", native.GetDnsServer(0));
        Assert.Equal("8.8.4.4", native.GetDnsServer(1));
    }

    [Fact]
    public void NetworkConfig_UseCloudflareDns_ConfiguresCloudflareDnsServers()
    {
        // Act
        var config = new NetworkConfig().UseCloudflareDns();
        var native = config.ToNative();

        // Assert
        Assert.Equal((int)DnsResolveMode.CustomDns, native.DnsMode);
        Assert.Equal(2, native.DnsServerCount);
        Assert.Equal("1.1.1.1", native.GetDnsServer(0));
        Assert.Equal("1.0.0.1", native.GetDnsServer(1));
    }

    [Fact]
    public void NetworkConfig_UseCloudflareDoH_ConfiguresCloudflareDoH()
    {
        // Act
        var config = new NetworkConfig().UseCloudflareDoH();
        var native = config.ToNative();

        // Assert
        Assert.Equal((int)DnsResolveMode.DnsOverHttps, native.DnsMode);
        Assert.Equal("https://cloudflare-dns.com/dns-query", native.GetDohUrl());
    }

    [Fact]
    public void NetworkConfig_UseGoogleDoH_ConfiguresGoogleDoH()
    {
        // Act
        var config = new NetworkConfig().UseGoogleDoH();
        var native = config.ToNative();

        // Assert
        Assert.Equal((int)DnsResolveMode.DnsOverHttps, native.DnsMode);
        Assert.Equal("https://dns.google/dns-query", native.GetDohUrl());
    }

    [Fact]
    public void NetworkConfig_UseDnsOverHttps_CustomUrl_ConfiguresCustomDoH()
    {
        // Act
        var config = new NetworkConfig().UseDnsOverHttps("https://custom.doh.server/dns-query");
        var native = config.ToNative();

        // Assert
        Assert.Equal((int)DnsResolveMode.DnsOverHttps, native.DnsMode);
        Assert.Equal("https://custom.doh.server/dns-query", native.GetDohUrl());
    }

    [Fact]
    public void NetworkConfig_AddDnsServer_AddsSingleServer()
    {
        // Act
        var config = new NetworkConfig().AddDnsServer("8.8.8.8");
        var native = config.ToNative();

        // Assert
        Assert.Equal((int)DnsResolveMode.CustomDns, native.DnsMode);
        Assert.Equal(1, native.DnsServerCount);
        Assert.Equal("8.8.8.8", native.GetDnsServer(0));
    }

    [Fact]
    public void NetworkConfig_AddDnsServer_MultipleTimes_AddsAllServers()
    {
        // Act
        var config = new NetworkConfig().AddDnsServer("8.8.8.8").AddDnsServer("8.8.4.4").AddDnsServer("1.1.1.1");
        var native = config.ToNative();

        // Assert
        Assert.Equal(3, native.DnsServerCount);
        Assert.Equal("8.8.8.8", native.GetDnsServer(0));
        Assert.Equal("8.8.4.4", native.GetDnsServer(1));
        Assert.Equal("1.1.1.1", native.GetDnsServer(2));
    }

    [Fact]
    public void NetworkConfig_AddDnsServer_ExceedsMaximum_ThrowsInvalidOperationException()
    {
        // Arrange
        var config = new NetworkConfig()
            .AddDnsServer("1.1.1.1")
            .AddDnsServer("1.0.0.1")
            .AddDnsServer("8.8.8.8")
            .AddDnsServer("8.8.4.4");

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => config.AddDnsServer("9.9.9.9"));
    }

    [Fact]
    public void NetworkConfig_ClearDnsServers_ClearsAllServers()
    {
        // Arrange
        var config = new NetworkConfig().AddDnsServer("8.8.8.8").AddDnsServer("8.8.4.4");

        // Act
        config.ClearDnsServers();
        var native = config.ToNative();

        // Assert
        Assert.Equal(0, native.DnsServerCount);
    }

    [Fact]
    public void NetworkConfig_WithIpResolveMode_SetsMode()
    {
        // Act
        var config = new NetworkConfig().WithIpResolveMode(IpResolveMode.IPv4Only);
        var native = config.ToNative();

        // Assert
        Assert.Equal((int)IpResolveMode.IPv4Only, native.IpResolveMode);
    }

    [Fact]
    public void NetworkConfig_WithConnectionTimeouts_SetsAllTimeouts()
    {
        // Act
        var config = new NetworkConfig().WithConnectionTimeouts(
            tcpConnect: TimeSpan.FromSeconds(5),
            tlsHandshake: TimeSpan.FromSeconds(10),
            firstByte: TimeSpan.FromSeconds(15)
        );
        var native = config.ToNative();

        // Assert
        Assert.Equal(5000, native.TcpConnectTimeoutMs);
        Assert.Equal(10000, native.TlsHandshakeTimeoutMs);
        Assert.Equal(15000, native.FirstByteTimeoutMs);
    }

    [Fact]
    public void NetworkConfig_WithConnectionTimeouts_UsesDefaults_WhenNotSpecified()
    {
        // Act
        var config = new NetworkConfig().WithConnectionTimeouts(tcpConnect: TimeSpan.FromSeconds(5));
        var native = config.ToNative();

        // Assert
        Assert.Equal(5000, native.TcpConnectTimeoutMs);
        Assert.Equal(5000, native.TlsHandshakeTimeoutMs); // Defaults to tcpConnect
        Assert.Equal(7500, native.FirstByteTimeoutMs); // Defaults to 1.5x tcpConnect
    }

    [Fact]
    public void NetworkConfig_WithTcpConnectTimeout_SetsTimeout()
    {
        // Act
        var config = new NetworkConfig().WithTcpConnectTimeout(TimeSpan.FromSeconds(7));
        var native = config.ToNative();

        // Assert
        Assert.Equal(7000, native.TcpConnectTimeoutMs);
    }

    [Fact]
    public void NetworkConfig_WithDnsTimeout_SetsTimeout()
    {
        // Act
        var config = new NetworkConfig().WithDnsTimeout(TimeSpan.FromSeconds(3));
        var native = config.ToNative();

        // Assert
        Assert.Equal(3000, native.DnsTimeoutMs);
    }

    [Fact]
    public void NetworkConfig_WithDnsCacheTimeout_SetsCacheTimeout()
    {
        // Act
        var config = new NetworkConfig().WithDnsCacheTimeout(TimeSpan.FromMinutes(5));
        var native = config.ToNative();

        // Assert
        Assert.Equal(300, native.DnsCacheTimeoutSec);
    }

    [Fact]
    public void NetworkConfig_WithDnsCacheTimeout_Zero_DisablesCaching()
    {
        // Act
        var config = new NetworkConfig().WithDnsCacheTimeout(TimeSpan.Zero);
        var native = config.ToNative();

        // Assert
        Assert.Equal(0, native.DnsCacheTimeoutSec);
    }

    [Fact]
    public void NetworkConfig_WithTcpKeepalive_EnablesKeepalive()
    {
        // Act
        var config = new NetworkConfig().WithTcpKeepalive(
            idle: TimeSpan.FromMinutes(2),
            interval: TimeSpan.FromSeconds(30)
        );
        var native = config.ToNative();

        // Assert
        Assert.Equal(1, native.TcpKeepaliveEnabled);
        Assert.Equal(120, native.TcpKeepaliveIdleSec);
        Assert.Equal(30, native.TcpKeepaliveIntervalSec);
    }

    [Fact]
    public void NetworkConfig_DisableTcpKeepalive_DisablesKeepalive()
    {
        // Act
        var config = new NetworkConfig().DisableTcpKeepalive();
        var native = config.ToNative();

        // Assert
        Assert.Equal(0, native.TcpKeepaliveEnabled);
    }

    [Fact]
    public void NetworkConfig_WithReceiveBufferSize_SetsBufferSize()
    {
        // Act
        var config = new NetworkConfig().WithReceiveBufferSize(131072); // 128KB
        var native = config.ToNative();

        // Assert
        Assert.Equal(131072, native.RecvBufferSize);
    }

    [Fact]
    public void NetworkConfig_WithHappyEyeballsTimeout_SetsTimeout()
    {
        // Act
        var config = new NetworkConfig().WithHappyEyeballsTimeout(TimeSpan.FromMilliseconds(500));
        var native = config.ToNative();

        // Assert
        Assert.Equal(500, native.HappyEyeballsTimeoutMs);
    }

    [Fact]
    public void NetworkConfig_FluentChaining_WorksCorrectly()
    {
        // Act
        var config = new NetworkConfig()
            .UseCloudflareDns()
            .WithIpResolveMode(IpResolveMode.PreferIPv4)
            .WithTcpConnectTimeout(TimeSpan.FromSeconds(5))
            .WithDnsTimeout(TimeSpan.FromSeconds(3))
            .WithDnsCacheTimeout(TimeSpan.FromMinutes(10))
            .WithTcpKeepalive(idle: TimeSpan.FromMinutes(1), interval: TimeSpan.FromSeconds(30))
            .WithReceiveBufferSize(65536)
            .WithHappyEyeballsTimeout(TimeSpan.FromMilliseconds(200));

        var native = config.ToNative();

        // Assert
        Assert.Equal((int)DnsResolveMode.CustomDns, native.DnsMode);
        Assert.Equal(2, native.DnsServerCount);
        Assert.Equal("1.1.1.1", native.GetDnsServer(0));
        Assert.Equal("1.0.0.1", native.GetDnsServer(1));
        Assert.Equal((int)IpResolveMode.PreferIPv4, native.IpResolveMode);
        Assert.Equal(5000, native.TcpConnectTimeoutMs);
        Assert.Equal(3000, native.DnsTimeoutMs);
        Assert.Equal(600, native.DnsCacheTimeoutSec);
        Assert.Equal(1, native.TcpKeepaliveEnabled);
        Assert.Equal(60, native.TcpKeepaliveIdleSec);
        Assert.Equal(30, native.TcpKeepaliveIntervalSec);
        Assert.Equal(65536, native.RecvBufferSize);
        Assert.Equal(200, native.HappyEyeballsTimeoutMs);
    }

    [Fact]
    public void NetworkConfig_ToNative_CopiesDnsServersCorrectly()
    {
        // Arrange
        var config = new NetworkConfig().AddDnsServer("8.8.8.8").AddDnsServer("1.1.1.1");

        // Act
        var native1 = config.ToNative();
        var native2 = config.ToNative();

        // Assert - each ToNative call should produce independent copies
        Assert.Equal(native1.GetDnsServer(0), native2.GetDnsServer(0));
        Assert.Equal(native1.GetDnsServer(1), native2.GetDnsServer(1));
    }

    [Fact]
    public void NetworkConfig_ToNative_CopiesDoHUrlCorrectly()
    {
        // Arrange
        var config = new NetworkConfig().UseCloudflareDoH();

        // Act
        var native = config.ToNative();

        // Assert
        Assert.Equal("https://cloudflare-dns.com/dns-query", native.GetDohUrl());
    }

    // ========================================================================
    // Enum Tests
    // ========================================================================

    [Theory]
    [InlineData(IpResolveMode.Whatever, 0)]
    [InlineData(IpResolveMode.IPv4Only, 1)]
    [InlineData(IpResolveMode.IPv6Only, 2)]
    [InlineData(IpResolveMode.PreferIPv4, 3)]
    [InlineData(IpResolveMode.PreferIPv6, 4)]
    public void IpResolveMode_HasCorrectValues(IpResolveMode mode, int expectedValue)
    {
        Assert.Equal(expectedValue, (int)mode);
    }

    [Theory]
    [InlineData(DnsResolveMode.System, 0)]
    [InlineData(DnsResolveMode.CustomDns, 1)]
    [InlineData(DnsResolveMode.DnsOverHttps, 2)]
    public void DnsResolveMode_HasCorrectValues(DnsResolveMode mode, int expectedValue)
    {
        Assert.Equal(expectedValue, (int)mode);
    }

    [Theory]
    [InlineData(DnsErrorType.None, 0)]
    [InlineData(DnsErrorType.ResolutionFailed, 1)]
    [InlineData(DnsErrorType.Timeout, 2)]
    [InlineData(DnsErrorType.ServerUnreachable, 3)]
    [InlineData(DnsErrorType.InvalidServer, 4)]
    [InlineData(DnsErrorType.DoHError, 5)]
    public void DnsErrorType_HasCorrectValues(DnsErrorType error, int expectedValue)
    {
        Assert.Equal(expectedValue, (int)error);
    }

    // ========================================================================
    // Property Tests
    // ========================================================================

    [Fact]
    public void NetworkConfig_Properties_CanBeSetAndRead()
    {
        // Arrange
        var config = new NetworkConfig();

        // Act
        config.DnsMode = DnsResolveMode.CustomDns;
        config.IpResolve = IpResolveMode.IPv6Only;
        config.DnsCacheTimeoutSeconds = 120;
        config.DnsTimeoutMs = 3000;
        config.TcpConnectTimeoutMs = 8000;
        config.TlsHandshakeTimeoutMs = 12000;
        config.FirstByteTimeoutMs = 20000;
        config.HappyEyeballsTimeoutMs = 400;
        config.TcpKeepaliveEnabled = false;
        config.TcpKeepaliveIdleSeconds = 90;
        config.TcpKeepaliveIntervalSeconds = 45;
        config.ReceiveBufferSize = 32768;

        // Assert
        Assert.Equal(DnsResolveMode.CustomDns, config.DnsMode);
        Assert.Equal(IpResolveMode.IPv6Only, config.IpResolve);
        Assert.Equal(120, config.DnsCacheTimeoutSeconds);
        Assert.Equal(3000, config.DnsTimeoutMs);
        Assert.Equal(8000, config.TcpConnectTimeoutMs);
        Assert.Equal(12000, config.TlsHandshakeTimeoutMs);
        Assert.Equal(20000, config.FirstByteTimeoutMs);
        Assert.Equal(400, config.HappyEyeballsTimeoutMs);
        Assert.False(config.TcpKeepaliveEnabled);
        Assert.Equal(90, config.TcpKeepaliveIdleSeconds);
        Assert.Equal(45, config.TcpKeepaliveIntervalSeconds);
        Assert.Equal(32768, config.ReceiveBufferSize);
    }

    [Fact]
    public void NetworkConfig_ToNative_TransfersAllProperties()
    {
        // Arrange
        var config = new NetworkConfig
        {
            DnsMode = DnsResolveMode.CustomDns,
            IpResolve = IpResolveMode.PreferIPv6,
            DnsCacheTimeoutSeconds = 180,
            DnsTimeoutMs = 4000,
            TcpConnectTimeoutMs = 7000,
            TlsHandshakeTimeoutMs = 9000,
            FirstByteTimeoutMs = 12000,
            HappyEyeballsTimeoutMs = 350,
            TcpKeepaliveEnabled = true,
            TcpKeepaliveIdleSeconds = 45,
            TcpKeepaliveIntervalSeconds = 20,
            ReceiveBufferSize = 98304,
        };

        // Act
        var native = config.ToNative();

        // Assert
        Assert.Equal((int)DnsResolveMode.CustomDns, native.DnsMode);
        Assert.Equal((int)IpResolveMode.PreferIPv6, native.IpResolveMode);
        Assert.Equal(180, native.DnsCacheTimeoutSec);
        Assert.Equal(4000, native.DnsTimeoutMs);
        Assert.Equal(7000, native.TcpConnectTimeoutMs);
        Assert.Equal(9000, native.TlsHandshakeTimeoutMs);
        Assert.Equal(12000, native.FirstByteTimeoutMs);
        Assert.Equal(350, native.HappyEyeballsTimeoutMs);
        Assert.Equal(1, native.TcpKeepaliveEnabled);
        Assert.Equal(45, native.TcpKeepaliveIdleSec);
        Assert.Equal(20, native.TcpKeepaliveIntervalSec);
        Assert.Equal(98304, native.RecvBufferSize);
    }
}
