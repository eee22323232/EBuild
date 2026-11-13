const fs = require('node:fs/promises');
const path = require('node:path');
const { randomUUID } = require('node:crypto');
const yauzl = require('yauzl');
const { download, extractZip, inside, networkFetch, pool, writeJson } = require('./download.cjs');
const { readOptional } = require('./storage.cjs');
const { version } = require('../package.json');

const modrinth = 'https://api.modrinth.com/v2';
const folders = { mod: 'mods', shader: 'shaderpacks', resourcepack: 'resourcepacks' };
const types = new Set([...Object.keys(folders), 'modpack']);
const sorts = new Set(['relevance', 'downloads', 'updated', 'newest']);
const supportedPackLoaders = new Set(['minecraft', 'vanilla', 'fabric', 'neoforge']);

async function api(endpoint, query = {}, signal) {
  const url = new URL(modrinth + endpoint);
  url.search = new URLSearchParams(query);
  const timeout = AbortSignal.timeout(15_000);
  const response = await networkFetch(url, {
    signal: signal ? AbortSignal.any([signal, timeout]) : timeout,
    headers: { 'User-Agent': 'EBuildLauncher/EBuild/' + version },
  });
  if (!response.ok) throw new Error('Modrinth 请求失败（HTTP ' + response.status + '）。');
  return response.json();
}

function resourceId(value) {
  if (typeof value !== 'string' || !/^[a-z0-9_-]{1,120}$/i.test(value)) throw new Error('无效的资源标识。');
  return value;
}

function search(input) {
  const facets = [];
  if (types.has(input.type)) facets.push(['project_type:' + input.type]);
  else facets.push([...types].map(type => 'project_type:' + type));
  if (input.version) facets.push(['versions:' + String(input.version).slice(0, 64)]);
  if (['fabric', 'neoforge', 'forge', 'quilt'].includes(input.loader)) {
    facets.push(['categories:' + input.loader]);
  }
  return api('/search', {
    query: String(input.query ?? '').slice(0, 150),
    index: sorts.has(input.sort) ? input.sort : 'relevance',
    facets: JSON.stringify(facets),
    offset: Math.max(0, Math.min(10000, Number(input.offset) || 0)),
    limit: 20,
  });
}

async function versions(id) {
  const key = resourceId(id);
  const [project, releases] = await Promise.all([api('/project/' + key), api('/project/' + key + '/version')]);
  return { project, releases };
}

function compatible(release, type, instance) {
  if (type === 'modpack') return release.loaders.some(loader => supportedPackLoaders.has(loader));
  if (!instance) return false;
  if (!release.game_versions.includes(instance.version)) return false;
  if (type !== 'mod') return true;
  return instance.loader !== 'vanilla' && release.loaders.includes(instance.loader);
}

function fileSpec(file) {
  if (typeof file?.filename !== 'string' || path.basename(file.filename) !== file.filename ||
      !/^https:\/\//i.test(file.url) || !/^[a-f0-9]{128}$/i.test(file.hashes?.sha512 ?? '') ||
      !Number.isSafeInteger(file.size)) throw new Error('资源文件信息无效。');
  return { url: file.url, size: file.size, sha512: file.hashes.sha512 };
}

async function plan(id, releaseId, instance) {
  const { project, releases } = await versions(id);
  if (!types.has(project.project_type)) throw new Error('该资源类型暂未支持。');
  const release = releaseId
    ? releases.find(item => item.id === releaseId)
    : releases.find(item => item.version_type === 'release' && compatible(item, project.project_type, instance))
      ?? releases.find(item => compatible(item, project.project_type, instance));
  if (!release || !compatible(release, project.project_type, instance)) {
    throw new Error('没有与目标实例匹配的资源版本。');
  }
  const picked = new Map();
  async function visit(project, item) {
    const existing = picked.get(project.id);
    if (existing) {
      if (existing.version !== item.id) throw new Error('存在互不兼容的必需依赖版本。');
      return;
    }
    if (picked.size >= 80) throw new Error('该资源的依赖数量超出限制。');
    const file = item.files.find(file => file.primary) ?? item.files[0];
    const folder = folders[project.project_type];
    if (!folder) throw new Error('该依赖的资源类型无法安装。');
    const spec = fileSpec(file);
    picked.set(project.id, { id: project.id, version: item.id, title: project.title, filename: file.filename, folder, spec });
    for (const dependency of item.dependencies.filter(x => x.dependency_type === 'required')) {
      const pinned = dependency.version_id && await api('/version/' + resourceId(dependency.version_id));
      const key = pinned?.project_id ?? dependency.project_id;
      if (!key) throw new Error('依赖需要从其他来源手动安装。');
      const next = await versions(key);
      const isCompatible = candidate => compatible(candidate, next.project.project_type, instance);
      const chosen = pinned || next.releases.find(item => item.version_type === 'release' && isCompatible(item)) ||
        next.releases.find(isCompatible);
      if (!chosen || !isCompatible(chosen)) throw new Error('找不到兼容的必需依赖：' + next.project.title);
      await visit(next.project, chosen);
    }
  }
  if (project.project_type === 'modpack') {
    const file = release.files.find(x => x.primary) ?? release.files[0];
    if (!file?.filename.endsWith('.mrpack')) throw new Error('该整合包未提供 .mrpack 文件。');
    return { type: 'modpack', title: project.title, release: release.version_number, file: fileSpec(file) };
  }
  await visit(project, release);
  return { type: project.project_type, title: project.title, release: release.version_number, files: [...picked.values()] };
}

