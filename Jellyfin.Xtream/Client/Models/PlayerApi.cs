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
/// Represents the player API response from the Xtream API.
/// </summary>
public class PlayerApi
{
    /// <summary>
    /// Gets or sets the user information.
    /// </summary>
    [JsonProperty("user_info")]
    public UserInfo UserInfo { get; set; } = new UserInfo();

    /// <summary>
    /// Gets or sets the server information.
    /// </summary>
    [JsonProperty("server_info")]
    public ServerInfo ServerInfo { get; set; } = new ServerInfo();
}
