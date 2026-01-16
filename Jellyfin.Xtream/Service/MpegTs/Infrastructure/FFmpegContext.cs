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
using System.Runtime.CompilerServices;
using FFmpeg.AutoGen.Abstractions;
using FFmpeg.AutoGen.Bindings.DynamicallyLoaded;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.MpegTs.Infrastructure;

/// <summary>
/// Manages FFmpeg library initialization using Jellyfin's FFmpeg installation.
/// Thread-safe singleton that uses Jellyfin's IMediaEncoder to locate FFmpeg libraries.
/// Supports lazy initialization to handle cases where IMediaEncoder isn't ready at startup.
/// </summary>
public sealed class FFmpegContext
{
    private static readonly object InitLock = new();
    private static bool _isAvailable;
    private static bool _permanentlyFailed; // True if initialization failed for a permanent reason
    private static object? _mediaEncoderRef; // Store as object to delay type resolution
    private static ILogger? _logger;

    private FFmpegContext() { }

    // Property to access media encoder with type cast
    // Separated to enable NoInlining and delay type resolution
    private static IMediaEncoder? MediaEncoder
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        get => _mediaEncoderRef as IMediaEncoder;
    }

    /// <summary>
    /// Gets a value indicating whether FFmpeg libraries are available and initialized.
    /// This property triggers lazy initialization if the encoder reference is available.
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            // Fast path: already initialized successfully
            if (_isAvailable)
            {
                return true;
            }

            // Fast path: permanently failed (non-transient error)
            if (_permanentlyFailed)
            {
                return false;
            }

            // Check if we have an encoder reference (using object? to avoid type resolution)
            // If we do, try to initialize in a separate method to handle missing assemblies
            return _mediaEncoderRef != null ? TryInitializeWithErrorHandling() : _isAvailable;
        }
    }

    /// <summary>
    /// Attempts initialization with error handling for missing assemblies.
    /// Separated into its own method to delay type resolution of IMediaEncoder.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryInitializeWithErrorHandling()
    {
        try
        {
            _ = TryInitializeCore();
            return _isAvailable;
        }
        catch (FileNotFoundException)
        {
            // In test environments, MediaBrowser.Controller assembly may not be loaded
            // Mark as permanently failed to avoid repeated exceptions
            _permanentlyFailed = true;
            return false;
        }
        catch (TypeLoadException)
        {
            // Type could not be loaded - same issue
            _permanentlyFailed = true;
            return false;
        }
    }

    /// <summary>
    /// Gets the path where FFmpeg libraries were loaded from.
    /// </summary>
    public static string? FFmpegPath { get; private set; }

    /// <summary>
    /// Registers the media encoder for lazy initialization.
    /// Call this early during startup to enable deferred initialization.
    /// </summary>
    /// <param name="mediaEncoder">Jellyfin's media encoder service.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public static void RegisterMediaEncoder(IMediaEncoder mediaEncoder, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(mediaEncoder);
        _mediaEncoderRef = mediaEncoder;
        _logger = logger;
    }

    /// <summary>
    /// Initializes the FFmpeg context using Jellyfin's media encoder.
    /// This should be called during plugin initialization.
    /// </summary>
    /// <param name="mediaEncoder">Jellyfin's media encoder service.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <returns>True if FFmpeg was initialized successfully.</returns>
    public static bool Initialize(IMediaEncoder mediaEncoder, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(mediaEncoder);

        // Store references for lazy initialization retry
        _mediaEncoderRef = mediaEncoder;
        _logger = logger;

        return TryInitializeCore();
    }

    /// <summary>
    /// Core initialization logic. Can be called multiple times until success or permanent failure.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryInitializeCore()
    {
        // Fast paths
        if (_isAvailable)
        {
            return true;
        }

        if (_permanentlyFailed)
        {
            return false;
        }

        var mediaEncoder = MediaEncoder;
        if (mediaEncoder == null)
        {
            return false;
        }

        lock (InitLock)
        {
            // Double-check after acquiring lock
            if (_isAvailable)
            {
                return true;
            }

            if (_permanentlyFailed)
            {
                return false;
            }

            try
            {
                // Get FFmpeg path from Jellyfin's media encoder
                var encoderPath = mediaEncoder.EncoderPath;
                if (string.IsNullOrEmpty(encoderPath))
                {
                    // This is a transient failure - MediaEncoder not ready yet
                    // Don't set _permanentlyFailed, allow retry later
                    _logger?.LogDebugIfEnabled(
                        "IMediaEncoder.EncoderPath is empty - Jellyfin startup not complete. "
                            + "FFmpeg initialization will be retried on next access."
                    );
                    return false;
                }

                // Get the directory containing the FFmpeg executable
                var ffmpegDir = Path.GetDirectoryName(encoderPath);
                if (string.IsNullOrEmpty(ffmpegDir))
                {
                    _logger?.PluginLogError(
                        "Could not determine FFmpeg directory from path: {EncoderPath}",
                        encoderPath
                    );
                    _permanentlyFailed = true;
                    return false;
                }

                // Jellyfin's FFmpeg typically has shared libraries in a 'lib' subdirectory
                var libraryPath = Path.Combine(ffmpegDir, "lib");
                if (!Directory.Exists(libraryPath))
                {
                    // Fall back to the main directory if 'lib' doesn't exist
                    libraryPath = ffmpegDir;
                }

                _logger?.LogDebugIfEnabled("Attempting to load FFmpeg libraries from: {LibraryPath}", libraryPath);

                // Initialize FFmpeg.AutoGen with Jellyfin's FFmpeg path
                DynamicallyLoadedBindings.LibrariesPath = libraryPath;
                DynamicallyLoadedBindings.Initialize();

                // Verify by calling a simple function
                _ = ffmpeg.avcodec_version();

                _isAvailable = true;
                FFmpegPath = libraryPath;

                _logger?.PluginLogInformation(
                    "FFmpeg initialized from Jellyfin path: {Path}. Version: {Version}",
                    libraryPath,
                    GetVersionString() ?? "unknown"
                );

                return true;
            }
            catch (DllNotFoundException ex)
            {
                // Permanent failure: FFmpeg libraries not found
                _logger?.PluginLogError(ex, "FFmpeg libraries not found. Program detection will be unavailable.");
                _permanentlyFailed = true;
                return false;
            }
            catch (Exception ex)
            {
                // Could be transient (e.g., file system not ready) or permanent
                // Log but allow retry
                _logger?.PluginLogWarning(ex, "FFmpeg initialization failed. Will retry on next access.");
                return false;
            }
        }
    }

    /// <summary>
    /// Gets the FFmpeg version string if available.
    /// </summary>
    /// <returns>Version string or null if unavailable.</returns>
    public static string? GetVersionString()
    {
        if (!_isAvailable)
        {
            return null;
        }

        try
        {
            return ffmpeg.av_version_info();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the libavcodec version.
    /// </summary>
    /// <returns>Version number or 0 if unavailable.</returns>
    public static uint GetAvCodecVersion()
    {
        if (!_isAvailable)
        {
            return 0;
        }

        try
        {
            return ffmpeg.avcodec_version();
        }
        catch
        {
            return 0;
        }
    }
}
