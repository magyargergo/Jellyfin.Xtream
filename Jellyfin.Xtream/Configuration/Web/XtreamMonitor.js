/**
 * Xtream Monitoring Dashboard
 *
 * Real-time monitoring of stream health, provider status, and quality metrics.
 * Uses Chart.js for time-series visualization with configurable time ranges.
 */

export default function (view) {
  view.addEventListener("viewshow", () => Promise.all([
    import(ApiClient.getUrl("web/ConfigurationPage", { name: "Xtream.js" })),
    import(ApiClient.getUrl("web/ConfigurationPage", { name: "XtreamStyles.js" })),
    import("https://cdn.jsdelivr.net/npm/chart.js@4.4.1/+esm")
  ]).then(([XtreamModule, StylesModule, ChartModule]) => {
    const Xtream = XtreamModule.default;
    const Chart = ChartModule.Chart;

    // Register Chart.js components
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

    Xtream.setTabs('XtreamMonitor');

    // DOM elements
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
    const streamDetailCount = view.querySelector('#StreamDetailCount');
    const streamsContainer = view.querySelector('#StreamsContainer');
    const noStreamsMessage = view.querySelector('#NoStreamsMessage');
    const healthMetricsGrid = view.querySelector('#HealthMetricsGrid');
    const noHealthMetricsMessage = view.querySelector('#NoHealthMetricsMessage');
    const healthSummaryCard = view.querySelector('#HealthSummaryCard');

    // State
    let autoRefreshInterval = null;
    let streamChart = null;
    let qualityChart = null;
    let currentTimeRange = 5; // minutes
    let lastTotalBytes = 0;
    let lastUpdateTime = Date.now();

    // Time series buffers (max 360 points for 30 min at 5s intervals)
    const MAX_POINTS = 360;
    const timeSeriesData = {
      streamCount: [],
      errors: [],
      drift: [],
      throughput: [],
      reconnections: []
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
      const pointsPerMinute = 12; // at 5s intervals
      return currentTimeRange * pointsPerMinute;
    }

    function trimToTimeRange(data) {
      const maxPoints = getTimeRangePoints();
      if (data.length > maxPoints) {
        return data.slice(-maxPoints);
      }
      return data;
    }

    // ========================================
    // Time Series Management
    // ========================================

    function addDataPoint(streams, connectionStatus) {
      const now = Date.now();

      // Calculate metrics
      const streamCount = streams.length;
      const totalErrors = streams.reduce((sum, s) => {
        const packetErrors = s.packetErrors ?? s.PacketErrors ?? 0;
        const continuityErrors = s.continuityErrors ?? s.ContinuityErrors ?? 0;
        const syncErrors = s.syncErrors ?? s.SyncErrors ?? 0;
        return sum + packetErrors + continuityErrors + syncErrors;
      }, 0);

      const avgDrift = streams.length > 0
        ? streams.reduce((sum, s) => {
            const drift = s.avDriftMs ?? s.AvDriftMs ?? 0;
            return sum + Math.abs(drift);
          }, 0) / streams.length
        : 0;

      // Calculate throughput (MB/s)
      const totalBytes = streams.reduce((sum, s) => {
        return sum + (s.totalBytesWritten ?? s.TotalBytesWritten ?? 0);
      }, 0);

      const timeDelta = (now - lastUpdateTime) / 1000; // seconds
      const bytesDelta = totalBytes - lastTotalBytes;
      const throughputMBps = timeDelta > 0 && lastTotalBytes > 0
        ? (bytesDelta / (1024 * 1024)) / timeDelta
        : 0;

      lastTotalBytes = totalBytes;
      lastUpdateTime = now;

      // Calculate total reconnections
      const totalReconnections = streams.reduce((sum, s) => {
        return sum + (s.reconnectionCount ?? s.ReconnectionCount ?? 0);
      }, 0);

      // Add to buffers
      timeSeriesData.streamCount.push({ x: now, y: streamCount });
      timeSeriesData.errors.push({ x: now, y: totalErrors });
      timeSeriesData.drift.push({ x: now, y: avgDrift });
      timeSeriesData.throughput.push({ x: now, y: Math.max(0, throughputMBps) });
      timeSeriesData.reconnections.push({ x: now, y: totalReconnections });

      // Trim to max points
      Object.keys(timeSeriesData).forEach(key => {
        if (timeSeriesData[key].length > MAX_POINTS) {
          timeSeriesData[key] = timeSeriesData[key].slice(-MAX_POINTS);
        }
      });
    }

    // ========================================
    // Chart Setup - Split into two charts
    // ========================================

    function getCommonChartOptions() {
      return {
        responsive: true,
        maintainAspectRatio: false,
        animation: { duration: 0 },
        interaction: {
          mode: 'index',
          intersect: false
        },
        plugins: {
          legend: { display: false },
          tooltip: {
            backgroundColor: 'rgba(0, 0, 0, 0.9)',
            titleColor: '#fff',
            bodyColor: '#ccc',
            borderColor: 'rgba(255, 255, 255, 0.2)',
            borderWidth: 1,
            padding: 10,
            displayColors: true,
            callbacks: {
              title: function(tooltipItems) {
                if (tooltipItems.length > 0) {
                  const ago = Math.round((Date.now() - tooltipItems[0].parsed.x) / 1000);
                  if (ago < 60) return `${ago} seconds ago`;
                  return `${Math.round(ago / 60)} minutes ago`;
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
        type: 'linear',
        display: true,
        grid: { color: 'rgba(255, 255, 255, 0.1)' },
        ticks: {
          color: 'rgba(255, 255, 255, 0.5)',
          callback: function(value) {
            const ago = Math.round((Date.now() - value) / 1000);
            if (ago < 60) return `${ago}s`;
            return `${Math.round(ago / 60)}m`;
          },
          maxTicksLimit: 5,
          font: { size: 10 }
        }
      };
    }

    function initStreamChart() {
      const ctx = streamChartCanvas.getContext('2d');
      const options = getCommonChartOptions();

      streamChart = new Chart(ctx, {
        type: 'line',
        data: {
          datasets: [
            {
              label: 'Active Streams',
              data: [],
              borderColor: '#00a4dc',
              backgroundColor: 'rgba(0, 164, 220, 0.15)',
              borderWidth: 2,
              fill: true,
              tension: 0.3,
              yAxisID: 'y',
              pointRadius: 0,
              pointHoverRadius: 4
            },
            {
              label: 'Throughput (MB/s)',
              data: [],
              borderColor: '#4caf50',
              backgroundColor: 'rgba(76, 175, 80, 0.1)',
              borderWidth: 2,
              fill: false,
              tension: 0.3,
              yAxisID: 'y1',
              pointRadius: 0,
              pointHoverRadius: 4
            }
          ]
        },
        options: {
          ...options,
          scales: {
            x: getTimeAxisConfig(),
            y: {
              type: 'linear',
              display: true,
              position: 'left',
              title: {
                display: true,
                text: 'Streams',
                color: '#00a4dc',
                font: { size: 10 }
              },
              grid: { color: 'rgba(255, 255, 255, 0.1)' },
              ticks: { color: '#00a4dc', font: { size: 10 } },
              beginAtZero: true
            },
            y1: {
              type: 'linear',
              display: true,
              position: 'right',
              title: {
                display: true,
                text: 'MB/s',
                color: '#4caf50',
                font: { size: 10 }
              },
              grid: { drawOnChartArea: false },
              ticks: { color: '#4caf50', font: { size: 10 } },
              beginAtZero: true
            }
          },
          plugins: {
            ...options.plugins,
            tooltip: {
              ...options.plugins.tooltip,
              callbacks: {
                ...options.plugins.tooltip.callbacks,
                label: function(context) {
                  const label = context.dataset.label || '';
                  const value = context.parsed.y;
                  if (label.includes('Streams')) return `Active Streams: ${value}`;
                  if (label.includes('Throughput')) return `Throughput: ${value.toFixed(2)} MB/s`;
                  return `${label}: ${value}`;
                }
              }
            }
          }
        }
      });
    }

    function initQualityChart() {
      const ctx = qualityChartCanvas.getContext('2d');
      const options = getCommonChartOptions();

      qualityChart = new Chart(ctx, {
        type: 'line',
        data: {
          datasets: [
            {
              label: 'Errors',
              data: [],
              borderColor: '#f44336',
              backgroundColor: 'rgba(244, 67, 54, 0.15)',
              borderWidth: 2,
              fill: true,
              tension: 0.3,
              yAxisID: 'y',
              pointRadius: 0,
              pointHoverRadius: 4
            },
            {
              label: 'A/V Drift (ms)',
              data: [],
              borderColor: '#ff9800',
              backgroundColor: 'rgba(255, 152, 0, 0.1)',
              borderWidth: 2,
              fill: false,
              tension: 0.3,
              yAxisID: 'y1',
              pointRadius: 0,
              pointHoverRadius: 4
            },
            {
              label: 'Reconnections',
              data: [],
              borderColor: '#9c27b0',
              backgroundColor: 'rgba(156, 39, 176, 0.1)',
              borderWidth: 2,
              fill: false,
              tension: 0.3,
              yAxisID: 'y',
              pointRadius: 2,
              pointHoverRadius: 5,
              pointStyle: 'circle'
            }
          ]
        },
        options: {
          ...options,
          scales: {
            x: getTimeAxisConfig(),
            y: {
              type: 'linear',
              display: true,
              position: 'left',
              title: {
                display: true,
                text: 'Errors / Reconnections',
                color: '#f44336',
                font: { size: 10 }
              },
              grid: { color: 'rgba(255, 255, 255, 0.1)' },
              ticks: { color: 'rgba(255, 255, 255, 0.5)', font: { size: 10 } },
              beginAtZero: true
            },
            y1: {
              type: 'linear',
              display: true,
              position: 'right',
              title: {
                display: true,
                text: 'Drift (ms)',
                color: '#ff9800',
                font: { size: 10 }
              },
              grid: { drawOnChartArea: false },
              ticks: { color: '#ff9800', font: { size: 10 } },
              beginAtZero: true
            }
          },
          plugins: {
            ...options.plugins,
            tooltip: {
              ...options.plugins.tooltip,
              callbacks: {
                ...options.plugins.tooltip.callbacks,
                label: function(context) {
                  const label = context.dataset.label || '';
                  const value = context.parsed.y;
                  if (label.includes('Errors')) return `Total Errors: ${value}`;
                  if (label.includes('Drift')) return `A/V Drift: ${value.toFixed(0)}ms`;
                  if (label.includes('Reconnections')) return `Reconnections: ${value}`;
                  return `${label}: ${value}`;
                }
              }
            }
          }
        }
      });
    }

    function initCharts() {
      initStreamChart();
      initQualityChart();
    }

    function updateCharts() {
      // Update Stream Activity chart
      if (streamChart) {
        streamChart.data.datasets[0].data = trimToTimeRange(timeSeriesData.streamCount);
        streamChart.data.datasets[1].data = trimToTimeRange(timeSeriesData.throughput);
        streamChart.update('none');
      }

      // Update Quality & Errors chart
      if (qualityChart) {
        qualityChart.data.datasets[0].data = trimToTimeRange(timeSeriesData.errors);
        qualityChart.data.datasets[1].data = trimToTimeRange(timeSeriesData.drift);
        qualityChart.data.datasets[2].data = trimToTimeRange(timeSeriesData.reconnections);
        qualityChart.update('none');
      }
    }

    // ========================================
    // UI Rendering
    // ========================================

    function updateSummaryCards(streams, connectionStatus) {
      // Stream count
      const activeStreams = connectionStatus.pluginActiveStreams ?? connectionStatus.PluginActiveStreams ?? streams.length;
      const maxStreams = connectionStatus.effectiveMaxStreams ?? connectionStatus.EffectiveMaxStreams ?? 0;

      streamCountCard.querySelector('.monitor-value').textContent = activeStreams;
      streamLimit.textContent = maxStreams > 0 ? `of ${maxStreams} max` : '';

      // Utilization
      const utilPct = connectionStatus.utilizationPercent ?? connectionStatus.UtilizationPercent ?? 0;
      const warningLevel = (connectionStatus.warningLevel ?? connectionStatus.WarningLevel ?? 'None').toLowerCase();

      utilizationCard.querySelector('.monitor-value').textContent = `${utilPct}%`;
      utilizationGauge.style.width = `${utilPct}%`;

      utilizationCard.classList.remove('warning', 'critical', 'good');
      if (warningLevel === 'critical') {
        utilizationCard.classList.add('critical');
        utilizationGauge.style.background = '#f44336';
      } else if (warningLevel === 'warning') {
        utilizationCard.classList.add('warning');
        utilizationGauge.style.background = '#ff9800';
      } else {
        utilizationGauge.style.background = '#00a4dc';
      }

      // Quality
      const totalErrors = streams.reduce((sum, s) => {
        const packetErrors = s.packetErrors ?? s.PacketErrors ?? 0;
        const continuityErrors = s.continuityErrors ?? s.ContinuityErrors ?? 0;
        const syncErrors = s.syncErrors ?? s.SyncErrors ?? 0;
        return sum + packetErrors + continuityErrors + syncErrors;
      }, 0);

      const hasQualityIssues = streams.some(s => s.hasQualityIssues ?? s.HasQualityIssues ?? false);

      qualityCard.classList.remove('warning', 'critical', 'good');
      if (hasQualityIssues || totalErrors > 100) {
        qualityCard.querySelector('.monitor-value').textContent = 'Issues';
        qualityCard.classList.add('critical');
      } else if (totalErrors > 10) {
        qualityCard.querySelector('.monitor-value').textContent = 'Fair';
        qualityCard.classList.add('warning');
      } else if (streams.length > 0) {
        qualityCard.querySelector('.monitor-value').textContent = 'Good';
        qualityCard.classList.add('good');
      } else {
        qualityCard.querySelector('.monitor-value').textContent = '-';
      }
      errorCount.textContent = `${totalErrors} errors`;

      // Throughput
      const latestThroughput = timeSeriesData.throughput.length > 0
        ? timeSeriesData.throughput[timeSeriesData.throughput.length - 1].y
        : 0;
      throughputCard.querySelector('.monitor-value').textContent = latestThroughput.toFixed(2);
    }

    function renderProviderCards(connectionStatus) {
      const providers = connectionStatus.providers ?? connectionStatus.Providers ?? [];

      if (providers.length === 0) {
        noProvidersMessage.style.display = 'block';
        return;
      }

      noProvidersMessage.style.display = 'none';

      // Clear existing cards (keep no-providers message)
      providerGrid.querySelectorAll('.provider-health-card').forEach(c => c.remove());

      providers.forEach(provider => {
        const name = provider.providerName ?? provider.ProviderName ?? 'Unknown';
        const active = provider.providerActiveConnections ?? provider.ProviderActiveConnections ?? 0;
        const max = provider.maxConnections ?? provider.MaxConnections ?? 0;
        const status = provider.status ?? provider.Status ?? 'Unknown';
        const isOnline = provider.isOnline ?? provider.IsOnline ?? false;
        const expirationDate = provider.expirationDate ?? provider.ExpirationDate;

        // Resilience service data
        const circuitState = provider.circuitState ?? provider.CircuitState ?? 'Closed';
        const selectionScore = provider.selectionScore ?? provider.SelectionScore ?? 0;
        const isAvailable = provider.isAvailable ?? provider.IsAvailable ?? true;
        const consecutiveFailures = provider.consecutiveFailures ?? provider.ConsecutiveFailures ?? 0;

        let cardClass = '';
        // Circuit breaker state takes priority
        if (circuitState === 'Open' || circuitState === 'Isolated') {
          cardClass = 'critical';
        } else if (!isOnline) {
          cardClass = 'offline';
        } else if (!isAvailable) {
          cardClass = 'critical';
        } else if (consecutiveFailures >= 2) {
          cardClass = 'warning';
        } else if (max > 0 && active >= max) {
          cardClass = 'critical';
        } else if (max > 0 && active >= max * 0.8) {
          cardClass = 'warning';
        }

        let expiryText = '';
        if (expirationDate) {
          const expDate = new Date(expirationDate);
          const daysLeft = Math.ceil((expDate - new Date()) / (1000 * 60 * 60 * 24));
          if (daysLeft > 0) {
            expiryText = `${daysLeft}d left`;
          } else {
            expiryText = 'Expired';
            cardClass = 'critical';
          }
        }

        // Circuit state indicator
        let circuitIndicator = '';
        if (circuitState === 'Open') {
          circuitIndicator = '<span class="circuit-badge critical" title="Circuit Open - Provider blacklisted">⛔ Open</span>';
        } else if (circuitState === 'Isolated') {
          circuitIndicator = '<span class="circuit-badge critical" title="Circuit Isolated - Connection limit">🔒 Isolated</span>';
        } else if (circuitState === 'HalfOpen') {
          circuitIndicator = '<span class="circuit-badge warning" title="Circuit Half-Open - Testing">⚡ Testing</span>';
        }

        // Health score badge color
        let scoreClass = 'good';
        if (selectionScore < 30) scoreClass = 'critical';
        else if (selectionScore < 60) scoreClass = 'warning';

        const card = document.createElement('div');
        card.className = `provider-health-card ${cardClass}`;
        card.innerHTML = `
          <div class="provider-name" title="${escapeHtml(name)}">${escapeHtml(name)}</div>
          <div class="provider-connections">${active}/${max}</div>
          <div class="provider-health-row">
            <span class="health-score ${scoreClass}" title="Health Score: ${selectionScore}/100">
              <span class="material-icons" style="font-size: 14px;">favorite</span> ${selectionScore}
            </span>
            ${circuitIndicator}
            ${consecutiveFailures > 0 ? `<span class="failure-count" title="Consecutive failures">⚠ ${consecutiveFailures}</span>` : ''}
          </div>
          <div class="provider-status">${escapeHtml(status)}</div>
          ${expiryText ? `<div class="provider-expiry">${expiryText}</div>` : ''}
        `;

        providerGrid.appendChild(card);
      });
    }

    function getErrorExplanation(packetErrors, continuityErrors, syncErrors) {
      const issues = [];
      if (packetErrors > 0) {
        issues.push(`<span class="error-type" title="Data packets lost during transmission - may cause pixelation or freezing"><span class="material-icons">broken_image</span> Packet: ${packetErrors}</span>`);
      }
      if (continuityErrors > 0) {
        issues.push(`<span class="error-type" title="Video frames out of sequence - may cause stuttering"><span class="material-icons">skip_next</span> Continuity: ${continuityErrors}</span>`);
      }
      if (syncErrors > 0) {
        issues.push(`<span class="error-type" title="Timing errors in stream - may cause audio/video desync"><span class="material-icons">sync_problem</span> Sync: ${syncErrors}</span>`);
      }
      return issues.length > 0 ? issues.join('') : '<span class="no-errors"><span class="material-icons">check_circle</span> No errors</span>';
    }

    function getStatusExplanation(status, qualityLevel, bufferFillPct, avDriftMs) {
      const explanations = [];
      const statusLower = status.toLowerCase();

      if (statusLower === 'lagging' || qualityLevel === 'Critical') {
        if (bufferFillPct < 20) {
          explanations.push('Buffer underrun - stream source too slow');
        }
        if (Math.abs(avDriftMs) > 100) {
          explanations.push('Audio/video out of sync');
        }
        if (explanations.length === 0) {
          explanations.push('Severe quality issues detected');
        }
      } else if (statusLower === 'ok' || qualityLevel === 'Warning') {
        if (bufferFillPct < 50) {
          explanations.push('Buffer low - possible network congestion');
        }
        if (Math.abs(avDriftMs) > 40) {
          explanations.push('Slight audio/video drift');
        }
        if (explanations.length === 0) {
          explanations.push('Minor quality issues');
        }
      }

      return explanations.length > 0 ? explanations.join('; ') : null;
    }

    function renderHealthMetrics(resilienceData) {
      if (!healthMetricsGrid || !resilienceData) return;

      const providers = resilienceData.providers ?? [];
      const summary = resilienceData.summary ?? {};

      // Update summary card
      if (healthSummaryCard) {
        const avgScore = summary.avgHealthScore ?? 0;
        const imminentFailures = summary.imminentFailures ?? 0;
        let summaryClass = 'good';
        if (imminentFailures > 0 || avgScore < 30) summaryClass = 'critical';
        else if (avgScore < 60) summaryClass = 'warning';

        healthSummaryCard.innerHTML = `
          <div class="health-summary-score ${summaryClass}">
            <span class="material-icons">speed</span>
            <span class="score-value">${avgScore}</span>
          </div>
          <div class="health-summary-stats">
            <div class="stat-row">
              <span class="stat-label">Healthy:</span>
              <span class="stat-value good">${summary.healthyProviders ?? 0}</span>
            </div>
            <div class="stat-row">
              <span class="stat-label">Degraded:</span>
              <span class="stat-value warning">${summary.degradedProviders ?? 0}</span>
            </div>
            <div class="stat-row">
              <span class="stat-label">Critical:</span>
              <span class="stat-value critical">${(summary.poorProviders ?? 0) + (summary.criticalProviders ?? 0)}</span>
            </div>
          </div>
          <div class="health-summary-trends">
            <div class="stat-row">
              <span class="material-icons trending-up">trending_up</span>
              <span class="stat-value good">${summary.improvingProviders ?? 0}</span>
            </div>
            <div class="stat-row">
              <span class="material-icons trending-down">trending_down</span>
              <span class="stat-value warning">${summary.degradingProviders ?? 0}</span>
            </div>
            ${imminentFailures > 0 ? `
            <div class="stat-row imminent-failure">
              <span class="material-icons">warning</span>
              <span class="stat-value critical">${imminentFailures} predicted</span>
            </div>` : ''}
          </div>
        `;
      }

      if (providers.length === 0) {
        if (noHealthMetricsMessage) noHealthMetricsMessage.style.display = 'block';
        return;
      }

      if (noHealthMetricsMessage) noHealthMetricsMessage.style.display = 'none';

      // Clear existing cards
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

        // Trend data
        const trendDirection = trend.direction ?? 'Stable';
        const predictedScore30s = trend.predictedScore30s ?? combinedScore;
        const predictedScore60s = trend.predictedScore60s ?? combinedScore;
        const suggestsImminentFailure = trend.suggestsImminentFailure ?? false;

        // Determine card class based on health status and predicted failure
        let cardClass = '';
        if (suggestsImminentFailure || healthStatus === 'Critical' || !isAvailable || circuitState === 'Open') {
          cardClass = 'critical';
        } else if (healthStatus === 'Poor' || healthStatus === 'Degraded') {
          cardClass = 'warning';
        }

        // Format latency display
        const latencyDisplay = perf.avgLatencyMs > 0
          ? `${perf.avgLatencyMs}ms`
          : '-';
        let latencyClass = '';
        if (perf.avgLatencyMs > 1000) latencyClass = 'critical';
        else if (perf.avgLatencyMs > 500) latencyClass = 'warning';

        // Format throughput display
        const throughputDisplay = perf.avgThroughputMBps > 0
          ? `${perf.avgThroughputMBps} MB/s`
          : '-';
        let throughputClass = '';
        if (perf.avgThroughputMBps > 3) throughputClass = 'good';
        else if (perf.avgThroughputMBps < 0.5) throughputClass = 'warning';

        // Format errors
        const totalErrors = perf.totalErrors ?? 0;
        let errorClass = '';
        if (totalErrors > 100) errorClass = 'critical';
        else if (totalErrors > 10) errorClass = 'warning';

        // Circuit state badge
        let circuitBadge = '';
        if (circuitState === 'Open') {
          circuitBadge = '<span class="circuit-badge-sm critical">Open</span>';
        } else if (circuitState === 'Isolated') {
          circuitBadge = '<span class="circuit-badge-sm critical">Isolated</span>';
        } else if (circuitState === 'HalfOpen') {
          circuitBadge = '<span class="circuit-badge-sm warning">Testing</span>';
        }

        // Trend icon and class
        let trendIcon = 'trending_flat';
        let trendClass = '';
        if (trendDirection === 'Improving') {
          trendIcon = 'trending_up';
          trendClass = 'good';
        } else if (trendDirection === 'Degrading') {
          trendIcon = 'trending_down';
          trendClass = 'warning';
        } else if (trendDirection === 'RapidlyDegrading') {
          trendIcon = 'trending_down';
          trendClass = 'critical';
        }

        // Predicted failure warning
        const failureWarning = suggestsImminentFailure
          ? `<div class="hm-failure-warning">
              <span class="material-icons">warning</span>
              <span>Predicted failure in ~60s (score: ${predictedScore60s})</span>
            </div>`
          : '';

        const card = document.createElement('div');
        card.className = `health-metric-card ${cardClass}`;
        card.innerHTML = `
          <div class="hm-header">
            <span class="hm-name" title="${escapeHtml(name)}">${escapeHtml(name)}</span>
            <span class="hm-status ${healthStatus.toLowerCase()}">${healthStatus}</span>
            ${circuitBadge}
          </div>
          ${failureWarning}
          <div class="hm-scores">
            <div class="hm-score-item" title="Combined Health Score (0-100)">
              <span class="material-icons">favorite</span>
              <span class="hm-score-value">${combinedScore}</span>
            </div>
            <div class="hm-trend-indicator ${trendClass}" title="Trend: ${trendDirection} | Predicted in 30s: ${predictedScore30s} | 60s: ${predictedScore60s}">
              <span class="material-icons">${trendIcon}</span>
              <span class="hm-predicted">${predictedScore30s}</span>
            </div>
            <div class="hm-score-breakdown">
              <span title="Resilience Score">R: ${resilienceScore}</span>
              <span title="Metrics Score">M: ${metricsScore}</span>
            </div>
          </div>
          <div class="hm-metrics">
            <div class="hm-metric" title="Average connection latency">
              <span class="hm-metric-label">Latency</span>
              <span class="hm-metric-value ${latencyClass}">${latencyDisplay}</span>
            </div>
            <div class="hm-metric" title="Average throughput">
              <span class="hm-metric-label">Throughput</span>
              <span class="hm-metric-value ${throughputClass}">${throughputDisplay}</span>
            </div>
            <div class="hm-metric" title="Total errors (packet + continuity + sync + timeout + network)">
              <span class="hm-metric-label">Errors</span>
              <span class="hm-metric-value ${errorClass}">${totalErrors}</span>
            </div>
            <div class="hm-metric" title="Stream disconnections">
              <span class="hm-metric-label">Disconnects</span>
              <span class="hm-metric-value">${perf.disconnections ?? 0}</span>
            </div>
          </div>
          ${perf.totalSamples > 0 ? `<div class="hm-samples">Based on ${perf.totalSamples} samples</div>` : ''}
        `;

        healthMetricsGrid.appendChild(card);
      });
    }

    function renderStreamCards(streams) {
      // Clear existing cards
      streamsContainer.querySelectorAll('.stream-detail-card').forEach(c => c.remove());

      if (!streams || streams.length === 0) {
        noStreamsMessage.style.display = 'block';
        streamDetailCount.textContent = '';
        return;
      }

      noStreamsMessage.style.display = 'none';
      streamDetailCount.textContent = `${streams.length} stream(s)`;

      streams.forEach(stream => {
        const channelName = stream.channelName ?? stream.ChannelName ?? 'Unknown';
        const status = stream.status ?? stream.Status ?? 'Unknown';
        const gapPct = stream.gapPercentage ?? stream.GapPercentage ?? 0;
        const bufferFillPct = Math.max(0, Math.min(100, 100 - gapPct));
        const avDriftMs = stream.avDriftMs ?? stream.AvDriftMs ?? 0;
        const syncStatus = stream.syncStatus ?? stream.SyncStatus ?? 'Unknown';
        const qualityLevel = stream.qualityLevel ?? stream.QualityLevel ?? 'None';

        const packetErrors = stream.packetErrors ?? stream.PacketErrors ?? 0;
        const continuityErrors = stream.continuityErrors ?? stream.ContinuityErrors ?? 0;
        const syncErrors = stream.syncErrors ?? stream.SyncErrors ?? 0;
        const totalErrors = packetErrors + continuityErrors + syncErrors;

        // Reconnection info
        const reconnectionCount = stream.reconnectionCount ?? stream.ReconnectionCount ?? 0;
        const secondsSinceLastReconnection = stream.secondsSinceLastReconnection ?? stream.SecondsSinceLastReconnection ?? null;

        let cardClass = '';
        let badgeClass = '';
        if (qualityLevel === 'Critical' || status.toLowerCase() === 'lagging') {
          cardClass = 'critical';
          badgeClass = 'critical';
        } else if (qualityLevel === 'Warning' || status.toLowerCase() === 'ok') {
          cardClass = 'warning';
          badgeClass = 'warning';
        }

        const driftClass = Math.abs(avDriftMs) > 100 ? 'critical' : Math.abs(avDriftMs) > 40 ? 'warning' : 'good';
        const errorClass = totalErrors > 10 ? 'critical' : totalErrors > 0 ? 'warning' : 'good';
        const bufferClass = bufferFillPct < 20 ? 'critical' : bufferFillPct < 50 ? 'warning' : '';

        const errorBreakdown = getErrorExplanation(packetErrors, continuityErrors, syncErrors);
        const statusExplanation = getStatusExplanation(status, qualityLevel, bufferFillPct, avDriftMs);

        // Build reconnection info HTML
        let reconnectionHtml = '';
        if (reconnectionCount > 0) {
          let timeAgo = '';
          if (secondsSinceLastReconnection !== null) {
            if (secondsSinceLastReconnection < 60) {
              timeAgo = `${Math.round(secondsSinceLastReconnection)}s ago`;
            } else {
              timeAgo = `${Math.round(secondsSinceLastReconnection / 60)}m ago`;
            }
          }
          reconnectionHtml = `
            <div class="stream-reconnection-info" title="Provider disconnected and stream was reconnected. May cause brief video interruption.">
              <span class="material-icons">sync</span>
              <span class="reconnection-count">${reconnectionCount}</span> reconnection${reconnectionCount > 1 ? 's' : ''}
              ${timeAgo ? ` (last: ${timeAgo})` : ''}
            </div>
          `;
        }

        const card = document.createElement('div');
        card.className = `stream-detail-card ${cardClass}`;
        card.innerHTML = `
          <div class="stream-detail-header">
            <span class="stream-detail-name">${escapeHtml(channelName)}</span>
            <span class="stream-detail-badge ${badgeClass}">${escapeHtml(status)}</span>
          </div>
          ${statusExplanation ? `<div class="stream-status-explanation">${escapeHtml(statusExplanation)}</div>` : ''}
          ${reconnectionHtml}
          <div class="stream-detail-stats">
            <div class="stream-stat" title="Buffer fill level - lower values may cause stuttering">
              <div class="stream-stat-label">Buffer Fill</div>
              <div class="stream-stat-value">${bufferFillPct.toFixed(0)}%</div>
              <div class="buffer-progress">
                <div class="buffer-progress-fill ${bufferClass}" style="width: ${bufferFillPct}%"></div>
              </div>
            </div>
            <div class="stream-stat" title="Audio/video synchronization - &lt;40ms ideal, &gt;100ms problematic">
              <div class="stream-stat-label">A/V Drift</div>
              <div class="stream-stat-value ${driftClass}">${avDriftMs.toFixed(0)}ms</div>
            </div>
            <div class="stream-stat" title="Total transmission errors">
              <div class="stream-stat-label">Errors</div>
              <div class="stream-stat-value ${errorClass}">${totalErrors}</div>
            </div>
            <div class="stream-stat" title="Stream timing synchronization status">
              <div class="stream-stat-label">Sync</div>
              <div class="stream-stat-value">${escapeHtml(syncStatus)}</div>
            </div>
          </div>
          <div class="stream-error-breakdown">
            ${errorBreakdown}
          </div>
        `;

        streamsContainer.appendChild(card);
      });
    }

    // ========================================
    // Data Loading
    // ========================================

    async function loadData() {
      try {
        const [streams, connectionStatus, resilienceMetrics] = await Promise.all([
          Xtream.fetchJson('Xtream/ActiveStreams'),
          Xtream.fetchJson('Xtream/ConnectionStatus'),
          Xtream.fetchJson('Xtream/ResilienceMetrics').catch(() => null)
        ]);

        // Add data point to time series
        addDataPoint(streams, connectionStatus);

        // Update UI
        updateSummaryCards(streams, connectionStatus);
        renderProviderCards(connectionStatus);
        renderStreamCards(streams);
        renderHealthMetrics(resilienceMetrics);
        updateCharts();

      } catch (err) {
        console.error('Failed to load monitoring data:', err);
      }
    }

    // ========================================
    // Auto-refresh Management
    // ========================================

    function startAutoRefresh() {
      if (autoRefreshInterval) return;

      autoRefreshInterval = setInterval(() => {
        if (document.hidden) return; // Skip if page not visible
        loadData();
      }, 5000);
    }

    function stopAutoRefresh() {
      if (autoRefreshInterval) {
        clearInterval(autoRefreshInterval);
        autoRefreshInterval = null;
      }
    }

    // ========================================
    // Event Handlers
    // ========================================

    refreshBtn.addEventListener('click', loadData);

    // Help toggle
    helpToggle.addEventListener('click', () => {
      helpSection.classList.toggle('hide');
      helpToggle.classList.toggle('active');
    });

    autoRefreshToggle.addEventListener('change', (e) => {
      if (e.target.checked) {
        startAutoRefresh();
      } else {
        stopAutoRefresh();
      }
    });

    timeRangeButtons.addEventListener('click', (e) => {
      const btn = e.target.closest('.time-range-btn');
      if (!btn) return;

      const range = parseInt(btn.dataset.range, 10);
      if (isNaN(range)) return;

      currentTimeRange = range;

      // Update button states
      timeRangeButtons.querySelectorAll('.time-range-btn').forEach(b => {
        b.classList.remove('active');
      });
      btn.classList.add('active');

      // Update charts
      updateCharts();
    });

    // Handle page visibility
    document.addEventListener('visibilitychange', () => {
      if (!document.hidden && autoRefreshToggle.checked) {
        loadData(); // Immediate refresh when page becomes visible
      }
    });

    // ========================================
    // Initialization
    // ========================================

    initCharts();
    loadData();

    if (autoRefreshToggle.checked) {
      startAutoRefresh();
    }

    // Cleanup on page leave
    view.addEventListener('viewhide', () => {
      stopAutoRefresh();
      if (streamChart) {
        streamChart.destroy();
        streamChart = null;
      }
      if (qualityChart) {
        qualityChart.destroy();
        qualityChart = null;
      }
    });
  }).catch(err => {
    console.error('Failed to initialize monitoring dashboard:', err);
  }));
}
