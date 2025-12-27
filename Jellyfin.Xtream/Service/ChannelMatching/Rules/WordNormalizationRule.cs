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

using System.Text.RegularExpressions;

namespace Jellyfin.Xtream.Service.ChannelMatching.Rules;

/// <summary>
/// Normalizes common word variations in channel names.
/// Handles singular/plural forms and common abbreviations.
/// </summary>
public sealed partial class WordNormalizationRule : INormalizationRule
{
    /// <summary>
    /// Gets the singleton instance.
    /// </summary>
    public static WordNormalizationRule Instance { get; } = new();

    private WordNormalizationRule() { }

    /// <summary>
    /// Pattern for matching "Sports" to normalize to "Sport".
    /// </summary>
    [GeneratedRegex(@"\bSPORTS\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, matchTimeoutMilliseconds: 100)]
    private static partial Regex SportsPattern();

    /// <summary>
    /// Pattern for matching "Movies" to normalize to "Movie".
    /// </summary>
    [GeneratedRegex(@"\bMOVIES\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, matchTimeoutMilliseconds: 100)]
    private static partial Regex MoviesPattern();

    /// <summary>
    /// Pattern for matching standalone "TV" word that's redundant in channel names.
    /// Examples: "TVN TV HD" -> "TVN HD", "Polsat TV" -> "Polsat".
    /// Uses negative lookbehind/lookahead to avoid matching TV in:
    /// - Channel names like "TVN", "TVP", "TV4" (letter/number directly after)
    /// - Brand names like "Apple TV+" (plus sign after)
    /// - Channel names like "TV 6", "TV 1" (space then number - these are numbered TV channels)
    /// </summary>
    [GeneratedRegex(
        @"(?<![A-Z])\bTV\b(?![A-Z0-9+])(?!\s+\d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        matchTimeoutMilliseconds: 100
    )]
    private static partial Regex StandaloneTvPattern();

    /// <summary>
    /// Pattern for matching "National Geographic" to normalize to "NATGEO".
    /// Handles various forms: "National Geographic", "Nat Geo", "NAT-GEO".
    /// </summary>
    [GeneratedRegex(
        @"\bNATIONAL\s*GEOGRAPHIC\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        matchTimeoutMilliseconds: 100
    )]
    private static partial Regex NationalGeographicPattern();

    /// <summary>
    /// Pattern for matching "Nat Geo" or "NAT-GEO" variations.
    /// </summary>
    [GeneratedRegex(
        @"\bNAT[\s\-]*GEO\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        matchTimeoutMilliseconds: 100
    )]
    private static partial Regex NatGeoPattern();

    /// <summary>
    /// Pattern for matching "Travel Channel" to normalize to "TRAVEL".
    /// </summary>
    [GeneratedRegex(
        @"\bTRAVEL\s*CHANNEL\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        matchTimeoutMilliseconds: 100
    )]
    private static partial Regex TravelChannelPattern();

    /// <summary>
    /// Pattern for matching "E! Entertainment" to normalize to "E".
    /// Also matches standalone "E!" channel.
    /// </summary>
    [GeneratedRegex(
        @"\bE!\s*ENTERTAINMENT\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        matchTimeoutMilliseconds: 100
    )]
    private static partial Regex EEntertainmentPattern();

    /// <summary>
    /// Pattern for matching standalone "Channel" word that's redundant.
    /// Examples: "Discovery Channel" -> "Discovery", "History Channel" -> "History".
    /// </summary>
    [GeneratedRegex(@"\s+CHANNEL\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, matchTimeoutMilliseconds: 100)]
    private static partial Regex StandaloneChannelPattern();

    /// <inheritdoc />
    public string Apply(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return input;
        }

        var result = input;

        // Apply common word normalizations (singular/plural)
        result = SportsPattern().Replace(result, "SPORT");
        result = MoviesPattern().Replace(result, "MOVIE");

        // Remove standalone "TV" that's redundant (but keep TVN, TVP, TV4, etc.)
        result = StandaloneTvPattern().Replace(result, string.Empty);

        // Normalize brand name variations
        result = NationalGeographicPattern().Replace(result, "NATGEO");
        result = NatGeoPattern().Replace(result, "NATGEO");
        result = TravelChannelPattern().Replace(result, "TRAVEL");
        result = EEntertainmentPattern().Replace(result, "E");

        // Remove redundant "Channel" suffix (but after brand-specific rules)
        result = StandaloneChannelPattern().Replace(result, string.Empty);

        return result;
    }
}
