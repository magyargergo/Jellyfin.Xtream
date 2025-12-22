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

namespace Jellyfin.Xtream.Service.ChannelMatching;

/// <summary>
/// Normalizes channel names for comparison and matching purposes.
/// </summary>
public interface IChannelNameNormalizer
{
    /// <summary>
    /// Normalizes a channel name by removing noise (prefixes, suffixes, quality indicators)
    /// to produce a canonical form suitable for matching.
    /// </summary>
    /// <param name="channelName">The raw channel name.</param>
    /// <returns>A normalized, uppercase string with only alphanumeric characters.</returns>
    string Normalize(string channelName);
}
