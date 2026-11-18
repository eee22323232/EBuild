using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EBuild.Native
{
    public partial class Launcher
    {
        StackPanel taskRows;
        StackPanel installTaskRows;
        string installTrackedId = "";
        readonly List<Action> progressBindings = new List<Action>();

        async Task ShowDownloads(int tab = 0, string game = "", string loader = "", string type = "", ResourceSearch existing = null) {
            var page = ShowPage("downloads", 1, "下载");
            var tabs = new TabControl();
            var minecraft = Stack();
            var modrinth = existing ?? new ResourceSearch(this, game, loader, type);
            if (modrinth.Root.Parent is Border previous) previous.Child = null;
            var gameFilter = Input();
            var release = Picker(new[] { Option("release", "正式版"), Option("snapshot", "快照与预发布") });
            var rows = Stack();
            var status = Note("");
            int limit = 35;
            Button more = null;
            more = Button("加载更多", () => { limit += 35; Render(); return Task.CompletedTask; });
            void Render() {
                rows.Children.Clear();
                var matches = catalog.A("versions").Where(item => item.S("type") == Chosen(release) &&
                    item.S("id").IndexOf(gameFilter.Text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
                var source = catalog.S("source") == "bmclapi" ? "BMCLAPI 国内镜像" : "Mojang 官方源";
                status.Text = catalog == null ? "正在获取 Minecraft 版本…" :
                    source + (catalog.B("cached") ? " · 缓存" : "") + " · " + matches.Length + " 个版本";
                foreach (var item in matches.Take(limit))
                    rows.Children.Add(Row(LoaderIcon("vanilla", 28), item.S("id"), DateLabel(item.S("releaseTime")),
                        Button("安装", () => ShowInstall(item.S("id")))));
                more.Visibility = matches.Length > limit ? Visibility.Visible : Visibility.Collapsed;
            }
            async Task Load(bool force) {
                status.Text = "正在获取 Minecraft 版本…";
                try { if (catalog == null || force) catalog = await Call("catalog", force); Render(); }
                catch (Exception error) { status.Text = error.Message; }
            }
            gameFilter.TextChanged += (_, __) => { limit = 35; Render(); };
            release.SelectionChanged += (_, __) => { limit = 35; Render(); };
            minecraft.Children.Add(Columns(Field("搜索 Minecraft 版本", gameFilter), Field("版本类型", release)));
            minecraft.Children.Add(Actions(Button("刷新", () => Load(true))));
            minecraft.Children.Add(status);
            minecraft.Children.Add(rows);
            minecraft.Children.Add(more);
            tabs.Items.Add(Tab("游戏本体", Card(minecraft)));
            tabs.Items.Add(Tab("Modrinth 资源", Card(modrinth.Root)));
            tabs.Items.Add(Tab("CurseForge", Card(Stack(Heading("CurseForge 待接入"), Note("取得 API 访问权限后启用。")))));
            tabs.SelectedIndex = tab;
            page.Children.Add(tabs);
            tabs.SelectionChanged += async (_, e) => {
                if (e.Source != tabs) return;
                if (tabs.SelectedIndex == 0) await Guard(() => Load(false));
                if (tabs.SelectedIndex == 1) await Guard(modrinth.EnsureLoaded);
            };
            if (tab == 0) await Load(false);
            else if (tab == 1) await modrinth.EnsureLoaded();
        }

        static string DateLabel(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? date.ToLocalTime().ToString("yyyy-MM-dd") : "";

        async Task ShowInstall(string preset) {
            var previous = route;
            var page = ShowPage("install", 1, "安装 Minecraft", async () => {
                if (previous == "home") ShowHome(); else await ShowDownloads();
            });
            var versions = Picker(Array.Empty<Choice>(), editable: true);
            var snapshots = Check("显示快照版");
            var loaders = Picker(LoaderOptions.Where(item => item.Key != "minecraft"));
            var loaderVersions = Picker(Array.Empty<Choice>());
            var loaderField = Field("加载器版本", loaderVersions);
            var name = Input("", 80);
            var status = Note("");
            string suggested = "";
            int request = 0;
            int catalogRequest = 0;
            bool filling = false;
            Button install = null;
            async Task VersionsChanged() {
                if (filling) return;
                int revision = ++request;
                string game = Chosen(versions);
                string loader = Chosen(loaders);
                bool plain = loader == "vanilla";
                loaderField.Visibility = plain ? Visibility.Collapsed : Visibility.Visible;
                SetOptions(loaderVersions, Array.Empty<Choice>(), "");
                install.IsEnabled = game != "" && plain;
                if (game == "") return;
                var next = game + (plain ? " 原版" : " " + Loader(loader));
                if (name.Text == "" || name.Text == suggested) name.Text = next;
                suggested = next;
                status.Text = plain ? "" : "正在查询加载器版本…";
                if (plain) return;
                try {
                    var available = (await Call("loaders", game, loader)).A();
                    if (revision != request) return;
                    SetOptions(loaderVersions, available.Select(item => Option(item.S("id"),
                        item.S("id") + (item.B("stable") ? "" : " · 预发布"))), available.FirstOrDefault(item => item.B("stable")).S("id"));
                    status.Text = available.Length == 0 ? "这个游戏版本暂无可用的 " + Loader(loader) + "。" : "";
                    install.IsEnabled = available.Length != 0;
                } catch (Exception error) { if (revision == request) status.Text = error.Message; }
            }
            async Task Load(string preferred, bool refresh = false) {
                int revision = ++catalogRequest;
                try {
                    if (catalog == null || refresh) catalog = await Call("catalog", refresh);
                    if (revision != catalogRequest) return;
                    filling = true;
                    var all = catalog.A("versions");
                    var list = all.Where(item => item.S("type") == "release" || snapshots.IsChecked == true);
                    SetOptions(versions, list.Select(item => Option(item.S("id"), item.S("id"))),
                        preferred == "" ? catalog.At("latest").S("release") : preferred);
                    filling = false;
                    status.Text = "";
                    await VersionsChanged();
                } catch (Exception error) { if (revision == catalogRequest) status.Text = error.Message; }
            }
            install = Button("安装实例", async () => {
                var game = Chosen(versions);
                var loader = Chosen(loaders);
                if (game == "" || versions.Text != game) throw new IOException("请从列表选择 Minecraft 版本。");
                var id = Guid.NewGuid().ToString();
                install.IsEnabled = false;
                try {
                    await TrackInstallation(() => RunJob("instances:create", J.Map("name", name.Text.Trim(), "version", game,
                        "loader", loader, "loaderVersion", loader == "vanilla" ? "" : Chosen(loaderVersions)), id), id);
                } finally {
                    install.IsEnabled = Chosen(versions) != "" && (Chosen(loaders) == "vanilla" || Chosen(loaderVersions) != "");
                }
            }, true);
            install.IsEnabled = false;
            var loaderDisplay = new Image { Width = 32, Height = 32, Margin = new Thickness(7, 2, 0, 0) };
            loaderDisplay.Source = LoaderIcon("vanilla", 32).Source;
            var loaderRow = new DockPanel();
            DockPanel.SetDock(loaderDisplay, Dock.Right);
            loaderRow.Children.Add(loaderDisplay);
            loaderRow.Children.Add(loaders);
            var loaderLabel = Heading("加载器", 13);
            page.Children.Add(Card(Stack(Columns(Field("Minecraft 版本", versions), Stack(snapshots)),
                loaderLabel, loaderRow, Columns(loaderField, Field("实例名称", name)))));
            page.Children.Add(status);
            page.Children.Add(Actions(install, Button("重新加载", () => Load(Chosen(versions), true)), Button("下载任务", () => ShowDownloadTasks())));
            installTaskRows = Stack();
            page.Children.Add(installTaskRows);
            installTrackedId = "";
            var snapshotPreset = catalog.A("versions").FirstOrDefault(item => item.S("id") == preset).S("type") == "snapshot" ||
                System.Text.RegularExpressions.Regex.IsMatch(preset, "[A-Za-z-]");
            snapshots.IsChecked = snapshotPreset;
            snapshots.Checked += async (_, __) => await Guard(() => Load(Chosen(versions)));
            snapshots.Unchecked += async (_, __) => await Guard(() => Load(Chosen(versions)));
            versions.SelectionChanged += async (_, __) => await Guard(VersionsChanged);
            loaders.SelectionChanged += async (_, __) => {
                loaderDisplay.Source = LoaderIcon(Chosen(loaders), 32).Source;
                await Guard(VersionsChanged);
            };
            await Load(preset);
        }

        Task ShowDownloadTasks() {
            var page = ShowPage("tasks", 0, "下载任务", () => { ShowHome(); return Task.CompletedTask; });
            taskRows = Stack();
            page.Children.Add(taskRows);
            RenderDownloads();
            return RefreshDownloads();
        }

        async Task RefreshDownloads() {
            downloads = (await Call("downloads:list")).A();
            RenderDownloads();
        }

        async Task TrackInstallation(Func<Task<object>> start, string id = "") {
            if (id != "") installTrackedId = id;
            try {
                var installed = await start();
                if (installed.S("id") == "") return;
                await RefreshInstances();
                completedInstance = installed.S("id");
                ViewInstalled.Visibility = Visibility.Visible;
                if (route == "home") ShowHome();
                Say("安装完成 · " + installed.S("name"));
            } finally { await RefreshDownloads(); }
        }

        void UpdateDownloadProgress() {
            foreach (var update in progressBindings) update();
        }

        void RenderDownloads() {
            progressBindings.Clear();
            if (route == "home" && downloadsButton != null) {
                int active = downloads.Count(item => item.S("state") != "complete");
                downloadsButton.Content = "下载任务" + (active == 0 ? "" : " · " + active);
            }
            if (route == "tasks" && taskRows != null) {
                taskRows.Children.Clear();
                if (downloads.Length == 0) taskRows.Children.Add(Card(Note("暂无下载任务")));
                foreach (var task in downloads) taskRows.Children.Add(DownloadCard(task));
            }
            if (route == "install" && installTaskRows != null) {
                installTaskRows.Children.Clear();
                var task = downloads.FirstOrDefault(item => item.S("id") == installTrackedId);
                if (task != null) installTaskRows.Children.Add(DownloadCard(task));
            }
        }

        FrameworkElement DownloadCard(object task) {
            string id = task.S("id");
            string state = task.S("state");
            var input = task.At("input");
            var tools = Actions();
            string badge;
            switch (state) {
                case "active": badge = "↓ 下载中"; tools.Children.Add(Button("暂停安装", () => Call("task:cancel"))); break;
                case "failed": badge = "！失败"; break;
                case "complete":
                    badge = "✓ 完成";
                    if (task.At("instance") != null) tools.Children.Add(Button("查看实例", () => ViewInstance(id)));
                    break;
                default: badge = "Ⅱ 已暂停"; break;
            }
            if (state == "failed" || state == "paused") tools.Children.Add(Button("继续安装",
                () => TrackInstallation(() => RunJob("downloads:resume", id), id), true));
            if (state != "active") tools.Children.Add(Button("移除记录", async () => {
                if (!Confirm("移除下载任务", state == "complete" ? "只移除下载记录，已安装的实例会保留。" :
                    "将移除此任务及其暂存进度。是否继续？")) return;
                await RunJob("downloads:remove", id);
                await RefreshDownloads();
            }));
            var subtitle = InstanceVersion(input) + (input.S("loaderVersion") == "" ? "" : " " + input.S("loaderVersion"));
            var details = Stack(Row(LoaderIcon(input.S("loader"), 33), input.S("name") + " · " + badge, subtitle));
            var message = Note(task.S("message"));
            details.Children.Add(message);
            if (state == "active") {
                var bar = new ProgressBar { Height = 5 };
                details.Children.Add(bar);
                void Update() {
                    bool installing = jobActive && jobProgress.S("kind") == "game-install";
                    message.Text = installing ? ProgressLabel(jobProgress) : task.S("message");
                    int total = installing ? jobProgress.I("total") : 0;
                    bar.Maximum = Math.Max(1, total);
                    bar.Value = installing ? jobProgress.I("done") : 0;
                    bar.IsIndeterminate = total == 0;
                }
                progressBindings.Add(Update);
                Update();
            }
            details.Children.Add(tools);
            return Card(details);
        }
    }
}
