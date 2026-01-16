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
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Client;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.Discovery.Pipeline.Stages;

/// <summary>
/// Second pipeline stage: API authentication validation.
/// Verifies credentials work and account is active.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="AuthenticationStage"/> class.
/// </remarks>
/// <param name="httpClientFactory">HTTP client factory.</param>
/// <param name="logger">The logger.</param>
public sealed class AuthenticationStage(IHttpClientFactory httpClientFactory, ILogger logger)
    : PipelineStageBase(
        PipelineStage.Authentication,
        logger,
        new StageConfiguration
        {
            Concurrency = 20,
            TimeoutMs = 10000,
            ContinueOnError = true,
        }
    )
{
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;

    /// <inheritdoc />
    protected override async ValueTask<StageResult<PipelineItem>> ProcessAsync(
        PipelineItem item,
        CancellationToken cancellationToken
    )
    {
        var credential = item.Credential;

        try
        {
            using var client = new XtreamClient(_httpClientFactory);
            var connectionInfo = new ConnectionInfo(credential.BaseUrl, credential.Username, credential.Password);

            var info = await client.GetUserAndServerInfoAsync(connectionInfo, cancellationToken).ConfigureAwait(false);

            if (info?.UserInfo == null)
            {
                return StageResult.Fail<PipelineItem>("No response");
            }

            var status = info.UserInfo.Status?.ToUpperInvariant();
            if (status != "ACTIVE")
            {
                return StageResult.Fail<PipelineItem>($"Status: {info.UserInfo.Status ?? "Unknown"}");
            }

            // Attach account info to item
            var enrichedItem = item.WithProperty(PipelinePropertyKeys.AuthInfo, connectionInfo)
                .WithProperty(PipelinePropertyKeys.MaxConnections, info.UserInfo.MaxConnections)
                .WithProperty(PipelinePropertyKeys.ActiveConnections, info.UserInfo.ActiveCons);

            if (info.UserInfo.ExpDate.HasValue)
            {
                enrichedItem = enrichedItem.WithProperty(
                    PipelinePropertyKeys.ExpirationDate,
                    info.UserInfo.ExpDate.Value
                );
            }

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
}
