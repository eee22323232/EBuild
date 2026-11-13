const path = require('node:path');

const defaults = {
  username: 'Player', accountMode: 'offline', clientId: '', selected: '',
  memory: 4096, java: '', width: 0, height: 0, afterLaunch: 'keep',
  theme: 'system', concurrency: 12, proxyMode: 'system', proxy: '',
  gc: '', jvmArgs: [], gameArgs: [], logs: true, dataRoot: '',
};

function int(value, min, max, message) {
  if (!Number.isInteger(value) || value < min || value > max) throw new Error(message);
  return value;
}

function oneOf(value, choices, message) {
  if (!choices.includes(value)) throw new Error(message);
  return value;
}

function argumentsList(value) {
  if (!Array.isArray(value) || value.length > 80 ||
      value.some(arg => typeof arg !== 'string' || arg.length > 1024 || /[\r\n\0]/.test(arg))) {
    throw new Error('启动参数格式无效。每行应为一个参数。');
  }
  return value;
}

function gameSettings(values) {
  const java = String(values.java ?? '').trim();
  if (java && (!path.isAbsolute(java) || !/^javaw?\.exe$/i.test(path.basename(java)))) {
    throw new Error('请选择 Java 安装目录下的 java.exe 或 javaw.exe。');
  }
  const width = values.width || 0;
  const height = values.height || 0;
  if (width || height) {
    int(width, 320, 16384, '游戏窗口宽度应为 320–16384。');
    int(height, 240, 16384, '游戏窗口高度应为 240–16384。');
  }
  return {
    java, width, height,
    memory: int(values.memory, 1024, 65536, '内存应在 1024–65536 MB 之间。'),
    afterLaunch: oneOf(values.afterLaunch, ['keep', 'minimize'], '无效的启动后行为。'),
    gc: oneOf(values.gc, ['', 'G1', 'Z', 'Parallel', 'Serial'], '无效的垃圾回收器。'),
    jvmArgs: argumentsList(values.jvmArgs), gameArgs: argumentsList(values.gameArgs), logs: Boolean(values.logs),
  };
}

function overrides(value) {
  const checked = gameSettings({ ...defaults, ...value });
  return Object.fromEntries(Object.keys(checked).filter(key => Object.hasOwn(value, key)).map(key => [key, checked[key]]));
}

function clean(value) {
  if (!/^[A-Za-z0-9_]{3,16}$/.test(value.username ?? '')) throw new Error('离线玩家名称需为 3–16 位英文字母、数字或下划线。');
  const clientId = String(value.clientId ?? '').trim();
  if (clientId && !/^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i.test(clientId)) throw new Error('微软 Client ID 格式无效。');
  const proxy = String(value.proxy ?? '').trim();
  if (value.proxyMode === 'manual') {
    let url;
    try { url = new URL(proxy); } catch { throw new Error('请输入 HTTP 或 HTTPS 代理地址。'); }
    if (!['http:', 'https:'].includes(url.protocol) || url.pathname !== '/' || url.search || url.hash) {
      throw new Error('代理地址应类似 http://127.0.0.1:7890。');
    }
  }
  return {
    ...gameSettings(value), username: value.username, clientId,
    selected: String(value.selected ?? ''), dataRoot: String(value.dataRoot ?? ''),
    accountMode: oneOf(value.accountMode, ['offline', 'microsoft'], '无效的账号类型。'),
    theme: oneOf(value.theme, ['light', 'dark', 'system'], '无效的外观设置。'),
    concurrency: int(value.concurrency, 1, 32, '下载并发数应在 1–32 之间。'),
    proxyMode: oneOf(value.proxyMode, ['system', 'manual', 'direct'], '无效的代理设置。'),
    proxy,
  };
}

module.exports = { defaults, clean, overrides };
