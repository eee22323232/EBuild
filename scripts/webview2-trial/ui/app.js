const api = window.launcher;
const el = id => document.getElementById(id);
const modal = el('modal');
const globalForm = el('settings-form');
const accountForm = el('account-form');
const loaderNames = { vanilla: '原版', fabric: 'Fabric', neoforge: 'NeoForge', forge: 'Forge', quilt: 'Quilt' };
const typeNames = { mod: '模组', modpack: '整合包', shader: '光影', resourcepack: '材质' };
let settings;
let instances = [];
let gameSessions = new Map();
let account;
let catalog;
let catalogRequest;
let catalogError = '';
let gameLimit = 35;
let resourceQuery;
let renderedResourceQuery;
let searchGeneration = 0;
let resourceOffset = 0;
let phase = 'idle';
let localBusy = false;
let deviceOpen = false;
let instanceEditor;
let selectingInstance = false;

function element(tag, text, className) {
  const node = document.createElement(tag);
  if (text !== undefined) node.textContent = text;
  if (className) node.className = className;
  return node;
}

function iconImage(source, className) {
  const image = element('img', undefined, className);
  image.src = source;
  image.alt = '';
  return image;
}

function button(text, action, className) {
  const node = element('button', text, className);
  node.type = 'button';
  node.addEventListener('click', action);
  return node;
}

function options(select, choices) {
  select.replaceChildren(...choices.map(([value, text]) => new Option(text, value)));
}

function displayStatus(message, type = '') {
  el('status').textContent = message;
  el('status').dataset.phase = type;
}

function busy() {
  return localBusy || !['idle', 'running', 'error'].includes(phase);
}

function canMutate() {
  return Boolean(settings) && !busy() && phase !== 'running';
}

function selectedInstance() {
  return instances.find(item => item.id === settings?.selected) ?? instances[0];
}

function actionState() {
  const can = canMutate();
  for (const id of ['add-instance', 'first-instance', 'open-import', 'change-directory', 'reset-directory', 'clear-cache', 'microsoft-login', 'microsoft-logout']) el(id).disabled = !can;
  updateGameControls();
  el('edit-active-instance').disabled = selectingInstance || !selectedInstance();
  for (const control of document.querySelectorAll('[data-mutates]')) control.disabled = !can;
  if (typeof updateInstallActions === 'function') updateInstallActions(can);
}

function taskStatus(update) {
  if (update.downloadsChanged) { report(refreshInstallTasks); return; }
  if (update.lanState) { applyLanState(update.lanState); return; }
  if (handleGameEvent(update)) return;
  phase = update.phase;
  displayStatus(update.message + (update.total ? ' · ' + update.done + ' / ' + update.total : ''), phase);
  const active = !['idle', 'running', 'error'].includes(phase);
  const progress = el('progress');
  progress.hidden = !active;
  if (update.total) {
    progress.max = update.total;
    progress.value = update.done;
  } else {
    progress.removeAttribute('value');
  }
  el('cancel').hidden = !active || ['starting', 'deleting'].includes(phase);
  if (update.kind === 'game-install') updateInstallProgress(update);
  if (update.kind === 'game-launch') updateLaunchProgress(update);
  if (phase === 'device') {
    deviceOpen = true;
    const panel = showModal('微软账号登录', () => api.cancel());
    panel.body.append(element('p', '在微软页面输入下列代码，然后在浏览器完成登录。'),
      element('code', update.code, 'device-code'));
    const actions = element('div', undefined, 'form-actions');
    actions.append(button('打开微软登录页', () => report(() => api.loginBrowser()), 'primary'));
    panel.body.append(actions);
  } else if (deviceOpen && !active) {
    deviceOpen = false;
    closeModal();
  }
  actionState();
}

async function report(action, errorNode) {
  if (errorNode) errorNode.textContent = '';
  try { return await action(); }
  catch (error) {
    displayStatus(error.message, 'error');
    if (errorNode) errorNode.textContent = error.message;
  }
}

async function run(action, errorNode) {
  if (!canMutate()) return;
  localBusy = true;
  actionState();
  try { return await report(action, errorNode); }
  finally { localBusy = false; actionState(); }
}

function closeModal() {
  closeSelectPicker();
  if (modal.open) modal.close();
}

function showModal(title, onClose = closeModal) {
  closeSelectPicker();
  const wrapper = element('div', undefined, 'dialog-body');
  const header = element('div', undefined, 'dialog-header');
  const heading = element('h2', title);
  heading.id = 'dialog-title';
  const close = button('关闭', onClose);
  header.append(heading, close);
  const body = element('div');
  const error = element('p', '', 'dialog-status');
  error.setAttribute('role', 'status');
  wrapper.append(header, body, error);
  el('modal-content').replaceChildren(wrapper);
  modal.setAttribute('aria-labelledby', heading.id);
  modal.oncancel = event => { event.preventDefault(); onClose(); };
  if (!modal.open) modal.showModal();
  return { body, error };
}

