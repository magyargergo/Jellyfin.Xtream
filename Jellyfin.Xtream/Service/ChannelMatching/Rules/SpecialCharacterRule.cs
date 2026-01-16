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

using System.Text;

namespace Jellyfin.Xtream.Service.ChannelMatching.Rules;

/// <summary>
/// A normalization rule that handles special characters commonly found in channel names.
/// Replaces separators with spaces, handles ampersand, preserves plus signs, and normalizes diacritics.
/// </summary>
public sealed class SpecialCharacterRule : INormalizationRule
{
    /// <summary>
    /// Gets the singleton instance.
    /// </summary>
    public static SpecialCharacterRule Instance { get; } = new();

    private SpecialCharacterRule() { }

    /// <inheritdoc />
    public string Apply(string input)
    {
        var sb = new StringBuilder(input.Length);

        foreach (var c in input)
        {
            switch (c)
            {
                // Common separators -> space
                case '_':
                case '-':
                case '.':
                case '/':
                case '\\':
                case '|':
                    _ = sb.Append(' ');
                    break;

                // Keep alphanumeric and spaces
                case >= 'A'
                and <= 'Z':
                case >= 'a' and <= 'z':
                case >= '0' and <= '9':
                case ' ':
                    _ = sb.Append(c);
                    break;

                // Preserve plus sign (Canal+, Disney+, etc.)
                case '+':
                    _ = sb.Append('+');
                    break;

                // Ampersand -> "and"
                case '&':
                    _ = sb.Append(" and ");
                    break;

                // Diacritics - lowercase and uppercase variants
                case 'ą':
                case 'Ą':
                case 'à':
                case 'À':
                case 'á':
                case 'Á':
                case 'â':
                case 'Â':
                case 'ã':
                case 'Ã':
                case 'ä':
                case 'Ä':
                case 'å':
                case 'Å':
                case 'æ':
                case 'Æ':
                    _ = sb.Append('a');
                    break;

                case 'ć':
                case 'Ć':
                case 'ç':
                case 'Ç':
                case 'č':
                case 'Č':
                    _ = sb.Append('c');
                    break;

                case 'ę':
                case 'Ę':
                case 'è':
                case 'È':
                case 'é':
                case 'É':
                case 'ê':
                case 'Ê':
                case 'ë':
                case 'Ë':
                    _ = sb.Append('e');
                    break;

                case 'ì':
                case 'Ì':
                case 'í':
                case 'Í':
                case 'î':
                case 'Î':
                case 'ï':
                case 'Ï':
                    _ = sb.Append('i');
                    break;

                case 'ł':
                case 'Ł':
                    _ = sb.Append('l');
                    break;

                case 'ń':
                case 'Ń':
                case 'ñ':
                case 'Ñ':
                case 'ň':
                case 'Ň':
                    _ = sb.Append('n');
                    break;

                case 'ó':
                case 'Ó':
                case 'ò':
                case 'Ò':
                case 'ô':
                case 'Ô':
                case 'õ':
                case 'Õ':
                case 'ö':
                case 'Ö':
                case 'ø':
                case 'Ø':
                    _ = sb.Append('o');
                    break;

                case 'ś':
                case 'Ś':
                case 'š':
                case 'Š':
                case 'ş':
                case 'Ş':
                    _ = sb.Append('s');
                    break;

                case 'ù':
                case 'Ù':
                case 'ú':
                case 'Ú':
                case 'û':
                case 'Û':
                case 'ü':
                case 'Ü':
                    _ = sb.Append('u');
                    break;

                case 'ý':
                case 'Ý':
                case 'ÿ':
                case 'Ÿ':
                    _ = sb.Append('y');
                    break;

                case 'ź':
                case 'Ź':
                case 'ż':
                case 'Ż':
                case 'ž':
                case 'Ž':
                    _ = sb.Append('z');
                    break;

                case 'ß':
                    _ = sb.Append("ss");
                    break;

                case 'đ':
                case 'Đ':
                    _ = sb.Append('d');
                    break;

                case 'þ':
                case 'Þ':
                    _ = sb.Append("th");
                    break;

                default:
                    // For any other letter, keep it for RemoveDiacritics to handle
                    if (char.IsLetter(c))
                    {
                        _ = sb.Append(c);
                    }
                    // Skip non-letter, non-handled characters
                    break;
            }
        }

        return sb.ToString();
    }
}
