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

using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Providers;
using Jellyfin.Xtream.Service;
using Jellyfin.Xtream.Service.Discovery;
using Jellyfin.Xtream.Service.Epg;
using Jellyfin.Xtream.Service.Logging;
using Jellyfin.Xtream.Service.ProviderManagement;
using Jellyfin.Xtream.Service.Switching;
using Jellyfin.Xtream.Utility;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream;

/// <inheritdoc />
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Register configuration provider (singleton for consistent access)
        serviceCollection.AddSingleton<IPluginConfigurationProvider, PluginConfigurationProvider>();

        // Register UserAgentProvider
        serviceCollection.AddSingleton<IUserAgentProvider, UserAgentProvider>();

        // Configure HTTP client for Xtream API
        serviceCollection.AddHttpClient(HttpClientConfiguration.XtreamClientName).ConfigureXtreamClient();

        // Register XtreamClient as transient (each request gets fresh instance)
        serviceCollection.AddTransient<XtreamClient>();

        // Register EPG providers
        serviceCollection.AddSingleton<XtreamEpgProvider>();
        serviceCollection.AddSingleton<ExternalXmltvEpgProvider>();
        serviceCollection.AddSingleton<XmltvEpgProvider>();

        // Register composite EPG provider that combines all EPG sources
        serviceCollection.AddSingleton<IEpgProvider>(sp =>
        {
            var providers = new IEpgProvider[]
            {
                sp.GetRequiredService<XtreamEpgProvider>(),
                sp.GetRequiredService<ExternalXmltvEpgProvider>(),
                sp.GetRequiredService<XmltvEpgProvider>(),
            };
            return new CompositeEpgProvider(providers, sp.GetRequiredService<ILogger<CompositeEpgProvider>>());
        });

        // Register Discord notification service
        serviceCollection.AddSingleton<IDiscordNotificationService, DiscordNotificationService>();

        // Register EPG refresh tracker
        serviceCollection.AddSingleton<EpgRefreshTracker>();

        // Register Live TV and Channel services
        serviceCollection.AddSingleton<ILiveTvService, LiveTvService>();
        serviceCollection.AddSingleton<IChannel, CatchupChannel>();
        serviceCollection.AddSingleton<IChannel, SeriesChannel>();
        serviceCollection.AddSingleton<IChannel, VodChannel>();

        // Register VOD metadata provider
        serviceCollection.AddSingleton<IPreRefreshProvider, XtreamVodProvider>();

        // Register provider discovery services
        serviceCollection.AddSingleton<ICredentialParser, CredentialParser>();
        serviceCollection.AddSingleton<IProviderDiscoveryService, ProviderDiscoveryService>();

        // Register unified provider resilience service (combines circuit breaker, health scoring, capacity, and connection status)
        serviceCollection.AddSingleton<ProviderAvailabilityService>();
        serviceCollection.AddSingleton<IProviderAvailabilityService>(sp =>
            sp.GetRequiredService<ProviderAvailabilityService>()
        );

        // Register segregated interfaces for consumers that need specific capabilities
        serviceCollection.AddSingleton<ICircuitBreakerService>(sp =>
            sp.GetRequiredService<ProviderAvailabilityService>()
        );
        serviceCollection.AddSingleton<IProviderHealthScorer>(sp =>
            sp.GetRequiredService<ProviderAvailabilityService>()
        );
        serviceCollection.AddSingleton<IProviderCapacityTracker>(sp =>
            sp.GetRequiredService<ProviderAvailabilityService>()
        );

        // Register provider metrics tracker for latency/throughput/error tracking
        serviceCollection.AddSingleton<IProviderMetricsTracker, ProviderMetricsTracker>();

        // Register health trend tracker for predictive failover
        serviceCollection.AddSingleton<IHealthTrendTracker, HealthTrendTracker>();

        // Register automatic failover service (combines health scoring + metrics + trends for optimal selection)
        serviceCollection.AddSingleton<AutomaticFailoverService>(sp => new AutomaticFailoverService(
            sp.GetRequiredService<IProviderHealthScorer>(),
            sp.GetRequiredService<ICircuitBreakerService>(),
            sp.GetRequiredService<IProviderMetricsTracker>(),
            sp.GetRequiredService<IHealthTrendTracker>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<AutomaticFailoverService>()
        ));
        serviceCollection.AddSingleton<IAutomaticFailoverService>(sp =>
            sp.GetRequiredService<AutomaticFailoverService>()
        );

        // Register provider monitoring service as both interface and hosted service
        // This runs in the background and refreshes provider status without impacting streaming
        serviceCollection.AddSingleton<ProviderMonitoringService>();
        serviceCollection.AddSingleton<IProviderMonitoringService>(sp =>
            sp.GetRequiredService<ProviderMonitoringService>()
        );
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<ProviderMonitoringService>());

        // Register hot-swap streaming services for mid-stream provider switching

        // PreconnectPool maintains warm connections to backup providers for <50ms switches
        serviceCollection.AddSingleton<IPreconnectPool>(sp => new PreconnectPool(
            sp.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient("XtreamClient"),
            sp.GetRequiredService<IProviderAvailabilityService>()
        ));

        // HotSwapStreamManager coordinates provider switches during active streams
        serviceCollection.AddSingleton<IHotSwapStreamManager>(sp => new HotSwapStreamManager(
            sp.GetRequiredService<IProviderAvailabilityService>(),
            sp.GetRequiredService<IAutomaticFailoverService>(),
            sp.GetRequiredService<IPreconnectPool>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<HotSwapStreamManager>()
        ));

        // ProviderUrlResolver resolves alternative provider URLs based on health scores
        serviceCollection.AddSingleton<IProviderUrlResolver>(sp => new ProviderUrlResolver(
            sp.GetRequiredService<IProviderAvailabilityService>(),
            sp.GetRequiredService<ILogger<ProviderUrlResolver>>()
        ));

        // StreamHotSwapService orchestrates hot-swap with cooldown, attempt limits, and timeouts
        serviceCollection.AddSingleton<IStreamHotSwapService>(sp => new StreamHotSwapService(
            sp.GetRequiredService<IProviderUrlResolver>(),
            sp.GetRequiredService<ILogger<StreamHotSwapService>>()
        ));

        // Register plugin log service and initialize PluginLogger
        serviceCollection.AddSingleton<IPluginLogService>(_ =>
        {
            var logService = new PluginLogService();
            PluginLogger.Initialize(logService);
            return logService;
        });
    }
}