function updateIndicator(bar, active) {
  if (!active?.offsetWidth) return;
  bar.style.setProperty('--indicator-x', active.offsetLeft + 'px');
  bar.style.setProperty('--indicator-width', active.offsetWidth + 'px');
  bar.classList.add('has-indicator');
}

function syncIndicators() {
  for (const bar of document.querySelectorAll('.tabs, .bottom-nav')) {
    updateIndicator(bar, bar.querySelector('[aria-pressed="true"], [aria-current="page"]'));
  }
}

function switchTab(buttons, panels, index) {
  const previous = buttons.findIndex(tab => tab.getAttribute('aria-pressed') === 'true');
  for (const [i, tab] of buttons.entries()) {
    tab.setAttribute('aria-pressed', String(i === index));
    panels[i].hidden = i !== index;
  }
  updateIndicator(buttons[index].parentElement, buttons[index]);
  if (previous >= 0 && previous !== index) {
    cancelMotion(panels[previous]);
    slideIn(panels[index], Math.sign(index - previous) * 18, 0, 210);
  }
}

function navigationSection(page) {
  if (page === 'game-install') return installNavigationSection();
  if (page === 'instance-settings' || page === 'download-tasks') return 'instances';
  if (page === 'resource-detail') return 'downloads';
  return page;
}

function showPage(page) {
  if (instanceEditor && page !== 'instance-settings') {
    leaveInstanceEditor(() => showPage(page));
    return;
  }
  if (page !== 'resource-detail' && page !== 'game-install') clearResourceDetail();
  closeSelectPicker();
  closeGamePopovers();
  const main = el('main');
  const previousPage = main.querySelector('.page:not([hidden])');
  const buttons = [...document.querySelectorAll('[data-page]')];
  const previousTab = buttons.findIndex(item => item.hasAttribute('aria-current'));
  const section = navigationSection(page);
  const nextTab = buttons.findIndex(item => item.dataset.page === section);
  for (const item of main.querySelectorAll('.page')) item.hidden = item.id !== page;
  for (const [index, item] of buttons.entries()) {
    if (index === nextTab) item.setAttribute('aria-current', 'page');
    else item.removeAttribute('aria-current');
  }
  main.scrollTop = 0;
  syncIndicators();
  if (previousPage?.id !== page) {
    cancelMotion(previousPage);
    if (page === 'game-install') slideIn(el(page), 0, -32, 280);
    else if (previousTab !== nextTab && previousTab >= 0) slideIn(el(page), Math.sign(nextTab - previousTab) * 24, 0);
    else if (page !== section) slideIn(el(page), 0, -19);
    else slideIn(el(page), 0, 19);
  }
  if (page === 'downloads' && !el('game-download').hidden && !catalog) loadCatalog();
  if (page === 'multiplayer') openLanPage();
  if (page === 'accounts' && typeof refreshLegacyAccount === 'function') refreshLegacyAccount();
}

function tabs(attribute, panel, onChange) {
  const buttons = [...document.querySelectorAll('[data-' + attribute + ']')];
  const panels = buttons.map(item => document.querySelector(panel + '#' + item.getAttribute('data-' + attribute)));
  for (const [index, item] of buttons.entries()) item.addEventListener('click', () => {
    const active = item.getAttribute('data-' + attribute);
    switchTab(buttons, panels, index);
    onChange?.(active);
  });
}

function renderInstances() {
  const selected = selectedInstance();
  el('active-instance').hidden = !selected;
  el('empty-instances').hidden = Boolean(selected);
  el('other-instances').hidden = !selected;
  if (selected) {
    el('active-instance').dataset.instanceId = selected.id;
    el('active-instance-icon').src = loaderIconPath(selected.loader);
    el('active-instance-name').textContent = selected.name;
    el('active-instance-version').textContent = instanceVersionLabel(selected);
    el('active-instance-missing').hidden = !selected.externalMissing;
    el('relocate-active-instance').hidden = !selected.externalMissing;
  }
  const rows = instances.filter(item => item.id !== selected?.id).map(item => {
    const row = button('', () => selectInstance(item.id, row), 'instance-choice');
    row.dataset.instanceId = item.id;
    row.setAttribute('aria-label', '切换到 ' + item.name);
    const icon = iconImage(loaderIconPath(item.loader), 'instance-icon');
    const info = element('span');
    info.append(element('strong', item.name), element('small', instanceVersionLabel(item)));
    const state = gameState(item.id);
    row.append(icon, info, element('span', gameLabels[state] || (item.externalMissing ? '找不到文件夹' : '切换'), 'instance-choice-hint'));
    if (state !== 'running' && state !== 'stopping') return row;
    const wrapper = element('div', undefined, 'instance-running-row');
    wrapper.dataset.instanceId = item.id;
    const stop = button(state === 'stopping' ? '正在停止' : '停止', () => stopGame(item.id), 'danger-button');
    stop.setAttribute('aria-label', '停止 ' + item.name);
    stop.disabled = state === 'stopping';
    wrapper.append(row, stop);
    return wrapper;
  });
  el('instance-list').replaceChildren(...rows);
  renderGameThreads();
  actionState();
  renderLanControls();
}

