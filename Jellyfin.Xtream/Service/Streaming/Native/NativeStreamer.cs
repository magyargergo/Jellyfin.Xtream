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
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.Streaming.Native;

/// <summary>
/// Native HTTP streamer with failover and mid-stream switching.
/// Wraps the native TsDuck interop streamer using SafeHandle and proper IDisposable pattern.
/// </summary>
/// <remarks>
/// <para>
/// This is a thin wrapper that sets up providers with URLs and initial health scores.
/// C++ handles all decision making: URL selection, failover, health tracking.
/// C# reads buffer data from shared memory.
/// </para>
/// <para>
/// Thread safety: Start/Stop/RequestSwitch/GetStatus are safe to call from any thread.
/// </para>
/// </remarks>
public sealed class NativeStreamer : IDisposable
{
    /// <summary>
    /// Default shared buffer size (64MB).
    /// </summary>
    public const long DefaultSharedBufferSize = 67108864L;

    private readonly TsDuckStreamerSafeHandle _streamer;
    private readonly ILogger? _logger;
    private readonly string _sharedBufferName;
    private readonly long _sharedBufferSize;

    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="NativeStreamer"/> class.
    /// </summary>
    /// <param name="config">Streamer configuration. Uses defaults if null.</param>
    /// <param name="analyzerConfig">Optional analyzer configuration. If provided, an internal analyzer is created.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <param name="sharedBufferName">Name for the shared memory region. Auto-generated if null.</param>
    /// <param name="sharedBufferSize">Size of the shared buffer in bytes.</param>
    /// <exception cref="TsDuckNativeException">Thrown if native streamer creation fails.</exception>
    internal NativeStreamer(
        TsDuckStreamerConfigNative? config = null,
        TsDuckConfigNative? analyzerConfig = null,
        ILogger? logger = null,
        string? sharedBufferName = null,
        long sharedBufferSize = DefaultSharedBufferSize
    )
    {
        _logger = logger;
        _sharedBufferName = sharedBufferName ?? $"xtream-stream-{Guid.NewGuid():N}";
        _sharedBufferSize = sharedBufferSize;

        // Initialize native logging (idempotent - safe to call multiple times)
        NativeLogging.Initialize(logger);

        var cfg = config ?? TsDuckStreamerConfigNative.Default;
        _streamer = TsDuckStreamerSafeHandle.Create(cfg, analyzerConfig);

        if (_streamer.IsInvalid)
        {
            throw new TsDuckNativeException("Failed to create native streamer");
        }

        _logger?.LogDebugIfEnabled(
            "NativeStreamer created with shared buffer: {BufferName} ({SizeMB}MB)",
            _sharedBufferName,
            _sharedBufferSize / 1048576.0
        );
    }

