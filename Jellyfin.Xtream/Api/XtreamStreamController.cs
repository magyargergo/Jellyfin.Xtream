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
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Xtream.Api.Models;
using Jellyfin.Xtream.Service;
using Jellyfin.Xtream.Service.Logging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Api;

/// <summary>
/// Active stream management endpoints: listing, killing, provider health per stream,
/// stream metrics, diagnostics bundle, and aggregate metrics.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="XtreamStreamController"/> class.
/// </remarks>
/// <param name="logger">The logger instance.</param>
[ApiController]
[Route("Xtream")]
[Produces("application/json")]
public class XtreamStreamController(ILogger<XtreamStreamController> logger) : ControllerBase
{
    private readonly ILogger<XtreamStreamController> _logger = logger;

    // =========================================================================
    // Active Streams
    // =========================================================================

    /// <summary>
    /// Get all active streams with their health statistics.
    /// Supports filtering, sorting, and pagination via query parameters.
    /// </summary>
    /// <param name="status">Filter by health status (Healthy, OK, Lagging).</param>
    /// <param name="hasQualityIssues">Filter by quality issue presence.</param>
    /// <param name="minGapPercent">Filter streams with gap percentage above this value.</param>
    /// <param name="maxGapPercent">Filter streams with gap percentage below this value.</param>
    /// <param name="sortBy">Sort field: startTime, gapPercent, overflowCount, bytesReceived (default: startTime).</param>
    /// <param name="sortDesc">Sort descending when true (default: false).</param>
    /// <param name="offset">Number of results to skip (default: 0).</param>
    /// <param name="limit">Maximum results to return, 0 for all (default: 0).</param>
    /// <returns>Filtered and sorted list of active streams with statistics.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("ActiveStreams")]
    public ActionResult<object> GetActiveStreams(
        [FromQuery] string? status = null,
        [FromQuery] bool? hasQualityIssues = null,
        [FromQuery] double? minGapPercent = null,
        [FromQuery] double? maxGapPercent = null,
        [FromQuery] string sortBy = "startTime",
        [FromQuery] bool sortDesc = false,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 0
    )
    {
        var streams = Restream.GetActiveStreamSnapshots();
        IEnumerable<StreamInfoSnapshot> filtered = streams;

        if (!string.IsNullOrEmpty(status))
        {
            filtered = filtered.Where(s => string.Equals(s.Status, status, StringComparison.OrdinalIgnoreCase));
        }

        if (hasQualityIssues.HasValue)
        {
            filtered = filtered.Where(s => s.HasQualityIssues == hasQualityIssues.Value);
        }

        if (minGapPercent.HasValue)
        {
            filtered = filtered.Where(s => s.GapPercentage >= minGapPercent.Value);
        }

        if (maxGapPercent.HasValue)
        {
            filtered = filtered.Where(s => s.GapPercentage <= maxGapPercent.Value);
        }

        filtered = sortBy.ToLowerInvariant() switch
        {
            "gappercent" => sortDesc
                ? filtered.OrderByDescending(s => s.GapPercentage)
                : filtered.OrderBy(s => s.GapPercentage),
            "overflowcount" => sortDesc
                ? filtered.OrderByDescending(s => s.OverflowCount)
                : filtered.OrderBy(s => s.OverflowCount),
            "bytesreceived" => sortDesc
                ? filtered.OrderByDescending(s => s.BytesReceived)
                : filtered.OrderBy(s => s.BytesReceived),
            _ => sortDesc ? filtered.OrderByDescending(s => s.StartTime) : filtered.OrderBy(s => s.StartTime),
        };

        var totalCount = streams.Count;
        var result = filtered.AsEnumerable();

        if (offset > 0)
        {
            result = result.Skip(offset);
        }

        if (limit > 0)
        {
            result = result.Take(limit);
        }

        var items = result.ToList();
        _logger.PluginLogInformation("Retrieved {Count}/{Total} active stream(s)", items.Count, totalCount);
        return Ok(
            new
            {
                totalCount,
                offset,
                limit,
                count = items.Count,
                items,
            }
        );
    }

    /// <summary>
    /// Kill (terminate) a specific stream by its ID.
    /// </summary>
    /// <param name="streamId">The stream ID to kill.</param>
    /// <returns>Result indicating success or failure.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpDelete("ActiveStreams/{streamId}")]
    public ActionResult<object> KillStream(string streamId)
    {
        _logger.PluginLogWarning("Killing stream {StreamId} via API", streamId);

        if (Restream.KillStream(streamId))
        {
            _logger.PluginLogInformation("Successfully killed stream {StreamId}", streamId);
            return Ok(new { success = true, message = "Stream " + streamId + " killed successfully" });
        }

        _logger.PluginLogWarning("Stream {StreamId} not found", streamId);
        return NotFound(
            XtreamControllerHelpers.CreateError(
                ErrorCodes.StreamNotFound,
                "Stream " + streamId + " not found",
                "List active streams via GET /Xtream/ActiveStreams"
            )
        );
    }

