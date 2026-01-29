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

        // Note: Provider health tracking is now handled by C++ native code
        // C++ manages health scores internally and persists them to SQLite

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
