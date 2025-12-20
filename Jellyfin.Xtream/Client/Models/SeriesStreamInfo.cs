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
using Newtonsoft.Json;

namespace Jellyfin.Xtream.Client.Models;

/// <summary>
/// Represents series stream information from the Xtream API.
/// </summary>
public class SeriesStreamInfo
{
    /// <summary>
    /// Gets or sets the collection of seasons.
    /// </summary>
    [JsonProperty("seasons")]
    [SuppressMessage(
        "Usage",
        "CA2227:Collection properties should be read only",
        Justification = "Required for JSON deserialization"
    )]
    public ICollection<Season> Seasons { get; set; } = new List<Season>();

    /// <summary>
    /// Gets or sets the series information.
    /// </summary>
    [JsonProperty("info")]
    public SeriesInfo Info { get; set; } = new SeriesInfo();

    /// <summary>
    /// Gets or sets the episodes grouped by season number.
    /// </summary>
    [JsonProperty("episodes")]
    [SuppressMessage(
        "Usage",
        "CA2227:Collection properties should be read only",
        Justification = "Required for JSON deserialization"
    )]
    public Dictionary<int, ICollection<Episode>> Episodes { get; set; } = new Dictionary<int, ICollection<Episode>>();
}
