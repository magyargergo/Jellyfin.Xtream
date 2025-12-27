/**
 * Xtream Plugin - Main Module
 *
 * This module provides shared utilities and UI components for all Xtream plugin pages.
 * Styles are loaded from Xtream.css - CSS is injected by XtreamStyles.js which is
 * loaded in parallel by each page's JS file.
 */

// Import XtreamStyles for use in this module
// This is loaded dynamically to work with Jellyfin's plugin system
let XtreamStyles = null;
const loadXtreamStyles = async () => {
  if (XtreamStyles) return XtreamStyles;
  const module = await import(ApiClient.getUrl('web/ConfigurationPage', { name: 'XtreamStyles.js' }));
  XtreamStyles = module.default;
  return XtreamStyles;
};
// Eagerly load styles when module loads
loadXtreamStyles();

const url = (name) =>
  ApiClient.getUrl("configurationpage", {
    name,
  });
const tab = (name) => '/configurationpage?name=' + name + '.html';

const htmlExpand = document.createElement('span');
htmlExpand.ariaHidden = true;
htmlExpand.classList.add('material-icons', 'expand_more');

// Helper to create DOM elements with properties
const createElement = (tag, props = {}, children = []) => {
  const elem = document.createElement(tag);
  Object.entries(props).forEach(([key, value]) => {
    if (key === 'classList') {
      elem.classList.add(...(Array.isArray(value) ? value : [value]));
    } else if (key === 'dataset') {
      Object.assign(elem.dataset, value);
    } else {
      elem[key] = value;
    }
  });
  children.forEach(child => {
    elem.appendChild(typeof child === 'string' ? document.createTextNode(child) : child);
  });
  return elem;
};

const createItemRow = (item, state, update) => {
  const tr = createElement('tr', { dataset: { itemId: item.Id } });
  
  const checkbox = createElement('input', { type: 'checkbox', checked: state, onchange: update });
  tr.appendChild(createElement('td', {}, [checkbox]));
  
  tr.appendChild(createElement('td', {}, [createElement('label', { innerText: item.Name })]));

  const catchupTd = document.createElement('td');
  if (item.HasCatchup) {
    catchupTd.title = `Catch-up supported for ${item.CatchupDuration} days.`;
    catchupTd.appendChild(createElement('span', { innerText: item.CatchupDuration }));
    catchupTd.appendChild(createElement('span', { ariaHidden: true, classList: ['material-icons', 'timer'] }));
  }
  tr.appendChild(catchupTd);

  return tr;
}

const populateItemsTable = (wrapper, table, items) => {
  for (let i = 0; i < items.length; ++i) {
    const item = items[i];
    const state = wrapper.live !== undefined && (wrapper.live.length === 0 || wrapper.live.includes(item.Id));
    const row = createItemRow(item, state, (e) => {
      let live = wrapper.live;
      if (e.target.checked) {
        live ??= [];
        live.push(item.Id);
        if (items.every(s => live.includes(s.Id))) {
          live = [];
        }
      } else {
        if (live.length === 0) {
          live = items.map(s => s.Id);
        }
        live = live.filter(id => id != item.Id);
        if (live.length === 0) {
          live = undefined;
        }
      }
      wrapper.live = live;
    });
    table.appendChild(row);
  }
}

const setCheckboxState = (checkbox, live) => {
  checkbox.indeterminate = live !== undefined && live.length > 0;
  checkbox.checked = live !== undefined && live.length === 0;
}

