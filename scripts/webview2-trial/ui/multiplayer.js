const lanPage = el('multiplayer');
const lanHost = el('lan-host');
const lanPort = el('lan-port');
const lanGuest = el('lan-guest-instance');
let lan = { room: null, working: false, node: '' };
let hostChoices = '';
let guestChoices = '';
let detectingPort = false;
let detectedPort = 0;
let portChecked = false;
let lanError = '';

function lanHostOptions() {
  return instances.filter(instance => gameState(instance.id) === 'running')
    .map(instance => [instance.id, instance.name + ' · ' + instanceVersionLabel(instance)]);
}

function lanVersion(room) {
  return instanceVersionLabel(room) + (room.loaderVersion ? ' ' + room.loaderVersion : '');
}

function canQuickJoin(version) {
  const match = /^(\d{1,3})\.(\d{1,3})(?:\.\d{1,3})?$/.exec(version);
  return match && (Number(match[1]) >= 26 || (Number(match[1]) === 1 && Number(match[2]) >= 20));
}

function renderLanControls() {
  const { room, working } = lan;
  const hosts = lanHostOptions();
  const hostSignature = JSON.stringify(hosts);
  if (hostChoices !== hostSignature) {
    hostChoices = hostSignature;
    const previous = lanHost.value;
    options(lanHost, hosts.length ? hosts : [['', '请先启动游戏']]);
    if (hosts.some(([id]) => id === previous)) lanHost.value = previous;
    else if (hosts.some(([id]) => id === selectedInstance()?.id)) lanHost.value = selectedInstance().id;
    if (lanHost.value !== previous) { detectedPort = 0; portChecked = false; lanPort.value = ''; }
    updateSelectPicker(lanHost);
  }

  el('lan-lobby').hidden = Boolean(room);
  el('lan-room').hidden = !room;
  el('lan-settings').disabled = working || Boolean(room);
  el('lan-create').disabled = working || !lanHost.value;
  el('lan-join').disabled = working;
  lanHost.disabled = working || !hosts.length;
  lanPort.disabled = working || !hosts.length;
  el('lan-code').disabled = working;
  el('lan-detect').disabled = working || !lanHost.value || detectingPort;
  el('lan-cancel').hidden = !working;
  el('lan-progress').textContent = lan.progress || lanError || lan.notice || '';
  const hint = el('lan-host-hint');
  lanPort.placeholder = detectedPort ? '已识别 ' + detectedPort : '自动识别';
  hint.hidden = Boolean(lanHost.value) && (!portChecked || Boolean(detectedPort));
  hint.textContent = !lanHost.value ? '在 EBuild 启动一个实例后，这里会显示可选择的游戏。'
    : '暂未识别到端口。请开放局域网，或填入游戏聊天栏显示的端口。';

  if (!room) return;
  el('lan-room-title').textContent = room.host ? room.name : '已加入好友房间';
  el('lan-room-version').textContent = lanVersion(room);
  el('lan-connection').textContent = room.connection || (room.host ? '等待好友' : '已连接');
  el('lan-room-host').hidden = !room.host;
  el('lan-room-guest').hidden = room.host;
  el('lan-leave').textContent = room.host ? '关闭房间' : '退出联机';
  el('lan-rotate').disabled = working;
  el('lan-copy-code').disabled = working;
  if (room.host) {
    if (el('lan-share-code').value !== room.invite) el('lan-share-code').value = room.invite;
    return;
  }

  if (el('lan-address').value !== room.address) el('lan-address').value = room.address;
  const matches = instances.filter(instance => !instance.externalMissing &&
    instance.version === room.version && instance.loader === room.loader &&
    (!room.loaderVersion || instance.loaderVersion === room.loaderVersion));
  const ready = matches.filter(instance => canQuickJoin(instance.version) && canLaunchInstance(instance.id));
  const guestItems = ready.map(instance => [instance.id, instance.name]);
  const guestSignature = JSON.stringify(guestItems);
  if (guestChoices !== guestSignature) {
    guestChoices = guestSignature;
    const previous = lanGuest.value;
    options(lanGuest, guestItems);
    if (guestItems.some(([id]) => id === previous)) lanGuest.value = previous;
    else if (guestItems.some(([id]) => id === selectedInstance()?.id)) lanGuest.value = selectedInstance().id;
    updateSelectPicker(lanGuest);
  }
  el('lan-quick').hidden = !ready.length;
  const compatibility = el('lan-compatibility');
  compatibility.hidden = Boolean(ready.length);
  compatibility.textContent = matches.length ? '可以复制地址，从已启动的游戏或不支持直接加入的版本连接。'
    : 'EBuild 未找到匹配实例。请选择与房主相同的 Minecraft 版本、加载器和整合包。';
}

