using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
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
        StackPanel instanceList;
        TextBlock instanceSearchEmpty;
        string instanceQuery = "";
        int instanceListRevision;
        Button launchButton;
        TextBlock launchState;
        Border launchStateBadge;
        Button stopButton;
        Button threadsButton;
        Button downloadsButton;
        TextBlock homeIdentity;
        Image homeAvatar;
        StackPanel threadRows;
        readonly Dictionary<string, object> diagnostics = new Dictionary<string, object>();
        bool checkingDiagnostics;
        bool ShowingGameCard => route == "home" || route == "instances";

        void ShowHome() {
            threadsButton = QuietAction("游戏线程", ShowThreads);
            downloadsButton = QuietAction("下载任务", () => ShowDownloadTasks(() => {
                ShowHome();
                return Task.CompletedTask;
            }, HomeSection));
            var tools = Actions(threadsButton, downloadsButton);
            tools.Margin = new Thickness(0, 0, 0, 6);
            var page = ShowPage("home", HomeSection, "首页", headerActions: tools);
            page.MaxWidth = 1120;
            var header = (FrameworkElement)page.Children[0];
            var hero = new ContentControl {
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch
            };
            void FitHomepage() {
                var height = PageScroll.ViewportHeight - Page.Margin.Top - Page.Margin.Bottom - header.ActualHeight;
                hero.MinHeight = Math.Max(0, Math.Floor(height));
            }
            ScrollChangedEventHandler fitViewport = (_, e) => {
                if (e.Source == PageScroll && e.ViewportHeightChange != 0) FitHomepage();
            };
            hero.Loaded += (_, __) => { PageScroll.ScrollChanged += fitViewport; FitHomepage(); };
            hero.Unloaded += (_, __) => PageScroll.ScrollChanged -= fitViewport;
            header.SizeChanged += (_, __) => FitHomepage();
            homeCard = hero;
            page.Children.Add(homeCard);
            instanceRows.Clear();
            UpdateHomeCard();
        }

        void ShowInstances() {
            threadsButton = QuietAction("游戏线程", ShowThreads);
            downloadsButton = QuietAction("下载任务", () => ShowDownloadTasks());
            var tools = Actions(QuietAction("导入", ShowImport), threadsButton, downloadsButton);
            tools.Margin = new Thickness(0, 0, 0, 6);
            var page = ShowPage("instances", InstancesSection, "实例", headerActions: tools);
            page.MaxWidth = 1120;

            homeCard = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 10) };
            page.Children.Add(homeCard);
            instanceRows.Clear();
            instanceList = null;
            if (instances.Length != 0) {
                var add = Button("＋ 新建实例", () => ShowInstall(""));
                add.Padding = new Thickness(9, 4, 9, 4);
                add.MinHeight = 28;
                add.Margin = new Thickness(0, 0, 0, 6);
                var libraryHeader = new DockPanel();
                DockPanel.SetDock(add, Dock.Right);
                libraryHeader.Children.Add(add);
                var search = CatalogInput("搜索实例名称、版本或加载器", instanceQuery, 80);
                AutomationProperties.SetAutomationId(search, "InstanceSearch");
                var clear = QuietAction("×", () => { search.Clear(); search.Focus(); return Task.CompletedTask; });
                clear.Width = 30;
                clear.Margin = new Thickness(0);
                clear.ToolTip = "清除搜索";
                AutomationProperties.SetName(clear, "清除实例搜索");
                var searchTools = new DockPanel { Margin = new Thickness(0, 0, 8, 6), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(clear, Dock.Right);
                searchTools.Children.Add(clear);
                searchTools.Children.Add(search);
                DockPanel.SetDock(searchTools, Dock.Right);
                libraryHeader.Children.Add(searchTools);
                var libraryTitle = Heading("已安装实例", 16);
                libraryTitle.Margin = new Thickness(0, 0, 0, 4);
                libraryTitle.VerticalAlignment = VerticalAlignment.Center;
                libraryHeader.Children.Add(libraryTitle);
                page.Children.Add(libraryHeader);
                instanceList = Stack();
                instanceSearchEmpty = Note("没有匹配的实例");
                instanceSearchEmpty.Margin = new Thickness(0, 10, 0, 0);
                var library = new Border { Child = Stack(instanceList, instanceSearchEmpty), BorderThickness = new Thickness(0, 1, 0, 0) };
                UseResource(library, Border.BorderBrushProperty, "Line");
                page.Children.Add(library);
                void UpdateSearchTools() {
                    bool empty = instanceQuery.Length == 0;
                    clear.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
                    search.Width = empty ? 305 : 275;
                }
                UpdateSearchTools();
                search.TextChanged += (_, __) => {
                    instanceQuery = search.Text;
                    UpdateSearchTools();
                    FilterInstanceLibrary();
                };
                search.PreviewKeyDown += (_, e) => {
                    if (e.Key == Key.Escape && search.Text.Length != 0) { search.Clear(); e.Handled = true; }
                };
                RenderInstanceLibrary();
            }
            UpdateHomeCard();
        }

        void FilterInstanceLibrary() {
            var keywords = instanceQuery.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            bool hasResults = false;
            foreach (FrameworkElement row in instanceList.Children) {
                bool matches = MatchesInstanceSearch((string)row.Tag, keywords);
                row.Visibility = matches ? Visibility.Visible : Visibility.Collapsed;
                hasResults |= matches;
            }
            instanceSearchEmpty.Visibility = hasResults ? Visibility.Collapsed : Visibility.Visible;
        }

        static string InstanceSearchText(object instance) => String.Join(" ", instance.S("name"), InstanceVersion(instance),
            instance.S("loader"), instance.S("loaderVersion"));

        static bool MatchesInstanceSearch(string text, string[] keywords) =>
            keywords.All(word => text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);

        IEnumerable<object> OrderedInstances() => instances.OrderByDescending(item => item.B("pinned")).ThenByDescending(LastInstanceLaunch)
            .ThenBy(item => item.S("name"), StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.S("id"), StringComparer.Ordinal);

        static DateTimeOffset LastInstanceLaunch(object instance) {
            return DateTimeOffset.TryParse(instance.S("lastLaunch"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date : DateTimeOffset.MinValue;
        }

        Dictionary<string,int> InstanceDuplicateNumbers() {
            var numbers = new Dictionary<string,int>();
            foreach (var group in instances.GroupBy(item => item.S("name"), StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1)) {
                int number = 0;
                foreach (var item in group.OrderBy(item => item.S("id"), StringComparer.Ordinal))
                    numbers[item.S("id")] = ++number;
            }
            return numbers;
        }

        static string DuplicateInstanceLabel(object instance, int number) {
            var last = LastInstanceLaunch(instance);
            string recent = last == DateTimeOffset.MinValue ? "暂无启动记录" : "上次 " + last.ToLocalTime().ToString("MM/dd HH:mm");
            return "同名 " + number + " · " + recent;
        }

        void RenderInstanceLibrary() {
            if (route != "instances" || instanceList == null) return;
            int revision = ++instanceListRevision;
            var duplicateNumbers = InstanceDuplicateNumbers();
            var duplicateLabels = new Dictionary<string,TextBlock>();
            instanceRows.Clear();
            instanceList.Children.Clear();
            foreach (var item in OrderedInstances()) {
                string id = item.S("id");
                duplicateNumbers.TryGetValue(id, out int number);
                instanceList.Children.Add(InstanceRow(item, number, duplicateLabels));
            }
            UpdateInstanceHighlights();
            FilterInstanceLibrary();
            if (duplicateLabels.Count != 0) _ = Guard(async () => {
                var counts = await backend.InstanceModCounts(instances.Where(item => duplicateLabels.ContainsKey(item.S("id"))).ToArray());
                if (route != "instances" || revision != instanceListRevision) return;
                foreach (var count in counts) {
                    var label = duplicateLabels[count.Key];
                    label.Text += " · " + count.Value + " 个模组";
                    label.ToolTip = label.Text;
                }
            });
        }

        async Task SetInstancePinned(string id, bool pinned) {
            await Call("instances:pin", id, pinned);
            await RefreshInstances();
            RenderInstanceLibrary();
            Say(pinned ? "实例已置顶" : "已取消置顶");
        }

        async Task RenameInstance(object instance) {
            var name = Input(instance.S("name"), 80);
            var dialog = new TaskDialog(this, "重命名实例", Stack(Field("实例名称", name)));
            dialog.Loaded += (_, __) => { name.Focus(); name.SelectAll(); };
            if (await dialog.Result("保存", async () => {
                await Call("instances:rename", instance.S("id"), name.Text);
                return true;
            }) != true) return;
            await RefreshInstances();
            RenderInstanceLibrary();
            UpdateHomeCard();
            Say("实例已重命名");
        }

        FrameworkElement InstanceRow(object instance, int duplicateNumber, Dictionary<string,TextBlock> duplicateLabels) {
            string id = instance.S("id");
            var row = Button("", () => SelectInstance(id));
            row.Style = (Style)FindResource("InstanceRowButton");
            row.Margin = new Thickness(0);
            AutomationProperties.SetName(row, InstanceLabel(instance) + (instance.B("externalMissing") ? " · 路径丢失" : ""));
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(31) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });
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
            if (instance.B("pinned")) {
                var pinned = new Run("  ·  置顶") { FontSize = 11 };
                pinned.SetResourceReference(TextElement.ForegroundProperty, "AccentText");
                title.Inlines.Add(pinned);
            }
            title.VerticalAlignment = VerticalAlignment.Center;
            title.Margin = new Thickness(0, 0, 12, 0);
            Grid.SetColumn(title, 2);
            content.Children.Add(title);
            var version = InstanceText(instance.S("version") + " · " + Loader(instance.S("loader")), 12);
            UseResource(version, TextBlock.ForegroundProperty, "Muted");
            var metadata = Stack(version);
            if (duplicateNumber > 0) {
                var duplicate = InstanceText(DuplicateInstanceLabel(instance, duplicateNumber), 11);
                duplicate.ToolTip = duplicate.Text;
                UseResource(duplicate, TextBlock.ForegroundProperty, "Muted");
                metadata.Children.Add(duplicate);
                duplicateLabels.Add(id, duplicate);
                AutomationProperties.SetName(row, InstanceLabel(instance) + " · 同名 " + duplicateNumber +
                    (instance.B("externalMissing") ? " · 路径丢失" : ""));
            }
            metadata.VerticalAlignment = VerticalAlignment.Center;
            metadata.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(metadata, 3);
            content.Children.Add(metadata);
            if (instance.B("externalMissing")) {
                var warning = InstanceText("路径丢失", 11);
                warning.VerticalAlignment = VerticalAlignment.Center;
                warning.Margin = new Thickness(10, 0, 7, 0);
                UseResource(warning, TextBlock.ForegroundProperty, "Danger");
                Grid.SetColumn(warning, 4);
                content.Children.Add(warning);
            }
            row.Content = content;
            instanceRows.Add(id, (row, mark));

            var menu = CreateActionMenu();
            AddMenu(menu, "启动游戏", () => StartGame(id));
            AddMenu(menu, "实例设置", () => { ShowInstanceSettings(instance); return Task.CompletedTask; });
            AddMenu(menu, "重命名", () => RenameInstance(instance));
            AddMenu(menu, instance.B("pinned") ? "取消置顶" : "置顶", () => SetInstancePinned(id, !instance.B("pinned")));
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
                Child = Stack(entry, reveal), BorderThickness = new Thickness(0, 0, 0, 1), Background = Brushes.Transparent,
                Tag = InstanceSearchText(instance)
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
            var identity = QuietAction("", () => Navigate(AccountsSection));
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

        void UpdateInstanceHighlights() {
            foreach (var pair in instanceRows) {
                bool isSelected = pair.Key == selected;
                pair.Value.Row.Tag = isSelected;
                pair.Value.Mark.Text = isSelected ? "✓" : "";
                AutomationProperties.SetItemStatus(pair.Value.Row, isSelected ? "已选中" : "");
            }
        }

        void UpdateHomeCard(bool animate = false) {
            if (!ShowingGameCard || homeCard == null) return;
            bool hero = route == "home";
            UpdateInstanceHighlights();
            launchButton = null;
            launchState = null;
            launchStateBadge = null;
            stopButton = null;
            var active = Active;
            string id = active.S("id");
            var identity = new DockPanel { VerticalAlignment = hero ? VerticalAlignment.Bottom : VerticalAlignment.Center };
            var icon = LoaderIcon(active.S("loader"), hero ? 86 : 38);
            if (!hero) {
                icon.Margin = new Thickness(0, 0, 12, 0);
                icon.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(icon, Dock.Left);
                identity.Children.Add(icon);
            }
            string title = active == null ? "还没有游戏实例" : active.S("name");
            var name = InstanceText(title, hero ? 29 : 21, FontWeights.SemiBold);
            name.ToolTip = title;
            var caption = InstanceText(active == null ? "安装 Minecraft，或拖入游戏文件夹、整合包。" : InstanceVersion(active), 12);
            UseResource(caption, TextBlock.ForegroundProperty, "Muted");
            caption.Margin = new Thickness(0, 2, 0, 0);
            var metadata = hero && active != null ? Stack(HomeInstanceButton(name, caption)) : Stack(name);
            if (!hero) metadata.Children.Add(caption);
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
                var create = hero ? HomeLaunchAction(icon, "添加游戏", caption.Text, () => ShowInstall(""), out _)
                    : InstancePrimary("新建实例", () => ShowInstall(""));
                controls.Children.Add(create);
            } else {
                var launch = hero ? HomeLaunchAction(icon, "开始游戏", "", () => StartGame(id), out launchButton)
                    : (launchButton = InstancePrimary("启动游戏", () => StartGame(id)));
                launchState = InstanceText("", 12);
                AutomationProperties.SetLiveSetting(launchState, AutomationLiveSetting.Polite);
                launchState.MaxWidth = 164;
                UseResource(launchState, TextBlock.ForegroundProperty, "Ink");
                launchStateBadge = new Border { Child = launchState, Padding = new Thickness(9, 5, 9, 5),
                    CornerRadius = new CornerRadius(5), VerticalAlignment = VerticalAlignment.Center };
                UseResource(launchStateBadge, Border.BackgroundProperty, "Canvas");
                stopButton = Button("停止", () => StopGame(id));
                stopButton.Margin = new Thickness(7, 0, 0, 0);
                UseResource(stopButton, Control.ForegroundProperty, "Danger");
                var commands = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                commands.Children.Add(launch);
                commands.Children.Add(launchStateBadge);
                commands.Children.Add(stopButton);
                controls.Children.Add(commands);
                var secondary = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 0, 0) };
                currentThread = QuietAction("查看详情", ShowThreads);
                secondary.Children.Add(currentThread);
                if (active.B("externalMissing")) secondary.Children.Add(QuietAction("重新定位", () => Relocate(active)));
                var settingsButton = QuietAction("实例设置", () => { ShowInstanceSettings(active); return Task.CompletedTask; });
                settingsButton.Margin = new Thickness(0);
                if (!hero) settingsButton.MinWidth = launchButton.MinWidth;
                secondary.Children.Add(settingsButton);
                controls.Children.Add(secondary);
            }
            controls.Margin = new Thickness(14, 0, 0, 0);
            controls.MaxWidth = hero ? 400 : 310;
            controls.VerticalAlignment = VerticalAlignment.Center;

            var layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition());
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(controls, 1);
            layout.Children.Add(identity);
            layout.Children.Add(controls);
            if (hero) {
                var surface = new Grid { MinHeight = 242, Background = Brushes.Transparent };
                surface.Children.Add(HomeCurveTexture(out var glow));
                surface.PreviewMouseLeftButtonDown += (_, __) => {
                    if (MotionEnabled) PlayHomeCurveFlow(glow);
                };
                layout.Margin = new Thickness(9, 21, 9, 14);
                layout.VerticalAlignment = VerticalAlignment.Bottom;
                surface.Children.Add(layout);
                homeCard.Content = surface;
            } else {
                var card = Card(layout);
                card.Padding = new Thickness(13, 9, 13, 8);
                card.Margin = new Thickness(0);
                homeCard.Content = card;
            }
            if (animate) AnimateEntrance(layout, 0, 10);
            UpdateGameControls();
            RenderDownloads();
        }

        FrameworkElement HomeInstanceButton(TextBlock name, TextBlock caption) {
            UseResource(name, TextBlock.ForegroundProperty, "Ink");
            var chevron = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 1,5 L 5,1 L 9,5"),
                StrokeThickness = 1.35, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            UseResource(chevron, System.Windows.Shapes.Shape.StrokeProperty, "Muted");
            var title = new DockPanel();
            DockPanel.SetDock(chevron, Dock.Right);
            title.Children.Add(chevron);
            title.Children.Add(name);
            var popup = new Popup { Placement = PlacementMode.Top, VerticalOffset = -6, StaysOpen = false,
                AllowsTransparency = true, PopupAnimation = MotionEnabled ? PopupAnimation.Fade : PopupAnimation.None };
            var choose = QuietAction("", () => {
                if (popup.IsOpen) popup.IsOpen = false;
                else OpenHomeInstanceMenu(popup);
                return Task.CompletedTask;
            });
            choose.Content = Stack(title, caption);
            choose.Padding = new Thickness(3, 1, 6, 2);
            choose.Margin = new Thickness(-3, 0, 0, 0);
            choose.HorizontalAlignment = HorizontalAlignment.Left;
            choose.HorizontalContentAlignment = HorizontalAlignment.Left;
            choose.ToolTip = "切换首页实例";
            AutomationProperties.SetName(choose, "切换首页实例 · " + name.Text);
            AutomationProperties.SetAutomationId(choose, "HomeInstanceSwitcher");
            popup.PlacementTarget = choose;
            popup.Closed += (_, __) => popup.Child = null;
            choose.Unloaded += (_, __) => popup.IsOpen = false;
            var host = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
            host.Children.Add(choose);
            host.Children.Add(popup);
            return host;
        }

        void OpenHomeInstanceMenu(Popup popup) {
            var search = CatalogInput("搜索实例名称、版本或加载器", max: 80);
            search.Margin = new Thickness(0, 0, 0, 8);
            var rows = Stack();
            var empty = Note("没有匹配的实例");
            empty.Margin = new Thickness(6);
            empty.Visibility = Visibility.Collapsed;
            var scroll = new ScrollViewer { Content = Stack(rows, empty), MaxHeight = Math.Min(246, Math.Max(130, ActualHeight - 260)),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var panel = new Border { Child = Stack(search, scroll), Width = 392, Padding = new Thickness(8),
                CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
            UseResource(panel, Border.BackgroundProperty, "Card");
            UseResource(panel, Border.BorderBrushProperty, "Line");
            UseResource(panel, TextElement.ForegroundProperty, "Ink");
            AutomationProperties.SetName(panel, "切换首页实例");
            var duplicateNumbers = InstanceDuplicateNumbers();
            var choices = new List<(Button Row, string Search)>();
            foreach (var instance in OrderedInstances()) {
                string id = instance.S("id");
                bool current = id == selected;
                string subtitle = instance.S("version") + " · " + Loader(instance.S("loader"));
                if (duplicateNumbers.TryGetValue(id, out int number)) subtitle += " · " + DuplicateInstanceLabel(instance, number);
                if (instance.B("externalMissing")) subtitle += " · 路径丢失";
                var row = QuietAction("", async () => {
                    popup.IsOpen = false;
                    await SelectInstance(id);
                });
                row.Style = (Style)FindResource("InstanceRowButton");
                var icon = LoaderIcon(instance.S("loader"), 24);
                var check = current ? InstanceText("✓", 13, FontWeights.SemiBold) : null;
                if (check != null) UseResource(check, TextBlock.ForegroundProperty, "AccentText");
                row.Content = current ? Row(icon, instance.S("name"), subtitle, check) : Row(icon, instance.S("name"), subtitle);
                row.Padding = new Thickness(9, 5, 9, 3);
                row.Margin = new Thickness(0);
                row.Tag = current;
                AutomationProperties.SetName(row, instance.S("name") + " · " + subtitle + (current ? " · 当前实例" : ""));
                choices.Add((row, InstanceSearchText(instance)));
                rows.Children.Add(row);
            }
            search.TextChanged += (_, __) => {
                var keywords = search.Text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                bool any = false;
                foreach (var choice in choices) {
                    bool matches = MatchesInstanceSearch(choice.Search, keywords);
                    choice.Row.Visibility = matches ? Visibility.Visible : Visibility.Collapsed;
                    any |= matches;
                }
                empty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
                scroll.ScrollToTop();
            };
            panel.PreviewKeyDown += (_, e) => {
                if (e.Key != Key.Escape) return;
                e.Handled = true;
                popup.IsOpen = false;
                popup.PlacementTarget.Focus();
            };
            popup.Child = panel;
            popup.IsOpen = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => {
                if (popup.IsOpen && popup.Child == panel) search.Focus();
            }));
        }

        FrameworkElement HomeLaunchAction(Image icon, string label, string hint, Func<Task> action, out Button button) {
            var title = InstanceText(label, 32);
            title.TextAlignment = TextAlignment.Right;
            UseResource(title, TextBlock.ForegroundProperty, "Ink");
            button = QuietAction("", action);
            button.Content = title;
            button.Margin = new Thickness(0);
            button.Padding = new Thickness(2, 0, 2, 0);
            button.HorizontalAlignment = HorizontalAlignment.Right;
            AutomationProperties.SetName(button, label);
            var labels = Stack(button);
            if (hint != "") {
                var description = InstanceText(hint, 13);
                description.TextAlignment = TextAlignment.Right;
                description.IsHitTestVisible = false;
                UseResource(description, TextBlock.ForegroundProperty, "Muted");
                labels.Children.Add(description);
                AutomationProperties.SetHelpText(button, hint);
            }
            labels.VerticalAlignment = VerticalAlignment.Bottom;
            icon.Margin = new Thickness(14, 0, 0, 0);
            icon.IsHitTestVisible = false;
            var content = new DockPanel();
            DockPanel.SetDock(icon, Dock.Right);
            content.Children.Add(icon);
            content.Children.Add(labels);
            content.SetBinding(VisibilityProperty, new Binding(nameof(UIElement.Visibility)) { Source = button });
            return content;
        }

        static readonly Geometry HomeCurves = CreateHomeCurves();

        static Geometry CreateHomeCurves() {
            var pattern = new GeometryGroup();
            pattern.Children.Add(Geometry.Parse(
                "M 8,262 A 405,405 0 0 1 531,-12 M 8,262 A 405,405 0 0 0 531,-12 M 82,280 A 310,310 0 0 1 634,193"));
            pattern.Children.Add(new EllipseGeometry(new Point(334, 170), 74, 74));
            pattern.Children.Add(new EllipseGeometry(new Point(531, 218), 121, 121));
            pattern.Freeze();
            return pattern;
        }

        static Border HomeCurveTexture(out System.Windows.Shapes.Path glow) {
            var curves = new System.Windows.Shapes.Path { Data = HomeCurves, StrokeThickness = 1.5, Opacity = .30 };
            UseResource(curves, System.Windows.Shapes.Shape.StrokeProperty, "AccentText");
            var horizontalFade = new LinearGradientBrush(new GradientStopCollection {
                new GradientStop(Colors.Transparent, 0), new GradientStop(Colors.White, .42),
                new GradientStop(Colors.White, .82), new GradientStop(Colors.Transparent, 1)
            }, new Point(0, .5), new Point(1, .5));
            horizontalFade.Freeze();
            var pattern = new Canvas { Width = 620, Height = 266, OpacityMask = horizontalFade };
            pattern.Children.Add(curves);
            glow = CreateHomeCurveGlow();
            pattern.Children.Add(glow);
            Canvas.SetRight(pattern, 0);
            Canvas.SetTop(pattern, 0);

            var verticalFade = new LinearGradientBrush(new GradientStopCollection {
                new GradientStop(Colors.Transparent, 0), new GradientStop(Colors.White, .15),
                new GradientStop(Colors.White, .83), new GradientStop(Colors.Transparent, 1)
            }, new Point(.5, 0), new Point(.5, 1));
            verticalFade.Freeze();
            var viewport = new Canvas { ClipToBounds = true };
            viewport.Children.Add(pattern);
            return new Border { Background = Brushes.Transparent, Child = viewport, IsHitTestVisible = false, OpacityMask = verticalFade };
        }

        static System.Windows.Shapes.Path CreateHomeCurveGlow() {
            var dashes = new DoubleCollection { 26, 230 };
            dashes.Freeze();
            var flow = new System.Windows.Shapes.Path {
                Data = HomeCurves, StrokeThickness = 2, Opacity = 0,
                StrokeDashArray = dashes, StrokeDashCap = PenLineCap.Round
            };
            UseResource(flow, System.Windows.Shapes.Shape.StrokeProperty, "AccentText");
            flow.Unloaded += (_, __) => {
                flow.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, null);
                flow.BeginAnimation(OpacityProperty, null);
            };
            return flow;
        }

        static void PlayHomeCurveFlow(System.Windows.Shapes.Path glow) {
            var duration = TimeSpan.FromMilliseconds(780);
            double start = glow.StrokeDashOffset;
            var travel = new DoubleAnimation(start, start - 220, duration) { FillBehavior = FillBehavior.Stop };
            var light = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            light.KeyFrames.Add(new LinearDoubleKeyFrame(glow.Opacity, TimeSpan.Zero));
            light.KeyFrames.Add(new LinearDoubleKeyFrame(.64, TimeSpan.FromMilliseconds(80)));
            light.KeyFrames.Add(new LinearDoubleKeyFrame(.64, TimeSpan.FromMilliseconds(470)));
            light.KeyFrames.Add(new EasingDoubleKeyFrame(0, duration, new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            glow.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, travel);
            glow.BeginAnimation(OpacityProperty, light);
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

        void ShowInstanceSettings(object instance) {
            var id = instance.S("id");
            bool fromHome = section == HomeSection;
            var page = ShowPage("instance", fromHome ? HomeSection : InstancesSection, "实例设置", async () => {
                if (!await CanLeavePage()) return;
                if (fromHome) ShowHome(); else ShowInstances();
            });
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
            var typed = original ? Input() : null;
            var content = Stack(Heading(name), Note(InstanceVersion(instance)));
            InstanceDuplicateNumbers().TryGetValue(id, out int duplicateNumber);
            string identity = duplicateNumber > 0 ? DuplicateInstanceLabel(instance, duplicateNumber) : "";
            var duplicate = duplicateNumber > 0 ? Note(identity + " · 正在读取模组数量…") : null;
            if (duplicate != null) content.Children.Add(duplicate);
            if (external) content.Children.Add(Note(instance.S("externalPath")));
            var consequence = Note(remove ? "从 EBuild 移除此实例记录，原游戏文件夹会保留。" :
                original ? "将永久删除原游戏文件夹及其中的存档、模组和设置。" :
                "将永久删除此实例的存档、模组和设置。共用库及 Java 会保留。");
            consequence.Margin = new Thickness(0, 8, 0, 6);
            content.Children.Add(consequence);
            if (typed != null) content.Children.Add(Field("输入实例名称以确认", typed));
            var dialog = new TaskDialog(this, remove ? "移除实例" : "永久删除游戏", content);
            if (typed != null) dialog.Add("复制名称", () => Call("resources:copy-name", name));
            if (duplicate != null) dialog.Loaded += async (_, __) => await Guard(async () => {
                var counts = await backend.InstanceModCounts(new[] { instance });
                if (dialog.IsVisible) duplicate.Text = identity +
                    (counts.TryGetValue(id, out int count) ? " · " + count + " 个模组" : " · 模组数量不可用");
            });
            if (await dialog.Result(remove ? "移除实例" : "永久删除", confirmation: typed, exact: name,
                cancelByDefault: typed == null) != true) return;
            if (await RunJob("instances:delete", id, typed?.Text ?? name, remove ? "remove" : original ? "original" : "") == null) return;
            await RefreshInstances();
            if (route == "instance" || route == "instances") ShowInstances();
            else if (route == "home") UpdateHomeCard();
            Say(remove ? "实例已移除" : "游戏已删除");
        }

        async Task Relocate(object instance) {
            if (await RunJob("instances:relocate", instance.S("id")) == null) return;
            await RefreshInstances();
            if (route == "instance") ShowInstanceSettings(FindInstance(instance.S("id")));
            else if (route == "home") UpdateHomeCard();
            else ShowInstances();
            Say("实例目录已更新");
        }

        async Task ShowImport() {
            var menu = new TaskDialog(this, "导入实例", Stack(Note("选择游戏文件夹或 Modrinth 整合包（.mrpack / .zip）。")));
            menu.Add("导入游戏文件夹", async () => {
                string dir = backend.Folder("选择 Minecraft 游戏文件夹", owner: menu);
                if (dir != "") { menu.Accept(); await PreviewImport(dir); }
            });
            menu.Add("导入整合包", async () => {
                var chooser = new OpenFileDialog { Title = "导入 Modrinth 整合包", Filter = "Modrinth 整合包|*.mrpack;*.zip" };
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
            if (result.S("id") != "") { await RefreshInstances(); ShowInstances(); Say("实例已导入"); }
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
