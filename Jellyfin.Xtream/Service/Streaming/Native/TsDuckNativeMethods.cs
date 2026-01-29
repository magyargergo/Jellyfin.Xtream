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
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

// Assembly-level attribute to satisfy CA5392 for all P/Invoke methods in this file.
// The actual library loading is controlled by our custom NativeLibrary resolver
// which searches the assembly directory and safe system paths.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]

namespace Jellyfin.Xtream.Service.Streaming.Native;

/// <summary>
/// P/Invoke declarations for the TsDuck interop native library.
/// Uses source-generated marshalling (LibraryImport) for performance.
/// </summary>
internal static partial class TsDuckNativeMethods
{
    /// <summary>
    /// Library name without platform-specific prefix/suffix.
    /// .NET runtime handles loading libtsduck_interop.so on Linux,
    /// tsduck_interop.dll on Windows, etc.
    /// </summary>
    private const string LibraryName = "tsduck_interop";

    /// <summary>
    /// Static constructor to register custom library resolver.
    /// This ensures native libraries are loaded from trusted locations only.
    /// </summary>
    static TsDuckNativeMethods()
    {
        NativeLibrary.SetDllImportResolver(typeof(TsDuckNativeMethods).Assembly, ResolveLibrary);
    }

    /// <summary>
    /// Custom resolver that loads native libraries from safe locations.
    /// Searches assembly directory, NuGet runtimes directory, and system paths.
    /// </summary>
    private static nint ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != LibraryName)
        {
            // Let the default resolver handle other libraries
            return 0;
        }

        var assemblyLocation = assembly.Location;
        var assemblyDirectory = Path.GetDirectoryName(assemblyLocation);

        // Log diagnostic info (will appear in DllNotFoundException if we fail)
        var diagnostics = new System.Text.StringBuilder();
        diagnostics.AppendLine("TsDuck library resolver diagnostics:");
        diagnostics.AppendLine($"  Assembly.Location: {assemblyLocation}");
        diagnostics.AppendLine($"  AssemblyDirectory: {assemblyDirectory}");

        if (!string.IsNullOrEmpty(assemblyDirectory))
        {
            // Get runtimes path for this platform
            var rid = GetRuntimeIdentifier();
            var runtimesPath = Path.Combine(assemblyDirectory, "runtimes", rid, "native");
            diagnostics.AppendLine($"  RuntimesPath: {runtimesPath}");
            diagnostics.AppendLine($"  RuntimesPath exists: {Directory.Exists(runtimesPath)}");

            // Jellyfin-specific path where deploy script installs native libraries
            const string JellyfinBinPath = "/usr/lib/jellyfin/bin";
            var jellyfinBinExists = OperatingSystem.IsLinux() && Directory.Exists(JellyfinBinPath);
            diagnostics.AppendLine($"  JellyfinBinPath exists: {jellyfinBinExists}");

            // Load libtsduck_interop.so (only runtime dependency is libcurl)
            var interopHandle = TryLoadFromPaths(
                libraryName,
                diagnostics,
                "Interop",
                runtimesPath,
                assemblyDirectory,
                jellyfinBinExists ? JellyfinBinPath : null
            );

            if (interopHandle != 0)
            {
                return interopHandle;
            }
        }

        // Fall back to system search paths
        if (NativeLibrary.TryLoad(libraryName, assembly, DllImportSearchPath.SafeDirectories, out var systemHandle))
        {
            return systemHandle;
        }

        // Store diagnostics so they appear in the exception
        _lastResolverDiagnostics = diagnostics.ToString();

        // Return 0 to indicate failure - the runtime will throw DllNotFoundException
        return 0;
    }

    /// <summary>
    /// Stores diagnostics from the last failed resolution attempt.
    /// </summary>
    private static string? _lastResolverDiagnostics;

    /// <summary>
    /// Gets the diagnostics from the last failed library resolution.
    /// </summary>
    internal static string? LastResolverDiagnostics => _lastResolverDiagnostics;

    /// <summary>
    /// Tries to load a library from multiple paths in order.
    /// </summary>
    /// <param name="libraryName">Name of the library without extension.</param>
    /// <param name="diagnostics">StringBuilder for diagnostic logging.</param>
    /// <param name="displayName">Human-readable name for logging.</param>
    /// <param name="paths">Paths to try, in order. Null paths are skipped.</param>
    /// <returns>Handle to loaded library, or 0 if not found.</returns>
    private static nint TryLoadFromPaths(
        string libraryName,
        System.Text.StringBuilder diagnostics,
        string displayName,
        params string?[] paths
    )
    {
        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            var libPath = GetPlatformSpecificLibraryPath(path, libraryName);
            var exists = libPath != null && File.Exists(libPath);
            diagnostics.AppendLine($"  {displayName} path ({path}): {libPath}");
            diagnostics.AppendLine($"  {displayName} exists: {exists}");

            if (exists && NativeLibrary.TryLoad(libPath!, out var handle))
            {
                diagnostics.AppendLine($"  {displayName} loaded: True");
                return handle;
            }
        }

        return 0;
    }

    /// <summary>
    /// Gets the runtime identifier for the current platform.
    /// </summary>
    private static string GetRuntimeIdentifier()
    {
        if (OperatingSystem.IsWindows())
        {
            return Environment.Is64BitProcess ? "win-x64" : "win-x86";
        }

        if (OperatingSystem.IsLinux())
        {
            return Environment.Is64BitProcess ? "linux-x64" : "linux-x86";
        }

        if (OperatingSystem.IsMacOS())
        {
            // Check for ARM64 (Apple Silicon)
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        }

        return "unknown";
    }

    /// <summary>
    /// Gets the platform-specific library path.
    /// </summary>
    private static string? GetPlatformSpecificLibraryPath(string directory, string libraryName)
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(directory, $"{libraryName}.dll");
        }

        if (OperatingSystem.IsLinux())
        {
            return Path.Combine(directory, $"lib{libraryName}.so");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(directory, $"lib{libraryName}.dylib");
        }

        return null;
    }

    // =========================================================================
    // Logging Configuration
    // =========================================================================

    /// <summary>
    /// Native log levels matching TsDuckLogLevel enum in tsduck_interop.h.
    /// </summary>
    internal enum NativeLogLevel
    {
        /// <summary>No logging.</summary>
        None = 0,

        /// <summary>Errors only.</summary>
        Error = 1,

        /// <summary>Warnings and errors.</summary>
        Warning = 2,

        /// <summary>Info, warnings, and errors.</summary>
        Info = 3,

        /// <summary>All messages including debug.</summary>
        Debug = 4,

        /// <summary>Most verbose - includes detailed tracing.</summary>
        Trace = 5,
    }

    /// <summary>
    /// Sets the global log level for native code.
    /// Messages at this level or below will be logged.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_set_log_level")]
    internal static partial void SetLogLevel(int level);

    /// <summary>
    /// Sets a custom log callback to receive log messages from native code.
    /// Pass null (0) to use default stderr output.
    /// </summary>
    /// <param name="callback">Function pointer: void (*)(int32_t level, const char* component, const char* message, void* userData).</param>
    /// <param name="userData">User-provided context pointer passed to callback.</param>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_set_log_callback")]
    internal static unsafe partial void SetLogCallback(
        delegate* unmanaged[Cdecl]<int, nint, nint, nint, void> callback,
        nint userData
    );

    // =========================================================================
    // Context Management
    // =========================================================================

    /// <summary>
    /// Creates a new TSDuck context.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_context_create")]
    internal static partial nint ContextCreate();

    /// <summary>
    /// Destroys a TSDuck context and frees all resources.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_context_destroy")]
    internal static partial void ContextDestroy(nint ctx);

    // =========================================================================
    // Analyzer Lifecycle
    // =========================================================================

    /// <summary>
    /// Creates a new stream analyzer attached to a context.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_analyzer_create")]
    internal static partial nint AnalyzerCreate(nint ctx, in TsDuckConfigNative config);

    /// <summary>
    /// Destroys an analyzer and frees its resources.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_analyzer_destroy")]
    internal static partial void AnalyzerDestroy(nint analyzer);

    // =========================================================================
    // Metrics Retrieval
    // =========================================================================

    /// <summary>
    /// Gets the current metrics snapshot.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_analyzer_get_metrics")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AnalyzerGetMetrics(nint analyzer, out TsDuckMetricsNative metrics);

    // =========================================================================
    // PCR Analysis
    // =========================================================================

    /// <summary>
    /// Gets PCR (Program Clock Reference) analysis metrics.
    /// </summary>
    /// <remarks>
    /// Provides detailed timing analysis including jitter, interval, and drift.
    /// Uses ts::PCRAnalyzer internally.
    /// </remarks>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_analyzer_get_pcr_analysis")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AnalyzerGetPcrAnalysis(nint analyzer, out PcrAnalysisNative analysis);

    // =========================================================================
    // A/V Sync Analysis
    // =========================================================================

    /// <summary>
    /// Gets A/V synchronization analysis.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_analyzer_get_av_sync_analysis")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AnalyzerGetAvSyncAnalysis(nint analyzer, out AvSyncAnalysisNative analysis);

    // =========================================================================
    // HTTP Streamer
    // =========================================================================

    /// <summary>
    /// Creates a new native streamer instance.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_streamer_create")]
    internal static partial nint StreamerCreate(
        in TsDuckStreamerConfigNative config,
        in TsDuckConfigNative analyzerConfig
    );

    /// <summary>
    /// Creates a streamer with default configuration (both null).
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_streamer_create")]
    internal static partial nint StreamerCreateDefault(nint config, nint analyzerConfig);

    /// <summary>
    /// Destroys a streamer and frees all resources.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_streamer_destroy")]
    internal static partial void StreamerDestroy(nint streamer);

    /// <summary>
    /// Adds a URL to the streamer's URL list with default health score.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_streamer_add_url", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int StreamerAddUrl(nint streamer, string url);

    /// <summary>
    /// Adds a URL with specified health score for intelligent selection.
    /// </summary>
    /// <param name="streamer">The streamer handle.</param>
    /// <param name="url">The stream URL.</param>
    /// <param name="healthScore">Health score (0.0-100.0), higher = better. Clamped to range.</param>
    /// <returns>TSDUCK_OK or error code.</returns>
    [LibraryImport(
        LibraryName,
        EntryPoint = "tsduck_streamer_add_url_with_score",
        StringMarshalling = StringMarshalling.Utf8
    )]
    internal static partial int StreamerAddUrlWithScore(nint streamer, string url, double healthScore);

    /// <summary>
    /// Updates the health score for an existing URL by index.
    /// Use this to update scores based on external provider reliability data.
    /// </summary>
    /// <param name="streamer">The streamer handle.</param>
    /// <param name="urlIndex">Index of URL to update (0-based).</param>
    /// <param name="newScore">New health score (0.0-100.0). Clamped to range.</param>
    /// <returns>TSDUCK_OK or TSDUCK_ERROR_INVALID_DATA if index invalid.</returns>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_streamer_update_url_score")]
    internal static partial int StreamerUpdateUrlScore(nint streamer, int urlIndex, double newScore);

    /// <summary>
    /// Gets the health score for a URL by index.
    /// </summary>
    /// <param name="streamer">The streamer handle.</param>
    /// <param name="urlIndex">Index of URL to query (0-based).</param>
    /// <returns>Health score (0.0-100.0) or -1.0 if index invalid.</returns>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_streamer_get_url_score")]
    internal static partial double StreamerGetUrlScore(nint streamer, int urlIndex);

    /// <summary>
    /// Starts streaming (spawns worker thread).
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_streamer_start")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool StreamerStart(nint streamer);

    /// <summary>
    /// Stops streaming (blocks until worker thread exits).
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_streamer_stop")]
    internal static partial void StreamerStop(nint streamer);

    /// <summary>
    /// Requests a switch to the next URL in rotation (async).
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_streamer_request_switch")]
    internal static partial void StreamerRequestSwitch(nint streamer);

    /// <summary>
    /// Gets the current streamer status snapshot (lock-free).
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_streamer_get_status")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool StreamerGetStatus(nint streamer, out TsDuckStreamerStatusNative status);

    /// <summary>
    /// Gets the internal analyzer handle (owned by streamer, do NOT destroy).
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_streamer_get_analyzer")]
    internal static partial nint StreamerGetAnalyzer(nint streamer);

    /// <summary>
    /// Callback signature for streamer events.
    /// Called from the streaming thread on state changes.
    /// </summary>
    /// <param name="eventType">Event type (StreamerEvent enum value).</param>
    /// <param name="detail">Event-specific detail (HTTP status, curl code, URL index).</param>
    /// <param name="userData">User-provided context pointer.</param>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void StreamerEventCallback(int eventType, int detail, nint userData);

    /// <summary>
    /// Sets the event callback for the streamer.
    /// Called from the streaming thread on state changes.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_streamer_set_event_callback")]
    internal static partial void StreamerSetEventCallback(nint streamer, nint callback, nint userData);

    // =========================================================================
    // Streamer Shared Memory Output Mode
    // =========================================================================

    /// <summary>
    /// Configures the streamer to use shared memory for output instead of callbacks.
    /// Must be called before StreamerStart().
    /// </summary>
    /// <param name="streamer">The streamer handle.</param>
    /// <param name="name">Unique name for the shared memory region.</param>
    /// <param name="slotCount">Number of slots in ring buffer (power of 2, 0 for default 1024).</param>
    /// <param name="slotSize">Size of each slot in bytes (multiple of 188, 0 for default 1316).</param>
    /// <returns>TSDUCK_OK on success, error code on failure.</returns>
    [LibraryImport(
        LibraryName,
        EntryPoint = "tsduck_streamer_set_shared_memory_output",
        StringMarshalling = StringMarshalling.Utf8
    )]
    internal static partial int StreamerSetSharedMemoryOutput(
        nint streamer,
        string name,
        uint slotCount,
        uint slotSize
    );

    /// <summary>
    /// Gets the shared memory name configured for the streamer.
    /// </summary>
    /// <param name="streamer">The streamer handle.</param>
    /// <returns>Pointer to name string, or zero if not in shared memory mode.</returns>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_streamer_get_shared_memory_name")]
    internal static partial nint StreamerGetSharedMemoryName(nint streamer);

    /// <summary>
    /// Checks if the streamer is in shared memory output mode.
    /// </summary>
    /// <param name="streamer">The streamer handle.</param>
    /// <returns>1 if in shared memory mode, 0 otherwise.</returns>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_streamer_is_shared_memory_mode")]
    internal static partial int StreamerIsSharedMemoryMode(nint streamer);

    // =========================================================================
    // Shared Memory Producer (for E2E testing)
    // =========================================================================

    /// <summary>
    /// Creates a shared memory producer for streaming TS data.
    /// </summary>
    /// <param name="name">Unique name for the shared memory region.</param>
    /// <param name="slotCount">Number of slots in ring buffer (must be power of 2).</param>
    /// <param name="slotSize">Size of each slot in bytes (must be multiple of 188).</param>
    /// <returns>Handle to the producer, or zero on failure.</returns>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_shm_producer_create", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint ShmProducerCreate(string name, uint slotCount, uint slotSize);

    /// <summary>
    /// Destroys a shared memory producer and frees all resources.
    /// Safe to call with zero handle.
    /// </summary>
    /// <param name="producer">The producer handle.</param>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_shm_producer_destroy")]
    internal static partial void ShmProducerDestroy(nint producer);

    /// <summary>
    /// Writes TS packet data to the shared memory ring buffer.
    /// </summary>
    /// <param name="producer">The producer handle.</param>
    /// <param name="data">Pointer to TS data (must be multiple of 188 bytes).</param>
    /// <param name="length">Number of bytes to write.</param>
    /// <param name="bytesWritten">Output: number of bytes actually written.</param>
    /// <returns>1 if overflow occurred, 0 otherwise, -1 on error.</returns>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_shm_producer_write")]
    internal static partial int ShmProducerWrite(nint producer, nint data, uint length, out uint bytesWritten);

    /// <summary>
    /// Signals the consumer that data is available.
    /// Call after writing a batch of data for efficient wakeup.
    /// </summary>
    /// <param name="producer">The producer handle.</param>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_shm_producer_signal")]
    internal static partial void ShmProducerSignal(nint producer);

    /// <summary>
    /// Sets the end-of-stream flag.
    /// Consumer will complete after reading remaining data.
    /// </summary>
    /// <param name="producer">The producer handle.</param>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_shm_producer_set_eos")]
    internal static partial void ShmProducerSetEndOfStream(nint producer);

    /// <summary>
    /// Sets an error condition.
    /// </summary>
    /// <param name="producer">The producer handle.</param>
    /// <param name="code">Error code.</param>
    /// <param name="message">Human-readable error message.</param>
    [LibraryImport(
        LibraryName,
        EntryPoint = "tsduck_shm_producer_set_error",
        StringMarshalling = StringMarshalling.Utf8
    )]
    internal static partial void ShmProducerSetError(nint producer, uint code, string message);

    /// <summary>
    /// Sets the discontinuity flag.
    /// Consumer should handle stream discontinuity (e.g., after URL switch).
    /// </summary>
    /// <param name="producer">The producer handle.</param>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_shm_producer_set_discontinuity")]
    internal static partial void ShmProducerSetDiscontinuity(nint producer);

    /// <summary>
    /// Clears the discontinuity flag.
    /// </summary>
    /// <param name="producer">The producer handle.</param>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_shm_producer_clear_discontinuity")]
    internal static partial void ShmProducerClearDiscontinuity(nint producer);

    /// <summary>
    /// Checks if consumer is attached to the shared memory region.
    /// </summary>
    /// <param name="producer">The producer handle.</param>
    /// <returns>1 if consumer is attached, 0 otherwise.</returns>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_shm_producer_is_consumer_attached")]
    internal static partial int ShmProducerIsConsumerAttached(nint producer);
}
