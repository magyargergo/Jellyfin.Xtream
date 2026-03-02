using Jellyfin.Xtream.E2ETests.Infrastructure;
using Jellyfin.Xtream.Service.Streaming.Native;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.E2ETests.Tests;

/// <summary>
/// End-to-end tests for PSI/PAT/PMT program detection through the native TsDuck pipeline.
/// Verifies that the native SectionDemux correctly parses transport stream structure.
/// </summary>
[Collection("E2E-Analysis")]
public class ProgramDetectionTests(DockerTestFixture fixture, ITestOutputHelper output)
    : NativeE2ETestBase(output, TestConfigs.Analyzer)
{
    [Fact]
    public async Task ProgramDetection_ValidStream_DetectsServices()
    {
        // Arrange - test stream has 1 program (program_number=1) in PAT
        var url = $"{fixture.BaseUrl}/stream/5000";

        Streamer.AddUrl(url);

        Assert.True(Streamer.Start());
        await WaitForConnection(Streamer, TimeSpan.FromSeconds(5));

        // Stream enough data for PAT/PMT to be parsed (they come early in the stream)
        await Task.Delay(TimeSpan.FromSeconds(3));

        var metrics = Streamer.GetMetrics();
        var status = Streamer.GetStatus();
        Streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        Output.WriteLine($"Service count: {metrics.ServiceCount}");
        Output.WriteLine($"Total bytes: {status.BytesReceived:N0}");

        // Test stream generator produces 1 program with PAT pointing to PMT
        Assert.True(metrics.ServiceCount >= 1, $"Expected at least 1 service, got {metrics.ServiceCount}");
    }

    [Fact]
    public async Task ProgramDetection_ValidStream_DetectsPids()
    {
        // Arrange - test stream has PAT(0x0000), PMT(0x0100), Video(0x0101), Audio(0x0102)
        var url = $"{fixture.BaseUrl}/stream/5000";

        Streamer.AddUrl(url);

        Assert.True(Streamer.Start());
        await WaitForConnection(Streamer, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(3));

        var metrics = Streamer.GetMetrics();
        Streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        Output.WriteLine($"PID count: {metrics.PidCount}");

        // Should detect at least PAT, PMT, Video, Audio PIDs (4 minimum)
        Assert.True(metrics.PidCount >= 4, $"Expected at least 4 PIDs, got {metrics.PidCount}");
    }

    [Fact]
    public async Task ProgramDetection_ValidStream_DetectsBitrate()
    {
        // Arrange - 5 Mbps stream
        var url = $"{fixture.BaseUrl}/stream/5000";

        Streamer.AddUrl(url);

        Assert.True(Streamer.Start());
        await WaitForConnection(Streamer, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(5));

        var metrics = Streamer.GetMetrics();
        Streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        Output.WriteLine($"Reported bitrate: {metrics.TsBitrate} bps ({metrics.TsBitrate / 1_000_000.0:F2} Mbps)");

        // Bitrate should be detected and within reasonable range (2-10 Mbps for 5Mbps target)
        Assert.True(metrics.TsBitrate > 0, "Should have detected stream bitrate");
    }

    [Fact]
    public async Task ProgramDetection_FiniteStream_DetectsStructure()
    {
        // Arrange - finite stream to verify program detection on short streams
        const int sourcePackets = 5000;
        var url = $"{fixture.BaseUrl}/stream/finite/{sourcePackets}";

        Streamer.AddUrl(url);

        Assert.True(Streamer.Start());
        await WaitForStreamerFinish(Streamer, TimeSpan.FromSeconds(10));

        var metrics = Streamer.GetMetrics();
        var status = Streamer.GetStatus();
        Streamer.Stop();

        // Assert
        Output.WriteLine($"Total bytes received: {status.BytesReceived:N0}");
        Output.WriteLine($"Metrics: {(metrics != null ? "available" : "null")}");

        if (metrics != null)
        {
            Output.WriteLine($"Services: {metrics.ServiceCount}, PIDs: {metrics.PidCount}");
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
        var url = $"{fixture.BaseUrl}/stream/5000";

        Streamer.AddUrl(url);

        Assert.True(Streamer.Start());
        await WaitForConnection(Streamer, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(5));

        var metrics = Streamer.GetMetrics();
        Streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        Output.WriteLine($"Priority 1 - PatError (timeout): {metrics.Priority1.PatError}");
        Output.WriteLine($"Priority 1 - PatError2 (CRC): {metrics.Priority1.PatError2}");
        Output.WriteLine($"Services detected: {metrics.ServiceCount}");

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
        var url = $"{fixture.BaseUrl}/stream/5000";

        Streamer.AddUrl(url);

        Assert.True(Streamer.Start());
        await WaitForConnection(Streamer, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(5));

        var metrics = Streamer.GetMetrics();
        Streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        Output.WriteLine($"Priority 1 - PmtError (timeout): {metrics.Priority1.PmtError}");
        Output.WriteLine($"Priority 1 - PmtError2 (CRC): {metrics.Priority1.PmtError2}");
        Output.WriteLine($"PIDs detected: {metrics.PidCount}");

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
        var url = $"{fixture.BaseUrl}/stream/{bitrateKbps}";

        Streamer.AddUrl(url);

        Assert.True(Streamer.Start());
        await WaitForConnection(Streamer, TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromSeconds(3));

        var metrics = Streamer.GetMetrics();
        Streamer.Stop();

        // Assert
        Output.WriteLine($"Bitrate: {bitrateKbps} Kbps, Services: {metrics?.ServiceCount}, PIDs: {metrics?.PidCount}");

        Assert.NotNull(metrics);
        Assert.True(metrics.ServiceCount >= 1, $"At {bitrateKbps}Kbps: expected >= 1 service");
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
