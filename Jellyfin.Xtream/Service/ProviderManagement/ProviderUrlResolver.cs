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
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.ProviderManagement;

/// <summary>
/// Resolves alternative provider URLs for hot-swap operations.
/// Delegates provider selection to IAutomaticFailoverService (source of truth)
/// which combines health scoring, circuit breaker state, metrics, and trend analysis.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ProviderUrlResolver"/> class.
/// </remarks>
/// <param name="failoverService">The automatic failover service (source of truth for provider selection).</param>
/// <param name="logger">The logger.</param>
/// <param name="configProvider">Configuration provider.</param>
public sealed class ProviderUrlResolver(
    IAutomaticFailoverService failoverService,
    ILogger<ProviderUrlResolver> logger,
    IPluginConfigurationProvider? configProvider = null
) : IProviderUrlResolver
{
    private readonly IAutomaticFailoverService _failoverService =
        failoverService ?? throw new ArgumentNullException(nameof(failoverService));
    private readonly ILogger<ProviderUrlResolver> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IPluginConfigurationProvider _configProvider = configProvider ?? new PluginConfigurationProvider();

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
            _logger.LogDebugIfEnabled("Could not extract provider ID from URL: {Url}", currentUrl);
            return Task.FromResult<string?>(null);
        }

        // Get configuration
        var config = _configProvider.GetConfiguration();
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

        // Extract stream ID from URL for ProviderStreamInfo creation
        var extractedStreamId = ExtractStreamId(currentUrl);

        // Build provider stream info list, excluding current provider
        var alternativeUrl = FindBestAlternative(providers, currentProviderId, currentUrl, extractedStreamId, reason);

        return Task.FromResult(alternativeUrl);
    }

    /// <summary>
    /// Finds the best alternative provider URL using AutomaticFailoverService as the source of truth.
    /// The failover service combines health scoring, circuit breaker state, metrics, and trend analysis.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private string? FindBestAlternative(
        List<XtreamProvider> providers,
        string currentProviderId,
        string currentUrl,
        int streamId,
        SwitchReason reason
    )
    {
        // Build ProviderStreamInfo list for failover service (excluding current provider)
        var alternatives = new List<ProviderStreamInfo>();

        // Create a minimal StreamInfo with the extracted stream ID
        var minimalStreamInfo = new StreamInfo { StreamId = streamId, Name = string.Empty };

        foreach (var provider in providers)
        {
            if (!provider.Enabled)
            {
                continue;
            }

            if (string.Equals(provider.Id, currentProviderId, StringComparison.Ordinal))
            {
                continue;
            }

            // Create ProviderStreamInfo for each enabled alternative using proper record constructor
            alternatives.Add(new ProviderStreamInfo(provider, minimalStreamInfo));
        }

        if (alternatives.Count == 0)
        {
            _logger.PluginLogWarning(
                "No alternative providers available for {CurrentProvider} (reason: {Reason})",
                currentProviderId,
                reason
            );
            return null;
        }

        // Delegate to AutomaticFailoverService for optimal provider selection
        // This is the source of truth for provider health and selection
        var orderedProviders = _failoverService.GetOrderedProviders(alternatives);

        if (orderedProviders.Count == 0)
        {
            _logger.PluginLogWarning(
                "AutomaticFailoverService returned no available providers for {CurrentProvider} (reason: {Reason})",
                currentProviderId,
                reason
            );
            return null;
        }

        var bestProvider = orderedProviders[0];

        // Build the alternative URL using the best provider
        var alternativeUrl = BuildAlternativeUrl(currentUrl, bestProvider.Provider);

        _logger.PluginLogInformation(
            "Hot-swap: selecting {Provider} from {CandidateCount} candidates for reason {Reason} (via AutomaticFailoverService)",
            bestProvider.Provider.Name,
            orderedProviders.Count,
            reason
        );

        return alternativeUrl;
    }

    /// <summary>
    /// Extracts the stream ID from a URL.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ExtractStreamId(string url)
    {
        // Xtream URLs typically end with /streamId.ts or /streamId
        // Example: http://provider.com/user/pass/12345.ts
        try
        {
            var uri = new Uri(url);
            var path = uri.AbsolutePath;
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0)
            {
                return 0;
            }

            // Get the last part (stream ID with optional extension)
            var lastPart = parts[^1];

            // Remove extension if present (.ts, .m3u8, etc.)
            var dotIndex = lastPart.LastIndexOf('.');
            if (dotIndex > 0)
            {
                lastPart = lastPart[..dotIndex];
            }

            return int.TryParse(lastPart, out var id) ? id : 0;
        }
        catch
        {
            return 0;
        }
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
            var config = _configProvider.GetConfiguration();
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
        var prefix = string.Empty;
        if (parts[0].Equals("live", StringComparison.OrdinalIgnoreCase))
        {
            prefix = "/live";
        }

        // Get the stream ID (last part)
        var streamIdPart = parts[^1];

        // Rebuild with new credentials
        return $"{prefix}/{newProvider.Username}/{newProvider.Password}/{streamIdPart}";
    }
}
