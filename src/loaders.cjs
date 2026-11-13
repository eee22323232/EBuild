const fs = require('node:fs/promises');
const path = require('node:path');
const { Version } = require('@xmcl/core');
const { installFabric, installNeoForgedTask, installLibrariesTask } = require('@xmcl/installer');
const { fetchJson, networkFetch, readJson, dispatcherFor } = require('./download.cjs');

const neoMaven = 'https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml';
let neoVersions;

async function loaderVersions(version, loader, signal) {
  if (typeof version !== 'string' || !/^[a-z0-9_.+-]{1,64}$/i.test(version)) throw new Error('无效的游戏版本。');
  if (loader === 'fabric') {
    const result = await fetchJson('https://meta.fabricmc.net/v2/versions/loader/' + encodeURIComponent(version), signal);
    return result.map(({ loader }) => ({ id: loader.version, stable: loader.stable }));
  }
  if (loader !== 'neoforge') return [];
  const parts = version.match(/^(\d+)\.(\d+)(?:\.(\d+))?$/);
  if (!parts || (parts[1] !== '1' && Number(parts[1]) < 26)) return [];
  if (!neoVersions) {
    const response = await networkFetch(neoMaven, { signal });
    if (!response.ok) throw new Error('无法读取 NeoForge 版本列表（HTTP ' + response.status + '）。');
    const xml = await response.text();
    neoVersions = Array.from(xml.matchAll(/<version>([\d.]+(?:-[a-z0-9.-]+)?)<\/version>/gi), match => match[1]);
  }
  const prefix = (parts[1] === '1' ? [parts[2], parts[3] || '0'] : [parts[1], parts[2], parts[3] || '0']).join('.');
  return neoVersions.filter(value => value.startsWith(prefix + '.')).reverse().map(id => ({ id, stable: !id.includes('-') }));
}

async function runTask(task, signal) {
  signal.throwIfAborted();
  let cancellation;
  const abort = () => { cancellation = task.cancel().catch(() => {}); };
  signal.addEventListener('abort', abort, { once: true });
  try {
    const result = await task.startAndWait();
    signal.throwIfAborted();
    return result;
  } finally {
    signal.removeEventListener('abort', abort);
    await cancellation;
  }
}

async function installLoader(files, spec, java, settings, signal, progress) {
  if (spec.loader === 'vanilla') return files;
  const base = spec.version;
  const root = path.dirname(files.assets);
  const versionRoot = path.join(root, 'versions', base);
  await fs.mkdir(versionRoot, { recursive: true });
  const baseJson = path.join(versionRoot, base + '.json');
  const baseJar = path.join(versionRoot, base + '.jar');
  await fs.copyFile(path.join(root, 'versions', base + '.json'), baseJson);
  const flatJar = path.join(root, 'versions', base + '.jar');
  try { await fs.link(flatJar, baseJar); }
  catch (error) {
    if (error.code !== 'EEXIST') await fs.copyFile(flatJar, baseJar);
  }

  progress('download', '正在安装 ' + (spec.loader === 'fabric' ? 'Fabric' : 'NeoForge'));
  const options = {
    java, librariesDownloadConcurrency: settings.concurrency,
    dispatcher: await dispatcherFor(spec.loader === 'fabric' ? 'https://maven.fabricmc.net' : neoMaven),
  };
  const fabric = spec.loader === 'fabric';
  const loaderId = fabric ? base + '-fabric' + spec.loaderVersion : 'neoforge-' + spec.loaderVersion;
  const ready = path.join(root, 'versions', loaderId, '.ebuild-ready');
  let id = loaderId;
  let cached;
  try { cached = await fs.readFile(ready, 'utf8'); }
  catch (error) { if (error.code !== 'ENOENT') throw error; }
  if (cached) id = cached;
  else if (fabric) {
    id = await installFabric({ minecraftVersion: base, version: spec.loaderVersion, minecraft: root, signal, fetch: networkFetch });
  } else {
    id = await runTask(installNeoForgedTask('neoforge', spec.loaderVersion, root, options), signal);
  }
  const resolved = await Version.parse(root, id);
  const profile = await readJson(path.join(root, 'versions', id, id + '.json'));
  await runTask(installLibrariesTask(resolved, options), signal);
  await fs.mkdir(path.dirname(ready), { recursive: true });
  await fs.writeFile(ready, id);
  return {
    ...files,
    baseVersion: base,
    metadata: {
      ...files.metadata,
      id,
      mainClass: profile.mainClass,
      arguments: resolved.arguments,
    },
    classpath: [...resolved.libraries.filter(lib => !lib.isNative).map(lib =>
      path.join(root, 'libraries', lib.download.path)), baseJar].join(path.delimiter),
  };
}

module.exports = { loaderVersions, installLoader };