function loaderIconPath(loader) { return 'icons/' + (Object.hasOwn(loaderNames, loader) ? loader : 'vanilla') + '.svg'; }

function resourceFileList(files, icons = {}) {
  const list = element('ul', undefined, 'resource-file-list');
  for (const file of [...files].sort((a, b) => a.localeCompare(b, 'zh-CN', { numeric: true }))) {
    const row = element('li');
    const fallback = 'icons/resource-file.svg';
    const source = Object.hasOwn(icons, file) && icons[file].startsWith('data:image/png;base64,') ? icons[file] : fallback;
    const image = iconImage(source, 'resource-file-icon');
    if (source !== fallback) image.addEventListener('error', () => { image.src = fallback; }, { once: true });
    row.append(image, element('span', file));
    list.append(row);
  }
  return list;
}
function instanceVersionLabel(item) { return 'Minecraft ' + item.version + ' · ' + loaderNames[item.loader]; }

async function refreshInstances() {
  instances = await api.instances();
  const value = await api.getSettings();
  settings = { ...settings, ...value };
  el('current-root').textContent = settings.currentRoot;
  renderInstances();
}

async function selectInstance(id, trigger) {
  if (selectingInstance) return;
  selectingInstance = true;
  actionState();
  try {
    const value = await report(() => api.selectInstance(id));
    if (!value) return;
    const moveFocus = document.activeElement === trigger;
    Object.assign(settings, value);
    renderInstances();
    slideIn(el('active-instance-icon'), -10, 0, 215);
    slideIn(el('active-instance').querySelector('.instance-summary'), -10, 0, 215);
    updateDetailInstance();
    if (moveFocus) el('active-instance-name').focus({ preventScroll: true });
  } finally {
    selectingInstance = false;
    actionState();
  }
}

async function loadCatalog(force = false) {
  if (catalog && !force) return catalog;
  if (catalogRequest) return catalogRequest;
  catalogError = '';
  el('game-results').textContent = '正在读取 Minecraft 版本列表…';
  el('refresh-game').disabled = true;
  const request = api.catalog(force).then(value => {
    if (catalogRequest !== request) return null;
    catalog = value;
    return value;
  }).catch(error => {
    if (catalogRequest === request) catalogError = error.message;
    return null;
  })
    .finally(() => {
      if (catalogRequest !== request) return;
      catalogRequest = null;
      el('refresh-game').disabled = false;
      renderGameVersions();
    });
  catalogRequest = request;
  return request;
}

function resetCatalog() {
  catalog = null;
  catalogRequest = null;
  catalogError = '';
  el('refresh-game').disabled = false;
  renderGameVersions();
}

function renderGameVersions(animateFrom = Infinity) {
  const query = el('game-query').value.trim().toLowerCase();
  const versions = (catalog?.versions ?? []).filter(item => item.type === el('game-type').value && item.id.toLowerCase().includes(query));
  const source = catalog?.source === 'bmclapi' ? 'BMCLAPI 国内镜像' : catalog?.source === 'mojang' ? 'Mojang 官方源' : '';
  let provenance = source || '来源未知';
  if (catalog?.cached) provenance = source ? source + '（本地缓存）' : '本地缓存';
  else if (catalog?.source === 'mojang' && catalog?.preferredSource === 'bmclapi') provenance += '（镜像回退）';
  el('game-results').textContent = catalogError || (catalog ? '版本列表来源：' + provenance + ' · ' + versions.length + ' 个版本' : '正在读取版本列表…');
  el('more-games').hidden = versions.length <= gameLimit;
  const list = el('game-list');
  const rows = versions.slice(0, gameLimit).map(version => {
    const row = element('div', undefined, 'download-row');
    const identity = element('div', undefined, 'game-version-identity');
    const icon = element('img');
    icon.src = 'icons/vanilla.svg';
    icon.alt = '';
    icon.width = 28;
    icon.height = 28;
    const name = element('div');
    name.append(element('strong', version.id), element('small', version.releaseTime.slice(0, 10)));
    identity.append(icon, name);
    const install = button('安装', () => createInstance(version.id));
    install.dataset.mutates = '';
    row.append(identity, install);
    return row;
  });
  list.replaceChildren(...rows);
  animateVisibleRows(list, rows.slice(animateFrom));
  actionState();
}

