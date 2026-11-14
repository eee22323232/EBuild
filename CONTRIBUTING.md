# 参与 EBuild C# 开发

本分支是 EBuild 的 Windows x64 版本，窗口使用 WinForms 和 WebView2，程序运行在 .NET Framework 4.8 上。

## 在 Visual Studio 里运行

1. 在 Visual Studio Installer 中安装「使用 .NET 的桌面开发」工作负载和 .NET Framework 4.8 目标包。运行程序的电脑需要 WebView2 Evergreen 运行时。
2. 打开仓库根目录的 [EBuild.sln](EBuild.sln)。等待 NuGet 还原。如果 Visual Studio 选中了验证项目，右键单击 EBuild，选择「设为启动项目」。
3. 选用 Debug，按 F5 启动。这个配置默认使用 %LOCALAPPDATA%\EBuild-WebView2-CSharp-Debug 存放配置、缓存和游戏数据，首次需要下载的游戏文件会放在该目录下。

也可以在装有 .NET SDK 的 PowerShell 中，从仓库根目录运行以下命令。

~~~powershell
dotnet build .\EBuild.sln
dotnet run --project .\scripts\webview2-trial\EBuild.csproj
~~~

## 找到要修改的代码

- [ui](scripts/webview2-trial/ui/) 保存 HTML、CSS 和 JavaScript。改布局、颜色、按钮或资源详情页，通常从这里改。
- [Host.cs](scripts/webview2-trial/Host.cs) 管窗口和 WebView2。[channels.json](scripts/webview2-trial/channels.json) 定义界面调用，后端在 [Backend.cs](scripts/webview2-trial/Backend.cs) 中分发。
- [Minecraft.cs](scripts/webview2-trial/Minecraft.cs) 管版本、加载器和启动。[Resources.cs](scripts/webview2-trial/Resources.cs) 管 Modrinth。[Network.cs](scripts/webview2-trial/Network.cs) 管下载和文件校验。[Accounts.cs](scripts/webview2-trial/Accounts.cs) 管微软登录。

## 提交前验证

~~~powershell
dotnet run --project .\scripts\webview2-trial\verify\EBuild.Verification.csproj
~~~

这条命令在临时目录验证版本清单筛选、下载文件校验和旧实例玩家数据的复制。不需要 Minecraft、网络请求或微软账号。首次构建需要恢复 NuGet 依赖。

改了界面时，再用 Debug 配置打开对应页面。要给手动测试指定另一个空目录，可以用环境变量 EBUILD_CSHARP_PROFILE，例如以下 PowerShell 命令。

~~~powershell
$env:EBUILD_CSHARP_PROFILE = Join-Path $env:TEMP ("EBuild-Test-" + [guid]::NewGuid().ToString("N"))
dotnet run --project .\scripts\webview2-trial\EBuild.csproj
Remove-Item Env:EBUILD_CSHARP_PROFILE
~~~

提交修改时，请说明改了哪个流程、怎样验证。原有免 SDK 的 PowerShell 构建见 [开发版说明](scripts/webview2-trial/README.md)。
