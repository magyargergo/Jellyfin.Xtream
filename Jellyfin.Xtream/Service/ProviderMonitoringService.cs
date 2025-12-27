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
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Api.Models;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service.ProviderManagement;
using Jellyfin.Xtream.Utility;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Interface for the provider monitoring service.
/// Provides cached monitoring data without impacting streaming.
/// </summary>
public interface IProviderMonitoringService
{
    /// <summary>
    /// Gets the last cached connection status response.
    /// Returns immediately without making any network calls.
    /// </summary>
    /// <returns>The cached connection status, or null if not yet available.</returns>
    ConnectionStatusResponse? GetCachedStatus();

    /// <summary>
    /// Gets the timestamp of the last successful refresh.
    /// </summary>
    DateTime? LastRefreshTime { get; }

    /// <summary>
    /// Gets a value indicating whether the service is currently refreshing.
    /// </summary>
    bool IsRefreshing { get; }

    /// <summary>
    /// Triggers an immediate refresh of the monitoring data.
    /// This is non-blocking and returns immediately.
    /// </summary>
    void TriggerRefresh();
}

/// <summary>
/// Background service that periodically refreshes provider monitoring data.
/// Ensures the monitoring dashboard can display data without impacting streaming.
/// </summary>
public sealed class ProviderMonitoringService : BackgroundService, IProviderMonitoringService
{
    private readonly IProviderAvailabilityService _resilienceService;
    private readonly ILogger<ProviderMonitoringService> _logger;
    private readonly TimeSpan _refreshInterval = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private ConnectionStatusResponse? _cachedStatus;
    private DateTime? _lastRefreshTime;
    private volatile bool _refreshRequested;
    private volatile bool _isRefreshing;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderMonitoringService"/> class.
    /// </summary>
    /// <param name="resilienceService">The resilience service.</param>
    /// <param name="logger">The logger.</param>
    public ProviderMonitoringService(
        IProviderAvailabilityService resilienceService,
        ILogger<ProviderMonitoringService> logger
    )
    {
        _resilienceService = resilienceService;
        _logger = logger;
    }

    /// <inheritdoc />
    public ConnectionStatusResponse? GetCachedStatus() => _cachedStatus;

    /// <inheritdoc />
    public DateTime? LastRefreshTime => _lastRefreshTime;

    /// <inheritdoc />
    public bool IsRefreshing => _isRefreshing;

    /// <inheritdoc />
    public void TriggerRefresh()
    {
        _refreshRequested = true;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogDebugIfEnabled("Provider monitoring service starting");

        // Initial delay to let the plugin fully initialize
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshMonitoringDataAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error refreshing monitoring data");
            }

            // Wait for next refresh interval or manual trigger
            var waitTime = _refreshInterval;
            while (waitTime > TimeSpan.Zero && !_refreshRequested && !stoppingToken.IsCancellationRequested)
            {
                var delay = TimeSpan.FromMilliseconds(Math.Min(1000, waitTime.TotalMilliseconds));
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                waitTime -= delay;
            }

