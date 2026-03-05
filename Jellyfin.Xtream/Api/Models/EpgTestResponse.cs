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

namespace Jellyfin.Xtream.Api.Models;

/// <summary>
/// EPG test response containing programs and provider information.
/// </summary>
public class EpgTestResponse
{
    /// <summary>
    /// Gets or sets the stream/channel ID.
    /// </summary>
    public int StreamId { get; set; }

    /// <summary>
    /// Gets or sets the channel name.
    /// </summary>
    public string ChannelName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the EPG provider that returned the data.
    /// </summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the collection of programs.
    /// </summary>
    public IReadOnlyList<EpgProgramResponse> Programs { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether the request was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets any error message.
    /// </summary>
    public string? ErrorMessage { get; set; }
}
