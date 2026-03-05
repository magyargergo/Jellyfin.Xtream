export default function (view) {
  view.addEventListener("viewshow", () => Promise.all([
    import(ApiClient.getUrl("web/ConfigurationPage", { name: "Xtream.js" })),
    import(ApiClient.getUrl("web/ConfigurationPage", { name: "XtreamStyles.js" }))
  ]).then(([XtreamModule, StylesModule]) => {
    const Xtream = XtreamModule.default;

    Xtream.setTabs('XtreamLogs');

    const pluginId = Xtream.pluginConfig.UniqueId;

    // DOM Elements
    const refreshBtn = view.querySelector('#RefreshBtn');
    const helpBtn = view.querySelector('#HelpBtn');
    const helpPanel = view.querySelector('#HelpPanel');
    const closeHelpBtn = view.querySelector('#CloseHelpBtn');
    const logStatus = view.querySelector('#LogStatus');
    const logLevel = view.querySelector('#LogLevel');
    const categoryFilter = view.querySelector('#CategoryFilter');
    const includeDebug = view.querySelector('#IncludeDebug');
    const searchText = view.querySelector('#SearchText');
    const clearSearchBtn = view.querySelector('#ClearSearchBtn');
    const autoRefreshBtn = view.querySelector('#AutoRefreshBtn');
    const autoRefreshLabel = view.querySelector('#AutoRefreshLabel');
    const followBtn = view.querySelector('#FollowBtn');
    const clearLogsBtn = view.querySelector('#ClearLogsBtn');
    const copyLogsBtn = view.querySelector('#CopyLogsBtn');
    const exportLogsBtn = view.querySelector('#ExportLogsBtn');
    const statsInfo = view.querySelector('#StatsInfo');
    const liveIndicator = view.querySelector('#LiveIndicator');
    const logLoading = view.querySelector('#LogLoading');
    const noLogsMessage = view.querySelector('#NoLogsMessage');
    const resetFiltersBtn = view.querySelector('#ResetFiltersBtn');
    const logEntries = view.querySelector('#LogEntries');
    const loadMoreBtn = view.querySelector('#LoadMoreBtn');
    const logsContainer = view.querySelector('#LogsContainer');
    const enableDebugLogging = view.querySelector('#EnableDebugLogging');
    const maxEntriesSelect = view.querySelector('#MaxEntries');
    const relativeTimeToggle = view.querySelector('#RelativeTime');
    const scrollTopBtn = view.querySelector('#ScrollTopBtn');
    const scrollBottomBtn = view.querySelector('#ScrollBottomBtn');

    // Quick filter buttons
    const filterAllBtn = view.querySelector('#FilterAll');
    const filterErrorsBtn = view.querySelector('#FilterErrors');
    const filterWarningsBtn = view.querySelector('#FilterWarnings');
    const filterStreamingBtn = view.querySelector('#FilterStreaming');
    const activeFilterTag = view.querySelector('#ActiveFilterTag');
    const activeFilterText = view.querySelector('#ActiveFilterText');
    const clearFilterBtn = view.querySelector('#ClearFilterBtn');

    // State
    let autoRefreshInterval = null;
    let isAutoRefreshEnabled = true;
    let isFollowMode = false;
    let latestLogId = 0;
    let currentSkip = 0;
    const pageSize = 100;
    let useRelativeTime = false;
    let activeCustomFilter = null; // { type: 'category'|'stream'|'channel', value: string }
    let knownCategories = new Set();

    // Buffering configuration
    let maxDomEntries = parseInt(maxEntriesSelect.value, 10) || 500;
    const NEW_ENTRY_HIGHLIGHT_MS = 2000;

    // Log buffer for batched updates
    let logBuffer = [];
    let bufferFlushScheduled = false;
    let renderFrameId = null;

    // Entry element pool
    const entryPool = [];
    const MAX_POOL_SIZE = 50;

    const logLevelNames = ['Trace', 'Debug', 'Info', 'Warn', 'Error', 'Crit'];
    const logLevelMap = {
      'trace': 0, 'debug': 1, 'information': 2, 'info': 2, 'warning': 3, 'error': 4, 'critical': 5
    };

    // Utility functions
    function parseLogLevel(level) {
      if (typeof level === 'number') return level;
      if (typeof level === 'string') {
        return logLevelMap[level.toLowerCase()] ?? 2;
      }
      return 2;
    }

    function showStatus(message, isError) {
      logStatus.textContent = message;
      logStatus.className = 'log-status ' + (isError ? 'error' : 'success');
      logStatus.style.display = 'flex';
      setTimeout(() => { logStatus.style.display = 'none'; }, 5000);
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

    function formatRelativeTime(isoString) {
      const date = new Date(isoString);
      const now = Date.now();
      const diffMs = now - date.getTime();
      const diffSec = Math.floor(diffMs / 1000);

      if (diffSec < 5) return 'just now';
      if (diffSec < 60) return `${diffSec}s ago`;
      if (diffSec < 3600) return `${Math.floor(diffSec / 60)}m ago`;
      if (diffSec < 86400) return `${Math.floor(diffSec / 3600)}h ago`;
      return `${Math.floor(diffSec / 86400)}d ago`;
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

    function highlightSearchText(text, searchTerm) {
      if (!searchTerm || searchTerm.length < 2) return escapeHtml(text);
      const escaped = escapeHtml(text);
      const regex = new RegExp(`(${searchTerm.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')})`, 'gi');
      return escaped.replace(regex, '<mark>$1</mark>');
    }

    // Element pooling
    function acquireEntryElement() {
      return entryPool.length > 0 ? entryPool.pop() : document.createElement('div');
    }

    function releaseEntryElement(element) {
      if (entryPool.length < MAX_POOL_SIZE) {
        element.className = '';
        element.innerHTML = '';
        element.style.cssText = '';
        entryPool.push(element);
      }
    }

    // Create log entry element
    function createLogEntry(entry, isNew = false) {
      const level = parseLogLevel(entry.level ?? entry.Level);
      const isDebug = entry.isDebug ?? entry.IsDebug ?? false;
      const displayLevel = isDebug ? 1 : level;
      const levelClass = getLevelClass(displayLevel);

      const div = acquireEntryElement();
      div.className = `log-entry level-${levelClass}${isDebug ? ' is-debug' : ''}${isNew ? ' new-entry' : ''}`;
      div.dataset.logId = entry.id ?? entry.Id;

      const timestamp = entry.timestamp ?? entry.Timestamp;
      const category = entry.category ?? entry.Category ?? 'Unknown';
      const message = entry.message ?? entry.Message ?? '';
      const streamId = entry.streamId ?? entry.StreamId;
      const channelName = entry.channelName ?? entry.ChannelName;
      const exceptionMessage = entry.exceptionMessage ?? entry.ExceptionMessage;
      const exceptionStack = entry.exceptionStackTrace ?? entry.ExceptionStackTrace;

      // Track categories for filter
      if (category && category !== 'Unknown') {
        if (!knownCategories.has(category)) {
          knownCategories.add(category);
          updateCategoryFilter();
        }
      }

      const timeDisplay = useRelativeTime ? formatRelativeTime(timestamp) : formatTimestamp(timestamp);
      const timeClass = useRelativeTime ? 'relative' : '';
      const searchTerm = searchText.value.trim();

      let metaHtml = '';
      if (streamId || channelName) {
        metaHtml = '<div class="log-meta">';
        if (channelName) {
          metaHtml += `<span class="log-channel-name" data-channel="${escapeHtml(channelName)}" title="Click to filter by channel">${escapeHtml(channelName)}</span>`;
        }
        if (streamId) {
          metaHtml += `<span class="log-stream-id" data-stream="${escapeHtml(streamId)}" title="Click to filter by stream">${escapeHtml(streamId.substring(0, 8))}...</span>`;
        }
        metaHtml += '</div>';
      }

      let exceptionHtml = '';
      if (exceptionMessage) {
        exceptionHtml = `
          <div class="log-exception" onclick="this.classList.toggle('expanded')">
            <div class="log-exception-header">
              <span class="material-icons">chevron_right</span>
              <span>${escapeHtml(exceptionMessage.split('\n')[0])}</span>
            </div>
            <div class="log-exception-details">${escapeHtml(exceptionMessage)}${exceptionStack ? '\n\n' + escapeHtml(exceptionStack) : ''}</div>
          </div>`;
      }

      div.innerHTML = `
        <span class="log-timestamp ${timeClass}" title="${formatTimestamp(timestamp)}">${timeDisplay}</span>
        <span class="log-level ${levelClass}">${getLevelName(displayLevel)}</span>
        <span class="log-category" data-category="${escapeHtml(category)}" title="Click to filter by ${escapeHtml(category)}">${escapeHtml(category)}</span>
        <span class="log-message">${highlightSearchText(message, searchTerm)}</span>
        ${metaHtml}
        ${exceptionHtml}
      `;

      // Add click handlers
      const categoryEl = div.querySelector('.log-category');
      if (categoryEl) {
        categoryEl.addEventListener('click', (e) => {
          e.stopPropagation();
          setCustomFilter('category', categoryEl.dataset.category);
        });
      }

      const streamEl = div.querySelector('.log-stream-id');
      if (streamEl) {
        streamEl.addEventListener('click', (e) => {
          e.stopPropagation();
          setCustomFilter('stream', streamEl.dataset.stream);
        });
      }

      const channelEl = div.querySelector('.log-channel-name');
      if (channelEl) {
        channelEl.addEventListener('click', (e) => {
          e.stopPropagation();
          setCustomFilter('channel', channelEl.dataset.channel);
        });
      }

      if (isNew) {
        setTimeout(() => div.classList.remove('new-entry'), NEW_ENTRY_HIGHLIGHT_MS);
      }

      return div;
    }

    // Custom filter management
    function setCustomFilter(type, value) {
      activeCustomFilter = { type, value };
      activeFilterTag.style.display = 'flex';
      activeFilterText.textContent = `${type}: ${value.length > 20 ? value.substring(0, 20) + '...' : value}`;
      searchText.value = value;
      clearSearchBtn.style.display = 'block';
      loadLogs();
    }

    function clearCustomFilter() {
      activeCustomFilter = null;
      activeFilterTag.style.display = 'none';
      searchText.value = '';
      clearSearchBtn.style.display = 'none';
      loadLogs();
    }

    // Category filter update
    function updateCategoryFilter() {
      const currentValue = categoryFilter.value;
      categoryFilter.innerHTML = '<option value="">All Sources</option>';
      const sorted = Array.from(knownCategories).sort();
      sorted.forEach(cat => {
        const opt = document.createElement('option');
        opt.value = cat;
        opt.textContent = cat;
        if (cat === currentValue) opt.selected = true;
        categoryFilter.appendChild(opt);
      });
    }

    // Quick filters
    function setQuickFilter(level) {
      [filterAllBtn, filterErrorsBtn, filterWarningsBtn, filterStreamingBtn].forEach(btn => btn.classList.remove('active'));

      if (level === 'all') {
        filterAllBtn.classList.add('active');
        logLevel.value = '2';
        categoryFilter.value = '';
      } else if (level === 'errors') {
        filterErrorsBtn.classList.add('active');
        logLevel.value = '4';
      } else if (level === 'warnings') {
        filterWarningsBtn.classList.add('active');
        logLevel.value = '3';
      } else if (level === 'streaming') {
        filterStreamingBtn.classList.add('active');
        logLevel.value = '0';
        searchText.value = 'stream';
        clearSearchBtn.style.display = 'block';
      }

      loadLogs();
    }

    // DOM trimming
    function trimOldEntries() {
      const entries = logEntries.children;
      while (entries.length > maxDomEntries) {
        const oldEntry = entries[entries.length - 1];
        releaseEntryElement(oldEntry);
        logEntries.removeChild(oldEntry);
      }
    }

    // Buffer management
    function scheduleBufferFlush() {
      if (bufferFlushScheduled) return;
      bufferFlushScheduled = true;
      renderFrameId = requestAnimationFrame(() => {
        flushLogBuffer();
        bufferFlushScheduled = false;
      });
    }

    function flushLogBuffer() {
      if (logBuffer.length === 0) return;

      const fragment = document.createDocumentFragment();
      const wasAtTop = logsContainer.scrollTop < 50;

      logBuffer.forEach(({ entry, prepend, isNew }) => {
        const entryEl = createLogEntry(entry, isNew);
        if (prepend) {
          fragment.insertBefore(entryEl, fragment.firstChild);
        } else {
          fragment.appendChild(entryEl);
        }

        const entryId = entry.id ?? entry.Id ?? 0;
        if (entryId > latestLogId) latestLogId = entryId;
      });

      if (logBuffer.some(item => item.prepend)) {
        logEntries.insertBefore(fragment, logEntries.firstChild);
      } else {
        logEntries.appendChild(fragment);
      }

      logBuffer = [];
      noLogsMessage.style.display = 'none';
      trimOldEntries();

      if (isFollowMode || wasAtTop) {
        logsContainer.scrollTo({ top: 0, behavior: 'smooth' });
      }
    }

    function bufferLogEntry(entry, prepend = false, isNew = false) {
      logBuffer.push({ entry, prepend, isNew });
      scheduleBufferFlush();
    }

    // Render logs
    function renderLogs(entries, append = false) {
      if (!append) {
        while (logEntries.firstChild) {
          releaseEntryElement(logEntries.firstChild);
          logEntries.removeChild(logEntries.firstChild);
        }
        currentSkip = 0;
        logBuffer = [];
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

      const fragment = document.createDocumentFragment();
      entries.forEach(entry => fragment.appendChild(createLogEntry(entry)));
      logEntries.appendChild(fragment);

      currentSkip += entries.length;
      loadMoreBtn.style.display = entries.length >= pageSize ? 'inline-flex' : 'none';

      if (entries.length > 0) {
        const firstEntry = entries[0];
        const entryId = firstEntry.id ?? firstEntry.Id ?? 0;
        if (entryId > latestLogId) latestLogId = entryId;
      }
    }

    function appendNewLogs(entries) {
      if (!entries || entries.length === 0) return;
      entries.forEach(entry => bufferLogEntry(entry, true, true));
    }

    // Filter params
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

    // Load logs
    function loadLogs(append = false) {
      const params = getFilterParams();
      const skip = append ? currentSkip : 0;
      const queryString = buildQueryString(params, skip);

      if (!append) {
        logLoading.style.display = 'block';
        noLogsMessage.style.display = 'none';
        while (logEntries.firstChild) {
          releaseEntryElement(logEntries.firstChild);
          logEntries.removeChild(logEntries.firstChild);
        }
      }

      Xtream.fetchJson(`Xtream/Logs?${queryString}`).then(entries => {
        // Client-side category filter
        if (categoryFilter.value) {
          entries = entries.filter(e => (e.category ?? e.Category) === categoryFilter.value);
        }
        renderLogs(entries, append);
      }).catch(err => {
        logLoading.style.display = 'none';
        showStatus('Failed to load logs: ' + err.message, true);
      });
    }

    function pollForNewLogs() {
      if (!isAutoRefreshEnabled || latestLogId === 0) return;

      const params = getFilterParams();
      const url = `Xtream/Logs/After/${latestLogId}?minLevel=${params.minLevel}&includeDebug=${params.includeDebug}`;

      Xtream.fetchJson(url).then(entries => {
        if (categoryFilter.value) {
          entries = entries.filter(e => (e.category ?? e.Category) === categoryFilter.value);
        }
        if (entries && entries.length > 0) {
          appendNewLogs(entries);
          updateStats();
        }
      }).catch(() => {});
    }

    // Stats
    function updateStats() {
      Xtream.fetchJson('Xtream/Logs/Stats').then(stats => {
        const total = stats.totalEntries ?? stats.TotalEntries ?? 0;
        const maxEntries = stats.maxEntries ?? stats.MaxEntries ?? 5000;
        const debugCount = stats.debugCount ?? stats.DebugCount ?? 0;
        const infoCount = stats.infoCount ?? stats.InfoCount ?? 0;
        const warningCount = stats.warningCount ?? stats.WarningCount ?? 0;
        const errorCount = stats.errorCount ?? stats.ErrorCount ?? 0;
        const domCount = logEntries.children.length;

        statsInfo.innerHTML = `
          <strong>${total}/${maxEntries}</strong> |
          <span style="color: #2196F3;">D:${debugCount}</span>
          <span style="color: #4caf50;">I:${infoCount}</span>
          <span style="color: #ff9800;">W:${warningCount}</span>
          <span style="color: #f44336;">E:${errorCount}</span>
          <span style="opacity: 0.6;">(${domCount} shown)</span>
        `;

        const statsLatestId = stats.latestId ?? stats.LatestId ?? 0;
        if (statsLatestId > latestLogId) latestLogId = statsLatestId;
      }).catch(() => {
        statsInfo.textContent = 'Failed to load stats';
      });
    }

    // Auto-refresh
    function toggleAutoRefresh() {
      isAutoRefreshEnabled = !isAutoRefreshEnabled;

      if (isAutoRefreshEnabled) {
        autoRefreshBtn.classList.add('auto-refresh-active');
        autoRefreshBtn.querySelector('.material-icons').textContent = 'pause';
        autoRefreshLabel.textContent = 'Live';
        liveIndicator.classList.remove('paused');
        startAutoRefresh();
      } else {
        autoRefreshBtn.classList.remove('auto-refresh-active');
        autoRefreshBtn.querySelector('.material-icons').textContent = 'play_arrow';
        autoRefreshLabel.textContent = 'Paused';
        liveIndicator.classList.add('paused');
        stopAutoRefresh();
      }
    }

    function startAutoRefresh() {
      if (autoRefreshInterval) return;
      autoRefreshInterval = setInterval(() => {
        pollForNewLogs();
        updateStats();
        if (useRelativeTime) updateRelativeTimestamps();
      }, 2000);
    }

    function stopAutoRefresh() {
      if (autoRefreshInterval) {
        clearInterval(autoRefreshInterval);
        autoRefreshInterval = null;
      }
    }

    // Follow mode
    function toggleFollowMode() {
      isFollowMode = !isFollowMode;
      if (isFollowMode) {
        followBtn.classList.add('follow-active');
        logsContainer.scrollTo({ top: 0, behavior: 'smooth' });
      } else {
        followBtn.classList.remove('follow-active');
      }
    }

    // Relative time update
    function updateRelativeTimestamps() {
      if (!useRelativeTime) return;
      logEntries.querySelectorAll('.log-timestamp').forEach(el => {
        const absTime = el.title;
        if (absTime) {
          el.textContent = formatRelativeTime(absTime);
        }
      });
    }

    // Clear logs
    function clearLogs() {
      if (!confirm('Clear all plugin logs?\n\nThis cannot be undone.')) return;

      Xtream.apiRequest('Xtream/Logs', { method: 'DELETE' }).then(result => {
        const success = result.success ?? result.Success;
        const message = result.message ?? result.Message;

        if (success) {
          showStatus(message, false);
          while (logEntries.firstChild) {
            releaseEntryElement(logEntries.firstChild);
            logEntries.removeChild(logEntries.firstChild);
          }
          latestLogId = 0;
          currentSkip = 0;
          logBuffer = [];
          noLogsMessage.style.display = 'block';
          loadMoreBtn.style.display = 'none';
          updateStats();
        } else {
          showStatus(message || 'Failed to clear logs', true);
        }
      }).catch(err => {
        showStatus('Failed to clear logs: ' + err.message, true);
      });
    }

    // Copy logs
    function copyLogs() {
      const entries = logEntries.querySelectorAll('.log-entry');
      if (entries.length === 0) {
        showStatus('No logs to copy', true);
        return;
      }

      const lines = [];
      entries.forEach(entry => {
        const timestamp = entry.querySelector('.log-timestamp')?.title || entry.querySelector('.log-timestamp')?.textContent || '';
        const level = entry.querySelector('.log-level')?.textContent || '';
        const category = entry.querySelector('.log-category')?.textContent || '';
        const message = entry.querySelector('.log-message')?.textContent || '';
        const exception = entry.querySelector('.log-exception-details')?.textContent || '';

        let line = `[${timestamp}] [${level.padEnd(5)}] [${category}] ${message}`;
        if (exception) line += `\n    Exception: ${exception}`;
        lines.push(line);
      });

      const text = lines.join('\n');
      navigator.clipboard.writeText(text).then(() => {
        showStatus(`Copied ${entries.length} entries`, false);
      }).catch(() => {
        showStatus('Failed to copy', true);
      });
    }

    // Export logs
    function exportLogs() {
      const entries = logEntries.querySelectorAll('.log-entry');
      if (entries.length === 0) {
        showStatus('No logs to export', true);
        return;
      }

      const lines = [];
      lines.push('='.repeat(80));
      lines.push(`Jellyfin Xtream Plugin Logs - Exported ${new Date().toISOString()}`);
      lines.push('='.repeat(80));
      lines.push('');

      entries.forEach(entry => {
        const timestamp = entry.querySelector('.log-timestamp')?.title || entry.querySelector('.log-timestamp')?.textContent || '';
        const level = entry.querySelector('.log-level')?.textContent || '';
        const category = entry.querySelector('.log-category')?.textContent || '';
        const message = entry.querySelector('.log-message')?.textContent || '';
        const exception = entry.querySelector('.log-exception-details')?.textContent || '';

        lines.push(`[${timestamp}] [${level.padEnd(5)}] [${category}]`);
        lines.push(`  ${message}`);
        if (exception) {
          lines.push(`  EXCEPTION:`);
          exception.split('\n').forEach(line => lines.push(`    ${line}`));
        }
        lines.push('');
      });

      const blob = new Blob([lines.join('\n')], { type: 'text/plain' });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `xtream-logs-${new Date().toISOString().replace(/[:.]/g, '-')}.txt`;
      a.click();
      URL.revokeObjectURL(url);
      showStatus(`Exported ${entries.length} entries`, false);
    }

    // Reset filters
    function resetFilters() {
      logLevel.value = '2';
      categoryFilter.value = '';
      includeDebug.checked = true;
      searchText.value = '';
      clearSearchBtn.style.display = 'none';
      activeCustomFilter = null;
      activeFilterTag.style.display = 'none';
      setQuickFilter('all');
    }

    // Keyboard shortcuts
    function handleKeyboard(e) {
      // Don't handle if typing in input
      if (e.target.tagName === 'INPUT' || e.target.tagName === 'TEXTAREA') {
        if (e.key === 'Escape') {
          e.target.blur();
          if (searchText.value) {
            searchText.value = '';
            clearSearchBtn.style.display = 'none';
            loadLogs();
          }
          if (helpPanel.style.display !== 'none') {
            helpPanel.style.display = 'none';
          }
        }
        return;
      }

      if (e.ctrlKey && e.key === 'f') {
        e.preventDefault();
        searchText.focus();
        return;
      }

      switch (e.key) {
        case 'F5':
          e.preventDefault();
          loadLogs();
          updateStats();
          break;
        case 'Escape':
          if (helpPanel.style.display !== 'none') {
            helpPanel.style.display = 'none';
          }
          break;
        case 'Home':
          logsContainer.scrollTo({ top: 0, behavior: 'smooth' });
          break;
        case 'End':
          logsContainer.scrollTo({ top: logsContainer.scrollHeight, behavior: 'smooth' });
          break;
        case 'e':
        case 'E':
          setQuickFilter('errors');
          break;
        case 'w':
        case 'W':
          setQuickFilter('warnings');
          break;
        case 'a':
        case 'A':
          setQuickFilter('all');
          break;
      }
    }

    // Event Listeners
    refreshBtn.addEventListener('click', () => { loadLogs(); updateStats(); });
    helpBtn.addEventListener('click', () => { helpPanel.style.display = helpPanel.style.display === 'none' ? 'block' : 'none'; });
    closeHelpBtn.addEventListener('click', () => { helpPanel.style.display = 'none'; });

    autoRefreshBtn.addEventListener('click', toggleAutoRefresh);
    followBtn.addEventListener('click', toggleFollowMode);
    copyLogsBtn.addEventListener('click', copyLogs);
    exportLogsBtn.addEventListener('click', exportLogs);
    clearLogsBtn.addEventListener('click', clearLogs);

    filterAllBtn.addEventListener('click', () => setQuickFilter('all'));
    filterErrorsBtn.addEventListener('click', () => setQuickFilter('errors'));
    filterWarningsBtn.addEventListener('click', () => setQuickFilter('warnings'));
    filterStreamingBtn.addEventListener('click', () => setQuickFilter('streaming'));
    clearFilterBtn.addEventListener('click', clearCustomFilter);

    logLevel.addEventListener('change', () => loadLogs());
    categoryFilter.addEventListener('change', () => loadLogs());
    includeDebug.addEventListener('change', () => loadLogs());

    maxEntriesSelect.addEventListener('change', () => {
      maxDomEntries = parseInt(maxEntriesSelect.value, 10) || 500;
      trimOldEntries();
    });

    relativeTimeToggle.addEventListener('change', () => {
      useRelativeTime = relativeTimeToggle.checked;
      loadLogs();
    });

    let searchDebounce;
    searchText.addEventListener('input', () => {
      clearSearchBtn.style.display = searchText.value ? 'block' : 'none';
      clearTimeout(searchDebounce);
      searchDebounce = setTimeout(() => loadLogs(), 300);
    });

    clearSearchBtn.addEventListener('click', () => {
      searchText.value = '';
      clearSearchBtn.style.display = 'none';
      if (activeCustomFilter) clearCustomFilter();
      else loadLogs();
    });

    loadMoreBtn.addEventListener('click', () => loadLogs(true));
    resetFiltersBtn.addEventListener('click', resetFilters);

    scrollTopBtn.addEventListener('click', () => logsContainer.scrollTo({ top: 0, behavior: 'smooth' }));
    scrollBottomBtn.addEventListener('click', () => logsContainer.scrollTo({ top: logsContainer.scrollHeight, behavior: 'smooth' }));

    document.addEventListener('keydown', handleKeyboard);

    // Debug logging toggle
    ApiClient.getPluginConfiguration(pluginId).then(config => {
      enableDebugLogging.checked = config.EnableDebugLogging ?? false;
    });

    enableDebugLogging.addEventListener('change', () => {
      ApiClient.getPluginConfiguration(pluginId).then(config => {
        config.EnableDebugLogging = enableDebugLogging.checked;
        ApiClient.updatePluginConfiguration(pluginId, config).then(() => {
          showStatus(enableDebugLogging.checked ? 'Debug logging enabled' : 'Debug logging disabled', false);
        }).catch(err => {
          showStatus('Failed to save: ' + err.message, true);
          enableDebugLogging.checked = !enableDebugLogging.checked;
        });
      });
    });

    // Initial load
    loadLogs();
    updateStats();

    if (isAutoRefreshEnabled) {
      autoRefreshBtn.classList.add('auto-refresh-active');
      startAutoRefresh();
    }

    // Cleanup
    view.addEventListener('viewhide', () => {
      stopAutoRefresh();
      if (renderFrameId) cancelAnimationFrame(renderFrameId);
      document.removeEventListener('keydown', handleKeyboard);
      logBuffer = [];
      entryPool.length = 0;
    });
  }));
}
