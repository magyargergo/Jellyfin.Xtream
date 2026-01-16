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
using Jellyfin.Xtream.Service.MpegTs.UseCases;

namespace Jellyfin.Xtream.Service.MpegTs.Infrastructure;

/// <summary>
/// Production implementation of <see cref="IFFmpegContext"/> that delegates to the static
/// <see cref="FFmpegContext"/> singleton. This adapter enables dependency injection and
/// testability while maintaining the singleton initialization behavior.
/// </summary>
public sealed class FFmpegContextAdapter : IFFmpegContext
{
    /// <summary>
    /// Gets the singleton instance of the adapter.
    /// </summary>
    public static FFmpegContextAdapter Instance { get; } = new();

    private FFmpegContextAdapter() { }

    /// <inheritdoc />
    public bool IsAvailable => GetIsAvailableSafe();

    /// <inheritdoc />
    public string? FFmpegPath => GetFFmpegPathSafe();

    /// <inheritdoc />
    public string? GetVersionString() => GetVersionStringSafe();

    /// <summary>
    /// Safely checks if FFmpeg is available, handling assembly loading errors.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool GetIsAvailableSafe()
    {
        try
        {
            return FFmpegContext.IsAvailable;
        }
        catch (FileNotFoundException)
        {
            // MediaBrowser.Controller assembly not available (test environment)
            return false;
        }
        catch (TypeLoadException)
        {
            // Type could not be loaded
            return false;
        }
    }

    /// <summary>
    /// Safely gets the FFmpeg path, handling assembly loading errors.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? GetFFmpegPathSafe()
    {
        try
        {
            return FFmpegContext.FFmpegPath;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (TypeLoadException)
        {
            return null;
        }
    }

    /// <summary>
    /// Safely gets the version string, handling assembly loading errors.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? GetVersionStringSafe()
    {
        try
        {
            return FFmpegContext.GetVersionString();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (TypeLoadException)
        {
            return null;
        }
    }
}
