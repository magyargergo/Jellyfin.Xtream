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
/// A single transformation rule applied during channel name normalization.
/// </summary>
public interface INormalizationRule
{
    /// <summary>
    /// Applies this rule to transform the input string.
    /// </summary>
    /// <param name="input">The current state of the channel name.</param>
    /// <returns>The transformed string.</returns>
    string Apply(string input);
}
