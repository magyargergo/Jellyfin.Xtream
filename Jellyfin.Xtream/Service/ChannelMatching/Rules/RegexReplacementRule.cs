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

using System.Text.RegularExpressions;

namespace Jellyfin.Xtream.Service.ChannelMatching.Rules;

/// <summary>
/// A normalization rule that applies a compiled regex replacement.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="RegexReplacementRule"/> class.
/// </remarks>
/// <param name="pattern">The compiled regex pattern.</param>
/// <param name="replacement">The replacement string.</param>
public sealed class RegexReplacementRule(Regex pattern, string replacement = "") : INormalizationRule
{
    private readonly Regex _pattern = pattern;
    private readonly string _replacement = replacement;

    /// <inheritdoc />
    public string Apply(string input)
    {
        try
        {
            return _pattern.Replace(input, _replacement);
        }
        catch (RegexMatchTimeoutException)
        {
            // On timeout, return input unchanged rather than crashing.
            // This can happen on first invocation when DFA compilation occurs,
            // especially with complex patterns containing Unicode alternations.
            return input;
        }
    }
}
