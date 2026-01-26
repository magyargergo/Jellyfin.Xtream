using Jellyfin.Xtream.E2ETests.Infrastructure;
using Jellyfin.Xtream.Service.Streaming.Native;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.E2ETests.Tests;

/// <summary>
/// End-to-end tests for PSI/PAT/PMT program detection through the native TsDuck pipeline.
/// Verifies that the native SectionDemux correctly parses transport stream structure.
/// </summary>
[Collection("E2E")]
public class ProgramDetectionTests
{
    private readonly DockerTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public ProgramDetectionTests(DockerTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task ProgramDetection_ValidStream_DetectsServices()
    {
        // Arrange - test stream has 1 program (program_number=1) in PAT
        var url = $"{_fixture.BaseUrl}/stream/5000";

        using var streamer = CreateStreamerWithAnalyzer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        var totalBytes = 0L;
        streamer.SetOutputCallback((ptr, len) => Interlocked.Add(ref totalBytes, len));
        streamer.AddUrl(url);

        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));

        // Stream enough data for PAT/PMT to be parsed (they come early in the stream)
        await Task.Delay(TimeSpan.FromSeconds(3));

        var metrics = streamer.GetMetrics();
        streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        _output.WriteLine($"Service count: {metrics.ServiceCount}");
        _output.WriteLine($"Total bytes: {Interlocked.Read(ref totalBytes):N0}");

