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

using Jellyfin.Xtream.Service.ChannelMatching;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Comprehensive tests for <see cref="CountryDetectionNormalizer"/>.
/// Tests all normalization steps including country prefixes, quality indicators,
/// special characters, diacritics, and whitespace handling.
/// </summary>
public sealed class CountryDetectionNormalizerTests
{
    private readonly ITestOutputHelper _output;
    private readonly CountryDetectionNormalizer _normalizer = CountryDetectionNormalizer.Default;

    public CountryDetectionNormalizerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    #region Empty and Null Input Tests

    [Fact]
    public void Normalize_NullInput_ReturnsEmptyString()
    {
        var result = _normalizer.Normalize(null!);
        Assert.Equal(string.Empty, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData("  \t\n  ")]
    public void Normalize_EmptyOrWhitespace_ReturnsEmptyString(string input)
    {
        var result = _normalizer.Normalize(input);
        Assert.Equal(string.Empty, result);
    }

    #endregion

    #region Country Prefix Stripping Tests

    [Theory]
    [InlineData("PL: TVN HD", "tvn")]
    [InlineData("PL | Polsat Sport", "polsat sport")]
    [InlineData("|PL| Discovery", "discovery")]
    [InlineData("[PL] National Geographic", "national geographic")]
    [InlineData("(PL) HBO", "hbo")]
    [InlineData("PL- Canal+", "canal+")]
    [InlineData("UK: BBC One", "bbc one")]
    [InlineData("UK | Sky News", "sky news")]
    [InlineData("[UK] ITV", "itv")]
    [InlineData("DE: RTL", "rtl")]
    [InlineData("DE | ProSieben", "prosieben")]
    [InlineData("FR: TF1", "tf1")]
    [InlineData("FR | Canal+", "canal+")]
    [InlineData("NL- Discovery", "discovery")]
    [InlineData("ES- Comedy Central", "comedy central")]
    [InlineData("US- HBO", "hbo")]
    public void Normalize_WithCountryPrefix_StripsPrefix(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("123 PL: TVN", "tvn")]
    [InlineData("456 UK: BBC", "bbc")]
    [InlineData("1 DE: RTL", "rtl")]
    public void Normalize_WithNumberedCountryPrefix_StripsPrefix(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    #endregion

    #region Quality Indicator Stripping Tests

    [Theory]
    [InlineData("TVN HD", "tvn")]
    [InlineData("Polsat FHD", "polsat")]
    [InlineData("Discovery SD", "discovery")]
    [InlineData("HBO 4K", "hbo")]
    [InlineData("Canal+ UHD", "canal+")]
    [InlineData("RTL 1080p", "rtl")]
    [InlineData("ProSieben 720p", "prosieben")]
    [InlineData("TF1 1080i", "tf1")]
    [InlineData("BBC 720i", "bbc")]
    [InlineData("Sky HEVC", "sky")]
    [InlineData("ITV H.265", "itv")]
    [InlineData("Canal H265", "canal")]
    [InlineData("RTL H.264", "rtl")]
    [InlineData("ZDF H264", "zdf")]
    [InlineData("ARD 480p", "ard")]
    [InlineData("ORF 576i", "orf")]
    [InlineData("Netflix 2160p", "netflix")]
    public void Normalize_WithQualityIndicator_StripsQuality(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("PL: TVN HD 1080p", "tvn")]
    [InlineData("UK: BBC One FHD HEVC", "bbc one")]
    [InlineData("DE: RTL 4K UHD H.265", "rtl")]
    public void Normalize_WithMultipleQualityIndicators_StripsAll(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    #endregion

    #region Streaming Suffix Stripping Tests

    [Theory]
    [InlineData("TVN Live", "tvn")]
    [InlineData("Polsat Stream", "polsat")]
    [InlineData("Discovery Streaming", "discovery")]
    [InlineData("HBO Online", "hbo")]
    [InlineData("Canal+ 24/7", "canal+")]
    [InlineData("RTL 247", "rtl")]
    [InlineData("ProSieben Backup", "prosieben")]
    [InlineData("TF1 Main", "tf1")]
    [InlineData("BBC Primary", "bbc")]
    [InlineData("Sky Secondary", "sky")]
    [InlineData("ITV Alt", "itv")]
    [InlineData("ZDF Alternative", "zdf")]
    public void Normalize_WithStreamingSuffix_StripsSuffix(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    #endregion

    #region Country Name Suffix Stripping Tests

    [Theory]
    [InlineData("HBO Poland", "hbo")]
    [InlineData("Discovery UK", "discovery")]
    [InlineData("MTV Germany", "mtv")]
    [InlineData("Eurosport France", "eurosport")]
    [InlineData("Comedy Central Spain", "comedy central")]
    [InlineData("Nickelodeon Italy", "nickelodeon")]
    [InlineData("Cartoon Network USA", "cartoon network")]
    [InlineData("Fox Sports Australia", "fox sports")]
    [InlineData("Discovery Canada", "discovery")]
    public void Normalize_WithCountrySuffix_StripsSuffix(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    #endregion

    #region Additional Noise Pattern Stripping Tests

    [Theory]
    [InlineData("TVN NEW", "tvn")]
    [InlineData("Polsat VIP", "polsat")]
    [InlineData("Discovery PREMIUM", "discovery")]
    [InlineData("HBO MULTI", "hbo")]
    [InlineData("Canal+ AUDIO", "canal+")]
    [InlineData("RTL DUBBED", "rtl")]
    [InlineData("ProSieben SUBBED", "prosieben")]
    [InlineData("TF1 ORIGINAL", "tf1")]
    [InlineData("BBC OV", "bbc")]
    [InlineData("Sky VO", "sky")]
    [InlineData("ITV VOST", "itv")]
    [InlineData("ZDF PPV", "zdf")]
    [InlineData("ARD EVENT", "ard")]
    [InlineData("ORF SPECIAL", "orf")]
    [InlineData("Netflix PROMO", "netflix")]
    [InlineData("Amazon TEST", "amazon")]
    [InlineData("Disney DEMO", "disney")]
    [InlineData("Paramount SAMPLE", "paramount")]
    public void Normalize_WithNoiseWords_StripsNoise(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("TVN [HD]", "tvn")]
    [InlineData("Polsat [NEW]", "polsat")]
    [InlineData("Discovery [Premium]", "discovery")]
    [InlineData("HBO [Multi-Audio]", "hbo")]
    public void Normalize_WithBracketedContent_StripsBrackets(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("TVN (HD)", "tvn")]
    [InlineData("Polsat (NEW)", "polsat")]
    [InlineData("Discovery (Premium)", "discovery")]
    [InlineData("HBO (Multi-Audio)", "hbo")]
    public void Normalize_WithParenthesizedContent_StripsParentheses(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("TVN #1", "tvn")]
    [InlineData("Polsat #123", "polsat")]
    [InlineData("Discovery #999", "discovery")]
    public void Normalize_WithHashNumbers_StripsHashNumbers(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Channel:", "channel")]
    [InlineData("Sports: ", "sports")]
    public void Normalize_WithTrailingColon_StripsColon(string input, string expected)
    {
        // Note: Short names like "TVN:" may be matched as country prefixes
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    #endregion

    #region Special Character Normalization Tests

    [Theory]
    [InlineData("Canal_Plus", "canal plus")]
    [InlineData("Eurosport-1", "eurosport 1")]
    [InlineData("National.Geographic", "national geographic")]
    [InlineData("Discovery/Science", "discovery science")]
    [InlineData("BBC\\News", "bbc news")]
    [InlineData("Music|Video", "music video")] // Note: MTV| looks like a country prefix pattern
    public void Normalize_WithSpecialCharacterSeparators_ReplacesWithSpaces(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Canal+", "canal+")]
    [InlineData("Disney+", "disney+")]
    [InlineData("Apple TV+", "apple tv+")]
    [InlineData("Paramount+", "paramount+")]
    public void Normalize_WithPlusSign_PreservesPlus(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Tom & Jerry", "tom and jerry")]
    [InlineData("Law & Order", "law and order")]
    [InlineData("Pinky & Brain", "pinky and brain")]
    public void Normalize_WithAmpersand_ReplacesWithAnd(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    #endregion

    #region Polish Diacritics Tests

    [Theory]
    [InlineData("ą", "a")]
    [InlineData("ć", "c")]
    [InlineData("ę", "e")]
    [InlineData("ł", "l")]
    [InlineData("ń", "n")]
    [InlineData("ó", "o")]
    [InlineData("ś", "s")]
    [InlineData("ź", "z")]
    [InlineData("ż", "z")]
    public void Normalize_WithPolishDiacritics_RemovesDiacritics(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("łódź", "lodz")] // Lowercase - handled by NormalizeSpecialCharacters
    [InlineData("Wiadomości", "wiadomosci")]
    [InlineData("Żywiec", "zywiec")]
    [InlineData("Święta", "swieta")]
    [InlineData("Książka", "ksiazka")]
    [InlineData("Piątek", "piatek")]
    [InlineData("Poniedziałek", "poniedzialek")]
    public void Normalize_WithPolishWords_NormalizesCorrectly(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    #endregion

    #region German Diacritics Tests

    [Theory]
    [InlineData("ä", "a")]
    [InlineData("ö", "o")]
    [InlineData("ü", "u")]
    [InlineData("ß", "ss")]
    public void Normalize_WithGermanDiacritics_RemovesDiacritics(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("München", "munchen")]
    [InlineData("Düsseldorf", "dusseldorf")]
    [InlineData("Köln", "koln")]
    [InlineData("Fußball", "fussball")]
    [InlineData("Größe", "grosse")]
    [InlineData("Straße", "strasse")]
    public void Normalize_WithGermanWords_NormalizesCorrectly(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    #endregion

    #region French Diacritics Tests

    [Theory]
    [InlineData("à", "a")]
    [InlineData("â", "a")]
    [InlineData("ç", "c")]
    [InlineData("é", "e")]
    [InlineData("è", "e")]
    [InlineData("ê", "e")]
    [InlineData("ë", "e")]
    [InlineData("î", "i")]
    [InlineData("ï", "i")]
    [InlineData("ô", "o")]
    [InlineData("ù", "u")]
    [InlineData("û", "u")]
    [InlineData("ü", "u")]
    [InlineData("ÿ", "y")]
    public void Normalize_WithFrenchDiacritics_RemovesDiacritics(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Français", "francais")]
    [InlineData("Télévision", "television")]
    [InlineData("Cinéma", "cinema")]
    [InlineData("Château", "chateau")]
    [InlineData("Café", "cafe")]
    [InlineData("Noël", "noel")]
    public void Normalize_WithFrenchWords_NormalizesCorrectly(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    #endregion

    #region Other Diacritics Tests

    [Theory]
    [InlineData("ñ", "n")] // Spanish
    [InlineData("ã", "a")] // Portuguese
    [InlineData("õ", "o")] // Portuguese
    [InlineData("ø", "o")] // Danish/Norwegian
    [InlineData("å", "a")] // Swedish/Norwegian
    [InlineData("æ", "a")] // Danish/Norwegian
    [InlineData("š", "s")] // Czech/Slovak
    [InlineData("č", "c")] // Czech/Slovak
    [InlineData("ž", "z")] // Czech/Slovak
    [InlineData("ň", "n")] // Czech/Slovak
    [InlineData("ş", "s")] // Turkish
    [InlineData("đ", "d")] // Croatian/Serbian
    [InlineData("þ", "th")] // Icelandic
    public void Normalize_WithOtherDiacritics_RemovesDiacritics(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("España", "espana")]
    [InlineData("São Paulo", "sao paulo")]
    [InlineData("København", "kobenhavn")]
    [InlineData("Malmö", "malmo")]
    [InlineData("Praha", "praha")]
    [InlineData("Reykjavík", "reykjavik")]
    public void Normalize_WithInternationalCityNames_NormalizesCorrectly(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    #endregion

    #region Whitespace Normalization Tests

    [Theory]
    [InlineData("TVN  HD", "tvn")]
    [InlineData("Polsat   Sport", "polsat sport")]
    [InlineData("Discovery    Channel", "discovery channel")]
    [InlineData("  HBO  ", "hbo")]
    [InlineData("\tCanal+\t", "canal+")]
    [InlineData("  Multiple   Spaces   Here  ", "multiple spaces here")]
    public void Normalize_WithMultipleWhitespace_CollapsesToSingleSpace(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    #endregion

    #region Case Normalization Tests

    [Theory]
    [InlineData("TVN", "tvn")]
    [InlineData("tvn", "tvn")]
    [InlineData("TvN", "tvn")]
    [InlineData("DISCOVERY CHANNEL", "discovery channel")]
    [InlineData("discovery channel", "discovery channel")]
    [InlineData("Discovery Channel", "discovery channel")]
    public void Normalize_WithMixedCase_ConvertsToLowercase(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    #endregion

    #region Complex Real-World Channel Name Tests

    [Theory]
    [InlineData("PL: TVN HD 1080p [NEW]", "tvn")]
    [InlineData("PL | Polsat Sport FHD Live", "polsat sport")]
    [InlineData("[UK] BBC One HD HEVC 24/7", "bbc one")]
    [InlineData("DE: RTL 4K UHD Germany Backup", "rtl")]
    [InlineData("FR | Canal+ Sport France FHD Stream", "canal+ sport")]
    [InlineData("123 PL: Discovery Channel HD Poland", "discovery channel")]
    [InlineData("|UK| Sky Sports News HD Primary", "sky sports news")]
    [InlineData("(DE) ProSieben MAXX FHD Alternative", "prosieben maxx")]
    public void Normalize_WithComplexRealWorldNames_NormalizesCorrectly(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("PL: Wiadomości HD", "wiadomosci")]
    [InlineData("DE: Fußball Bundesliga", "fussball bundesliga")]
    [InlineData("FR: Télévision Française HD", "television francaise")]
    [InlineData("ES: Fútbol HD", "futbol")]
    public void Normalize_WithDiacriticsAndPrefixes_NormalizesCorrectly(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    #endregion

    #region Edge Cases Tests

    [Theory]
    [InlineData("HD", "")]
    [InlineData("FHD 4K", "")]
    [InlineData("1080p HEVC", "")]
    public void Normalize_WithOnlyQualityIndicators_ReturnsEmpty(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("PL:", "")]
    [InlineData("UK |", "")]
    [InlineData("[DE]", "")]
    public void Normalize_WithOnlyCountryPrefix_ReturnsEmpty(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Normalize_WithAllTransformations_AppliesInCorrectOrder()
    {
        // This test verifies the order of transformations:
        // 1. Country prefix -> 2. Quality -> 3. Streaming suffix -> 4. Country suffix
        // -> 5. Noise -> 6. Special chars -> 7. Diacritics -> 8. Whitespace -> 9. Lowercase
        // Note: CountrySuffixPattern requires country name at end ($ anchor)
        var input = "123 PL: Wiadomości HD [NEW] #1 Poland";
        var expected = "wiadomosci";

        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Normalize_PreservesNumericChannelIdentifiers()
    {
        // Numbers within channel names should be preserved
        var testCases = new[]
        {
            ("TVP1", "tvp1"),
            ("TVP 1", "tvp 1"),
            ("TVP1 HD", "tvp1"),
            ("Eurosport 1", "eurosport 1"),
            ("Eurosport 2", "eurosport 2"),
            ("Canal+ 1", "canal+ 1"),
            ("HBO 2", "hbo 2"),
            ("Sky 1", "sky 1"),
        };

        foreach (var (input, expected) in testCases)
        {
            var result = _normalizer.Normalize(input);
            _output.WriteLine($"'{input}' -> '{result}'");
            Assert.Equal(expected, result);
        }
    }

    #endregion

    #region Consistency Tests

    [Fact]
    public void Normalize_SameChannelDifferentFormats_ProducesSameResult()
    {
        // All these should normalize to the same value
        var variants = new[]
        {
            "PL: TVN HD",
            "PL | TVN FHD",
            "|PL| TVN 4K",
            "[PL] TVN 1080p",
            "(PL) TVN UHD",
            "TVN HD Poland",
            "TVN Poland HD",
            "tvn hd",
            "TVN  HD",
        };

        var normalized = variants.Select(v => _normalizer.Normalize(v)).ToArray();
        _output.WriteLine("Normalized values:");
        for (int i = 0; i < variants.Length; i++)
        {
            _output.WriteLine($"  '{variants[i]}' -> '{normalized[i]}'");
        }

        // All should equal "tvn"
        Assert.All(normalized, n => Assert.Equal("tvn", n));
    }

    [Fact]
    public void Normalize_UnderscoreSeparator_ReplacesWithSpace()
    {
        // Underscore is replaced with space, so TVN_HD becomes "tvn hd"
        // which still contains "hd" that gets stripped
        var result = _normalizer.Normalize("Discovery_Channel");
        Assert.Equal("discovery channel", result);
    }

    [Fact]
    public void Normalize_IsIdempotent()
    {
        // Normalizing an already normalized string should produce the same result
        var inputs = new[] { "PL: TVN HD Poland", "UK: BBC One FHD Live", "DE: RTL 4K Germany Backup" };

        foreach (var input in inputs)
        {
            var firstPass = _normalizer.Normalize(input);
            var secondPass = _normalizer.Normalize(firstPass);

            _output.WriteLine($"'{input}' -> '{firstPass}' -> '{secondPass}'");
            Assert.Equal(firstPass, secondPass);
        }
    }

    [Fact]
    public void Normalize_DifferentChannels_ProduceDifferentResults()
    {
        // Different channels should NOT normalize to the same value
        var channels = new[]
        {
            ("TVN", "TVN24"),
            ("Polsat", "Polsat Sport"),
            ("HBO", "HBO 2"),
            ("Eurosport 1", "Eurosport 2"),
            ("Canal+", "Canal+ Sport"),
            ("Discovery", "Discovery Science"),
        };

        foreach (var (channel1, channel2) in channels)
        {
            var norm1 = _normalizer.Normalize(channel1);
            var norm2 = _normalizer.Normalize(channel2);

            _output.WriteLine($"'{channel1}' -> '{norm1}' vs '{channel2}' -> '{norm2}'");
            Assert.NotEqual(norm1, norm2);
        }
    }

    #endregion
}
