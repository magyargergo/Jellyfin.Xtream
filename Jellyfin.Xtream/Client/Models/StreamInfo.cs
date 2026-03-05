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
/// Represents stream information from the Xtream API.
/// </summary>
public class StreamInfo
{
    /// <summary>
    /// Gets or sets the stream number.
    /// </summary>
    [JsonProperty("num")]
    public int Num { get; set; }

    /// <summary>
    /// Gets or sets the stream name.
    /// </summary>
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the stream type.
    /// </summary>
    [JsonProperty("stream_type")]
    public string StreamType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the stream identifier.
    /// </summary>
    [JsonProperty("stream_id")]
    public int StreamId { get; set; }

    /// <summary>
    /// Gets or sets the stream icon URL.
    /// </summary>
    [JsonProperty("stream_icon")]
    public string StreamIcon { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the EPG channel identifier.
    /// </summary>
    [JsonProperty("epg_channel_id")]
    public string EpgChannelId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the date when the stream was added.
    /// </summary>
    [JsonProperty("added")]
    public string Added { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the category identifier.
    /// </summary>
    [JsonProperty("category_id")]
    public int? CategoryId { get; set; }

    /// <summary>
    /// Gets or sets the container extension.
    /// </summary>
    [JsonProperty("container_extension")]
    public string ContainerExtension { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the custom SID.
    /// </summary>
    [JsonProperty("custom_sid")]
    public string CustomSid { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether TV archive is available.
    /// </summary>
    [JsonProperty("tv_archive")]
    public bool TvArchive { get; set; }

    /// <summary>
    /// Gets or sets the direct source URL.
    /// </summary>
    [JsonProperty("direct_source")]
    public string DirectSource { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the TV archive duration in days.
    /// </summary>
    [JsonProperty("tv_archive_duration")]
    public int TvArchiveDuration { get; set; }
}
