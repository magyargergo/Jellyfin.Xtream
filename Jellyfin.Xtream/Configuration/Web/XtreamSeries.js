export default function (view) {
  view.addEventListener("viewshow", () => import(
    ApiClient.getUrl("web/ConfigurationPage", {
      name: "Xtream.js",
    })
  ).then((Xtream) => Xtream.default
  ).then((Xtream) => {
    const pluginId = Xtream.pluginConfig.UniqueId;
    Xtream.setTabs('XtreamSeries');

    const visible = view.querySelector("#Visible");
    const providerSelect = view.querySelector("#ProviderSelect");
    const noProviderMessage = view.querySelector("#NoProviderMessage");
    const providerContent = view.querySelector("#ProviderContent");
    const table = view.querySelector('#SeriesContent');

    let providers = [];
    let currentProviderId = null;
    let currentData = {};

    // Load providers into dropdown
    const loadProviders = async () => {
      const config = await ApiClient.getPluginConfiguration(pluginId);
      visible.checked = config.IsSeriesVisible;
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
        noProviderMessage.innerHTML = '<p>No enabled providers. Please enable a provider in the <a href="/configurationpage?name=XtreamProviders.html">Providers</a> tab.</p>';
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
      await loadSeriesForProvider(currentProviderId);
    };

    // Load Series for a specific provider
    const loadSeriesForProvider = async (providerId) => {
      if (!providerId) return;

      currentProviderId = providerId;
      table.innerHTML = '';
      Dashboard.showLoadingMsg();

      try {
        const config = await ApiClient.getPluginConfiguration(pluginId);
        const provider = config.Providers.find(p => p.Id === providerId);

        if (!provider) {
          Dashboard.hideLoadingMsg();
          return;
        }

        // Get the provider's current Series configuration
        const providerSeries = provider.Series || {};

        currentData = await Xtream.populateCategoriesTable(
          table,
          () => Promise.resolve(providerSeries),
          () => Xtream.fetchJson(`Xtream/SeriesCategories?providerId=${providerId}`),
          (categoryId) => Xtream.fetchJson(`Xtream/SeriesCategories/${categoryId}?providerId=${providerId}`),
        );

        Dashboard.hideLoadingMsg();
      } catch (err) {
        console.error('Failed to load Series:', err);
        Dashboard.hideLoadingMsg();
        table.innerHTML = '<tr><td colspan="3" style="color: #f44; padding: 20px;">Failed to load Series. Check provider credentials.</td></tr>';
      }
    };

    // Provider selection change handler
    providerSelect.addEventListener('change', () => {
      const selectedId = providerSelect.value;
      if (selectedId && selectedId !== currentProviderId) {
        loadSeriesForProvider(selectedId);
      }
    });

    // Form submit handler
    view.querySelector('#XtreamSeriesForm').addEventListener('submit', (e) => {
      e.preventDefault();
      Dashboard.showLoadingMsg();

      ApiClient.getPluginConfiguration(pluginId).then((config) => {
        config.IsSeriesVisible = visible.checked;

        // Update the current provider's Series configuration
        if (currentProviderId) {
          const providerIndex = config.Providers.findIndex(p => p.Id === currentProviderId);
          if (providerIndex !== -1) {
            config.Providers[providerIndex].Series = currentData;
          }
        }

        ApiClient.updatePluginConfiguration(pluginId, config).then((result) => {
          Dashboard.processPluginConfigurationUpdateResult(result);
        });
      });

      return false;
    });

    // Refresh button handler
    view.querySelector('#RefreshSeries').addEventListener('click', () => {
      if (currentProviderId) {
        loadSeriesForProvider(currentProviderId);
      }
    });

    // Initial load
    loadProviders();
  }));
}
