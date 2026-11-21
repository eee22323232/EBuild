using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace EBuild.Native
{
    public partial class Launcher
    {
        const int VersionPageSize = 35;
        const int VersionRenderBatch = 6;
        StackPanel taskRows;
        StackPanel installTaskRows;
        string installTrackedId = "";
        readonly List<Action> progressBindings = new List<Action>();

        async Task ShowDownloads(int tab = 0, string game = "", string loader = "", string type = "",
            ResourceSearch existing = null, (string Query, string Type, int Limit)? catalogFilter = null) {
            Func<Task> backToCatalog = () => ShowDownloads(tab, game, loader, type, existing, catalogFilter);
            downloadsButton = QuietAction("下载任务", () => ShowDownloadTasks(backToCatalog, DownloadsSection));
            downloadsButton.Margin = new Thickness(0, 0, 0, 8);
            var page = ShowPage("downloads", DownloadsSection, "下载", headerActions: downloadsButton);
            page.MaxWidth = 1120;
            RenderDownloads();
            int currentPage = pageRevision;
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (currentPage != pageRevision) return;

            var tabs = new TabControl { Style = (Style)FindResource("DownloadTabs") };
            var minecraft = Stack();
            var modrinth = existing;
            var modrinthView = new Border();
            TextElement.SetFontWeight(minecraft, FontWeights.Normal);
            TextElement.SetFontWeight(modrinthView, FontWeights.Normal);
            var gameFilter = CatalogInput("搜索 Minecraft 版本", catalogFilter?.Query ?? "", 64);
            var release = Picker(new[] { Option("release", "正式版"), Option("snapshot", "快照与预发布") },
                catalogFilter?.Type ?? "release");
            var rows = Stack();
            var status = Text("", 12);
            status.VerticalAlignment = VerticalAlignment.Center;
            UseResource(status, TextBlock.ForegroundProperty, "Muted");
            int limit = catalogFilter?.Limit ?? VersionPageSize;
            int renderRevision = 0;
            int loadRevision = 0;
            bool rendered = false;
            Task BackToCatalog() => ShowDownloads(tabs.SelectedIndex, existing: modrinth,
                catalogFilter: (gameFilter.Text, Chosen(release), limit));
            backToCatalog = BackToCatalog;

            Button more = null;
            more = QuietAction("加载更多", async () => { limit += VersionPageSize; await Render(append: true); });
            more.HorizontalAlignment = HorizontalAlignment.Center;
            more.Margin = new Thickness(0, 8, 0, 0);
            more.Visibility = Visibility.Collapsed;
            async Task Render(bool animateRows = false, bool append = false) {
                int revision = ++renderRevision;
                bool Current() => revision == renderRevision && currentPage == pageRevision && tabs.SelectedIndex == 0;
                rendered = false;
                more.Visibility = Visibility.Collapsed;
                if (!append) rows.Children.Clear();
                await Dispatcher.Yield(DispatcherPriority.Background);
                if (!Current()) return;

                string query = gameFilter.Text.Trim();
                var matches = catalog.A("versions").Where(item => item.S("type") == Chosen(release) &&
                    item.S("id").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
                var source = catalog.S("source") == "bmclapi" ? "BMCLAPI 国内镜像" : "Mojang 官方源";
                status.Text = catalog == null ? "正在获取 Minecraft 版本…" :
                    source + (catalog.B("cached") ? " · 缓存" : "") + " · " + matches.Length + " 个版本";
                int first = rows.Children.Count;
                int end = Math.Min(limit, matches.Length);
                for (int position = first; position < end; position++) {
                    if (position > first && (position - first) % VersionRenderBatch == 0) {
                        await Dispatcher.Yield(DispatcherPriority.Background);
                        if (!Current()) return;
                    }
                    var row = MinecraftDownloadRow(matches[position], BackToCatalog);
                    rows.Children.Add(row);
                    if (animateRows && position < VersionRenderBatch) AnimateEntrance(row, 0, 9, position * 17);
                }
                if (catalog != null && matches.Length == 0) rows.Children.Add(Note("没有找到匹配的版本。"));
                more.Visibility = matches.Length > limit ? Visibility.Visible : Visibility.Collapsed;
                rendered = catalog != null;
            }
            async Task Load(bool force) {
                if (!force && rendered) return;
                int revision = ++loadRevision;
                status.Text = "正在获取 Minecraft 版本…";
                try {
                    bool fresh = catalog == null || force;
                    if (fresh) {
                        var result = await Call("catalog", force);
                        if (revision != loadRevision || currentPage != pageRevision) return;
                        catalog = result;
                    }
                    if (tabs.SelectedIndex == 0) await Render(fresh);
                }
                catch (Exception error) {
                    if (revision == loadRevision && currentPage == pageRevision) status.Text = error.Message;
                }
            }
            async Task LoadModrinth() {
                if (modrinthView.Child == null) {
                    if (modrinth == null) modrinth = new ResourceSearch(this, game, loader, type);
                    if (modrinth.Root.Parent is Border previous) previous.Child = null;
                    modrinthView.Child = modrinth.Root;
                }
                await modrinth.EnsureLoaded();
            }
            gameFilter.TextChanged += async (_, __) => { limit = VersionPageSize; await Guard(() => Render()); };
            release.SelectionChanged += async (_, __) => { limit = VersionPageSize; await Guard(() => Render(true)); };
            var filters = new Grid { Height = 36 };
            filters.ColumnDefinitions.Add(new ColumnDefinition());
            filters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            AutomationProperties.SetName(release, "版本类型");
            release.Width = 148;
            release.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(release, 1);
            filters.Children.Add(gameFilter);
            filters.Children.Add(release);
            var summary = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            var reload = QuietAction("刷新", () => Load(true));
            reload.Margin = new Thickness(0);
            reload.MinHeight = 25;
            reload.Padding = new Thickness(8, 3, 8, 3);
            reload.FontSize = 12;
            DockPanel.SetDock(reload, Dock.Right);
            summary.Children.Add(reload);
            summary.Children.Add(status);
            minecraft.Children.Add(filters);
            minecraft.Children.Add(DownloadListLine(summary));
            minecraft.Children.Add(rows);
            minecraft.Children.Add(more);
            tabs.Items.Add(Tab("Minecraft", minecraft));
            tabs.Items.Add(Tab("Modrinth", modrinthView));
            tabs.Items.Add(Tab("CurseForge", Note("CurseForge 资源将在获得 API 访问权限后开放。")));
            tabs.SelectedIndex = tab;
            page.Children.Add(tabs);
            int previousTab = tab;
            tabs.SelectionChanged += async (_, e) => {
                if (e.Source != tabs) return;
                if (tabs.SelectedItem is TabItem activeTab && activeTab.Content is UIElement content)
                    AnimateEntrance(content, tabs.SelectedIndex > previousTab ? 14 : -14, 0);
                previousTab = tabs.SelectedIndex;
                if (tabs.SelectedIndex == 0) await Guard(() => Load(false));
                if (tabs.SelectedIndex == 1) await Guard(LoadModrinth);
            };
            if (tab == 0) await Load(false);
            else if (tab == 1) await LoadModrinth();
        }

        static TextBox CatalogInput(string label, string value = "", int max = 0) {
            var input = Input(value, max);
            input.SetResourceReference(StyleProperty, "CatalogInput");
            input.Tag = label;
            AutomationProperties.SetName(input, label);
            return input;
        }

        static Border DownloadListLine(UIElement content) {
            var line = new Border { Child = content, BorderThickness = new Thickness(0, 0, 0, 1) };
            UseResource(line, Border.BorderBrushProperty, "Line");
            return line;
        }

        FrameworkElement MinecraftDownloadRow(object item, Func<Task> back) {
            var version = item.S("id");
            var row = new Grid { MinHeight = 38 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            var icon = LoaderIcon("vanilla", 23);
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            row.Children.Add(icon);
            var name = InstanceText(version, 13, FontWeights.SemiBold);
            var date = Text(DateLabel(item.S("releaseTime")), 11);
            UseResource(date, TextBlock.ForegroundProperty, "Muted");
            var details = Stack(name, date);
            details.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(details, 1);
            row.Children.Add(details);
            var install = QuietAction("安装", () => ShowInstall(version, back));
            install.Margin = new Thickness(0);
            install.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetName(install, "安装 Minecraft " + version);
            UseResource(install, Control.ForegroundProperty, "AccentText");
            Grid.SetColumn(install, 2);
            row.Children.Add(install);
            return DownloadListLine(row);
        }

        static string DateLabel(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? date.ToLocalTime().ToString("yyyy-MM-dd") : "";

        async Task ShowInstall(string preset, Func<Task> back = null) {
            var previous = route;
            var page = ShowPage("install", DownloadsSection, "安装 Minecraft", back ?? (async () => {
                if (previous == "home") ShowHome();
                else if (previous == "instances") ShowInstances();
                else await ShowDownloads();
            }));
            var versions = Picker(Array.Empty<Choice>(), editable: true);
            var snapshots = Check("显示快照版");
            var loaders = Picker(LoaderOptions.Where(item => item.Key != "minecraft"));
            var loaderVersions = Picker(Array.Empty<Choice>(), editable: true);
            loaderVersions.IsTextSearchEnabled = false;
            var loaderField = Field("加载器版本", loaderVersions, "可从列表选择，也可直接输入版本号。");
            var name = Input("", 80);
            var status = Note("");
            string suggested = "";
            string previousGame = "", previousLoader = "";
            int request = 0;
            int catalogRequest = 0;
            bool filling = false;
            bool installing = false;
            Button install = null;
            string EnteredLoaderVersion() {
                string value = loaderVersions.Text.Trim();
                if (loaderVersions.SelectedItem is Choice selected && value == selected.Label) return selected.Key;
                return Minecraft.ValidLoaderVersion(value) ? value : "";
            }
            void UpdateInstallButton() {
                if (install == null) return;
                string game = Chosen(versions);
                install.IsEnabled = !installing && game != "" && versions.Text == game &&
                    (Chosen(loaders) == "vanilla" || EnteredLoaderVersion() != "");
            }
            async Task VersionsChanged(bool refresh = false) {
                if (filling) return;
                int revision = ++request;
                string game = Chosen(versions);
                string loader = Chosen(loaders);
                bool plain = loader == "vanilla";
                loaderField.Visibility = plain ? Visibility.Collapsed : Visibility.Visible;
                if (game != previousGame || loader != previousLoader) {
                    previousGame = game;
                    previousLoader = loader;
                    SetOptions(loaderVersions, Array.Empty<Choice>(), "");
                    loaderVersions.Text = "";
                }
                UpdateInstallButton();
                if (game == "") return;
                var next = game + (plain ? " 原版" : " " + Loader(loader));
                if (name.Text == "" || name.Text == suggested) name.Text = next;
                suggested = next;
                status.Text = plain ? "" : "正在查询加载器版本…";
                if (plain) return;
                try {
                    var available = (await Call("loaders", game, loader, refresh)).A();
                    if (revision != request) return;
                    string enteredText = loaderVersions.Text;
                    string enteredVersion = EnteredLoaderVersion();
                    SetOptions(loaderVersions, available.Select(item => Option(item.S("id"),
                        item.S("id") + (item.B("stable") ? "" : " · 预发布"))),
                        enteredVersion == "" ? available.FirstOrDefault(item => item.B("stable")).S("id") : enteredVersion);
                    if (enteredText != "" && (enteredVersion == "" || Chosen(loaderVersions) != enteredVersion))
                        loaderVersions.Text = enteredText;
                    status.Text = available.Length == 0 ? "列表暂无版本，可以输入已知兼容的版本号。" : "";
                    UpdateInstallButton();
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
                    await VersionsChanged(refresh);
                } catch (Exception error) { if (revision == catalogRequest) status.Text = error.Message; }
            }
            install = Button("安装实例", async () => {
                var game = Chosen(versions);
                var loader = Chosen(loaders);
                if (game == "" || versions.Text != game) throw new IOException("请从列表选择 Minecraft 版本。");
                var loaderVersion = loader == "vanilla" ? "" : EnteredLoaderVersion();
                if (loader != "vanilla" && loaderVersion == "") throw new IOException("请选择加载器版本，或输入有效的版本号。");
                var id = Guid.NewGuid().ToString();
                installing = true;
                UpdateInstallButton();
                try {
                    await TrackInstallation(() => RunJob("instances:create", J.Map("name", name.Text.Trim(), "version", game,
                        "loader", loader, "loaderVersion", loaderVersion), id), id);
                } finally {
                    installing = false;
                    UpdateInstallButton();
                }
            }, true);
            install.IsEnabled = false;
            var loaderDisplay = new Image { Width = 32, Height = 32, Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center };
            loaderDisplay.Source = LoaderIcon("vanilla", 32).Source;
            var loaderRow = new DockPanel();
            DockPanel.SetDock(loaderDisplay, Dock.Right);
            loaderRow.Children.Add(loaderDisplay);
            loaderRow.Children.Add(loaders);
            var details = Columns(Field("实例名称", name), loaderField);
            details.Margin = new Thickness(0, 8, 0, 0);
            snapshots.Margin = new Thickness(0, 8, 0, 0);
            page.Children.Add(Card(Stack(Columns(Field("Minecraft 版本", versions),
                Field("加载器", loaders, controlBody: loaderRow)), details, snapshots)));
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
            versions.SelectionChanged += async (_, __) => await Guard(() => VersionsChanged());
            versions.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, __) => UpdateInstallButton()));
            loaderVersions.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, __) => UpdateInstallButton()));
            loaders.SelectionChanged += async (_, __) => {
                loaderDisplay.Source = LoaderIcon(Chosen(loaders), 32).Source;
                await Guard(() => VersionsChanged());
            };
            await Load(preset);
        }

        Task ShowDownloadTasks(Func<Task> back = null, int section = InstancesSection) {
            var page = ShowPage("tasks", section, "下载任务", back ?? (() => { ShowInstances(); return Task.CompletedTask; }));
            page.MaxWidth = 1120;
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
                if (route == "instances") ShowInstances();
                if (route == "home") UpdateHomeCard();
                Say("安装完成 · " + installed.S("name"));
            } finally { await RefreshDownloads(); }
        }

        void UpdateDownloadProgress() {
            foreach (var update in progressBindings) update();
        }

        void RenderDownloads() {
            progressBindings.Clear();
            if ((ShowingGameCard || route == "downloads") && downloadsButton != null) {
                int active = downloads.Count(item => item.S("state") != "complete");
                downloadsButton.Content = "下载任务" + (active == 0 ? "" : " · " + active);
            }
            if (route == "tasks" && taskRows != null) {
                taskRows.Children.Clear();
                if (downloads.Length == 0) taskRows.Children.Add(Note("暂无下载任务"));
                foreach (var task in downloads) taskRows.Children.Add(DownloadTaskRow(task));
            }
            if (route == "install" && installTaskRows != null) {
                installTaskRows.Children.Clear();
                var task = downloads.FirstOrDefault(item => item.S("id") == installTrackedId);
                if (task != null) installTaskRows.Children.Add(DownloadTaskRow(task));
            }
            UpdateFooterProgress();
        }

        FrameworkElement DownloadTaskRow(object task) {
            string id = task.S("id");
            string state = task.S("state");
            var input = task.At("input");
            var tools = Actions();
            string badge;
            switch (state) {
                case "active": badge = "安装中"; tools.Children.Add(QuietAction("暂停安装", () => Call("task:cancel"))); break;
                case "failed": badge = "！失败"; break;
                case "complete":
                    badge = "✓ 完成";
                    if (task.At("instance") != null) tools.Children.Add(QuietAction("查看实例", () => ViewInstance(id)));
                    break;
                default: badge = "Ⅱ 已暂停"; break;
            }
            if (state == "failed" || state == "paused") tools.Children.Add(QuietAction("继续安装",
                () => TrackInstallation(() => RunJob("downloads:resume", id), id)));
            if (state != "active") tools.Children.Add(QuietAction("移除记录", async () => {
                if (!Confirm("移除下载任务", state == "complete" ? "只移除下载记录，已安装的实例会保留。" :
                    "将移除此任务及其暂存进度。是否继续？")) return;
                await RunJob("downloads:remove", id);
                await RefreshDownloads();
            }));
            var subtitle = InstanceVersion(input) + (input.S("loaderVersion") == "" ? "" : " " + input.S("loaderVersion"));
            var label = Text(badge, 12);
            label.Margin = new Thickness(12, 0, 0, 0);
            label.VerticalAlignment = VerticalAlignment.Center;
            UseResource(label, TextBlock.ForegroundProperty, state == "active" ? "AccentText" : state == "failed" ? "Danger" : "Muted");
            var title = InstanceText(input.S("name"), 14, FontWeights.SemiBold);
            title.ToolTip = input.S("name");
            var header = new DockPanel();
            DockPanel.SetDock(label, Dock.Right);
            header.Children.Add(label);
            header.Children.Add(title);
            var details = Stack(header, Note(subtitle));
            if (state == "active") {
                details.Children.Add(DownloadTaskProgress(task));
            } else if (state != "complete" && task.S("message") != "") details.Children.Add(Note(task.S("message")));
            if (state != "active" && task.At("transfer").S("switch") != "") details.Children.Add(Note(task.At("transfer").S("switch")));
            if (tools.Children.Count != 0) details.Children.Add(tools);
            var icon = LoaderIcon(input.S("loader"), 32);
            icon.Margin = new Thickness(0, 1, 12, 0);
            icon.VerticalAlignment = VerticalAlignment.Top;
            var row = new DockPanel { Margin = new Thickness(0, 10, 0, 6) };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(details);
            return DownloadListLine(row);
        }

        FrameworkElement DownloadTaskProgress(object task) {
            var message = InstanceText("", 12, FontWeights.SemiBold);
            var count = InstanceText("", 12);
            UseResource(count, TextBlock.ForegroundProperty, "Muted");
            count.Margin = new Thickness(12, 0, 0, 0);
            var heading = new DockPanel();
            DockPanel.SetDock(count, Dock.Right);
            heading.Children.Add(count);
            heading.Children.Add(message);
            var bar = new ProgressBar { Margin = new Thickness(0, 6, 0, 0) };
            UseResource(bar, StyleProperty, "DownloadProgressBar");
            AutomationProperties.SetName(bar, "当前安装阶段进度");
            var telemetry = InstanceText("", 12);
            UseResource(telemetry, TextBlock.ForegroundProperty, "Muted");
            telemetry.Margin = new Thickness(0, 5, 0, 0);
            var progress = Stack(heading, bar, telemetry);
            progress.Margin = new Thickness(0, 8, 0, 4);

            string id = task.S("id");
            string previousMessage = "", previousPhase = "", previousSource = "";
            int previousTotal = 0, previousDone = 0;
            long previousBytes = 0;
            var responseClock = Stopwatch.StartNew();
            void UpdateProgress() {
                bool active = jobActive && jobProgress.S("kind") == "game-install";
                var phase = active ? jobProgress.S("phase") : "";
                var stage = active ? jobProgress.S("message") : task.S("message");
                int total = active ? Math.Max(0, jobProgress.I("total")) : 0;
                int done = active ? Math.Max(0, Math.Min(total, jobProgress.I("done"))) : 0;
                message.Text = stage == "" ? "正在准备安装…" : stage;
                message.ToolTip = message.Text;
                count.Text = total == 0 ? "" : done.ToString("N0") + " / " + total.ToString("N0") + " 项";
                bar.Tag = MotionEnabled ? "Animate" : null;
                bar.IsIndeterminate = total == 0;
                if (phase == previousPhase && stage == previousMessage && total == previousTotal && done == previousDone) return;
                bool animate = MotionEnabled && phase == previousPhase && stage == previousMessage && total == previousTotal && done > previousDone;
                double from = bar.Value;
                bar.BeginAnimation(ProgressBar.ValueProperty, null);
                bar.Maximum = Math.Max(1, total);
                bar.Value = done;
                if (animate) bar.BeginAnimation(ProgressBar.ValueProperty,
                    new DoubleAnimation(from, done, TimeSpan.FromMilliseconds(160)) { FillBehavior = FillBehavior.Stop });
                previousPhase = phase;
                previousMessage = stage;
                previousTotal = total;
                previousDone = done;
            }
            void UpdateTransfer() {
                var transfer = backend.DownloadTransferStatus(id);
                string source = transfer.S("source");
                long bytes = Convert.ToInt64(transfer.At("bytes"));
                long speed = Convert.ToInt64(transfer.At("bytesPerSecond"));
                bool transferring = previousPhase == "download" || previousPhase == "java";
                if (bytes != previousBytes || source != previousSource) responseClock.Restart();
                previousBytes = bytes;
                previousSource = source;
                bool waiting = transferring && source != "" && speed == 0 && responseClock.ElapsedMilliseconds >= 3500;
                string change = transfer.S("switch");
                if (change == "") change = task.At("transfer").S("switch");
                telemetry.Text = String.Join("  ·  ", new[] {
                    waiting ? "等待下载源响应" : transferring && speed > 0 ? TransferSize(speed) + "/s" : "",
                    bytes > 0 ? "已接收 " + TransferSize(bytes) : "", transferring ? source : "", change
                }.Where(value => value != ""));
                telemetry.ToolTip = telemetry.Text;
                telemetry.Visibility = telemetry.Text == "" ? Visibility.Collapsed : Visibility.Visible;
            }
            var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += (_, __) => { UpdateTransfer(); bar.Tag = MotionEnabled ? "Animate" : null; };
            progress.Loaded += (_, __) => { UpdateTransfer(); timer.Start(); };
            progress.Unloaded += (_, __) => timer.Stop();
            progressBindings.Add(UpdateProgress);
            UpdateProgress();
            return progress;
        }

        static string TransferSize(long bytes) {
            if (bytes >= 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024 * 1024)).ToString("0.00") + " GB";
            if (bytes >= 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("0.0") + " MB";
            return bytes >= 1024 ? (bytes / 1024.0).ToString("0") + " KB" : bytes + " B";
        }
    }
}
