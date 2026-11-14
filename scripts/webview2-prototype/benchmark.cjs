// Run with Node 24+ on the test PC. Uses scratch profiles and a capped loopback download.
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const http = require('node:http');
const { createReadStream, createWriteStream } = require('node:fs');
const { spawn } = require('node:child_process');
const { pipeline } = require('node:stream/promises');
const { performance } = require('node:perf_hooks');
const { setTimeout: wait } = require('node:timers/promises');
const { once } = require('node:events');

async function until(check, description) {
  const end = performance.now() + 24000;
  while (performance.now() < end) {
    const result = await check();
    if (result) return result;
    await wait(40);
  }
  throw new Error('Timed out: ' + description);
}

async function connect(portFile) {
  const url = await until(async () => {
    const ports = await fs.readFile(portFile, 'utf8').catch(() => '');
    if (!ports) return;
    const tabs = await fetch('http://127.0.0.1:' + parseInt(ports, 10) + '/json/list').then(r => r.json()).catch(() => []);
    return tabs.find(tab => tab.url.endsWith('/index.html'))?.webSocketDebuggerUrl;
  }, 'launcher window');
  const ws = new WebSocket(url);
  await once(ws, 'open');
  let counter = 0;
  const evaluate = expression => new Promise((resolve, reject) => {
    const id = ++counter;
    const timer = setTimeout(() => { ws.removeEventListener('message', receive); reject(new Error('CDP timeout')); }, 8000);
    const receive = ({ data }) => {
      const message = JSON.parse(data);
      if (message.id !== id) return;
      clearTimeout(timer);
      ws.removeEventListener('message', receive);
      if (message.error || message.result.exceptionDetails) reject(new Error(JSON.stringify(message.error ?? message.result.exceptionDetails)));
      else resolve(message.result.result.value);
    };
    ws.addEventListener('message', receive);
    ws.send(JSON.stringify({ id, method: 'Runtime.evaluate', params: { expression, returnByValue: true, awaitPromise: true } }));
  });
  return { ws, evaluate };
}

async function runExe(exe, args) {
  const child = spawn(exe, args, { windowsHide: true, stdio: 'ignore' });
  const [code] = await once(child, 'close');
  return code;
}

async function bytesIn(dir) {
  let bytes = 0;
  for (const entry of await fs.readdir(dir, { withFileTypes: true })) {
    const item = path.join(dir, entry.name);
    if (entry.isDirectory()) bytes += await bytesIn(item);
    else bytes += (await fs.stat(item)).size;
  }
  return bytes;
}

