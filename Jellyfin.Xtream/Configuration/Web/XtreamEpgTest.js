export default function (view) {
  view.addEventListener("viewshow", () => import(
    ApiClient.getUrl("web/ConfigurationPage", {
      name: "Xtream.js",
    })
  ).then((Xtream) => Xtream.default
  ).then((Xtream) => {
    Xtream.setTabs('XtreamEpgTest');

    const channelSelect = view.querySelector('#ChannelSelect');
    const loadBtn = view.querySelector('#LoadEpgBtn');
    const refreshAllBtn = view.querySelector('#RefreshAllEpgBtn');
    const refreshStatus = view.querySelector('#RefreshStatus');
    const resultsDiv = view.querySelector('#EpgResults');
    const timelineDiv = view.querySelector('#EpgTimeline');
    const summaryDiv = view.querySelector('#EpgSummary');
    const resultsTitle = view.querySelector('#ResultsTitle');
    const providerStatus = view.querySelector('#EpgProviderStatus');

    // Load provider status
    Xtream.fetchJson('Xtream/EpgStatus').then((status) => {
      providerStatus.querySelector('.provider-name').textContent = `Provider: ${status.providerName}`;
      const availability = providerStatus.querySelector('.provider-availability');
      availability.textContent = status.isAvailable ? 'Available' : 'Unavailable';
      availability.classList.add(status.isAvailable ? 'available' : 'unavailable');
    }).catch(() => {
      providerStatus.querySelector('.provider-name').textContent = 'Provider: Unknown';
    });

    // Load channels
    Dashboard.showLoadingMsg();
    Xtream.fetchJson('Xtream/LiveTv').then((channels) => {
      channels.forEach((channel) => {
        const option = document.createElement('option');
        option.value = channel.Id;
        option.textContent = channel.Number ? `${channel.Number}. ${channel.Name}` : channel.Name;
        channelSelect.appendChild(option);
      });
      Dashboard.hideLoadingMsg();
    }).catch((err) => {
      Dashboard.hideLoadingMsg();
      Dashboard.alert('Failed to load channels: ' + err.message);
    });

    // Enable button when channel selected
    channelSelect.addEventListener('change', () => {
      loadBtn.disabled = !channelSelect.value;
      resultsDiv.style.display = 'none';
    });

    // Load EPG data
    loadBtn.addEventListener('click', () => {
      const streamId = channelSelect.value;
      if (!streamId) return;

      Dashboard.showLoadingMsg();
      loadBtn.disabled = true;

      Xtream.fetchJson(`Xtream/EpgTest/${streamId}`).then((result) => {
        Dashboard.hideLoadingMsg();
        loadBtn.disabled = false;
        resultsDiv.style.display = 'block';

        // Handle both PascalCase and camelCase property names
        const success = result.Success ?? result.success;
        const errorMessage = result.ErrorMessage ?? result.errorMessage;
        const channelName = result.ChannelName ?? result.channelName;
        const provider = result.Provider ?? result.provider;
        const programs = result.Programs ?? result.programs ?? [];
        const resultStreamId = result.StreamId ?? result.streamId;

        if (!success) {
          timelineDiv.innerHTML = `<div class="epg-error">Error: ${errorMessage || 'Unknown error'}</div>`;
          summaryDiv.innerHTML = '';
          return;
        }

        resultsTitle.textContent = `Program Schedule - ${channelName}`;

        if (programs.length === 0) {
          timelineDiv.innerHTML = '<div class="epg-no-data">No EPG data available for this channel</div>';
          summaryDiv.innerHTML = `<strong>Provider:</strong> ${provider} | <strong>Programs:</strong> 0`;
          return;
        }

        summaryDiv.innerHTML = `
          <strong>Provider:</strong> ${provider} |
          <strong>Programs:</strong> ${programs.length} |
          <strong>Channel ID:</strong> ${resultStreamId}
        `;

        const now = new Date();
        timelineDiv.innerHTML = programs.map((program) => {
          const start = new Date(program.StartUtc ?? program.startUtc);
          const end = new Date(program.EndUtc ?? program.endUtc);

          let statusClass = '';
          if (end < now) {
            statusClass = 'past';
          } else if (start <= now && end >= now) {
            statusClass = 'current';
          }

          const formatTime = (date) => {
            if (!date || date.getFullYear() < 2000) return 'N/A';
            return date.toLocaleString(undefined, {
              weekday: 'short',
              month: 'short',
              day: 'numeric',
              hour: '2-digit',
              minute: '2-digit'
            });
          };

          const duration = (end - start) / 60000; // minutes
          const durationStr = duration > 0 ? ` (${Math.round(duration)} min)` : '';

          const title = program.Title ?? program.title;
          const description = program.Description ?? program.description;

          return `
            <div class="epg-program ${statusClass}">
              <div class="epg-program-title">${escapeHtml(title || 'No Title')}</div>
              <div class="epg-program-time">
                ${formatTime(start)} - ${formatTime(end)}${durationStr}
              </div>
              ${description ? `<div class="epg-program-description">${escapeHtml(description)}</div>` : ''}
            </div>
          `;
        }).join('');
      }).catch((err) => {
        Dashboard.hideLoadingMsg();
        loadBtn.disabled = false;
        resultsDiv.style.display = 'block';
        timelineDiv.innerHTML = `<div class="epg-error">Failed to load EPG: ${err.message}</div>`;
        summaryDiv.innerHTML = '';
      });
    });

    // Refresh all EPG data in parallel
    refreshAllBtn.addEventListener('click', () => {
      refreshAllBtn.disabled = true;
      refreshStatus.style.display = 'block';
      refreshStatus.className = 'refresh-status loading';
      refreshStatus.textContent = 'Refreshing EPG data for all channels... This may take a while.';

      Xtream.fetchJson('Xtream/RefreshEpg', { method: 'POST' }).then((result) => {
        refreshAllBtn.disabled = false;
        const success = result.success ?? result.Success;
        const message = result.message ?? result.Message;

        if (success) {
          refreshStatus.className = 'refresh-status success';
          refreshStatus.textContent = message;
        } else {
          refreshStatus.className = 'refresh-status error';
          refreshStatus.textContent = message || 'Refresh failed';
        }

        // Hide status after 10 seconds
        setTimeout(() => {
          refreshStatus.style.display = 'none';
        }, 10000);
      }).catch((err) => {
        refreshAllBtn.disabled = false;
        refreshStatus.className = 'refresh-status error';
        refreshStatus.textContent = `Failed to refresh EPG: ${err.message}`;
      });
    });

    function escapeHtml(text) {
      const div = document.createElement('div');
      div.textContent = text;
      return div.innerHTML;
    }
  }));
}
