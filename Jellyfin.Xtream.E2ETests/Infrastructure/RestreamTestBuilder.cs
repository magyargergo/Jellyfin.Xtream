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

using System.Net;
using Jellyfin.Xtream.Service;
using MediaBrowser.Controller;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Jellyfin.Xtream.E2ETests.Infrastructure;

/// <summary>
/// Fluent builder for creating <see cref="Restream"/> instances in E2E tests.
/// Handles mock setup and provides test-friendly defaults.
/// </summary>
public sealed class RestreamTestBuilder
{
    private readonly List<string> _urls = new();
    private readonly List<double> _scores = new();
    private string _channelId = Guid.NewGuid().ToString();
    private string _channelName = "Test Channel";
    private string _qualityHint = "HD";
    private ILoggerFactory? _loggerFactory;

    /// <summary>
    /// Adds URLs to the URL list for the restream.
    /// </summary>
    /// <param name="urls">The URLs to add.</param>
    /// <returns>This builder for chaining.</returns>
    public RestreamTestBuilder WithUrls(params string[] urls)
    {
        _urls.AddRange(urls);
        return this;
    }

    /// <summary>
    /// Sets the initial health scores for URLs.
    /// </summary>
    /// <param name="scores">The scores in the same order as URLs.</param>
    /// <returns>This builder for chaining.</returns>
    public RestreamTestBuilder WithInitialScores(params double[] scores)
    {
        _scores.AddRange(scores);
        return this;
    }

    /// <summary>
    /// Sets the channel ID for the media source.
    /// </summary>
    /// <param name="channelId">The channel ID.</param>
    /// <returns>This builder for chaining.</returns>
    public RestreamTestBuilder WithChannelId(string channelId)
    {
        _channelId = channelId;
        return this;
    }

    /// <summary>
    /// Sets the channel name for the media source.
    /// </summary>
    /// <param name="channelName">The channel name.</param>
    /// <returns>This builder for chaining.</returns>
    public RestreamTestBuilder WithChannelName(string channelName)
    {
        _channelName = channelName;
        return this;
    }

    /// <summary>
    /// Sets the quality hint for buffer sizing (SD, HD, Full HD, UHD/4K).
    /// </summary>
    /// <param name="quality">The quality hint.</param>
    /// <returns>This builder for chaining.</returns>
    public RestreamTestBuilder WithQuality(string quality)
    {
        _qualityHint = quality;
        return this;
    }

    /// <summary>
    /// Sets a custom logger factory for the restream.
    /// </summary>
    /// <param name="loggerFactory">The logger factory to use.</param>
    /// <returns>This builder for chaining.</returns>
    public RestreamTestBuilder WithLoggerFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        return this;
    }

    /// <summary>
    /// Builds the <see cref="Restream"/> instance with the configured options.
    /// </summary>
    /// <returns>A new <see cref="Restream"/> instance.</returns>
    /// <exception cref="InvalidOperationException">Thrown if no URLs have been configured.</exception>
    public Restream Build()
    {
        if (_urls.Count == 0)
        {
            throw new InvalidOperationException("At least one URL must be configured");
        }

        // Create mock IServerApplicationHost
        var appHost = Substitute.For<IServerApplicationHost>();
        appHost.GetSmartApiUrl(Arg.Any<IPAddress>()).Returns("http://localhost:8096");
        appHost.GetApiUrlForLocalAccess().Returns("http://127.0.0.1:8096");

        // Create logger factory
        var loggerFactory = _loggerFactory ?? CreateTestLoggerFactory();

        // Create media source with quality hint in name
        var mediaSource = new MediaSourceInfo
        {
            Id = _channelId,
            Name = _qualityHint switch
            {
                "4K" or "UHD" => $"{_channelName} 4K UHD",
                "Full HD" or "FHD" => $"{_channelName} FHD 1080p",
                "HD" => $"{_channelName} HD 720p",
                "SD" => $"{_channelName} SD",
                _ => _channelName,
            },
        };

        var logger = loggerFactory.CreateLogger<Restream>();
        var scores = _scores.Count > 0 ? _scores : null;

        return new Restream(appHost, logger, loggerFactory, mediaSource, _urls, scores, discordService: null);
    }

    private static ILoggerFactory CreateTestLoggerFactory()
    {
        return LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddConsole();
        });
    }
}
