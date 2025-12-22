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
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Interface for Discord notification service.
/// </summary>
public interface IDiscordNotificationService
{
    /// <summary>
    /// Sends a buffer overflow notification to Discord.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="channelName">The channel name.</param>
    /// <param name="overflowCount">Number of overflow events.</param>
    /// <param name="lostMB">Megabytes of data lost.</param>
    /// <param name="totalLostMB">Total megabytes lost.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task NotifyBufferOverflowAsync(
        string streamId,
        string channelName,
        int overflowCount,
        double lostMB,
        double totalLostMB,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Sends a stream start notification to Discord.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="channelName">The channel name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task NotifyStreamStartAsync(string streamId, string channelName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a stream error notification to Discord.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="channelName">The channel name.</param>
    /// <param name="errorMessage">The error message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task NotifyStreamErrorAsync(
        string streamId,
        string channelName,
        string errorMessage,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Sends a stream killed notification to Discord.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="channelName">The channel name.</param>
    /// <param name="reason">The reason the stream was killed.</param>
    /// <param name="duration">How long the stream was running.</param>
    /// <param name="bytesTransferred">Total bytes transferred during the stream.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task NotifyStreamKilledAsync(
        string streamId,
        string channelName,
        string reason,
        TimeSpan duration,
        long bytesTransferred,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Sends buffer diagnostics for a specific stream to Discord.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="channelName">The channel name.</param>
    /// <param name="diagnostics">Diagnostic information string.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SendBufferDiagnosticsAsync(
        string streamId,
        string channelName,
        string diagnostics,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Sends a buffer health issue notification to Discord.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="channelName">The channel name.</param>
    /// <param name="underrunCount">Number of buffer underrun events.</param>
    /// <param name="fillPercentage">Current buffer fill percentage.</param>
    /// <param name="currentBitrate">Current stream bitrate in Mbps.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task NotifyBufferHealthIssueAsync(
        string streamId,
        string channelName,
        int underrunCount,
        double fillPercentage,
        double currentBitrate,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Tests the Discord webhook configuration.
    /// </summary>
    /// <param name="webhookUrl">The webhook URL to test.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the test succeeded, false otherwise.</returns>
    Task<bool> TestWebhookAsync(string webhookUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends MPEG-TS indexer diagnostics to Discord.
    /// Reports stream health metrics including packet loss, PCR jitter, and transport errors.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="channelName">The channel name.</param>
    /// <param name="diagnostics">The TsIndexer diagnostics string from GetDiagnostics().</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SendTsIndexerDiagnosticsAsync(
        string streamId,
        string channelName,
        string diagnostics,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Sends a TR 101 290 stream quality violation notification to Discord.
    /// Used to report issues like missing PCR values, version changes, or jitter violations.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="channelName">The channel name.</param>
    /// <param name="violationType">The type of violation (e.g., "PCR PID Invalid", "PAT Version Change").</param>
    /// <param name="details">Detailed description of the violation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task NotifyStreamQualityViolationAsync(
        string streamId,
        string channelName,
        string violationType,
        string details,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Sends an EPG refresh started notification to Discord.
    /// </summary>
    /// <param name="channelCount">The number of channels to refresh.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task NotifyEpgRefreshStartedAsync(int channelCount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an EPG refresh completion notification to Discord.
    /// Reports success, partial completion, or failure with diagnostic details.
    /// </summary>
    /// <param name="result">The EPG refresh result containing success count, total, duration, and any errors.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task NotifyEpgRefreshAsync(EpgRefreshResult result, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an A/V synchronization drift notification to Discord.
    /// Reports when audio and video streams are out of sync beyond acceptable thresholds.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="channelName">The channel name.</param>
    /// <param name="driftMs">The current drift in milliseconds. Positive = audio ahead, negative = audio behind.</param>
    /// <param name="status">The sync status (AudioAhead, AudioBehind, etc.).</param>
    /// <param name="peakDriftMs">The peak drift observed during the stream.</param>
    /// <param name="violationCount">The total number of drift violations detected.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task NotifyAVDriftAsync(
        string streamId,
        string channelName,
        double driftMs,
        string status,
        double peakDriftMs,
        long violationCount,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Sends a provider connection limit notification to Discord.
    /// Reports when a provider reaches its connection limit or connections become available.
    /// </summary>
    /// <param name="providerName">The provider name.</param>
    /// <param name="activeConnections">Current active connections.</param>
    /// <param name="maxConnections">Maximum connections allowed.</param>
    /// <param name="isAtLimit">Whether the provider is at its connection limit.</param>
    /// <param name="externalConnections">Number of connections from external sources (not this plugin).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task NotifyConnectionLimitChangeAsync(
        string providerName,
        int activeConnections,
        int maxConnections,
        bool isAtLimit,
        int externalConnections = 0,
        CancellationToken cancellationToken = default
    );
}
