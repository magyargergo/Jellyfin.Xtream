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

namespace Jellyfin.Xtream.Service.MpegTs.TsDuck;

/// <summary>
/// Configuration for native TSDuck integration.
/// </summary>
public sealed record TsDuckConfiguration
{
    /// <summary>
    /// Gets the metrics reporting interval in seconds.
    /// </summary>
    public int MetricsIntervalSeconds { get; init; } = 1;

    /// <summary>
    /// Gets a value indicating whether TR 101 290 compliance checking is enabled.
    /// </summary>
    public bool EnableTr101290 { get; init; } = true;

    /// <summary>
    /// Gets the sample size in bytes for feeding to the analyzer.
    /// </summary>
    public int SampleSizeBytes { get; init; } = 256 * 1024; // 256KB

    /// <summary>
    /// Gets the default configuration.
    /// </summary>
    public static TsDuckConfiguration Default { get; } = new();
}
