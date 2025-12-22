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

namespace Jellyfin.Xtream.Service.ChannelMatching.Rules;

/// <summary>
/// A normalization rule that converts the input to lowercase using invariant culture.
/// </summary>
public sealed class LowercaseRule : INormalizationRule
{
    /// <summary>
    /// Gets the singleton instance.
    /// </summary>
    public static LowercaseRule Instance { get; } = new();

    private LowercaseRule() { }

    /// <inheritdoc />
    public string Apply(string input) => input.ToLowerInvariant();
}
