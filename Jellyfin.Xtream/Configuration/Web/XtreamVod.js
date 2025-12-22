export default function (view) {
  view.addEventListener("viewshow", () => Promise.all([
    import(ApiClient.getUrl("web/ConfigurationPage", { name: "Xtream.js" })),
    import(ApiClient.getUrl("web/ConfigurationPage", { name: "XtreamStyles.js" }))
  ]).then(([XtreamModule, StylesModule]) => {
    const Xtream = XtreamModule.default;
    const XtreamStyles = StylesModule.default;

    // CSS is auto-loaded by XtreamStyles module
    const pluginId = Xtream.pluginConfig.UniqueId;
    Xtream.setTabs('XtreamVod');

    // DOM Elements
    const visible = view.querySelector("#Visible");
    const tmdbOverride = view.querySelector("#TmdbOverride");
    const providerSelect = view.querySelector("#ProviderSelect");
    const noProviderMessage = view.querySelector("#NoProviderMessage");
    const providerContent = view.querySelector("#ProviderContent");
    const vodContent = view.querySelector('#VodContent');

    // State
    let providers = [];
    let currentProviderId = null;
    let currentData = {};

    // Load providers into dropdown
    const loadProviders = async () => {
      const config = await ApiClient.getPluginConfiguration(pluginId);
      visible.checked = config.IsVodVisible;
      tmdbOverride.checked = config.IsTmdbVodOverride;
      providers = config.Providers || [];

      providerSelect.innerHTML = '';

      if (providers.length === 0) {
        noProviderMessage.classList.remove('hide');
        providerContent.classList.add('hide');
        providerSelect.innerHTML = '<option value="">No providers configured</option>';
        return;
      }

      noProviderMessage.classList.add('hide');

      // Filter to only enabled providers
      const enabledProviders = providers.filter(p => p.Enabled);

      if (enabledProviders.length === 0) {
        noProviderMessage.classList.remove('hide');
        noProviderMessage.innerHTML = '<span class="material-icons">cloud_off</span><p>No enabled providers. Please enable a provider in the <a href="/configurationpage?name=XtreamProviders.html">Providers</a> tab.</p>';
        providerContent.classList.add('hide');
        providerSelect.innerHTML = '<option value="">No enabled providers</option>';
        return;
      }

      enabledProviders.forEach(provider => {
        const option = document.createElement('option');
        option.value = provider.Id;
        option.textContent = provider.Name || provider.Id;
        providerSelect.appendChild(option);
      });

      // Select first provider by default or previously selected
      if (currentProviderId && enabledProviders.some(p => p.Id === currentProviderId)) {
        providerSelect.value = currentProviderId;
      } else {
        currentProviderId = enabledProviders[0].Id;
        providerSelect.value = currentProviderId;
      }

      providerContent.classList.remove('hide');
      await loadVodForProvider(currentProviderId);
    };

    // Load VOD for a specific provider using the new card-based UI
    const loadVodForProvider = async (providerId) => {
      if (!providerId) return;

      currentProviderId = providerId;
      vodContent.innerHTML = '';
      vodContent.appendChild(XtreamStyles.createLoadingSpinner('Loading VOD categories...'));

      try {
        const config = await ApiClient.getPluginConfiguration(pluginId);
        const provider = config.Providers.find(p => p.Id === providerId);

        if (!provider) {
          vodContent.innerHTML = '';
          vodContent.appendChild(XtreamStyles.createErrorState('Provider not found'));
          return;
        }

        // Get the provider's current Vod configuration
        currentData = provider.Vod || {};

        // Create the searchable categories UI
        await Xtream.createSearchableCategories(
          vodContent,
          currentData,
          () => Xtream.fetchJson(`Xtream/VodCategories?providerId=${providerId}`),
          (categoryId) => Xtream.fetchJson(`Xtream/VodCategories/${categoryId}?providerId=${providerId}`),
          {
            icon: 'movie',
            searchPlaceholder: 'Search movies...',
            emptyMessage: 'No VOD categories available'
          }
        );

      } catch (err) {
        console.error('Failed to load VOD:', err);
        vodContent.innerHTML = '';
        vodContent.appendChild(XtreamStyles.createErrorState('Failed to load VOD. Check provider credentials.'));
      }
    };

    // Provider selection change handler
    providerSelect.addEventListener('change', () => {
      const selectedId = providerSelect.value;
      if (selectedId && selectedId !== currentProviderId) {
        loadVodForProvider(selectedId);
      }
    });

    // Form submit handler
    view.querySelector('#XtreamVodForm').addEventListener('submit', (e) => {
      e.preventDefault();
      Dashboard.showLoadingMsg();

      ApiClient.getPluginConfiguration(pluginId).then((config) => {
        config.IsVodVisible = visible.checked;
        config.IsTmdbVodOverride = tmdbOverride.checked;

        // Update the current provider's Vod configuration
        if (currentProviderId) {
          const providerIndex = config.Providers.findIndex(p => p.Id === currentProviderId);
          if (providerIndex !== -1) {
            config.Providers[providerIndex].Vod = currentData;
          }
        }

        ApiClient.updatePluginConfiguration(pluginId, config).then((result) => {
          Dashboard.processPluginConfigurationUpdateResult(result);
        });
      });

      return false;
    });

    // Refresh button handler
    view.querySelector('#RefreshVod').addEventListener('click', () => {
      if (currentProviderId) {
        loadVodForProvider(currentProviderId);
      }
    });

    // Initial load
    loadProviders();
  }));
}
