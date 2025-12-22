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
using System.Collections.Generic;

namespace Jellyfin.Xtream.Service.Discovery;

/// <summary>
/// Trust level classification for discovered providers.
/// </summary>
public enum TrustLevel
{
    /// <summary>
    /// Excellent provider - all checks passed with high quality.
    /// </summary>
    Excellent,

    /// <summary>
    /// Good provider - reliable with minor issues.
    /// </summary>
    Good,

    /// <summary>
    /// Fair provider - usable but has some concerns.
    /// </summary>
    Fair,

    /// <summary>
    /// Poor provider - significant issues detected.
    /// </summary>
    Poor,

    /// <summary>
    /// Untrusted provider - failed critical checks.
    /// </summary>
    Untrusted,
}

/// <summary>
/// Comprehensive trust score for a discovered provider combining multiple weighted factors.
/// </summary>
public sealed class ProviderTrustScore
{
    /// <summary>
    /// Weight factors for trust score calculation (total = 100).
    /// </summary>
    private static class Weights
    {
        public const int StreamQuality = 30;
        public const int EpgAvailability = 20;
        public const int ChannelCount = 15;
        public const int AccountLongevity = 15;
        public const int ConnectionCapacity = 10;
        public const int StreamReliability = 10;
    }

    /// <summary>
    /// Gets the overall trust score (0-100).
    /// </summary>
    public int Score { get; private set; }

    /// <summary>
    /// Gets the trust level classification.
    /// </summary>
    public TrustLevel Level { get; private set; }

    /// <summary>
    /// Gets the individual factor scores for transparency.
    /// </summary>
    public TrustFactors Factors { get; } = new();

