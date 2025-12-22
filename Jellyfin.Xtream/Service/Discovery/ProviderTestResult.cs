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

namespace Jellyfin.Xtream.Service.Discovery;

/// <summary>
/// Represents the status of a tested provider credential.
/// </summary>
public enum ProviderStatus
{
    /// <summary>
    /// Account is active and working.
    /// </summary>
    Active,

    /// <summary>
    /// Account has expired.
    /// </summary>
    Expired,

    /// <summary>
    /// Credentials are invalid.
    /// </summary>
    Invalid,

    /// <summary>
    /// Error occurred during testing.
    /// </summary>
    Error,
}

/// <summary>
/// Represents the result of testing a discovered provider credential.
/// </summary>
[SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "DTO requires setter")]
[SuppressMessage("Design", "CA1002:Do not expose generic lists", Justification = "DTO requires setter")]
public sealed class ProviderTestResult
{
    /// <summary>
    /// Gets or sets the original discovered credential.
    /// </summary>
    public DiscoveredCredential Credential { get; set; } = new();

    /// <summary>
    /// Gets or sets the provider status.
    /// </summary>
    public ProviderStatus Status { get; set; } = ProviderStatus.Error;

    /// <summary>
    /// Gets or sets the account expiration date.
    /// </summary>
    public DateTime? ExpirationDate { get; set; }

    /// <summary>
    /// Gets a value indicating whether the account is expiring soon (within 7 days).
    /// </summary>
    public bool IsExpiringSoon =>
        ExpirationDate.HasValue
        && Status == ProviderStatus.Active
        && ExpirationDate.Value <= DateTime.UtcNow.AddDays(7);

    /// <summary>
    /// Gets the number of days until expiration, or null if no expiration date.
    /// </summary>
    public int? DaysUntilExpiration =>
        ExpirationDate.HasValue ? (int)Math.Ceiling((ExpirationDate.Value - DateTime.UtcNow).TotalDays) : null;

    /// <summary>
    /// Gets or sets the maximum allowed connections.
    /// </summary>
    public int MaxConnections { get; set; }

    /// <summary>
    /// Gets or sets the current active connections.
    /// </summary>
    public int ActiveConnections { get; set; }

    /// <summary>
    /// Gets or sets whether the provider has channels matching the country filter.
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
    /// Gets or sets whether stream playback works.
    /// </summary>
    public bool StreamWorks { get; set; }

    /// <summary>
    /// Gets or sets the stream test status message.
    /// </summary>
    public string? StreamStatus { get; set; }

    /// <summary>
    /// Gets or sets the stream quality metrics captured during testing.
    /// </summary>
    public StreamQualitySnapshot? StreamQuality { get; set; }

    /// <summary>
    /// Gets or sets whether EPG data is available.
    /// </summary>
    public bool HasEpg { get; set; }

    /// <summary>
    /// Gets or sets the EPG program count from test.
    /// </summary>
    public int EpgProgramCount { get; set; }

    /// <summary>
    /// Gets or sets any error message.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Gets or sets the channel names found matching the country filter.
    /// </summary>
    public List<string> CountryChannelNames { get; set; } = [];

    /// <summary>
    /// Gets or sets the category prefixes found matching the country filter.
    /// </summary>
    public List<string> CountryCategories { get; set; } = [];

    /// <summary>
    /// Gets a value indicating whether this provider passed all checks.
    /// </summary>
    public bool IsFullyWorking => Status == ProviderStatus.Active && StreamWorks && HasEpg && HasCountryChannels;

    /// <summary>
    /// Gets a value indicating whether this provider has high quality streams.
    /// </summary>
    public bool HasHighQualityStreams => StreamQuality?.PassesQualityThreshold ?? false;

    /// <summary>
    /// Gets a value indicating whether this provider is excellent (all checks + high quality).
    /// </summary>
    public bool IsExcellent => IsFullyWorking && HasHighQualityStreams;

    /// <summary>
    /// Gets or sets the comprehensive trust score for this provider.
    /// </summary>
    public ProviderTrustScore? TrustScore { get; set; }

    /// <summary>
    /// Calculates and sets the trust score based on current test results.
    /// </summary>
    public void CalculateTrustScore()
    {
        TrustScore = ProviderTrustScore.Calculate(this);
    }
}
