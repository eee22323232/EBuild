const detailRoot = el('resource-detail');
const detailStack = [];
const releaseChannels = { release: '正式版', beta: 'Beta', alpha: 'Alpha' };
const detailLoaderNames = { ...loaderNames, minecraft: '原版', forge: 'Forge', quilt: 'Quilt',
  iris: 'Iris', optifine: 'OptiFine', canvas: 'Canvas', vanilla: '原版' };
let searchReturn;

function resourceIcon(project) {
  const icon = element('div', (typeNames[project.project_type] || '资').slice(0, 1), 'project-icon');
  if (project.icon_url?.startsWith('https://cdn.modrinth.com/')) {
    const image = element('img', undefined, 'project-icon');
    image.src = project.icon_url;
    image.alt = '';
    image.addEventListener('load', () => icon.replaceChildren(image), { once: true });
  }
  return icon;
}

function clearResourceDetail() {
  if (!detailStack.length) return;
  detailStack.length = 0;
  detailRoot.replaceChildren();
  searchReturn = undefined;
}

function showResourceView(view, focus, returning = false) {
  const alreadyInDetail = !detailRoot.hidden;
  showPage('resource-detail');
  detailRoot.replaceChildren(view.node);
  if (alreadyInDetail) slideIn(view.node, 0, returning ? 19 : -19);
  syncIndicators();
  el('main').scrollTop = view.scroll || 0;
  (focus || view.back)?.focus({ preventScroll: true });
  actionState();
}

function backFromResource() {
  const previous = detailStack.pop();
  const view = detailStack.at(-1);
  if (view) {
    showResourceView(view, previous?.anchor, true);
  } else {
    const restore = searchReturn;
    searchReturn = undefined;
    detailRoot.replaceChildren();
    showPage('downloads');
    if (restore) {
      el('main').scrollTop = restore.scroll;
      restore.anchor?.focus({ preventScroll: true });
    }
  }
}

function detailDate(value) {
  const date = new Date(value);
  return value && !Number.isNaN(date.getTime()) ? date.toLocaleDateString('zh-CN') : '';
}

function downloadsText(value) {
  return Number(value ?? 0).toLocaleString('zh-CN') + ' 次下载';
}

function series(version) {
  return version.match(/^\d+\.\d+/)?.[0] ?? version;
}

function isStableGame(version, tags) {
  return tags.has(version) ? tags.get(version).type === 'release' : /^\d+(?:\.\d+){1,2}$/.test(version);
}

function installablePack(release) {
  return release.loaders.some(loader => loader === 'minecraft' || Object.hasOwn(loaderNames, loader));
}

function matchesInstance(release, type, instance) {
  if (!instance || !release.game_versions.includes(instance.version)) return false;
  if (type === 'modpack') return release.loaders.includes(instance.loader) ||
    (instance.loader === 'vanilla' && release.loaders.includes('minecraft'));
  return type !== 'mod' || (instance.loader !== 'vanilla' && release.loaders.includes(instance.loader));
}

function groupVersions(releases, type, view) {
  const groups = new Map();
  const current = selectedInstance();
  for (const release of releases) {
    if (view.compatible && !matchesInstance(release, type, current)) continue;
    if (view.loader && !release.loaders.includes(view.loader)) continue;
    for (const game of new Set(release.game_versions)) {
      if (isStableGame(game, view.gameTags) === view.snapshots) continue;
      if (view.game && (view.exact ? game !== view.game : series(game) !== view.game)) continue;
      if (view.compatible && game !== current?.version) continue;
      const loaders = type === 'mod' || type === 'modpack' ? release.loaders : [];
      for (const loader of new Set(loaders.length ? loaders : [''])) {
        if (loader && view.loader && loader !== view.loader) continue;
        if (loader && view.compatible && loader !== current.loader && !(loader === 'minecraft' && current.loader === 'vanilla')) continue;
        const key = game + ':' + loader;
        if (!groups.has(key)) groups.set(key, { key, game, loader, releases: [] });
        groups.get(key).releases.push(release);
      }
    }
  }
  return [...groups.values()].sort((a, b) => b.game.localeCompare(a.game, 'en', { numeric: true }) || a.loader.localeCompare(b.loader));
}

