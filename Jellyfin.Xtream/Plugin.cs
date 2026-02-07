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
using System.Globalization;
using System.Net.Http;
using System.Reflection;
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Utility;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream;

/// <summary>
/// The main plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private static Plugin? _instance;

    private readonly IHttpClientFactory _httpClientFactory;

    private readonly ILogger<Plugin> _logger;

    /// <inheritdoc />
    public override string Name => "Jellyfin Xtream";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("5d774c35-8567-46d3-a950-9bb8227a0c5d");

    /// <summary>
    /// Gets the data version used to trigger a cache invalidation on plugin update or config change.
    /// </summary>
    public string DataVersion =>
        Assembly.GetCallingAssembly().GetName().Version?.ToString() + Configuration.GetHashCode();

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin Instance => _instance ?? throw new InvalidOperationException("Plugin instance not available");

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        IHttpClientFactory httpClientFactory,
        ILogger<Plugin> logger
    )
        : base(applicationPaths, xmlSerializer)
    {
        _instance = this;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        LogConfigurationState();
        MigrateLegacyConfiguration();
    }

    /// <summary>
    /// Creates a new XtreamClient with proxy configuration via dependency injection.
    /// </summary>
    /// <returns>A configured XtreamClient instance.</returns>
    public XtreamClient CreateXtreamClient() => new(_httpClientFactory);

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            // Active pages (XtreamProviders first = default plugin config page)
            CreateStatic("XtreamProviders.html"),
            CreateStatic("XtreamProviders.js"),
            CreateStatic("XtreamChannels.html"),
            CreateStatic("XtreamChannels.js"),
            CreateStatic("XtreamSettings.html"),
            CreateStatic("XtreamSettings.js"),
            CreateStatic("XtreamDashboard.html"),
            CreateStatic("XtreamDashboard.js"),
            CreateStatic("XtreamLogs.html"),
            CreateStatic("XtreamLogs.js"),
            // Shared assets
            CreateStatic("Xtream.css"),
            CreateStatic("Xtream.js"),
            CreateStatic("XtreamStyles.js"),
            // Legacy redirects (minimal stubs for old bookmarks)
            CreateStatic("XtreamMigration.html"),
            CreateStatic("XtreamMigration.js"),
            CreateStatic("XtreamAdvanced.html"),
            CreateStatic("XtreamAdvanced.js"),
            CreateStatic("XtreamLive.html"),
            CreateStatic("XtreamLive.js"),
            CreateStatic("XtreamLiveOverrides.html"),
            CreateStatic("XtreamLiveOverrides.js"),
            CreateStatic("XtreamSeries.html"),
            CreateStatic("XtreamSeries.js"),
            CreateStatic("XtreamVod.html"),
            CreateStatic("XtreamVod.js"),
            CreateStatic("XtreamEpgTest.html"),
            CreateStatic("XtreamEpgTest.js"),
            CreateStatic("XtreamStreams.html"),
            CreateStatic("XtreamStreams.js"),
            CreateStatic("XtreamMonitor.html"),
            CreateStatic("XtreamMonitor.js"),
        ];
    }

    private static PluginPageInfo CreateStatic(string name) =>
        new()
        {
            Name = name,
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Configuration.Web.{1}",
                typeof(Plugin).Namespace,
                name
            ),
        };

    private void LogConfigurationState()
    {
        var config = Configuration;
        _logger.PluginLogInformation(
            "Plugin startup - Configuration state: BaseUrl={BaseUrl}, Username={Username}, Providers={ProviderCount}, LiveTv={LiveTvCount}, EnableProxy={EnableProxy}, ProxyAddress={ProxyAddress}",
            config.BaseUrl,
            config.Username,
            config.Providers?.Count ?? 0,
            config.LiveTv?.Count ?? 0,
            config.EnableProxy,
            config.ProxyAddress
        );

        var providers = config.Providers;

        if (providers == null || providers.Count == 0)
        {
            return;
        }

        foreach (var provider in providers)
        {
            _logger.PluginLogInformation(
                "Provider {ProviderId} ({ProviderName}): LiveTv={LiveTvCount}, Vod={VodCount}, Series={SeriesCount}, LiveTvOverrides={OverridesCount}",
                provider.Id,
                provider.Name,
                provider.LiveTv?.Count ?? 0,
                provider.Vod?.Count ?? 0,
                provider.Series?.Count ?? 0,
                provider.LiveTvOverrides?.Count ?? 0
            );
        }
    }

    /// <summary>
    /// Clones a SerializableDictionary to avoid reference sharing issues during migration.
    /// </summary>
    private static SerializableDictionary<TKey, TValue> CloneDictionary<TKey, TValue>(
        SerializableDictionary<TKey, TValue>? source
    )
        where TKey : notnull
    {
        SerializableDictionary<TKey, TValue> clone = [];

        if (source != null)
        {
            foreach (var kvp in source)
            {
                clone[kvp.Key] = kvp.Value;
            }
        }

        return clone;
    }

    /// <summary>
    /// Migrates legacy single-provider configuration to the new multi-provider format.
    /// </summary>
    private void MigrateLegacyConfiguration()
    {
        var config = Configuration;

        if (config.NeedsMigration)
        {
            _logger.PluginLogInformation(
                "Migrating legacy configuration to new multi-provider format. LiveTv categories: {LiveTvCount}, Vod categories: {VodCount}, Series categories: {SeriesCount}",
                config.LiveTv?.Count ?? 0,
                config.Vod?.Count ?? 0,
                config.Series?.Count ?? 0
            );

            var migratedProvider = new XtreamProvider
            {
                Id = "migrated",
                Name = "Migrated Provider",
                BaseUrl = config.BaseUrl,
                Username = config.Username,
                Password = config.Password,
                Enabled = true,
                LiveTv = CloneDictionary(config.LiveTv),
                Vod = CloneDictionary(config.Vod),
                Series = CloneDictionary(config.Series),
                LiveTvOverrides = CloneDictionary(config.LiveTvOverrides),
            };

            config.Providers.Add(migratedProvider);
            config.BaseUrl = string.Empty;
            config.Username = string.Empty;
            config.Password = string.Empty;
            config.LiveTv?.Clear();
            config.Vod?.Clear();
            config.Series?.Clear();
            config.LiveTvOverrides?.Clear();

            SaveConfiguration();

            _logger.PluginLogInformation(
                "Legacy configuration migrated successfully to provider: {ProviderId}. Migrated LiveTv: {LiveTvCount}, Vod: {VodCount}, Series: {SeriesCount}",
                migratedProvider.Id,
                migratedProvider.LiveTv?.Count ?? 0,
                migratedProvider.Vod?.Count ?? 0,
                migratedProvider.Series?.Count ?? 0
            );
        }
    }
}
