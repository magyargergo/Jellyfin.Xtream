export default function (view) {
  view.addEventListener("viewshow", () => import(
    window.ApiClient.getUrl("web/ConfigurationPage", {
      name: "Xtream.js",
    })
  ).then((Xtream) => Xtream.default
  ).then((Xtream) => {
    const pluginId = Xtream.pluginConfig.UniqueId;
    Xtream.setTabs('XtreamProviders');

    let providers = [];

    const container = view.querySelector('#ProvidersContainer');
    const dialog = view.querySelector('#ProviderDialog');
    const form = view.querySelector('#ProviderForm');
    const dialogTitle = view.querySelector('#ProviderDialogTitle');
    const testResultPanel = view.querySelector('#TestResultPanel');
    const migrateLegacyBtn = view.querySelector('#MigrateLegacyBtn');

    const generateProviderId = () => {
      return Math.random().toString(36).substring(2, 10);
    };

    const showDialog = (isEdit = false) => {
      dialogTitle.textContent = isEdit ? 'Edit Provider' : 'Add Provider';
      view.querySelector('#ProviderDialogSaveText').textContent = isEdit ? 'Save' : 'Add';
      dialog.style.display = 'block';
      dialog.classList.remove('hide');
    };

    const hideDialog = () => {
      dialog.style.display = 'none';
      dialog.classList.add('hide');
      form.reset();
      view.querySelector('#ProviderId').value = '';
    };

    const createProviderCard = (provider) => {
      const card = document.createElement('div');
      card.className = 'card provider-card';
      card.dataset.providerId = provider.Id;
      card.style.cssText = 'margin-bottom: 16px; padding: 16px; background: var(--theme-card-background-color, #1c1c1e); border-radius: 8px; border-left: 4px solid ' + (provider.Enabled ? '#4caf50' : '#888') + ';';

      const header = document.createElement('div');
      header.style.cssText = 'display: flex; justify-content: space-between; align-items: center; margin-bottom: 12px;';

      const titleContainer = document.createElement('div');
      const title = document.createElement('h3');
      title.style.cssText = 'margin: 0; font-size: 1.1em;';
      title.textContent = provider.Name || 'Unnamed Provider';
      titleContainer.appendChild(title);

      const subtitle = document.createElement('div');
      subtitle.style.cssText = 'font-size: 0.85em; color: #888; margin-top: 4px;';
      subtitle.textContent = provider.BaseUrl || 'No URL configured';
      titleContainer.appendChild(subtitle);

      const actions = document.createElement('div');
      actions.style.cssText = 'display: flex; gap: 8px;';

      const testBtn = document.createElement('button');
      testBtn.setAttribute('is', 'emby-button');
      testBtn.className = 'fab emby-button';
      testBtn.title = 'Test Connection';
      testBtn.innerHTML = '<span class="material-icons wifi_tethering"></span>';
      testBtn.onclick = () => testProvider(provider);

      const editBtn = document.createElement('button');
      editBtn.setAttribute('is', 'emby-button');
      editBtn.className = 'fab emby-button';
      editBtn.title = 'Edit Provider';
      editBtn.innerHTML = '<span class="material-icons edit"></span>';
      editBtn.onclick = () => editProvider(provider);

      const deleteBtn = document.createElement('button');
      deleteBtn.setAttribute('is', 'emby-button');
      deleteBtn.className = 'fab emby-button';
      deleteBtn.title = 'Delete Provider';
      deleteBtn.innerHTML = '<span class="material-icons delete"></span>';
      deleteBtn.style.color = '#f44336';
      deleteBtn.onclick = () => deleteProvider(provider);

      actions.appendChild(testBtn);
      actions.appendChild(editBtn);
      actions.appendChild(deleteBtn);

      header.appendChild(titleContainer);
      header.appendChild(actions);
      card.appendChild(header);

      // Status/info row
      const infoRow = document.createElement('div');
      infoRow.style.cssText = 'display: flex; gap: 16px; font-size: 0.9em; color: #aaa;';

      const statusBadge = document.createElement('span');
      statusBadge.style.cssText = 'padding: 2px 8px; border-radius: 4px; font-size: 0.85em;';
      if (provider.Enabled) {
        statusBadge.style.background = 'rgba(76, 175, 80, 0.2)';
        statusBadge.style.color = '#4caf50';
        statusBadge.textContent = 'Enabled';
      } else {
        statusBadge.style.background = 'rgba(136, 136, 136, 0.2)';
        statusBadge.style.color = '#888';
        statusBadge.textContent = 'Disabled';
      }
      infoRow.appendChild(statusBadge);

      const userInfo = document.createElement('span');
      userInfo.textContent = `User: ${provider.Username || 'Not set'}`;
      infoRow.appendChild(userInfo);

      card.appendChild(infoRow);

      return card;
    };

    const renderProviders = () => {
      container.innerHTML = '';
      if (providers.length === 0) {
        const empty = document.createElement('div');
        empty.style.cssText = 'text-align: center; padding: 40px; color: #888;';
        empty.innerHTML = `
          <span class="material-icons" style="font-size: 48px; display: block; margin-bottom: 16px;">live_tv</span>
          <p>No providers configured yet.</p>
          <p>Click the + button above to add your first Xtream provider.</p>
        `;
        container.appendChild(empty);
        return;
      }

      providers.forEach(provider => {
        container.appendChild(createProviderCard(provider));
      });
    };

    const loadProviders = () => {
      Dashboard.showLoadingMsg();
      ApiClient.getPluginConfiguration(pluginId).then((config) => {
        providers = config.Providers || [];
        renderProviders();
        Dashboard.hideLoadingMsg();
        // Check for legacy configuration
        checkLegacyConfig();
      });
    };

    const checkLegacyConfig = async () => {
      try {
        const result = await Xtream.apiRequest('Xtream/LegacyConfig');
        if (result.hasLegacyConfig) {
          migrateLegacyBtn.classList.remove('hide');
          migrateLegacyBtn.title = `Migrate legacy configuration (${result.username}@${result.baseUrl})`;
        } else {
          migrateLegacyBtn.classList.add('hide');
        }
      } catch (err) {
        console.error('Failed to check legacy config:', err);
      }
    };

    const migrateLegacyConfiguration = async () => {
      if (!confirm('This will migrate your old configuration to a new provider.\n\nThe old credentials and channel selections will be converted.\n\nContinue?')) {
        return;
      }

      Dashboard.showLoadingMsg();
      try {
        const result = await Xtream.apiRequest('Xtream/MigrateLegacy', { method: 'POST' });
        if (result.success) {
          testResultPanel.classList.remove('hide');
          testResultPanel.style.background = 'rgba(76, 175, 80, 0.1)';
          testResultPanel.style.borderLeft = '3px solid #4caf50';
          testResultPanel.innerHTML = `<span style="color: #4caf50;"><strong>Success:</strong> ${result.message}</span>`;
          // Reload providers to show the migrated one
          loadProviders();
        } else {
          testResultPanel.classList.remove('hide');
          testResultPanel.style.background = 'rgba(244, 67, 54, 0.1)';
          testResultPanel.style.borderLeft = '3px solid #f44336';
          testResultPanel.innerHTML = `<span style="color: #f44336;"><strong>Error:</strong> ${result.message}</span>`;
        }
      } catch (err) {
        testResultPanel.classList.remove('hide');
        testResultPanel.style.background = 'rgba(244, 67, 54, 0.1)';
        testResultPanel.style.borderLeft = '3px solid #f44336';
        testResultPanel.innerHTML = `<span style="color: #f44336;"><strong>Error:</strong> ${err.message}</span>`;
      } finally {
        Dashboard.hideLoadingMsg();
      }
    };

    const saveProviders = () => {
      Dashboard.showLoadingMsg();
      ApiClient.getPluginConfiguration(pluginId).then((config) => {
        config.Providers = providers;
        ApiClient.updatePluginConfiguration(pluginId, config).then((result) => {
          Dashboard.processPluginConfigurationUpdateResult(result);
          renderProviders();
        });
      });
    };

    const editProvider = (provider) => {
      view.querySelector('#ProviderId').value = provider.Id;
      view.querySelector('#ProviderName').value = provider.Name || '';
      view.querySelector('#ProviderBaseUrl').value = provider.BaseUrl || '';
      view.querySelector('#ProviderUsername').value = provider.Username || '';
      view.querySelector('#ProviderPassword').value = provider.Password || '';
      view.querySelector('#ProviderEnabled').checked = provider.Enabled !== false;
      showDialog(true);
    };

    const deleteProvider = (provider) => {
      if (!confirm(`Are you sure you want to delete "${provider.Name}"?\n\nThis will remove all channel selections for this provider.`)) {
        return;
      }

      providers = providers.filter(p => p.Id !== provider.Id);
      saveProviders();
    };

    const testProvider = async (provider) => {
      testResultPanel.classList.remove('hide');
      testResultPanel.style.background = 'rgba(102, 126, 234, 0.1)';
      testResultPanel.style.borderLeft = '3px solid #667eea';
      testResultPanel.innerHTML = '<span style="color: #888;">Testing connection...</span>';

      try {
        const result = await Xtream.apiRequest(`Xtream/TestProvider/${provider.Id}`);
        if (result.success) {
          testResultPanel.style.background = 'rgba(76, 175, 80, 0.1)';
          testResultPanel.style.borderLeft = '3px solid #4caf50';

          let expInfo = '';
          if (result.expDate) {
            const expDate = new Date(result.expDate);
            const daysLeft = Math.ceil((expDate - new Date()) / (1000 * 60 * 60 * 24));
            expInfo = daysLeft > 0 ? ` (${daysLeft} days left)` : ' (EXPIRED)';
          }

          testResultPanel.innerHTML = `
            <div style="display: grid; grid-template-columns: repeat(2, 1fr); gap: 8px; font-size: 0.9em;">
              <div><strong>Provider:</strong> ${provider.Name}</div>
              <div><strong>Status:</strong> <span style="color: #4caf50; font-weight: bold;">${result.status || 'Connected'}</span></div>
              <div><strong>Account:</strong> ${result.username || 'N/A'}${result.isTrial ? ' (Trial)' : ''}</div>
              <div><strong>Max Connections:</strong> ${result.maxConnections || 'N/A'}</div>
              <div style="grid-column: span 2;"><strong>Expires:</strong> ${result.expDate ? new Date(result.expDate).toLocaleDateString() + expInfo : 'N/A'}</div>
            </div>
          `;
        } else {
          throw new Error(result.message || 'Connection failed');
        }
      } catch (err) {
        testResultPanel.style.background = 'rgba(244, 67, 54, 0.1)';
        testResultPanel.style.borderLeft = '3px solid #f44336';
        testResultPanel.innerHTML = `<span style="color: #f44336;"><strong>Error:</strong> ${err.message}</span>`;
      }
    };

    // Event handlers
    view.querySelector('#AddProviderBtn').addEventListener('click', () => {
      form.reset();
      view.querySelector('#ProviderId').value = '';
      view.querySelector('#ProviderEnabled').checked = true;
      showDialog(false);
    });

    view.querySelector('#CancelProviderBtn').addEventListener('click', hideDialog);

    migrateLegacyBtn.addEventListener('click', migrateLegacyConfiguration);

    form.addEventListener('submit', (e) => {
      e.preventDefault();

      // Get form data using FormData API which works reliably with custom elements
      const formData = new FormData(form);

      // Also try direct element access as fallback
      const providerIdEl = view.querySelector('#ProviderId');
      const providerNameEl = view.querySelector('#ProviderName');
      const providerBaseUrlEl = view.querySelector('#ProviderBaseUrl');
      const providerUsernameEl = view.querySelector('#ProviderUsername');
      const providerPasswordEl = view.querySelector('#ProviderPassword');
      const providerEnabledEl = view.querySelector('#ProviderEnabled');

      // Debug: log both FormData and direct element values
      console.log('FormData entries:', Object.fromEntries(formData.entries()));
      console.log('Direct element values:', {
        ProviderId: providerIdEl?.value,
        ProviderName: providerNameEl?.value,
        ProviderBaseUrl: providerBaseUrlEl?.value,
        ProviderUsername: providerUsernameEl?.value,
        ProviderPassword: providerPasswordEl?.value ? '***' : '(empty)',
        ProviderEnabled: providerEnabledEl?.checked
      });

      // Use FormData values (by name attribute), fall back to direct value
      const providerId = formData.get('ProviderId') || providerIdEl?.value || '';
      const providerName = formData.get('ProviderName') || providerNameEl?.value || '';
      const providerBaseUrl = formData.get('ProviderBaseUrl') || providerBaseUrlEl?.value || '';
      const providerUsername = formData.get('ProviderUsername') || providerUsernameEl?.value || '';
      const providerPassword = formData.get('ProviderPassword') || providerPasswordEl?.value || '';
      const providerEnabled = providerEnabledEl?.checked ?? true;

      // Validate required fields
      if (!providerName || !providerBaseUrl || !providerUsername || !providerPassword) {
        console.error('Validation failed - empty fields detected:', {
          providerName: providerName || '(empty)',
          providerBaseUrl: providerBaseUrl || '(empty)',
          providerUsername: providerUsername || '(empty)',
          providerPassword: providerPassword ? '(set)' : '(empty)'
        });
        // Show an error to the user
        testResultPanel.classList.remove('hide');
        testResultPanel.style.background = 'rgba(244, 67, 54, 0.1)';
        testResultPanel.style.borderLeft = '3px solid #f44336';
        testResultPanel.innerHTML = '<span style="color: #f44336;"><strong>Error:</strong> All fields are required. Please fill in all provider details.</span>';
        return;
      }

      const providerData = {
        Id: providerId || generateProviderId(),
        Name: providerName,
        BaseUrl: providerBaseUrl.replace(/\/$/, ''), // Remove trailing slash
        Username: providerUsername,
        Password: providerPassword,
        Enabled: providerEnabled,
      };

      console.log('Provider data to save:', { ...providerData, Password: '***' });

      if (providerId) {
        // Update existing provider - preserve channel selections
        const existingIndex = providers.findIndex(p => p.Id === providerId);
        if (existingIndex !== -1) {
          // Preserve LiveTv, Vod, Series, LiveTvOverrides from existing provider
          providerData.LiveTv = providers[existingIndex].LiveTv || {};
          providerData.Vod = providers[existingIndex].Vod || {};
          providerData.Series = providers[existingIndex].Series || {};
          providerData.LiveTvOverrides = providers[existingIndex].LiveTvOverrides || {};
          providers[existingIndex] = providerData;
        }
      } else {
        // Add new provider with empty channel selections
        providerData.LiveTv = {};
        providerData.Vod = {};
        providerData.Series = {};
        providerData.LiveTvOverrides = {};
        providers.push(providerData);
      }

      hideDialog();
      saveProviders();
    });

    // Close dialog on background click
    dialog.addEventListener('click', (e) => {
      if (e.target === dialog) {
        hideDialog();
      }
    });

    // Initial load
    loadProviders();
  }));
}
