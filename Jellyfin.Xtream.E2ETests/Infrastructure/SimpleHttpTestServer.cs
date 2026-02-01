using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.E2ETests.Infrastructure;

/// <summary>
/// Configurable behavior for a simulated provider.
/// </summary>
public sealed class ProviderBehavior
{
    /// <summary>
    /// Gets or sets the connection/response latency in milliseconds.
    /// Applied before first byte is sent.
    /// </summary>
    public int LatencyMs { get; set; }

    /// <summary>
    /// Gets or sets the failure rate (0.0 to 1.0).
    /// When a request fails, the server returns HTTP 503.
    /// </summary>
    public double FailureRate { get; set; }

    /// <summary>
    /// Gets or sets how many milliseconds to stream before dropping the connection.
    /// 0 = never drop (infinite stream).
    /// </summary>
    public int DropAfterMs { get; set; }

    /// <summary>
    /// Gets or sets the streaming bitrate in Kbps.
    /// </summary>
    public int BitrateKbps { get; set; } = 5000;

    /// <summary>
    /// Gets or sets whether to immediately refuse connections (connection refused).
    /// </summary>
    public bool SimulateConnectionRefused { get; set; }

    /// <summary>
    /// Gets or sets whether to hang indefinitely without sending data (timeout test).
    /// </summary>
    public bool SimulateResponseTimeout { get; set; }

    /// <summary>
    /// Gets or sets after how many milliseconds latency should increase.
    /// 0 = no latency degradation.
    /// </summary>
    public int SlowdownAfterMs { get; set; }

    /// <summary>
    /// Gets or sets the latency multiplier after SlowdownAfterMs.
    /// E.g., 3.0 means latency becomes 3x higher per chunk.
    /// </summary>
    public double SlowdownFactor { get; set; } = 1.0;

    /// <summary>
    /// Gets or sets whether to corrupt TS packets (bad sync byte + TEI flag).
    /// </summary>
    public bool CorruptPackets { get; set; }

    /// <summary>
    /// Gets or sets the corruption rate (0.0 to 1.0) when CorruptPackets is enabled.
    /// </summary>
    public double CorruptionRate { get; set; } = 0.1;
}

/// <summary>
/// Statistics tracked per provider.
/// </summary>
public sealed class ProviderStats
{
    private int _connectionAttempts;
    private int _successfulConnections;
    private int _failedConnections;
    private int _droppedConnections;
    private long _bytesServed;
    private long _requestsServed;

    /// <summary>Gets the total connection attempts.</summary>
    public int ConnectionAttempts => Volatile.Read(ref _connectionAttempts);

    /// <summary>Gets the successful connections.</summary>
    public int SuccessfulConnections => Volatile.Read(ref _successfulConnections);

    /// <summary>Gets the failed connections (HTTP errors).</summary>
    public int FailedConnections => Volatile.Read(ref _failedConnections);

    /// <summary>Gets the connections that were dropped mid-stream.</summary>
    public int DroppedConnections => Volatile.Read(ref _droppedConnections);

    /// <summary>Gets the total bytes served.</summary>
    public long BytesServed => Volatile.Read(ref _bytesServed);

    /// <summary>Gets the total requests served.</summary>
    public long RequestsServed => Volatile.Read(ref _requestsServed);

    internal void IncrementConnectionAttempt() => Interlocked.Increment(ref _connectionAttempts);

    internal void IncrementSuccessful() => Interlocked.Increment(ref _successfulConnections);

    internal void IncrementFailed() => Interlocked.Increment(ref _failedConnections);

    internal void IncrementDropped() => Interlocked.Increment(ref _droppedConnections);

    internal void AddBytes(long bytes) => Interlocked.Add(ref _bytesServed, bytes);

    internal void IncrementRequests() => Interlocked.Increment(ref _requestsServed);

    internal void Reset()
    {
        Volatile.Write(ref _connectionAttempts, 0);
        Volatile.Write(ref _successfulConnections, 0);
        Volatile.Write(ref _failedConnections, 0);
        Volatile.Write(ref _droppedConnections, 0);
        Volatile.Write(ref _bytesServed, 0);
        Volatile.Write(ref _requestsServed, 0);
    }
}

/// <summary>
/// Simple HTTP test server using Kestrel that serves MPEG-TS streams.
/// Supports throttled streaming, finite streams, delayed responses, unstable connections,
/// and per-provider configurable behaviors for health system testing.
/// </summary>
internal sealed class SimpleHttpTestServer : IAsyncDisposable
{
    private const int TsPacketSize = 188;
    private const int DefaultChunkPackets = 7; // 7 packets per chunk = 1316 bytes (UDP-sized)

