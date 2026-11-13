const fs = require('node:fs/promises');
const { createReadStream, createWriteStream } = require('node:fs');
const { createHash, randomUUID } = require('node:crypto');
const path = require('node:path');
const { Readable } = require('node:stream');
const { pipeline } = require('node:stream/promises');
const { setTimeout: delay } = require('node:timers/promises');
const yauzl = require('yauzl');
const { fetch: undiciFetch, ProxyAgent } = require('undici');

let proxyResolver;
const agents = new Map();

function configureProxy(resolver) {
  proxyResolver = resolver;
  for (const agent of agents.values()) agent.close().catch(() => {});
  agents.clear();
}

async function dispatcherFor(url) {
  const proxy = await proxyResolver?.(String(url));
  if (!proxy) return;
  if (!agents.has(proxy)) agents.set(proxy, new ProxyAgent(proxy));
  return agents.get(proxy);
}

async function networkFetch(url, options) {
  const dispatcher = await dispatcherFor(url);
  return dispatcher ? undiciFetch(url, { ...options, dispatcher }) : fetch(url, options);
}

function inside(root, relative) {
  const destination = path.resolve(root, relative);
  if (!destination.startsWith(path.resolve(root) + path.sep) ||
      relative.split(/[\\/]/).some(part => /[:\x00-\x1f]|[ .]$|^(?:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)/i.test(part))) {
    throw new Error('文件清单包含无效路径。');
  }
  return destination;
}

async function writeJson(file, data) {
  await fs.mkdir(path.dirname(file), { recursive: true });
  const temp = file + '.' + randomUUID() + '.tmp';
  try {
    await fs.writeFile(temp, JSON.stringify(data), { flag: 'wx' });
    await fs.rename(temp, file);
  } finally {
    await fs.rm(temp, { force: true });
  }
}

async function readJson(file) {
  return JSON.parse(await fs.readFile(file, 'utf8'));
}

async function retry(action, url, signal) {
  if (new URL(url).protocol !== 'https:') throw new Error('下载地址必须使用 HTTPS。');
  for (let attempt = 0; ; attempt++) {
    signal?.throwIfAborted();
    try {
      const timeout = AbortSignal.timeout(120_000);
      const response = await networkFetch(url, {
        signal: signal ? AbortSignal.any([signal, timeout]) : timeout,
      });
      if (!response.ok) {
        await response.body?.cancel();
        throw new Error('HTTP ' + response.status);
      }
      return await action(response);
    } catch (error) {
      signal?.throwIfAborted();
      if (attempt >= 2) {
        throw new Error(new URL(url).hostname + ' 下载失败（' + error.message + '）');
      }
      await delay(500 * (attempt + 1), undefined, { signal });
    }
  }
}

function fetchJson(url, signal) {
  return retry(response => response.json(), url, signal);
}

async function matches(file, { size, sha1, sha256, sha512 }) {
  try {
    const info = await fs.stat(file);
    if (!info.isFile() || (size !== undefined && size !== info.size)) return false;
    const expected = sha512 || sha256 || sha1;
    if (!expected) return true;
    const hash = createHash(sha512 ? 'sha512' : sha256 ? 'sha256' : 'sha1');
    for await (const chunk of createReadStream(file)) hash.update(chunk);
    return hash.digest('hex') === expected;
  } catch (error) {
    if (error.code === 'ENOENT') return false;
    throw error;
  }
}

async function download(spec, file, signal) {
  signal?.throwIfAborted();
  if (await matches(file, spec)) return;
  await fs.mkdir(path.dirname(file), { recursive: true });
  await retry(async response => {
    const temp = file + '.' + randomUUID() + '.part';
    try {
      await pipeline(Readable.fromWeb(response.body), createWriteStream(temp, { flags: 'wx' }), { signal });
      if (!await matches(temp, spec)) throw new Error('文件校验失败');
      await fs.rename(temp, file);
    } finally {
      await fs.rm(temp, { force: true });
    }
  }, spec.url, signal);
}

async function pool(items, limit, task, signal) {
  const controller = new AbortController();
  const activeSignal = signal ? AbortSignal.any([signal, controller.signal]) : controller.signal;
  let next = 0;
  let firstError;
  const workers = Array.from({ length: Math.min(limit, items.length) }, async () => {
    try {
      while (next < items.length) {
        activeSignal.throwIfAborted();
        await task(items[next++], activeSignal);
      }
    } catch (error) {
      firstError ??= error;
      controller.abort(error);
    }
  });
  await Promise.all(workers);
  if (firstError) throw firstError;
}

async function extractZip(file, destination, signal, exclude = [], select = name => name) {
  const zip = await yauzl.openPromise(file, { lazyEntries: true, strictFileNames: true });
  try {
    for await (const entry of zip.eachEntry()) {
      signal?.throwIfAborted();
      if (entry.fileName.endsWith('/') || exclude.some(prefix => entry.fileName.startsWith(prefix))) continue;
      const relative = select(entry.fileName);
      if (!relative) continue;
      const filePath = inside(destination, relative);
      await fs.mkdir(path.dirname(filePath), { recursive: true });
      await pipeline(await zip.openReadStreamPromise(entry), createWriteStream(filePath), { signal });
    }
  } finally {
    if (zip.isOpen) zip.close();
  }
}

module.exports = { inside, writeJson, readJson, fetchJson, networkFetch, configureProxy, dispatcherFor, download, pool, extractZip };
