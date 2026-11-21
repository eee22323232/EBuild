using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace EBuild.Native
{
    public partial class Launcher
    {
        static readonly Choice[] ResourceTypes = { Option("", "全部类型"), Option("mod", "模组"), Option("modpack", "整合包"),
            Option("shader", "光影"), Option("resourcepack", "材质") };
        static readonly Choice[] ModCategories = {
            Option("", "全部模组分类"), Option("optimization", "性能优化"), Option("utility", "实用工具"),
            Option("library", "前置与支持库"), Option("adventure", "冒险"), Option("technology", "科技"),
            Option("magic", "魔法"), Option("decoration", "装饰"), Option("worldgen", "世界生成"),
            Option("game-mechanics", "游戏机制"), Option("equipment", "装备与工具"), Option("mobs", "生物"),
            Option("food", "食物与烹饪"), Option("storage", "仓储"), Option("transportation", "运输"),
            Option("management", "管理"), Option("social", "社交"), Option("economy", "经济"),
            Option("minigame", "小游戏"), Option("cursed", "恶搞")
        };

        static string ResourceType(string key) => ResourceTypes.FirstOrDefault(option => option.Key == key)?.Label ?? key;
        static IEnumerable<Choice> FilterLoaders() => new[] { Option("", "全部加载器") }
            .Concat(LoaderOptions.Where(item => item.Key != "vanilla" && item.Key != "minecraft"));

        sealed class ResourceSearch
        {
            static readonly Choice AnyVersion = Option("", "全部（也可输入版本号）");
            readonly Launcher host;
            readonly TextBox query = CatalogInput("输入资源名称", max: 150);
            readonly ComboBox version;
            readonly ComboBox type;
            readonly ComboBox category;
            readonly ComboBox loader;
            readonly ComboBox sort;
            readonly StackPanel results = Stack();
            readonly TextBlock status = Note("");
            readonly Button more;
            object filters;
            int request;
            int count;
            bool loaded;
            bool loadingVersions;
            string GameVersion => version.Text == AnyVersion.Label ? "" : version.Text.Trim();

            internal StackPanel Root { get; }

            internal ResourceSearch(Launcher host, string game, string selectedLoader, string selectedType) {
                this.host = host;
                status.Margin = new Thickness(0, 1, 0, 5);
                version = Picker(new[] { AnyVersion }, editable: true);
                version.IsTextSearchEnabled = false;
                version.Text = game == "" ? AnyVersion.Label : game;
                version.DropDownOpened += async (_, __) => await LoadVersionChoices();
                type = Picker(ResourceTypes, selectedType == "" ? "mod" : selectedType);
                category = Picker(ModCategories);
                var heading = Heading("", 15);
                heading.Margin = new Thickness(0, 0, 0, 6);
                void UpdateCategory() {
                    string selected = Chosen(type);
                    heading.Text = selected == "" ? "搜索资源" : "搜索" + ResourceType(selected);
                    category.IsEnabled = selected == "mod";
                    if (!category.IsEnabled) category.SelectedIndex = 0;
                }
                UpdateCategory();
                loader = Picker(FilterLoaders(), selectedLoader);
                sort = Picker(new[] { Option("relevance", "相关性"), Option("downloads", "下载量"),
                    Option("updated", "最近更新"), Option("newest", "最新发布") });
                more = host.QuietAction("加载更多", () => Search(true));
                more.HorizontalAlignment = HorizontalAlignment.Center;
                more.Margin = new Thickness(0, 8, 0, 0);
                more.Visibility = Visibility.Collapsed;
                var search = host.Button("搜索", () => Search(false), true);
                search.MinWidth = 88;
                search.Margin = new Thickness(0, 0, 6, 0);
                var reset = host.Button("重置条件", () => {
                    query.Clear();
                    version.SelectedIndex = 0;
                    version.Text = AnyVersion.Label;
                    category.SelectedIndex = loader.SelectedIndex = sort.SelectedIndex = 0;
                    return Search(false);
                });
                reset.MinWidth = 88;
                reset.Margin = search.Margin;

                var fields = new Grid();
                foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star),
                    new GridLength(22), GridLength.Auto, new GridLength(.65, GridUnitType.Star) })
                    fields.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
                for (int row = 0; row < 3; row++) fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                AddSearchField(fields, "名称", query, 0, 0);
                AddSearchField(fields, "资源类型", type, 0, 3);
                AddSearchField(fields, "游戏版本", version, 1, 0);
                AddSearchField(fields, "模组分类", category, 1, 3);

                AddSearchField(fields, "加载器", loader, 2, 0);
                AddSearchField(fields, "排序", sort, 2, 3);
                var advanced = fields.Children.Cast<UIElement>().Where(item => Grid.GetRow(item) == 2).ToArray();
                foreach (var item in advanced) item.Visibility = Visibility.Collapsed;
                bool advancedOpen = false;
                var filterButton = new Button { MinWidth = 88, Style = (Style)host.FindResource("QuietButton") };
                filterButton.Click += (_, __) => {
                    advancedOpen = !advancedOpen;
                    foreach (var item in advanced) item.Visibility = advancedOpen ? Visibility.Visible : Visibility.Collapsed;
                    UpdateFilterLabel();
                };
                void UpdateFilterLabel() {
                    int count = (Chosen(loader) == "" ? 0 : 1) + (Chosen(sort) == "relevance" ? 0 : 1);
                    filterButton.Content = advancedOpen ? "收起筛选" : count == 0 ? "更多筛选" : "更多筛选 · " + count;
                    AutomationProperties.SetItemStatus(filterButton, advancedOpen ? "已展开" : "已折叠");
                }
                UpdateFilterLabel();
                var actions = Actions(search, reset, filterButton);
                actions.Margin = new Thickness(0, 7, 0, 0);
                var form = Card(Stack(heading, fields, actions));
                form.Padding = new Thickness(14, 8, 14, 8);
                form.Margin = new Thickness(0, 0, 0, 6);
                Root = Stack(form, status, results, more);

                async void ApplyOnEnter(object sender, KeyEventArgs e) {
                    if (e.Key != Key.Enter || (sender == version && version.IsDropDownOpen)) return;
                    e.Handled = true;
                    await host.Guard(() => Search(false));
                }
                query.KeyDown += ApplyOnEnter;
                version.PreviewKeyDown += ApplyOnEnter;
                type.SelectionChanged += (_, __) => UpdateCategory();
                foreach (var filter in new[] { loader, sort })
                    filter.SelectionChanged += (_, __) => UpdateFilterLabel();
            }

            static void AddSearchField(Grid fields, string name, Control control, int row, int column) {
                var margin = row == 0 ? 0 : 5;
                var label = new Label { Content = name, Target = control, Padding = new Thickness(0, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, margin, 0, 0) };
                UseResource(label, Control.ForegroundProperty, "Ink");
                AutomationProperties.SetName(control, name);
                control.Margin = new Thickness(0, margin, 0, 0);
                Grid.SetRow(label, row);
                Grid.SetRow(control, row);
                Grid.SetColumn(label, column);
                Grid.SetColumn(control, column + 1);
                fields.Children.Add(label);
                fields.Children.Add(control);
            }

            async Task LoadVersionChoices() {
                if (loadingVersions || version.Items.Count > 1) return;
                loadingVersions = true;
                try {
                    var data = host.catalog ?? await host.Call("catalog", false);
                    var selected = GameVersion;
                    var releases = data.A("versions").Where(item => item.S("type") == "release").Select(item => item.S("id"));
                    SetOptions(version, new[] { AnyVersion }.Concat(releases.Select(id => Option(id, id))), selected);
                    if (selected != "" && Chosen(version) != selected) version.Text = selected;
                } catch {
                    version.ToolTip = "版本列表加载失败，可以直接输入版本号。";
                } finally { loadingVersions = false; }
            }

            internal Task EnsureLoaded() => loaded ? Task.CompletedTask : Search(false);

            internal async Task Search(bool append) {
                int revision = ++request;
                if (!append) {
                    filters = J.Map("query", query.Text.Trim(), "version", GameVersion, "type", Chosen(type),
                        "category", Chosen(category), "loader", Chosen(loader), "sort", Chosen(sort));
                    results.Children.Clear();
                    count = 0;
                }
                more.Visibility = Visibility.Collapsed;
                status.Text = "正在搜索 Modrinth…";
                try {
                    var data = await host.Call("resources:search", J.Merge(filters, J.Map("offset", count)));
                    if (revision != request) return;
                    loaded = true;
                    var found = data.A("hits");
                    count += found.Length;
                    status.Text = count == 0 ? "没有找到匹配资源。" : "共 " + data.I("total_hits").ToString("N0") + " 个结果";
                    int position = 0;
                    var categoryKey = filters.S("category");
                    var appliedCategory = categoryKey == "" ? null : ModCategories.FirstOrDefault(option => option.Key == categoryKey);
                    foreach (var project in found) {
                        var row = SearchResult(project, filters, appliedCategory);
                        results.Children.Add(row);
                        if (position < 6) host.AnimateEntrance(row, 0, 9, position++ * 17);
                    }
                    more.Visibility = count < data.I("total_hits") && found.Length != 0 ? Visibility.Visible : Visibility.Collapsed;
                } catch (Exception error) {
                    if (revision == request) status.Text = error.Message;
                }
            }

            FrameworkElement SearchResult(object project, object currentFilters, Choice appliedCategory) {
                var name = project.S("title");
                var summary = project.S("description");
                var hasSummary = !string.IsNullOrWhiteSpace(summary);
                var projectType = project.S("project_type");
                var tags = project.A("display_categories").Concat(project.A("categories")).Select(tag => tag.S());
                var matched = projectType == "mod" ? appliedCategory ?? tags.Select(tag => ModCategories.FirstOrDefault(option => option.Key == tag))
                    .FirstOrDefault(option => option != null) : null;

                var title = InstanceText(name, 14, FontWeights.SemiBold);
                var details = Stack(title);
                if (hasSummary) {
                    var description = InstanceText(summary, 12);
                    description.Margin = new Thickness(0, 1, 0, 0);
                    UseResource(description, TextBlock.ForegroundProperty, "Muted");
                    details.Children.Add(description);
                }
                var categoryLabel = InstanceText(matched?.Label ?? ResourceType(projectType), 11, FontWeights.SemiBold);
                UseResource(categoryLabel, TextBlock.ForegroundProperty, "AccentText");
                var downloads = InstanceText(DownloadCount(project), 11);
                downloads.Margin = new Thickness(9, 0, 0, 0);
                UseResource(downloads, TextBlock.ForegroundProperty, "Muted");
                var info = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
                info.Children.Add(categoryLabel);
                info.Children.Add(downloads);
                details.Children.Add(info);
                details.VerticalAlignment = VerticalAlignment.Center;

                const int iconSize = 42;
                var icon = host.ProjectImage(project, iconSize);
                icon.Margin = new Thickness(0, 0, 11, 0);
                icon.Clip = new RectangleGeometry(new Rect(0, 0, iconSize, iconSize), 5, 5);
                var preview = new DockPanel();
                DockPanel.SetDock(icon, Dock.Left);
                preview.Children.Add(icon);
                preview.Children.Add(details);

                var result = host.QuietAction("", () => host.OpenResource(project.S("project_id"), currentFilters, Back));
                result.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                result.Margin = new Thickness(0);
                result.Padding = new Thickness(10, 6, 8, 6);
                result.Content = preview;
                result.ToolTip = hasSummary ? name + "\n" + summary : name;
                UseResource(result, Control.ForegroundProperty, "Ink");
                AutomationProperties.SetName(result, "查看 " + name + " 的详情");

                var quick = host.Button("安装", () => host.QuickInstall(project, currentFilters, Back));
                var actionLabel = projectType == "modpack" ? "安装整合包" : "安装兼容版本";
                quick.ToolTip = actionLabel;
                quick.VerticalAlignment = VerticalAlignment.Center;
                quick.MinWidth = 56;
                quick.Padding = new Thickness(8, 4, 8, 4);
                UseResource(quick, Control.ForegroundProperty, "AccentText");
                AutomationProperties.SetName(quick, actionLabel + " · " + name);
                var card = Card(WithActions(result, quick));
                card.Padding = new Thickness(0, 0, 9, 0);
                card.Margin = new Thickness(0, 0, 0, 5);
                card.CornerRadius = new CornerRadius(7);
                return card;
            }

            Task Back() {
                return host.ShowDownloads(1, GameVersion, Chosen(loader), Chosen(type), this);
            }
        }

        Image ProjectImage(object project, int size) {
            var image = AssetIcon("resource-file", size);
            string url = project.S("icon_url");
            if (url != "") image.Loaded += async (_, __) => {
                var bitmap = await RemoteImage(url, size * 2, "cdn.modrinth.com", "images.modrinth.com");
                if (bitmap != null) image.Source = bitmap;
            };
            return image;
        }

        static string DownloadCount(object project) => long.TryParse(project.S("downloads"), out long count) ?
            count.ToString("N0") + " 次下载" : "";

        async Task OpenResource(string id, object filters, Func<Task> back) {
            var title = new ContentControl { Content = Heading("资源详情", 19), HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 9) };
            var page = ShowPage("resource", DownloadsSection, "", back, titleContent: title);
            var status = Note("正在读取资源详情…");
            page.Children.Add(status);
            int revision = pageRevision;
            try {
                var result = await Call("resources:detail", id);
                if (pageRevision != revision) return;
                page.Children.Remove(status);
                RenderResource(page, title, result, filters, back);
            } catch (Exception error) {
                status.Text = error.Message;
                page.Children.Add(Actions(Button("重试", () => OpenResource(id, filters, back))));
            }
        }

        static bool Matches(object release, string type, object instance) {
            if (instance == null || instance.B("externalMissing") || !release.A("game_versions").Any(value => value.S() == instance.S("version")))
                return false;
            var loader = instance.S("loader");
            if (type == "modpack") return release.A("loaders").Any(value => value.S() == loader || (value.S() == "minecraft" && loader == "vanilla"));
            return type != "mod" || release.A("loaders").Any(value => value.S() == loader);
        }

        static string VersionSeries(string version) {
            var match = Regex.Match(version, @"^\d+\.\d+");
            return match.Success ? match.Value : version;
        }

        void RenderResource(StackPanel page, ContentControl titleHost, object result, object filters, Func<Task> back) {
            var project = result.At("project");
            var id = project.S("id");
            var title = project.S("title");
            var slug = project.S("slug");
            var type = project.S("project_type");
            var releases = result.A("releases");
            var links = CreateActionMenu();
            AddMenu(links, "打开 Modrinth", () => Call("resources:open-link", "https://modrinth.com/" +
                Uri.EscapeDataString(type) + "/" + Uri.EscapeDataString(slug)));
            AddMenu(links, "复制名称", () => Call("resources:copy-name", title));
            AddMenu(links, "在 MC 百科搜索", () => Call("resources:open-link", "https://search.mcmod.cn/s?key=" + Uri.EscapeDataString(title)));
            if (result.S("wikiUrl") != "") AddMenu(links, "打开 MC 百科", () => Call("resources:open-link", result.S("wikiUrl")));
            var moreActions = new Button { Content = "项目操作 ▾", ContextMenu = links, MinHeight = 30 };
            moreActions.Click += (_, __) => {
                links.PlacementTarget = moreActions;
                links.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                links.IsOpen = true;
            };
            var projectInfo = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
            var icon = ProjectImage(project, 31);
            icon.Margin = new Thickness(0, 0, 9, 0);
            DockPanel.SetDock(icon, Dock.Left);
            projectInfo.Children.Add(icon);
            var projectTitle = InstanceText(title, 18, FontWeights.SemiBold);
            projectTitle.ToolTip = title;
            var projectMeta = InstanceText(ResourceType(type) + " · " + DownloadCount(project), 11);
            var updated = DateLabel(project.S("updated"));
            if (updated != "") projectMeta.ToolTip = "更新于 " + updated;
            UseResource(projectMeta, TextBlock.ForegroundProperty, "Muted");
            projectInfo.Children.Add(Stack(projectTitle, projectMeta));
            titleHost.Content = WithActions(projectInfo, moreActions);

            var tags = result.A("gameVersionTags").GroupBy(tag => tag.S("version")).ToDictionary(group => group.Key, group => group.First());
            var allGame = releases.SelectMany(release => release.A("game_versions")).Select(value => value.S()).Distinct().ToArray();
            bool Stable(string version) => tags.TryGetValue(version, out object tag) ?
                tag.S("type") == "release" : Regex.IsMatch(version, @"^\d+(?:\.\d+){1,2}$");
            var game = filters.S("version");
            var exact = game != "";
            bool experimental = game != "" && !Stable(game);
            var compatible = Check("仅看兼容");
            AutomationProperties.SetName(compatible, "仅看当前实例兼容版本");
            compatible.ToolTip = "仅看当前实例兼容版本";
            compatible.Margin = new Thickness(9, 0, 0, 0);
            compatible.IsEnabled = Active != null && !Active.B("externalMissing");
            var loader = Picker(FilterLoaders(), filters.S("loader"));
            AutomationProperties.SetName(loader, "加载器");
            loader.Width = 124;
            loader.Margin = new Thickness(8, 0, 0, 0);
            var filtersRow = new StackPanel { Orientation = Orientation.Horizontal };
            var scroll = new ScrollViewer { Content = filtersRow, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "滚动查看更多游戏版本" };
            scroll.PreviewMouseWheel += (_, e) => {
                if (scroll.ScrollableWidth > 0) { scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta / 2.0); e.Handled = true; }
            };
            var groups = Stack();
            var versionGroups = Card(groups);
            versionGroups.Padding = new Thickness(0);
            versionGroups.Margin = new Thickness(0, 9, 0, 9);
            var groupStyle = (Style)FindResource("ResourceVersionGroup");
            int display = 12;
            object dependencyCache = null;
            Task<object> dependencyRequest = null;
            Func<Task> self = () => OpenResource(id, filters, back);
            Button loadMore = null;
            loadMore = Button("显示更多版本分组", () => { display += 12; RenderGroups(false); return Task.CompletedTask; });
            Button snap = null;
            snap = QuietAction(experimental ? "▴" : "▾", () => {
                experimental = !experimental;
                game = ""; exact = false;
                scroll.ScrollToLeftEnd();
                VersionButtons(); RenderGroups(true);
                return Task.CompletedTask;
            });
            snap.Padding = new Thickness(5, 2, 5, 2);
            snap.Width = 28;
            snap.MinHeight = 28;
            snap.Margin = new Thickness(3, 0, 0, 0);
            snap.ToolTip = "快照与预发布版本";
            AutomationProperties.SetName(snap, "快照与预发布版本");
            snap.Visibility = allGame.Any(version => !Stable(version)) ? Visibility.Visible : Visibility.Collapsed;
            var strip = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            strip.ColumnDefinitions.Add(new ColumnDefinition());
            strip.Children.Add(scroll);
            foreach (var control in new Control[] { snap, loader, compatible }) {
                Grid.SetColumn(control, strip.ColumnDefinitions.Count);
                strip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                strip.Children.Add(control);
            }

            async Task<object> Dependencies() {
                if (dependencyCache != null) return dependencyCache;
                if (dependencyRequest == null) dependencyRequest = Call("resources:dependencies", id);
                try { dependencyCache = await dependencyRequest; return dependencyCache; }
                finally { dependencyRequest = null; }
            }

            void VersionButtons() {
                bool restoreFocus = filtersRow.IsKeyboardFocusWithin;
                filtersRow.Children.Clear();
                snap.Content = experimental ? "▴" : "▾";
                AutomationProperties.SetItemStatus(snap, experimental ? "正在查看快照" : "正在查看正式版");
                Button current = null;
                void Add(string value, bool precise, string label) {
                    var chip = QuietAction(label, () => {
                        game = value; exact = precise;
                        RenderGroups(true); VersionButtons();
                        return Task.CompletedTask;
                    });
                    chip.MinHeight = 28;
                    chip.Padding = new Thickness(8, 3, 8, 3);
                    chip.Margin = new Thickness(0, 0, 2, 0);
                    if (game == value && exact == precise) {
                        current = chip;
                        UseResource(chip, Control.BackgroundProperty, "Hover");
                        UseResource(chip, Control.ForegroundProperty, "AccentText");
                        chip.FontWeight = FontWeights.SemiBold;
                        AutomationProperties.SetItemStatus(chip, "已选中");
                    }
                    filtersRow.Children.Add(chip);
                }
                Add("", false, experimental ? "全部快照" : "全部");
                if (game != "" && exact && !experimental) Add(game, true, game + "（精确）");
                var versions = allGame.Where(v => Stable(v) != experimental)
                    .OrderByDescending(v => tags.TryGetValue(v, out object tag) ? tag.S("date") : "").ThenByDescending(v => ParseVersion(v)).ToArray();
                foreach (string item in (experimental ? versions : versions.Select(VersionSeries).Distinct().ToArray()))
                    Add(item, experimental, item);
                if (restoreFocus) current?.Focus();
            }

            void RenderGroups(bool reset) {
                if (reset) display = 12;
                groups.Children.Clear();
                var selectedLoader = Chosen(loader);
                bool onlyCurrent = compatible.IsChecked == true;
                var byVersion = new Dictionary<Tuple<string,string>, List<object>>();
                foreach (var release in releases) {
                    if (onlyCurrent && !Matches(release, type, Active)) continue;
                    var releaseLoaders = release.A("loaders").Select(value => value.S()).Distinct().ToArray();
                    if (selectedLoader != "" && !releaseLoaders.Contains(selectedLoader)) continue;
                    foreach (var version in release.A("game_versions").Select(value => value.S()).Distinct()) {
                        if (Stable(version) == experimental || (onlyCurrent && version != Active.S("version"))) continue;
                        if (game != "" && (exact ? version != game : VersionSeries(version) != game)) continue;
                        var variants = type == "mod" || type == "modpack" ? releaseLoaders.DefaultIfEmpty("") : new[] { "" };
                        foreach (var variant in variants) {
                            if (variant != "" && selectedLoader != "" && variant != selectedLoader) continue;
                            if (onlyCurrent && variant != "" && variant != Active.S("loader") &&
                                !(variant == "minecraft" && Active.S("loader") == "vanilla")) continue;
                            var key = Tuple.Create(version, variant);
                            if (!byVersion.TryGetValue(key, out var items)) byVersion.Add(key, items = new List<object>());
                            items.Add(release);
                        }
                    }
                }
                if (byVersion.Count == 0) {
                    var empty = Note("没有匹配的版本。");
                    empty.Margin = new Thickness(15, 12, 15, 12);
                    groups.Children.Add(empty);
                }
                bool first = true;
                foreach (var group in byVersion.OrderByDescending(g => tags.TryGetValue(g.Key.Item1, out object tag) ? tag.S("date") : "")
                    .ThenByDescending(g => ParseVersion(g.Key.Item1)).ThenBy(g => g.Key.Item2).Take(display)) {
                    var version = group.Key.Item1;
                    var variant = group.Key.Item2;
                    var groupName = (variant == "" ? "Minecraft" : Loader(variant)) + " " + version;
                    var countLabel = group.Value.Count + " 个版本";
                    var expander = new Expander { Style = groupStyle, Header = groupName, Tag = countLabel };
                    AutomationProperties.SetName(expander, groupName + "，" + countLabel);
                    bool built = false;
                    expander.Expanded += (_, __) => {
                        if (built) return;
                        built = true;
                        var rows = Stack();
                        foreach (var release in group.Value.OrderByDescending(value => value.S("date_published"))) {
                            bool installable = type != "modpack" || release.A("loaders").Any(value =>
                                value.S() == "minecraft" || Minecraft.SupportsLoader(value.S()));
                            var action = QuietAction("安装", () => InstallResource(project, release, version, variant, ""));
                            action.IsEnabled = installable;
                            rows.Children.Add(ResourceVersionRow(release, version, action));
                            if (release.A("dependencies").Length != 0) {
                                var required = new Expander { Header = "必需依赖 · " + release.A("dependencies").Length,
                                    Margin = new Thickness(0, 4, 0, 8) };
                                UseResource(required, Control.ForegroundProperty, "Ink");
                                required.Expanded += async (sender, e) => {
                                    if (e.Source != required || required.Content != null) return;
                                    required.Content = Note("正在读取依赖…");
                                    try {
                                        var data = await Dependencies();
                                        var linksToDependencies = Stack();
                                        foreach (var dependency in release.A("dependencies")) {
                                            var pinned = data.A("releases").FirstOrDefault(value => value.S("id") == dependency.S("version_id"));
                                            var dependencyId = dependency.S("project_id");
                                            if (dependencyId == "") dependencyId = pinned.S("project_id");
                                            var entry = data.A("projects").FirstOrDefault(value => value.S("id") == dependencyId);
                                            if (dependencyId == "") linksToDependencies.Children.Add(Note("需要手动安装 · " + dependency.S("file_name")));
                                            else linksToDependencies.Children.Add(Button(entry.S("title") == "" ? dependencyId : entry.S("title"),
                                                () => OpenResource(dependencyId, J.Map("version", version, "loader", variant), self)));
                                        }
                                        required.Content = linksToDependencies;
                                    } catch (Exception error) { required.Content = Button("重试依赖", () => {
                                        required.Content = null; required.IsExpanded = false; required.IsExpanded = true;
                                        Say(error.Message); return Task.CompletedTask;
                                    }); }
                                };
                                rows.Children.Add(required);
                            }
                        }
                        expander.Content = rows;
                    };
                    var section = new Border { Child = expander, BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0) };
                    UseResource(section, Border.BorderBrushProperty, "Line");
                    groups.Children.Add(section);
                    expander.IsExpanded = first;
                    first = false;
                }
                loadMore.Visibility = byVersion.Count > display ? Visibility.Visible : Visibility.Collapsed;
            }

            compatible.Checked += (_, __) => RenderGroups(true);
            compatible.Unchecked += (_, __) => RenderGroups(true);
            loader.SelectionChanged += (_, __) => RenderGroups(true);
            VersionButtons();
            RenderGroups(true);
            var versionTab = Stack(strip, versionGroups, loadMore);
            TextElement.SetFontWeight(versionTab, FontWeights.Normal);
            var description = WebUtility.HtmlDecode(result.S("description").Replace("</p>", "\n\n").Replace("<p>", "").Replace("<br>", "\n")).Trim();
            var tabs = new TabControl { Style = (Style)FindResource("DownloadTabs") };
            tabs.Items.Add(Tab("版本列表", versionTab));
            var about = Card(Stack(Text(description == "" ? project.S("description") : description)));
            TextElement.SetFontWeight(about, FontWeights.Normal);
            tabs.Items.Add(Tab("项目介绍", about));
            page.Children.Add(tabs);
        }

        static FrameworkElement ResourceVersionRow(object release, string game, Button install) {
            var originalName = release.S("name");
            var prefix = "[" + game + "] ";
            var number = release.S("version_number");
            var name = originalName.StartsWith(prefix, StringComparison.Ordinal) ? originalName.Substring(prefix.Length) : originalName;
            if (name == "") name = number;
            var date = DateLabel(release.S("date_published"));
            var versionType = release.S("version_type");
            (string Letter, string Label, string Fill, string Ink) channel;
            switch (versionType) {
                case "release": channel = ("R", "正式版", "Hover", "AccentText"); break;
                case "beta": channel = ("B", "测试版", "BetaFill", "BetaText"); break;
                case "alpha": channel = ("A", "预览版", "AlphaFill", "AlphaText"); break;
                default: channel = ("?", "未标注发布类型", "Canvas", "Muted"); break;
            }
            var tag = versionType == "beta" || versionType == "alpha" ? channel.Label : "";
            var summary = String.Join(" · ", new[] { tag, date }.Where(value => value != ""));
            var note = String.Join(" · ", new[] { number, channel.Label, DownloadCount(release), date }.Where(value => value != ""));
            var letter = new TextBlock { Text = channel.Letter, FontSize = 11, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            AutomationProperties.SetName(letter, channel.Label);
            UseResource(letter, TextBlock.ForegroundProperty, channel.Ink);
            var icon = new Border { Child = letter, Width = 20, Height = 20, CornerRadius = new CornerRadius(5),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0), ToolTip = channel.Label };
            UseResource(icon, Border.BackgroundProperty, channel.Fill);
            var title = InstanceText(name, 14);
            title.VerticalAlignment = VerticalAlignment.Center;
            title.Margin = new Thickness(0, 0, 8, 0);
            var metadata = InstanceText(summary, 12);
            metadata.VerticalAlignment = VerticalAlignment.Center;
            metadata.TextAlignment = TextAlignment.Right;
            UseResource(metadata, TextBlock.ForegroundProperty, "Muted");
            var details = new DockPanel();
            DockPanel.SetDock(icon, Dock.Left);
            DockPanel.SetDock(metadata, Dock.Right);
            details.Children.Add(icon);
            details.Children.Add(metadata);
            details.Children.Add(title);
            install.MinHeight = 30;
            install.MinWidth = 54;
            install.Padding = new Thickness(8, 4, 8, 4);
            UseResource(install, Control.ForegroundProperty, "AccentText");
            AutomationProperties.SetName(install, "安装 " + originalName);
            var row = WithActions(details, install);
            row.MinHeight = 38;
            row.ToolTip = (originalName == "" ? name : originalName) + "\n" + note;
            return DownloadListLine(row);
        }

        static Version ParseVersion(string value) => Version.TryParse(value, out var version) ? version : new Version();

        async Task QuickInstall(object project, object filters, Func<Task> back) {
            string id = project.S("project_id");
            bool pack = project.S("project_type") == "modpack";
            var target = Picker(instances.Where(item => !item.B("externalMissing")).Select(InstanceOption), selected);
            var content = Stack(Heading(project.S("title")));
            if (pack) content.Children.Add(Note("安装后会创建独立实例。"));
            else if (target.Items.Count == 0) content.Children.Add(Note("请先创建一个实例，或从详情页下载到其他文件夹。"));
            else content.Children.Add(Field("目标实例", target));
            var dialog = new TaskDialog(this, "安装兼容版本", content);
            dialog.Add("查看详情", async () => {
                dialog.Close();
                await OpenResource(id, filters, back);
            });
            if (!pack && target.Items.Count == 0) { await dialog.Result("关闭"); return; }
            object plan = null;
            string targetId = "";
            if (await dialog.Result(pack ? "选择推荐版本" : "匹配并继续", async () => {
                targetId = pack ? "" : Chosen(target);
                plan = await Call("resources:plan", id, "", targetId);
                return plan != null;
            }) == true) await ConfirmResourcePlan(plan, targetId, "");
        }

        async Task InstallResource(object project, object release, string version, string loader, string preferred) {
            string id = project.S("id") != "" ? project.S("id") : project.S("project_id");
            string type = project.S("project_type");
            if (type == "modpack") {
                var result = await Call("resources:destination", id, release.S("id"), selected, version, loader);
                if (result.At("plan") != null) await ConfirmResourcePlan(result.At("plan"), "", "");
                return;
            }
            if (!global::Resources.Folders.TryGetValue(type, out string folderName)) throw new IOException("此资源类型无法下载。");
            var matches = instances.Where(item => Matches(release, type, item)).ToArray();
            var target = Picker(matches.Select(InstanceOption), preferred == "" ? selected : preferred);
            var preview = Stack();
            var content = Stack(Heading(project.S("title") + " · " + release.S("version_number")));
            if (matches.Length != 0) {
                content.Children.Add(Field("目标实例", target));
                content.Children.Add(Card(preview));
            } else content.Children.Add(Note("没有兼容实例。可以将文件下载到其他文件夹。"));
            var dialog = new TaskDialog(this, "选择安装位置", content);
            object destination = null;
            int pending = 0;
            bool choosing = false;
            async Task Preview() {
                int current = ++pending;
                preview.Children.Clear();
                preview.Children.Add(Note("正在读取 " + folderName + " 文件夹…"));
                try {
                    var info = (await Call("instances:resources", Chosen(target), folderName)).A().FirstOrDefault();
                    if (current != pending || !dialog.IsVisible) return;
                    preview.Children.Clear();
                    preview.Children.Add(Row(AssetIcon("folder", 25), FindInstance(Chosen(target)).S("name") + " / " + folderName, ""));
                    preview.Children.Add(FileList(info));
                } catch (Exception error) {
                    if (current == pending) { preview.Children.Clear(); preview.Children.Add(Note(error.Message)); }
                }
            }
            async Task Choose(bool useInstanceFolder) {
                if (choosing) return;
                choosing = true;
                try {
                    if (jobActive || mutations != 0) throw new IOException("请等待当前任务结束。");
                    var instance = Chosen(target);
                    // The native picker belongs to the main window. Keep focus with the target dialog.
                    destination = await Call("resources:destination", id, release.S("id"), instance, version, loader, useInstanceFolder, dialog);
                    if (destination != null && dialog.IsVisible) dialog.Accept();
                } finally { choosing = false; }
            }
            dialog.Add("取消", () => { dialog.Close(); return Task.CompletedTask; });
            dialog.Add("选择其他文件夹…", () => Choose(false));
            if (matches.Length != 0) dialog.Add("继续安装", () => Choose(true), true);
            var done = dialog.Open();
            if (matches.Length != 0) {
                target.SelectionChanged += async (_, __) => await Guard(Preview);
                _ = Guard(Preview);
            }
            if (await done != true || destination == null) return;
            if (destination.S("mode") == "folder") await ConfirmResourceExport(destination);
            else await ConfirmResourcePlan(destination.At("plan"), destination.S("instanceId"), destination.S("directory"));
        }

        async Task ConfirmResourcePlan(object plan, string instance, string folder) {
            var replace = plan.A("replacements");
            var view = Stack(Heading(plan.S("title") + " · " + plan.S("release")),
                Note(instance == "" ? "安装为新的游戏实例" : "目标实例 · " + FindInstance(instance).S("name")));
            if (folder != "") view.Children.Add(Note(folder));
            foreach (var file in plan.A("files").Skip(1)) view.Children.Add(Note("必需依赖 · " + file.S()));
            if (replace.Length != 0) {
                view.Children.Add(Heading("将替换以下已安装的文件"));
                foreach (var file in replace) view.Children.Add(Note(file.S()));
                view.Children.Add(Note("旧文件会移到实例的 mods-backup 文件夹。"));
            }
            if (await new TaskDialog(this, "确认安装资源", view).Result(replace.Length == 0 ? "安装" : "替换并安装") != true) return;
            if (plan.S("type") == "modpack") await TrackInstallation(() => RunJob("resources:install", plan.S("key"), replace.Length != 0));
            else if (await RunJob("resources:install", plan.S("key"), replace.Length != 0) != null) Say("已安装 " + plan.S("title"));
        }

        async Task ConfirmResourceExport(object result) {
            var dependencies = Check("同时下载必需依赖");
            var choices = Picker(FilterLoaders());
            var field = Field("匹配依赖的加载器", choices);
            var view = Stack(Heading(result.S("title") + " · " + result.S("version")), Note(result.S("directory")));
            if (result.B("hasDependencies")) {
                view.Children.Add(dependencies);
                view.Children.Add(field);
                field.Visibility = Visibility.Collapsed;
                dependencies.Checked += (_, __) => field.Visibility = result.S("dependencyLoader") == "" ? Visibility.Visible : Visibility.Collapsed;
                dependencies.Unchecked += (_, __) => field.Visibility = Visibility.Collapsed;
            }
            var dialog = new TaskDialog(this, "下载到文件夹", view);
            if (await dialog.Result("下载", () => {
                if (dependencies.IsChecked == true && result.S("dependencyLoader") == "" && Chosen(choices) == "")
                    throw new IOException("请先选择用于匹配依赖的加载器。");
                return Task.FromResult(true);
            }) == true && await RunJob("resources:export", result.S("key"), dependencies.IsChecked == true, Chosen(choices)) != null)
                Say("已下载 " + result.S("title"));
        }
    }
}
