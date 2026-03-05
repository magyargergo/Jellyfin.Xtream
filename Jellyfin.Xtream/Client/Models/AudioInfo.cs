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
/// Represents audio stream information from the Xtream API.
/// </summary>
public class AudioInfo
{
    /// <summary>
    /// Gets or sets the stream index.
    /// </summary>
    [JsonProperty("index")]
    public int Index { get; set; }

    /// <summary>
    /// Gets or sets the codec name.
    /// </summary>
    [JsonProperty("codec_name")]
    public string CodecName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the codec profile.
    /// </summary>
    [JsonProperty("profile")]
    public string Profile { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the sample format.
    /// </summary>
    [JsonProperty("sample_fmt")]
    public string SampleFormat { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the sample rate in Hz.
    /// </summary>
    [JsonProperty("sample_rate")]
    public int SampleRate { get; set; }

    /// <summary>
    /// Gets or sets the number of audio channels.
    /// </summary>
    [JsonProperty("channels")]
    public int Channels { get; set; }

    /// <summary>
    /// Gets or sets the channel layout.
    /// </summary>
    [JsonProperty("channel_layout")]
    public string ChannelLayout { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the bitrate in bits per second.
    /// </summary>
    [JsonProperty("bit_rate")]
    public int Bitrate { get; set; }
}
