const installForm = el('game-install-form');
const installVersion = el('install-version');
const installSnapshots = el('install-snapshots');
const installLoaderVersion = el('install-loader-version');
const installLoaderField = el('install-loader-version-field');
const installName = el('install-name');
const installStatus = el('install-configuration-status');
const installTasks = [];
let installDraft;
let activeInstall;
let installPageTask;
let toastInstance;
let toastTimer;

function installNavigationSection() {
  return installDraft?.origin.page === 'instances' ? 'instances' : 'downloads';
}

function gameVersionType(type) {
  return type === 'release' ? '正式版' : '快照与预发布';
}

function createInstance(version, requestedLoader) {
  if (!canMutate()) return;
  const origin = {
    page: document.querySelector('.page:not([hidden])').id,
    scroll: el('main').scrollTop,
    anchor: document.activeElement,
  };
  const loader = Object.hasOwn(loaderNames, requestedLoader) ? requestedLoader : 'vanilla';
  openInstallDraft({ version, loader }, origin);
}

function openInstallDraft(input, origin) {
  installDraft = { origin, input, games: [], automaticName: '', ready: false, loaderRequest: 0 };
  installPageTask = undefined;
  installName.value = input.name ?? '';
  installName.setCustomValidity('');
  installSnapshots.checked = false;
  installForm.elements.loader.value = input.loader;
  installForm.hidden = false;
  el('install-progress').hidden = true;
  showPage('game-install');
  el('back-from-install').focus({ preventScroll: true });
  loadInstallVersions(installDraft);
}

async function loadInstallVersions(draft, force = false) {
  draft.ready = false;
  draft.loading = true;
  el('install-options').disabled = true;
  installLoaderField.hidden = true;
  options(installVersion, [['', '正在读取版本…']]);
  updateSelectPicker(installVersion);
  installStatus.textContent = '';
  el('reload-install-options').hidden = true;
  actionState();
  const data = await loadCatalog(force);
  if (installDraft !== draft) return;
  draft.loading = false;
  draft.games = (data?.versions ?? []).filter(item => item.type === 'release' || item.type === 'snapshot');
  el('install-options').disabled = !draft.games.length;
  if (!draft.games.length) {
    options(installVersion, [['', '没有可用版本']]);
    updateSelectPicker(installVersion);
    installStatus.textContent = catalogError || '没有可用的 Minecraft 版本。';
    el('reload-install-options').hidden = false;
    return;
  }
  const preferred = draft.games.find(item => item.id === draft.input.version);
  installSnapshots.checked = preferred?.type === 'snapshot';
  fillInstallVersions(preferred?.id);
  updateInstallLoader(draft.input.loaderVersion);
}

function fillInstallVersions(selected) {
  const types = installSnapshots.checked ? ['release', 'snapshot'] : ['release'];
  const groups = types.map(type => {
    const group = element('optgroup');
    group.label = gameVersionType(type);
    for (const item of installDraft.games) {
      if (item.type !== type) continue;
      const option = new Option(item.id, item.id);
      if (item.releaseTime) option.dataset.date = item.releaseTime.slice(0, 10);
      group.append(option);
    }
    return group;
  }).filter(group => group.childElementCount);
  installVersion.replaceChildren(...groups);
  if (selected && [...installVersion.options].some(option => option.value === selected)) installVersion.value = selected;
  updateSelectPicker(installVersion);
}

async function updateInstallLoader(preferred) {
  const draft = installDraft;
  const revision = ++draft.loaderRequest;
  const version = installVersion.value;
  const loader = installForm.elements.loader.value;
  const automaticName = version ? version + ' ' + loaderNames[loader] : '';
  if (!installName.value || installName.value === draft.automaticName) installName.value = automaticName;
  draft.automaticName = automaticName;
  draft.ready = Boolean(version) && loader === 'vanilla';
  const needsLoader = Boolean(version) && loader !== 'vanilla';
  if (installLoaderField.hidden === needsLoader) {
    const nameField = installName.parentElement;
    const previousLeft = nameField.getBoundingClientRect().left;
    installLoaderField.hidden = !needsLoader;
    contentAnimations.get(nameField)?.cancel();
    const offset = previousLeft - nameField.getBoundingClientRect().left;
    if (offset) slideIn(nameField, offset, 0, 220);
  }
  installLoaderVersion.disabled = true;
  options(installLoaderVersion, []);
  installStatus.textContent = version ? '' : '没有正式版可选。可以打开「显示快照版」。';
  el('reload-install-options').hidden = true;
  actionState();
  if (!needsLoader) return;
  options(installLoaderVersion, [['', '正在读取版本…']]);
  try {
    const list = await api.loaders(version, loader);
    if (installDraft !== draft || revision !== draft.loaderRequest) return;
    options(installLoaderVersion, list.length
      ? list.map(item => [item.id, item.id + (item.stable ? '' : ' · 预发布')])
      : [['', '没有兼容版本']]);
    if (list.length) {
      setControl(installLoaderVersion, (list.find(item => item.id === preferred) ?? list.find(item => item.stable) ?? list[0]).id);
      installLoaderVersion.disabled = false;
      draft.ready = true;
    } else {
      installStatus.textContent = loaderNames[loader] + ' 暂无兼容 ' + version + ' 的版本。';
    }
  } catch (error) {
    if (installDraft !== draft || revision !== draft.loaderRequest) return;
    options(installLoaderVersion, [['', '读取失败']]);
    installStatus.textContent = error.message;
    el('reload-install-options').hidden = false;
  }
  actionState();
}