const createCategoryRow = (wrapper, category, loadItems) => {
  const tr = createElement('tr', { dataset: { categoryId: category.Id } });
  
  const checkbox = createElement('input', { type: 'checkbox' });
  setCheckboxState(checkbox, wrapper.live);
  
  const onchange = () => {
    wrapper.live = checkbox.checked ? [] : undefined;
  };
  checkbox.onchange = onchange;
  tr.appendChild(createElement('td', {}, [checkbox]));

  const _wrapper = {
    get live() { return wrapper.live; },
    set live(value) {
      wrapper.live = value;
      setCheckboxState(checkbox, wrapper.live);
    },
  }

  tr.appendChild(createElement('td', { innerHTML: category.Name }));

  const td = document.createElement('td');
  const expand = createElement('button', { 
    type: 'button', 
    classList: 'paper-icon-button-light' 
  }, [htmlExpand.cloneNode(true)]);
  
  expand.onclick = (e) => {
    e.preventDefault();
    const originalClick = expand.onclick;

    Dashboard.showLoadingMsg();
    expand.firstElementChild.classList.replace('expand_more', 'expand_less');
    const table = document.createElement('table');
    loadItems(category.Id).then((items) => {
      populateItemsTable(_wrapper, table, items);
      Dashboard.hideLoadingMsg();
    });
    checkbox.onchange = () => {
      onchange();
      table.querySelectorAll('input[type="checkbox"]').forEach((c) => c.checked = checkbox.checked);
    };
    td.appendChild(table);

    expand.onclick = () => {
      expand.onclick = originalClick;

      Dashboard.showLoadingMsg();
      expand.firstElementChild.classList.replace('expand_less', 'expand_more');
      td.removeChild(table);
      Dashboard.hideLoadingMsg();
    };
  };
  td.appendChild(expand);
  tr.appendChild(td);

  return tr;
};

const populateCategoriesTable = (table, loadConfig, loadCategories, loadItems) => {
  Dashboard.showLoadingMsg();
  const fetchConfig = loadConfig();
  const fetchCategories = loadCategories();

  return Promise.all([fetchConfig, fetchCategories])
    .then(([config, categories]) => {
      const data = config;
      for (let i = 0; i < categories.length; ++i) {
        const category = categories[i];
        const wrapper = {
          get live() { return data[category.Id]; },
          set live(value) {
            data[category.Id] = value;
          },
        }
        const elem = createCategoryRow(wrapper, category, loadItems);
        table.appendChild(elem);
      }
      Dashboard.hideLoadingMsg();
      return data;
    });
}

const fetchJson = (url, options = {}) => ApiClient.fetch({
  dataType: 'json',
  type: options.method || 'GET',
  url: ApiClient.getUrl(url),
  ...options,
});

const filter = (obj, predicate) => Object.keys(obj)
  .filter(key => predicate(obj[key]))
  .reduce((res, key) => (res[key] = obj[key], res), {});

const tabs = [
  {
    href: tab('XtreamMigration'),
    name: 'Setup'
  },
  {
    href: tab('XtreamProviders'),
    name: 'Providers'
  },
  {
    href: tab('XtreamAdvanced'),
    name: 'Advanced Settings'
  },
  {
    href: tab('XtreamLive'),
    name: 'Live TV'
  },
  {
    href: tab('XtreamLiveOverrides'),
    name: 'TV overrides'
  },
  {
    href: tab('XtreamVod'),
    name: 'Video On-Demand',
  },
  {
    href: tab('XtreamSeries'),
    name: 'Series',
  },
  {
    href: tab('XtreamEpgTest'),
    name: 'EPG Test',
  },
  {
    href: tab('XtreamStreams'),
    name: 'Active Streams',
  },
  {
    href: tab('XtreamMonitor'),
    name: 'Monitor',
  },
  {
    href: tab('XtreamLogs'),
    name: 'Logs',
  },
];

const setTabs = (pageName) => {
  // If pageName is a number (legacy), use it directly as index
  if (typeof pageName === 'number') {
    const name = tabs[pageName].name;
    LibraryMenu.setTabs(name, pageName, () => tabs);
    return;
  }
  
  // Find the tab index based on the page name
  const index = tabs.findIndex(tab => tab.href.includes(pageName));
  if (index !== -1) {
    LibraryMenu.setTabs(tabs[index].name, index, () => tabs);
  }
}

