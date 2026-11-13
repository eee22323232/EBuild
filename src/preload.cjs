const { contextBridge, ipcRenderer } = require('electron');

const channels = {
  catalog: 'catalog', loaders: 'loaders', getSettings: 'settings:get', saveSettings: 'settings:save',
  chooseJava: 'java:select', javaList: 'java:list', installJava: 'java:install',
  chooseData: 'data:choose', changeData: 'data:change', openData: 'data:open', clearCache: 'data:cache',
  instances: 'instances:list', selectInstance: 'instances:select', createInstance: 'instances:create',
  editInstance: 'instances:edit', openInstance: 'instances:folder', instanceResources: 'instances:resources',
  start: 'game:start', cancel: 'task:cancel', search: 'resources:search', versions: 'resources:versions',
  plan: 'resources:plan', install: 'resources:install', importPack: 'resources:import',
  account: 'account:summary', login: 'account:login', loginBrowser: 'account:browser', logout: 'account:logout',
};
const bridge = Object.fromEntries(Object.entries(channels).map(([name, channel]) => [name, async (...args) => {
  const result = await ipcRenderer.invoke(channel, ...args);
  if (result.error) throw new Error(result.error);
  return result.value;
}]));
bridge.onStatus = callback => {
  const listener = (_, update) => callback(update);
  ipcRenderer.on('status', listener);
  return () => ipcRenderer.removeListener('status', listener);
};
contextBridge.exposeInMainWorld('launcher', bridge);
