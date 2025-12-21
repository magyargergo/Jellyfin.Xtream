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

using Jellyfin.Xtream.Service.Discovery;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Tests for Polish channel detection logic in ProviderTester.
/// </summary>
public class PolishChannelDetectionTests
{
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
    [InlineData("TV Republika")]
    [InlineData("Kino Polska")]
    [InlineData("4Fun TV")]
    [InlineData("Eska TV")]
    [InlineData("Polo TV")]
    [InlineData("Eleven Sports Polska")]
    public void IsPolishChannel_WithPolishBroadcaster_ReturnsTrue(string channelName)
    {
        // Arrange - use reflection to call private method
        var type = typeof(ProviderTester);
        var method = type.GetMethod(
            "IsPolishChannel",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
        );
        Assert.NotNull(method);

        // Act
        var result = (bool)method.Invoke(null, [channelName])!;

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
    [InlineData("Some Channel | PL")]
    [InlineData("[PL] Some Channel")]
    [InlineData("(PL) Some Channel")]
    [InlineData("PL- Some Channel")]
    [InlineData("123 PL: Some Channel")]
    [InlineData("45. PL | Some Channel")]
    public void IsPolishChannel_WithPlPrefix_ReturnsTrue(string channelName)
    {
        // Arrange
        var type = typeof(ProviderTester);
        var method = type.GetMethod(
            "IsPolishChannel",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
        );
        Assert.NotNull(method);

        // Act
        var result = (bool)method.Invoke(null, [channelName])!;

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
        // Arrange
        var type = typeof(ProviderTester);
        var method = type.GetMethod(
            "IsPolishChannel",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
        );
        Assert.NotNull(method);

        // Act
        var result = (bool)method.Invoke(null, [channelName])!;

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
        // Arrange
        var type = typeof(ProviderTester);
        var method = type.GetMethod(
            "IsPolishChannel",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
        );
        Assert.NotNull(method);

        // Act
        var result = (bool)method.Invoke(null, [channelName])!;

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
    [InlineData(null)]
    public void IsPolishChannel_WithEmptyOrNull_ReturnsFalse(string? channelName)
    {
        // Arrange
        var type = typeof(ProviderTester);
        var method = type.GetMethod(
            "IsPolishChannel",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
        );
        Assert.NotNull(method);

        // Act
        var result = (bool)method.Invoke(null, [channelName])!;

        // Assert
        Assert.False(result, $"Expected empty/null to NOT be detected as Polish");
    }
}
