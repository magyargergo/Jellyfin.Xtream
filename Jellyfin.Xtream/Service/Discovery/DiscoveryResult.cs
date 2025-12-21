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
using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Xtream.Service.Discovery;

/// <summary>
/// Represents the result of a credential discovery operation.
/// </summary>
[SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "DTO requires setter")]
[SuppressMessage("Design", "CA1002:Do not expose generic lists", Justification = "DTO requires setter")]
public sealed class DiscoveryResult
{
    /// <summary>
    /// Gets or sets a value indicating whether the discovery was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the discovered credentials.
    /// </summary>
    public List<DiscoveredCredential> Credentials { get; set; } = [];

    /// <summary>
    /// Gets or sets the number of pages successfully processed.
    /// </summary>
    public int PagesProcessed { get; set; }

    /// <summary>
    /// Gets or sets the number of pages that failed.
    /// </summary>
    public int PagesFailed { get; set; }

    /// <summary>
    /// Gets or sets any error message.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Gets or sets the source URL that was processed.
    /// </summary>
    public string? SourceUrl { get; set; }
}
