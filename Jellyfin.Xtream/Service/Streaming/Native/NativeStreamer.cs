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
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.Streaming.Native;

/// <summary>
/// Callback delegate for receiving streamer events from the native streamer.
/// </summary>
/// <param name="eventType">The type of event.</param>
/// <param name="detail">Event-specific detail (HTTP status, curl code, URL index).</param>
public delegate void NativeEventCallback(StreamerEvent eventType, int detail);

/// <summary>
/// Native HTTP streamer with failover and mid-stream switching.
/// Wraps the native TsDuck interop streamer using SafeHandle and proper IDisposable pattern.
/// </summary>
/// <remarks>
/// <para>
/// This is a thin wrapper that sets up providers with URLs and initial health scores.
/// C++ handles all decision making: URL selection, failover, health tracking.
/// Data is transferred via shared memory for high-performance zero-copy IPC.
/// </para>
/// <para>
/// Thread safety: Start/Stop/RequestSwitch/GetStatus are safe to call from any thread.
/// Event callbacks are invoked from the native worker thread.
/// </para>
/// </remarks>
public sealed class NativeStreamer : IDisposable
{
    private readonly TsDuckStreamerSafeHandle _streamer;
    private readonly ILogger? _logger;

    // Keep delegate instance alive to prevent GC during native callback
    private TsDuckNativeMethods.StreamerEventCallback? _nativeEventCallback;
    private GCHandle _eventCallbackHandle;

    // User-provided event callback
    private NativeEventCallback? _eventCallback;

    // Shared memory mode
    private string? _sharedMemoryName;

    // Registry integration (keep reference to prevent GC)
    private NativeChannelRegistry? _registry;

    // Network configuration (keep copy for diagnostics)
    private NetworkConfigNative? _networkConfig;

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
    /// Sets the event callback that receives events from the native streamer.
    /// Must be called before Start(). The callback is invoked from the native worker thread.
    /// </summary>
    /// <param name="callback">The callback to receive events, or null to disable.</param>
    /// <exception cref="InvalidOperationException">Thrown if the streamer has been disposed.</exception>
    public void SetEventCallback(NativeEventCallback? callback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _eventCallback = callback;

        if (callback == null)
        {
            // Clear native callback FIRST to prevent new invocations
            // This ensures the native worker thread will no longer call the delegate
            // before we free the GCHandle that keeps it alive
            TsDuckNativeMethods.StreamerSetEventCallback(_streamer.DangerousGetHandle(), 0, 0);

            // Memory barrier ensures native side sees the cleared callback
            Thread.MemoryBarrier();

            // Now safe to free the GCHandle since native will no longer call the callback
            if (_eventCallbackHandle.IsAllocated)
            {
                _eventCallbackHandle.Free();
            }

            _nativeEventCallback = null;
            return;
        }

        // Create native callback that wraps the managed one
        _nativeEventCallback = NativeEventCallbackHandler;
        _eventCallbackHandle = GCHandle.Alloc(_nativeEventCallback);

        var callbackPtr = Marshal.GetFunctionPointerForDelegate(_nativeEventCallback);
        TsDuckNativeMethods.StreamerSetEventCallback(_streamer.DangerousGetHandle(), callbackPtr, 0);
    }

    /// <summary>
    /// Gets the shared memory name if configured in shared memory mode.
    /// </summary>
    public string? SharedMemoryName => _sharedMemoryName;

    /// <summary>
    /// Gets whether the streamer is in shared memory output mode.
    /// </summary>
    public bool IsSharedMemoryMode
    {
        get
        {
            if (_disposed)
            {
                return false;
            }

            return TsDuckNativeMethods.StreamerIsSharedMemoryMode(_streamer.DangerousGetHandle()) != 0;
        }
    }

