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

namespace Jellyfin.Xtream.Client;

/// <summary>
/// A wrapper class for Xtream API client connection information.
/// </summary>
/// <param name="baseUrl">The base url including protocol and port number, without trailing slash.</param>
/// <param name="username">The username for authentication.</param>
/// <param name="password">The password for authentication.</param>
public class ConnectionInfo(string baseUrl, string username, string password)
{
    /// <summary>
    /// Gets or sets the base url including protocol and port number, without trailing slash.
    /// </summary>
    public string BaseUrl { get; set; } = baseUrl;

    /// <summary>
    /// Gets or sets the username for authentication.
    /// </summary>
    public string UserName { get; set; } = username;

    /// <summary>
    /// Gets or sets the password for authentication.
    /// </summary>
    public string Password { get; set; } = password;

    /// <inheritdoc />
    public override string ToString()
    {
        var masked = Password.Length switch
        {
            0 => string.Empty,
            1 => "*",
            2 => $"{Password[0]}*",
            _ => $"{Password[0]}{"".PadRight(Password.Length - 2, '*')}{Password[^1]}",
        };
        return $"{BaseUrl} {UserName}:{masked}";
    }
}