function updateInstallActions(can) {
  el('start-install').disabled = !can || !installDraft?.ready;
  for (const control of document.querySelectorAll('[data-install-retry]')) control.disabled = !can;
}

function backFromInstall() {
  const { page, scroll, anchor } = installDraft.origin;
  const destination = page === 'resource-detail' && !el(page).childElementCount ? 'downloads' : page;
  showPage(destination);
  if (destination === 'resource-detail') updateDetailInstance();
  el('main').scrollTop = scroll;
  if (anchor instanceof HTMLElement && anchor.isConnected && !anchor.matches(':disabled')) {
    anchor.focus({ preventScroll: true });
  }
}

const installStatePresentation = {
  active: ['↓', '下载中'],
  cancelling: ['…', '正在取消'],
  complete: ['✓', '完成'],
  failed: ['!', '失败'],
  cancelled: ['–', '已取消'],
};

function installTaskCard(task, compact = false) {
  const { input } = task;
  const card = element(compact ? 'article' : 'li', undefined, 'install-task-card');
  const icon = iconImage(loaderIconPath(input.loader), 'install-task-icon');
  const identity = element('div', undefined, 'install-task-identity');
  identity.append(element('strong', input.name),
    element('small', instanceVersionLabel(input) + (input.loaderVersion ? ' ' + input.loaderVersion : '') +
      (task.versionType ? ' · ' + gameVersionType(task.versionType) : '')));
  const [mark, label] = installStatePresentation[task.state];
  const state = element('span', undefined, 'install-task-state');
  const stateIcon = element('span', mark, 'install-status-icon');
  stateIcon.dataset.state = task.state;
  stateIcon.setAttribute('aria-hidden', 'true');
  state.append(stateIcon, element('span', label));
  const details = element('div', undefined, 'install-task-details');
  const message = element('p', task.message);
  details.append(message);
  const controls = element('div', undefined, 'actions');
  if (task.state === 'active' || task.state === 'cancelling') {
    const bar = element('progress');
    bar.setAttribute('aria-label', '当前安装阶段的进度');
    details.append(bar);
    const cancel = button('取消安装', cancelGameInstall);
    cancel.disabled = task.state === 'cancelling';
    controls.append(cancel);
    task.views.push({ message, bar });
  } else if (task.state === 'failed' || task.state === 'cancelled') {
    const retry = button('重试', () => startGameInstall(task), 'primary');
    retry.dataset.installRetry = '';
    controls.append(button('返回修改', () => openInstallDraft(task.input, task.origin)), retry);
  } else {
    controls.append(button('查看实例', () => viewInstalledInstance(task.instance.id)));
  }
  card.append(icon, identity, state, details, controls);
  return card;
}

function paintInstallProgress(task) {
  for (const { message, bar } of task.views) {
    message.textContent = task.message + (task.total ? ' · ' + task.done + ' / ' + task.total : '');
    if (task.total) {
      bar.max = task.total;
      bar.value = task.done;
    } else {
      bar.removeAttribute('value');
    }
  }
}

function renderInstallTasks() {
  if (activeInstall) activeInstall.views = [];
  el('active-download-count').hidden = !activeInstall;
  el('download-tasks-empty').hidden = installTasks.length > 0;
  el('download-task-list').replaceChildren(...installTasks.map(task => installTaskCard(task)));
  if (installPageTask) el('install-progress').replaceChildren(installTaskCard(installPageTask, true));
  if (activeInstall) paintInstallProgress(activeInstall);
  actionState();
}

function updateInstallProgress({ phase, message, done, total }) {
  const task = activeInstall;
  if (task?.state !== 'active' || phase === 'idle' || phase === 'error') return;
  Object.assign(task, { message, done, total });
  paintInstallProgress(task);
}

