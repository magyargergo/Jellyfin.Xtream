// Copyright (C) 2025  Gergo Magyar

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service.Logging;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Utility;

/// <summary>
/// Centralized logging utility for the Xtream plugin.
/// Provides extension methods for ILogger that integrate with the plugin log viewer.
/// Zero-overhead when log capture is disabled.
/// </summary>
public static partial class PluginLogger
{
    private static IPluginLogService? _logService;
    private static IPluginConfigurationProvider? _configProvider;
    private static volatile bool _isCapturing;

    /// <summary>
    /// Gets whether log capture is currently active.
    /// </summary>
    public static bool IsCapturing => _isCapturing;

    /// <summary>
    /// Initializes the plugin logger with the log service instance.
    /// </summary>
    /// <param name="logService">The log service to use.</param>
    /// <param name="configProvider">Optional configuration provider for debug logging state.</param>
    public static void Initialize(IPluginLogService? logService, IPluginConfigurationProvider? configProvider = null)
    {
        _logService = logService;
        _configProvider = configProvider;
        _isCapturing = logService != null;
    }

    /// <summary>
    /// Enables or disables log capture at runtime.
    /// </summary>
    /// <param name="enabled">Whether to enable capture.</param>
    public static void SetCaptureEnabled(bool enabled) => _isCapturing = enabled && _logService != null;

    /// <summary>
    /// Logs a debug message if debug logging is enabled.
    /// When enabled, logs at Information level for better visibility.
    /// </summary>
    /// <typeparam name="T">The type of the logger.</typeparam>
    /// <param name="logger">The logger instance.</param>
    /// <param name="message">The message to log.</param>
    /// <param name="args">Optional formatting arguments.</param>
    [SuppressMessage(
        "Usage",
        "CA2254:Template should be a static expression",
        Justification = "Dynamic logging wrapper for debug control"
    )]
    public static void LogDebugIfEnabled<T>(this ILogger<T> logger, string message, params object?[] args)
    {
        if (!IsDebugEnabled())
        {
            return;
        }

        logger.Log(LogLevel.Information, message, args);
        if (_isCapturing)
        {
            CaptureLog(LogLevel.Information, typeof(T).Name, message, args, exception: null, isDebug: true);
        }
    }

    /// <summary>
    /// Logs a debug message if debug logging is enabled (non-generic ILogger overload).
    /// When enabled, logs at Information level for better visibility.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="message">The message to log.</param>
    /// <param name="args">Optional formatting arguments.</param>
    [SuppressMessage(
        "Usage",
        "CA2254:Template should be a static expression",
        Justification = "Dynamic logging wrapper for debug control"
    )]
    public static void LogDebugIfEnabled(this ILogger logger, string message, params object?[] args)
    {
        if (!IsDebugEnabled())
        {
            return;
        }

        logger.Log(LogLevel.Information, message, args);
        if (_isCapturing)
        {
            CaptureLog(LogLevel.Information, "Plugin", message, args, exception: null, isDebug: true);
        }
    }

    /// <summary>
    /// Logs a debug message with exception if debug logging is enabled.
    /// When enabled, logs at Information level for better visibility.
    /// </summary>
    /// <typeparam name="T">The type of the logger.</typeparam>
    /// <param name="logger">The logger instance.</param>
    /// <param name="exception">The exception to log.</param>
    /// <param name="message">The message to log.</param>
    /// <param name="args">Optional formatting arguments.</param>
    [SuppressMessage(
        "Usage",
        "CA2254:Template should be a static expression",
        Justification = "Dynamic logging wrapper for debug control"
    )]
    public static void LogDebugIfEnabled<T>(
        this ILogger<T> logger,
        Exception exception,
        string message,
        params object?[] args
    )
    {
        if (!IsDebugEnabled())
        {
            return;
        }

        logger.Log(LogLevel.Information, exception, message, args);
        if (_isCapturing)
        {
            CaptureLog(LogLevel.Information, typeof(T).Name, message, args, exception, isDebug: true);
        }
    }

    /// <summary>
    /// Logs a debug message with exception if debug logging is enabled (non-generic ILogger overload).
    /// When enabled, logs at Information level for better visibility.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="exception">The exception to log.</param>
    /// <param name="message">The message to log.</param>
    /// <param name="args">Optional formatting arguments.</param>
    [SuppressMessage(
        "Usage",
        "CA2254:Template should be a static expression",
        Justification = "Dynamic logging wrapper for debug control"
    )]
    public static void LogDebugIfEnabled(
        this ILogger logger,
        Exception exception,
        string message,
        params object?[] args
    )
    {
        if (!IsDebugEnabled())
        {
            return;
        }

        logger.Log(LogLevel.Information, exception, message, args);
        if (_isCapturing)
        {
            CaptureLog(LogLevel.Information, "Plugin", message, args, exception, isDebug: true);
        }
    }

