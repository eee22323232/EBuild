// Disposable size experiment. This bridge only exposes the homepage and data chooser.
const path = require('node:path');
const fs = require('node:fs/promises');
const readline = require('node:readline');
const storage = require('./src/storage.cjs');
const preferences = require('./src/preferences.cjs');
const { writeJson } = require('./src/download.cjs');

const profile = process.env.EBUILD_PROTOTYPE_PROFILE;
const defaultRoot = path.join(profile, '游戏数据');
const settingsFile = path.join(profile, 'prototype-settings.json');
let root;
let settings;

const demoInstances = [
  ['demo-fabric', 'Fabric 测试实例', '1.21.1', 'fabric', '0.16.14'],
  ['demo-vanilla', '原版测试实例', '1.21.1', 'vanilla', ''],
];

async function initialize() {
  settings = preferences.clean({ ...preferences.defaults, selected: 'demo-fabric',
    ...await storage.readOptional(settingsFile) });
  root = settings.dataRoot || defaultRoot;
  await fs.mkdir(root, { recursive: true });
  if (root === defaultRoot) await seed();
}

async function seed() {
  for (const [id, name, version, loader, loaderVersion] of demoInstances) {
    const folder = storage.instanceDirectory(root, id);
    await fs.mkdir(folder, { recursive: true });
    const file = path.join(folder, '.ebuild.json');
    if (!await storage.readOptional(file)) {
      await writeJson(file, { name, version, loader, loaderVersion, overrides: {} });
    }
  }
}

const actions = {
  'settings:get': () => ({ ...settings, currentRoot: root, defaultRoot, version: 'WebView2 实验版' }),
  'account:summary': () => null,
  'instances:list': () => storage.listInstances(root),
  'instances:select': async id => {
    if (!(await storage.listInstances(root)).some(item => item.id === id)) throw new Error('实例不存在。');
    const next = { ...settings, selected: id };
    await writeJson(settingsFile, next);
    settings = next;
    return settings;
  },
  'data:change': async (parent, migrate) => {
    if (typeof parent !== 'string' || (parent && !path.isAbsolute(parent))) throw new Error('请选择绝对路径。');
    const destination = parent ? await fs.mkdtemp(path.join(parent, 'EBuild-WebView2-实验-')) : defaultRoot;
    // Never point this experiment at an existing game's files.
    const next = await storage.changeRoot(root, destination, Boolean(migrate) && Boolean(parent), AbortSignal.timeout(30000));
    if (!parent) { root = next; await seed(); }
    const nextSettings = { ...settings, dataRoot: parent ? next : '' };
    await writeJson(settingsFile, nextSettings);
    root = next;
    settings = nextSettings;
    return root;
  },
};

async function main() {
  await initialize();
  const input = readline.createInterface({ input: process.stdin, terminal: false });
  for await (const line of input) {
    let id;
    try {
      const request = JSON.parse(line);
      id = request.id;
      if (!Object.hasOwn(actions, request.channel)) throw new Error('此原型只验证首页、实例切换和游戏数据目录。');
      const value = await actions[request.channel](...request.args);
      process.stdout.write(JSON.stringify({ id, value }) + '\n');
    } catch (error) {
      process.stdout.write(JSON.stringify({ id, error: error.message }) + '\n');
    }
  }
}
main().catch(error => { console.error(error.message); process.exitCode = 1; });
