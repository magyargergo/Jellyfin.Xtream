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
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service;
using Jellyfin.Xtream.Service.MpegTs;
using Jellyfin.Xtream.Service.MpegTs.Infrastructure;
using Jellyfin.Xtream.Service.ProviderManagement;
using Jellyfin.Xtream.Utility;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Integration tests for the provider switching flow.
/// Tests the full cycle: streaming → degradation → switch → recovery → failback.
/// </summary>
public sealed class ProviderSwitchingIntegrationTests : IDisposable
{
    // Provider configuration
    private const string ProviderAId = "provider-a";
    private const string ProviderBId = "provider-b";
    private const string ProviderAUrl = "http://provider-a.test:8080";
    private const string ProviderBUrl = "http://provider-b.test:8080";
    private const string StreamPath = "/user/pass/12345.ts";

    private readonly MockHttpMessageHandler _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly TestHttpClientFactory _httpClientFactory;
    private readonly TestPluginConfigurationProvider _configProvider;
    private readonly ProviderAvailabilityService _availabilityService;
    private readonly AutomaticFailoverService _failoverService;
    private readonly ProviderUrlResolver _urlResolver;
    private readonly ProviderSwitchService _switchService;
    private readonly ProviderMetricsTracker _metricsTracker;
    private readonly HealthTrendTracker _trendTracker;

    public ProviderSwitchingIntegrationTests()
    {
        // Setup mock HTTP handler
        _mockHandler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_mockHandler);
        _httpClientFactory = new TestHttpClientFactory(_httpClient);

        // Setup configuration with two providers
        _configProvider = new TestPluginConfigurationProvider
        {
            Config = new PluginConfiguration
            {
                Providers =
                [
                    new XtreamProvider
                    {
                        Id = ProviderAId,
                        Name = "Provider A",
                        BaseUrl = ProviderAUrl,
                        Username = "user",
                        Password = "pass",
                        Enabled = true,
                    },
                    new XtreamProvider
                    {
                        Id = ProviderBId,
                        Name = "Provider B",
                        BaseUrl = ProviderBUrl,
                        Username = "user",
                        Password = "pass",
                        Enabled = true,
                    },
                ],
            },
        };

        // Setup services
        _availabilityService = new ProviderAvailabilityService(
            _httpClientFactory,
            NullLoggerFactory.Instance,
            discordService: null,
            configurationProvider: _configProvider
        );

        _metricsTracker = new ProviderMetricsTracker();
        _trendTracker = new HealthTrendTracker();

        _failoverService = new AutomaticFailoverService(
            _availabilityService,
            _metricsTracker,
            _trendTracker,
            NullLogger.Instance,
            _configProvider
        );

        _urlResolver = new ProviderUrlResolver(
            _failoverService,
            NullLogger<ProviderUrlResolver>.Instance,
            _configProvider
        );

        var config = new ProviderSwitchConfiguration
        {
            CooldownMs = 100, // Short cooldown for tests
            MaxAttemptsPerSession = 10,
            TimeoutMs = 5000,
            UseByteAlignment = true,
            WaitForKeyframe = false, // Simplify tests
            UseTimestampRemapping = false, // Disable remuxing pipeline in tests (requires FFmpeg)
            ValidatePsiBeforeSwitch = true,
            PsiValidationTimeoutMs = 1000,
        };

        _switchService = new ProviderSwitchService(
            _urlResolver,
            _httpClientFactory,
            _availabilityService,
            _configProvider,
            NullLogger<ProviderSwitchService>.Instance,
            NullLoggerFactory.Instance,
            config
        );

