document.addEventListener('DOMContentLoaded', () => {
  const node = document.getElementById('current-root');
  const button = document.createElement('button');
  button.type = 'button';
  button.textContent = '导入旧实例副本';
  button.addEventListener('click', () => run(async () => {
    const result = await api.importInstance();
    if (!result) return;
    await refreshInstances();
    showPage('instances');
    displayStatus('已导入 ' + result.name + '，游戏和 Java 会在首次启动时按需下载');
  }));
  node.closest('.setting-row').querySelector('.actions').append(button);
});