function field(label, control) {
  if (control instanceof HTMLSelectElement) {
    const wrapper = element('div', undefined, 'select-field');
    control.setAttribute('aria-label', label);
    const caption = element('strong', label);
    caption.setAttribute('aria-hidden', 'true');
    wrapper.append(caption, control);
    return wrapper;
  }
  const wrapper = element('label');
  wrapper.append(element('strong', label), control);
  return wrapper;
}

function setControl(control, value) {
  if (control.type === 'checkbox') control.checked = Boolean(value);
  else if (control.tagName === 'TEXTAREA') control.value = (value ?? []).join('\n');
  else control.value = value ?? '';
  if (control instanceof HTMLSelectElement) updateSelectPicker(control);
}

function getControl(control) {
  if (control.type === 'checkbox') return control.checked;
  if (control.type === 'number') return control.valueAsNumber || 0;
  if (control.tagName === 'TEXTAREA') return control.value.split(/\r?\n/).map(value => value.trim()).filter(Boolean);
  return control.value;
}

function values(form) {
  return Object.fromEntries([...form.elements].filter(control => control.name).map(control => [control.name, getControl(control)]));
}

function fillForm(form, data) {
  for (const control of form.elements) if (control.name) setControl(control, data[control.name]);
}

const gameFields = [
  ['java', 'Java 路径', '自动匹配版本或指定本机 Java。', 'java'],
  ['memory', '最大内存（MB）', '1024–65536 MB。', 'number', [1024, 65536]],
  ['resolution', '游戏窗口分辨率', '宽、高都填 0 时由 Minecraft 决定。', 'resolution'],
  ['afterLaunch', '启动后行为', '', 'select', [['keep', '保留启动器'], ['minimize', '最小化启动器']]],
];
const advancedFields = [
  ['gc', '垃圾回收器', '', 'select', [['', 'Java 默认'], ['G1', 'G1'], ['Z', 'ZGC'], ['Parallel', 'Parallel'], ['Serial', 'Serial']]],
  ['jvmArgs', 'JVM 参数', '每行一个参数。', 'textarea'],
  ['gameArgs', '游戏参数', '每行一个参数。', 'textarea'],
  ['logs', '保存启动日志', '', 'checkbox'],
];

function settingsFields(parent, definitions, inherited) {
  for (const [key, label, hint, type, config] of definitions) {
    const row = element('div', undefined, 'setting-row');
    const caption = element('div');
    caption.append(element('strong', label));
    if (hint) caption.append(element('small', hint));
    const group = element('div', undefined, 'field-group');
    const names = key === 'resolution' ? ['width', 'height'] : [key];
    const controls = [];
    for (const name of names) {
      const tag = type === 'textarea' || type === 'select' ? type : 'input';
      const control = element(tag);
      control.name = name;
      control.setAttribute('aria-label', name === 'width' ? '窗口宽度' : name === 'height' ? '窗口高度' : label);
      if (tag === 'input') control.type = type === 'resolution' ? 'number' : type === 'java' ? 'text' : type;
      if (type === 'java') { control.readOnly = true; control.placeholder = '自动配置'; }
      if (type === 'number') { [control.min, control.max] = config; control.required = true; }
      if (type === 'resolution') { control.min = '0'; control.max = '16384'; }
      if (type === 'select') options(control, config);
      if (type === 'textarea') control.rows = 3;
      controls.push(control);
      group.append(control);
    }
    if (type === 'java') {
      group.append(button('选择', () => report(async () => {
        const file = await api.chooseJava();
        if (file) controls[0].value = file;
      })), button('自动', () => { controls[0].value = ''; }));
    }
    if (inherited) {
      const check = element('input');
      check.type = 'checkbox';
      check.checked = names.some(name => Object.hasOwn(inherited, name));
      const toggle = element('label', undefined, 'check');
      toggle.append(check, element('small', '覆盖全局'));
      caption.append(toggle);
      const refresh = () => {
        for (const control of group.children) control.disabled = !check.checked;
        if (!check.checked) for (const control of controls) setControl(control, settings[control.name]);
      };
      check.addEventListener('change', refresh);
      refresh();
    }
    row.append(caption, group);
    parent.append(row);
  }
}

