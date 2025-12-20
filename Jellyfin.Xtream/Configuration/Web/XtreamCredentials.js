export default function (view) {
  view.addEventListener("viewshow", () => import(
    window.ApiClient.getUrl("web/ConfigurationPage", {
      name: "Xtream.js",
    })
  ).then((Xtream) => Xtream.default
  ).then((Xtream) => {
    const pluginId = Xtream.pluginConfig.UniqueId;
    Xtream.setTabs('XtreamCredentials');

    const fieldMappings = [
      ['#BaseUrl', 'BaseUrl', ''],
      ['#Username', 'Username', ''],
      ['#Password', 'Password', '']
    ];

    Dashboard.showLoadingMsg();
    ApiClient.getPluginConfiguration(pluginId).then(function (config) {
      Xtream.loadConfigFields(view, config, fieldMappings);
      Dashboard.hideLoadingMsg();
    });

    view.querySelector('#XtreamCredentialsForm').addEventListener('submit',
      Xtream.createFormSubmitHandler(pluginId, null, (config) => {
        Xtream.saveConfigFields(view, config, fieldMappings);
      })
    );

    // Connection Info refresh
    const loadConnectionInfo = () => {
      const contentDiv = view.querySelector('#ConnectionInfoContent');
      contentDiv.innerHTML = '<span style="color: #888;">Loading...</span>';

      Xtream.fetchJson('Xtream/ConnectionInfo')
        .then(data => {
          if (data.success) {
            const statusColor = data.status === 'Active' ? '#4caf50' : '#f44336';
            const usagePercent = data.maxConnections > 0
              ? Math.round((data.activeConnections / data.maxConnections) * 100)
              : 0;
            const usageColor = usagePercent >= 100 ? '#f44336' : (usagePercent >= 75 ? '#ff9800' : '#4caf50');

            let expDateStr = 'N/A';
            if (data.expDate) {
              const expDate = new Date(data.expDate);
              expDateStr = expDate.toLocaleDateString();
              const daysLeft = Math.ceil((expDate - new Date()) / (1000 * 60 * 60 * 24));
              if (daysLeft > 0) {
                expDateStr += ` (${daysLeft} days left)`;
              } else {
                expDateStr += ' (EXPIRED)';
              }
            }

            contentDiv.innerHTML = `
              <div style="display: grid; grid-template-columns: repeat(2, 1fr); gap: 8px; font-size: 0.9em;">
                <div><strong>Account:</strong> ${data.username || 'N/A'}${data.isTrial ? ' (Trial)' : ''}</div>
                <div><strong>Status:</strong> <span style="color: ${statusColor}; font-weight: bold;">${data.status}</span></div>
                <div><strong>Provider Connections:</strong> <span style="color: ${usageColor};">${data.activeConnections}/${data.maxConnections}</span></div>
                <div><strong>Plugin Active Streams:</strong> ${data.pluginActiveStreams}</div>
                <div style="grid-column: span 2;"><strong>Expires:</strong> ${expDateStr}</div>
              </div>
            `;
          } else {
            contentDiv.innerHTML = `<span style="color: #f44336;">Error: ${data.message || 'Failed to load'}</span>`;
          }
        })
        .catch(err => {
          contentDiv.innerHTML = `<span style="color: #f44336;">Error: ${err.message}</span>`;
        });
    };

    view.querySelector('#RefreshConnectionInfo').addEventListener('click', loadConnectionInfo);

    // Auto-load connection info on page load
    loadConnectionInfo();
  }));
}