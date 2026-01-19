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
using System.Diagnostics.CodeAnalysis;
using Jellyfin.Xtream.Service.MpegTs.TsDuck.Native;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.MpegTs.TsDuck;

/// <summary>
/// Factory for creating TSDuck analyzers with graceful fallback.
/// </summary>
/// <remarks>
/// The factory attempts to create a native P/Invoke-based analyzer using libtsduck_interop.
/// If native initialization fails, it falls back to a no-op analyzer that allows the
/// application to function without TsDuck metrics.
/// </remarks>
public static class TsDuckAnalyzerFactory
{
    /// <summary>
    /// Creates a TSDuck analyzer with automatic fallback.
    /// </summary>
    /// <param name="config">Configuration options. Uses defaults if null.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <returns>
    /// A native TsDuck analyzer if available, otherwise a NullTsDuckAnalyzer.
    /// Never returns null.
    /// </returns>
    public static ITsDuckAnalyzer Create(TsDuckConfiguration? config = null, ILogger? logger = null)
    {
        if (TryCreateNative(config, logger, out var analyzer))
        {
            return analyzer;
        }

        // Fall back to no-op analyzer
        logger?.PluginLogInformation(
            "TsDuck: Native library unavailable, using no-op analyzer (TR 101 290 metrics disabled)"
        );
        return new NullTsDuckAnalyzer();
    }

    /// <summary>
    /// Creates a native TSDuck analyzer without fallback.
    /// </summary>
    /// <param name="config">Configuration options. Uses defaults if null.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <returns>A native TsDuck analyzer.</returns>
    /// <exception cref="TsDuckNativeException">Thrown if native library initialization fails.</exception>
    public static ITsDuckAnalyzer CreateNative(TsDuckConfiguration? config = null, ILogger? logger = null)
    {
        config ??= TsDuckConfiguration.Default;

        var analyzer = new NativeTsDuckAnalyzer(config, logger);
        logger?.PluginLogInformation("TsDuck: Using native analyzer (P/Invoke)");
        return analyzer;
    }

    /// <summary>
    /// Attempts to create a native TSDuck analyzer.
    /// </summary>
    /// <param name="config">Configuration options. Uses defaults if null.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <param name="analyzer">The created analyzer if successful, null otherwise.</param>
    /// <returns>True if native analyzer was created successfully, false otherwise.</returns>
    public static bool TryCreateNative(
        TsDuckConfiguration? config,
        ILogger? logger,
        [NotNullWhen(true)] out ITsDuckAnalyzer? analyzer
    )
    {
        NativeTsDuckAnalyzer? native = null;
        try
        {
            config ??= TsDuckConfiguration.Default;
            native = new NativeTsDuckAnalyzer(config, logger);

            if (native.IsAvailable)
            {
                logger?.PluginLogInformation("TsDuck: Using native analyzer (P/Invoke)");
                analyzer = native;
                return true;
            }

            // Native created but not available
            logger?.LogDebugIfEnabled("TsDuck: Native analyzer created but not available");
        }
        catch (DllNotFoundException ex)
        {
            logger?.LogDebugIfEnabled(ex, "TsDuck: Native library (libtsduck_interop) not found");

            // Log resolver diagnostics to help debug library loading issues
            var diagnostics = TsDuckNativeMethods.LastResolverDiagnostics;
            if (!string.IsNullOrEmpty(diagnostics))
            {
                logger?.LogDebugIfEnabled("TsDuck resolver diagnostics:\n{Diagnostics}", diagnostics);
            }
        }
        catch (TsDuckNativeException ex)
        {
            logger?.LogDebugIfEnabled(ex, "TsDuck: Native library initialization failed");
        }
        catch (Exception ex)
        {
            logger?.LogDebugIfEnabled(ex, "TsDuck: Unexpected error creating native analyzer");
        }

        // Dispose on any failure path
        native?.Dispose();
        analyzer = null;
        return false;
    }

    /// <summary>
    /// Checks if native TSDuck library is available.
    /// </summary>
    /// <param name="logger">Optional logger.</param>
    /// <returns>True if native library can be loaded.</returns>
    public static bool CheckNativeAvailability(ILogger? logger = null)
    {
        try
        {
            var available = TsDuckNativeMethods.IsAvailable();
            if (available)
            {
                var version = TsDuckNativeMethods.GetVersion();
                logger?.LogDebugIfEnabled("TsDuck native library available (version: {Version})", version);
            }

            return available;
        }
        catch (DllNotFoundException)
        {
            logger?.LogDebugIfEnabled("TsDuck native library (libtsduck_interop) not found");
            return false;
        }
        catch (Exception ex)
        {
            logger?.LogDebugIfEnabled(ex, "Error checking TsDuck native availability");
            return false;
        }
    }

    /// <summary>
    /// Creates a no-op analyzer that discards all data.
    /// </summary>
    /// <returns>A NullTsDuckAnalyzer instance.</returns>
    /// <remarks>
    /// Use this when you want to explicitly disable TsDuck analysis
    /// or for testing purposes.
    /// </remarks>
    public static ITsDuckAnalyzer CreateNull() => new NullTsDuckAnalyzer();
}