function leaveInstanceEditor(navigate) {
  const editor = instanceEditor;
  if (!editor || editor.saving) return;
  function leave() {
    closeModal();
    instanceEditor = undefined;
    el('instance-editor').replaceChildren();
    navigate();
    if (!el('instances').hidden) {
      el('main').scrollTop = editor.scroll;
      el('edit-active-instance').focus({ preventScroll: true });
    }
  }
  if (editor.snapshot() === editor.saved) return leave();
  const panel = showModal('实例设置尚未保存');
  panel.body.append(element('p', '继续离开会丢弃这一页的改动。'));
  const actions = element('div', undefined, 'form-actions');
  actions.append(button('继续编辑', closeModal), button('放弃改动并离开', leave));
  panel.body.append(actions);
}

function editInstance(instance) {
  const container = el('instance-editor');
  const editor = { scroll: el('main').scrollTop, saving: false };
  instanceEditor = editor;
  const back = button('← 返回实例', () => showPage('instances'), 'text-button');
  const header = element('div', undefined, 'heading');
  const title = element('h1', instance.name);
  header.append(title);
  const tabBar = element('div', undefined, 'tabs');
  tabBar.setAttribute('aria-label', '实例设置分类');
  const form = element('form');
  form.noValidate = true;
  const basic = element('section');
  const mods = element('section');
  const java = element('section');
  const sections = [[basic, '基本信息'], [mods, '模组'], [java, 'Java 与高级设置']];
  const tabs = new Map(sections.map(([section, label]) => [section, button(label, () => activate(section))]));
  tabBar.append(...tabs.values());
  const name = element('input');
  name.required = true;
  name.maxLength = 80;
  name.value = instance.name;
  const identity = element('div', undefined, 'instance-basic');
  const fact = (label, value) => {
    const row = element('div', undefined, 'setting-row');
    row.append(element('strong', label), element('span', value));
    return row;
  };
  identity.append(field('实例名称', name), fact('Minecraft 版本', instance.version),
    fact('加载器', loaderNames[instance.loader]), fact('加载器版本', instance.loaderVersion || '—'));
  if (instance.externalPath) identity.append(fact('原游戏目录', instance.externalPath));
  const basicActions = element('div', undefined, 'instance-tools');
  basicActions.append(button('打开实例文件夹', () => report(() => api.openInstance(instance.id))));
  const verify = button('完整校验', () => run(async () => {
    if (await api.verifyInstance(instance.id)) displayStatus('游戏文件已校验');
  }));
  verify.dataset.idleInstance = instance.id;
  if (instance.externalMissing) basicActions.append(button('重新定位', () => relocateExternal(instance)));
  else basicActions.append(verify);
  basicActions.append(...instanceRemovalButtons(instance));
  basic.append(identity, basicActions);
  settingsFields(java, [...gameFields, ...advancedFields], instance.overrides ?? {});
  for (const control of java.querySelectorAll('[name]')) setControl(control, instance.overrides?.[control.name] ?? settings[control.name]);

  const modActions = element('div', undefined, 'instance-tools');
  const modList = element('div');
  modActions.append(button('打开 mods 文件夹', () => report(() => api.openMods(instance.id))),
    button('去下载模组', () => leaveInstanceEditor(() => downloadInstanceMods(instance)), 'primary'));
  mods.append(modActions, modList);
  async function loadMods() {
    modList.replaceChildren(element('p', '正在读取模组…', 'results-info'));
    try {
      const items = await api.instanceResources(instance.id, 'mods');
      if (instanceEditor !== editor) return;
      const info = items.find(item => item.folder === 'mods');
      const files = info?.files ?? [];
      const count = element('p', '已安装模组 · ' + files.length, 'results-info');
      if (!files.length) { modList.replaceChildren(count, element('p', '这个实例还没有模组。', 'empty')); return; }
      modList.replaceChildren(count, resourceFileList(files, info.icons));
    } catch (error) {
      if (instanceEditor === editor) modList.replaceChildren(element('p', error.message, 'results-info'), button('重试', loadMods));
    }
  }
  const actions = element('div', undefined, 'form-actions');
  const save = element('button', '保存', 'primary');
  save.dataset.mutates = '';
  const errorNode = element('p', '', 'dialog-status');
  errorNode.setAttribute('role', 'status');
  actions.append(save);
  form.append(basic, mods, java, errorNode, actions);
  container.replaceChildren(back, header, tabBar, form);
  editor.snapshot = () => JSON.stringify([name.value, [...java.querySelectorAll('[name]')]
    .filter(control => !control.disabled).map(control => [control.name, control.type === 'checkbox' ? control.checked : control.value])]);
  editor.saved = editor.snapshot();
  function activate(active) {
    const panels = [...tabs.keys()];
    switchTab([...tabs.values()], panels, panels.indexOf(active));
    actions.hidden = active === mods;
    if (active === mods) loadMods();
  }
  activate(basic);
  showPage('instance-settings');
  back.focus({ preventScroll: true });
  actionState();
  form.addEventListener('submit', event => {
    event.preventDefault();
    if (!canMutate() || editor.saving) return;
    name.setCustomValidity(name.value.trim() ? '' : '请输入实例名称。');
    const invalid = form.querySelector(':invalid:not(form)');
    if (invalid) {
      if (basic.contains(invalid)) activate(basic);
      else if (java.contains(invalid)) activate(java);
      invalid.reportValidity();
      return;
    }
    const snapshot = editor.snapshot();
    editor.saving = true;
    run(async () => {
      const overrides = Object.fromEntries(Object.entries(values(form)).filter(([key]) => !form.elements.namedItem(key).disabled));
      const result = await api.editInstance(instance.id, { name: name.value, overrides });
      if (!result) return;
      editor.saved = snapshot;
      title.textContent = result.name;
      await refreshInstances();
      displayStatus('实例设置已保存');
    }, errorNode).finally(() => { editor.saving = false; });
  });
  name.addEventListener('input', () => name.setCustomValidity(''));
}

