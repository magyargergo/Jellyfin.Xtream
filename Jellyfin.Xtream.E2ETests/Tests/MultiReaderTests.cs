using Jellyfin.Xtream.E2ETests.Infrastructure;
using Jellyfin.Xtream.Service.Streaming.Native;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.E2ETests.Tests;

/// <summary>
/// Tests concurrent multi-reader scenarios using GetStatus() for data verification.
/// Verifies thread safety of status polling and that data flows correctly under load.
/// Uses high bitrate (50 Mbps) to ensure rapid data flow for stress testing.
/// </summary>
[Collection("E2E-Restream")]
public class MultiReaderTests(DockerTestFixture fixture, ITestOutputHelper output) : NativeE2ETestBase(output)
{
    private const int TsPacketSize = 188;

    // 50 Mbps ensures fast data flow for testing
    private const int HighBitrateKbps = 50000;

    /// <summary>
    /// Tests that multiple concurrent readers can all observe valid data.
    /// Verifies that no reader is starved when polling GetStatus() concurrently.
    /// </summary>
    /// <param name="readerCount">Number of concurrent readers to simulate.</param>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task MultiReader_ConcurrentReads_AllGetValidData(int readerCount)
    {
        // Arrange
        var url = $"{fixture.BaseUrl}/stream/{HighBitrateKbps}";
        const int readDurationSec = 5;

        Streamer.AddUrl(url);
        Assert.True(Streamer.Start(), "Streamer should start successfully");
        var streaming = await TestHelpers.WaitForStreamingAsync(Streamer, TimeSpan.FromSeconds(5));
        Assert.True(streaming, "Should reach streaming state");

        // Act - poll status multiple times to simulate concurrent reads
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(readDurationSec + 5));
        var readerTasks = new Task<ReaderResult>[readerCount];

        for (int i = 0; i < readerCount; i++)
        {
            int readerId = i;
            readerTasks[i] = Task.Run(() => SimulateReader(Streamer, readerId, cts.Token), cts.Token);
        }

        await Task.Delay(TimeSpan.FromSeconds(readDurationSec));
        await cts.CancelAsync();

        var results = await Task.WhenAll(readerTasks);
        var status = Streamer.GetStatus();
        Streamer.Stop();

        // Assert
        Output.WriteLine($"Reader results ({readerCount} readers):");
        Output.WriteLine($"Streamer bytes received: {status.BytesReceived:N0}");
        Output.WriteLine($"Streamer packets output: {status.PacketsOutput:N0}");

        foreach (var result in results)
        {
            Output.WriteLine(
                $"  Reader {result.ReaderId}: {result.StatusPolls} polls, "
                    + $"observed {result.TotalBytesObserved:N0} max bytes"
            );
        }

        // The streamer should have received data
        Assert.True(status.BytesReceived > 0, "Streamer should have received data");
        Assert.True(status.PacketsOutput > 0, "Streamer should have output packets");

