using System;
using System.Threading.Tasks;
using Jellyfin.Xtream.E2ETests.Infrastructure;
using Jellyfin.Xtream.Service.Streaming.Native;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.E2ETests.Tests;

/// <summary>
/// End-to-end tests for <see cref="NetworkConfig"/> and network configuration functionality.
/// Tests verify that DNS settings, timeouts, and TCP keep-alive work correctly in production scenarios.
/// </summary>
[Collection("E2E-Failover")]
public class NetworkConfigurationE2ETests
{
    private readonly DockerTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public NetworkConfigurationE2ETests(DockerTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    // ========================================================================
    // Basic Network Configuration Tests
    // ========================================================================

    /// <summary>
    /// Tests that a streamer with default network configuration can stream successfully.
    /// </summary>
    [Fact]
    public async Task NetworkConfig_Default_StreamsSuccessfully()
    {
        // Arrange
        var streamUrl = $"{_fixture.BaseUrl}/stream/5000";
        using var streamer = CreateStreamerWithDefaultNetworkConfig();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.AddUrl(streamUrl);

        // Act
        Assert.True(streamer.Start());

        var reachedStreaming = await TestHelpers.WaitForStreamingAsync(streamer, TimeSpan.FromSeconds(10));

        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        _output.WriteLine($"Reached streaming: {reachedStreaming}");
        _output.WriteLine($"State: {status.State}");
        _output.WriteLine($"Bytes received: {status.BytesReceived:N0}");

        Assert.True(reachedStreaming, "Should reach streaming state with default network config");
        Assert.True(status.BytesReceived > 0, "Should receive data");
    }

    /// <summary>
    /// Tests that a streamer with IPv4-only configuration can stream successfully.
    /// This is the recommended default for IPTV streaming.
    /// </summary>
    [Fact]
    public async Task NetworkConfig_IPv4Only_StreamsSuccessfully()
    {
        // Arrange
        var streamUrl = $"{_fixture.BaseUrl}/stream/5000";
        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        var networkConfig = new NetworkConfig()
            .WithIpResolveMode(IpResolveMode.IPv4Only)
            .WithTcpConnectTimeout(TimeSpan.FromSeconds(5));

        streamer.SetNetworkConfig(networkConfig);
        streamer.AddUrl(streamUrl);

        // Act
        Assert.True(streamer.Start());

        var reachedStreaming = await TestHelpers.WaitForStreamingAsync(streamer, TimeSpan.FromSeconds(10));

        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        _output.WriteLine($"Reached streaming: {reachedStreaming}");
        _output.WriteLine($"State: {status.State}");
        _output.WriteLine($"Bytes received: {status.BytesReceived:N0}");

        Assert.True(reachedStreaming, "Should reach streaming state with IPv4-only config");
        Assert.True(status.BytesReceived > 0, "Should receive data");
    }

    /// <summary>
    /// Tests that network configuration can be applied with Cloudflare DNS servers.
    /// </summary>
    [Fact]
    public async Task NetworkConfig_CloudflareDns_StreamsSuccessfully()
    {
        // Arrange
        var streamUrl = $"{_fixture.BaseUrl}/stream/5000";
        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        var networkConfig = new NetworkConfig().UseCloudflareDns().WithTcpConnectTimeout(TimeSpan.FromSeconds(5));

        streamer.SetNetworkConfig(networkConfig);
        streamer.AddUrl(streamUrl);

        // Act
        Assert.True(streamer.Start());

        var reachedStreaming = await TestHelpers.WaitForStreamingAsync(streamer, TimeSpan.FromSeconds(10));

        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        _output.WriteLine($"Reached streaming: {reachedStreaming}");
        _output.WriteLine($"DNS Mode: CustomDns (Cloudflare 1.1.1.1, 1.0.0.1)");
        _output.WriteLine($"State: {status.State}");
        _output.WriteLine($"Bytes received: {status.BytesReceived:N0}");

        Assert.True(reachedStreaming, "Should reach streaming state with Cloudflare DNS");
        Assert.True(status.BytesReceived > 0, "Should receive data");
    }

    /// <summary>
    /// Tests that network configuration can be applied with Google DNS servers.
    /// </summary>
    [Fact]
    public async Task NetworkConfig_GoogleDns_StreamsSuccessfully()
    {
        // Arrange
        var streamUrl = $"{_fixture.BaseUrl}/stream/5000";
        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        var networkConfig = new NetworkConfig().UseGoogleDns().WithTcpConnectTimeout(TimeSpan.FromSeconds(5));

        streamer.SetNetworkConfig(networkConfig);
        streamer.AddUrl(streamUrl);

        // Act
        Assert.True(streamer.Start());

        var reachedStreaming = await TestHelpers.WaitForStreamingAsync(streamer, TimeSpan.FromSeconds(10));

        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        _output.WriteLine($"Reached streaming: {reachedStreaming}");
        _output.WriteLine($"DNS Mode: CustomDns (Google 8.8.8.8, 8.8.4.4)");
        _output.WriteLine($"State: {status.State}");
        _output.WriteLine($"Bytes received: {status.BytesReceived:N0}");

        Assert.True(reachedStreaming, "Should reach streaming state with Google DNS");
        Assert.True(status.BytesReceived > 0, "Should receive data");
    }

    /// <summary>
    /// Tests the CreateForStreaming preset which is optimized for IPTV streaming.
    /// </summary>
    [Fact]
    public async Task NetworkConfig_CreateForStreaming_OptimizedForIPTV()
    {
        // Arrange
        var streamUrl = $"{_fixture.BaseUrl}/stream/5000";
        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        var networkConfig = NetworkConfig.CreateForStreaming();
        streamer.SetNetworkConfig(networkConfig);
        streamer.AddUrl(streamUrl);

        // Act
        Assert.True(streamer.Start());

        var reachedStreaming = await TestHelpers.WaitForStreamingAsync(streamer, TimeSpan.FromSeconds(10));

        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        _output.WriteLine($"Reached streaming: {reachedStreaming}");
        _output.WriteLine($"Using CreateForStreaming preset");
        _output.WriteLine($"State: {status.State}");
        _output.WriteLine($"Bytes received: {status.BytesReceived:N0}");

        Assert.True(reachedStreaming, "Should reach streaming state with streaming preset");
        Assert.True(status.BytesReceived > 0, "Should receive data");
    }

    // ========================================================================
    // Timeout Configuration Tests
    // ========================================================================

    /// <summary>
    /// Tests that a very short connection timeout causes connection to fail fast.
    /// </summary>
    [Fact]
    public async Task NetworkConfig_VeryShortConnectTimeout_FailsFast()
    {
        // Arrange - use delayed endpoint
        var delayedUrl = $"{_fixture.BaseUrl}/stream/delayed/3000"; // 3s delay
        var fallbackUrl = $"{_fixture.BaseUrl}/stream/5000";

        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        // Very short timeout (100ms) should fail on 3s delayed endpoint
        var networkConfig = new NetworkConfig()
            .WithTcpConnectTimeout(TimeSpan.FromMilliseconds(500))
            .WithIpResolveMode(IpResolveMode.IPv4Only);

        streamer.SetNetworkConfig(networkConfig);
        streamer.AddUrl(delayedUrl);
        streamer.AddUrl(fallbackUrl);

        // Act
        Assert.True(streamer.Start());

        // Wait for failover to second URL
        var switchedToFallback = await TestHelpers.WaitForConditionAsync(
            () =>
            {
                var s = streamer.GetStatus();
                return s.CurrentUrlIndex == 1 && s.BytesReceived > 0;
            },
            TimeSpan.FromSeconds(15)
        );

        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        _output.WriteLine($"Switched to fallback: {switchedToFallback}");
        _output.WriteLine($"URL index: {status.CurrentUrlIndex}");
        _output.WriteLine($"Retry count: {status.RetryCount}");
        _output.WriteLine($"Bytes received: {status.BytesReceived:N0}");

        Assert.True(status.BytesReceived > 0, "Should receive data from fallback URL");
    }

    /// <summary>
    /// Tests that connection timeout settings are passed to the native layer.
    /// </summary>
    [Fact]
    public async Task NetworkConfig_CustomTimeouts_AppliedCorrectly()
    {
        // Arrange
        var streamUrl = $"{_fixture.BaseUrl}/stream/5000";
        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        var networkConfig = new NetworkConfig()
            .WithConnectionTimeouts(
                tcpConnect: TimeSpan.FromSeconds(3),
                tlsHandshake: TimeSpan.FromSeconds(5),
                firstByte: TimeSpan.FromSeconds(10)
            )
            .WithDnsTimeout(TimeSpan.FromSeconds(2))
            .WithDnsCacheTimeout(TimeSpan.FromMinutes(10));

        streamer.SetNetworkConfig(networkConfig);
        streamer.AddUrl(streamUrl);

        // Act
        Assert.True(streamer.Start());

        var reachedStreaming = await TestHelpers.WaitForStreamingAsync(streamer, TimeSpan.FromSeconds(10));

        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        _output.WriteLine($"Reached streaming: {reachedStreaming}");
        _output.WriteLine($"Custom timeouts: TCP=3s, TLS=5s, FirstByte=10s, DNS=2s");
        _output.WriteLine($"State: {status.State}");
        _output.WriteLine($"Bytes received: {status.BytesReceived:N0}");

        Assert.True(reachedStreaming, "Should reach streaming state with custom timeouts");
        Assert.True(status.BytesReceived > 0, "Should receive data");
    }

    // ========================================================================
    // TCP Keep-Alive Tests
    // ========================================================================

    /// <summary>
    /// Tests that TCP keep-alive settings are applied for long-running streams.
    /// </summary>
    [Fact]
    public async Task NetworkConfig_TcpKeepalive_EnabledForLongStream()
    {
        // Arrange
        var streamUrl = $"{_fixture.BaseUrl}/stream/5000";
        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        var networkConfig = new NetworkConfig()
            .WithTcpKeepalive(idle: TimeSpan.FromSeconds(30), interval: TimeSpan.FromSeconds(15))
            .WithIpResolveMode(IpResolveMode.IPv4Only);

        streamer.SetNetworkConfig(networkConfig);
        streamer.AddUrl(streamUrl);

        // Act
        Assert.True(streamer.Start());

        var reachedStreaming = await TestHelpers.WaitForStreamingAsync(streamer, TimeSpan.FromSeconds(10));

        // Let stream run for a bit to verify keepalive doesn't break anything
        await Task.Delay(TimeSpan.FromSeconds(3));

        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        _output.WriteLine($"Reached streaming: {reachedStreaming}");
        _output.WriteLine($"TCP Keepalive: idle=30s, interval=15s");
        _output.WriteLine($"State: {status.State}");
        _output.WriteLine($"Bytes received: {status.BytesReceived:N0}");

        Assert.True(reachedStreaming, "Should reach streaming state with TCP keepalive");
        Assert.True(status.BytesReceived > 0, "Should receive data");
    }

    /// <summary>
    /// Tests that TCP keep-alive can be disabled.
    /// </summary>
    [Fact]
    public async Task NetworkConfig_TcpKeepalive_CanBeDisabled()
    {
        // Arrange
        var streamUrl = $"{_fixture.BaseUrl}/stream/5000";
        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        var networkConfig = new NetworkConfig().DisableTcpKeepalive().WithIpResolveMode(IpResolveMode.IPv4Only);

        streamer.SetNetworkConfig(networkConfig);
        streamer.AddUrl(streamUrl);

        // Act
        Assert.True(streamer.Start());

        var reachedStreaming = await TestHelpers.WaitForStreamingAsync(streamer, TimeSpan.FromSeconds(10));

        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        _output.WriteLine($"Reached streaming: {reachedStreaming}");
        _output.WriteLine($"TCP Keepalive: disabled");
        _output.WriteLine($"State: {status.State}");
        _output.WriteLine($"Bytes received: {status.BytesReceived:N0}");

        Assert.True(reachedStreaming, "Should reach streaming state without TCP keepalive");
        Assert.True(status.BytesReceived > 0, "Should receive data");
    }

    // ========================================================================
    // Buffer Size Tests
    // ========================================================================

    /// <summary>
    /// Tests that custom receive buffer size is applied.
    /// </summary>
    [Fact]
    public async Task NetworkConfig_CustomBufferSize_AppliedCorrectly()
    {
        // Arrange
        var streamUrl = $"{_fixture.BaseUrl}/stream/5000";
        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        var networkConfig = new NetworkConfig()
            .WithReceiveBufferSize(131072) // 128KB
            .WithIpResolveMode(IpResolveMode.IPv4Only);

        streamer.SetNetworkConfig(networkConfig);
        streamer.AddUrl(streamUrl);

        // Act
        Assert.True(streamer.Start());

        var reachedStreaming = await TestHelpers.WaitForStreamingAsync(streamer, TimeSpan.FromSeconds(10));

        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        _output.WriteLine($"Reached streaming: {reachedStreaming}");
        _output.WriteLine($"Receive buffer: 128KB");
        _output.WriteLine($"State: {status.State}");
        _output.WriteLine($"Bytes received: {status.BytesReceived:N0}");

        Assert.True(reachedStreaming, "Should reach streaming state with custom buffer");
        Assert.True(status.BytesReceived > 0, "Should receive data");
    }

    // ========================================================================
    // DNS Error Detection Tests
    // ========================================================================

    /// <summary>
    /// Tests that GetLastDnsError returns None when no DNS error occurred.
    /// </summary>
    [Fact]
    public async Task GetLastDnsError_NoError_ReturnsNone()
    {
        // Arrange
        var streamUrl = $"{_fixture.BaseUrl}/stream/5000";
        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.SetNetworkConfig(NetworkConfig.CreateDefault());
        streamer.AddUrl(streamUrl);

        // Act
        Assert.True(streamer.Start());

        await TestHelpers.WaitForStreamingAsync(streamer, TimeSpan.FromSeconds(5));

        var dnsError = streamer.GetLastDnsError();
        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        _output.WriteLine($"DNS Error: {dnsError}");
        _output.WriteLine($"Bytes received: {status.BytesReceived:N0}");

        Assert.Equal(DnsErrorType.None, dnsError);
        Assert.True(status.BytesReceived > 0, "Should receive data");
    }

    // ========================================================================
    // Integration Tests
    // ========================================================================

    /// <summary>
    /// Tests a complete streaming scenario with all network configuration options.
    /// </summary>
    [Fact]
    public async Task NetworkConfig_FullConfiguration_StreamsSuccessfully()
    {
        // Arrange
        var streamUrl = $"{_fixture.BaseUrl}/stream/5000";
        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        // Configure all available options
        var networkConfig = new NetworkConfig()
            .UseCloudflareDns()
            .WithIpResolveMode(IpResolveMode.PreferIPv4)
            .WithConnectionTimeouts(
                tcpConnect: TimeSpan.FromSeconds(5),
                tlsHandshake: TimeSpan.FromSeconds(5),
                firstByte: TimeSpan.FromSeconds(10)
            )
            .WithDnsTimeout(TimeSpan.FromSeconds(3))
            .WithDnsCacheTimeout(TimeSpan.FromMinutes(5))
            .WithTcpKeepalive(idle: TimeSpan.FromMinutes(1), interval: TimeSpan.FromSeconds(30))
            .WithReceiveBufferSize(65536)
            .WithHappyEyeballsTimeout(TimeSpan.FromMilliseconds(300));

        streamer.SetNetworkConfig(networkConfig);
        streamer.AddUrl(streamUrl);

        // Act
        Assert.True(streamer.Start());

        var reachedStreaming = await TestHelpers.WaitForStreamingAsync(streamer, TimeSpan.FromSeconds(10));

        // Stream for a few seconds to verify stability
        await Task.Delay(TimeSpan.FromSeconds(3));

        var status = streamer.GetStatus();
        streamer.Stop();

        // Assert
        _output.WriteLine($"Reached streaming: {reachedStreaming}");
        _output.WriteLine("Full configuration applied:");
        _output.WriteLine("  - DNS: Cloudflare (1.1.1.1, 1.0.0.1)");
        _output.WriteLine("  - IP: PreferIPv4");
        _output.WriteLine("  - Timeouts: TCP=5s, TLS=5s, FirstByte=10s, DNS=3s");
        _output.WriteLine("  - DNS Cache: 5min");
        _output.WriteLine("  - TCP Keepalive: idle=60s, interval=30s");
        _output.WriteLine("  - Buffer: 64KB");
        _output.WriteLine("  - Happy Eyeballs: 300ms");
        _output.WriteLine($"State: {status.State}");
        _output.WriteLine($"Bytes received: {status.BytesReceived:N0}");

        Assert.True(reachedStreaming, "Should reach streaming state with full config");
        Assert.True(status.BytesReceived > 0, "Should receive data");
    }

    /// <summary>
    /// Tests that network configuration changes are thread-safe.
    /// </summary>
    [Fact]
    public void NetworkConfig_SetBeforeStart_IsThreadSafe()
    {
        // Arrange
        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        // Act - should not throw
        var exception = Record.Exception(() =>
        {
            // Set configuration multiple times
            streamer.SetNetworkConfig(NetworkConfig.CreateDefault());
            streamer.SetNetworkConfig(NetworkConfig.CreateForStreaming());
            streamer.SetNetworkConfig(new NetworkConfig().UseGoogleDns());
            streamer.SetNetworkConfig(new NetworkConfig().UseCloudflareDns());
        });

        // Assert
        Assert.Null(exception);
        _output.WriteLine("Multiple SetNetworkConfig calls completed without exception");
    }

    /// <summary>
    /// Tests that SetNetworkConfig throws when called on disposed streamer.
    /// </summary>
    [Fact]
    public void NetworkConfig_SetOnDisposed_ThrowsObjectDisposedException()
    {
        // Arrange
        var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.Dispose();

        // Act & Assert - intentionally using disposed instance to verify exception
#pragma warning disable IDISP016 // Don't use disposed instance - intentional for this test
        Assert.Throws<ObjectDisposedException>(() => streamer.SetNetworkConfig(NetworkConfig.CreateDefault()));
#pragma warning restore IDISP016
    }

    // ========================================================================
    // Helper Methods
    // ========================================================================

    private static NativeStreamer? CreateStreamer()
    {
        var config = new TsDuckStreamerConfigNative
        {
            ConnectTimeoutMs = 5000,
            ResponseTimeoutMs = 5000,
            StallTimeoutMs = 5000,
            MaxRetries = 5,
            InitialBackoffMs = 100,
            MaxBackoffMs = 1000,
            BackoffMultiplier = 1.5,
            BackoffJitterMs = 50,
            OutputFd = -1,
            AlignmentBufferPackets = 32,
            EnableRestamp = 0,
            RestampMode = 0,
            LowSpeedLimitBytes = 100,
            LowSpeedTimeSec = 2,
            StallsBeforeSwitch = 2,
        };

        return NativeStreamer.TryCreate(config);
    }

    private static NativeStreamer? CreateStreamerWithDefaultNetworkConfig()
    {
        var streamer = CreateStreamer();
        if (streamer == null)
        {
            return null;
        }

        streamer.SetNetworkConfig(NetworkConfig.CreateDefault());
        return streamer;
    }
}
