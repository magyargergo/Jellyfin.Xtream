export default function (view) {
  view.addEventListener("viewshow", () => Promise.all([
    import(ApiClient.getUrl("web/ConfigurationPage", { name: "Xtream.js" })),
    import(ApiClient.getUrl("web/ConfigurationPage", { name: "XtreamStyles.js" }))
  ]).then(([XtreamModule, StylesModule]) => {
    const Xtream = XtreamModule.default;
    const XtreamStyles = StylesModule.default;

    // CSS is auto-loaded by XtreamStyles module
    Xtream.setTabs('XtreamEpgTest');

    const channelSelect = view.querySelector('#ChannelSelect');
    const loadBtn = view.querySelector('#LoadEpgBtn');
    const refreshAllBtn = view.querySelector('#RefreshAllEpgBtn');
    const refreshStatus = view.querySelector('#RefreshStatus');
    const resultsDiv = view.querySelector('#EpgResults');
    const timelineDiv = view.querySelector('#EpgTimeline');
    const resultsTitle = view.querySelector('#ResultsTitle');
    const programCountEl = view.querySelector('#ProgramCount');
    const providerInfoEl = view.querySelector('#ProviderInfo');
    const providerNameEl = view.querySelector('#EpgProviderName');
    const providerStatusEl = view.querySelector('#EpgProviderStatus');
    const providerStatusCard = view.querySelector('#ProviderStatusCard');

    // Load provider status
    Xtream.fetchJson('Xtream/EpgStatus').then((status) => {
      providerNameEl.textContent = status.providerName || 'Unknown Provider';

      providerStatusEl.className = 'status-badge ' + (status.isAvailable ? 'available' : 'unavailable');
      providerStatusEl.innerHTML = `
        <span class="material-icons" style="font-size: 14px;">${status.isAvailable ? 'check_circle' : 'error'}</span>
        <span>${status.isAvailable ? 'Available' : 'Unavailable'}</span>
      `;

      if (status.isAvailable) {
        providerStatusCard.style.borderLeftColor = '#4caf50';
      } else {
        providerStatusCard.style.borderLeftColor = '#f44336';
      }
    }).catch(() => {
      providerNameEl.textContent = 'Unknown Provider';
      providerStatusEl.className = 'status-badge unavailable';
      providerStatusEl.innerHTML = `
        <span class="material-icons" style="font-size: 14px;">error</span>
        <span>Error</span>
      `;
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
      XtreamStyles.setVisible(resultsDiv, false);
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
        XtreamStyles.setVisible(resultsDiv, true);

        // Handle both PascalCase and camelCase property names
        const success = result.Success ?? result.success;
        const errorMessage = result.ErrorMessage ?? result.errorMessage;
        const channelName = result.ChannelName ?? result.channelName;
        const provider = result.Provider ?? result.provider;
        const programs = result.Programs ?? result.programs ?? [];
        const resultStreamId = result.StreamId ?? result.streamId;

        if (!success) {
          timelineDiv.innerHTML = `<div class="empty-state" style="text-align:center;padding:50px 40px;color:#888;"><span class="material-icons" style="font-size:48px;opacity:0.5;display:block;margin-bottom:12px;">error</span><p>${errorMessage || 'Unknown error'}</p></div>`;
          programCountEl.textContent = '0 programs';
          providerInfoEl.textContent = '-';
          return;
        }

        resultsTitle.textContent = channelName || 'Program Schedule';
        programCountEl.textContent = `${programs.length} programs`;
        providerInfoEl.textContent = `${provider} (ID: ${resultStreamId})`;

        if (programs.length === 0) {
          timelineDiv.innerHTML = '<div class="empty-state" style="text-align:center;padding:50px 40px;color:#888;"><span class="material-icons" style="font-size:48px;opacity:0.5;display:block;margin-bottom:12px;">event_busy</span><p>No EPG data available for this channel</p></div>';
          return;
        }

        const now = new Date();
        timelineDiv.innerHTML = programs.map((program) => {
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
            <div class="epg-program-card" style="${cardStyle}">
              <div class="program-title" style="font-weight:600;font-size:15px;margin-bottom:6px;">${escapeHtml(title || 'No Title')}</div>
              <div class="program-time" style="color:#888;font-size:13px;">
                ${formatTime(start)} - ${formatTime(end)}${durationStr}
              </div>
              ${description ? `<div class="program-description" style="color:#aaa;font-size:13px;margin-top:8px;line-height:1.4;">${escapeHtml(description)}</div>` : ''}
            </div>
          `;
        }).join('');
      }).catch((err) => {
        Dashboard.hideLoadingMsg();
        loadBtn.disabled = false;
        resultsDiv.style.display = 'block';
        timelineDiv.innerHTML = `<div class="empty-state" style="text-align:center;padding:50px 40px;color:#888;"><span class="material-icons" style="font-size:48px;opacity:0.5;display:block;margin-bottom:12px;">error</span><p>Failed to load EPG: ${err.message}</p></div>`;
        programCountEl.textContent = '0 programs';
        providerInfoEl.textContent = '-';
      });
    });

    // Refresh all EPG data in parallel
    refreshAllBtn.addEventListener('click', () => {
      refreshAllBtn.disabled = true;
      refreshStatus.style.display = 'block';
      refreshStatus.style.cssText = 'display:block;padding:12px 16px;border-radius:8px;background:rgba(255,152,0,0.15);color:#ffb74d;';
      refreshStatus.textContent = 'Refreshing EPG data for all channels... This may take a while.';

      Xtream.fetchJson('Xtream/RefreshEpg', { method: 'POST' }).then((result) => {
        refreshAllBtn.disabled = false;
        const success = result.success ?? result.Success;
        const message = result.message ?? result.Message;

        if (success) {
          refreshStatus.style.cssText = 'display:block;padding:12px 16px;border-radius:8px;background:rgba(76,175,80,0.15);color:#81c784;';
          refreshStatus.textContent = message;
        } else {
          refreshStatus.style.cssText = 'display:block;padding:12px 16px;border-radius:8px;background:rgba(244,67,54,0.15);color:#e57373;';
          refreshStatus.textContent = message || 'Refresh failed';
        }

        // Hide status after 10 seconds
        setTimeout(() => {
          refreshStatus.style.display = 'none';
        }, 10000);
      }).catch((err) => {
        refreshAllBtn.disabled = false;
        refreshStatus.style.cssText = 'display:block;padding:12px 16px;border-radius:8px;background:rgba(244,67,54,0.15);color:#e57373;';
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
