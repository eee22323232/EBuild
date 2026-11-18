const gameThreads = el('game-threads');
const gameThreadButton = el('open-game-threads');
const instanceMenu = el('instance-context-menu');
const launchPrompts = [];
const forcePrompts = [];
const gameLabels = { queued: '排队中', preparing: '准备中', running: 'Java 已启动', stopping: '正在停止' };
const preparationLabels = { java: '准备 Java', account: '验证微软账号', download: '准备游戏文件', install: '安装加载器', starting: '正在启动 Java' };
const launchDiagnostics = new Map();
const diagnosticRows = new Map();
let preparation;
let pollingDiagnostics = false;
let menuTarget;
let leavingGames = false;

function gameState(id) { return gameSessions.get(id)?.state; }
function canLaunchInstance(id) { return Boolean(settings) && !leavingGames && !gameState(id) &&
  !instances.find(instance => instance.id === id)?.externalMissing; }

function elapsedSince(time) {
  const seconds = Math.max(0, Math.floor((Date.now() - time) / 1000));
  return seconds < 60 ? seconds + ' 秒' : Math.floor(seconds / 60) + ' 分 ' + (seconds % 60) + ' 秒';
}

function launchDetails(entry) {
  const { id, state, startedAt } = entry;
  const phase = gameLabels[state];
  if (state === 'preparing') {
    const progress = preparation?.id === id ? preparation.update : null;
    return {
      phase: preparationLabels[progress?.phase] || phase,
      detail: (progress?.message || '正在准备启动') + (progress?.total ? ' · ' + progress.done + ' / ' + progress.total : ''),
      time: '已准备 ' + elapsedSince(startedAt),
      loading: true
    };
  }
  if (state !== 'running') return { phase };
  if (!entry.logs) return { phase, detail: '已关闭启动日志记录' };

  const diagnostic = launchDiagnostics.get(id);
  if (diagnostic?.signal === 'ready') return { phase: '游戏已就绪', detail: diagnostic.summary };
  if (diagnostic?.signal === 'window-running') return { phase: '游戏运行中', detail: diagnostic.summary };
  const lastLogAt = diagnostic?.lastLogAt || 0;
  const quiet = Date.now() - Math.max(lastLogAt, startedAt) >= 60000;
  let detail = diagnostic?.summary || (lastLogAt ? '已收到本次启动日志' : '等待本次启动日志');
  if (quiet) detail += lastLogAt ? '；日志已 ' + elapsedSince(lastLogAt) + ' 未更新' : '；已等待超过 1 分钟';
  return {
    phase: diagnostic?.signal === 'language-timeout' ? '汉化包下载超时' : quiet ? phase : '游戏加载中',
    detail,
    time: 'Java 已启动 ' + elapsedSince(startedAt),
    loading: !quiet && diagnostic?.signal !== 'language-timeout'
  };
}

function updateLaunchProgress(update) {
  const pending = [...gameSessions.values()].find(entry => entry.state === 'preparing');
  if (!pending || !Object.hasOwn(preparationLabels, update.phase)) return;
  preparation = { id: pending.id, update };
  updateDiagnosticLabels();
}

function updateDiagnosticLabels() {
  const active = gameSessions.get(selectedInstance()?.id);
  const launchLabel = el('launch-label');
  const label = active ? launchDetails(active) : { phase: '启动游戏' };
  if (launchLabel.textContent !== label.phase) {
    launchLabel.textContent = label.phase;
    slideIn(launchLabel, 0, 4, 170);
  }
  el('launch-dot').hidden = !label.loading;
  for (const [id, nodes] of diagnosticRows) {
    const entry = gameSessions.get(id);
    if (!entry) continue;
    const { phase, detail, time, loading } = launchDetails(entry);
    if (nodes.phase.textContent !== phase) nodes.phase.textContent = phase;
    if (nodes.detail.textContent !== (detail || '')) nodes.detail.textContent = detail || '';
    if (nodes.time.textContent !== (time || '')) nodes.time.textContent = time || '';
    nodes.detail.hidden = !detail;
    nodes.time.hidden = !time;
    nodes.dot.hidden = !loading;
  }
}

async function refreshLaunchDiagnostics() {
  if (document.hidden || pollingDiagnostics) return;
  const entries = [...gameSessions.values()].filter(entry =>
    entry.state === 'running' && entry.logs && !['ready', 'window-running'].includes(launchDiagnostics.get(entry.id)?.signal));
  if (!entries.length) { updateDiagnosticLabels(); return; }
  pollingDiagnostics = true;
  try {
    await Promise.allSettled(entries.map(async entry => {
      const result = await api.diagnoseGame(entry.id);
      if (!result || result.startedAt !== gameSessions.get(entry.id)?.startedAt) return;
      launchDiagnostics.set(entry.id, result);
    }));
    updateDiagnosticLabels();
  } finally {
    pollingDiagnostics = false;
  }
}