    /// <summary>
    /// Logs an information message and captures it to the plugin log viewer.
    /// </summary>
    /// <typeparam name="T">The type of the logger.</typeparam>
    /// <param name="logger">The logger instance.</param>
    /// <param name="message">The message to log.</param>
    /// <param name="args">Optional formatting arguments.</param>
    [SuppressMessage(
        "Usage",
        "CA2254:Template should be a static expression",
        Justification = "Dynamic logging wrapper for centralized capture"
    )]
    public static void PluginLogInformation<T>(this ILogger<T> logger, string message, params object?[] args)
    {
        logger.Log(LogLevel.Information, message, args);
        if (_isCapturing)
        {
            CaptureLog(LogLevel.Information, typeof(T).Name, message, args, exception: null, isDebug: false);
        }
    }

    /// <summary>
    /// Logs an information message and captures it to the plugin log viewer.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="message">The message to log.</param>
    /// <param name="args">Optional formatting arguments.</param>
    [SuppressMessage(
        "Usage",
        "CA2254:Template should be a static expression",
        Justification = "Dynamic logging wrapper for centralized capture"
    )]
    public static void PluginLogInformation(this ILogger logger, string message, params object?[] args)
    {
        logger.Log(LogLevel.Information, message, args);
        if (_isCapturing)
        {
            CaptureLog(LogLevel.Information, "Plugin", message, args, exception: null, isDebug: false);
        }
    }

    /// <summary>
    /// Logs a warning message and captures it to the plugin log viewer.
    /// </summary>
    /// <typeparam name="T">The type of the logger.</typeparam>
    /// <param name="logger">The logger instance.</param>
    /// <param name="message">The message to log.</param>
    /// <param name="args">Optional formatting arguments.</param>
    [SuppressMessage(
        "Usage",
        "CA2254:Template should be a static expression",
        Justification = "Dynamic logging wrapper for centralized capture"
    )]
    public static void PluginLogWarning<T>(this ILogger<T> logger, string message, params object?[] args)
    {
        logger.Log(LogLevel.Warning, message, args);
        if (_isCapturing)
        {
            CaptureLog(LogLevel.Warning, typeof(T).Name, message, args, exception: null, isDebug: false);
        }
    }

    /// <summary>
    /// Logs a warning message with exception and captures it to the plugin log viewer.
    /// </summary>
    /// <typeparam name="T">The type of the logger.</typeparam>
    /// <param name="logger">The logger instance.</param>
    /// <param name="exception">The exception to log.</param>
    /// <param name="message">The message to log.</param>
    /// <param name="args">Optional formatting arguments.</param>
    [SuppressMessage(
        "Usage",
        "CA2254:Template should be a static expression",
        Justification = "Dynamic logging wrapper for centralized capture"
    )]
    public static void PluginLogWarning<T>(
        this ILogger<T> logger,
        Exception exception,
        string message,
        params object?[] args
    )
    {
        logger.Log(LogLevel.Warning, exception, message, args);
        if (_isCapturing)
        {
            CaptureLog(LogLevel.Warning, typeof(T).Name, message, args, exception, isDebug: false);
        }
    }

    /// <summary>
    /// Logs an error message and captures it to the plugin log viewer.
    /// </summary>
    /// <typeparam name="T">The type of the logger.</typeparam>
    /// <param name="logger">The logger instance.</param>
    /// <param name="message">The message to log.</param>
    /// <param name="args">Optional formatting arguments.</param>
    [SuppressMessage(
        "Usage",
        "CA2254:Template should be a static expression",
        Justification = "Dynamic logging wrapper for centralized capture"
    )]
    public static void PluginLogError<T>(this ILogger<T> logger, string message, params object?[] args)
    {
        logger.Log(LogLevel.Error, message, args);
        if (_isCapturing)
        {
            CaptureLog(LogLevel.Error, typeof(T).Name, message, args, exception: null, isDebug: false);
        }
    }

    /// <summary>
    /// Logs an error message with exception and captures it to the plugin log viewer.
    /// </summary>
    /// <typeparam name="T">The type of the logger.</typeparam>
    /// <param name="logger">The logger instance.</param>
    /// <param name="exception">The exception to log.</param>
    /// <param name="message">The message to log.</param>
    /// <param name="args">Optional formatting arguments.</param>
    [SuppressMessage(
        "Usage",
        "CA2254:Template should be a static expression",
        Justification = "Dynamic logging wrapper for centralized capture"
    )]
    public static void PluginLogError<T>(
        this ILogger<T> logger,
        Exception exception,
        string message,
        params object?[] args
    )
    {
        logger.Log(LogLevel.Error, exception, message, args);
        if (_isCapturing)
        {
            CaptureLog(LogLevel.Error, typeof(T).Name, message, args, exception, isDebug: false);
        }
    }

    /// <summary>
    /// Logs a warning message and captures it to the plugin log viewer (non-generic ILogger overload).
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="message">The message to log.</param>
    /// <param name="args">Optional formatting arguments.</param>
    [SuppressMessage(
        "Usage",
        "CA2254:Template should be a static expression",
        Justification = "Dynamic logging wrapper for centralized capture"
    )]
    public static void PluginLogWarning(this ILogger logger, string message, params object?[] args)
    {
        logger.Log(LogLevel.Warning, message, args);
        if (_isCapturing)
        {
            CaptureLog(LogLevel.Warning, "Plugin", message, args, exception: null, isDebug: false);
        }
    }