const pluginConfig = {
  UniqueId: '5d774c35-8567-46d3-a950-9bb8227a0c5d'
};

// Helper to create a toggle visibility function
const createToggleFn = (checkbox, element) => () => {
  element.style.display = checkbox.checked ? 'block' : 'none';
};

// Helper to load config fields from configuration
const loadConfigFields = (view, config, fieldMappings) => {
  for (const [selector, configKey, defaultValue, isCheckbox] of fieldMappings) {
    const element = view.querySelector(selector);
    if (element) {
      if (isCheckbox) {
        element.checked = defaultValue !== undefined ? 
          (config[configKey] ?? defaultValue) : 
          (config[configKey] || false);
      } else {
        element.value = config[configKey] ?? defaultValue ?? '';
      }
    }
  }
};

// Helper to save config fields to configuration
const saveConfigFields = (view, config, fieldMappings) => {
  for (const [selector, configKey, _, isCheckbox, parser] of fieldMappings) {
    const element = view.querySelector(selector);
    if (element) {
      if (isCheckbox) {
        config[configKey] = element.checked;
      } else {
        const value = parser ? parser(element.value) : element.value;
        config[configKey] = value;
      }
    }
  }
};

// Helper to create form submit handler
const createFormSubmitHandler = (pluginId, onBeforeSave, onAfterSave) => (e) => {
  e.preventDefault();
  
  // Optional validation callback
  if (onBeforeSave && onBeforeSave() === false) {
    return false;
  }
  
  Dashboard.showLoadingMsg();
  
  ApiClient.getPluginConfiguration(pluginId).then((config) => {
    // Save configuration callback
    if (onAfterSave) {
      onAfterSave(config);
    }
    
    ApiClient.updatePluginConfiguration(pluginId, config).then((result) => {
      Dashboard.processPluginConfigurationUpdateResult(result);
    });
  });
  
  return false;
};

// Helper to perform API requests with any HTTP method
// Options: method, headers, body, timeout (in ms, default none)
const apiRequest = (endpoint, options = {}) => {
  const url = ApiClient.getUrl(endpoint);

  // Set up abort controller for timeout if specified
  const controller = new AbortController();
  let timeoutId = null;
  if (options.timeout) {
    timeoutId = setTimeout(() => controller.abort(), options.timeout);
  }

  return fetch(url, {
    method: options.method || 'GET',
    headers: {
      'X-Emby-Token': ApiClient.accessToken(),
      'Content-Type': 'application/json',
      ...options.headers,
    },
    body: options.body ? JSON.stringify(options.body) : undefined,
    signal: controller.signal,
  }).then(response => {
    if (timeoutId) clearTimeout(timeoutId);
    if (response.ok) {
      // Handle empty responses (204 No Content)
      const contentLength = response.headers.get('content-length');
      if (contentLength === '0' || response.status === 204) {
        return { success: true };
      }
      return response.json();
    }
    return response.text().then(text => {
      throw new Error(`HTTP ${response.status}: ${text}`);
    });
  }).catch(err => {
    if (timeoutId) clearTimeout(timeoutId);
    if (err.name === 'AbortError') {
      throw new Error('Request timed out');
    }
    throw err;
  });
};

// Helper to perform API POST request (legacy, uses apiRequest)
const apiPost = (endpoint, params = {}) => {
  const url = params ?
    `${ApiClient.getUrl(endpoint)}?${new URLSearchParams(params)}` :
    ApiClient.getUrl(endpoint);

  return fetch(url, {
    method: 'POST',
    headers: {
      'X-Emby-Token': ApiClient.accessToken()
    }
  }).then(response => {
    if (response.ok) {
      return response.json();
    }
    return response.text().then(text => {
      throw new Error(`HTTP ${response.status}: ${text}`);
    });
  });
};

// ============================================
// Modern Card-Based Category UI Helpers
// ============================================
// All styles are now handled by Xtream.css utility classes

