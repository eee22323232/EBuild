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
using System.Windows.Input;
using System.Windows.Media;

namespace EBuild.Native
{
    public partial class Launcher
    {
        static readonly Choice[] ResourceTypes = { Option("", "全部类型"), Option("mod", "模组"), Option("modpack", "整合包"),
            Option("shader", "光影"), Option("resourcepack", "材质") };

        static string ResourceType(string key) => ResourceTypes.FirstOrDefault(option => option.Key == key)?.Label ?? key;
        static IEnumerable<Choice> FilterLoaders() => new[] { Option("", "全部加载器") }
            .Concat(LoaderOptions.Where(item => item.Key != "vanilla" && item.Key != "minecraft"));

        sealed class ResourceSearch
        {
            readonly Launcher host;
            readonly TextBox query = Input("", 150);
            readonly TextBox version;
            readonly ComboBox type;
            readonly ComboBox loader;
            readonly ComboBox sort;
            readonly StackPanel results = Stack();
            readonly TextBlock status = Note("");
            readonly Button more;
            object filters;
            int request;
            int count;
            bool loaded;

            internal StackPanel Root { get; }

            internal ResourceSearch(Launcher host, string game, string selectedLoader, string selectedType) {
                this.host = host;
                version = Input(game, 64);
                version.MinHeight = 34;
                query.MinHeight = 34;
                type = Picker(ResourceTypes, selectedType);
                loader = Picker(FilterLoaders(), selectedLoader);
                sort = Picker(new[] { Option("relevance", "相关性"), Option("downloads", "下载量"),
                    Option("updated", "最近更新"), Option("newest", "最新发布") });
                more = host.QuietAction("加载更多", () => Search(true));
                more.HorizontalAlignment = HorizontalAlignment.Center;
                more.Margin = new Thickness(0, 8, 0, 0);
                more.Visibility = Visibility.Collapsed;
                var search = host.Button("搜索", () => Search(false), true);
                search.MinWidth = 76;
                search.VerticalAlignment = VerticalAlignment.Bottom;
                search.Margin = new Thickness(12, 0, 0, 9);
                var searchRow = new DockPanel();
                DockPanel.SetDock(search, Dock.Right);
                searchRow.Children.Add(search);
                searchRow.Children.Add(Field("搜索 Modrinth 资源", query));
                Root = Stack(searchRow,
                    Columns(Columns(Field("游戏版本", version), Field("资源类型", type)),
                        Columns(Field("加载器", loader), Field("排序", sort))),
                    DownloadListLine(status), results, more);
                query.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await host.Guard(() => Search(false)); };
                foreach (var filter in new[] { type, loader, sort })
                    filter.SelectionChanged += async (_, __) => await host.Guard(() => Search(false));
            }

            internal Task EnsureLoaded() => loaded ? Task.CompletedTask : Search(false);

            internal async Task Search(bool append) {
                int revision = ++request;
                if (!append) {
                    filters = J.Map("query", query.Text.Trim(), "version", version.Text.Trim(), "type", Chosen(type), "loader", Chosen(loader), "sort", Chosen(sort));
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
                    status.Text = count == 0 ? "没有找到匹配资源。" : "Modrinth · " + count + " / " + data.I("total_hits") + " 个资源";
                    int position = 0;
                    foreach (var project in found) {
                        var currentFilters = filters;
                        var result = host.QuietAction("", () => host.OpenResource(project.S("project_id"), currentFilters, Back));
                        result.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                        result.Margin = new Thickness(0);
                        result.Padding = new Thickness(0, 9, 8, 9);
                        UseResource(result, Control.ForegroundProperty, "Ink");
                        AutomationProperties.SetName(result, "查看 " + project.S("title") + " 的详情");
                        var image = host.ProjectImage(project, 36);
                        image.Margin = new Thickness(0, 0, 12, 0);
                        var title = InstanceText(project.S("title"), 14, FontWeights.SemiBold);
                        var description = InstanceText(project.S("description"), 12);
                        UseResource(description, TextBlock.ForegroundProperty, "Muted");
                        var info = InstanceText(ResourceType(project.S("project_type")) + " · " + DownloadCount(project), 11);
                        UseResource(info, TextBlock.ForegroundProperty, "Muted");
                        var preview = new DockPanel();
                        DockPanel.SetDock(image, Dock.Left);
                        preview.Children.Add(image);
                        preview.Children.Add(Stack(title, description, info));
                        result.Content = preview;
                        result.ToolTip = project.S("description");
                        var row = new DockPanel();
                        var quick = host.QuietAction(project.S("project_type") == "modpack" ? "安装整合包" : "安装兼容版本",
                            () => host.QuickInstall(project, currentFilters, Back));
                        quick.VerticalAlignment = VerticalAlignment.Center;
                        quick.Margin = new Thickness(8, 0, 0, 0);
                        UseResource(quick, Control.ForegroundProperty, "AccentText");
                        DockPanel.SetDock(quick, Dock.Right);
                        row.Children.Add(quick);
                        row.Children.Add(result);
                        var line = DownloadListLine(row);
                        results.Children.Add(line);
                        if (position < 6) host.AnimateEntrance(line, 0, 9, position++ * 17);
                    }
                    more.Visibility = count < data.I("total_hits") && found.Length != 0 ? Visibility.Visible : Visibility.Collapsed;
                } catch (Exception error) {
                    if (revision == request) status.Text = error.Message;
                }
            }

