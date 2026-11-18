# EBuild WebView2 + C# 开发版

主线分支为 `main`。启动器面向 Windows 10 22H2 和 Windows 11 x64，通过 WinForms 托管 WebView2，运行在 .NET Framework 4.8 上。默认配置和游戏数据保存在当前 Windows 用户的 `%LOCALAPPDATA%\EBuild-WebView2-CSharp` 目录。

## 构建并运行

从仓库根目录运行下面的命令。构建使用 Windows 自带的 .NET Framework 编译器，并缓存 WebView2 SDK。

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\webview2-trial\build.ps1 -Output .\dist\csharp-dev -Cache .\dist\webview2-sdk-cache
~~~

双击输出的 `stage-…` 子文件夹中的 `EBuild-WebView2.exe`，保留同目录的 DLL 和 `ui` 文件夹。电脑需要 WebView2 Evergreen 运行时。若要生成 Windows x64 安装器，在命令中添加 `-Iscc` 和 `ISCC.exe` 的路径。构建脚本会下载 Inno Setup 官方简体中文语言文件，并验证随安装器打包的微软 WebView2 引导程序签名。安装器默认安装到当前用户的 `AppData\Local\Programs\EBuild`，不允许选择磁盘根目录。

Visual Studio 调试、目录结构和离线验证命令见 [贡献指南](../../CONTRIBUTING.md)。

## GitHub Actions 安装包

构建相关文件推送到 `main` 后，[Windows installer 工作流](../../.github/workflows/windows-installer.yml) 会验证对应提交并生成 Windows x64 安装包。每次成功运行都会生成 `EBuild-Windows-x64-Installer` 工件，内含 `.exe` 和 `SHA256SUMS.txt`。发布预发行版时，把该次运行的两个文件上传到 GitHub Releases，并将标签指向该次运行使用的提交。

构建固定 Inno Setup、简体中文语言文件及 WebView2 SDK 和引导程序的版本或内容，并检查 SHA-256。更新这些依赖时同步更新下载地址及校验值。当前工作流生成未签名安装包。

## 导入旧实例

在「设置 → 启动器设置 → 导入旧实例副本」里，选择包含 `.ebuild.json` 的旧实例文件夹。启动器会创建一个新实例，复制存档、模组、光影、材质包、配置、截图和常用玩家选项。游戏本体、库和 Java 在新实例启动时按需准备。启动器继续使用当前配置下的账号和 Java 设置。

## 空间管理与安装恢复

在「设置 → 启动器设置 → 查看空间」统计占用，勾选要清理的安装包缓存、未完成下载或模组旧版备份，确认后才删除。文件校验索引、存档、已安装的游戏和 Java 会保留。清理只处理统计时列出的文件，已变化或无法访问的文件会跳过。统计按文件长度计算，外部游戏目录、共享 WebView2 运行时和其他位置的 Java 不包含在内。

游戏与 `.mrpack` 安装任务会保存在数据目录的 `downloads` 中。暂停、连接失败或重启后，可以从实例页右上角的「下载任务」手动继续；移除未完成任务会删除该任务的安装暂存文件。已完成任务只移除记录，不删除实例。整合包安装成功后会清理任务私有的源包副本。

带校验信息的文件在 `cache/transfers` 中保留下载进度。服务器支持有效的 HTTP Range 响应时续传，不支持时重新下载当前文件。下载完成后验证文件大小和哈希，再替换目标文件。主动清理未完成下载会丢弃这些进度，但保留任务记录。

整合包中数千个覆盖文件由后台解压，下载任务会显示已解压数量。该改动避免解压占住窗口消息循环；磁盘写入和整合包文件下载仍可能耗时。

## Windows 11 验证记录

- PowerShell 构建、前端脚本语法和 C# 离线验证已通过。离线验证覆盖版本清单筛选、文件校验和旧实例导入。
- 已实测 BMCLAPI 游戏清单、刷新、缓存和切换到 Mojang 官方源。Java 流程用独立测试实例验证了直接启动、取消下载和下载 Java 21 后自动启动。
- 已实际下载并替换两个 Sodium 版本，验证了实例文件夹预览、JAR 图标和 `mods-backup` 备份。
- 空间分类清理、校验索引保留、错误续传响应和暂停后的任务恢复通过离线回归验证。真实 WebView2 界面使用隔离夹具验证了清理预览/取消/确认、小窗口布局、重启后继续安装、整合包暂存恢复及移除记录保留实例；该夹具验证安装流程，不用于启动游戏。
- 早期实测 Minecraft 26.2 的原版、Fabric 0.20.1 和 NeoForge 26.2.0.93，启动日志显示已进入渲染阶段。
- 尚待完整验证的场景包括正版账号登入后启动、真实整合包安装、Windows 10 和需要单独安装 WebView2 的机器。

## 早期安装体积测量

2026-11-15 在 Windows 11 24H2、预装 WebView2 157.0.4341.46 的环境中，安装器为 4,105,220 B。干净安装后的程序文件为 5,589,601 B，首次打开至首页就绪后配置和缓存为 7,985,064 B，合计约 13.57 MB。结果按文件长度统计，包含卸载器。共享 WebView2 运行时、Minecraft、Java 和玩家数据另计。
