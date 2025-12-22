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

using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Service.ChannelMatching;
using Jellyfin.Xtream.Service.ChannelMatching.Rules;
using Jellyfin.Xtream.Service.Discovery.Pipeline.Stages;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Tests for Polish channel detection logic.
/// </summary>
public sealed class PolishChannelDetectionTests
{
    private static readonly CountryDetectionNormalizer Normalizer = CountryDetectionNormalizer.Default;
    private static readonly CountryProfile Poland = CountryBroadcasters.Poland;

    /// <summary>
    /// Tests if a channel matches the Polish country profile.
    /// </summary>
    private static bool IsPolishChannel(string? channelName)
    {
        if (string.IsNullOrWhiteSpace(channelName))
        {
            return false;
        }

        // Tier 1: Check country code prefix using shared extraction (highest priority)
        var countryCode = NormalizationPatterns.ExtractCountryCode(channelName);
        if (string.Equals(countryCode, Poland.CountryCode, System.StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Tier 2: Exact broadcaster match
        var normalizedName = Normalizer.Normalize(channelName);
        if (Poland.BroadcasterSet.Contains(normalizedName))
        {
            return true;
        }

        // Tier 3: Check if any broadcaster is contained in the name
        foreach (var broadcaster in Poland.Broadcasters)
        {
            if (ContainsWordBoundary(normalizedName, broadcaster))
            {
                return true;
            }
        }

        // Tier 4: Regex pattern match (if configured)
        if (Poland.BroadcasterRegex?.IsMatch(channelName) == true)
        {
            return true;
        }

        // Tier 5: Country name indicators in the channel name
        foreach (var countryName in Poland.CountryNames)
        {
            if (channelName.Contains(countryName, System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsWordBoundary(string text, string word)
    {
        var index = text.IndexOf(word, System.StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return false;
        }

        var beforeOk = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
        var afterIndex = index + word.Length;
        var afterOk = afterIndex >= text.Length || !char.IsLetterOrDigit(text[afterIndex]);

        return beforeOk && afterOk;
    }

    /// <summary>
    /// Tests that specific Polish broadcaster names are detected.
    /// </summary>
    /// <param name="channelName">The channel name to test.</param>
    [Theory]
    [InlineData("TVP1")]
    [InlineData("TVP1 HD")]
    [InlineData("PL: TVP1 HD")]
    [InlineData("TVP Info")]
    [InlineData("tvp sport")]
    [InlineData("Polsat")]
    [InlineData("POLSAT SPORT")]
    [InlineData("Polsat News HD")]
    [InlineData("TVN24")]
    [InlineData("TVN24 BIS")]
    [InlineData("Canal+ Polska")]
    [InlineData("Canal+ Sport HD")]
    [InlineData("Fokus TV")]
    [InlineData("Nowa TV")]
    [InlineData("Zoom TV")]
    public void IsPolishChannel_WithPolishBroadcaster_ReturnsTrue(string channelName)
    {
        // Act
        var result = IsPolishChannel(channelName);

        // Assert
        Assert.True(result, $"Expected '{channelName}' to be detected as Polish");
    }

    /// <summary>
    /// Tests that PL prefix patterns are detected correctly.
    /// </summary>
    /// <param name="channelName">The channel name to test.</param>
    [Theory]
    [InlineData("PL: Some Channel")]
    [InlineData("PL | Some Channel")]
    [InlineData("[PL] Some Channel")]
    [InlineData("(PL) Some Channel")]
    public void IsPolishChannel_WithPlPrefix_ReturnsTrue(string channelName)
    {
        // Act
        var result = IsPolishChannel(channelName);

        // Assert
        Assert.True(result, $"Expected '{channelName}' to be detected as Polish");
    }

    /// <summary>
    /// Tests that Polish language indicators with word boundaries are detected.
    /// </summary>
    /// <param name="channelName">The channel name to test.</param>
    [Theory]
    [InlineData("Poland News")]
    [InlineData("Polish TV")]
    [InlineData("TV Polska")]
    [InlineData("Polskie Kino")]
    public void IsPolishChannel_WithPolishIndicator_ReturnsTrue(string channelName)
    {
        // Act
        var result = IsPolishChannel(channelName);

        // Assert
        Assert.True(result, $"Expected '{channelName}' to be detected as Polish");
    }

    /// <summary>
    /// Tests that false positives are not detected as Polish.
    /// </summary>
    /// <param name="channelName">The channel name to test.</param>
    [Theory]
    [InlineData("Replay TV")]
    [InlineData("Playboy Channel")]
    [InlineData("Player HD")]
    [InlineData("Apple TV")]
    [InlineData("Discovery Plus")]
    [InlineData("Simple TV")]
    [InlineData("HBO Max")]
    [InlineData("CNN International")]
    [InlineData("BBC World")]
    [InlineData("ESPN")]
    [InlineData("Fox Sports")]
    public void IsPolishChannel_WithNonPolishChannel_ReturnsFalse(string channelName)
    {
        // Act
        var result = IsPolishChannel(channelName);

        // Assert
        Assert.False(result, $"Expected '{channelName}' NOT to be detected as Polish");
    }

    /// <summary>
    /// Tests edge cases and empty inputs.
    /// </summary>
    /// <param name="channelName">The channel name to test.</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void IsPolishChannel_WithEmptyOrWhitespace_ReturnsFalse(string channelName)
    {
        // Act
        var result = IsPolishChannel(channelName);

        // Assert
        Assert.False(result, $"Expected empty/whitespace to NOT be detected as Polish");
    }

    /// <summary>
    /// Tests UK country detection using the same patterns.
    /// </summary>
    [Theory]
    [InlineData("UK: BBC One", true)]
    [InlineData("[UK] Sky News", true)]
    [InlineData("BBC One HD", true)]
    [InlineData("ITV", true)]
    [InlineData("Channel 4 HD", true)]
    [InlineData("Sky Sports", true)]
    [InlineData("PL: TVP1", false)] // Polish, not UK
    [InlineData("FR: TF1", false)] // French, not UK
    public void CountryDetection_UKProfile_CorrectlyDetects(string channelName, bool expectedResult)
    {
        var uk = CountryBroadcasters.UnitedKingdom;

        // Check country code prefix
        var countryCode = NormalizationPatterns.ExtractCountryCode(channelName);
        if (string.Equals(countryCode, uk.CountryCode, System.StringComparison.OrdinalIgnoreCase))
        {
            Assert.True(expectedResult, $"'{channelName}' has UK prefix but expected false");
            return;
        }

        // Check broadcaster match
        var normalizedName = Normalizer.Normalize(channelName);
        var isBroadcaster =
            uk.BroadcasterSet.Contains(normalizedName) || uk.BroadcasterRegex?.IsMatch(channelName) == true;

        foreach (var broadcaster in uk.Broadcasters)
        {
            if (ContainsWordBoundary(normalizedName, broadcaster))
            {
                isBroadcaster = true;
                break;
            }
        }

        Assert.Equal(expectedResult, isBroadcaster);
    }
}