    private WebApplication? _app;
    private readonly int _port;
    private int _connectionCount;
    private int _unstableDropAfterMs = 2000;

    // Per-provider configurable behaviors for health system testing
    private readonly ConcurrentDictionary<string, ProviderBehavior> _behaviors = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ProviderStats> _stats = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _failNextCounts = new(StringComparer.Ordinal);
    private readonly Random _random = new();
    private int _totalProviderConnections;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

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
    /// Gets the total number of provider connections across all providers.
    /// </summary>
    public int TotalProviderConnectionCount => Volatile.Read(ref _totalProviderConnections);

    /// <summary>
    /// Gets or sets the default behavior for providers without explicit configuration.
    /// </summary>
    public ProviderBehavior DefaultBehavior { get; set; } = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="SimpleHttpTestServer"/> class.
    /// </summary>
    /// <param name="port">Port to listen on (0 for random).</param>
    public SimpleHttpTestServer(int port = 0)
    {
        _port = port == 0 ? GetRandomPort() : port;
    }

    /// <summary>
    /// Gets the URL for a specific provider.
    /// </summary>
    /// <param name="providerId">The provider identifier.</param>
    /// <returns>The full URL for streaming from this provider.</returns>
    public string GetProviderUrl(string providerId) => $"{BaseUrl}/provider/{providerId}/stream";

    /// <summary>
    /// Configures the behavior for a specific provider.
    /// </summary>
    /// <param name="providerId">The provider identifier.</param>
    /// <param name="behavior">The behavior configuration.</param>
    public void ConfigureProvider(string providerId, ProviderBehavior behavior)
    {
        _behaviors[providerId] = behavior;
        _stats.GetOrAdd(providerId, _ => new ProviderStats());
    }

    /// <summary>
    /// Gets the statistics for a specific provider.
    /// </summary>
    /// <param name="providerId">The provider identifier.</param>
    /// <returns>The provider statistics, or a new empty stats object if not found.</returns>
    public ProviderStats GetStats(string providerId)
    {
        return _stats.GetOrAdd(providerId, _ => new ProviderStats());
    }

    /// <summary>
    /// Configures the provider to fail the next N requests.
    /// </summary>
    /// <param name="providerId">The provider identifier.</param>
    /// <param name="count">Number of requests to fail.</param>
    public void FailNextRequests(string providerId, int count)
    {
        _failNextCounts[providerId] = count;
    }

    /// <summary>
    /// Resets all provider statistics.
    /// </summary>
    public void ResetAllProviderStats()
    {
        foreach (var stats in _stats.Values)
        {
            stats.Reset();
        }

        _failNextCounts.Clear();
        Interlocked.Exchange(ref _totalProviderConnections, 0);
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

        // GET /provider/{providerId}/stream - provider-specific streaming with configurable behavior
        _app.MapGet("/provider/{providerId}/stream", HandleProviderStream);

        // POST /provider/{providerId}/configure - configure provider behavior dynamically
        _app.MapPost(
            "/provider/{providerId}/configure",
            async (string providerId, HttpContext ctx) =>
            {
                try
                {
                    var behavior = await JsonSerializer.DeserializeAsync<ProviderBehavior>(
                        ctx.Request.Body,
                        JsonOptions,
                        ctx.RequestAborted
                    );

                    if (behavior != null)
                    {
                        ConfigureProvider(providerId, behavior);
                        ctx.Response.StatusCode = 200;
                        await ctx.Response.WriteAsync("OK");
                    }
                    else
                    {
                        ctx.Response.StatusCode = 400;
                        await ctx.Response.WriteAsync("Invalid behavior configuration");
                    }
                }
                catch (JsonException ex)
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsync($"JSON error: {ex.Message}");
                }
            }
        );

        // GET /provider/{providerId}/stats - get provider statistics
        _app.MapGet(
            "/provider/{providerId}/stats",
            async (string providerId, HttpContext ctx) =>
            {
                var stats = GetStats(providerId);
                ctx.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(
                    ctx.Response.Body,
                    new
                    {
                        stats.ConnectionAttempts,
                        stats.SuccessfulConnections,
                        stats.FailedConnections,
                        stats.DroppedConnections,
                        stats.BytesServed,
                        stats.RequestsServed,
                    }
                );
            }
        );

