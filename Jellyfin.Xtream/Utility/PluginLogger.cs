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
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Utility;

/// <summary>
/// Centralized logging utility for the Xtream plugin.
/// </summary>
public static class PluginLogger
{
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
        if (Plugin.Instance.Configuration.EnableDebugLogging)
        {
            logger.Log(LogLevel.Information, message, args);
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
        if (Plugin.Instance.Configuration.EnableDebugLogging)
        {
            logger.Log(LogLevel.Information, message, args);
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
        if (Plugin.Instance.Configuration.EnableDebugLogging)
        {
            logger.Log(LogLevel.Information, exception, message, args);
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
        if (Plugin.Instance.Configuration.EnableDebugLogging)
        {
            logger.Log(LogLevel.Information, exception, message, args);
        }
    }
}
