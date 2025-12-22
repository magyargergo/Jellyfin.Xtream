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
using Jellyfin.Xtream.Providers;
using Jellyfin.Xtream.Service;
using Jellyfin.Xtream.Service.Discovery;
using Jellyfin.Xtream.Service.Epg;
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
        // Register UserAgentProvider
        serviceCollection.AddSingleton<IUserAgentProvider>(sp => new UserAgentProvider(
            () => Plugin.Instance.Configuration,
            sp.GetRequiredService<ILogger<UserAgentProvider>>()
        ));

        // Configure HTTP client for Xtream API
        serviceCollection
            .AddHttpClient(HttpClientConfiguration.XtreamClientName)
            .ConfigureXtreamClient(() => Plugin.Instance.Configuration);

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

        // Register provider connection cache for connection-aware channel ordering
        serviceCollection.AddSingleton<ProviderConnectionCache>();
    }
}
