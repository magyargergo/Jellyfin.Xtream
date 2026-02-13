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
using Jellyfin.Xtream.Api.Models;
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Configuration;

namespace Jellyfin.Xtream.Api;

/// <summary>
/// Shared helper methods used across Xtream API controllers.
/// </summary>
internal static class XtreamControllerHelpers
{
    /// <summary>
    /// Get a provider by ID, or the first enabled provider if no ID is specified.
    /// </summary>
    /// <param name="providerId">Optional provider ID.</param>
    /// <returns>The provider, or null if not found.</returns>
    internal static XtreamProvider? GetProvider(string? providerId)
    {
        var config = Plugin.Instance.Configuration;
        return string.IsNullOrEmpty(providerId)
            ? config.GetEnabledProviders().FirstOrDefault()
            : config.GetProvider(providerId);
    }

    /// <summary>
    /// Create a standardized error response.
    /// </summary>
    /// <param name="errorCode">The machine-readable error code.</param>
    /// <param name="message">The human-readable message.</param>
    /// <param name="suggestedAction">Optional suggested action.</param>
    /// <param name="context">Optional additional context.</param>
    /// <returns>A new <see cref="ErrorResponse"/> instance.</returns>
    internal static ErrorResponse CreateError(
        string errorCode,
        string message,
        string? suggestedAction = null,
        Dictionary<string, string>? context = null
    )
    {
        return new ErrorResponse
        {
            ErrorCode = errorCode,
            Message = message,
            SuggestedAction = suggestedAction,
            Context = context,
        };
    }

    /// <summary>
    /// Publish a configuration changed event on the plugin event bus.
    /// </summary>
    /// <param name="section">The configuration section that changed.</param>
    internal static void PublishConfigChanged(string section)
    {
        Service.Events.PluginEventBus.Instance.Publish(
            "config.changed",
            data: new Dictionary<string, object>(StringComparer.Ordinal) { ["section"] = section }
        );
    }

    /// <summary>
    /// Map a <see cref="Category"/> to a <see cref="CategoryResponse"/>.
    /// </summary>
    /// <param name="category">The category to map.</param>
    /// <returns>A new <see cref="CategoryResponse"/>.</returns>
    internal static CategoryResponse CreateCategoryResponse(Category category) =>
        new() { Id = category.CategoryId, Name = category.CategoryName };

    /// <summary>
    /// Map a <see cref="StreamInfo"/> to an <see cref="ItemResponse"/>.
    /// </summary>
    /// <param name="stream">The stream to map.</param>
    /// <returns>A new <see cref="ItemResponse"/>.</returns>
    internal static ItemResponse CreateItemResponse(StreamInfo stream)
    {
        return new ItemResponse
        {
            Id = stream.StreamId,
            Name = stream.Name,
            HasCatchup = stream.TvArchive,
            CatchupDuration = stream.TvArchiveDuration,
        };
    }

    /// <summary>
    /// Map a <see cref="Series"/> to an <see cref="ItemResponse"/>.
    /// </summary>
    /// <param name="series">The series to map.</param>
    /// <returns>A new <see cref="ItemResponse"/>.</returns>
    internal static ItemResponse CreateItemResponse(Series series)
    {
        return new ItemResponse
        {
            Id = series.SeriesId,
            Name = series.Name,
            HasCatchup = false,
            CatchupDuration = 0,
        };
    }

    /// <summary>
    /// Map a <see cref="StreamInfo"/> to a <see cref="ChannelResponse"/>.
    /// </summary>
    /// <param name="stream">The stream to map.</param>
    /// <returns>A new <see cref="ChannelResponse"/>.</returns>
    internal static ChannelResponse CreateChannelResponse(StreamInfo stream)
    {
        return new ChannelResponse
        {
            Id = stream.StreamId,
            LogoUrl = stream.StreamIcon,
            Name = stream.Name,
            Number = stream.Num,
        };
    }
}
