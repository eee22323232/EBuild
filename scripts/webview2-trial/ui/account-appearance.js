const playerPreview = new PlayerPreview(el('account-model'), el('home-account-avatar'));
const accountCard = el('home-account-card');
const accountMenu = el('home-account-menu');
const offlineModel = el('offline-skin-model');
const officialProfilePages = {
  name: 'https://www.minecraft.net/msaprofile/mygames',
  skin: 'https://www.minecraft.net/msaprofile/mygames/editskin',
  cape: 'https://www.minecraft.net/msaprofile/mygames',
};
let offlineLook = {};
let triedLegacyProfileRefresh = false;

function renderAccountAppearance() {
  if (!settings) return;
  const official = settings.accountMode === 'microsoft';
  const name = official ? account?.name || '未登录' : settings.username;
  const identity = (official ? '官方登录' : '离线登录') + ' · ' + name;
  el('home-account-name').textContent = identity;
  el('account-preview-name').textContent = identity;
  el('offline-appearance-controls').hidden = official;
  el('official-appearance-controls').hidden = !official || !account;
  el('refresh-account-profile').hidden = !account;
  el('remove-offline-skin').hidden = !offlineLook.skin;
  el('remove-offline-cape').hidden = !offlineLook.cape;
  el('official-appearance-note').textContent = account?.cape
    ? '官网修改后，回到这里刷新档案。'
    : '当前未装备披风。官网修改后，回到这里刷新档案。';
  playerPreview.setTextures(official ? account || {} : {
    ...offlineLook, variant: offlineModel.value || settings.offlineSkinModel,
  });
}

function closeAccountMenu(restoreFocus = false) {
  accountMenu.hidden = true;
  accountCard.setAttribute('aria-expanded', 'false');
  if (restoreFocus) accountCard.focus();
}

function openAccountMenu() {
  if (!settings) return;
  accountMenu.hidden = false;
  accountCard.setAttribute('aria-expanded', 'true');
  accountMenu.firstElementChild.focus();
}

function manageAppearance(part) {
  if (!settings) return;
  if (settings.accountMode === 'microsoft' && account) {
    report(() => api.openResourceLink(officialProfilePages[part]));
    return;
  }
  showPage('accounts');
  if (settings.accountMode === 'microsoft') {
    el('microsoft-login').focus();
  } else if (part === 'name') {
    accountForm.elements.username.focus();
    accountForm.elements.username.select();
  } else {
    if (part === 'cape') playerPreview.turnTo(true);
    el('import-offline-' + part).focus();
  }
}

function refreshPlayerProfile() {
  return run(async () => {
    const refreshed = await api.refreshAccount();
    if (!refreshed) return;
    account = refreshed;
    showAccount();
    displayStatus('正版档案已刷新');
  });
}

function refreshLegacyAccount() {
  if (!account?.needsRefresh || triedLegacyProfileRefresh || !canMutate()) return;
  triedLegacyProfileRefresh = true;
  refreshPlayerProfile();
}

accountCard.addEventListener('click', () => {
  closeAccountMenu();
  showPage('accounts');
  el('account-preview-title').focus();
});
accountCard.addEventListener('contextmenu', event => {
  event.preventDefault();
  openAccountMenu();
});
accountCard.addEventListener('keydown', event => {
  if (event.key !== 'ContextMenu' && !(event.shiftKey && event.key === 'F10')) return;
  event.preventDefault();
  openAccountMenu();
});
accountMenu.addEventListener('keydown', event => {
  const items = [...accountMenu.children];
  const index = items.indexOf(document.activeElement);
  if (event.key === 'Escape') {
    event.preventDefault();
    closeAccountMenu(true);
  } else if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
    event.preventDefault();
    items[(index + (event.key === 'ArrowDown' ? 1 : items.length - 1)) % items.length].focus();
  } else if (event.key === 'Home' || event.key === 'End') {
    event.preventDefault();
    items[event.key === 'Home' ? 0 : items.length - 1].focus();
  }
});
accountMenu.addEventListener('focusout', event => {
  if (!accountMenu.contains(event.relatedTarget)) closeAccountMenu();
});
document.addEventListener('pointerdown', event => {
  if (!accountCard.contains(event.target) && !accountMenu.contains(event.target)) closeAccountMenu();
});
for (const item of accountMenu.querySelectorAll('[data-profile-action]')) {
  item.addEventListener('click', () => {
    closeAccountMenu(true);
    manageAppearance(item.dataset.profileAction);
  });
}
for (const item of document.querySelectorAll('[data-official-profile]')) {
  item.addEventListener('click', () => manageAppearance(item.dataset.officialProfile));
}
el('preview-front').addEventListener('click', () => playerPreview.turnTo(false));
el('preview-back').addEventListener('click', () => playerPreview.turnTo(true));
el('refresh-account-profile').addEventListener('click', refreshPlayerProfile);
offlineModel.addEventListener('change', renderAccountAppearance);
for (const [part, label] of [['skin', '皮肤'], ['cape', '披风']]) {
  el('import-offline-' + part).addEventListener('click', () => run(async () => {
    const result = await api.importAppearance(part);
    if (!result) return;
    offlineLook = result;
    playerPreview.turnTo(part === 'cape');
    renderAccountAppearance();
    displayStatus(label + '已导入');
  }));
  el('remove-offline-' + part).addEventListener('click', () => run(async () => {
    offlineLook = await api.removeAppearance(part);
    renderAccountAppearance();
    el('import-offline-' + part).focus();
    displayStatus(label + '已还原');
  }));
}
report(async () => {
  offlineLook = await api.offlineAppearance();
  renderAccountAppearance();
});
