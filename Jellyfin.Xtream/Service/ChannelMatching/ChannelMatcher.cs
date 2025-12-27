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
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Service.ChannelMatching.Rules;

namespace Jellyfin.Xtream.Service.ChannelMatching;

/// <summary>
/// Matches channels between providers using a two-phase strategy:
/// 1. Exact match on normalized names (O(1) lookup)
/// 2. Fuzzy match using Levenshtein distance for near-matches.
/// </summary>
public sealed class ChannelMatcher : IChannelMatcher
{
    /// <summary>
    /// The similarity threshold (0-100) for fuzzy matching.
    /// 90% balances matching variations like "Sports" vs "Sport" (92% similarity)
    /// while still avoiding false positives like "TVN" vs "TVN24" (75% similarity).
    /// </summary>
    public const int SimilarityThreshold = 90;

    private readonly IChannelNameNormalizer _normalizer;

    /// <summary>
    /// Gets the default matcher instance with standard normalizer.
    /// </summary>
    public static ChannelMatcher Default { get; } = new(ChannelNameNormalizer.Default);

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelMatcher"/> class.
    /// </summary>
    /// <param name="normalizer">The normalizer to use for channel names.</param>
    public ChannelMatcher(IChannelNameNormalizer normalizer)
    {
        _normalizer = normalizer;
    }

    /// <inheritdoc />
    public ChannelMatchIndex BuildIndex(IEnumerable<StreamInfo> streams)
    {
        var normalizedStreams = new List<(StreamInfo Stream, string NormalizedName)>();
        var exactMatchLookup = new Dictionary<string, List<StreamInfo>>(StringComparer.OrdinalIgnoreCase);

        foreach (var stream in streams)
        {
            var normalizedName = _normalizer.Normalize(stream.Name ?? string.Empty);
            if (string.IsNullOrEmpty(normalizedName))
            {
                continue;
            }

            normalizedStreams.Add((stream, normalizedName));

            if (!exactMatchLookup.TryGetValue(normalizedName, out var list))
            {
                list = new List<StreamInfo>();
                exactMatchLookup[normalizedName] = list;
            }

            list.Add(stream);
        }

        return new ChannelMatchIndex(exactMatchLookup, normalizedStreams);
    }

    /// <inheritdoc />
    public ChannelMatchResult FindBestMatch(StreamInfo sourceStream, ChannelMatchIndex targetIndex)
    {
        var sourceName = sourceStream.Name ?? string.Empty;
        var normalizedSource = _normalizer.Normalize(sourceName);

        if (string.IsNullOrEmpty(normalizedSource))
        {
            return new ChannelMatchResult(null, 0, string.Empty);
        }

        // Extract country code from source channel for country-aware matching
        var sourceCountry = NormalizationPatterns.ExtractCountryCode(sourceName);

        // Phase 1: Try exact normalized match (O(1) lookup)
        if (targetIndex.ExactMatchLookup.TryGetValue(normalizedSource, out var exactMatches))
        {
            // If source has a country, only match same-country targets
            if (sourceCountry != null)
            {
                foreach (var match in exactMatches)
                {
                    var targetCountry = NormalizationPatterns.ExtractCountryCode(match.Name);
                    if (string.Equals(sourceCountry, targetCountry, StringComparison.OrdinalIgnoreCase))
                    {
                        return new ChannelMatchResult(match, 100, normalizedSource);
                    }
                }

                // Source has country but no same-country target found - don't match to different country
                // Continue to fuzzy matching which will also respect country constraints
            }
            else
            {
                // Source has no country - return first match
                return new ChannelMatchResult(exactMatches[0], 100, normalizedSource);
            }
        }

        // Phase 2: Fuzzy match - first try same-country matches only
        var (sameCountryMatch, sameCountryScore, sameCountryName) = FindBestFuzzyMatch(
            normalizedSource,
            sourceCountry,
            targetIndex,
            sameCountryOnly: true
        );

        if (sameCountryMatch != null)
        {
            return new ChannelMatchResult(sameCountryMatch, sameCountryScore, sameCountryName);
        }

        // Phase 3: Fall back to any matching channel - but ONLY if source has no country prefix
        // If source has a country (e.g., "PL | HBO"), we should NOT match to different countries (e.g., "FR- HBO")
        if (sourceCountry == null)
        {
            var (anyMatch, anyScore, anyName) = FindBestFuzzyMatch(
                normalizedSource,
                sourceCountry,
                targetIndex,
                sameCountryOnly: false
            );

            if (anyMatch != null)
            {
                return new ChannelMatchResult(anyMatch, anyScore, anyName);
            }
        }

        // No match found
        return new ChannelMatchResult(null, 0, normalizedSource);
    }

    private static (StreamInfo? Match, int Score, string Name) FindBestFuzzyMatch(
        string normalizedSource,
        string? sourceCountry,
        ChannelMatchIndex targetIndex,
        bool sameCountryOnly
    )
    {
        StreamInfo? bestMatch = null;
        var bestScore = 0;
        var bestName = string.Empty;

        foreach (var (targetStream, targetNormalized) in targetIndex.NormalizedStreams)
        {
            // If same-country only, skip channels from different countries
            if (sameCountryOnly && sourceCountry != null)
            {
                var targetCountry = NormalizationPatterns.ExtractCountryCode(targetStream.Name);
                if (!string.Equals(sourceCountry, targetCountry, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            var similarity = StringSimilarity.CalculateSimilarity(normalizedSource, targetNormalized);
            if (similarity > bestScore && similarity >= SimilarityThreshold)
            {
                bestScore = similarity;
                bestMatch = targetStream;
                bestName = targetNormalized;
            }
        }

        return (bestMatch, bestScore, bestName);
    }
}