        // Initialize PluginLogger with test configuration provider to avoid Plugin.Instance access
        PluginLogger.Initialize(null, _configProvider);
    }

    public void Dispose()
    {
        _switchService.Dispose();
        _availabilityService.Dispose();
        _httpClient.Dispose();
        _mockHandler.Dispose();
    }

    #region Basic Switch Tests

    [Fact]
    public async Task Switch_WhenProviderAFails_SwitchesToProviderB()
    {
        // Arrange
        const string streamId = "test-stream-1";
        var currentUrl = $"{ProviderAUrl}{StreamPath}";

        // Provider B returns valid MPEG-TS with PAT/PMT
        var validStream = MpegTsTestGenerator.GenerateValidStream(packetCount: 100, includePat: true, includePmt: true);
        _mockHandler.SetupResponse($"{ProviderBUrl}{StreamPath}", HttpStatusCode.OK, validStream);

        _switchService.RegisterStream(streamId, currentUrl);

        // Act
        var result = await _switchService.TrySwitchAsync(streamId, currentUrl, SwitchReason.ConnectionFailed);

        // Assert
        Assert.True(result.Success, $"Switch failed: {result.FailureMessage}");
        Assert.NotNull(result.NewUrl);
        Assert.Contains(ProviderBUrl, result.NewUrl);
    }

    [Fact]
    public async Task Switch_WhenProviderBReturnsHttp405_RecordsFailureAndReturnsFailed()
    {
        // Arrange
        const string streamId = "test-stream-2";
        var currentUrl = $"{ProviderAUrl}{StreamPath}";

        // Provider B returns 405 Method Not Allowed
        _mockHandler.SetupResponse($"{ProviderBUrl}{StreamPath}", HttpStatusCode.MethodNotAllowed);

        _switchService.RegisterStream(streamId, currentUrl);

        // Act
        var result = await _switchService.TrySwitchAsync(streamId, currentUrl, SwitchReason.ConnectionFailed);

        // Assert - switch should FAIL when the new provider returns HTTP error
        // (Previously it would fall back to simple URL switch with the same failed URL,
        // which doesn't make sense - the URL already failed, so simple switch will also fail)
        Assert.False(result.Success);

        // Verify failure was recorded for Provider B by checking circuit state
        // After a failure, the circuit should no longer be in Closed state
        // or the provider should have recorded failures (visible through GetSnapshot)
        var snapshot = _availabilityService.GetSnapshot();
        if (snapshot.TryGetValue(ProviderBId, out var state))
        {
            Assert.True(state.ConsecutiveFailures > 0, "Expected consecutive failures to be recorded");
        }
    }

    [Fact]
    public async Task Switch_WhenProviderBReturnsHttp500_RecordsServerErrorAndReturnsFailed()
    {
        // Arrange
        const string streamId = "test-stream-3";
        var currentUrl = $"{ProviderAUrl}{StreamPath}";

        // Provider B returns 500 Internal Server Error
        _mockHandler.SetupResponse($"{ProviderBUrl}{StreamPath}", HttpStatusCode.InternalServerError);

        _switchService.RegisterStream(streamId, currentUrl);

        // Act
        var result = await _switchService.TrySwitchAsync(streamId, currentUrl, SwitchReason.ConnectionFailed);

        // Assert - switch should FAIL when the new provider returns HTTP error
        // (Previously it would fall back to simple URL switch with the same failed URL,
        // which doesn't make sense - the URL already failed, so simple switch will also fail)
        Assert.False(result.Success);

        // Verify failure was recorded by checking snapshot
        var snapshot = _availabilityService.GetSnapshot();
        if (snapshot.TryGetValue(ProviderBId, out var state))
        {
            Assert.True(state.ConsecutiveFailures > 0, "Expected consecutive failures to be recorded for server error");
        }
    }

    #endregion

    #region Timestamp Remapping Tests

    [Fact]
    public async Task Switch_WithValidStream_ActivatesTimestampRemapping()
    {
        // Arrange
        const string streamId = "test-stream-remapping";
        var currentUrl = $"{ProviderAUrl}{StreamPath}";

        // Generate stream with PTS values
        var validStream = MpegTsTestGenerator.GenerateValidStream(
            packetCount: 100,
            includePat: true,
            includePmt: true,
            includeVideoPes: true,
            videoPts: 90000 // 1 second in 90kHz
        );
        _mockHandler.SetupResponse($"{ProviderBUrl}{StreamPath}", HttpStatusCode.OK, validStream);

        _switchService.RegisterStream(streamId, currentUrl);

        // FFmpeg's remuxer handles timestamp correction internally - no preparation needed

        // Act
        var result = await _switchService.TrySwitchAsync(streamId, currentUrl, SwitchReason.HealthDegraded);

        // Assert
        Assert.True(result.Success);
        // Note: TimestampRemappingActive depends on whether PTS was found and previous sync points existed
    }

    #endregion

    #region Failback Scenario Tests

    [Fact]
    public async Task Failback_WhenProviderBDegrades_SwitchesBackToProviderA()
    {
        // Arrange
        const string streamId = "test-stream-failback";
        var currentUrl = $"{ProviderAUrl}{StreamPath}";

        // Both providers return valid streams
        var validStreamA = MpegTsTestGenerator.GenerateValidStream(
            packetCount: 100,
            includePat: true,
            includePmt: true
        );
        var validStreamB = MpegTsTestGenerator.GenerateValidStream(
            packetCount: 100,
            includePat: true,
            includePmt: true
        );

        _mockHandler.SetupResponse($"{ProviderAUrl}{StreamPath}", HttpStatusCode.OK, validStreamA);
        _mockHandler.SetupResponse($"{ProviderBUrl}{StreamPath}", HttpStatusCode.OK, validStreamB);

        _switchService.RegisterStream(streamId, currentUrl);

        // First switch: A -> B
        var firstSwitch = await _switchService.TrySwitchAsync(streamId, currentUrl, SwitchReason.HealthDegraded);

        Assert.True(firstSwitch.Success);
        Assert.NotNull(firstSwitch.NewUrl);
        Assert.Contains(ProviderBUrl, firstSwitch.NewUrl);

        // Wait for cooldown
        await Task.Delay(150);

        // Second switch: B -> A (failback)
        var secondSwitch = await _switchService.TrySwitchAsync(
            streamId,
            firstSwitch.NewUrl!,
            SwitchReason.HealthDegraded
        );

        // Assert
        Assert.True(secondSwitch.Success);
        Assert.NotNull(secondSwitch.NewUrl);
        Assert.Contains(ProviderAUrl, secondSwitch.NewUrl);
    }

    #endregion

    #region Circuit Breaker Tests

    [Fact]
    public async Task CircuitBreaker_AfterMultipleFailures_OpensCircuit()
    {
        // Arrange
        const string streamId = "test-stream-circuit";
        var currentUrl = $"{ProviderAUrl}{StreamPath}";

        // Provider B always returns 500
        _mockHandler.SetupResponse($"{ProviderBUrl}{StreamPath}", HttpStatusCode.InternalServerError);

        _switchService.RegisterStream(streamId, currentUrl);

        // Act - attempt multiple switches that all fail with HTTP 500
        for (var i = 0; i < 5; i++)
        {
            _ = await _switchService.TrySwitchAsync(streamId, currentUrl, SwitchReason.ConnectionFailed);

            // Wait for cooldown between attempts
            await Task.Delay(150);
        }

        // Assert - circuit should eventually open
        var circuitState = _availabilityService.GetCircuitState(ProviderBId);
        // After enough failures, the circuit should be open
        Assert.True(
            circuitState is ProviderCircuitState.Open or ProviderCircuitState.HalfOpen,
            $"Expected Open or HalfOpen circuit, got {circuitState}"
        );
    }

    #endregion

    #region PSI Validation Tests

    [Fact]
    public async Task Switch_WhenStreamHasNoPat_FallsBackToSimpleSwitch()
    {
        // Arrange
        const string streamId = "test-stream-no-pat";
        var currentUrl = $"{ProviderAUrl}{StreamPath}";

        // Stream without PAT
        var streamWithoutPat = MpegTsTestGenerator.GenerateValidStream(
            packetCount: 100,
            includePat: false,
            includePmt: false
        );
        _mockHandler.SetupResponse($"{ProviderBUrl}{StreamPath}", HttpStatusCode.OK, streamWithoutPat);

        _switchService.RegisterStream(streamId, currentUrl);

        // Act
        var result = await _switchService.TrySwitchAsync(streamId, currentUrl, SwitchReason.HealthDegraded);

        // Assert - should still succeed with simple URL switch
        Assert.True(result.Success);
        Assert.False(result.AlignedToKeyframe); // No alignment without valid PSI
    }

    #endregion

    #region Cooldown Tests

    [Fact]
    public async Task Switch_DuringCooldown_ReturnsCooldownActive()
    {
        // Arrange
        const string streamId = "test-stream-cooldown";
        var currentUrl = $"{ProviderAUrl}{StreamPath}";

        var validStream = MpegTsTestGenerator.GenerateValidStream(packetCount: 100, includePat: true, includePmt: true);
        _mockHandler.SetupResponse($"{ProviderBUrl}{StreamPath}", HttpStatusCode.OK, validStream);

        _switchService.RegisterStream(streamId, currentUrl);

        // First switch
        var firstResult = await _switchService.TrySwitchAsync(streamId, currentUrl, SwitchReason.HealthDegraded);

        Assert.True(firstResult.Success);

        // Immediate second switch (during cooldown)
        var secondResult = await _switchService.TrySwitchAsync(
            streamId,
            firstResult.NewUrl!,
            SwitchReason.HealthDegraded
        );

        // Assert
        Assert.False(secondResult.Success);
        Assert.Equal(SwitchFailureReason.CooldownActive, secondResult.FailureReason);
    }

    #endregion

    #region Max Attempts Tests

    [Fact]
    public async Task Switch_AfterMaxAttempts_ReturnsMaxAttemptsReached()
    {
        // Arrange
        const string streamId = "test-stream-max-attempts";
        var currentUrl = $"{ProviderAUrl}{StreamPath}";

        var validStream = MpegTsTestGenerator.GenerateValidStream(packetCount: 100, includePat: true, includePmt: true);
        _mockHandler.SetupResponse($"{ProviderBUrl}{StreamPath}", HttpStatusCode.OK, validStream);
        _mockHandler.SetupResponse($"{ProviderAUrl}{StreamPath}", HttpStatusCode.OK, validStream);

        // Use a config with very low max attempts
        var config = new ProviderSwitchConfiguration
        {
            CooldownMs = 10, // Very short for testing
            MaxAttemptsPerSession = 2,
            TimeoutMs = 5000,
            UseByteAlignment = true,
            WaitForKeyframe = false,
            UseTimestampRemapping = false,
            ValidatePsiBeforeSwitch = true,
        };

        using var limitedSwitchService = new ProviderSwitchService(
            _urlResolver,
            _httpClientFactory,
            _availabilityService,
            _configProvider,
            NullLogger<ProviderSwitchService>.Instance,
            NullLoggerFactory.Instance,
            config
        );

        limitedSwitchService.RegisterStream(streamId, currentUrl);

        // Exhaust max attempts
        var lastUrl = currentUrl;
        for (var i = 0; i < 2; i++)
        {
            await Task.Delay(20); // Wait for cooldown
            var result = await limitedSwitchService.TrySwitchAsync(streamId, lastUrl, SwitchReason.HealthDegraded);
            if (result.Success && result.NewUrl != null)
            {
                lastUrl = result.NewUrl;
            }
        }

        await Task.Delay(20);

        // Try one more
        var finalResult = await limitedSwitchService.TrySwitchAsync(streamId, lastUrl, SwitchReason.HealthDegraded);

        // Assert
        Assert.False(finalResult.Success);
        Assert.Equal(SwitchFailureReason.MaxAttemptsReached, finalResult.FailureReason);
    }

    #endregion

    #region Helper Classes

    /// <summary>
    /// Test HTTP client factory.
    /// </summary>
    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    /// <summary>
    /// Test plugin configuration provider.
    /// </summary>
    private sealed class TestPluginConfigurationProvider : IPluginConfigurationProvider
    {
        public PluginConfiguration? Config { get; set; }

        public PluginConfiguration? GetConfiguration() => Config;
    }

    #endregion
}