function downloadInstanceMods(instance) {
  for (const tab of document.querySelectorAll('[data-download]')) {
    tab.setAttribute('aria-pressed', String(tab.dataset.download === 'modrinth-download'));
  }
  for (const panel of document.querySelectorAll('.download-panel')) panel.hidden = panel.id !== 'modrinth-download';
  el('resource-query').value = '';
  el('resource-game').value = instance.version;
  setControl(el('resource-type'), 'mod');
  setControl(el('resource-loader'), instance.loader === 'vanilla' ? '' : instance.loader);
  showPage('downloads');
  searchResources();
}

async function searchResources(append = false) {
  const generation = ++searchGeneration;
  const list = el('resource-list');
  const more = el('more-resources');
  more.hidden = true;
  if (!append) {
    resourceOffset = 0;
    resourceQuery = {
      query: el('resource-query').value, version: el('resource-game').value.trim(),
      type: el('resource-type').value, loader: el('resource-loader').value, sort: el('resource-sort').value,
    };
  }
  const query = resourceQuery;
  const sorting = !append && renderedResourceQuery && renderedResourceQuery.sort !== query.sort &&
    ['query', 'version', 'type', 'loader'].every(key => renderedResourceQuery[key] === query[key]);
  if (!append && !sorting) {
    list.replaceChildren();
    renderedResourceQuery = undefined;
  }
  el('resource-results').textContent = '正在搜索 Modrinth…';
  try {
    const result = await api.search({ ...query, offset: resourceOffset });
    if (generation !== searchGeneration) return;
    const rows = result.hits.map(projectRow);
    const positions = sorting ? captureRowPositions(list) : undefined;
    if (append) list.append(...rows);
    else list.replaceChildren(...rows);
    renderedResourceQuery = query;
    animateVisibleRows(list, rows, positions);
    resourceOffset += result.hits.length;
    el('resource-results').textContent = result.total_hits ? '已显示 ' + resourceOffset + ' / ' + result.total_hits + ' 项' : '没有匹配的资源';
    more.hidden = resourceOffset >= result.total_hits || !result.hits.length;
    actionState();
  } catch (error) {
    if (generation === searchGeneration) el('resource-results').textContent = error.message;
  }
}

function projectRow(project) {
  const row = element('div', undefined, 'resource-row');
  row.dataset.motionKey = project.project_id || project.slug;
  const open = element('button', undefined, 'resource-open');
  open.type = 'button';
  open.setAttribute('aria-label', '查看 ' + project.title + ' 详情');
  open.addEventListener('click', () => openResourceDetail(project, open));
  const info = element('div');
  const description = element('p', project.description);
  description.title = project.description;
  info.append(element('strong', project.title), description,
    element('small', (typeNames[project.project_type] || project.project_type) + ' · ' + Number(project.downloads).toLocaleString('zh-CN') + ' 次下载'));
  open.append(resourceIcon(project), info);
  const actions = element('div', undefined, 'actions');
  const install = button(project.project_type === 'modpack' ? '安装整合包' : '安装兼容版本', () => quickInstall(project, open));
  install.dataset.mutates = '';
  actions.append(install);
  row.append(open, actions);
  return row;
}

