const fs = require('node:fs/promises');
const path = require('node:path');
const { setTimeout: delay } = require('node:timers/promises');
const { networkFetch, writeJson } = require('./download.cjs');
const { readOptional } = require('./storage.cjs');

const oauth = 'https://login.microsoftonline.com/consumers/oauth2/v2.0/';
const scope = 'XboxLive.signin offline_access';

async function request(url, options, signal) {
  const timeout = AbortSignal.timeout(20_000);
  const response = await networkFetch(url, { ...options, signal: signal ? AbortSignal.any([signal, timeout]) : timeout });
  return { status: response.status, ok: response.ok, data: await response.json().catch(() => ({})) };
}

function form(url, values, signal) {
  return request(url, { method: 'POST', body: new URLSearchParams(values) }, signal);
}

function json(url, values, signal) {
  return request(url, {
    method: 'POST', body: JSON.stringify(values), headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
  }, signal);
}

async function deviceLogin(clientId, showCode, signal) {
  const { ok, data } = await form(oauth + 'devicecode', { client_id: clientId, scope }, signal);
  if (!ok || !data.device_code || !data.user_code) throw new Error('微软拒绝了设备登录请求，请检查应用 Client ID 和公开客户端设置。');
  showCode(data.user_code);
  const deadline = Date.now() + data.expires_in * 1000;
  let interval = (data.interval || 5) * 1000;
  while (Date.now() < deadline) {
    await delay(interval, undefined, { signal });
    const token = await form(oauth + 'token', {
      client_id: clientId, grant_type: 'urn:ietf:params:oauth:grant-type:device_code', device_code: data.device_code,
    }, signal);
    if (token.ok) return token.data;
    if (token.data.error === 'slow_down') interval += 5000;
    else if (token.data.error !== 'authorization_pending') throw new Error('微软登录未完成，请重新登录。');
  }
  throw new Error('微软登录代码已过期。');
}

async function minecraftLogin(ms, clientId, signal) {
  const user = await json('https://user.auth.xboxlive.com/user/authenticate', {
    Properties: { AuthMethod: 'RPS', SiteName: 'user.auth.xboxlive.com', RpsTicket: 'd=' + ms.access_token },
    RelyingParty: 'http://auth.xboxlive.com', TokenType: 'JWT',
  }, signal);
  if (!user.ok) throw new Error('Xbox Live 登录失败，请检查账号是否已创建 Xbox 档案。');
  const xbox = await json('https://xsts.auth.xboxlive.com/xsts/authorize', {
    Properties: { SandboxId: 'RETAIL', UserTokens: [user.data.Token] },
    RelyingParty: 'rp://api.minecraftservices.com/', TokenType: 'JWT',
  }, signal);
  if (!xbox.ok) throw new Error('Xbox 授权失败' + (xbox.data.XErr ? '（' + xbox.data.XErr + '）' : '。'));
  const claim = xbox.data.DisplayClaims?.xui?.[0];
  if (!claim?.uhs || !xbox.data.Token) throw new Error('Xbox 未返回有效身份信息。');
  const result = await json('https://api.minecraftservices.com/authentication/login_with_xbox', {
    identityToken: 'XBL3.0 x=' + claim.uhs + ';' + xbox.data.Token,
  }, signal);
  if (result.status === 403) throw new Error('Minecraft Services 拒绝了应用授权。请使用已获批准的 EBuild Client ID。');
  if (!result.ok || !result.data.access_token) throw new Error('Minecraft Services 登录失败。');
  const headers = { Authorization: 'Bearer ' + result.data.access_token };
  const entitlement = await request('https://api.minecraftservices.com/entitlements/mcstore', { headers }, signal);
  if (!entitlement.ok || !entitlement.data.items?.length) throw new Error('未能验证此微软账号的 Minecraft Java 版所有权。');
  const profile = await request('https://api.minecraftservices.com/minecraft/profile', { headers }, signal);
  if (!profile.ok || !profile.data.id || !profile.data.name) throw new Error('未找到 Minecraft Java 版玩家档案。');
  return {
    id: profile.data.id, name: profile.data.name, token: result.data.access_token, xuid: claim.xid ?? '',
    clientId, expires: Date.now() + result.data.expires_in * 1000, refresh: ms.refresh_token,
  };
}

function accounts(profileRoot, safeStorage) {
  const file = path.join(profileRoot, 'microsoft-account.json');
  let active;
  async function get() {
    if (active) return active;
    const stored = await readOptional(file);
    if (!stored) return null;
    active = JSON.parse(safeStorage.decryptString(Buffer.from(stored.encrypted, 'base64')));
    return active;
  }
  async function save(account) {
    if (!safeStorage.isEncryptionAvailable()) throw new Error('当前 Windows 用户环境无法加密登录凭据。');
    const encrypted = safeStorage.encryptString(JSON.stringify(account)).toString('base64');
    await writeJson(file, { encrypted });
    active = account;
    return { name: account.name };
  }
  return {
    summary: async () => { const account = await get(); return account && { name: account.name }; },
    login: async (clientId, code, signal) => save(await minecraftLogin(await deviceLogin(clientId, code, signal), clientId, signal)),
    logout: async () => { await fs.rm(file, { force: true }); active = null; },
    forLaunch: async signal => {
      let account = await get();
      if (!account) throw new Error('请先登录微软账号，或选择离线档案。');
      if (account.expires < Date.now() + 60_000) {
        const token = await form(oauth + 'token', {
          client_id: account.clientId, grant_type: 'refresh_token', refresh_token: account.refresh, scope,
        }, signal);
        if (!token.ok) throw new Error('微软登录已过期，请重新登录。');
        account = await minecraftLogin(token.data, account.clientId, signal);
        await save(account);
      }
      return account;
    },
  };
}

module.exports = { accounts };
