export default function (view) {
  view.addEventListener("viewshow", () => import(
    window.ApiClient.getUrl("web/ConfigurationPage", {
      name: "Xtream.js",
    })
  ).then((Xtream) => Xtream.default
  ).then((Xtream) => {
    const pluginId = Xtream.pluginConfig.UniqueId;
    Xtream.setTabs('XtreamMigration');

    // Elements
    const step1 = view.querySelector('#MigrationStep1');
    const stepNoLegacy = view.querySelector('#MigrationStepNoLegacy');
    const stepResult = view.querySelector('#MigrationStepResult');
    const loadingStep = view.querySelector('#MigrationLoading');
    const legacyConfigDetails = view.querySelector('#LegacyConfigDetails');
    const legacyConfigInfo = view.querySelector('#LegacyConfigInfo');
    const migrationSuccess = view.querySelector('#MigrationSuccess');
    const migrationError = view.querySelector('#MigrationError');
    const migratedProviderInfo = view.querySelector('#MigratedProviderInfo');
    const migrationErrorMessage = view.querySelector('#MigrationErrorMessage');

    // Helper to show/hide steps
    const showStep = (stepElement) => {
      [step1, stepNoLegacy, stepResult, loadingStep].forEach(el => {
        el.classList.add('hide');
      });
      stepElement.classList.remove('hide');
    };

    // Navigate to providers page
    const goToProviders = () => {
      Dashboard.navigate('/configurationpage?name=XtreamProviders.html');
    };

    // Check for legacy configuration
    const checkLegacyConfig = async () => {
      showStep(loadingStep);

      try {
        const result = await Xtream.apiRequest('Xtream/LegacyConfig');
        console.log('Legacy config check result:', result);

        if (result.hasLegacyConfig) {
          // Show legacy config details
          legacyConfigDetails.innerHTML = '';

          if (result.baseUrl) {
            const li = document.createElement('li');
            li.textContent = `Server: ${result.baseUrl}`;
            legacyConfigDetails.appendChild(li);
          }

          if (result.username) {
            const li = document.createElement('li');
            li.textContent = `Username: ${result.username}`;
            legacyConfigDetails.appendChild(li);
          }

          if (result.liveTvCount > 0) {
            const li = document.createElement('li');
            li.textContent = `Live TV categories: ${result.liveTvCount}`;
            legacyConfigDetails.appendChild(li);
          }

          if (result.vodCount > 0) {
            const li = document.createElement('li');
            li.textContent = `VOD categories: ${result.vodCount}`;
            legacyConfigDetails.appendChild(li);
          }

          if (result.seriesCount > 0) {
            const li = document.createElement('li');
            li.textContent = `Series categories: ${result.seriesCount}`;
            legacyConfigDetails.appendChild(li);
          }

          if (legacyConfigDetails.children.length === 0) {
            legacyConfigInfo.classList.add('hide');
          }

          showStep(step1);
        } else {
          // Check if there are any providers already configured
          const config = await ApiClient.getPluginConfiguration(pluginId);
          if (config.Providers && config.Providers.length > 0) {
            // Already has providers, go directly to providers page
            goToProviders();
          } else {
            // No legacy config, no providers - show setup
            showStep(stepNoLegacy);
          }
        }
      } catch (err) {
        console.error('Failed to check legacy config:', err);
        showStep(stepNoLegacy);
      }
    };

    // Perform migration
    const performMigration = async () => {
      showStep(loadingStep);

      try {
        const result = await Xtream.apiRequest('Xtream/MigrateLegacy', { method: 'POST' });
        console.log('Migration result:', result);

        if (result.success) {
          migratedProviderInfo.innerHTML = `
            <p style="margin: 0;"><strong>Provider Created:</strong> ${result.providerName}</p>
            <p style="margin: 8px 0 0 0; font-size: 0.9em; color: #888;">ID: ${result.providerId}</p>
          `;
          migrationSuccess.classList.remove('hide');
          migrationError.classList.add('hide');
          showStep(stepResult);
        } else {
          migrationErrorMessage.textContent = result.message || 'Migration failed for unknown reason.';
          migrationSuccess.classList.add('hide');
          migrationError.classList.remove('hide');
          showStep(stepResult);
        }
      } catch (err) {
        console.error('Migration failed:', err);
        migrationErrorMessage.textContent = err.message || 'An unexpected error occurred.';
        migrationSuccess.classList.add('hide');
        migrationError.classList.remove('hide');
        showStep(stepResult);
      }
    };

    // Event handlers
    view.querySelector('#MigrateBtn').addEventListener('click', performMigration);
    view.querySelector('#SkipMigrationBtn').addEventListener('click', goToProviders);
    view.querySelector('#GoToProvidersBtn').addEventListener('click', goToProviders);
    view.querySelector('#GoToProvidersAfterMigrationBtn').addEventListener('click', goToProviders);
    view.querySelector('#RetryMigrationBtn').addEventListener('click', performMigration);
    view.querySelector('#SkipAfterErrorBtn').addEventListener('click', goToProviders);

    // Initial check
    checkLegacyConfig();
  }));
}
