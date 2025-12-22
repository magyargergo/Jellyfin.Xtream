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
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Xtream.Service.ChannelMatching;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Maps channels to their available providers, supporting deduplication and failover.
/// Uses frozen collections for O(1) lookup after build phase.
/// </summary>
public sealed partial class ChannelProviderMap
{
    // Threshold for using parallel processing (avoid overhead for small datasets)
    private const int ParallelThreshold = 500;

    private readonly FrozenDictionary<string, ChannelWithProviders> _channels;
    private readonly FrozenDictionary<Guid, string> _guidToNormalizedName;

    private ChannelProviderMap(
        FrozenDictionary<string, ChannelWithProviders> channels,
        FrozenDictionary<Guid, string> guidToNormalizedName,
        int skippedCount
    )
    {
        _channels = channels;
        _guidToNormalizedName = guidToNormalizedName;
        SkippedCount = skippedCount;
    }

    /// <summary>
    /// Gets the number of unique channels (after deduplication).
    /// </summary>
    public int ChannelCount => _channels.Count;

    /// <summary>
    /// Gets the number of streams that were skipped due to empty names after normalization.
    /// </summary>
    public int SkippedCount { get; }

    /// <summary>
    /// Gets all unique channels with their providers.
    /// </summary>
    public IEnumerable<ChannelWithProviders> Channels => _channels.Values;

    /// <summary>
    /// Builds the channel map from a collection of provider streams.
    /// Optimized with single-pass grouping, parallel processing for large datasets,
    /// and combined name normalization to avoid redundant regex operations.
    /// </summary>
    /// <param name="providerStreams">All streams from all providers.</param>
    /// <returns>A new <see cref="ChannelProviderMap"/> with channels grouped and sorted by quality.</returns>
    public static ChannelProviderMap Build(IEnumerable<ProviderStreamInfo> providerStreams)
    {
        return Build(providerStreams, availabilityScorer: null);
    }

    /// <summary>
    /// Builds the channel map with connection-aware provider ordering.
    /// Providers are sorted by a combination of quality and availability scores.
    /// </summary>
    /// <param name="providerStreams">All streams from all providers.</param>
    /// <param name="availabilityScorer">Function to get availability score (0-100) for a provider ID.
    /// Higher scores indicate more available capacity. Pass null to use quality-only sorting.</param>
    /// <returns>A new <see cref="ChannelProviderMap"/> with channels grouped and sorted.</returns>
    public static ChannelProviderMap Build(
        IEnumerable<ProviderStreamInfo> providerStreams,
        Func<string, int>? availabilityScorer
    )
    {
        // Materialize once to avoid multiple enumeration
        var streamsList = providerStreams as IList<ProviderStreamInfo> ?? providerStreams.ToList();

        if (streamsList.Count == 0)
        {
            return new ChannelProviderMap(
                FrozenDictionary<string, ChannelWithProviders>.Empty,
                FrozenDictionary<Guid, string>.Empty,
                skippedCount: 0
            );
        }

        // Use parallel processing for large datasets
        return streamsList.Count >= ParallelThreshold
            ? BuildParallel(streamsList, availabilityScorer)
            : BuildSequential(streamsList, availabilityScorer);
    }

    private static ChannelProviderMap BuildSequential(
        IList<ProviderStreamInfo> streamsList,
        Func<string, int>? availabilityScorer
    )
    {
        var channelDict = new Dictionary<string, List<(ProviderStreamInfo Stream, int Score, string DisplayName)>>(
            streamsList.Count / 2,
            StringComparer.OrdinalIgnoreCase
        );

        var guidMapping = new Dictionary<Guid, string>(streamsList.Count);
        int skippedCount = 0;

        foreach (var ps in streamsList)
        {
            var (normalizedName, displayName) = ProcessChannelName(ps.Stream.Name);
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                skippedCount++;
                continue;
            }

            int score = CalculateProviderScore(ps, availabilityScorer);

            ref var listRef = ref CollectionsMarshal.GetValueRefOrAddDefault(
                channelDict,
                normalizedName,
                out bool exists
            );
            if (!exists)
            {
                listRef = new List<(ProviderStreamInfo, int, string)>(4);
            }

            listRef!.Add((ps, score, displayName));

            var guid = StreamService.ToProviderGuid(StreamService.LiveTvPrefix, ps.Provider, ps.Stream.StreamId);
            guidMapping[guid] = normalizedName;
        }

