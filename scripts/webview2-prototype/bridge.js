// Installed by the host only in the local top-level page.
(() => {
  if (window !== window.top || location.href !== 'https://ebuild-prototype.example/index.html') return;
  const methods = {
    getSettings: 'settings:get', account: 'account:summary', instances: 'instances:list',
    selectInstance: 'instances:select', chooseData: 'data:choose', changeData: 'data:change',
  };
  const blocked = new Set(['launch', 'edit-active-instance', 'add-instance', 'first-instance', 'import-pack',
    'import-pack-empty', 'open-download-tasks', 'data-folder', 'save-global', 'clear-cache',
    'microsoft-login', 'microsoft-logout', 'manage-java']);
  const pending = new Map();
  let id = 0;
  let exited = false;
  chrome.webview.addEventListener('message', ({ data }) => {
    if (data.backendExited) {
      exited = true;
      for (const request of pending.values()) request.reject(new Error('原型的 Node 后端已退出。'));
      pending.clear();
      return;
    }
    const request = pending.get(data.id);
    if (!request) return;
    pending.delete(data.id);
    if (data.error) request.reject(new Error(data.error));
    else request.resolve(data.value);
  });
  const launcher = Object.fromEntries(Object.entries(methods).map(([name, channel]) => [name, (...args) => (
    new Promise((resolve, reject) => {
      if (exited) { reject(new Error('原型的 Node 后端已退出。')); return; }
      const requestId = ++id;
      pending.set(requestId, { resolve, reject });
      chrome.webview.postMessage({ id: requestId, channel, args });
    })
  )]));
  launcher.onStatus = () => () => {};
  window.launcher = new Proxy(launcher, {
    get(target, property) {
      return Object.hasOwn(target, property) ? target[property] : async () => {
        throw new Error('此原型只验证首页、实例切换和游戏数据目录。');
      };
    },
  });
  // Prevent actions outside experiment scope, while preserving the production layout.
  document.addEventListener('click', event => {
    const button = event.target.closest('button');
    if (!button || (!blocked.has(button.id) && !['downloads', 'accounts'].includes(button.dataset.page))) return;
    event.preventDefault();
    event.stopImmediatePropagation();
    const status = document.getElementById('status');
    status.textContent = '原型只支持切换测试实例和选择游戏数据目录';
  }, true);
  document.addEventListener('submit', event => {
    event.preventDefault();
    event.stopImmediatePropagation();
  }, true);
})();
