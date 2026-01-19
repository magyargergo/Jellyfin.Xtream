export default function (view) {
  view.addEventListener("viewshow", () => Promise.all([
    import(window.ApiClient.getUrl("web/ConfigurationPage", { name: "Xtream.js" })),
    import(window.ApiClient.getUrl("web/ConfigurationPage", { name: "XtreamStyles.js" }))
  ]).then(([XtreamModule, StylesModule]) => {
    const Xtream = XtreamModule.default;
    const XtreamStyles = StylesModule.default;

    // CSS is auto-loaded by XtreamStyles module
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

    const formatErrorMessage = (message) => {
      if (!message) return 'Unknown error';
      if (message.includes('Unexpected character')) {
        return 'Invalid response (provider may be down or returning HTML instead of JSON)';
      }
      if (message.includes('timeout') || message.includes('Timeout')) {
        return 'Connection timeout - provider may be slow or unreachable';
      }
      if (message.includes('401') || message.includes('403')) {
        return 'Authentication failed - check your credentials';
      }
      if (message.includes('404')) {
        return 'Provider API not found - check the URL';
      }
      if (message.includes('ECONNREFUSED') || message.includes('connection refused')) {
        return 'Connection refused - provider server may be down';
      }
      if (message.includes('ENOTFOUND') || message.includes('getaddrinfo')) {
        return 'DNS lookup failed - check the provider URL';
      }
      if (message.length > 80) {
        return message.substring(0, 77) + '...';
      }
      return message;
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

      // Show priority badge (calculated automatically based on performance)
      const priority = provider.Priority ?? 50;
      const priorityBadge = document.createElement('span');
      priorityBadge.style.cssText = 'padding: 2px 8px; border-radius: 4px; font-size: 0.85em;';
      priorityBadge.title = 'Auto-calculated from streaming performance history';
      if (priority <= 20) {
        priorityBadge.style.background = 'rgba(76, 175, 80, 0.2)';
        priorityBadge.style.color = '#4caf50';
        priorityBadge.textContent = `★ Excellent (${priority})`;
      } else if (priority <= 40) {
        priorityBadge.style.background = 'rgba(33, 150, 243, 0.2)';
        priorityBadge.style.color = '#2196f3';
        priorityBadge.textContent = `Good (${priority})`;
      } else if (priority >= 80) {
        priorityBadge.style.background = 'rgba(244, 67, 54, 0.2)';
        priorityBadge.style.color = '#f44336';
        priorityBadge.textContent = `Poor (${priority})`;
      } else {
        priorityBadge.style.background = 'rgba(255, 255, 255, 0.1)';
        priorityBadge.style.color = '#aaa';
        priorityBadge.textContent = `Score: ${priority}`;
      }
      infoRow.appendChild(priorityBadge);

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

      // Sort providers by priority (lower = higher priority)
      const sortedProviders = [...providers].sort((a, b) => {
        const priorityA = a.Priority ?? 50;
        const priorityB = b.Priority ?? 50;
        if (priorityA !== priorityB) return priorityA - priorityB;
        // Secondary sort by name for consistent ordering
        return (a.Name || '').localeCompare(b.Name || '');
      });

      sortedProviders.forEach(provider => {
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
        // Load connection status
        loadConnectionStatus();
      });
    };

    const connectionStatusPanel = view.querySelector('#ConnectionStatusPanel');
    const connectionStatusContent = view.querySelector('#ConnectionStatusContent');
    const connectionStatusIcon = view.querySelector('#ConnectionStatusIcon');

    const loadConnectionStatus = async () => {
      try {
        const status = await Xtream.apiRequest('Xtream/ConnectionStatus');

        connectionStatusPanel.classList.remove('hide');

        // Set icon color based on warning level
        if (status.WarningLevel === 'Critical') {
          connectionStatusIcon.style.color = '#f44336';
          connectionStatusIcon.textContent = 'warning';
          connectionStatusPanel.style.borderLeft = '4px solid #f44336';
        } else if (status.WarningLevel === 'Warning') {
          connectionStatusIcon.style.color = '#ff9800';
          connectionStatusIcon.textContent = 'warning';
          connectionStatusPanel.style.borderLeft = '4px solid #ff9800';
        } else {
          connectionStatusIcon.style.color = '#4caf50';
          connectionStatusIcon.textContent = 'wifi';
          connectionStatusPanel.style.borderLeft = '4px solid #4caf50';
        }

        // Calculate provider utilization (more meaningful than plugin-only utilization)
        const providerUtilization = status.TotalProviderCapacity > 0
          ? Math.round(100 * status.TotalProviderActiveConnections / status.TotalProviderCapacity)
          : 0;
        const hasExternalUsage = status.TotalProviderActiveConnections > status.PluginActiveStreams;

        // Determine gauge color based on provider utilization (this is what actually matters)
        const gaugeColor = providerUtilization >= 100 ? '#f44336' :
                          providerUtilization >= 80 ? '#ff9800' : '#4caf50';

        let html = '';

        // Main stats row with provider capacity gauge
        html += '<div style="display: grid; grid-template-columns: repeat(auto-fit, minmax(140px, 1fr)); gap: 12px; margin-bottom: 16px;">';

        // Provider Capacity Gauge (the main indicator)
        html += `
          <div style="text-align: center;">
            <div style="position: relative; width: 90px; height: 90px; margin: 0 auto;">
              <svg viewBox="0 0 36 36" style="transform: rotate(-90deg);">
                <path d="M18 2.5 a 15.5 15.5 0 1 1 0 31 a 15.5 15.5 0 1 1 0 -31"
                      fill="none" stroke="#333" stroke-width="3" />
                <path d="M18 2.5 a 15.5 15.5 0 1 1 0 31 a 15.5 15.5 0 1 1 0 -31"
                      fill="none" stroke="${gaugeColor}" stroke-width="3"
                      stroke-dasharray="${Math.min(providerUtilization, 100)}, 100" />
              </svg>
              <div style="position: absolute; top: 50%; left: 50%; transform: translate(-50%, -50%); text-align: center;">
                <div style="font-size: 1.1em; font-weight: bold; color: ${gaugeColor};">${providerUtilization}%</div>
              </div>
            </div>
            <div style="font-size: 0.8em; color: #888; margin-top: 4px;">Provider Load</div>
          </div>
        `;

        // Connections breakdown
        html += `
          <div style="background: rgba(255,255,255,0.05); padding: 12px; border-radius: 8px; text-align: center;">
            <div style="font-size: 1.6em; font-weight: bold;">
              <span style="color: ${gaugeColor};">${status.TotalProviderActiveConnections}</span>
              <span style="color: #666;">/</span>
              <span style="color: #888;">${status.TotalProviderCapacity}</span>
            </div>
            <div style="font-size: 0.8em; color: #888;">Connections Used</div>
            ${hasExternalUsage ? `
              <div style="font-size: 0.7em; color: #ff9800; margin-top: 4px;">
                <span class="material-icons" style="font-size: 12px; vertical-align: middle;">devices_other</span>
                ${status.TotalProviderActiveConnections - status.PluginActiveStreams} external
              </div>
            ` : ''}
          </div>
        `;

        // This Plugin's streams
        html += `
          <div style="background: rgba(33, 150, 243, 0.1); padding: 12px; border-radius: 8px; text-align: center; border: 1px solid rgba(33, 150, 243, 0.2);">
            <div style="font-size: 1.6em; font-weight: bold; color: #2196f3;">${status.PluginActiveStreams}</div>
            <div style="font-size: 0.8em; color: #888;">This Plugin</div>
            ${status.ConfiguredMaxStreams > 0 ? `
              <div style="font-size: 0.7em; color: #666; margin-top: 4px;">Limit: ${status.ConfiguredMaxStreams}</div>
            ` : ''}
          </div>
        `;

        // Available slots (the key actionable metric)
        const availableColor = status.AvailableSlots > 2 ? '#4caf50' :
                               status.AvailableSlots > 0 ? '#ff9800' : '#f44336';
        html += `
          <div style="background: rgba(${status.AvailableSlots > 0 ? '76, 175, 80' : '244, 67, 54'}, 0.1); padding: 12px; border-radius: 8px; text-align: center; border: 1px solid rgba(${status.AvailableSlots > 0 ? '76, 175, 80' : '244, 67, 54'}, 0.3);">
            <div style="font-size: 1.6em; font-weight: bold; color: ${availableColor};">${status.AvailableSlots}</div>
            <div style="font-size: 0.8em; color: #888;">Available Now</div>
            ${status.AvailableSlots === 0 && hasExternalUsage ? `
              <div style="font-size: 0.7em; color: #f44336; margin-top: 4px;">Blocked by external</div>
            ` : ''}
          </div>
        `;

        html += '</div>';

        // Warning message (if any)
        if (status.WarningMessage) {
          const warningColor = status.WarningLevel === 'Critical' ? '#f44336' : '#ff9800';
          const warningBg = status.WarningLevel === 'Critical' ? '244, 67, 54' : '255, 152, 0';
          html += `
            <div style="padding: 10px 14px; background: rgba(${warningBg}, 0.1); border-radius: 6px; border-left: 3px solid ${warningColor}; margin-bottom: 16px;">
              <span class="material-icons" style="font-size: 18px; vertical-align: middle; color: ${warningColor};">warning</span>
              <span style="color: ${warningColor}; margin-left: 6px; font-size: 0.9em;">${status.WarningMessage}</span>
            </div>
          `;
        }

        // Provider details with visual progress bars
        if (status.Providers && status.Providers.length > 0) {
          html += '<div style="background: rgba(255,255,255,0.02); border-radius: 8px; padding: 12px;">';
          html += '<div style="font-size: 0.85em; font-weight: bold; color: #aaa; margin-bottom: 10px; text-transform: uppercase; letter-spacing: 0.5px;">Provider Breakdown</div>';

          status.Providers.forEach(p => {
            const isOnline = p.IsOnline;
            const usagePercent = p.MaxConnections > 0 ? Math.round(100 * p.ProviderActiveConnections / p.MaxConnections) : 0;
            const barColor = !isOnline ? '#666' :
                            usagePercent >= 100 ? '#f44336' :
                            usagePercent >= 80 ? '#ff9800' : '#4caf50';
            const statusDot = isOnline ? '#4caf50' : '#f44336';

            // Format expiration
            let expInfo = '';
            if (p.ExpirationDate) {
              const expDate = new Date(p.ExpirationDate);
              const daysLeft = Math.ceil((expDate - new Date()) / (1000 * 60 * 60 * 24));
              if (daysLeft <= 0) {
                expInfo = '<span style="color: #f44336; font-size: 0.75em;">Expired</span>';
              } else if (daysLeft <= 7) {
                expInfo = `<span style="color: #ff9800; font-size: 0.75em;">${daysLeft}d left</span>`;
              } else {
                expInfo = `<span style="color: #666; font-size: 0.75em;">${expDate.toLocaleDateString()}</span>`;
              }
            }

            html += `
              <div style="margin-bottom: 10px;">
                <div style="display: flex; justify-content: space-between; align-items: center; margin-bottom: 4px;">
                  <div style="display: flex; align-items: center; gap: 8px;">
                    <span style="color: ${statusDot}; font-size: 8px;">●</span>
                    <span style="font-size: 0.9em; color: #ddd;">${p.ProviderName}</span>
                    ${p.IsTrial ? '<span style="background: rgba(255, 152, 0, 0.2); color: #ff9800; padding: 1px 6px; border-radius: 3px; font-size: 0.7em;">Trial</span>' : ''}
                  </div>
                  <div style="display: flex; align-items: center; gap: 12px;">
                    ${isOnline ? `
                      <span style="font-size: 0.85em; font-weight: bold; color: ${barColor};">${p.ProviderActiveConnections}/${p.MaxConnections}</span>
                      ${expInfo}
                    ` : `
                      <span style="color: #f44336; font-size: 0.85em;">${formatErrorMessage(p.ErrorMessage) || 'Offline'}</span>
                    `}
                  </div>
                </div>
                ${isOnline ? `
                  <div style="height: 4px; background: #333; border-radius: 2px; overflow: hidden;">
                    <div style="height: 100%; width: ${Math.min(usagePercent, 100)}%; background: ${barColor}; border-radius: 2px; transition: width 0.3s;"></div>
                  </div>
                ` : ''}
              </div>
            `;
          });

          html += '</div>';
        }

        // Enforcement status footer
        html += `
          <div style="margin-top: 12px; padding-top: 12px; border-top: 1px solid rgba(255,255,255,0.1); display: flex; justify-content: space-between; align-items: center; font-size: 0.8em; color: #666;">
            <div>
              Limit Enforcement: ${status.EnforcementEnabled ? '<span style="color: #4caf50;">On</span>' : '<span style="color: #666;">Off</span>'}
              ${status.EnforcementEnabled && status.AutoKillEnabled ? ' <span style="color: #888;">|</span> Auto-kill: <span style="color: #ff9800;">On</span>' : ''}
            </div>
            <div style="color: #555;">
              ${status.Providers ? status.Providers.filter(p => p.IsOnline).length : 0}/${status.Providers ? status.Providers.length : 0} providers online
            </div>
          </div>
        `;

        connectionStatusContent.innerHTML = html;
      } catch (err) {
        console.error('Failed to load connection status:', err);
        connectionStatusPanel.classList.add('hide');
      }
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
          testResultPanel.innerHTML = `<span style="color: #f44336;"><strong>Error:</strong> ${formatErrorMessage(result.message)}</span>`;
        }
      } catch (err) {
        testResultPanel.classList.remove('hide');
        testResultPanel.style.background = 'rgba(244, 67, 54, 0.1)';
        testResultPanel.style.borderLeft = '3px solid #f44336';
        testResultPanel.innerHTML = `<span style="color: #f44336;"><strong>Error:</strong> ${formatErrorMessage(err.message)}</span>`;
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
        testResultPanel.innerHTML = `<span style="color: #f44336;"><strong>Error:</strong> ${formatErrorMessage(err.message)}</span>`;
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

      // Get existing provider's calculated priority if updating
      const existingProvider = providerId ? providers.find(p => p.Id === providerId) : null;
      const existingPriority = existingProvider?.Priority ?? 50; // Preserve calculated priority, default 50 for new

      const providerData = {
        Id: providerId || generateProviderId(),
        Name: providerName,
        BaseUrl: providerBaseUrl.replace(/\/$/, ''), // Remove trailing slash
        Username: providerUsername,
        Password: providerPassword,
        Priority: existingPriority, // Priority is auto-calculated, not user-editable
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

      // Hide spinner when complete
      const spinner = view.querySelector('#ScrapeSpinner');
      if (spinner) spinner.style.display = 'none';

      const summary = view.querySelector('#ScrapeResultsSummary');
      const list = view.querySelector('#ScrapeResultsList');

      if (result.Success) {
        summary.style.background = 'linear-gradient(135deg, rgba(76, 175, 80, 0.1), rgba(46, 125, 50, 0.05))';
        summary.style.borderLeftColor = '#4caf50';
        summary.style.borderRadius = '8px';
        summary.innerHTML = `
          <div style="display: flex; align-items: center; gap: 12px; margin-bottom: 8px;">
            <span class="material-icons" style="color: #4caf50; font-size: 28px;">check_circle</span>
            <span style="font-size: 1.2em; font-weight: bold;">Discovery Complete!</span>
          </div>
          <div style="display: grid; grid-template-columns: repeat(auto-fit, minmax(120px, 1fr)); gap: 12px; margin-top: 12px;">
            <div style="text-align: center; padding: 8px; background: rgba(255,255,255,0.05); border-radius: 6px;">
              <div style="font-size: 1.5em; font-weight: bold; color: #fff;">${result.TotalCredentialsFound}</div>
              <div style="font-size: 0.8em; color: #888;">Found</div>
            </div>
            <div style="text-align: center; padding: 8px; background: rgba(76, 175, 80, 0.1); border-radius: 6px;">
              <div style="font-size: 1.5em; font-weight: bold; color: #4caf50;">${result.WorkingProviderCount}</div>
              <div style="font-size: 0.8em; color: #888;">Working</div>
            </div>
            <div style="text-align: center; padding: 8px; background: rgba(156, 39, 176, 0.1); border-radius: 6px;">
              <div style="font-size: 1.5em; font-weight: bold; color: #9c27b0;">${result.FullyWorkingCount}</div>
              <div style="font-size: 0.8em; color: #888;">+ ${result.CountryCode || 'Country'}</div>
            </div>
            ${result.ExcellentCount > 0 ? `
            <div style="text-align: center; padding: 8px; background: linear-gradient(135deg, rgba(255, 215, 0, 0.15), rgba(255, 193, 7, 0.1)); border: 1px solid rgba(255, 215, 0, 0.3); border-radius: 6px;">
              <div style="font-size: 1.5em; font-weight: bold; color: #ffd700;">★ ${result.ExcellentCount}</div>
              <div style="font-size: 0.8em; color: #888;">Excellent</div>
            </div>
            ` : ''}
          </div>
        `;
      } else {
        summary.style.background = 'rgba(244, 67, 54, 0.1)';
        summary.style.borderLeftColor = '#f44336';
        summary.innerHTML = `
          <div style="display: flex; align-items: center; gap: 12px;">
            <span class="material-icons" style="color: #f44336; font-size: 28px;">error</span>
            <div>
              <div style="font-weight: bold;">Discovery Failed</div>
              <div style="font-size: 0.9em; color: #aaa;">${result.ErrorMessage || 'Unknown error'}</div>
            </div>
          </div>
        `;
      }

      list.innerHTML = '';

      // Show excellent providers first, then fully working, then working
      const allProviders = result.ExcellentProviders?.length > 0
        ? result.ExcellentProviders
        : result.FullyWorkingProviders?.length > 0
          ? result.FullyWorkingProviders
          : result.WorkingProviders || [];

      // Get existing provider usernames for duplicate detection
      const existingUsernames = new Set(
        providers.map(p => {
          // Extract username from provider - it's stored in the provider config
          return p.Username?.toLowerCase();
        }).filter(Boolean)
      );

      // Filter out providers that are already configured
      const providersToShow = allProviders.filter(p =>
        !existingUsernames.has(p.Username?.toLowerCase())
      );
      const alreadyAddedCount = allProviders.length - providersToShow.length;

      if (providersToShow.length === 0) {
        const message = alreadyAddedCount > 0
          ? `All ${alreadyAddedCount} discovered provider${alreadyAddedCount !== 1 ? 's are' : ' is'} already configured`
          : 'No working providers found';
        const subMessage = alreadyAddedCount > 0
          ? 'Try a different time range to find new providers'
          : 'Try selecting a longer time range';

        list.innerHTML = `
          <div style="text-align: center; padding: 40px; color: #888;">
            <span class="material-icons" style="font-size: 48px; display: block; margin-bottom: 12px; opacity: 0.5;">${alreadyAddedCount > 0 ? 'check_circle' : 'search_off'}</span>
            <div>${message}</div>
            <div style="font-size: 0.85em; margin-top: 8px;">${subMessage}</div>
          </div>
        `;
        return;
      }

      // Add section header
      const sectionHeader = document.createElement('div');
      sectionHeader.style.cssText = 'display: flex; justify-content: space-between; align-items: center; margin-bottom: 12px; padding-bottom: 8px; border-bottom: 1px solid rgba(255,255,255,0.1);';
      const alreadyAddedText = alreadyAddedCount > 0 ? ` (${alreadyAddedCount} already added)` : '';
      sectionHeader.innerHTML = `
        <span style="font-weight: bold; color: #fff;">${providersToShow.length} New Provider${providersToShow.length !== 1 ? 's' : ''} Ready to Import${alreadyAddedText}</span>
        <span style="font-size: 0.85em; color: #888;">Sorted by Trust Score</span>
      `;
      list.appendChild(sectionHeader);

      providersToShow.forEach((provider) => {
        const card = document.createElement('div');

        // Determine card styling based on trust level
        let cardGradient, borderColor, trustBadgeBg;
        if (provider.TrustLevel === 'Excellent' || provider.TrustScore >= 85) {
          cardGradient = 'linear-gradient(135deg, rgba(255, 215, 0, 0.08), rgba(255, 193, 7, 0.03))';
          borderColor = '#ffd700';
          trustBadgeBg = 'linear-gradient(135deg, #ffd700, #ffb300)';
        } else if (provider.TrustLevel === 'Good' || provider.TrustScore >= 70) {
          cardGradient = 'linear-gradient(135deg, rgba(76, 175, 80, 0.08), rgba(46, 125, 50, 0.03))';
          borderColor = '#4caf50';
          trustBadgeBg = 'linear-gradient(135deg, #4caf50, #2e7d32)';
        } else if (provider.TrustScore >= 50) {
          cardGradient = 'rgba(255,255,255,0.03)';
          borderColor = '#ff9800';
          trustBadgeBg = 'linear-gradient(135deg, #ff9800, #f57c00)';
        } else {
          cardGradient = 'rgba(255,255,255,0.02)';
          borderColor = '#666';
          trustBadgeBg = '#666';
        }

        card.style.cssText = `
          padding: 16px;
          margin-bottom: 12px;
          background: ${cardGradient};
          border-radius: 12px;
          border-left: 4px solid ${borderColor};
          transition: transform 0.2s, box-shadow 0.2s;
        `;
        card.onmouseenter = () => { card.style.transform = 'translateX(4px)'; card.style.boxShadow = '0 4px 12px rgba(0,0,0,0.2)'; };
        card.onmouseleave = () => { card.style.transform = 'translateX(0)'; card.style.boxShadow = 'none'; };

        // Build feature pills
        const features = [];
        if (provider.StreamWorks) {
          features.push(`<span style="background: rgba(76, 175, 80, 0.15); color: #4caf50; padding: 3px 8px; border-radius: 12px; font-size: 0.75em;">Stream OK</span>`);
        }
        if (provider.HasEpg) {
          features.push(`<span style="background: rgba(33, 150, 243, 0.15); color: #2196f3; padding: 3px 8px; border-radius: 12px; font-size: 0.75em;">EPG ${provider.EpgProgramCount}</span>`);
        }
        if (provider.HasCountryChannels) {
          const countryLabel = provider.CountryCode || 'Country';
          features.push(`<span style="background: rgba(156, 39, 176, 0.15); color: #9c27b0; padding: 3px 8px; border-radius: 12px; font-size: 0.75em;">${countryLabel} ${provider.CountryChannelCount}</span>`);
        }
        if (provider.MaxConnections > 1) {
          features.push(`<span style="background: rgba(255,255,255,0.1); color: #aaa; padding: 3px 8px; border-radius: 12px; font-size: 0.75em;">${provider.MaxConnections} conn</span>`);
        }

        // Expiration info
        let expBadge = '';
        if (provider.ExpirationDate) {
          const expDate = new Date(provider.ExpirationDate);
          const daysLeft = Math.ceil((expDate - new Date()) / (1000 * 60 * 60 * 24));
          if (daysLeft <= 0) {
            expBadge = `<span style="background: rgba(244, 67, 54, 0.2); color: #f44336; padding: 3px 8px; border-radius: 12px; font-size: 0.75em;">Expired</span>`;
          } else if (daysLeft <= 7) {
            expBadge = `<span style="background: rgba(255, 152, 0, 0.2); color: #ff9800; padding: 3px 8px; border-radius: 12px; font-size: 0.75em;">${daysLeft}d left</span>`;
          } else {
            expBadge = `<span style="background: rgba(255,255,255,0.1); color: #888; padding: 3px 8px; border-radius: 12px; font-size: 0.75em;">${daysLeft}d</span>`;
          }
        }

        // Trust score circle
        const trustScore = provider.TrustScore ?? 0;
        const trustCircle = trustScore > 0 ? `
          <div style="position: relative; width: 56px; height: 56px; flex-shrink: 0;">
            <svg viewBox="0 0 36 36" style="transform: rotate(-90deg); width: 100%; height: 100%;">
              <circle cx="18" cy="18" r="15.5" fill="none" stroke="rgba(255,255,255,0.1)" stroke-width="3"/>
              <circle cx="18" cy="18" r="15.5" fill="none" stroke="${borderColor}" stroke-width="3"
                      stroke-dasharray="${trustScore}, 100" stroke-linecap="round"/>
            </svg>
            <div style="position: absolute; top: 50%; left: 50%; transform: translate(-50%, -50%); text-align: center;">
              <div style="font-size: 0.9em; font-weight: bold; color: ${borderColor};">${trustScore}</div>
            </div>
          </div>
        ` : '';

        card.innerHTML = `
          <div style="display: flex; gap: 16px; align-items: flex-start;">
            ${trustCircle}
            <div style="flex: 1; min-width: 0;">
              <div style="display: flex; justify-content: space-between; align-items: flex-start; margin-bottom: 6px;">
                <div>
                  <div style="font-weight: bold; font-size: 1.05em; color: #fff;">${provider.Server}:${provider.Port}</div>
                  <div style="font-size: 0.85em; color: #888;">User: ${provider.Username} | ${provider.TotalChannelCount} channels</div>
                </div>
                ${provider.TrustLevel ? `
                <span style="background: ${trustBadgeBg}; color: #000; padding: 4px 10px; border-radius: 12px; font-size: 0.75em; font-weight: bold;">
                  ${provider.TrustLevel === 'Excellent' ? '★ ' : ''}${provider.TrustLevel}
                </span>
                ` : ''}
              </div>
              <div style="display: flex; flex-wrap: wrap; gap: 6px; margin-top: 8px;">
                ${features.join('')}
                ${expBadge}
              </div>
              ${provider.TrustSummary ? `<div style="font-size: 0.8em; color: #666; margin-top: 8px;" title="${provider.TrustSummary}">${provider.TrustSummary}</div>` : ''}
            </div>
            <button is="emby-button" class="fab emby-button import-provider-btn" title="Import this provider"
                    style="background: linear-gradient(135deg, rgba(76, 175, 80, 0.3), rgba(46, 125, 50, 0.2)); border: 1px solid rgba(76, 175, 80, 0.3); flex-shrink: 0;"
                    data-server="${provider.Server}" data-port="${provider.Port}" data-username="${provider.Username}" data-password="${provider.Password}">
              <span class="material-icons" style="color: #4caf50;">add</span>
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
      const progressPercent = view.querySelector('#ScrapeProgressPercent');

      // Update phase display with icons
      const phaseConfig = {
        'Scraping': { label: 'Discovering Providers', icon: 'cloud_download' },
        'Discovering': { label: 'Discovering Providers', icon: 'cloud_download' },
        'Testing': { label: 'Testing Providers', icon: 'speed' },
        'Completed': { label: 'Discovery Complete!', icon: 'check_circle' },
        'Failed': { label: 'Discovery Failed', icon: 'error' }
      };
      const phaseInfo = phaseConfig[progress.Phase] || { label: progress.Phase, icon: 'hourglass_empty' };
      progressPhase.textContent = phaseInfo.label;

      // Update progress bar and percentage
      if (progress.TotalItems > 0) {
        const percent = Math.round((progress.CurrentItem / progress.TotalItems) * 100);
        progressBar.style.width = percent + '%';
        progressPercent.textContent = percent + '%';
      }

      // Update detailed message
      const inProgress = progress.InProgress ?? progress.inProgress ?? 0;
      if (progress.Message) {
        const inProgressSuffix = inProgress > 0 ? ` (${inProgress} in queue)` : '';
        progressText.textContent = progress.Message + inProgressSuffix;
      } else if (progress.TotalItems > 0) {
        const inProgressSuffix = inProgress > 0 ? ` | ${inProgress} in queue` : '';
        progressText.textContent = `${progress.CurrentItem} of ${progress.TotalItems} processed${inProgressSuffix}`;
      }

      // Update stats with animation effect
      const animateValue = (elementId, newValue) => {
        const el = view.querySelector('#' + elementId);
        const oldValue = parseInt(el.textContent) || 0;
        if (newValue !== oldValue) {
          el.textContent = newValue;
          el.style.transform = 'scale(1.2)';
          setTimeout(() => { el.style.transform = 'scale(1)'; }, 150);
        }
      };

      // Update all pipeline stage stats
      if (progress.CredentialsFound !== undefined) {
        animateValue('StatFound', progress.CredentialsFound);
      }
      if (progress.ConnectivityPassed !== undefined) {
        animateValue('StatReachable', progress.ConnectivityPassed);
      }
      if (progress.AuthenticationPassed !== undefined) {
        animateValue('StatAuthenticated', progress.AuthenticationPassed);
      }
      if (progress.WorkingProviders !== undefined) {
        animateValue('StatWorking', progress.WorkingProviders);
      }
      if (progress.FullyWorking !== undefined) {
        animateValue('StatFullyWorking', progress.FullyWorking);
      }
      if (progress.Excellent !== undefined) {
        animateValue('StatExcellent', progress.Excellent);
      }
    };

    // Country code to display name mapping
    const countryNames = {
      'PL': 'Polish',
      'UK': 'UK',
      'DE': 'German',
      'FR': 'French',
      '': 'All'
    };

    const startScraping = async () => {
      showScrapeProgress();

      const countryCode = view.querySelector('#ScrapeCountry').value;
      const timeRange = view.querySelector('#ScrapeTimeRange').value || 'LastMonth';
      const maxWorkers = parseInt(view.querySelector('#ScrapeWorkers').value) || 5;
      const testStream = view.querySelector('#ScrapeTestStream').checked;

      // Update the country label in the stats pipeline
      const countryLabel = view.querySelector('#StatCountryLabel');
      if (countryLabel) {
        countryLabel.textContent = countryNames[countryCode] || 'Country';
      }

      // Build request body
      const requestBody = {
        TimeRange: timeRange,
        MaxDiscoveryWorkers: maxWorkers,
        TestStream: testStream,
        CountryCode: countryCode || null
      };

      // Add custom dates if Custom range selected
      if (timeRange === 'Custom') {
        const startDate = view.querySelector('#ScrapeStartDate').value;
        const endDate = view.querySelector('#ScrapeEndDate').value;

        if (!startDate || !endDate) {
          showScrapeResults({
            Success: false,
            ErrorMessage: 'Please select both start and end dates for custom range'
          });
          return;
        }

        if (new Date(startDate) > new Date(endDate)) {
          showScrapeResults({
            Success: false,
            ErrorMessage: 'Start date must be before end date'
          });
          return;
        }

        requestBody.CustomStartDate = startDate;
        requestBody.CustomEndDate = endDate;
      }

      view.querySelector('#ScrapeProgressPhase').textContent = 'Starting...';
      view.querySelector('#ScrapeProgressBar').style.width = '0%';
      view.querySelector('#ScrapeProgressText').textContent = 'Initializing...';
      view.querySelector('#StatFound').textContent = '0';
      view.querySelector('#StatReachable').textContent = '0';
      view.querySelector('#StatAuthenticated').textContent = '0';
      view.querySelector('#StatWorking').textContent = '0';
      view.querySelector('#StatFullyWorking').textContent = '0';
      view.querySelector('#StatExcellent').textContent = '0';

      try {
        // First, start the discovery operation
        const startResult = await Xtream.apiRequest('Xtream/StartDiscovery', {
          method: 'POST',
          body: requestBody
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

        // Check if operation completed - use IsComplete to ensure all in-progress items are done
        const isComplete = progress?.IsComplete ?? progress?.isComplete ?? false;
        if (!isRunning && isComplete) {
          console.log('[Discovery] Operation completed');
          stopPolling();
          fetchResults();
        } else if (!isRunning && !isComplete) {
          // Still waiting for in-progress items to complete
          console.log('[Discovery] Waiting for in-progress items:', progress?.InProgress ?? progress?.inProgress ?? 0);
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

    // Time range change handler - show/hide custom date inputs
    const timeRangeSelect = view.querySelector('#ScrapeTimeRange');
    const customDateRange = view.querySelector('#CustomDateRange');
    const startDateInput = view.querySelector('#ScrapeStartDate');
    const endDateInput = view.querySelector('#ScrapeEndDate');

    // Set default dates (last 30 days)
    const today = new Date().toISOString().split('T')[0];
    const thirtyDaysAgo = new Date(Date.now() - 30 * 24 * 60 * 60 * 1000).toISOString().split('T')[0];
    startDateInput.value = thirtyDaysAgo;
    endDateInput.value = today;
    endDateInput.max = today; // Don't allow future dates

    timeRangeSelect.addEventListener('change', () => {
      if (timeRangeSelect.value === 'Custom') {
        customDateRange.classList.remove('hide');
        customDateRange.style.display = 'block';
      } else {
        customDateRange.classList.add('hide');
        customDateRange.style.display = 'none';
      }
    });

    // Connection status refresh
    view.querySelector('#RefreshConnectionStatusBtn').addEventListener('click', loadConnectionStatus);

    scrapeDialog.addEventListener('click', (e) => {
      // Only allow closing by clicking outside if discovery is not running
      if (e.target === scrapeDialog && !isDiscoveryActive) {
        hideScrapeDialog();
      }
    });

    // Initial load
    loadProviders();
  }));
}