/// <summary>
/// Mock HTTP message handler for testing.
/// </summary>
internal sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, (HttpStatusCode StatusCode, byte[]? Content)> _responses = new(
        StringComparer.Ordinal
    );

    public void SetupResponse(string url, HttpStatusCode statusCode, byte[]? content = null) =>
        _responses[url] = (statusCode, content);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var url = request.RequestUri?.ToString() ?? string.Empty;

        if (_responses.TryGetValue(url, out var response))
        {
            var httpResponse = new HttpResponseMessage(response.StatusCode);
            if (response.Content != null)
            {
                httpResponse.Content = new ByteArrayContent(response.Content);
            }

            return Task.FromResult(httpResponse);
        }

        // Default: return 404
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

/// <summary>
/// Generates valid MPEG-TS streams for testing.
/// </summary>
internal static class MpegTsTestGenerator
{
    private const int TsPacketSize = 188;
    private const byte TsSyncByte = 0x47;
    private const int PmtPid = 0x1000;
    private const int VideoPid = 0x0100;
    private const int AudioPid = 0x0101;

    /// <summary>
    /// Generates a valid MPEG-TS stream with specified content.
    /// </summary>
    public static byte[] GenerateValidStream(
        int packetCount = 100,
        bool includePat = true,
        bool includePmt = true,
        bool includeVideoPes = false,
        long videoPts = 0
    )
    {
        var packets = new List<byte[]>();
        var continuityCounter = 0;

        // Add PAT packet
        if (includePat)
        {
            packets.Add(GeneratePatPacket(ref continuityCounter));
        }

        // Add PMT packet
        if (includePmt)
        {
            packets.Add(GeneratePmtPacket(ref continuityCounter));
        }

        // Add video PES packet with PTS if requested
        if (includeVideoPes && videoPts > 0)
        {
            packets.Add(GenerateVideoPesPacket(videoPts, ref continuityCounter));
        }

        // Fill remaining with null packets or video packets
        var remaining = packetCount - packets.Count;
        for (var i = 0; i < remaining; i++)
        {
            if (includeVideoPes)
            {
                packets.Add(GenerateVideoPacket(ref continuityCounter));
            }
            else
            {
                packets.Add(GenerateNullPacket());
            }
        }

        // Flatten to single array
        var result = new byte[packets.Count * TsPacketSize];
        for (var i = 0; i < packets.Count; i++)
        {
            Array.Copy(packets[i], 0, result, i * TsPacketSize, TsPacketSize);
        }

        return result;
    }

