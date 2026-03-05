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
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Jellyfin.Xtream.Client.Models;

/// <summary>
/// Represents series information from the Xtream API.
/// </summary>
public class SeriesInfo
{
    /// <summary>
    /// Gets or sets the series name.
    /// </summary>
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the cover image URL.
    /// </summary>
    [JsonProperty("cover")]
    public string Cover { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the plot summary.
    /// </summary>
    [JsonProperty("plot")]
    public string Plot { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the cast members.
    /// </summary>
    [JsonProperty("cast")]
    public string Cast { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the director.
    /// </summary>
    [JsonProperty("director")]
    public string Director { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the genre.
    /// </summary>
    [JsonProperty("genre")]
    public string Genre { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the last modified date.
    /// </summary>
    [JsonConverter(typeof(UnixDateTimeConverter))]
    [JsonProperty("last_modified")]
    public DateTime LastModified { get; set; }

    /// <summary>
    /// Gets or sets the rating.
    /// </summary>
    [JsonProperty("rating")]
    public decimal Rating { get; set; }

    /// <summary>
    /// Gets or sets the rating on a 5-point scale.
    /// </summary>
    [JsonProperty("rating_5based")]
    public decimal Rating5Based { get; set; }

    /// <summary>
    /// Gets or sets the backdrop image paths.
    /// </summary>
    [JsonConverter(typeof(SingularToListConverter<string>))]
    [JsonProperty("backdrop_path")]
    [SuppressMessage(
        "Usage",
        "CA2227:Collection properties should be read only",
        Justification = "Required for JSON deserialization"
    )]
    public ICollection<string> BackdropPaths { get; set; } = [];

    /// <summary>
    /// Gets or sets the YouTube trailer URL.
    /// </summary>
    [JsonProperty("youtube_trailer")]
    public string YoutubeTrailer { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the episode run time in minutes.
    /// </summary>
    [JsonProperty("episode_run_time")]
    public int EpisodeRunTime { get; set; }

    /// <summary>
    /// Gets or sets the category identifier.
    /// </summary>
    [JsonProperty("category_id")]
    public int CategoryId { get; set; }
}