            Task Back() {
                return host.ShowDownloads(1, version.Text, Chosen(loader), Chosen(type), this);
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
            var page = ShowPage("resource", 1, "资源详情", back);
            var status = Note("正在读取资源详情…");
            page.Children.Add(status);
            int revision = pageRevision;
            try {
                var result = await Call("resources:detail", id);
                if (pageRevision != revision) return;
                page.Children.Remove(status);
                RenderResource(page, result, filters, back);
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

        void RenderResource(StackPanel page, object result, object filters, Func<Task> back) {
            var project = result.At("project");
            var id = project.S("id");
            var title = project.S("title");
            var slug = project.S("slug");
            var type = project.S("project_type");
            var releases = result.A("releases");
            var titleCard = Stack(Row(ProjectImage(project, 54), title, ResourceType(type) + " · Modrinth · " + DownloadCount(project) +
                " · 更新于 " + DateLabel(project.S("updated"))), Note(project.S("description")));
            var links = Actions(Button("打开 Modrinth", () => Call("resources:open-link", "https://modrinth.com/" +
                Uri.EscapeDataString(type) + "/" + Uri.EscapeDataString(slug))),
                Button("复制名称", () => Call("resources:copy-name", title)),
                Button("在 MC 百科搜索", () => Call("resources:open-link", "https://search.mcmod.cn/s?key=" + Uri.EscapeDataString(title))));
            if (result.S("wikiUrl") != "") links.Children.Add(Button("打开 MC 百科", () => Call("resources:open-link", result.S("wikiUrl"))));
            titleCard.Children.Add(links);
            page.Children.Add(Card(titleCard));

            var tags = result.A("gameVersionTags").GroupBy(tag => tag.S("version")).ToDictionary(group => group.Key, group => group.First());
            var allGame = releases.SelectMany(release => release.A("game_versions")).Select(value => value.S()).Distinct().ToArray();
            bool Stable(string version) => tags.TryGetValue(version, out object tag) ?
                tag.S("type") == "release" : Regex.IsMatch(version, @"^\d+(?:\.\d+){1,2}$");
            var game = filters.S("version");
            var exact = game != "";
            bool experimental = game != "" && !Stable(game);
            var compatible = Check("仅看当前实例兼容版本");
            compatible.IsEnabled = Active != null && !Active.B("externalMissing");
            var loader = Picker(FilterLoaders(), filters.S("loader"));
            var filtersRow = new StackPanel { Orientation = Orientation.Horizontal };
            var scroll = new ScrollViewer { Content = filtersRow, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 0, 0, 8) };
            scroll.PreviewMouseWheel += (_, e) => {
                if (scroll.ScrollableWidth > 0) { scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta / 2.0); e.Handled = true; }
            };
            var groups = Stack();
            int display = 12;
            object dependencyCache = null;
            Task<object> dependencyRequest = null;
            Func<Task> self = () => OpenResource(id, filters, back);
            Button loadMore = null;
            loadMore = Button("显示更多版本分组", () => { display += 12; RenderGroups(false); return Task.CompletedTask; });
            Button snap = null;
            snap = Button(experimental ? "▴" : "▾", () => {
                experimental = !experimental;
                game = ""; exact = false;
                VersionButtons(); RenderGroups(true);
                return Task.CompletedTask;
            });
            snap.ToolTip = "快照与预发布版本";
            System.Windows.Automation.AutomationProperties.SetName(snap, "快照与预发布版本");
            snap.Visibility = allGame.Any(version => !Stable(version)) ? Visibility.Visible : Visibility.Collapsed;
            var strip = new DockPanel();
            DockPanel.SetDock(snap, Dock.Right);
            strip.Children.Add(snap);
            strip.Children.Add(scroll);

            async Task<object> Dependencies() {
                if (dependencyCache != null) return dependencyCache;
                if (dependencyRequest == null) dependencyRequest = Call("resources:dependencies", id);
                try { dependencyCache = await dependencyRequest; return dependencyCache; }
                finally { dependencyRequest = null; }
            }

            void VersionButtons() {
                filtersRow.Children.Clear();
                snap.Content = experimental ? "▴" : "▾";
                void Add(string value, bool precise, string label) {
                    filtersRow.Children.Add(Button(label, () => {
                        game = value; exact = precise;
                        RenderGroups(true); VersionButtons();
                        return Task.CompletedTask;
                    }, game == value && exact == precise));
                }
                Add("", false, experimental ? "全部快照" : "全部");
                if (game != "" && exact && !experimental) Add(game, true, game + "（精确）");
                var versions = allGame.Where(v => Stable(v) != experimental)
                    .OrderByDescending(v => tags.TryGetValue(v, out object tag) ? tag.S("date") : "").ThenByDescending(v => ParseVersion(v)).ToArray();
                foreach (string item in (experimental ? versions : versions.Select(VersionSeries).Distinct().ToArray()))
                    Add(item, experimental, item);
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
                if (byVersion.Count == 0) groups.Children.Add(Note("没有匹配的版本。"));
                bool first = true;
                foreach (var group in byVersion.OrderByDescending(g => tags.TryGetValue(g.Key.Item1, out object tag) ? tag.S("date") : "")
                    .ThenByDescending(g => ParseVersion(g.Key.Item1)).ThenBy(g => g.Key.Item2).Take(display)) {
                    var version = group.Key.Item1;
                    var variant = group.Key.Item2;
                    var expander = new Expander { Header = (variant == "" ? "Minecraft" : Loader(variant)) + " " + version +
                        " · " + group.Value.Count + " 个版本", Margin = new Thickness(0, 4, 0, 4), Padding = new Thickness(4) };
                    bool built = false;
                    expander.Expanded += (_, __) => {
                        if (built) return;
                        built = true;
                        var rows = Stack();
                        foreach (var release in group.Value.OrderByDescending(value => value.S("date_published"))) {
                            bool installable = type != "modpack" || release.A("loaders").Any(value =>
                                value.S() == "minecraft" || Minecraft.SupportsLoader(value.S()));
                            var action = Button("安装", () => InstallResource(project, release, version, variant, ""));
                            action.IsEnabled = installable;
                            var label = release.S("version_type") == "beta" ? "测试版" : release.S("version_type") == "alpha" ? "预览版" : "正式版";
                            rows.Children.Add(Row(AssetIcon("resource-file", 23), release.S("name"),
                                release.S("version_number") + " · " + label + " · " + DownloadCount(release) + " · " + DateLabel(release.S("date_published")), action));
                            if (release.A("dependencies").Length != 0) {
                                var required = new Expander { Header = "必需依赖 · " + release.A("dependencies").Length };
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
                    groups.Children.Add(Card(expander));
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
            var versionTab = Stack(Card(Stack(strip, Columns(Field("加载器", loader), Actions(compatible)))), groups, loadMore);
            var description = WebUtility.HtmlDecode(result.S("description").Replace("</p>", "\n\n").Replace("<p>", "").Replace("<br>", "\n")).Trim();
            var tabs = new TabControl();
            tabs.Items.Add(Tab("版本列表", versionTab));
            tabs.Items.Add(Tab("项目介绍", Card(Stack(Text(description == "" ? project.S("description") : description)))));
            page.Children.Add(tabs);
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