    /// <summary>
    /// Creates a new <see cref="NativeStreamer"/> instance, returning null if the native library
    /// is unavailable or creation fails. Use this for graceful degradation.
    /// </summary>
    /// <param name="config">Streamer configuration.</param>
    /// <param name="analyzerConfig">Optional analyzer configuration.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <param name="sharedBufferName">Name for the shared memory region. Auto-generated if null.</param>
    /// <param name="sharedBufferSize">Size of the shared buffer in bytes.</param>
    /// <returns>A new streamer instance, or null if creation failed.</returns>
    internal static NativeStreamer? TryCreate(
        TsDuckStreamerConfigNative? config = null,
        TsDuckConfigNative? analyzerConfig = null,
        ILogger? logger = null,
        string? sharedBufferName = null,
        long sharedBufferSize = DefaultSharedBufferSize
    )
    {
        try
        {
            return new NativeStreamer(config, analyzerConfig, logger, sharedBufferName, sharedBufferSize);
        }
        catch (TsDuckNativeException ex)
        {
            logger?.PluginLogWarning(ex, "Native streamer unavailable: {Message}", ex.Message);
            return null;
        }
        catch (DllNotFoundException ex)
        {
            logger?.PluginLogWarning(ex, "Native TsDuck library not found: {Message}", ex.Message);
            return null;
        }
        catch (EntryPointNotFoundException ex)
        {
            logger?.PluginLogWarning(ex, "Native TsDuck entry point missing: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Gets the name of the shared memory buffer for reading stream data.
    /// </summary>
    public string SharedBufferName => _sharedBufferName;

    /// <summary>
    /// Gets the size of the shared memory buffer in bytes.
    /// </summary>
    public long SharedBufferSize => _sharedBufferSize;

    /// <summary>
    /// Adds a URL to the streamer's URL list for failover rotation with default health score.
    /// </summary>
    /// <param name="url">The stream URL (HTTP/HTTPS).</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="url"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the streamer has been disposed.</exception>
    public void AddUrl(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var result = TsDuckNativeMethods.StreamerAddUrl(_streamer.DangerousGetHandle(), url);
        if (result != 0)
        {
            throw new InvalidOperationException($"Failed to add URL: error {result}");
        }
    }

    /// <summary>
    /// Adds a URL with specified health score for intelligent provider selection.
    /// The native streamer uses health scores to prioritize URLs during failover and rotation.
    /// </summary>
    /// <param name="url">The stream URL (HTTP/HTTPS).</param>
    /// <param name="healthScore">Health score (0.0-100.0), higher = better. Values are clamped to valid range.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="url"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the streamer has been disposed or add fails.</exception>
    public void AddUrlWithScore(string url, double healthScore)
    {
        ArgumentNullException.ThrowIfNull(url);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var result = TsDuckNativeMethods.StreamerAddUrlWithScore(_streamer.DangerousGetHandle(), url, healthScore);
        if (result != 0)
        {
            throw new InvalidOperationException($"Failed to add URL with score: error {result}");
        }
    }

    /// <summary>
    /// Updates the health score for an existing URL by index.
    /// Note: C++ handles health tracking internally via SQLite.
    /// This method allows external score updates if needed.
    /// </summary>
    /// <param name="urlIndex">Index of URL to update (0-based).</param>
    /// <param name="newScore">New health score (0.0-100.0). Values are clamped to valid range.</param>
    /// <exception cref="InvalidOperationException">Thrown if the streamer has been disposed or index is invalid.</exception>
    public void UpdateUrlScore(int urlIndex, double newScore)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var result = TsDuckNativeMethods.StreamerUpdateUrlScore(_streamer.DangerousGetHandle(), urlIndex, newScore);
        if (result != 0)
        {
            throw new InvalidOperationException($"Failed to update URL score at index {urlIndex}: error {result}");
        }
    }

    /// <summary>
    /// Gets the current health score for a URL by index.
    /// C++ maintains and updates these scores based on streaming outcomes.
    /// </summary>
    /// <param name="urlIndex">Index of URL to query (0-based).</param>
    /// <returns>Health score (0.0-100.0), or -1.0 if the index is invalid.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the streamer has been disposed.</exception>
    public double GetUrlScore(int urlIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return TsDuckNativeMethods.StreamerGetUrlScore(_streamer.DangerousGetHandle(), urlIndex);
    }

    /// <summary>
    /// Creates a shared memory reader for consuming stream data.
    /// Call this after Start() to read from the buffer that C++ writes to.
    /// </summary>
    /// <returns>A new SharedMemoryReader instance. Caller is responsible for disposal.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the streamer has been disposed.</exception>
    /// <exception cref="System.IO.FileNotFoundException">Thrown if shared memory hasn't been created yet (call Start first).</exception>
    public SharedMemoryReader CreateReader()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return new SharedMemoryReader(_sharedBufferName, _sharedBufferSize);
    }

    /// <summary>
    /// Starts streaming. Spawns a native worker thread that connects to the first URL
    /// and begins the data pipeline (fetch → align → restamp → output).
    /// </summary>
    /// <returns>True if started successfully; false if already running or no URLs configured.</returns>
    public bool Start()
    {
        if (_disposed)
        {
            return false;
        }

        var started = TsDuckNativeMethods.StreamerStart(_streamer.DangerousGetHandle());
        if (started)
        {
            _logger?.LogDebugIfEnabled("NativeStreamer started");
        }

        return started;
    }

    /// <summary>
    /// Stops streaming. Blocks until the native worker thread exits.
    /// Safe to call multiple times.
    /// </summary>
    public void Stop()
    {
        if (_disposed)
        {
            return;
        }

        TsDuckNativeMethods.StreamerStop(_streamer.DangerousGetHandle());
        _logger?.LogDebugIfEnabled("NativeStreamer stopped");
    }

    /// <summary>
    /// Requests an asynchronous switch to the next URL in rotation.
    /// The worker thread will disconnect the current source, rotate URLs,
    /// and reconnect with timestamp continuity via the restamper.
    /// </summary>
    public void RequestSwitch()
    {
        if (_disposed)
        {
            return;
        }

        TsDuckNativeMethods.StreamerRequestSwitch(_streamer.DangerousGetHandle());
        _logger?.LogDebugIfEnabled("NativeStreamer switch requested");
    }

    /// <summary>
    /// Gets a snapshot of the current streamer status.
    /// Lock-free; safe to call from any thread while streaming.
    /// </summary>
    /// <returns>The current status, or a default idle status if disposed or unavailable.</returns>
    public StreamerStatus GetStatus()
    {
        if (_disposed)
        {
            return default;
        }

        if (TsDuckNativeMethods.StreamerGetStatus(_streamer.DangerousGetHandle(), out var native))
        {
            return native.ToManaged();
        }

        return default;
    }

    /// <summary>
    /// Gets the current metrics from the streamer's internal analyzer.
    /// Returns null if the streamer has no analyzer or is disposed.
    /// </summary>
    /// <returns>The current metrics snapshot, or null if unavailable.</returns>
    public TsDuckMetrics? GetMetrics()
    {
        if (_disposed)
        {
            return null;
        }

        var analyzerHandle = TsDuckNativeMethods.StreamerGetAnalyzer(_streamer.DangerousGetHandle());
        if (analyzerHandle == 0)
        {
            return null;
        }

        if (TsDuckNativeMethods.AnalyzerGetMetrics(analyzerHandle, out var native))
        {
            return native.ToManaged();
        }

        return null;
    }

    /// <summary>
    /// Gets PCR analysis from the streamer's internal analyzer.
    /// </summary>
    /// <returns>PCR analysis data, or null if unavailable.</returns>
    public PcrAnalysis? GetPcrAnalysis()
    {
        if (_disposed)
        {
            return null;
        }

        var analyzerHandle = TsDuckNativeMethods.StreamerGetAnalyzer(_streamer.DangerousGetHandle());
        if (analyzerHandle == 0)
        {
            return null;
        }

        return TsDuckNativeMethods.AnalyzerGetPcrAnalysis(analyzerHandle, out var native) ? native.ToManaged() : null;
    }

    /// <summary>
    /// Gets A/V synchronization analysis from the streamer's internal analyzer.
    /// </summary>
    /// <returns>A/V sync analysis data, or null if unavailable.</returns>
    public AvSyncAnalysis? GetAvSyncAnalysis()
    {
        if (_disposed)
        {
            return null;
        }

        var analyzerHandle = TsDuckNativeMethods.StreamerGetAnalyzer(_streamer.DangerousGetHandle());
        if (analyzerHandle == 0)
        {
            return null;
        }

        return TsDuckNativeMethods.AnalyzerGetAvSyncAnalysis(analyzerHandle, out var native)
            ? native.ToManaged()
            : null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Stop streaming before disposing handle
        if (!_streamer.IsInvalid)
        {
            TsDuckNativeMethods.StreamerStop(_streamer.DangerousGetHandle());
        }

        // SafeHandle calls StreamerDestroy which cleans up the worker thread,
        // shared memory, and all native resources
        _streamer.Dispose();

        _logger?.LogDebugIfEnabled("NativeStreamer disposed");
    }
}