async function main() {
  const [electron, webview, output] = process.argv.slice(2).map(x => path.resolve(x));
  // The unmodified Electron installer uses its usual Inno Setup ID. Use a test account
  // or VM and refuse to benchmark over an existing installation.
  for (const hive of ['HKCU', 'HKLM']) {
    for (const id of ['90D1E0FE-93C9-44EA-B37C-FA1CCF90EA0F', 'B2E45AFC-8F3E-4446-8C9C-CFD11CEE7BF3']) {
      for (const view of ['/reg:64', '/reg:32']) {
        const key = hive + '\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{' + id + '}_is1';
        const code = await runExe('reg.exe', ['query', key, view]);
        assert.equal(code, 1, 'Use a test account or VM without either launcher installed: ' + key);
      }
    }
  }
  const base = await fs.mkdtemp(path.join(output, 'bench-'));
  const sources = [electron, webview];
  const speeds = [50, 100, 50];
  const results = [];
  let rate;
  const server = http.createServer(async (req, res) => {
    const source = sources[Number(req.url.slice(1))];
    if (!source) { res.writeHead(404).end(); return; }
    res.writeHead(200, { 'Content-Length': (await fs.stat(source)).size });
    const start = performance.now();
    let bytes = 0;
    try {
      for await (const chunk of createReadStream(source, { highWaterMark: 256 * 1024 })) {
        bytes += chunk.length;
        const pause = bytes / rate * 1000 - (performance.now() - start);
        if (pause > 0) await wait(pause);
        if (!res.write(chunk)) await once(res, 'drain');
      }
      res.end();
    } catch { res.destroy(); }
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  try {
    for (let round = 0; round < speeds.length; round++) {
      rate = speeds[round] * 1e6 / 8;
      for (const kind of round % 2 ? [1, 0] : [0, 1]) {
        const test = await fs.mkdtemp(path.join(base, 'r' + round + 'v' + kind + '-'));
        const setup = path.join(test, 'setup.exe');
        const install = path.join(test, '测试目录');
        const profile = path.join(test, 'profile');
        const localAppData = path.join(test, 'local');
        const log = path.join(test, 'install.log');
        const silent = ['/VERYSILENT', '/SUPPRESSMSGBOXES', '/SP-', '/NORESTART'];
        const start = performance.now();
        const response = await fetch('http://127.0.0.1:' + server.address().port + '/' + kind);
        assert.equal(response.status, 200);
        await pipeline(response.body, createWriteStream(setup));
        const downloaded = performance.now();
        let child, browser;
        try {
          assert.equal(await runExe(setup, [...silent, '/DIR=' + install, '/TASKS=', '/LOG=' + log]), 0);
          const installed = performance.now();
          const env = { ...process.env, EBUILD_PROTOTYPE_PROFILE: profile, LOCALAPPDATA: localAppData,
            APPDATA: path.join(test, 'roaming') };
          if (kind) env.WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = '--remote-debugging-port=0 --remote-debugging-address=127.0.0.1';
          const exe = path.join(install, kind ? 'EBuild-WebView2.exe' : 'EBuild.exe');
          child = spawn(exe, kind ? [] : ['--user-data-dir=' + profile, '--remote-debugging-port=0'],
            { env, windowsHide: true, stdio: 'ignore' });
          const portFile = path.join(profile, kind ? 'WebView2/EBWebView/DevToolsActivePort' : 'DevToolsActivePort');
          browser = await connect(portFile);
          await until(() => browser.evaluate('document.querySelector("#status")?.textContent === "就绪"'), 'ready home page');
          const ready = performance.now();
          const sizes = { installerBytes: (await fs.stat(setup)).size, payloadBytes: await bytesIn(install) };
          if (kind) {
            assert.equal(await browser.evaluate('document.querySelector("#active-instance-name").textContent'), 'Fabric 测试实例');
            await browser.evaluate('document.querySelector("[data-instance-id=demo-vanilla]").click()');
            await until(() => browser.evaluate('document.querySelector("#active-instance-name").textContent === "原版测试实例"'), 'switch sample instance');
            assert.match(await browser.evaluate('window.launcher.getSettings().then(x => x.currentRoot)'), /游戏数据$/);
            const chosenByTest = path.join(test, '测试目录位置');
            await fs.mkdir(chosenByTest);
            const changed = await browser.evaluate('window.launcher.changeData(' + JSON.stringify(chosenByTest) + ', true)');
            assert.equal(path.dirname(changed), chosenByTest);
            assert.equal(await browser.evaluate('window.launcher.instances().then(x => x.length)'), 2);
          }
          const row = { round: round + 1, megabits: speeds[round], variant: kind ? 'WebView2' : 'Electron', ...sizes,
            downloadSeconds: (downloaded - start) / 1000, installSeconds: (installed - downloaded) / 1000,
            firstOpenSeconds: (ready - installed) / 1000, totalSeconds: (ready - start) / 1000 };
          console.log(JSON.stringify(row));
          results.push(row);
        } finally {
          browser?.ws.close();
          if (child?.pid && child.exitCode === null) await runExe('taskkill.exe', ['/pid', String(child.pid), '/t', '/f']);
          const uninstaller = path.join(install, 'unins000.exe');
          if (await fs.stat(uninstaller).catch(() => null)) assert.equal(await runExe(uninstaller, silent), 0);
        }
      }
    }
    await fs.writeFile(path.join(output, 'benchmark-results.json'), JSON.stringify(results, null, 2) + '\n');
  } finally { server.close(); }
}
module.exports = { connect };
if (require.main === module) main().catch(error => { console.error(error); process.exitCode = 1; });
