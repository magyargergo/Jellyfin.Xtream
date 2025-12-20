export default function (view) {
  view.addEventListener("viewshow", () => import(
    ApiClient.getUrl("web/ConfigurationPage", {
      name: "Xtream.js",
    })
  ).then((Xtream) => Xtream.default
  ).then((Xtream) => {
    Xtream.setTabs('XtreamStreams');

    const refreshBtn = view.querySelector('#RefreshBtn');
    const killAllBtn = view.querySelector('#KillAllBtn');
    const streamStatus = view.querySelector('#StreamStatus');
    const streamCount = view.querySelector('#StreamCount');
    const streamsContainer = view.querySelector('#StreamsContainer');
    const noStreamsMessage = view.querySelector('#NoStreamsMessage');

    let autoRefreshInterval = null;

    function formatBytes(bytes) {
      if (bytes === 0) return '0 B';
      const k = 1024;
      const sizes = ['B', 'KB', 'MB', 'GB'];
      const i = Math.floor(Math.log(bytes) / Math.log(k));
      return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
    }

    function showStatus(message, isError) {
      streamStatus.textContent = message;
      streamStatus.className = 'stream-status ' + (isError ? 'error' : 'success');
      streamStatus.style.display = 'block';

      setTimeout(() => {
        streamStatus.style.display = 'none';
      }, 5000);
    }

    function createStreamCard(stream) {
      const streamStatus = stream.status ?? stream.Status ?? 'Unknown';
      const statusClass = streamStatus.toLowerCase();
      const gapPct = stream.gapPercentage ?? stream.GapPercentage ?? 0;
      const bufferFillPct = 100 - gapPct;

      const card = document.createElement('div');
      card.className = `stream-card ${statusClass}`;
      card.dataset.streamId = stream.streamId ?? stream.StreamId;

      const channelName = stream.channelName ?? stream.ChannelName ?? 'Unknown';
      const streamId = stream.streamId ?? stream.StreamId ?? 'Unknown';
      const status = streamStatus;
      const bufferSize = stream.bufferSizeBytes ?? stream.BufferSizeBytes ?? 0;
      const totalWritten = stream.totalBytesWritten ?? stream.TotalBytesWritten ?? 0;
      const totalRead = stream.totalBytesRead ?? stream.TotalBytesRead ?? 0;
      const currentGap = stream.currentGapBytes ?? stream.CurrentGapBytes ?? 0;
      const overflowCount = stream.overflowCount ?? stream.OverflowCount ?? 0;
      const overflowBytes = stream.overflowBytes ?? stream.OverflowBytes ?? 0;
      const isAligned = stream.isAligned ?? stream.IsAligned ?? false;

      card.innerHTML = `
        <div class="stream-header">
          <div class="stream-title">${escapeHtml(channelName)}</div>
          <span class="stream-status-badge ${statusClass}">${status}</span>
        </div>
        <div class="stream-stats">
          <div class="stat-item">
            <div class="stat-label">Buffer Size</div>
            <div class="stat-value">${formatBytes(bufferSize)}</div>
          </div>
          <div class="stat-item">
            <div class="stat-label">Total Written</div>
            <div class="stat-value">${formatBytes(totalWritten)}</div>
          </div>
          <div class="stat-item">
            <div class="stat-label">Total Read</div>
            <div class="stat-value">${formatBytes(totalRead)}</div>
          </div>
          <div class="stat-item">
            <div class="stat-label">Buffer Gap</div>
            <div class="stat-value">${formatBytes(currentGap)} (${gapPct.toFixed(1)}%)</div>
          </div>
          <div class="stat-item">
            <div class="stat-label">Overflows</div>
            <div class="stat-value">${overflowCount} (${formatBytes(overflowBytes)} lost)</div>
          </div>
          <div class="stat-item">
            <div class="stat-label">Keyframe Aligned</div>
            <div class="stat-value">${isAligned ? 'Yes' : 'No'}</div>
          </div>
        </div>
        <div>
          <div class="stat-label">Buffer Fill</div>
          <div class="progress-bar">
            <div class="progress-fill ${statusClass}" style="width: ${bufferFillPct}%"></div>
          </div>
        </div>
        <div class="stream-id">ID: ${escapeHtml(streamId)}</div>
        <div class="stream-actions">
          <button is="emby-button" type="button" class="raised button-cancel kill-stream-btn emby-button" data-stream-id="${escapeHtml(streamId)}">
            <span class="material-icons close"></span>
            <span>Kill Stream</span>
          </button>
        </div>
      `;

      // Add kill button handler
      const killBtn = card.querySelector('.kill-stream-btn');
      killBtn.addEventListener('click', () => killStream(streamId));

      return card;
    }

    function renderStreams(streams) {
      // Clear existing stream cards (but keep the no-streams message element)
      const existingCards = streamsContainer.querySelectorAll('.stream-card');
      existingCards.forEach(card => card.remove());

      if (!streams || streams.length === 0) {
        noStreamsMessage.style.display = 'block';
        streamCount.textContent = 'No active streams';
        killAllBtn.disabled = true;
        return;
      }

      noStreamsMessage.style.display = 'none';
      streamCount.textContent = `${streams.length} active stream(s)`;
      killAllBtn.disabled = false;

      streams.forEach(stream => {
        const card = createStreamCard(stream);
        streamsContainer.appendChild(card);
      });
    }

    function loadStreams() {
      Xtream.fetchJson('Xtream/ActiveStreams').then((streams) => {
        renderStreams(streams);
      }).catch((err) => {
        showStatus('Failed to load streams: ' + err.message, true);
      });
    }

    function killStream(streamId) {
      if (!confirm(`Are you sure you want to kill this stream?\n\nStream ID: ${streamId}`)) {
        return;
      }

      Xtream.apiRequest(`Xtream/ActiveStreams/${encodeURIComponent(streamId)}`, {
        method: 'DELETE'
      }).then((result) => {
        const success = result.success ?? result.Success;
        const message = result.message ?? result.Message;

        if (success) {
          showStatus(message, false);
          loadStreams();
        } else {
          showStatus(message || 'Failed to kill stream', true);
        }
      }).catch((err) => {
        showStatus('Failed to kill stream: ' + err.message, true);
      });
    }

    function killAllStreams() {
      if (!confirm('Are you sure you want to kill ALL active streams?\n\nThis will disconnect all current viewers.')) {
        return;
      }

      Xtream.apiRequest('Xtream/ActiveStreams', {
        method: 'DELETE'
      }).then((result) => {
        const success = result.success ?? result.Success;
        const message = result.message ?? result.Message;

        if (success) {
          showStatus(message, false);
          loadStreams();
        } else {
          showStatus(message || 'Failed to kill streams', true);
        }
      }).catch((err) => {
        showStatus('Failed to kill streams: ' + err.message, true);
      });
    }

    function escapeHtml(text) {
      if (!text) return '';
      const div = document.createElement('div');
      div.textContent = text;
      return div.innerHTML;
    }

    // Event handlers
    refreshBtn.addEventListener('click', loadStreams);
    killAllBtn.addEventListener('click', killAllStreams);

    // Initial load
    loadStreams();

    // Auto-refresh every 5 seconds
    autoRefreshInterval = setInterval(loadStreams, 5000);

    // Cleanup on page leave
    view.addEventListener('viewhide', () => {
      if (autoRefreshInterval) {
        clearInterval(autoRefreshInterval);
        autoRefreshInterval = null;
      }
    });
  }));
}
