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

using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.MpegTs.Infrastructure;

/// <summary>
/// Hosted service that initializes FFmpeg on startup using Jellyfin's media encoder.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="FFmpegInitializationService"/> class.
/// </remarks>
/// <param name="mediaEncoder">Jellyfin's media encoder.</param>
/// <param name="logger">Logger for diagnostics.</param>
public sealed class FFmpegInitializationService(IMediaEncoder mediaEncoder, ILogger<FFmpegInitializationService> logger)
    : IHostedService
{
    private readonly IMediaEncoder _mediaEncoder = mediaEncoder;
    private readonly ILogger<FFmpegInitializationService> _logger = logger;

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.PluginLogInformation("Registering FFmpeg media encoder for lazy initialization...");

        // Register the encoder reference for lazy initialization
        // This allows FFmpegContext.IsAvailable to trigger initialization on first access
        // even if IMediaEncoder.EncoderPath is empty during startup
        FFmpegContext.RegisterMediaEncoder(_mediaEncoder, _logger);

        // Try immediate initialization (may fail if Jellyfin startup not complete)
        if (FFmpegContext.Initialize(_mediaEncoder, _logger))
        {
            _logger.PluginLogInformation(
                "FFmpeg initialized successfully. Path: {Path}, Version: {Version}",
                FFmpegContext.FFmpegPath,
                FFmpegContext.GetVersionString()
            );
        }
        else
        {
            // Not an error - will retry lazily when first stream is opened
            _logger.LogDebugIfEnabled(
                "FFmpeg initialization deferred - IMediaEncoder may not be ready yet. "
                    + "Will retry when first stream is opened."
            );
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) =>
        // Nothing to clean up - FFmpeg.AutoGen manages library lifecycle
        Task.CompletedTask;
}
