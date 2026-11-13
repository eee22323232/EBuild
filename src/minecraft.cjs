const { createHash, randomUUID } = require('node:crypto');
const { execFile, spawn } = require('node:child_process');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { promisify } = require('node:util');
const { inside, writeJson, readJson, fetchJson, download, pool, extractZip } = require('./download.cjs');
const { installLoader } = require('./loaders.cjs');
const { version: launcherVersion } = require('../package.json');

const exec = promisify(execFile);
const manifestUrl = 'https://piston-meta.mojang.com/mc/game/version_manifest_v2.json';
const versionId = /^[A-Za-z0-9][A-Za-z0-9_.+-]{0,63}$/;
const allowedTypes = new Set(['release', 'snapshot']);

async function versionList(root) {
  const file = path.join(root, 'versions.json');
  let manifest;
  let cached = false;
  try {
    manifest = await fetchJson(manifestUrl, AbortSignal.timeout(12_000));
    if (!Array.isArray(manifest.versions)) throw new Error('版本清单格式错误。');
    await writeJson(file, manifest);
  } catch (error) {
    try {
      manifest = await readJson(file);
      cached = true;
    } catch {
      throw new Error('暂时无法获取 Mojang 版本列表，请检查网络后重试。');
    }
  }
  const versions = manifest.versions.filter(item => allowedTypes.has(item.type) && versionId.test(item.id));
  return { latest: manifest.latest, versions, cached };
}

function rulesAllow(rules, features = {}) {
  if (!rules) return true;
  let allowed = false;
  for (const rule of rules) {
    const target = rule.os;
    if (target?.name && target.name !== 'windows') continue;
    if (target?.version && !new RegExp(target.version).test(os.release())) continue;
    if (target?.arch && !new RegExp(target.arch).test('amd64')) continue;
    if (Object.entries(rule.features ?? {}).some(([key, value]) => Boolean(features[key]) !== value)) continue;
    allowed = rule.action === 'allow';
  }
  return allowed;
}

function expandArgument(arg, values) {
  return arg.replace(/\$\{([^}]+)\}/g, (_, key) => {
    if (!Object.hasOwn(values, key)) throw new Error('当前版本包含未知的启动参数：' + key);
    return values[key];
  });
}

function expandArguments(args, values, features) {
  return args.flatMap(arg => {
    if (typeof arg === 'string') return [expandArgument(arg, values)];
    if (!rulesAllow(arg.rules, features)) return [];
    return [arg.value].flat().map(value => expandArgument(value, values));
  });
}

