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

namespace Jellyfin.Xtream.Service.ChannelMatching;

/// <summary>
/// Provides string similarity algorithms for channel name matching.
/// </summary>
public static class StringSimilarity
{
    /// <summary>
    /// Calculates the Levenshtein (edit) distance between two strings.
    /// Uses a memory-efficient two-row algorithm: O(m*n) time, O(n) space.
    /// </summary>
    /// <param name="s1">The first string.</param>
    /// <param name="s2">The second string.</param>
    /// <returns>The minimum number of single-character edits to transform s1 into s2.</returns>
    public static int LevenshteinDistance(string s1, string s2)
    {
        if (string.IsNullOrEmpty(s1))
        {
            return s2?.Length ?? 0;
        }

        if (string.IsNullOrEmpty(s2))
        {
            return s1.Length;
        }

        var m = s1.Length;
        var n = s2.Length;

        // Optimize by using the shorter string for the column dimension
        if (m < n)
        {
            (s1, s2) = (s2, s1);
            (m, n) = (n, m);
        }

        // Use two rows instead of full matrix for space efficiency
        var prev = new int[n + 1];
        var curr = new int[n + 1];

        // Initialize first row
        for (var j = 0; j <= n; j++)
        {
            prev[j] = j;
        }

        // Fill the matrix row by row
        for (var i = 1; i <= m; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= n; j++)
            {
                var cost = s1[i - 1] == s2[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            // Swap rows
            (prev, curr) = (curr, prev);
        }

        return prev[n];
    }

    /// <summary>
    /// Calculates a similarity score (0-100) based on Levenshtein distance.
    /// 100 means identical strings, 0 means completely different.
    /// </summary>
    /// <param name="s1">The first string.</param>
    /// <param name="s2">The second string.</param>
    /// <returns>Similarity percentage (0-100).</returns>
    public static int CalculateSimilarity(string s1, string s2)
    {
        if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2))
        {
            return 0;
        }

        var maxLen = Math.Max(s1.Length, s2.Length);
        if (maxLen == 0)
        {
            return 100;
        }

        var distance = LevenshteinDistance(s1, s2);
        return (int)(((maxLen - distance) * 100.0) / maxLen);
    }
}
