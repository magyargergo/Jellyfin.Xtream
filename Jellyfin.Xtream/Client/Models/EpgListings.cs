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
using Newtonsoft.Json;

namespace Jellyfin.Xtream.Client.Models;

/// <summary>
/// EPG listings response wrapper.
/// Handles multiple response formats via <see cref="EpgListingsConverter"/>.
/// </summary>
[JsonConverter(typeof(EpgListingsConverter))]
public class EpgListings
{
    /// <summary>
    /// Gets or sets the collection of EPG listings.
    /// </summary>
    [JsonProperty("epg_listings")]
    [SuppressMessage(
        "Usage",
        "CA2227:Collection properties should be read only",
        Justification = "Required for JSON deserialization"
    )]
    public ICollection<EpgInfo> Listings { get; set; } = new List<EpgInfo>();
}