async function javaMajor(executable, signal) {
  try {
    const result = await exec(executable, ['-XshowSettings:properties', '-version'], {
      windowsHide: true, timeout: 10_000, signal,
    });
    const output = result.stderr + result.stdout;
    if (!/^\s*os\.arch\s*=\s*(amd64|x86_64)\s*$/m.test(output)) return null;
    const match = output.match(/version\s+"(?:1\.)?(\d+)/);
    return match ? Number(match[1]) : null;
  } catch {
    signal?.throwIfAborted();
    return null;
  }
}

async function resolveJava(root, major, custom, progress, signal) {
  progress('java', '正在检查 Java ' + major);
  if (custom) {
    const executable = path.join(path.dirname(custom), 'java.exe');
    if (await javaMajor(executable, signal) !== major) {
      throw new Error('当前版本需要 64 位 Java ' + major + '，请更换 Java 路径或切回自动配置。');
    }
    return executable;
  }

  const runtime = path.join(root, 'runtimes', 'java-' + major);
  const managedJava = path.join(runtime, 'bin', 'java.exe');
  const candidates = [managedJava, process.env.JAVA_HOME && path.join(process.env.JAVA_HOME, 'bin', 'java.exe'), 'java.exe'];
  for (const candidate of new Set(candidates.filter(Boolean))) {
    if (await javaMajor(candidate, signal) === major) return candidate;
  }

  progress('java', '正在下载并配置 Java ' + major);
  const endpoint = 'https://api.adoptium.net/v3/assets/latest/' + major +
    '/hotspot?architecture=x64&image_type=jre&os=windows&vendor=eclipse';
  const releases = await fetchJson(endpoint, signal);
  const pkg = releases[0]?.binary?.package;
  if (!pkg?.link || !/^[a-f0-9]{64}$/.test(pkg.checksum) || !Number.isSafeInteger(pkg.size)) {
    throw new Error('未找到适用的 Java ' + major + '，可在设置中选择本机 Java。');
  }
  const archive = path.join(root, 'cache', 'java-' + major + '.zip');
  await download({ url: pkg.link, size: pkg.size, sha256: pkg.checksum }, archive, signal);
  const staging = runtime + '.' + randomUUID() + '.tmp';
  try {
    await extractZip(archive, staging, signal);
    const folders = await fs.readdir(staging, { withFileTypes: true });
    const folder = folders.find(entry => entry.isDirectory());
    const extracted = folder && path.join(staging, folder.name);
    if (!extracted || await javaMajor(path.join(extracted, 'bin', 'java.exe'), signal) !== major) {
      throw new Error('Java 解压后校验失败。');
    }
    await fs.rm(runtime, { recursive: true, force: true });
    await fs.rename(extracted, runtime);
    await fs.rm(archive, { force: true });
  } finally {
    await fs.rm(staging, { recursive: true, force: true });
  }
  return managedJava;
}

function planLibraries(metadata, libraryRoot) {
  const jobs = [];
  const classpath = [];
  const natives = [];
  for (const library of metadata.libraries) {
    if (!rulesAllow(library.rules)) continue;
    const artifact = library.downloads?.artifact;
    if (artifact) {
      const file = inside(libraryRoot, artifact.path);
      jobs.push({ spec: artifact, file });
      classpath.push(file);
    }
    const classifier = library.natives?.windows?.replace('${arch}', '64');
    if (classifier) {
      const spec = library.downloads.classifiers[classifier];
      const file = inside(libraryRoot, spec.path);
      jobs.push({ spec, file });
      natives.push({ file, exclude: library.extract?.exclude ?? ['META-INF/'] });
    }
  }
  return { jobs, classpath, natives };
}

async function prepare(root, version, instance, settings, signal, progress) {
  const content = path.join(root, 'game');
  const descriptor = path.join(content, 'versions', version.id + '.json');
  progress('download', '正在读取游戏清单');
  await download(version, descriptor, signal);
  const metadata = await readJson(descriptor);
  if (!metadata.downloads?.client || !metadata.assetIndex) throw new Error('此版本没有可用的客户端清单。');

  const assets = path.join(content, 'assets');
  const indexId = metadata.assetIndex.id;
  const index = inside(assets, path.join('indexes', indexId + '.json'));
  await download(metadata.assetIndex, index, signal);
  const assetIndex = await readJson(index);
  const client = path.join(content, 'versions', version.id + '.jar');
  const libraries = planLibraries(metadata, path.join(content, 'libraries'));
  const jobs = [{ spec: metadata.downloads.client, file: client }, ...libraries.jobs];
  const logging = metadata.logging?.client;
  let logConfig;
  if (logging) {
    logConfig = inside(assets, path.join('log_configs', logging.file.id));
    jobs.push({ spec: logging.file, file: logConfig });
  }
  const assetFiles = new Map();
  for (const { hash, size } of Object.values(assetIndex.objects)) {
    const objectName = hash.slice(0, 2) + '/' + hash;
    const file = inside(assets, 'objects/' + objectName);
    assetFiles.set(hash, file);
    jobs.push({ spec: { url: 'https://resources.download.minecraft.net/' + objectName, sha1: hash, size }, file });
  }
  const unique = [...new Map(jobs.map(job => [job.file, job])).values()];
  let done = 0;
  progress('download', '正在下载或校验游戏文件', 0, unique.length);
  await pool(unique, settings.concurrency ?? 12, async ({ spec, file }, activeSignal) => {
    await download(spec, file, activeSignal);
    done++;
    if (done % 12 === 0 || done === unique.length) {
      progress('download', '正在下载或校验游戏文件', done, unique.length);
    }
  }, signal);

  progress('download', '正在准备游戏目录');
  const nativeRoot = path.join(instance, '.natives');
  await fs.mkdir(nativeRoot, { recursive: true });
  for (const native of libraries.natives) await extractZip(native.file, nativeRoot, signal, native.exclude);
  const virtualAssets = inside(assets, path.join('virtual', indexId));
  const assetTargets = [];
  if (assetIndex.virtual) assetTargets.push(virtualAssets);
  if (assetIndex.map_to_resources) assetTargets.push(path.join(instance, 'resources'));
  for (const targetRoot of assetTargets) {
    await pool(Object.entries(assetIndex.objects), 8, async ([name, spec]) => {
      const target = inside(targetRoot, name);
      await fs.mkdir(path.dirname(target), { recursive: true });
      await fs.copyFile(assetFiles.get(spec.hash), target);
    }, signal);
  }
  return {
    metadata, instance, assets, nativeRoot, logConfig, virtualAssets,
    classpath: [...libraries.classpath, client].join(path.delimiter),
  };
}

function gameArguments(files, settings) {
  const { metadata, instance, assets, nativeRoot, logConfig, virtualAssets, classpath } = files;
  const hash = createHash('md5').update('OfflinePlayer:' + settings.username).digest();
  hash[6] = (hash[6] & 0x0f) | 0x30;
  hash[8] = (hash[8] & 0x3f) | 0x80;
  const values = {
    auth_player_name: settings.account?.name ?? settings.username,
    auth_uuid: settings.account?.id ?? hash.toString('hex'),
    auth_access_token: settings.account?.token ?? '0',
    auth_session: settings.account?.token ?? '0',
    auth_xuid: settings.account?.xuid ?? '',
    clientid: settings.account?.clientId ?? '',
    user_type: settings.account ? 'msa' : 'legacy',
    user_properties: '{}',
    version_name: metadata.id,
    version_type: metadata.type,
    game_directory: instance,
    game_assets: virtualAssets,
    assets_root: assets,
    assets_index_name: metadata.assetIndex.id,
    natives_directory: nativeRoot,
    launcher_name: 'EBuild',
    launcher_version: launcherVersion,
    classpath,
    classpath_separator: path.delimiter,
    library_directory: path.join(path.dirname(assets), 'libraries'),
    resolution_width: String(settings.width || 0),
    resolution_height: String(settings.height || 0),
  };
  const features = { has_custom_resolution: Boolean(settings.width && settings.height) };
  const jvm = metadata.arguments?.jvm ?? ['-Djava.library.path=${natives_directory}', '-cp', '${classpath}'];
  const game = metadata.arguments?.game ?? (metadata.minecraftArguments ?? '').split(/\s+/).filter(Boolean);
  const jvmValues = { ...values, version_name: files.baseVersion ?? metadata.id };
  const args = ['-Xmx' + settings.memory + 'M', ...expandArguments(jvm, jvmValues, features)];
  if (settings.gc) args.push('-XX:+Use' + settings.gc + 'GC');
  args.push(...(settings.jvmArgs ?? []));
  if (logConfig) args.push(expandArgument(metadata.logging.client.argument, { path: logConfig }));
  const gameArgs = expandArguments(game, values, features);
  if (features.has_custom_resolution && !gameArgs.includes('--width')) {
    gameArgs.push('--width', values.resolution_width, '--height', values.resolution_height);
  }
  return { jvm: [...args, metadata.mainClass], game: [...gameArgs, ...(settings.gameArgs ?? [])] };
}

async function installGame(root, version, spec, directory, settings, signal, progress) {
  if (process.platform !== 'win32' || process.arch !== 'x64') throw new Error('此版本需要 Windows x64。');
  const files = await prepare(root, version, directory, settings, signal, progress);
  const major = files.metadata.javaVersion?.majorVersion ?? 8;
  const java = await resolveJava(root, major, settings.java, progress, signal);
  return { files: await installLoader(files, spec, java, settings, signal, progress), java, major };
}

async function launch(root, version, spec, directory, settings, signal, progress) {
  const { files, java, major } = await installGame(root, version, spec, directory, settings, signal, progress);
  const args = gameArguments(files, settings);
  let command = [...args.jvm, ...args.game];
  let argfile;
  if (major >= 9) {
    argfile = path.join(files.instance, '.launch-' + randomUUID() + '.args');
    const quoted = args.jvm.map(arg => '"' + arg.replace(/[\\"]/g, '\\$&') + '"');
    await fs.writeFile(argfile, quoted.join('\n'), 'utf8');
    command = ['@' + argfile, ...args.game];
  }
  signal.throwIfAborted();
  progress('starting', '正在启动 Minecraft');
  const logDir = path.join(files.instance, 'logs');
  await fs.mkdir(logDir, { recursive: true });
  signal.throwIfAborted();
  const log = settings.logs === false ? null : await fs.open(path.join(logDir, 'ebuild-launch.log'), 'w');
  try {
    const child = spawn(java, command, {
      cwd: files.instance,
      windowsHide: true,
      stdio: ['ignore', log?.fd ?? 'ignore', log?.fd ?? 'ignore'],
    });
    const closed = new Promise(resolve => { child.once('close', resolve); });
    if (argfile) closed.then(() => fs.rm(argfile, { force: true })).catch(() => {});
    const started = new Promise((resolve, reject) => {
      child.once('spawn', resolve);
      child.once('error', reject);
    });
    await started;
    return { child, closed };
  } finally {
    await log?.close();
  }
}

module.exports = { versionList, installGame, launch, resolveJava, javaMajor };
