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
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.Streaming.Native;

/// <summary>
/// Event arguments for streamer events.
/// </summary>
public sealed class StreamerEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StreamerEventArgs"/> class.
    /// </summary>
    /// <param name="eventType">The event type.</param>
    /// <param name="detail">Additional detail (e.g. HTTP status, curl error code).</param>
    public StreamerEventArgs(StreamerEvent eventType, int detail)
    {
        EventType = eventType;
        Detail = detail;
    }

    /// <summary>
    /// Gets the event type.
    /// </summary>
    public StreamerEvent EventType { get; }

    /// <summary>
    /// Gets the event detail (context-dependent: HTTP status, error code, etc.).
    /// </summary>
    public int Detail { get; }
}

/// <summary>
/// Native HTTP streamer with failover and mid-stream switching.
/// Wraps the native TsDuck interop streamer using SafeHandle and proper IDisposable pattern.
/// </summary>
/// <remarks>
/// Thread safety: Start/Stop/RequestSwitch/GetStatus are safe to call from any thread.
/// The output callback is invoked on the native worker thread.
/// </remarks>
public sealed unsafe class NativeStreamer : IDisposable
{
    private readonly TsDuckStreamerSafeHandle _streamer;
    private readonly ILogger? _logger;

    // Keep delegates alive to prevent GC collection
    private readonly TsDuckNativeMethods.StreamerEventCallbackDelegate _eventDelegate;
    private TsDuckNativeMethods.StreamerOutputCallbackDelegate? _outputDelegate;

    // User-facing output callback (receives pointer + length for zero-copy writes)
    private Action<nint, int>? _outputAction;

    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="NativeStreamer"/> class.
    /// </summary>
    /// <param name="config">Streamer configuration. Uses defaults if null.</param>
    /// <param name="analyzerConfig">Optional analyzer configuration. If provided, an internal analyzer is created.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <exception cref="TsDuckNativeException">Thrown if native streamer creation fails.</exception>
    internal NativeStreamer(
        TsDuckStreamerConfigNative? config = null,
        TsDuckConfigNative? analyzerConfig = null,
        ILogger? logger = null
    )
    {
        _logger = logger;

        // Initialize native logging (idempotent - safe to call multiple times)
        NativeLogging.Initialize(logger);

        var cfg = config ?? TsDuckStreamerConfigNative.Default;
        _streamer = TsDuckStreamerSafeHandle.Create(cfg, analyzerConfig);

        if (_streamer.IsInvalid)
        {
            throw new TsDuckNativeException("Failed to create native streamer");
        }

        // Set up event callback (always active)
        _eventDelegate = OnNativeEventCallback;
        var eventFnPtr = Marshal.GetFunctionPointerForDelegate(_eventDelegate);
        TsDuckNativeMethods.StreamerSetEventCallback(_streamer.DangerousGetHandle(), eventFnPtr, 0);

        _logger?.LogDebugIfEnabled("NativeStreamer created");
    }

    /// <summary>
    /// Creates a new <see cref="NativeStreamer"/> instance, returning null if the native library
    /// is unavailable or creation fails. Use this for graceful degradation.
    /// </summary>
    /// <param name="config">Streamer configuration.</param>
    /// <param name="analyzerConfig">Optional analyzer configuration.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <returns>A new streamer instance, or null if creation failed.</returns>
    internal static NativeStreamer? TryCreate(
        TsDuckStreamerConfigNative? config = null,
        TsDuckConfigNative? analyzerConfig = null,
        ILogger? logger = null
    )
    {
        try
        {
            return new NativeStreamer(config, analyzerConfig, logger);
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
    /// Occurs when a streamer event is raised (connected, disconnected, error, etc.).
    /// </summary>
    public event EventHandler<StreamerEventArgs>? StreamEvent;

    /// <summary>
    /// Adds a URL to the streamer's URL list for failover rotation.
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
    /// Sets the output callback for receiving aligned TS data.
    /// The callback is invoked on the native worker thread with a pointer to the data and its length.
    /// </summary>
    /// <param name="callback">
    /// Callback receiving (dataPointer, length). The data is valid only for the duration of the callback.
    /// Use <c>new ReadOnlySpan&lt;byte&gt;((void*)ptr, length)</c> to access the data.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="callback"/> is null.</exception>
    public void SetOutputCallback(Action<nint, int> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _outputAction = callback;
        _outputDelegate = OnNativeOutputCallback;
        var outputFnPtr = Marshal.GetFunctionPointerForDelegate(_outputDelegate);
        TsDuckNativeMethods.StreamerSetOutputCallback(_streamer.DangerousGetHandle(), outputFnPtr, 0);
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

        // Clear callbacks before disposing handle to prevent callbacks during teardown
        if (!_streamer.IsInvalid)
        {
            TsDuckNativeMethods.StreamerStop(_streamer.DangerousGetHandle());
            TsDuckNativeMethods.StreamerSetEventCallback(_streamer.DangerousGetHandle(), 0, 0);
            TsDuckNativeMethods.StreamerSetOutputCallback(_streamer.DangerousGetHandle(), 0, 0);
        }

        _outputAction = null;
        _outputDelegate = null;

        // SafeHandle calls StreamerDestroy which cleans up the worker thread and all native resources
        _streamer.Dispose();

        _logger?.LogDebugIfEnabled("NativeStreamer disposed");
    }

    private void OnNativeEventCallback(int eventType, int detail, nint userData)
    {
        if (_disposed)
        {
            return;
        }

        var streamerEvent = (StreamerEvent)eventType;
        _logger?.LogDebugIfEnabled("NativeStreamer event: {Event} (detail: {Detail})", streamerEvent, detail);

        StreamEvent?.Invoke(this, new StreamerEventArgs(streamerEvent, detail));
    }

    private void OnNativeOutputCallback(byte* data, int length, nint userData)
    {
        if (_disposed || _outputAction == null)
        {
            return;
        }

        _outputAction((nint)data, length);
    }
}
