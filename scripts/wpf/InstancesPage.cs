using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace EBuild.Native
{
    public partial class Launcher
    {
        ContentControl homeCard;
        readonly Dictionary<string, (Button Row, TextBlock Mark)> instanceRows = new Dictionary<string, (Button, TextBlock)>();
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
            threadsButton = QuietAction("游戏线程", ShowThreads);
            downloadsButton = QuietAction("下载任务", () => ShowDownloadTasks());
            var tools = Actions(QuietAction("导入", ShowImport), threadsButton, downloadsButton);
            tools.Margin = new Thickness(0, 0, 0, 6);
            var page = ShowPage("home", 0, "实例", headerActions: tools);
            page.MaxWidth = 1120;

            homeCard = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 6, 0, 18) };
            page.Children.Add(homeCard);
            instanceRows.Clear();
            if (instances.Length != 0) {
                var add = QuietAction("＋ 新建实例", () => ShowInstall(""));
                add.Margin = new Thickness(0, 0, 0, 4);
                var libraryHeader = new DockPanel();
                DockPanel.SetDock(add, Dock.Right);
                libraryHeader.Children.Add(add);
                var libraryTitle = Heading("已安装实例", 16);
                libraryTitle.Margin = new Thickness(0, 0, 0, 4);
                libraryTitle.VerticalAlignment = VerticalAlignment.Center;
                libraryHeader.Children.Add(libraryTitle);
                page.Children.Add(libraryHeader);
                var list = Stack();
                foreach (var instance in instances) list.Children.Add(InstanceRow(instance));
                var library = new Border { Child = list, BorderThickness = new Thickness(0, 1, 0, 0) };
                UseResource(library, Border.BorderBrushProperty, "Line");
                page.Children.Add(library);
            }
            UpdateHomeCard();
        }

        FrameworkElement InstanceRow(object instance) {
            string id = instance.S("id");
            var row = Button("", () => SelectInstance(id));
            row.Style = (Style)FindResource("InstanceRowButton");
            row.Margin = new Thickness(0);
            AutomationProperties.SetName(row, InstanceLabel(instance) + (instance.B("externalMissing") ? " · 路径丢失" : ""));
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            content.ColumnDefinitions.Add(new ColumnDefinition());
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var mark = InstanceText("", 13, FontWeights.SemiBold);
            mark.HorizontalAlignment = HorizontalAlignment.Center;
            mark.VerticalAlignment = VerticalAlignment.Center;
            UseResource(mark, TextBlock.ForegroundProperty, "AccentText");
            content.Children.Add(mark);
            var icon = LoaderIcon(instance.S("loader"), 22);
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetColumn(icon, 1);
            content.Children.Add(icon);
            var title = InstanceText(instance.S("name"), 13);
            title.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(title, 2);
            content.Children.Add(title);
            if (instance.B("externalMissing")) {
                var warning = InstanceText("路径丢失", 11);
                warning.VerticalAlignment = VerticalAlignment.Center;
                warning.Margin = new Thickness(10, 0, 7, 0);
                UseResource(warning, TextBlock.ForegroundProperty, "Danger");
                Grid.SetColumn(warning, 3);
                content.Children.Add(warning);
            }
            row.Content = content;
            instanceRows.Add(id, (row, mark));

            var menu = CreateActionMenu();
            AddMenu(menu, "启动游戏", () => StartGame(id));
            AddMenu(menu, "实例设置", () => { ShowInstanceSettings(instance); return Task.CompletedTask; });
            if (instance.B("externalMissing")) AddMenu(menu, "重新定位", () => Relocate(instance));
            AddMenu(menu, instance.S("externalPath") == "" ? "删除游戏" : "移除实例", () => DeleteInstance(instance, false), destructive: true);
            if (instance.B("canDeleteOriginal")) AddMenu(menu, "删除原游戏", () => DeleteInstance(instance, true), destructive: true);
            row.ContextMenu = menu;
            var more = new Button { Content = "⋯", Width = 32, Padding = new Thickness(0), FontSize = 17,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                Style = (Style)FindResource("QuietButton") };
            menu.CustomPopupPlacementCallback = (popup, target, _) => new[] {
                new CustomPopupPlacement(new Point(target.Width - popup.Width, target.Height + 4), PopupPrimaryAxis.Horizontal),
                new CustomPopupPlacement(new Point(target.Width - popup.Width, -popup.Height - 4), PopupPrimaryAxis.Horizontal)
            };
            more.Click += (_, __) => {
                menu.PlacementTarget = more;
                menu.Placement = PlacementMode.Custom;
                menu.IsOpen = true;
            };
            menu.Closed += (_, __) => {
                menu.ClearValue(ContextMenu.PlacementTargetProperty);
                menu.ClearValue(ContextMenu.PlacementProperty);
            };
            AutomationProperties.SetName(more, "实例操作 · " + instance.S("name"));
            var entry = new Grid();
            entry.ColumnDefinitions.Add(new ColumnDefinition());
            entry.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            Grid.SetColumn(more, 1);
            entry.Children.Add(row);
            entry.Children.Add(more);
            var details = InstanceDetails(instance, row);
            var reveal = new Border { Child = details, ClipToBounds = true, Visibility = Visibility.Collapsed };
            var line = new Border {
                Child = Stack(entry, reveal), BorderThickness = new Thickness(0, 0, 0, 1), Background = Brushes.Transparent
            };
            UseResource(line, Border.BorderBrushProperty, "Line");
            BindDetailsReveal(row, line, reveal, details, menu);
            return line;
        }

        void BindDetailsReveal(Button row, Border line, Border reveal, Border details, ContextMenu menu) {
            var hover = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
            var expanded = false;
            int transition = 0;

            void Finish(bool open) {
                reveal.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                reveal.BeginAnimation(HeightProperty, null);
                reveal.BeginAnimation(OpacityProperty, null);
            }

            void SetExpanded(bool open, bool animate = true) {
                if (open == expanded && animate) return;
                expanded = open;
                var revision = ++transition;
                if (!animate || !MotionEnabled) {
                    Finish(open);
                    return;
                }

                bool wasVisible = reveal.Visibility == Visibility.Visible;
                double height = wasVisible ? reveal.ActualHeight : 0;
                double opacity = wasVisible ? reveal.Opacity : 0;
                reveal.Visibility = Visibility.Visible;
                details.Measure(new Size(line.ActualWidth, double.PositiveInfinity));
                var duration = TimeSpan.FromMilliseconds(open ? 175 : 125);
                var size = new DoubleAnimation(height, open ? details.DesiredSize.Height : 0, duration) {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                size.Completed += (_, __) => {
                    if (revision == transition) Finish(open);
                };
                reveal.BeginAnimation(HeightProperty, size);
                reveal.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, open ? 1 : 0, duration));
                if (open) AnimateEntrance(details, 0, 7);
            }

            void CloseDetails(bool animate) {
                hover.Stop();
                SetExpanded(false, animate);
            }

            hover.Tick += (_, __) => {
                hover.Stop();
                if (row.IsMouseOver && IsActive && !menu.IsOpen) SetExpanded(true);
            };
            row.MouseEnter += (_, __) => hover.Start();
            row.MouseLeave += (_, __) => hover.Stop();
            line.MouseLeave += (_, __) => CloseDetails(true);
            line.Unloaded += (_, __) => CloseDetails(false);
            menu.Opened += (_, __) => CloseDetails(false);
        }

        static Border InstanceDetails(object instance, Button row) {
            string loaderVersion = instance.S("loaderVersion");
            string version = InstanceVersion(instance) + (loaderVersion == "" ? "" : " " + loaderVersion);
            string path = instance.S("externalPath");
            string origin = path == "" ? "EBuild 管理的实例" :
                (instance.B("externalMissing") ? "目录不可用 · " : "外部文件夹 · ") + path;
            var versionText = InstanceText(version, 12);
            var originText = InstanceText(origin, 11);
            originText.Margin = new Thickness(0, 3, 0, 0);
            if (path != "") originText.ToolTip = path;
            UseResource(versionText, TextBlock.ForegroundProperty, "Muted");
            UseResource(originText, TextBlock.ForegroundProperty, instance.B("externalMissing") ? "Danger" : "Muted");
            AutomationProperties.SetHelpText(row, version + "。 " + origin);
            var details = new Border {
                Child = Stack(versionText, originText), VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(56, 0, 40, 6), Padding = new Thickness(9, 2, 0, 3), BorderThickness = new Thickness(1, 0, 0, 0)
            };
            UseResource(details, Border.BorderBrushProperty, "Line");
            return details;
        }

        static TextBlock InstanceText(string value, double size, FontWeight? weight = null) =>
            new TextBlock { Text = value, FontSize = size, FontWeight = weight ?? FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis };

        Button HomeAccountButton() {
            homeAvatar = new Image { Width = 24, Height = 24, Margin = new Thickness(0, 0, 8, 0), SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(homeAvatar, BitmapScalingMode.NearestNeighbor);
            homeIdentity = InstanceText("", 12);
            homeIdentity.VerticalAlignment = VerticalAlignment.Center;
            var identity = QuietAction("", () => Navigate(4));
            identity.Padding = new Thickness(2, 3, 6, 3);
            identity.MinHeight = 32;
            identity.Margin = new Thickness(-3, 4, 0, 0);
            identity.HorizontalAlignment = HorizontalAlignment.Left;
            identity.MaxWidth = 200;
            var person = new DockPanel();
            DockPanel.SetDock(homeAvatar, Dock.Left);
            person.Children.Add(homeAvatar);
            person.Children.Add(homeIdentity);
            identity.Content = person;
            AutomationProperties.SetName(identity, "玩家账号");
            var accounts = CreateActionMenu();
            AddMenu(accounts, "管理名称", () => ManageAppearance("name"));
            AddMenu(accounts, "管理皮肤", () => ManageAppearance("skin"));
            AddMenu(accounts, "管理披风", () => ManageAppearance("cape"));
            identity.ContextMenu = accounts;
            UpdateAccountIdentity();
            return identity;
        }

        ContextMenu CreateActionMenu() => new ContextMenu { Style = (Style)FindResource("ActionMenu") };

        void AddMenu(ContextMenu menu, string title, Func<Task> action, bool destructive = false) {
            var entry = new MenuItem { Header = title, Style = (Style)FindResource("ActionMenuItem") };
            if (destructive) UseResource(entry, Control.ForegroundProperty, "Danger");
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

        void UpdateHomeCard(bool animate = false) {
            if (route != "home" || homeCard == null) return;
            foreach (var pair in instanceRows) {
                bool isSelected = pair.Key == selected;
                pair.Value.Mark.Text = isSelected ? "✓" : "";
                AutomationProperties.SetItemStatus(pair.Value.Row, isSelected ? "已选中" : "");
            }
            launchButton = null;
            stopButton = null;
            var active = Active;
            string id = active.S("id");
            var identity = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
            var icon = LoaderIcon(active.S("loader"), 40);
            icon.Margin = new Thickness(8, 0, 8, 0);
            icon.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(icon, Dock.Left);
            identity.Children.Add(icon);
            string title = active == null ? "还没有游戏实例" : active.S("name");
            var name = InstanceText(title, 24, FontWeights.SemiBold);
            name.ToolTip = title;
            var caption = InstanceText(active == null ? "安装 Minecraft，或拖入游戏文件夹、整合包。" : InstanceVersion(active), 12);
            UseResource(caption, TextBlock.ForegroundProperty, "Muted");
            caption.Margin = new Thickness(0, 2, 0, 0);
            var metadata = Stack(name, caption);
            metadata.VerticalAlignment = VerticalAlignment.Center;
            if (active.B("externalMissing")) {
                var missing = InstanceText("找不到游戏文件夹", 12);
                UseResource(missing, TextBlock.ForegroundProperty, "Danger");
                missing.Margin = new Thickness(0, 4, 0, 0);
                metadata.Children.Add(missing);
            }
            metadata.Children.Add(HomeAccountButton());
            identity.Children.Add(metadata);

            var controls = Stack();
            if (active == null) {
                controls.Children.Add(InstancePrimary("新建实例", () => ShowInstall("")));
            } else {
                launchButton = InstancePrimary("启动游戏", () => StartGame(id));
                controls.Children.Add(launchButton);
                var secondary = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 3, 0, 0) };
                currentThread = QuietAction("查看详情", ShowThreads);
                stopButton = QuietAction("停止", () => StopGame(id));
                UseResource(stopButton, Control.ForegroundProperty, "Danger");
                secondary.Children.Add(currentThread);
                secondary.Children.Add(stopButton);
                if (active.B("externalMissing")) secondary.Children.Add(QuietAction("重新定位", () => Relocate(active)));
                var settingsButton = QuietAction("实例设置", () => { ShowInstanceSettings(active); return Task.CompletedTask; });
                settingsButton.Margin = new Thickness(0);
                settingsButton.MinWidth = launchButton.MinWidth;
                secondary.Children.Add(settingsButton);
                controls.Children.Add(secondary);
            }
            controls.Margin = new Thickness(14, 0, 0, 0);
            controls.MaxWidth = 310;
            controls.VerticalAlignment = VerticalAlignment.Center;

            var layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition());
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(controls, 1);
            layout.Children.Add(identity);
            layout.Children.Add(controls);
            homeCard.Content = layout;
            if (animate) AnimateEntrance(layout, 0, 10);
            UpdateGameControls();
            RenderDownloads();
        }

        Button QuietAction(string label, Func<Task> action) {
            var button = Button(label, action);
            button.Style = (Style)FindResource("QuietButton");
            return button;
        }

        Button InstancePrimary(string label, Func<Task> action) {
            var button = Button(label, action, true);
            button.HorizontalAlignment = HorizontalAlignment.Right;
            button.MinWidth = 112;
            button.Margin = new Thickness(0);
            return button;
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
                    launchButton.Content = game == null ? "▶  启动游戏" : GameLabel(game);
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
