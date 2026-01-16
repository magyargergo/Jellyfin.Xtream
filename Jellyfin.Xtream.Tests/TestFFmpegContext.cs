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

using Jellyfin.Xtream.Service.MpegTs.UseCases;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Test implementation of <see cref="IFFmpegContext"/> that allows tests to control
/// FFmpeg availability without requiring actual FFmpeg libraries.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="TestFFmpegContext"/> class.
/// </remarks>
/// <param name="isAvailable">Whether FFmpeg should be reported as available.</param>
/// <param name="ffmpegPath">The mock FFmpeg path to report.</param>
/// <param name="versionString">The mock version string to report.</param>
internal sealed class TestFFmpegContext(
    bool isAvailable = false,
    string? ffmpegPath = null,
    string? versionString = null
) : IFFmpegContext
{
    /// <summary>
    /// Gets a shared instance that reports FFmpeg as unavailable.
    /// Use this for most tests that don't need FFmpeg functionality.
    /// </summary>
    public static TestFFmpegContext Unavailable { get; } = new(isAvailable: false);

    /// <summary>
    /// Gets a shared instance that reports FFmpeg as available.
    /// Use this for tests that need to simulate FFmpeg being present.
    /// </summary>
    public static TestFFmpegContext Available { get; } = new(isAvailable: true, ffmpegPath: "/mock/ffmpeg");

    /// <inheritdoc />
    public bool IsAvailable { get; } = isAvailable;

    /// <inheritdoc />
    public string? FFmpegPath { get; } = ffmpegPath;

    private readonly string? _versionString = versionString;

    /// <inheritdoc />
    public string? GetVersionString() => _versionString;
}
