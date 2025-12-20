export default function (view) {
  const createInputField = (type, placeholder, value, onChangeFn) => {
    const input = document.createElement('input');
    input.type = type;
    input.setAttribute('is', 'emby-input');
    input.placeholder = placeholder;
    input.value = value ?? '';
    input.onchange = onChangeFn;
    return input;
  };

  const createChannelRow = (channel, overrides) => {
    const tr = document.createElement('tr');
    tr.dataset['channelId'] = channel.Id;

    const numberInput = createInputField(
      'number',
      channel.Number,
      overrides.Number,
      () => numberInput.value ? overrides.Number = parseInt(numberInput.value) : delete overrides.Number
    );
    tr.appendChild(document.createElement('td')).appendChild(numberInput);

    const nameInput = createInputField(
      'text',
      channel.Name,
      overrides.Name,
      () => nameInput.value ? overrides.Name = nameInput.value : delete overrides.Name
    );
    tr.appendChild(document.createElement('td')).appendChild(nameInput);

    const imageInput = createInputField(
      'text',
      channel.LogoUrl,
      overrides.LogoUrl,
      () => imageInput.value ? overrides.LogoUrl = imageInput.value : delete overrides.LogoUrl
    );
    tr.appendChild(document.createElement('td')).appendChild(imageInput);

    return tr;
  };

  view.addEventListener("viewshow", () => import(
    ApiClient.getUrl("web/ConfigurationPage", {
      name: "Xtream.js",
    })
  ).then((Xtream) => Xtream.default
  ).then((Xtream) => {
    const pluginId = Xtream.pluginConfig.UniqueId;
    Xtream.setTabs('XtreamLiveOverrides');

    const providerSelect = view.querySelector("#ProviderSelect");
    const noProviderMessage = view.querySelector("#NoProviderMessage");
    const providerContent = view.querySelector("#ProviderContent");
    const table = view.querySelector('#LiveChannels');

    let providers = [];
    let currentProviderId = null;
    let currentData = {};

    // Load providers into dropdown
    const loadProviders = async () => {
      const config = await ApiClient.getPluginConfiguration(pluginId);
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
      await loadOverridesForProvider(currentProviderId);
    };

    // Load overrides for a specific provider
    const loadOverridesForProvider = async (providerId) => {
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

        // Get the provider's current LiveTvOverrides configuration
        currentData = provider.LiveTvOverrides || {};

        // Fetch live TV channels for this provider
        const channels = await Xtream.fetchJson(`Xtream/LiveTv?providerId=${providerId}`);

        for (const channel of channels) {
          currentData[channel.Id] ??= {};
          const row = createChannelRow(channel, currentData[channel.Id]);
          table.appendChild(row);
        }

        Dashboard.hideLoadingMsg();
      } catch (err) {
        console.error('Failed to load overrides:', err);
        Dashboard.hideLoadingMsg();
        table.innerHTML = '<tr><td colspan="3" style="color: #f44; padding: 20px;">Failed to load channels. Check provider credentials.</td></tr>';
      }
    };

    // Provider selection change handler
    providerSelect.addEventListener('change', () => {
      const selectedId = providerSelect.value;
      if (selectedId && selectedId !== currentProviderId) {
        loadOverridesForProvider(selectedId);
      }
    });

    // Form submit handler
    view.querySelector('#XtreamLiveOverridesForm').addEventListener('submit', (e) => {
      e.preventDefault();
      Dashboard.showLoadingMsg();

      ApiClient.getPluginConfiguration(pluginId).then((config) => {
        // Update the current provider's LiveTvOverrides configuration
        if (currentProviderId) {
          const providerIndex = config.Providers.findIndex(p => p.Id === currentProviderId);
          if (providerIndex !== -1) {
            config.Providers[providerIndex].LiveTvOverrides = Xtream.filter(
              currentData,
              overrides => Object.keys(overrides).length > 0
            );
          }
        }

        ApiClient.updatePluginConfiguration(pluginId, config).then((result) => {
          Dashboard.processPluginConfigurationUpdateResult(result);
        });
      });

      return false;
    });

    // Refresh button handler
    view.querySelector('#RefreshOverrides').addEventListener('click', () => {
      if (currentProviderId) {
        loadOverridesForProvider(currentProviderId);
      }
    });

    // Initial load
    loadProviders();
  }));
}
