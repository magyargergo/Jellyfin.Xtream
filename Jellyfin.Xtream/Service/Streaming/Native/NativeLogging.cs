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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Jellyfin.Xtream.Utility;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.Streaming.Native;

/// <summary>
/// Bridges native TsDuck logging to both the managed ILogger infrastructure and the frontend dashboard.
/// </summary>
/// <remarks>
/// <para>
/// This class is thread-safe and can be initialized once for the application lifetime.
/// Native logs from all TsDuck components (Analyzer, Streamer, etc.) are forwarded to:
/// 1. The configured ILogger (for Docker/console logs)
/// 2. The PluginLogger dashboard (for the frontend log viewer)
/// </para>
/// <para>
/// Uses hybrid filtering for optimal performance:
/// 1. Native code filters at coarse level (synced with ILogger's minimum)
/// 2. Managed callback checks both ILogger.IsEnabled and PluginLogger.IsCapturing
/// 3. String marshaling only occurs if at least one destination will consume the log
/// This avoids P/Invoke overhead and string allocations for completely filtered logs.
/// </para>
/// </remarks>
public static unsafe class NativeLogging
{
    private static readonly object _lock = new();
    private static ILogger? _logger;
    private static bool _initialized;

    /// <summary>
    /// Initializes native logging with the specified logger.
    /// </summary>
    /// <param name="logger">The logger to forward native logs to.</param>
    /// <remarks>
    /// <para>
    /// This method is idempotent - calling it multiple times with different loggers
    /// will update the logger reference and re-sync the native log level.
    /// </para>
    /// <para>
    /// Native log level is synced with ILogger's minimum enabled level for performance.
    /// This prevents native code from calling the callback for logs that ILogger will filter.
    /// </para>
    /// </remarks>
    public static void Initialize(ILogger? logger)
    {
        lock (_lock)
        {
            _logger = logger;

            // Determine native log level based on what ILogger accepts
            var nativeLevel = DetermineNativeLogLevel(logger);

            if (_initialized)
            {
                // Already initialized - just update log level in case config changed
                TsDuckNativeMethods.SetLogLevel((int)nativeLevel);
                return;
            }

            try
            {
                // Register function pointer callback with native code
                TsDuckNativeMethods.SetLogCallback(&OnNativeLog, 0);

                // Set native level to match ILogger's minimum
                // Native filters at coarse level, callback checks IsEnabled for final filtering
                TsDuckNativeMethods.SetLogLevel((int)nativeLevel);

                _initialized = true;

                _logger?.LogDebug("Native TsDuck logging initialized (native level: {NativeLevel})", nativeLevel);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to initialize native logging - native logs will go to stderr");
            }
        }
    }

    /// <summary>
    /// Determines the native log level based on what the ILogger accepts.
    /// </summary>
    /// <param name="logger">The logger to check.</param>
    /// <returns>The native log level that matches ILogger's minimum enabled level.</returns>
    private static TsDuckNativeMethods.NativeLogLevel DetermineNativeLogLevel(ILogger? logger)
    {
        if (logger == null)
        {
            return TsDuckNativeMethods.NativeLogLevel.Warning;
        }

        // Check levels from most verbose to least verbose
        // Return the most verbose level that ILogger accepts
        return logger.IsEnabled(LogLevel.Trace)
            ? TsDuckNativeMethods.NativeLogLevel.Trace
            : logger.IsEnabled(LogLevel.Debug)
            ? TsDuckNativeMethods.NativeLogLevel.Debug
            : logger.IsEnabled(LogLevel.Information)
            ? TsDuckNativeMethods.NativeLogLevel.Info
            : logger.IsEnabled(LogLevel.Warning)
            ? TsDuckNativeMethods.NativeLogLevel.Warning
            : TsDuckNativeMethods.NativeLogLevel.Error;
    }

    /// <summary>
    /// Shuts down native logging and restores default behavior.
    /// </summary>
    public static void Shutdown()
    {
        lock (_lock)
        {
            if (!_initialized)
            {
                return;
            }

            try
            {
                // Clear callback - native code will fall back to stderr
                TsDuckNativeMethods.SetLogCallback(callback: null, 0);
            }
            catch
            {
                // Ignore errors during shutdown
            }

            _logger = null;
            _initialized = false;
        }
    }

    /// <summary>
    /// Gets a value indicating whether native logging is initialized.
    /// </summary>
    public static bool IsInitialized
    {
        get
        {
            lock (_lock)
            {
                return _initialized;
            }
        }
    }

    /// <summary>
    /// Callback invoked by native code for each log message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method uses [UnmanagedCallersOnly] for zero-overhead native callbacks.
    /// It must not throw exceptions or use any managed features that could trigger GC.
    /// </para>
    /// <para>
    /// Logs are sent to two destinations:
    /// 1. ILogger - for Docker/console logs (if logger.IsEnabled returns true)
    /// 2. PluginLogger.DirectLog - for frontend dashboard (if PluginLogger.IsCapturing is true)
    /// </para>
    /// <para>
    /// Performance optimization: We check both destinations BEFORE marshaling strings to avoid
    /// ~100-200ns + GC allocations for logs that will be completely filtered.
    /// </para>
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeLog(int level, nint componentPtr, nint messagePtr, nint userData)
    {
        var logger = _logger;

        try
        {
            // Map level FIRST before any string marshaling
            var logLevel = MapLogLevel((TsDuckNativeMethods.NativeLogLevel)level);

            // Determine if we need to do anything
            var loggerEnabled = logger?.IsEnabled(logLevel) ?? false;
            var dashboardCapturing = PluginLogger.IsCapturing;

            // Early exit if nothing will consume this log
            // This avoids expensive string marshaling for completely filtered logs
            if (!loggerEnabled && !dashboardCapturing)
            {
                return;
            }

            // Only marshal strings if we'll actually log to at least one destination
            var component = componentPtr != 0 ? Marshal.PtrToStringAnsi(componentPtr) : "Native";
            var message = messagePtr != 0 ? Marshal.PtrToStringAnsi(messagePtr) : string.Empty;
            var formattedMessage = $"[{component}] {message}";

            // Send to ILogger (for Docker/console logs)
            if (loggerEnabled)
            {
                logger!.Log(logLevel, "[{Component}] {Message}", component, message);
            }

            // Send to frontend dashboard via PluginLogger
            // DirectLog checks IsCapturing internally, but we already checked so this is safe
            if (dashboardCapturing)
            {
                PluginLogger.DirectLog(logLevel, $"Native.{component}", formattedMessage);
            }
        }
        catch
        {
            // Never throw from callback - could crash native code
        }
    }

    /// <summary>
    /// Maps native log level to .NET LogLevel.
    /// </summary>
    private static LogLevel MapLogLevel(TsDuckNativeMethods.NativeLogLevel nativeLevel)
    {
        return nativeLevel switch
        {
            TsDuckNativeMethods.NativeLogLevel.Error => LogLevel.Error,
            TsDuckNativeMethods.NativeLogLevel.Warning => LogLevel.Warning,
            TsDuckNativeMethods.NativeLogLevel.Info => LogLevel.Information,
            TsDuckNativeMethods.NativeLogLevel.Debug => LogLevel.Debug,
            TsDuckNativeMethods.NativeLogLevel.Trace => LogLevel.Trace,
            _ => LogLevel.None,
        };
    }
}
