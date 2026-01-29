// Copyright (C) 2025  Gergo Magyar
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using Jellyfin.Xtream.Service.Streaming.Native;
using Jellyfin.Xtream.Utility;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.Resilience;

/// <summary>
/// Records streaming outcomes to the <see cref="ProviderHealthScorer"/> for intelligent provider selection.
/// Listens to native streamer events and maps them to success/failure records.
/// </summary>
public sealed class StreamingOutcomeRecorder
{
    private readonly ProviderHealthScorer _healthScorer;
    private readonly ILogger<StreamingOutcomeRecorder> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamingOutcomeRecorder"/> class.
    /// </summary>
    /// <param name="healthScorer">The provider health scorer to record outcomes to.</param>
    /// <param name="logger">Logger instance.</param>
    public StreamingOutcomeRecorder(ProviderHealthScorer healthScorer, ILogger<StreamingOutcomeRecorder> logger)
    {
        _healthScorer = healthScorer;
        _logger = logger;
    }

    /// <summary>
    /// Records a successful stream operation.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="qualityScore">Quality score of the stream (0-100).</param>
    /// <param name="connectionTimeMs">Time to establish connection in milliseconds.</param>
    public void RecordSuccess(string providerId, int qualityScore, double connectionTimeMs)
    {
        _healthScorer.RecordSuccess(providerId, qualityScore, connectionTimeMs);
        _logger.LogDebugIfEnabled(
            "Recorded success for provider {ProviderId}: quality={Quality}, connectionTime={ConnectionTimeMs}ms",
            providerId,
            qualityScore,
            connectionTimeMs
        );
    }

    /// <summary>
    /// Records a failed stream operation.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="failureType">The type of failure.</param>
    public void RecordFailure(string providerId, FailureType failureType)
    {
        _healthScorer.RecordFailure(providerId, failureType);
        _logger.LogDebugIfEnabled(
            "Recorded failure for provider {ProviderId}: type={FailureType}",
            providerId,
            failureType
        );
    }

    /// <summary>
    /// Records an outcome based on a native streamer event.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="eventType">The streamer event type.</param>
    /// <param name="detail">Event detail (e.g., HTTP status code, curl error).</param>
    /// <param name="connectionTimeMs">Optional connection time in milliseconds.</param>
    /// <param name="qualityScore">Optional quality score (0-100) for success events.</param>
    public void RecordFromEvent(
        string providerId,
        StreamerEvent eventType,
        int detail,
        double? connectionTimeMs = null,
        int? qualityScore = null
    )
    {
        switch (eventType)
        {
            case StreamerEvent.Connected:
                // Connected but waiting for quality data - don't record yet
                _logger.LogDebugIfEnabled("Provider {ProviderId} connected, awaiting quality data", providerId);
                break;

            case StreamerEvent.DataReceived:
                // First data received - record success with provided or default quality
                var quality = qualityScore ?? 70; // Default to "good" if no quality data
                var connTime = connectionTimeMs ?? 1000.0; // Default reasonable connection time
                RecordSuccess(providerId, quality, connTime);
                break;

            case StreamerEvent.QualityDegraded:
                RecordFailure(providerId, FailureType.QualityDegradation);
                break;

            case StreamerEvent.Stalled:
                RecordFailure(providerId, FailureType.DataStall);
                break;

            case StreamerEvent.Error:
                var failureType = MapCurlErrorToFailureType(detail);
                RecordFailure(providerId, failureType);
                break;

            case StreamerEvent.Disconnected:
            case StreamerEvent.Reconnecting:
            case StreamerEvent.Switched:
            case StreamerEvent.Stopped:
                // These events don't directly indicate success/failure for scoring
                break;
        }
    }

    /// <summary>
    /// Maps a libcurl error code to a <see cref="FailureType"/>.
    /// </summary>
    /// <param name="curlError">The libcurl error code.</param>
    /// <returns>The corresponding failure type.</returns>
    private static FailureType MapCurlErrorToFailureType(int curlError)
    {
        // Common libcurl error codes:
        // https://curl.se/libcurl/c/libcurl-errors.html
        return curlError switch
        {
            // CURLE_OPERATION_TIMEDOUT = 28
            28 => FailureType.ConnectionTimeout,

            // CURLE_COULDNT_CONNECT = 7
            7 => FailureType.ConnectionTimeout,

            // CURLE_COULDNT_RESOLVE_HOST = 6
            6 => FailureType.ConnectionTimeout,

            // HTTP errors (CURLE_HTTP_RETURNED_ERROR = 22)
            22 => FailureType.HttpError,

            // Authentication errors (CURLE_AUTH_ERROR = 94)
            94 => FailureType.AuthenticationFailed,

            // CURLE_TOO_MANY_REDIRECTS = 47
            47 => FailureType.CapacityExceeded,

            // CURLE_RECV_ERROR = 56 (data stall)
            56 => FailureType.DataStall,

            // CURLE_SEND_ERROR = 55
            55 => FailureType.DataStall,

            // CURLE_PARTIAL_FILE = 18 (incomplete transfer)
            18 => FailureType.DataStall,

            // Default to unknown for other errors
            _ => FailureType.Unknown,
        };
    }

    /// <summary>
    /// Maps an HTTP status code to a <see cref="FailureType"/>.
    /// </summary>
    /// <param name="httpStatus">The HTTP status code.</param>
    /// <returns>The corresponding failure type.</returns>
    public static FailureType MapHttpStatusToFailureType(int httpStatus)
    {
        return httpStatus switch
        {
            401 or 403 => FailureType.AuthenticationFailed,
            429 => FailureType.CapacityExceeded,
            >= 500 and < 600 => FailureType.HttpError,
            >= 400 and < 500 => FailureType.HttpError,
            _ => FailureType.Unknown,
        };
    }
}