// Create search bar component
const createSearchBar = (placeholder = 'Search...', onSearch) => {
  const container = createElement('div', { classList: 'search-container' });

  const icon = createElement('span', { classList: ['material-icons', 'search-icon'], innerText: 'search' });

  const input = createElement('input', {
    type: 'text',
    classList: 'search-input',
    placeholder: placeholder
  });

  const clearBtn = createElement('button', {
    type: 'button',
    classList: ['clear-search', 'hide']
  }, [createElement('span', { classList: 'material-icons', innerText: 'close' })]);

  let debounceTimer;
  input.addEventListener('input', () => {
    clearTimeout(debounceTimer);
    clearBtn.classList.toggle('hide', !input.value);
    debounceTimer = setTimeout(() => {
      if (onSearch) onSearch(input.value.toLowerCase().trim());
    }, 150);
  });

  clearBtn.addEventListener('click', () => {
    input.value = '';
    clearBtn.classList.add('hide');
    if (onSearch) onSearch('');
    input.focus();
  });

  container.appendChild(icon);
  container.appendChild(input);
  container.appendChild(clearBtn);

  return { container, input, clearBtn };
};

// Create selection stats component
const createSelectionStats = () => {
  const container = createElement('div', { classList: 'selection-stats' });

  const selectedSpan = createElement('span', { classList: 'stat-selected', innerText: '0' });
  const totalSpan = createElement('span', { classList: 'stat-total', innerText: '0' });
  const divider = createElement('span', { classList: 'stat-divider' });
  const categoriesSpan = createElement('span', { classList: 'stat-categories', innerText: '0 categories' });

  container.appendChild(selectedSpan);
  container.appendChild(document.createTextNode('/'));
  container.appendChild(totalSpan);
  container.appendChild(document.createTextNode(' channels'));
  container.appendChild(divider);
  container.appendChild(categoriesSpan);

  return {
    container,
    update: (selected, total, categories) => {
      selectedSpan.innerText = selected;
      totalSpan.innerText = total;
      categoriesSpan.innerText = `${categories} ${categories === 1 ? 'category' : 'categories'}`;
    }
  };
};

// Create a channel item for the grid
const createChannelItem = (item, isSelected, onToggle) => {
  const div = createElement('div', {
    classList: ['channel-item', isSelected ? 'selected' : ''].filter(Boolean),
    dataset: { itemId: item.Id, itemName: item.Name.toLowerCase() }
  });

  const checkbox = createElement('input', {
    type: 'checkbox',
    classList: 'channel-checkbox',
    checked: isSelected
  });

  const name = createElement('span', { classList: 'channel-name', innerText: item.Name });

  div.appendChild(checkbox);
  div.appendChild(name);

  // Add catchup badge if supported
  if (item.HasCatchup) {
    const badge = createElement('span', {
      classList: 'catchup-badge',
      title: `Catch-up supported for ${item.CatchupDuration} days`
    }, [
      createElement('span', { classList: 'material-icons', innerText: 'timer' }),
      document.createTextNode(`${item.CatchupDuration}d`)
    ]);
    div.appendChild(badge);
  }

  // Handle click on the item (not just checkbox)
  div.addEventListener('click', (e) => {
    if (e.target !== checkbox) {
      checkbox.checked = !checkbox.checked;
    }
    div.classList.toggle('selected', checkbox.checked);
    if (onToggle) onToggle(item.Id, checkbox.checked);
  });

  checkbox.addEventListener('change', () => {
    div.classList.toggle('selected', checkbox.checked);
    if (onToggle) onToggle(item.Id, checkbox.checked);
  });

  return { element: div, checkbox, setSelected: (sel) => {
    checkbox.checked = sel;
    div.classList.toggle('selected', sel);
  }};
};