    /// <summary>
    /// Sets the channel registry for GUID-based URL lookups.
    /// When a registry is set, call <see cref="SetChannelGuid"/> to specify which channel to stream.
    /// The streamer will use the registry to populate URLs on start and track health internally.
    /// </summary>
    /// <param name="registry">The registry to use for URL lookups, or null to clear.</param>
    /// <remarks>
    /// The registry must be built before start(). The registry is kept alive by the streamer
    /// until cleared or the streamer is disposed.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown if the streamer has been disposed.</exception>
    public void SetRegistry(NativeChannelRegistry? registry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var registryHandle = registry?.DangerousGetHandle() ?? 0;
        var result = TsDuckNativeMethods.StreamerSetRegistry(_streamer.DangerousGetHandle(), registryHandle);

        if (result != 0)
        {
            throw new InvalidOperationException($"Failed to set registry: error {result}");
        }

        // Keep reference to prevent GC while streamer uses it
        _registry = registry;
        _logger?.LogDebugIfEnabled("NativeStreamer registry set: {HasRegistry}", registry != null);
    }

    /// <summary>
    /// Sets the channel GUID for registry-based streaming.
    /// When the registry is set and GUID is valid, Start() will populate URLs from the registry.
    /// The streamer handles all URL selection, health tracking, and failover internally.
    /// </summary>
    /// <param name="channelGuid">The channel GUID to stream.</param>
    /// <remarks>
    /// The GUID is split into high and low 64-bit parts for C interop.
    /// Call <see cref="SetRegistry"/> first with a built registry.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown if the streamer has been disposed.</exception>
    public void SetChannelGuid(Guid channelGuid)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Convert GUID to two int64 values (big-endian byte order)
        var bytes = channelGuid.ToByteArray();
        var guidHigh = BitConverter.ToInt64(bytes, 0);
        var guidLow = BitConverter.ToInt64(bytes, 8);

        var result = TsDuckNativeMethods.StreamerSetChannelGuid(_streamer.DangerousGetHandle(), guidHigh, guidLow);

        if (result != 0)
        {
            throw new InvalidOperationException($"Failed to set channel GUID: error {result}");
        }

