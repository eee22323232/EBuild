async function finishInstanceImport(instance) {
  await refreshInstances();
  showPage('instances');
  displayStatus('已导入 ' + instance.name);
  showInstanceToast('导入完成', instance.name, instance);
  el('active-instance-name').focus({ preventScroll: true });
}

function importVersionLabel(item) {
  return instanceVersionLabel(item) + (item.loaderVersion ? ' ' + item.loaderVersion : '');
}

function confirmImport(panel, label, action) {
  const actions = element('div', undefined, 'form-actions');
  const accept = button(label, () => {
    if (!canMutate()) return;
    const start = () => importInstanceFrom(action);
    if (instanceEditor) {
      leaveInstanceEditor(start);
      return;
    }
    closeModal();
    start();
  }, 'primary');
  actions.append(button('取消', closeModal), accept);
  panel.body.append(actions);
}

function confirmFolderImport(result, dropped = false) {
  const panel = showModal(dropped ? '确认导入游戏文件夹' : '选择要添加的版本');
  const [first] = result.choices;
  panel.body.append(element('p', first.externalPath, 'import-confirm-path'));
  let selectedProfile = () => first.externalProfile;
  if (result.choices.length > 1) {
    const versions = element('select');
    options(versions, result.choices.map(item => [item.externalProfile,
      item.externalProfile + ' · ' + importVersionLabel(item)]));
    panel.body.append(element('p', '这个文件夹有多个版本。使用同一目录的实例会共用存档、模组和设置。'),
      field('游戏版本', versions));
    selectedProfile = () => versions.value;
  } else {
    panel.body.append(element('strong', first.name), element('p', importVersionLabel(first)));
  }
  confirmImport(panel, '添加实例', () => api.importExternal(result.source, selectedProfile()));
}

function confirmPackImport(result) {
  const panel = showModal('确认导入整合包');
  panel.body.append(element('strong', result.instance.name), element('p', importVersionLabel(result.instance)),
    element('p', result.source, 'import-confirm-path'));
  confirmImport(panel, '安装整合包', () => api.importPack(result.source));
}

async function importInstanceFrom(action, panel = null) {
  const result = await run(action, panel?.error);
  if (!result) return;
  if (panel) closeModal();
  if (result.choices) confirmFolderImport(result);
  else await report(() => finishInstanceImport(result));
}

async function relocateExternal(instance) {
  if (!canMutate() || gameState(instance.id)) return;
  if (instanceEditor && instanceEditor.snapshot() !== instanceEditor.saved) {
    leaveInstanceEditor(() => relocateExternal(instance));
    return;
  }
  await run(async () => {
    const updated = await api.relocateInstance(instance.id);
    if (!updated) return;
    await refreshInstances();
    if (instanceEditor) editInstance(instances.find(item => item.id === instance.id));
    displayStatus('已重新定位 ' + updated.name);
  });
}

el('open-import').addEventListener('click', () => {
  if (!canMutate()) return;
  const panel = showModal('导入');
  const actions = element('div', undefined, 'form-actions');
  const pickFolder = button('导入游戏文件夹', () => importInstanceFrom(() => api.importExternal(), panel), 'primary');
  const pickPack = button('导入整合包', () => importInstanceFrom(() => api.importPack(), panel));
  actions.append(pickFolder, pickPack);
  panel.body.append(actions);
});

el('relocate-active-instance').addEventListener('click', () => {
  const instance = selectedInstance();
  if (instance?.externalPath) relocateExternal(instance);
});

const importDropHint = el('import-drop-hint');
window.addEventListener('dragover', event => {
  if (!event.dataTransfer?.types.includes('Files')) return;
  event.preventDefault();
  const ready = canMutate() && !modal.open;
  event.dataTransfer.dropEffect = ready ? 'copy' : 'none';
  importDropHint.hidden = !ready;
});
window.addEventListener('dragleave', event => {
  if (!event.relatedTarget) importDropHint.hidden = true;
});
window.addEventListener('drop', async event => {
  importDropHint.hidden = true;
  if (!event.dataTransfer?.types.includes('Files')) return;
  event.preventDefault();
  if (!canMutate() || modal.open) return;
  if (event.dataTransfer.files.length !== 1) {
    displayStatus('请一次拖入一个游戏文件夹或整合包。', 'error');
    return;
  }
  const file = event.dataTransfer.files[0];
  const preview = await run(() => api.previewDroppedImport(file));
  if (!preview || modal.open) return;
  if (preview.kind === 'folder') confirmFolderImport(preview, true);
  else if (preview.kind === 'pack') confirmPackImport(preview);
});
window.addEventListener('focus', () => {
  if (canMutate() && instances.some(instance => instance.externalPath)) report(refreshInstances);
});
