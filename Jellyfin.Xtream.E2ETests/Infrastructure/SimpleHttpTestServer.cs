using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.E2ETests.Infrastructure;

/// <summary>
/// Simple HTTP test server using Kestrel that serves MPEG-TS streams.
/// Supports throttled streaming, finite streams, delayed responses, and unstable connections.
/// </summary>
internal sealed class SimpleHttpTestServer : IAsyncDisposable
{
    private const int TsPacketSize = 188;
    private const int DefaultChunkPackets = 7; // 7 packets per chunk = 1316 bytes (UDP-sized)

    private WebApplication? _app;
    private readonly int _port;
    private int _connectionCount;
    private int _unstableDropAfterMs = 2000;

    /// <summary>
    /// Gets the base URL of the test server.
    /// </summary>
    public string BaseUrl => $"http://localhost:{_port}";

    /// <summary>
    /// Gets the total number of connections served.
    /// </summary>
    public int ConnectionCount => Volatile.Read(ref _connectionCount);

    /// <summary>
    /// Gets or sets how many milliseconds the unstable endpoint streams before dropping.
    /// </summary>
    public int UnstableDropAfterMs
    {
        get => _unstableDropAfterMs;
        set => _unstableDropAfterMs = value;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SimpleHttpTestServer"/> class.
    /// </summary>
    /// <param name="port">Port to listen on (0 for random).</param>
    public SimpleHttpTestServer(int port = 0)
    {
        _port = port == 0 ? GetRandomPort() : port;
    }

    /// <summary>
    /// Starts the HTTP server.
    /// </summary>
    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{_port}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        _app = builder.Build();

        // GET /stream/{bitrateKbps} - infinite throttled stream
        _app.MapGet(
            "/stream/{bitrateKbps:int}",
            async (int bitrateKbps, HttpContext ctx) =>
            {
                Interlocked.Increment(ref _connectionCount);
                ctx.Response.ContentType = "video/mp2t";
                ctx.Response.Headers["Connection"] = "close";

                var generator = new TestStreamGenerator(bitrateKbps);
                var bytesPerSecond = bitrateKbps * 1000.0 / 8.0;
                // Use larger chunks (50 packets = 9400 bytes) for less per-chunk overhead
                const int chunkPackets = 50;
                long totalBytesSent = 0;
                var stopwatch = Stopwatch.StartNew();

                try
                {
                    while (!ctx.RequestAborted.IsCancellationRequested)
                    {
                        var chunk = generator.GenerateChunk(chunkPackets);
                        await ctx.Response.Body.WriteAsync(chunk, ctx.RequestAborted);
                        totalBytesSent += chunk.Length;

                        // Stopwatch-based throttling: calculate how far ahead we are
                        var targetElapsedMs = totalBytesSent / bytesPerSecond * 1000.0;
                        var actualElapsedMs = stopwatch.Elapsed.TotalMilliseconds;
                        var sleepMs = (int)(targetElapsedMs - actualElapsedMs);

                        if (sleepMs > 1)
                        {
                            await Task.Delay(sleepMs, ctx.RequestAborted);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Client disconnected
                }
            }
        );

        // GET /stream/finite/{packets} - fixed-length stream (no throttling)
        _app.MapGet(
            "/stream/finite/{packets:int}",
            async (int packets, HttpContext ctx) =>
            {
                Interlocked.Increment(ref _connectionCount);
                ctx.Response.ContentType = "video/mp2t";
                ctx.Response.Headers["Content-Length"] = (packets * TsPacketSize).ToString();

                var generator = new TestStreamGenerator(5000);
                var data = generator.GenerateChunk(packets);
                await ctx.Response.Body.WriteAsync(data, ctx.RequestAborted);
            }
        );

        // GET /stream/delayed/{delayMs} - stream with initial delay before data
        _app.MapGet(
            "/stream/delayed/{delayMs:int}",
            async (int delayMs, HttpContext ctx) =>
            {
                Interlocked.Increment(ref _connectionCount);
                ctx.Response.ContentType = "video/mp2t";
                ctx.Response.Headers["Connection"] = "close";

                await Task.Delay(delayMs, ctx.RequestAborted);

                var generator = new TestStreamGenerator(5000);
                var bytesPerSecond = 5000.0 * 1000.0 / 8.0;
                var chunkBytes = DefaultChunkPackets * TsPacketSize;
                var delayPerChunkMs = (int)(chunkBytes / bytesPerSecond * 1000.0);

                try
                {
                    while (!ctx.RequestAborted.IsCancellationRequested)
                    {
                        var chunk = generator.GenerateChunk(DefaultChunkPackets);
                        await ctx.Response.Body.WriteAsync(chunk, ctx.RequestAborted);
                        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

                        if (delayPerChunkMs > 0)
                        {
                            await Task.Delay(delayPerChunkMs, ctx.RequestAborted);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Client disconnected
                }
            }
        );

        // GET /stream/unstable - drops connection after configured interval
        _app.MapGet(
            "/stream/unstable",
            async (HttpContext ctx) =>
            {
                Interlocked.Increment(ref _connectionCount);
                ctx.Response.ContentType = "video/mp2t";
                ctx.Response.Headers["Connection"] = "close";

                var generator = new TestStreamGenerator(5000);
                var startTime = DateTime.UtcNow;
                var dropAfter = TimeSpan.FromMilliseconds(_unstableDropAfterMs);

                try
                {
                    while (!ctx.RequestAborted.IsCancellationRequested)
                    {
                        if (DateTime.UtcNow - startTime > dropAfter)
                        {
                            // Abort the connection to simulate network failure
                            ctx.Abort();
                            return;
                        }

                        var chunk = generator.GenerateChunk(DefaultChunkPackets);
                        await ctx.Response.Body.WriteAsync(chunk, ctx.RequestAborted);
                        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                        await Task.Delay(2, ctx.RequestAborted); // Fast streaming
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected
                }
            }
        );

        // GET /stream/burst/{bitrateKbps} - sends data in bursts (for jitter testing)
        _app.MapGet(
            "/stream/burst/{bitrateKbps:int}",
            async (int bitrateKbps, HttpContext ctx) =>
            {
                Interlocked.Increment(ref _connectionCount);
                ctx.Response.ContentType = "video/mp2t";
                ctx.Response.Headers["Connection"] = "close";

                var generator = new TestStreamGenerator(bitrateKbps);
                var burstPackets = DefaultChunkPackets * 10; // Send 10x normal chunk
                var bytesPerSecond = bitrateKbps * 1000.0 / 8.0;
                var burstBytes = burstPackets * TsPacketSize;
                var delayPerBurstMs = (int)(burstBytes / bytesPerSecond * 1000.0);

                try
                {
                    while (!ctx.RequestAborted.IsCancellationRequested)
                    {
                        var chunk = generator.GenerateChunk(burstPackets);
                        await ctx.Response.Body.WriteAsync(chunk, ctx.RequestAborted);
                        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

                        if (delayPerBurstMs > 0)
                        {
                            await Task.Delay(delayPerBurstMs, ctx.RequestAborted);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Client disconnected
                }
            }
        );

        // GET /stream/corrupted/{bitrateKbps} - stream with periodic corrupt packets for TR 101 290 testing
        _app.MapGet(
            "/stream/corrupted/{bitrateKbps:int}",
            async (int bitrateKbps, HttpContext ctx) =>
            {
                Interlocked.Increment(ref _connectionCount);
                ctx.Response.ContentType = "video/mp2t";
                ctx.Response.Headers["Connection"] = "close";

                var generator = new TestStreamGenerator(bitrateKbps);
                var bytesPerSecond = bitrateKbps * 1000.0 / 8.0;
                const int chunkPackets = 50;
                long totalBytesSent = 0;
                int chunkIndex = 0;
                var stopwatch = Stopwatch.StartNew();

                try
                {
                    while (!ctx.RequestAborted.IsCancellationRequested)
                    {
                        var chunk = generator.GenerateChunk(chunkPackets);

                        // Every 5th chunk, corrupt some packets (bad sync byte + TEI flag)
                        if (chunkIndex % 5 == 4)
                        {
                            // Corrupt first packet sync byte
                            chunk[0] = 0xFF;
                            // Set TEI (transport error indicator) on second packet
                            if (chunk.Length >= TsPacketSize * 2)
                            {
                                chunk[TsPacketSize + 1] |= 0x80;
                            }
                        }

                        await ctx.Response.Body.WriteAsync(chunk, ctx.RequestAborted);
                        totalBytesSent += chunk.Length;
                        chunkIndex++;

                        var targetElapsedMs = totalBytesSent / bytesPerSecond * 1000.0;
                        var actualElapsedMs = stopwatch.Elapsed.TotalMilliseconds;
                        var sleepMs = (int)(targetElapsedMs - actualElapsedMs);

                        if (sleepMs > 1)
                        {
                            await Task.Delay(sleepMs, ctx.RequestAborted);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Client disconnected
                }
            }
        );

        await _app.StartAsync();
    }

    /// <summary>
    /// Resets the connection counter.
    /// </summary>
    public void ResetConnectionCount()
    {
        Interlocked.Exchange(ref _connectionCount, 0);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
    }

    private static int GetRandomPort()
    {
        // Use a listener to find an available port
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
