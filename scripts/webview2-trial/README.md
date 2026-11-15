# EBuild WebView2 + C# 开发版

开发分支为 `EBuild/webview2-csharp`。启动器面向 Windows 10 22H2 和 Windows 11 x64，通过 WinForms 托管 WebView2，运行在 .NET Framework 4.8 上。默认配置和游戏数据保存在当前 Windows 用户的 `%LOCALAPPDATA%\EBuild-WebView2-CSharp` 目录。

## 构建并运行

从仓库根目录运行下面的命令。构建使用 Windows 自带的 .NET Framework 编译器，并缓存 WebView2 SDK。

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\webview2-trial\build.ps1 -Output .\dist\csharp-dev -Cache .\dist\webview2-sdk-cache
~~~

双击输出的 `stage-…` 子文件夹中的 `EBuild-WebView2.exe`，保留同目录的 DLL 和 `ui` 文件夹。电脑需要 WebView2 Evergreen 运行时。若要生成带 WebView2 检测的安装器，可在命令中添加 `-Iscc` 和 `ISCC.exe` 的路径。

Visual Studio 调试、目录结构和离线验证命令见 [贡献指南](../../CONTRIBUTING.md)。

## 导入旧实例

在「设置 → 启动器设置 → 导入旧实例副本」里，选择包含 `.ebuild.json` 的旧实例文件夹。启动器会创建一个新实例，复制存档、模组、光影、材质包、配置、截图和常用玩家选项。游戏本体、库和 Java 在新实例启动时按需准备。启动器继续使用当前配置下的账号和 Java 设置。

## Windows 11 验证记录

- PowerShell 构建、前端脚本语法和 C# 离线验证已通过。离线验证覆盖版本清单筛选、文件校验和旧实例导入。
- 已实测 BMCLAPI 游戏清单、刷新、缓存和切换到 Mojang 官方源。Java 流程用独立测试实例验证了直接启动、取消下载和下载 Java 21 后自动启动。
- 已实际下载并替换两个 Sodium 版本，验证了实例文件夹预览、JAR 图标和 `mods-backup` 备份。
- 早期实测 Minecraft 26.2 的原版、Fabric 0.20.1 和 NeoForge 26.2.0.93，启动日志显示已进入渲染阶段。
- 尚待完整验证的场景包括正版账号登入后启动、真实整合包安装、Windows 10 和需要单独安装 WebView2 的机器。

## 早期安装体积测量

2026-11-15 在 Windows 11 24H2、预装 WebView2 157.0.4341.46 的环境中，安装器为 4,105,220 B。干净安装后的程序文件为 5,589,601 B，首次打开至首页就绪后配置和缓存为 7,985,064 B，合计约 13.57 MB。结果按文件长度统计，包含卸载器。共享 WebView2 运行时、Minecraft、Java 和玩家数据另计。
