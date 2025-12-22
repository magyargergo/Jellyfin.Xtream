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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Utility;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Cached connection status for a provider.
/// </summary>
public sealed class CachedProviderStatus
{
    /// <summary>
    /// Gets or sets the provider ID.
    /// </summary>
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the maximum connections allowed.
    /// </summary>
    public int MaxConnections { get; set; }

    /// <summary>
    /// Gets or sets the current active connections reported by the provider.
    /// </summary>
    public int ActiveConnections { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the provider is online.
    /// </summary>
    public bool IsOnline { get; set; }

    /// <summary>
    /// Gets or sets when this status was last updated.
    /// </summary>
    public DateTime LastUpdated { get; set; }

    /// <summary>
    /// Gets or sets the account status (Active, Expired, etc.).
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the account expiration date.
    /// </summary>
    public DateTime? ExpirationDate { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the account is a trial.
    /// </summary>
    public bool IsTrial { get; set; }

    /// <summary>
    /// Gets or sets any error message if connection info couldn't be retrieved.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Gets the number of available connection slots.
    /// </summary>
    public int AvailableSlots => Math.Max(0, MaxConnections - ActiveConnections);

    /// <summary>
    /// Gets the utilization percentage (0-100).
    /// </summary>
    public int UtilizationPercent =>
        MaxConnections > 0 ? (int)Math.Round(100.0 * ActiveConnections / MaxConnections) : 100;

    /// <summary>
    /// Gets a value indicating whether the provider has available capacity.
    /// </summary>
    public bool HasCapacity => IsOnline && AvailableSlots > 0;
}

/// <summary>
/// Service that caches provider connection status for use in channel ordering.
/// Sends Discord notifications when providers reach or leave connection limits.
/// </summary>
public sealed class ProviderConnectionCache : IDisposable
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ProviderConnectionCache> _logger;
    private readonly IDiscordNotificationService _discordNotificationService;
    private readonly ConcurrentDictionary<string, CachedProviderStatus> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _previousAtLimitState = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromMinutes(2);
    private DateTime _lastFullRefresh = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderConnectionCache"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="discordNotificationService">Discord notification service.</param>
    public ProviderConnectionCache(
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        IDiscordNotificationService discordNotificationService
    )
    {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ProviderConnectionCache>();
        _discordNotificationService = discordNotificationService;
    }

    /// <summary>
    /// Gets the cached status for a provider.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>The cached status, or null if not available.</returns>
    public CachedProviderStatus? GetStatus(string providerId)
    {
        if (_cache.TryGetValue(providerId, out var status))
        {
            // Check if still valid
            if (DateTime.UtcNow - status.LastUpdated < _cacheExpiry)
            {
                return status;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets all cached provider statuses.
    /// </summary>
    /// <returns>A dictionary of provider ID to status.</returns>
    public IReadOnlyDictionary<string, CachedProviderStatus> GetAllStatuses()
    {
        var now = DateTime.UtcNow;
        return _cache
            .Where(kvp => now - kvp.Value.LastUpdated < _cacheExpiry)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// Refreshes the connection status for all enabled providers.
    /// </summary>
    /// <param name="providers">The providers to refresh.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    public async Task RefreshAsync(IEnumerable<XtreamProvider> providers, CancellationToken cancellationToken = default)
    {
        // Avoid concurrent refreshes
        if (!await _refreshLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var providerList = providers.ToList();
            _logger.LogDebug("Refreshing connection status for {Count} providers", providerList.Count);

            var tasks = providerList.Select(provider => RefreshProviderAsync(provider, cancellationToken));
            await Task.WhenAll(tasks).ConfigureAwait(false);

            _lastFullRefresh = DateTime.UtcNow;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Refreshes the connection status for a single provider.
    /// Sends Discord notifications when connection limit state changes.
    /// </summary>
    /// <param name="provider">The provider to refresh.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated status.</returns>
    public async Task<CachedProviderStatus> RefreshProviderAsync(
        XtreamProvider provider,
        CancellationToken cancellationToken = default
    )
    {
        var status = new CachedProviderStatus { ProviderId = provider.Id, LastUpdated = DateTime.UtcNow };

        try
        {
            using var client = new XtreamClient(_httpClientFactory, _loggerFactory.CreateLogger<XtreamClient>());
            var playerApi = await client
                .GetUserAndServerInfoAsync(provider.ToConnectionInfo(), cancellationToken)
                .ConfigureAwait(false);

            if (playerApi?.UserInfo != null)
            {
                var userInfo = playerApi.UserInfo;
                status.MaxConnections = userInfo.MaxConnections;
                status.ActiveConnections = userInfo.ActiveCons;
                status.Status = userInfo.Status ?? "Unknown";
                status.ExpirationDate = userInfo.ExpDate;
                status.IsTrial = userInfo.IsTrial;
                status.IsOnline = true;

                // Check for connection limit state change and notify
                await CheckAndNotifyLimitChangeAsync(provider, status, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                status.ErrorMessage = "Failed to get user info";
                status.IsOnline = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get connection status for provider {ProviderId}", provider.Id);
            status.ErrorMessage = ex.Message;
            status.IsOnline = false;
        }

        _cache[provider.Id] = status;
        return status;
    }

    /// <summary>
    /// Checks if the connection limit state has changed and sends a Discord notification if so.
    /// </summary>
    private async Task CheckAndNotifyLimitChangeAsync(
        XtreamProvider provider,
        CachedProviderStatus status,
        CancellationToken cancellationToken
    )
    {
        if (status.MaxConnections <= 0)
        {
            return; // Unknown max, can't track state
        }

        bool currentlyAtLimit = status.ActiveConnections >= status.MaxConnections;
        bool previouslyAtLimit = _previousAtLimitState.TryGetValue(provider.Id, out var wasAtLimit) && wasAtLimit;

        // Detect state transition
        if (currentlyAtLimit != previouslyAtLimit)
        {
            // Calculate external connections (connections not from this plugin)
            // This is useful info even though we can't be sure without tracking our own usage
            int externalConnections = status.ActiveConnections;

            _logger.LogDebugIfEnabled(
                "Provider {Provider} connection limit state changed: {Previous} -> {Current} ({Active}/{Max})",
                provider.Name,
                previouslyAtLimit ? "AtLimit" : "Available",
                currentlyAtLimit ? "AtLimit" : "Available",
                status.ActiveConnections,
                status.MaxConnections
            );

            // Send Discord notification
            await _discordNotificationService
                .NotifyConnectionLimitChangeAsync(
                    provider.Name,
                    status.ActiveConnections,
                    status.MaxConnections,
                    currentlyAtLimit,
                    externalConnections,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        // Update tracked state
        _previousAtLimitState[provider.Id] = currentlyAtLimit;
    }

    /// <summary>
    /// Checks if a refresh is needed based on cache age.
    /// </summary>
    /// <returns>True if refresh is recommended.</returns>
    public bool NeedsRefresh()
    {
        return DateTime.UtcNow - _lastFullRefresh > _cacheExpiry;
    }

    /// <summary>
    /// Calculates an availability score for a provider (0-100).
    /// Higher score = more available capacity = better choice.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>Availability score, or 50 (neutral) if status unknown.</returns>
    public int GetAvailabilityScore(string providerId)
    {
        var status = GetStatus(providerId);
        if (status == null || !status.IsOnline)
        {
            return 0; // Offline or unknown = worst score
        }

        if (status.MaxConnections <= 0)
        {
            return 50; // Unknown capacity = neutral
        }

        // Score based on available slots
        // More available slots = higher score
        // At capacity = 0, 1 slot = 20, 2 slots = 40, etc.
        var availableScore = Math.Min(100, status.AvailableSlots * 20);

        // Also factor in utilization - prefer less utilized providers
        var utilizationPenalty = status.UtilizationPercent / 2; // 0-50 penalty

        return Math.Max(0, availableScore - utilizationPenalty);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _refreshLock.Dispose();
    }
}
