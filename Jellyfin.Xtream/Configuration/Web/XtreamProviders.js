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
          let expWarning = '';
          if (result.expDate) {
            const expDate = new Date(result.expDate);
            const daysLeft = Math.ceil((expDate - new Date()) / (1000 * 60 * 60 * 24));
            if (daysLeft <= 0) {
              expInfo = ' <span style="background: rgba(244, 67, 54, 0.2); color: #f44336; padding: 2px 6px; border-radius: 3px; font-size: 0.85em; font-weight: bold;">EXPIRED</span>';
            } else if (daysLeft <= 7) {
              expInfo = ` <span style="background: rgba(255, 152, 0, 0.2); color: #ff9800; padding: 2px 6px; border-radius: 3px; font-size: 0.85em;">Expires in ${daysLeft} days</span>`;
              expWarning = '<div style="grid-column: span 2; background: rgba(255, 152, 0, 0.1); padding: 8px; border-radius: 4px; margin-top: 4px;"><span class="material-icons" style="font-size: 16px; vertical-align: middle; color: #ff9800;">warning</span> <span style="color: #ff9800;">This account is expiring soon!</span></div>';
            } else {
              expInfo = ` (${daysLeft} days left)`;
            }
          }

          testResultPanel.innerHTML = `
            <div style="display: grid; grid-template-columns: repeat(2, 1fr); gap: 8px; font-size: 0.9em;">
              <div><strong>Provider:</strong> ${provider.Name}</div>
              <div><strong>Status:</strong> <span style="color: #4caf50; font-weight: bold;">${result.status || 'Connected'}</span></div>
              <div><strong>Account:</strong> ${result.username || 'N/A'}${result.isTrial ? ' (Trial)' : ''}</div>
              <div><strong>Max Connections:</strong> ${result.maxConnections || 'N/A'}</div>
              <div style="grid-column: span 2;"><strong>Expires:</strong> ${result.expDate ? new Date(result.expDate).toLocaleDateString() + expInfo : 'N/A'}</div>
              ${expWarning}
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

    // =====================================================
    // DISCOVERY FUNCTIONALITY
    // =====================================================
    const scrapeDialog = view.querySelector('#ScrapeDialog');
    const scrapeOptions = view.querySelector('#ScrapeOptions');
    const scrapeProgress = view.querySelector('#ScrapeProgress');
    const scrapeResults = view.querySelector('#ScrapeResults');

    // Simple polling-based progress tracking
    // Polling is more reliable than WebSocket for this use case and provides smooth updates
    let pollInterval = null;
    let isDiscoveryActive = false;
    const POLL_INTERVAL_MS = 1000; // Poll every second for smooth updates

    const stopPolling = () => {
      if (pollInterval) {
        clearInterval(pollInterval);
        pollInterval = null;
      }
      isDiscoveryActive = false;
    };

    const showScrapeDialog = () => {
      scrapeDialog.style.display = 'block';
      scrapeDialog.classList.remove('hide');
      scrapeOptions.classList.remove('hide');
      scrapeProgress.classList.add('hide');
      scrapeResults.classList.add('hide');
    };

    const hideScrapeDialog = () => {
      stopPolling();
      scrapeDialog.style.display = 'none';
      scrapeDialog.classList.add('hide');
    };

    const showScrapeProgress = () => {
      scrapeOptions.classList.add('hide');
      scrapeProgress.classList.remove('hide');
      scrapeResults.classList.add('hide');
    };

    const showScrapeResults = (result) => {
      scrapeOptions.classList.add('hide');
      scrapeProgress.classList.add('hide');
      scrapeResults.classList.remove('hide');

      const summary = view.querySelector('#ScrapeResultsSummary');
      const list = view.querySelector('#ScrapeResultsList');

      if (result.Success) {
        summary.style.background = 'rgba(76, 175, 80, 0.1)';
        summary.style.borderLeftColor = '#4caf50';
        summary.innerHTML = `
          <strong>Discovery Complete!</strong><br>
          Found ${result.TotalCredentialsFound} providers, tested ${result.TotalCredentialsTested}.<br>
          Working: ${result.WorkingProviderCount} | + EPG: ${result.WorkingWithEpgCount} | + Polish: ${result.FullyWorkingCount}
        `;
      } else {
        summary.style.background = 'rgba(244, 67, 54, 0.1)';
        summary.style.borderLeftColor = '#f44336';
        summary.innerHTML = `<strong>Discovery Failed:</strong> ${result.ErrorMessage || 'Unknown error'}`;
      }

      list.innerHTML = '';

      // Show fully working providers first, then working
      const providersToShow = result.FullyWorkingProviders?.length > 0
        ? result.FullyWorkingProviders
        : result.WorkingProviders || [];

      if (providersToShow.length === 0) {
        list.innerHTML = '<div style="color: #888; text-align: center; padding: 20px;">No working providers found.</div>';
        return;
      }

      providersToShow.forEach(provider => {
        const card = document.createElement('div');
        card.style.cssText = 'padding: 12px; margin-bottom: 8px; background: rgba(255,255,255,0.05); border-radius: 4px; border-left: 3px solid ' + (provider.IsFullyWorking ? '#4caf50' : '#2196f3') + ';';

        const statusBadges = [];
        if (provider.StreamWorks) {
          const streamLabel = provider.StreamStatus || 'Stream OK';
          statusBadges.push('<span style="background: rgba(76, 175, 80, 0.2); color: #4caf50; padding: 2px 6px; border-radius: 3px; font-size: 0.8em;">' + streamLabel + '</span>');
        }
        if (provider.HasEpg) statusBadges.push('<span style="background: rgba(33, 150, 243, 0.2); color: #2196f3; padding: 2px 6px; border-radius: 3px; font-size: 0.8em;">EPG (' + provider.EpgProgramCount + ')</span>');
        if (provider.HasPolishChannels) statusBadges.push('<span style="background: rgba(156, 39, 176, 0.2); color: #9c27b0; padding: 2px 6px; border-radius: 3px; font-size: 0.8em;">Polish: ' + provider.PolishChannelCount + '</span>');

        let expInfo = '';
        let daysLeft = null;
        if (provider.ExpirationDate) {
          const expDate = new Date(provider.ExpirationDate);
          daysLeft = Math.ceil((expDate - new Date()) / (1000 * 60 * 60 * 24));
          if (daysLeft <= 0) {
            expInfo = ' <span style="background: rgba(244, 67, 54, 0.2); color: #f44336; padding: 2px 6px; border-radius: 3px; font-size: 0.8em;">EXPIRED</span>';
          } else if (daysLeft <= 7) {
            expInfo = ` <span style="background: rgba(255, 152, 0, 0.2); color: #ff9800; padding: 2px 6px; border-radius: 3px; font-size: 0.8em;">Expires in ${daysLeft}d</span>`;
          } else {
            expInfo = ` (${daysLeft} days)`;
          }
        }

        card.innerHTML = `
          <div style="display: flex; justify-content: space-between; align-items: flex-start;">
            <div>
              <div style="font-weight: bold;">${provider.Server}:${provider.Port}</div>
              <div style="font-size: 0.9em; color: #aaa;">User: ${provider.Username} | Channels: ${provider.TotalChannelCount}${expInfo}</div>
              <div style="margin-top: 6px;">${statusBadges.join(' ')}</div>
            </div>
            <button is="emby-button" class="fab emby-button import-provider-btn" title="Import Provider" style="background: rgba(76, 175, 80, 0.2);"
              data-server="${provider.Server}" data-port="${provider.Port}" data-username="${provider.Username}" data-password="${provider.Password}">
              <span class="material-icons">add</span>
            </button>
          </div>
        `;
        list.appendChild(card);
      });

      // Attach import handlers
      list.querySelectorAll('.import-provider-btn').forEach(btn => {
        btn.addEventListener('click', async () => {
          const providerData = {
            Server: btn.dataset.server,
            Port: parseInt(btn.dataset.port),
            Username: btn.dataset.username,
            Password: btn.dataset.password
          };

          try {
            const result = await Xtream.apiRequest('Xtream/ImportDiscoveredProvider', {
              method: 'POST',
              body: providerData
            });

            if (result.success) {
              btn.innerHTML = '<span class="material-icons">check</span>';
              btn.style.background = 'rgba(76, 175, 80, 0.5)';
              btn.disabled = true;
              loadProviders();
            } else {
              alert(result.message || 'Import failed');
            }
          } catch (err) {
            alert('Import failed: ' + err.message);
          }
        });
      });
    };

    const updateProgress = (progress) => {
      const progressBar = view.querySelector('#ScrapeProgressBar');
      const progressPhase = view.querySelector('#ScrapeProgressPhase');
      const progressText = view.querySelector('#ScrapeProgressText');

      // Update phase display
      const phaseLabels = {
        'Scraping': 'Discovering providers...',
        'Discovering': 'Discovering providers...',
        'Testing': 'Testing providers...',
        'Completed': 'Complete!',
        'Failed': 'Failed'
      };
      progressPhase.textContent = phaseLabels[progress.Phase] || progress.Phase;

      // Update progress bar
      if (progress.TotalItems > 0) {
        const percent = Math.round((progress.CurrentItem / progress.TotalItems) * 100);
        progressBar.style.width = percent + '%';
      }

      // Update message
      if (progress.Message) {
        progressText.textContent = progress.Message;
      }

      // Update stats (funnel: Working -> +EPG -> +Polish)
      if (progress.WorkingProviders !== undefined) {
        view.querySelector('#StatWorking').textContent = progress.WorkingProviders;
      }
      if (progress.WorkingWithEpg !== undefined) {
        view.querySelector('#StatWithEpg').textContent = progress.WorkingWithEpg;
      }
      if (progress.FullyWorking !== undefined) {
        view.querySelector('#StatFullyWorking').textContent = progress.FullyWorking;
      }
    };

    const startScraping = async () => {
      showScrapeProgress();

      const maxPages = parseInt(view.querySelector('#ScrapeMaxPages').value) || 5;
      const maxWorkers = parseInt(view.querySelector('#ScrapeWorkers').value) || 10;
      const testStream = view.querySelector('#ScrapeTestStream').checked;
      const testEpg = view.querySelector('#ScrapeTestEpg').checked;

      view.querySelector('#ScrapeProgressPhase').textContent = 'Starting...';
      view.querySelector('#ScrapeProgressBar').style.width = '0%';
      view.querySelector('#ScrapeProgressText').textContent = 'Initializing...';
      view.querySelector('#StatWorking').textContent = '0';
      view.querySelector('#StatWithEpg').textContent = '0';
      view.querySelector('#StatFullyWorking').textContent = '0';

      try {
        // First, start the discovery operation
        const startResult = await Xtream.apiRequest('Xtream/StartDiscovery', {
          method: 'POST',
          body: {
            MaxPages: maxPages,
            MaxDiscoveryWorkers: 5,
            MaxTestWorkers: maxWorkers,
            TestStream: testStream,
            TestEpg: testEpg,
            PolishOnly: true
          }
        });

        if (!startResult.success) {
          showScrapeResults({
            Success: false,
            ErrorMessage: startResult.message || 'Failed to start discovery'
          });
          return;
        }

        // Start polling for progress updates
        startProgressPolling();

      } catch (err) {
        showScrapeResults({
          Success: false,
          ErrorMessage: err.message
        });
      }
    };

    const startProgressPolling = () => {
      isDiscoveryActive = true;

      // Poll immediately, then every second
      pollProgress();
      pollInterval = setInterval(pollProgress, POLL_INTERVAL_MS);
    };

    const pollProgress = async () => {
      if (!isDiscoveryActive) {
        stopPolling();
        return;
      }

      try {
        const status = await Xtream.apiRequest('Xtream/DiscoveryStatus');
        const isRunning = status.IsRunning ?? status.isRunning;
        const progress = status.Progress || status.progress;

        if (progress) {
          updateProgress(progress);
        }

        // Check if operation completed
        if (!isRunning) {
          console.log('[Discovery] Operation completed');
          stopPolling();
          fetchResults();
        }
      } catch (err) {
        console.error('[Discovery] Status poll failed:', err);
        // Don't stop polling on error - server might be temporarily busy
      }
    };

    const fetchResults = async () => {
      try {
        const result = await Xtream.apiRequest('Xtream/DiscoveryResult');
        view.querySelector('#ScrapeProgressBar').style.width = '100%';
        showScrapeResults(result);
      } catch (err) {
        showScrapeResults({
          Success: false,
          ErrorMessage: 'Failed to fetch results: ' + err.message
        });
      }
    };

    const cancelScraping = async () => {
      stopPolling();
      try {
        await Xtream.apiRequest('Xtream/CancelDiscovery', { method: 'POST' });
        view.querySelector('#ScrapeProgressPhase').textContent = 'Cancelled';
        view.querySelector('#ScrapeProgressText').textContent = 'Operation cancelled by user';
      } catch (err) {
        console.error('[Discovery] Cancel failed:', err);
      }
    };

    const loadLastResults = async () => {
      try {
        const result = await Xtream.apiRequest('Xtream/DiscoveryResult');
        if (result.Success || result.success) {
          showScrapeResults(result);
        } else {
          const errorMsg = result.ErrorMessage || result.errorMessage || 'No cached results available';
          alert(errorMsg);
        }
      } catch (err) {
        alert('Failed to load last results: ' + err.message);
      }
    };

    const clearCache = async () => {
      if (!confirm('Clear the cached discovery results?\n\nYou will need to run a new discovery to see providers.')) {
        return;
      }

      try {
        const result = await Xtream.apiRequest('Xtream/ClearDiscoveryCache', { method: 'POST' });
        if (result.success) {
          alert(result.message);
        } else {
          alert('Failed to clear cache');
        }
      } catch (err) {
        alert('Failed to clear cache: ' + err.message);
      }
    };

    // Discovery event handlers
    view.querySelector('#ScrapeProvidersBtn').addEventListener('click', showScrapeDialog);
    view.querySelector('#CloseScrapeDialogBtn').addEventListener('click', hideScrapeDialog);
    view.querySelector('#StartScrapeBtn').addEventListener('click', startScraping);
    view.querySelector('#LoadLastResultsBtn').addEventListener('click', loadLastResults);
    view.querySelector('#ClearCacheBtn').addEventListener('click', clearCache);
    view.querySelector('#CancelScrapeBtn').addEventListener('click', cancelScraping);
    view.querySelector('#ScrapeAgainBtn').addEventListener('click', () => {
      scrapeOptions.classList.remove('hide');
      scrapeResults.classList.add('hide');
    });
    view.querySelector('#CloseScrapeResultsBtn').addEventListener('click', hideScrapeDialog);

    scrapeDialog.addEventListener('click', (e) => {
      if (e.target === scrapeDialog) {
        hideScrapeDialog();
      }
    });

    // Initial load
    loadProviders();
  }));
}
