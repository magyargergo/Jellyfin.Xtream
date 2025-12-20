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

using System.Diagnostics.CodeAnalysis;
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Configuration;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Represents a stream with its associated provider information.
/// </summary>
/// <param name="Provider">The provider this stream belongs to.</param>
/// <param name="Stream">The stream information.</param>
[SuppressMessage(
    "StyleCop.CSharp.MaintainabilityRules",
    "SA1402:File may only contain a single type",
    Justification = "Related record types are intentionally grouped together"
)]
public record ProviderStreamInfo(XtreamProvider Provider, StreamInfo Stream);

/// <summary>
/// Represents a category with its associated provider information.
/// </summary>
/// <param name="Provider">The provider this category belongs to.</param>
/// <param name="Category">The category information.</param>
[SuppressMessage(
    "StyleCop.CSharp.MaintainabilityRules",
    "SA1402:File may only contain a single type",
    Justification = "Related record types are intentionally grouped together"
)]
public record ProviderCategory(XtreamProvider Provider, Category Category);

/// <summary>
/// Represents a series with its associated provider information.
/// </summary>
/// <param name="Provider">The provider this series belongs to.</param>
/// <param name="Series">The series information.</param>
[SuppressMessage(
    "StyleCop.CSharp.MaintainabilityRules",
    "SA1402:File may only contain a single type",
    Justification = "Related record types are intentionally grouped together"
)]
public record ProviderSeries(XtreamProvider Provider, Series Series);

/// <summary>
/// Represents parsed GUID information containing provider and stream IDs.
/// </summary>
/// <param name="Prefix">The ID prefix (e.g., LiveTvPrefix, StreamPrefix).</param>
/// <param name="ProviderId">The provider ID string.</param>
/// <param name="StreamId">The stream ID from the provider.</param>
[SuppressMessage(
    "StyleCop.CSharp.MaintainabilityRules",
    "SA1402:File may only contain a single type",
    Justification = "Related record types are intentionally grouped together"
)]
public record ParsedStreamId(int Prefix, string ProviderId, int StreamId);
