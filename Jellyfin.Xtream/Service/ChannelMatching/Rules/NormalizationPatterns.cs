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
/// Pre-compiled regex patterns for channel name normalization.
/// Patterns are compiled once and reused for optimal performance.
/// </summary>
public static partial class NormalizationPatterns
{
    /// <summary>
    /// Matches country/region prefixes in various formats.
    /// Examples: "PL:", "PL |", "|PL|", "[PL]", "(PL)", "NL-", "UK:", "123 PL:".
    /// </summary>
    [GeneratedRegex(
        @"^(\d+\s+)?([A-Z]{2,3}\s*[\|:\-]|\|[A-Z]{2,3}\||\[[A-Z]{2,3}\]|\([A-Z]{2,3}\)|[A-Z]{2,3}-)\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        matchTimeoutMilliseconds: 100
    )]
    public static partial Regex CountryPrefixPattern();

    /// <summary>
    /// Matches video quality and resolution indicators.
    /// Examples: "HD", "FHD", "4K", "4K+", "UHD", "1080p", "720i", "H.264", "HEVC".
    /// Also matches multi-language indicators like "MULTI" and quality variations.
    /// Uses NonBacktracking mode to prevent catastrophic backtracking with Unicode characters.
    /// </summary>
    [GeneratedRegex(
        @"\b(HD|FHD|SD|4K\+?|8K|UHD|HEVC|H\.?265|H\.?264|1080[PI]?|720[PI]?|480[PI]?|576[PI]?|2160[PI]?|MULTI|DUAL|AAC|AC3|DTS|DOLBY|ATMOS)\b",
        RegexOptions.IgnoreCase | RegexOptions.NonBacktracking,
        matchTimeoutMilliseconds: 100
    )]
    public static partial Regex QualityIndicatorPattern();

    /// <summary>
    /// Matches country name suffixes at the end of channel names.
    /// Examples: "Channel Poland", "HBO UK", "Discovery Germany", "MTV POLSKA".
    /// Includes native language country names like "POLSKA" (Polish), "DEUTSCHLAND" (German), etc.
    /// </summary>
    [GeneratedRegex(
        @"\s+(Poland|Polska|PL|UK|Germany|Deutschland|DE|France|FR|Spain|Espana|España|ES|Italy|Italia|IT|Netherlands|Nederland|NL|USA|US|Canada|CA|Australia|AU|Austria|Osterreich|Österreich|AT|Belgium|Belgique|België|BE|Switzerland|Schweiz|Suisse|CH|Czech|Cesko|Česko|CZ|Slovakia|Slovensko|SK|Hungary|Magyarorszag|Magyarország|HU|Romania|RO|Bulgaria|BG|Croatia|Hrvatska|HR|Serbia|Srbija|RS|Slovenia|Slovenija|SI|Portugal|PT|Brazil|Brasil|BR|Mexico|México|MX|Argentina|AR|Chile|CL|Colombia|CO|Peru|Perú|PE|Venezuela|VE|India|IN|Pakistan|PK|Bangladesh|BD|Russia|Rossiya|Россия|RU|Ukraine|Ukraina|Україна|UA|Belarus|BY|Kazakhstan|KZ|Turkey|Turkiye|Türkiye|TR|Greece|Hellas|GR|Israel|IL|Egypt|EG|South Africa|ZA|Nigeria|NG|Kenya|KE|Morocco|MA|Tunisia|TN|Algeria|DZ|Japan|Nippon|JP|China|CN|Korea|KR|Taiwan|TW|Hong Kong|HK|Singapore|SG|Malaysia|MY|Indonesia|ID|Thailand|TH|Vietnam|VN|Philippines|PH|Sweden|Sverige|SE|Norway|Norge|NO|Denmark|Danmark|DK|Finland|Suomi|FI|Iceland|IS|Ireland|IE|Scotland|Wales|England|Latvia|Latvija|LV|Lithuania|Lietuva|LT|Estonia|Eesti|EE)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        matchTimeoutMilliseconds: 100
    )]
    public static partial Regex CountrySuffixPattern();

    /// <summary>
    /// Matches common streaming/broadcast suffixes.
    /// Examples: "Live", "Stream", "TV", "Channel", "Plus", "Extra".
    /// </summary>
    [GeneratedRegex(
        @"\s+(Live|Stream|Streaming|Online|24/7|247|Backup|Main|Primary|Secondary|Alt|Alternative)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        matchTimeoutMilliseconds: 100
    )]
    public static partial Regex StreamingSuffixPattern();

    /// <summary>
    /// Matches all non-alphanumeric characters for final cleanup.
    /// </summary>
    [GeneratedRegex(@"[^A-Za-z0-9]", RegexOptions.Compiled, matchTimeoutMilliseconds: 100)]
    public static partial Regex NonAlphanumericPattern();

    /// <summary>
    /// Matches additional noise patterns commonly found in IPTV channel names.
    /// Includes: NEW, VIP, PREMIUM, MULTI, AUDIO, DUBBED, SUBBED, ORIGINAL, OV, VO, VOST, PPV, EVENT, SPECIAL, PROMO, TEST, DEMO, SAMPLE.
    /// Also matches bracketed content [text], parenthesized content (text), hash numbers #123, and trailing colons.
    /// Uses NonBacktracking mode to prevent catastrophic backtracking with Unicode characters.
    /// </summary>
    [GeneratedRegex(
        @"\b(NEW|VIP|PREMIUM|MULTI|AUDIO|DUBBED|SUBBED|ORIGINAL|OV|VO|VOST|PPV|EVENT|SPECIAL|PROMO|TEST|DEMO|SAMPLE)\b|\[[^\]]*\]|\([^)]*\)|#\d+|:\s*$",
        RegexOptions.IgnoreCase | RegexOptions.NonBacktracking,
        matchTimeoutMilliseconds: 100
    )]
    public static partial Regex AdditionalNoisePattern();

    /// <summary>
    /// Captures the country code from channel name prefixes.
    /// Captures group 1 contains the 2-3 letter country code.
    /// Examples: "PL:" -> "PL", "PL |" -> "PL", "|PL|" -> "PL", "[UK]" -> "UK", "FR-" -> "FR".
    /// </summary>
    [GeneratedRegex(
        @"^(?:\d+\s+)?(?:([A-Z]{2,3})\s*[\|:\-]|\|([A-Z]{2,3})\||\[([A-Z]{2,3})\]|\(([A-Z]{2,3})\)|([A-Z]{2,3})-)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        matchTimeoutMilliseconds: 100
    )]
    public static partial Regex CountryCodeExtractPattern();

    /// <summary>
    /// Extracts the country code from a channel name if present.
    /// </summary>
    /// <param name="channelName">The channel name to extract from.</param>
    /// <returns>The uppercase country code, or null if not found.</returns>
    public static string? ExtractCountryCode(string? channelName)
    {
        if (string.IsNullOrWhiteSpace(channelName))
        {
            return null;
        }

        var match = CountryCodeExtractPattern().Match(channelName);
        if (!match.Success)
        {
            return null;
        }

        // Find which capture group matched (groups 1-5 for different formats)
        for (var i = 1; i <= 5; i++)
        {
            if (match.Groups[i].Success)
            {
                return match.Groups[i].Value.ToUpperInvariant();
            }
        }

        return null;
    }
}
