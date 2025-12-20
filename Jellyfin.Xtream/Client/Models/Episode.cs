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
/// Represents a TV series episode from the Xtream API.
/// </summary>
public class Episode
{
    /// <summary>
    /// Gets or sets the episode identifier.
    /// </summary>
    [JsonProperty("id")]
    public int EpisodeId { get; set; }

    /// <summary>
    /// Gets or sets the episode number.
    /// </summary>
    [JsonProperty("episode_num")]
    public int EpisodeNum { get; set; }

    /// <summary>
    /// Gets or sets the episode title.
    /// </summary>
    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the container extension.
    /// </summary>
    [JsonProperty("container_extension")]
    public string ContainerExtension { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the episode information.
    /// </summary>
    [JsonConverter(typeof(OnlyObjectConverter<EpisodeInfo>))]
    [JsonProperty("info")]
    public EpisodeInfo? Info { get; set; } = new EpisodeInfo();

    /// <summary>
    /// Gets or sets the custom SID.
    /// </summary>
    [JsonProperty("custom_sid")]
    public string CustomSid { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the timestamp when the episode was added.
    /// </summary>
    [JsonProperty("added")]
    public long Added { get; set; }

    /// <summary>
    /// Gets or sets the season number.
    /// </summary>
    [JsonProperty("season")]
    public int Season { get; set; }

    /// <summary>
    /// Gets or sets the direct source URL.
    /// </summary>
    [JsonProperty("direct_source")]
    public string DirectSource { get; set; } = string.Empty;
}