async function copyLaunchDiagnostics(instance, id) {
  const entry = gameSessions.get(id);
  if (!entry) return;
  const { phase, detail, time } = launchDetails(entry);
  const summary = ['EBuild 启动诊断', instance?.name, instance && instanceVersionLabel(instance), phase, detail, time].filter(Boolean).join('\n');
  if (await report(() => api.copyGameDiagnostic(summary))) displayStatus('已复制启动诊断');
}

function updateGameControls() {
  const item = selectedInstance();
  const state = item && gameState(item.id);
  const stop = el('stop-active-game');
  el('launch').disabled = !item || selectingInstance || !canLaunchInstance(item.id);
  el('launch-icon').hidden = Boolean(state);
  el('active-game-details').hidden = !state;
  stop.hidden = !['running', 'stopping'].includes(state);
  stop.disabled = state === 'stopping';
  stop.textContent = state === 'stopping' ? '正在停止' : '停止';
  for (const id of ['change-directory', 'reset-directory']) el(id).disabled = !canMutate() || gameSessions.size > 0;
  for (const control of document.querySelectorAll('[data-idle-instance]')) {
    control.disabled = !canMutate() || Boolean(gameState(control.dataset.idleInstance));
  }
  updateDiagnosticLabels();
}

function closeGamePopovers() {
  for (const popover of [gameThreads, instanceMenu]) if (popover.matches(':popover-open')) popover.hidePopover();
}

function renderGameThreads() {
  const entries = [...gameSessions.values()];
  gameThreadButton.hidden = entries.length === 0;
  gameThreadButton.textContent = '游戏线程 · ' + entries.length;
  if (!entries.length && gameThreads.matches(':popover-open')) gameThreads.hidePopover();
  diagnosticRows.clear();
  const items = entries.map(entry => {
    const instance = instances.find(item => item.id === entry.id);
    const row = element('li', undefined, 'game-thread-row');
    const text = element('div', undefined, 'game-thread-info');
    const nodes = { phase: element('span'), detail: element('small'), time: element('small'),
      dot: element('span', undefined, 'loading-dot') };
    const state = element('small', undefined, 'game-thread-phase');
    nodes.dot.setAttribute('aria-hidden', 'true');
    state.append(nodes.dot, nodes.phase);
    diagnosticRows.set(entry.id, nodes);
    text.append(element('strong', instance?.name || entry.id), state, nodes.detail, nodes.time);
    row.append(iconImage(loaderIconPath(instance?.loader), 'instance-icon'), text);
    const actions = element('div', undefined, 'game-thread-actions');
    if (entry.state === 'queued') {
      actions.append(button('取消排队', async () => {
        const removed = await report(() => api.cancelQueuedGame(entry.id));
        if (removed === false) displayStatus('该任务已开始准备，可以在底部取消任务。');
      }));
    } else {
      if (entry.state === 'running' || entry.state === 'stopping') {
        actions.append(button('查看日志', () => report(() => api.openGameLog(entry.id))));
      }
      actions.append(button('复制诊断', () => copyLaunchDiagnostics(instance, entry.id)));
      if (entry.state === 'running') actions.append(button('停止', () => stopGame(entry.id), 'danger-button'));
    }
    if (actions.childElementCount) row.append(actions);
    return row;
  });
  el('game-thread-list').replaceChildren(...items);
  updateDiagnosticLabels();
  positionGameThreads();
}

function applyGameStates(entries) {
  const next = new Map(entries.map(entry => [entry.id, entry]));
  for (const [id, value] of launchDiagnostics) {
    if (next.get(id)?.startedAt !== value.startedAt || next.get(id)?.state === 'preparing') launchDiagnostics.delete(id);
  }
  if (next.get(preparation?.id)?.state !== 'preparing') preparation = undefined;
  gameSessions = next;
  if (menuTarget && instanceMenu.matches(':popover-open')) populateInstanceMenu(menuTarget);
  if (!settings) return;
  renderInstances();
  refreshLaunchDiagnostics();
}

function handleGameEvent(update) {
  if (Array.isArray(update.games)) { applyGameStates(update.games); return true; }
  if (update.launchJava) {
    launchPrompts.push({ id: update.launchJava, major: update.major, room: update.room });
    showNextLaunchPrompt();
    return true;
  }
  if (update.launchError) {
    const item = instances.find(instance => instance.id === update.launchError);
    displayStatus((item ? item.name + '：' : '') + update.message, 'error');
    if (item?.externalPath) report(refreshInstances);
    return true;
  }
  if (update.gameExit) {
    const item = instances.find(instance => instance.id === update.gameExit);
    if (!busy()) displayStatus((item?.name || '游戏') + (update.code ? ' 已退出（代码 ' + update.code + '）' : ' 已退出'), update.code ? 'error' : '');
    return true;
  }
  if (update.confirmGameExit) { confirmLauncherExit(update.lan, update.gamesRunning); return true; }
  return false;
}

