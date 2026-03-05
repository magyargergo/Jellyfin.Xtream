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
/// Represents video stream information from the Xtream API.
/// </summary>
public class VideoInfo
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
    /// Gets or sets the video width in pixels.
    /// </summary>
    [JsonProperty("width")]
    public int Width { get; set; }

    /// <summary>
    /// Gets or sets the video height in pixels.
    /// </summary>
    [JsonProperty("height")]
    public int Height { get; set; }

    /// <summary>
    /// Gets or sets the display aspect ratio.
    /// </summary>
    [JsonProperty("display_aspect_ratio")]
    public string AspectRatio { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the pixel format.
    /// </summary>
    [JsonProperty("pix_fmt")]
    public string PixelFormat { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the codec level.
    /// </summary>
    [JsonProperty("level")]
    public int Level { get; set; }

    /// <summary>
    /// Gets or sets the color range.
    /// </summary>
    [JsonProperty("color_range")]
    public string ColorRange { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the color space.
    /// </summary>
    [JsonProperty("color_space")]
    public string ColorSpace { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the color transfer characteristics.
    /// </summary>
    [JsonProperty("color_transfer")]
    public string ColorTransfer { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the color primaries.
    /// </summary>
    [JsonProperty("color_primaries")]
    public string ColorPrimaries { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the stream is AVC.
    /// </summary>
    [JsonProperty("is_avc")]
    public bool IsAVC { get; set; }

    /// <summary>
    /// Gets or sets the bits per raw sample.
    /// </summary>
    [JsonProperty("bits_per_raw_sample")]
    public int BitsPerRawSample { get; set; }
}
