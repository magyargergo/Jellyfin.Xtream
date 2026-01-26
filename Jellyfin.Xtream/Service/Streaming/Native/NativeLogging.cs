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
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.Streaming.Native;

/// <summary>
/// Bridges native TsDuck logging to the managed ILogger infrastructure.
/// </summary>
/// <remarks>
/// This class is thread-safe and can be initialized once for the application lifetime.
/// Native logs from all TsDuck components (Analyzer, Streamer, etc.) are forwarded to
/// the configured ILogger with appropriate log levels.
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
    /// <param name="enableDebugLogging">
    /// If true, sets native log level to Debug. Otherwise defaults to Warning.
    /// </param>
    /// <remarks>
    /// This method is idempotent - calling it multiple times with different loggers
    /// will update the logger reference but not re-register the callback.
    /// </remarks>
    public static void Initialize(ILogger? logger, bool enableDebugLogging = false)
    {
        lock (_lock)
        {
            _logger = logger;

            if (_initialized)
            {
                // Already initialized, just update the log level
                var level = enableDebugLogging
                    ? TsDuckNativeMethods.NativeLogLevel.Debug
                    : TsDuckNativeMethods.NativeLogLevel.Warning;
                TsDuckNativeMethods.SetLogLevel((int)level);
                return;
            }

            try
            {
                // Register function pointer callback with native code
                TsDuckNativeMethods.SetLogCallback(&OnNativeLog, 0);

                // Set log level based on debug mode
                var logLevel = enableDebugLogging
                    ? TsDuckNativeMethods.NativeLogLevel.Debug
                    : TsDuckNativeMethods.NativeLogLevel.Warning;
                TsDuckNativeMethods.SetLogLevel((int)logLevel);

                _initialized = true;

                _logger?.LogDebug("Native TsDuck logging initialized (level: {Level})", logLevel);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to initialize native logging - native logs will go to stderr");
            }
        }
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
                TsDuckNativeMethods.SetLogCallback(null, 0);
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
    /// This method uses [UnmanagedCallersOnly] for zero-overhead native callbacks.
    /// It must not throw exceptions or use any managed features that could trigger GC.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeLog(int level, nint componentPtr, nint messagePtr, nint userData)
    {
        var logger = _logger;
        if (logger == null)
        {
            return;
        }

        try
        {
            var component = componentPtr != 0 ? Marshal.PtrToStringAnsi(componentPtr) : "Native";
            var message = messagePtr != 0 ? Marshal.PtrToStringAnsi(messagePtr) : string.Empty;

            var logLevel = MapLogLevel((TsDuckNativeMethods.NativeLogLevel)level);

            // Use structured logging with component tag
            logger.Log(logLevel, "[{Component}] {Message}", component, message);
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
