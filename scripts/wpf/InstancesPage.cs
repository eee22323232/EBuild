using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace EBuild.Native
{
    public partial class Launcher
    {
        StackPanel homeCard;
        Button launchButton;
        Button stopButton;
        Button threadsButton;
        Button downloadsButton;
        TextBlock homeIdentity;
        Image homeAvatar;
        StackPanel threadRows;
        readonly Dictionary<string, object> diagnostics = new Dictionary<string, object>();
        bool checkingDiagnostics;

        void ShowHome() {
            var page = ShowPage("home", 0, "实例");
            threadsButton = Button("游戏线程", ShowThreads);
            downloadsButton = Button("下载任务", () => ShowDownloadTasks());
            var tools = Actions(Button("导入", ShowImport), threadsButton, downloadsButton);
            tools.HorizontalAlignment = HorizontalAlignment.Right;
            page.Children.Add(tools);
            homeCard = Stack();
            page.Children.Add(homeCard);
            if (instances.Length != 0) {
                page.Children.Add(Heading("已安装的实例"));
                var list = Stack();
                foreach (var instance in instances) {
                    var row = Row(LoaderIcon(instance.S("loader"), 27), instance.S("name"),
                        InstanceVersion(instance) + (instance.B("externalMissing") ? " · 路径丢失" : ""),
                        Button("切换", () => SelectInstance(instance.S("id"))));
                    var menu = new ContextMenu();
                    AddMenu(menu, "启动游戏", () => StartGame(instance.S("id")));
                    AddMenu(menu, "实例设置", () => { ShowInstanceSettings(instance); return Task.CompletedTask; });
                    if (instance.B("externalMissing")) AddMenu(menu, "重新定位", () => Relocate(instance));
                    AddMenu(menu, instance.S("externalPath") == "" ? "删除游戏" : "移除实例", () => DeleteInstance(instance, false));
                    if (instance.B("canDeleteOriginal")) AddMenu(menu, "删除原游戏", () => DeleteInstance(instance, true));
                    row.ContextMenu = menu;
                    list.Children.Add(row);
                }
                page.Children.Add(Card(list));
                page.Children.Add(Actions(Button("＋ 新建实例", () => ShowInstall(""))));
            }
            homeAvatar = new Image { Width = 25, Height = 25, Margin = new Thickness(0, 0, 8, 0) };
            RenderOptions.SetBitmapScalingMode(homeAvatar, BitmapScalingMode.NearestNeighbor);
            homeIdentity = Text("");
            var identity = Button("", () => Navigate(4));
            identity.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { homeAvatar, homeIdentity } };
            var accounts = new ContextMenu();
            AddMenu(accounts, "管理名称", () => ManageAppearance("name"));
            AddMenu(accounts, "管理皮肤", () => ManageAppearance("skin"));
            AddMenu(accounts, "管理披风", () => ManageAppearance("cape"));
            identity.ContextMenu = accounts;
            page.Children.Add(Actions(identity));
            UpdateAccountIdentity();
            UpdateHomeCard();
        }

        void AddMenu(ContextMenu menu, string title, Func<Task> action) {
            var entry = new MenuItem { Header = title };
            entry.Click += async (_, __) => await Guard(action);
            menu.Items.Add(entry);
        }

        static DockPanel Row(Image icon, string title, string subtitle, params UIElement[] buttons) {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
            if (icon != null) {
                icon.Margin = new Thickness(0, 0, 10, 0);
                DockPanel.SetDock(icon, Dock.Left);
                row.Children.Add(icon);
            }
            if (buttons.Length != 0) {
                var actions = Actions(buttons);
                DockPanel.SetDock(actions, Dock.Right);
                row.Children.Add(actions);
            }
            var label = Stack(Heading(title, 14), Note(subtitle));
            label.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(label);
            return row;
        }

        void UpdateHomeCard() {
            if (route != "home" || homeCard == null) return;
            homeCard.Children.Clear();
            launchButton = null;
            stopButton = null;
            var active = Active;
            if (active == null) {
                homeCard.Children.Add(Card(Row(LoaderIcon("vanilla", 52), "还没有游戏实例",
                    "安装 Minecraft，或拖入游戏文件夹、整合包。", Button("新建实例", () => ShowInstall(""), true))));
            } else {
                string id = active.S("id");
                launchButton = Button("启动游戏", () => StartGame(id), true);
                stopButton = Button("停止", () => StopGame(id));
                UseResource(stopButton, ForegroundProperty, "Danger");
                var actions = Stack(launchButton,
                    Button("实例设置", () => { ShowInstanceSettings(active); return Task.CompletedTask; }),
                    stopButton);
                if (active.B("externalMissing")) actions.Children.Add(Button("重新定位", () => Relocate(active)));
                var details = Stack(Note("当前实例"), Heading(active.S("name"), 21),
                    Note(InstanceVersion(active)), Note(active.B("externalMissing") ? "找不到游戏文件夹" : ""));
                var showThread = Button("查看详情", ShowThreads);
                showThread.Name = "CurrentThread";
                details.Children.Add(Actions(showThread));
                var row = new DockPanel();
                var icon = LoaderIcon(active.S("loader"), 57);
                icon.Margin = new Thickness(0, 0, 15, 0);
                DockPanel.SetDock(icon, Dock.Left);
                DockPanel.SetDock(actions, Dock.Right);
                row.Children.Add(icon);
                row.Children.Add(actions);
                row.Children.Add(details);
                homeCard.Children.Add(Card(row));
                currentThread = showThread;
            }
            UpdateGameControls();
            RenderDownloads();
        }

        Button currentThread;
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
            if (route == "home" && threadsButton != null) {
                var game = Game(selected);
                threadsButton.Visibility = games.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                threadsButton.Content = "游戏线程 · " + games.Length;
                if (launchButton != null) {
                    launchButton.Content = GameLabel(game);
                    launchButton.IsEnabled = Active != null && !Active.B("externalMissing") && game == null && !leaving;
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

        void ShowInstanceSettings(object instance) {
            var id = instance.S("id");
            var page = ShowPage("instance", 0, "实例设置", async () => { if (await CanLeavePage()) ShowHome(); });
            var tabs = new TabControl();
            var name = Input(instance.S("name"), 80);
            var origin = instance.S("externalPath");
            var basic = Stack(Field("实例名称", name), Note(InstanceVersion(instance) +
                (instance.S("loaderVersion") == "" ? "" : " · " + instance.S("loaderVersion"))));
            if (origin != "") basic.Children.Add(Note("原游戏目录 · " + origin));
            var tools = Actions(Button("打开实例文件夹", () => Call("instances:folder", id)));
            if (instance.B("externalMissing")) tools.Children.Add(Button("重新定位", () => Relocate(instance)));
            else tools.Children.Add(Button("完整校验", async () => {
                if (await RunJob("instances:verify", id) != null) Say("游戏文件已校验");
            }));
            tools.Children.Add(Button(origin == "" ? "删除游戏" : "移除实例", () => DeleteInstance(instance, false)));
            if (instance.B("canDeleteOriginal")) tools.Children.Add(Button("删除原游戏", () => DeleteInstance(instance, true)));
            basic.Children.Add(tools);
            var mods = Stack(Actions(Button("打开 mods 文件夹", () => Call("instances:mods-folder", id)),
                Button("去下载模组", async () => {
                    if (!await CanLeavePage()) return;
                    await ShowDownloads(1, instance.S("version"), instance.S("loader") == "vanilla" ? "" : instance.S("loader"), "mod");
                }, true)));
            var installed = Stack();
            mods.Children.Add(installed);
            var editor = new SettingsEditor(this, settings, instance.At("overrides") ?? J.Map());
            var java = Stack(editor.GameFields(), editor.AdvancedFields());
            tabs.Items.Add(Tab("基本信息", Card(basic)));
            tabs.Items.Add(Tab("模组", Card(mods)));
            tabs.Items.Add(Tab("Java 与高级设置", Card(java)));
            page.Children.Add(tabs);
            var revision = pageRevision;
            bool loadedMods = false;
            tabs.SelectionChanged += async (_, e) => {
                if (e.Source != tabs || tabs.SelectedIndex != 1 || loadedMods) return;
                loadedMods = true;
                installed.Children.Add(Note("正在读取模组…"));
                try {
                    var info = (await Call("instances:resources", id, "mods")).A().FirstOrDefault();
                    if (pageRevision != revision) return;
                    installed.Children.Clear();
                    installed.Children.Add(FileList(info));
                } catch (Exception error) { loadedMods = false; installed.Children.Clear(); installed.Children.Add(Note(error.Message)); }
            };
            Func<string> snapshot = () => J.Dump(new object[] { name.Text, editor.Read(false) });
            var saved = snapshot();
            leavePage = () => Task.FromResult(saved == snapshot() || Confirm("放弃实例设置？", "实例设置有未保存的修改。是否离开？"));
            var save = Button("保存实例设置", async () => {
                var values = editor.Read(true);
                if (await RunJob("instances:edit", id, J.Map("name", name.Text.Trim(), "overrides", values)) == null) return;
                saved = snapshot();
                await RefreshInstances();
                Say("实例设置已保存");
            }, true);
            tabs.SelectionChanged += (_, e) => {
                if (e.Source == tabs) save.Visibility = tabs.SelectedIndex == 1 ? Visibility.Collapsed : Visibility.Visible;
            };
            page.Children.Add(Actions(save));
        }

        async Task DeleteInstance(object instance, bool original) {
            var id = instance.S("id");
            var name = instance.S("name");
            var external = instance.S("externalPath") != "";
            var remove = external && !original;
            var typed = Input();
            var content = Stack(Note(remove ? "从 EBuild 移除此实例记录，原游戏文件夹会保留。" :
                original ? "将永久删除原游戏文件夹及其中的存档、模组和设置。" :
                "将永久删除此实例的存档、模组和设置。共用库及 Java 会保留。"), Heading(name));
            if (external) content.Children.Add(Note(instance.S("externalPath")));
            if (!remove) content.Children.Add(Field("输入实例名称以确认", typed));
            var dialog = new TaskDialog(this, remove ? "移除实例" : "永久删除游戏", content);
            if (!remove) dialog.Add("复制名称", () => Call("resources:copy-name", name));
            if (await dialog.Result(remove ? "移除实例" : "永久删除", async () => {
                if (!remove && typed.Text != name) throw new IOException("输入的名称与实例名称不一致。");
                return await RunJob("instances:delete", id, remove ? name : typed.Text, remove ? "remove" : original ? "original" : "") != null;
            }, remove ? null : typed, name) == true) {
                await RefreshInstances();
                ShowHome();
                Say(remove ? "实例已移除" : "游戏已删除");
            }
        }

        async Task Relocate(object instance) {
            if (await RunJob("instances:relocate", instance.S("id")) == null) return;
            await RefreshInstances();
            if (route == "instance") ShowInstanceSettings(FindInstance(instance.S("id")));
            else ShowHome();
            Say("实例目录已更新");
        }

        async Task ShowImport() {
            var menu = new TaskDialog(this, "导入实例", Stack(Note("选择游戏文件夹或 Modrinth .mrpack 整合包。")));
            menu.Add("导入游戏文件夹", async () => {
                string dir = backend.Folder("选择 Minecraft 游戏文件夹", owner: menu);
                if (dir != "") { menu.Accept(); await PreviewImport(dir); }
            });
            menu.Add("导入整合包", async () => {
                var chooser = new OpenFileDialog { Title = "导入 Modrinth 整合包", Filter = "Modrinth 整合包|*.mrpack" };
                if (chooser.ShowDialog(menu) == true) { menu.Accept(); await PreviewImport(chooser.FileName); }
            });
            await menu.Open();
        }

        void DragFiles(object sender, DragEventArgs e) {
            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            bool accept = !initializing && !leaving && paths != null && paths.Length == 1;
            DropHint.Visibility = accept ? Visibility.Visible : Visibility.Collapsed;
            e.Effects = accept ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        async Task DropFiles(DragEventArgs e) {
            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            DropHint.Visibility = Visibility.Collapsed;
            if (!initializing && paths != null && paths.Length == 1 && await CanLeavePage()) await PreviewImport(paths[0]);
        }

        async Task PreviewImport(string path) {
            var preview = await backend.PreviewDroppedImport(path);
            if (preview.S("kind") == "pack") {
                var spec = preview.At("instance");
                if (await new TaskDialog(this, "确认导入整合包", Stack(Heading(spec.S("name")), Note(InstanceVersion(spec)), Note(path))).Result("开始安装") != true) return;
                await TrackInstallation(() => RunJob("resources:import", path));
                return;
            }
            var choices = Picker(preview.A("choices").Select(item => Option(item.S("externalProfile"), InstanceLabel(item))));
            if (await new TaskDialog(this, "确认导入游戏文件夹", Stack(Note(path), Field("选择游戏版本", choices),
                Note("EBuild 会引用此文件夹中的游戏数据。"))).Result("导入") != true) return;
            var result = await RunJob("instances:external", preview.S("source"), Chosen(choices));
            if (result.S("id") != "") { await RefreshInstances(); ShowHome(); Say("实例已导入"); }
        }

        FrameworkElement FileList(object info) {
            var files = info.A("files");
            var list = Stack(Note(files.Length == 0 ? "此文件夹还没有文件。" : "共 " + files.Length + " 个文件"));
            if (info.S("directory") != "") list.Children.Add(Note(info.S("directory")));
            if (files.Length == 0) return list;
            var rows = Stack();
            foreach (var file in files) {
                var name = file.S();
                var icon = ImageFromData(info.At("icons").S(name));
                rows.Children.Add(Row(icon == null ? AssetIcon("resource-file", 18) : new Image { Source = icon, Width = 21, Height = 21 },
                    name, ""));
            }
            list.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 215, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            return list;
        }
    }
}