            _refreshRequested = false;
        }

        _logger.LogDebugIfEnabled("Provider monitoring service stopped");
    }

    private async Task RefreshMonitoringDataAsync(CancellationToken cancellationToken)
    {
        if (!await _refreshLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return; // Already refreshing
        }

        try
        {
            _isRefreshing = true;

            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                return;
            }

            var enabledProviders = config.GetEnabledProviders().ToList();
            if (enabledProviders.Count == 0)
            {
                _cachedStatus = new ConnectionStatusResponse();
                _lastRefreshTime = DateTime.UtcNow;
                return;
            }

            // Refresh the resilience service (this makes HTTP calls to providers)
            await _resilienceService.RefreshAsync(enabledProviders, cancellationToken).ConfigureAwait(false);

            // Build the response from cached data
            var response = BuildStatusResponse(config, enabledProviders);

            _cachedStatus = response;
            _lastRefreshTime = DateTime.UtcNow;

            _logger.LogDebugIfEnabled(
                "Monitoring data refreshed: {ProviderCount} providers, {ActiveStreams} active streams, {Utilization}% utilization",
                response.Providers.Count,
                response.PluginActiveStreams,
                response.UtilizationPercent
            );
        }
        finally
        {
            _isRefreshing = false;
            _refreshLock.Release();
        }
    }

    private ConnectionStatusResponse BuildStatusResponse(
        PluginConfiguration config,
        List<XtreamProvider> enabledProviders
    )
    {
        var response = new ConnectionStatusResponse
        {
            PluginActiveStreams = Restream.GetActiveStreamCount(),
            ConfiguredMaxStreams = config.MaxConcurrentStreams,
            EnforcementEnabled = config.EnforceConnectionLimit,
            AutoKillEnabled = config.AutoKillOldestStream,
        };

        // Build provider status from the shared cache
        response.Providers = enabledProviders.Select(provider => BuildProviderStatus(provider)).ToList();

        // Calculate effective max streams and available slots
        var onlineProviders = response.Providers.Where(p => p.IsOnline).ToList();

        // Calculate provider-side availability
        var providerAvailableSlots = onlineProviders.Sum(p =>
            Math.Max(0, p.MaxConnections - p.ProviderActiveConnections)
        );
        var providerTotalCapacity = onlineProviders.Sum(p => p.MaxConnections);
        var providerTotalActiveConnections = onlineProviders.Sum(p => p.ProviderActiveConnections);

        // Set provider-side totals
        response.TotalProviderActiveConnections = providerTotalActiveConnections;
        response.TotalProviderCapacity = providerTotalCapacity;

        if (config.MaxConcurrentStreams > 0)
        {
            response.EffectiveMaxStreams = config.MaxConcurrentStreams;
            var configAvailableSlots = Math.Max(0, config.MaxConcurrentStreams - response.PluginActiveStreams);
            response.AvailableSlots = Math.Min(configAvailableSlots, providerAvailableSlots);
        }
        else if (onlineProviders.Count > 0)
        {
            response.EffectiveMaxStreams = providerTotalCapacity;
            response.AvailableSlots = providerAvailableSlots;
        }
        else
        {
            response.EffectiveMaxStreams = 1;
            response.AvailableSlots = 1;
        }

        // Calculate utilization
        response.UtilizationPercent =
            response.EffectiveMaxStreams > 0
                ? (int)Math.Round(100.0 * response.PluginActiveStreams / response.EffectiveMaxStreams)
                : 0;

        // Set warning level and message
        SetWarningLevel(response);

        return response;
    }

    private ProviderConnectionStatus BuildProviderStatus(XtreamProvider provider)
    {
        var status = new ProviderConnectionStatus { ProviderId = provider.Id, ProviderName = provider.Name };

        // Get cached status from resilience service
        var cachedStatus = _resilienceService.GetStatus(provider.Id);
        if (cachedStatus.HasValue)
        {
            var state = cachedStatus.Value;
            status.MaxConnections = state.MaxConnections;
            status.ProviderActiveConnections = state.ActiveConnections;
            status.Status = state.Status ?? string.Empty;
            status.ExpirationDate = state.ExpirationDate;
            status.IsTrial = state.IsTrial;
            status.IsOnline = state.IsOnline;
            status.ErrorMessage = state.ErrorMessage;
            status.CircuitState = state.CircuitState.ToString();
            status.SelectionScore = state.SelectionScore;
            status.IsAvailable = state.IsAvailable;
            status.ConsecutiveFailures = state.ConsecutiveFailures;
        }
        else
        {
            status.ErrorMessage = "No cached status";
            status.IsOnline = false;
            status.CircuitState = _resilienceService.GetCircuitState(provider.Id).ToString();
            status.SelectionScore = _resilienceService.GetSelectionScore(provider.Id);
            status.IsAvailable = _resilienceService.IsAvailable(provider.Id);
            status.ConsecutiveFailures = GetConsecutiveFailures(provider.Id);
        }

        return status;
    }

    private int GetConsecutiveFailures(string providerId)
    {
        var snapshot = _resilienceService.GetSnapshot();
        return snapshot.TryGetValue(providerId, out var state) ? state.ConsecutiveFailures : 0;
    }

    private static void SetWarningLevel(ConnectionStatusResponse response)
    {
        if (response.PluginActiveStreams >= response.EffectiveMaxStreams)
        {
            response.WarningLevel = "Critical";
            response.WarningMessage =
                $"Connection limit reached! {response.PluginActiveStreams}/{response.EffectiveMaxStreams} streams active.";
        }
        else if (response.UtilizationPercent >= 80)
        {
            response.WarningLevel = "Warning";
            response.WarningMessage =
                $"High utilization: {response.PluginActiveStreams}/{response.EffectiveMaxStreams} streams ({response.UtilizationPercent}%).";
        }
        else
        {
            response.WarningLevel = "None";
        }

        // Check each provider for over-subscription
        foreach (var providerStatus in response.Providers.Where(p => p.IsOnline))
        {
            if (providerStatus.ProviderActiveConnections >= providerStatus.MaxConnections)
            {
                response.WarningLevel = "Critical";
                response.WarningMessage =
                    $"Provider {providerStatus.ProviderName} at limit: {providerStatus.ProviderActiveConnections}/{providerStatus.MaxConnections} connections.";
                break;
            }

            if (
                providerStatus.ProviderActiveConnections > 0
                && (double)providerStatus.ProviderActiveConnections / providerStatus.MaxConnections >= 0.8
            )
            {
                if (response.WarningLevel != "Critical")
                {
                    response.WarningLevel = "Warning";
                    response.WarningMessage =
                        $"Provider {providerStatus.ProviderName} high usage: {providerStatus.ProviderActiveConnections}/{providerStatus.MaxConnections} connections.";
                }
            }
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _refreshLock.Dispose();
        base.Dispose();
    }
}
