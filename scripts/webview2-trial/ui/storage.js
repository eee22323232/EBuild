const storageLabels = {
  game: ['游戏本体与公共库', '多个实例共用的文件'],
  java: ['Java', '已安装的运行环境'],
  instances: ['实例文件', '存档、模组、材质和实例设置'],
  archives: ['安装包缓存', '已下载的安装包，需要时可重新下载'],
  transfers: ['未完成的下载', '清理后，未下载完的文件需要重新下载'],
  backups: ['模组旧版备份', '清理后无法用这些备份还原旧版模组'],
  installations: ['安装任务', '保留的整合包和安装进度，可在下载任务中移除'],
  metadata: ['索引与其他文件', '包含文件校验记录，清理缓存时保留'],
};

function storageSize(bytes) {
  if (bytes < 1024) return bytes + ' B';
  const unit = bytes >= 1024 ** 3 ? ['GB', 1024 ** 3] : bytes >= 1024 ** 2 ? ['MB', 1024 ** 2] : ['KB', 1024];
  return (bytes / unit[1]).toLocaleString('zh-CN', { maximumFractionDigits: 2 }) + ' ' + unit[0];
}

async function openStorage(resultMessage = '') {
  const panel = showModal('空间管理');
  panel.body.append(element('p', '正在统计文件大小…', 'results-info'));
  const snapshot = await report(() => api.storage(), panel.error);
  if (!snapshot || !panel.body.isConnected) return;
  const summary = element('div', undefined, 'storage-summary');
  summary.append(element('strong', '游戏数据 ' + storageSize(snapshot.total)),
    element('small', '程序文件 ' + storageSize(snapshot.programBytes) + ' · 界面缓存 ' + storageSize(snapshot.webBytes)),
    element('small', '按文件大小统计；不含共享 WebView2 运行时、外部游戏目录和其他位置的 Java。'),
    element('p', snapshot.root, 'path'));
  summary.querySelector('.path').title = snapshot.root;
  if (resultMessage) summary.append(element('p', resultMessage, 'results-info'));
  if (snapshot.skipped) summary.append(element('small', snapshot.skipped + ' 个无法访问的项目或链接已跳过，占用统计可能不完整。'));
  const list = element('div', undefined, 'storage-list');
  const chosen = new Map();
  const amount = element('p', '', 'storage-selection');
  const clean = button('清理所选…', () => confirmStorageClean(snapshot, [...chosen.values()].filter(x => x.control.checked).map(x => x.item)), 'primary');
  clean.dataset.mutates = '';
  function update() {
    const items = [...chosen.values()].filter(x => x.control.checked);
    amount.textContent = '预计释放 ' + storageSize(items.reduce((sum, x) => sum + x.item.bytes, 0));
    clean.disabled = !items.length || !canMutate();
  }
  for (const item of snapshot.categories) {
    const [title, description] = storageLabels[item.id];
    const row = element('label', undefined, 'storage-row');
    const name = element('span');
    name.append(element('strong', title), element('small', description));
    const control = element('input');
    control.type = 'checkbox';
    control.disabled = !item.cleanable || !item.bytes;
    control.hidden = !item.cleanable;
    control.setAttribute('aria-label', '清理' + title);
    if (item.cleanable) chosen.set(item.id, { item, control });
    control.addEventListener('change', update);
    row.append(control, name, element('span', storageSize(item.bytes), 'storage-bytes'));
    list.append(row);
  }
  const actions = element('div', undefined, 'form-actions');
  actions.append(button('刷新', () => report(openStorage)), clean);
  panel.body.replaceChildren(summary, list, amount, actions);
  update();
}

function confirmStorageClean(snapshot, items) {
  if (!items.length) return;
  const panel = showModal('确认清理所选文件？');
  panel.body.append(element('p', items.map(x => storageLabels[x.id][0]).join('、') + '，预计释放 ' + storageSize(items.reduce((sum, x) => sum + x.bytes, 0)) + '。'));
  if (items.some(x => x.id === 'backups')) panel.body.append(element('p', '这些模组旧版备份将永久删除。'));
  if (items.some(x => x.id === 'transfers')) panel.body.append(element('p', '未完成的文件将从头下载，已经安装的游戏和存档会保留。'));
  const actions = element('div', undefined, 'form-actions');
  const accept = button('确认清理', async () => {
    accept.disabled = true;
    const result = await run(() => api.cleanStorage(snapshot.token, items.map(x => x.id)), panel.error);
    if (result) {
      const message = '已释放 ' + storageSize(result.freed) + (result.skipped ? '，跳过 ' + result.skipped + ' 个已变化或无法清理的文件。' : '。');
      displayStatus(message);
      if (panel.body.isConnected) await openStorage(message);
    } else accept.disabled = false;
  }, 'primary');
  actions.append(button('取消', () => report(openStorage)), accept);
  panel.body.append(actions);
}
