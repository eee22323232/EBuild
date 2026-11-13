const { app, BrowserWindow, dialog, ipcMain, shell, nativeTheme, safeStorage, session } = require('electron');
const fs = require('node:fs/promises');
const path = require('node:path');
const { randomUUID } = require('node:crypto');
const { pathToFileURL } = require('node:url');
const { configureProxy, download, writeJson } = require('./download.cjs');
const { versionList, installGame, launch, resolveJava, javaMajor } = require('./minecraft.cjs');
const { loaderVersions } = require('./loaders.cjs');
const storage = require('./storage.cjs');
const resources = require('./resources.cjs');
const preferences = require('./preferences.cjs');
const { accounts } = require('./accounts.cjs');

const entry = path.join(__dirname, 'ui', 'index.html');
const localDev = !app.isPackaged && process.env.EBUILD_DEV_DATA;
if (localDev) app.setPath('userData', path.resolve(localDev));
const programDirectory = app.isPackaged ? path.dirname(process.execPath) : path.join(__dirname, '..');
const defaultRoot = path.join(localDev ? path.resolve(localDev) : programDirectory, '游戏数据');
const plans = new Map();
let window;
let root;
let configFile;
let settings;
let identities;
let catalog;
let catalogRequest;
let task;
let game;
let saveQueue = Promise.resolve();

function emit(update) {
  if (window && !window.isDestroyed()) window.webContents.send('status', update);
}

function handle(channel, action) {
  ipcMain.handle(channel, async (event, ...args) => {
    if (event.senderFrame?.url !== pathToFileURL(entry).href || event.senderFrame !== event.sender.mainFrame) {
      return { error: '无法验证请求来源。' };
    }
    try { return { value: await action(...args) }; }
    catch (error) { return { error: error.message || String(error) }; }
  });
}

function applyTheme() {
  nativeTheme.themeSource = settings.theme;
  const dark = nativeTheme.shouldUseDarkColors;
  window?.setTitleBarOverlay({
    color: dark ? '#191b1e' : '#f9fafb', symbolColor: dark ? '#e9ebed' : '#25292e', height: 48,
  });
}

function applyProxy() {
  const { proxyMode, proxy } = settings;
  configureProxy(proxyMode === 'manual' ? () => proxy : proxyMode === 'direct' ? undefined : async url => {
    const candidates = await session.defaultSession.resolveProxy(url);
    const first = candidates.split(';')[0].trim();
    const match = first.match(/^(PROXY|HTTPS) (\S+)$/);
    if (match) return (match[1] === 'HTTPS' ? 'https://' : 'http://') + match[2];
    if (first && first !== 'DIRECT') throw new Error('系统代理类型暂未支持，请选择手动 HTTP 代理或直连。');
  });
}

function updateSettings(patch) {
  const save = saveQueue.then(async () => {
    const next = preferences.clean({ ...settings, ...patch });
    await writeJson(configFile, next);
    const changedProxy = next.proxy !== settings.proxy || next.proxyMode !== settings.proxyMode;
    const changedTheme = next.theme !== settings.theme;
    settings = next;
    if (changedProxy) applyProxy();
    if (changedTheme) applyTheme();
    return settings;
  });
  saveQueue = save.catch(() => {});
  return save;
}

function refreshCatalog() {
  const activeRoot = root;
  catalogRequest ??= versionList(activeRoot).then(value => {
    if (root === activeRoot) catalog = value;
    return value;
  }).finally(() => { catalogRequest = null; });
  return catalogRequest;
}

async function gameVersion(id) {
  const version = (catalog ?? await refreshCatalog()).versions.find(item => item.id === id);
  if (!version) throw new Error('游戏版本不在 Mojang 版本列表中。');
  return version;
}

async function getInstance(id) {
  const item = (await storage.listInstances(root)).find(item => item.id === id);
  if (!item) throw new Error('实例不存在。');
  return item;
}

async function run(action, { duringGame = false } = {}) {
  if (task || (game && !duringGame)) throw new Error('请先结束当前游戏或下载任务。');
  const controller = new AbortController();
  task = controller;
  const progress = (phase, message, done, total) => emit({ phase, message, done, total });
  try {
    const result = await action(controller.signal, progress);
    if (!game) progress('idle', '就绪');
    return result;
  } catch (error) {
    if (!controller.signal.aborted) {
      progress('error', error.message);
      throw error;
    }
    progress('idle', '任务已取消');
    return null;
  } finally {
    task = null;
  }
}

async function addInstance(input, signal, progress, pack) {
  const version = await gameVersion(input.version);
  const saved = await storage.installInstance(root, input, async (instance, directory) => {
    if (pack) await resources.unpackPack(pack.file, pack.index, directory, settings.concurrency, signal, progress);
    await installGame(root, version, instance, directory, settings, signal, progress);
    signal.throwIfAborted();
  });
  await updateSettings({ selected: saved.id });
  return saved;
}

async function installPack(file, signal, progress) {
  const { index, instance } = await resources.readPack(file);
  return addInstance(instance, signal, progress, { file, index });
}

