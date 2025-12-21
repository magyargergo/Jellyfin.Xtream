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
    /// Gets or sets a value indicating whether the provider has Polish channels.
    /// </summary>
    public bool HasPolishChannels { get; set; }

    /// <summary>
    /// Gets or sets the count of Polish channels.
    /// </summary>
    public int PolishChannelCount { get; set; }

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
    /// Gets or sets any error message.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Gets or sets the Polish channel names.
    /// </summary>
    public List<string> PolishChannelNames { get; set; } = [];
}

/// <summary>
/// Backward compatibility alias for DiscoveredProviderResponse.
/// </summary>
[SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "DTO requires setter")]
[SuppressMessage("Design", "CA1002:Do not expose generic lists", Justification = "DTO requires setter")]
public sealed class ScrapedProviderResponse
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
    /// Gets or sets a value indicating whether the provider has Polish channels.
    /// </summary>
    public bool HasPolishChannels { get; set; }

    /// <summary>
    /// Gets or sets the count of Polish channels.
    /// </summary>
    public int PolishChannelCount { get; set; }

    /// <summary>
    /// Gets or sets the total channel count.
    /// </summary>
    public int TotalChannelCount { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether streams work.
    /// </summary>
    public bool StreamWorks { get; set; }

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
    /// Gets or sets any error message.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Gets or sets the Polish channel names.
    /// </summary>
    public List<string> PolishChannelNames { get; set; } = [];
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
    /// Gets or sets the working providers.
    /// </summary>
    public List<DiscoveredProviderResponse> WorkingProviders { get; set; } = [];

    /// <summary>
    /// Gets or sets the fully working providers (with EPG, stream, and Polish).
    /// </summary>
    public List<DiscoveredProviderResponse> FullyWorkingProviders { get; set; } = [];
}

/// <summary>
/// Backward compatibility alias for DiscoveryResponse.
/// </summary>
[SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "DTO requires setter")]
[SuppressMessage("Design", "CA1002:Do not expose generic lists", Justification = "DTO requires setter")]
public sealed class ScrapeResponse
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
    public int PagesScraped { get; set; }

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
    /// Gets or sets the working providers.
    /// </summary>
    public List<ScrapedProviderResponse> WorkingProviders { get; set; } = [];

    /// <summary>
    /// Gets or sets the fully working providers (with EPG, stream, and Polish).
    /// </summary>
    public List<ScrapedProviderResponse> FullyWorkingProviders { get; set; } = [];
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
    /// Gets or sets the working providers found.
    /// </summary>
    public int WorkingProviders { get; set; }

    /// <summary>
    /// Gets or sets the working providers with EPG found.
    /// </summary>
    public int WorkingWithEpg { get; set; }

    /// <summary>
    /// Gets or sets the fully working providers found (Working + EPG + Polish).
    /// </summary>
    public int FullyWorking { get; set; }

    /// <summary>
    /// Gets or sets the status message.
    /// </summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Backward compatibility alias for DiscoveryProgressResponse.
/// </summary>
public sealed class ScrapeProgressResponse
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
    /// Gets or sets the working providers found.
    /// </summary>
    public int WorkingProviders { get; set; }

    /// <summary>
    /// Gets or sets the working providers with EPG found.
    /// </summary>
    public int WorkingWithEpg { get; set; }

    /// <summary>
    /// Gets or sets the fully working providers found (Working + EPG + Polish).
    /// </summary>
    public int FullyWorking { get; set; }

    /// <summary>
    /// Gets or sets the status message.
    /// </summary>
    public string Message { get; set; } = string.Empty;
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

/// <summary>
/// Backward compatibility alias for DiscoveryStatusResponse.
/// </summary>
public sealed class ScrapeStatusResponse
{
    /// <summary>
    /// Gets or sets a value indicating whether discovery is running.
    /// </summary>
    public bool IsRunning { get; set; }

    /// <summary>
    /// Gets or sets the current progress.
    /// </summary>
    public ScrapeProgressResponse? Progress { get; set; }
}
