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
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Service.ChannelMatching;
using Jellyfin.Xtream.Service.ChannelMatching.Rules;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.Discovery.Pipeline.Stages;

/// <summary>
/// Generic country filter stage that can be configured for any country.
/// Uses country code prefix detection and optional broadcaster matching.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="CountryFilterStage"/> class.
/// </remarks>
/// <param name="httpClientFactory">HTTP client factory.</param>
/// <param name="countryProfile">The country profile to filter by.</param>
/// <param name="stage">The pipeline stage identifier.</param>
/// <param name="logger">The logger.</param>
public class CountryFilterStage(
    IHttpClientFactory httpClientFactory,
    CountryProfile countryProfile,
    PipelineStage stage,
    ILogger logger
)
    : PipelineStageBase(
        stage,
        logger,
        new StageConfiguration
        {
            Concurrency = 10,
            TimeoutMs = 15000,
            ContinueOnError = true,
        }
    )
{
    private static readonly CountryDetectionNormalizer Normalizer = CountryDetectionNormalizer.Default;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;

    /// <summary>
    /// Gets the country profile used by this filter.
    /// </summary>
    protected CountryProfile CountryProfile { get; } = countryProfile;

    /// <inheritdoc />
    protected override async ValueTask<StageResult<PipelineItem>> ProcessAsync(
        PipelineItem item,
        CancellationToken cancellationToken
    )
    {
        var connectionInfo = item.GetProperty<ConnectionInfo>(PipelinePropertyKeys.AuthInfo);

        if (connectionInfo == null)
        {
            return StageResult.Fail<PipelineItem>("No connection info");
        }

        // Fetch streams if not already available
        var streams = item.GetProperty<IReadOnlyCollection<StreamInfo>>(PipelinePropertyKeys.Streams);

        if (streams == null || streams.Count == 0)
        {
            try
            {
                using var client = new XtreamClient(_httpClientFactory);
                streams = await client.GetLiveStreamsAsync(connectionInfo, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                return StageResult.Fail<PipelineItem>($"HTTP: {ex.Message}");
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return StageResult.Fail<PipelineItem>("Timeout");
            }
            catch (Exception ex)
            {
                return StageResult.Fail<PipelineItem>($"Error fetching streams: {ex.Message}");
            }
        }

        if (streams == null || streams.Count == 0)
        {
            return StageResult.Fail<PipelineItem>("No streams available");
        }

        var matchedChannels = new List<StreamInfo>();
        var matchedCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var stream in streams)
        {
            if (IsCountryChannel(stream, out var category))
            {
                matchedChannels.Add(stream);
                if (!string.IsNullOrEmpty(category))
                {
                    _ = matchedCategories.Add(category);
                }
            }
        }

        if (matchedChannels.Count == 0)
        {
            return StageResult.Fail<PipelineItem>($"No {CountryProfile.CountryCode} channels");
        }

        var enrichedItem = item.WithProperties(
            (PipelinePropertyKeys.Streams, streams),
            (PipelinePropertyKeys.StreamCount, (object)streams.Count),
            (PipelinePropertyKeys.FilteredChannels, matchedChannels),
            (PipelinePropertyKeys.FilteredChannelCount, matchedChannels.Count),
            (PipelinePropertyKeys.FilteredCategories, matchedCategories.Take(10).ToList()),
            (PipelinePropertyKeys.FilterCountryCode, CountryProfile.CountryCode)
        );

        return StageResult.Pass(enrichedItem);
    }

    /// <summary>
    /// Determines if a stream belongs to the configured country.
    /// </summary>
    /// <param name="stream">The stream to check.</param>
    /// <param name="category">Output: the detected category, if any.</param>
    /// <returns>True if the stream matches the country filter.</returns>
    protected virtual bool IsCountryChannel(StreamInfo stream, out string? category)
    {
        category = null;
        var name = stream.Name;

        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        // Tier 1: Check country code prefix using shared extraction (highest priority)
        var countryCode = NormalizationPatterns.ExtractCountryCode(name);
        if (string.Equals(countryCode, CountryProfile.CountryCode, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Tier 2: Exact broadcaster match
        var normalizedName = Normalizer.Normalize(name);
        if (CountryProfile.BroadcasterSet.Contains(normalizedName))
        {
            return true;
        }

        // Tier 3: Check if any broadcaster is contained in the name
        foreach (var broadcaster in CountryProfile.Broadcasters)
        {
            if (ContainsWordBoundary(normalizedName, broadcaster))
            {
                return true;
            }
        }

        // Tier 4: Regex pattern match (if configured)
        if (CountryProfile.BroadcasterRegex?.IsMatch(name) == true)
        {
            return true;
        }

        // Tier 5: Country name indicators in the channel name
        foreach (var countryName in CountryProfile.CountryNames)
        {
            if (name.Contains(countryName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if text contains a word at word boundaries.
    /// </summary>
    private static bool ContainsWordBoundary(string text, string word)
    {
        var index = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return false;
        }

        // Check word boundaries
        var beforeOk = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
        var afterIndex = index + word.Length;
        var afterOk = afterIndex >= text.Length || !char.IsLetterOrDigit(text[afterIndex]);

        return beforeOk && afterOk;
    }
}
