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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Api.Models;
using Jellyfin.Xtream.Service;
using Jellyfin.Xtream.Service.Epg;
using Jellyfin.Xtream.Service.Logging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Api;

/// <summary>
/// EPG-related endpoints: testing, status, and refresh operations.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="XtreamEpgController"/> class.
/// </remarks>
/// <param name="logger">The logger instance.</param>
[ApiController]
[Route("Xtream")]
[Produces("application/json")]
public class XtreamEpgController(ILogger<XtreamEpgController> logger) : ControllerBase
{
    private readonly ILogger<XtreamEpgController> _logger = logger;

    /// <summary>
    /// Test EPG data retrieval for a specific channel.
    /// </summary>
    /// <param name="streamId">The stream ID to get EPG for.</param>
    /// <param name="epgProvider">The EPG provider service.</param>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>EPG test response with programs.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("EpgTest/{streamId}")]
    public async Task<ActionResult<EpgTestResponse>> GetEpgTest(
        int streamId,
        [FromServices] IEpgProvider epgProvider,
        CancellationToken cancellationToken
    )
    {
        var response = new EpgTestResponse { StreamId = streamId, Provider = epgProvider.Name };

        try
        {
            response.ChannelName =
                (await StreamService.GetAllLiveStreams(cancellationToken).ConfigureAwait(false))
                    .FirstOrDefault(s => s.Stream.StreamId == streamId)
                    ?.Stream.Name
                ?? $"Stream {streamId}";

            response.Programs =
            [
                .. (await epgProvider.GetProgramsAsync(streamId, cancellationToken).ConfigureAwait(false)).Select(
                    p => new EpgProgramResponse
                    {
                        Id = p.Id,
                        Title = p.Title,
                        Description = p.Description,
                        StartUtc = p.StartUtc,
                        EndUtc = p.EndUtc,
                        ImageUrl = p.ImageUrl,
                    }
                ),
            ];

            response.Success = true;
            _logger.PluginLogInformation(
                "EPG test for stream {StreamId}: {Count} programs from {Provider}",
                streamId,
                response.Programs.Count,
                epgProvider.Name
            );
        }
        catch (Exception ex)
        {
            _logger.PluginLogError(ex, "EPG test failed for stream {StreamId}", streamId);
            response.Success = false;
            response.ErrorMessage = "EPG test failed. Check server logs for details.";
        }

        return Ok(response);
    }

    /// <summary>
    /// Get EPG provider status information.
    /// </summary>
    /// <param name="epgProvider">The EPG provider service.</param>
    /// <returns>EPG provider status.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("EpgStatus")]
    public ActionResult<object> GetEpgStatus([FromServices] IEpgProvider epgProvider)
    {
        return Ok(
            new
            {
                providerName = epgProvider.Name,
                isAvailable = epgProvider.IsAvailable,
                priority = epgProvider.Priority,
            }
        );
    }

    /// <summary>
    /// Refresh EPG data for all channels in parallel.
    /// This pre-warms the EPG cache for faster guide loading.
    /// </summary>
    /// <param name="liveTvService">The Live TV service.</param>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>Result with success count and total channels.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("RefreshEpg")]
    public async Task<ActionResult<object>> RefreshEpgParallel(
        [FromServices] LiveTvService liveTvService,
        CancellationToken cancellationToken
    )
    {
        _logger.PluginLogInformation("Starting parallel EPG refresh via API");

        try
        {
            var (successCount, totalCount) = await liveTvService
                .RefreshAllEpgParallelAsync(cancellationToken)
                .ConfigureAwait(false);
            return Ok(
                new
                {
                    success = true,
                    successCount,
                    totalCount,
                    message = $"EPG refresh complete: {successCount}/{totalCount} channels with EPG data",
                }
            );
        }
        catch (OperationCanceledException)
        {
            return Ok(
                new
                {
                    success = false,
                    successCount = 0,
                    totalCount = 0,
                    message = "EPG refresh was cancelled",
                }
            );
        }
        catch (Exception ex)
        {
            _logger.PluginLogError(ex, "EPG refresh failed");
            return Ok(
                new
                {
                    success = false,
                    successCount = 0,
                    totalCount = 0,
                    message = "EPG refresh failed. Check server logs for details.",
                }
            );
        }
    }
}