    /// <summary>
    /// Gets a human-readable summary of the trust assessment.
    /// </summary>
    public string Summary { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the list of positive attributes found.
    /// </summary>
    public IList<string> Strengths { get; } = new List<string>();

    /// <summary>
    /// Gets the list of concerns found.
    /// </summary>
    public IList<string> Concerns { get; } = new List<string>();

    /// <summary>
    /// Calculates the trust score from a provider test result.
    /// </summary>
    /// <param name="result">The provider test result.</param>
    /// <returns>A calculated trust score.</returns>
    public static ProviderTrustScore Calculate(ProviderTestResult result)
    {
        var score = new ProviderTrustScore();
        score.CalculateFromResult(result);
        return score;
    }

    private void CalculateFromResult(ProviderTestResult result)
    {
        // Factor 1: Stream Quality (30%)
        CalculateStreamQualityFactor(result);

        // Factor 2: EPG Availability (20%)
        CalculateEpgFactor(result);

        // Factor 3: Channel Count (15%)
        CalculateChannelCountFactor(result);

        // Factor 4: Account Longevity (15%)
        CalculateAccountLongevityFactor(result);

        // Factor 5: Connection Capacity (10%)
        CalculateConnectionCapacityFactor(result);

        // Factor 6: Stream Reliability (10%)
        CalculateStreamReliabilityFactor(result);

        // Calculate weighted total
        Score = (int)
            Math.Round(
                (Factors.StreamQuality * Weights.StreamQuality / 100.0)
                    + (Factors.EpgAvailability * Weights.EpgAvailability / 100.0)
                    + (Factors.ChannelCount * Weights.ChannelCount / 100.0)
                    + (Factors.AccountLongevity * Weights.AccountLongevity / 100.0)
                    + (Factors.ConnectionCapacity * Weights.ConnectionCapacity / 100.0)
                    + (Factors.StreamReliability * Weights.StreamReliability / 100.0)
            );

        // Apply critical penalties
        ApplyCriticalPenalties(result);

        // Clamp to valid range
        Score = Math.Clamp(Score, 0, 100);

        // Determine trust level
        Level = Score switch
        {
            >= 85 => TrustLevel.Excellent,
            >= 70 => TrustLevel.Good,
            >= 50 => TrustLevel.Fair,
            >= 25 => TrustLevel.Poor,
            _ => TrustLevel.Untrusted,
        };

        // Generate summary
        GenerateSummary(result);
    }

    private void CalculateStreamQualityFactor(ProviderTestResult result)
    {
        if (result.StreamQuality == null)
        {
            Factors.StreamQuality = 50; // Unknown = neutral
            return;
        }

        // Use the quality score directly (already 0-100)
        Factors.StreamQuality = result.StreamQuality.QualityScore;

        if (result.StreamQuality.QualityLevel == StreamQualityLevel.Excellent)
        {
            Strengths.Add("Excellent stream quality with no errors");
        }
        else if (result.StreamQuality.QualityLevel == StreamQualityLevel.Good)
        {
            Strengths.Add("Good stream quality");
        }
        else if (result.StreamQuality.QualityLevel == StreamQualityLevel.Poor)
        {
            Concerns.Add($"Poor stream quality: {result.StreamQuality.QualityIssues}");
        }
    }

    private void CalculateEpgFactor(ProviderTestResult result)
    {
        if (!result.HasEpg)
        {
            Factors.EpgAvailability = 0;
            Concerns.Add("No EPG data available");
            return;
        }

        // Score based on EPG program count
        Factors.EpgAvailability = result.EpgProgramCount switch
        {
            >= 50 => 100,
            >= 20 => 80,
            >= 10 => 60,
            >= 1 => 40,
            _ => 0,
        };

        if (result.EpgProgramCount >= 20)
        {
            Strengths.Add($"Rich EPG data ({result.EpgProgramCount} programs)");
        }
    }

    private void CalculateChannelCountFactor(ProviderTestResult result)
    {
        // Focus on country-specific channels since that's what we're looking for
        if (result.HasCountryChannels)
        {
            Factors.ChannelCount = result.CountryChannelCount switch
            {
                >= 100 => 100,
                >= 50 => 90,
                >= 25 => 75,
                >= 10 => 60,
                >= 5 => 45,
                _ => 30,
            };

            var countryLabel = result.CountryCode ?? "country";
            Strengths.Add($"{result.CountryChannelCount} {countryLabel} channels available");
        }
        else
        {
            // Fall back to total channel count
            Factors.ChannelCount = result.TotalChannelCount switch
            {
                >= 1000 => 60,
                >= 500 => 50,
                >= 100 => 40,
                _ => 20,
            };

            var countryLabel = result.CountryCode ?? "country";
            Concerns.Add($"No {countryLabel} channels detected");
        }
    }

    private void CalculateAccountLongevityFactor(ProviderTestResult result)
    {
        if (!result.ExpirationDate.HasValue)
        {
            Factors.AccountLongevity = 50; // Unknown = neutral
            return;
        }

        var daysUntilExpiry = (result.ExpirationDate.Value - DateTime.UtcNow).TotalDays;

        Factors.AccountLongevity = daysUntilExpiry switch
        {
            >= 365 => 100,
            >= 180 => 90,
            >= 90 => 75,
            >= 30 => 50,
            >= 7 => 25,
            >= 0 => 10,
            _ => 0, // Already expired
        };

        if (daysUntilExpiry >= 180)
        {
            Strengths.Add($"Long validity ({(int)daysUntilExpiry} days remaining)");
        }
        else if (daysUntilExpiry < 7 && daysUntilExpiry >= 0)
        {
            Concerns.Add($"Expires soon ({(int)daysUntilExpiry} days remaining)");
        }
        else if (daysUntilExpiry < 0)
        {
            Concerns.Add("Account expired");
        }
    }

    private void CalculateConnectionCapacityFactor(ProviderTestResult result)
    {
        Factors.ConnectionCapacity = result.MaxConnections switch
        {
            >= 5 => 100,
            4 => 90,
            3 => 75,
            2 => 50,
            1 => 25,
            _ => 0,
        };

        if (result.MaxConnections >= 3)
        {
            Strengths.Add($"Multiple connections allowed ({result.MaxConnections})");
        }
        else if (result.MaxConnections == 1)
        {
            Concerns.Add("Single connection limit");
        }
    }

    private void CalculateStreamReliabilityFactor(ProviderTestResult result)
    {
        if (!result.StreamWorks)
        {
            Factors.StreamReliability = 0;
            Concerns.Add($"Stream test failed: {result.StreamStatus ?? "Unknown error"}");
            return;
        }

        // Parse stream status for reliability indicators
        var status = result.StreamStatus ?? string.Empty;

        if (status.Contains("3/3", StringComparison.Ordinal) || status.Contains("OK (HLS)", StringComparison.Ordinal))
        {
            Factors.StreamReliability = 100;
            Strengths.Add("All stream formats working");
        }
        else if (status.Contains("2/3", StringComparison.Ordinal))
        {
            Factors.StreamReliability = 75;
        }
        else if (
            status.Contains("1/3", StringComparison.Ordinal) || status.Contains("Partial", StringComparison.Ordinal)
        )
        {
            Factors.StreamReliability = 50;
            Concerns.Add("Some stream formats not working");
        }
        else
        {
            Factors.StreamReliability = 70; // Default for working stream
        }
    }

    private void ApplyCriticalPenalties(ProviderTestResult result)
    {
        // Account not active
        if (result.Status != ProviderStatus.Active)
        {
            Score = Math.Min(Score, 10);
            Concerns.Add($"Account status: {result.Status}");
        }

        // Encrypted streams (usually means we can't play them)
        if (result.StreamQuality?.IsEncrypted == true)
        {
            Score -= 30;
            Concerns.Add("Stream appears encrypted");
        }
    }

    private void GenerateSummary(ProviderTestResult result)
    {
        var parts = new List<string>();

        if (result.HasCountryChannels)
        {
            var countryLabel = result.CountryCode ?? "country";
            parts.Add($"{result.CountryChannelCount} {countryLabel} channels");
        }

        if (result.HasEpg)
        {
            parts.Add("EPG available");
        }

        if (result.StreamQuality != null)
        {
            parts.Add($"Quality: {result.StreamQuality.QualityLevel}");
        }

        if (result.ExpirationDate.HasValue)
        {
            var days = (int)(result.ExpirationDate.Value - DateTime.UtcNow).TotalDays;
            parts.Add(days > 0 ? $"{days}d validity" : "Expired");
        }

        Summary = parts.Count > 0 ? string.Join(" | ", parts) : "Insufficient data for assessment";
    }
}

/// <summary>
/// Individual factor scores for trust calculation transparency.
/// </summary>
public sealed class TrustFactors
{
    /// <summary>
    /// Gets or sets stream quality score (0-100).
    /// </summary>
    public int StreamQuality { get; set; }

    /// <summary>
    /// Gets or sets EPG availability score (0-100).
    /// </summary>
    public int EpgAvailability { get; set; }

    /// <summary>
    /// Gets or sets channel count score (0-100).
    /// </summary>
    public int ChannelCount { get; set; }

    /// <summary>
    /// Gets or sets account longevity score (0-100).
    /// </summary>
    public int AccountLongevity { get; set; }

    /// <summary>
    /// Gets or sets connection capacity score (0-100).
    /// </summary>
    public int ConnectionCapacity { get; set; }

    /// <summary>
    /// Gets or sets stream reliability score (0-100).
    /// </summary>
    public int StreamReliability { get; set; }
}
