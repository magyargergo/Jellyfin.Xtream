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

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace Jellyfin.Xtream.Service.Discovery;

/// <summary>
/// A resilient web credential source that uses AngleSharp for proper DOM parsing
/// and Polly for retry policies with exponential backoff.
/// </summary>
public sealed class WebCredentialSource : ICredentialSource, IDisposable
{
    private bool _disposed;
    private static readonly string[] UserAgents =
    [
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/119.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Safari/605.1.15",
    ];

    private readonly HttpClient _httpClient;
    private readonly ICredentialParser _parser;
    private readonly ILogger<WebCredentialSource> _logger;
    private readonly string _baseUrl;
    private readonly string _categoryPath;
    private readonly Random _random = new();
    private readonly IBrowsingContext _browsingContext;
    private readonly ResiliencePipeline<HttpResponseMessage> _resiliencePipeline;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebCredentialSource"/> class.
    /// </summary>
    /// <param name="httpClient">The HTTP client.</param>
    /// <param name="parser">The credential parser.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="baseUrl">The base URL to discover from.</param>
    /// <param name="categoryPath">The category path for finding posts.</param>
    public WebCredentialSource(
        HttpClient httpClient,
        ICredentialParser parser,
        ILogger<WebCredentialSource> logger,
        string baseUrl = "https://www.tvappapk.com",
        string categoryPath = "/tag/xtream-codes/"
    )
    {
        _httpClient = httpClient;
        _parser = parser;
        _logger = logger;
        _baseUrl = baseUrl.TrimEnd('/');
        _categoryPath = categoryPath;

        // Configure AngleSharp for HTML parsing
        var angleSharpConfig = AngleSharp.Configuration.Default;
        _browsingContext = BrowsingContext.New(angleSharpConfig);

        // Build resilience pipeline with Polly
        // Note: 404 is NOT retried - only transient errors that may succeed on retry
        _resiliencePipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(
                new RetryStrategyOptions<HttpResponseMessage>
                {
                    ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                        .HandleResult(r => r.StatusCode == HttpStatusCode.TooManyRequests)
                        .HandleResult(r => r.StatusCode == HttpStatusCode.ServiceUnavailable)
                        .HandleResult(r => r.StatusCode == HttpStatusCode.GatewayTimeout)
                        .HandleResult(r => r.StatusCode == HttpStatusCode.RequestTimeout)
                        .Handle<HttpRequestException>()
                        .Handle<TaskCanceledException>(),
                    MaxRetryAttempts = 2, // Reduced from 3 - fail faster for discovery
                    Delay = TimeSpan.FromMilliseconds(500), // Reduced from 2s - faster retries
                    BackoffType = DelayBackoffType.Linear, // Linear instead of exponential for speed
                    UseJitter = true,
                    OnRetry = args =>
                    {
                        logger.LogDebug(
                            "Retry {AttemptNumber} after {Delay}ms due to {Reason}",
                            args.AttemptNumber,
                            args.RetryDelay.TotalMilliseconds,
                            args.Outcome.Result?.StatusCode.ToString() ?? args.Outcome.Exception?.Message ?? "unknown"
                        );
                        return ValueTask.CompletedTask;
                    },
                }
            )
            .Build();
    }

    /// <inheritdoc />
    public string Name => "Web Credential Source";

    /// <inheritdoc />
    public string BaseUrl => _baseUrl;