async function startGame(id, signal, progress) {
  const instance = await getInstance(id);
  const version = await gameVersion(instance.version);
  const effective = { ...settings, ...instance.overrides };
  if (effective.accountMode === 'microsoft') effective.account = await identities.forLaunch(signal);
  const directory = storage.instanceDirectory(root, id);
  const { child, closed } = await launch(root, version, instance, directory, effective, signal, progress);
  game = child;
  progress('running', 'Minecraft 正在运行');
  if (effective.afterLaunch === 'minimize') window?.minimize();
  closed.then(code => {
    game = null;
    if (code === 0) progress('idle', '游戏已退出');
    else progress('error', '游戏退出（代码 ' + (code ?? '未知') + '）。' +
      (effective.logs ? '可在实例目录查看日志。' : ''));
  });
}

async function openFolder(directory) {
  await fs.mkdir(directory, { recursive: true });
  const error = await shell.openPath(directory);
  if (error) throw new Error(error);
}

async function choose(title, properties, filters) {
  const { filePaths } = await dialog.showOpenDialog(window, { title, properties, filters });
  return filePaths[0] ?? '';
}

function register() {
  handle('catalog', refreshCatalog);
  handle('loaders', (version, loader) => loaderVersions(version, loader, AbortSignal.timeout(15_000)));
  handle('settings:get', () => ({ ...settings, currentRoot: root, defaultRoot, version: app.getVersion() }));
  handle('settings:save', value => {
    const { selected, dataRoot, ...editable } = value;
    return updateSettings(editable);
  });
  handle('java:select', () => choose('选择 Java', ['openFile'], [{ name: 'Java 可执行文件', extensions: ['exe'] }]));
  handle('java:list', async () => {
    const runtimes = path.join(root, 'runtimes');
    const entries = await fs.readdir(runtimes).catch(error => {
      if (error.code === 'ENOENT') return [];
      throw error;
    });
    const candidates = [...entries.map(name => path.join(runtimes, name, 'bin', 'java.exe')), 'java.exe'];
    if (settings.java) candidates.unshift(path.join(path.dirname(settings.java), 'java.exe'));
    if (process.env.JAVA_HOME) candidates.push(path.join(process.env.JAVA_HOME, 'bin', 'java.exe'));
    const list = await Promise.all([...new Set(candidates)].map(async executable => ({
      executable, major: await javaMajor(executable, AbortSignal.timeout(12_000)),
    })));
    return list.filter(value => value.major);
  });
  handle('java:install', major => run(async (signal, progress) => {
    if (![8, 16, 17, 21, 25].includes(major)) throw new Error('请选择支持的 Java 版本。');
    await resolveJava(root, major, '', progress, signal);
    return true;
  }));
  handle('data:choose', () => choose('选择游戏数据目录', ['openDirectory', 'createDirectory']));
  handle('data:change', (target, migrate) => run(async (signal, progress) => {
    if (typeof target !== 'string' || (target && !path.isAbsolute(target))) throw new Error('请选择绝对路径。');
    const destination = target || defaultRoot;
    const executableDirectory = path.resolve(programDirectory).toLowerCase();
    const targetDirectory = path.resolve(destination).toLowerCase();
    if (executableDirectory === targetDirectory || executableDirectory.startsWith(targetDirectory + path.sep)) {
      throw new Error('请选择独立的游戏数据子文件夹。');
    }
    if (catalogRequest) await catalogRequest.catch(() => {});
    progress('download', migrate ? '正在复制并校验游戏数据' : '正在切换游戏数据目录');
    const next = await storage.changeRoot(root, destination, migrate, signal);
    await updateSettings({ dataRoot: target ? next : '' });
    root = next;
    catalog = null;
    plans.clear();
    return root;
  }));
  handle('data:open', () => openFolder(root));
  handle('data:cache', () => run(async () => {
    await fs.rm(path.join(root, 'cache'), { recursive: true, force: true });
    return true;
  }));
  handle('instances:list', () => storage.listInstances(root));
  handle('instances:select', async id => { await getInstance(id); return updateSettings({ selected: id }); });
  handle('instances:create', value => run((signal, progress) => addInstance(value, signal, progress)));
  handle('instances:edit', (id, input) => run(() =>
    storage.saveInstance(root, id, { name: input.name, overrides: preferences.overrides(input.overrides ?? {}) })));
  handle('instances:folder', async id => { await getInstance(id); return openFolder(storage.instanceDirectory(root, id)); });
  handle('instances:resources', async id => {
    await getInstance(id);
    const dir = storage.instanceDirectory(root, id);
    return Promise.all(['mods', 'shaderpacks', 'resourcepacks'].map(async folder => ({
      folder, files: (await fs.readdir(path.join(dir, folder), { withFileTypes: true }).catch(error => {
        if (error.code === 'ENOENT') return [];
        throw error;
      })).filter(entry => entry.isFile()).map(entry => entry.name),
    })));
  });
  handle('game:start', id => run((signal, progress) => startGame(id, signal, progress)));
  handle('task:cancel', () => task?.abort());
  handle('resources:search', input => resources.search(input));
  handle('resources:versions', id => resources.versions(id));
  handle('resources:plan', async (id, release, instanceId) => {
    const instance = instanceId ? await getInstance(instanceId) : null;
    const plan = await resources.plan(id, release, instance);
    const key = randomUUID();
    if (plans.size >= 30) plans.delete(plans.keys().next().value);
    plans.set(key, { plan, instanceId, root });
    return { key, title: plan.title, type: plan.type, release: plan.release, files: plan.files?.map(file => file.title) ?? [] };
  });
  handle('resources:install', key => run(async (signal, progress) => {
    const selected = plans.get(key);
    if (!selected || selected.root !== root) throw new Error('资源安装计划已失效，请重新选择。');
    const { plan, instanceId } = selected;
    if (plan.type === 'modpack') {
      const file = path.join(root, 'cache', plan.file.sha512 + '.mrpack');
      progress('download', '正在下载整合包清单');
      await download(plan.file, file, signal);
      const result = await installPack(file, signal, progress);
      plans.delete(key);
      return result;
    }
    await getInstance(instanceId);
    await resources.installResources(storage.instanceDirectory(root, instanceId), plan.files, settings.concurrency, signal, progress);
    plans.delete(key);
    return true;
  }));
  handle('resources:import', async () => {
    const file = await choose('导入 Modrinth 整合包', ['openFile'], [{ name: 'Modrinth 整合包', extensions: ['mrpack'] }]);
    if (file) return run((signal, progress) => installPack(file, signal, progress));
    return null;
  });
  handle('account:summary', () => identities.summary());
  handle('account:login', () => run(async (signal, progress) => {
    if (!settings.clientId) throw new Error('请先保存已获批准的 EBuild Client ID。');
    progress('download', '正在连接微软登录');
    const result = await identities.login(settings.clientId, code => emit({
      phase: 'device', message: '在浏览器输入设备代码', code,
    }), signal);
    await updateSettings({ accountMode: 'microsoft' });
    return result;
  }));
  handle('account:browser', () => shell.openExternal('https://www.microsoft.com/link'));
  handle('account:logout', () => run(async () => {
    await identities.logout();
    await updateSettings({ accountMode: 'offline' });
    return true;
  }));
}