    /// <summary>
    /// Kill all active streams.
    /// </summary>
    /// <returns>Result with the number of streams killed.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpDelete("ActiveStreams")]
    public ActionResult<object> KillAllStreams()
    {
        _logger.PluginLogWarning("Killing all active streams via API");
        var count = Restream.KillAllStreams();
        _logger.PluginLogInformation("Killed {Count} active stream(s)", count);
        return Ok(
            new
            {
                success = true,
                count,
                message = $"Killed {count} stream(s)",
            }
        );
    }

    // =========================================================================
    // Per-Stream Provider Health & Control Endpoints
    // =========================================================================

    /// <summary>
    /// Get all provider health snapshots for a specific stream.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <returns>List of provider health snapshots.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("ActiveStreams/{streamId}/Providers")]
    public ActionResult<object> GetStreamProviders(string streamId)
    {
        var providers = Restream.GetAllProviderHealth(streamId);
        if (providers == null)
        {
            return NotFound(
                XtreamControllerHelpers.CreateError(
                    ErrorCodes.StreamNotFound,
                    "Stream " + streamId + " not found",
                    "List active streams via GET /Xtream/ActiveStreams"
                )
            );
        }

        return Ok(
            providers.Select(p => new
            {
                providerIndex = p.ProviderIndex,
                state = p.State.ToString(),
                successRate = p.SuccessRate,
                latencyEwmaMs = p.LatencyEwmaMs,
                activeRequests = p.ActiveRequests,
                isolatedTimes = p.IsolatedTimes,
                isolationDurationMs = p.IsolationDurationMs,
                isHealthy = p.IsHealthy,
                isEjected = p.IsEjected,
            })
        );
    }

    /// <summary>
    /// Get health snapshot for a specific provider in a stream.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <param name="providerIndex">The provider index (0-based).</param>
    /// <returns>Provider health snapshot.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("ActiveStreams/{streamId}/Providers/{providerIndex:int}/Health")]
    public ActionResult<object> GetStreamProviderHealth(string streamId, int providerIndex)
    {
        var health = Restream.GetProviderHealth(streamId, providerIndex);
        if (health == null)
        {
            return NotFound(
                XtreamControllerHelpers.CreateError(ErrorCodes.StreamNotFound, "Stream or provider not found")
            );
        }

        var h = health.Value;
        return Ok(
            new
            {
                providerIndex = h.ProviderIndex,
                state = h.State.ToString(),
                successRate = h.SuccessRate,
                latencyEwmaMs = h.LatencyEwmaMs,
                activeRequests = h.ActiveRequests,
                isolatedTimes = h.IsolatedTimes,
                isolationDurationMs = h.IsolationDurationMs,
                isHealthy = h.IsHealthy,
                isEjected = h.IsEjected,
                isInProbation = h.IsInProbation,
            }
        );
    }

    /// <summary>
    /// Force a URL switch (reconnect) for a specific stream.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <returns>Result indicating success.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("ActiveStreams/{streamId}/ForceReconnect")]
    public ActionResult<object> ForceStreamReconnect(string streamId)
    {
        _logger.PluginLogWarning("Force reconnect requested for stream {StreamId}", streamId);

        if (Restream.RequestSwitch(streamId))
        {
            return Ok(new { success = true, message = "URL switch requested for stream " + streamId });
        }

        return NotFound(
            XtreamControllerHelpers.CreateError(
                ErrorCodes.StreamNotFound,
                "Stream " + streamId + " not found",
                "List active streams via GET /Xtream/ActiveStreams"
            )
        );
    }

    /// <summary>
    /// Force eject a provider from a stream's health system.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <param name="providerIndex">The provider index (0-based).</param>
    /// <param name="durationMs">Ejection duration in milliseconds (default 30000).</param>
    /// <returns>Result indicating success.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("ActiveStreams/{streamId}/Providers/{providerIndex:int}/Eject")]
    public ActionResult<object> EjectProvider(string streamId, int providerIndex, [FromQuery] int durationMs = 30000)
    {
        _logger.PluginLogWarning(
            "Ejecting provider {ProviderIndex} from stream {StreamId} for {Duration}ms",
            providerIndex,
            streamId,
            durationMs
        );

        if (Restream.ForceEjectProvider(streamId, providerIndex, durationMs))
        {
            return Ok(new { success = true, message = $"Provider {providerIndex} ejected for {durationMs}ms" });
        }

        return NotFound(
            XtreamControllerHelpers.CreateError(
                ErrorCodes.StreamNotFound,
                "Stream " + streamId + " not found",
                "List active streams via GET /Xtream/ActiveStreams"
            )
        );
    }

