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

namespace Jellyfin.Xtream.Service.MpegTs.TsDuck.Native;

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
    /// Name of the TsDuck core library (libtscore.so) - basic utilities.
    /// libtsduck.so depends on this, so it must be loaded first.
    /// </summary>
    private const string TsDuckCoreLibrary = "tscore";

    /// <summary>
    /// Name of the TsDuck runtime library (libtsduck.so) - TS processing.
    /// libtsduck_interop.so depends on this.
    /// </summary>
    private const string TsDuckRuntimeLibrary = "tsduck";

    /// <summary>
    /// Handle to preloaded TsDuck core library (libtscore.so).
    /// Must be loaded before libtsduck.so since it depends on it.
    /// </summary>
    private static nint _tsduckCoreHandle;

    /// <summary>
    /// Handle to preloaded TsDuck runtime library (libtsduck.so).
    /// Must be kept alive so the dependency remains loaded when libtsduck_interop needs it.
    /// </summary>
    private static nint _tsduckRuntimeHandle;

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
        diagnostics.AppendLine($"TsDuck library resolver diagnostics:");
        diagnostics.AppendLine($"  Assembly.Location: {assemblyLocation}");
        diagnostics.AppendLine($"  AssemblyDirectory: {assemblyDirectory}");

        if (!string.IsNullOrEmpty(assemblyDirectory))
        {
            // Get runtimes path for this platform
            var rid = GetRuntimeIdentifier();
            var runtimesPath = Path.Combine(assemblyDirectory, "runtimes", rid, "native");
            diagnostics.AppendLine($"  RuntimesPath: {runtimesPath}");
            diagnostics.AppendLine($"  RuntimesPath exists: {Directory.Exists(runtimesPath)}");

            // CRITICAL: Load dependencies in order before loading libtsduck_interop.so
            // Dependency chain: libtsduck_interop.so -> libtsduck.so -> libtscore.so
            // The dynamic linker won't find them in non-standard paths unless we preload them

            // Jellyfin-specific path where deploy script installs native libraries
            const string JellyfinBinPath = "/usr/lib/jellyfin/bin";
            var jellyfinBinExists = OperatingSystem.IsLinux() && Directory.Exists(JellyfinBinPath);
            diagnostics.AppendLine($"  JellyfinBinPath exists: {jellyfinBinExists}");

            // Step 1: Load libtscore.so FIRST (libtsduck.so depends on it)
            if (_tsduckCoreHandle == 0)
            {
                _tsduckCoreHandle = TryLoadFromPaths(
                    TsDuckCoreLibrary,
                    diagnostics,
                    "TsDuck core",
                    runtimesPath,
                    assemblyDirectory,
                    jellyfinBinExists ? JellyfinBinPath : null
                );
            }

            // Step 2: Load libtsduck.so (libtsduck_interop.so depends on it)
            if (_tsduckRuntimeHandle == 0)
            {
                _tsduckRuntimeHandle = TryLoadFromPaths(
                    TsDuckRuntimeLibrary,
                    diagnostics,
                    "TsDuck runtime",
                    runtimesPath,
                    assemblyDirectory,
                    jellyfinBinExists ? JellyfinBinPath : null
                );
            }

            // Step 3: Now load libtsduck_interop.so from same paths
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
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_is_available")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsAvailable();

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
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_context_is_available")]
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
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_analyzer_is_initialized")]
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
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_analyzer_has_new_metrics")]
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
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "tsduck_analyzer_get_pid_count")]
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
}
