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
using System.Buffers;
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
    // Library Initialization
    // =========================================================================

    /// <summary>
    /// Gets the TSDuck library version string.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_get_version")]
    private static partial nint GetVersionPtr();

    /// <summary>
    /// Gets the TSDuck version as a managed string.
    /// </summary>
    internal static string? GetVersion()
    {
        var ptr = GetVersionPtr();
        return ptr != 0 ? Marshal.PtrToStringAnsi(ptr) : null;
    }

    /// <summary>
    /// Checks if TSDuck is available on this system.
    /// Uses SuppressGCTransition for minimal overhead on this trivial check.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_is_available")]
    [SuppressGCTransition]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsAvailable();

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
    /// Gets the current native log level.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_get_log_level")]
    [SuppressGCTransition]
    internal static partial int GetLogLevel();

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

    /// <summary>
    /// Checks if a specific log level is enabled.
    /// Useful for avoiding expensive operations when logging is disabled.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_is_log_enabled")]
    [SuppressGCTransition]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsLogEnabled(int level);

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

    /// <summary>
    /// Checks if the context is valid and TSDuck is operational.
    /// Uses SuppressGCTransition for minimal overhead on this trivial check.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_context_is_available")]
    [SuppressGCTransition]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ContextIsAvailable(nint ctx);

    // =========================================================================
    // Analyzer Lifecycle
    // =========================================================================

    /// <summary>
    /// Creates a new stream analyzer attached to a context.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_analyzer_create")]
    internal static partial nint AnalyzerCreate(nint ctx, in TsDuckConfigNative config);

    /// <summary>
    /// Creates analyzer with default configuration.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_analyzer_create")]
    internal static partial nint AnalyzerCreateDefault(nint ctx, nint config);

    /// <summary>
    /// Destroys an analyzer and frees its resources.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_analyzer_destroy")]
    internal static partial void AnalyzerDestroy(nint analyzer);

    /// <summary>
    /// Checks if the analyzer is initialized and ready.
    /// Uses SuppressGCTransition for minimal overhead on this trivial check.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_analyzer_is_initialized")]
    [SuppressGCTransition]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AnalyzerIsInitialized(nint analyzer);

    // =========================================================================
    // Data Processing
    // =========================================================================

    /// <summary>
    /// Feeds MPEG-TS data to the analyzer.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_analyzer_feed")]
    internal static partial int AnalyzerFeed(nint analyzer, nint data, int length);

    /// <summary>
    /// Feeds MPEG-TS data to the analyzer using ReadOnlySpan.
    /// </summary>
    internal static unsafe int AnalyzerFeed(nint analyzer, ReadOnlySpan<byte> data)
    {
        fixed (byte* ptr = data)
        {
            return AnalyzerFeed(analyzer, (nint)ptr, data.Length);
        }
    }

    /// <summary>
    /// Resets the analyzer state for a new stream.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_analyzer_reset")]
    internal static partial void AnalyzerReset(nint analyzer);

    /// <summary>
    /// Feeds MPEG-TS data with integrated restamping.
    /// This function modifies data IN-PLACE to apply timestamp corrections.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_analyzer_feed_restamp")]
    internal static partial int AnalyzerFeedRestamp(nint analyzer, nint data, int length);

    /// <summary>
    /// Feeds MPEG-TS data with integrated restamping using Span (modifies in-place).
    /// </summary>
    internal static unsafe int AnalyzerFeedRestamp(nint analyzer, Span<byte> data)
    {
        fixed (byte* ptr = data)
        {
            return AnalyzerFeedRestamp(analyzer, (nint)ptr, data.Length);
        }
    }

    // =========================================================================
    // Integrated Restamping (through Analyzer)
    // =========================================================================

    /// <summary>
    /// Configures integrated restamping on an analyzer.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_analyzer_configure_restamp")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AnalyzerConfigureRestamp(
        nint analyzer,
        int mode,
        int smoothPcr,
        int fixDiscontinuities
    );

    /// <summary>
    /// Notifies analyzer's integrated restamper of a provider switch.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_analyzer_handle_switch")]
    internal static partial void AnalyzerHandleSwitch(nint analyzer, long lastOutputPts, long newInputFirstPts);

    /// <summary>
    /// Gets integrated restamping statistics from analyzer.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_analyzer_get_restamp_statistics")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AnalyzerGetRestampStatistics(nint analyzer, out RestampingStatisticsNative stats);

    /// <summary>
    /// Checks if integrated restamping is enabled on the analyzer.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_analyzer_is_restamping_enabled")]
    [SuppressGCTransition]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AnalyzerIsRestampingEnabled(nint analyzer);

    // =========================================================================
    // Metrics Retrieval
    // =========================================================================

    /// <summary>
    /// Gets the current metrics snapshot.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_analyzer_get_metrics")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AnalyzerGetMetrics(nint analyzer, out TsDuckMetricsNative metrics);

    /// <summary>
    /// Checks if new metrics are available since last retrieval.
    /// Uses SuppressGCTransition for minimal overhead on this trivial check.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_analyzer_has_new_metrics")]
    [SuppressGCTransition]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AnalyzerHasNewMetrics(nint analyzer);

    // =========================================================================
    // PCR Analysis (Phase 2a)
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

    /// <summary>
    /// Sets the PCR jitter threshold for violation alerts.
    /// </summary>
    /// <param name="analyzer">The analyzer handle.</param>
    /// <param name="maxJitterUs">Maximum acceptable jitter in microseconds. TR 101 290 limit is 0.5us (500ns).</param>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_analyzer_set_pcr_jitter_threshold")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AnalyzerSetPcrJitterThreshold(nint analyzer, double maxJitterUs);

    // =========================================================================
    // IAT (Inter-packet Arrival Time) Analysis (Phase 2a)
    // =========================================================================

    /// <summary>
    /// Gets IAT (Inter-packet Arrival Time) analysis metrics.
    /// </summary>
    /// <remarks>
    /// Provides network jitter analysis for UDP/IP streams.
    /// Detects congestion before TS-level errors manifest.
    /// </remarks>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_analyzer_get_iat_analysis")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AnalyzerGetIatAnalysis(nint analyzer, out IatAnalysisNative analysis);

    /// <summary>
    /// Sets the IAT jitter threshold for violation alerts.
    /// </summary>
    /// <param name="analyzer">The analyzer handle.</param>
    /// <param name="maxJitterUs">Maximum acceptable jitter in microseconds. Default is 1000us (1ms).</param>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_analyzer_set_iat_jitter_threshold")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AnalyzerSetIatJitterThreshold(nint analyzer, double maxJitterUs);

    // =========================================================================
    // Bitrate Analysis (Phase 2a)
    // =========================================================================

    /// <summary>
    /// Gets detailed bitrate analysis metrics.
    /// </summary>
    /// <remarks>
    /// Provides PCR-based bitrate, null packet ratio, and bandwidth utilization.
    /// </remarks>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_analyzer_get_bitrate_analysis")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AnalyzerGetBitrateAnalysis(nint analyzer, out BitrateAnalysisNative analysis);

    // =========================================================================
    // Extended PID Information (Phase 2b)
    // =========================================================================

    /// <summary>
    /// Gets the number of PIDs currently being tracked.
    /// Uses SuppressGCTransition for minimal overhead on this trivial accessor.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_analyzer_get_pid_count")]
    [SuppressGCTransition]
    internal static partial int AnalyzerGetPidCount(nint analyzer);

    /// <summary>
    /// Gets extended information for all PIDs currently being tracked.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_analyzer_get_pid_info_extended")]
    internal static partial int AnalyzerGetPidInfoExtended(nint analyzer, nint outPids, int maxPids);

    /// <summary>
    /// Maximum PID count to prevent unbounded allocation.
    /// MPEG-TS supports PIDs 0-8191, so 8192 is the theoretical maximum.
    /// </summary>
    private const int MaxPidCount = 8192;

    /// <summary>
    /// Gets extended PID information using a managed array.
    /// Uses ArrayPool to reduce GC pressure for the intermediate native array.
    /// </summary>
    internal static unsafe TsDuckPidInfoExtended[] GetPidInfoExtended(nint analyzer)
    {
        int count = AnalyzerGetPidCount(analyzer);
        if (count <= 0)
        {
            return [];
        }

        // Clamp to maximum to prevent malicious/corrupted counts from causing huge allocations
        count = Math.Min(count, MaxPidCount);

        // Rent from pool instead of allocating - reduces GC pressure
        var nativeArray = ArrayPool<TsDuckPidInfoExtendedNative>.Shared.Rent(count);
        try
        {
            int actualCount;
            fixed (TsDuckPidInfoExtendedNative* ptr = nativeArray)
            {
                actualCount = AnalyzerGetPidInfoExtended(analyzer, (nint)ptr, count);
            }

            if (actualCount <= 0)
            {
                return [];
            }

            // Convert to managed array (this allocation is unavoidable for the return value)
            var result = new TsDuckPidInfoExtended[actualCount];
            for (int i = 0; i < actualCount; i++)
            {
                result[i] = nativeArray[i].ToManaged();
            }

            return result;
        }
        finally
        {
            ArrayPool<TsDuckPidInfoExtendedNative>.Shared.Return(nativeArray);
        }
    }

    // =========================================================================
    // Callbacks
    // =========================================================================

    /// <summary>
    /// Delegate for metrics update callbacks.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal unsafe delegate void MetricsCallbackDelegate(TsDuckMetricsNative* metrics, nint userData);

    /// <summary>
    /// Delegate for violation callbacks.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ViolationCallbackDelegate(nint violationType, nint details, nint userData);

    /// <summary>
    /// Sets the metrics update callback.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_analyzer_set_metrics_callback")]
    internal static partial void AnalyzerSetMetricsCallback(
        nint analyzer,
        MetricsCallbackDelegate? callback,
        nint userData
    );

    /// <summary>
    /// Sets the violation callback.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_analyzer_set_violation_callback")]
    internal static partial void AnalyzerSetViolationCallback(
        nint analyzer,
        ViolationCallbackDelegate? callback,
        nint userData
    );

    // =========================================================================
    // A/V Sync Analysis (Phase 3 - Restamping)
    // =========================================================================

    /// <summary>
    /// Gets A/V synchronization analysis.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_analyzer_get_av_sync_analysis")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AnalyzerGetAvSyncAnalysis(nint analyzer, out AvSyncAnalysisNative analysis);

    /// <summary>
    /// Gets the number of PTS/DTS samples currently buffered.
    /// Uses SuppressGCTransition for minimal overhead on this trivial accessor.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_analyzer_get_pts_sample_count")]
    [SuppressGCTransition]
    internal static partial int AnalyzerGetPtsSampleCount(nint analyzer);

    /// <summary>
    /// Gets recent PTS/DTS samples for detailed analysis.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_analyzer_get_pts_samples")]
    internal static partial int AnalyzerGetPtsSamples(nint analyzer, nint outSamples, int maxSamples);

    /// <summary>
    /// Maximum PTS sample count to prevent unbounded allocation.
    /// </summary>
    private const int MaxPtsSampleCount = 128;

    /// <summary>
    /// Gets PTS/DTS samples using a managed array.
    /// </summary>
    internal static unsafe PtsDtsSample[] GetPtsSamples(nint analyzer, int maxSamples = MaxPtsSampleCount)
    {
        int count = AnalyzerGetPtsSampleCount(analyzer);
        if (count <= 0)
        {
            return [];
        }

        count = Math.Min(count, Math.Min(maxSamples, MaxPtsSampleCount));

        var nativeArray = ArrayPool<PtsDtsSampleNative>.Shared.Rent(count);
        try
        {
            int actualCount;
            fixed (PtsDtsSampleNative* ptr = nativeArray)
            {
                actualCount = AnalyzerGetPtsSamples(analyzer, (nint)ptr, count);
            }

            if (actualCount <= 0)
            {
                return [];
            }

            var result = new PtsDtsSample[actualCount];
            for (int i = 0; i < actualCount; i++)
            {
                result[i] = nativeArray[i].ToManaged();
            }

            return result;
        }
        finally
        {
            ArrayPool<PtsDtsSampleNative>.Shared.Return(nativeArray);
        }
    }

    // =========================================================================
    // HTTP Streamer
    // =========================================================================

    /// <summary>
    /// Delegate for streamer event callbacks.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void StreamerEventCallbackDelegate(int eventType, int detail, nint userData);

    /// <summary>
    /// Delegate for streamer data output callbacks.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal unsafe delegate void StreamerOutputCallbackDelegate(byte* data, int length, nint userData);

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
    /// Adds a URL to the streamer's URL list.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_streamer_add_url", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int StreamerAddUrl(nint streamer, string url);

    /// <summary>
    /// Clears all URLs from the streamer.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_streamer_clear_urls")]
    internal static partial void StreamerClearUrls(nint streamer);

    /// <summary>
    /// Sets the output file descriptor (pipe for FFmpeg).
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_streamer_set_output_fd")]
    internal static partial void StreamerSetOutputFd(nint streamer, int fd);

    /// <summary>
    /// Sets the output data callback.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_streamer_set_output_callback")]
    internal static partial void StreamerSetOutputCallback(nint streamer, nint callback, nint userData);

    /// <summary>
    /// Sets the event callback.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_streamer_set_event_callback")]
    internal static partial void StreamerSetEventCallback(nint streamer, nint callback, nint userData);

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

    // =========================================================================
    // Function Pointers for Hot Path Optimization
    // =========================================================================
    // Using unmanaged function pointers instead of LibraryImport for the
    // hottest paths provides ~5-10% performance improvement by avoiding
    // the P/Invoke stub overhead on each call.

    /// <summary>
    /// Lock object for thread-safe initialization.
    /// </summary>
    private static readonly object _initLock = new();

    /// <summary>
    /// Handle to the loaded interop library for function pointer resolution.
    /// </summary>
    private static nint _interopLibraryHandle;

    /// <summary>
    /// Function pointer for analyzer feed - the hottest path.
    /// Signature: int tsduck_analyzer_feed(TsDuckAnalyzerHandle analyzer, const uint8_t* data, int32_t length)
    /// </summary>
    private static unsafe delegate* unmanaged[Cdecl]<nint, byte*, int, int> _analyzerFeedPtr;

    /// <summary>
    /// Function pointer for analyzer feed with restamping - hot path when restamping enabled.
    /// Signature: int tsduck_analyzer_feed_restamp(TsDuckAnalyzerHandle analyzer, uint8_t* data, int32_t length)
    /// </summary>
    private static unsafe delegate* unmanaged[Cdecl]<nint, byte*, int, int> _analyzerFeedRestampPtr;

    /// <summary>
    /// Indicates whether function pointers have been initialized.
    /// </summary>
    private static volatile bool _functionPointersInitialized;

    /// <summary>
    /// Initializes function pointers for hot path optimization.
    /// Thread-safe, idempotent - can be called multiple times safely.
    /// </summary>
    internal static unsafe void InitializeFunctionPointers()
    {
        if (_functionPointersInitialized)
        {
            return;
        }

        lock (_initLock)
        {
            if (_functionPointersInitialized)
            {
                return;
            }

            try
            {
                // Try to load the library handle if not already loaded
                if (_interopLibraryHandle == 0)
                {
                    // Use the same resolution logic as the DllImportResolver
                    var assemblyLocation = typeof(TsDuckNativeMethods).Assembly.Location;
                    var assemblyDirectory = Path.GetDirectoryName(assemblyLocation);

                    if (!string.IsNullOrEmpty(assemblyDirectory))
                    {
                        var rid = GetRuntimeIdentifier();
                        var runtimesPath = Path.Combine(assemblyDirectory, "runtimes", rid, "native");
                        var libPath =
                            GetPlatformSpecificLibraryPath(runtimesPath, LibraryName)
                            ?? GetPlatformSpecificLibraryPath(assemblyDirectory, LibraryName);

                        if (libPath != null && File.Exists(libPath))
                        {
                            _ = NativeLibrary.TryLoad(libPath, out _interopLibraryHandle);
                        }
                    }

                    // Fall back to system search
                    if (_interopLibraryHandle == 0)
                    {
                        _ = NativeLibrary.TryLoad(
                            LibraryName,
                            typeof(TsDuckNativeMethods).Assembly,
                            DllImportSearchPath.SafeDirectories,
                            out _interopLibraryHandle
                        );
                    }
                }

                if (_interopLibraryHandle != 0)
                {
                    // Get function pointer for the hot path (read-only feed)
                    var feedPtr = NativeLibrary.GetExport(_interopLibraryHandle, "tsduck_analyzer_feed");
                    if (feedPtr != 0)
                    {
                        _analyzerFeedPtr = (delegate* unmanaged[Cdecl]<nint, byte*, int, int>)feedPtr;
                    }

                    // Get function pointer for feed with restamping (modifies data in-place)
                    var feedRestampPtr = NativeLibrary.GetExport(_interopLibraryHandle, "tsduck_analyzer_feed_restamp");
                    if (feedRestampPtr != 0)
                    {
                        _analyzerFeedRestampPtr = (delegate* unmanaged[Cdecl]<nint, byte*, int, int>)feedRestampPtr;
                    }
                }

                _functionPointersInitialized = true;
            }
            catch
            {
                // If function pointer initialization fails, we'll fall back to LibraryImport
                _functionPointersInitialized = true;
            }
        }
    }

    /// <summary>
    /// Feeds MPEG-TS data to the analyzer using optimized function pointer.
    /// Falls back to LibraryImport if function pointer not available.
    /// </summary>
    internal static unsafe int AnalyzerFeedOptimized(nint analyzer, ReadOnlySpan<byte> data)
    {
        if (!_functionPointersInitialized)
        {
            InitializeFunctionPointers();
        }

        fixed (byte* ptr = data)
        {
            if (_analyzerFeedPtr != null)
            {
                return _analyzerFeedPtr(analyzer, ptr, data.Length);
            }

            // Fallback to standard P/Invoke
            return AnalyzerFeed(analyzer, (nint)ptr, data.Length);
        }
    }

    /// <summary>
    /// Feeds MPEG-TS data with integrated restamping using optimized function pointer.
    /// Data is modified in-place to apply timestamp corrections before analysis.
    /// Falls back to LibraryImport if function pointer not available.
    /// </summary>
    internal static unsafe int AnalyzerFeedRestampOptimized(nint analyzer, Span<byte> data)
    {
        if (!_functionPointersInitialized)
        {
            InitializeFunctionPointers();
        }

        fixed (byte* ptr = data)
        {
            if (_analyzerFeedRestampPtr != null)
            {
                return _analyzerFeedRestampPtr(analyzer, ptr, data.Length);
            }

            // Fallback to standard P/Invoke
            return AnalyzerFeedRestamp(analyzer, (nint)ptr, data.Length);
        }
    }

    /// <summary>
    /// Gets whether function pointers are available for hot path optimization.
    /// </summary>
    internal static bool AreFunctionPointersAvailable
    {
        get
        {
            if (!_functionPointersInitialized)
            {
                InitializeFunctionPointers();
            }

            unsafe
            {
                return _analyzerFeedPtr != null;
            }
        }
    }
}