function quickInstall(project, anchor) {
  const panel = showModal(project.title);
  const pack = project.project_type === 'modpack';
  const target = element('select');
  if (!pack) {
    options(target, instances.length ? instances.map(item => [item.id, item.name + ' · ' + item.version + ' · ' + loaderNames[item.loader]]) : [['', '请先创建实例']]);
    if (selectedInstance()) target.value = selectedInstance().id;
    panel.body.append(field('目标实例', target));
  } else {
    panel.body.append(element('p', '安装后会创建独立实例。'));
  }
  const actions = element('div', undefined, 'form-actions');
  panel.body.append(actions);
  const getPlan = async () => {
    if (!canMutate() || (!pack && !target.value)) return;
    const instanceId = target.value || '';
    const plan = await run(() => api.plan(project.project_id, '', instanceId), panel.error);
    if (plan && panel.body.isConnected) confirmPlan(plan, instanceId);
  };
  const next = button(pack ? '选择推荐版本' : '匹配并继续', getPlan, 'primary');
  next.disabled = !pack && !target.value;
  actions.append(next, button('查看详情', () => { closeModal(); openResourceDetail(project, anchor); }));
}

function confirmPlan(plan, instanceId, directory) {
  const panel = showModal(instanceId ? '安装到实例' : '安装为独立实例');
  const destination = instances.find(item => item.id === instanceId);
  panel.body.append(element('p', plan.title + ' · ' + plan.release),
    element('p', destination ? '目标实例 · ' + destination.name : '安装为新的游戏实例'));
  if (directory) panel.body.append(element('p', directory, 'path'));
  if (plan.files.length) {
    const list = element('ul', undefined, 'dependency-list');
    for (const [index, file] of plan.files.entries()) list.append(element('li', file + (index ? '（必需依赖）' : '')));
    panel.body.append(list);
  }
  const replacements = plan.replacements ?? [];
  if (replacements.length) {
    const notice = element('section', undefined, 'mod-replacement');
    notice.append(element('strong', '将替换以下已安装的文件'), resourceFileList(replacements),
      element('small', '旧文件会移到实例的 mods-backup 文件夹。'));
    panel.body.append(notice);
  }
  const actions = element('div', undefined, 'form-actions');
  actions.append(button('取消', closeModal), button(replacements.length ? '替换并安装' : '安装', () => {
    if (!canMutate()) return;
    closeModal();
    run(async () => {
      const result = await api.install(plan.key, replacements.length > 0);
      if (!result) return;
      await refreshInstances();
      if (plan.type === 'modpack') showPage('instances');
      displayStatus('已安装 ' + plan.title);
    });
  }, 'primary'));
  panel.body.append(actions);
}


async function manageJava() {
  const panel = showModal('Java 管理');
  const body = element('div', undefined, 'dialog-scroll');
  const actions = element('div', undefined, 'form-actions');
  const select = element('select');
  select.setAttribute('aria-label', 'Java 版本');
  options(select, [8, 16, 17, 21, 25].map(major => [major, 'Java ' + major]));
  select.value = '21';
  actions.append(select, button('下载或检查', () => {
    if (!canMutate()) return;
    closeModal();
    run(async () => {
      const result = await api.installJava(Number(select.value));
      if (result) await manageJava();
    });
  }, 'primary'));
  panel.body.append(body, actions);
  const list = await report(() => api.javaList(), panel.error);
  if (!list || !panel.body.isConnected) return;
  if (!list.length) body.append(element('p', '未发现可用的本机 Java。'));
  for (const java of list) {
    const row = element('div', undefined, 'release-row');
    const info = element('div');
    info.append(element('strong', 'Java ' + java.major), element('p', java.executable, 'path'));
    row.append(info);
    if (java.executable !== 'java.exe') row.append(button('选用', () => {
      globalForm.elements.java.value = java.executable;
      closeModal();
    }));
    body.append(row);
  }
}

function chooseMigration(target) {
  const panel = showModal('切换游戏数据目录');
  const checkbox = element('input');
  checkbox.type = 'checkbox';
  checkbox.checked = true;
  const label = element('label', undefined, 'check');
  label.append(checkbox, element('span', '复制并校验现有数据，保留旧目录'));
  const options = element('div', undefined, 'stack');
  options.append(element('p', target || settings.defaultRoot, 'path'), label);
  const actions = element('div', undefined, 'form-actions');
  actions.append(button('切换目录', () => {
    closeModal();
    run(async () => {
      const result = await api.changeData(target, checkbox.checked);
      if (!result) return;
      resetCatalog();
      await refreshInstances();
      await refreshInstallTasks();
      displayStatus('游戏数据目录已切换');
    });
  }, 'primary'));
  panel.body.append(options, actions);
}

