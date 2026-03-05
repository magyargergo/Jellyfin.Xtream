export default function (view) {
  view.addEventListener("viewshow", () => Promise.all([
    import(ApiClient.getUrl("web/ConfigurationPage", { name: "Xtream.js" })),
    import(ApiClient.getUrl("web/ConfigurationPage", { name: "XtreamStyles.js" }))
  ]).then(([XtreamModule, StylesModule]) => {
    const Xtream = XtreamModule.default;
    const XtreamStyles = StylesModule.default;

    const pluginId = Xtream.pluginConfig.UniqueId;
    Xtream.setTabs('XtreamSettings');

    // ========================================
    // Toggle elements
    // ========================================
    const togglePairs = [
      ['#EnableProxy', '#ProxySettings'],
      ['#EnableUserAgentRotation', '#UserAgentRotationSettings'],
      ['#EnableRateLimiting', '#RateLimitingSettings'],
      ['#EnforceConnectionLimit', '#ConnectionLimitSettings'],
      ['#EnableExternalEpg', '#ExternalEpgSettings'],
    ];

    const toggleFns = togglePairs.map(([checkboxSel, contentSel]) => {
      const checkbox = view.querySelector(checkboxSel);
      const content = view.querySelector(contentSel);
      const fn = () => XtreamStyles.setVisible(content, checkbox.checked);
      checkbox.addEventListener('change', fn);
      return fn;
    });

    // ========================================
    // Collapsible section headers
    // ========================================
    view.querySelectorAll('.settings-section-header').forEach(header => {
      const sectionName = header.dataset.section;
      const content = view.querySelector(`.settings-section-content[data-section="${sectionName}"]`);
      const icon = header.querySelector('.material-icons:last-child');

      if (content && icon) {
        // Determine initial state from display style
        let isExpanded = content.style.display !== 'none';
        icon.textContent = isExpanded ? 'expand_less' : 'expand_more';

        header.addEventListener('click', () => {
          isExpanded = !isExpanded;
          content.style.display = isExpanded ? 'block' : 'none';
          icon.textContent = isExpanded ? 'expand_less' : 'expand_more';
        });
      }
    });

    // ========================================
    // Field mappings (migrated from former XtreamAdvanced.js)
    // ========================================
    const fieldMappings = [
      // Networking
      ['#EnableProxy', 'EnableProxy', false, true],
      ['#ProxyType', 'ProxyType', 0, false, (v) => parseInt(v) || 0],
      ['#ProxyAddress', 'ProxyAddress', ''],
      ['#ProxyPort', 'ProxyPort', 8080, false, (v) => parseInt(v) || 8080],
      ['#ProxyUsername', 'ProxyUsername', ''],
      ['#ProxyPassword', 'ProxyPassword', ''],
      ['#ProxyBypassLocal', 'ProxyBypassLocal', true, true],
      ['#EnableUserAgentRotation', 'EnableUserAgentRotation', false, true],
      ['#UseRandomUserAgent', 'UseRandomUserAgent', true, true],
      ['#CustomUserAgent', 'CustomUserAgent', ''],
      ['#EnableRateLimiting', 'EnableRateLimiting', false, true],
      ['#RequestsPerSecond', 'RequestsPerSecond', 5, false, (v) => parseInt(v) || 5],
      ['#BurstSize', 'BurstSize', 20, false, (v) => parseInt(v) || 20],

      // Connection limits
      ['#EnforceConnectionLimit', 'EnforceConnectionLimit', false, true],
      ['#MaxConcurrentStreams', 'MaxConcurrentStreams', 0, false, (v) => parseInt(v) || 0],
      ['#AutoKillOldestStream', 'AutoKillOldestStream', true, true],
      ['#FilterChannelsByCapacity', 'FilterChannelsByCapacity', false, true],

      // External EPG
      ['#EnableExternalEpg', 'EnableExternalEpg', false, true],
      ['#ExternalEpgUrl', 'ExternalEpgUrl', ''],
      ['#ExternalEpgLogoBaseUrl', 'ExternalEpgLogoBaseUrl', ''],
      ['#UseExternalLogoFallback', 'UseExternalLogoFallback', true, true],

      // Stream processing
      ['#ForceRemux', 'ForceRemux', true, true],

      // Streaming timeouts
      ['#StreamConnectTimeoutSeconds', 'StreamConnectTimeoutSeconds', 5, false, (v) => parseInt(v) || 5],
      ['#StreamFirstByteTimeoutSeconds', 'StreamFirstByteTimeoutSeconds', 5, false, (v) => parseInt(v) || 5],
      ['#StreamDataStallTimeoutSeconds', 'StreamDataStallTimeoutSeconds', 10, false, (v) => parseInt(v) || 10],
      ['#FailoverBudgetSeconds', 'FailoverBudgetSeconds', 15, false, (v) => parseInt(v) || 15],
      ['#ProviderBlacklistSeconds', 'ProviderBlacklistSeconds', 30, false, (v) => parseInt(v) || 30],

      // Failover
      ['#EnableProviderFailover', 'EnableProviderFailover', true, true],
      ['#MaxFailoverAttempts', 'MaxFailoverAttempts', 3, false, (v) => parseInt(v) || 3],
      ['#MergeDuplicateChannels', 'MergeDuplicateChannels', true, true],
      ['#SkipUnavailableProviders', 'SkipUnavailableProviders', true, true],
      ['#ProviderCheckIntervalSeconds', 'ProviderCheckIntervalSeconds', 60, false, (v) => parseInt(v) || 60],

      // Health & Load Balancing
      ['#EnableP2CLoadBalancing', 'EnableP2CLoadBalancing', true, true],
      ['#EnableOutlierDetection', 'EnableOutlierDetection', true, true],
      ['#OutlierStddevFactor', 'OutlierStddevFactor', 1.9, false, (v) => parseFloat(v) || 1.9],
      ['#ProbationSuccessThreshold', 'ProbationSuccessThreshold', 3, false, (v) => parseInt(v) || 3],

      // Hedging
      ['#EnableHedging', 'EnableHedging', false, true],
      ['#HedgingDelayMs', 'HedgingDelayMs', 200, false, (v) => parseInt(v) || 200],
      ['#MaxHedgedAttempts', 'MaxHedgedAttempts', 2, false, (v) => parseInt(v) || 2],

      // Visibility
      ['#IsCatchupVisible', 'IsCatchupVisible', false, true],
      ['#IsSeriesVisible', 'IsSeriesVisible', false, true],
      ['#IsVodVisible', 'IsVodVisible', false, true],
      ['#IsTmdbVodOverride', 'IsTmdbVodOverride', true, true],

      // Logging
      ['#EnableDebugLogging', 'EnableDebugLogging', false, true],
      ['#LogViewerMaxEntries', 'LogViewerMaxEntries', 5000, false, (v) => parseInt(v) || 5000],
      ['#EnablePeriodicHealthReports', 'EnablePeriodicHealthReports', false, true],
      ['#HealthReportIntervalMinutes', 'HealthReportIntervalMinutes', 60, false, (v) => parseInt(v) || 60],

      // Discord
      ['#EnableDiscordNotifications', 'EnableDiscordNotifications', false, true],
      ['#DiscordWebhookUrl', 'DiscordWebhookUrl', ''],
      ['#NotifyOnBufferOverflow', 'NotifyOnBufferOverflow', true, true],
      ['#NotifyOnStreamStart', 'NotifyOnStreamStart', false, true],
      ['#NotifyOnStreamError', 'NotifyOnStreamError', true, true],
      ['#NotifyOnStreamKilled', 'NotifyOnStreamKilled', true, true],
      ['#NotifyOnStreamQualityViolation', 'NotifyOnStreamQualityViolation', true, true],
      ['#NotifyOnEpgRefresh', 'NotifyOnEpgRefresh', true, true],
      ['#NotifyOnConnectionLimitChange', 'NotifyOnConnectionLimitChange', true, true],
      ['#NotifyOnProviderBlacklist', 'NotifyOnProviderBlacklist', true, true],
    ];

    // ========================================
    // Load configuration
    // ========================================
    Dashboard.showLoadingMsg();
    ApiClient.getPluginConfiguration(pluginId).then(function (config) {
      Xtream.loadConfigFields(view, config, fieldMappings);
      toggleFns.forEach(fn => fn());
      Dashboard.hideLoadingMsg();
    }).catch(function (err) {
      Dashboard.hideLoadingMsg();
      console.error('Failed to load settings:', err);
    });

    // ========================================
    // Form submission
    // ========================================
    view.querySelector('#XtreamSettingsForm').addEventListener('submit',
      Xtream.createFormSubmitHandler(
        pluginId,
        () => {
          const enableProxy = view.querySelector('#EnableProxy').checked;
          if (enableProxy) {
            const proxyAddress = view.querySelector('#ProxyAddress').value.trim();
            const proxyPort = parseInt(view.querySelector('#ProxyPort').value);
            if (!proxyAddress) {
              Dashboard.alert('Please enter a proxy address or disable the proxy.');
              return false;
            }
            if (!proxyPort || proxyPort < 1 || proxyPort > 65535) {
              Dashboard.alert('Please enter a valid proxy port (1-65535).');
              return false;
            }
          }
        },
        (config) => {
          Xtream.saveConfigFields(view, config, fieldMappings);

          let proxyAddress = config.ProxyAddress;
          if (proxyAddress) {
            proxyAddress = proxyAddress
              .replace(/^https?:\/\//, '')
              .replace(/^socks[45]?a?:\/\//, '')
              .replace(/\/$/, '');
            config.ProxyAddress = proxyAddress;
          }

          if (config.CustomUserAgent) {
            config.CustomUserAgent = config.CustomUserAgent.trim();
          }

          Xtream.apiPost('Xtream/RefreshDiscordConfiguration')
            .catch(err => console.error('Failed to refresh Discord configuration:', err));
        }
      )
    );

    // ========================================
    // Discord Test Webhook
    // ========================================
    view.querySelector('#testWebhookButton').addEventListener('click', function () {
      const webhookUrl = view.querySelector('#DiscordWebhookUrl').value;
      if (!webhookUrl) {
        Dashboard.alert('Please enter a webhook URL first');
        return;
      }

      Dashboard.showLoadingMsg();
      Xtream.apiPost('Xtream/TestDiscordWebhook', { webhookUrl })
        .then(data => {
          Dashboard.hideLoadingMsg();
          Dashboard.alert(data.success ?
            'Test notification sent successfully! Check your Discord channel.' :
            'Test failed: ' + (data.message || 'Unknown error'));
        })
        .catch(error => {
          Dashboard.hideLoadingMsg();
          Dashboard.alert('Error testing webhook: ' + error.message);
        });
    });

    // ========================================
    // Config Preview Buttons
    // ========================================
    view.querySelectorAll('.preview-btn').forEach(btn => {
      btn.addEventListener('click', function () {
        const section = this.dataset.section;
        if (!section) return;

        const previewConfig = {};
        Xtream.saveConfigFields(view, previewConfig, fieldMappings);

        Dashboard.showLoadingMsg();
        Xtream.apiRequest(`Xtream/Configuration/${section}/Preview`, {
          method: 'POST',
          body: previewConfig
        }).then(result => {
          Dashboard.hideLoadingMsg();

          let msg = '';
          if (result.changes && result.changes.length > 0) {
            msg += 'Changes:\n';
            result.changes.forEach(c => {
              if (c.changed) msg += `  ${c.field}: ${c.oldValue} -> ${c.newValue}\n`;
            });
          } else {
            msg += 'No changes detected.\n';
          }

          if (result.warnings && result.warnings.length > 0) {
            msg += '\nWarnings:\n';
            result.warnings.forEach(w => { msg += `  ! ${w}\n`; });
          }
          if (result.errors && result.errors.length > 0) {
            msg += '\nErrors:\n';
            result.errors.forEach(e => { msg += `  x ${e}\n`; });
          }
          if (result.impactedStreams > 0) {
            msg += `\n${result.impactedStreams} active stream(s) will be affected.`;
          }
          if (result.requiresRestart) {
            msg += '\nPlugin restart required for changes to take effect.';
          }

          Dashboard.alert(msg || 'Preview generated.');
        }).catch(err => {
          Dashboard.hideLoadingMsg();
          Dashboard.alert('Preview failed: ' + err.message);
        });
      });
    });

    // ========================================
    // Buffer Diagnostics Button
    // ========================================
    view.querySelector('#sendBufferDiagnosticsButton').addEventListener('click', function () {
      Dashboard.showLoadingMsg();
      Xtream.apiPost('Xtream/SendBufferDiagnostics')
        .then(data => {
          Dashboard.hideLoadingMsg();
          if (data.success) {
            Dashboard.alert(data.count > 0
              ? `Buffer diagnostics sent for ${data.count} active stream(s). Check your Discord channel.`
              : 'No active streams found. Start watching a channel first.');
          } else {
            Dashboard.alert('Failed: ' + (data.message || 'Unknown error'));
          }
        })
        .catch(error => {
          Dashboard.hideLoadingMsg();
          Dashboard.alert('Error sending buffer diagnostics: ' + error.message);
        });
    });

    // ========================================
    // Embedded EPG Test Tool
    // ========================================
    const epgChannelSelect = view.querySelector('#EpgChannelSelect');
    const loadEpgBtn = view.querySelector('#LoadEpgBtn');
    const refreshAllEpgBtn = view.querySelector('#RefreshAllEpgBtn');
    const epgRefreshStatus = view.querySelector('#EpgRefreshStatus');
    const epgResults = view.querySelector('#EpgResults');
    const epgTimeline = view.querySelector('#EpgTimeline');
    const epgResultsTitle = view.querySelector('#EpgResultsTitle');
    const epgProgramCount = view.querySelector('#EpgProgramCount');
    const epgProviderInfo = view.querySelector('#EpgProviderInfo');
    const epgProviderName = view.querySelector('#EpgProviderName');
    const epgProviderStatus = view.querySelector('#EpgProviderStatus');
    const providerStatusCard = view.querySelector('#ProviderStatusCard');

    function escapeHtml(text) {
      const div = document.createElement('div');
      div.textContent = text;
      return div.innerHTML;
    }

    // Load provider status
    Xtream.fetchJson('Xtream/EpgStatus').then((status) => {
      epgProviderName.textContent = status.providerName || 'Unknown Provider';
      epgProviderStatus.className = 'status-badge ' + (status.isAvailable ? 'available' : 'unavailable');
      epgProviderStatus.innerHTML = `
        <span class="material-icons" style="font-size: 14px;">${status.isAvailable ? 'check_circle' : 'error'}</span>
        <span>${status.isAvailable ? 'Available' : 'Unavailable'}</span>
      `;
      providerStatusCard.style.borderLeftColor = status.isAvailable ? '#4caf50' : '#f44336';
    }).catch(() => {
      epgProviderName.textContent = 'Unknown Provider';
      epgProviderStatus.className = 'status-badge unavailable';
      epgProviderStatus.innerHTML = '<span class="material-icons" style="font-size: 14px;">error</span><span>Error</span>';
    });

    // Load channels for EPG test
    Xtream.fetchJson('Xtream/LiveTv').then((channels) => {
      channels.forEach((channel) => {
        const option = document.createElement('option');
        option.value = channel.Id;
        option.textContent = channel.Number ? `${channel.Number}. ${channel.Name}` : channel.Name;
        epgChannelSelect.appendChild(option);
      });
    }).catch(() => {});

    epgChannelSelect.addEventListener('change', () => {
      loadEpgBtn.disabled = !epgChannelSelect.value;
      XtreamStyles.setVisible(epgResults, false);
    });

    loadEpgBtn.addEventListener('click', () => {
      const streamId = epgChannelSelect.value;
      if (!streamId) return;

      Dashboard.showLoadingMsg();
      loadEpgBtn.disabled = true;

      Xtream.fetchJson(`Xtream/EpgTest/${streamId}`).then((result) => {
        Dashboard.hideLoadingMsg();
        loadEpgBtn.disabled = false;
        XtreamStyles.setVisible(epgResults, true);

        const success = result.Success ?? result.success;
        const errorMessage = result.ErrorMessage ?? result.errorMessage;
        const channelName = result.ChannelName ?? result.channelName;
        const provider = result.Provider ?? result.provider;
        const programs = result.Programs ?? result.programs ?? [];
        const resultStreamId = result.StreamId ?? result.streamId;

        if (!success) {
          epgTimeline.innerHTML = `<div class="empty-state" style="text-align:center;padding:50px;color:#888;"><span class="material-icons" style="font-size:48px;opacity:0.5;">error</span><p>${errorMessage || 'Unknown error'}</p></div>`;
          epgProgramCount.textContent = '0 programs';
          epgProviderInfo.textContent = '-';
          return;
        }

        epgResultsTitle.textContent = channelName || 'Program Schedule';
        epgProgramCount.textContent = `${programs.length} programs`;
        epgProviderInfo.textContent = `${provider} (ID: ${resultStreamId})`;

        if (programs.length === 0) {
          epgTimeline.innerHTML = '<div class="empty-state" style="text-align:center;padding:50px;color:#888;"><span class="material-icons" style="font-size:48px;opacity:0.5;">event_busy</span><p>No EPG data available</p></div>';
          return;
        }

        const now = new Date();
        epgTimeline.innerHTML = programs.map((program) => {
          const start = new Date(program.StartUtc ?? program.startUtc);
          const end = new Date(program.EndUtc ?? program.endUtc);

          let cardStyle = 'background:rgba(255,255,255,0.04);border-radius:8px;padding:16px;margin-bottom:12px;border-left:4px solid #00a4dc;';
          if (end < now) {
            cardStyle = 'background:rgba(255,255,255,0.02);border-radius:8px;padding:16px;margin-bottom:12px;border-left:4px solid #555;opacity:0.6;';
          } else if (start <= now && end >= now) {
            cardStyle = 'background:rgba(76,175,80,0.15);border-radius:8px;padding:16px;margin-bottom:12px;border-left:4px solid #4caf50;';
          }

          const formatTime = (date) => {
            if (!date || date.getFullYear() < 2000) return 'N/A';
            return date.toLocaleString(undefined, { weekday: 'short', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
          };

          const duration = (end - start) / 60000;
          const durationStr = duration > 0 ? ` (${Math.round(duration)} min)` : '';
          const title = program.Title ?? program.title;
          const description = program.Description ?? program.description;

          return `
            <div style="${cardStyle}">
              <div style="font-weight:600;font-size:15px;margin-bottom:6px;">${escapeHtml(title || 'No Title')}</div>
              <div style="color:#888;font-size:13px;">${formatTime(start)} - ${formatTime(end)}${durationStr}</div>
              ${description ? `<div style="color:#aaa;font-size:13px;margin-top:8px;line-height:1.4;">${escapeHtml(description)}</div>` : ''}
            </div>
          `;
        }).join('');
      }).catch((err) => {
        Dashboard.hideLoadingMsg();
        loadEpgBtn.disabled = false;
        XtreamStyles.setVisible(epgResults, true);
        epgTimeline.innerHTML = `<div class="empty-state" style="text-align:center;padding:50px;color:#888;"><span class="material-icons" style="font-size:48px;opacity:0.5;">error</span><p>Failed to load EPG: ${err.message}</p></div>`;
        epgProgramCount.textContent = '0 programs';
        epgProviderInfo.textContent = '-';
      });
    });

    refreshAllEpgBtn.addEventListener('click', () => {
      refreshAllEpgBtn.disabled = true;
      epgRefreshStatus.style.display = 'block';
      epgRefreshStatus.style.cssText = 'display:block;padding:12px 16px;border-radius:8px;background:rgba(255,152,0,0.15);color:#ffb74d;';
      epgRefreshStatus.textContent = 'Refreshing EPG data for all channels... This may take a while.';

      Xtream.fetchJson('Xtream/RefreshEpg', { method: 'POST' }).then((result) => {
        refreshAllEpgBtn.disabled = false;
        const success = result.success ?? result.Success;
        const message = result.message ?? result.Message;

        if (success) {
          epgRefreshStatus.style.cssText = 'display:block;padding:12px 16px;border-radius:8px;background:rgba(76,175,80,0.15);color:#81c784;';
          epgRefreshStatus.textContent = message;
        } else {
          epgRefreshStatus.style.cssText = 'display:block;padding:12px 16px;border-radius:8px;background:rgba(244,67,54,0.15);color:#e57373;';
          epgRefreshStatus.textContent = message || 'Refresh failed';
        }

        setTimeout(() => { epgRefreshStatus.style.display = 'none'; }, 10000);
      }).catch((err) => {
        refreshAllEpgBtn.disabled = false;
        epgRefreshStatus.style.cssText = 'display:block;padding:12px 16px;border-radius:8px;background:rgba(244,67,54,0.15);color:#e57373;';
        epgRefreshStatus.textContent = `Failed to refresh EPG: ${err.message}`;
      });
    });
  }));
}
