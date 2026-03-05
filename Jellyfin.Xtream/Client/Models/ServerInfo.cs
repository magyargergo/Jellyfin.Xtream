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
/// Represents server information from the Xtream API.
/// </summary>
public class ServerInfo
{
    /// <summary>
    /// Gets or sets the server URL.
    /// </summary>
    [JsonProperty("url")]
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the server port.
    /// </summary>
    [JsonProperty("port")]
    public string Port { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the HTTPS port.
    /// </summary>
    [JsonProperty("https_port")]
    public string HttpsPort { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the server protocol.
    /// </summary>
    [JsonProperty("server_protocol")]
    public string ServerProtocol { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the RTMP port.
    /// </summary>
    [JsonProperty("rtmp_port")]
    public string RtmpPort { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the server timezone.
    /// </summary>
    [JsonProperty("timezone")]
    public string Timezone { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the current timestamp on the server.
    /// </summary>
    [JsonProperty("timestamp_now")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int TimestampNow { get; set; }

    /// <summary>
    /// Gets or sets the current time on the server as a string.
    /// </summary>
    [JsonProperty("time_now")]
    public string TimeNow { get; set; } = string.Empty;
}
