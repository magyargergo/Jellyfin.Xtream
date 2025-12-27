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
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Utility;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Client;

/// <summary>
/// DelegatingHandler that adds a rotated User-Agent header to each request.
/// This ensures User-Agent rotation happens per-request rather than per-client.
/// </summary>
public sealed class UserAgentHandler : DelegatingHandler
{
    private readonly IUserAgentProvider _userAgentProvider;
    private readonly IPluginConfigurationProvider _configurationProvider;
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserAgentHandler"/> class.
    /// </summary>
    /// <param name="userAgentProvider">The User-Agent provider.</param>
    /// <param name="configurationProvider">Provider for current configuration.</param>
    /// <param name="logger">Optional logger.</param>
    public UserAgentHandler(
        IUserAgentProvider userAgentProvider,
        IPluginConfigurationProvider configurationProvider,
        ILogger? logger = null
    )
    {
        _userAgentProvider = userAgentProvider;
        _configurationProvider = configurationProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var config = _configurationProvider.GetConfiguration();

        // Only rotate User-Agent if rotation is enabled
        if (config?.EnableUserAgentRotation == true)
        {
            // Remove any existing User-Agent header
            request.Headers.Remove("User-Agent");

            // Get a fresh User-Agent for this request
            var userAgent = _userAgentProvider.GetUserAgent();
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);

            _logger?.LogDebugIfEnabled(
                "Request to {Host}: User-Agent rotated to {UserAgent}",
                request.RequestUri?.Host ?? "unknown",
                TruncateUserAgent(userAgent)
            );
        }

        return base.SendAsync(request, cancellationToken);
    }

    private static string TruncateUserAgent(string userAgent)
    {
        const int maxLength = 60;
        return userAgent.Length > maxLength ? $"{userAgent[..maxLength]}..." : userAgent;
    }
}