        return BuildFromGrouped(channelDict, guidMapping, skippedCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CalculateProviderScore(ProviderStreamInfo ps, Func<string, int>? availabilityScorer)
    {
        int qualityScore = QualityScorer.ScoreStream(ps.Stream);
        if (availabilityScorer == null)
        {
            return qualityScore;
        }

        int availabilityScore = availabilityScorer(ps.Provider.Id);
        return QualityScorer.CombinedScore(qualityScore, availabilityScore);
    }

    private static ChannelProviderMap BuildParallel(
        IList<ProviderStreamInfo> streamsList,
        Func<string, int>? availabilityScorer
    )
    {
        // Thread-safe collections for parallel grouping
        var channelDict = new Dictionary<string, List<(ProviderStreamInfo Stream, int Score, string DisplayName)>>(
            streamsList.Count / 2,
            StringComparer.OrdinalIgnoreCase
        );

        var guidMapping = new Dictionary<Guid, string>(streamsList.Count);
        int skippedCount = 0;

        // Lock object for dictionary access (fine-grained locking is overkill here)
        object lockObj = new();

        // Parallel processing with partitioning for better cache locality
        Parallel.ForEach(
            Partitioner.Create(0, streamsList.Count),
            () =>
                (
                    List: new List<(ProviderStreamInfo Ps, int Score, string Normalized, string Display, Guid Guid)>(
                        64
                    ),
                    Skipped: 0
                ),
            (range, _, local) =>
            {
                for (int i = range.Item1; i < range.Item2; i++)
                {
                    var ps = streamsList[i];
                    var (normalizedName, displayName) = ProcessChannelName(ps.Stream.Name);
                    if (string.IsNullOrWhiteSpace(normalizedName))
                    {
                        local.Skipped++;
                        continue;
                    }

                    int score = CalculateProviderScore(ps, availabilityScorer);

                    var guid = StreamService.ToProviderGuid(
                        StreamService.LiveTvPrefix,
                        ps.Provider,
                        ps.Stream.StreamId
                    );
                    local.List.Add((ps, score, normalizedName, displayName, guid));
                }

                return local;
            },
            local =>
            {
                if (local.List.Count == 0 && local.Skipped == 0)
                {
                    return;
                }

                lock (lockObj)
                {
                    skippedCount += local.Skipped;
                    foreach (var item in local.List)
                    {
                        ref var listRef = ref CollectionsMarshal.GetValueRefOrAddDefault(
                            channelDict,
                            item.Normalized,
                            out bool exists
                        );
                        if (!exists)
                        {
                            listRef = new List<(ProviderStreamInfo, int, string)>(4);
                        }

                        listRef!.Add((item.Ps, item.Score, item.Display));
                        guidMapping[item.Guid] = item.Normalized;
                    }
                }
            }
        );

        return BuildFromGrouped(channelDict, guidMapping, skippedCount);
    }

    private static ChannelProviderMap BuildFromGrouped(
        Dictionary<string, List<(ProviderStreamInfo Stream, int Score, string DisplayName)>> channelDict,
        Dictionary<Guid, string> guidMapping,
        int skippedCount
    )
    {
        var channels = new Dictionary<string, ChannelWithProviders>(
            channelDict.Count,
            StringComparer.OrdinalIgnoreCase
        );

        foreach (var (name, streamScores) in channelDict)
        {
            // Sort by score descending, then by provider name for stability
            streamScores.Sort(
                (a, b) =>
                {
                    int cmp = b.Score.CompareTo(a.Score);
                    return cmp != 0
                        ? cmp
                        : string.Compare(a.Stream.Provider.Name, b.Stream.Provider.Name, StringComparison.Ordinal);
                }
            );

            var sortedProviders = new ProviderStreamInfo[streamScores.Count];
            for (int i = 0; i < streamScores.Count; i++)
            {
                sortedProviders[i] = streamScores[i].Stream;
            }

            // Use the display name from the best provider (first after sorting)
            var displayName = streamScores[0].DisplayName;

            channels[name] = new ChannelWithProviders(name, displayName, sortedProviders);
        }

        return new ChannelProviderMap(
            channels.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            guidMapping.ToFrozenDictionary(),
            skippedCount
        );
    }

    /// <summary>
    /// Gets all providers for a channel by its GUID.
    /// </summary>
    /// <param name="channelGuid">The channel GUID.</param>
    /// <returns>The channel with all its providers, or null if not found.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ChannelWithProviders? GetByGuid(Guid channelGuid)
    {
        if (_guidToNormalizedName.TryGetValue(channelGuid, out var normalizedName))
        {
            return GetByName(normalizedName);
        }

        return null;
    }

    /// <summary>
    /// Gets all providers for a channel by its normalized name.
    /// </summary>
    /// <param name="normalizedName">The normalized channel name.</param>
    /// <returns>The channel with all its providers, or null if not found.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ChannelWithProviders? GetByName(string normalizedName)
    {
        _channels.TryGetValue(normalizedName, out var channel);
        return channel;
    }

    /// <summary>
    /// Gets alternative providers for a channel, excluding a specific provider.
    /// </summary>
    /// <param name="channelGuid">The channel GUID.</param>
    /// <param name="excludeProviderId">The provider ID to exclude.</param>
    /// <returns>Alternative providers ordered by quality, or empty if none available.</returns>
    public IEnumerable<ProviderStreamInfo> GetAlternatives(Guid channelGuid, string excludeProviderId)
    {
        var channel = GetByGuid(channelGuid);
        if (channel == null)
        {
            return [];
        }

        return channel.Providers.Where(p => p.Provider.Id != excludeProviderId);
    }

    /// <summary>
    /// Gets the next best provider for failover.
    /// </summary>
    /// <param name="channelGuid">The channel GUID.</param>
    /// <param name="failedProviderIds">Provider IDs that have already failed.</param>
    /// <returns>The next available provider, or null if all have failed.</returns>
    public ProviderStreamInfo? GetNextProvider(Guid channelGuid, ISet<string> failedProviderIds)
    {
        var channel = GetByGuid(channelGuid);
        if (channel == null)
        {
            return null;
        }

        var providers = channel.Providers;
        for (int i = 0; i < providers.Count; i++)
        {
            if (!failedProviderIds.Contains(providers[i].Provider.Id))
            {
                return providers[i];
            }
        }

        return null;
    }

    /// <summary>
    /// Processes a channel name, returning both the normalized name (for deduplication)
    /// and the display name (for UI).
    /// Uses the same ChannelNameNormalizer as channel copying for consistency.
    /// </summary>
    /// <param name="name">The raw channel name.</param>
    /// <returns>A tuple of (NormalizedName, DisplayName) where NormalizedName is for matching
    /// and DisplayName is cleaned for readability.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (string NormalizedName, string DisplayName) ProcessChannelName(string name)
    {
        // Use the same normalizer as CopyChannelSelections for consistent matching
        var normalizedName = ChannelNameNormalizer.Default.Normalize(name);

        // For display name, use StreamService.ParseName which strips tags but keeps the name readable
        var parsed = StreamService.ParseName(name);
        var displayName = DisplayNameCleanupRegex().Replace(parsed.Title, " ").Trim();
        displayName = MultipleSpacesRegex().Replace(displayName, " ");

        return (normalizedName, displayName);
    }

    // Matches country prefixes and quality indicators for display name cleanup
    // Less aggressive than full normalization - keeps the name readable
    // Handles: "PL:", "PL-", "PL|", "PL |", "|PL|", "[PL]", "(PL)"
    [GeneratedRegex(
        @"^(\d+\s+)?(\|?[A-Z]{2,3}\||\[[A-Z]{2,3}\]|\([A-Z]{2,3}\)|[A-Z]{2,3}\s*[:\-\|])\s*|\b(HD|FHD|SD|4K|UHD)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    )]
    private static partial Regex DisplayNameCleanupRegex();

    // Matches multiple consecutive spaces
    [GeneratedRegex(@"\s{2,}", RegexOptions.Compiled)]
    private static partial Regex MultipleSpacesRegex();
}