    /// <summary>
    /// Reset all providers in a stream to Active state.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <returns>Result indicating success.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("ActiveStreams/{streamId}/Providers/Reset")]
    public ActionResult<object> ResetStreamProviders(string streamId)
    {
        if (Restream.ResetAllProviders(streamId))
        {
            return Ok(new { success = true, message = "All providers reset to Active" });
        }

        return NotFound(
            XtreamControllerHelpers.CreateError(
                ErrorCodes.StreamNotFound,
                "Stream " + streamId + " not found",
                "List active streams via GET /Xtream/ActiveStreams"
            )
        );
    }

    // =========================================================================
    // Stream Quality Metrics
    // =========================================================================

    /// <summary>
    /// Get detailed TR 101 290 quality metrics for a specific stream.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <returns>Detailed quality metrics including Priority 1, Priority 2, A/V sync, and PCR analysis.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("ActiveStreams/{streamId}/Metrics")]
    public ActionResult<object> GetStreamMetrics(string streamId)
    {
        var metrics = Restream.GetStreamMetrics(streamId);
        if (metrics == null)
        {
            return NotFound(
                XtreamControllerHelpers.CreateError(
                    ErrorCodes.HealthDataUnavailable,
                    "Stream " + streamId + " not found or no metrics available"
                )
            );
        }

        var m = metrics;
        var avSync = Restream.GetStreamAvSync(streamId);
        var pcr = Restream.GetStreamPcrAnalysis(streamId);

        return Ok(
            new
            {
                timestamp = m.Timestamp,
                tsBitrate = m.TsBitrate,
                serviceCount = m.ServiceCount,
                pidCount = m.PidCount,
                priority1 = new
                {
                    syncByteError = m.Priority1.SyncByteError,
                    syncLoss = m.Priority1.SyncLoss,
                    patError = m.Priority1.PatError,
                    patError2 = m.Priority1.PatError2,
                    continuityCountError = m.Priority1.ContinuityCountError,
                    pmtError = m.Priority1.PmtError,
                    pmtError2 = m.Priority1.PmtError2,
                    pidError = m.Priority1.PidError,
                },
                priority2 = new
                {
                    transportError = m.Priority2.TransportError,
                    crcError = m.Priority2.CrcError,
                    pcrRepetitionError = m.Priority2.PcrRepetitionError,
                    pcrDiscontinuityError = m.Priority2.PcrDiscontinuityError,
                    pcrAccuracyError = m.Priority2.PcrAccuracyError,
                    ptsError = m.Priority2.PtsError,
                    catError = m.Priority2.CatError,
                },
                avSync = avSync != null
                    ? new
                    {
                        videoAudioDriftMs = avSync.Value.VideoAudioDriftMs,
                        driftRateMsPerSec = avSync.Value.DriftRateMsPerSec,
                        peakDriftMs = avSync.Value.PeakDriftMs,
                        avgDriftMs = avSync.Value.AvgDriftMs,
                        status = avSync.Value.Status.ToString(),
                        statusDescription = avSync.Value.StatusDescription,
                        isSynchronized = avSync.Value.IsSynchronized,
                    }
                    : (object?)null,
                pcr = pcr != null
                    ? new
                    {
                        pcrJitterUs = pcr.Value.PcrJitterUs,
                        pcrJitterMaxUs = pcr.Value.PcrJitterMaxUs,
                        pcrJitterAvgUs = pcr.Value.PcrJitterAvgUs,
                        pcrIntervalMs = pcr.Value.PcrIntervalMs,
                        pcrDriftPpm = pcr.Value.PcrDriftPpm,
                        pcrCount = pcr.Value.PcrCount,
                        pcrFrequencyOffsetPpm = pcr.Value.PcrFrequencyOffsetPpm,
                        pcrAccuracyNs = pcr.Value.PcrAccuracyNs,
                    }
                    : (object?)null,
            }
        );
    }

    // =========================================================================
    // Diagnostics Bundle
    // =========================================================================