        // POST /provider/{providerId}/fail-next/{count} - fail next N requests
        _app.MapPost(
            "/provider/{providerId}/fail-next/{count:int}",
            (string providerId, int count, HttpContext ctx) =>
            {
                FailNextRequests(providerId, count);
                ctx.Response.StatusCode = 200;
                return ctx.Response.WriteAsync($"Will fail next {count} requests for provider {providerId}");
            }
        );

        // POST /provider/{providerId}/reset - reset provider stats
        _app.MapPost(
            "/provider/{providerId}/reset",
            (string providerId, HttpContext ctx) =>
            {
                if (_stats.TryGetValue(providerId, out var stats))
                {
                    stats.Reset();
                }

                _failNextCounts.TryRemove(providerId, out _);
                ctx.Response.StatusCode = 200;
                return ctx.Response.WriteAsync("OK");
            }
        );

        await _app.StartAsync();
    }

    private async Task HandleProviderStream(string providerId, HttpContext ctx)
    {
        Interlocked.Increment(ref _connectionCount);
        Interlocked.Increment(ref _totalProviderConnections);
        var stats = _stats.GetOrAdd(providerId, _ => new ProviderStats());
        stats.IncrementConnectionAttempt();

        var behavior = _behaviors.GetValueOrDefault(providerId) ?? DefaultBehavior;

        // Check for forced failures
        if (_failNextCounts.TryGetValue(providerId, out var failCount) && failCount > 0)
        {
            _failNextCounts[providerId] = failCount - 1;
            stats.IncrementFailed();
            ctx.Response.StatusCode = 503;
            await ctx.Response.WriteAsync("Forced failure");
            return;
        }

        // Check for connection refused simulation
        if (behavior.SimulateConnectionRefused)
        {
            stats.IncrementFailed();
            ctx.Abort();
            return;
        }

        // Check for response timeout simulation (hang forever)
        if (behavior.SimulateResponseTimeout)
        {
            stats.IncrementFailed();
            try
            {
                await Task.Delay(Timeout.Infinite, ctx.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                // Client timeout expected
            }

            return;
        }

        // Random failure based on failure rate
        if (behavior.FailureRate > 0)
        {
            double roll;
            lock (_random)
            {
                roll = _random.NextDouble();
            }

            if (roll < behavior.FailureRate)
            {
                stats.IncrementFailed();
                ctx.Response.StatusCode = 503;
                await ctx.Response.WriteAsync("Random failure");
                return;
            }
        }

        // Apply initial latency
        if (behavior.LatencyMs > 0)
        {
            await Task.Delay(behavior.LatencyMs, ctx.RequestAborted);
        }

        // Start streaming
        stats.IncrementSuccessful();
        ctx.Response.ContentType = "video/mp2t";
        ctx.Response.Headers["Connection"] = "close";

        var generator = new TestStreamGenerator(behavior.BitrateKbps);
        var bytesPerSecond = behavior.BitrateKbps * 1000.0 / 8.0;
        var stopwatch = Stopwatch.StartNew();
        long totalBytesSent = 0;
        const int chunkPackets = 50;

        try
        {
            while (!ctx.RequestAborted.IsCancellationRequested)
            {
                // Check if we should drop the connection
                if (behavior.DropAfterMs > 0 && stopwatch.ElapsedMilliseconds > behavior.DropAfterMs)
                {
                    stats.IncrementDropped();
                    ctx.Abort();
                    return;
                }

                var chunk = generator.GenerateChunk(chunkPackets);

                // Apply corruption if enabled
                if (behavior.CorruptPackets)
                {
                    double roll;
                    lock (_random)
                    {
                        roll = _random.NextDouble();
                    }

                    if (roll < behavior.CorruptionRate)
                    {
                        // Corrupt first packet sync byte
                        chunk[0] = 0xFF;

                        // Set TEI (transport error indicator) on second packet
                        if (chunk.Length >= TsPacketSize * 2)
                        {
                            chunk[TsPacketSize + 1] |= 0x80;
                        }
                    }
                }

                await ctx.Response.Body.WriteAsync(chunk, ctx.RequestAborted);
                totalBytesSent += chunk.Length;
                stats.AddBytes(chunk.Length);

                // Calculate throttle delay with optional slowdown
                double currentLatencyFactor = 1.0;
                if (behavior.SlowdownAfterMs > 0 && stopwatch.ElapsedMilliseconds > behavior.SlowdownAfterMs)
                {
                    currentLatencyFactor = behavior.SlowdownFactor;
                }

                var targetElapsedMs = totalBytesSent / bytesPerSecond * 1000.0 * currentLatencyFactor;
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
            // Client disconnected normally
        }

        stats.IncrementRequests();
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
