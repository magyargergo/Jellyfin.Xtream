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
/// Comprehensive tests for <see cref="ChannelNameNormalizer"/>.
/// Tests all normalization steps including country prefixes, quality indicators,
/// special characters, diacritics, and uppercase conversion.
/// Note: ChannelNameNormalizer produces UPPERCASE output (unlike CountryDetectionNormalizer which produces lowercase).
/// </summary>
public sealed class ChannelNameNormalizerTests
{
    private readonly ITestOutputHelper _output;
    private readonly ChannelNameNormalizer _normalizer = ChannelNameNormalizer.Default;

    public ChannelNameNormalizerTests(ITestOutputHelper output)
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
    [InlineData("PL: TVN HD", "TVN")]
    [InlineData("PL | Polsat Sport", "POLSATSPORT")]
    [InlineData("|PL| Discovery", "DISCOVERY")]
    [InlineData("[PL] National Geographic", "NATGEO")]
    [InlineData("(PL) HBO", "HBO")]
    [InlineData("PL- Canal+", "CANAL")]
    [InlineData("UK: BBC One", "BBCONE")]
    [InlineData("UK | Sky News", "SKYNEWS")]
    [InlineData("[UK] ITV", "ITV")]
    [InlineData("DE: RTL", "RTL")]
    [InlineData("DE | ProSieben", "PROSIEBEN")]
    [InlineData("FR: TF1", "TF1")]
    [InlineData("FR | Canal+", "CANAL")]
    [InlineData("NL- Discovery", "DISCOVERY")]
    [InlineData("ES- Comedy Central", "COMEDYCENTRAL")]
    [InlineData("US- HBO", "HBO")]
    public void Normalize_WithCountryPrefix_StripsPrefix(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("123 PL: TVN", "TVN")]
    [InlineData("456 UK: BBC", "BBC")]
    [InlineData("1 DE: RTL", "RTL")]
    public void Normalize_WithNumberedCountryPrefix_StripsPrefix(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    #endregion

    #region Quality Indicator Stripping Tests

    [Theory]
    [InlineData("TVN HD", "TVN")]
    [InlineData("Polsat FHD", "POLSAT")]
    [InlineData("Discovery SD", "DISCOVERY")]
    [InlineData("HBO 4K", "HBO")]
    [InlineData("Canal+ UHD", "CANAL")]
    [InlineData("RTL 1080p", "RTL")]
    [InlineData("ProSieben 720p", "PROSIEBEN")]
    [InlineData("TF1 1080i", "TF1")]
    [InlineData("BBC 720i", "BBC")]
    [InlineData("Sky HEVC", "SKY")]
    [InlineData("ITV H.265", "ITV")]
    [InlineData("Canal H265", "CANAL")]
    [InlineData("RTL H.264", "RTL")]
    [InlineData("ZDF H264", "ZDF")]
    [InlineData("ARD 480p", "ARD")]
    [InlineData("ORF 576i", "ORF")]
    [InlineData("Netflix 2160p", "NETFLIX")]
    public void Normalize_WithQualityIndicator_StripsQuality(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("PL: TVN HD 1080p", "TVN")]
    [InlineData("UK: BBC One FHD HEVC", "BBCONE")]
    [InlineData("DE: RTL 4K UHD H.265", "RTL")]
    public void Normalize_WithMultipleQualityIndicators_StripsAll(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    #endregion

    #region Streaming Suffix Stripping Tests

    [Theory]
    [InlineData("TVN Live", "TVN")]
    [InlineData("Polsat Stream", "POLSAT")]
    [InlineData("Discovery Streaming", "DISCOVERY")]
    [InlineData("HBO Online", "HBO")]
    [InlineData("Canal+ 24/7", "CANAL")]
    [InlineData("RTL 247", "RTL")]
    [InlineData("ProSieben Backup", "PROSIEBEN")]
    [InlineData("TF1 Main", "TF1")]
    [InlineData("BBC Primary", "BBC")]
    [InlineData("Sky Secondary", "SKY")]
    [InlineData("ITV Alt", "ITV")]
    [InlineData("ZDF Alternative", "ZDF")]
    public void Normalize_WithStreamingSuffix_StripsSuffix(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    #endregion

    #region Country Name Suffix Stripping Tests

    [Theory]
    [InlineData("HBO Poland", "HBO")]
    [InlineData("Discovery UK", "DISCOVERY")]
    [InlineData("MTV Germany", "MTV")]
    [InlineData("Eurosport France", "EUROSPORT")]
    [InlineData("Comedy Central Spain", "COMEDYCENTRAL")]
    [InlineData("Nickelodeon Italy", "NICKELODEON")]
    [InlineData("Cartoon Network USA", "CARTOONNETWORK")]
    [InlineData("Fox Sports Australia", "FOXSPORT")] // SPORTS -> SPORT
    [InlineData("Discovery Canada", "DISCOVERY")]
    public void Normalize_WithCountrySuffix_StripsSuffix(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    #endregion

    #region Additional Noise Pattern Stripping Tests

    [Theory]
    [InlineData("TVN NEW", "TVN")]
    [InlineData("Polsat VIP", "POLSAT")]
    [InlineData("Discovery PREMIUM", "DISCOVERY")]
    [InlineData("HBO MULTI", "HBO")]
    [InlineData("Canal+ AUDIO", "CANAL")]
    [InlineData("RTL DUBBED", "RTL")]
    [InlineData("ProSieben SUBBED", "PROSIEBEN")]
    [InlineData("TF1 ORIGINAL", "TF1")]
    [InlineData("BBC OV", "BBC")]
    [InlineData("Sky VO", "SKY")]
    [InlineData("ITV VOST", "ITV")]
    [InlineData("ZDF PPV", "ZDF")]
    [InlineData("ARD EVENT", "ARD")]
    [InlineData("ORF SPECIAL", "ORF")]
    [InlineData("Netflix PROMO", "NETFLIX")]
    [InlineData("Amazon TEST", "AMAZON")]
    [InlineData("Disney DEMO", "DISNEY")]
    [InlineData("Paramount SAMPLE", "PARAMOUNT")]
    public void Normalize_WithNoiseWords_StripsNoise(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("TVN [HD]", "TVN")]
    [InlineData("Polsat [NEW]", "POLSAT")]
    [InlineData("Discovery [Premium]", "DISCOVERY")]
    [InlineData("HBO [Multi-Audio]", "HBO")]
    public void Normalize_WithBracketedContent_StripsBrackets(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("TVN (HD)", "TVN")]
    [InlineData("Polsat (NEW)", "POLSAT")]
    [InlineData("Discovery (Premium)", "DISCOVERY")]
    [InlineData("HBO (Multi-Audio)", "HBO")]
    public void Normalize_WithParenthesizedContent_StripsParentheses(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("TVN #1", "TVN")]
    [InlineData("Polsat #123", "POLSAT")]
    [InlineData("Discovery #999", "DISCOVERY")]
    public void Normalize_WithHashNumbers_StripsHashNumbers(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Channel:", "CHANNEL")]
    [InlineData("Sports: ", "SPORT")] // SPORTS -> SPORT
    public void Normalize_WithTrailingColon_StripsColon(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    #endregion

    #region Special Character Normalization Tests

    [Theory]
    [InlineData("Canal_Plus", "CANALPLUS")]
    [InlineData("Eurosport-1", "EUROSPORT1")]
    [InlineData("National.Geographic", "NATGEO")]
    [InlineData("Discovery/Science", "DISCOVERYSCIENCE")]
    [InlineData("BBC\\News", "BBCNEWS")]
    [InlineData("Music|Video", "MUSICVIDEO")]
    public void Normalize_WithSpecialCharacterSeparators_RemovesSeparators(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Canal+", "CANAL")]
    [InlineData("Disney+", "DISNEY")]
    [InlineData("Apple TV+", "APPLETV")]
    [InlineData("Paramount+", "PARAMOUNT")]
    public void Normalize_WithPlusSign_RemovesPlus(string input, string expected)
    {
        // Note: ChannelNameNormalizer removes + via NonAlphanumericPattern
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Tom & Jerry", "TOMANDJERRY")]
    [InlineData("Law & Order", "LAWANDORDER")]
    [InlineData("Pinky & Brain", "PINKYANDBRAIN")]
    public void Normalize_WithAmpersand_ReplacesWithAnd(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    #endregion

    #region Polish Diacritics Tests

    [Theory]
    [InlineData("ą", "A")]
    [InlineData("ć", "C")]
    [InlineData("ę", "E")]
    [InlineData("ł", "L")]
    [InlineData("ń", "N")]
    [InlineData("ó", "O")]
    [InlineData("ś", "S")]
    [InlineData("ź", "Z")]
    [InlineData("ż", "Z")]
    public void Normalize_WithPolishDiacritics_RemovesDiacritics(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("łódź", "LODZ")]
    [InlineData("Wiadomości", "WIADOMOSCI")]
    [InlineData("Żywiec", "ZYWIEC")]
    [InlineData("Święta", "SWIETA")]
    [InlineData("Książka", "KSIAZKA")]
    [InlineData("Piątek", "PIATEK")]
    [InlineData("Poniedziałek", "PONIEDZIALEK")]
    public void Normalize_WithPolishWords_NormalizesCorrectly(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    #endregion

    #region German Diacritics Tests

    [Theory]
    [InlineData("ä", "A")]
    [InlineData("ö", "O")]
    [InlineData("ü", "U")]
    [InlineData("ß", "SS")]
    public void Normalize_WithGermanDiacritics_RemovesDiacritics(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("München", "MUNCHEN")]
    [InlineData("Düsseldorf", "DUSSELDORF")]
    [InlineData("Köln", "KOLN")]
    [InlineData("Fußball", "FUSSBALL")]
    [InlineData("Größe", "GROSSE")]
    [InlineData("Straße", "STRASSE")]
    public void Normalize_WithGermanWords_NormalizesCorrectly(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    #endregion

    #region French Diacritics Tests

    [Theory]
    [InlineData("à", "A")]
    [InlineData("â", "A")]
    [InlineData("ç", "C")]
    [InlineData("é", "E")]
    [InlineData("è", "E")]
    [InlineData("ê", "E")]
    [InlineData("ë", "E")]
    [InlineData("î", "I")]
    [InlineData("ï", "I")]
    [InlineData("ô", "O")]
    [InlineData("ù", "U")]
    [InlineData("û", "U")]
    [InlineData("ü", "U")]
    [InlineData("ÿ", "Y")]
    public void Normalize_WithFrenchDiacritics_RemovesDiacritics(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Français", "FRANCAIS")]
    [InlineData("Télévision", "TELEVISION")]
    [InlineData("Cinéma", "CINEMA")]
    [InlineData("Château", "CHATEAU")]
    [InlineData("Café", "CAFE")]
    [InlineData("Noël", "NOEL")]
    public void Normalize_WithFrenchWords_NormalizesCorrectly(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    #endregion

    #region Other Diacritics Tests

    [Theory]
    [InlineData("ñ", "N")] // Spanish
    [InlineData("ã", "A")] // Portuguese
    [InlineData("õ", "O")] // Portuguese
    [InlineData("ø", "O")] // Danish/Norwegian
    [InlineData("å", "A")] // Swedish/Norwegian
    [InlineData("æ", "A")] // Danish/Norwegian
    [InlineData("š", "S")] // Czech/Slovak
    [InlineData("č", "C")] // Czech/Slovak
    [InlineData("ž", "Z")] // Czech/Slovak
    [InlineData("ň", "N")] // Czech/Slovak
    [InlineData("ş", "S")] // Turkish
    [InlineData("đ", "D")] // Croatian/Serbian
    [InlineData("þ", "TH")] // Icelandic
    public void Normalize_WithOtherDiacritics_RemovesDiacritics(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("España", "ESPANA")]
    [InlineData("São Paulo", "SAOPAULO")]
    [InlineData("København", "KOBENHAVN")]
    [InlineData("Malmö", "MALMO")]
    [InlineData("Praha", "PRAHA")]
    [InlineData("Reykjavík", "REYKJAVIK")]
    public void Normalize_WithInternationalCityNames_NormalizesCorrectly(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    #endregion

    #region Whitespace and Case Normalization Tests

    [Theory]
    [InlineData("TVN  HD", "TVN")]
    [InlineData("Polsat   Sport", "POLSATSPORT")]
    [InlineData("Discovery    Channel", "DISCOVERY")]
    [InlineData("  HBO  ", "HBO")]
    [InlineData("\tCanal+\t", "CANAL")]
    [InlineData("  Multiple   Spaces   Here  ", "MULTIPLESPACESHERE")]
    public void Normalize_WithMultipleWhitespace_CollapsesAndRemoves(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("TVN", "TVN")]
    [InlineData("tvn", "TVN")]
    [InlineData("TvN", "TVN")]
    [InlineData("DISCOVERY CHANNEL", "DISCOVERY")]
    [InlineData("discovery channel", "DISCOVERY")]
    [InlineData("Discovery Channel", "DISCOVERY")]
    public void Normalize_WithMixedCase_ConvertsToUppercase(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    #endregion

    #region Complex Real-World Channel Name Tests

    [Theory]
    [InlineData("PL: TVN HD 1080p [NEW]", "TVN")]
    [InlineData("PL | Polsat Sport FHD Live", "POLSATSPORT")]
    [InlineData("[UK] BBC One HD HEVC 24/7", "BBCONE")]
    [InlineData("DE: RTL 4K UHD Germany Backup", "RTL")]
    [InlineData("FR | Canal+ Sport France FHD Stream", "CANALSPORT")]
    [InlineData("123 PL: Discovery Channel HD Poland", "DISCOVERY")]
    [InlineData("|UK| Sky Sports News HD Primary", "SKYSPORTNEWS")] // SPORTS -> SPORT
    [InlineData("(DE) ProSieben MAXX FHD Alternative", "PROSIEBENMAXX")]
    public void Normalize_WithComplexRealWorldNames_NormalizesCorrectly(string input, string expected)
    {
        var result = _normalizer.Normalize(input);
        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("PL: Wiadomości HD", "WIADOMOSCI")]
    [InlineData("DE: Fußball Bundesliga", "FUSSBALLBUNDESLIGA")]
    [InlineData("FR: Télévision Française HD", "TELEVISIONFRANCAISE")]
    [InlineData("ES: Fútbol HD", "FUTBOL")]
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
        // This test verifies the order of transformations
        var input = "123 PL: Wiadomości HD [NEW] #1 Poland";
        var expected = "WIADOMOSCI";

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
            ("TVP1", "TVP1"),
            ("TVP 1", "TVP1"),
            ("TVP1 HD", "TVP1"),
            ("Eurosport 1", "EUROSPORT1"),
            ("Eurosport 2", "EUROSPORT2"),
            ("Canal+ 1", "CANAL1"),
            ("HBO 2", "HBO2"),
            ("Sky 1", "SKY1"),
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

        // All should equal "TVN"
        Assert.All(normalized, n => Assert.Equal("TVN", n));
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

    #region Comparison with CountryDetectionNormalizer Tests

    [Fact]
    public void Normalize_ProducesUppercaseOutput()
    {
        // ChannelNameNormalizer should produce uppercase output
        var input = "PL: TVN HD";
        var result = _normalizer.Normalize(input);

        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal(result, result.ToUpperInvariant());
        Assert.Equal("TVN", result);
    }

    [Fact]
    public void Normalize_RemovesNonAlphanumericCharacters()
    {
        // Unlike CountryDetectionNormalizer, this removes all non-alphanumeric chars including spaces
        var input = "Canal+ Sport HD";
        var result = _normalizer.Normalize(input);

        _output.WriteLine($"'{input}' -> '{result}'");
        Assert.Equal("CANALSPORT", result);
        Assert.DoesNotContain(" ", result);
        Assert.DoesNotContain("+", result);
    }

    #endregion
}
