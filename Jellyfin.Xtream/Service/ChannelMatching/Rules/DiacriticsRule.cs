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

using System.Globalization;
using System.Text;

namespace Jellyfin.Xtream.Service.ChannelMatching.Rules;

/// <summary>
/// A normalization rule that removes diacritics (accents) from a string using Unicode normalization.
/// Handles characters not covered by <see cref="SpecialCharacterRule"/>.
/// </summary>
public sealed class DiacriticsRule : INormalizationRule
{
    /// <summary>
    /// Gets the singleton instance.
    /// </summary>
    public static DiacriticsRule Instance { get; } = new();

    private DiacriticsRule() { }

    /// <inheritdoc />
    public string Apply(string input)
    {
        var normalized = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);

        foreach (var c in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
