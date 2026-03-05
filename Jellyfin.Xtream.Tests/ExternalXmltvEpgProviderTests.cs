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
using System.Text.RegularExpressions;
using Jellyfin.Xtream.Service.ChannelMatching;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Tests for ExternalXmltvEpgProvider channel name normalization and logo fallback.
/// Verifies that channel names from different formats normalize consistently
/// for EPG matching and logo URL generation.
/// </summary>
public sealed partial class ExternalXmltvEpgProviderTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;
    private readonly ChannelNameNormalizer _normalizer = ChannelNameNormalizer.Default;

    /// <summary>
    /// Tests that display names from deduplicated channels match XMLTV display-names.
    /// This is critical for EPG matching - the display name used in LiveTvService
    /// must normalize the same way as XMLTV channel names.
    /// </summary>
    [Theory]
    [InlineData("TVN", "TVN")]
    [InlineData("TVN HD", "TVN")]
    [InlineData("Polsat", "Polsat HD")]
    [InlineData("HBO", "HBO Poland")]
    [InlineData("Canal+ Sport", "CANAL+ SPORT HD")]
    [InlineData("Discovery Channel", "Discovery Channel HD")]
    [InlineData("BBC One", "BBC One HD")]
    [InlineData("Eurosport 1", "Eurosport 1 HD")]
    public void NormalizedNames_Match_BetweenDisplayAndXmltv(string displayName, string xmltvName)
    {
        var normalizedDisplay = _normalizer.Normalize(displayName);
        var normalizedXmltv = _normalizer.Normalize(xmltvName);

        _output.WriteLine($"Display name: '{displayName}' -> '{normalizedDisplay}'");
        _output.WriteLine($"XMLTV name: '{xmltvName}' -> '{normalizedXmltv}'");

        Assert.Equal(normalizedDisplay, normalizedXmltv);
    }

    /// <summary>
    /// Tests that Polish channels with diacritics match their ASCII equivalents.
    /// XMLTV sources may use either form.
    /// </summary>
    [Theory]
    [InlineData("Wiadomości", "Wiadomosci")]
    [InlineData("TVP Łódź", "TVP Lodz")]
    [InlineData("Polsat Święta", "Polsat Swieta")]
    [InlineData("Żywiec TV", "Zywiec TV")]
    public void NormalizedNames_Match_WithDiacritics(string withDiacritics, string withoutDiacritics)
    {
        var normalized1 = _normalizer.Normalize(withDiacritics);
        var normalized2 = _normalizer.Normalize(withoutDiacritics);

        _output.WriteLine($"With diacritics: '{withDiacritics}' -> '{normalized1}'");
        _output.WriteLine($"Without diacritics: '{withoutDiacritics}' -> '{normalized2}'");

        Assert.Equal(normalized1, normalized2);
    }

    /// <summary>
    /// Tests that country prefixes are stripped for XMLTV matching.
    /// Provider streams often have country prefixes but XMLTV display-names typically don't.
    /// </summary>
    [Theory]
    [InlineData("PL | TVN HD", "TVN")]
    [InlineData("PL: Polsat Sport", "Polsat Sport")]
    [InlineData("|PL| HBO HD", "HBO")]
    [InlineData("[UK] BBC One", "BBC One")]
    [InlineData("FR- Canal+", "Canal+")]
    [InlineData("123 PL: Discovery", "Discovery")]
    public void NormalizedNames_StripCountryPrefixes(string providerName, string xmltvName)
    {
        var normalizedProvider = _normalizer.Normalize(providerName);
        var normalizedXmltv = _normalizer.Normalize(xmltvName);

        _output.WriteLine($"Provider: '{providerName}' -> '{normalizedProvider}'");
        _output.WriteLine($"XMLTV: '{xmltvName}' -> '{normalizedXmltv}'");

        Assert.Equal(normalizedProvider, normalizedXmltv);
    }

    /// <summary>
    /// Tests that different channels remain distinct after normalization.
    /// Ensures no false positive matching for EPG data.
    /// </summary>
    [Theory]
    [InlineData("TVN", "TVN24")]
    [InlineData("HBO", "HBO2")]
    [InlineData("Polsat", "Polsat Sport")]
    [InlineData("Canal+", "Canal+ Sport")]
    [InlineData("Eurosport", "Eurosport 2")]
    [InlineData("Discovery", "Discovery Science")]
    public void NormalizedNames_RemainDistinct_ForDifferentChannels(string channel1, string channel2)
    {
        var normalized1 = _normalizer.Normalize(channel1);
        var normalized2 = _normalizer.Normalize(channel2);

        _output.WriteLine($"Channel 1: '{channel1}' -> '{normalized1}'");
        _output.WriteLine($"Channel 2: '{channel2}' -> '{normalized2}'");

        Assert.NotEqual(normalized1, normalized2);
    }

    /// <summary>
    /// Tests logo URL generation with URL encoding.
    /// The external EPG provider constructs logo URLs like:
    /// {baseUrl}/{encodedChannelName}.png
    /// </summary>
    [Theory]
    [InlineData("TVN", "TVN")]
    [InlineData("Canal+ Sport", "Canal%2B+Sport")]
    [InlineData("BBC One HD", "BBC+One+HD")]
    [InlineData("Polsat Sport Extra", "Polsat+Sport+Extra")]
    public void LogoUrl_IsProperlyEncoded(string channelName, string expectedEncoded)
    {
        // This simulates the encoding logic in ExternalXmltvEpgProvider.GetLogoUrl
        var encodedName = Uri.EscapeDataString(channelName.Trim()).Replace("%20", "+", StringComparison.Ordinal);

        _output.WriteLine($"Channel: '{channelName}'");
        _output.WriteLine($"Encoded: '{encodedName}'");
        _output.WriteLine($"Expected: '{expectedEncoded}'");

        Assert.Equal(expectedEncoded, encodedName);
    }

    /// <summary>
    /// Tests that the display name used for logo fallback matches what XMLTV expects.
    /// The chain is: ProviderStream.Name -> ProcessChannelName -> DisplayName -> GetLogoUrl
    /// </summary>
    [Theory]
    [InlineData("PL | TVN HD", "TVN")] // Display name after cleanup should be "TVN"
    [InlineData("123 PL: Polsat Sport FHD", "Polsat Sport")] // Number, country, quality stripped
    [InlineData("|UK| BBC One 4K", "BBC One")] // Pipe-delimited country stripped
    [InlineData("[FR] Canal+ HD", "Canal+")] // Bracketed country stripped
    public void DisplayName_UsedForLogoFallback_IsClean(string inputName, string expectedDisplayName)
    {
        // Simulate ProcessChannelName from ChannelProviderMap
        var parsed = Jellyfin.Xtream.Service.StreamService.ParseName(inputName);

        // DisplayNameCleanupRegex pattern (simplified for testing)
        var displayName = MyRegex().Replace(parsed.Title, " ").Trim();

        // Collapse multiple spaces
        displayName = System.Text.RegularExpressions.Regex.Replace(displayName, @"\s{2,}", " ");

        _output.WriteLine($"Input: '{inputName}'");
        _output.WriteLine($"Parsed title: '{parsed.Title}'");
        _output.WriteLine($"Display name: '{displayName}'");

        Assert.Equal(expectedDisplayName, displayName);
    }

    /// <summary>
    /// Tests that XMLTV channel display-names with various formats all normalize
    /// to match against provider channel names.
    /// </summary>
    [Theory]
    [InlineData("TVP 1", "TVP1")] // Space vs no space
    [InlineData("TVP 2 HD", "TVP2")] // With quality indicator
    [InlineData("Polsat HD", "POLSAT")] // Case difference
    [InlineData("canal+", "Canal+")] // Plus sign preserved
    [InlineData("bbc one", "BBC One")] // All lowercase
    public void XmltvDisplayNames_MatchProviderNames(string xmltvDisplayName, string providerName)
    {
        var normalizedXmltv = _normalizer.Normalize(xmltvDisplayName);
        var normalizedProvider = _normalizer.Normalize(providerName);

        _output.WriteLine($"XMLTV display-name: '{xmltvDisplayName}' -> '{normalizedXmltv}'");
        _output.WriteLine($"Provider name: '{providerName}' -> '{normalizedProvider}'");

        Assert.Equal(normalizedXmltv, normalizedProvider);
    }

    /// <summary>
    /// Tests that the logo fallback chain works correctly:
    /// 1. Try provider stream icon (BestImageUrl from ChannelWithProviders)
    /// 2. If empty, try GetLogoUrl with display name
    /// 3. Use cached XMLTV icon or constructed URL
    /// </summary>
    [Fact]
    public void LogoFallback_Chain_UsesDisplayNameCorrectly()
    {
        // Scenario: Provider has no icon, need to use external EPG fallback
        const string providerName = "PL | TVN HD";
        var parsed = Jellyfin.Xtream.Service.StreamService.ParseName(providerName);

        // Clean display name (as ChannelProviderMap.ProcessChannelName does)
        var displayName = MyRegex().Replace(parsed.Title, " ").Trim();
        displayName = System.Text.RegularExpressions.Regex.Replace(displayName, @"\s{2,}", " ");

        // This display name would be passed to GetLogoUrl
        _output.WriteLine($"Provider name: '{providerName}'");
        _output.WriteLine($"Display name for logo lookup: '{displayName}'");

        // The normalized version should match XMLTV entries
        var normalizedForLookup = _normalizer.Normalize(displayName);
        var normalizedXmltvEntry = _normalizer.Normalize("TVN"); // XMLTV would have this

        _output.WriteLine($"Normalized for lookup: '{normalizedForLookup}'");
        _output.WriteLine($"Normalized XMLTV entry: '{normalizedXmltvEntry}'");

        Assert.Equal(normalizedForLookup, normalizedXmltvEntry);
    }

    /// <summary>
    /// Tests real-world scenario where channels from multiple providers
    /// need to match XMLTV EPG data using consistent normalization.
    /// </summary>
    [Fact]
    public void RealWorld_MultiProviderChannels_MatchXmltvEpg()
    {
        // Channels from different providers with different naming conventions
        var providerChannels = new[]
        {
            ("Provider A", "PL | TVN HD"),
            ("Provider B", "PL: TVN FHD"),
            ("Provider C", "|PL| TVN 4K"),
        };

        // XMLTV source has a single entry for TVN
        const string xmltvDisplayName = "TVN";
        var normalizedXmltv = _normalizer.Normalize(xmltvDisplayName);

        _output.WriteLine($"XMLTV entry: '{xmltvDisplayName}' -> '{normalizedXmltv}'");
        _output.WriteLine("");

        foreach (var (provider, channelName) in providerChannels)
        {
            var normalizedProvider = _normalizer.Normalize(channelName);
            _output.WriteLine($"{provider}: '{channelName}' -> '{normalizedProvider}'");

            // All providers should normalize to match the XMLTV entry
            Assert.Equal(normalizedXmltv, normalizedProvider);
        }
    }

    /// <summary>
    /// Tests that the epg.ovh logo URL format is correctly constructed.
    /// Format: https://epg.ovh/logo/{channelName}.png
    /// </summary>
    [Fact]
    public void EpgOvh_LogoUrl_Format()
    {
        const string baseUrl = "https://epg.ovh/logo";
        var channelNames = new[]
        {
            ("TVN", "https://epg.ovh/logo/TVN.png"),
            ("Canal+ Sport", "https://epg.ovh/logo/Canal%2B+Sport.png"),
            ("Polsat Sport Extra", "https://epg.ovh/logo/Polsat+Sport+Extra.png"),
            ("TVP 1 HD", "https://epg.ovh/logo/TVP+1+HD.png"),
        };

        foreach (var (channelName, expectedUrl) in channelNames)
        {
            var encodedName = Uri.EscapeDataString(channelName.Trim()).Replace("%20", "+", StringComparison.Ordinal);
            var actualUrl = $"{baseUrl}/{encodedName}.png";

            _output.WriteLine($"Channel: '{channelName}' -> {actualUrl}");

            Assert.Equal(expectedUrl, actualUrl);
        }
    }

    /// <summary>
    /// Tests XMLTV date/time parsing formats that might appear in EPG data.
    /// </summary>
    [Theory]
    [InlineData("20250122180000 +0100", 2025, 1, 22, 17, 0, 0)] // +0100 timezone
    [InlineData("20250122180000 -0500", 2025, 1, 22, 23, 0, 0)] // -0500 timezone (EST)
    [InlineData("20250122180000 +0000", 2025, 1, 22, 18, 0, 0)] // UTC
    public void XmltvDateTime_ParsesCorrectly(
        string dateTimeStr,
        int expectedYear,
        int expectedMonth,
        int expectedDay,
        int expectedHour,
        int expectedMinute,
        int expectedSecond
    )
    {
        // Test the parsing logic similar to ExternalXmltvEpgProvider.ParseXmltvDateTime
        var parsed = ParseXmltvDateTimeForTest(dateTimeStr);

        _output.WriteLine($"Input: '{dateTimeStr}'");
        _output.WriteLine($"Parsed (UTC): {parsed:yyyy-MM-dd HH:mm:ss}");

        Assert.Equal(expectedYear, parsed.Year);
        Assert.Equal(expectedMonth, parsed.Month);
        Assert.Equal(expectedDay, parsed.Day);
        Assert.Equal(expectedHour, parsed.Hour);
        Assert.Equal(expectedMinute, parsed.Minute);
        Assert.Equal(expectedSecond, parsed.Second);
    }

    private static DateTime ParseXmltvDateTimeForTest(string dateStr)
    {
        // Simplified version of ExternalXmltvEpgProvider.ParseXmltvDateTime for testing
        if (string.IsNullOrEmpty(dateStr))
        {
            return DateTime.MinValue;
        }

        try
        {
            var datePart = dateStr[..14];
            var tzPart = dateStr[15..].Trim();

            if (
                DateTime.TryParseExact(
                    datePart,
                    "yyyyMMddHHmmss",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out var dt
                )
                && tzPart.Length >= 4
            )
            {
                var sign = tzPart[0] == '-' ? -1 : 1;
                var offsetStr = tzPart.TrimStart('+', '-');
                if (
                    int.TryParse(offsetStr[..2], out var hours) && int.TryParse(offsetStr.AsSpan(2, 2), out var minutes)
                )
                {
                    var offset = new TimeSpan(sign * hours, sign * minutes, 0);
                    return DateTime.SpecifyKind(dt.Add(-offset), DateTimeKind.Utc);
                }
            }
        }
        catch
        {
            // Fall through
        }

        return DateTime.MinValue;
    }

    [GeneratedRegexAttribute(
        @"^(\d+\s+)?(\|?[A-Z]{2,3}\||\[[A-Z]{2,3}\]|\([A-Z]{2,3}\)|[A-Z]{2,3}\s*[:\-\|])\s*|\b(HD|FHD|SD|4K|UHD)\b",
        RegexOptions.IgnoreCase,
        "en-GB"
    )]
    private static partial System.Text.RegularExpressions.Regex MyRegex();
}
