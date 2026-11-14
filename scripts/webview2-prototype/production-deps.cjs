const fs = require('node:fs');
const path = require('node:path');
const [root, stage] = process.argv.slice(2);
const lock = require(path.join(root, 'package-lock.json'));
for (const [key, pkg] of Object.entries(lock.packages)) {
  if (!key.startsWith('node_modules/') || pkg.dev) continue;
  const source = path.join(root, key);
  if (pkg.optional && !fs.existsSync(source)) continue;
  fs.cpSync(source, path.join(stage, key), {
    recursive: true,
    // Keep every locked production package and its licenses; drop developer docs and sourcemaps.
    filter: item => item === source || (!['node_modules', 'docs'].includes(path.basename(item)) &&
      !/\.(?:map|md)$/i.test(item)) || /[\\/](?:license|licence|notice|copyright|third.party)[^\\/]*\.md$/i.test(item),
  });
}