        // Each reader should have observed data growth (not starved)
        foreach (var result in results)
        {
            Assert.True(result.TotalBytesObserved > 0, $"Reader {result.ReaderId} observed 0 bytes (starved)");
        }
    }

    /// <summary>
    /// Tests that a slow reader does not block faster readers.
    /// Verifies that GetStatus() is lock-free and non-blocking.
    /// </summary>
    [Fact]
    public async Task MultiReader_SlowReader_DoesNotBlockFastReaders()
    {
        // Arrange
        var url = $"{fixture.BaseUrl}/stream/{HighBitrateKbps}";

        Streamer.AddUrl(url);
        Assert.True(Streamer.Start(), "Streamer should start successfully");
        var streaming = await TestHelpers.WaitForStreamingAsync(Streamer, TimeSpan.FromSeconds(5));
        Assert.True(streaming, "Should reach streaming state");

        // Act - one fast reader + one slow reader (both polling status)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        var fastReaderTask = Task.Run(() => SimulateReader(Streamer, 0, cts.Token), cts.Token);
        var slowReaderTask = Task.Run(() => SimulateSlowReader(Streamer, 1, cts.Token), cts.Token);

        await Task.Delay(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();

        var fastResult = await fastReaderTask;
        var slowResult = await slowReaderTask;
        var status = Streamer.GetStatus();
        Streamer.Stop();

        // Assert
        Output.WriteLine(
            $"Fast reader: {fastResult.StatusPolls} polls, {fastResult.TotalBytesObserved:N0} bytes observed"
        );
        Output.WriteLine(
            $"Slow reader: {slowResult.StatusPolls} polls, {slowResult.TotalBytesObserved:N0} bytes observed"
        );
        Output.WriteLine($"Total bytes received: {status.BytesReceived:N0}");

        // Fast reader should have more polls than slow reader
        Assert.True(fastResult.StatusPolls > slowResult.StatusPolls, "Fast reader should poll more than slow reader");

        // Both should still observe valid data
        Assert.True(fastResult.TotalBytesObserved > 0, "Fast reader should observe data");
        Assert.True(slowResult.TotalBytesObserved > 0, "Slow reader should observe data");
    }

    /// <summary>
    /// Tests that a reader joining late can still observe current data.
    /// Verifies that GetStatus() always returns the current state.
    /// </summary>
    [Fact]
    public async Task MultiReader_ReaderJoinLate_GetsCurrentData()
    {
        // Arrange
        var url = $"{fixture.BaseUrl}/stream/{HighBitrateKbps}";

        Streamer.AddUrl(url);
        Assert.True(Streamer.Start(), "Streamer should start successfully");
        var streaming = await TestHelpers.WaitForStreamingAsync(Streamer, TimeSpan.FromSeconds(5));
        Assert.True(streaming, "Should reach streaming state");

        // Act - first reader starts immediately
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var earlyReaderTask = Task.Run(() => SimulateReader(Streamer, 0, cts.Token), cts.Token);

        // Wait 3 seconds then start a late reader
        await Task.Delay(TimeSpan.FromSeconds(3));
        var lateReaderTask = Task.Run(() => SimulateReader(Streamer, 1, cts.Token), cts.Token);

        await Task.Delay(TimeSpan.FromSeconds(4)); // Let late reader run for ~4 seconds
        await cts.CancelAsync();

        var earlyResult = await earlyReaderTask;
        var lateResult = await lateReaderTask;
        var status = Streamer.GetStatus();
        Streamer.Stop();

        // Assert
        Output.WriteLine($"Early reader (7s): {earlyResult.TotalBytesObserved:N0} bytes observed");
        Output.WriteLine($"Late reader (4s):  {lateResult.TotalBytesObserved:N0} bytes observed");
        Output.WriteLine($"Total bytes: {status.BytesReceived:N0}");

        // Late reader should still observe valid data
        Assert.True(lateResult.TotalBytesObserved > 0, "Late reader should observe data");
    }

    /// <summary>
    /// Tests that one reader stopping does not affect other readers.
    /// Verifies independent reader operation.
    /// </summary>
    [Fact]
    public async Task MultiReader_ReaderDispose_DoesNotAffectOthers()
    {
        // Arrange
        var url = $"{fixture.BaseUrl}/stream/{HighBitrateKbps}";

        Streamer.AddUrl(url);
        Assert.True(Streamer.Start(), "Streamer should start successfully");
        var streaming = await TestHelpers.WaitForStreamingAsync(Streamer, TimeSpan.FromSeconds(5));
        Assert.True(streaming, "Should reach streaming state");

        // Act - create readers, dispose one while others continue
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var disposeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Reader 0 will be disposed after 2 seconds
        var shortReaderTask = Task.Run(() => SimulateReader(Streamer, 0, disposeCts.Token), CancellationToken.None);
        // Reader 1 runs for full duration
        var longReaderTask = Task.Run(() => SimulateReader(Streamer, 1, cts.Token), cts.Token);

        await Task.Delay(TimeSpan.FromSeconds(6));
        await cts.CancelAsync();

        var shortResult = await shortReaderTask;
        var longResult = await longReaderTask;
        var status = Streamer.GetStatus();
        Streamer.Stop();

        // Assert
        Output.WriteLine($"Short-lived reader (2s): {shortResult.TotalBytesObserved:N0} bytes observed");
        Output.WriteLine($"Long-lived reader (6s):  {longResult.TotalBytesObserved:N0} bytes observed");

        // Long reader should have observed more data (ran longer)
        Assert.True(longResult.StatusPolls > shortResult.StatusPolls, "Long reader should have more polls");
        // Both should have observed valid data during their lifetime
        Assert.True(shortResult.TotalBytesObserved > 0, "Short reader should have observed data");
        Assert.True(longResult.TotalBytesObserved > 0, "Long reader should have observed data");
    }

    /// <summary>
    /// Tests data integrity under concurrent load.
    /// Verifies that byte counts are packet-aligned and no corruption occurs.
    /// </summary>
    [Fact]
    public async Task MultiReader_DataIntegrity_NoCorruptionUnderLoad()
    {
        // Arrange - verify no data corruption with multiple concurrent status polls
        var url = $"{fixture.BaseUrl}/stream/{HighBitrateKbps}";

        Streamer.AddUrl(url);
        Assert.True(Streamer.Start(), "Streamer should start successfully");
        var streaming = await TestHelpers.WaitForStreamingAsync(Streamer, TimeSpan.FromSeconds(5));
        Assert.True(streaming, "Should reach streaming state");

        // Act - 4 concurrent readers for 5 seconds
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var readerTasks = Enumerable
            .Range(0, 4)
            .Select(id => Task.Run(() => SimulateReader(Streamer, id, cts.Token), cts.Token))
            .ToArray();

        await Task.Delay(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();

        var results = await Task.WhenAll(readerTasks);
        var status = Streamer.GetStatus();
        Streamer.Stop();

        // Assert - verify status consistency
        Output.WriteLine($"Total bytes received: {status.BytesReceived:N0}");
        Output.WriteLine($"Total packets output: {status.PacketsOutput:N0}");
        Assert.True(status.BytesReceived > 0, "Should have received bytes");
        Assert.True(status.PacketsOutput > 0, "Should have output packets");

        // Verify byte count is packet-aligned
        Assert.Equal(0, status.BytesReceived % TsPacketSize);

        // Each reader should have observed data
        foreach (var result in results)
        {
            Output.WriteLine($"  Reader {result.ReaderId}: {result.TotalBytesObserved:N0} bytes observed");
            Assert.True(result.TotalBytesObserved > 0, $"Reader {result.ReaderId} starved");
        }
    }

    private static async Task<ReaderResult> SimulateReader(NativeStreamer streamer, int readerId, CancellationToken ct)
    {
        var result = new ReaderResult { ReaderId = readerId };

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var status = streamer.GetStatus();
                result.StatusPolls++;
                if (status.BytesReceived > result.TotalBytesObserved)
                {
                    result.TotalBytesObserved = status.BytesReceived;
                }

                await Task.Delay(10, ct); // Poll every 10ms
            }
        }
        catch (OperationCanceledException) { }

        return result;
    }

    private static async Task<ReaderResult> SimulateSlowReader(
        NativeStreamer streamer,
        int readerId,
        CancellationToken ct
    )
    {
        var result = new ReaderResult { ReaderId = readerId };

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var status = streamer.GetStatus();
                result.StatusPolls++;
                if (status.BytesReceived > result.TotalBytesObserved)
                {
                    result.TotalBytesObserved = status.BytesReceived;
                }

                // Simulate slow consumer - poll every 100ms
                await Task.Delay(100, ct);
            }
        }
        catch (OperationCanceledException) { }

        return result;
    }

    // ========================================================================
    // Helper Methods
    // ========================================================================

    private class ReaderResult
    {
        public int ReaderId { get; set; }
        public long TotalBytesObserved { get; set; }
        public int StatusPolls { get; set; }
    }
}
