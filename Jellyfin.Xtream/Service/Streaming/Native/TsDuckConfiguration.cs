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

namespace Jellyfin.Xtream.Service.Streaming.Native;

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
    /// Gets a value indicating whether automatic restamping is enabled during feed.
    /// When enabled, the analyzer will apply timestamp corrections in-place before analysis.
    /// </summary>
    public bool EnableAutoRestamp { get; init; } = true;

    /// <summary>
    /// Gets the restamping mode (Disabled, Monitor, or Correct).
    /// </summary>
    public RestampingMode RestampMode { get; init; } = RestampingMode.Correct;

    /// <summary>
    /// Gets a value indicating whether PCR jitter smoothing is enabled.
    /// </summary>
    public bool SmoothPcr { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether PTS discontinuity repair is enabled.
    /// </summary>
    public bool FixDiscontinuities { get; init; } = true;

    /// <summary>
    /// Gets the drift threshold (in ms) at which corrections start being applied.
    /// </summary>
    public double CorrectionThresholdMs { get; init; } = 25.0;

    /// <summary>
    /// Gets the maximum correction rate in milliseconds per second.
    /// </summary>
    public double MaxCorrectionRateMs { get; init; } = 20.0;

    /// <summary>
    /// Gets the drift threshold (in ms) below which corrections stop.
    /// </summary>
    public double HysteresisThresholdMs { get; init; } = 10.0;

    /// <summary>
    /// Gets the stream bitrate hint for PCR smoothing (0 = auto-detect).
    /// </summary>
    public long StreamBitrateHint { get; init; } = 20_000_000; // 20 Mbps typical for 1080i IPTV

    /// <summary>
    /// Gets a value indicating whether debug logging is enabled for native code.
    /// When enabled, native TsDuck components emit detailed diagnostic logs.
    /// </summary>
    public bool EnableDebugLogging { get; init; }

    /// <summary>
    /// Gets the default configuration (with auto-restamping enabled).
    /// </summary>
    public static TsDuckConfiguration Default { get; } = new();

    /// <summary>
    /// Gets a configuration with restamping disabled (analysis only).
    /// </summary>
    public static TsDuckConfiguration AnalysisOnly { get; } =
        new() { EnableAutoRestamp = false, RestampMode = RestampingMode.Disabled };
}