function createWindow() {
  window = new BrowserWindow({
    title: 'EBuild 启动器', width: 1140, height: 790, minWidth: 850, minHeight: 620,
    backgroundColor: '#f9fafb',
    icon: path.join(__dirname, 'ui', 'icon.ico'),
    titleBarStyle: 'hidden', titleBarOverlay: { height: 48 },
    autoHideMenuBar: true,
    webPreferences: { preload: path.join(__dirname, 'preload.cjs'), contextIsolation: true, sandbox: true, nodeIntegration: false },
  });
  applyTheme();
  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  window.webContents.on('will-navigate', event => event.preventDefault());
  window.on('closed', () => { window = null; });
  window.loadFile(entry);
}

if (!app.requestSingleInstanceLock()) app.quit();
else {
  app.on('second-instance', () => { if (window?.isMinimized()) window.restore(); window?.focus(); });
  app.whenReady().then(async () => {
    if (app.isPackaged && path.parse(programDirectory).root === programDirectory) {
      dialog.showErrorBox('EBuild 启动器', '请将 EBuild.exe 和同目录的文件移到磁盘的子文件夹后启动。');
      app.quit();
      return;
    }
    const profileRoot = app.getPath('userData');
    configFile = path.join(profileRoot, 'ebuild-settings.json');
    const existing = await storage.readOptional(configFile);
    const appData = app.getPath('appData');
    const legacyFolders = ['blockfield-launcher', 'Blockfield'].map(name => path.join(appData, name, 'data'));
    let legacy;
    let previous;
    if (!existing && !localDev) {
      for (const directory of legacyFolders) {
        previous = await storage.readOptional(path.join(directory, 'settings.json')).catch(error => {
          if (error.code !== 'EACCES' && error.code !== 'EPERM') throw error;
        });
        if (!previous) {
          const saved = await fs.readdir(path.join(directory, 'instances')).catch(error => {
            if (!['ENOENT', 'EACCES', 'EPERM'].includes(error.code)) throw error;
            return [];
          });
          if (saved.length) previous = {};
        }
        if (previous) { legacy = directory; break; }
      }
    }
    settings = preferences.clean({ ...preferences.defaults, ...previous, ...existing });
    root = settings.dataRoot || defaultRoot;
    await fs.mkdir(root, { recursive: true });
    if (previous && !(await fs.readdir(root)).length) {
      await storage.changeRoot(legacy, root, true, AbortSignal.timeout(600_000));
      await writeJson(configFile, settings);
    }
    identities = accounts(profileRoot, safeStorage);
    applyProxy();
    nativeTheme.on('updated', applyTheme);
    register();
    createWindow();
  }).catch(error => {
    dialog.showErrorBox('EBuild 无法启动', error.message);
    app.quit();
  });
  app.on('window-all-closed', () => {
    task?.abort();
    game?.unref();
    app.quit();
  });
}