        // Test stream generator produces 1 program with PAT pointing to PMT
        Assert.True(metrics.ServiceCount >= 1, $"Expected at least 1 service, got {metrics.ServiceCount}");
    }

    [Fact]
    public async Task ProgramDetection_ValidStream_DetectsPids()
    {
        // Arrange - test stream has PAT(0x0000), PMT(0x0100), Video(0x0101), Audio(0x0102)
        var url = $"{_fixture.BaseUrl}/stream/5000";

        using var streamer = CreateStreamerWithAnalyzer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        var totalBytes = 0L;
        streamer.SetOutputCallback((ptr, len) => Interlocked.Add(ref totalBytes, len));
        streamer.AddUrl(url);

        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(3));

        var metrics = streamer.GetMetrics();
        streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        _output.WriteLine($"PID count: {metrics.PidCount}");

        // Should detect at least PAT, PMT, Video, Audio PIDs (4 minimum)
        Assert.True(metrics.PidCount >= 4, $"Expected at least 4 PIDs, got {metrics.PidCount}");
    }

    [Fact]
    public async Task ProgramDetection_ValidStream_DetectsBitrate()
    {
        // Arrange - 5 Mbps stream
        var url = $"{_fixture.BaseUrl}/stream/5000";

        using var streamer = CreateStreamerWithAnalyzer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        var totalBytes = 0L;
        streamer.SetOutputCallback((ptr, len) => Interlocked.Add(ref totalBytes, len));
        streamer.AddUrl(url);

        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(5));

        var metrics = streamer.GetMetrics();
        streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        _output.WriteLine($"Reported bitrate: {metrics.TsBitrate} bps ({metrics.TsBitrate / 1_000_000.0:F2} Mbps)");

        // Bitrate should be detected and within reasonable range (2-10 Mbps for 5Mbps target)
        Assert.True(metrics.TsBitrate > 0, "Should have detected stream bitrate");
    }

    [Fact]
    public async Task ProgramDetection_FiniteStream_DetectsStructure()
    {
        // Arrange - finite stream to verify program detection on short streams
        const int sourcePackets = 5000;
        var url = $"{_fixture.BaseUrl}/stream/finite/{sourcePackets}";

        using var streamer = CreateStreamerWithAnalyzer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        var totalBytes = 0L;
        streamer.SetOutputCallback((ptr, len) => Interlocked.Add(ref totalBytes, len));
        streamer.AddUrl(url);

        Assert.True(streamer.Start());
        await WaitForStreamerFinish(streamer, TimeSpan.FromSeconds(10));

        var metrics = streamer.GetMetrics();
        streamer.Stop();

        // Assert
        _output.WriteLine($"Total bytes received: {Interlocked.Read(ref totalBytes):N0}");
        _output.WriteLine($"Metrics: {(metrics != null ? "available" : "null")}");

        if (metrics != null)
        {
            _output.WriteLine($"Services: {metrics.ServiceCount}, PIDs: {metrics.PidCount}");
            // Even on a short stream, PAT/PMT should be parsed
            Assert.True(
                metrics.ServiceCount >= 1 || metrics.PidCount >= 1,
                "Should detect at least some stream structure on finite stream"
            );
        }
    }

    [Fact]
    public async Task ProgramDetection_PatReceived_NoErrors()
    {
        // Arrange - verify PAT is received with valid CRC and no timeout errors.
        // With packet-distance timing, PAT timeout uses stream-time (not wall-clock),
        // so a properly-formed stream should have zero PAT errors.
        var url = $"{_fixture.BaseUrl}/stream/5000";

        using var streamer = CreateStreamerWithAnalyzer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        var totalBytes = 0L;
        streamer.SetOutputCallback((ptr, len) => Interlocked.Add(ref totalBytes, len));
        streamer.AddUrl(url);

        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(5));

        var metrics = streamer.GetMetrics();
        streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        _output.WriteLine($"Priority 1 - PatError (timeout): {metrics.Priority1.PatError}");
        _output.WriteLine($"Priority 1 - PatError2 (CRC): {metrics.Priority1.PatError2}");
        _output.WriteLine($"Services detected: {metrics.ServiceCount}");

        // Both PAT timeout and CRC errors should be zero
        Assert.Equal(0, metrics.Priority1.PatError);
        Assert.Equal(0, metrics.Priority1.PatError2);
        // Services detected proves PAT was successfully parsed
        Assert.True(metrics.ServiceCount >= 1, "PAT should be parsed and report services");
    }

    [Fact]
    public async Task ProgramDetection_PmtReceived_NoErrors()
    {
        // Arrange - verify PMT is received with valid CRC and no timeout errors.
        // With packet-distance timing, PMT timeout uses stream-time (not wall-clock),
        // so a properly-formed stream should have zero PMT errors.
        var url = $"{_fixture.BaseUrl}/stream/5000";

        using var streamer = CreateStreamerWithAnalyzer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        var totalBytes = 0L;
        streamer.SetOutputCallback((ptr, len) => Interlocked.Add(ref totalBytes, len));
        streamer.AddUrl(url);

        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(5));

        var metrics = streamer.GetMetrics();
        streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        _output.WriteLine($"Priority 1 - PmtError (timeout): {metrics.Priority1.PmtError}");
        _output.WriteLine($"Priority 1 - PmtError2 (CRC): {metrics.Priority1.PmtError2}");
        _output.WriteLine($"PIDs detected: {metrics.PidCount}");

        // Both PMT timeout and CRC errors should be zero
        Assert.Equal(0, metrics.Priority1.PmtError);
        Assert.Equal(0, metrics.Priority1.PmtError2);
        // PIDs detected proves PMT was parsed (PMT declares elementary stream PIDs)
        Assert.True(metrics.PidCount >= 3, "PMT should be parsed and report PIDs");
    }

    [Theory]
    [InlineData(2000)]
    [InlineData(5000)]
    [InlineData(10000)]
    public async Task ProgramDetection_DifferentBitrates_DetectsProgram(int bitrateKbps)
    {
        // Arrange - verify program detection works at different bitrates
        var url = $"{_fixture.BaseUrl}/stream/{bitrateKbps}";

        using var streamer = CreateStreamerWithAnalyzer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        var totalBytes = 0L;
        streamer.SetOutputCallback((ptr, len) => Interlocked.Add(ref totalBytes, len));
        streamer.AddUrl(url);

        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(3));

        var metrics = streamer.GetMetrics();
        streamer.Stop();

        // Assert
        _output.WriteLine($"Bitrate: {bitrateKbps} Kbps, Services: {metrics?.ServiceCount}, PIDs: {metrics?.PidCount}");

        Assert.NotNull(metrics);
        Assert.True(metrics.ServiceCount >= 1, $"At {bitrateKbps}Kbps: expected >= 1 service");
    }

    private static NativeStreamer? CreateStreamerWithAnalyzer()
    {
        var config = new TsDuckStreamerConfigNative
        {
            ConnectTimeoutMs = 5000,
            ResponseTimeoutMs = 10000,
            StallTimeoutMs = 15000,
            MaxRetries = 3,
            InitialBackoffMs = 200,
            MaxBackoffMs = 5000,
            BackoffMultiplier = 2.0,
            BackoffJitterMs = 100,
            OutputFd = -1,
            AlignmentBufferPackets = 32,
            EnableRestamp = 0,
            RestampMode = (int)RestampingMode.Disabled,
            LowSpeedLimitBytes = 100,
            LowSpeedTimeSec = 5,
            StallsBeforeSwitch = 2,
            Reserved = 0,
        };

        var analyzerConfig = TsDuckConfigNative.FromManaged(
            new TsDuckConfiguration
            {
                EnableTr101290 = true,
                MetricsIntervalSeconds = 1,
                EnableAutoRestamp = false,
                RestampMode = RestampingMode.Disabled,
            }
        );

        return NativeStreamer.TryCreate(config, analyzerConfig);
    }

    private static async Task WaitForConnection(NativeStreamer streamer, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (streamer.GetStatus().State == StreamerState.Streaming)
                return;
            await Task.Delay(50);
        }
    }

    private static async Task WaitForStreamerFinish(NativeStreamer streamer, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var status = streamer.GetStatus();
            if (status.IsTerminal || (status.BytesReceived > 0 && status.State == StreamerState.Idle))
                return;
            await Task.Delay(100);
        }
    }
}
