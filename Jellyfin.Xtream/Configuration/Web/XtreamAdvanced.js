export default function (view) {
  view.addEventListener("viewshow", () => Promise.all([
    import(window.ApiClient.getUrl("web/ConfigurationPage", { name: "Xtream.js" })),
    import(window.ApiClient.getUrl("web/ConfigurationPage", { name: "XtreamStyles.js" }))
  ]).then(([XtreamModule, StylesModule]) => {
    const Xtream = XtreamModule.default;
    const XtreamStyles = StylesModule.default;

    // CSS is auto-loaded by XtreamStyles module
    const pluginId = Xtream.pluginConfig.UniqueId;
    Xtream.setTabs('XtreamAdvanced');

    // Element references for networking section
    const enableProxyCheckbox = view.querySelector('#EnableProxy');
    const proxySettings = view.querySelector('#ProxySettings');
    const enableUserAgentRotationCheckbox = view.querySelector('#EnableUserAgentRotation');
    const userAgentRotationSettings = view.querySelector('#UserAgentRotationSettings');
    const enableRateLimitingCheckbox = view.querySelector('#EnableRateLimiting');
    const rateLimitingSettings = view.querySelector('#RateLimitingSettings');
    const enforceConnectionLimitCheckbox = view.querySelector('#EnforceConnectionLimit');
    const connectionLimitSettings = view.querySelector('#ConnectionLimitSettings');

    // Element references for external EPG section
    const enableExternalEpgCheckbox = view.querySelector('#EnableExternalEpg');
    const externalEpgSettings = view.querySelector('#ExternalEpgSettings');

    // Toggle function using styles system for visibility
    const createToggle = (checkbox, element) => () => {
      XtreamStyles.setVisible(element, checkbox.checked);
    };

    // Toggle functions
    const toggleProxySettings = createToggle(enableProxyCheckbox, proxySettings);
    const toggleUserAgentRotationSettings = createToggle(enableUserAgentRotationCheckbox, userAgentRotationSettings);
    const toggleRateLimitingSettings = createToggle(enableRateLimitingCheckbox, rateLimitingSettings);
    const toggleConnectionLimitSettings = createToggle(enforceConnectionLimitCheckbox, connectionLimitSettings);
    const toggleExternalEpgSettings = createToggle(enableExternalEpgCheckbox, externalEpgSettings);

    // Add event listeners for toggles
    enableProxyCheckbox.addEventListener('change', toggleProxySettings);
    enableUserAgentRotationCheckbox.addEventListener('change', toggleUserAgentRotationSettings);
    enableRateLimitingCheckbox.addEventListener('change', toggleRateLimitingSettings);
    enforceConnectionLimitCheckbox.addEventListener('change', toggleConnectionLimitSettings);
    enableExternalEpgCheckbox.addEventListener('change', toggleExternalEpgSettings);

    // Setup collapsible section headers
    view.querySelectorAll('.settings-section-header').forEach(header => {
      const sectionName = header.dataset.section;
      const content = view.querySelector(`.settings-section-content[data-section="${sectionName}"]`);
      const icon = header.querySelector('.material-icons:last-child');

      if (content && icon) {
        let isExpanded = true;

        header.addEventListener('click', () => {
          isExpanded = !isExpanded;
          content.style.display = isExpanded ? 'block' : 'none';
          icon.textContent = isExpanded ? 'expand_less' : 'expand_more';
        });
      }
    });

    // Combined field mappings
    const fieldMappings = [
      // Networking fields
      ['#EnableProxy', 'EnableProxy', false, true],
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

      // Connection limit fields
      ['#EnforceConnectionLimit', 'EnforceConnectionLimit', false, true],
      ['#MaxConcurrentStreams', 'MaxConcurrentStreams', 0, false, (v) => parseInt(v) || 0],
      ['#AutoKillOldestStream', 'AutoKillOldestStream', true, true],

      // External EPG fields
      ['#EnableExternalEpg', 'EnableExternalEpg', false, true],
      ['#ExternalEpgUrl', 'ExternalEpgUrl', ''],
      ['#ExternalEpgLogoBaseUrl', 'ExternalEpgLogoBaseUrl', ''],
      ['#UseExternalLogoFallback', 'UseExternalLogoFallback', true, true],

      // Logging fields
      ['#EnableDebugLogging', 'EnableDebugLogging', false, true],

      // Discord fields
      ['#EnableDiscordNotifications', 'EnableDiscordNotifications', false, true],
      ['#DiscordWebhookUrl', 'DiscordWebhookUrl', ''],
      ['#NotifyOnBufferOverflow', 'NotifyOnBufferOverflow', true, true],
      ['#NotifyOnStreamStart', 'NotifyOnStreamStart', false, true],
      ['#NotifyOnStreamError', 'NotifyOnStreamError', true, true],
      ['#NotifyOnStreamKilled', 'NotifyOnStreamKilled', true, true],
      ['#NotifyOnStreamQualityViolation', 'NotifyOnStreamQualityViolation', true, true],
      ['#NotifyOnEpgRefresh', 'NotifyOnEpgRefresh', true, true],
      ['#NotifyOnConnectionLimitChange', 'NotifyOnConnectionLimitChange', true, true]
    ];

    // Load configuration
    Dashboard.showLoadingMsg();
    ApiClient.getPluginConfiguration(pluginId).then(function (config) {
      Xtream.loadConfigFields(view, config, fieldMappings);

      // Initialize toggle states
      toggleProxySettings();
      toggleUserAgentRotationSettings();
      toggleRateLimitingSettings();
      toggleConnectionLimitSettings();
      toggleExternalEpgSettings();

      Dashboard.hideLoadingMsg();
    });

    // Form submission handler
    view.querySelector('#XtreamAdvancedForm').addEventListener('submit',
      Xtream.createFormSubmitHandler(
        pluginId,
        () => {
          // Validation for proxy settings
          if (enableProxyCheckbox.checked) {
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

          // Clean up proxy address (remove protocol if present)
          let proxyAddress = config.ProxyAddress;
          if (proxyAddress) {
            proxyAddress = proxyAddress.replace(/^https?:\/\//, '').replace(/\/$/, '');
            config.ProxyAddress = proxyAddress;
          }

          // Trim custom user agent
          if (config.CustomUserAgent) {
            config.CustomUserAgent = config.CustomUserAgent.trim();
          }

          // Refresh Discord service to update health report timer after saving
          Xtream.apiPost('Xtream/RefreshDiscordConfiguration')
            .catch(err => console.error('Failed to refresh Discord configuration:', err));
        }
      )
    );

    // Discord Test Webhook Button
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

    // Send Buffer Diagnostics Button
    view.querySelector('#sendBufferDiagnosticsButton').addEventListener('click', function () {
      Dashboard.showLoadingMsg();

      Xtream.apiPost('Xtream/SendBufferDiagnostics')
        .then(data => {
          Dashboard.hideLoadingMsg();

          if (data.success) {
            if (data.count > 0) {
              Dashboard.alert(`Buffer diagnostics sent successfully!\n\nSent diagnostics for ${data.count} active stream(s).\nCheck your Discord channel.`);
            } else {
              Dashboard.alert('No active streams found.\n\nStart watching a channel first, then try again.');
            }
          } else {
            Dashboard.alert('Failed to send diagnostics: ' + (data.message || 'Unknown error'));
          }
        })
        .catch(error => {
          Dashboard.hideLoadingMsg();
          Dashboard.alert('Error sending buffer diagnostics: ' + error.message);
        });
    });
  }));
}
