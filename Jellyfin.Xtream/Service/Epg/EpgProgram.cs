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

namespace Jellyfin.Xtream.Service.Epg;

/// <summary>
/// Unified EPG program model used across all providers.
/// Acts as a Data Transfer Object (DTO) between providers and consumers.
/// </summary>
public sealed class EpgProgram
{
    /// <summary>
    /// Gets the unique program identifier.
    /// </summary>
    public int Id { get; init; }

    /// <summary>
    /// Gets the program title.
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Gets the program description/overview.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets the start time (UTC).
    /// </summary>
    public DateTime StartUtc { get; init; }

    /// <summary>
    /// Gets the end time (UTC).
    /// </summary>
    public DateTime EndUtc { get; init; }

    /// <summary>
    /// Gets the thumbnail/icon URL.
    /// </summary>
    public string? ImageUrl { get; init; }

    /// <summary>
    /// Gets the program categories/genres.
    /// </summary>
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether this program has valid time data.
    /// </summary>
    public bool HasValidTimes => StartUtc > DateTime.MinValue && EndUtc > DateTime.MinValue;
}