async function launchInstance(instance, approvedJava = null, room = '') {
  if (!canLaunchInstance(instance.id)) return;
  await report(async () => {
    try { return await api.start(instance.id, approvedJava, room); }
    catch (error) {
      if (instance.externalPath) await report(refreshInstances);
      throw error;
    }
  });
}

function showNextLaunchPrompt() {
  if (modal.open || leavingGames) return;
  const stopped = forcePrompts.shift();
  if (stopped) {
    if (gameState(stopped.id) === "running") confirmForceStop(stopped.id, stopped.identity);
    else showNextLaunchPrompt();
    return;
  }
  const next = launchPrompts.shift();
  if (!next) return;
  const instance = instances.find(item => item.id === next.id);
  if (!instance) { showNextLaunchPrompt(); return; }
  const { major } = next;
  const panel = showModal('需要 Java ' + major);
  panel.body.append(element('p', '「' + instance.name + '」需要 Java ' + major + '。本机未找到兼容的版本。'),
    element('small', '下载的 Java 将保存在启动器的游戏数据目录。'));
  const actions = element('div', undefined, 'form-actions');
  actions.append(button('取消', closeModal), button('下载并启动', () => {
    closeModal();
    launchInstance(instance, major, next.room);
  }, 'primary'));
  panel.body.append(actions);
}

async function stopGame(id) {
  const result = await report(() => api.stopGame(id));
  if (!result?.forceIdentity) return;
  forcePrompts.push({ id, identity: result.forceIdentity });
  showNextLaunchPrompt();
}

function confirmForceStop(id, identity) {
  closeGamePopovers();
  const name = instances.find(item => item.id === id)?.name || '游戏';
  const panel = showModal('强制结束游戏？');
  panel.body.append(element('p', '「' + name + '」尚未正常退出。强制结束可能丢失未保存的进度。'));
  const actions = element('div', undefined, 'form-actions');
  actions.append(button('继续等待', closeModal), button('强制结束', async () => {
    const stopped = await report(() => api.stopGame(id, identity), panel.error);
    if (stopped || !gameState(id)) closeModal();
  }, 'danger-button'));
  panel.body.append(actions);
}

function confirmLauncherExit(lan = false, gamesRunning = true) {
  if (leavingGames || el('dialog-title')?.textContent === '关闭启动器') return;
  closeGamePopovers();
  const panel = showModal('关闭启动器');
  if (lan) panel.body.append(element('p', '退出 EBuild 会结束当前的联机房间或断开好友连接。'));
  if (gamesRunning) panel.body.append(element('p', '已启动的游戏可以保留，排队中的启动任务会取消。'));
  const actions = element('div', undefined, 'form-actions');
  const exit = button(gamesRunning ? '保留游戏并退出' : '断开并退出', async () => {
    exit.disabled = true;
    leavingGames = true;
    actionState();
    if (await report(() => api.keepGamesAndExit(), panel.error)) return;
    leavingGames = false;
    exit.disabled = false;
    actionState();
  }, 'primary');
  actions.append(button('取消', closeModal), button('最小化启动器', () => {
    closeModal();
    report(() => api.minimizeLauncher());
  }), exit);
  panel.body.append(actions);
}

function confirmDeleteInstance(instance, mode = '') {
  if (!canMutate() || gameState(instance.id)) return;
  closeGamePopovers();
  const remove = mode === 'remove';
  const externalDelete = mode === 'original';
  const title = remove ? '移除实例？' : externalDelete ? '永久删除原文件夹？' : '永久删除游戏？';
  const label = remove ? '移除实例' : '永久删除';
  const panel = showModal(title);
  if (remove) panel.body.append(element('p', '将「' + instance.name + '」从 EBuild 移除。原游戏文件夹和存档会保留。'));
  else if (externalDelete) panel.body.append(element('p', '这会永久删除下方文件夹中的存档、模组和设置。原启动器也将失去这些文件。'),
    element('p', instance.externalPath, 'original-delete-path'));
  else panel.body.append(element('p', '这会永久删除「' + instance.name + '」的存档、模组和实例设置。共用的 Minecraft 文件和 Java 会保留。'));
  const name = element('div', undefined, 'delete-instance-name');
  name.append(element('strong', instance.name), button('复制名称', async () => {
    await report(() => api.copyResourceName(instance.name), panel.error);
    confirmation.focus();
  }));
  const confirmation = element('input');
  confirmation.autocomplete = 'off';
  confirmation.spellcheck = false;
  const submit = button(label, async () => {
    panel.error.textContent = '';
    submit.disabled = true;
    const deleted = await run(() => api.deleteInstance(instance.id, remove ? instance.name : confirmation.value, mode), panel.error);
    if (!deleted) { submit.disabled = !remove && confirmation.value !== instance.name; return; }
    closeModal();
    if (instanceEditor) {
      instanceEditor = undefined;
      el('instance-editor').replaceChildren();
    }
    await report(refreshInstances);
    showPage('instances');
    const done = remove ? '已移除' : '已删除';
    displayStatus(done + ' ' + instance.name);
    showInstanceToast(done, instance.name);
    (selectedInstance() ? el('active-instance-name') : el('first-instance')).focus({ preventScroll: true });
  }, 'danger-button');
  submit.disabled = !remove;
  confirmation.addEventListener('input', () => { submit.disabled = confirmation.value !== instance.name; });
  const confirmationField = field('输入实例名称以确认', confirmation);
  confirmationField.classList.add('delete-instance-confirmation');
  if (!remove) panel.body.append(name, confirmationField);
  const actions = element('div', undefined, 'form-actions');
  actions.append(button('取消', closeModal), submit);
  panel.body.append(actions);
  if (!remove) confirmation.focus();
}

