# EBuild 启动器

EBuild 是一个采用 MIT 许可证的 Windows x64 Minecraft Java 版桌面启动器。本分支 `EBuild/webview2-csharp` 使用 C#、WinForms 和 WebView2，面向 Windows 10 22H2 和 Windows 11 x64。

## 当前功能

- 安装 Minecraft 原版、Fabric 和 NeoForge。同一版本可以创建多个独立实例，共用游戏本体和 Java。
- 游戏本体优先使用 BMCLAPI 国内镜像，失败时回退到 Mojang 官方源。可在「设置 → 网络」切换渠道，下载页会显示版本列表的实际来源。
- 在 Modrinth 查找模组、整合包、光影和材质。安装资源时可以预览兼容实例中的目标文件夹和已有文件，也可以选择其他下载位置。安装前会列出必需依赖。
- 模组列表和文件夹预览支持读取 JAR 中的图标。安装到实例时，识别到同一 Modrinth 项目的其他版本会提示替换，并将确认替换的文件备份到实例的 `mods-backup`。
- 在「启动游戏」时检查实例需要的 Java。找不到兼容版本时可以选择「下载并启动」。
- 支持离线档案和微软账号设备码登录。账号页可以预览角色，并为离线档案导入皮肤和披风。
- 实例默认继承全局游戏设置，可单独覆盖 Java、内存、分辨率和启动参数。设置还提供主题、代理、下载并发和数据目录管理。

整合包支持从 Modrinth 安装或导入本地 `.mrpack`，安装后创建独立实例。CurseForge 和双源聚合仍待接入。Forge 与 Quilt 可用于筛选搜索结果。

## 从源码运行

在 Windows x64 上，从仓库根目录运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\webview2-trial\build.ps1 -Output .\dist\csharp-dev -Cache .\dist\webview2-sdk-cache
```

构建完成后，打开输出的 `stage-…` 文件夹并双击 `EBuild-WebView2.exe`。运行需要 WebView2 Evergreen 运行时，同目录的 DLL 和 `ui` 文件夹需要随程序保留。

默认游戏数据目录为 `%LOCALAPPDATA%\EBuild-WebView2-CSharp\游戏数据`。可在「设置 → 启动器设置」更换目录或导入旧实例副本。实例日志默认写入实例目录下的 `logs/ebuild-launch.log`。

- [开发版运行、导入和验证记录](scripts/webview2-trial/README.md)
- [Visual Studio 调试与贡献指南](CONTRIBUTING.md)

## 历史版本

[`dev.0.1.0` 预发行版](https://github.com/eee22323232/EBuild/releases/tag/dev.0.1.0) 是此前的 Electron 版本。

[MIT 许可证](LICENSE)
