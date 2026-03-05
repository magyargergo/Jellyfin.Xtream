/**
 * Xtream Dashboard
 *
 * Unified monitoring + stream management page.
 * Merges Monitor (charts, summary cards, provider health) with
 * Streams (filter/sort/kill, rich stream cards).
 */

export default function (view) {
  view.addEventListener("viewshow", () => Promise.all([
    import(ApiClient.getUrl("web/ConfigurationPage", { name: "Xtream.js" })),
    import(ApiClient.getUrl("web/ConfigurationPage", { name: "XtreamStyles.js" })),
    import("https://cdn.jsdelivr.net/npm/chart.js@4.4.1/+esm")
  ]).then(([XtreamModule, StylesModule, ChartModule]) => {
    const Xtream = XtreamModule.default;
    const Chart = ChartModule.Chart;

    ChartModule.Chart.register(
      ChartModule.LineController,
      ChartModule.LineElement,
      ChartModule.PointElement,
      ChartModule.LinearScale,
      ChartModule.TimeScale,
      ChartModule.Title,
      ChartModule.Tooltip,
      ChartModule.Legend,
      ChartModule.Filler
    );

    Xtream.setTabs('XtreamDashboard');

    // ========================================
    // DOM Elements - Monitor
    // ========================================
    const refreshBtn = view.querySelector('#RefreshBtn');
    const autoRefreshToggle = view.querySelector('#AutoRefreshToggle');
    const helpToggle = view.querySelector('#HelpToggle');
    const helpSection = view.querySelector('#HelpSection');
    const streamCountCard = view.querySelector('#StreamCountCard');
    const utilizationCard = view.querySelector('#UtilizationCard');
    const qualityCard = view.querySelector('#QualityCard');
    const throughputCard = view.querySelector('#ThroughputCard');
    const streamLimit = view.querySelector('#StreamLimit');
    const utilizationGauge = view.querySelector('#UtilizationGauge');
    const errorCount = view.querySelector('#ErrorCount');
    const providerGrid = view.querySelector('#ProviderGrid');
    const noProvidersMessage = view.querySelector('#NoProvidersMessage');
    const timeRangeButtons = view.querySelector('#TimeRangeButtons');
    const streamChartCanvas = view.querySelector('#StreamChart');
    const qualityChartCanvas = view.querySelector('#QualityChart');
    const healthMetricsGrid = view.querySelector('#HealthMetricsGrid');
    const noHealthMetricsMessage = view.querySelector('#NoHealthMetricsMessage');
    const healthSummaryCard = view.querySelector('#HealthSummaryCard');

    // DOM Elements - Streams
    const streamDetailCount = view.querySelector('#StreamDetailCount');
    const streamsContainer = view.querySelector('#StreamsContainer');
    const noStreamsMessage = view.querySelector('#NoStreamsMessage');
    const streamStatusEl = view.querySelector('#StreamStatus');
    const killAllBtn = view.querySelector('#KillAllBtn');
    const statusFilter = view.querySelector('#StatusFilter');
    const qualityFilter = view.querySelector('#QualityFilter');
    const sortBySelect = view.querySelector('#SortBy');
    const sortDirBtn = view.querySelector('#SortDirBtn');
    const sortDirIcon = view.querySelector('#SortDirIcon');

    // ========================================
    // State
    // ========================================
    let autoRefreshInterval = null;
    let streamChart = null;
    let qualityChart = null;
    let currentTimeRange = 5;
    let lastTotalBytes = 0;
    let lastUpdateTime = Date.now();

    const MAX_POINTS = 360;
    const timeSeriesData = {
      streamCount: [], errors: [], drift: [], throughput: [], reconnections: []
    };

    const filterState = {
      status: '',
      qualityIssuesOnly: false,
      sortBy: 'startTime',
      sortDesc: false
    };

    // ========================================
    // Utility Functions
    // ========================================
    function formatBytes(bytes) {
      if (bytes === 0) return '0 B';
      const k = 1024;
      const sizes = ['B', 'KB', 'MB', 'GB'];
      const i = Math.floor(Math.log(bytes) / Math.log(k));
      return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
    }

    function escapeHtml(text) {
      if (!text) return '';
      const div = document.createElement('div');
      div.textContent = text;
      return div.innerHTML;
    }

    function getTimeRangePoints() {
      return currentTimeRange * 12;
    }

    function trimToTimeRange(data) {
      const maxPoints = getTimeRangePoints();
      return data.length > maxPoints ? data.slice(-maxPoints) : data;
    }

    function showStatus(message, isError) {
      streamStatusEl.textContent = message;
      streamStatusEl.className = 'stream-status ' + (isError ? 'error' : 'success');
      streamStatusEl.style.display = 'block';
      setTimeout(() => { streamStatusEl.style.display = 'none'; }, 5000);
    }

    function showToast(message) {
      const toast = document.createElement('div');
      toast.style.cssText = 'position: fixed; bottom: 20px; right: 20px; background: rgba(244,67,54,0.95); color: white; padding: 12px 20px; border-radius: 6px; z-index: 10000; font-size: 0.9em; box-shadow: 0 4px 12px rgba(0,0,0,0.3);';
      toast.textContent = message;
      document.body.appendChild(toast);
      setTimeout(() => toast.remove(), 5000);
    }

    // ========================================
    // Time Series
    // ========================================
    function addDataPoint(streams, connectionStatus) {
      const now = Date.now();
      const streamCount = streams.length;
      const totalErrors = streams.reduce((sum, s) => {
        return sum + (s.packetErrors ?? s.PacketErrors ?? 0) + (s.continuityErrors ?? s.ContinuityErrors ?? 0) + (s.syncErrors ?? s.SyncErrors ?? 0);
      }, 0);

      const avgDrift = streams.length > 0
        ? streams.reduce((sum, s) => sum + Math.abs(s.avDriftMs ?? s.AvDriftMs ?? 0), 0) / streams.length
        : 0;

      const totalBytes = streams.reduce((sum, s) => sum + (s.totalBytesWritten ?? s.TotalBytesWritten ?? 0), 0);
      const timeDelta = (now - lastUpdateTime) / 1000;
      const bytesDelta = totalBytes - lastTotalBytes;
      const throughputMBps = timeDelta > 0 && lastTotalBytes > 0 ? (bytesDelta / (1024 * 1024)) / timeDelta : 0;

      lastTotalBytes = totalBytes;
      lastUpdateTime = now;

      const totalReconnections = streams.reduce((sum, s) => sum + (s.reconnectionCount ?? s.ReconnectionCount ?? 0), 0);

      timeSeriesData.streamCount.push({ x: now, y: streamCount });
      timeSeriesData.errors.push({ x: now, y: totalErrors });
      timeSeriesData.drift.push({ x: now, y: avgDrift });
      timeSeriesData.throughput.push({ x: now, y: Math.max(0, throughputMBps) });
      timeSeriesData.reconnections.push({ x: now, y: totalReconnections });

      Object.keys(timeSeriesData).forEach(key => {
        if (timeSeriesData[key].length > MAX_POINTS) {
          timeSeriesData[key] = timeSeriesData[key].slice(-MAX_POINTS);
        }
      });
    }

    // ========================================
    // Charts
    // ========================================
    function getCommonChartOptions() {
      return {
        responsive: true, maintainAspectRatio: false,
        animation: { duration: 0 },
        interaction: { mode: 'index', intersect: false },
        plugins: {
          legend: { display: false },
          tooltip: {
            backgroundColor: 'rgba(0, 0, 0, 0.9)', titleColor: '#fff', bodyColor: '#ccc',
            borderColor: 'rgba(255, 255, 255, 0.2)', borderWidth: 1, padding: 10, displayColors: true,
            callbacks: {
              title: function(tooltipItems) {
                if (tooltipItems.length > 0) {
                  const ago = Math.round((Date.now() - tooltipItems[0].parsed.x) / 1000);
                  return ago < 60 ? `${ago} seconds ago` : `${Math.round(ago / 60)} minutes ago`;
                }
                return '';
              }
            }
          }
        }
      };
    }

    function getTimeAxisConfig() {
      return {
        type: 'linear', display: true,
        grid: { color: 'rgba(255, 255, 255, 0.1)' },
        ticks: {
          color: 'rgba(255, 255, 255, 0.5)',
          callback: function(value) {
            const ago = Math.round((Date.now() - value) / 1000);
            return ago < 60 ? `${ago}s` : `${Math.round(ago / 60)}m`;
          },
          maxTicksLimit: 5, font: { size: 10 }
        }
      };
    }

    function initCharts() {
      const opts = getCommonChartOptions();
      const xAxis = getTimeAxisConfig();

      streamChart = new Chart(streamChartCanvas.getContext('2d'), {
        type: 'line',
        data: {
          datasets: [
            { label: 'Active Streams', data: [], borderColor: '#00a4dc', backgroundColor: 'rgba(0, 164, 220, 0.15)', borderWidth: 2, fill: true, tension: 0.3, yAxisID: 'y', pointRadius: 0, pointHoverRadius: 4 },
            { label: 'Throughput (MB/s)', data: [], borderColor: '#4caf50', backgroundColor: 'rgba(76, 175, 80, 0.1)', borderWidth: 2, fill: false, tension: 0.3, yAxisID: 'y1', pointRadius: 0, pointHoverRadius: 4 }
          ]
        },
        options: {
          ...opts,
          scales: {
            x: xAxis,
            y: { type: 'linear', display: true, position: 'left', title: { display: true, text: 'Streams', color: '#00a4dc', font: { size: 10 } }, grid: { color: 'rgba(255, 255, 255, 0.1)' }, ticks: { color: '#00a4dc', font: { size: 10 } }, beginAtZero: true },
            y1: { type: 'linear', display: true, position: 'right', title: { display: true, text: 'MB/s', color: '#4caf50', font: { size: 10 } }, grid: { drawOnChartArea: false }, ticks: { color: '#4caf50', font: { size: 10 } }, beginAtZero: true }
          },
          plugins: { ...opts.plugins, tooltip: { ...opts.plugins.tooltip, callbacks: { ...opts.plugins.tooltip.callbacks,
            label: function(ctx) {
              const l = ctx.dataset.label || '';
              return l.includes('Streams') ? `Active Streams: ${ctx.parsed.y}` : l.includes('Throughput') ? `Throughput: ${ctx.parsed.y.toFixed(2)} MB/s` : `${l}: ${ctx.parsed.y}`;
            }
          }}}
        }
      });

      qualityChart = new Chart(qualityChartCanvas.getContext('2d'), {
        type: 'line',
        data: {
          datasets: [
            { label: 'Errors', data: [], borderColor: '#f44336', backgroundColor: 'rgba(244, 67, 54, 0.15)', borderWidth: 2, fill: true, tension: 0.3, yAxisID: 'y', pointRadius: 0, pointHoverRadius: 4 },
            { label: 'A/V Drift (ms)', data: [], borderColor: '#ff9800', backgroundColor: 'rgba(255, 152, 0, 0.1)', borderWidth: 2, fill: false, tension: 0.3, yAxisID: 'y1', pointRadius: 0, pointHoverRadius: 4 },
            { label: 'Reconnections', data: [], borderColor: '#9c27b0', backgroundColor: 'rgba(156, 39, 176, 0.1)', borderWidth: 2, fill: false, tension: 0.3, yAxisID: 'y', pointRadius: 2, pointHoverRadius: 5, pointStyle: 'circle' }
          ]
        },
        options: {
          ...opts,
          scales: {
            x: xAxis,
            y: { type: 'linear', display: true, position: 'left', title: { display: true, text: 'Errors / Reconnections', color: '#f44336', font: { size: 10 } }, grid: { color: 'rgba(255, 255, 255, 0.1)' }, ticks: { color: 'rgba(255, 255, 255, 0.5)', font: { size: 10 } }, beginAtZero: true },
            y1: { type: 'linear', display: true, position: 'right', title: { display: true, text: 'Drift (ms)', color: '#ff9800', font: { size: 10 } }, grid: { drawOnChartArea: false }, ticks: { color: '#ff9800', font: { size: 10 } }, beginAtZero: true }
          },
          plugins: { ...opts.plugins, tooltip: { ...opts.plugins.tooltip, callbacks: { ...opts.plugins.tooltip.callbacks,
            label: function(ctx) {
              const l = ctx.dataset.label || '';
              if (l.includes('Errors')) return `Total Errors: ${ctx.parsed.y}`;
              if (l.includes('Drift')) return `A/V Drift: ${ctx.parsed.y.toFixed(0)}ms`;
              if (l.includes('Reconnections')) return `Reconnections: ${ctx.parsed.y}`;
              return `${l}: ${ctx.parsed.y}`;
            }
          }}}
        }
      });
    }

    function updateCharts() {
      if (streamChart) {
        streamChart.data.datasets[0].data = trimToTimeRange(timeSeriesData.streamCount);
        streamChart.data.datasets[1].data = trimToTimeRange(timeSeriesData.throughput);
        streamChart.update('none');
      }
      if (qualityChart) {
        qualityChart.data.datasets[0].data = trimToTimeRange(timeSeriesData.errors);
        qualityChart.data.datasets[1].data = trimToTimeRange(timeSeriesData.drift);
        qualityChart.data.datasets[2].data = trimToTimeRange(timeSeriesData.reconnections);
        qualityChart.update('none');
      }
    }

    // ========================================
    // Summary Cards
    // ========================================
    function updateSummaryCards(streams, connectionStatus) {
      const activeStreams = connectionStatus.pluginActiveStreams ?? streams.length;
      const maxStreams = connectionStatus.effectiveMaxStreams ?? 0;
      streamCountCard.querySelector('.monitor-value').textContent = activeStreams;
      streamLimit.textContent = maxStreams > 0 ? `of ${maxStreams} max` : '';

      const utilPct = connectionStatus.utilizationPercent ?? 0;
      const warningLevel = (connectionStatus.warningLevel ?? 'None').toLowerCase();
      utilizationCard.querySelector('.monitor-value').textContent = `${utilPct}%`;
      utilizationGauge.style.width = `${utilPct}%`;
      utilizationCard.classList.remove('warning', 'critical', 'good');
      if (warningLevel === 'critical') { utilizationCard.classList.add('critical'); utilizationGauge.style.background = '#f44336'; }
      else if (warningLevel === 'warning') { utilizationCard.classList.add('warning'); utilizationGauge.style.background = '#ff9800'; }
      else { utilizationGauge.style.background = '#00a4dc'; }

      const totalErrors = streams.reduce((sum, s) => sum + (s.packetErrors ?? s.PacketErrors ?? 0) + (s.continuityErrors ?? s.ContinuityErrors ?? 0) + (s.syncErrors ?? s.SyncErrors ?? 0), 0);
      const hasQualityIssues = streams.some(s => s.hasQualityIssues ?? s.HasQualityIssues ?? false);
      qualityCard.classList.remove('warning', 'critical', 'good');
      if (hasQualityIssues || totalErrors > 100) { qualityCard.querySelector('.monitor-value').textContent = 'Issues'; qualityCard.classList.add('critical'); }
      else if (totalErrors > 10) { qualityCard.querySelector('.monitor-value').textContent = 'Fair'; qualityCard.classList.add('warning'); }
      else if (streams.length > 0) { qualityCard.querySelector('.monitor-value').textContent = 'Good'; qualityCard.classList.add('good'); }
      else { qualityCard.querySelector('.monitor-value').textContent = '-'; }
      errorCount.textContent = `${totalErrors} errors`;

      const latestThroughput = timeSeriesData.throughput.length > 0 ? timeSeriesData.throughput[timeSeriesData.throughput.length - 1].y : 0;
      throughputCard.querySelector('.monitor-value').textContent = latestThroughput.toFixed(2);
    }

    // ========================================
    // Provider Health Cards
    // ========================================
    function renderProviderCards(connectionStatus) {
      const providers = connectionStatus.providers ?? [];
      if (providers.length === 0) { noProvidersMessage.style.display = 'block'; return; }
      noProvidersMessage.style.display = 'none';
      providerGrid.querySelectorAll('.provider-health-card').forEach(c => c.remove());

      providers.forEach(provider => {
        const name = provider.providerName ?? provider.ProviderName ?? 'Unknown';
        const active = provider.providerActiveConnections ?? provider.ProviderActiveConnections ?? 0;
        const max = provider.maxConnections ?? provider.MaxConnections ?? 0;
        const status = provider.status ?? provider.Status ?? 'Unknown';
        const isOnline = provider.isOnline ?? provider.IsOnline ?? false;
        const expirationDate = provider.expirationDate ?? provider.ExpirationDate;
        const circuitState = provider.circuitState ?? provider.CircuitState ?? 'Closed';
        const selectionScore = provider.selectionScore ?? provider.SelectionScore ?? 0;
        const isAvailable = provider.isAvailable ?? provider.IsAvailable ?? true;
        const consecutiveFailures = provider.consecutiveFailures ?? provider.ConsecutiveFailures ?? 0;

        let cardClass = '';
        if (circuitState === 'Open' || circuitState === 'Isolated') cardClass = 'critical';
        else if (!isOnline) cardClass = 'offline';
        else if (!isAvailable) cardClass = 'critical';
        else if (consecutiveFailures >= 2) cardClass = 'warning';
        else if (max > 0 && active >= max) cardClass = 'critical';
        else if (max > 0 && active >= max * 0.8) cardClass = 'warning';

        let expiryText = '';
        if (expirationDate) {
          const daysLeft = Math.ceil((new Date(expirationDate) - new Date()) / (1000 * 60 * 60 * 24));
          expiryText = daysLeft > 0 ? `${daysLeft}d left` : 'Expired';
          if (daysLeft <= 0) cardClass = 'critical';
        }

        let circuitIndicator = '';
        if (circuitState === 'Open') circuitIndicator = '<span class="circuit-badge critical">Open</span>';
        else if (circuitState === 'Isolated') circuitIndicator = '<span class="circuit-badge critical">Isolated</span>';
        else if (circuitState === 'HalfOpen') circuitIndicator = '<span class="circuit-badge warning">Testing</span>';

        let scoreClass = 'good';
        if (selectionScore < 30) scoreClass = 'critical';
        else if (selectionScore < 60) scoreClass = 'warning';

        const card = document.createElement('div');
        card.className = `provider-health-card ${cardClass}`;
        card.innerHTML = `
          <div class="provider-name" title="${escapeHtml(name)}">${escapeHtml(name)}</div>
          <div class="provider-connections">${active}/${max}</div>
          <div class="provider-health-row">
            <span class="health-score ${scoreClass}"><span class="material-icons" style="font-size: 14px;">favorite</span> ${selectionScore}</span>
            ${circuitIndicator}
            ${consecutiveFailures > 0 ? `<span class="failure-count">${consecutiveFailures} failures</span>` : ''}
          </div>
          <div class="provider-status">${escapeHtml(status)}</div>
          ${expiryText ? `<div class="provider-expiry">${expiryText}</div>` : ''}
        `;
        providerGrid.appendChild(card);
      });
    }

    // ========================================
    // Health Metrics
    // ========================================
    function renderHealthMetrics(resilienceData) {
      if (!healthMetricsGrid || !resilienceData) return;
      const providers = resilienceData.providers ?? [];
      const summary = resilienceData.summary ?? {};

      if (healthSummaryCard) {
        const avgScore = summary.avgHealthScore ?? 0;
        const imminentFailures = summary.imminentFailures ?? 0;
        let summaryClass = 'good';
        if (imminentFailures > 0 || avgScore < 30) summaryClass = 'critical';
        else if (avgScore < 60) summaryClass = 'warning';

        healthSummaryCard.innerHTML = `
          <div class="health-summary-score ${summaryClass}"><span class="material-icons">speed</span><span class="score-value">${avgScore}</span></div>
          <div class="health-summary-stats">
            <div class="stat-row"><span class="stat-label">Healthy:</span><span class="stat-value good">${summary.healthyProviders ?? 0}</span></div>
            <div class="stat-row"><span class="stat-label">Degraded:</span><span class="stat-value warning">${summary.degradedProviders ?? 0}</span></div>
            <div class="stat-row"><span class="stat-label">Critical:</span><span class="stat-value critical">${(summary.poorProviders ?? 0) + (summary.criticalProviders ?? 0)}</span></div>
          </div>
          <div class="health-summary-trends">
            <div class="stat-row"><span class="material-icons trending-up">trending_up</span><span class="stat-value good">${summary.improvingProviders ?? 0}</span></div>
            <div class="stat-row"><span class="material-icons trending-down">trending_down</span><span class="stat-value warning">${summary.degradingProviders ?? 0}</span></div>
            ${imminentFailures > 0 ? `<div class="stat-row imminent-failure"><span class="material-icons">warning</span><span class="stat-value critical">${imminentFailures} predicted</span></div>` : ''}
          </div>
        `;
      }

      if (providers.length === 0) {
        if (noHealthMetricsMessage) noHealthMetricsMessage.style.display = 'block';
        return;
      }
      if (noHealthMetricsMessage) noHealthMetricsMessage.style.display = 'none';
      healthMetricsGrid.querySelectorAll('.health-metric-card').forEach(c => c.remove());

      providers.forEach(provider => {
        const name = provider.providerName ?? 'Unknown';
        const healthStatus = provider.healthStatus ?? 'Unknown';
        const combinedScore = provider.combinedHealthScore ?? 0;
        const resilienceScore = provider.selectionScore ?? 0;
        const metricsScore = provider.metricsScore ?? 0;
        const circuitState = provider.circuitState ?? 'Closed';
        const isAvailable = provider.isAvailable ?? true;
        const perf = provider.performance ?? {};
        const trend = provider.trend ?? {};

        const trendDirection = trend.direction ?? 'Stable';
        const predictedScore30s = trend.predictedScore30s ?? combinedScore;
        const predictedScore60s = trend.predictedScore60s ?? combinedScore;
        const suggestsImminentFailure = trend.suggestsImminentFailure ?? false;

        let cardClass = '';
        if (suggestsImminentFailure || healthStatus === 'Critical' || !isAvailable || circuitState === 'Open') cardClass = 'critical';
        else if (healthStatus === 'Poor' || healthStatus === 'Degraded') cardClass = 'warning';

        const latencyDisplay = perf.avgLatencyMs > 0 ? `${perf.avgLatencyMs}ms` : '-';
        let latencyClass = perf.avgLatencyMs > 1000 ? 'critical' : perf.avgLatencyMs > 500 ? 'warning' : '';

        const throughputDisplay = perf.avgThroughputMBps > 0 ? `${perf.avgThroughputMBps} MB/s` : '-';
        let throughputClass = perf.avgThroughputMBps > 3 ? 'good' : perf.avgThroughputMBps < 0.5 ? 'warning' : '';

        const totalErrors = perf.totalErrors ?? 0;
        let errorClass = totalErrors > 100 ? 'critical' : totalErrors > 10 ? 'warning' : '';

        let circuitBadge = '';
        if (circuitState === 'Open') circuitBadge = '<span class="circuit-badge-sm critical">Open</span>';
        else if (circuitState === 'Isolated') circuitBadge = '<span class="circuit-badge-sm critical">Isolated</span>';
        else if (circuitState === 'HalfOpen') circuitBadge = '<span class="circuit-badge-sm warning">Testing</span>';

        let trendIcon = 'trending_flat', trendClass = '';
        if (trendDirection === 'Improving') { trendIcon = 'trending_up'; trendClass = 'good'; }
        else if (trendDirection === 'Degrading') { trendIcon = 'trending_down'; trendClass = 'warning'; }
        else if (trendDirection === 'RapidlyDegrading') { trendIcon = 'trending_down'; trendClass = 'critical'; }

        const failureWarning = suggestsImminentFailure
          ? `<div class="hm-failure-warning"><span class="material-icons">warning</span><span>Predicted failure in ~60s (score: ${predictedScore60s})</span></div>`
          : '';

        const card = document.createElement('div');
        card.className = `health-metric-card ${cardClass}`;
        card.innerHTML = `
          <div class="hm-header"><span class="hm-name" title="${escapeHtml(name)}">${escapeHtml(name)}</span><span class="hm-status ${healthStatus.toLowerCase()}">${healthStatus}</span>${circuitBadge}</div>
          ${failureWarning}
          <div class="hm-scores">
            <div class="hm-score-item" title="Combined Health Score"><span class="material-icons">favorite</span><span class="hm-score-value">${combinedScore}</span></div>
            <div class="hm-trend-indicator ${trendClass}" title="Trend: ${trendDirection}"><span class="material-icons">${trendIcon}</span><span class="hm-predicted">${predictedScore30s}</span></div>
            <div class="hm-score-breakdown"><span title="Resilience">R: ${resilienceScore}</span><span title="Metrics">M: ${metricsScore}</span></div>
          </div>
          <div class="hm-metrics">
            <div class="hm-metric"><span class="hm-metric-label">Latency</span><span class="hm-metric-value ${latencyClass}">${latencyDisplay}</span></div>
            <div class="hm-metric"><span class="hm-metric-label">Throughput</span><span class="hm-metric-value ${throughputClass}">${throughputDisplay}</span></div>
            <div class="hm-metric"><span class="hm-metric-label">Errors</span><span class="hm-metric-value ${errorClass}">${totalErrors}</span></div>
            <div class="hm-metric"><span class="hm-metric-label">Disconnects</span><span class="hm-metric-value">${perf.disconnections ?? 0}</span></div>
          </div>
          ${perf.totalSamples > 0 ? `<div class="hm-samples">Based on ${perf.totalSamples} samples</div>` : ''}
        `;
        healthMetricsGrid.appendChild(card);
      });
    }

    // ========================================
    // Stream Cards (from Streams page - rich with kill buttons)
    // ========================================
    function createStreamCard(stream) {
      const streamStatus = stream.status ?? stream.Status ?? 'Unknown';
      const statusClass = streamStatus.toLowerCase();
      const gapPct = stream.gapPercentage ?? stream.GapPercentage ?? 0;
      const bufferFillPct = 100 - gapPct;
      const channelName = stream.channelName ?? stream.ChannelName ?? 'Unknown';
      const streamId = stream.streamId ?? stream.StreamId ?? 'Unknown';
      const bufferSize = stream.bufferSizeBytes ?? stream.BufferSizeBytes ?? 0;
      const totalWritten = stream.totalBytesWritten ?? stream.TotalBytesWritten ?? 0;
      const totalRead = stream.totalBytesRead ?? stream.TotalBytesRead ?? 0;
      const currentGap = stream.currentGapBytes ?? stream.CurrentGapBytes ?? 0;
      const hasQualityIssues = stream.hasQualityIssues ?? stream.HasQualityIssues ?? false;
      const qualityLevel = stream.qualityLevel ?? stream.QualityLevel ?? 'None';
      const qualityIssues = stream.qualityIssues ?? stream.QualityIssues ?? null;
      const packetErrors = stream.packetErrors ?? stream.PacketErrors ?? 0;
      const continuityErrors = stream.continuityErrors ?? stream.ContinuityErrors ?? 0;
      const syncErrors = stream.syncErrors ?? stream.SyncErrors ?? 0;
      const patViolations = stream.patViolations ?? stream.PatViolations ?? 0;
      const crcErrors = stream.crcErrors ?? stream.CrcErrors ?? 0;
      const avDriftMs = stream.avDriftMs ?? stream.AvDriftMs ?? 0;
      const syncStatus = stream.syncStatus ?? stream.SyncStatus ?? 'Unknown';

      let qualityColor = '#4caf50', qualityIcon = 'check_circle';
      if (qualityLevel === 'Critical') { qualityColor = '#f44336'; qualityIcon = 'error'; }
      else if (qualityLevel === 'Warning') { qualityColor = '#ff9800'; qualityIcon = 'warning'; }

      let qualitySection = '';
      if (hasQualityIssues) {
        qualitySection = `
          <div class="quality-warning" style="margin-top: 12px; padding: 8px 12px; background: rgba(${qualityLevel === 'Critical' ? '244, 67, 54' : '255, 152, 0'}, 0.1); border-radius: 4px; border-left: 3px solid ${qualityColor};">
            <div style="display: flex; align-items: center; gap: 8px;"><span class="material-icons" style="color: ${qualityColor};">${qualityIcon}</span><span style="color: ${qualityColor}; font-weight: bold;">Quality Issues</span></div>
            <div style="font-size: 0.9em; margin-top: 4px; color: #aaa;">${escapeHtml(qualityIssues)}</div>
          </div>`;
      }

      const totalStreamErrors = packetErrors + continuityErrors + syncErrors + patViolations + crcErrors;

      const card = document.createElement('div');
      card.className = `stream-card ${statusClass}`;
      card.dataset.streamId = streamId;
      card.innerHTML = `
        <div class="stream-header">
          <div class="stream-title">${escapeHtml(channelName)}</div>
          <div style="display: flex; align-items: center; gap: 8px;">
            <span class="material-icons" style="color: ${qualityColor}; font-size: 18px;" title="Quality: ${qualityLevel}">${qualityIcon}</span>
            <span class="stream-status-badge ${statusClass}">${streamStatus}</span>
          </div>
        </div>
        <div class="stream-stats">
          <div class="stat-item"><div class="stat-label">Buffer Size</div><div class="stat-value">${formatBytes(bufferSize)}</div></div>
          <div class="stat-item"><div class="stat-label">Written</div><div class="stat-value">${formatBytes(totalWritten)}</div></div>
          <div class="stat-item"><div class="stat-label">Read</div><div class="stat-value">${formatBytes(totalRead)}</div></div>
          <div class="stat-item"><div class="stat-label">Buffer Gap</div><div class="stat-value">${formatBytes(currentGap)} (${gapPct.toFixed(1)}%)</div></div>
          <div class="stat-item"><div class="stat-label">A/V Sync</div><div class="stat-value" style="color: ${Math.abs(avDriftMs) > 100 ? '#f44336' : Math.abs(avDriftMs) > 40 ? '#ff9800' : '#4caf50'};">${syncStatus}${Math.abs(avDriftMs) > 1 ? ` (${avDriftMs.toFixed(0)}ms)` : ''}</div></div>
          <div class="stat-item" title="Packet: ${packetErrors} | Continuity: ${continuityErrors} | Sync: ${syncErrors} | PAT: ${patViolations} | CRC: ${crcErrors}"><div class="stat-label">Errors</div><div class="stat-value" style="color: ${totalStreamErrors > 0 ? '#ff9800' : '#4caf50'};">${totalStreamErrors} total</div></div>
        </div>
        ${qualitySection}
        <div style="margin-top: 8px;"><div class="stat-label">Buffer Fill</div><div class="progress-bar"><div class="progress-fill ${statusClass}" style="width: ${bufferFillPct}%"></div></div></div>
        <div class="stream-id">ID: ${escapeHtml(streamId)}</div>
        <div class="stream-actions">
          <button is="emby-button" type="button" class="raised button-cancel kill-stream-btn emby-button" data-stream-id="${escapeHtml(streamId)}">
            <span class="material-icons close"></span><span>Kill Stream</span>
          </button>
        </div>
      `;

      card.querySelector('.kill-stream-btn').addEventListener('click', (e) => {
        e.stopPropagation();
        killStream(streamId);
      });

      card.addEventListener('click', () => toggleStreamDetail(card, streamId));
      return card;
    }

    function toggleStreamDetail(card, streamId) {
      const existing = card.querySelector('.stream-detail-panel');
      if (existing) { existing.remove(); return; }

      const panel = document.createElement('div');
      panel.className = 'stream-detail-panel';
      panel.style.cssText = 'margin-top: 12px; padding: 12px; background: rgba(0,0,0,0.2); border-radius: 6px; font-size: 0.9em;';
      panel.innerHTML = '<div style="color: #888;">Loading details...</div>';
      card.appendChild(panel);

      Promise.all([
        Xtream.fetchJson(`Xtream/ActiveStreams/${encodeURIComponent(streamId)}/Metrics`).catch(() => null),
        Xtream.fetchJson(`Xtream/ActiveStreams/${encodeURIComponent(streamId)}/Providers`).catch(() => null)
      ]).then(([metrics, providers]) => {
        let html = '';
        if (metrics) {
          html += '<div style="margin-bottom: 8px;"><strong>Metrics</strong></div><div class="stream-stats" style="margin-bottom: 12px;">';
          Object.entries(metrics).filter(([k]) => k !== 'streamId' && k !== 'StreamId').forEach(([key, value]) => {
            const label = key.replace(/([A-Z])/g, ' $1').trim();
            html += `<div class="stat-item"><div class="stat-label">${escapeHtml(label)}</div><div class="stat-value">${typeof value === 'number' ? (Number.isInteger(value) ? value : value.toFixed(2)) : escapeHtml(String(value ?? '-'))}</div></div>`;
          });
          html += '</div>';
        }
        if (providers && Array.isArray(providers.providers)) {
          html += '<div style="margin-bottom: 8px;"><strong>Providers</strong></div>';
          providers.providers.forEach(p => {
            html += `<div style="padding: 4px 0; display: flex; gap: 12px; align-items: center;"><span>${escapeHtml(p.providerName ?? p.ProviderName ?? 'Unknown')}</span><span class="stream-status-badge" style="font-size: 0.8em;">${escapeHtml(p.state ?? p.State ?? 'Unknown')}</span><span style="color: #888;">Success: ${((p.successRate ?? p.SuccessRate ?? 0) * 100).toFixed(0)}%</span></div>`;
          });
        }
        panel.innerHTML = html || '<div style="color: #888;">No additional details available</div>';
      });
    }

    function renderStreams(streams, totalCount) {
      streamsContainer.querySelectorAll('.stream-card').forEach(card => card.remove());
      const count = totalCount ?? (streams ? streams.length : 0);

      if (!streams || streams.length === 0) {
        noStreamsMessage.style.display = 'block';
        streamDetailCount.textContent = '';
        killAllBtn.disabled = true;
        return;
      }

      noStreamsMessage.style.display = 'none';
      streamDetailCount.textContent = `${count} stream(s)`;
      killAllBtn.disabled = false;
      streams.forEach(stream => streamsContainer.appendChild(createStreamCard(stream)));
    }

    // ========================================
    // Kill Stream Actions
    // ========================================
    function killStream(streamId) {
      if (!confirm(`Kill this stream?\n\nStream ID: ${streamId}`)) return;
      Xtream.apiRequest(`Xtream/ActiveStreams/${encodeURIComponent(streamId)}`, { method: 'DELETE' }).then((result) => {
        const success = result.success ?? result.Success;
        showStatus(result.message ?? result.Message ?? (success ? 'Stream killed' : 'Failed'), !success);
        if (success) loadData();
      }).catch(err => showStatus('Failed to kill stream: ' + err.message, true));
    }

    function killAllStreams() {
      if (!confirm('Kill ALL active streams?\n\nThis will disconnect all viewers.')) return;
      Xtream.apiRequest('Xtream/ActiveStreams', { method: 'DELETE' }).then((result) => {
        const success = result.success ?? result.Success;
        showStatus(result.message ?? result.Message ?? (success ? 'All streams killed' : 'Failed'), !success);
        if (success) loadData();
      }).catch(err => showStatus('Failed to kill streams: ' + err.message, true));
    }

    // ========================================
    // Data Loading (single source from Monitor)
    // ========================================
    async function loadData() {
      try {
        // Build query params from filter state
        const params = new URLSearchParams();
        if (filterState.status) params.set('status', filterState.status);
        if (filterState.qualityIssuesOnly) params.set('hasQualityIssues', 'true');
        if (filterState.sortBy) params.set('sortBy', filterState.sortBy);
        if (filterState.sortDesc) params.set('sortDesc', 'true');
        const streamsUrl = 'Xtream/ActiveStreams' + (params.toString() ? '?' + params : '');

        const [activeStreamsData, connectionInfo, providerHealth] = await Promise.all([
          Xtream.fetchJson(streamsUrl),
          Xtream.fetchJson('Xtream/ConnectionInfo').catch(() => ({})),
          Xtream.fetchJson('Xtream/ProviderHealth').catch(() => null)
        ]);

        const streams = activeStreamsData.items || [];
        const totalCount = activeStreamsData.totalCount ?? streams.length;

        const maxConn = connectionInfo.maxConnections ?? 0;
        const pluginActive = connectionInfo.pluginActiveStreams ?? streams.length;
        const utilPct = maxConn > 0 ? Math.round((pluginActive / maxConn) * 100) : 0;

        let warningLevel = 'None';
        if (maxConn > 0) {
          if (pluginActive >= maxConn) warningLevel = 'Critical';
          else if (pluginActive >= maxConn * 0.8) warningLevel = 'Warning';
        }

        const connectionStatus = {
          pluginActiveStreams: pluginActive,
          effectiveMaxStreams: maxConn,
          utilizationPercent: utilPct,
          warningLevel,
          providers: providerHealth ? (providerHealth.providers || []) : []
        };

        const resilienceMetrics = providerHealth ? {
          providers: providerHealth.providers || [],
          summary: providerHealth.summary || {}
        } : null;

        addDataPoint(streams, connectionStatus);
        updateSummaryCards(streams, connectionStatus);
        renderProviderCards(connectionStatus);
        renderStreams(streams, totalCount);
        renderHealthMetrics(resilienceMetrics);
        updateCharts();
      } catch (err) {
        console.error('Failed to load dashboard data:', err);
        showToast('Failed to load data: ' + (err.message || 'Unknown error'));
      }
    }

    // ========================================
    // Auto-refresh & SSE
    // ========================================
    function startAutoRefresh() {
      if (autoRefreshInterval) return;
      autoRefreshInterval = setInterval(() => {
        if (!document.hidden) loadData();
      }, 5000);
    }

    function stopAutoRefresh() {
      if (autoRefreshInterval) { clearInterval(autoRefreshInterval); autoRefreshInterval = null; }
    }

    let eventSource = null;
    function startSSE() {
      if (eventSource) return;
      try {
        eventSource = Xtream.createPluginEventSource();
        eventSource.addEventListener('stream.started', () => loadData());
        eventSource.addEventListener('stream.stopped', () => loadData());
        eventSource.addEventListener('stream.killed', () => loadData());
        eventSource.addEventListener('buffer.overflow', (e) => {
          try {
            const data = JSON.parse(e.data);
            showToast(`Buffer overflow: ${data.data?.channelName || data.streamId || 'Unknown'}`);
          } catch (_) { showToast('Buffer overflow detected'); }
        });
        eventSource.onerror = () => { stopSSE(); if (autoRefreshToggle.checked) startAutoRefresh(); };
      } catch (_) {}
    }
    function stopSSE() { if (eventSource) { eventSource.close(); eventSource = null; } }

    // ========================================
    // Event Handlers
    // ========================================
    refreshBtn.addEventListener('click', loadData);
    killAllBtn.addEventListener('click', killAllStreams);

    helpToggle.addEventListener('click', () => {
      helpSection.classList.toggle('hide');
      helpToggle.classList.toggle('active');
    });

    autoRefreshToggle.addEventListener('change', (e) => {
      if (e.target.checked) { startSSE(); startAutoRefresh(); }
      else { stopSSE(); stopAutoRefresh(); }
    });

    // Filter handlers
    statusFilter.addEventListener('change', () => { filterState.status = statusFilter.value; loadData(); });
    qualityFilter.addEventListener('change', () => { filterState.qualityIssuesOnly = qualityFilter.checked; loadData(); });
    sortBySelect.addEventListener('change', () => { filterState.sortBy = sortBySelect.value; loadData(); });
    sortDirBtn.addEventListener('click', () => {
      filterState.sortDesc = !filterState.sortDesc;
      sortDirIcon.textContent = filterState.sortDesc ? 'arrow_downward' : 'arrow_upward';
      loadData();
    });

    timeRangeButtons.addEventListener('click', (e) => {
      const btn = e.target.closest('.time-range-btn');
      if (!btn) return;
      const range = parseInt(btn.dataset.range, 10);
      if (isNaN(range)) return;
      currentTimeRange = range;
      timeRangeButtons.querySelectorAll('.time-range-btn').forEach(b => b.classList.remove('active'));
      btn.classList.add('active');
      updateCharts();
    });

    // Diagnostics download
    const diagBtn = view.querySelector('#DownloadDiagnosticsBtn');
    if (diagBtn) {
      diagBtn.addEventListener('click', () => {
        Xtream.fetchJson('Xtream/Diagnostics/Bundle').then(data => {
          const blob = new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' });
          const url = URL.createObjectURL(blob);
          const a = document.createElement('a');
          a.href = url;
          a.download = `xtream-diagnostics-${new Date().toISOString().slice(0, 19).replace(/:/g, '-')}.json`;
          a.click();
          URL.revokeObjectURL(url);
        }).catch(err => showToast('Failed to download diagnostics: ' + err.message));
      });
    }

    document.addEventListener('visibilitychange', () => {
      if (!document.hidden && autoRefreshToggle.checked) loadData();
    });

    // ========================================
    // Initialization
    // ========================================
    initCharts();
    Dashboard.showLoadingMsg();
    loadData().finally(() => Dashboard.hideLoadingMsg());
    if (autoRefreshToggle.checked) { startSSE(); startAutoRefresh(); }

    // Cleanup
    view.addEventListener('viewhide', () => {
      stopSSE();
      stopAutoRefresh();
      if (streamChart) { streamChart.destroy(); streamChart = null; }
      if (qualityChart) { qualityChart.destroy(); qualityChart = null; }
    });
  }).catch(err => {
    console.error('Failed to initialize dashboard:', err);
  }));
}
