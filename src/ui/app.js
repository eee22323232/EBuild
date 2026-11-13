const api = window.launcher;
const el = id => document.getElementById(id);
const modal = el('modal');
const globalForm = el('settings-form');
const accountForm = el('account-form');
const current = el('current');
const loaderNames = { vanilla: '原版', fabric: 'Fabric', neoforge: 'NeoForge' };
const typeNames = { mod: '模组', modpack: '整合包', shader: '光影', resourcepack: '材质' };
const folderNames = { mods: '模组', shaderpacks: '光影', resourcepacks: '材质' };
let settings;
let instances = [];
let account;
let catalog;
let catalogRequest;
let catalogError = '';
let gameLimit = 35;
let resourceQuery;
let searchGeneration = 0;
let resourceOffset = 0;
let phase = 'idle';
let localBusy = false;
let deviceOpen = false;

function element(tag, text, className) {
  const node = document.createElement(tag);
  if (text !== undefined) node.textContent = text;
  if (className) node.className = className;
  return node;
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
  return instances.find(item => item.id === current.value);
}

function actionState() {
  const can = canMutate();
  for (const id of ['add-instance', 'import-pack', 'launch', 'change-directory', 'reset-directory', 'clear-cache', 'microsoft-login', 'microsoft-logout']) el(id).disabled = !can;
  el('launch').disabled = !can || !selectedInstance();
  el('launch-label').textContent = phase === 'running' ? '游戏运行中' : '启动游戏';
  for (const control of document.querySelectorAll('[data-mutates]')) control.disabled = !can;
}

function taskStatus(update) {
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
  el('cancel').hidden = !active || phase === 'starting';
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
  if (modal.open) modal.close();
}

