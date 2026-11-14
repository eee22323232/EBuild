# EBuild WebView2 体积实验

这个分支只回答一个问题：保留 Node.js 后端、生产依赖和现有 HTML/CSS 页面，换成系统 WebView2 后，Windows x64 玩家初次装好会快多少？实验版使用独立安装 ID、程序目录及配置目录。它没有接通微软登录、游戏启动、加载器安装或 Modrinth，不能取代正式版。

打包：用装有 x64 Node、生产依赖和 Inno Setup 的 Windows 环境，从实验分支运行以下命令。`LauncherRoot` 指向包含当前完整版首页和后端的主工作区，实验分支本身不跟踪它的未提交文件。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\webview2-prototype\build.ps1 -LauncherRoot <EBuild主工作区> -Output <输出目录> -Iscc <ISCC.exe路径>
```

安装后打开「EBuild WebView2 实验版」，可以切换 Fabric 和原版测试实例。「设置 → 启动器设置」可以选择测试数据目录，并在选中位置下新建独立实验子目录。默认配置在 `%LOCALAPPDATA%\EBuild-WebView2-Prototype`。

| 指标 | Electron dev.0.1.1 | WebView2 原型 |
| --- | ---: | ---: |
| 安装包 | 117,326,840 B | 29,603,191 B |
| 安装目录的文件大小之和（含卸载器） | 393,363,312 B | 111,633,863 B |
| 50 Mbps，第 1 轮：开始下载到首页就绪 | 43.37 秒 | 19.29 秒 |
| 100 Mbps，第 2 轮 | 24.05 秒 | 11.82 秒 |
| 50 Mbps，第 3 轮 | 33.05 秒 | 14.80 秒 |
| 单独首次打开首页（三轮范围） | 1.12–1.27 秒 | 1.33–1.55 秒 |

Windows 11 Pro 24H2，WebView2 157.0.4341.46，Node 24.19.0。同一台机器、通过本地 HTTP 限速实际下载、用同样的无人值守参数安装到新目录，用新配置启动，等首页状态为「就绪」。没有清理系统文件缓存或重启。使用 `benchmark.cjs` 可重跑；测试会写临时安装记录并安装、卸载应用，请在未安装这两款启动器的测试帐号或虚拟机运行。原型验证了首页、实例切换、数据目录切换和测试实例复制；原生目录弹窗未能通过桌面自动化验证。

目标是 30 MB 以下，且机器已有 WebView2 时，下载到首页至少两倍快。这三轮满足目标；把它当成进入完整兼容性验证的理由。没有 WebView2 时，安装器会调用包内附带的 1,897,688 B [微软 Evergreen Bootstrapper](https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution) 在线补装。本机已有运行时，所以缺运行库场景的流量和时间、Windows 10 上的实际兼容性，以及账号和游戏功能迁移都还没有验证。

原型安装包 SHA-256：`15b697a40a1a7c0da0a775255809f8285b3848505ae6cff1a4d44798b1b9ebf5`