function applyLanState(state) {
  if (!state) return;
  lan = state;
  renderLanControls();
}

async function lanAction(action) {
  lanError = '';
  renderLanControls();
  try { return await action(); }
  catch (error) { lanError = error.message; renderLanControls(); }
}

async function detectLanPort(explicit = false) {
  const id = lanHost.value;
  if (!id || lan.room || lan.working || detectingPort) return;
  detectingPort = true;
  el('lan-detect').disabled = true;
  try {
    const port = await api.lanPort(id);
    if (id !== lanHost.value) return;
    detectedPort = port;
    portChecked = explicit || Boolean(port) || portChecked;
  } catch (error) {
    if (explicit) lanError = error.message;
  } finally {
    detectingPort = false;
    renderLanControls();
  }
}

function openLanPage() {
  renderLanControls();
  detectLanPort();
}

lanHost.addEventListener('change', () => {
  lanPort.value = '';
  portChecked = false;
  detectedPort = 0;
  detectLanPort();
});
el('lan-detect').addEventListener('click', () => detectLanPort(true));
el('lan-create-form').addEventListener('submit', event => {
  event.preventDefault();
  if (!lanHost.value || lan.working) return;
  lanAction(() => api.lanCreate(lanHost.value, lanPort.valueAsNumber || 0));
});
el('lan-join-form').addEventListener('submit', event => {
  event.preventDefault();
  const input = el('lan-code');
  if (lan.working || !input.value.trim()) return;
  lanAction(async () => { if (await api.lanJoin(input.value.trim())) input.value = ''; });
});
el('lan-copy-code').addEventListener('click', () => lanAction(async () => {
  if (await api.lanCopy('invite')) displayStatus('已复制邀请码');
}));
el('lan-copy-address').addEventListener('click', () => lanAction(async () => {
  if (await api.lanCopy('address')) displayStatus('已复制游戏地址');
}));
el('lan-launch').addEventListener('click', () => {
  const instance = instances.find(item => item.id === lanGuest.value);
  if (instance && lan.room && !lan.room.host) launchInstance(instance, null, lan.room.id);
});
el('lan-cancel').addEventListener('click', () => lanAction(() => api.lanLeave()));
el('lan-leave').addEventListener('click', () => lanAction(() => api.lanLeave()));
el('lan-rotate').addEventListener('click', () => {
  const panel = showModal('更换邀请码？');
  panel.body.append(element('p', '换码后旧邀请码失效，已加入的好友需要用新码重新加入。'));
  const actions = element('div', undefined, 'form-actions');
  actions.append(button('取消', closeModal), button('更换', () => {
    closeModal();
    lanAction(() => api.lanRotate());
  }, 'primary'));
  panel.body.append(actions);
});
el('lan-settings').addEventListener('click', () => {
  const panel = showModal('联机设置');
  const form = element('form');
  const input = element('input');
  input.value = lan.node || '';
  input.placeholder = '留空时自动选择';
  input.autocomplete = 'off';
  input.spellcheck = false;
  input.maxLength = 280;
  form.append(field('自定义节点', input), element('small', '格式为 tcp://主机:端口 或 udp://主机:端口。使用自定义节点时，好友需在这里填写同一地址。', 'lan-hint'));
  const actions = element('div', undefined, 'form-actions');
  const save = button('保存', () => {}, 'primary');
  save.type = 'submit';
  actions.append(button('取消', closeModal), save);
  form.append(actions);
  form.addEventListener('submit', async event => {
    event.preventDefault();
    save.disabled = true;
    const result = await report(() => api.lanSaveNode(input.value.trim()), panel.error);
    if (result) { applyLanState(result); closeModal(); }
    else save.disabled = false;
  });
  panel.body.append(form);
  input.focus();
});

setInterval(() => {
  if (!document.hidden && !lanPage.hidden) detectLanPort();
}, 3500);
