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
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.Xtream.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Client;

/// <summary>
/// Thread-safe User-Agent provider with configurable rotation strategies.
/// Provides browser-like User-Agent strings to avoid WAF/CDN blocking.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="UserAgentProvider"/> class.
/// </remarks>
/// <param name="getConfiguration">Function to retrieve current configuration.</param>
/// <param name="logger">Logger instance.</param>
public sealed class UserAgentProvider(Func<PluginConfiguration> getConfiguration, ILogger<UserAgentProvider> logger)
    : IUserAgentProvider
{
    private static readonly FrozenDictionary<BrowserFamily, string[]> UserAgentsByFamily = CreateUserAgentDictionary();

    private readonly Func<PluginConfiguration> _getConfiguration = getConfiguration;
    private readonly ILogger<UserAgentProvider> _logger = logger;
    private readonly object _lock = new();
    private int _roundRobinIndex = Random.Shared.Next(TotalUserAgentCount);
    private string? _cachedCustomUserAgent;

    private enum BrowserFamily
    {
        Chrome,
        Firefox,
        Edge,
        Safari,
        Opera,
    }

    private static int TotalUserAgentCount
    {
        get
        {
            int count = 0;
            foreach (var agents in UserAgentsByFamily.Values)
            {
                count += agents.Length;
            }

            return count;
        }
    }

    /// <inheritdoc />
    public string GetUserAgent()
    {
        var config = _getConfiguration();

        // Custom User-Agent takes precedence
        var customUserAgent = config.CustomUserAgent?.Trim();
        if (!string.IsNullOrEmpty(customUserAgent))
        {
            if (_cachedCustomUserAgent != customUserAgent)
            {
                _cachedCustomUserAgent = customUserAgent;
                _logger.LogDebug("Using custom User-Agent: {UserAgent}", TruncateForLogging(customUserAgent));
            }

            return customUserAgent;
        }

        // Select User-Agent based on rotation strategy
        return config.UseRandomUserAgent ? GetRandomUserAgent() : GetRoundRobinUserAgent();
    }

    private static FrozenDictionary<BrowserFamily, string[]> CreateUserAgentDictionary()
    {
        return new Dictionary<BrowserFamily, string[]>
        {
            [BrowserFamily.Chrome] =
            [
                // Chrome 131 (December 2024) - Windows
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
                "Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
                // Chrome 131 - macOS
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
                // Chrome 131 - Linux
                "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
                // Chrome 130 - Windows (fallback)
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36",
                // Chrome 130 - macOS (fallback)
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36",
            ],

            [BrowserFamily.Firefox] =
            [
                // Firefox 133 (December 2024) - Windows
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:133.0) Gecko/20100101 Firefox/133.0",
                // Firefox 133 - macOS
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 14.0; rv:133.0) Gecko/20100101 Firefox/133.0",
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:133.0) Gecko/20100101 Firefox/133.0",
                // Firefox 133 - Linux
                "Mozilla/5.0 (X11; Linux x86_64; rv:133.0) Gecko/20100101 Firefox/133.0",
                "Mozilla/5.0 (X11; Ubuntu; Linux x86_64; rv:133.0) Gecko/20100101 Firefox/133.0",
                // Firefox 132 (fallback)
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:132.0) Gecko/20100101 Firefox/132.0",
            ],

            [BrowserFamily.Edge] =
            [
                // Edge 131 (Chromium-based) - Windows
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0",
                // Edge 131 - macOS
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0",
                // Edge 130 (fallback)
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0",
            ],

            [BrowserFamily.Safari] =
            [
                // Safari 17.2 - macOS Sonoma
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_0) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Safari/605.1.15",
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_1) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Safari/605.1.15",
                // Safari 17.1 - macOS Ventura
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 13_6) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.1 Safari/605.1.15",
                // Safari 16.6 - macOS (fallback)
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.6 Safari/605.1.15",
            ],

            [BrowserFamily.Opera] =
            [
                // Opera 115 (Chromium-based)
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 OPR/115.0.0.0",
                // Opera 114
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36 OPR/114.0.0.0",
                // Opera - macOS
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 OPR/115.0.0.0",
            ],
        }.ToFrozenDictionary();
    }

    private string GetRandomUserAgent()
    {
        // Weighted random: Chrome has highest market share, so higher probability
        var familyRoll = Random.Shared.Next(100);
        BrowserFamily family = familyRoll switch
        {
            < 65 => BrowserFamily.Chrome,
            < 80 => BrowserFamily.Edge,
            < 90 => BrowserFamily.Firefox,
            < 97 => BrowserFamily.Safari,
            _ => BrowserFamily.Opera,
        };

        var agents = UserAgentsByFamily[family];
        var index = Random.Shared.Next(agents.Length);
        return agents[index];
    }

    private string GetRoundRobinUserAgent()
    {
        // Flatten all User-Agents into a single rotation
        var index = Interlocked.Increment(ref _roundRobinIndex);
        if (index < 0)
        {
            // Handle overflow
            lock (_lock)
            {
                if (_roundRobinIndex < 0)
                {
                    _roundRobinIndex = 0;
                }
            }

            index = Interlocked.Increment(ref _roundRobinIndex);
        }

        var totalCount = TotalUserAgentCount;
        var normalizedIndex = index % totalCount;

        foreach (var kvp in UserAgentsByFamily)
        {
            if (normalizedIndex < kvp.Value.Length)
            {
                return kvp.Value[normalizedIndex];
            }

            normalizedIndex -= kvp.Value.Length;
        }

        // Fallback (should never reach here)
        return UserAgentsByFamily[BrowserFamily.Chrome][0];
    }

    private static string TruncateForLogging(string value)
    {
        const int maxLength = 50;
        return value.Length > maxLength ? string.Concat(value.AsSpan(0, maxLength), "...") : value;
    }
}