function instanceRemovalButtons(instance, before = () => {}) {
  const create = (label, mode) => {
    const control = button(label, () => { before(); confirmDeleteInstance(instance, mode); }, 'danger-button');
    control.dataset.idleInstance = instance.id;
    control.disabled = !canMutate() || Boolean(gameState(instance.id));
    return control;
  };
  if (!instance.externalPath) return [create('删除游戏', '')];
  const controls = [create('移除实例', 'remove')];
  if (instance.canDeleteOriginal) controls.push(create('永久删除原文件夹', 'original'));
  return controls;
}

function populateInstanceMenu(instance) {
  const state = gameState(instance.id);
  const launch = button('启动游戏', () => { instanceMenu.hidePopover(); launchInstance(instance); });
  const edit = button('游戏设置', () => { instanceMenu.hidePopover(); editInstance(instance); });
  const remove = instanceRemovalButtons(instance, () => instanceMenu.hidePopover());
  launch.disabled = !canLaunchInstance(instance.id);
  const controls = [launch, edit, button('查看启动日志', () => {
    instanceMenu.hidePopover();
    report(() => api.openGameLog(instance.id));
  })];
  if (instance.externalMissing) controls.push(button('重新定位', () => { instanceMenu.hidePopover(); relocateExternal(instance); }));
  if (state) remove.forEach(control => { control.title = '请先停止游戏或取消排队'; });
  controls.push(...remove);
  for (const item of controls) item.setAttribute('role', 'menuitem');
  instanceMenu.replaceChildren(...controls);
}

function placeGamePopover(popover, x, y) {
  popover.style.left = Math.max(8, Math.min(x, innerWidth - popover.offsetWidth - 8)) + 'px';
  popover.style.top = Math.max(8, Math.min(y, innerHeight - popover.offsetHeight - 8)) + 'px';
}

el('instances').addEventListener('contextmenu', event => {
  const card = event.target.closest('[data-instance-id]');
  const item = card && instances.find(instance => instance.id === card.dataset.instanceId);
  if (!item) return;
  event.preventDefault();
  menuTarget = item;
  populateInstanceMenu(item);
  if (!instanceMenu.matches(':popover-open')) instanceMenu.showPopover();
  const rect = card.getBoundingClientRect();
  placeGamePopover(instanceMenu, event.clientX || rect.left + 10, event.clientY || rect.bottom);
  instanceMenu.querySelector('button:not(:disabled)')?.focus();
});

instanceMenu.addEventListener('keydown', event => {
  const items = [...instanceMenu.querySelectorAll('button:not(:disabled)')];
  const current = items.indexOf(document.activeElement);
  if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
    event.preventDefault();
    items[(current + (event.key === 'ArrowDown' ? 1 : items.length - 1)) % items.length]?.focus();
  }
});

function positionGameThreads() {
  if (!gameThreads.matches(':popover-open')) return;
  const target = gameThreadButton.getBoundingClientRect();
  placeGamePopover(gameThreads, target.right - gameThreads.offsetWidth, target.bottom + 6);
}

gameThreads.addEventListener('toggle', () => {
  positionGameThreads();
  if (gameThreads.matches(':popover-open')) refreshLaunchDiagnostics();
});
setInterval(() => { if (gameSessions.size) refreshLaunchDiagnostics(); }, 4000);
document.addEventListener('visibilitychange', () => { if (!document.hidden && gameSessions.size) refreshLaunchDiagnostics(); });

el('stop-active-game').addEventListener('click', () => {
  const instance = selectedInstance();
  if (instance) stopGame(instance.id);
});
window.addEventListener('resize', closeGamePopovers);
modal.addEventListener('close', showNextLaunchPrompt);
