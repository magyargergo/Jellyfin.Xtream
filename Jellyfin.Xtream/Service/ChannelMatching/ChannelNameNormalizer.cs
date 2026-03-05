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
using Jellyfin.Xtream.Service.ChannelMatching.Rules;

namespace Jellyfin.Xtream.Service.ChannelMatching;

/// <summary>
/// Normalizes channel names using a configurable pipeline of transformation rules.
/// Used for channel matching between providers. Produces uppercase output for similarity comparison.
/// Thread-safe and optimized for repeated use with pre-compiled patterns.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ChannelNameNormalizer"/> class.
/// </remarks>
/// <param name="rules">The ordered list of normalization rules to apply.</param>
public sealed class ChannelNameNormalizer(IReadOnlyList<INormalizationRule> rules) : IChannelNameNormalizer
{
    private readonly IReadOnlyList<INormalizationRule> _rules = rules;

    /// <summary>
    /// Gets the default normalizer instance with standard rules for IPTV channel matching.
    /// </summary>
    public static ChannelNameNormalizer Default { get; } = CreateDefault();

    /// <inheritdoc />
    public string Normalize(string channelName)
    {
        if (string.IsNullOrWhiteSpace(channelName))
        {
            return string.Empty;
        }

        var result = channelName;
        foreach (var rule in _rules)
        {
            result = rule.Apply(result);
        }

        return result;
    }

    /// <summary>
    /// Creates the default normalizer with standard IPTV channel name rules.
    /// Rules are applied in order:
    /// 1. Strip country/region prefixes (e.g., "PL:", "|UK|", "[DE]")
    /// 2. Strip quality indicators (e.g., "HD", "4K", "1080p")
    /// 3. Strip streaming suffixes (e.g., "Live", "Stream", "Backup")
    /// 4. Strip country name suffixes (e.g., "Poland", "UK")
    /// 5. Strip additional noise (e.g., "NEW", "VIP", "[text]", "(text)", "#123")
    /// 6. Normalize special characters (separators -> spaces, diacritics -> base chars)
    /// 7. Remove remaining diacritics via Unicode normalization
    /// 8. Collapse whitespace and trim
    /// 9. Normalize common word variations (e.g., "Sports" -> "Sport")
    /// 10. Remove all non-alphanumeric characters
    /// 11. Convert to uppercase for case-insensitive comparison.
    /// </summary>
    private static ChannelNameNormalizer CreateDefault()
    {
        var rules = new INormalizationRule[]
        {
            // 1. Strip country/region prefixes (e.g., "PL:", "|UK|", "[DE]")
            new RegexReplacementRule(NormalizationPatterns.CountryPrefixPattern()),
            // 2. Strip quality indicators (e.g., "HD", "4K", "1080p")
            new RegexReplacementRule(NormalizationPatterns.QualityIndicatorPattern()),
            // 3. Strip streaming suffixes (e.g., "Live", "Stream", "Backup")
            new RegexReplacementRule(NormalizationPatterns.StreamingSuffixPattern()),
            // 4. Strip country name suffixes (e.g., "Poland", "UK")
            new RegexReplacementRule(NormalizationPatterns.CountrySuffixPattern()),
            // 5. Strip additional noise (e.g., "NEW", "VIP", "[text]", "(text)", "#123")
            new RegexReplacementRule(NormalizationPatterns.AdditionalNoisePattern()),
            // 6. Normalize special characters (separators -> spaces, diacritics -> base chars)
            SpecialCharacterRule.Instance,
            // 7. Remove remaining diacritics via Unicode normalization
            DiacriticsRule.Instance,
            // 8. Collapse whitespace and trim
            WhitespaceRule.Instance,
            // 9. Normalize common word variations (e.g., "Sports" -> "Sport")
            WordNormalizationRule.Instance,
            // 10. Remove all non-alphanumeric characters
            new RegexReplacementRule(NormalizationPatterns.NonAlphanumericPattern()),
            // 11. Convert to uppercase for case-insensitive comparison
            UppercaseRule.Instance,
        };

        return new ChannelNameNormalizer(rules);
    }
}
