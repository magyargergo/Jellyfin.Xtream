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
using Jellyfin.Xtream.Service.Discovery;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Integration tests for the WebCredentialSource against real URLs.
/// These tests are skipped by default since they require network access.
/// Set the environment variable RUN_INTEGRATION_TESTS=true to run them.
/// </summary>
public sealed partial class WebCredentialSourceIntegrationTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;
    private readonly bool _runIntegrationTests = Environment.GetEnvironmentVariable("RUN_INTEGRATION_TESTS") == "true";

    /// <summary>
    /// Tests discovering credentials from the real tvappapk.com website.
    /// </summary>
    [Fact]
    public async Task DiscoverAsync_RealWebsite_FindsCredentials()
    {
        if (!_runIntegrationTests)
        {
            _output.WriteLine("Skipping integration test. Set RUN_INTEGRATION_TESTS=true to run.");
            return;
        }

        // Arrange
        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(30);

        var parser = new CredentialParser();
        var logger = NullLogger<WebCredentialSource>.Instance;
        using var source = new WebCredentialSource(httpClient, parser, logger);

        var progress = new Progress<DiscoveryProgress>(p =>
        {
            _output.WriteLine($"[{p.Phase}] {p.CurrentItem}/{p.TotalItems} - {p.Message}");
            _output.WriteLine(
                $"  Credentials: {p.CredentialsFound}, Working: {p.WorkingProviders}, +EPG: {p.WorkingWithEpg}, +Polish: {p.FullyWorking}"
            );
        });

        // Act - search last week
        var startDate = DateTime.UtcNow.Date.AddDays(-7);
        var endDate = DateTime.UtcNow.Date;
        var result = await source.DiscoverAsync(
            startDate: startDate,
            endDate: endDate,
            maxWorkers: 3,
            progress: progress,
            cancellationToken: CancellationToken.None
        );

        // Assert & Output
        _output.WriteLine("\n=== Discovery Results ===");
        _output.WriteLine($"Success: {result.Success}");
        _output.WriteLine($"Pages Processed: {result.PagesProcessed}");
        _output.WriteLine($"Pages Failed: {result.PagesFailed}");
        _output.WriteLine($"Credentials Found: {result.Credentials.Count}");
        _output.WriteLine($"Source URL: {result.SourceUrl}");

        if (!string.IsNullOrEmpty(result.ErrorMessage))
        {
            _output.WriteLine($"Error: {result.ErrorMessage}");
        }

        if (result.Credentials.Count > 0)
        {
            _output.WriteLine("\n=== Sample Credentials (first 10) ===");
            foreach (var cred in result.Credentials.Take(10))
            {
                _output.WriteLine($"  Server: {cred.Server}:{cred.Port}");
                _output.WriteLine($"  User: {cred.Username}");
                _output.WriteLine($"  URL: {cred.BaseUrl}");
                _output.WriteLine("  ---");
            }
        }

        // We expect some credentials to be found if the site is accessible
        Assert.True(
            result.Success || result.Credentials.Count > 0 || result.PagesProcessed > 0,
            "Expected to either succeed or find some data"
        );
    }

    /// <summary>
    /// Tests the credential parser against a sample HTML page structure.
    /// </summary>
    [Fact]
    public async Task DiscoverAsync_WithTimeout_HandlesGracefully()
    {
        if (!_runIntegrationTests)
        {
            _output.WriteLine("Skipping integration test. Set RUN_INTEGRATION_TESTS=true to run.");
            return;
        }

        // Arrange
        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(5);

        var parser = new CredentialParser();
        var logger = NullLogger<WebCredentialSource>.Instance;
        using var source = new WebCredentialSource(httpClient, parser, logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act - search last 3 days
        var startDate = DateTime.UtcNow.Date.AddDays(-3);
        var endDate = DateTime.UtcNow.Date;
        var result = await source.DiscoverAsync(
            startDate: startDate,
            endDate: endDate,
            maxWorkers: 1,
            progress: null,
            cancellationToken: cts.Token
        );

        // Assert - should handle gracefully even with short timeout
        _output.WriteLine($"Success: {result.Success}");
        _output.WriteLine($"Credentials Found: {result.Credentials.Count}");
        _output.WriteLine($"Error: {result.ErrorMessage ?? "None"}");

        // Should not throw
        Assert.NotNull(result);
    }

    /// <summary>
    /// Tests cancellation of discovery operation.
    /// </summary>
    [Fact]
    public async Task DiscoverAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        if (!_runIntegrationTests)
        {
            _output.WriteLine("Skipping integration test. Set RUN_INTEGRATION_TESTS=true to run.");
            return;
        }

        // Arrange
        using var httpClient = new HttpClient();
        var parser = new CredentialParser();
        var logger = NullLogger<WebCredentialSource>.Instance;
        using var source = new WebCredentialSource(httpClient, parser, logger);

        using var cts = new CancellationTokenSource();
        await
        // Cancel immediately
        cts.CancelAsync();

        // Act & Assert - TaskCanceledException derives from OperationCanceledException
        var startDate = DateTime.UtcNow.Date.AddDays(-7);
        var endDate = DateTime.UtcNow.Date;
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            _ = await source
                .DiscoverAsync(startDate, endDate, maxWorkers: 5, progress: null, cancellationToken: cts.Token)
                .ConfigureAwait(false)
        );

        _output.WriteLine($"Cancellation handled correctly: {ex.GetType().Name}");
    }

    /// <summary>
    /// Tests fetching a single page and parsing its content.
    /// </summary>
    [Fact]
    public async Task FetchAndParse_SinglePage_ShowsDetailedResults()
    {
        if (!_runIntegrationTests)
        {
            _output.WriteLine("Skipping integration test. Set RUN_INTEGRATION_TESTS=true to run.");
            return;
        }

        // Arrange
        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(30);
        httpClient.DefaultRequestHeaders.Add(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"
        );

        var parser = new CredentialParser();

        // Fetch the category page first
        const string categoryUrl = "https://www.tvappapk.com/tag/xtream-codes/";
        _output.WriteLine($"Fetching category page: {categoryUrl}");

        try
        {
            var categoryHtml = await httpClient.GetStringAsync(categoryUrl);
            _output.WriteLine($"Category page size: {categoryHtml.Length} bytes");

            // Parse category page for links
            var categoryCredentials = parser.ParseHtml(categoryHtml);
            _output.WriteLine($"Credentials found in category page: {categoryCredentials.Count}");

            // Try to find daily list links
            var linkPattern = MyRegex();

            var matches = linkPattern.Matches(categoryHtml);
            _output.WriteLine($"Daily list links found: {matches.Count}");

            if (matches.Count > 0)
            {
                var firstLink = matches[0].Groups[1].Value;
                _output.WriteLine($"\nFetching first daily list: {firstLink}");

                try
                {
                    var pageHtml = await httpClient.GetStringAsync(firstLink);
                    _output.WriteLine($"Page size: {pageHtml.Length} bytes");

                    var credentials = parser.ParseHtml(pageHtml);
                    _output.WriteLine($"Credentials found: {credentials.Count}");

                    foreach (var cred in credentials.Take(5))
                    {
                        _output.WriteLine($"  - {cred.Server}:{cred.Port} | {cred.Username}");
                    }
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"Failed to fetch daily list: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Failed to fetch category page: {ex.Message}");
            // Don't fail the test - the site might be down
        }
    }

    [GeneratedRegexAttribute(
        @"href=[""']([^""']*xtream-codes[^""']*daily[^""']*)[""']",
        RegexOptions.IgnoreCase,
        "en-GB"
    )]
    private static partial System.Text.RegularExpressions.Regex MyRegex();
}
