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
using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Xtream.Api.Models;

/// <summary>
/// Response model for a discovered provider.
/// </summary>
[SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "DTO requires setter")]
[SuppressMessage("Design", "CA1002:Do not expose generic lists", Justification = "DTO requires setter")]
public sealed class DiscoveredProviderResponse
{
    /// <summary>
    /// Gets or sets the server URL.
    /// </summary>
    public string Server { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the port.
    /// </summary>
    public int Port { get; set; }

    /// <summary>
    /// Gets or sets the username.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the password.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the status.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the expiration date.
    /// </summary>
    public DateTime? ExpirationDate { get; set; }

    /// <summary>
    /// Gets or sets the max connections.
    /// </summary>
    public int MaxConnections { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the provider has channels matching the country filter.
    /// </summary>
    public bool HasCountryChannels { get; set; }

    /// <summary>
    /// Gets or sets the count of channels matching the country filter.
    /// </summary>
    public int CountryChannelCount { get; set; }

    /// <summary>
    /// Gets or sets the country code used for filtering (e.g., "PL", "UK", "DE").
    /// </summary>
    public string? CountryCode { get; set; }

    /// <summary>
    /// Gets or sets the total channel count.
    /// </summary>
    public int TotalChannelCount { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether streams work.
    /// </summary>
    public bool StreamWorks { get; set; }

    /// <summary>
    /// Gets or sets the detailed stream test status message (e.g., "OK (HLS)", "OK (3/3 streams)").
    /// </summary>
    public string? StreamStatus { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether EPG is available.
    /// </summary>
    public bool HasEpg { get; set; }

    /// <summary>
    /// Gets or sets the EPG program count.
    /// </summary>
    public int EpgProgramCount { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this provider is fully working.
    /// </summary>
    public bool IsFullyWorking { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this provider has high quality streams.
    /// </summary>
    public bool HasHighQualityStreams { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this provider is excellent (fully working + high quality).
    /// </summary>
    public bool IsExcellent { get; set; }

    /// <summary>
    /// Gets or sets the stream quality score (0-100, higher is better).
    /// </summary>
    public int? QualityScore { get; set; }

    /// <summary>
    /// Gets or sets the stream quality level (Excellent, Good, Fair, Poor, Unknown).
    /// </summary>
    public string? QualityLevel { get; set; }

    /// <summary>
    /// Gets or sets any quality issues found.
    /// </summary>
    public string? QualityIssues { get; set; }

    /// <summary>
    /// Gets or sets the comprehensive trust score (0-100).
    /// </summary>
    public int? TrustScore { get; set; }

    /// <summary>
    /// Gets or sets the trust level (Excellent, Good, Fair, Poor, Untrusted).
    /// </summary>
    public string? TrustLevel { get; set; }

    /// <summary>
    /// Gets or sets the trust assessment summary.
    /// </summary>
    public string? TrustSummary { get; set; }

    /// <summary>
    /// Gets or sets any error message.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Gets or sets the channel names matching the country filter.
    /// </summary>
    public List<string> CountryChannelNames { get; set; } = [];
}

/// <summary>
/// Response model for the discovery operation.
/// </summary>
[SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "DTO requires setter")]
[SuppressMessage("Design", "CA1002:Do not expose generic lists", Justification = "DTO requires setter")]
public sealed class DiscoveryResponse
{
    /// <summary>
    /// Gets or sets a value indicating whether the operation was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets any error message.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Gets or sets the number of pages processed.
    /// </summary>
    public int PagesProcessed { get; set; }

    /// <summary>
    /// Gets or sets the total credentials found.
    /// </summary>
    public int TotalCredentialsFound { get; set; }

    /// <summary>
    /// Gets or sets the total credentials tested.
    /// </summary>
    public int TotalCredentialsTested { get; set; }

    /// <summary>
    /// Gets or sets the count of working providers.
    /// </summary>
    public int WorkingProviderCount { get; set; }

    /// <summary>
    /// Gets or sets the count of working providers with EPG.
    /// </summary>
    public int WorkingWithEpgCount { get; set; }

    /// <summary>
    /// Gets or sets the count of fully working providers.
    /// </summary>
    public int FullyWorkingCount { get; set; }

    /// <summary>
    /// Gets or sets the count of excellent providers (fully working + high quality).
    /// </summary>
    public int ExcellentCount { get; set; }

    /// <summary>
    /// Gets or sets the country code used for filtering (e.g., "PL", "UK", "DE").
    /// </summary>
    public string? CountryCode { get; set; }

    /// <summary>
    /// Gets or sets the working providers.
    /// </summary>
    public List<DiscoveredProviderResponse> WorkingProviders { get; set; } = [];

    /// <summary>
    /// Gets or sets the fully working providers (with EPG, stream, and country channels).
    /// </summary>
    public List<DiscoveredProviderResponse> FullyWorkingProviders { get; set; } = [];

    /// <summary>
    /// Gets or sets the excellent providers (fully working + high quality streams).
    /// </summary>
    public List<DiscoveredProviderResponse> ExcellentProviders { get; set; } = [];
}

/// <summary>
/// Response model for discovery progress.
/// </summary>
public sealed class DiscoveryProgressResponse
{
    /// <summary>
    /// Gets or sets the current phase.
    /// </summary>
    public string Phase { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the current item.
    /// </summary>
    public int CurrentItem { get; set; }

    /// <summary>
    /// Gets or sets the total items.
    /// </summary>
    public int TotalItems { get; set; }

    /// <summary>
    /// Gets or sets the progress percentage.
    /// </summary>
    public int ProgressPercent { get; set; }

    /// <summary>
    /// Gets or sets the credentials found.
    /// </summary>
    public int CredentialsFound { get; set; }

    /// <summary>
    /// Gets or sets the count of providers that passed connectivity check.
    /// </summary>
    public int ConnectivityPassed { get; set; }

    /// <summary>
    /// Gets or sets the count of providers that passed authentication.
    /// </summary>
    public int AuthenticationPassed { get; set; }

    /// <summary>
    /// Gets or sets the working providers found.
    /// </summary>
    public int WorkingProviders { get; set; }

    /// <summary>
    /// Gets or sets the working providers with EPG found.
    /// </summary>
    public int WorkingWithEpg { get; set; }

    /// <summary>
    /// Gets or sets the fully working providers found (Working + EPG + country channels).
    /// </summary>
    public int FullyWorking { get; set; }

    /// <summary>
    /// Gets or sets the excellent providers found (fully working + high quality).
    /// </summary>
    public int Excellent { get; set; }

    /// <summary>
    /// Gets or sets the status message.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the count of items currently being processed across all stages.
    /// </summary>
    public int InProgress { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the pipeline has finished processing all items.
    /// </summary>
    public bool IsComplete { get; set; }
}

/// <summary>
/// Response for discovery status.
/// </summary>
public sealed class DiscoveryStatusResponse
{
    /// <summary>
    /// Gets or sets a value indicating whether discovery is running.
    /// </summary>
    public bool IsRunning { get; set; }

    /// <summary>
    /// Gets or sets the current progress.
    /// </summary>
    public DiscoveryProgressResponse? Progress { get; set; }
}