function showAccount() {
  el('microsoft-name').textContent = account?.name ?? '未登录';
  el('microsoft-logout').hidden = !account;
  if (typeof renderAccountAppearance === 'function') renderAccountAppearance();
}

function showPreferences() {
  document.documentElement.dataset.theme = settings.theme;
  applyMotionPreference(settings.motion || 'on');
  fillForm(globalForm, settings);
  fillForm(accountForm, settings);
  el('current-root').textContent = settings.currentRoot;
  el('app-version').textContent = settings.version;
  el('manual-proxy').hidden = globalForm.elements.proxyMode.value !== 'manual';
  showAccount();
}

settingsFields(el('global-fields'), gameFields);
settingsFields(el('advanced-fields'), advancedFields);
syncIndicators();
window.addEventListener('resize', syncIndicators);
document.fonts.ready.then(syncIndicators);
for (const item of document.querySelectorAll('[data-page]')) item.addEventListener('click', () => showPage(item.dataset.page));
tabs('download', '.download-panel', tab => {
  if (tab === 'modrinth-download' && !resourceQuery) searchResources();
  if (tab === 'game-download' && !catalog) loadCatalog();
});
tabs('settings', '.settings-panel', tab => { el('save-global').hidden = tab === 'launcher-settings'; });
el('launch').addEventListener('click', () => { const item = selectedInstance(); if (item) launchInstance(item); });
el('edit-active-instance').addEventListener('click', () => { const item = selectedInstance(); if (item) editInstance(item); });
el('cancel').addEventListener('click', () => {
  if (!cancelGameInstall()) report(() => api.cancel());
});
el('data-folder').addEventListener('click', () => report(() => api.openData()));
el('add-instance').addEventListener('click', () => createInstance());
el('first-instance').addEventListener('click', () => createInstance());
el('game-query').addEventListener('input', () => { gameLimit = 35; renderGameVersions(0); });
el('game-type').addEventListener('change', () => { gameLimit = 35; renderGameVersions(0); });
el('refresh-game').addEventListener('click', () => loadCatalog(true));
el('more-games').addEventListener('click', () => { const previousLimit = gameLimit; gameLimit += 35; renderGameVersions(previousLimit); });
el('search-form').addEventListener('submit', event => { event.preventDefault(); searchResources(); });
for (const id of ['resource-type', 'resource-loader', 'resource-sort']) el(id).addEventListener('change', () => searchResources());
el('more-resources').addEventListener('click', () => searchResources(true));
el('manage-java').addEventListener('click', manageJava);
el('change-directory').addEventListener('click', () => report(async () => {
  const target = await api.chooseData();
  if (target) chooseMigration(target);
}));
el('reset-directory').addEventListener('click', () => chooseMigration(''));
el('clear-cache').addEventListener('click', () => report(openStorage));
globalForm.elements.proxyMode.addEventListener('change', () => {
  el('manual-proxy').hidden = globalForm.elements.proxyMode.value !== 'manual';
});
globalForm.elements.motion.addEventListener('change', () => applyMotionPreference(globalForm.elements.motion.value));
globalForm.addEventListener('submit', event => {
  event.preventDefault();
  report(async () => {
    const previousSource = settings.downloadSource;
    Object.assign(settings, await api.saveSettings(values(globalForm)));
    if (settings.downloadSource !== previousSource) resetCatalog();
    showPreferences();
    displayStatus('全局设置已保存');
  });
});
accountForm.addEventListener('submit', event => {
  event.preventDefault();
  report(async () => {
    Object.assign(settings, await api.saveSettings(values(accountForm)));
    showPreferences();
    displayStatus('账号设置已保存');
  });
});
el('microsoft-login').addEventListener('click', () => run(async () => {
  account = await api.login() || account;
  if (account) Object.assign(settings, await api.getSettings());
  showPreferences();
}));
el('microsoft-logout').addEventListener('click', () => run(async () => {
  if (!await api.logout()) return;
  account = null;
  Object.assign(settings, await api.getSettings());
  showPreferences();
}));
api.onStatus(taskStatus);
report(async () => {
  settings = await api.getSettings();
  applyMotionPreference(settings.motion || 'on');
  applyGameStates(await api.gameStates());
  account = await report(() => api.account());
  showPreferences();
  await refreshInstances();
  await refreshInstallTasks();
  applyLanState(await api.lanState());
  displayStatus('就绪');
});
