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

namespace Jellyfin.Xtream.Client.Models;

/// <summary>
/// Represents user information from the Xtream API.
/// </summary>
public class UserInfo
{
    /// <summary>
    /// Gets or sets the username.
    /// </summary>
    [JsonProperty("username")]
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the password.
    /// </summary>
    [JsonProperty("password")]
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the authentication status.
    /// </summary>
    [JsonProperty("auth")]
    public int Auth { get; set; }

    /// <summary>
    /// Gets or sets the account status.
    /// </summary>
    [JsonProperty("status")]
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the expiration date.
    /// </summary>
    [JsonProperty("exp_date")]
    [JsonConverter(typeof(FlexibleDateTimeConverter))]
    public DateTime? ExpDate { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this is a trial account.
    /// </summary>
    [JsonProperty("is_trial")]
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool IsTrial { get; set; }

    /// <summary>
    /// Gets or sets the number of active connections.
    /// </summary>
    [JsonProperty("active_cons")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int ActiveCons { get; set; }

    /// <summary>
    /// Gets or sets the account creation date.
    /// </summary>
    [JsonProperty("created_at")]
    [JsonConverter(typeof(FlexibleDateTimeConverter))]
    public DateTime? CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of connections allowed.
    /// </summary>
    [JsonProperty("max_connections")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int MaxConnections { get; set; }

    /// <summary>
    /// Gets or sets the allowed output formats.
    /// </summary>
    [JsonProperty("allowed_output_formats")]
    [SuppressMessage(
        "Usage",
        "CA2227:Collection properties should be read only",
        Justification = "Required for JSON deserialization"
    )]
    public ICollection<string> AllowedOutputFormats { get; set; } = new List<string>();
}
