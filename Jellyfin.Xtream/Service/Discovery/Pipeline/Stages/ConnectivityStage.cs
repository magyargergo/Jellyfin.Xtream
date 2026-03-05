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
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.Discovery.Pipeline.Stages;

/// <summary>
/// First pipeline stage: Fast TCP connectivity check.
/// Eliminates unreachable hosts before expensive API calls.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ConnectivityStage"/> class.
/// </remarks>
/// <param name="logger">The logger.</param>
/// <param name="timeoutMs">Connection timeout in milliseconds.</param>
public sealed class ConnectivityStage(ILogger logger, int timeoutMs = ConnectivityStage.DefaultTimeoutMs)
    : PipelineStageBase(
        PipelineStage.Connectivity,
        logger,
        new StageConfiguration
        {
            Concurrency = 50,
            TimeoutMs = timeoutMs + 50,
            ContinueOnError = true,
        }
    )
{
    private const int DefaultTimeoutMs = 100;

    private readonly int _timeoutMs = timeoutMs;

    /// <inheritdoc />
    protected override async ValueTask<StageResult<PipelineItem>> ProcessAsync(
        PipelineItem item,
        CancellationToken cancellationToken
    )
    {
        var credential = item.Credential;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_timeoutMs);

            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.NoDelay = true;

            await socket.ConnectAsync(credential.Server, credential.Port, timeoutCts.Token).ConfigureAwait(false);

            return StageResult.Pass(item);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StageResult.Fail<PipelineItem>("Timeout");
        }
        catch (SocketException ex)
        {
            var reason = ex.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => "Refused",
                SocketError.HostNotFound => "Not found",
                SocketError.HostUnreachable => "Unreachable",
                SocketError.NetworkUnreachable => "Network unreachable",
                SocketError.TimedOut => "Timeout",
                _ => $"Socket error: {ex.SocketErrorCode}",
            };
            return StageResult.Fail<PipelineItem>(reason);
        }
        catch (Exception ex)
        {
            return StageResult.Fail<PipelineItem>($"Error: {ex.Message}");
        }
    }
}
