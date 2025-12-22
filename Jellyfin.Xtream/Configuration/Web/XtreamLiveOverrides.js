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
    const div = document.createElement('div');
    div.className = 'override-row';
    div.dataset['channelId'] = channel.Id;
    div.dataset['channelName'] = channel.Name.toLowerCase();

    // Number input
    const numberContainer = document.createElement('div');
    const numberInput = createInputField(
      'number',
      channel.Number,
      overrides.Number,
      () => numberInput.value ? overrides.Number = parseInt(numberInput.value) : delete overrides.Number
    );
    numberContainer.appendChild(numberInput);
    div.appendChild(numberContainer);

    // Name input with original name below
    const nameContainer = document.createElement('div');
    const nameInput = createInputField(
      'text',
      channel.Name,
      overrides.Name,
      () => nameInput.value ? overrides.Name = nameInput.value : delete overrides.Name
    );
    nameContainer.appendChild(nameInput);
    const originalName = document.createElement('div');
    originalName.className = 'channel-original';
    originalName.textContent = channel.Name;
    nameContainer.appendChild(originalName);
    div.appendChild(nameContainer);

    // Logo URL input
    const logoContainer = document.createElement('div');
    const imageInput = createInputField(
      'text',
      channel.LogoUrl || 'Logo URL',
      overrides.LogoUrl,
      () => imageInput.value ? overrides.LogoUrl = imageInput.value : delete overrides.LogoUrl
    );
    logoContainer.appendChild(imageInput);
    div.appendChild(logoContainer);

    return div;
  };

  view.addEventListener("viewshow", () => Promise.all([
    import(ApiClient.getUrl("web/ConfigurationPage", { name: "Xtream.js" })),
    import(ApiClient.getUrl("web/ConfigurationPage", { name: "XtreamStyles.js" }))
  ]).then(([XtreamModule, StylesModule]) => {
    const Xtream = XtreamModule.default;
    const XtreamStyles = StylesModule.default;

    // CSS is auto-loaded by XtreamStyles module
    const pluginId = Xtream.pluginConfig.UniqueId;
    Xtream.setTabs('XtreamLiveOverrides');

    const providerSelect = view.querySelector("#ProviderSelect");
    const noProviderMessage = view.querySelector("#NoProviderMessage");
    const providerContent = view.querySelector("#ProviderContent");
    const channelList = view.querySelector('#LiveChannels');
    const searchInput = view.querySelector('#OverrideSearch');
    const clearSearchBtn = view.querySelector('#ClearSearch');
    const overrideCountEl = view.querySelector('#OverrideCount');
    const channelCountEl = view.querySelector('#ChannelCount');

    let providers = [];
    let currentProviderId = null;
    let currentData = {};
    let channelRows = [];

    // Search functionality
    let debounceTimer;
    searchInput.addEventListener('input', () => {
      clearTimeout(debounceTimer);
      clearSearchBtn.classList.toggle('hide', !searchInput.value);
      debounceTimer = setTimeout(() => {
        filterChannels(searchInput.value.toLowerCase().trim());
      }, 150);
    });

    clearSearchBtn.addEventListener('click', () => {
      searchInput.value = '';
      clearSearchBtn.classList.add('hide');
      filterChannels('');
      searchInput.focus();
    });

    const filterChannels = (term) => {
      channelRows.forEach(row => {
        const name = row.dataset.channelName;
        const matches = !term || name.includes(term);
        row.style.display = matches ? '' : 'none';
      });
    };

    const updateStats = () => {
      let overrideCount = 0;
      Object.values(currentData).forEach(overrides => {
        if (Object.keys(overrides).length > 0) overrideCount++;
      });
      overrideCountEl.textContent = overrideCount;
      channelCountEl.textContent = `${channelRows.length} channels`;
    };

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
      await loadOverridesForProvider(currentProviderId);
    };

    // Load overrides for a specific provider
    const loadOverridesForProvider = async (providerId) => {
      if (!providerId) return;

      currentProviderId = providerId;
      channelList.innerHTML = '<div class="loading-overlay"><div class="loading-spinner"></div><span>Loading channels...</span></div>';
      channelRows = [];

      try {
        const config = await ApiClient.getPluginConfiguration(pluginId);
        const provider = config.Providers.find(p => p.Id === providerId);

        if (!provider) {
          channelList.innerHTML = '<div class="empty-state"><span class="material-icons">error</span><p>Provider not found</p></div>';
          return;
        }

        // Get the provider's current LiveTvOverrides configuration
        currentData = provider.LiveTvOverrides || {};

        // Fetch live TV channels for this provider
        const channels = await Xtream.fetchJson(`Xtream/LiveTv?providerId=${providerId}`);

        channelList.innerHTML = '';

        if (channels.length === 0) {
          channelList.innerHTML = '<div class="empty-state"><span class="material-icons">inbox</span><p>No channels available</p></div>';
          updateStats();
          return;
        }

        for (const channel of channels) {
          currentData[channel.Id] ??= {};
          const row = createChannelRow(channel, currentData[channel.Id]);
          channelRows.push(row);
          channelList.appendChild(row);
        }

        updateStats();

        // Re-apply search filter if active
        if (searchInput.value) {
          filterChannels(searchInput.value.toLowerCase().trim());
        }

      } catch (err) {
        console.error('Failed to load overrides:', err);
        channelList.innerHTML = '<div class="empty-state"><span class="material-icons">error</span><p>Failed to load channels. Check provider credentials.</p></div>';
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
          updateStats();
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
