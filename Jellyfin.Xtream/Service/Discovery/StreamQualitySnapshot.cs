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

namespace Jellyfin.Xtream.Service.Discovery;

/// <summary>
/// Quality level classification for discovered streams.
/// </summary>
public enum StreamQualityLevel
{
    /// <summary>
    /// Excellent quality - no errors detected.
    /// </summary>
    Excellent,

    /// <summary>
    /// Good quality - minimal errors within acceptable thresholds.
    /// </summary>
    Good,

    /// <summary>
    /// Fair quality - some errors detected but stream is usable.
    /// </summary>
    Fair,

    /// <summary>
    /// Poor quality - significant errors detected.
    /// </summary>
    Poor,

    /// <summary>
    /// Unknown quality - insufficient data to determine quality.
    /// </summary>
    Unknown,
}

/// <summary>
/// Captures stream quality metrics during discovery and monitoring.
/// </summary>
public sealed class StreamQualitySnapshot
{
    /// <summary>
    /// Gets or sets the total TS packets successfully parsed.
    /// </summary>
    public long PacketsParsed { get; set; }

    /// <summary>
    /// Gets or sets the total bytes processed.
    /// </summary>
    public long BytesProcessed { get; set; }

    /// <summary>
    /// Gets or sets the number of sync byte errors detected.
    /// </summary>
    public long SyncByteErrors { get; set; }

    /// <summary>
    /// Gets or sets the number of times sync was recovered.
    /// </summary>
    public long SyncRecoveries { get; set; }

    /// <summary>
    /// Gets or sets the number of continuity counter errors.
    /// </summary>
    public long ContinuityErrors { get; set; }

    /// <summary>
    /// Gets or sets the number of packets with Transport Error Indicator set.
    /// </summary>
    public long PacketErrors { get; set; }

    /// <summary>
    /// Gets or sets the number of PAT interval violations (>500ms).
    /// </summary>
    public long PatViolations { get; set; }

    /// <summary>
    /// Gets or sets the number of CRC-32 validation failures.
    /// </summary>
    public long CrcErrors { get; set; }

    /// <summary>
    /// Gets or sets the number of programs detected in the stream.
    /// </summary>
    public int ProgramCount { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the stream is encrypted.
    /// </summary>
    public bool IsEncrypted { get; set; }

    /// <summary>
    /// Gets or sets the number of scrambled PIDs detected.
    /// </summary>
    public int ScrambledPidCount { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the stream has video.
    /// </summary>
    public bool HasVideo { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the stream has audio.
    /// </summary>
    public bool HasAudio { get; set; }

    /// <summary>
    /// Gets or sets the A/V drift in milliseconds (if available).
    /// </summary>
    public double? AvDriftMs { get; set; }

    /// <summary>
    /// Gets or sets the overall quality level.
    /// </summary>
    public StreamQualityLevel QualityLevel { get; set; } = StreamQualityLevel.Unknown;

    /// <summary>
    /// Gets or sets the quality score (0-100, higher is better).
    /// </summary>
    public int QualityScore { get; set; }

    /// <summary>
    /// Gets or sets a summary of quality issues found.
    /// </summary>
    public string? QualityIssues { get; set; }

    /// <summary>
    /// Gets a value indicating whether this stream passes quality thresholds.
    /// </summary>
    public bool PassesQualityThreshold => QualityLevel is StreamQualityLevel.Excellent or StreamQualityLevel.Good;

    /// <summary>
    /// Gets the total error count across all error types.
    /// </summary>
    public long TotalErrors => SyncByteErrors + ContinuityErrors + PacketErrors + PatViolations + CrcErrors;

    /// <summary>
    /// Calculates quality level and score based on collected metrics.
    /// </summary>
    public void CalculateQuality()
    {
        if (PacketsParsed == 0)
        {
            QualityLevel = StreamQualityLevel.Unknown;
            QualityScore = 0;
            return;
        }

        // Calculate error rate per 1000 packets
        _ = (double)TotalErrors / PacketsParsed * 1000;

        var issues = new System.Collections.Generic.List<string>();

        // Score starts at 100 and decreases based on issues
        var score = 100;

        // Sync byte errors are serious
        if (SyncByteErrors > 0)
        {
            score -= (int)System.Math.Min(30, SyncByteErrors * 5);
            issues.Add($"{SyncByteErrors} sync errors");
        }

        // Continuity errors indicate packet loss
        if (ContinuityErrors > 0)
        {
            score -= (int)System.Math.Min(20, ContinuityErrors * 2);
            issues.Add($"{ContinuityErrors} continuity errors");
        }

        // Packet errors (TEI bit set)
        if (PacketErrors > 0)
        {
            score -= (int)System.Math.Min(20, PacketErrors * 2);
            issues.Add($"{PacketErrors} packet errors");
        }

        // PAT violations
        if (PatViolations > 0)
        {
            score -= (int)System.Math.Min(10, PatViolations * 2);
            issues.Add($"{PatViolations} PAT violations");
        }

        // CRC errors
        if (CrcErrors > 0)
        {
            score -= (int)System.Math.Min(15, CrcErrors * 3);
            issues.Add($"{CrcErrors} CRC errors");
        }

        // Encrypted streams
        if (IsEncrypted)
        {
            score -= 50;
            issues.Add($"encrypted ({ScrambledPidCount} PIDs)");
        }

        // A/V drift
        if (AvDriftMs.HasValue && System.Math.Abs(AvDriftMs.Value) > 100)
        {
            score -= 10;
            issues.Add($"A/V drift {AvDriftMs.Value:F0}ms");
        }

        // Missing video/audio
        if (!HasVideo && PacketsParsed > 100)
        {
            score -= 10;
            issues.Add("no video detected");
        }

        if (!HasAudio && PacketsParsed > 100)
        {
            score -= 5;
            issues.Add("no audio detected");
        }

        // Clamp score
        QualityScore = System.Math.Max(0, System.Math.Min(100, score));

        // Determine quality level
        QualityLevel = QualityScore switch
        {
            >= 95 => StreamQualityLevel.Excellent,
            >= 80 => StreamQualityLevel.Good,
            >= 60 => StreamQualityLevel.Fair,
            _ => StreamQualityLevel.Poor,
        };

        QualityIssues = issues.Count > 0 ? string.Join(", ", issues) : null;
    }
}