    /// <summary>
    /// Logs a warning message with exception and captures it to the plugin log viewer (non-generic ILogger overload).
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="exception">The exception to log.</param>
    /// <param name="message">The message to log.</param>
    /// <param name="args">Optional formatting arguments.</param>
    [SuppressMessage(
        "Usage",
        "CA2254:Template should be a static expression",
        Justification = "Dynamic logging wrapper for centralized capture"
    )]
    public static void PluginLogWarning(this ILogger logger, Exception exception, string message, params object?[] args)
    {
        logger.Log(LogLevel.Warning, exception, message, args);
        if (_isCapturing)
        {
            CaptureLog(LogLevel.Warning, "Plugin", message, args, exception, isDebug: false);
        }
    }

    /// <summary>
    /// Logs an error message and captures it to the plugin log viewer (non-generic ILogger overload).
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="message">The message to log.</param>
    /// <param name="args">Optional formatting arguments.</param>
    [SuppressMessage(
        "Usage",
        "CA2254:Template should be a static expression",
        Justification = "Dynamic logging wrapper for centralized capture"
    )]
    public static void PluginLogError(this ILogger logger, string message, params object?[] args)
    {
        logger.Log(LogLevel.Error, message, args);
        if (_isCapturing)
        {
            CaptureLog(LogLevel.Error, "Plugin", message, args, exception: null, isDebug: false);
        }
    }

    /// <summary>
    /// Logs an error message with exception and captures it to the plugin log viewer (non-generic ILogger overload).
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="exception">The exception to log.</param>
    /// <param name="message">The message to log.</param>
    /// <param name="args">Optional formatting arguments.</param>
    [SuppressMessage(
        "Usage",
        "CA2254:Template should be a static expression",
        Justification = "Dynamic logging wrapper for centralized capture"
    )]
    public static void PluginLogError(this ILogger logger, Exception exception, string message, params object?[] args)
    {
        logger.Log(LogLevel.Error, exception, message, args);
        if (_isCapturing)
        {
            CaptureLog(LogLevel.Error, "Plugin", message, args, exception, isDebug: false);
        }
    }

    /// <summary>
    /// Directly logs a message to the plugin log viewer without going through ILogger.
    /// Useful for capturing logs from callbacks or contexts where ILogger is not available.
    /// </summary>
    /// <param name="level">The log level.</param>
    /// <param name="category">The log category.</param>
    /// <param name="message">The message to log.</param>
    /// <param name="exception">Optional exception.</param>
    public static void DirectLog(LogLevel level, string category, string message, Exception? exception = null)
    {
        if (_isCapturing)
        {
            CaptureLog(level, category, message, [], exception, isDebug: false);
        }
    }

    private static bool IsDebugEnabled()
    {
        // Use injected configuration provider
        if (_configProvider != null)
        {
            return _configProvider.GetConfiguration()?.EnableDebugLogging ?? false;
        }

        // No configuration provider available (e.g., in test environment without initialization)
        return false;
    }

    private static void CaptureLog(
        LogLevel level,
        string category,
        string messageTemplate,
        object?[] args,
        Exception? exception,
        bool isDebug
    )
    {
        // Caller ensures _isCapturing is true, so _logService is non-null
        try
        {
            var formattedMessage = FormatMessage(messageTemplate, args);
            var streamId = ExtractStreamId(args);
            var channelName = ExtractChannelName(args);

            _logService!.Log(level, category, formattedMessage, exception, isDebug, streamId, channelName);
        }
        catch
        {
            // Logging should never throw
        }
    }

    private static string FormatMessage(string template, object?[] args)
    {
        if (args.Length == 0)
        {
            return template;
        }

        try
        {
            var argIndex = 0;
            return PlaceholderRegex()
                .Replace(
                    template,
                    match => argIndex < args.Length ? args[argIndex++]?.ToString() ?? "null" : match.Value
                );
        }
        catch
        {
            return template;
        }
    }

    private static string? ExtractStreamId(object?[] args)
    {
        foreach (var arg in args)
        {
            if (
                arg is string s
                && s.Contains('-', StringComparison.Ordinal)
                && s.Length >= 8
                && GuidLikeRegex().IsMatch(s)
            )
            {
                return s;
            }
        }

        return null;
    }

    private static string? ExtractChannelName(object?[] args)
    {
        foreach (var arg in args)
        {
            if (
                arg is string s
                && s.Length > 2
                && s.Length < 100
                && !s.Contains('/', StringComparison.Ordinal)
                && !s.Contains('\\', StringComparison.Ordinal)
                && char.IsUpper(s[0])
                && !GuidLikeRegex().IsMatch(s)
            )
            {
                return s;
            }
        }

        return null;
    }

    [GeneratedRegex(@"\{[^}]+\}")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"^[0-9a-f]{8}(-[0-9a-f]{4}){3,4}")]
    private static partial Regex GuidLikeRegex();
}
