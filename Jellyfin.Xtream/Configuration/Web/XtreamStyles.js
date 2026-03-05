/**
 * Xtream Plugin CSS Loader
 *
 * A lightweight CSS file loader that ensures styles are applied before any UI renders.
 * This follows the Tailwind approach - utility classes defined in a CSS file,
 * loaded synchronously at the earliest possible moment.
 *
 * Architecture:
 * - Injects a <link> element for the CSS file
 * - Returns a Promise that resolves when CSS is loaded
 * - Provides utility functions for common DOM operations
 * - No CSS-in-JS, all styles live in Xtream.css
 */

const CSS_LINK_ID = 'xtream-plugin-styles';

/**
 * Gets the URL for the CSS file through Jellyfin's configuration page API
 */
const getCssUrl = () => {
  return ApiClient
    ? ApiClient.getUrl('web/ConfigurationPage', { name: 'Xtream.css' })
    : '/web/ConfigurationPage?name=Xtream.css';
};

/**
 * Injects the CSS file link into the document head.
 * Uses <link> element for browser-native caching and parallel loading.
 * Returns a Promise that resolves when CSS is loaded.
 */
const loadStyles = () => {
  return new Promise((resolve, reject) => {
    // Check if already loaded
    if (document.getElementById(CSS_LINK_ID)) {
      resolve();
      return;
    }

    const link = document.createElement('link');
    link.id = CSS_LINK_ID;
    link.rel = 'stylesheet';
    link.type = 'text/css';
    link.href = getCssUrl();

    link.onload = () => {
      console.log('[Xtream] CSS loaded successfully');
      resolve();
    };

    link.onerror = (err) => {
      console.error('[Xtream] Failed to load CSS:', err);
      // Resolve anyway to not block the UI
      resolve();
    };

    // Insert at the beginning of head for highest priority
    const firstChild = document.head.firstChild;
    if (firstChild) {
      document.head.insertBefore(link, firstChild);
    } else {
      document.head.appendChild(link);
    }
  });
};

/**
 * Ensures styles are loaded synchronously by injecting immediately.
 * Call this at module load time for earliest possible injection.
 */
const ensureStylesSync = () => {
  if (document.getElementById(CSS_LINK_ID)) {
    return;
  }

  const link = document.createElement('link');
  link.id = CSS_LINK_ID;
  link.rel = 'stylesheet';
  link.type = 'text/css';
  link.href = getCssUrl();

  // Insert at the beginning of head for highest priority
  const firstChild = document.head.firstChild;
  if (firstChild) {
    document.head.insertBefore(link, firstChild);
  } else {
    document.head.appendChild(link);
  }
};

/**
 * Removes the CSS link (for cleanup if needed)
 */
const removeStyles = () => {
  const existing = document.getElementById(CSS_LINK_ID);
  if (existing) {
    existing.remove();
  }
};

// ============================================
// DOM Utility Functions
// ============================================

/**
 * Shows or hides an element.
 * For .toggle-content elements, toggles the .visible class.
 * For other elements, toggles the .hide class.
 * @param {HTMLElement} element - The element to show/hide
 * @param {boolean} visible - Whether to show (true) or hide (false)
 */
const setVisible = (element, visible) => {
  if (!element) return;
  // .toggle-content uses .visible class (display: none by default, display: block when .visible)
  if (element.classList.contains('toggle-content')) {
    element.classList.toggle('visible', visible);
  } else {
    // Other elements use .hide class
    element.classList.toggle('hide', !visible);
  }
};

/**
 * Toggles a class on an element
 * @param {HTMLElement} element - The element
 * @param {string} className - The class to toggle
 * @param {boolean} [force] - Optional force add/remove
 */
const toggleClass = (element, className, force) => {
  if (!element) return;
  element.classList.toggle(className, force);
};

/**
 * Creates a loading spinner element using CSS classes
 * @param {string} message - Loading message to display
 * @returns {HTMLElement} The loading overlay element
 */
const createLoadingSpinner = (message = 'Loading...') => {
  const container = document.createElement('div');
  container.className = 'loading-overlay';

  const spinner = document.createElement('div');
  spinner.className = 'loading-spinner';

  const text = document.createElement('span');
  text.textContent = message;

  container.appendChild(spinner);
  container.appendChild(text);

  return container;
};

/**
 * Creates an empty state element using CSS classes
 * @param {string} icon - Material icon name
 * @param {string} message - Message to display
 * @returns {HTMLElement} The empty state element
 */
const createEmptyState = (icon, message) => {
  const container = document.createElement('div');
  container.className = 'empty-state';

  const iconEl = document.createElement('span');
  iconEl.className = 'material-icons';
  iconEl.textContent = icon;

  const text = document.createElement('p');
  text.textContent = message;

  container.appendChild(iconEl);
  container.appendChild(text);

  return container;
};

/**
 * Creates an error state element using CSS classes
 * @param {string} message - Error message to display
 * @returns {HTMLElement} The error state element
 */
const createErrorState = (message) => createEmptyState('error', message);

/**
 * Creates a no-results element using CSS classes
 * @param {string} message - Message to display
 * @returns {HTMLElement} The no-results element
 */
const createNoResults = (message = 'No results found') => {
  const container = document.createElement('div');
  container.className = 'no-results';

  const iconEl = document.createElement('span');
  iconEl.className = 'material-icons';
  iconEl.textContent = 'search_off';

  const text = document.createElement('p');
  text.textContent = message;

  container.appendChild(iconEl);
  container.appendChild(text);

  return container;
};

// ============================================
// Auto-inject CSS at module load time
// ============================================
ensureStylesSync();

// ============================================
// Exports
// ============================================

export default {
  loadStyles,
  ensureStylesSync,
  removeStyles,
  setVisible,
  toggleClass,
  createLoadingSpinner,
  createEmptyState,
  createErrorState,
  createNoResults,
};

export {
  loadStyles,
  ensureStylesSync,
  removeStyles,
  setVisible,
  toggleClass,
  createLoadingSpinner,
  createEmptyState,
  createErrorState,
  createNoResults,
};
