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
using System.Globalization;
using Newtonsoft.Json;

namespace Jellyfin.Xtream.Client.Models;

/// <summary>
/// EPG (Electronic Program Guide) information for a channel.
/// Handles multiple Xtream provider response formats.
/// Based on Xtream Codes PHP API standard.
/// </summary>
/// <remarks>
/// Xtream API quirks handled:
/// - id/epg_id come as strings but represent integers
/// - title/description are base64 encoded
/// - timestamps come as Unix epoch OR date strings
/// - booleans come as 0/1 integers or "true"/"false" strings.
/// </remarks>
public class EpgInfo
{
    /// <summary>
    /// Gets or sets the EPG entry ID (comes as string in JSON, parsed to int).
    /// </summary>
    [JsonConverter(typeof(FlexibleIntConverter))]
    [JsonProperty("id")]
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the EPG ID (comes as string in JSON, parsed to int).
    /// </summary>
    [JsonConverter(typeof(FlexibleIntConverter))]
    [JsonProperty("epg_id")]
    public int EpgId { get; set; }

    /// <summary>
    /// Gets or sets the program title (base64 encoded in Xtream API).
    /// </summary>
    [JsonConverter(typeof(Base64Converter))]
    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the language code.
    /// </summary>
    [JsonProperty("lang")]
    public string Language { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the start timestamp as Unix epoch (preferred source).
    /// </summary>
    [JsonConverter(typeof(FlexibleDateTimeConverter))]
    [JsonProperty("start_timestamp")]
    public DateTime? StartTimestamp { get; set; }

    /// <summary>
    /// Gets or sets the stop timestamp as Unix epoch (preferred source).
    /// </summary>
    [JsonConverter(typeof(FlexibleDateTimeConverter))]
    [JsonProperty("stop_timestamp")]
    public DateTime? StopTimestamp { get; set; }

    /// <summary>
    /// Gets or sets the start time as a string (format: "Y-m-d H:i:s").
    /// </summary>
    [JsonProperty("start")]
    public string? StartString { get; set; }

    /// <summary>
    /// Gets or sets the end time as a string (format: "Y-m-d H:i:s").
    /// </summary>
    [JsonProperty("end")]
    public string? EndString { get; set; }

    /// <summary>
    /// Gets or sets the stop time as a string (alternative to EndString).
    /// </summary>
    [JsonProperty("stop")]
    public string? StopString { get; set; }

    /// <summary>
    /// Gets or sets the program description (base64 encoded in Xtream API).
    /// </summary>
    [JsonConverter(typeof(Base64Converter))]
    [JsonProperty("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the channel ID.
    /// </summary>
    [JsonProperty("channel_id")]
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this program is currently playing (1 for first item, 0 for others).
    /// </summary>
    [JsonConverter(typeof(FlexibleBoolConverter))]
    [JsonProperty("now_playing")]
    public bool NowPlaying { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this program has catchup/archive available.
    /// </summary>
    [JsonConverter(typeof(FlexibleBoolConverter))]
    [JsonProperty("has_archive")]
    public bool HasArchive { get; set; }

    /// <summary>
    /// Gets the effective start time, handling multiple provider formats.
    /// </summary>
    [JsonIgnore]
    public DateTime Start
    {
        get
        {
            return StartTimestamp > DateTime.MinValue ? StartTimestamp.Value
                : !string.IsNullOrEmpty(StartString)
                && DateTime.TryParse(StartString, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                : DateTime.MinValue;
        }
    }

    /// <summary>
    /// Gets the start time in local time (for catchup/archive functionality).
    /// </summary>
    [JsonIgnore]
    public DateTime StartLocalTime => Start.ToLocalTime();

    /// <summary>
    /// Gets the effective end time, handling multiple provider formats.
    /// </summary>
    [JsonIgnore]
    public DateTime End
    {
        get
        {
            if (StopTimestamp > DateTime.MinValue)
            {
                return StopTimestamp.Value;
            }

            var endStr = EndString ?? StopString;
            return
                !string.IsNullOrEmpty(endStr) && DateTime.TryParse(endStr, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : DateTime.MinValue;
        }
    }

    /// <summary>
    /// Gets or sets the thumbnail image URL for the currently playing program.
    /// This is populated locally and not part of the Xtream API response.
    /// </summary>
    [JsonIgnore]
    public string? ThumbImageUrl { get; set; }
}
