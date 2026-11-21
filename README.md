# EBuild 启动器

EBuild 是一个采用 MIT 许可证的 Minecraft Java 版桌面启动器。主线使用 C#、WPF 和 .NET Framework 4.8，面向 Windows 10／11 x64。

[发布页](https://github.com/eee22323232/EBuild/releases)中的 `-WPF-Windows-x64-Setup.exe` 是安装版，`-WPF-Windows-x64.zip` 是便携版。发布包可用同页的 `WPF-SHA256SUMS.txt` 校验。也可以在 Windows 上从源码运行。

## 当前功能

- 安装 Minecraft 原版、Fabric、NeoForge、Forge 和 Quilt。Forge 安装器支持 Minecraft 1.13 及以后版本。同一版本可以创建多个独立实例，共用游戏本体和 Java。
- 游戏本体优先使用 BMCLAPI 国内镜像，失败时回退到 Mojang 官方源。可在「设置 → 网络」切换渠道，下载页会显示版本列表的实际来源。
- 在 Modrinth 查找模组、整合包、光影和材质。安装资源时可以预览兼容实例中的目标文件夹和已有文件，也可以选择其他下载位置。安装前会列出必需依赖。
- 模组列表和文件夹预览支持读取 JAR 中的图标。安装到实例时，识别到同一 Modrinth 项目的其他版本会提示替换，并将确认替换的文件备份到实例的 `mods-backup`。
- 在「启动游戏」时检查实例需要的 Java。找不到兼容版本时可以选择「下载并启动」。
- 支持离线档案和微软账号设备码登录。账号页可以预览角色，并为离线档案导入皮肤和披风。
- 实例默认继承全局游戏设置，可单独覆盖 Java、内存、分辨率和启动参数。设置还提供主题、代理、下载并发和数据目录管理。
- 底部「联机」支持分享已经对局域网开放的单人世界。好友可以粘贴邀请码加入，再用匹配实例启动或把本地地址复制到其他启动器启动的 Minecraft。
- 「设置 → 启动器设置 → 空间管理」显示游戏、Java、实例、安装任务和缓存占用。按项目预览并确认清理安装包缓存、未完成下载或模组旧版备份，保留文件校验记录。外部游戏目录不计入清理范围。
- 游戏与整合包安装支持暂停和重启后手动继续，可在「下载任务」中移除未完成任务。下载文件有校验信息且服务器支持 HTTP Range 时可续传，否则重新下载当前文件；完成后仍校验完整性。
- 「设置 → 启动器设置」可以检查 WPF 更新和切换启动检查。确认后从 GitHub 下载并校验，空闲时可重启安装。更新未能打开主界面时会恢复上一版。

整合包支持从 Modrinth 安装或导入本地 `.mrpack`，也支持根目录包含 `modrinth.index.json` 的 ZIP。安装后会创建独立实例。CurseForge 清单包和双源搜索仍待接入。

实例页的「导入」也支持添加已有游戏文件夹。文件夹和受支持的整合包均可拖进窗口预览，确认后再添加或安装。EBuild 使用原目录里的存档、模组和设置，按识别出的 Minecraft 和加载器版本准备启动所需的游戏文件。外部目录可以重新定位，移除记录会保留原文件。只有可识别的独立实例才可以永久删除原目录。

## 从源码运行

在 Windows x64 上，从仓库根目录运行：

```powershell
dotnet run --project .\scripts\wpf\EBuild.Wpf.csproj
```

需要 .NET SDK 和 .NET Framework 4.8。要打包 Windows 安装器和便携 ZIP，参见[贡献指南](CONTRIBUTING.md)。

默认游戏数据目录为 `%LOCALAPPDATA%\EBuild-WebView2-CSharp\游戏数据`。可在「设置 → 启动器设置」更换目录或导入旧实例副本。实例日志默认写入实例目录下的 `logs/ebuild-launch.log`。

### 好友联机

1. 房主在 EBuild 启动实例，进入单人世界后选择「对局域网开放」。回到「联机」页选该实例，创建并复制邀请码。识别不到端口时可手动填写。
2. 好友在 EBuild 粘贴邀请码。连接建立后，兼容的实例可「启动并加入」，也可复制地址到 Minecraft 的「多人游戏 → 直接连接」。
3. 房主退出世界或关闭房间后，邀请码失效。更换邀请码会断开旧连接。联机遵循游戏原有的账号验证规则。

默认公共节点支持分组短码。房主可分享模组、任务书、脚本、资源包和选定配置，好友可以预览后下载到新实例。

首次使用时，EBuild 下载并校验 [EasyTier 联机组件](https://github.com/EasyTier/EasyTier/releases/tag/v2.6.4)。自动模式尝试 [DreamLife](https://easytier.dreamlife.indevs.in/) 和 EasyTier 文档中的共享节点。使用自建或其他获准使用的节点时，双方在「联机设置」填入相同地址。EasyTier 代码使用 [LGPL-3.0](https://github.com/EasyTier/EasyTier/blob/v2.6.4/LICENSE)。

- [开发版运行、导入和验证记录](scripts/webview2-trial/README.md)
- [Visual Studio 调试与贡献指南](CONTRIBUTING.md)

## 历史版本

[`dev.0.1.2`](https://github.com/eee22323232/EBuild/releases/tag/dev.0.1.2) 为 WebView2 版，[`dev.0.1.0`](https://github.com/eee22323232/EBuild/releases/tag/dev.0.1.0) 为 Electron 版。已使用 C# 版的玩家可以直接用 WPF 打开现有实例。

[MIT 许可证](LICENSE)