// Create a category card
const createCategoryCard = (category, config, loadItems, onSelectionChange, icon = 'folder') => {
  const card = createElement('div', {
    classList: 'category-card',
    dataset: { categoryId: category.Id, categoryName: category.Name.toLowerCase() }
  });

  const header = createElement('div', { classList: 'category-header' });
  const info = createElement('div', { classList: 'category-info' });
  const actions = createElement('div', { classList: 'category-actions' });

  // Checkbox for category-level selection
  const checkbox = createElement('input', {
    type: 'checkbox',
    classList: 'category-checkbox'
  });

  const iconEl = createElement('span', { classList: ['material-icons', 'category-icon'], innerText: icon });
  const nameEl = createElement('span', { classList: 'category-name', innerText: category.Name });
  const countEl = createElement('span', { classList: 'category-count' });
  const selectedCountEl = createElement('span', { classList: 'category-selected-count' });

  info.appendChild(checkbox);
  info.appendChild(iconEl);
  info.appendChild(nameEl);
  info.appendChild(countEl);
  info.appendChild(selectedCountEl);

  // Action buttons
  const selectAllBtn = createElement('button', {
    type: 'button',
    classList: 'select-all-btn',
    innerText: 'Select All'
  });

  const expandBtn = createElement('button', {
    type: 'button',
    classList: 'expand-btn'
  }, [createElement('span', { classList: 'material-icons', innerText: 'expand_more' })]);

  actions.appendChild(selectAllBtn);
  actions.appendChild(expandBtn);

  header.appendChild(info);
  header.appendChild(actions);

  // Content area (expandable)
  const content = createElement('div', { classList: 'category-content' });
  const contentInner = createElement('div', { classList: 'category-content-inner' });
  const grid = createElement('div', { classList: 'channel-grid' });

  contentInner.appendChild(grid);
  content.appendChild(contentInner);

  card.appendChild(header);
  card.appendChild(content);

  // State
  let items = [];
  let itemElements = [];
  let isExpanded = false;
  let isLoaded = false;

  // Get selection state from config
  // config[categoryId] = undefined (not selected), [] (all selected), or [id1, id2...] (specific items)
  const getSelectedIds = () => {
    const sel = config[category.Id];
    if (sel === undefined) return new Set();
    if (sel.length === 0) return 'all';
    return new Set(sel);
  };

  const setSelectedIds = (ids) => {
    if (ids === 'all' || (ids instanceof Set && items.length > 0 && ids.size === items.length)) {
      config[category.Id] = [];
    } else if (ids instanceof Set && ids.size === 0) {
      delete config[category.Id];
    } else if (ids instanceof Set) {
      config[category.Id] = Array.from(ids);
    }
    updateCounts();
    if (onSelectionChange) onSelectionChange();
  };

  const updateCounts = () => {
    const sel = getSelectedIds();
    let selectedCount = 0;
    if (sel === 'all') {
      selectedCount = items.length;
    } else {
      selectedCount = sel.size;
    }

    countEl.innerText = items.length > 0 ? `(${items.length})` : '';
    selectedCountEl.innerText = selectedCount > 0 ? `${selectedCount} selected` : '';

    // Update checkbox state
    if (selectedCount === 0) {
      checkbox.checked = false;
      checkbox.indeterminate = false;
    } else if (selectedCount === items.length) {
      checkbox.checked = true;
      checkbox.indeterminate = false;
    } else {
      checkbox.checked = false;
      checkbox.indeterminate = true;
    }
  };

  const isItemSelected = (itemId) => {
    const sel = getSelectedIds();
    if (sel === 'all') return true;
    return sel.has(itemId);
  };

  const toggleItem = (itemId, selected) => {
    const sel = getSelectedIds();
    let newSet;

    if (sel === 'all') {
      // Was all selected, now deselecting one
      newSet = new Set(items.map(i => i.Id));
      if (!selected) newSet.delete(itemId);
    } else {
      newSet = new Set(sel);
      if (selected) {
        newSet.add(itemId);
      } else {
        newSet.delete(itemId);
      }
    }

    setSelectedIds(newSet);
  };

  const loadContent = async () => {
    if (isLoaded) return;

    // Ensure XtreamStyles is available
    await loadXtreamStyles();

    // Use styles helper for loading spinner
    const loadingDiv = XtreamStyles.createLoadingSpinner('Loading channels...');
    grid.innerHTML = '';
    grid.appendChild(loadingDiv);

    try {
      items = await loadItems(category.Id);
      isLoaded = true;

      grid.innerHTML = '';
      itemElements = [];

      if (items.length === 0) {
        grid.appendChild(XtreamStyles.createEmptyState('inbox', 'No items in this category'));
        return;
      }

      items.forEach(item => {
        const selected = isItemSelected(item.Id);
        const itemEl = createChannelItem(item, selected, toggleItem);
        itemElements.push(itemEl);
        grid.appendChild(itemEl.element);
      });

      updateCounts();
    } catch (err) {
      console.error('Failed to load items:', err);
      grid.innerHTML = '';
      grid.appendChild(XtreamStyles.createErrorState('Failed to load items'));
    }
  };

  const toggleExpand = async () => {
    isExpanded = !isExpanded;
    // Use CSS classes for state - styles are handled by Xtream.css
    card.classList.toggle('expanded', isExpanded);
    content.classList.toggle('expanded', isExpanded);
    expandBtn.classList.toggle('expanded', isExpanded);

    if (isExpanded && !isLoaded) {
      await loadContent();
    }
  };

  // Event handlers
  expandBtn.addEventListener('click', (e) => {
    e.stopPropagation();
    toggleExpand();
  });

  header.addEventListener('click', (e) => {
    if (e.target === checkbox || e.target === selectAllBtn) return;
    toggleExpand();
  });

  checkbox.addEventListener('change', () => {
    if (checkbox.checked) {
      // Select all
      setSelectedIds('all');
      itemElements.forEach(el => el.setSelected(true));
    } else {
      // Deselect all
      setSelectedIds(new Set());
      itemElements.forEach(el => el.setSelected(false));
    }
  });

  selectAllBtn.addEventListener('click', async (e) => {
    e.stopPropagation();
    if (!isLoaded) await loadContent();

    const sel = getSelectedIds();
    const allSelected = sel === 'all' || (sel instanceof Set && sel.size === items.length);

    if (allSelected) {
      // Deselect all
      setSelectedIds(new Set());
      itemElements.forEach(el => el.setSelected(false));
      selectAllBtn.innerText = 'Select All';
    } else {
      // Select all
      setSelectedIds('all');
      itemElements.forEach(el => el.setSelected(true));
      selectAllBtn.innerText = 'Deselect All';
    }
  });

  // Initialize counts based on config
  const initSelection = () => {
    const sel = config[category.Id];
    if (sel !== undefined) {
      if (sel.length === 0) {
        // All selected - we don't know count yet
        checkbox.checked = true;
      } else {
        checkbox.indeterminate = true;
      }
    }
  };

  initSelection();

  return {
    element: card,
    filter: (searchTerm) => {
      // Helper function for visibility - uses .hide class directly
      const setVisible = (el, visible) => el.classList.toggle('hide', !visible);

      if (!searchTerm) {
        setVisible(card, true);
        itemElements.forEach(el => {
          setVisible(el.element, true);
          el.element.classList.remove('search-match');
        });
        return true;
      }

      const categoryMatch = category.Name.toLowerCase().includes(searchTerm);
      let hasVisibleItems = false;

      itemElements.forEach(el => {
        const itemName = el.element.dataset.itemName;
        const matches = itemName.includes(searchTerm);
        // Show/hide based on match
        setVisible(el.element, matches || categoryMatch);
        // Highlight matching items using CSS class
        el.element.classList.toggle('search-match', matches);
        if (matches || categoryMatch) hasVisibleItems = true;
      });

      // If category name matches, show all items
      if (categoryMatch) {
        itemElements.forEach(el => {
          setVisible(el.element, true);
        });
        hasVisibleItems = true;
      }

      // Auto-expand if there are search matches
      if (hasVisibleItems && searchTerm && !isExpanded) {
        toggleExpand();
      }

      setVisible(card, hasVisibleItems || categoryMatch);
      return hasVisibleItems || categoryMatch;
    },
    getSelectedCount: () => {
      const sel = getSelectedIds();
      if (sel === 'all') return items.length || 1; // Return 1 if not loaded yet
      return sel.size;
    },
    getTotalCount: () => items.length,
    isLoaded: () => isLoaded,
    loadContent,
    updateCounts
  };
};

