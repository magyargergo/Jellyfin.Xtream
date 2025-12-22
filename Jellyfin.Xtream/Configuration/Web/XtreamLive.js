export default function (view) {
  view.addEventListener("viewshow", () => import(
    ApiClient.getUrl("web/ConfigurationPage", {
      name: "Xtream.js",
    })
  ).then((Xtream) => Xtream.default
  ).then((Xtream) => {
    const pluginId = Xtream.pluginConfig.UniqueId;
    Xtream.setTabs('XtreamLive');

    const visible = view.querySelector("#Visible");
    const mergeDuplicateChannels = view.querySelector("#MergeDuplicateChannels");
    const enableProviderFailover = view.querySelector("#EnableProviderFailover");
    const maxFailoverAttempts = view.querySelector("#MaxFailoverAttempts");
    const providerSelect = view.querySelector("#ProviderSelect");
    const noProviderMessage = view.querySelector("#NoProviderMessage");
    const providerContent = view.querySelector("#ProviderContent");
    const table = view.querySelector('#LiveContent');
    const copyFromProviderSection = view.querySelector("#CopyFromProviderSection");
    const copyFromProviderSelect = view.querySelector("#CopyFromProviderSelect");
    const copyChannelsBtn = view.querySelector("#CopyChannelsBtn");
    const copyResultMessage = view.querySelector("#CopyResultMessage");

    let providers = [];
    let currentProviderId = null;
    let currentData = {};

    // Load providers into dropdown
    const loadProviders = async () => {
      const config = await ApiClient.getPluginConfiguration(pluginId);
      visible.checked = config.IsCatchupVisible;
      mergeDuplicateChannels.checked = config.MergeDuplicateChannels;
      enableProviderFailover.checked = config.EnableProviderFailover;
      maxFailoverAttempts.value = config.MaxFailoverAttempts || 3;
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
      await loadChannelsForProvider(currentProviderId);

      // Update copy from provider dropdown
      updateCopyFromProviderSelect(enabledProviders);
    };

    // Update the copy from provider dropdown
    const updateCopyFromProviderSelect = (enabledProviders) => {
      copyFromProviderSelect.innerHTML = '<option value="">Select source provider...</option>';

      // Only show copy section if there are multiple providers
      if (enabledProviders.length < 2) {
        copyFromProviderSection.classList.add('hide');
        return;
      }

      copyFromProviderSection.classList.remove('hide');

      // Add all providers except the current one
      enabledProviders.forEach(provider => {
        if (provider.Id !== currentProviderId) {
          const option = document.createElement('option');
          option.value = provider.Id;
          option.textContent = provider.Name || provider.Id;
          copyFromProviderSelect.appendChild(option);
        }
      });
    };

    // Load channels for a specific provider
    const loadChannelsForProvider = async (providerId) => {
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

        // Get the provider's current LiveTv configuration
        const providerLiveTv = provider.LiveTv || {};

        currentData = await Xtream.populateCategoriesTable(
          table,
          () => Promise.resolve(providerLiveTv),
          () => Xtream.fetchJson(`Xtream/LiveCategories?providerId=${providerId}`),
          (categoryId) => Xtream.fetchJson(`Xtream/LiveCategories/${categoryId}?providerId=${providerId}`),
        );

        Dashboard.hideLoadingMsg();
      } catch (err) {
        console.error('Failed to load channels:', err);
        Dashboard.hideLoadingMsg();
        table.innerHTML = '<tr><td colspan="3" style="color: #f44; padding: 20px;">Failed to load channels. Check provider credentials.</td></tr>';
      }
    };

    // Provider selection change handler
    providerSelect.addEventListener('change', () => {
      const selectedId = providerSelect.value;
      if (selectedId && selectedId !== currentProviderId) {
        loadChannelsForProvider(selectedId);
      }
    });

    // Form submit handler
    view.querySelector('#XtreamLiveForm').addEventListener('submit', (e) => {
      e.preventDefault();
      Dashboard.showLoadingMsg();

      ApiClient.getPluginConfiguration(pluginId).then((config) => {
        config.IsCatchupVisible = visible.checked;
        config.MergeDuplicateChannels = mergeDuplicateChannels.checked;
        config.EnableProviderFailover = enableProviderFailover.checked;
        config.MaxFailoverAttempts = parseInt(maxFailoverAttempts.value, 10) || 3;

        // Update the current provider's LiveTv configuration
        if (currentProviderId) {
          const providerIndex = config.Providers.findIndex(p => p.Id === currentProviderId);
          if (providerIndex !== -1) {
            config.Providers[providerIndex].LiveTv = currentData;
          }
        }

        ApiClient.updatePluginConfiguration(pluginId, config).then((result) => {
          Dashboard.processPluginConfigurationUpdateResult(result);
        });
      });

      return false;
    });

    // Refresh button handler
    view.querySelector('#RefreshChannels').addEventListener('click', () => {
      if (currentProviderId) {
        loadChannelsForProvider(currentProviderId);
      }
    });

    // Copy channels button handler
    copyChannelsBtn.addEventListener('click', async () => {
      const sourceProviderId = copyFromProviderSelect.value;
      if (!sourceProviderId) {
        showCopyResult('Please select a source provider', false);
        return;
      }

      if (!currentProviderId) {
        showCopyResult('No target provider selected', false);
        return;
      }

      copyChannelsBtn.disabled = true;
      copyChannelsBtn.innerHTML = '<span class="material-icons" style="margin-right: 8px;">hourglass_empty</span><span>Copying...</span>';
      copyResultMessage.classList.add('hide');

      try {
        const result = await Xtream.apiRequest('Xtream/CopyChannelSelections', {
          method: 'POST',
          body: {
            SourceProviderId: sourceProviderId,
            TargetProviderId: currentProviderId
          }
        });

        if (result.Success) {
          const sourceProvider = providers.find(p => p.Id === sourceProviderId);
          const sourceName = sourceProvider?.Name || sourceProviderId;
          const unmatchedInfo = result.UnmatchedCount > 0 ? ` (${result.UnmatchedCount} not found)` : '';
          showCopyResult(
            `Copied ${result.MatchedCount} of ${result.SourceSelectedCount} channels from "${sourceName}"${unmatchedInfo}`,
            result.MatchedCount > 0
          );
          // Reload channels to show updated selections
          await loadChannelsForProvider(currentProviderId);
        } else {
          showCopyResult(result.Message || 'Copy failed', false);
        }
      } catch (err) {
        console.error('Failed to copy channels:', err);
        showCopyResult('Failed to copy channels: ' + (err.message || 'Unknown error'), false);
      } finally {
        copyChannelsBtn.disabled = false;
        copyChannelsBtn.innerHTML = '<span class="material-icons" style="margin-right: 8px;">content_copy</span><span>Copy Channels</span>';
      }
    });

    // Show copy result message
    const showCopyResult = (message, success) => {
      copyResultMessage.textContent = message;
      copyResultMessage.style.background = success ? 'rgba(76, 175, 80, 0.2)' : 'rgba(244, 67, 54, 0.2)';
      copyResultMessage.style.color = success ? '#4caf50' : '#f44336';
      copyResultMessage.style.border = `1px solid ${success ? '#4caf50' : '#f44336'}`;
      copyResultMessage.classList.remove('hide');

      // Auto-hide after 5 seconds
      setTimeout(() => {
        copyResultMessage.classList.add('hide');
      }, 5000);
    };

    // Update copy dropdown when provider changes
    providerSelect.addEventListener('change', () => {
      const enabledProviders = providers.filter(p => p.Enabled);
      updateCopyFromProviderSelect(enabledProviders);
      copyResultMessage.classList.add('hide');
    });

    // Initial load
    loadProviders();
  }));
}
