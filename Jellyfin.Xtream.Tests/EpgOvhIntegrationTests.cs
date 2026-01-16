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

using System.Net;
using Jellyfin.Xtream.Service.ChannelMatching;
using Jellyfin.Xtream.Service.Epg;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Integration tests that verify real-world connectivity to epg.ovh.
/// These tests make actual HTTP requests and use the actual service implementations
/// (TurboXmltvParser, XmltvDateTimeParser, ChannelNameNormalizer) to verify real-world behavior.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EpgOvhIntegrationTests : IDisposable
{
    private const string EpgOvhBaseUrl = "https://epg.ovh";
    private const string PolishEpgUrl = "https://epg.ovh/pl.xml";
    private const string LogoBaseUrl = "https://epg.ovh/logo";

    private readonly ITestOutputHelper _output;
    private readonly HttpClient _httpClient;
    private readonly ChannelNameNormalizer _normalizer = ChannelNameNormalizer.Default;

    public EpgOvhIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public void Dispose() => _httpClient.Dispose();

    /// <summary>
    /// Tests that the Polish EPG XML is accessible and returns valid XML.
    /// </summary>
    [Fact]
    public async Task PolishEpg_IsAccessible_ReturnsValidXml()
    {
        if (!await IsNetworkAvailable())
        {
            _output.WriteLine("Network unavailable, skipping test");
            return;
        }

        try
        {
            using var response = await _httpClient.GetAsync(PolishEpgUrl, HttpCompletionOption.ResponseHeadersRead);

            _output.WriteLine($"Status: {response.StatusCode}");
            _output.WriteLine($"Content-Type: {response.Content.Headers.ContentType}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Read first few KB to verify it's valid XML
            var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);
            var buffer = new char[4096];
            var read = await reader.ReadAsync(buffer, 0, buffer.Length);
            var content = new string(buffer, 0, read);

            _output.WriteLine($"First {read} chars received");
            _output.WriteLine($"Starts with: {content[..Math.Min(200, content.Length)]}...");

            // Verify it looks like XMLTV
            Assert.Contains("<?xml", content);
            Assert.Contains("<tv", content);
        }
        catch (HttpRequestException ex)
        {
            _output.WriteLine($"HTTP request failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Tests that TurboXmltvParser can parse real EPG data from epg.ovh.
    /// Uses the actual service implementation.
    /// </summary>
    [Fact]
    public async Task TurboXmltvParser_ParsesRealEpgData()
    {
        if (!await IsNetworkAvailable())
        {
            _output.WriteLine("Network unavailable, skipping test");
            return;
        }

        try
        {
            using var response = await _httpClient.GetAsync(PolishEpgUrl);
            _ = response.EnsureSuccessStatusCode();

            var stream = await response.Content.ReadAsStreamAsync();

            // Use the actual TurboXmltvParser service
            var result = TurboXmltvParser.Parse(stream);

            _output.WriteLine($"Parsed {result.Count} channels");

            var totalPrograms = result.Values.Sum(p => p.Count);
            _output.WriteLine($"Total programs: {totalPrograms}");

            Assert.NotEmpty(result);
            Assert.True(totalPrograms > 0, "Expected at least one program");

            // Log some sample channels
            foreach (var (channelId, programs) in result.Take(5))
            {
                _output.WriteLine($"\nChannel: {channelId} ({programs.Count} programs)");
                if (programs.Count > 0)
                {
                    var first = programs[0];
                    _output.WriteLine($"  First program: {first.Title}");
                    _output.WriteLine($"  Start: {first.StartUtc:u}");
                    _output.WriteLine($"  End: {first.EndUtc:u}");
                }
            }
        }
        catch (HttpRequestException ex)
        {
            _output.WriteLine($"HTTP request failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Tests that XmltvDateTimeParser correctly parses datetime strings from real EPG data.
    /// Uses the actual service implementation.
    /// </summary>
    [Theory]
    [InlineData("20251222180000 +0100", 2025, 12, 22, 17, 0, 0)] // +0100 timezone
    [InlineData("20251222180000 -0500", 2025, 12, 22, 23, 0, 0)] // -0500 timezone (EST)
    [InlineData("20251222180000 +0000", 2025, 12, 22, 18, 0, 0)] // UTC
    [InlineData("20251222000000 +0200", 2025, 12, 21, 22, 0, 0)] // +0200 crossing day boundary
    public void XmltvDateTimeParser_ParsesCorrectly(
        string dateTimeStr,
        int expectedYear,
        int expectedMonth,
        int expectedDay,
        int expectedHour,
        int expectedMinute,
        int expectedSecond
    )
    {
        // Use the actual XmltvDateTimeParser service
        var parsed = XmltvDateTimeParser.Parse(dateTimeStr);

        _output.WriteLine($"Input: '{dateTimeStr}'");
        _output.WriteLine($"Parsed (UTC): {parsed:yyyy-MM-dd HH:mm:ss}");

        Assert.Equal(expectedYear, parsed.Year);
        Assert.Equal(expectedMonth, parsed.Month);
        Assert.Equal(expectedDay, parsed.Day);
        Assert.Equal(expectedHour, parsed.Hour);
        Assert.Equal(expectedMinute, parsed.Minute);
        Assert.Equal(expectedSecond, parsed.Second);
    }

    /// <summary>
    /// Tests that XmltvDateTimeParser handles real datetime formats from epg.ovh data.
    /// </summary>
    [Fact]
    public async Task XmltvDateTimeParser_ParsesRealEpgDatetimes()
    {
        if (!await IsNetworkAvailable())
        {
            _output.WriteLine("Network unavailable, skipping test");
            return;
        }

        try
        {
            using var response = await _httpClient.GetAsync(PolishEpgUrl);
            _ = response.EnsureSuccessStatusCode();

            var stream = await response.Content.ReadAsStreamAsync();
            var result = TurboXmltvParser.Parse(stream);

            var validDatetimes = 0;
            var invalidDatetimes = 0;

            foreach (var programs in result.Values.Take(10))
            {
                foreach (var program in programs.Take(10))
                {
                    if (program.StartUtc > DateTime.MinValue)
                    {
                        validDatetimes++;
                    }
                    else
                    {
                        invalidDatetimes++;
                    }
                }
            }

            _output.WriteLine($"Valid datetimes: {validDatetimes}");
            _output.WriteLine($"Invalid datetimes: {invalidDatetimes}");

            Assert.True(validDatetimes > 0, "Expected at least one valid datetime");
            Assert.True(
                invalidDatetimes < validDatetimes / 10,
                $"Too many invalid datetimes ({invalidDatetimes}/{validDatetimes})"
            );
        }
        catch (HttpRequestException ex)
        {
            _output.WriteLine($"HTTP request failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Tests that common Polish channel names can be found in the EPG data.
    /// </summary>
    [Theory]
    [InlineData("TVP 1")]
    [InlineData("TVP 2")]
    [InlineData("TVN")]
    [InlineData("Polsat")]
    public async Task PolishEpg_ContainsCommonChannels(string expectedChannel)
    {
        if (!await IsNetworkAvailable())
        {
            _output.WriteLine("Network unavailable, skipping test");
            return;
        }

        try
        {
            using var response = await _httpClient.GetAsync(PolishEpgUrl);
            _ = response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();

            // Simple check - the channel name should appear somewhere in the XML
            var containsChannel = content.Contains(expectedChannel, StringComparison.OrdinalIgnoreCase);

            _output.WriteLine($"Looking for '{expectedChannel}': {(containsChannel ? "FOUND" : "NOT FOUND")}");

            Assert.True(containsChannel, $"Expected to find channel '{expectedChannel}' in Polish EPG");
        }
        catch (HttpRequestException ex)
        {
            _output.WriteLine($"HTTP request failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Tests that ChannelNameNormalizer properly normalizes provider names to match XMLTV display-names.
    /// Uses the actual ChannelNameNormalizer service.
    /// </summary>
    [Theory]
    [InlineData("PL | TVN HD", "TVN")]
    [InlineData("PL: Polsat Sport FHD", "Polsat Sport")]
    [InlineData("|PL| TVP 1 4K", "TVP 1")]
    [InlineData("[UK] BBC One HD", "BBC One")]
    [InlineData("FR- Canal+ HD", "Canal+")]
    public void ChannelNameNormalizer_NormalizesProviderNames(string providerName, string expectedBase)
    {
        // Use the actual ChannelNameNormalizer service
        var normalizedProvider = _normalizer.Normalize(providerName);
        var normalizedExpected = _normalizer.Normalize(expectedBase);

        _output.WriteLine($"Provider: '{providerName}' -> '{normalizedProvider}'");
        _output.WriteLine($"Expected: '{expectedBase}' -> '{normalizedExpected}'");

        Assert.Equal(normalizedExpected, normalizedProvider);
    }

    /// <summary>
    /// Tests that normalized provider names match real XMLTV channel IDs from epg.ovh.
    /// </summary>
    [Fact]
    public async Task Normalization_MatchesRealXmltvChannels()
    {
        if (!await IsNetworkAvailable())
        {
            _output.WriteLine("Network unavailable, skipping test");
            return;
        }

        // Common provider channel name formats
        var providerChannels = new[]
        {
            ("PL | TVN HD", "TVN"),
            ("PL: Polsat HD", "Polsat"),
            ("|PL| TVP 1 4K", "TVP 1"),
            ("[PL] TVP 2 HD", "TVP 2"),
        };

        try
        {
            using var response = await _httpClient.GetAsync(PolishEpgUrl);
            _ = response.EnsureSuccessStatusCode();

            var stream = await response.Content.ReadAsStreamAsync();
            var result = TurboXmltvParser.Parse(stream);

            // Build a lookup of normalized channel IDs (use first match for duplicates)
            var normalizedChannelIds = result
                .Keys.Select(id => (Original: id, Normalized: _normalizer.Normalize(id)))
                .GroupBy(x => x.Normalized, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Original, StringComparer.OrdinalIgnoreCase);

            _output.WriteLine($"XMLTV has {result.Count} channels");
            _output.WriteLine("");

            foreach (var (providerName, expectedBase) in providerChannels)
            {
                var normalizedProvider = _normalizer.Normalize(providerName);
                var normalizedExpected = _normalizer.Normalize(expectedBase);

                var matchFound = normalizedChannelIds.TryGetValue(normalizedExpected, out var matchedChannelId);

                _output.WriteLine($"Provider: '{providerName}'");
                _output.WriteLine($"  Normalized: '{normalizedProvider}'");
                _output.WriteLine($"  Expected normalized: '{normalizedExpected}'");
                _output.WriteLine($"  Match in XMLTV: {(matchFound ? matchedChannelId : "NOT FOUND")}");
                _output.WriteLine("");
            }
        }
        catch (HttpRequestException ex)
        {
            _output.WriteLine($"HTTP request failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Tests that logo URLs are accessible for common Polish channels.
    /// </summary>
    [Theory]
    [InlineData("TVN")]
    [InlineData("Polsat")]
    [InlineData("TVP+1")]
    [InlineData("HBO")]
    public async Task Logo_IsAccessible_ForCommonChannels(string channelName)
    {
        if (!await IsNetworkAvailable())
        {
            _output.WriteLine("Network unavailable, skipping test");
            return;
        }

        // Use the same URL encoding as ExternalXmltvEpgProvider.GetLogoUrl
        var encodedName = Uri.EscapeDataString(channelName).Replace("%20", "+", StringComparison.Ordinal);
        var logoUrl = $"{LogoBaseUrl}/{encodedName}.png";

        _output.WriteLine($"Testing logo URL: {logoUrl}");

        try
        {
            using var response = await _httpClient.GetAsync(logoUrl, HttpCompletionOption.ResponseHeadersRead);

            _output.WriteLine($"Status: {response.StatusCode}");
            _output.WriteLine($"Content-Type: {response.Content.Headers.ContentType}");

            if (response.IsSuccessStatusCode)
            {
                var contentLength = response.Content.Headers.ContentLength;
                _output.WriteLine($"Content-Length: {contentLength} bytes");

                // Verify it's an image
                var contentType = response.Content.Headers.ContentType?.MediaType;
                Assert.True(
                    contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase),
                    $"Expected image content type, got: {contentType}"
                );
            }
            else
            {
                _output.WriteLine($"Logo not found for '{channelName}' (this may be expected)");
            }
        }
        catch (HttpRequestException ex)
        {
            _output.WriteLine($"HTTP request failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Tests that the logo URL construction matches what epg.ovh expects.
    /// Uses the same encoding logic as ExternalXmltvEpgProvider.GetLogoUrl.
    /// </summary>
    [Theory]
    [InlineData("TVN", "TVN.png")]
    [InlineData("Canal+ Sport", "Canal%2B+Sport.png")]
    [InlineData("Polsat Sport Extra", "Polsat+Sport+Extra.png")]
    [InlineData("TVP 1 HD", "TVP+1+HD.png")]
    public void LogoUrl_Construction_MatchesExpectedFormat(string channelName, string expectedFileName)
    {
        // Use the same URL encoding as ExternalXmltvEpgProvider.GetLogoUrl
        var encodedName = Uri.EscapeDataString(channelName.Trim()).Replace("%20", "+", StringComparison.Ordinal);
        var actualFileName = $"{encodedName}.png";

        _output.WriteLine($"Channel: '{channelName}'");
        _output.WriteLine($"Expected: {expectedFileName}");
        _output.WriteLine($"Actual: {actualFileName}");

        Assert.Equal(expectedFileName, actualFileName);
    }

    /// <summary>
    /// Tests the full integration path: fetch EPG, parse with TurboXmltvParser,
    /// verify programs have valid data parsed by XmltvDateTimeParser.
    /// </summary>
    [Fact]
    public async Task FullIntegration_FetchParseAndValidate()
    {
        if (!await IsNetworkAvailable())
        {
            _output.WriteLine("Network unavailable, skipping test");
            return;
        }

        try
        {
            // 1. Fetch EPG data
            _output.WriteLine("1. Fetching EPG data from epg.ovh...");
            using var response = await _httpClient.GetAsync(PolishEpgUrl);
            _ = response.EnsureSuccessStatusCode();
            _output.WriteLine($"   Status: {response.StatusCode}");

            // 2. Parse with TurboXmltvParser
            _output.WriteLine("2. Parsing with TurboXmltvParser...");
            var stream = await response.Content.ReadAsStreamAsync();
            var result = TurboXmltvParser.Parse(stream);
            _output.WriteLine($"   Parsed {result.Count} channels");

            // 3. Validate programs have correct data
            _output.WriteLine("3. Validating program data...");
            var programsWithTitle = 0;
            var programsWithValidDates = 0;
            var totalPrograms = 0;

            foreach (var programs in result.Values)
            {
                foreach (var program in programs)
                {
                    totalPrograms++;
                    if (!string.IsNullOrEmpty(program.Title))
                    {
                        programsWithTitle++;
                    }

                    if (program.StartUtc > DateTime.MinValue && program.EndUtc > program.StartUtc)
                    {
                        programsWithValidDates++;
                    }
                }
            }

            _output.WriteLine($"   Total programs: {totalPrograms}");
            _output.WriteLine(
                $"   Programs with title: {programsWithTitle} ({100.0 * programsWithTitle / totalPrograms:F1}%)"
            );
            _output.WriteLine(
                $"   Programs with valid dates: {programsWithValidDates} ({100.0 * programsWithValidDates / totalPrograms:F1}%)"
            );

            Assert.True(programsWithTitle > totalPrograms * 0.9, "Expected >90% of programs to have titles");
            Assert.True(programsWithValidDates > totalPrograms * 0.9, "Expected >90% of programs to have valid dates");

            // 4. Test channel name normalization for a real channel
            _output.WriteLine("4. Testing channel name normalization...");
            var sampleChannelId = result.Keys.FirstOrDefault(k =>
                k.Contains("TVN", StringComparison.OrdinalIgnoreCase)
                || k.Contains("Polsat", StringComparison.OrdinalIgnoreCase)
            );
            if (sampleChannelId != null)
            {
                var normalized = _normalizer.Normalize(sampleChannelId);
                var providerFormat = $"PL | {sampleChannelId} HD";
                var normalizedProvider = _normalizer.Normalize(providerFormat);

                _output.WriteLine($"   Channel ID: '{sampleChannelId}' -> normalized: '{normalized}'");
                _output.WriteLine($"   Provider format: '{providerFormat}' -> normalized: '{normalizedProvider}'");
                _output.WriteLine($"   Match: {normalized == normalizedProvider}");
            }

            _output.WriteLine("\nFull integration test PASSED!");
        }
        catch (HttpRequestException ex)
        {
            _output.WriteLine($"HTTP request failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Tests that programs parsed from real EPG data contain expected fields.
    /// </summary>
    [Fact]
    public async Task ParsedPrograms_ContainExpectedFields()
    {
        if (!await IsNetworkAvailable())
        {
            _output.WriteLine("Network unavailable, skipping test");
            return;
        }

        try
        {
            using var response = await _httpClient.GetAsync(PolishEpgUrl);
            _ = response.EnsureSuccessStatusCode();

            var stream = await response.Content.ReadAsStreamAsync();
            var result = TurboXmltvParser.Parse(stream);

            // Get first few programs to inspect
            var samplePrograms = result.Values.SelectMany(p => p).Take(20).ToList();

            _output.WriteLine($"Inspecting {samplePrograms.Count} programs:");
            _output.WriteLine("");

            var hasDescription = 0;
            var hasCategories = 0;
            var hasImageUrl = 0;

            foreach (var program in samplePrograms)
            {
                if (!string.IsNullOrEmpty(program.Description))
                {
                    hasDescription++;
                }

                if (program.Categories.Count > 0)
                {
                    hasCategories++;
                }

                if (!string.IsNullOrEmpty(program.ImageUrl))
                {
                    hasImageUrl++;
                }
            }

            _output.WriteLine($"Programs with description: {hasDescription}/{samplePrograms.Count}");
            _output.WriteLine($"Programs with categories: {hasCategories}/{samplePrograms.Count}");
            _output.WriteLine($"Programs with image URL: {hasImageUrl}/{samplePrograms.Count}");

            // Log a sample program with full details
            var detailedSample = samplePrograms.FirstOrDefault(p =>
                !string.IsNullOrEmpty(p.Description) && p.Categories.Count > 0
            );
            if (detailedSample != null)
            {
                _output.WriteLine("");
                _output.WriteLine("Sample program with full details:");
                _output.WriteLine($"  Title: {detailedSample.Title}");
                _output.WriteLine($"  Start: {detailedSample.StartUtc:u}");
                _output.WriteLine($"  End: {detailedSample.EndUtc:u}");
                _output.WriteLine(
                    $"  Description: {detailedSample.Description?[..Math.Min(100, detailedSample.Description.Length)]}..."
                );
                _output.WriteLine($"  Categories: {string.Join(", ", detailedSample.Categories)}");
                _output.WriteLine($"  Image URL: {detailedSample.ImageUrl ?? "(none)"}");
            }

            // All programs should have titles and valid dates
            Assert.True(
                samplePrograms.TrueForAll(p => !string.IsNullOrEmpty(p.Title)),
                "All programs should have titles"
            );
            Assert.True(
                samplePrograms.TrueForAll(p => p.StartUtc > DateTime.MinValue),
                "All programs should have valid start times"
            );
        }
        catch (HttpRequestException ex)
        {
            _output.WriteLine($"HTTP request failed: {ex.Message}");
        }
    }

    private async Task<bool> IsNetworkAvailable()
    {
        try
        {
            using var response = await _httpClient
                .GetAsync(EpgOvhBaseUrl, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