async function openResourceDetail(project, anchor) {
  const parent = detailStack.at(-1);
  const id = project.id ?? project.project_id;
  if (!id) return;
  if (parent?.id === id) return;
  if (parent) parent.scroll = el('main').scrollTop;
  else searchReturn = { scroll: el('main').scrollTop, anchor };
  const filter = parent ? { version: parent.game, loader: parent.loader } : resourceQuery;
  const view = { id, anchor, node: element('div', undefined, 'detail-layout'), scroll: 0,
    game: filter?.version ?? '', loader: filter?.loader ?? '', exact: parent?.exact ?? Boolean(filter?.version),
    compatible: false, expanded: new Map(), dependencies: new Map(), pinned: new Map(), intro: false };
  const back = button(parent ? '← 返回上一级' : '← 返回搜索结果', backFromResource, 'text-button');
  const body = element('div', undefined, 'detail-body');
  view.back = back;
  view.node.append(back, body);
  detailStack.push(view);
  showResourceView(view);
  async function load() {
    const loading = element('p', '正在读取 ' + project.title + '…', 'detail-card');
    loading.setAttribute('role', 'status');
    body.replaceChildren(loading);
    try {
      view.data = await api.detail(id);
      if (!detailStack.includes(view)) return;
      buildDetail(view, body);
      if (detailStack.at(-1) === view) actionState();
    } catch (error) {
      if (!detailStack.includes(view)) return;
      body.replaceChildren(element('p', error.message, 'detail-card'), button('重试', load));
    }
  }
  await load();
}

