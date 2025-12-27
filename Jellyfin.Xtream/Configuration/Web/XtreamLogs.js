export default function (view) {
  view.addEventListener("viewshow", () => Promise.all([
    import(ApiClient.getUrl("web/ConfigurationPage", { name: "Xtream.js" })),
    import(ApiClient.getUrl("web/ConfigurationPage", { name: "XtreamStyles.js" }))
  ]).then(([XtreamModule, StylesModule]) => {
    const Xtream = XtreamModule.default;

    Xtream.setTabs('XtreamLogs');

    const pluginId = Xtream.pluginConfig.UniqueId;
    const refreshBtn = view.querySelector('#RefreshBtn');
    const logStatus = view.querySelector('#LogStatus');
    const logLevel = view.querySelector('#LogLevel');
    const includeDebug = view.querySelector('#IncludeDebug');
    const searchText = view.querySelector('#SearchText');
    const autoRefreshBtn = view.querySelector('#AutoRefreshBtn');
    const autoRefreshLabel = view.querySelector('#AutoRefreshLabel');
    const clearLogsBtn = view.querySelector('#ClearLogsBtn');
    const statsInfo = view.querySelector('#StatsInfo');
    const logLoading = view.querySelector('#LogLoading');
    const noLogsMessage = view.querySelector('#NoLogsMessage');
    const logEntries = view.querySelector('#LogEntries');
    const loadMoreBtn = view.querySelector('#LoadMoreBtn');
    const enableDebugLogging = view.querySelector('#EnableDebugLogging');

    let autoRefreshInterval = null;
    let isAutoRefreshEnabled = true;
    let latestLogId = 0;
    let currentSkip = 0;
    const pageSize = 100;

    const logLevelNames = ['Trace', 'Debug', 'Info', 'Warning', 'Error', 'Critical'];
    const logLevelMap = {
      'trace': 0, 'debug': 1, 'information': 2, 'info': 2, 'warning': 3, 'error': 4, 'critical': 5
    };

    function parseLogLevel(level) {
      if (typeof level === 'number') return level;
      if (typeof level === 'string') {
        const normalized = level.toLowerCase();
        return logLevelMap[normalized] ?? 2;
      }
      return 2; // Default to Info
    }

    function showStatus(message, isError) {
      logStatus.textContent = message;
      logStatus.className = 'log-status ' + (isError ? 'error' : 'success');
      logStatus.style.display = 'block';

      setTimeout(() => {
        logStatus.style.display = 'none';
      }, 5000);
    }

    function formatTimestamp(isoString) {
      const date = new Date(isoString);
      return date.toLocaleTimeString('en-US', {
        hour12: false,
        hour: '2-digit',
        minute: '2-digit',
        second: '2-digit'
      }) + '.' + String(date.getMilliseconds()).padStart(3, '0');
    }

    function getLevelClass(level) {
      const names = ['trace', 'debug', 'info', 'warning', 'error', 'critical'];
      return names[level] || 'info';
    }

    function getLevelName(level) {
      return logLevelNames[level] || 'Info';
    }

    function escapeHtml(text) {
      if (!text) return '';
      const div = document.createElement('div');
      div.textContent = text;
      return div.innerHTML;
    }

    function createLogEntry(entry) {
      const level = parseLogLevel(entry.level ?? entry.Level);
      const isDebug = entry.isDebug ?? entry.IsDebug ?? false;
      // Debug entries show as 'debug' level regardless of actual log level
      const displayLevel = isDebug ? 1 : level;
      const levelClass = getLevelClass(displayLevel);

      const div = document.createElement('div');
      div.className = `log-entry level-${levelClass}${isDebug ? ' is-debug' : ''}`;
      div.dataset.logId = entry.id ?? entry.Id;

      const timestamp = entry.timestamp ?? entry.Timestamp;
      const category = entry.category ?? entry.Category ?? 'Unknown';
      const message = entry.message ?? entry.Message ?? '';
      const streamId = entry.streamId ?? entry.StreamId;
      const channelName = entry.channelName ?? entry.ChannelName;
      const exceptionMessage = entry.exceptionMessage ?? entry.ExceptionMessage;
      const exceptionStack = entry.exceptionStackTrace ?? entry.ExceptionStackTrace;

      let metaHtml = '';
      if (streamId || channelName) {
        metaHtml = '<div class="log-meta">';
        if (channelName) {
          metaHtml += `<span class="log-channel-name" title="Channel">${escapeHtml(channelName)}</span>`;
        }
        if (streamId) {
          metaHtml += `<span class="log-stream-id" title="Stream ID">${escapeHtml(streamId.substring(0, 8))}...</span>`;
        }
        metaHtml += '</div>';
      }

      let exceptionHtml = '';
      if (exceptionMessage) {
        exceptionHtml = `<div class="log-exception">${escapeHtml(exceptionMessage)}`;
        if (exceptionStack) {
          exceptionHtml += `\n\n${escapeHtml(exceptionStack)}`;
        }
        exceptionHtml += '</div>';
      }

      div.innerHTML = `
        <span class="log-timestamp">${formatTimestamp(timestamp)}</span>
        <span class="log-level ${levelClass}">${getLevelName(displayLevel)}</span>
        <span class="log-category" title="${escapeHtml(category)}">${escapeHtml(category)}</span>
        <span class="log-message">${escapeHtml(message)}</span>
        ${metaHtml}
        ${exceptionHtml}
      `;

      return div;
    }

    function renderLogs(entries, append = false) {
      if (!append) {
        logEntries.innerHTML = '';
        currentSkip = 0;
      }

      if (!entries || entries.length === 0) {
        if (!append) {
          logLoading.style.display = 'none';
          noLogsMessage.style.display = 'block';
          loadMoreBtn.style.display = 'none';
        }
        return;
      }

      logLoading.style.display = 'none';
      noLogsMessage.style.display = 'none';

      entries.forEach(entry => {
        const entryEl = createLogEntry(entry);
        logEntries.appendChild(entryEl);
      });

      currentSkip += entries.length;
      loadMoreBtn.style.display = entries.length >= pageSize ? 'inline-flex' : 'none';

      if (entries.length > 0) {
        const firstEntry = entries[0];
        const entryId = firstEntry.id ?? firstEntry.Id ?? 0;
        if (entryId > latestLogId) {
          latestLogId = entryId;
        }
      }
    }

    function appendNewLogs(entries) {
      if (!entries || entries.length === 0) return;

      entries.forEach(entry => {
        const entryEl = createLogEntry(entry);
        if (logEntries.firstChild) {
          logEntries.insertBefore(entryEl, logEntries.firstChild);
        } else {
          logEntries.appendChild(entryEl);
        }

        const entryId = entry.id ?? entry.Id ?? 0;
        if (entryId > latestLogId) {
          latestLogId = entryId;
        }
      });

      noLogsMessage.style.display = 'none';
    }

    function getFilterParams() {
      return {
        minLevel: parseInt(logLevel.value, 10),
        includeDebug: includeDebug.checked,
        searchText: searchText.value.trim() || undefined
      };
    }

    function buildQueryString(params, skip = 0, take = pageSize) {
      const qs = new URLSearchParams();
      qs.set('minLevel', params.minLevel);
      qs.set('includeDebug', params.includeDebug);
      if (params.searchText) qs.set('searchText', params.searchText);
      qs.set('skip', skip);
      qs.set('take', take);
      return qs.toString();
    }

    function loadLogs(append = false) {
      const params = getFilterParams();
      const skip = append ? currentSkip : 0;
      const queryString = buildQueryString(params, skip);

      if (!append) {
        logLoading.style.display = 'block';
        noLogsMessage.style.display = 'none';
        logEntries.innerHTML = '';
      }

      Xtream.fetchJson(`Xtream/Logs?${queryString}`).then((entries) => {
        renderLogs(entries, append);
      }).catch((err) => {
        logLoading.style.display = 'none';
        showStatus('Failed to load logs: ' + err.message, true);
      });
    }

    function pollForNewLogs() {
      if (!isAutoRefreshEnabled || latestLogId === 0) return;

      const params = getFilterParams();
      const url = `Xtream/Logs/After/${latestLogId}?minLevel=${params.minLevel}&includeDebug=${params.includeDebug}`;

      Xtream.fetchJson(url).then((entries) => {
        if (entries && entries.length > 0) {
          appendNewLogs(entries);
          updateStats();
        }
      }).catch(() => {
        // Silently ignore polling errors
      });
    }

    function updateStats() {
      Xtream.fetchJson('Xtream/Logs/Stats').then((stats) => {
        const total = stats.totalEntries ?? stats.TotalEntries ?? 0;
        const maxEntries = stats.maxEntries ?? stats.MaxEntries ?? 5000;
        const debugCount = stats.debugCount ?? stats.DebugCount ?? 0;
        const infoCount = stats.infoCount ?? stats.InfoCount ?? 0;
        const warningCount = stats.warningCount ?? stats.WarningCount ?? 0;
        const errorCount = stats.errorCount ?? stats.ErrorCount ?? 0;

        statsInfo.innerHTML = `
          <strong>${total}/${maxEntries}</strong> entries |
          <span style="color: #2196F3;">Debug: ${debugCount}</span> |
          <span style="color: #4caf50;">Info: ${infoCount}</span> |
          <span style="color: #ff9800;">Warning: ${warningCount}</span> |
          <span style="color: #f44336;">Error: ${errorCount}</span>
        `;

        const statsLatestId = stats.latestId ?? stats.LatestId ?? 0;
        if (statsLatestId > latestLogId) {
          latestLogId = statsLatestId;
        }
      }).catch(() => {
        statsInfo.textContent = 'Failed to load stats';
      });
    }

    function toggleAutoRefresh() {
      isAutoRefreshEnabled = !isAutoRefreshEnabled;

      if (isAutoRefreshEnabled) {
        autoRefreshBtn.classList.add('auto-refresh-active');
        autoRefreshBtn.querySelector('.material-icons').textContent = 'pause';
        autoRefreshLabel.textContent = 'Auto-refresh';
        startAutoRefresh();
      } else {
        autoRefreshBtn.classList.remove('auto-refresh-active');
        autoRefreshBtn.querySelector('.material-icons').textContent = 'play_arrow';
        autoRefreshLabel.textContent = 'Paused';
        stopAutoRefresh();
      }
    }

    function startAutoRefresh() {
      if (autoRefreshInterval) return;
      autoRefreshInterval = setInterval(() => {
        pollForNewLogs();
        updateStats();
      }, 3000);
    }

    function stopAutoRefresh() {
      if (autoRefreshInterval) {
        clearInterval(autoRefreshInterval);
        autoRefreshInterval = null;
      }
    }

    function clearLogs() {
      if (!confirm('Are you sure you want to clear all plugin logs?\n\nThis cannot be undone.')) {
        return;
      }

      Xtream.apiRequest('Xtream/Logs', { method: 'DELETE' }).then((result) => {
        const success = result.success ?? result.Success;
        const message = result.message ?? result.Message;

        if (success) {
          showStatus(message, false);
          logEntries.innerHTML = '';
          latestLogId = 0;
          currentSkip = 0;
          noLogsMessage.style.display = 'block';
          loadMoreBtn.style.display = 'none';
          updateStats();
        } else {
          showStatus(message || 'Failed to clear logs', true);
        }
      }).catch((err) => {
        showStatus('Failed to clear logs: ' + err.message, true);
      });
    }

    // Event handlers
    refreshBtn.addEventListener('click', () => {
      loadLogs();
      updateStats();
    });

    autoRefreshBtn.addEventListener('click', toggleAutoRefresh);
    clearLogsBtn.addEventListener('click', clearLogs);

    logLevel.addEventListener('change', () => loadLogs());
    includeDebug.addEventListener('change', () => loadLogs());

    let searchDebounce;
    searchText.addEventListener('input', () => {
      clearTimeout(searchDebounce);
      searchDebounce = setTimeout(() => loadLogs(), 300);
    });

    loadMoreBtn.addEventListener('click', () => loadLogs(true));

    // Load debug logging setting
    ApiClient.getPluginConfiguration(pluginId).then(config => {
      enableDebugLogging.checked = config.EnableDebugLogging ?? false;
    });

    // Save debug logging setting on change
    enableDebugLogging.addEventListener('change', () => {
      ApiClient.getPluginConfiguration(pluginId).then(config => {
        config.EnableDebugLogging = enableDebugLogging.checked;
        ApiClient.updatePluginConfiguration(pluginId, config).then(() => {
          showStatus(enableDebugLogging.checked ? 'Debug logging enabled' : 'Debug logging disabled', false);
        }).catch(err => {
          showStatus('Failed to save setting: ' + err.message, true);
          // Revert checkbox on error
          enableDebugLogging.checked = !enableDebugLogging.checked;
        });
      });
    });

    // Initial load
    loadLogs();
    updateStats();

    // Start auto-refresh
    if (isAutoRefreshEnabled) {
      autoRefreshBtn.classList.add('auto-refresh-active');
      startAutoRefresh();
    }

    // Cleanup on page leave
    view.addEventListener('viewhide', () => {
      stopAutoRefresh();
    });
  }));
}
