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
using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Client.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.Discovery;

/// <summary>
/// Tests discovered provider credentials for validity, streams, and EPG.
/// </summary>
public sealed partial class ProviderTester : IProviderTester
{
    /// <summary>
    /// Major Polish TV broadcasters and channels - high confidence matches.
    /// These are specific enough to avoid false positives.
    /// </summary>
    private static readonly HashSet<string> PolishBroadcasters = new(StringComparer.OrdinalIgnoreCase)
    {
        // TVP - Public broadcaster
        "tvp1",
        "tvp2",
        "tvp3",
        "tvp info",
        "tvp sport",
        "tvp historia",
        "tvp kultura",
        "tvp abc",
        "tvp seriale",
        "tvp polonia",
        "tvp hd",
        "tvp rozrywka",
        "tvp world",
        "tvp dokument",
        "alfa tvp",
        // Polsat group
        "polsat",
        "polsat news",
        "polsat sport",
        "polsat film",
        "polsat play",
        "polsat cafe",
        "polsat games",
        "polsat doku",
        "super polsat",
        "polsat comedy",
        "polsat reality",
        "polsat music",
        "polsat box go",
        "polsat sport extra",
        "polsat sport news",
        "polsat sport fight",
        // TVN group
        "tvn24",
        "tvn24 bis",
        "tvn7",
        "tvn style",
        "tvn turbo",
        "tvn fabula",
        "tvn international",
        "metro tv",
        // Canal+ Poland
        "canal+ polska",
        "canal+ sport",
        "canal+ film",
        "canal+ seriale",
        "canal+ family",
        "canal+ discovery",
        "canal+ dokument",
        "canal+ domo",
        "canal+ premium",
        "canal+ now",
        "ale kino",
        // Other Polish channels
        "tv puls",
        "puls 2",
        "tv4",
        "tv6",
        "tv republika",
        "tv trwam",
        "fokus tv",
        "stopklatka",
        "kino polska",
        "kino tv",
        "4fun tv",
        "4fun dance",
        "4fun kids",
        "4fun gold hits",
        "eska tv",
        "eska rock",
        "polo tv",
        "vox music tv",
        "tele5",
        "nowa tv",
        "zoom tv",
        "wp",
        "wpolsce24",
        "biznes24",
        "belsat",
        "superstacja",
        "paramount channel polska",
        "comedy central polska",
        "mtv polska",
        "nickelodeon polska",
        "disney channel polska",
        "cartoon network polska",
        "minimini+",
        "tlc polska",
        "investigation discovery polska",
        "tbn polska",
        "religia tv",
        "ewtn polska",
        // Polish sports
        "eleven sports polska",
        "eurosport polska",
    };

    /// <summary>
    /// Generic Polish language/country indicators - require word boundary matching.
    /// </summary>
    private static readonly HashSet<string> PolishIndicators = new(StringComparer.OrdinalIgnoreCase)
    {
        "poland",
        "polish",
        "polska",
        "polskie",
        "polskiej",
        "polski",
    };