function buildDetail(view, body) {
  const { project, releases, wikiUrl, description, gameVersionTags } = view.data;
  const tags = view.gameTags = new Map(gameVersionTags.map(tag => [tag.version, tag]));
  view.snapshots = Boolean(view.game && view.exact && !isStableGame(view.game, tags));
  const header = element('div', undefined, 'detail-card detail-header');
  const overview = element('div', undefined, 'detail-overview');
  const identity = element('div');
  const updated = detailDate(project.updated);
  identity.append(element('h1', project.title), element('p', project.description),
    element('small', [typeNames[project.project_type], 'Modrinth', downloadsText(project.downloads), updated && '更新于 ' + updated].filter(Boolean).join(' · ')));
  overview.append(resourceIcon(project), identity);
  const actions = element('div', undefined, 'detail-actions');
  const website = 'https://modrinth.com/' + project.project_type + '/' + encodeURIComponent(project.slug);
  const linkButton = (label, url) => button(label, () => report(() => api.openResourceLink(url)));
  actions.append(linkButton('打开 Modrinth', website), button('复制名称', () => report(async () => {
    await api.copyResourceName(project.title);
    displayStatus('已复制 ' + project.title);
  })), linkButton('在 MC 百科搜索', 'https://search.mcmod.cn/s?key=' + encodeURIComponent(project.title)));
  if (wikiUrl) actions.append(linkButton('打开 MC 百科', wikiUrl));
  header.append(overview, actions);

  const tabs = element('div', undefined, 'tabs');
  const versionsButton = button('版本列表', () => toggle(false));
  const introButton = button('项目介绍', () => toggle(true));
  tabs.append(versionsButton, introButton);
  const versions = element('div', undefined, 'detail-body');
  const intro = element('article', undefined, 'detail-card detail-intro');
  function toggle(value) {
    view.intro = value;
    switchTab([versionsButton, introButton], [versions, intro], value ? 1 : 0);
  }
  const filters = element('div', undefined, 'detail-card detail-filters');
  const gameBar = element('div', undefined, 'detail-game-bar');
  const games = versionStrip('Minecraft 正式版');
  const snapshots = versionStrip('Minecraft 快照与预发布');
  snapshots.id = 'detail-snapshot-versions-' + detailStack.indexOf(view);
  const triangle = button('▾', () => {
    view.snapshots = !view.snapshots;
    selectGame('', false);
  }, 'detail-snapshot-toggle');
  triangle.setAttribute('aria-controls', snapshots.id);
  gameBar.append(games, triangle);
  const loader = element('select');
  loader.setAttribute('aria-label', '加载器筛选');
  const loaderValues = [...new Set(releases.flatMap(item => item.loaders).filter(Boolean))];
  if (view.loader && !loaderValues.includes(view.loader)) loaderValues.push(view.loader);
  options(loader, [['', '全部加载器'], ...loaderValues.sort().map(key => [key, detailLoaderNames[key] || key])]);
  loader.value = view.loader;
  const onlyCompatible = element('input');
  onlyCompatible.type = 'checkbox';
  const compatibleLabel = element('label', undefined, 'check');
  compatibleLabel.append(onlyCompatible, element('span', '仅看当前实例兼容版本'));
  const controls = element('div', undefined, 'detail-actions');
  controls.append(loader, compatibleLabel);
  loader.hidden = !loaderValues.length || (loaderValues.length === 1 && ['minecraft', 'vanilla'].includes(loaderValues[0]));
  filters.append(gameBar, snapshots, controls);
  const list = element('div', undefined, 'detail-body');
  versions.append(filters, list);
  const allGames = [...new Set(releases.flatMap(item => item.game_versions))];
  const gameValues = [...new Set(allGames.filter(game => isStableGame(game, tags)).map(series))]
    .sort((a, b) => b.localeCompare(a, 'en', { numeric: true }));
  const snapshotValues = allGames.filter(game => !isStableGame(game, tags)).sort((a, b) => {
    const aDate = tags.get(a)?.date;
    const bDate = tags.get(b)?.date;
    return (aDate && bDate ? bDate.localeCompare(aDate) : Number(Boolean(bDate)) - Number(Boolean(aDate))) ||
      b.localeCompare(a, 'en', { numeric: true });
  });
  triangle.hidden = !snapshotValues.length;
  const chips = [];
  function selectGame(game, exact) {
    view.exact = exact;
    view.game = game;
    update(true);
  }
  function addChip(strip, game, exact, label) {
    const snapshot = strip === snapshots;
    const chip = button(label, () => { view.snapshots = snapshot; selectGame(game, exact); });
    chips.push({ game, exact, chip, snapshot });
    strip.append(chip);
  }
  addChip(games, '', false, '全部');
  if (view.exact && view.game && !view.snapshots) addChip(games, view.game, true, view.game + (gameValues.includes(view.game) ? '（精确）' : ''));
  for (const game of gameValues) addChip(games, game, false, game);
  addChip(snapshots, '', false, '全部快照');
  for (const game of snapshotValues) addChip(snapshots, game, true, game);
  loader.addEventListener('change', () => { view.loader = loader.value; update(true); });
  onlyCompatible.addEventListener('change', () => { view.compatible = onlyCompatible.checked; update(true); });
  const sorted = releases.slice().sort((a, b) => String(b.date_published).localeCompare(String(a.date_published)));
  function update(animate = false) {
    const instance = selectedInstance();
    onlyCompatible.disabled = !instance;
    if (!instance) view.compatible = false;
    onlyCompatible.checked = view.compatible;
    snapshots.hidden = !view.snapshots;
    triangle.textContent = view.snapshots ? '▴' : '▾';
    const triangleLabel = view.snapshots ? '收起快照与预发布版本' : '查看快照与预发布版本';
    triangle.title = triangleLabel;
    triangle.setAttribute('aria-label', triangleLabel);
    triangle.setAttribute('aria-expanded', String(view.snapshots));
    for (const { game, exact, chip, snapshot } of chips) {
      chip.setAttribute('aria-pressed', String(view.game === game && view.exact === exact && view.snapshots === snapshot));
    }
    const groups = groupVersions(sorted, project.project_type, view);
    const rows = groups.map((group, index) => versionGroup(view, group, index === 0));
    list.replaceChildren(...rows);
    if (animate) animateVisibleRows(list, rows);
    if (!groups.length) list.append(element('p', '没有匹配的发布版本。可以调整游戏版本、加载器或兼容筛选。', 'empty'));
    actionState();
  }

  // This HTML is generated in the main process with raw HTML and Markdown images disabled.
  if (description) {
    intro.innerHTML = description;
    for (const a of intro.querySelectorAll('a')) {
      const href = a.getAttribute('href');
      let target;
      try { target = new URL(href, website + '/'); } catch { /* Unsupported link. */ }
      if (!['https:', 'http:'].includes(target?.protocol)) { a.removeAttribute('href'); continue; }
      a.href = target.href;
      a.addEventListener('click', event => { event.preventDefault(); report(() => api.openResourceLink(target.href)); });
      a.addEventListener('auxclick', event => event.preventDefault());
    }
  } else intro.append(element('p', '这个项目没有提供长介绍。'));
  intro.append(element('p', '图片和视频可在 Modrinth 原页查看。'), linkButton('打开 Modrinth', website));
  body.replaceChildren(header, tabs, versions, intro);
  view.update = update;
  toggle(false);
  update();
}

function versionStrip(label) {
  const strip = element('div', undefined, 'detail-pills');
  strip.setAttribute('role', 'group');
  strip.setAttribute('aria-label', label);
  strip.addEventListener('wheel', event => {
    if (event.shiftKey || event.deltaX || !event.deltaY || strip.scrollWidth <= strip.clientWidth) return;
    const previous = strip.scrollLeft;
    strip.scrollLeft += event.deltaY * (event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? strip.clientWidth : 1);
    if (strip.scrollLeft !== previous) event.preventDefault();
  }, { passive: false });
  return strip;
}

function updateDetailInstance() {
  detailStack.at(-1)?.update?.();
}

