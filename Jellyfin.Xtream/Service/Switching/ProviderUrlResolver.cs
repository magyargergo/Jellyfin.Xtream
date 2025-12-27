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
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service.ProviderManagement;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.Switching;

/// <summary>
/// Resolves alternative provider URLs for hot-swap operations.
/// Selects the best available provider based on health scores, capacity, and circuit breaker state.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ProviderUrlResolver"/> class.
/// </remarks>
/// <param name="resilienceService">The provider resilience service.</param>
/// <param name="logger">The logger.</param>
/// <param name="configProvider">Optional configuration provider.</param>
public sealed class ProviderUrlResolver(
    IProviderAvailabilityService resilienceService,
    ILogger<ProviderUrlResolver> logger,
    Func<PluginConfiguration?>? configProvider = null
) : IProviderUrlResolver
{
    private readonly IProviderAvailabilityService _resilienceService =
        resilienceService ?? throw new ArgumentNullException(nameof(resilienceService));
    private readonly ILogger<ProviderUrlResolver> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly Func<PluginConfiguration?> _configProvider = configProvider ?? GetDefaultConfiguration;

    /// <inheritdoc />
    public Task<string?> GetAlternativeUrlAsync(
        string streamId,
        string currentUrl,
        SwitchReason reason,
        CancellationToken cancellationToken
    )
    {
        // Extract current provider from URL
        var currentProviderId = ExtractProviderId(currentUrl);
        if (string.IsNullOrEmpty(currentProviderId))
        {
            _logger.LogDebug("Could not extract provider ID from URL: {Url}", currentUrl);
            return Task.FromResult<string?>(null);
        }

        // Get configuration
        var config = _configProvider();
        if (config == null)
        {
            return Task.FromResult<string?>(null);
        }

        // Get all providers from configuration
        var providers = config.Providers;
        if (providers == null || providers.Count == 0)
        {
            return Task.FromResult<string?>(null);
        }

        // Build provider stream info list, excluding current provider
        var alternativeUrl = FindBestAlternative(providers, currentProviderId, currentUrl, reason);

        return Task.FromResult(alternativeUrl);
    }

    /// <summary>
    /// Finds the best alternative provider URL.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private string? FindBestAlternative(
        List<XtreamProvider> providers,
        string currentProviderId,
        string currentUrl,
        SwitchReason reason
    )
    {
        XtreamProvider? bestProvider = null;
        int bestScore = -1;

        for (int i = 0; i < providers.Count; i++)
        {
            var provider = providers[i];

            // Skip current provider
            if (string.Equals(provider.Id, currentProviderId, StringComparison.Ordinal))
            {
                continue;
            }

            // Skip disabled providers
            if (!provider.Enabled)
            {
                continue;
            }

            // Check if provider is available (circuit breaker not open)
            if (!_resilienceService.IsAvailable(provider.Id))
            {
                _logger.LogDebug("Skipping provider {Provider} - circuit breaker open", provider.Name);
                continue;
            }

            // Check capacity
            if (!_resilienceService.HasCapacity(provider.Id))
            {
                _logger.LogDebug("Skipping provider {Provider} - no capacity", provider.Name);
                continue;
            }

            // Get combined health score
            var score = _resilienceService.GetSelectionScore(provider.Id);

            if (score > bestScore)
            {
                bestScore = score;
                bestProvider = provider;
            }
        }

        if (bestProvider == null)
        {
            _logger.LogDebug(
                "No alternative provider found for {CurrentProvider} (reason: {Reason})",
                currentProviderId,
                reason
            );
            return null;
        }

        // Build the alternative URL using the best provider
        var alternativeUrl = BuildAlternativeUrl(currentUrl, bestProvider);

        _logger.LogInformation(
            "Found alternative provider: {Provider} (score: {Score}) for reason: {Reason}",
            bestProvider.Name,
            bestScore,
            reason
        );

        return alternativeUrl;
    }

    /// <summary>
    /// Extracts the provider ID from a stream URL by matching against known providers.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private string? ExtractProviderId(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        try
        {
            var uri = new Uri(url);
            var config = _configProvider();
            if (config == null)
            {
                return null;
            }

            // Match URL host against known providers
            foreach (var provider in config.Providers)
            {
                if (string.IsNullOrEmpty(provider.BaseUrl))
                {
                    continue;
                }

                try
                {
                    var providerUri = new Uri(provider.BaseUrl);
                    if (
                        uri.Host.Equals(providerUri.Host, StringComparison.OrdinalIgnoreCase)
                        && uri.Port == providerUri.Port
                    )
                    {
                        return provider.Id;
                    }
                }
                catch
                {
                    // Skip invalid provider URLs
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Builds an alternative URL using a different provider.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string? BuildAlternativeUrl(string currentUrl, XtreamProvider newProvider)
    {
        try
        {
            var currentUri = new Uri(currentUrl);
            var providerUri = new Uri(newProvider.BaseUrl);

            // Extract the stream path (e.g., /username/password/12345.ts or /live/username/password/12345.ts)
            var path = currentUri.AbsolutePath;

            // Build new URI with the alternative provider's host
            var newUri = new UriBuilder
            {
                Scheme = providerUri.Scheme,
                Host = providerUri.Host,
                Port = providerUri.Port,
                Path = RebuildPath(path, newProvider),
            };

            return newUri.Uri.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Rebuilds the URL path with the new provider's credentials.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string RebuildPath(string originalPath, XtreamProvider newProvider)
    {
        // Xtream paths are typically: /username/password/streamId.ts or /live/username/password/streamId.ts
        var parts = originalPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 3)
        {
            return originalPath;
        }

        // Check if it starts with 'live'
        string prefix = string.Empty;
        if (parts[0].Equals("live", StringComparison.OrdinalIgnoreCase))
        {
            prefix = "/live";
        }

        // Get the stream ID (last part)
        var streamIdPart = parts[^1];

        // Rebuild with new credentials
        return $"{prefix}/{newProvider.Username}/{newProvider.Password}/{streamIdPart}";
    }

    private static PluginConfiguration? GetDefaultConfiguration()
    {
        try
        {
            return Plugin.Instance?.Configuration;
        }
        catch
        {
            return null;
        }
    }
}