    /// <summary>
    /// Patterns that might contain "pl" but are NOT Polish - false positive exclusions.
    /// </summary>
    private static readonly HashSet<string> FalsePositivePatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "replay",
        "playboy",
        "player",
        "playlist",
        "play",
        "plus",
        "simple",
        "apple",
        "purple",
        "people",
        "triple",
        "maple",
        "temple",
        "example",
        "iple",
        "ople",
        "aple",
        "uple",
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ProviderTester> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderTester"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public ProviderTester(IHttpClientFactory httpClientFactory, ILogger<ProviderTester> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ProviderTestResult> TestCredentialAsync(
        DiscoveredCredential credential,
        bool testStream,
        bool testEpg,
        CancellationToken cancellationToken
    )
    {
        var result = new ProviderTestResult { Credential = credential, Status = ProviderStatus.Error };

        try
        {
            // Quick connectivity check before making full HTTP requests
            if (
                !await IsHostReachableAsync(credential.Server, credential.Port, cancellationToken).ConfigureAwait(false)
            )
            {
                result.Status = ProviderStatus.Error;
                result.ErrorMessage = "Host unreachable";
                return result;
            }

            using var client = new XtreamClient(_httpClientFactory);
            var connectionInfo = new ConnectionInfo(credential.BaseUrl, credential.Username, credential.Password);

            // Test authentication
            var playerApi = await client
                .GetUserAndServerInfoAsync(connectionInfo, cancellationToken)
                .ConfigureAwait(false);

            if (playerApi?.UserInfo == null)
            {
                result.Status = ProviderStatus.Invalid;
                result.ErrorMessage = "No user info returned";
                return result;
            }

            var userInfo = playerApi.UserInfo;
            if (userInfo.Auth != 1)
            {
                result.Status = ProviderStatus.Invalid;
                result.ErrorMessage = "Authentication failed";
                return result;
            }

            // Parse user info
            result.Status = ParseStatus(userInfo.Status);
            result.MaxConnections = userInfo.MaxConnections;
            result.ActiveConnections = userInfo.ActiveCons;
            result.ExpirationDate = userInfo.ExpDate;

            // Only continue testing if account is active
            if (result.Status != ProviderStatus.Active)
            {
                return result;
            }

            // Get all live streams
            var streams = await client.GetLiveStreamsAsync(connectionInfo, cancellationToken).ConfigureAwait(false);

            result.TotalChannelCount = streams.Count;

            // Find Polish channels
            var polishStreams = FindPolishChannels(streams);
            result.HasPolishChannels = polishStreams.Count > 0;
            result.PolishChannelCount = polishStreams.Count;
            result.PolishChannelNames = polishStreams.Select(s => s.Name).Take(50).ToList();
            result.PolishCategories = polishStreams
                .Where(s => s.Name.Contains(':', StringComparison.Ordinal))
                .Select(s => s.Name.Split(':')[0].Trim())
                .Where(p => p.Length < 20)
                .Distinct(StringComparer.Ordinal)
                .Take(10)
                .ToList();

            // Run stream and EPG tests in parallel for efficiency
            var testStreamIds =
                polishStreams.Count > 0
                    ? polishStreams.Take(3).Select(s => s.StreamId).ToArray()
                    : streams.Take(3).Select(s => s.StreamId).ToArray();

            if (testStreamIds.Length > 0)
            {
                var streamTask = testStream
                    ? TestMultipleStreamsAsync(credential, testStreamIds, cancellationToken)
                    : Task.FromResult((Works: false, Status: "Skipped"));

                var epgTask = testEpg
                    ? TestEpgParallelAsync(client, connectionInfo, testStreamIds, cancellationToken)
                    : Task.FromResult((HasData: false, ProgramCount: 0));

                await Task.WhenAll(streamTask, epgTask).ConfigureAwait(false);

                (result.StreamWorks, result.StreamStatus) = await streamTask.ConfigureAwait(false);
                (result.HasEpg, result.EpgProgramCount) = await epgTask.ConfigureAwait(false);
            }
        }
        catch (HttpRequestException ex)
        {
            result.Status = ProviderStatus.Error;
            result.ErrorMessage = $"HTTP error: {ex.Message}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result.Status = ProviderStatus.Error;
            result.ErrorMessage = ex.Message;
            _logger.LogDebug(ex, "Error testing credential {Server}:{Port}", credential.Server, credential.Port);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProviderTestResult>> TestCredentialsAsync(
        IReadOnlyList<DiscoveredCredential> credentials,
        int maxWorkers,
        bool testStream,
        bool testEpg,
        IProgress<DiscoveryProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        var results = new ConcurrentBag<ProviderTestResult>();
        var completed = 0;
        var workingCount = 0;
        var workingWithEpgCount = 0;
        var fullyWorkingCount = 0;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxWorkers,
            CancellationToken = cancellationToken,
        };

        await Parallel
            .ForEachAsync(
                credentials,
                parallelOptions,
                async (credential, ct) =>
                {
                    var result = await TestCredentialAsync(credential, testStream, testEpg, ct).ConfigureAwait(false);
                    results.Add(result);

                    var currentCompleted = Interlocked.Increment(ref completed);

                    // Track funnel: Working -> Working+EPG -> Working+EPG+Polish
                    if (result.Status == ProviderStatus.Active && result.StreamWorks)
                    {
                        Interlocked.Increment(ref workingCount);

                        if (result.HasEpg)
                        {
                            Interlocked.Increment(ref workingWithEpgCount);

                            if (result.HasPolishChannels)
                            {
                                Interlocked.Increment(ref fullyWorkingCount);
                            }
                        }
                    }

                    progress?.Report(
                        new DiscoveryProgress
                        {
                            Phase = DiscoveryPhase.Testing,
                            CurrentItem = currentCompleted,
                            TotalItems = credentials.Count,
                            CredentialsFound = credentials.Count,
                            WorkingProviders = workingCount,
                            WorkingWithEpg = workingWithEpgCount,
                            FullyWorking = fullyWorkingCount,
                            Message = $"Tested {credential.Username}@{credential.Server}",
                        }
                    );
                }
            )
            .ConfigureAwait(false);

        return results.ToList();
    }

    private static ProviderStatus ParseStatus(string? status)
    {
        return status?.ToUpperInvariant() switch
        {
            "ACTIVE" => ProviderStatus.Active,
            "EXPIRED" => ProviderStatus.Expired,
            "BANNED" => ProviderStatus.Invalid,
            "DISABLED" => ProviderStatus.Invalid,
            _ => ProviderStatus.Error,
        };
    }

    private static List<StreamInfo> FindPolishChannels(IReadOnlyList<StreamInfo> streams)
    {
        var polishStreams = new List<StreamInfo>();

        foreach (var stream in streams)
        {
            if (IsPolishChannel(stream.Name))
            {
                polishStreams.Add(stream);
            }
        }

        return polishStreams;
    }

    /// <summary>
    /// Determines if a channel name indicates Polish content using multi-tier detection.
    /// </summary>
    /// <param name="channelName">The channel name to check.</param>
    /// <returns>True if the channel appears to be Polish.</returns>
    private static bool IsPolishChannel(string channelName)
    {
        if (string.IsNullOrWhiteSpace(channelName))
        {
            return false;
        }

        var name = channelName.ToLowerInvariant();

        // Tier 1: Check for specific Polish broadcaster names (highest confidence)
        if (PolishBroadcasters.Any(broadcaster => name.Contains(broadcaster, StringComparison.Ordinal)))
        {
            return true;
        }

        // Tier 2: Check for PL country prefix patterns (high confidence)
        // Common IPTV formats: "PL:", "PL |", "| PL", "[PL]", "(PL)", "PL-"
        if (PolishPrefixRegex().IsMatch(name))
        {
            return true;
        }

        // Tier 3: Check for Polish language indicators with word boundary
        // This avoids false positives like "replay" matching "pl"
        foreach (var indicator in PolishIndicators)
        {
            if (ContainsWordBoundary(name, indicator))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a string contains a word with proper word boundaries.
    /// This prevents "pl" in "replay" or "playboy" from matching.
    /// </summary>
    private static bool ContainsWordBoundary(string text, string word)
    {
        var index = text.IndexOf(word, StringComparison.Ordinal);
        while (index >= 0)
        {
            // Check left boundary
            var leftOk = index == 0 || !char.IsLetterOrDigit(text[index - 1]);

            // Check right boundary
            var rightIndex = index + word.Length;
            var rightOk = rightIndex >= text.Length || !char.IsLetterOrDigit(text[rightIndex]);

            if (leftOk && rightOk)
            {
                // Additional check: ensure this isn't a known false positive pattern
                if (!IsFalsePositive(text))
                {
                    return true;
                }
            }

            // Search for next occurrence
            index = text.IndexOf(word, index + 1, StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>
    /// Checks if the text contains known false positive patterns.
    /// </summary>
    private static bool IsFalsePositive(string text)
    {
        return FalsePositivePatterns.Any(pattern => text.Contains(pattern, StringComparison.Ordinal));
    }

    /// <summary>
    /// MPEG-TS sync byte - every TS packet starts with 0x47.
    /// </summary>
    private const byte TsSyncByte = 0x47;

    /// <summary>
    /// MPEG-TS packet size is 188 bytes.
    /// </summary>
    private const int TsPacketSize = 188;

    /// <summary>
    /// Valid Content-Type values for video streams.
    /// </summary>
    private static readonly HashSet<string> ValidStreamContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "video/mp2t",
        "video/mpeg",
        "video/x-mpegts",
        "video/MP2T",
        "application/octet-stream",
        "application/x-mpegurl",
        "application/vnd.apple.mpegurl",
        "audio/mpegurl",
        "audio/x-mpegurl",
    };

    /// <summary>
    /// Valid Content-Type values for HLS playlists.
    /// </summary>
    private static readonly HashSet<string> ValidHlsContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/x-mpegurl",
        "application/vnd.apple.mpegurl",
        "audio/mpegurl",
        "audio/x-mpegurl",
        "text/plain",
    };

    private async Task<(bool Works, string Status)> TestMultipleStreamsAsync(
        DiscoveredCredential credential,
        int[] streamIds,
        CancellationToken cancellationToken
    )
    {
        if (streamIds.Length == 0)
        {
            return (false, "No streams to test");
        }

        var tasks = streamIds.Select(id => TestStreamAsync(credential, id, cancellationToken));
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        var workingCount = results.Count(r => r.Works);
        var totalTested = results.Length;

        if (workingCount == 0)
        {
            var mostInformative = results.FirstOrDefault(r => r.Status is not "Error" and not "Timeout");
            return mostInformative.Status != null ? mostInformative : results[0];
        }

        var status =
            workingCount == totalTested
                ? string.Create(CultureInfo.InvariantCulture, $"OK ({workingCount}/{totalTested} streams)")
                : string.Create(CultureInfo.InvariantCulture, $"Partial ({workingCount}/{totalTested} streams)");

        return (true, status);
    }

    private async Task<(bool Works, string Status)> TestStreamAsync(
        DiscoveredCredential credential,
        int streamId,
        CancellationToken cancellationToken
    )
    {
        var httpClient = _httpClientFactory.CreateClient("XtreamClient");

        // Test URL formats concurrently - first successful response wins
        // Includes TS (MPEG-TS), M3U8 (HLS), and base format
        var formats = new[]
        {
            $"{credential.BaseUrl}/live/{credential.Username}/{credential.Password}/{streamId}.ts",
            $"{credential.BaseUrl}/live/{credential.Username}/{credential.Password}/{streamId}.m3u8",
            $"{credential.BaseUrl}/{credential.Username}/{credential.Password}/{streamId}",
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var token = cts.Token;

        // Create tasks eagerly with captured token and await them
        var task1 = TestSingleStreamUrlAsync(httpClient, formats[0], token);
        var task2 = TestSingleStreamUrlAsync(httpClient, formats[1], token);
        var task3 = TestSingleStreamUrlAsync(httpClient, formats[2], token);
        var results = await Task.WhenAll(task1, task2, task3).ConfigureAwait(false);

        // Return first working result, or first non-error result, or failure
        var working = results.FirstOrDefault(r => r.Works);
        if (working.Works)
        {
            return working;
        }

        var blocked = results.FirstOrDefault(r => r.Status == "Blocked (456)");
        if (blocked.Status != null)
        {
            return blocked;
        }

        // Return the most informative failure status
        var httpError = results.FirstOrDefault(r => r.Status.StartsWith("HTTP", StringComparison.Ordinal));
        if (httpError.Status != null)
        {
            return httpError;
        }

        return results.FirstOrDefault(r => r.Status != null && r.Status != "Error") is { Status: not null } informative
            ? informative
            : (false, "Connection failed");
    }

    private async Task<(bool Works, string Status)> TestSingleStreamUrlAsync(
        HttpClient httpClient,
        string url,
        CancellationToken cancellationToken
    )
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // Request enough data to validate TS packets (at least 2 packets for sync verification)
            request.Headers.Add("Range", "bytes=0-1023");

            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            var statusCode = (int)response.StatusCode;

            // Handle redirect - follow and validate final destination
            if (statusCode is 301 or 302 or 307 or 308)
            {
                var redirectUrl = response.Headers.Location?.ToString();
                if (string.IsNullOrEmpty(redirectUrl))
                {
                    return (Works: false, Status: "Redirect without location");
                }

                // Handle relative URLs
                if (!redirectUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    var baseUri = new Uri(url);
                    redirectUrl = new Uri(baseUri, redirectUrl).ToString();
                }

                return await TestRedirectDestinationAsync(httpClient, redirectUrl, cancellationToken)
                    .ConfigureAwait(false);
            }

            // Handle Range not supported - retry without Range header
            if (statusCode == 416)
            {
                return await TestStreamWithoutRangeAsync(httpClient, url, cancellationToken).ConfigureAwait(false);
            }

            if (statusCode is not (200 or 206))
            {
                if (statusCode == 456)
                {
                    return (Works: false, Status: "Blocked (456)");
                }

                return (
                    Works: false,
                    Status: string.Concat("HTTP ", statusCode.ToString(CultureInfo.InvariantCulture))
                );
            }

            // Validate Content-Type header
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType != null && !ValidStreamContentTypes.Contains(contentType))
            {
                // Check if it's an HTML error page
                if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                {
                    return (Works: false, Status: "HTML error page");
                }

                // Check if it's JSON (API error)
                if (contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
                {
                    return (Works: false, Status: "JSON error response");
                }
            }

            // Read initial bytes to validate actual stream data
            var buffer = new byte[TsPacketSize * 2]; // Read enough for 2 TS packets
            var bytesRead = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            if (bytesRead.Length == 0)
            {
                return (Works: false, Status: "Empty response");
            }

            // For .ts URLs, validate MPEG-TS sync byte pattern
            if (url.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
            {
                var tsValidation = ValidateTsStream(bytesRead);
                if (!tsValidation.IsValid)
                {
                    // Not a fatal error - some servers send pre-roll data before TS packets
                    _logger.LogDebug(
                        "TS validation failed for {Url}: {Reason}, but accepting as working",
                        url,
                        tsValidation.Reason
                    );
                }
            }

            // For .m3u8 URLs, validate HLS playlist content
            if (url.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                var (isValid, reason) = ValidateHlsPlaylist(bytesRead, contentType);
                if (!isValid)
                {
                    return (Works: false, Status: reason ?? "Invalid HLS");
                }

                return (Works: true, Status: "OK (HLS)");
            }

            // If we got here with actual data, the stream is working
            return (Works: true, Status: $"OK ({bytesRead.Length} bytes)");
        }
        catch (OperationCanceledException)
        {
            return (Works: false, Status: "Timeout");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Stream test HTTP error for {Url}", url);
            return (Works: false, Status: "HTTP error");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stream test failed for {Url}", url);
            return (Works: false, Status: "Error");
        }
    }

    private async Task<(bool Works, string Status)> TestStreamWithoutRangeAsync(
        HttpClient httpClient,
        string url,
        CancellationToken cancellationToken
    )
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            var statusCode = (int)response.StatusCode;

            if (statusCode is not 200)
            {
                return (
                    Works: false,
                    Status: string.Create(CultureInfo.InvariantCulture, $"HTTP {statusCode} (no Range)")
                );
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType != null)
            {
                if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                {
                    return (Works: false, Status: "HTML error page (no Range)");
                }

                if (contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
                {
                    return (Works: false, Status: "JSON error (no Range)");
                }
            }

            var buffer = new byte[512];
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (bytesRead == 0)
            {
                return (Works: false, Status: "Empty response (no Range)");
            }

            return (
                Works: true,
                Status: string.Create(CultureInfo.InvariantCulture, $"OK ({bytesRead} bytes, no Range)")
            );
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stream test without Range failed for {Url}", url);
            return (Works: false, Status: "Error");
        }
    }

    private async Task<(bool Works, string Status)> TestRedirectDestinationAsync(
        HttpClient httpClient,
        string url,
        CancellationToken cancellationToken
    )
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Range", "bytes=0-1023");

            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            var statusCode = (int)response.StatusCode;

            if (statusCode is not (200 or 206))
            {
                return (
                    Works: false,
                    Status: string.Create(CultureInfo.InvariantCulture, $"Redirect to HTTP {statusCode}")
                );
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType != null)
            {
                if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                {
                    return (Works: false, Status: "Redirect to HTML page");
                }

                if (contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
                {
                    return (Works: false, Status: "Redirect to JSON error");
                }
            }

            var bytesRead = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            if (bytesRead.Length == 0)
            {
                return (Works: false, Status: "Redirect to empty response");
            }

            return (
                Works: true,
                Status: string.Create(CultureInfo.InvariantCulture, $"OK via redirect ({bytesRead.Length} bytes)")
            );
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Redirect destination test failed for {Url}", url);
            return (Works: false, Status: "Redirect failed");
        }
    }

    /// <summary>
    /// Validates that the byte array contains valid MPEG-TS data.
    /// </summary>
    /// <param name="data">The byte array to validate.</param>
    /// <returns>Validation result with reason if invalid.</returns>
    private static (bool IsValid, string? Reason) ValidateTsStream(byte[] data)
    {
        if (data.Length < TsPacketSize)
        {
            return (false, "Response too small for TS packet");
        }

        // Find the first sync byte
        var syncIndex = Array.IndexOf(data, TsSyncByte);
        if (syncIndex < 0)
        {
            return (false, "No TS sync byte (0x47) found");
        }

        // If we have enough data, verify the next packet also starts with sync byte
        if (data.Length >= syncIndex + TsPacketSize + 1)
        {
            var nextSyncIndex = syncIndex + TsPacketSize;
            if (data[nextSyncIndex] != TsSyncByte)
            {
                // Could be a different packet size (192 for M2TS) or corrupted
                return (false, "TS packet boundary mismatch");
            }
        }

        return (true, null);
    }

    /// <summary>
    /// Validates that the byte array contains valid HLS playlist data.
    /// </summary>
    /// <param name="data">The byte array to validate.</param>
    /// <param name="contentType">The Content-Type header value.</param>
    /// <returns>Validation result with reason if invalid.</returns>
    private static (bool IsValid, string? Reason) ValidateHlsPlaylist(byte[] data, string? contentType)
    {
        // Check Content-Type if available
        if (contentType != null && !ValidHlsContentTypes.Contains(contentType))
        {
            return (false, "Invalid HLS Content-Type");
        }

        if (data.Length < 7)
        {
            return (false, "Response too small for HLS");
        }

        // Convert to string and check for HLS markers
        var content = System.Text.Encoding.UTF8.GetString(data);

        // HLS playlists must start with #EXTM3U
        if (!content.StartsWith("#EXTM3U", StringComparison.Ordinal))
        {
            // Check if it's an HTML error page
            if (
                content.Contains("<html", StringComparison.OrdinalIgnoreCase)
                || content.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
            )
            {
                return (false, "HTML error page");
            }

            return (false, "Missing #EXTM3U header");
        }

        // Valid HLS playlist should have at least one of these tags
        var hasStreamInfo = content.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal);
        var hasMediaSequence = content.Contains("#EXT-X-MEDIA-SEQUENCE", StringComparison.Ordinal);
        var hasTargetDuration = content.Contains("#EXT-X-TARGETDURATION", StringComparison.Ordinal);
        var hasExtInf = content.Contains("#EXTINF", StringComparison.Ordinal);

        if (!hasStreamInfo && !hasMediaSequence && !hasTargetDuration && !hasExtInf)
        {
            return (false, "No valid HLS tags found");
        }

        return (true, null);
    }

    /// <summary>
    /// Tests EPG availability for multiple streams in parallel.
    /// </summary>
    private static async Task<(bool HasData, int ProgramCount)> TestEpgParallelAsync(
        XtreamClient client,
        ConnectionInfo connectionInfo,
        int[] streamIds,
        CancellationToken cancellationToken
    )
    {
        // Test all EPG streams concurrently
        var tasks = streamIds.Select(async streamId =>
        {
            try
            {
                var epg = await client
                    .GetShortEpgAsync(connectionInfo, streamId, 4, cancellationToken)
                    .ConfigureAwait(false);

                return epg?.Listings?.Count > 0 ? epg.Listings.Count : 0;
            }
            catch
            {
                return 0;
            }
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var totalPrograms = results.Sum();
        var channelsWithData = results.Count(r => r > 0);

        return (channelsWithData > 0, totalPrograms);
    }

    /// <summary>
    /// Regex pattern for Polish country prefix in IPTV channel names.
    /// Matches common formats: "PL:", "PL |", "| PL", "[PL]", "(PL)", "PL-", "PL_", etc.
    /// Also matches numbered prefixes like "123 PL:" or "45. PL |".
    /// </summary>
    [GeneratedRegex(@"(?:^|\s|\||:|\[|\()pl(?:\s*[\|:\-_\]\)]|$)|^\d+[\.\s]+pl[\s:\|]", RegexOptions.IgnoreCase)]
    private static partial Regex PolishPrefixRegex();

    /// <summary>
    /// Quick TCP connectivity check to determine if a host is reachable.
    /// Much faster than a full HTTP request for unreachable hosts.
    /// </summary>
    private static async Task<bool> IsHostReachableAsync(string host, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3)); // 3 second timeout for connectivity check

            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout - host didn't respond in time
            return false;
        }
        catch (SocketException)
        {
            // Connection refused, host not found, network unreachable, etc.
            return false;
        }
        catch
        {
            // Any other connection error
            return false;
        }
    }
}
