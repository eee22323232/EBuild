using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace EBuild.Native
{
    public partial class Launcher
    {
        StackPanel threadRows;
        readonly Dictionary<string, object> diagnostics = new Dictionary<string, object>();
        bool checkingDiagnostics;

        object Game(string id) => games.FirstOrDefault(item => item.S("id") == id);

        string GameLabel(object game) {
            switch (game.S("state")) {
                case "queued": return "排队中";
                case "preparing": return jobProgress.S("kind") == "game-launch" ? jobProgress.S("message") : "正在准备启动";
                case "stopping": return "正在停止";
                case "running":
                    diagnostics.TryGetValue(game.S("id"), out var info);
                    if (info.S("signal") == "ready") return "游戏已就绪";
                    if (info.S("signal") == "window-running") return "游戏运行中";
                    if (info.S("signal") == "language-timeout") return "汉化包下载超时";
                    long.TryParse(game.S("startedAt"), out long started);
                    long.TryParse(info.S("lastLogAt"), out long lastLog);
                    return game.B("logs") && LaunchDiagnostics.Time(DateTime.UtcNow) - Math.Max(started, lastLog) < 60000 ?
                        "游戏加载中" : "Java 已启动";
                default: return "启动游戏";
            }
        }

        void UpdateGameControls() {
            foreach (var stale in diagnostics.Keys.Where(id => Game(id) == null).ToArray()) diagnostics.Remove(stale);
            if (ShowingGameCard && threadsButton != null) {
                var game = Game(selected);
                threadsButton.Visibility = games.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                threadsButton.Content = "游戏线程 · " + games.Length;
                if (launchButton != null) {
                    if (route == "instances") launchButton.Content = "▶  启动游戏";
                    launchButton.Visibility = game == null ? Visibility.Visible : Visibility.Collapsed;
                    launchButton.IsEnabled = Active != null && !Active.B("externalMissing") && game == null && !leaving;
                    launchStateBadge.Visibility = game == null ? Visibility.Collapsed : Visibility.Visible;
                    launchState.Text = game == null ? "" : GameLabel(game);
                    launchState.ToolTip = launchState.Text;
                    stopButton.Visibility = game.S("state") == "running" ? Visibility.Visible : Visibility.Collapsed;
                    currentThread.Visibility = game == null ? Visibility.Collapsed : Visibility.Visible;
                }
            }
            if (threadRows != null) RenderThreads();
        }

        async Task PollDiagnostics() {
            if (checkingDiagnostics || initializing || leaving) return;
            var pending = games.Where(game => game.S("state") == "running" && game.B("logs") &&
                (!diagnostics.TryGetValue(game.S("id"), out var info) ||
                 (info.S("signal") != "ready" && info.S("signal") != "window-running"))).ToArray();
            if (pending.Length == 0) return;
            checkingDiagnostics = true;
            try {
                foreach (var game in pending) {
                    var id = game.S("id");
                    var details = await Call("game:diagnose", id);
                    if (Game(id).S("startedAt") == game.S("startedAt")) diagnostics[id] = details;
                }
                UpdateGameControls();
            } finally { checkingDiagnostics = false; }
        }

        Task StartGame(string id, int approved = 0, string room = "") {
            if (FindInstance(id) == null) throw new IOException("实例已移除。");
            return Call("game:start", id, approved, room);
        }

        async Task AskForLaunchJava(object update) {
            var id = update.S("launchJava");
            var version = update.I("major");
            if (FindInstance(id) != null && Confirm("需要 Java", "该实例需要 Java " + version + "。下载完成后将继续启动。"))
                await StartGame(id, version, update.S("room"));
        }

        async Task StopGame(string id) {
            var result = await Call("game:stop", id);
            var identity = result.S("forceIdentity");
            if (identity != "" && Confirm("强制结束游戏", "游戏仍未退出。强制结束可能丢失尚未保存的进度。是否继续？"))
                await Call("game:stop", id, identity);
        }

        async Task ShowThreads() {
            if (threadRows != null) return;
            threadRows = Stack();
            RenderThreads();
            try { await new TaskDialog(this, "游戏线程", threadRows).Result("关闭"); }
            finally { threadRows = null; }
        }

        void RenderThreads() {
            threadRows.Children.Clear();
            if (games.Length == 0) threadRows.Children.Add(Note("暂无游戏线程"));
            foreach (var game in games) {
                var id = game.S("id");
                var item = FindInstance(id);
                diagnostics.TryGetValue(id, out var diagnostic);
                var phase = GameLabel(game);
                var details = diagnostic.S("summary");
                var controls = new List<UIElement>();
                if (game.S("state") == "running") controls.Add(Button("停止", () => StopGame(id)));
                if (game.S("state") == "queued") controls.Add(Button("取消排队", () => Call("game:cancel-queued", id)));
                if (game.S("state") == "preparing") controls.Add(Button("取消启动", () => Call("task:cancel")));
                if (game.B("logs")) {
                    controls.Add(Button("查看日志", () => Call("game:log", id)));
                    controls.Add(Button("复制诊断", () => Call("game:copy-diagnostic",
                        item.S("name") + "\n" + InstanceVersion(item) + "\n" + phase + "\n" + details)));
                }
                threadRows.Children.Add(Card(Stack(Heading(item.S("name")), Note(phase), Note(details), Actions(controls.ToArray()))));
            }
        }
    }
}
