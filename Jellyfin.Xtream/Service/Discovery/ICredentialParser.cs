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

using System.Collections.Generic;

namespace Jellyfin.Xtream.Service.Discovery;

/// <summary>
/// Interface for parsing credentials from text or HTML content.
/// </summary>
public interface ICredentialParser
{
    /// <summary>
    /// Parses credentials from raw text content.
    /// </summary>
    /// <param name="content">The text content to parse.</param>
    /// <returns>List of parsed credentials.</returns>
    IReadOnlyList<DiscoveredCredential> ParseText(string content);

    /// <summary>
    /// Parses credentials from HTML content.
    /// </summary>
    /// <param name="html">The HTML content to parse.</param>
    /// <returns>List of parsed credentials.</returns>
    IReadOnlyList<DiscoveredCredential> ParseHtml(string html);
}
