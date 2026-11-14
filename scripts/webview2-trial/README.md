# EBuild WebView2 + C# 开发版

面向 Windows 10 22H2 / Windows 11 x64。沿用 EBuild 网页界面，用 C# 和系统 .NET Framework 4.8 替换 Electron/Node 后端。安装器检测并在需要时在线补装 WebView2；安装 ID、配置目录和游戏目录与 Electron 版隔离。

Visual Studio 调试、目录结构和自动验证命令见 [贡献指南](../../CONTRIBUTING.md)。

本地测试用 Windows 自带的 .NET Framework 编译器构建：

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\webview2-trial\build.ps1 -Output <输出目录> -Cache <构建缓存>
~~~

双击输出目录的 stage 子文件夹里的 EBuild-WebView2.exe，保留同目录的 DLL 和 ui 文件夹。电脑需要已有 WebView2。只有显式添加 -Iscc <ISCC.exe路径> 时才会额外生成安装包。

旧实例从「设置 → 启动器设置 → 导入旧实例副本」导入。选择包含 .ebuild.json 的实例文件夹。程序会创建不同 ID 的实例，复制存档、模组、光影、材质包、配置、截图和常用玩家选项；Java 路径、游戏本体、库、运行时、日志和账号凭据不会复制。游戏文件在首次启动时按需准备。

2026-11-15 验证环境：Windows 11 24H2，已有 WebView2 157.0.4341.46。安装器 4,105,220 B，干净安装后的程序文件 5,589,601 B，首次打开至首页就绪后配置和缓存 7,985,064 B，合计 13,574,665 B（13.57 MB）。按文件长度统计，含卸载器，不含共享 WebView2 运行库、Minecraft、Java 或玩家数据。

- 最新安装包：首页、下载页、账号页、Minecraft 与两个加载器版本列表、Modrinth 搜索和详情、Sodium 实际下载、微软设备码与取消均通过。
- 离线 Java 版 26.2：原版、Fabric 0.20.1、NeoForge 26.2.0.93 的真实安装与启动日志显示已进入渲染阶段。使用独立测试目录和已有的官方游戏缓存。
- 旧实例的文件筛选与复制通过隔离测试。系统标题栏和页面标题已合为一条蓝色窗口栏。
- 待验证：拥有 Minecraft Java 版的账号完整授权及登录后启动、真实整合包及其他类别的文件下载、Windows 10、缺 WebView2 时的额外占用。完成前不分发给朋友。

开发安装器 SHA-256：84374AD37100BD32921E185BCBED2B70713C81DBE4B7240F9929BBB250C7F17A
