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

    const numberContainer = document.createElement('div');
    const numberInput = createInputField(
      'number', channel.Number, overrides.Number,
      () => numberInput.value ? overrides.Number = parseInt(numberInput.value) : delete overrides.Number
    );
    numberContainer.appendChild(numberInput);
    div.appendChild(numberContainer);

    const nameContainer = document.createElement('div');
    const nameInput = createInputField(
      'text', channel.Name, overrides.Name,
      () => nameInput.value ? overrides.Name = nameInput.value : delete overrides.Name
    );
    nameContainer.appendChild(nameInput);
    const originalName = document.createElement('div');
    originalName.className = 'channel-original';
    originalName.textContent = channel.Name;
    nameContainer.appendChild(originalName);
    div.appendChild(nameContainer);

    const logoContainer = document.createElement('div');
    const imageInput = createInputField(
      'text', channel.LogoUrl || 'Logo URL', overrides.LogoUrl,
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

    const pluginId = Xtream.pluginConfig.UniqueId;
    Xtream.setTabs('XtreamChannels');

    // ========================================
    // Sub-tab configuration
    // ========================================
    const typeConfig = {
      live:   { endpoint: 'LiveCategories',   configKey: 'LiveTv',  icon: 'live_tv',  contentEl: 'LiveContent' },
      vod:    { endpoint: 'VodCategories',    configKey: 'Vod',     icon: 'movie',    contentEl: 'VodContent' },
      series: { endpoint: 'SeriesCategories', configKey: 'Series',  icon: 'tv',       contentEl: 'SeriesContent' },
    };

    // ========================================
    // DOM Elements
    // ========================================
    const providerSelect = view.querySelector('#ProviderSelect');
    const noProviderMessage = view.querySelector('#NoProviderMessage');
    const providerContent = view.querySelector('#ProviderContent');
    const tabButtons = view.querySelectorAll('.channel-type-tab');
    const tabPanels = {
      live: view.querySelector('#TabLive'),
      vod: view.querySelector('#TabVod'),
      series: view.querySelector('#TabSeries'),
      overrides: view.querySelector('#TabOverrides'),
    };

    // Live-specific
    const isCatchupVisible = view.querySelector('#IsCatchupVisible');
    const enableProviderFailover = view.querySelector('#EnableProviderFailover');
    const maxFailoverAttempts = view.querySelector('#MaxFailoverAttempts');
    const failoverSettingsHeader = view.querySelector('#FailoverSettingsHeader');
    const failoverSettingsContent = view.querySelector('#FailoverSettingsContent');
    const copyFromProviderSection = view.querySelector('#CopyFromProviderSection');
    const copyFromProviderSelect = view.querySelector('#CopyFromProviderSelect');
    const copyChannelsBtn = view.querySelector('#CopyChannelsBtn');
    const copyResultMessage = view.querySelector('#CopyResultMessage');

    // VOD-specific
    const isVodVisible = view.querySelector('#IsVodVisible');
    const isTmdbVodOverride = view.querySelector('#IsTmdbVodOverride');

    // Series-specific
    const isSeriesVisible = view.querySelector('#IsSeriesVisible');

    // Overrides-specific
    const overrideSearch = view.querySelector('#OverrideSearch');
    const clearOverrideSearch = view.querySelector('#ClearOverrideSearch');
    const overrideCountEl = view.querySelector('#OverrideCount');
    const overrideChannelCountEl = view.querySelector('#OverrideChannelCount');
    const overrideChannelList = view.querySelector('#OverrideChannels');

    // ========================================
    // State
    // ========================================
    let providers = [];
    let currentProviderId = null;
    let activeTab = 'live';

    // Per-tab data (only populated when tab is visited)
    const tabData = {
      live: { data: null, loaded: false, dirty: false },
      vod: { data: null, loaded: false, dirty: false },
      series: { data: null, loaded: false, dirty: false },
      overrides: { data: null, loaded: false, dirty: false, rows: [] },
    };

    // ========================================
    // Failover settings toggle
    // ========================================
    let failoverExpanded = false;
    failoverSettingsHeader.addEventListener('click', () => {
      failoverExpanded = !failoverExpanded;
      failoverSettingsContent.style.display = failoverExpanded ? 'block' : 'none';
      failoverSettingsHeader.querySelector('.material-icons').textContent =
        failoverExpanded ? 'expand_less' : 'expand_more';
    });

    // ========================================
    // Sub-tab switching
    // ========================================
    const switchTab = (tab) => {
      activeTab = tab;
      tabButtons.forEach(btn => {
        btn.classList.toggle('active', btn.dataset.tab === tab);
      });
      Object.entries(tabPanels).forEach(([key, panel]) => {
        panel.classList.toggle('active', key === tab);
      });

      // Lazy-load content for this tab + provider
      if (currentProviderId) {
        loadContentForTab(tab, currentProviderId);
      }
    };

    tabButtons.forEach(btn => {
      btn.addEventListener('click', (e) => {
        e.preventDefault();
        switchTab(btn.dataset.tab);
      });
    });

    // ========================================
    // Provider loading
    // ========================================
    const loadProviders = async () => {
      Dashboard.showLoadingMsg();
      try {
        const config = await ApiClient.getPluginConfiguration(pluginId);

        // Load global config values
        isCatchupVisible.checked = config.IsCatchupVisible;
        enableProviderFailover.checked = config.EnableProviderFailover;
        maxFailoverAttempts.value = config.MaxFailoverAttempts || 3;
        isVodVisible.checked = config.IsVodVisible;
        isTmdbVodOverride.checked = config.IsTmdbVodOverride;
        isSeriesVisible.checked = config.IsSeriesVisible;

        providers = config.Providers || [];
        providerSelect.innerHTML = '';

        if (providers.length === 0) {
          noProviderMessage.classList.remove('hide');
          providerContent.classList.add('hide');
          providerSelect.innerHTML = '<option value="">No providers configured</option>';
          return;
        }

        noProviderMessage.classList.add('hide');

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

        if (currentProviderId && enabledProviders.some(p => p.Id === currentProviderId)) {
          providerSelect.value = currentProviderId;
        } else {
          currentProviderId = enabledProviders[0].Id;
          providerSelect.value = currentProviderId;
        }

        providerContent.classList.remove('hide');

        // Reset tab loaded states
        Object.values(tabData).forEach(td => { td.loaded = false; td.dirty = false; });

        // Load content for active tab
        await loadContentForTab(activeTab, currentProviderId);

        // Update copy-from dropdown
        updateCopyFromProviderSelect(enabledProviders);
      } catch (err) {
        console.error('Failed to load providers:', err);
      } finally {
        Dashboard.hideLoadingMsg();
      }
    };

    // ========================================
    // Content loading per tab
    // ========================================
    const loadContentForTab = async (tab, providerId) => {
      if (!providerId) return;

      if (tab === 'overrides') {
        if (!tabData.overrides.loaded) {
          await loadOverridesForProvider(providerId);
        }
        return;
      }

      const cfg = typeConfig[tab];
      if (!cfg) return;

      // Skip if already loaded for this provider
      if (tabData[tab].loaded) return;

      const contentEl = view.querySelector('#' + cfg.contentEl);
      contentEl.innerHTML = '';
      contentEl.appendChild(XtreamStyles.createLoadingSpinner('Loading...'));

      try {
        const config = await ApiClient.getPluginConfiguration(pluginId);
        const provider = config.Providers.find(p => p.Id === providerId);

        if (!provider) {
          contentEl.innerHTML = '';
          contentEl.appendChild(XtreamStyles.createErrorState('Provider not found'));
          return;
        }

        tabData[tab].data = provider[cfg.configKey] || {};

        await Xtream.createSearchableCategories(
          contentEl,
          tabData[tab].data,
          () => Xtream.fetchJson(`Xtream/${cfg.endpoint}?providerId=${providerId}`),
          (categoryId) => Xtream.fetchJson(`Xtream/${cfg.endpoint}/${categoryId}?providerId=${providerId}`),
          {
            icon: cfg.icon,
            searchPlaceholder: `Search ${tab === 'live' ? 'channels' : tab === 'vod' ? 'movies' : 'series'}...`,
            emptyMessage: `No ${tab === 'live' ? 'Live TV' : tab === 'vod' ? 'VOD' : 'Series'} categories available`
          }
        );

        tabData[tab].loaded = true;
        tabData[tab].dirty = true;
      } catch (err) {
        console.error(`Failed to load ${tab}:`, err);
        contentEl.innerHTML = '';
        const detail = err.detail ? `: ${err.detail}` : '';
        contentEl.appendChild(XtreamStyles.createErrorState(
          err.code ? `${err.message}${detail}` : `Failed to load content. Check provider credentials.`
        ));
      }
    };

    // ========================================
    // Overrides loading
    // ========================================
    const loadOverridesForProvider = async (providerId) => {
      if (!providerId) return;

      overrideChannelList.innerHTML = '<div class="loading-overlay"><div class="loading-spinner"></div><span>Loading channels...</span></div>';
      tabData.overrides.rows = [];

      try {
        const config = await ApiClient.getPluginConfiguration(pluginId);
        const provider = config.Providers.find(p => p.Id === providerId);

        if (!provider) {
          overrideChannelList.innerHTML = '<div class="empty-state"><span class="material-icons">error</span><p>Provider not found</p></div>';
          return;
        }

        tabData.overrides.data = provider.LiveTvOverrides || {};

        const channels = await Xtream.fetchJson(`Xtream/LiveTv?providerId=${providerId}`);
        overrideChannelList.innerHTML = '';

        if (channels.length === 0) {
          overrideChannelList.innerHTML = '<div class="empty-state"><span class="material-icons">inbox</span><p>No channels available</p></div>';
          updateOverrideStats();
          return;
        }

        for (const channel of channels) {
          tabData.overrides.data[channel.Id] ??= {};
          const row = createChannelRow(channel, tabData.overrides.data[channel.Id]);
          tabData.overrides.rows.push(row);
          overrideChannelList.appendChild(row);
        }

        tabData.overrides.loaded = true;
        tabData.overrides.dirty = true;
        updateOverrideStats();

        if (overrideSearch.value) {
          filterOverrideChannels(overrideSearch.value.toLowerCase().trim());
        }
      } catch (err) {
        console.error('Failed to load overrides:', err);
        const detail = err.detail ? `<br><small>${err.detail}</small>` : '';
        const msg = err.code ? `${err.message}${detail}` : 'Failed to load channels. Check provider credentials.';
        overrideChannelList.innerHTML = `<div class="empty-state"><span class="material-icons">error</span><p>${msg}</p></div>`;
      }
    };

    // ========================================
    // Override search & stats
    // ========================================
    let overrideDebounce;
    overrideSearch.addEventListener('input', () => {
      clearTimeout(overrideDebounce);
      clearOverrideSearch.classList.toggle('hide', !overrideSearch.value);
      overrideDebounce = setTimeout(() => {
        filterOverrideChannels(overrideSearch.value.toLowerCase().trim());
      }, 150);
    });

    clearOverrideSearch.addEventListener('click', () => {
      overrideSearch.value = '';
      clearOverrideSearch.classList.add('hide');
      filterOverrideChannels('');
      overrideSearch.focus();
    });

    const filterOverrideChannels = (term) => {
      tabData.overrides.rows.forEach(row => {
        const name = row.dataset.channelName;
        row.style.display = (!term || name.includes(term)) ? '' : 'none';
      });
    };

    const updateOverrideStats = () => {
      let overrideCount = 0;
      if (tabData.overrides.data) {
        Object.values(tabData.overrides.data).forEach(overrides => {
          if (Object.keys(overrides).length > 0) overrideCount++;
        });
      }
      overrideCountEl.textContent = overrideCount;
      overrideChannelCountEl.textContent = `${tabData.overrides.rows.length} channels`;
    };

    // ========================================
    // Provider selection change
    // ========================================
    providerSelect.addEventListener('change', () => {
      const selectedId = providerSelect.value;
      if (selectedId && selectedId !== currentProviderId) {
        currentProviderId = selectedId;
        // Reset all tabs
        Object.values(tabData).forEach(td => { td.loaded = false; td.dirty = false; });
        loadContentForTab(activeTab, currentProviderId);
        // Update copy dropdown
        const enabledProviders = providers.filter(p => p.Enabled);
        updateCopyFromProviderSelect(enabledProviders);
        copyResultMessage.classList.add('hide');
      }
    });

    // ========================================
    // Copy from provider
    // ========================================
    const updateCopyFromProviderSelect = (enabledProviders) => {
      copyFromProviderSelect.innerHTML = '<option value="">Select source provider...</option>';

      if (enabledProviders.length < 2) {
        copyFromProviderSection.classList.add('hide');
        return;
      }

      copyFromProviderSection.classList.remove('hide');
      enabledProviders.forEach(provider => {
        if (provider.Id !== currentProviderId) {
          const option = document.createElement('option');
          option.value = provider.Id;
          option.textContent = provider.Name || provider.Id;
          copyFromProviderSelect.appendChild(option);
        }
      });
    };

    copyChannelsBtn.addEventListener('click', async () => {
      const sourceProviderId = copyFromProviderSelect.value;
      if (!sourceProviderId || !currentProviderId) {
        showCopyResult(sourceProviderId ? 'No target provider selected' : 'Please select a source provider', false);
        return;
      }

      copyChannelsBtn.disabled = true;
      copyChannelsBtn.innerHTML = '<span class="material-icons" style="margin-right: 8px;">hourglass_empty</span><span>Copying...</span>';
      copyResultMessage.classList.add('hide');

      try {
        const result = await Xtream.apiRequest('Xtream/CopyChannelSelections', {
          method: 'POST',
          body: { SourceProviderId: sourceProviderId, TargetProviderId: currentProviderId }
        });

        if (result.Success) {
          const sourceProvider = providers.find(p => p.Id === sourceProviderId);
          const sourceName = sourceProvider?.Name || sourceProviderId;
          const unmatchedInfo = result.UnmatchedCount > 0 ? ` (${result.UnmatchedCount} not found)` : '';
          showCopyResult(`Copied ${result.MatchedCount} of ${result.SourceSelectedCount} channels from "${sourceName}"${unmatchedInfo}`, result.MatchedCount > 0);
          // Reload live tab
          tabData.live.loaded = false;
          await loadContentForTab('live', currentProviderId);
        } else {
          showCopyResult(result.Message || 'Copy failed', false);
        }
      } catch (err) {
        showCopyResult('Failed to copy channels: ' + (err.message || 'Unknown error'), false);
      } finally {
        copyChannelsBtn.disabled = false;
        copyChannelsBtn.innerHTML = '<span class="material-icons" style="margin-right: 8px;">content_copy</span><span>Copy Channels</span>';
      }
    });

    const showCopyResult = (message, success) => {
      copyResultMessage.textContent = message;
      copyResultMessage.classList.remove('success', 'error');
      copyResultMessage.classList.add(success ? 'success' : 'error');
      copyResultMessage.classList.remove('hide');
      setTimeout(() => copyResultMessage.classList.add('hide'), 5000);
    };

    // ========================================
    // Overrides: Export / Import / Clear
    // ========================================
    view.querySelector('#ExportOverridesBtn').addEventListener('click', () => {
      if (!currentProviderId) return;
      Xtream.fetchJson(`Xtream/Providers/${encodeURIComponent(currentProviderId)}/Channels/Overrides`)
        .then(data => {
          const blob = new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' });
          const url = URL.createObjectURL(blob);
          const a = document.createElement('a');
          a.href = url;
          a.download = `overrides-${currentProviderId}.json`;
          a.click();
          URL.revokeObjectURL(url);
        })
        .catch(err => Dashboard.alert('Export failed: ' + err.message));
    });

    const importFileInput = view.querySelector('#ImportFileInput');
    view.querySelector('#ImportOverridesBtn').addEventListener('click', () => importFileInput.click());

    importFileInput.addEventListener('change', () => {
      const file = importFileInput.files[0];
      if (!file || !currentProviderId) return;

      const reader = new FileReader();
      reader.onload = (e) => {
        try {
          const data = JSON.parse(e.target.result);
          const overrides = data.overrides || data;

          if (!Array.isArray(overrides)) {
            Dashboard.alert('Invalid format: expected an array of overrides or {overrides: [...]}');
            return;
          }

          Dashboard.showLoadingMsg();
          Xtream.apiRequest(`Xtream/Providers/${encodeURIComponent(currentProviderId)}/Channels/Overrides/Batch`, {
            method: 'POST',
            body: { overrides }
          }).then(result => {
            Dashboard.hideLoadingMsg();
            Dashboard.alert(`Import complete: ${result.applied ?? 0} override(s) applied.`);
            tabData.overrides.loaded = false;
            loadOverridesForProvider(currentProviderId);
          }).catch(err => {
            Dashboard.hideLoadingMsg();
            Dashboard.alert('Import failed: ' + err.message);
          });
        } catch (parseErr) {
          Dashboard.alert('Invalid JSON file: ' + parseErr.message);
        }
      };
      reader.readAsText(file);
      importFileInput.value = '';
    });

    view.querySelector('#ClearOverridesBtn').addEventListener('click', () => {
      if (!currentProviderId) return;
      if (!confirm('Are you sure you want to clear ALL channel overrides for this provider?\n\nThis cannot be undone.')) return;

      Dashboard.showLoadingMsg();
      Xtream.apiRequest(`Xtream/Providers/${encodeURIComponent(currentProviderId)}/Channels/Overrides`, {
        method: 'DELETE'
      }).then(result => {
        Dashboard.hideLoadingMsg();
        Dashboard.alert(result.message || `Cleared ${result.cleared ?? 0} override(s).`);
        tabData.overrides.loaded = false;
        loadOverridesForProvider(currentProviderId);
      }).catch(err => {
        Dashboard.hideLoadingMsg();
        Dashboard.alert('Clear failed: ' + err.message);
      });
    });

    // ========================================
    // Refresh button
    // ========================================
    view.querySelector('#RefreshContent').addEventListener('click', () => {
      if (currentProviderId) {
        tabData[activeTab].loaded = false;
        loadContentForTab(activeTab, currentProviderId);
      }
    });

    // ========================================
    // Form submit - only saves dirty tabs
    // ========================================
    view.querySelector('#XtreamChannelsForm').addEventListener('submit', (e) => {
      e.preventDefault();
      Dashboard.showLoadingMsg();

      ApiClient.getPluginConfiguration(pluginId).then((config) => {
        // Save global settings
        config.IsCatchupVisible = isCatchupVisible.checked;
        config.EnableProviderFailover = enableProviderFailover.checked;
        config.MaxFailoverAttempts = parseInt(maxFailoverAttempts.value, 10) || 3;
        config.IsVodVisible = isVodVisible.checked;
        config.IsTmdbVodOverride = isTmdbVodOverride.checked;
        config.IsSeriesVisible = isSeriesVisible.checked;

        if (currentProviderId) {
          const providerIndex = config.Providers.findIndex(p => p.Id === currentProviderId);
          if (providerIndex !== -1) {
            // Only write back data for tabs that were actually visited
            if (tabData.live.dirty && tabData.live.data) {
              config.Providers[providerIndex].LiveTv = tabData.live.data;
            }
            if (tabData.vod.dirty && tabData.vod.data) {
              config.Providers[providerIndex].Vod = tabData.vod.data;
            }
            if (tabData.series.dirty && tabData.series.data) {
              config.Providers[providerIndex].Series = tabData.series.data;
            }
            if (tabData.overrides.dirty && tabData.overrides.data) {
              config.Providers[providerIndex].LiveTvOverrides = Xtream.filter(
                tabData.overrides.data,
                overrides => Object.keys(overrides).length > 0
              );
            }
          }
        }

        ApiClient.updatePluginConfiguration(pluginId, config).then((result) => {
          Dashboard.processPluginConfigurationUpdateResult(result);
          updateOverrideStats();
        });
      });

      return false;
    });

    // ========================================
    // Initial load
    // ========================================
    loadProviders();
  }));
}