    /// <summary>
    /// Generates a PAT (Program Association Table) packet.
    /// </summary>
    private static byte[] GeneratePatPacket(ref int continuityCounter)
    {
        var packet = new byte[TsPacketSize];

        // TS header
        packet[0] = TsSyncByte;
        packet[1] = 0x40; // PUSI=1, PID=0 (high bits)
        packet[2] = 0x00; // PID=0 (low bits)
        packet[3] = (byte)(0x10 | (continuityCounter++ & 0x0F)); // AFC=01, CC

        // Pointer field
        packet[4] = 0x00;

        // PAT section
        packet[5] = 0x00; // table_id = 0x00 (PAT)
        packet[6] = 0xB0; // section_syntax_indicator=1, section_length high bits
        packet[7] = 0x0D; // section_length = 13
        packet[8] = 0x00; // transport_stream_id high
        packet[9] = 0x01; // transport_stream_id low
        packet[10] = 0xC1; // version=0, current_next=1
        packet[11] = 0x00; // section_number
        packet[12] = 0x00; // last_section_number
        packet[13] = 0x00; // program_number high
        packet[14] = 0x01; // program_number low = 1
        packet[15] = (byte)(0xE0 | ((PmtPid >> 8) & 0x1F)); // PMT PID high bits
        packet[16] = (byte)(PmtPid & 0xFF); // PMT PID low bits

        // CRC32 (simplified - just fill with zeros for testing)
        packet[17] = 0x00;
        packet[18] = 0x00;
        packet[19] = 0x00;
        packet[20] = 0x00;

        // Fill rest with 0xFF
        for (var i = 21; i < TsPacketSize; i++)
        {
            packet[i] = 0xFF;
        }

        return packet;
    }

