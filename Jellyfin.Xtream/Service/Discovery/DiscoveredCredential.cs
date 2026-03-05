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

namespace Jellyfin.Xtream.Service.Discovery;

/// <summary>
/// Represents a discovered Xtream provider credential.
/// </summary>
public sealed class DiscoveredCredential
{
    /// <summary>
    /// Gets or sets the server hostname or IP address.
    /// </summary>
    public string Server { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the server port.
    /// </summary>
    public int Port { get; set; } = 8080;

    /// <summary>
    /// Gets or sets the username.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the password.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Gets the base URL constructed from server and port.
    /// </summary>
    public string BaseUrl => $"http://{Server}:{Port}";

    /// <summary>
    /// Gets a unique key for deduplication.
    /// </summary>
    public string UniqueKey => $"{Server}:{Port}:{Username}";

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is DiscoveredCredential other
            && Server == other.Server
            && Port == other.Port
            && Username == other.Username
            && Password == other.Password;
    }

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Server, Port, Username, Password);
}
