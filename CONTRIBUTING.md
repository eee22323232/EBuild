# 参与 EBuild C# 开发

`main` 是 EBuild 的开发主线。Windows x64 版本使用 WPF 和 .NET Framework 4.8。

## 在 Visual Studio 里运行

1. 在 Visual Studio Installer 中安装「使用 .NET 的桌面开发」工作负载和 .NET Framework 4.8 目标包。
2. 打开仓库根目录的 [EBuild.sln](EBuild.sln)。等待 NuGet 还原，将 `EBuild.Wpf` 设为启动项目。
3. 选用 Debug，按 F5 启动。这个配置默认使用 %LOCALAPPDATA%\EBuild-WebView2-CSharp-Debug 存放配置、缓存和游戏数据，首次需要下载的游戏文件会放在该目录下。

也可以在装有 .NET SDK 的 PowerShell 中，从仓库根目录运行以下命令。

~~~powershell
dotnet build .\EBuild.sln
dotnet run --project .\scripts\wpf\EBuild.Wpf.csproj
~~~

## 找到要修改的代码

- [scripts/wpf](scripts/wpf/) 保存主线界面。窗口样式在 [Launcher.xaml](scripts/wpf/Launcher.xaml)，各页面按功能分文件。
- [Backend.cs](scripts/webview2-trial/Backend.cs) 分发界面调用。WPF 和历史 WebView2 版共用此目录的游戏后端。
- [Minecraft.cs](scripts/webview2-trial/Minecraft.cs) 管版本、加载器和启动。[Resources.cs](scripts/webview2-trial/Resources.cs) 管 Modrinth 搜索、安装和版本替换。[ModIcons.cs](scripts/webview2-trial/ModIcons.cs) 从 JAR 读取模组图标。[Network.cs](scripts/webview2-trial/Network.cs) 管下载和文件校验。[Accounts.cs](scripts/webview2-trial/Accounts.cs) 管微软登录。
- [Storage.cs](scripts/webview2-trial/Storage.cs) 统计数据目录并按预览清理；[Downloads.cs](scripts/webview2-trial/Downloads.cs) 保存游戏和整合包安装任务。文件续传数据放在 `cache/transfers`，任务和安装暂存放在 `downloads`，恢复任务应复用原实例 ID。

## 提交前验证

WPF 版使用 Windows 自带的 .NET Framework 4.8，读取现有 C# 版的实例和配置。Debug 版使用独立的调试配置目录，也可设置 `EBUILD_CSHARP_PROFILE` 指定另一个目录。

~~~powershell
dotnet run --project .\scripts\webview2-trial\verify\EBuild.Verification.csproj
~~~

这条命令在临时目录验证版本清单、文件校验、玩家数据复制、外部实例、分类清理和下载恢复。续传测试通过模拟 HTTP 响应覆盖 Range 支持、忽略或错误响应、416 和校验失败；任务测试覆盖暂停后重新载入和实例提交后的恢复。不需要 Minecraft、网络请求或微软账号。首次构建需要恢复 NuGet 依赖。

改了界面时，再用 Debug 配置打开对应页面。要给手动测试指定另一个空目录，可以用环境变量 EBUILD_CSHARP_PROFILE，例如以下 PowerShell 命令。

~~~powershell
$env:EBUILD_CSHARP_PROFILE = Join-Path $env:TEMP ("EBuild-Test-" + [guid]::NewGuid().ToString("N"))
dotnet run --project .\scripts\wpf\EBuild.Wpf.csproj
Remove-Item Env:EBUILD_CSHARP_PROFILE
~~~

提交修改时，请说明改了哪个流程、怎样验证。

## 打包和发布 WPF

用 Inno Setup 6 和 .NET SDK 构建安装器、便携 ZIP 与 SHA-256 清单。

~~~powershell
.\scripts\wpf\package.ps1 -Output .\dist\wpf -Iscc 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
~~~

发布前提高 [项目文件](scripts/wpf/EBuild.Wpf.csproj)中的 `WpfReleaseVersion`。在 GitHub Releases 使用对应的 `dev.X.Y.Z` 标签，并附上 `assets` 目录下的安装器、ZIP 和 `WPF-SHA256SUMS.txt`。点击应用内更新时，两种安装方式都使用同一个 ZIP 替换启动器文件。Windows 安装记录中的版本号由安装器写入，应用内显示的是当前程序版本。
