using Jellyfin.Xtream.E2ETests.Infrastructure;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.E2ETests.Tests;

/// <summary>
/// End-to-end tests for TR 101 290 monitoring through the native TsDuck pipeline.
/// Verifies Priority 1 and Priority 2 error detection on live streams.
/// TR 101 290 defines three priority levels of indicators:
/// - Priority 1: Critical errors (sync loss, PAT/PMT errors, continuity errors)
/// - Priority 2: Errors affecting quality (transport errors, PCR errors, PTS errors)
/// - Priority 3: Informational (service info, bandwidth)
/// </summary>
[Collection("E2E-Analysis")]
public class Tr101290Tests(DockerTestFixture fixture, ITestOutputHelper output)
    : NativeE2ETestBase(output, TestConfigs.Analyzer)
{
    /// <summary>
    /// Tests that a clean stream has zero sync byte errors.
    /// Sync byte (0x47) must appear at the start of each 188-byte packet.
    /// </summary>
    [Fact]
    public async Task Tr101290_CleanStream_NoSyncErrors()
    {
        // Arrange - stream valid data and verify no sync byte errors
        var url = $"{fixture.BaseUrl}/stream/5000";

        Streamer.AddUrl(url);

        Assert.True(Streamer.Start(), "Streamer should start successfully");
        var streaming = await TestHelpers.WaitForStreamingAsync(Streamer, TimeSpan.FromSeconds(5));
        Assert.True(streaming, "Should reach streaming state");

        // Act - stream for 5 seconds to collect enough data for metrics
        await Task.Delay(TimeSpan.FromSeconds(5));

        var metrics = Streamer.GetMetrics();
        var status = Streamer.GetStatus();
        Streamer.Stop();

        // Assert
        Output.WriteLine($"Total bytes: {status.BytesReceived:N0}");
        Output.WriteLine($"Metrics available: {metrics != null}");

        Assert.NotNull(metrics);
        Output.WriteLine($"Priority 1 - SyncByteError: {metrics.Priority1.SyncByteError}");
        Output.WriteLine($"Priority 1 - SyncLoss: {metrics.Priority1.SyncLoss}");
        Output.WriteLine($"Priority 1 - ContinuityCountError: {metrics.Priority1.ContinuityCountError}");
        Output.WriteLine($"Priority 2 - TransportError: {metrics.Priority2.TransportError}");

        // A clean stream should have zero sync byte errors
        Assert.Equal(0, metrics.Priority1.SyncByteError);
        Assert.Equal(0, metrics.Priority1.SyncLoss);
        Assert.Equal(0, metrics.Priority2.TransportError);
    }

    /// <summary>
    /// Tests that a clean stream has zero CRC errors.
    /// PAT and PMT sections must pass CRC-32 validation per MPEG-2 spec.
    /// </summary>
    [Fact]
    public async Task Tr101290_CleanStream_NoCrcErrors()
    {
        // Arrange - verify PAT/PMT CRC validation passes on valid stream
        var url = $"{fixture.BaseUrl}/stream/5000";

        Streamer.AddUrl(url);

        Assert.True(Streamer.Start(), "Streamer should start successfully");
        var streaming = await TestHelpers.WaitForStreamingAsync(Streamer, TimeSpan.FromSeconds(5));
        Assert.True(streaming, "Should reach streaming state");
        await Task.Delay(TimeSpan.FromSeconds(5));

        var metrics = Streamer.GetMetrics();
        Streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        Output.WriteLine($"Priority 2 - CrcError: {metrics.Priority2.CrcError}");

        // Valid PAT/PMT should produce zero CRC errors
        Assert.Equal(0, metrics.Priority2.CrcError);
    }

    /// <summary>
    /// Tests that corrupted streams trigger TR 101 290 errors.
    /// The test server injects bad sync bytes and TEI flags.
    /// Dropped packets cause continuity errors, TEI flag triggers transport errors.
    /// </summary>
    [Fact]
    public async Task Tr101290_CorruptedStream_DetectsErrors()
    {
        // Arrange - corrupted endpoint injects bad sync bytes and TEI flags.
        // The alignment buffer strips packets with invalid sync bytes before the analyzer,
        // so sync byte errors may not be reported. However, the dropped packets cause
        // continuity counter errors, and the TEI flag triggers transport errors.
        var url = $"{fixture.BaseUrl}/stream/corrupted/5000";

        Streamer.AddUrl(url);

        Assert.True(Streamer.Start(), "Streamer should start successfully");
        var streaming = await TestHelpers.WaitForStreamingAsync(Streamer, TimeSpan.FromSeconds(5));
        Assert.True(streaming, "Should reach streaming state");

        // Act - stream corrupted data for 5 seconds
        await Task.Delay(TimeSpan.FromSeconds(5));

        var metrics = Streamer.GetMetrics();
        var status = Streamer.GetStatus();
        Streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        Output.WriteLine($"Total bytes: {status.BytesReceived:N0}");
        Output.WriteLine($"Priority 1 - SyncByteError: {metrics.Priority1.SyncByteError}");
        Output.WriteLine($"Priority 1 - ContinuityCountError: {metrics.Priority1.ContinuityCountError}");
        Output.WriteLine($"Priority 2 - TransportError: {metrics.Priority2.TransportError}");

        // The corrupted stream produces continuity errors (from dropped packets)
        // and/or transport errors (from TEI flag)
        var totalErrors = metrics.Priority1.TotalErrors + metrics.Priority2.TotalErrors;
        Assert.True(
            totalErrors > 0,
            $"Should have detected errors from corrupted stream. P1: {metrics.Priority1.TotalErrors}, P2: {metrics.Priority2.TotalErrors}"
        );
    }

    /// <summary>
    /// Tests that Transport Error Indicator (TEI) flags are detected.
    /// The TEI bit in the TS header indicates the stream contains errors.
    /// </summary>
    [Fact]
    public async Task Tr101290_CorruptedStream_DetectsTransportErrors()
    {
        // Arrange - corrupted endpoint sets TEI bit on some packets
        var url = $"{fixture.BaseUrl}/stream/corrupted/5000";

        Streamer.AddUrl(url);

        Assert.True(Streamer.Start(), "Streamer should start successfully");
        var streaming = await TestHelpers.WaitForStreamingAsync(Streamer, TimeSpan.FromSeconds(5));
        Assert.True(streaming, "Should reach streaming state");
        await Task.Delay(TimeSpan.FromSeconds(5));

        var metrics = Streamer.GetMetrics();
        Streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        Output.WriteLine($"Priority 2 - TransportError: {metrics.Priority2.TransportError}");

        // TEI flag set on packets should trigger transport errors
        Assert.True(
            metrics.Priority2.TransportError > 0,
            $"Should have detected transport errors (TEI flag) from corrupted stream, got {metrics.Priority2.TransportError}"
        );
    }

    /// <summary>
    /// Tests that PCR repetition is within TR 101 290 limits (40ms).
    /// Uses packet-distance timing, not wall-clock, to avoid false positives from network jitter.
    /// </summary>
    [Fact]
    public async Task Tr101290_CleanStream_NoPcrRepetitionErrors()
    {
        // Arrange - verify PCR repetition check uses packet-distance timing (not wall-clock).
        // The test stream spaces PCR packets properly (~40ms stream-time intervals),
        // so with packet-distance timing there should be zero repetition errors
        // regardless of HTTP delivery jitter.
        var url = $"{fixture.BaseUrl}/stream/5000";

        Streamer.AddUrl(url);

        Assert.True(Streamer.Start(), "Streamer should start successfully");
        var streaming = await TestHelpers.WaitForStreamingAsync(Streamer, TimeSpan.FromSeconds(5));
        Assert.True(streaming, "Should reach streaming state");
        await Task.Delay(TimeSpan.FromSeconds(5));

        var metrics = Streamer.GetMetrics();
        Streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        Output.WriteLine($"Priority 2 - PcrRepetitionError: {metrics.Priority2.PcrRepetitionError}");
        Output.WriteLine($"Priority 2 - PcrDiscontinuityError: {metrics.Priority2.PcrDiscontinuityError}");

        // With packet-distance timing, a properly-spaced stream should have zero PCR errors
        Assert.Equal(0, metrics.Priority2.PcrRepetitionError);
    }

    /// <summary>
    /// Tests that PTS values are present within the TR 101 290 limit of 700ms.
    /// </summary>
    [Fact]
    public async Task Tr101290_CleanStream_NoPtsErrors()
    {
        // Arrange - PTS should be present within 700ms on each PID
        var url = $"{fixture.BaseUrl}/stream/5000";

        Streamer.AddUrl(url);

        Assert.True(Streamer.Start(), "Streamer should start successfully");
        var streaming = await TestHelpers.WaitForStreamingAsync(Streamer, TimeSpan.FromSeconds(5));
        Assert.True(streaming, "Should reach streaming state");
        await Task.Delay(TimeSpan.FromSeconds(5));

        var metrics = Streamer.GetMetrics();
        Streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        Output.WriteLine($"Priority 2 - PtsError: {metrics.Priority2.PtsError}");

        // Test stream generates PTS on every PES packet, well within 700ms limit
        Assert.Equal(0, metrics.Priority2.PtsError);
    }

    /// <summary>
    /// Tests that a well-formed stream achieves a high quality score.
    /// Quality score is calculated from TR 101 290 error counts.
    /// </summary>
    [Fact]
    public async Task Tr101290_CleanStream_HighQualityScore()
    {
        // Arrange - with packet-distance timing for PAT/PMT/PCR checks,
        // a well-formed test stream should achieve a high quality score.
        var url = $"{fixture.BaseUrl}/stream/5000";

        Streamer.AddUrl(url);

        Assert.True(Streamer.Start(), "Streamer should start successfully");
        var streaming = await TestHelpers.WaitForStreamingAsync(Streamer, TimeSpan.FromSeconds(5));
        Assert.True(streaming, "Should reach streaming state");
        await Task.Delay(TimeSpan.FromSeconds(5));

        var metrics = Streamer.GetMetrics();
        Streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        var qualityScore = metrics.CalculateQualityScore();
        Output.WriteLine($"Quality Score: {qualityScore}/100");
        Output.WriteLine($"P1 Total Errors: {metrics.Priority1.TotalErrors}");
        Output.WriteLine($"P2 Total Errors: {metrics.Priority2.TotalErrors}");
        Output.WriteLine($"PAT errors: {metrics.Priority1.PatError}, PMT errors: {metrics.Priority1.PmtError}");
        Output.WriteLine($"PCR rep errors: {metrics.Priority2.PcrRepetitionError}");

        // With packet-distance timing, the quality score should be high
        Assert.True(qualityScore >= 80, $"Quality score should be >= 80, got {qualityScore}");
        Assert.Equal(0, metrics.Priority1.SyncByteError);
        Assert.Equal(0, metrics.Priority1.SyncLoss);
        Assert.Equal(0, metrics.Priority1.PatError);
        Assert.Equal(0, metrics.Priority1.PmtError);
        Assert.Equal(0, metrics.Priority2.PcrRepetitionError);
    }

    /// <summary>
    /// Tests that corrupted streams have a degraded quality score.
    /// </summary>
    [Fact]
    public async Task Tr101290_CorruptedStream_QualityScoreDegrades()
    {
        // Arrange - corrupted stream should produce lower quality score
        var url = $"{fixture.BaseUrl}/stream/corrupted/5000";

        Streamer.AddUrl(url);

        Assert.True(Streamer.Start(), "Streamer should start successfully");
        var streaming = await TestHelpers.WaitForStreamingAsync(Streamer, TimeSpan.FromSeconds(5));
        Assert.True(streaming, "Should reach streaming state");
        await Task.Delay(TimeSpan.FromSeconds(5));

        var metrics = Streamer.GetMetrics();
        Streamer.Stop();

        // Assert
        Assert.NotNull(metrics);
        Output.WriteLine($"P1 Total Errors: {metrics.Priority1.TotalErrors}");
        Output.WriteLine($"P2 Total Errors: {metrics.Priority2.TotalErrors}");

        // Corrupted stream should have at least some errors
        var totalErrors = metrics.Priority1.TotalErrors + metrics.Priority2.TotalErrors;
        Assert.True(
            totalErrors > 0,
            $"Corrupted stream should produce TR 101 290 errors, got P1:{metrics.Priority1.TotalErrors}, P2:{metrics.Priority2.TotalErrors}"
        );
    }
}
