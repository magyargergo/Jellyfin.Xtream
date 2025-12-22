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

using System.Collections.Generic;
using Jellyfin.Xtream.Client.Models;

namespace Jellyfin.Xtream.Service.ChannelMatching;

/// <summary>
/// Matches channels between providers using normalized name comparison.
/// </summary>
public interface IChannelMatcher
{
    /// <summary>
    /// Finds the best matching target stream for a given source stream.
    /// </summary>
    /// <param name="sourceStream">The source stream to match.</param>
    /// <param name="targetIndex">The pre-built index of target streams.</param>
    /// <returns>The match result containing the best match and similarity score.</returns>
    ChannelMatchResult FindBestMatch(StreamInfo sourceStream, ChannelMatchIndex targetIndex);

    /// <summary>
    /// Builds an index of streams for efficient matching.
    /// Normalizes all stream names once and creates lookup structures.
    /// </summary>
    /// <param name="streams">The streams to index.</param>
    /// <returns>An index optimized for channel matching.</returns>
    ChannelMatchIndex BuildIndex(IEnumerable<StreamInfo> streams);
}

/// <summary>
/// Result of a channel matching operation.
/// </summary>
/// <param name="MatchedStream">The matched stream, or null if no match found.</param>
/// <param name="SimilarityScore">The similarity score (0-100), 100 being exact match.</param>
/// <param name="NormalizedName">The normalized name used for matching.</param>
public readonly record struct ChannelMatchResult(StreamInfo? MatchedStream, int SimilarityScore, string NormalizedName);

/// <summary>
/// Pre-computed index of streams for efficient channel matching.
/// </summary>
public sealed class ChannelMatchIndex
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelMatchIndex"/> class.
    /// </summary>
    /// <param name="exactMatchLookup">Dictionary for O(1) exact match lookups.</param>
    /// <param name="normalizedStreams">List of streams with their pre-computed normalized names.</param>
    internal ChannelMatchIndex(
        IReadOnlyDictionary<string, List<StreamInfo>> exactMatchLookup,
        IReadOnlyList<(StreamInfo Stream, string NormalizedName)> normalizedStreams
    )
    {
        ExactMatchLookup = exactMatchLookup;
        NormalizedStreams = normalizedStreams;
    }

    /// <summary>
    /// Gets the dictionary for O(1) exact normalized name lookups.
    /// </summary>
    internal IReadOnlyDictionary<string, List<StreamInfo>> ExactMatchLookup { get; }

    /// <summary>
    /// Gets the list of all streams with their pre-computed normalized names.
    /// Used for fuzzy matching when exact match fails.
    /// </summary>
    internal IReadOnlyList<(StreamInfo Stream, string NormalizedName)> NormalizedStreams { get; }

    /// <summary>
    /// Gets the total number of indexed streams.
    /// </summary>
    public int Count => NormalizedStreams.Count;
}
