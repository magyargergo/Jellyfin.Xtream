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

using Newtonsoft.Json;

namespace Jellyfin.Xtream.Client.Models;

/// <summary>
/// Represents VOD stream information from the Xtream API.
/// </summary>
public class VodStreamInfo
{
    /// <summary>
    /// Gets or sets the VOD info.
    /// </summary>
    [JsonProperty("info")]
    [JsonConverter(typeof(OnlyObjectConverter<VodInfo>))]
    public VodInfo? Info { get; set; }

    /// <summary>
    /// Gets or sets the movie data.
    /// </summary>
    [JsonProperty("movie_data")]
    [JsonConverter(typeof(OnlyObjectConverter<StreamInfo>))]
    public StreamInfo? MovieData { get; set; }
}