function showModal(title, onClose = closeModal) {
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

function showPage(page) {
  for (const item of document.querySelectorAll('.page')) item.hidden = item.id !== page;
  for (const item of document.querySelectorAll('[data-page]')) {
    if (item.dataset.page === page) item.setAttribute('aria-current', 'page');
    else item.removeAttribute('aria-current');
  }
  el('main').scrollTop = 0;
  if (page === 'downloads' && !catalog) loadCatalog();
}

function tabs(attribute, panel, onChange) {
  const buttons = [...document.querySelectorAll('[data-' + attribute + ']')];
  for (const item of buttons) item.addEventListener('click', () => {
    const active = item.getAttribute('data-' + attribute);
    for (const tab of buttons) tab.setAttribute('aria-pressed', String(tab === item));
    for (const section of document.querySelectorAll(panel)) section.hidden = section.id !== active;
    onChange?.(active);
  });
}

function renderInstances() {
  const selected = instances.find(item => item.id === settings?.selected) ?? instances[0];
  options(current, instances.length ? instances.map(item => [item.id, item.name]) : [['', '尚无实例']]);
  current.value = selected?.id ?? '';
  current.disabled = !instances.length;
  const rows = instances.map(item => {
    const row = element('div', undefined, 'instance-row');
    row.classList.toggle('selected', selected?.id === item.id);
    const name = element('strong', item.name);
    const loader = element('div', loaderNames[item.loader]);
    if (item.loaderVersion) loader.append(element('small', item.loaderVersion));
    const actions = element('div', undefined, 'actions');
    const choose = button(selected?.id === item.id ? '当前' : '选择', () => selectInstance(item.id));
    choose.disabled = selected?.id === item.id;
    actions.append(choose, button('管理', () => editInstance(item)), button('目录', () => report(() => api.openInstance(item.id))));
    row.append(name, element('span', item.version), loader, actions);
    return row;
  });
  el('instance-list').replaceChildren(...rows);
  el('empty-instances').hidden = rows.length > 0;
  actionState();
}

async function refreshInstances() {
  instances = await api.instances();
  const value = await api.getSettings();
  settings = { ...settings, ...value };
  el('current-root').textContent = settings.currentRoot;
  renderInstances();
}

async function selectInstance(id) {
  const value = await report(() => api.selectInstance(id));
  if (value) { Object.assign(settings, value); renderInstances(); }
}

async function loadCatalog(force = false) {
  if (catalog && !force) return catalog;
  if (catalogRequest) return catalogRequest;
  catalogError = '';
  el('game-results').textContent = '正在读取 Mojang 版本列表…';
  el('refresh-game').disabled = true;
  catalogRequest = api.catalog().then(value => { catalog = value; return value; })
    .catch(error => { catalogError = error.message; return null; })
    .finally(() => {
      catalogRequest = null;
      el('refresh-game').disabled = false;
      renderGameVersions();
    });
  return catalogRequest;
}

function renderGameVersions() {
  const query = el('game-query').value.trim().toLowerCase();
  const versions = (catalog?.versions ?? []).filter(item => item.type === el('game-type').value && item.id.toLowerCase().includes(query));
  el('game-results').textContent = catalogError || (catalog ? (catalog.cached ? '缓存 · ' : '') + versions.length + ' 个版本' : '正在读取版本列表…');
  el('more-games').hidden = versions.length <= gameLimit;
  el('game-list').replaceChildren(...versions.slice(0, gameLimit).map(version => {
    const row = element('div', undefined, 'download-row');
    const name = element('div');
    name.append(element('strong', version.id), element('small', version.releaseTime.slice(0, 10)));
    const install = button('安装', () => createInstance(version.id));
    install.dataset.mutates = '';
    row.append(name, install);
    return row;
  }));
  actionState();
}

function field(label, control) {
  const wrapper = element('label');
  wrapper.append(element('strong', label), control);
  return wrapper;
}

async function createInstance(initial) {
  if (!canMutate()) return;
  const panel = showModal('新建实例');
  const form = element('form', undefined, 'stack');
  const name = element('input');
  name.required = true;
  name.maxLength = 80;
  const version = element('select');
  const kind = element('select');
  options(kind, [['release', '正式版'], ['snapshot', '快照与预发布']]);
  const loader = element('select');
  options(loader, Object.entries(loaderNames));
  const loaderVersion = element('select');
  const loaderField = field('加载器版本', loaderVersion);
  loaderField.hidden = true;
  const submit = element('button', '安装实例', 'primary');
  submit.disabled = true;
  const actions = element('div', undefined, 'form-actions');
  actions.append(submit);
  form.append(field('实例名称', name), field('版本类型', kind), field('Minecraft 版本', version), field('加载器', loader), loaderField, actions);
  panel.body.append(form);
  let automaticName = '';
  let generation = 0;
  const data = await loadCatalog();
  if (!form.isConnected) return;
  if (!data) { panel.error.textContent = catalogError; return; }
  const initialVersion = data.versions.find(item => item.id === initial);
  if (initialVersion) kind.value = initialVersion.type;
  function fillVersions(selected) {
    options(version, data.versions.filter(item => item.type === kind.value).map(item => [item.id, item.id]));
    if (selected && [...version.options].some(item => item.value === selected)) version.value = selected;
  }
  async function updateLoader() {
    const revision = ++generation;
    const previousName = automaticName;
    automaticName = version.value + ' ' + loaderNames[loader.value];
    if (!name.value || name.value === previousName) name.value = automaticName;
    panel.error.textContent = '';
    loaderField.hidden = loader.value === 'vanilla';
    submit.disabled = loader.value !== 'vanilla';
    if (loader.value === 'vanilla') return;
    options(loaderVersion, [['', '正在读取版本…']]);
    const list = await api.loaders(version.value, loader.value).catch(error => {
      if (revision === generation) panel.error.textContent = error.message;
      return [];
    });
    if (revision !== generation) return;
    options(loaderVersion, list.length ? list.map(item => [item.id, item.id + (item.stable ? '' : ' · 预发布')]) : [['', '没有兼容版本']]);
    if (list.find(item => item.stable)) loaderVersion.value = list.find(item => item.stable).id;
    submit.disabled = !list.length;
  }
  fillVersions(initial);
  kind.addEventListener('change', () => { fillVersions(); updateLoader(); });
  version.addEventListener('change', updateLoader);
  loader.addEventListener('change', updateLoader);
  await updateLoader();
  form.addEventListener('submit', event => {
    event.preventDefault();
    if (!canMutate()) return;
    closeModal();
    run(async () => {
      const result = await api.createInstance({ name: name.value, version: version.value, loader: loader.value, loaderVersion: loaderVersion.value });
      if (!result) return;
      await refreshInstances();
      showPage('instances');
      displayStatus('已安装 ' + result.name);
    });
  });
}

function setControl(control, value) {
  if (control.type === 'checkbox') control.checked = Boolean(value);
  else if (control.tagName === 'TEXTAREA') control.value = (value ?? []).join('\n');
  else control.value = value ?? '';
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

async function editInstance(instance) {
  const panel = showModal('管理实例');
  const form = element('form');
  const scroll = element('div', undefined, 'dialog-scroll');
  const name = element('input');
  name.required = true;
  name.maxLength = 80;
  name.value = instance.name;
  const top = element('div', undefined, 'stack');
  top.append(field('实例名称', name), element('p', instance.version + ' · ' + loaderNames[instance.loader] + ' ' + instance.loaderVersion));
  scroll.append(top);
  settingsFields(scroll, [...gameFields, ...advancedFields], instance.overrides ?? {});
  for (const control of scroll.querySelectorAll('[name]')) setControl(control, instance.overrides?.[control.name] ?? settings[control.name]);
  const actions = element('div', undefined, 'form-actions');
  const save = element('button', '保存实例', 'primary');
  actions.append(button('查看资源文件', () => showInstalled(instance)), button('打开目录', () => report(() => api.openInstance(instance.id))), save);
  form.append(scroll, actions);
  panel.body.append(form);
  form.addEventListener('submit', event => {
    event.preventDefault();
    report(async () => {
      const overrides = Object.fromEntries(Object.entries(values(form)).filter(([key]) => !form.elements.namedItem(key).disabled));
      await api.editInstance(instance.id, { name: name.value, overrides });
      await refreshInstances();
      closeModal();
      displayStatus('实例设置已保存');
    }, panel.error);
  });
}

async function showInstalled(instance) {
  const panel = showModal('实例资源 · ' + instance.name);
  const items = await report(() => api.instanceResources(instance.id), panel.error);
  if (!items || !panel.body.isConnected) return;
  const content = element('div', undefined, 'dialog-scroll');
  for (const item of items) {
    content.append(element('h3', folderNames[item.folder] + ' · ' + item.files.length));
    const list = element('ul', undefined, 'dependency-list');
    for (const file of item.files) list.append(element('li', file));
    content.append(list);
  }
  panel.body.append(content);
}

async function searchResources(append = false) {
  const generation = ++searchGeneration;
  const more = el('more-resources');
  more.hidden = true;
  if (!append) {
    resourceOffset = 0;
    resourceQuery = {
      query: el('resource-query').value, version: el('resource-game').value.trim(),
      type: el('resource-type').value, loader: el('resource-loader').value, sort: el('resource-sort').value,
    };
    el('resource-list').replaceChildren();
  }
  el('resource-results').textContent = '正在搜索 Modrinth…';
  try {
    const result = await api.search({ ...resourceQuery, offset: resourceOffset });
    if (generation !== searchGeneration) return;
    for (const project of result.hits) el('resource-list').append(projectRow(project));
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
  const icon = element('div', undefined, 'project-icon');
  icon.textContent = (typeNames[project.project_type] || '资').slice(0, 1);
  if (project.icon_url?.startsWith('https://cdn.modrinth.com/')) {
    const image = element('img', undefined, 'project-icon');
    image.src = project.icon_url;
    image.alt = '';
    image.addEventListener('load', () => icon.replaceChildren(image), { once: true });
    image.loading = 'eager';
  }
  const info = element('div');
  const description = element('p', project.description);
  description.title = project.description;
  info.append(element('strong', project.title), description,
    element('small', (typeNames[project.project_type] || project.project_type) + ' · ' + Number(project.downloads).toLocaleString('zh-CN') + ' 次下载'));
  const actions = element('div', undefined, 'actions');
  const versions = button('查看版本', () => projectDialog(project, true));
  const install = button(project.project_type === 'modpack' ? '安装整合包' : '安装兼容版本', () => projectDialog(project, false));
  install.dataset.mutates = '';
  actions.append(versions, install);
  row.append(icon, info, actions);
  return row;
}

async function projectDialog(project, showVersions) {
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
  const body = element('div', undefined, 'dialog-scroll');
  const actions = element('div', undefined, 'form-actions');
  panel.body.append(body, actions);
  const getPlan = async version => {
    if (!canMutate() || (!pack && !target.value)) return;
    const instanceId = target.value || '';
    const plan = await report(() => api.plan(project.project_id, version, instanceId), panel.error);
    if (plan && panel.body.isConnected) confirmPlan(plan, instanceId);
  };
  if (!showVersions) {
    const next = button(pack ? '选择推荐版本' : '匹配并继续', () => getPlan(''), 'primary');
    next.disabled = !pack && !target.value;
    actions.append(next, button('查看版本', () => projectDialog(project, true)));
    return;
  }
  body.append(element('p', '正在读取资源版本…'));
  const result = await report(() => api.versions(project.project_id), panel.error);
  if (!result || !panel.body.isConnected) return;
  function render() {
    const instance = instances.find(item => item.id === target.value);
    const compatible = item => pack
      ? item.loaders.some(loader => ['minecraft', 'vanilla', 'fabric', 'neoforge'].includes(loader))
      : instance && item.game_versions.includes(instance.version) &&
        (project.project_type !== 'mod' || item.loaders.includes(instance.loader));
    const ordered = result.releases.slice().sort((a, b) => Number(Boolean(compatible(b))) - Number(Boolean(compatible(a))));
    body.replaceChildren(...ordered.map(release => {
      const row = element('div', undefined, 'release-row');
      const info = element('div');
      info.append(element('strong', release.version_number), element('small', release.game_versions.join('、') + ' · ' + release.loaders.join('、')));
      const choice = button(compatible(release) ? '选择' : '不兼容', () => getPlan(release.id));
      choice.disabled = !compatible(release);
      row.append(info, choice);
      return row;
    }));
    if (!result.releases.length) body.append(element('p', '没有可用版本。'));
  }
  target.addEventListener('change', render);
  render();
}

function confirmPlan(plan, instanceId) {
  const panel = showModal('确认安装');
  const destination = instances.find(item => item.id === instanceId);
  panel.body.append(element('p', plan.title + ' · ' + plan.release),
    element('p', destination ? '目标实例 · ' + destination.name : '安装为新的游戏实例'));
  if (plan.files.length) {
    const list = element('ul', undefined, 'dependency-list');
    for (const [index, file] of plan.files.entries()) list.append(element('li', file + (index ? '（必需依赖）' : '')));
    panel.body.append(list);
  }
  const actions = element('div', undefined, 'form-actions');
  actions.append(button('安装', () => {
    if (!canMutate()) return;
    closeModal();
    run(async () => {
      const result = await api.install(plan.key);
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
      catalog = null;
      await refreshInstances();
      displayStatus('游戏数据目录已切换');
    });
  }, 'primary'));
  panel.body.append(options, actions);
}

function showAccount() {
  el('microsoft-name').textContent = account?.name ?? '未登录';
  el('microsoft-logout').hidden = !account;
  el('active-account').textContent = settings.accountMode === 'microsoft'
    ? '微软 · ' + (account?.name || '未登录') : '离线 · ' + settings.username;
}

function showPreferences() {
  document.documentElement.dataset.theme = settings.theme;
  fillForm(globalForm, settings);
  fillForm(accountForm, settings);
  el('current-root').textContent = settings.currentRoot;
  el('app-version').textContent = settings.version;
  el('manual-proxy').hidden = globalForm.elements.proxyMode.value !== 'manual';
  showAccount();
}

settingsFields(el('global-fields'), gameFields);
settingsFields(el('advanced-fields'), advancedFields);
for (const item of document.querySelectorAll('[data-page]')) item.addEventListener('click', () => showPage(item.dataset.page));
tabs('download', '.download-panel', tab => { if (tab === 'modrinth-download' && !resourceQuery) searchResources(); });
tabs('settings', '.settings-panel', tab => { el('save-global').hidden = tab === 'launcher-settings'; });
current.addEventListener('change', () => selectInstance(current.value));
el('launch').addEventListener('click', () => { const item = selectedInstance(); if (item) run(() => api.start(item.id)); });
el('cancel').addEventListener('click', () => report(() => api.cancel()));
el('data-folder').addEventListener('click', () => report(() => api.openData()));
el('add-instance').addEventListener('click', () => createInstance());
el('import-pack').addEventListener('click', () => run(async () => {
  const result = await api.importPack();
  if (result) { await refreshInstances(); displayStatus('已导入 ' + result.name); }
}));
el('game-query').addEventListener('input', () => { gameLimit = 35; renderGameVersions(); });
el('game-type').addEventListener('change', () => { gameLimit = 35; renderGameVersions(); });
el('refresh-game').addEventListener('click', () => loadCatalog(true));
el('more-games').addEventListener('click', () => { gameLimit += 35; renderGameVersions(); });
el('search-form').addEventListener('submit', event => { event.preventDefault(); searchResources(); });
el('more-resources').addEventListener('click', () => searchResources(true));
el('manage-java').addEventListener('click', manageJava);
el('change-directory').addEventListener('click', () => report(async () => {
  const target = await api.chooseData();
  if (target) chooseMigration(target);
}));
el('reset-directory').addEventListener('click', () => chooseMigration(''));
el('clear-cache').addEventListener('click', () => run(async () => {
  if (await api.clearCache()) displayStatus('下载缓存已清理');
}));
globalForm.elements.proxyMode.addEventListener('change', () => {
  el('manual-proxy').hidden = globalForm.elements.proxyMode.value !== 'manual';
});
globalForm.addEventListener('submit', event => {
  event.preventDefault();
  report(async () => {
    Object.assign(settings, await api.saveSettings(values(globalForm)));
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
  Object.assign(settings, await api.saveSettings({ clientId: accountForm.elements.clientId.value }));
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
  account = await report(() => api.account());
  showPreferences();
  await refreshInstances();
  displayStatus('就绪');
});