function dependencySignature(dependencies) {
  return JSON.stringify(dependencies.map(item => [item.project_id, item.version_id, item.file_name].map(value => value ?? '')).map(item => JSON.stringify(item)).sort());
}

function loadDependencies(view) {
  view.dependencyRequest ??= api.dependencies(view.data.project.id).then(value => {
    view.dependencies = new Map(value.projects.map(project => [project.id, project]));
    view.pinned = new Map(value.releases.map(release => [release.id, release]));
  }).catch(() => {});
  return view.dependencyRequest;
}

function dependencyBlock(view, dependencies) {
  const container = element('div', undefined, 'detail-dependencies');
  container.append(element('h3', '必需依赖'));
  const list = element('div', undefined, 'dependency-cards');
  list.append(element('small', '正在读取前置资料…'));
  container.append(list);
  loadDependencies(view).then(() => {
    list.replaceChildren(...dependencies.map(dependency => {
      const pinned = view.pinned.get(dependency.version_id);
      const key = pinned?.project_id ?? dependency.project_id;
      const project = view.dependencies.get(key);
      const title = project?.title || dependency.file_name || key || dependency.version_id || '未知依赖';
      const info = element('div');
      info.append(element('strong', title));
      if (pinned?.version_number) info.append(element('small', '指定版本 · ' + pinned.version_number));
      if (project?.description) info.append(element('small', project.description));
      if (!key) { info.append(element('small', '需从项目页面查找此文件')); return info; }
      const card = button('', () => openResourceDetail(project || { id: key, title }, card), 'dependency-card');
      card.append(resourceIcon(project || {}), info);
      return card;
    }));
  });
  return container;
}

function versionGroup(view, group, first) {
  const card = element('details', undefined, 'detail-card version-group');
  card.open = view.expanded.get(group.key) ?? first;
  const summary = element('summary', [detailLoaderNames[group.loader] || group.loader, group.game].filter(Boolean).join(' '));
  summary.append(element('small', group.releases.length + ' 个版本'));
  const body = element('div', undefined, 'version-group-body');
  card.append(summary, body);
  function populate() {
    if (!card.open || body.childElementCount) return;
    const first = group.releases[0].dependencies;
    const same = group.releases.every(release => dependencySignature(release.dependencies) === dependencySignature(first));
    if (same && first.length) body.append(dependencyBlock(view, first));
    body.append(element('h3', '版本列表'));
    for (const release of group.releases) body.append(versionRow(view, group, release, !same));
    actionState();
  }
  card.addEventListener('toggle', () => {
    if (!card.isConnected) return;
    view.expanded.set(group.key, card.open);
    populate();
  });
  populate();
  return card;
}

function versionRow(view, group, release, showDependencies) {
  const type = view.data.project.project_type;
  const row = element('div', undefined, 'detail-release');
  const line = element('div', undefined, 'detail-release-line');
  const info = element('div');
  const name = element('div', undefined, 'detail-release-name');
  name.append(element('strong', release.name || release.version_number), element('span', releaseChannels[release.version_type] || release.version_type, 'release-badge'));
  const date = detailDate(release.date_published);
  const compatible = instances.some(item => matchesInstance(release, type, item));
  info.append(name, element('small', [release.version_number, downloadsText(release.downloads), date && '发布于 ' + date,
    type !== 'modpack' && (compatible ? '有兼容实例' : '无兼容实例，可另存')].filter(Boolean).join(' · ')));
  const actions = element('div', undefined, 'detail-actions');
  if (!compatible && type !== 'modpack' && (!group.loader || group.loader === 'minecraft' || Object.hasOwn(loaderNames, group.loader))) {
    const create = button('新建匹配实例', () => createInstance(group.game, group.loader === 'minecraft' ? 'vanilla' : group.loader || 'vanilla'));
    create.dataset.mutates = '';
    actions.append(create);
  }
  if (type === 'modpack' && !installablePack(release)) actions.append(element('small', '此加载器暂不支持'));
  else {
    const install = button('安装', () => installDetailVersion(view, group, release));
    install.dataset.mutates = '';
    actions.append(install);
  }
  line.append(info, actions);
  row.append(line);
  if (showDependencies && release.dependencies.length) {
    const more = element('details', undefined, 'release-dependencies');
    more.append(element('summary', '查看 ' + release.dependencies.length + ' 项必需依赖'));
    more.addEventListener('toggle', () => { if (more.open && more.childElementCount === 1) more.append(dependencyBlock(view, release.dependencies)); });
    row.append(more);
  }
  return row;
}

