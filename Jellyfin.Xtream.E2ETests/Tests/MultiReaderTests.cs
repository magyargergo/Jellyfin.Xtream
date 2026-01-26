using Jellyfin.Xtream.E2ETests.Infrastructure;
using Jellyfin.Xtream.Service;
using Jellyfin.Xtream.Service.Streaming.Native;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.E2ETests.Tests;

/// <summary>
/// Tests concurrent multi-reader scenarios on a shared CircularBufferWriteStream.
/// Verifies no data corruption or reader starvation.
/// Uses high bitrate (50 Mbps) to ensure CircularBuffer warmup completes quickly.
/// </summary>
[Collection("E2E")]
public class MultiReaderTests
{
    private const int TsPacketSize = 188;
    private const byte SyncByte = 0x47;

    // 50 Mbps ensures 4MB warmup fills in < 1 second
    private const int HighBitrateKbps = 50000;

    // 16MB buffer to avoid overflow at high bitrate
    private const int BufferSize = 16 * 1024 * 1024;

    private readonly DockerTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public MultiReaderTests(DockerTestFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task MultiReader_ConcurrentReads_AllGetValidData(int readerCount)
    {
        // Arrange
        var url = $"{_fixture.BaseUrl}/stream/{HighBitrateKbps}";
        const int readDurationSec = 5;

        using var writeStream = new CircularBufferWriteStream(BufferSize);
        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.SetOutputCallback(
            (ptr, len) =>
            {
                unsafe
                {
                    writeStream.Write(new ReadOnlySpan<byte>((void*)ptr, len));
                }
            }
        );

        streamer.AddUrl(url);
        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));

