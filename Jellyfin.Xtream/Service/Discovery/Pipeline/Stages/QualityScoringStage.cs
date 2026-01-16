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
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Client.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.Discovery.Pipeline.Stages;

/// <summary>
/// Final pipeline stage: Quality and trust scoring.
/// Calculates comprehensive provider trust score and produces final result.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="QualityScoringStage"/> class.
/// </remarks>
/// <param name="logger">The logger.</param>
public sealed class QualityScoringStage(ILogger logger)
    : PipelineStageBase(
        PipelineStage.QualityScoring,
        logger,
        new StageConfiguration
        {
            Concurrency = 4,
            TimeoutMs = 1000,
            ContinueOnError = true,
        }
    )
{
    /// <inheritdoc />
    protected override ValueTask<StageResult<PipelineItem>> ProcessAsync(
        PipelineItem item,
        CancellationToken cancellationToken
    )
    {
        // Build the final ProviderTestResult from accumulated properties
        var result = BuildTestResult(item);

        // Calculate trust score
        result.CalculateTrustScore();

        // All items that reach this stage pass - we just attach the score
        var enrichedItem = item.WithProperty("TestResult", result).AdvanceTo(PipelineStage.Completed);

        return ValueTask.FromResult(StageResult.Pass(enrichedItem));
    }

    private static ProviderTestResult BuildTestResult(PipelineItem item)
    {
        var credential = item.Credential;
        var streams = item.GetProperty<IReadOnlyCollection<StreamInfo>>(PipelinePropertyKeys.Streams);
        var filteredChannels = item.GetProperty<IList<StreamInfo>>(PipelinePropertyKeys.FilteredChannels);
        var filteredCategories = item.GetProperty<IList<string>>(PipelinePropertyKeys.FilteredCategories);

        var result = new ProviderTestResult
        {
            Credential = credential,
            Status = ProviderStatus.Active,
            TotalChannelCount = streams?.Count ?? 0,
            StreamWorks = true,
            StreamStatus = item.GetProperty<string>(PipelinePropertyKeys.StreamResult) ?? "OK",
            StreamQuality = item.GetProperty<StreamQualitySnapshot>(PipelinePropertyKeys.StreamQuality),
            HasEpg = item.HasProperty(PipelinePropertyKeys.EpgCount),
            EpgProgramCount = item.GetProperty<int>(PipelinePropertyKeys.EpgCount),
            HasCountryChannels = filteredChannels?.Count > 0,
            CountryChannelCount = filteredChannels?.Count ?? 0,
            CountryCode = item.GetProperty<string>(PipelinePropertyKeys.FilterCountryCode),
            MaxConnections = item.GetProperty<int>(PipelinePropertyKeys.MaxConnections),
            ActiveConnections = item.GetProperty<int>(PipelinePropertyKeys.ActiveConnections),
        };

        // Set expiration date
        var expDate = item.GetProperty<DateTime?>(PipelinePropertyKeys.ExpirationDate);
        if (expDate.HasValue)
        {
            result.ExpirationDate = expDate.Value;
        }

        // Set filtered channel names (for display)
        if (filteredChannels != null)
        {
            result.CountryChannelNames =
            [
                .. filteredChannels.Take(50).Select(s => s.Name ?? string.Empty).Where(n => !string.IsNullOrEmpty(n)),
            ];
        }

        // Set filtered categories
        if (filteredCategories != null)
        {
            result.CountryCategories = [.. filteredCategories];
        }

        return result;
    }
}