async function installResources(directory, files, concurrency, signal, progress) {
  const manifest = path.join(directory, '.ebuild-resources.json');
  const installed = await readOptional(manifest) ?? {};
  const paths = new Set();
  const pending = [];
  for (const file of files) {
    const current = installed[file.id];
    if (current && current.version !== file.version) {
      const old = inside(path.join(directory, current.folder), current.filename);
      const exists = await fs.access(old).then(() => true, error => {
        if (error.code === 'ENOENT') return false;
        throw error;
      });
      if (exists) throw new Error('实例已安装其他版本：' + file.title + '。请在实例目录移除旧文件后重试。');
    }
    const target = inside(path.join(directory, file.folder), file.filename);
    if (paths.has(target.toLowerCase())) throw new Error('两个资源使用同一文件名：' + file.filename);
    paths.add(target.toLowerCase());
    const exists = await fs.stat(target).then(() => true, error => {
      if (error.code === 'ENOENT') return false;
      throw error;
    });
    if (exists && current?.version !== file.version) throw new Error('实例已有同名文件：' + file.filename);
    if (!exists) pending.push({ file, target });
  }
  const stage = path.join(directory, '.resource-' + randomUUID());
  const moved = [];
  try {
    let done = 0;
    progress('download', '正在下载资源', 0, pending.length);
    await pool(pending, concurrency, async (job, activeSignal) => {
      job.temp = inside(stage, path.join(job.file.folder, job.file.filename));
      await download(job.file.spec, job.temp, activeSignal);
      progress('download', '正在下载资源', ++done, pending.length);
    }, signal);
    for (const job of pending) {
      signal.throwIfAborted();
      await fs.mkdir(path.dirname(job.target), { recursive: true });
      await fs.link(job.temp, job.target);
      moved.push(job.target);
    }
    for (const { id, version, title, filename, folder } of files) installed[id] = { version, title, filename, folder };
    await writeJson(manifest, installed);
  } catch (error) {
    await Promise.all(moved.map(file => fs.rm(file, { force: true })));
    throw error;
  } finally {
    await fs.rm(stage, { recursive: true, force: true });
  }
}

async function readPack(file) {
  const zip = await yauzl.openPromise(file, { lazyEntries: true, strictFileNames: true });
  try {
    for await (const entry of zip.eachEntry()) {
      if (entry.fileName !== 'modrinth.index.json') continue;
      if (entry.uncompressedSize > 16 * 1024 * 1024) throw new Error('整合包清单过大。');
      const stream = await zip.openReadStreamPromise(entry);
      const buffers = [];
      for await (const buffer of stream) buffers.push(buffer);
      const index = JSON.parse(Buffer.concat(buffers).toString('utf8'));
      if (index.formatVersion !== 1 || index.game !== 'minecraft' || !Array.isArray(index.files)) {
        throw new Error('不支持的 Modrinth 整合包格式。');
      }
      const deps = index.dependencies ?? {};
      const loaderMap = { 'fabric-loader': 'fabric', neoforge: 'neoforge' };
      const names = Object.keys(deps).filter(key => key !== 'minecraft');
      if (names.length > 1 || names.some(key => !Object.hasOwn(loaderMap, key))) {
        throw new Error('此整合包需要暂未支持的加载器。');
      }
      return {
        index,
        instance: {
          name: index.name,
          version: deps.minecraft,
          loader: loaderMap[names[0]] ?? 'vanilla',
          loaderVersion: deps[names[0]] ?? '',
        },
      };
    }
  } finally {
    if (zip.isOpen) zip.close();
  }
  throw new Error('压缩包缺少 Modrinth 整合包清单。');
}

async function unpackPack(file, index, directory, concurrency, signal, progress) {
  const list = index.files.filter(entry => entry.env?.client !== 'unsupported');
  const paths = new Set();
  let done = 0;
  progress('download', '正在下载整合包文件', 0, list.length);
  await pool(list, concurrency, async (entry, activeSignal) => {
    const relative = entry.path;
    const target = packPath(directory, relative);
    if (paths.has(target.toLowerCase())) throw new Error('整合包含重复文件路径。');
    paths.add(target.toLowerCase());
    if (!/^[a-f0-9]{128}$/i.test(entry.hashes?.sha512 ?? '') || !Number.isSafeInteger(entry.fileSize)) {
      throw new Error('整合包文件缺少校验信息。');
    }
    const urls = (entry.downloads ?? []).filter(url => {
      try {
        const parsed = new URL(url);
        return parsed.protocol === 'https:' && ['cdn.modrinth.com', 'github.com', 'raw.githubusercontent.com', 'gitlab.com'].includes(parsed.hostname);
      } catch { return false; }
    });
    let last;
    for (const url of urls) {
      try {
        await download({ url, sha512: entry.hashes.sha512, size: entry.fileSize }, target, activeSignal);
        progress('download', '正在下载整合包文件', ++done, list.length);
        return;
      } catch (error) {
        activeSignal.throwIfAborted();
        last = error;
      }
    }
    throw last ?? new Error('整合包文件缺少受支持的下载地址：' + relative);
  }, signal);
  for (const prefix of ['overrides/', 'client-overrides/']) {
    await extractZip(file, directory, signal, [], entry => {
      if (!entry.startsWith(prefix)) return null;
      const relative = entry.slice(prefix.length);
      packPath(directory, relative);
      return relative;
    });
  }
}

function packPath(directory, relative) {
  if (typeof relative !== 'string' || relative.split(/[\\/]/).some(part => part === '..' || part === '.' || /^\.(ebuild|installed|launch|natives|resource)/i.test(part))) {
    throw new Error('整合包包含无效的游戏文件路径。');
  }
  return inside(directory, relative);
}

module.exports = { search, versions, compatible, plan, installResources, readPack, unpackPack };
