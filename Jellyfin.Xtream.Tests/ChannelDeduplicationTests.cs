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
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service;
using Jellyfin.Xtream.Service.ChannelMatching;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Tests for ChannelProviderMap deduplication logic.
/// Verifies that channels from multiple providers are correctly grouped by normalized name.
/// </summary>
public sealed class ChannelDeduplicationTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    private static XtreamProvider CreateProvider(string id, string name) => new() { Id = id, Name = name };

    private static StreamInfo CreateStream(int id, string name) => new() { StreamId = id, Name = name };

    /// <summary>
    /// Tests that identical channel names from different providers are deduplicated.
    /// </summary>
    [Fact]
    public void Build_IdenticalNames_DeduplicatesToSingleChannel()
    {
        var provider1 = CreateProvider("p1", "Provider 1");
        var provider2 = CreateProvider("p2", "Provider 2");

        var streams = new ProviderStreamInfo[]
        {
            new(provider1, CreateStream(1, "TVN HD")),
            new(provider2, CreateStream(2, "TVN HD")),
        };

        var map = ChannelProviderMap.Build(streams);

        Assert.Equal(1, map.ChannelCount);
        var channel = map.Channels.First();
        Assert.Equal(2, channel.ProviderCount);

        _output.WriteLine($"Channel: {channel.DisplayName}");
        _output.WriteLine($"Providers: {string.Join(", ", channel.Providers.Select(p => p.Provider.Name))}");
    }

    /// <summary>
    /// Tests that channels with different quality indicators are deduplicated.
    /// </summary>
    [Theory]
    [InlineData("TVN HD", "TVN FHD")]
    [InlineData("TVN HD", "TVN 4K")]
    [InlineData("TVN SD", "TVN HD")]
    [InlineData("TVN", "TVN HD")]
    [InlineData("Polsat HD", "POLSAT FHD")]
    [InlineData("HBO HD", "HBO 4K")]
    public void Build_DifferentQualityIndicators_DeduplicatesToSingleChannel(string name1, string name2)
    {
        var provider1 = CreateProvider("p1", "Provider 1");
        var provider2 = CreateProvider("p2", "Provider 2");

        var streams = new ProviderStreamInfo[]
        {
            new(provider1, CreateStream(1, name1)),
            new(provider2, CreateStream(2, name2)),
        };

        var map = ChannelProviderMap.Build(streams);

        _output.WriteLine($"Input: '{name1}' and '{name2}'");
        _output.WriteLine(
            $"Normalized: '{ChannelNameNormalizer.Default.Normalize(name1)}' and '{ChannelNameNormalizer.Default.Normalize(name2)}'"
        );
        _output.WriteLine($"Channels: {map.ChannelCount}");

        Assert.Equal(1, map.ChannelCount);
        Assert.Equal(2, map.Channels.First().ProviderCount);
    }

    /// <summary>
    /// Tests that channels with different country prefixes are deduplicated.
    /// </summary>
    [Theory]
    [InlineData("PL: TVN HD", "TVN HD")]
    [InlineData("PL | TVN HD", "TVN")]
    [InlineData("|PL| TVN", "TVN HD")]
    [InlineData("[PL] TVN", "TVN HD")]
    [InlineData("(PL) TVN HD", "TVN")]
    [InlineData("PL- TVN HD", "TVN")]
    [InlineData("123 PL: TVN HD", "TVN HD")]
    public void Build_DifferentCountryPrefixes_DeduplicatesToSingleChannel(string name1, string name2)
    {
        var provider1 = CreateProvider("p1", "Provider 1");
        var provider2 = CreateProvider("p2", "Provider 2");

        var streams = new ProviderStreamInfo[]
        {
            new(provider1, CreateStream(1, name1)),
            new(provider2, CreateStream(2, name2)),
        };

        var map = ChannelProviderMap.Build(streams);

        _output.WriteLine($"Input: '{name1}' and '{name2}'");
        _output.WriteLine(
            $"Normalized: '{ChannelNameNormalizer.Default.Normalize(name1)}' and '{ChannelNameNormalizer.Default.Normalize(name2)}'"
        );
        _output.WriteLine($"Channels: {map.ChannelCount}");

        Assert.Equal(1, map.ChannelCount);
        Assert.Equal(2, map.Channels.First().ProviderCount);
    }

    /// <summary>
    /// Tests that different channels are NOT deduplicated.
    /// </summary>
    [Theory]
    [InlineData("TVN", "TVN24")]
    [InlineData("TVN", "TVN7")]
    [InlineData("Polsat", "Polsat Sport")]
    [InlineData("HBO", "HBO2")]
    [InlineData("Canal+", "Canal+ Sport")]
    [InlineData("Eurosport", "Eurosport 2")]
    [InlineData("Discovery", "Discovery Science")]
    public void Build_DifferentChannels_NotDeduplicated(string name1, string name2)
    {
        var provider1 = CreateProvider("p1", "Provider 1");
        var provider2 = CreateProvider("p2", "Provider 2");

        var streams = new ProviderStreamInfo[]
        {
            new(provider1, CreateStream(1, name1)),
            new(provider2, CreateStream(2, name2)),
        };

        var map = ChannelProviderMap.Build(streams);

        _output.WriteLine($"Input: '{name1}' and '{name2}'");
        _output.WriteLine(
            $"Normalized: '{ChannelNameNormalizer.Default.Normalize(name1)}' and '{ChannelNameNormalizer.Default.Normalize(name2)}'"
        );
        _output.WriteLine($"Channels: {map.ChannelCount}");

        Assert.Equal(2, map.ChannelCount);
    }

    /// <summary>
    /// Tests that channels with diacritics are properly normalized and deduplicated.
    /// </summary>
    [Theory]
    [InlineData("PL: Wiadomości", "Wiadomosci HD")]
    [InlineData("Télévision Française", "Television Francaise HD")]
    [InlineData("Fußball Bundesliga", "Fussball Bundesliga HD")]
    [InlineData("PL: Żywiec Sport", "Zywiec Sport HD")]
    public void Build_DiacriticsNormalized_DeduplicatesToSingleChannel(string name1, string name2)
    {
        var provider1 = CreateProvider("p1", "Provider 1");
        var provider2 = CreateProvider("p2", "Provider 2");

        var streams = new ProviderStreamInfo[]
        {
            new(provider1, CreateStream(1, name1)),
            new(provider2, CreateStream(2, name2)),
        };

        var map = ChannelProviderMap.Build(streams);

        _output.WriteLine($"Input: '{name1}' and '{name2}'");
        _output.WriteLine(
            $"Normalized: '{ChannelNameNormalizer.Default.Normalize(name1)}' and '{ChannelNameNormalizer.Default.Normalize(name2)}'"
        );
        _output.WriteLine($"Channels: {map.ChannelCount}");

        Assert.Equal(1, map.ChannelCount);
        Assert.Equal(2, map.Channels.First().ProviderCount);
    }

    /// <summary>
    /// Tests that providers are sorted by quality (higher quality first).
    /// </summary>
    [Fact]
    public void Build_ProvidersAreSortedByQuality()
    {
        var provider1 = CreateProvider("p1", "Provider 1");
        var provider2 = CreateProvider("p2", "Provider 2");
        var provider3 = CreateProvider("p3", "Provider 3");

        var streams = new ProviderStreamInfo[]
        {
            new(provider1, CreateStream(1, "TVN SD")),
            new(provider2, CreateStream(2, "TVN 4K")),
            new(provider3, CreateStream(3, "TVN HD")),
        };

        var map = ChannelProviderMap.Build(streams);

        Assert.Equal(1, map.ChannelCount);
        var channel = map.Channels.First();

        _output.WriteLine($"Channel: {channel.DisplayName}");
        foreach (var p in channel.Providers)
        {
            _output.WriteLine($"  Provider: {p.Provider.Name}, Stream: {p.Stream.Name}");
        }

        // 4K should be first (highest quality)
        Assert.Equal("TVN 4K", channel.Providers[0].Stream.Name);
        // HD should be second
        Assert.Equal("TVN HD", channel.Providers[1].Stream.Name);
        // SD should be last
        Assert.Equal("TVN SD", channel.Providers[2].Stream.Name);
    }

    /// <summary>
    /// Tests that providers are sorted by quality when no failover service is present.
    /// </summary>
    [Fact]
    public void Build_WithoutResilienceService_SortsByQuality()
    {
        var provider1 = CreateProvider("p1", "Provider 1");
        var provider2 = CreateProvider("p2", "Provider 2");

        var streams = new ProviderStreamInfo[]
        {
            // Provider 1 has 4K quality
            new(provider1, CreateStream(1, "TVN 4K")),
            // Provider 2 has HD quality (lower)
            new(provider2, CreateStream(2, "TVN HD")),
        };

        // Without resilience service - 4K wins (quality only)
        var map = ChannelProviderMap.Build(streams);
        var channel = map.Channels.First();
        Assert.Equal("TVN 4K", channel.Providers[0].Stream.Name);

        _output.WriteLine("Without resilience service:");
        _output.WriteLine($"  First: {channel.Providers[0].Stream.Name} ({channel.Providers[0].Provider.Name})");
    }

    /// <summary>
    /// Tests that empty/whitespace-only names are skipped.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("HD")] // Only quality indicator
    [InlineData("4K")] // Only quality indicator
    [InlineData("FHD")] // Only quality indicator
    public void Build_EmptyOrOnlyQualityNames_AreSkipped(string name)
    {
        var provider = CreateProvider("p1", "Provider 1");

        var streams = new ProviderStreamInfo[] { new(provider, CreateStream(1, name)) };

        var map = ChannelProviderMap.Build(streams);

        _output.WriteLine($"Input: '{name}'");
        _output.WriteLine($"Normalized: '{ChannelNameNormalizer.Default.Normalize(name)}'");
        _output.WriteLine($"Channels: {map.ChannelCount}, Skipped: {map.SkippedCount}");

        Assert.Equal(0, map.ChannelCount);
        Assert.Equal(1, map.SkippedCount);
    }

    /// <summary>
    /// Tests that GetByGuid returns the correct channel.
    /// </summary>
    [Fact]
    public void GetByGuid_ReturnsCorrectChannel()
    {
        var provider = CreateProvider("p1", "Provider 1");
        var stream = CreateStream(123, "TVN HD");

        var streams = new ProviderStreamInfo[] { new(provider, stream) };

        var map = ChannelProviderMap.Build(streams);

        var guid = StreamService.ToProviderGuid(StreamService.LiveTvPrefix, provider, 123);
        var channel = map.GetByGuid(guid);

        Assert.NotNull(channel);
        Assert.Equal("TVN", channel.DisplayName);
    }

    /// <summary>
    /// Tests that GetNextProvider returns providers in order, skipping failed ones.
    /// </summary>
    [Fact]
    public void GetNextProvider_SkipsFailedProviders()
    {
        var provider1 = CreateProvider("p1", "Provider 1");
        var provider2 = CreateProvider("p2", "Provider 2");
        var provider3 = CreateProvider("p3", "Provider 3");

        var streams = new ProviderStreamInfo[]
        {
            new(provider1, CreateStream(1, "TVN 4K")),
            new(provider2, CreateStream(2, "TVN HD")),
            new(provider3, CreateStream(3, "TVN SD")),
        };

        var map = ChannelProviderMap.Build(streams);
        var guid = StreamService.ToProviderGuid(StreamService.LiveTvPrefix, provider1, 1);

        // No failed providers - should return first (4K)
        var next1 = map.GetNextProvider(guid, new HashSet<string>(StringComparer.Ordinal));
        Assert.NotNull(next1);
        Assert.Equal("p1", next1.Provider.Id);

        // First provider failed - should return second (HD)
        var next2 = map.GetNextProvider(guid, new HashSet<string>(StringComparer.Ordinal) { "p1" });
        Assert.NotNull(next2);
        Assert.Equal("p2", next2.Provider.Id);

        // First two providers failed - should return third (SD)
        var next3 = map.GetNextProvider(guid, new HashSet<string>(StringComparer.Ordinal) { "p1", "p2" });
        Assert.NotNull(next3);
        Assert.Equal("p3", next3.Provider.Id);

        // All providers failed - should return null
        var next4 = map.GetNextProvider(guid, new HashSet<string>(StringComparer.Ordinal) { "p1", "p2", "p3" });
        Assert.Null(next4);
    }

    /// <summary>
    /// Tests real-world scenario with multiple providers and various naming conventions.
    /// </summary>
    [Fact]
    public void Build_RealWorldScenario_DeduplicatesCorrectly()
    {
        var provider1 = CreateProvider("p1", "Provider A");
        var provider2 = CreateProvider("p2", "Provider B");
        var provider3 = CreateProvider("p3", "Provider C");

        var streams = new ProviderStreamInfo[]
        {
            // TVN from all providers with different naming
            new(provider1, CreateStream(1, "PL | TVN HD")),
            new(provider2, CreateStream(101, "PL: TVN FHD")),
            new(provider3, CreateStream(201, "|PL| TVN 4K")),
            // Polsat from two providers
            new(provider1, CreateStream(2, "PL | Polsat HD")),
            new(provider2, CreateStream(102, "Polsat FHD")),
            // HBO from one provider
            new(provider1, CreateStream(3, "PL | HBO HD")),
            // Discovery from all providers
            new(provider1, CreateStream(4, "PL | Discovery Channel HD")),
            new(provider2, CreateStream(104, "Discovery Channel FHD")),
            new(provider3, CreateStream(204, "[PL] Discovery Channel 4K")),
        };

        var map = ChannelProviderMap.Build(streams);

        _output.WriteLine($"Total channels: {map.ChannelCount}");
        foreach (var channel in map.Channels)
        {
            _output.WriteLine($"\n{channel.DisplayName} ({channel.ProviderCount} providers):");
            foreach (var p in channel.Providers)
            {
                _output.WriteLine($"  - {p.Provider.Name}: {p.Stream.Name}");
            }
        }

        // Should have 4 unique channels: TVN, Polsat, HBO, Discovery
        Assert.Equal(4, map.ChannelCount);

        // TVN should have 3 providers
        var tvn = map.Channels.First(c => c.NormalizedName == "TVN");
        Assert.Equal(3, tvn.ProviderCount);

        // Polsat should have 2 providers
        var polsat = map.Channels.First(c => c.NormalizedName == "POLSAT");
        Assert.Equal(2, polsat.ProviderCount);

        // HBO should have 1 provider
        var hbo = map.Channels.First(c => c.NormalizedName == "HBO");
        Assert.Equal(1, hbo.ProviderCount);

        // Discovery Channel should have 3 providers (normalized to DISCOVERY)
        var discovery = map.Channels.First(c => c.NormalizedName == "DISCOVERY");
        Assert.Equal(3, discovery.ProviderCount);
    }

    /// <summary>
    /// Tests that the display name is cleaned properly.
    /// </summary>
    [Theory]
    [InlineData("PL | TVN HD", "TVN")]
    [InlineData("123 PL: Polsat Sport FHD", "Polsat Sport")]
    [InlineData("|UK| BBC One 4K", "BBC One")]
    [InlineData("[FR] Canal+ HD", "Canal+")]
    public void Build_DisplayNameIsCleanedProperly(string inputName, string expectedDisplayName)
    {
        var provider = CreateProvider("p1", "Provider 1");
        var streams = new ProviderStreamInfo[] { new(provider, CreateStream(1, inputName)) };

        var map = ChannelProviderMap.Build(streams);
        var channel = map.Channels.First();

        _output.WriteLine($"Input: '{inputName}'");
        _output.WriteLine($"Display name: '{channel.DisplayName}'");

        Assert.Equal(expectedDisplayName, channel.DisplayName);
    }

    /// <summary>
    /// Tests that deduplication uses the same normalization as CopyChannelSelections.
    /// This ensures consistency between the two features.
    /// </summary>
    [Fact]
    public void Build_UsesConsistentNormalizationWithChannelMatcher()
    {
        var provider1 = CreateProvider("p1", "Provider 1");
        var provider2 = CreateProvider("p2", "Provider 2");

        // These channel names should normalize identically
        var testPairs = new[]
        {
            ("PL: TVN HD", "TVN"),
            ("PL | Polsat Sport HD", "Polsat Sport FHD"),
            ("123 PL: Discovery Channel 4K", "[PL] Discovery Channel HD"),
            ("(FR) Canal+ Sport", "FR- CANAL+ SPORT HD"),
        };

        foreach (var (name1, name2) in testPairs)
        {
            // Verify ChannelNameNormalizer produces the same result
            var normalized1 = ChannelNameNormalizer.Default.Normalize(name1);
            var normalized2 = ChannelNameNormalizer.Default.Normalize(name2);

            _output.WriteLine($"'{name1}' -> '{normalized1}'");
            _output.WriteLine($"'{name2}' -> '{normalized2}'");
            _output.WriteLine($"Match: {normalized1 == normalized2}");
            _output.WriteLine("");

            Assert.Equal(normalized1, normalized2);

            // Verify ChannelProviderMap deduplicates them
            var streams = new ProviderStreamInfo[]
            {
                new(provider1, CreateStream(1, name1)),
                new(provider2, CreateStream(2, name2)),
            };

            var map = ChannelProviderMap.Build(streams);
            Assert.Equal(1, map.ChannelCount);
            Assert.Equal(2, map.Channels.First().ProviderCount);
        }
    }

    /// <summary>
    /// Tests parallel processing path by providing enough streams.
    /// </summary>
    [Fact]
    public void Build_LargeDataset_UsesParallelProcessing()
    {
        var provider = CreateProvider("p1", "Provider 1");

        // Create 600 streams (above the ParallelThreshold of 500)
        // Use unique names that won't normalize to duplicates
        var streams = Enumerable
            .Range(1, 600)
            .Select(i => new ProviderStreamInfo(provider, CreateStream(i, $"UniqueChannel{i:D4}")))
            .ToList();

        var map = ChannelProviderMap.Build(streams);

        // Should have all 600 unique channels
        Assert.Equal(600, map.ChannelCount);
        Assert.Equal(0, map.SkippedCount);
    }
}
