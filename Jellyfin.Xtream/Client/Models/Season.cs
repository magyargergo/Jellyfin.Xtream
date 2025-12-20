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
using Newtonsoft.Json;

namespace Jellyfin.Xtream.Client.Models;

/// <summary>
/// Represents a TV series season from the Xtream API.
/// </summary>
public class Season
{
    /// <summary>
    /// Gets or sets the air date of the season.
    /// </summary>
    [JsonProperty("air_date")]
    public DateTime AirDate { get; set; }

    /// <summary>
    /// Gets or sets the number of episodes in the season.
    /// </summary>
    [JsonProperty("episode_count")]
    public int EpisodeCount { get; set; }

    /// <summary>
    /// Gets or sets the season identifier.
    /// </summary>
    [JsonProperty("id")]
    public int SeasonId { get; set; }

    /// <summary>
    /// Gets or sets the season name.
    /// </summary>
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the season overview.
    /// </summary>
    [JsonProperty("overview")]
    public string Overview { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the season number.
    /// </summary>
    [JsonProperty("season_number")]
    public int Cast { get; set; }

    /// <summary>
    /// Gets or sets the cover image URL.
    /// </summary>
    [JsonProperty("cover")]
    public string Cover { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the large cover image URL.
    /// </summary>
    [JsonProperty("cover_big")]
    public string CoverBig { get; set; } = string.Empty;
}
