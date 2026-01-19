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
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Service.MpegTs;
using Jellyfin.Xtream.Service.MpegTs.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.Discovery.Pipeline.Stages;

/// <summary>
/// Fifth pipeline stage: Stream playback validation.
/// Tests that Polish streams actually work and captures quality metrics.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="StreamTestStage"/> class.
/// </remarks>
/// <param name="httpClientFactory">HTTP client factory.</param>
/// <param name="logger">The logger.</param>
public sealed class StreamTestStage(IHttpClientFactory httpClientFactory, ILogger logger)
    : PipelineStageBase(
        PipelineStage.StreamTest,
        logger,
        new StageConfiguration
        {
            Concurrency = 10,
            TimeoutMs = 15000,
            ContinueOnError = true,
        }
    )
{
    private const int StreamTestBytes = 16384;
    private const byte TsSyncByte = 0x47;
    private const int TsPacketSize = 188;

    private static readonly HashSet<string> ValidStreamContentTypes =
    [
        "video/mp2t",
        "video/mpeg",
        "video/mp4",
        "application/octet-stream",
        "application/vnd.apple.mpegurl",
        "audio/x-mpegurl",
    ];

    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly ILogger _logger = logger;

    /// <inheritdoc />
    protected override async ValueTask<StageResult<PipelineItem>> ProcessAsync(
        PipelineItem item,
        CancellationToken cancellationToken
    )
    {
        var credential = item.Credential;
        var connectionInfo = item.GetProperty<ConnectionInfo>(PipelinePropertyKeys.AuthInfo);

        if (connectionInfo == null)
        {
            return StageResult.Fail<PipelineItem>("No connection info");
        }

        try
        {
            // Use filtered channels from CountryFilterStage - we only test filtered channels
            var filteredChannels = item.GetProperty<IList<StreamInfo>>(PipelinePropertyKeys.FilteredChannels);

            if (filteredChannels == null || filteredChannels.Count == 0)
            {
                return StageResult.Fail<PipelineItem>("No filtered channels to test");
            }

            // Pick first filtered channel for testing (fail-fast approach)
            var testStream = filteredChannels[0];
            var streamUrl =
                $"{credential.BaseUrl}/{credential.Username}/{credential.Password}/{testStream.StreamId}.ts";

            using var httpClient = _httpClientFactory.CreateClient(HttpClientConfiguration.XtreamClientName);
            var (works, status, quality) = await TestStreamUrlAsync(httpClient, streamUrl, cancellationToken)
                .ConfigureAwait(false);

            if (!works)
            {
                // Try HLS format as fallback
                var hlsUrl =
                    $"{credential.BaseUrl}/live/{credential.Username}/{credential.Password}/{testStream.StreamId}.m3u8";
                (works, status, quality) = await TestStreamUrlAsync(httpClient, hlsUrl, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!works)
            {
                return StageResult.Fail<PipelineItem>(status ?? "Stream test failed");
            }

            // Only add stream test results - Streams and FilteredChannels already set by CountryFilterStage
            var enrichedItem = item.WithProperties(
                (PipelinePropertyKeys.StreamResult, (object)(status ?? "OK")),
                (PipelinePropertyKeys.StreamQuality, (object?)quality ?? DBNull.Value)
            );

            return StageResult.Pass(enrichedItem);
        }
        catch (HttpRequestException ex)
        {
            return StageResult.Fail<PipelineItem>($"HTTP: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StageResult.Fail<PipelineItem>("Timeout");
        }
        catch (Exception ex)
        {
            return StageResult.Fail<PipelineItem>($"Error: {ex.Message}");
        }
    }

    private async Task<(bool Works, string? Status, StreamQualitySnapshot? Quality)> TestStreamUrlAsync(
        HttpClient httpClient,
        string url,
        CancellationToken cancellationToken
    )
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, StreamTestBytes - 1);
            request.Headers.Add("User-Agent", "Jellyfin/1.0");

            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            var statusCode = (int)response.StatusCode;

            if (statusCode is not (200 or 206))
            {
                if (statusCode == 456)
                {
                    return (false, "Blocked (456)", null);
                }

                return (false, $"HTTP {statusCode}", null);
            }

            // Validate content type
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType != null && !ValidStreamContentTypes.Contains(contentType))
            {
                if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                {
                    return (false, "HTML error page", null);
                }

                if (contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
                {
                    return (false, "JSON error response", null);
                }
            }

            // Read data for validation
            var buffer = ArrayPool<byte>.Shared.Rent(StreamTestBytes);
            try
            {
                var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var bytesRead = await stream
                    .ReadAsync(buffer.AsMemory(0, StreamTestBytes), cancellationToken)
                    .ConfigureAwait(false);

                if (bytesRead == 0)
                {
                    return (false, "Empty response", null);
                }

                // For TS streams, validate and analyze quality
                if (url.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
                {
                    var data = buffer.AsSpan(0, bytesRead).ToArray();
                    if (!ValidateTsSync(data))
                    {
                        return (false, "Invalid TS data", null);
                    }

                    var quality = AnalyzeStreamQuality(data);
                    return (true, $"OK ({bytesRead} bytes)", quality);
                }

                // For HLS, just check if it looks valid
                if (url.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                {
                    var content = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    if (content.Contains("#EXTM3U", StringComparison.Ordinal))
                    {
                        return (true, "OK (HLS)", null);
                    }

                    return (false, "Invalid HLS playlist", null);
                }

                return (true, $"OK ({bytesRead} bytes)", null);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, "Timeout", null);
        }
        catch (HttpRequestException)
        {
            return (false, "Connection failed", null);
        }
        catch (Exception ex)
        {
            _logger.LogDebugIfEnabled(ex, "Stream test failed for {Url}", url);
            return (false, "Error", null);
        }
    }

    private static bool ValidateTsSync(byte[] data)
    {
        if (data.Length < TsPacketSize)
        {
            return false;
        }

        // Find first sync byte
        var syncIndex = Array.IndexOf(data, TsSyncByte);
        if (syncIndex < 0)
        {
            return false;
        }

        // Verify at least one valid packet boundary
        return syncIndex + TsPacketSize >= data.Length || data[syncIndex + TsPacketSize] == TsSyncByte;
    }

    private StreamQualitySnapshot? AnalyzeStreamQuality(byte[] data)
    {
        try
        {
            var indexer = new TsIndexer(StreamTestBytes);
            indexer.ProcessChunk(data, 0);

            // Note: PatViolations is set to 0 because TR 101 290 monitoring requires TsDuck
            // which isn't available during quick stream discovery tests
            var snapshot = new StreamQualitySnapshot
            {
                PacketsParsed = (int)indexer.TotalPacketsParsed,
                BytesProcessed = (int)indexer.TotalBytesProcessed,
                SyncByteErrors = (int)indexer.SyncByteErrors,
                ContinuityErrors = (int)indexer.TotalContinuityErrors,
                PatViolations = 0,
                CrcErrors = (int)(indexer.PatCrcErrors + indexer.PmtCrcErrors),
                ProgramCount = indexer.ProgramCount,
                IsEncrypted = indexer.IsEncrypted,
                ScrambledPidCount = indexer.ScrambledPids.Length,
            };
            snapshot.CalculateQuality();

            return snapshot;
        }
        catch (Exception ex)
        {
            _logger.LogDebugIfEnabled(ex, "Quality analysis failed");
            return null;
        }
    }
}