async function installDetailVersion(view, group, release) {
  const project = view.data.project;
  const folder = { mod: 'mods', shader: 'shaderpacks', resourcepack: 'resourcepacks' }[project.project_type];
  const current = selectedInstance()?.id;
  if (!folder) {
    const result = await run(() => api.chooseResourceDestination(project.id, release.id, current, group.game, group.loader));
    if (result && detailStack.at(-1) === view) confirmPlan(result.plan);
    return;
  }
  const compatible = instances.filter(item => !item.externalMissing && matchesInstance(release, project.project_type, item));
  const panel = showModal('选择安装位置');
  panel.body.append(element('p', project.title + ' · ' + release.version_number, 'destination-release'));
  const targets = element('select');
  options(targets, compatible.map(item => [item.id, item.name + ' · ' + instanceVersionLabel(item)]));
  if (compatible.some(item => item.id === current)) targets.value = current;
  targets.dataset.mutates = '';

  function isCurrent() { return modal.open && panel.body.isConnected && detailStack.at(-1) === view; }
  async function choose(useInstanceFolder) {
    const instanceId = targets.value || current;
    const result = await run(() => api.chooseResourceDestination(project.id, release.id, instanceId, group.game, group.loader, useInstanceFolder), panel.error);
    if (!result || !isCurrent()) return;
    if (result.mode === 'folder') confirmExport(result);
    else confirmPlan(result.plan, result.instanceId, result.directory);
  }

  const actions = element('div', undefined, 'form-actions');
  const browse = button('选择其他文件夹…', () => choose(false));
  browse.dataset.mutates = '';
  actions.append(button('取消', closeModal), browse);
  if (compatible.length) {
    const preview = element('div', undefined, 'resource-folder-preview');
    const header = element('div', undefined, 'resource-folder-heading');
    const identity = element('div');
    const folderName = element('strong');
    const directory = element('small', '', 'path');
    identity.append(folderName, directory);
    header.append(iconImage('icons/folder.svg', 'resource-folder-icon'), identity);
    const files = element('div', undefined, 'resource-folder-files');
    files.setAttribute('aria-live', 'polite');
    preview.append(header, files);
    panel.body.append(field('目标实例', targets), preview);

    let revision = 0;
    async function loadPreview() {
      const request = ++revision;
      const target = compatible.find(item => item.id === targets.value);
      folderName.textContent = target.name + ' / ' + folder;
      directory.textContent = '';
      files.replaceChildren(element('p', '正在读取文件夹…'));
      try {
        const [info] = await api.instanceResources(target.id, folder);
        if (request !== revision || !isCurrent()) return;
        directory.textContent = info.directory;
        files.replaceChildren(info.files.length ? resourceFileList(info.files, info.icons) : element('p', '此文件夹里还没有文件。'));
      } catch (error) {
        if (request === revision && isCurrent()) files.replaceChildren(element('p', error.message));
      }
    }

    targets.addEventListener('change', loadPreview);
    loadPreview();
    const next = button('继续安装', () => choose(true), 'primary');
    next.dataset.mutates = '';
    actions.append(next);
  } else {
    panel.body.append(element('p', '没有兼容的实例。可以将文件下载到其他文件夹。', 'results-info'));
  }
  panel.body.append(actions);
  actionState();
}

function confirmExport(result) {
  const panel = showModal('下载到文件夹');
  panel.body.append(element('p', result.title + ' · ' + result.version), element('p', result.directory, 'path'),
    element('p', '文件将保存在所选位置，不会由 EBuild 安装到实例。'));
  const withDependencies = element('input');
  withDependencies.type = 'checkbox';
  const loader = element('select');
  options(loader, [['', '选择加载器'], ...Object.entries(detailLoaderNames).filter(([key]) => ['fabric', 'neoforge', 'forge', 'quilt'].includes(key))]);
  const loaderField = field('用于匹配前置的加载器', loader);
  loaderField.hidden = true;
  if (result.hasDependencies) {
    const label = element('label', undefined, 'check');
    label.append(withDependencies, element('span', '同时下载必需依赖'));
    withDependencies.addEventListener('change', () => { loaderField.hidden = !withDependencies.checked || Boolean(result.dependencyLoader); });
    panel.body.append(label, loaderField);
  }
  const actions = element('div', undefined, 'form-actions');
  const submit = button('下载', async () => {
    const saved = await run(() => api.exportResource(result.key, withDependencies.checked, loader.value), panel.error);
    if (saved) { closeModal(); displayStatus('已下载 ' + result.title); }
  }, 'primary');
  submit.dataset.mutates = '';
  actions.append(submit);
  panel.body.append(actions);
}
