using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace EBuild.Native
{
    public partial class Launcher
    {
        readonly CancellationTokenSource updateLifetime = new CancellationTokenSource();
        string[] updateStartupArgs = Array.Empty<string>();
        string stagedUpdate;
        bool checkingUpdate;
        bool installingUpdate;

        async Task InitializeUpdates() {
            var args = updateStartupArgs;
            if (args.Length == 2 && args[0] == "--launcher-update-probe") {
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                LauncherUpdate.MarkReady(profile, args[1]);
                Say("已更新到 " + LauncherUpdate.CurrentTag);
                return;
            }
            if (args.SequenceEqual(new[] { "--launcher-update-restored" })) {
                var result = LauncherUpdate.LastResult(profile);
                Say("更新失败，已恢复 " + LauncherUpdate.CurrentTag + "。可在设置里手动重试。");
                Status.ToolTip = result.S("message");
                return;
            }
            try { stagedUpdate = LauncherUpdate.Staged(profile); }
            catch (Exception error) { Say("无法读取已暂存更新 · " + error.GetBaseException().Message); }
            if (stagedUpdate != null) {
                ApplyUpdate.Visibility = Visibility.Visible;
                Say("更新已下载，空闲时可重启并安装。");
            } else if (settings.B("checkUpdatesOnStart")) {
                _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () => await CheckUpdate(false)));
            }
        }

        FrameworkElement UpdateSettings() {
            var automatic = Check("启动时检查更新", settings.B("checkUpdatesOnStart"));
            bool resetting = false;
            RoutedEventHandler save = async (_, __) => {
                if (resetting) return;
                automatic.IsEnabled = false;
                try {
                    settings = J.Merge(settings, await Call("settings:save", J.Map("checkUpdatesOnStart", automatic.IsChecked == true)));
                } catch (Exception error) {
                    resetting = true;
                    automatic.IsChecked = settings.B("checkUpdatesOnStart");
                    resetting = false;
                    Say(error.GetBaseException().Message);
                } finally { automatic.IsEnabled = true; }
            };
            automatic.Checked += save;
            automatic.Unchecked += save;
            var version = Stack(Heading("启动器更新", 15), Note("当前版本 " + LauncherUpdate.CurrentTag), Note(LauncherUpdate.BuildSource));
            return Stack(WithActions(version, Button("检查更新", () => CheckUpdate(true))), automatic);
        }

        async Task CheckUpdate(bool manual) {
            if (checkingUpdate || leaving || updateLifetime.IsCancellationRequested) return;
            if (stagedUpdate != null) { if (manual) await InstallUpdate(); return; }
            checkingUpdate = true;
            bool accepted = false;
            try {
                if (manual) Say("正在检查 EBuild 更新…");
                var previous = LauncherUpdate.LastResult(profile);
                using (var network = new Network(settings, J.Inside(profile, "launcher-updates/transfers"))) {
                    var release = await LauncherUpdate.Check(network, manual ? "" : previous.S("failedTag"), updateLifetime.Token);
                    if (release == null) { if (manual) Say("当前已是最新的 WPF 版本"); return; }
                    if (leaving || updateLifetime.IsCancellationRequested || (!manual && ActiveDialog != this)) return;
                    var version = release.S("tag");
                    var notes = release.S("notes");
                    var dialog = new TaskDialog(this, "发现 EBuild 更新", Stack(Heading(version),
                        Note("当前版本 " + LauncherUpdate.CurrentTag), Note(notes == "" ? "发布页提供此版本的详细信息。" : notes)));
                    dialog.Add("下次提醒", () => { dialog.Close(); return Task.CompletedTask; });
                    dialog.Add("立即更新", () => { dialog.Accept(); return Task.CompletedTask; }, true);
                    if (await dialog.Open() != true) return;
                    accepted = true;
                    Say("正在下载并校验 EBuild " + version + "…");
                    stagedUpdate = await LauncherUpdate.Stage(profile, network, release, updateLifetime.Token);
                    ApplyUpdate.Visibility = Visibility.Visible;
                    Say("更新已下载，空闲时可重启并安装。");
                    if (UpdateIdle()) await InstallUpdate();
                }
            } catch (OperationCanceledException) { }
            catch (Exception error) {
                if (manual || accepted) Say("更新失败 · " + error.GetBaseException().Message);
            } finally { checkingUpdate = false; }
        }

        bool UpdateIdle() => !leaving && !backend.HasGameWork && !backend.HasLanWork && !backend.HasActiveTask && !jobActive && mutations == 0;

        async void ApplyUpdateClick(object sender, RoutedEventArgs e) => await Guard(InstallUpdate);

        async Task InstallUpdate() {
            if (stagedUpdate == null || installingUpdate) return;
            if (!UpdateIdle()) { Say("等游戏、联机和下载任务结束后，再重启并安装。"); return; }
            installingUpdate = true;
            try {
                var dialog = new TaskDialog(this, "安装 EBuild 更新", Stack(Note("EBuild 会关闭并在安装后重新打开。")));
                if (await dialog.Result("重启并安装") != true) return;
                if (!UpdateIdle() || !await CanLeavePage()) return;
                LauncherUpdate.Start(profile, stagedUpdate);
                Close();
            } finally { installingUpdate = false; }
        }
    }
}
