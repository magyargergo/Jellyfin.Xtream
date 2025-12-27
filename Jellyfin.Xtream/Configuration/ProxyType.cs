// Copyright (C) 2025  Gergo Magyar
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

namespace Jellyfin.Xtream.Configuration;

/// <summary>
/// Defines the supported proxy protocol types.
/// </summary>
public enum ProxyType
{
    /// <summary>
    /// HTTP/HTTPS proxy (default).
    /// Uses standard HTTP CONNECT method for tunneling.
    /// </summary>
    Http = 0,

    /// <summary>
    /// SOCKS4 proxy protocol.
    /// Supports TCP connections only, no authentication or UDP.
    /// </summary>
    Socks4 = 1,

    /// <summary>
    /// SOCKS4a proxy protocol.
    /// Extension of SOCKS4 that supports hostname resolution by the proxy server.
    /// </summary>
    Socks4a = 2,

    /// <summary>
    /// SOCKS5 proxy protocol.
    /// Supports TCP/UDP, authentication, and IPv6.
    /// Most feature-rich and widely supported SOCKS version.
    /// </summary>
    Socks5 = 3,
}