function cancelGameInstall() {
  const task = activeInstall;
  if (!task) return false;
  if (task.state === 'cancelling') return true;
  task.state = 'cancelling';
  task.message = '正在取消安装…';
  task.total = undefined;
  renderInstallTasks();
  api.cancel().catch(error => {
    if (activeInstall !== task) return;
    task.state = 'active';
    task.message = error.message;
    renderInstallTasks();
  });
  return true;
}

async function startGameInstall(task) {
  if (!canMutate() || activeInstall) return;
  const previous = installTasks.indexOf(task);
  if (previous >= 0) installTasks.splice(previous, 1);
  installTasks.unshift(task);
  activeInstall = task;
  Object.assign(task, { state: 'active', message: '正在准备安装…', done: 0, total: undefined });
  if (!el('game-install').hidden) {
    installPageTask = task;
    installForm.hidden = true;
    el('install-progress').hidden = false;
  }
  el('install-complete-toast').hidden = true;
  localBusy = true;
  renderInstallTasks();
  if (installPageTask === task) slideIn(el('install-progress'), 0, 14, 220);
  try {
    task.instance = await api.createInstance(task.input);
    task.state = task.instance ? 'complete' : 'cancelled';
    task.message = task.instance ? '已添加到实例列表。' : '已取消安装。';
  } catch (error) {
    task.state = 'failed';
    task.message = error.message;
    displayStatus(error.message, 'error');
  } finally {
    if (task.instance) await report(refreshInstances);
    task.views = [];
    activeInstall = undefined;
    localBusy = false;
    renderInstallTasks();
  }
  if (task.state !== 'complete') return;
  updateDetailInstance();
  displayStatus('已安装 ' + task.instance.name);
  if (!el('game-install').hidden && installPageTask === task) {
    await viewInstalledInstance(task.instance.id);
  } else {
    showInstallToast(task.instance);
  }
}

async function viewInstalledInstance(id) {
  if (instanceEditor) {
    leaveInstanceEditor(() => viewInstalledInstance(id));
    return;
  }
  if (selectedInstance()?.id !== id) await selectInstance(id);
  if (selectedInstance()?.id !== id) return;
  el('install-complete-toast').hidden = true;
  showPage('instances');
  el('active-instance-name').focus({ preventScroll: true });
}

function showInstallToast(instance) {
  toastInstance = instance;
  el('install-toast-title').textContent = '安装完成';
  el('install-toast-name').textContent = instance.name;
  el('install-complete-toast').hidden = false;
  scheduleInstallToastClose();
}

function scheduleInstallToastClose() {
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => {
    const toast = el('install-complete-toast');
    if (!toast.matches(':hover, :focus-within')) toast.hidden = true;
  }, 7500);
}

el('open-download-tasks').addEventListener('click', () => {
  showPage('download-tasks');
  el('back-from-tasks').focus({ preventScroll: true });
});
el('back-from-tasks').addEventListener('click', () => {
  showPage('instances');
  el('open-download-tasks').focus({ preventScroll: true });
});
el('back-from-install').addEventListener('click', backFromInstall);
el('install-toast-view').addEventListener('click', () => viewInstalledInstance(toastInstance.id));
el('install-toast-close').addEventListener('click', () => { el('install-complete-toast').hidden = true; });
el('install-complete-toast').addEventListener('mouseleave', scheduleInstallToastClose);
el('install-complete-toast').addEventListener('focusout', scheduleInstallToastClose);
el('reload-install-options').addEventListener('click', () => {
  if (installDraft.games.length) updateInstallLoader();
  else if (!installDraft.loading) loadInstallVersions(installDraft, true);
});
installSnapshots.addEventListener('change', () => {
  fillInstallVersions(installVersion.value);
  updateInstallLoader();
});
installForm.addEventListener('change', event => {
  if (event.target === installVersion || event.target.name === 'loader') updateInstallLoader();
});
installName.addEventListener('input', () => { installName.setCustomValidity(''); });
installForm.addEventListener('submit', event => {
  event.preventDefault();
  if (!canMutate() || !installDraft.ready) return;
  const name = installName.value.trim();
  if (!name) {
    installName.setCustomValidity('请输入实例名称。');
    installName.reportValidity();
    return;
  }
  startGameInstall({
    origin: installDraft.origin,
    input: { name, version: installVersion.value, loader: installForm.elements.loader.value, loaderVersion: installLoaderVersion.value },
    versionType: installDraft.games.find(item => item.id === installVersion.value)?.type,
  });
});