// Create the full searchable categories UI
const createSearchableCategories = async (container, config, loadCategories, loadItems, options = {}) => {
  // Ensure XtreamStyles is loaded before using it
  await loadXtreamStyles();

  const {
    icon = 'folder',
    searchPlaceholder = 'Search channels...',
    emptyMessage = 'No categories available'
  } = options;

  container.innerHTML = '';

  // Create search and stats bar - styles handled by CSS classes
  const filterBar = createElement('div', { classList: 'search-filter-bar' });

  let categoryCards = [];

  const updateStats = () => {
    let totalSelected = 0;
    let totalItems = 0;
    let categoriesWithSelection = 0;

    categoryCards.forEach(card => {
      const sel = card.getSelectedCount();
      const total = card.getTotalCount();
      totalSelected += sel;
      totalItems += total;
      if (sel > 0) categoriesWithSelection++;
    });

    stats.update(totalSelected, totalItems || '?', categoryCards.length);
  };

  const handleSearch = (term) => {
    let hasResults = false;
    categoryCards.forEach(card => {
      if (card.filter(term)) hasResults = true;
    });

    noResults.classList.toggle('hide', hasResults || !term);
  };

  const { container: searchContainer } = createSearchBar(searchPlaceholder, handleSearch);
  const stats = createSelectionStats();

  filterBar.appendChild(searchContainer);
  filterBar.appendChild(stats.container);
  container.appendChild(filterBar);

  // Create categories container
  const categoriesContainer = createElement('div', { classList: 'categories-container' });
  container.appendChild(categoriesContainer);

  // No results message - uses CSS class styling
  const noResults = createElement('div', { classList: ['no-results', 'hide'] }, [
    createElement('span', { classList: 'material-icons', innerText: 'search_off' }),
    createElement('p', { innerText: 'No channels match your search' })
  ]);
  container.appendChild(noResults);

  // Show loading state while categories load
  categoriesContainer.appendChild(XtreamStyles.createLoadingSpinner('Loading categories...'));

  // Load categories
  try {
    const categories = await loadCategories();

    // Clear loading spinner
    categoriesContainer.innerHTML = '';

    if (categories.length === 0) {
      categoriesContainer.appendChild(XtreamStyles.createEmptyState('inbox', emptyMessage));
      stats.update(0, 0, 0);
      return { config, updateStats };
    }

    categories.forEach(category => {
      const card = createCategoryCard(category, config, loadItems, updateStats, icon);
      categoryCards.push(card);
      categoriesContainer.appendChild(card.element);
    });

    // Initial stats update
    stats.update(0, '?', categories.length);

  } catch (err) {
    console.error('Failed to load categories:', err);
    categoriesContainer.innerHTML = '';
    categoriesContainer.appendChild(XtreamStyles.createErrorState('Failed to load categories. Check provider credentials.'));
  }

  return { config, updateStats, categoryCards };
};

export default {
  apiPost,
  apiRequest,
  createElement,
  createFormSubmitHandler,
  createToggleFn,
  fetchJson,
  filter,
  loadConfigFields,
  pluginConfig,
  populateCategoriesTable,
  saveConfigFields,
  setTabs,
  // New card-based UI helpers
  createSearchBar,
  createSelectionStats,
  createChannelItem,
  createCategoryCard,
  createSearchableCategories,
}
