(() => {
  if (window !== window.top || location.href !== 'https://ebuild-trial.example/index.html') return;
  const channels = /* CHANNELS */ {};
  const pending = new Map();
  const status = new Set();
  let next = 0;
  chrome.webview.addEventListener('message', ({ data }) => {
    if (data.status) {
      for (const callback of status) callback(data.status);
      return;
    }
    const p = pending.get(data.id);
    if (!p) return;
    pending.delete(data.id);
    if (data.error) p.reject(new Error(data.error));
    else p.resolve(data.value);
  });
  const api = Object.fromEntries(Object.entries(channels).map(([name, channel]) => [name, (...args) => new Promise((resolve, reject) => {
    const id = ++next;
    pending.set(id, { resolve, reject });
    chrome.webview.postMessage({ id, channel, args });
  })]));
  api.onStatus = callback => {
    status.add(callback);
    return () => status.delete(callback);
  };
  Object.defineProperty(window, 'launcher', { value: Object.freeze(api) });
})();