    /// <summary>
    /// Get a comprehensive diagnostics bundle with all stream, provider, and configuration data.
    /// </summary>
    /// <returns>Complete system diagnostics for troubleshooting.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Diagnostics/Bundle")]
    public ActionResult<object> GetDiagnosticsBundle()
    {
        var config = Plugin.Instance.Configuration;
        var streams = Restream.GetActiveStreamSnapshots();

        // Collect per-stream provider health
        var streamDetails = new List<object>();
        foreach (var s in streams)
        {
            var providers = Restream.GetAllProviderHealth(s.StreamId);
            var streamMetrics = Restream.GetStreamMetrics(s.StreamId);
            var avSync = Restream.GetStreamAvSync(s.StreamId);

            streamDetails.Add(
                new
                {
                    stream = s,
                    providers = providers?.Select(p => new
                    {
                        providerIndex = p.ProviderIndex,
                        state = p.State.ToString(),
                        successRate = p.SuccessRate,
                        latencyEwmaMs = p.LatencyEwmaMs,
                        isolatedTimes = p.IsolatedTimes,
                    }),
                    tsBitrate = streamMetrics?.TsBitrate,
                    avSyncStatus = avSync?.Status.ToString(),
                    avDriftMs = avSync?.VideoAudioDriftMs,
                }
            );
        }

        return Ok(
            new
            {
                generatedAt = DateTime.UtcNow,
                activeStreamCount = streams.Count,
                configuration = new
                {
                    timeouts = new
                    {
                        connectTimeoutSeconds = config.StreamConnectTimeoutSeconds,
                        firstByteTimeoutSeconds = config.StreamFirstByteTimeoutSeconds,
                        responseHeadersTimeoutSeconds = config.StreamResponseHeadersTimeoutSeconds,
                        dataStallTimeoutSeconds = config.StreamDataStallTimeoutSeconds,
                        failoverBudgetSeconds = config.FailoverBudgetSeconds,
                        providerBlacklistSeconds = config.ProviderBlacklistSeconds,
                        maxFailoverAttempts = config.MaxFailoverAttempts,
                        dnsTimeoutSeconds = config.DnsTimeoutSeconds,
                        tcpKeepaliveEnabled = config.TcpKeepaliveEnabled,
                    },
                    health = new
                    {
                        enableP2C = config.EnableP2CLoadBalancing,
                        enableOutlierDetection = config.EnableOutlierDetection,
                        outlierStddevFactor = config.OutlierStddevFactor,
                        probationSuccessThreshold = config.ProbationSuccessThreshold,
                    },
                    buffer = new
                    {
                        underrunThresholdPercent = config.BufferUnderrunThresholdPercent,
                        nearFullThresholdPercent = config.BufferNearFullThresholdPercent,
                        underrunNotificationThreshold = config.BufferUnderrunNotificationThreshold,
                        consumerDisconnectGraceSeconds = config.ConsumerDisconnectGraceSeconds,
                    },
                },
                streams = streamDetails,
            }
        );
    }

    // =========================================================================
    // Aggregate Metrics
    // =========================================================================

    /// <summary>
    /// Get system-wide aggregated metrics across all active streams.
    /// </summary>
    /// <returns>Aggregated stream, buffer, quality, and network metrics.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Metrics/Aggregate")]
    public ActionResult<object> GetAggregateMetrics()
    {
        var streams = Restream.GetActiveStreamSnapshots();

        return Ok(
            new
            {
                timestamp = DateTime.UtcNow,
                streams = new
                {
                    total = streams.Count,
                    streaming = streams.Count(s => s.StreamerState == "Streaming"),
                    connecting = streams.Count(s => s.StreamerState == "Connecting"),
                    withQualityIssues = streams.Count(s => s.HasQualityIssues),
                },
                buffer = new
                {
                    totalOverflows = streams.Sum(s => s.OverflowCount),
                    totalBytesWritten = streams.Sum(s => s.TotalBytesWritten),
                    avgGapPercentage = streams.Count > 0 ? streams.Average(s => s.GapPercentage) : 0.0,
                },
                quality = new
                {
                    totalPacketErrors = streams.Sum(s => s.PacketErrors),
                    totalContinuityErrors = streams.Sum(s => s.ContinuityErrors),
                    totalSyncErrors = streams.Sum(s => s.SyncErrors),
                    streamsWithAVDrift = streams.Count(s => Math.Abs(s.AvDriftMs) > 100),
                },
                network = new
                {
                    totalBytesReceived = streams.Sum(s => s.BytesReceived),
                    totalPacketsOutput = streams.Sum(s => s.PacketsOutput),
                    avgBitrateBps = streams.Count > 0 ? streams.Average(s => s.TsBitrate) : 0.0,
                },
                providers = new
                {
                    totalSwitches = streams.Sum(s => s.SwitchesCompleted),
                    qualityTriggeredSwitches = streams.Sum(s => s.QualitySwitches),
                    totalProviders = streams.Sum(s => s.ProviderCount),
                },
            }
        );
    }
}
