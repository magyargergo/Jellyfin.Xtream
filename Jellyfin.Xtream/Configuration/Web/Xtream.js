const url = (name) =>
  ApiClient.getUrl("configurationpage", {
    name,
  });
const tab = (name) => '/configurationpage?name=' + name + '.html';

$(document).ready(() => {
  const style = document.createElement('link');
  style.rel = 'stylesheet';
  style.href = url('Xtream.css')
  document.head.appendChild(style);
});

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
}