        _logger?.LogDebugIfEnabled("NativeStreamer channel GUID set: {Guid}", channelGuid);
    }

    /// <summary>
    /// Sets the network configuration for the streamer.
    /// Must be called before <see cref="Start"/>.
    /// </summary>
    /// <param name="config">The network configuration to apply.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="config"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the streamer has been disposed or configuration fails.</exception>
    /// <remarks>
    /// This method allows customization of DNS resolution, connection timeouts,
    /// TCP keepalive settings, and other network parameters. The configuration
    /// is applied to all subsequent connections made by the streamer.
    /// </remarks>
    public void SetNetworkConfig(NetworkConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var native = config.ToNative();
        _networkConfig = native;

        var result = TsDuckNativeMethods.StreamerSetNetworkConfig(_streamer.DangerousGetHandle(), in native);
        if (result != 0)
        {
            throw new InvalidOperationException($"Failed to set network config: error {result}");
        }

        _logger?.LogDebugIfEnabled(
            "NativeStreamer network config set: DnsMode={DnsMode}, IpResolve={IpResolve}",
            (DnsResolveMode)native.DnsMode,
            (IpResolveMode)native.IpResolveMode
        );
    }

    /// <summary>
    /// Gets the last DNS error that occurred during streaming.
    /// </summary>
    /// <returns>
    /// A <see cref="DnsErrorType"/> indicating the last DNS error,
    /// or <see cref="DnsErrorType.None"/> if no error occurred or the streamer is disposed.
    /// </returns>
    /// <remarks>
    /// This method is useful for diagnosing connection failures. After a connection
    /// error event, check this property to determine if DNS resolution was the cause.
    /// </remarks>
    public DnsErrorType GetLastDnsError()
    {
        if (_disposed)
        {
            return DnsErrorType.None;
        }

        return (DnsErrorType)TsDuckNativeMethods.StreamerGetLastDnsError(_streamer.DangerousGetHandle());
    }

    /// <summary>
    /// Configures the streamer to use shared memory for output instead of callbacks.
    /// Must be called before Start(). This disables the output callback.
    /// </summary>
    /// <param name="name">Unique name for the shared memory region.</param>
    /// <param name="slotCount">Number of slots in ring buffer (power of 2, 0 for default 1024).</param>
    /// <param name="slotSize">Size of each slot in bytes (multiple of 188, 0 for default 1316).</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="name"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown if configuration fails.</exception>
    public void SetSharedMemoryOutput(string name, uint slotCount = 0, uint slotSize = 0)
    {
        ArgumentNullException.ThrowIfNull(name);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var result = TsDuckNativeMethods.StreamerSetSharedMemoryOutput(
            _streamer.DangerousGetHandle(),
            name,
            slotCount,
            slotSize
        );

        if (result != 0)
        {
            throw new InvalidOperationException($"Failed to set shared memory output: error {result}");
        }

        _sharedMemoryName = name;
        _logger?.LogDebugIfEnabled("NativeStreamer configured for shared memory: {Name}", name);
    }

    /// <summary>
    /// Native event callback handler that marshals events to the managed callback.
    /// </summary>
    private void NativeEventCallbackHandler(int eventType, int detail, nint userData)
    {
        if (_disposed || _eventCallback == null)
        {
            return;
        }

        try
        {
            _eventCallback((StreamerEvent)eventType, detail);
        }
        catch (Exception ex)
        {
            _logger?.PluginLogWarning(ex, "Exception in event callback: {Message}", ex.Message);
        }
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

    // =========================================================================
    // Provider Health System (E2E testing and diagnostics)
    // =========================================================================

    /// <summary>
    /// Gets the current state of a provider.
    /// </summary>
    /// <param name="providerIndex">Index of the provider (0-based).</param>
    /// <returns>The provider state, or <see cref="ProviderState.Ejected"/> if disposed or invalid index.</returns>
    public ProviderState GetProviderState(int providerIndex)
    {
        if (_disposed)
        {
            return ProviderState.Ejected;
        }

        var state = TsDuckNativeMethods.StreamerGetProviderState(_streamer.DangerousGetHandle(), providerIndex);
        return state < 0 ? ProviderState.Ejected : (ProviderState)state;
    }

    /// <summary>
    /// Gets detailed health snapshot for a provider.
    /// </summary>
    /// <param name="providerIndex">Index of the provider (0-based).</param>
    /// <returns>The health snapshot, or null if unavailable.</returns>
    public ProviderHealthSnapshot? GetProviderHealth(int providerIndex)
    {
        if (_disposed)
        {
            return null;
        }

        if (
            TsDuckNativeMethods.StreamerGetProviderHealth(_streamer.DangerousGetHandle(), providerIndex, out var native)
        )
        {
            return native.ToManaged();
        }

        return null;
    }

    /// <summary>
    /// Gets the number of times a provider's circuit has been opened.
    /// </summary>
    /// <param name="providerIndex">Index of the provider (0-based).</param>
    /// <returns>Isolated times count, or -1 if unavailable.</returns>
    public int GetIsolatedTimes(int providerIndex)
    {
        if (_disposed)
        {
            return -1;
        }

        return TsDuckNativeMethods.StreamerGetIsolatedTimes(_streamer.DangerousGetHandle(), providerIndex);
    }

    /// <summary>
    /// Runs outlier detection manually (for testing).
    /// Normally called periodically by the health system.
    /// </summary>
    public void RunOutlierDetection()
    {
        if (_disposed)
        {
            return;
        }

        TsDuckNativeMethods.StreamerRunOutlierDetection(_streamer.DangerousGetHandle());
    }

    /// <summary>
    /// Gets the number of registered providers in the health system.
    /// </summary>
    /// <returns>Provider count, or 0 if unavailable.</returns>
    public int GetProviderCount()
    {
        if (_disposed)
        {
            return 0;
        }

        return TsDuckNativeMethods.StreamerGetProviderCount(_streamer.DangerousGetHandle());
    }

    /// <summary>
    /// Resets all providers to Active state (for testing).
    /// </summary>
    public void ResetAllProviders()
    {
        if (_disposed)
        {
            return;
        }

        TsDuckNativeMethods.StreamerResetAllProviders(_streamer.DangerousGetHandle());
        _logger?.LogDebugIfEnabled("NativeStreamer: all providers reset to Active");
    }

    /// <summary>
    /// Force ejects a provider (for testing).
    /// </summary>
    /// <param name="providerIndex">Index of the provider (0-based).</param>
    /// <param name="durationMs">Duration of ejection in milliseconds.</param>
    public void ForceEjectProvider(int providerIndex, int durationMs)
    {
        if (_disposed)
        {
            return;
        }

        TsDuckNativeMethods.StreamerForceEjectProvider(_streamer.DangerousGetHandle(), providerIndex, durationMs);
        _logger?.LogDebugIfEnabled(
            "NativeStreamer: provider {Index} force ejected for {Duration}ms",
            providerIndex,
            durationMs
        );
    }

    // ========================================================================
    // DNS Failure Tracking (E2E testing)
    // ========================================================================

    /// <summary>
    /// Gets the current DNS failure count for a provider.
    /// </summary>
    /// <param name="providerIndex">Index of the provider (0-based).</param>
    /// <returns>DNS failure count, or 0 if disposed or invalid index.</returns>
    public int GetDnsFailureCount(int providerIndex)
    {
        if (_disposed)
        {
            return 0;
        }

        return TsDuckNativeMethods.StreamerGetDnsFailureCount(_streamer.DangerousGetHandle(), providerIndex);
    }

    /// <summary>
    /// Simulates a DNS failure for testing purposes.
    /// Returns the resulting policy: 0=Switch, 1=EjectAndSwitch.
    /// </summary>
    /// <param name="providerIndex">Index of the provider (0-based).</param>
    /// <returns>DNS failure policy (0=Switch, 1=EjectAndSwitch), or -1 on error.</returns>
    public DnsFailurePolicy SimulateDnsFailure(int providerIndex)
    {
        if (_disposed)
        {
            return DnsFailurePolicy.Switch;
        }

        var result = TsDuckNativeMethods.StreamerSimulateDnsFailure(_streamer.DangerousGetHandle(), providerIndex);
        _logger?.LogDebugIfEnabled(
            "NativeStreamer: simulated DNS failure for provider {Index}, policy={Policy}",
            providerIndex,
            result
        );
        return result < 0 ? DnsFailurePolicy.Switch : (DnsFailurePolicy)result;
    }

    /// <summary>
    /// Resets DNS failure count for a provider (simulates successful connection).
    /// </summary>
    /// <param name="providerIndex">Index of the provider (0-based).</param>
    public void ResetDnsFailureCount(int providerIndex)
    {
        if (_disposed)
        {
            return;
        }

        TsDuckNativeMethods.StreamerResetDnsFailureCount(_streamer.DangerousGetHandle(), providerIndex);
        _logger?.LogDebugIfEnabled("NativeStreamer: DNS failure count reset for provider {Index}", providerIndex);
    }

    /// <summary>
    /// Check for provider recovery from ejection.
    /// Triggers ejection expiry checks and transitions providers from Ejected to Probation
    /// when their quarantine period has elapsed.
    /// </summary>
    public void CheckRecovery()
    {
        if (_disposed)
        {
            return;
        }

        TsDuckNativeMethods.StreamerCheckRecovery(_streamer.DangerousGetHandle());
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // CRITICAL: Clear native callback registration FIRST to prevent new callbacks.
        // This ensures the native worker thread will stop invoking our delegate
        // before we mark ourselves as disposed or free resources.
        if (!_streamer.IsInvalid)
        {
            TsDuckNativeMethods.StreamerSetEventCallback(_streamer.DangerousGetHandle(), 0, 0);
        }

        // Memory barrier ensures native side sees the cleared callback before we proceed
        Thread.MemoryBarrier();

        // Now safe to mark as disposed - any in-flight callback will complete
        // but no new callbacks will start
        _disposed = true;
        _eventCallback = null;

        // Stop streaming before disposing handle
        if (!_streamer.IsInvalid)
        {
            TsDuckNativeMethods.StreamerStop(_streamer.DangerousGetHandle());
        }

        // Free GCHandle for event callback (safe now since native callback is cleared)
        if (_eventCallbackHandle.IsAllocated)
        {
            _eventCallbackHandle.Free();
        }

        // SafeHandle calls StreamerDestroy which cleans up the worker thread
        // and all native resources
        _streamer.Dispose();

        // Clear registry reference (the registry is NOT owned by streamer, just referenced)
        _registry = null;

        _logger?.LogDebugIfEnabled("NativeStreamer disposed");
    }
}
