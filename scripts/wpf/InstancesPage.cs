using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace EBuild.Native
{
    public partial class Launcher
    {
        readonly Dictionary<string, (Button Row, TextBlock Mark)> instanceRows = new Dictionary<string, (Button, TextBlock)>();
        StackPanel instanceList;
        TextBlock instanceSearchEmpty;
        string instanceQuery = "";
        int instanceListRevision;

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

        void UpdateInstanceHighlights() {
            foreach (var pair in instanceRows) {
                bool isSelected = pair.Key == selected;
                pair.Value.Row.Tag = isSelected;
                pair.Value.Mark.Text = isSelected ? "✓" : "";
                AutomationProperties.SetItemStatus(pair.Value.Row, isSelected ? "已选中" : "");
            }
        }
    }
}