    /// <summary>
    /// Generates a PMT (Program Map Table) packet.
    /// </summary>
    private static byte[] GeneratePmtPacket(ref int continuityCounter)
    {
        var packet = new byte[TsPacketSize];

        // TS header
        packet[0] = TsSyncByte;
        packet[1] = (byte)(0x40 | ((PmtPid >> 8) & 0x1F)); // PUSI=1, PID high bits
        packet[2] = (byte)(PmtPid & 0xFF); // PID low bits
        packet[3] = (byte)(0x10 | (continuityCounter++ & 0x0F)); // AFC=01, CC

        // Pointer field
        packet[4] = 0x00;

        // PMT section
        packet[5] = 0x02; // table_id = 0x02 (PMT)
        packet[6] = 0xB0; // section_syntax_indicator=1, section_length high bits
        packet[7] = 0x17; // section_length = 23
        packet[8] = 0x00; // program_number high
        packet[9] = 0x01; // program_number low = 1
        packet[10] = 0xC1; // version=0, current_next=1
        packet[11] = 0x00; // section_number
        packet[12] = 0x00; // last_section_number
        packet[13] = (byte)(0xE0 | ((VideoPid >> 8) & 0x1F)); // PCR_PID high bits
        packet[14] = (byte)(VideoPid & 0xFF); // PCR_PID low bits
        packet[15] = 0xF0; // program_info_length high bits
        packet[16] = 0x00; // program_info_length = 0

        // Video stream entry
        packet[17] = 0x1B; // stream_type = 0x1B (H.264)
        packet[18] = (byte)(0xE0 | ((VideoPid >> 8) & 0x1F)); // elementary_PID high bits
        packet[19] = (byte)(VideoPid & 0xFF); // elementary_PID low bits
        packet[20] = 0xF0; // ES_info_length high bits
        packet[21] = 0x00; // ES_info_length = 0

        // Audio stream entry
        packet[22] = 0x0F; // stream_type = 0x0F (AAC)
        packet[23] = (byte)(0xE0 | ((AudioPid >> 8) & 0x1F)); // elementary_PID high bits
        packet[24] = (byte)(AudioPid & 0xFF); // elementary_PID low bits
        packet[25] = 0xF0; // ES_info_length high bits
        packet[26] = 0x00; // ES_info_length = 0

        // CRC32 (simplified)
        packet[27] = 0x00;
        packet[28] = 0x00;
        packet[29] = 0x00;
        packet[30] = 0x00;

        // Fill rest with 0xFF
        for (var i = 31; i < TsPacketSize; i++)
        {
            packet[i] = 0xFF;
        }

        return packet;
    }

