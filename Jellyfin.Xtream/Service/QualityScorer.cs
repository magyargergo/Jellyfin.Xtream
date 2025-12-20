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
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Jellyfin.Xtream.Client.Models;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Scores streams based on quality indicators in their names and tags.
/// </summary>
public static class QualityScorer
{
    /// <summary>
    /// Score for 4K/UHD quality streams.
    /// </summary>
    public const int Score4K = 100;

    /// <summary>
    /// Score for Full HD (1080p) quality streams.
    /// </summary>
    public const int ScoreFHD = 80;

    /// <summary>
    /// Score for HD (720p) quality streams.
    /// </summary>
    public const int ScoreHD = 60;

    /// <summary>
    /// Score for SD quality streams.
    /// </summary>
    public const int ScoreSD = 40;

    /// <summary>
    /// Default score when quality cannot be determined.
    /// </summary>
    public const int ScoreUnknown = 50;

    /// <summary>
    /// Bonus score for streams that have an icon/image.
    /// </summary>
    public const int ScoreHasImage = 10;

    private static readonly FrozenDictionary<string, int> QualityPatterns = new Dictionary<string, int>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        // 4K variants
        { "4K", Score4K },
        { "UHD", Score4K },
        { "2160", Score4K },
        { "2160P", Score4K },
        { "ULTRAHD", Score4K },
        // Full HD variants
        { "FHD", ScoreFHD },
        { "1080", ScoreFHD },
        { "1080P", ScoreFHD },
        { "1080I", ScoreFHD },
        { "FULLHD", ScoreFHD },
        // HD variants
        { "HD", ScoreHD },
        { "720", ScoreHD },
        { "720P", ScoreHD },
        // SD variants
        { "SD", ScoreSD },
        { "576", ScoreSD },
        { "576P", ScoreSD },
        { "576I", ScoreSD },
        { "480", ScoreSD },
        { "480P", ScoreSD },
        { "480I", ScoreSD },
        { "LQ", ScoreSD - 10 },
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    // Pre-sorted patterns by score (highest first) for early exit optimization
    private static readonly (string Pattern, int Score)[] SortedPatterns =
    [
        ("4K", Score4K),
        ("UHD", Score4K),
        ("2160", Score4K),
        ("FHD", ScoreFHD),
        ("1080", ScoreFHD),
        ("FULLHD", ScoreFHD),
        ("HD", ScoreHD),
        ("720", ScoreHD),
        ("SD", ScoreSD),
        ("576", ScoreSD),
        ("480", ScoreSD),
        ("LQ", ScoreSD - 10),
    ];

    /// <summary>
    /// Calculates a quality score from parsed tags.
    /// </summary>
    /// <param name="tags">The tags extracted from the stream name.</param>
    /// <returns>The highest quality score found, or <see cref="ScoreUnknown"/> if none detected.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ScoreFromTags(IReadOnlyList<string> tags)
    {
        if (tags.Count == 0)
        {
            return ScoreUnknown;
        }

        int bestScore = ScoreUnknown;

        for (int i = 0; i < tags.Count; i++)
        {
            var tag = tags[i];
            if (string.IsNullOrEmpty(tag))
            {
                continue;
            }

            // Trim inline without allocation for common cases
            var trimmed = tag.AsSpan().Trim();
            if (trimmed.IsEmpty)
            {
                continue;
            }

            // Try direct lookup first (most common case)
            if (QualityPatterns.TryGetValue(tag, out int score))
            {
                if (score == Score4K)
                {
                    return Score4K; // Early exit - can't get better
                }

                if (score > bestScore)
                {
                    bestScore = score;
                }
            }
        }

        return bestScore;
    }

    /// <summary>
    /// Calculates a quality score by scanning the stream name for quality indicators.
    /// Uses optimized pattern matching with word boundary checking and early exit.
    /// </summary>
    /// <param name="name">The stream name to analyze.</param>
    /// <returns>The highest quality score found, or <see cref="ScoreUnknown"/> if none detected.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ScoreFromName(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty || name.IsWhiteSpace())
        {
            return ScoreUnknown;
        }

        // Check patterns in order of score (highest first) for early exit
        foreach (var (pattern, score) in SortedPatterns)
        {
            if (ContainsWord(name, pattern.AsSpan()))
            {
                return score; // Return immediately - patterns are sorted by score
            }
        }

        return ScoreUnknown;
    }

    /// <summary>
    /// Checks if the name contains the pattern as a whole word (not as a substring).
    /// For example, "SHADE" should NOT match "HD", but "Polsat HD" should.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ContainsWord(ReadOnlySpan<char> name, ReadOnlySpan<char> pattern)
    {
        int index = 0;
        while (index <= name.Length - pattern.Length)
        {
            int found = name[index..].IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                return false;
            }

            int matchStart = index + found;
            int matchEnd = matchStart + pattern.Length;

            // Check word boundaries: character before must be non-alphanumeric or start of string
            bool startOk = matchStart == 0 || !char.IsLetterOrDigit(name[matchStart - 1]);

            // Check word boundaries: character after must be non-alphanumeric or end of string
            bool endOk = matchEnd >= name.Length || !char.IsLetterOrDigit(name[matchEnd]);

            if (startOk && endOk)
            {
                return true;
            }

            // Continue searching after this position
            index = matchStart + 1;
        }

        return false;
    }

    /// <summary>
    /// Calculates a quality score by scanning the stream name for quality indicators.
    /// </summary>
    /// <param name="name">The stream name to analyze.</param>
    /// <returns>The highest quality score found, or <see cref="ScoreUnknown"/> if none detected.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ScoreFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ScoreUnknown;
        }

        return ScoreFromName(name.AsSpan());
    }

    /// <summary>
    /// Calculates a combined quality score using both tags and name analysis.
    /// Optimized to check tags first (O(n) with small n) and short-circuit if max score found.
    /// </summary>
    /// <param name="name">The stream name.</param>
    /// <param name="tags">The tags extracted from the stream name.</param>
    /// <returns>The highest quality score found from either source.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Score(string name, IReadOnlyList<string> tags)
    {
        // Check tags first - usually fewer items and direct lookup
        int tagScore = ScoreFromTags(tags);

        // Early exit if we found the best possible score
        if (tagScore == Score4K)
        {
            return Score4K;
        }

        // Check name only if tags didn't give us max score
        int nameScore = ScoreFromName(name);

        return tagScore > nameScore ? tagScore : nameScore;
    }

    /// <summary>
    /// Calculates a comprehensive score for a stream including quality and image availability.
    /// Streams with images are preferred over those without.
    /// </summary>
    /// <param name="stream">The stream info to score.</param>
    /// <returns>The total score including quality and image bonus.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ScoreStream(StreamInfo stream)
    {
        int qualityScore = ScoreFromName(stream.Name);

        // Add bonus for having an image
        if (!string.IsNullOrEmpty(stream.StreamIcon))
        {
            qualityScore += ScoreHasImage;
        }

        return qualityScore;
    }
}
