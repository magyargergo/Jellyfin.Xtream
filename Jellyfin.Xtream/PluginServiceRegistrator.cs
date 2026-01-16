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
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Providers;
using Jellyfin.Xtream.Service;
using Jellyfin.Xtream.Service.Discovery;
using Jellyfin.Xtream.Service.Epg;
using Jellyfin.Xtream.Service.Logging;
using Jellyfin.Xtream.Service.MpegTs.Infrastructure;
using Jellyfin.Xtream.Service.MpegTs.Infrastructure.Pooling;
using Jellyfin.Xtream.Service.MpegTs.UseCases;
using Jellyfin.Xtream.Service.ProviderManagement;
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
        _ = serviceCollection.AddSingleton<IPluginConfigurationProvider, PluginConfigurationProvider>();

        // Register UserAgentProvider
        _ = serviceCollection.AddSingleton<IUserAgentProvider, UserAgentProvider>();

        // Configure HTTP client for Xtream API
        _ = serviceCollection.AddHttpClient(HttpClientConfiguration.XtreamClientName).ConfigureXtreamClient();

        // Register XtreamClient as transient (each request gets fresh instance)
        _ = serviceCollection.AddTransient<XtreamClient>();

        // Register EPG providers
        _ = serviceCollection.AddSingleton<XtreamEpgProvider>();
        _ = serviceCollection.AddSingleton<ExternalXmltvEpgProvider>();
        _ = serviceCollection.AddSingleton<XmltvEpgProvider>();

        // Register composite EPG provider that combines all EPG sources
        _ = serviceCollection.AddSingleton<IEpgProvider>(sp =>
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
        _ = serviceCollection.AddSingleton<IDiscordNotificationService, DiscordNotificationService>();

        // Register EPG refresh tracker
        _ = serviceCollection.AddSingleton<EpgRefreshTracker>();

        // Register Live TV and Channel services
        _ = serviceCollection.AddSingleton<ILiveTvService, LiveTvService>();
        _ = serviceCollection.AddSingleton<IChannel, CatchupChannel>();
        _ = serviceCollection.AddSingleton<IChannel, SeriesChannel>();
        _ = serviceCollection.AddSingleton<IChannel, VodChannel>();

        // Register VOD metadata provider
        _ = serviceCollection.AddSingleton<IPreRefreshProvider, XtreamVodProvider>();

        // Register provider discovery services
        _ = serviceCollection.AddSingleton<ICredentialParser, CredentialParser>();
        _ = serviceCollection.AddSingleton<IProviderDiscoveryService, ProviderDiscoveryService>();

        // Register unified provider resilience service (combines circuit breaker, health scoring, capacity, and connection status)
        // Used internally by AutomaticFailoverService - external code should use IAutomaticFailoverService
        _ = serviceCollection.AddSingleton<ProviderAvailabilityService>();
        _ = serviceCollection.AddSingleton<IProviderAvailabilityService>(sp =>
            sp.GetRequiredService<ProviderAvailabilityService>()
        );

        // Register provider metrics tracker for latency/throughput/error tracking
        _ = serviceCollection.AddSingleton<IProviderMetricsTracker, ProviderMetricsTracker>();

        // Register health trend tracker for predictive failover
        _ = serviceCollection.AddSingleton<IHealthTrendTracker, HealthTrendTracker>();

        // Register automatic failover service - the single source of truth for provider selection
        // Combines availability service + metrics + trends for optimal provider selection
        _ = serviceCollection.AddSingleton<AutomaticFailoverService>(sp => new AutomaticFailoverService(
            sp.GetRequiredService<IProviderAvailabilityService>(),
            sp.GetRequiredService<IProviderMetricsTracker>(),
            sp.GetRequiredService<IHealthTrendTracker>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<AutomaticFailoverService>()
        ));
        _ = serviceCollection.AddSingleton<IAutomaticFailoverService>(sp =>
            sp.GetRequiredService<AutomaticFailoverService>()
        );

        // Register provider monitoring service as both interface and hosted service
        // This runs in the background and refreshes provider status without impacting streaming
        _ = serviceCollection.AddSingleton<ProviderMonitoringService>();
        _ = serviceCollection.AddSingleton<IProviderMonitoringService>(sp =>
            sp.GetRequiredService<ProviderMonitoringService>()
        );
        _ = serviceCollection.AddHostedService(sp => sp.GetRequiredService<ProviderMonitoringService>());

        // Register FFmpeg initialization service as hosted service
        // Initializes FFmpeg using Jellyfin's media encoder path on startup
        _ = serviceCollection.AddHostedService<FFmpegInitializationService>();

        // Register FFmpeg context adapter for dependency injection
        // Uses the singleton adapter that delegates to the static FFmpegContext initialized by FFmpegInitializationService
        _ = serviceCollection.AddSingleton<IFFmpegContext>(FFmpegContextAdapter.Instance);

        // Register FFmpeg processor pools (unified pools for demuxers and remuxers)
        // Avoids SIGSEGV during rapid switches by using "replace don't reset" pattern
        // Each pool is warmed on first use and maintains min/max processor instances

        // Demuxer pool for PSI parsing and PTS extraction
        _ = serviceCollection.AddKeyedSingleton<IFFmpegProcessorPool>(
            "demuxer",
            (sp, _) =>
                FFmpegProcessorPoolFactory.CreateDemuxerPool(
                    ffmpegContext: sp.GetRequiredService<IFFmpegContext>(),
                    logger: sp.GetRequiredService<ILogger<FFmpegProcessorPool>>(),
                    minPoolSize: 2,
                    maxPoolSize: 8
                )
        );

        // Remuxer pool for A/V sync with non-blocking data processing
        // Background worker processes data to avoid blocking HTTP read loop during FFmpeg initialization
        _ = serviceCollection.AddKeyedSingleton<IFFmpegProcessorPool>(
            "remuxer",
            (sp, _) =>
                FFmpegProcessorPoolFactory.CreateRemuxerPool(
                    ffmpegContext: sp.GetRequiredService<IFFmpegContext>(),
                    logger: sp.GetRequiredService<ILogger<FFmpegProcessorPool>>(),
                    minPoolSize: 2,
                    maxPoolSize: 8
                )
        );

        // Register system clock for high-precision timing
        _ = serviceCollection.AddSingleton<ISystemClock, StopwatchClock>();

        // ProviderUrlResolver resolves alternative provider URLs using AutomaticFailoverService as source of truth
        // This centralizes all provider selection logic (health scoring, circuit breakers, metrics, trends)
        _ = serviceCollection.AddSingleton<IProviderUrlResolver>(sp => new ProviderUrlResolver(
            sp.GetRequiredService<IAutomaticFailoverService>(),
            sp.GetRequiredService<ILogger<ProviderUrlResolver>>()
        ));

        // Register unified provider switch service for mid-stream provider switching
        // Combines URL resolution, MPEG-TS alignment, and pooled FFmpeg remuxing for A/V synchronization
        // Uses FFmpeg.AutoGen directly for proper audio/video sync without subprocess overhead
        // The remuxer pool provides non-blocking data processing to avoid stalling the HTTP read loop
        _ = serviceCollection.AddSingleton<IProviderSwitchService>(sp => new ProviderSwitchService(
            sp.GetRequiredService<IProviderUrlResolver>(),
            sp.GetRequiredService<System.Net.Http.IHttpClientFactory>(),
            sp.GetRequiredService<IProviderAvailabilityService>(),
            sp.GetRequiredService<IPluginConfigurationProvider>(),
            sp.GetRequiredService<ILogger<ProviderSwitchService>>(),
            sp.GetRequiredService<ILoggerFactory>(),
            config: null,
            ffmpegContext: sp.GetRequiredService<IFFmpegContext>(),
            demuxerPool: sp.GetRequiredKeyedService<IFFmpegProcessorPool>("demuxer"),
            remuxerPool: sp.GetRequiredKeyedService<IFFmpegProcessorPool>("remuxer")
        ));

        // Register violation switch trigger for TR 101 290 quality-based switching
        // Evaluates stream quality violations and determines when to trigger provider switches
        _ = serviceCollection.AddSingleton<IViolationSwitchTrigger, ViolationSwitchTrigger>();

        // ===== Fast Channel Change (FCC) Services =====
        // Implements DVB-IPTV Fast Channel Change for sub-200ms channel switching
        // Reference: Nokia FCC specification, DVB-IPTV Handbook

        // Register preconnect pool for connection warmup
        // Pre-establishes TCP/TLS connections to IPTV providers during idle time
        // This removes DNS resolution, TCP handshake, and TLS negotiation from critical path
        _ = serviceCollection.AddSingleton<IPreconnectPool>(sp => new PreconnectPool(
            sp.GetRequiredService<System.Net.Http.IHttpClientFactory>(),
            sp.GetRequiredService<ILogger<PreconnectPool>>(),
            config: null // Uses default configuration
        ));

        // Register channel warmup service for predictive warmup
        // Warms connections to channels the user is likely to switch to:
        // - Adjacent channels (±3 from current)
        // - Channels visible in EPG guide
        // - Frequently-watched channels
        _ = serviceCollection.AddSingleton<IChannelWarmupService>(sp => new ChannelWarmupService(
            sp.GetRequiredService<IPreconnectPool>(),
            sp.GetRequiredService<ILogger<ChannelWarmupService>>(),
            config: null // Uses default configuration
        ));

        // Register plugin log service and initialize PluginLogger
        _ = serviceCollection.AddSingleton<IPluginLogService>(sp =>
        {
            var logService = new PluginLogService();
            var configProvider = sp.GetRequiredService<IPluginConfigurationProvider>();
            PluginLogger.Initialize(logService, configProvider);
            return logService;
        });
    }
}