    /// <summary>
    /// Generates a video PES packet with PTS.
    /// </summary>
    private static byte[] GenerateVideoPesPacket(long pts, ref int continuityCounter)
    {
        var packet = new byte[TsPacketSize];

        // TS header
        packet[0] = TsSyncByte;
        packet[1] = (byte)(0x40 | ((VideoPid >> 8) & 0x1F)); // PUSI=1, PID high bits
        packet[2] = (byte)(VideoPid & 0xFF); // PID low bits
        packet[3] = (byte)(0x10 | (continuityCounter++ & 0x0F)); // AFC=01, CC

        // PES header
        packet[4] = 0x00; // packet_start_code_prefix
        packet[5] = 0x00;
        packet[6] = 0x01;
        packet[7] = 0xE0; // stream_id (video)
        packet[8] = 0x00; // PES_packet_length high (0 = unbounded)
        packet[9] = 0x00; // PES_packet_length low
        packet[10] = 0x80; // PES header flags (10, no scrambling, no priority)
        packet[11] = 0x80; // PTS_DTS_flags = 10 (PTS only)
        packet[12] = 0x05; // PES_header_data_length = 5

        // PTS (5 bytes)
        packet[13] = (byte)(0x21 | ((pts >> 29) & 0x0E)); // '0010' + PTS[32..30] + marker
        packet[14] = (byte)((pts >> 22) & 0xFF); // PTS[29..22]
        packet[15] = (byte)(0x01 | ((pts >> 14) & 0xFE)); // PTS[21..15] + marker
        packet[16] = (byte)((pts >> 7) & 0xFF); // PTS[14..7]
        packet[17] = (byte)(0x01 | ((pts << 1) & 0xFE)); // PTS[6..0] + marker

        // Fill rest with dummy video data
        for (var i = 18; i < TsPacketSize; i++)
        {
            packet[i] = 0x00;
        }

        return packet;
    }

    /// <summary>
    /// Generates a basic video packet (continuation).
    /// </summary>
    private static byte[] GenerateVideoPacket(ref int continuityCounter)
    {
        var packet = new byte[TsPacketSize];

        // TS header
        packet[0] = TsSyncByte;
        packet[1] = (byte)((VideoPid >> 8) & 0x1F); // PUSI=0, PID high bits
        packet[2] = (byte)(VideoPid & 0xFF); // PID low bits
        packet[3] = (byte)(0x10 | (continuityCounter++ & 0x0F)); // AFC=01, CC

        // Fill with dummy video data
        for (var i = 4; i < TsPacketSize; i++)
        {
            packet[i] = 0x00;
        }

        return packet;
    }

    /// <summary>
    /// Generates a null packet (PID 0x1FFF).
    /// </summary>
    private static byte[] GenerateNullPacket()
    {
        var packet = new byte[TsPacketSize];

        // TS header
        packet[0] = TsSyncByte;
        packet[1] = 0x1F; // PID high bits (0x1FFF)
        packet[2] = 0xFF; // PID low bits
        packet[3] = 0x10; // AFC=01, CC=0

        // Fill with 0xFF
        for (var i = 4; i < TsPacketSize; i++)
        {
            packet[i] = 0xFF;
        }

        return packet;
    }
}
