const fs = require('node:fs/promises');
const { createReadStream } = require('node:fs');
const { createHash, randomUUID } = require('node:crypto');
const path = require('node:path');
const { inside, readJson, writeJson } = require('./download.cjs');

async function readOptional(file) {
  try { return await readJson(file); }
  catch (error) { if (error.code !== 'ENOENT') throw error; }
}

function instanceDirectory(root, id) {
  if (typeof id !== 'string' || !/^[a-z0-9][a-z0-9_.+-]{0,63}$/i.test(id) ||
      /\.$|^(?:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)/i.test(id)) {
    throw new Error('无效的实例标识。');
  }
  return inside(path.join(root, 'instances'), id);
}

function instanceSpec(value) {
  const name = typeof value?.name === 'string' ? value.name.trim() : '';
  if (!name || name.length > 80) throw new Error('请输入 1–80 个字符的实例名称。');
  if (typeof value.version !== 'string' || !/^[a-z0-9][a-z0-9_.+-]{0,63}$/i.test(value.version)) {
    throw new Error('请选择 Minecraft 版本。');
  }
  if (!['vanilla', 'fabric', 'neoforge'].includes(value.loader)) throw new Error('此加载器尚未支持。');
  const loaderVersion = value.loader === 'vanilla' ? '' : value.loaderVersion;
  if (value.loader !== 'vanilla' && !/^[a-z0-9][a-z0-9_.+-]{0,63}$/i.test(loaderVersion ?? '')) {
    throw new Error('请选择加载器版本。');
  }
  return { name, version: value.version, loader: value.loader, loaderVersion };
}

async function listInstances(root) {
  const folder = path.join(root, 'instances');
  const entries = await fs.readdir(folder, { withFileTypes: true }).catch(error => {
    if (error.code === 'ENOENT') return [];
    throw error;
  });
  const instances = [];
  for (const entry of entries) {
    if (!entry.isDirectory() || entry.name.startsWith('.')) continue;
    const dir = instanceDirectory(root, entry.name);
    const manifest = await readOptional(path.join(dir, '.ebuild.json'));
    if (manifest) {
      instances.push({ ...manifest, id: entry.name });
      continue;
    }
    const legacy = await readOptional(path.join(dir, '.installed.json'));
    if (legacy?.id !== entry.name) continue;
    const migrated = { name: entry.name + ' 原版', version: entry.name, loader: 'vanilla', loaderVersion: '', overrides: {} };
    await writeJson(path.join(dir, '.ebuild.json'), migrated);
    instances.push({ ...migrated, id: entry.name });
  }
  return instances.sort((a, b) => a.name.localeCompare(b.name, 'zh-CN'));
}

async function saveInstance(root, id, value) {
  const directory = instanceDirectory(root, id);
  const existing = await readOptional(path.join(directory, '.ebuild.json'));
  if (!existing) throw new Error('实例不存在。');
  const name = instanceSpec({ ...existing, name: value.name }).name;
  const result = { ...existing, name, overrides: value.overrides };
  await writeJson(path.join(directory, '.ebuild.json'), result);
  return { ...result, id };
}

async function installInstance(root, input, install) {
  const instance = { ...instanceSpec(input), overrides: {} };
  const id = randomUUID();
  const directory = instanceDirectory(root, id);
  const staging = path.join(path.dirname(directory), '.' + id);
  await fs.mkdir(staging, { recursive: true });
  try {
    await install(instance, staging);
    await writeJson(path.join(staging, '.ebuild.json'), instance);
    await fs.rename(staging, directory);
    return { ...instance, id };
  } finally {
    await fs.rm(staging, { recursive: true, force: true });
  }
}

function equalPath(a, b) {
  return path.resolve(a).toLowerCase() === path.resolve(b).toLowerCase();
}

async function digest(file, signal) {
  const hash = createHash('sha256');
  for await (const chunk of createReadStream(file, { signal })) hash.update(chunk);
  return hash.digest('hex');
}

async function copyVerified(from, to, signal) {
  await fs.mkdir(to);
  for (const entry of await fs.readdir(from, { withFileTypes: true })) {
    signal.throwIfAborted();
    const source = path.join(from, entry.name);
    const destination = path.join(to, entry.name);
    if (entry.isDirectory()) {
      await copyVerified(source, destination, signal);
    } else if (entry.isFile()) {
      await fs.copyFile(source, destination);
      if (await digest(source, signal) !== await digest(destination, signal)) {
        throw new Error('复制校验失败：' + entry.name);
      }
    } else {
      throw new Error('数据目录包含链接或不支持的文件：' + entry.name);
    }
  }
}

async function changeRoot(current, destination, migrate, signal) {
  destination = path.resolve(destination);
  await fs.mkdir(destination, { recursive: true });
  const target = await fs.realpath(destination);
  const source = await fs.realpath(current);
  if (equalPath(target, source) || equalPath(target, path.parse(target).root)) {
    if (equalPath(target, source)) return source;
    throw new Error('请选择磁盘上的子文件夹。');
  }
  const nested = (a, b) => a.toLowerCase().startsWith(b.toLowerCase() + path.sep);
  if (nested(target, source) || nested(source, target)) {
    throw new Error('新旧游戏数据目录不能互相包含。');
  }
  if (!migrate) return target;
  if ((await fs.readdir(target)).length) throw new Error('迁移目标必须是空文件夹。');
  const staging = path.join(path.dirname(target), '.ebuild-migrate-' + randomUUID());
  try {
    await copyVerified(source, staging, signal);
    signal.throwIfAborted();
    await fs.rmdir(target);
    await fs.rename(staging, target);
  } finally {
    await fs.rm(staging, { recursive: true, force: true });
  }
  return target;
}

module.exports = { readOptional, instanceDirectory, listInstances, saveInstance, installInstance, changeRoot };
