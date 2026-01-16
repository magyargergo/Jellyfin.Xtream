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

namespace Jellyfin.Xtream.Service.MpegTs.UseCases;

/// <summary>
/// Abstraction for FFmpeg context to enable testability.
/// Provides access to FFmpeg availability and configuration without
/// requiring actual FFmpeg libraries in test environments.
/// </summary>
public interface IFFmpegContext
{
    /// <summary>
    /// Gets a value indicating whether FFmpeg libraries are available and initialized.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Gets the path where FFmpeg libraries were loaded from.
    /// Returns null if FFmpeg is not available.
    /// </summary>
    string? FFmpegPath { get; }

    /// <summary>
    /// Gets the FFmpeg version string if available.
    /// </summary>
    /// <returns>Version string or null if unavailable.</returns>
    string? GetVersionString();
}