    /// <inheritdoc />
    public async Task<DiscoveryResult> DiscoverAsync(
        int maxPages,
        int maxWorkers,
        IProgress<DiscoveryProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        var result = new DiscoveryResult { SourceUrl = _baseUrl + _categoryPath };

        try
        {
            ReportProgress(progress, DiscoveryPhase.Discovering, 0, maxPages, 0, "Discovering pages...");

            var urls = await GetPageUrlsAsync(maxPages, cancellationToken).ConfigureAwait(false);
            if (urls.Count == 0)
            {
                result.ErrorMessage = "No pages found to process";
                return result;
            }

            _logger.LogInformation("Found {Count} pages to process", urls.Count);

            var allCredentials = new ConcurrentBag<DiscoveredCredential>();
            var pagesCompleted = 0;
            var pagesFailed = 0;

            // Use semaphore for controlled parallelism with rate limiting
            using var semaphore = new SemaphoreSlim(maxWorkers);
            var tasks = urls.Select(async url =>
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    // Add jitter delay to avoid thundering herd
                    await Task.Delay(_random.Next(100, 500), cancellationToken).ConfigureAwait(false);

                    var pageCredentials = await DiscoverFromPageAsync(url, cancellationToken).ConfigureAwait(false);
                    foreach (var cred in pageCredentials)
                    {
                        allCredentials.Add(cred);
                    }

                    var completed = Interlocked.Increment(ref pagesCompleted);
                    var urlPart = url.Split('/').LastOrDefault(s => !string.IsNullOrEmpty(s)) ?? url;
                    ReportProgress(
                        progress,
                        DiscoveryPhase.Discovering,
                        completed,
                        urls.Count,
                        allCredentials.Count,
                        $"Processed {urlPart}"
                    );
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref pagesFailed);
                    _logger.LogWarning(ex, "Failed to process page: {Url}", url);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);

            // Deduplicate credentials
            var uniqueCredentials = DeduplicateCredentials(allCredentials);

            result.Success = true;
            result.Credentials = uniqueCredentials;
            result.PagesProcessed = pagesCompleted;
            result.PagesFailed = pagesFailed;

            _logger.LogInformation(
                "Discovery complete: {CredentialCount} unique credentials from {PagesProcessed} pages ({PagesFailed} failed)",
                uniqueCredentials.Count,
                pagesCompleted,
                pagesFailed
            );
        }
        catch (OperationCanceledException)
        {
            result.ErrorMessage = "Discovery was cancelled";
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discovery failed");
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    private async Task<List<string>> GetPageUrlsAsync(int maxPages, CancellationToken cancellationToken)
    {
        var urls = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            // Discover from multiple category pages with pagination
            // URL format: /tag/xtream-codes/ for page 1, /tag/xtream-codes/page/2/ for page 2, etc.
            var categoryPagesToProcess = Math.Max(1, (maxPages + 9) / 10); // Roughly 10 posts per category page

            for (var pageNum = 1; pageNum <= categoryPagesToProcess && urls.Count < maxPages * 2; pageNum++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var categoryUrl =
                    pageNum == 1
                        ? _baseUrl + _categoryPath
                        : string.Create(CultureInfo.InvariantCulture, $"{_baseUrl}{_categoryPath}page/{pageNum}/");

                _logger.LogInformation("Fetching category page {PageNum}: {Url}", pageNum, categoryUrl);

                var html = await FetchPageWithRetryAsync(categoryUrl, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrEmpty(html))
                {
                    _logger.LogDebug("Empty response for category page {PageNum}, stopping pagination", pageNum);
                    break;
                }

                var document = await _browsingContext
                    .OpenAsync(req => req.Content(html), cancellationToken)
                    .ConfigureAwait(false);

                // Check if we've hit a 404 page (no more pages)
                var title = document.Title?.ToLowerInvariant() ?? string.Empty;
                if (
                    title.Contains("404", StringComparison.Ordinal)
                    || title.Contains("not found", StringComparison.Ordinal)
                )
                {
                    _logger.LogDebug("Hit 404 at category page {PageNum}, stopping pagination", pageNum);
                    break;
                }

                // Use AngleSharp's CSS selector to find daily list links
                // Try multiple selectors to find the "Read more" links to daily list posts
                var links = document
                    .QuerySelectorAll(
                        "a[href*='xtream-codes-daily'], a.blogpost-button[href*='xtream-codes'], article.tag-xtream-codes-daily-lists a[href]"
                    )
                    .OfType<IHtmlAnchorElement>()
                    .Select(a => a.Href)
                    .Where(href =>
                        !string.IsNullOrEmpty(href)
                        && href.Contains("tvappapk.com", StringComparison.OrdinalIgnoreCase)
                        && href.Contains("xtream-codes", StringComparison.OrdinalIgnoreCase)
                    )
                    .ToList();

                var countBefore = urls.Count;
                foreach (var link in links)
                {
                    urls.Add(link);
                }

                var newCount = urls.Count - countBefore;
                _logger.LogDebug(
                    "Found {NewCount} new daily list links from category page {PageNum} (total: {TotalCount})",
                    newCount,
                    pageNum,
                    urls.Count
                );

                // If no new links found on this page, stop pagination
                if (newCount == 0)
                {
                    _logger.LogDebug("No new links on category page {PageNum}, stopping pagination", pageNum);
                    break;
                }

                // Small delay between category page fetches to be polite
                if (pageNum < categoryPagesToProcess)
                {
                    await Task.Delay(_random.Next(200, 500), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch category pages, falling back to date-based URLs");
        }

        // Fallback to date-based URLs if category discovery failed or found few results
        if (urls.Count < maxPages)
        {
            var today = DateTime.UtcNow.Date;
            for (var i = 0; i < maxPages * 3 && urls.Count < maxPages * 2; i++)
            {
                var date = today.AddDays(-i);
                var dateUrl = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{_baseUrl}/xtream-codes-daily-lists-{date:dd-MM-yyyy}/"
                );
                urls.Add(dateUrl);
            }
        }

        return [.. urls.Take(maxPages * 2)];
    }

    private async Task<IEnumerable<DiscoveredCredential>> DiscoverFromPageAsync(
        string url,
        CancellationToken cancellationToken
    )
    {
        var html = await FetchPageWithRetryAsync(url, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(html))
        {
            return [];
        }

        // Use AngleSharp to check for 404 pages
        var document = await _browsingContext
            .OpenAsync(req => req.Content(html), cancellationToken)
            .ConfigureAwait(false);

        // Check if this is a 404 page
        var title = document.Title?.ToLowerInvariant() ?? string.Empty;
        if (
            title.Contains("404", StringComparison.Ordinal)
            || title.Contains("not found", StringComparison.Ordinal)
            || title.Contains("page can't be found", StringComparison.Ordinal)
        )
        {
            return [];
        }

        // Use the battle-tested CredentialParser for extraction
        // Pass the original HTML - CredentialParser handles Cloudflare decoding itself
        return _parser.ParseHtml(html);
    }

    private async Task<string> FetchPageWithRetryAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            // Rotate user agents to appear more human-like
            request.Headers.Add("User-Agent", UserAgents[_random.Next(UserAgents.Length)]);
            request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
            request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
            // Note: Don't add Accept-Encoding here - HttpClient handles decompression automatically
            // when AutomaticDecompression is set, or we'd need to decompress manually
            request.Headers.Add("Connection", "keep-alive");
            request.Headers.Add("Upgrade-Insecure-Requests", "1");

            // Use Polly resilience pipeline
            var response = await _resiliencePipeline
                .ExecuteAsync(
                    async ct =>
                    {
                        var resp = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
                        return resp;
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("HTTP {StatusCode} for {Url}", response.StatusCode, url);
                return string.Empty;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to fetch page: {Url}", url);
            return string.Empty;
        }
    }

    private static List<DiscoveredCredential> DeduplicateCredentials(IEnumerable<DiscoveredCredential> credentials)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<DiscoveredCredential>();

        foreach (var cred in credentials)
        {
            if (seen.Add(cred.UniqueKey))
            {
                result.Add(cred);
            }
        }

        return result;
    }

    private static void ReportProgress(
        IProgress<DiscoveryProgress>? progress,
        DiscoveryPhase phase,
        int currentItem,
        int totalItems,
        int credentialsFound,
        string message
    )
    {
        progress?.Report(
            new DiscoveryProgress
            {
                Phase = phase,
                CurrentItem = currentItem,
                TotalItems = totalItems,
                CredentialsFound = credentialsFound,
                Message = message,
            }
        );
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _browsingContext.Dispose();
        _disposed = true;
    }
}