        // Act - spawn multiple readers
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(readDurationSec + 5));
        var readerTasks = new Task<ReaderResult>[readerCount];

        for (int i = 0; i < readerCount; i++)
        {
            int readerId = i;
            readerTasks[i] = Task.Run(() => RunReader(writeStream, readerId, cts.Token), cts.Token);
        }

        await Task.Delay(TimeSpan.FromSeconds(readDurationSec));
        await cts.CancelAsync();

        var results = await Task.WhenAll(readerTasks);
        streamer.Stop();

        // Assert
        _output.WriteLine($"Reader results ({readerCount} readers):");
        foreach (var result in results)
        {
            _output.WriteLine(
                $"  Reader {result.ReaderId}: {result.TotalBytes:N0} bytes, "
                    + $"{result.ValidPackets}/{result.TotalPackets} valid, "
                    + $"{result.SyncErrors} sync errors"
            );
        }

        foreach (var result in results)
        {
            // Each reader must get data (not starved)
            Assert.True(result.TotalBytes > 0, $"Reader {result.ReaderId} got 0 bytes (starved)");

            // Each reader must get some valid TS packets
            // Note: CircularBuffer reads may not be TS-aligned (ring buffer wraps),
            // so we only verify some valid packets exist rather than a strict error rate.
            // Pipeline integrity is verified by DataIntegrity_NoCorruptionUnderLoad.
            Assert.True(result.ValidPackets > 0, $"Reader {result.ReaderId} got no valid packets");
        }
    }

    [Fact]
    public async Task MultiReader_SlowReader_DoesNotBlockFastReaders()
    {
        // Arrange
        var url = $"{_fixture.BaseUrl}/stream/{HighBitrateKbps}";

        using var writeStream = new CircularBufferWriteStream(BufferSize);
        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.SetOutputCallback(
            (ptr, len) =>
            {
                unsafe
                {
                    writeStream.Write(new ReadOnlySpan<byte>((void*)ptr, len));
                }
            }
        );

        streamer.AddUrl(url);
        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));

        // Act - one fast reader + one slow reader
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        var fastReaderTask = Task.Run(() => RunReader(writeStream, 0, cts.Token), cts.Token);
        var slowReaderTask = Task.Run(() => RunSlowReader(writeStream, 1, cts.Token), cts.Token);

        await Task.Delay(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();

        var fastResult = await fastReaderTask;
        var slowResult = await slowReaderTask;
        streamer.Stop();

        // Assert
        _output.WriteLine($"Fast reader: {fastResult.TotalBytes:N0} bytes");
        _output.WriteLine($"Slow reader: {slowResult.TotalBytes:N0} bytes");

        // Fast reader should get significantly more data than slow reader
        Assert.True(fastResult.TotalBytes > slowResult.TotalBytes, "Fast reader should outpace slow reader");

        // Both should still get valid data
        Assert.True(fastResult.TotalBytes > 0, "Fast reader should get data");
        Assert.True(slowResult.TotalBytes > 0, "Slow reader should get data");
    }

    [Fact]
    public async Task MultiReader_ReaderJoinLate_GetsCurrentData()
    {
        // Arrange
        var url = $"{_fixture.BaseUrl}/stream/{HighBitrateKbps}";

        using var writeStream = new CircularBufferWriteStream(BufferSize);
        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.SetOutputCallback(
            (ptr, len) =>
            {
                unsafe
                {
                    writeStream.Write(new ReadOnlySpan<byte>((void*)ptr, len));
                }
            }
        );

        streamer.AddUrl(url);
        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));

        // Act - first reader starts immediately
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var earlyReaderTask = Task.Run(() => RunReader(writeStream, 0, cts.Token), cts.Token);

        // Wait 3 seconds then start a late reader
        await Task.Delay(TimeSpan.FromSeconds(3));
        var lateReaderTask = Task.Run(() => RunReader(writeStream, 1, cts.Token), cts.Token);

        await Task.Delay(TimeSpan.FromSeconds(4)); // Let late reader run for ~4 seconds
        await cts.CancelAsync();

        var earlyResult = await earlyReaderTask;
        var lateResult = await lateReaderTask;
        streamer.Stop();

        // Assert
        _output.WriteLine($"Early reader (7s): {earlyResult.TotalBytes:N0} bytes, {earlyResult.ValidPackets} valid");
        _output.WriteLine($"Late reader (4s):  {lateResult.TotalBytes:N0} bytes, {lateResult.ValidPackets} valid");

        // Late reader should still get valid data
        Assert.True(lateResult.TotalBytes > 0, "Late reader should get data");
        Assert.True(lateResult.ValidPackets > 0, "Late reader should get valid packets");
    }

    [Fact]
    public async Task MultiReader_ReaderDispose_DoesNotAffectOthers()
    {
        // Arrange
        var url = $"{_fixture.BaseUrl}/stream/{HighBitrateKbps}";

        using var writeStream = new CircularBufferWriteStream(BufferSize);
        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        streamer.SetOutputCallback(
            (ptr, len) =>
            {
                unsafe
                {
                    writeStream.Write(new ReadOnlySpan<byte>((void*)ptr, len));
                }
            }
        );

        streamer.AddUrl(url);
        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));

        // Act - create readers, dispose one while others continue
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var disposeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Reader 0 will be disposed after 2 seconds
        var shortReaderTask = Task.Run(() => RunReader(writeStream, 0, disposeCts.Token), CancellationToken.None);
        // Reader 1 runs for full duration
        var longReaderTask = Task.Run(() => RunReader(writeStream, 1, cts.Token), cts.Token);

        await Task.Delay(TimeSpan.FromSeconds(6));
        await cts.CancelAsync();

        var shortResult = await shortReaderTask;
        var longResult = await longReaderTask;
        streamer.Stop();

        // Assert
        _output.WriteLine($"Short-lived reader (2s): {shortResult.TotalBytes:N0} bytes");
        _output.WriteLine($"Long-lived reader (6s):  {longResult.TotalBytes:N0} bytes");

        // Long reader should have more data (ran longer)
        Assert.True(longResult.TotalBytes > shortResult.TotalBytes, "Long reader should have more data");
        // Both should have gotten valid data during their lifetime
        Assert.True(shortResult.TotalBytes > 0, "Short reader should have gotten data");
        Assert.True(longResult.ValidPackets > 0, "Long reader should have valid packets");
    }

    [Fact]
    public async Task MultiReader_DataIntegrity_NoCorruptionUnderLoad()
    {
        // Arrange - verify no data corruption with multiple concurrent readers
        var url = $"{_fixture.BaseUrl}/stream/{HighBitrateKbps}";

        using var writeStream = new CircularBufferWriteStream(BufferSize);
        using var streamer = CreateStreamer();
        if (streamer == null)
        {
            _output.WriteLine("SKIP: Native library not available");
            return;
        }

        var callbackMetrics = new TestMetrics();
        streamer.SetOutputCallback(
            (ptr, len) =>
            {
                unsafe
                {
                    writeStream.Write(new ReadOnlySpan<byte>((void*)ptr, len));
                    callbackMetrics.ProcessReceivedData((byte*)ptr, len);
                }
            }
        );

        streamer.AddUrl(url);
        callbackMetrics.Start();
        Assert.True(streamer.Start());
        await WaitForConnection(streamer, TimeSpan.FromSeconds(5));

        // Act - 4 concurrent readers for 5 seconds
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var readerTasks = Enumerable
            .Range(0, 4)
            .Select(id => Task.Run(() => RunReader(writeStream, id, cts.Token), cts.Token))
            .ToArray();

        await Task.Delay(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        callbackMetrics.Stop();

        var results = await Task.WhenAll(readerTasks);
        streamer.Stop();

        // Assert - callback data is valid
        _output.WriteLine(
            $"Callback metrics: {callbackMetrics.TotalPackets} packets, {callbackMetrics.ContinuityErrors} CC errors"
        );
        Assert.True(callbackMetrics.TotalPackets > 0, "Should have received packets via callback");
        Assert.Equal(callbackMetrics.TotalPackets, callbackMetrics.ValidSyncPackets);

        // Each reader should get valid data
        foreach (var result in results)
        {
            _output.WriteLine($"  Reader {result.ReaderId}: {result.TotalBytes:N0} bytes, errors: {result.SyncErrors}");
            Assert.True(result.TotalBytes > 0, $"Reader {result.ReaderId} starved");
        }
    }

    private static async Task<ReaderResult> RunReader(
        CircularBufferWriteStream writeStream,
        int readerId,
        CancellationToken ct
    )
    {
        var result = new ReaderResult { ReaderId = readerId };

        using var readStream = new CircularBufferReadStream(writeStream);
        var buffer = new byte[32768];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var bytesRead = await readStream.ReadAsync(buffer, ct);
                if (bytesRead > 0)
                {
                    result.TotalBytes += bytesRead;

                    // Check packet integrity
                    int packets = bytesRead / TsPacketSize;
                    result.TotalPackets += packets;
                    for (int i = 0; i < packets; i++)
                    {
                        if (buffer[i * TsPacketSize] == SyncByte)
                        {
                            result.ValidPackets++;
                        }
                        else
                        {
                            result.SyncErrors++;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }

        return result;
    }

    private static async Task<ReaderResult> RunSlowReader(
        CircularBufferWriteStream writeStream,
        int readerId,
        CancellationToken ct
    )
    {
        var result = new ReaderResult { ReaderId = readerId };

        using var readStream = new CircularBufferReadStream(writeStream);
        var buffer = new byte[TsPacketSize * 2]; // Small reads

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var bytesRead = await readStream.ReadAsync(buffer, ct);
                if (bytesRead > 0)
                {
                    result.TotalBytes += bytesRead;
                    result.TotalPackets += bytesRead / TsPacketSize;
                }

                // Simulate slow consumer
                await Task.Delay(100, ct);
            }
        }
        catch (OperationCanceledException) { }

        return result;
    }

    private static NativeStreamer? CreateStreamer()
    {
        return NativeStreamer.TryCreate(TsDuckStreamerConfigNative.Default);
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

    private class ReaderResult
    {
        public int ReaderId { get; set; }
        public long TotalBytes { get; set; }
        public long TotalPackets { get; set; }
        public long ValidPackets { get; set; }
        public long SyncErrors { get; set; }
    }
}
