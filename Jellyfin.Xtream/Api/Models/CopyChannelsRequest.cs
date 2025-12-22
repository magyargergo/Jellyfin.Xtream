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
using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Xtream.Api.Models;

/// <summary>
/// Request model for copying channel selections between providers.
/// </summary>
public sealed class CopyChannelsRequest
{
    /// <summary>
    /// Gets or sets the source provider ID to copy selections from.
    /// </summary>
    public string SourceProviderId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the target provider ID to copy selections to.
    /// </summary>
    public string TargetProviderId { get; set; } = string.Empty;
}

/// <summary>
/// Response model for channel copy operation.
/// </summary>
[SuppressMessage("Design", "CA1002:Do not expose generic lists", Justification = "DTO requires list")]
[SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "DTO requires setter")]
public sealed class CopyChannelsResponse
{
    /// <summary>
    /// Gets or sets a value indicating whether the operation was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the message describing the result.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Gets or sets the number of channels matched.
    /// </summary>
    public int MatchedCount { get; set; }

    /// <summary>
    /// Gets or sets the number of source channels that were selected.
    /// </summary>
    public int SourceSelectedCount { get; set; }

    /// <summary>
    /// Gets or sets the number of channels that could not be matched.
    /// </summary>
    public int UnmatchedCount { get; set; }

    /// <summary>
    /// Gets or sets the matched channels with their details.
    /// </summary>
    public List<MatchedChannelInfo> MatchedChannels { get; set; } = new();

    /// <summary>
    /// Gets or sets the unmatched source channel names (for debugging).
    /// </summary>
    public List<string> UnmatchedChannels { get; set; } = new();
}

/// <summary>
/// Information about a matched channel.
/// </summary>
public sealed class MatchedChannelInfo
{
    /// <summary>
    /// Gets or sets the source channel name.
    /// </summary>
    public string SourceName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the target channel name.
    /// </summary>
    public string TargetName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the target stream ID.
    /// </summary>
    public int TargetStreamId { get; set; }

    /// <summary>
    /// Gets or sets the target category ID.
    /// </summary>
    public int TargetCategoryId { get; set; }

    /// <summary>
    /// Gets or sets the normalized name used for matching.
    /// </summary>
    public string NormalizedName { get; set; } = string.Empty;
}
