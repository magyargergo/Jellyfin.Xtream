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
/// Represents VOD information from the Xtream API.
/// </summary>
public class VodInfo
{
    /// <summary>
    /// Gets or sets the movie image URL.
    /// </summary>
    [JsonProperty("movie_image")]
    public string? MovieImage { get; set; }

    /// <summary>
    /// Gets or sets the genre.
    /// </summary>
    [JsonProperty("genre")]
    public string? Genre { get; set; }

    /// <summary>
    /// Gets or sets the plot summary.
    /// </summary>
    [JsonProperty("plot")]
    public string? Plot { get; set; }

    /// <summary>
    /// Gets or sets the director name.
    /// </summary>
    [JsonProperty("director")]
    public string? Director { get; set; }

    /// <summary>
    /// Gets or sets the rating.
    /// </summary>
    [JsonProperty("rating")]
    public decimal? Rating { get; set; }

    /// <summary>
    /// Gets or sets the release date.
    /// </summary>
    [JsonProperty("releasedate")]
    public DateTime? ReleaseDate { get; set; }

    /// <summary>
    /// Gets or sets the duration in seconds.
    /// </summary>
    [JsonProperty("duration_secs")]
    public int? DurationSecs { get; set; }

    /// <summary>
    /// Gets or sets the TMDB identifier.
    /// </summary>
    [JsonProperty("tmdb_id")]
    public int? TmdbId { get; set; }

    /// <summary>
    /// Gets or sets the bitrate.
    /// </summary>
    [JsonProperty("bitrate")]
    public int Bitrate { get; set; }

    /// <summary>
    /// Gets or sets the video information.
    /// </summary>
    [JsonProperty("video")]
    [JsonConverter(typeof(OnlyObjectConverter<VideoInfo>))]
    public VideoInfo? Video { get; set; }

    /// <summary>
    /// Gets or sets the audio information.
    /// </summary>
    [JsonProperty("audio")]
    [JsonConverter(typeof(OnlyObjectConverter<AudioInfo>))]
    public AudioInfo? Audio { get; set; }
}
